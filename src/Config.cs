using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FocusKeep
{
    /// <summary>配置读写：纯文本 INI。</summary>
    internal sealed class Config
    {
        public const string FolderName = "FocusKeep";
        public const string FileName = "config.ini";

        /// <summary>键盘锁定的硬超时（分钟）。</summary>
        public int MaxLockMinutes = 60;

        /// <summary>OLED 护屏总开关（屏幕常亮期间周期性黑屏休息）。</summary>
        public bool OledGuardEnabled = true;

        /// <summary>两次休息之间至少间隔多久（分钟）。</summary>
        public int OledIntervalMinutes = 20;

        /// <summary>每次休息黑屏多长时间（秒）。</summary>
        public int OledBlankSeconds = 30;

        private static string _resolvedDir;

        /// <summary>
        /// 配置目录按优先级挑选，并实际探测可写性：
        /// 1) 环境变量 FOCUSKEEP_CONFIG_DIR（便携模式）
        /// 2) exe 同目录已存在的 data\（U 盘绿色版）
        /// 3) %APPDATA%\FocusKeep（标准位置）
        /// 4) exe 同目录的 data\（漫游配置不可写 / 绿色版）
        /// 5) %TEMP%\FocusKeep（兜底）
        /// </summary>
        public static string ConfigDirectory
        {
            get
            {
                if (_resolvedDir != null) return _resolvedDir;

                string overrideDir = Environment.GetEnvironmentVariable("FOCUSKEEP_CONFIG_DIR");
                string exeDir = string.Empty;
                try
                {
                    string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                    exeDir = Path.Combine(Path.GetDirectoryName(exe), "data");
                }
                catch (Exception) { }

                string appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);
                string tempDir = Path.Combine(Path.GetTempPath(), FolderName);

                // 1) 环境变量显式指定
                if (!string.IsNullOrEmpty(overrideDir) && TryPrepare(overrideDir))
                    return _resolvedDir = overrideDir;

                // 2) exe 旁边已经有 data\ = 用户明确要绿色便携，配置跟 exe 走
                if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir) && TryPrepare(exeDir))
                    return _resolvedDir = exeDir;

                // 3) 标准位置
                if (TryPrepare(appDataDir)) return _resolvedDir = appDataDir;

                // 4) 兜底：exe 同目录新建 data\
                if (!string.IsNullOrEmpty(exeDir) && TryPrepare(exeDir)) return _resolvedDir = exeDir;

                // 5) 最后兜底
                if (TryPrepare(tempDir)) return _resolvedDir = tempDir;

                _resolvedDir = Path.GetTempPath();
                return _resolvedDir;
            }
        }

        /// <summary>建目录并真的写一次探针文件，确认当前令牌能落盘。</summary>
        private static bool TryPrepare(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDirectory, FileName); }
        }

        public static Config Load()
        {
            Config cfg = new Config();
            string path = ConfigPath;

            if (!File.Exists(path))
            {
                try { cfg.Save(); }
                catch (Exception ex) { Log.Write("首次创建配置失败：" + ex.Message); }
                return cfg;
            }

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    if (string.Equals(key, "MaxLockMinutes", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                            cfg.MaxLockMinutes = Clamp(n, 1, 480);
                    }
                    else if (string.Equals(key, "OledGuardEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.OledGuardEnabled = ParseBool(val, true);
                    }
                    else if (string.Equals(key, "OledIntervalMinutes", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                            cfg.OledIntervalMinutes = Clamp(n,
                                OledGuard.MinIntervalMinutes, OledGuard.MaxIntervalMinutes);
                    }
                    else if (string.Equals(key, "OledBlankSeconds", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                            cfg.OledBlankSeconds = Clamp(n,
                                OledGuard.MinBlankSeconds, OledGuard.MaxBlankSeconds);
                    }
                    // 旧版本遗留的 KeepDisplay= / Schedule= 行忽略，保证升级不报错
                }
            }
            catch (Exception ex)
            {
                Log.Write("读取配置失败：" + ex.Message);
            }

            return cfg;
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# FocusKeep 配置（可手工编辑，改完重启程序生效）");
            sb.AppendLine("# MaxLockMinutes      : 键盘锁定硬超时（1-480 分钟），到点强制解锁");
            sb.AppendLine("# OledGuardEnabled    : OLED 护屏开关（1/0），屏幕常亮期间周期性黑屏休息");
            sb.AppendLine("# OledIntervalMinutes : 两次休息的最小间隔（5-180 分钟）");
            sb.AppendLine("# OledBlankSeconds    : 每次休息黑屏时长（10-180 秒）");
            sb.AppendLine();
            sb.AppendLine("MaxLockMinutes=" + MaxLockMinutes);
            sb.AppendLine("OledGuardEnabled=" + (OledGuardEnabled ? 1 : 0));
            sb.AppendLine("OledIntervalMinutes=" + OledIntervalMinutes);
            sb.AppendLine("OledBlankSeconds=" + OledBlankSeconds);

            string path = ConfigPath;
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // 先写临时文件再原子替换：中途断电/崩溃不会留下半截配置
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception) { }
            }
        }

        internal static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        /// <summary>接受 1/0、true/false、on/off、yes/no，认不出来就用默认值。</summary>
        private static bool ParseBool(string val, bool fallback)
        {
            if (string.IsNullOrEmpty(val)) return fallback;

            string v = val.Trim();
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;

            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;

            return fallback;
        }
    }
}
