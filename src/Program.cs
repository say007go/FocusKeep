using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FocusKeep
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const int ATTACH_PARENT_PROCESS = -1;
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        [STAThread]
        private static void Main(string[] args)
        {
            if (HasFlag(args, "--icon-preview"))
            {
                int iconSize = 128;
                string sizeArg = ArgValue(args, "--icon-size");
                int parsedSize;
                if (sizeArg != null && int.TryParse(sizeArg, out parsedSize)) iconSize = parsedSize;

                RenderIconPreviews(ArgValue(args, "--icon-preview") ?? Path.GetTempPath(), iconSize);
                return;
            }

            if (HasFlag(args, "--selftest"))
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                Environment.Exit(SelfTest());
                return;
            }

            if (HasFlag(args, "--blank-preview"))
            {
                int seconds = 3;
                string raw = ArgValue(args, "--blank-preview");
                int parsed;
                if (raw != null && int.TryParse(raw, out parsed)) seconds = parsed;

                Application.EnableVisualStyles();
                RunBlankPreview(seconds);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                Log.Error("UI 线程未处理异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log.Error("未处理异常", e.ExceptionObject as Exception);
            };

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "FocusKeep.SingleInstance.v1", out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例：默认只发提示气泡；--menu 弹菜单；--quit 让它退出
                    string eventName = AppContext.NotifyEventName;
                    if (HasFlag(args, "--quit")) eventName = AppContext.QuitEventName;
                    else if (HasFlag(args, "--menu")) eventName = AppContext.MenuEventName;
                    else if (HasFlag(args, "--rest")) eventName = AppContext.RestEventName;

                    try
                    {
                        using (EventWaitHandle signal = EventWaitHandle.OpenExisting(eventName))
                        {
                            signal.Set();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("通知已有实例失败：" + ex.Message);
                    }
                    return;
                }

                try
                {
                    using (AppContext context = new AppContext())
                    {
                        Application.Run(context);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("启动失败", ex);
                    MessageBox.Show("FocusKeep 启动失败：\r\n" + ex.Message +
                                    "\r\n\r\n日志：\r\n" + Log.LogPath,
                        "FocusKeep", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    try { mutex.ReleaseMutex(); }
                    catch (Exception) { }
                }
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>取 --xxx 后面跟的参数值；用 --xxx=value 也行。</summary>
        private static string ArgValue(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1] : null;

                string prefix = flag + "=";
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(prefix.Length);
            }
            return null;
        }

        /// <summary>把四种组合（常亮/关闭 × 是否锁定）的图标导出成 PNG，便于核对配色。</summary>
        private static void RenderIconPreviews(string dir, int size)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                bool[] screenStates = new bool[] { true, false };
                bool[] lockStates = new bool[] { false, true };

                for (int s = 0; s < screenStates.Length; s++)
                {
                    for (int l = 0; l < lockStates.Length; l++)
                    {
                        string name = "icon-" + (screenStates[s] ? "On" : "Off") + (lockStates[l] ? "-Locked" : "");
                        using (System.Drawing.Bitmap bmp = TrayIconFactory.RenderPreview(screenStates[s], lockStates[l], size))
                        {
                            bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("导出图标预览", ex);
            }
        }

        /// <summary>
        /// 手动验证护屏遮罩：铺一块纯黑全屏 N 秒（默认 3 秒），
        /// 用来确认多显示器都被盖住、且到点自动撤掉。
        /// </summary>
        private static void RunBlankPreview(int seconds)
        {
            seconds = Math.Max(1, Math.Min(60, seconds));
            try
            {
                using (BlankOverlay overlay = new BlankOverlay())
                {
                    overlay.ShowPassive();
                    DateTime until = DateTime.UtcNow.AddSeconds(seconds);
                    while (DateTime.UtcNow < until)
                    {
                        Application.DoEvents();
                        Thread.Sleep(20);
                    }
                    overlay.Hide();
                }
                try { Console.WriteLine("blank preview ok (" + seconds + "s)"); }
                catch (Exception) { }
            }
            catch (Exception ex)
            {
                Log.Error("遮罩预览", ex);
            }
        }

        /// <summary>无界面自检：验证配置读写、图标绘制、电源 API。不装钩子、不改用户状态。</summary>
        private static int SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            int code = 0;

            try
            {
                sb.AppendLine("FocusKeep selftest");
                sb.AppendLine("os        : " + Environment.OSVersion);
                sb.AppendLine("clr       : " + Environment.Version);
                sb.AppendLine("config    : " + Config.ConfigPath);
                sb.AppendLine("log       : " + Log.LogPath);

                Config cfg = Config.Load();
                sb.AppendLine("maxLockMinutes : " + cfg.MaxLockMinutes);
                sb.AppendLine("oledGuard      : " + (cfg.OledGuardEnabled ? "on" : "off") +
                              " / " + cfg.OledIntervalMinutes + "min / " + cfg.OledBlankSeconds + "s");

                // 配置往返
                int original = cfg.MaxLockMinutes;
                bool originalOled = cfg.OledGuardEnabled;
                int originalInterval = cfg.OledIntervalMinutes;
                int originalBlank = cfg.OledBlankSeconds;

                cfg.MaxLockMinutes = 7;
                cfg.OledGuardEnabled = false;
                cfg.OledIntervalMinutes = 15;
                cfg.OledBlankSeconds = 45;
                cfg.Save();
                Config reloaded = Config.Load();
                bool roundTrip = reloaded.MaxLockMinutes == 7
                    && reloaded.OledGuardEnabled == false
                    && reloaded.OledIntervalMinutes == 15
                    && reloaded.OledBlankSeconds == 45;
                sb.AppendLine("config round-trip : " + (roundTrip ? "OK" : "FAILED"));
                if (!roundTrip) code = 2;

                cfg.MaxLockMinutes = original;
                cfg.OledGuardEnabled = originalOled;
                cfg.OledIntervalMinutes = originalInterval;
                cfg.OledBlankSeconds = originalBlank;
                cfg.Save();

                // 图标绘制
                sb.AppendLine("--- tray icons ---");
                bool[] screenStates = new bool[] { true, false };
                bool[] lockStates = new bool[] { false, true };
                for (int s = 0; s < screenStates.Length; s++)
                {
                    for (int l = 0; l < lockStates.Length; l++)
                    {
                        string name = (screenStates[s] ? "on" : "off") + (lockStates[l] ? "+lock" : "");
                        using (System.Drawing.Icon icon = TrayIconFactory.Create(screenStates[s], lockStates[l]))
                        {
                            bool ok = icon != null && icon.Width >= 16 && icon.Height >= 16;
                            sb.AppendLine("  " + name + " -> " +
                                          (ok ? (icon.Width + "x" + icon.Height + " OK") : "FAILED"));
                            if (!ok) code = 4;
                        }
                    }
                }

                // 电源 API 探针（立即复位）
                sb.AppendLine("--- power api probe ---");
                uint before = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                sb.AppendLine("  assert  system+display : " +
                    (before != 0 ? "accepted (prev=0x" + before.ToString("X8") + ")" : "REJECTED"));
                uint after = SetThreadExecutionState(ES_CONTINUOUS);
                sb.AppendLine("  release ES_CONTINUOUS  : " +
                    (after != 0 ? "accepted (prev=0x" + after.ToString("X8") + ")" : "REJECTED"));
                if (before == 0 || after == 0) code = 3;

                // OLED 护屏能力探针（只读，不弹遮罩）
                sb.AppendLine("--- oled guard probe ---");
                System.Drawing.Rectangle vs = SystemInformation.VirtualScreen;
                sb.AppendLine("  virtual screen        : " + vs.X + "," + vs.Y + " " + vs.Width + "x" + vs.Height);
                sb.AppendLine("  idle seconds          : " + OledGuard.IdleSeconds());
                sb.AppendLine("  foreground fullscreen : " + OledGuard.ForegroundIsFullscreen());
                try
                {
                    double peak = OledGuard.AudioPeak();
                    sb.AppendLine("  audio peak            : " + peak.ToString("0.0000") +
                                  (peak > 0.002 ? " (有声音)" : " (静音)"));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  audio peak            : 不可用（" + ex.GetType().Name + "，护屏将不考虑声音）");
                }

                sb.AppendLine("RESULT: " + (code == 0 ? "ALL OK" : "FAILED code=" + code));
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex);
                code = 1;
            }

            string text = sb.ToString();

            // 只有双击运行（没有控制台）时才落一份临时报告，避免平时留下无谓文件
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "focuskeep-selftest.txt"),
                        text, new UTF8Encoding(false));
                }
                catch (Exception) { }
            }

            try { Console.WriteLine(text); } catch (Exception) { }
            return code;
        }
    }
}
