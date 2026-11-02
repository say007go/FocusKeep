using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FocusKeep
{
    /// <summary>
    /// 纯托盘应用：屏幕常亮、键盘锁定、OLED 护屏。
    /// 没有主窗口，所有操作都在托盘菜单里。
    /// </summary>
    internal sealed class AppContext : ApplicationContext
    {
        public const string NotifyEventName = "FocusKeep.Notify.v1";
        public const string MenuEventName = "FocusKeep.Menu.v1";
        public const string QuitEventName = "FocusKeep.Quit.v1";
        public const string RestEventName = "FocusKeep.Rest.v1";

        private const int HOTKEY_ID_LOCK = 0x0F01;
        private const int MOD_ALT = 0x0001;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_NOREPEAT = 0x4000;
        private const int VK_L = 0x4C;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private readonly HostWindow _invoker;
        private readonly SynchronizationContext _ui;
        private readonly Config _cfg;
        private readonly PowerKeeper _power;
        private readonly OledGuard _oled;
        private readonly KeyboardLock _locker;
        private readonly NotifyIcon _tray;
        private readonly MessageWindow _hotkeyWindow;

        // 四种状态图标只渲染一次，常驻复用：不再随状态切换堆积 Icon 对象
        private readonly Icon[] _stateIcons = new Icon[4];

        private System.Windows.Forms.Timer _restTimer;
        private System.Windows.Forms.Timer _memoryTimer;

        private EventWaitHandle _notifyEvent;
        private EventWaitHandle _menuEvent;
        private EventWaitHandle _quitEvent;
        private EventWaitHandle _restEvent;
        private Thread _watcher;

        private bool _screenOn = true;         // 启动即开启屏幕常亮
        private bool _hotkeyRegistered;
        private bool _firstTipDone;
        private bool _disposed;
        private bool _exiting;
        private bool _appliedScreenOn;
        private bool _iconScreenOn;
        private bool _iconLocked;
        private bool _iconReady;

        public AppContext()
        {
            Log.Write("=== FocusKeep 启动（纯托盘）===");
            Log.Write("配置：" + Config.ConfigPath);

            // 屏幕外的 1x1 宿主窗口：不抢焦点、不进 Alt+Tab，
            // 只用来在弹原生菜单时拿到前台身份（托盘程序没有可见主窗口）。
            _invoker = new HostWindow();
            _invoker.Show();
            GC.KeepAlive(_invoker.Handle);
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            _cfg = Config.Load();
            _power = new PowerKeeper();
            _oled = new OledGuard(_ui);
            _oled.Configure(_cfg.OledGuardEnabled, _cfg.OledIntervalMinutes, _cfg.OledBlankSeconds);

            _locker = new KeyboardLock(_ui);
            _locker.Unlocked += OnUnlocked;

            // 顺序很关键：图标必须在 Visible=true 之前就位，
            // 否则外壳添加托盘项时拿到的是空图标句柄。
            Icon initial = GetIcon(true, false);
            _iconScreenOn = true;
            _iconLocked = false;
            _iconReady = true;

            _tray = new NotifyIcon();
            _tray.Icon = initial;
            _tray.Text = "FocusKeep";
            _tray.Visible = true;
            _tray.MouseClick += OnTrayMouseClick;
            Log.Write("托盘图标已添加");

            _hotkeyWindow = new MessageWindow();
            _hotkeyWindow.HotkeyPressed += delegate { Post(delegate { LockNow(); }); };
            _hotkeyRegistered = RegisterHotKey(_hotkeyWindow.Handle, HOTKEY_ID_LOCK,
                MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_L);
            Log.Write("热键 Ctrl+Alt+L 注册" + (_hotkeyRegistered ? "成功" : "失败（可能被占用）"));

            StartWatcher();
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            Evaluate(true);
            ScheduleFirstRunTip();
            ScheduleMemoryTrim();
            Log.Write("初始化完成");
        }

        // ---------------- 状态 ----------------

        public Config Cfg { get { return _cfg; } }
        public bool IsLocked { get { return _locker.IsLocked; } }
        public bool ScreenOn { get { return _screenOn; } }
        public bool Exiting { get { return _exiting; } }

        private void Evaluate(bool force)
        {
            if (_disposed) return;

            if (force || _screenOn != _appliedScreenOn)
            {
                _appliedScreenOn = _screenOn;
                _power.Apply(_screenOn);
                _oled.SetScreenOn(_screenOn);
                Log.Write("屏幕常亮 -> " + (_screenOn ? "开" : "关"));
            }

            ApplyTrayState();
        }

        private void ApplyTrayState()
        {
            bool locked = _locker.IsLocked;

            if (!_iconReady || locked != _iconLocked || _screenOn != _iconScreenOn)
            {
                _tray.Icon = GetIcon(_screenOn, locked);
                _iconScreenOn = _screenOn;
                _iconLocked = locked;
                _iconReady = true;
                Log.Write("托盘图标 -> " + (_screenOn ? "常亮" : "已关闭") + (locked ? " + 键盘锁定角标" : ""));
            }

            string text = StatusText();
            if (text.Length > 63) text = text.Substring(0, 63);
            if (_tray.Text != text) _tray.Text = text;
        }

        /// <summary>四种组合的图标常驻缓存：渲染一次，之后只换引用，不产生新对象。</summary>
        private Icon GetIcon(bool screenOn, bool locked)
        {
            int slot = (screenOn ? 1 : 0) | (locked ? 2 : 0);
            Icon icon = _stateIcons[slot];
            if (icon == null)
            {
                icon = TrayIconFactory.Create(screenOn, locked);
                _stateIcons[slot] = icon;
            }
            return icon;
        }

        /// <summary>DPI / 显示设置变化后图标尺寸会变，缓存必须重建。</summary>
        private void ResetIconCache()
        {
            for (int i = 0; i < _stateIcons.Length; i++)
            {
                if (_stateIcons[i] == null) continue;
                try { _stateIcons[i].Dispose(); }
                catch (Exception error) { Debug.WriteLine(error.Message); }
                _stateIcons[i] = null;
            }
            _iconReady = false;
        }

        private string StatusText()
        {
            if (_locker.IsLocked) return "键盘已锁定 · 右键图标或按 Ctrl+Alt+L 解锁";
            if (!_screenOn) return "屏幕常亮已关闭";
            return _cfg.OledGuardEnabled
                ? "屏幕常亮中 · 护屏每 " + _cfg.OledIntervalMinutes + " 分钟"
                : "屏幕常亮中";
        }

        private void SetScreenOn(bool on)
        {
            if (_screenOn == on) return;
            _screenOn = on;
            Evaluate(true);
        }

        private void SetMaxLockMinutes(int minutes)
        {
            int clamped = Config.Clamp(minutes, 1, 480);
            if (_cfg.MaxLockMinutes == clamped) return;
            _cfg.MaxLockMinutes = clamped;
            SaveConfig();
            Log.Write("最长锁定改为 " + clamped + " 分钟");
        }

        private void SaveConfig()
        {
            try { _cfg.Save(); }
            catch (Exception ex) { Log.Error("保存配置", ex); }
        }

        // ---------------- 托盘交互 ----------------

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            // 原生菜单不再挂在 NotifyIcon 上，左右键都由这里统一弹出
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) ShowMenuAtCursor();
        }

        /// <summary>
        /// 再次双击 exe 的默认反馈：只发气泡，不弹菜单。
        /// 菜单出现在鼠标位置会被"顺手的下一次点击"误选（曾因此误触锁定）。
        /// </summary>
        private void ShowAlreadyRunningHint()
        {
            if (_disposed) return;
            _tray.ShowBalloonTip(6000, "FocusKeep 正在运行",
                "请在任务栏右下角找它的图标（可能在 ^ 里）右键打开菜单。\r\n" +
                "需要立刻弹出菜单可运行：FocusKeep.exe --menu",
                ToolTipIcon.Info);
        }

        private void ShowMenuAtCursor()
        {
            if (_disposed) return;

            try
            {
                int command = BuildAndShowMenu(Cursor.Position);
                RunCommand(command);
            }
            catch (Exception ex) { Log.Error("显示菜单", ex); }
        }

        // 原生菜单命令 id（子菜单按偏移量区分）
        private const int CmdScreenOn = 101;
        private const int CmdScreenOff = 102;
        private const int CmdOledOff = 201;
        private const int CmdOledPreset = 210;
        private const int CmdOledRest = 220;
        private const int CmdLock = 301;
        private const int CmdUnlock = 302;
        private const int CmdLockMinutes = 310;
        private const int CmdAbout = 401;
        private const int CmdExitApp = 402;
        private const int CmdProject = 403;
        private const int CmdOpenConfig = 404;

        private static readonly int[] OledIntervals = new int[] { 10, 20, 30, 60 };
        private static readonly int[] OledBlanks = new int[] { 20, 30, 45, 60 };
        private static readonly int[] LockMinutesOptions = new int[] { 5, 15, 30, 60, 120, 240, 480 };

        /// <summary>
        /// 用系统原生菜单（Win11 上就是圆角、跟随系统主题的原生样式），
        /// 返回用户选中的命令 id，取消返回 0。
        /// </summary>
        private int BuildAndShowMenu(Point at)
        {
            using (NativeMenu menu = new NativeMenu())
            {
                menu.AddDisabled(StatusText());
                menu.AddSeparator();

                menu.Add(CmdScreenOn, "开启屏幕常亮", _screenOn, true);
                menu.Add(CmdScreenOff, "关闭屏幕常亮", !_screenOn, true);

                NativeMenu oled = menu.AddSubMenu("OLED 护屏　" + (_cfg.OledGuardEnabled
                    ? "每 " + _cfg.OledIntervalMinutes + " 分钟"
                    : "已关闭"));

                oled.Add(CmdOledOff, "关闭", !_cfg.OledGuardEnabled, true);
                oled.AddSeparator();

                for (int i = 0; i < OledIntervals.Length; i++)
                {
                    bool current = _cfg.OledGuardEnabled
                        && _cfg.OledIntervalMinutes == OledIntervals[i]
                        && _cfg.OledBlankSeconds == OledBlanks[i];

                    oled.Add(CmdOledPreset + i,
                        "每 " + OledIntervals[i] + " 分钟 · 黑屏 " + OledBlanks[i] + " 秒",
                        current, true);
                }

                oled.AddSeparator();
                oled.Add(CmdOledRest, "立即休息一次（黑屏 " + _cfg.OledBlankSeconds + " 秒）");

                menu.AddSeparator();

                if (_locker.IsLocked)
                {
                    menu.Add(CmdUnlock, "解锁键盘");
                    menu.SetDefaultItem(CmdUnlock);      // 默认项在原生菜单里是加粗的
                }
                else
                {
                    menu.Add(CmdLock, "锁定键盘　Ctrl+Alt+L");

                    NativeMenu minutes = menu.AddSubMenu("最长锁定 " + _cfg.MaxLockMinutes + " 分钟");
                    for (int i = 0; i < LockMinutesOptions.Length; i++)
                    {
                        minutes.Add(CmdLockMinutes + i, LockMinutesOptions[i] + " 分钟",
                            _cfg.MaxLockMinutes == LockMinutesOptions[i], true);
                    }
                }

                menu.AddSeparator();
                menu.Add(CmdProject, "项目主页（GitHub）");
                menu.Add(CmdOpenConfig, "打开配置文件");
                menu.Add(CmdAbout, "关于 / 限制说明");
                menu.AddSeparator();
                menu.Add(CmdExitApp, "退出 FocusKeep");

                return menu.Show(_invoker.Handle, at);
            }
        }

        private void RunCommand(int command)
        {
            if (command <= 0) return;

            if (command == CmdScreenOn) { SetScreenOn(true); return; }
            if (command == CmdScreenOff) { SetScreenOn(false); return; }
            if (command == CmdOledOff)
            {
                SetOledGuard(false, _cfg.OledIntervalMinutes, _cfg.OledBlankSeconds);
                return;
            }
            if (command == CmdOledRest) { RestSoon(); return; }
            if (command == CmdLock) { LockNow(); return; }
            if (command == CmdUnlock) { _locker.Unlock("托盘菜单解锁"); return; }
            if (command == CmdAbout) { ShowAbout(); return; }
            if (command == CmdProject) { TaskDialog.OpenUrl(ProjectUrl); return; }
            if (command == CmdOpenConfig) { OpenConfigFile(); return; }
            if (command == CmdExitApp) { ExitApp(); return; }

            int index = command - CmdOledPreset;
            if (index >= 0 && index < OledIntervals.Length)
            {
                SetOledGuard(true, OledIntervals[index], OledBlanks[index]);
                return;
            }

            index = command - CmdLockMinutes;
            if (index >= 0 && index < LockMinutesOptions.Length)
            {
                SetMaxLockMinutes(LockMinutesOptions[index]);
            }
        }

        private void SetOledGuard(bool enabled, int intervalMinutes, int blankSeconds)
        {
            int interval = Config.Clamp(intervalMinutes,
                OledGuard.MinIntervalMinutes, OledGuard.MaxIntervalMinutes);
            int blank = Config.Clamp(blankSeconds,
                OledGuard.MinBlankSeconds, OledGuard.MaxBlankSeconds);

            if (_cfg.OledGuardEnabled == enabled
                && _cfg.OledIntervalMinutes == interval
                && _cfg.OledBlankSeconds == blank) return;

            _cfg.OledGuardEnabled = enabled;
            _cfg.OledIntervalMinutes = interval;
            _cfg.OledBlankSeconds = blank;
            SaveConfig();

            _oled.Configure(enabled, interval, blank);
            ApplyTrayState();

            Log.Write("OLED 护屏设置 -> " + (enabled ? "每 " + interval + " 分钟黑屏 " + blank + " 秒" : "关闭"));
        }

        /// <summary>菜单点完先把菜单收掉再黑屏，否则弹出菜单会盖在遮罩上。</summary>
        private void RestSoon()
        {
            if (_restTimer != null)
            {
                _restTimer.Stop();
                _restTimer.Dispose();
            }

            _restTimer = new System.Windows.Forms.Timer();
            _restTimer.Interval = 300;
            _restTimer.Tick += delegate
            {
                if (_restTimer != null)
                {
                    _restTimer.Stop();
                    _restTimer.Dispose();
                    _restTimer = null;
                }
                if (_disposed) return;
                _oled.RestNow();
            };
            _restTimer.Start();
        }

        private const string ProjectUrl = "https://github.com/say007go/FocusKeep";

        /// <summary>版本号只写在 src/AssemblyInfo.cs 一处，这里从程序集信息读，避免两边不一致。</summary>
        private static string AppVersion
        {
            get
            {
                try
                {
                    Version version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    return version.Major + "." + version.Minor + "." + version.Build;
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        private void ShowAbout()
        {
            string content =
                "屏幕常亮 · 键盘锁定 · OLED 护屏\r\n" +
                "\r\n" +
                "开发者　圣婴\r\n" +
                "微信　　say007go\r\n" +
                "项目主页　" + ProjectUrl;

            string detail =
                "热键\r\n" +
                "　Ctrl+Alt+L　　　　锁定键盘；锁定期间再按一次 = 解锁\r\n" +
                "　Ctrl+Alt+Shift+U　解锁\r\n" +
                "\r\n" +
                "托盘图标\r\n" +
                "　蓝绿显示器　　　屏幕常亮中\r\n" +
                "　灰色显示器　　　屏幕常亮已关闭\r\n" +
                "　右下角红色小锁　键盘已锁定（可与上面两种同时存在）\r\n" +
                "\r\n" +
                "OLED 护屏\r\n" +
                "　屏幕常亮期间，键鼠空闲 1 分钟以上、前台不是全屏、也没有声音时，\r\n" +
                "　用纯黑遮罩让屏幕休息一段时间。OLED 上黑色 = 像素不发光，等效熄屏，\r\n" +
                "　但常亮状态、后台任务、远程连接都不受影响；动一下鼠标立刻恢复。\r\n" +
                "\r\n" +
                "找不到托盘图标\r\n" +
                "　· 点任务栏的 ^ 展开，把图标拖到任务栏上固定\r\n" +
                "　· 或运行 FocusKeep.exe --menu 直接弹菜单\r\n" +
                "　· 运行 FocusKeep.exe --rest 立刻休息一次\r\n" +
                "\r\n" +
                "已知限制\r\n" +
                "　· Ctrl+Alt+Del 由安全桌面处理，拦不住\r\n" +
                "　· 结束进程会立刻摘掉键盘钩子\r\n" +
                "　· 只锁键盘，鼠标始终可用（所以能从菜单解锁）";

            TaskDialog.Show(_invoker.Handle, "关于 FocusKeep",
                "FocusKeep v" + AppVersion,
                content, detail, null);
        }

        /// <summary>用系统默认程序打开配置文件（通常是记事本），顺手把配置项说明带到眼前。</summary>
        private void OpenConfigFile()
        {
            try
            {
                string path = Config.ConfigPath;
                if (!System.IO.File.Exists(path)) SaveConfig();

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Error("打开配置文件", ex);
                _tray.ShowBalloonTip(4000, "打不开配置文件",
                    "可以手动打开：" + Config.ConfigPath, ToolTipIcon.Warning);
            }
        }

        // ---------------- 键盘锁 ----------------

        public void LockNow()
        {
            if (_locker.IsLocked) return;

            string message =
                "键盘即将锁定（鼠标不受影响）。\r\n\r\n" +
                "解锁方式（三种都行）：\r\n" +
                "  · 右键托盘图标 → 解锁键盘\r\n" +
                "  · 按 Ctrl + Alt + Shift + U\r\n" +
                "  · 再按一次 Ctrl + Alt + L\r\n\r\n" +
                "最长锁定：" + _cfg.MaxLockMinutes + " 分钟后自动解锁\r\n" +
                "Ctrl+Alt+Del 由系统处理，本工具拦不住；结束进程也会立即恢复。\r\n\r\n" +
                "确定锁定吗？";

            DialogResult answer = MessageBox.Show(message, "FocusKeep",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            if (!_locker.Lock(_cfg.MaxLockMinutes))
            {
                Log.Write("键盘锁定失败：SetWindowsHookEx 返回空");
                MessageBox.Show("锁定失败：系统拒绝了键盘钩子。", "FocusKeep",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Log.Write("键盘已锁定，上限 " + _cfg.MaxLockMinutes + " 分钟");
            ApplyTrayState();
            _tray.ShowBalloonTip(8000, "键盘已锁定",
                "右键托盘图标可解锁；也可以按 Ctrl+Alt+L 或 Ctrl+Alt+Shift+U；" +
                _cfg.MaxLockMinutes + " 分钟后自动解锁。", ToolTipIcon.Warning);
        }

        private void OnUnlocked(object sender, EventArgs e)
        {
            if (_disposed) return;
            Log.Write("键盘已解锁：" + _locker.LastReason);
            ApplyTrayState();
            _tray.ShowBalloonTip(4000, "键盘已解锁",
                "原因：" + (_locker.LastReason.Length > 0 ? _locker.LastReason : "手动"),
                ToolTipIcon.Info);
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason != SessionSwitchReason.SessionLock &&
                e.Reason != SessionSwitchReason.ConsoleDisconnect &&
                e.Reason != SessionSwitchReason.SessionLogoff) return;

            Post(delegate
            {
                if (_oled != null) _oled.HideNow("系统会话已锁定");
                if (_locker.IsLocked) _locker.Unlock("系统会话已锁定");
            });
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            Post(delegate
            {
                if (_oled != null) _oled.HideNow("系统已从休眠唤醒");
                Evaluate(true);
            });
        }

        /// <summary>注销 / 关机时干净退出：摘钩子、撤遮罩、去托盘图标。</summary>
        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            try { Post(delegate { if (!_disposed) ExitApp(); }); }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }

        /// <summary>换显示器 / 改缩放后，托盘图标尺寸会变，缓存要重建。</summary>
        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Post(delegate
            {
                if (_disposed) return;
                ResetIconCache();
                ApplyTrayState();
                Log.Write("显示设置变化，托盘图标已按新尺寸重建");
            });
        }

        /// <summary>
        /// 托盘常驻程序：启动 20 秒后收一次工作集，之后每 10 分钟收一次。
        /// 只是把不活跃的页换出内存，不影响功能，任务管理器里读数会明显下降。
        /// </summary>
        private void ScheduleMemoryTrim()
        {
            _memoryTimer = new System.Windows.Forms.Timer();
            _memoryTimer.Interval = 20000;
            _memoryTimer.Tick += delegate
            {
                _memoryTimer.Interval = 600000;
                TrimWorkingSet();
            };
            _memoryTimer.Start();
        }

        private static void TrimWorkingSet()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Optimized, false);
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch (Exception)
            {
                // 收工作集失败没有任何副作用，忽略
            }
        }

        private void ScheduleFirstRunTip()
        {
            System.Windows.Forms.Timer once = new System.Windows.Forms.Timer();
            once.Interval = 1500;
            once.Tick += delegate
            {
                once.Stop();
                once.Dispose();
                if (_firstTipDone || _disposed) return;
                _firstTipDone = true;
                _tray.ShowBalloonTip(7000, "FocusKeep 已启动",
                    "默认已开启屏幕常亮。" +
                    (_cfg.OledGuardEnabled
                        ? "OLED 护屏已启用：每 " + _cfg.OledIntervalMinutes + " 分钟黑屏 " +
                          _cfg.OledBlankSeconds + " 秒给屏幕休息。"
                        : "OLED 护屏当前是关闭状态。") + "\r\n" +
                    "右键（或左键单击）托盘图标打开菜单。",
                    ToolTipIcon.Info);
            };
            once.Start();
        }

        // ---------------- 单实例控制 ----------------

        private void StartWatcher()
        {
            try
            {
                _notifyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, NotifyEventName);
                _menuEvent = new EventWaitHandle(false, EventResetMode.AutoReset, MenuEventName);
                _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
                _restEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RestEventName);

                _watcher = new Thread(Watch);
                _watcher.IsBackground = true;
                _watcher.Name = "FocusKeep.Watcher";
                _watcher.Start();
            }
            catch (Exception ex)
            {
                Log.Error("创建单实例事件", ex);
            }
        }

        private void Watch()
        {
            WaitHandle[] waits = new WaitHandle[] { _notifyEvent, _menuEvent, _quitEvent, _restEvent };
            while (!_exiting)
            {
                int index;
                try { index = WaitHandle.WaitAny(waits, 1000); }
                catch (Exception) { return; }

                if (_exiting) return;
                if (index == 0) Post(delegate { ShowAlreadyRunningHint(); });
                else if (index == 1) Post(delegate { ShowMenuAtCursor(); });
                else if (index == 2) Post(delegate { ExitApp(); });
                else if (index == 3) Post(delegate { _oled.RestNow(); });
            }
        }

        private void Post(SendOrPostCallback callback)
        {
            try { _ui.Post(callback, null); }
            catch (Exception ex) { Log.Error("Post", ex); }
        }

        public void ExitApp()
        {
            _exiting = true;
            Dispose();
            ExitThread();
        }

        // ---------------- 生命周期 ----------------

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }
            _disposed = true;
            _exiting = true;
            Log.Write("=== FocusKeep 退出 ===");

            if (disposing)
            {
                try { SystemEvents.SessionSwitch -= OnSessionSwitch; }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }
                try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }
                try { SystemEvents.SessionEnding -= OnSessionEnding; }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }
                try { SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }

                try
                {
                    if (_hotkeyRegistered && _hotkeyWindow != null && _hotkeyWindow.Handle != IntPtr.Zero)
                        UnregisterHotKey(_hotkeyWindow.Handle, HOTKEY_ID_LOCK);
                }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }

                try
                {
                    if (_quitEvent != null) _quitEvent.Set();
                    if (_watcher != null) _watcher.Join(600);
                    if (_notifyEvent != null) _notifyEvent.Close();
                    if (_menuEvent != null) _menuEvent.Close();
                    if (_quitEvent != null) _quitEvent.Close();
                    if (_restEvent != null) _restEvent.Close();
                }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }

                if (_locker != null) _locker.Dispose();
                if (_oled != null) _oled.Dispose();
                if (_power != null) _power.Dispose();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_hotkeyWindow != null) _hotkeyWindow.Dispose();
                if (_restTimer != null) { _restTimer.Dispose(); _restTimer = null; }
                if (_memoryTimer != null) { _memoryTimer.Dispose(); _memoryTimer = null; }

                for (int i = 0; i < _stateIcons.Length; i++)
                {
                    try { if (_stateIcons[i] != null) _stateIcons[i].Dispose(); }
                    catch (Exception ex) { Debug.WriteLine(ex.Message); }
                    _stateIcons[i] = null;
                }


                if (_invoker != null) _invoker.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
