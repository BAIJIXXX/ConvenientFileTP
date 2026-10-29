using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ConvenientFileTP.Core
{
    /// <summary>Simple thread-safe file logger. Never throws into callers.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _file;
        private static bool _initialized;

        public static string LogFile
        {
            get { return _file; }
        }

        public static void Initialize(string filePath)
        {
            lock (Gate)
            {
                try
                {
                    _file = filePath;
                    string dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    if (File.Exists(filePath) && new FileInfo(filePath).Length > 2 * 1024 * 1024)
                    {
                        string backup = filePath + ".old";
                        try
                        {
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(filePath, backup);
                        }
                        catch { }
                    }
                    _initialized = true;
                    WriteUnlocked("INFO", "===== ConvenientFileTP started =====");
                }
                catch { }
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Debug(string message)
        {
            if (StartupOptions.Current != null && StartupOptions.Current.RichDebug) Write("DEBUG", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + (ex == null ? "" : "\r\n" + ex));
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        private static void Write(string level, string message)
        {
            lock (Gate)
            {
                if (!_initialized)
                {
                    try
                    {
                        string fallback = Path.Combine(Path.GetTempPath(), "ConvenientFileTP.log");
                        _file = fallback;
                        _initialized = true;
                    }
                    catch { return; }
                }
                WriteUnlocked(level, message);
            }
        }

        private static void WriteUnlocked(string level, string message)
        {
            try
            {
                var sb = new StringBuilder(160);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(" [").Append(level).Append("] [T").Append(Thread.CurrentThread.ManagedThreadId).Append("] ");
                sb.Append(message);
                sb.Append("\r\n");
                File.AppendAllText(_file, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
