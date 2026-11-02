using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FocusKeep
{
    /// <summary>
    /// 纯黑全屏遮罩。OLED 屏上黑 = 像素不发光，所以这一块遮罩等效于"熄屏"，
    /// 但不会触发系统的显示电源状态变化，也就不会打断屏幕常亮。
    ///
    /// 关键点：
    ///  1. WS_EX_NOACTIVATE + ShowWithoutActivation → 弹出时不抢焦点，前台程序无感；
    ///  2. WS_EX_TOOLWINDOW + ShowInTaskbar=false → 不进 Alt+Tab、不进任务栏；
    ///  3. 任何鼠标动作立刻上报 DismissRequested，用户回来就消失。
    /// </summary>
    internal sealed class BlankOverlay : Form
    {
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_XBUTTONDOWN = 0x020B;

        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        /// <summary>鼠标一落到遮罩上就请求撤掉（刚弹出的一小段时间内的抖动忽略）。</summary>
        public event EventHandler DismissRequested;

        private int _shownTicks;
        private readonly System.Windows.Forms.Timer _topMostTimer;

        /// <summary>true 时忽略纯鼠标移动，只有点击/滚轮才算"用户要中断"。</summary>
        public bool IgnoreMoveDismiss { get; set; }

        /// <summary>最后一次导致关闭的窗口消息，排障用。</summary>
        public int LastDismissMessage { get; private set; }

        public BlankOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            ForeColor = Color.Black;
            DoubleBuffered = true;
            SetStyle(ControlStyles.Opaque, true);
            Bounds = SystemInformation.VirtualScreen;

            // Win11 的任务栏本身也是"置顶"窗口，偶尔会重新压到遮罩上面
            // （比如自动隐藏的任务栏被鼠标唤出）。显示期间每秒重申一次置顶，
            // 保证任务栏也一起被盖住。
            _topMostTimer = new System.Windows.Forms.Timer();
            _topMostTimer.Interval = 1000;
            _topMostTimer.Tick += delegate { KeepOnTop(); };
        }

        /// <summary>把遮罩重新顶到最前面（不抢焦点、不改大小）。</summary>
        public void KeepOnTop()
        {
            if (!Visible || Handle == IntPtr.Zero) return;

            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (_topMostTimer == null) return;
            if (Visible) _topMostTimer.Start();
            else _topMostTimer.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _topMostTimer.Stop();
                    _topMostTimer.Dispose();
                }
                catch (Exception) { }
            }

            base.Dispose(disposing);
        }

        /// <summary>窗口显示时不激活（不抢前台焦点）。</summary>
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        /// <summary>显示并且保持"不激活、置顶"。多显示器时铺满整个虚拟桌面。</summary>
        public void ShowPassive()
        {
            Bounds = SystemInformation.VirtualScreen;
            _shownTicks = Environment.TickCount;   // 必须在 Show() 之前，Show 期间可能就收到 WM_MOUSEMOVE
            Show();
            if (Handle != IntPtr.Zero)
            {
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEMOVE:
                    // 手动触发的休息：鼠标在动不代表要中断，否则刚点完菜单遮罩就没了
                    if (IgnoreMoveDismiss) break;
                    if (_shownTicks != 0 && unchecked(Environment.TickCount - _shownTicks) > 500) RaiseDismiss(m.Msg);
                    break;

                case WM_LBUTTONDOWN:
                case WM_RBUTTONDOWN:
                case WM_MBUTTONDOWN:
                case WM_MOUSEWHEEL:
                case WM_XBUTTONDOWN:
                    // 窗口刚出现时系统可能补发一次 WM_MOUSEMOVE，别让它自己把自己关掉
                    if (_shownTicks != 0 && unchecked(Environment.TickCount - _shownTicks) > 500) RaiseDismiss(m.Msg);
                    break;
            }
            base.WndProc(ref m);
        }

        private void RaiseDismiss(int message)
        {
            LastDismissMessage = message;
            EventHandler handler = DismissRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// OLED 护屏：屏幕常亮期间，人离开了就让屏幕周期性"休息"——
    /// 用纯黑遮罩盖住所有显示器，OLED 像素基本不发光，等效于熄屏，
    /// 但常亮状态、后台任务、远程会话都不受影响。
    ///
    /// 五个条件全部满足才休息（避免误伤）：
    ///   1. 护屏开关打开、屏幕常亮处于开启状态；
    ///   2. 键鼠空闲 ≥ IdleGateSeconds（人大概率不在）；
    ///   3. 前台窗口不是全屏（不挡视频 / 游戏 / 幻灯片放映）；
    ///   4. 系统没有正在出声（不挡正在看 / 听的内容）；
    ///   5. 距上次休息已满一个间隔。
    /// 休息期间任何键鼠输入都会立刻撤销遮罩。
    /// </summary>
    internal sealed class OledGuard : IDisposable
    {
        public const int MinIntervalMinutes = 5;
        public const int MaxIntervalMinutes = 180;
        public const int MinBlankSeconds = 10;
        public const int MaxBlankSeconds = 180;

        /// <summary>键鼠至少空闲这么久才认为"人不在"。</summary>
        public const int IdleGateSeconds = 60;

        /// <summary>响过声后，至少安静这么久才允许黑屏。</summary>
        private const int AudioHoldSeconds = 45;
        private const double AudioPeakThreshold = 0.002;

        private const int FastTickMs = 250;    // 遮罩显示期间：快速响应"人回来了"
        private const int SlowTickMs = 5000;   // 平时：5 秒一次足够，少唤醒 CPU

        /// <summary>
        /// 仅用于自动化验证：设置环境变量 FOCUSKEEP_OLED_TEST=1 后，
        /// 空闲门槛缩到 5 秒、间隔缩到 15 秒、黑屏缩到 3 秒，方便一分钟内看完整个流程。
        /// </summary>
        private static readonly bool FastMode = string.Equals(
            Environment.GetEnvironmentVariable("FOCUSKEEP_OLED_TEST"), "1", StringComparison.Ordinal);

        /// <summary>排障用：FOCUSKEEP_DEBUG=1 时每 5 秒把判定依据写一行日志。</summary>
        private static readonly bool DebugMode = string.Equals(
            Environment.GetEnvironmentVariable("FOCUSKEEP_DEBUG"), "1", StringComparison.Ordinal);

        private DateTime _lastDebugUtc = DateTime.MinValue;
        private float _lastPeak;

        private static int IdleGate { get { return FastMode ? 5 : IdleGateSeconds; } }
        private int BlankSecondsNow { get { return FastMode ? 3 : _blankSeconds; } }
        private double IntervalSecondsNow { get { return FastMode ? 15.0 : _intervalMinutes * 60.0; } }

        private readonly SynchronizationContext _ui;
        private readonly object _gate = new object();
        private readonly System.Threading.Timer _timer;

        private volatile bool _enabled;
        private volatile bool _screenOn = true;
        private volatile bool _restVisible;
        private volatile bool _manualRest;
        private volatile bool _disposed;
        private volatile bool _audioUnsupported;

        private int _intervalMinutes = 20;
        private int _blankSeconds = 30;
        private DateTime _restStartUtc = DateTime.MinValue;
        private DateTime _lastRestEndUtc = DateTime.MinValue;
        private DateTime _lastAudioUtc = DateTime.MinValue;
        private int _restCount;
        private string _lastReason = string.Empty;

        private BlankOverlay _overlay;
        private int _audioFailures;
        private IMMDeviceEnumerator _enumerator;
        private IAudioMeterInformation _meter;

        public OledGuard(SynchronizationContext ui)
        {
            _ui = ui ?? new SynchronizationContext();
            _timer = new System.Threading.Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public bool Enabled { get { return _enabled; } }
        public int IntervalMinutes { get { return _intervalMinutes; } }
        public int BlankSeconds { get { return _blankSeconds; } }
        public bool IsResting { get { return _restVisible; } }
        public int RestCount { get { return _restCount; } }
        public string LastReason { get { return _lastReason; } }

        public string Describe()
        {
            if (!_enabled) return "已关闭";
            return "每 " + _intervalMinutes + " 分钟休息 " + _blankSeconds + " 秒";
        }

        /// <summary>读取配置 / 菜单改动后调用，立即生效。</summary>
        public void Configure(bool enabled, int intervalMinutes, int blankSeconds)
        {
            lock (_gate)
            {
                _enabled = enabled;
                _intervalMinutes = Clamp(intervalMinutes, MinIntervalMinutes, MaxIntervalMinutes);
                _blankSeconds = Clamp(blankSeconds, MinBlankSeconds, MaxBlankSeconds);
                _lastRestEndUtc = DateTime.UtcNow;      // 改设置后重新计时
            }

            if (!enabled) RequestHide("护屏已关闭");
            UpdateTimer();
            Log.Write("OLED 护屏 -> " + Describe());
        }

        /// <summary>屏幕常亮开关变化时同步：关掉常亮就不需要护屏了。</summary>
        public void SetScreenOn(bool on)
        {
            _screenOn = on;
            if (!on) RequestHide("屏幕常亮已关闭");
            UpdateTimer();
        }

        /// <summary>
        /// 定时器只在"需要看"的时候跑：
        /// 护屏关掉或屏幕常亮关掉时直接停掉，避免每秒空转唤醒 CPU；
        /// 休息期间用 250ms 的快速节拍，其余时间 5 秒一次足够。
        /// </summary>
        private void UpdateTimer()
        {
            if (_disposed) return;

            bool resting = _restVisible;
            bool wanted = resting || (_enabled && _screenOn);

            try
            {
                if (!wanted) _timer.Change(Timeout.Infinite, Timeout.Infinite);
                else if (resting) _timer.Change(0, FastTickMs);
                else _timer.Change(SlowTickMs, SlowTickMs);
            }
            catch (ObjectDisposedException)
            {
                // 退出过程中定时器已释放，忽略
            }
        }

        /// <summary>立刻休息一次（菜单手动触发），不看空闲/声音/全屏条件。</summary>
        public void RestNow()
        {
            ShowRest(true);
        }

        /// <summary>立刻撤掉遮罩（切会话、休眠、退出等场景）。</summary>
        public void HideNow(string reason)
        {
            HideRest(reason, true);
        }

        // ---------------- 调度 ----------------

        private void Tick(object state)
        {
            if (_disposed) return;

            try
            {
                DateTime now = DateTime.UtcNow;
                int idle = IdleSeconds();

                if (_enabled && _screenOn && !_audioUnsupported) SampleAudio(now);

                bool visible = _restVisible;

                if (DebugMode && (now - _lastDebugUtc).TotalSeconds >= 5)
                {
                    _lastDebugUtc = now;
                    DateTime lastEndDbg, lastAudioDbg;
                    lock (_gate) { lastEndDbg = _lastRestEndUtc; lastAudioDbg = _lastAudioUtc; }
                    Log.Write(string.Format(
                        "护屏探测：空闲 {0}s / 距上次休息 {1:F0}s / 最近有声 {2:F0}s 前 / 峰值 {3:0.0000} / 全屏 {4} / 遮罩 {5}",
                        idle, (now - lastEndDbg).TotalSeconds, (now - lastAudioDbg).TotalSeconds,
                        _lastPeak, ForegroundIsFullscreen(), visible));
                }

                if (visible)
                {
                    if (_manualRest)
                    {
                        // 手动触发的休息不受空闲/开关影响：只认"到点"和"用户点一下鼠标"
                        DateTime manualStart;
                        lock (_gate) { manualStart = _restStartUtc; }
                        if ((now - manualStart).TotalSeconds >= BlankSecondsNow) RequestHide("休息结束");
                        return;
                    }

                    if (!_enabled || !_screenOn) { RequestHide("护屏已关闭"); return; }
                    if (idle < IdleGate) { RequestHide("检测到键鼠输入"); return; }

                    DateTime started;
                    lock (_gate) { started = _restStartUtc; }
                    if ((now - started).TotalSeconds >= BlankSecondsNow) RequestHide("休息结束");
                    return;
                }

                if (!_enabled || !_screenOn) return;

                DateTime lastEnd;
                DateTime lastAudio;
                lock (_gate)
                {
                    lastEnd = _lastRestEndUtc;
                    lastAudio = _lastAudioUtc;
                }

                if (idle < IdleGate) return;
                if ((now - lastEnd).TotalSeconds < IntervalSecondsNow) return;
                if ((now - lastAudio).TotalSeconds < AudioHoldSeconds) return;
                if (ForegroundIsFullscreen()) return;

                RequestShow();
            }
            catch (Exception ex)
            {
                Log.Error("护屏计时", ex);
            }
        }

        private void RequestShow()
        {
            _ui.Post(delegate { ShowRest(false); }, null);
        }

        private void RequestHide(string reason)
        {
            _ui.Post(delegate { HideRest(reason, false); }, null);
        }

        /// <summary>必须在 UI 线程执行。</summary>
        private void ShowRest(bool manual)
        {
            if (_disposed) return;

            lock (_gate)
            {
                if (_restVisible) return;
                _restVisible = true;
                _restStartUtc = DateTime.UtcNow;
                _manualRest = manual;
            }

            try
            {
                if (_overlay == null || _overlay.IsDisposed)
                {
                    _overlay = new BlankOverlay();
                    _overlay.DismissRequested += delegate
                    {
                        HideRest("检测到键鼠输入（消息 0x" +
                            _overlay.LastDismissMessage.ToString("X2") + "）", false);
                    };
                }

                // 手动休息时忽略鼠标移动，避免"点完菜单手一划就没了"
                _overlay.IgnoreMoveDismiss = manual;
                _overlay.ShowPassive();
                UpdateTimer();

                string reason = manual ? "手动触发" : "空闲 " + IdleSeconds() + " 秒";
                lock (_gate) { _lastReason = reason; }
                Log.Write("OLED 护屏：开始休息 " + BlankSecondsNow + " 秒（" + reason + "）");
            }
            catch (Exception ex)
            {
                Log.Error("显示护屏遮罩", ex);
                lock (_gate) { _restVisible = false; _manualRest = false; }
                UpdateTimer();
            }
        }

        /// <summary>必须在 UI 线程执行。</summary>
        private void HideRest(string reason, bool force)
        {
            bool wasVisible;
            lock (_gate)
            {
                wasVisible = _restVisible;
                if (!wasVisible && !force) return;
                _restVisible = false;
                _manualRest = false;
                _lastRestEndUtc = DateTime.UtcNow;
            }

            try
            {
                if (_overlay != null && !_overlay.IsDisposed && _overlay.Visible) _overlay.Hide();
            }
            catch (Exception ex)
            {
                Log.Error("关闭护屏遮罩", ex);
            }

            if (_disposed) return;
            UpdateTimer();

            if (wasVisible)
            {
                _restCount++;
                lock (_gate) { _lastReason = reason; }
                Log.Write("OLED 护屏：结束（" + reason + "）");
            }
        }

        // ---------------- 环境探测 ----------------

        /// <summary>键鼠空闲秒数。</summary>
        public static int IdleSeconds()
        {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref info)) return int.MaxValue;

            uint idle = unchecked((uint)Environment.TickCount) - info.dwTime;
            if (idle > int.MaxValue) idle = 0;
            return (int)(idle / 1000);
        }

        /// <summary>前台窗口是否是一个真正的全屏窗口（最大化窗口不算）。</summary>
        public static bool ForegroundIsFullscreen()
        {
            IntPtr handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return false;
            if (IsZoomed(handle)) return false;          // 最大化 ≠ 全屏

            StringBuilder cls = new StringBuilder(64);
            GetClassName(handle, cls, cls.Capacity);
            string name = cls.ToString();
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd") return false;

            RECT rect;
            if (!GetWindowRect(handle, out rect)) return false;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;

            Rectangle bounds = Screen.FromHandle(handle).Bounds;
            return width >= bounds.Width && height >= bounds.Height;
        }

        /// <summary>当前是否有声音在播（取默认输出设备的峰值）。失败一律当作"没声音"。</summary>
        public static double AudioPeak()
        {
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
            IMMDevice device;
            int hr = enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out device);
            if (hr != 0 || device == null) throw new COMException("GetDefaultAudioEndpoint", hr);

            try
            {
                Guid iid = typeof(IAudioMeterInformation).GUID;
                object raw;
                hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out raw);
                if (hr != 0 || raw == null) throw new COMException("Activate", hr);

                IAudioMeterInformation meter = (IAudioMeterInformation)raw;
                float peak;
                hr = meter.GetPeakValue(out peak);
                if (hr != 0) throw new COMException("GetPeakValue", hr);
                return peak;
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
            }
        }

        private void SampleAudio(DateTime now)
        {
            try
            {
                if (_meter == null)
                {
                    if (_enumerator == null)
                        _enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());

                    IMMDevice device;
                    int hr = _enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out device);
                    if (hr != 0 || device == null) throw new COMException("GetDefaultAudioEndpoint", hr);

                    try
                    {
                        Guid iid = typeof(IAudioMeterInformation).GUID;
                        object raw;
                        hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out raw);
                        if (hr != 0 || raw == null) throw new COMException("Activate", hr);
                        _meter = (IAudioMeterInformation)raw;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }

                float peak;
                int hresult = _meter.GetPeakValue(out peak);
                if (hresult != 0)
                {
                    ReleaseAudio();
                    throw new COMException("GetPeakValue", hresult);
                }

                _audioFailures = 0;
                _lastPeak = peak;
                if (peak > AudioPeakThreshold) lock (_gate) { _lastAudioUtc = now; }
            }
            catch (Exception)
            {
                _audioFailures++;
                if (_audioFailures >= 5)
                {
                    _audioUnsupported = true;
                    ReleaseAudio();
                    Log.Write("音频检测不可用（" + _audioFailures + " 次失败），护屏不再考虑声音状态");
                }
            }
        }

        private void ReleaseAudio()
        {
            try { if (_meter != null) Marshal.ReleaseComObject(_meter); }
            catch (Exception) { }
            _meter = null;
        }

        // ---------------- 清理 ----------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (Exception) { }
            try { _timer.Dispose(); }
            catch (Exception) { }

            try
            {
                if (_overlay != null && !_overlay.IsDisposed) _overlay.Dispose();
            }
            catch (Exception ex) { Log.Error("释放护屏遮罩", ex); }
            _overlay = null;
            _restVisible = false;

            ReleaseAudio();
            try
            {
                if (_enumerator != null) Marshal.ReleaseComObject(_enumerator);
            }
            catch (Exception) { }
            _enumerator = null;

            Log.Write("OLED 护屏已释放，累计休息 " + _restCount + " 次");
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // ---------------- 原生 ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private const int CLSCTX_ALL = 0x17;
        private const int EDataFlowRender = 0;      // eRender
        private const int ERoleConsole = 0;         // eConsole

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice(string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object deviceInterface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport]
        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out int channelCount);
            [PreserveSig] int GetChannelsPeakValues(int channelCount, [Out] float[] peakValues);
            [PreserveSig] int QueryHardwareSupport(out int hardwareSupportMask);
        }
    }
}
