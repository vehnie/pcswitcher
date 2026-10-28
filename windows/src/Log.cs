using System;
using System.IO;
using System.Text;

namespace MonitorAutoSwitch
{
    /// Rotating file logger. Also echoes to stderr when Verbose is set (CLI / `mas watch`).
    static class Log
    {
        public static bool Verbose;
        const long MaxBytes = 2 * 1024 * 1024;
        const int KeepFiles = 3;
        static readonly object Sync = new object();

        public static string LogDir { get { return Path.Combine(Config.ConfigDir, "logs"); } }
        public static string FilePath { get { return Path.Combine(LogDir, "mas.log"); } }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }
        public static void Debug(string msg) { Write("DEBUG", msg); }

        static void Write(string level, string msg)
        {
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}", DateTime.Now, level, msg);
            if (Verbose)
            {
                try { Console.Error.WriteLine(line); } catch { }
            }
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(LogDir);
                    RotateIfNeeded();
                    using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        w.WriteLine(line);
                    }
                }
                catch { }
            }
        }

        static void RotateIfNeeded()
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length <= MaxBytes) return;
            for (int i = KeepFiles - 1; i >= 1; i--)
            {
                string src = Path.Combine(LogDir, i == 1 ? "mas.log" : "mas." + (i - 1) + ".log");
                string dst = Path.Combine(LogDir, "mas." + i + ".log");
                try { if (File.Exists(dst)) File.Delete(dst); if (File.Exists(src)) File.Move(src, dst); } catch { }
            }
        }
    }
}
