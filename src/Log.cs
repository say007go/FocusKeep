using System;
using System.IO;
using System.Text;

namespace FocusKeep
{
    /// <summary>极简日志：出问题时有据可查，正常时不打扰用户。</summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 256 * 1024;

        public static string LogPath
        {
            get
            {
                string dir = Path.GetDirectoryName(Config.ConfigPath);
                return Path.Combine(dir, "focuskeep.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    string path = LogPath;
                    string dir = Path.GetDirectoryName(path);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string backup = path + ".old";
                        try { if (File.Exists(backup)) File.Delete(backup); }
                        catch (Exception) { }
                        try { File.Move(path, backup); }
                        catch (Exception) { }
                    }

                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + "\r\n",
                        new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // 日志本身失败绝不能影响主流程
            }
        }

        public static void Error(string where, Exception ex)
        {
            Write("[ERROR] " + where + " : " + (ex == null ? "unknown" : ex.ToString()));
        }
    }
}
