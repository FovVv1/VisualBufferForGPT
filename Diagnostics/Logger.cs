using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace VisualBuffer.Diagnostics
{
    public static class Logger
    {
        private static readonly object _gate = new();
        private static string _dir = "";
        private static string _file = "";

        public static void Init()
        {
            try
            {
                _dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VisualBuffer", "logs");

                Directory.CreateDirectory(_dir);
                _file = Path.Combine(_dir, $"log-{DateTime.Now:yyyyMMdd}.txt");
                Info("=== App start ===");
            }
            catch { /* logging must never throw */ }
        }

        public static void Info(string message) => Write("INFO", message, null);
        public static void Warn(string message) => Write("WARN", message, null);
        public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception? ex)
        {
            try
            {
                var sb = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] ")
                    .Append("(pid:").Append(Environment.ProcessId)
                    .Append(", tid:").Append(Environment.CurrentManagedThreadId).Append(") ")
                    .Append(message);

                if (ex != null)
                {
                    sb.AppendLine().Append(ex.GetType().FullName + ": " + ex.Message)
                      .AppendLine().Append(ex.StackTrace);
                }

                lock (_gate)
                {
                    File.AppendAllText(_file, sb.AppendLine().ToString(), Encoding.UTF8);
                }
            }
            catch { /* swallow */ }
        }
    }
}
