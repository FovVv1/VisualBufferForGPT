using System;
using System.Diagnostics;
using System.Text;

namespace VisualBuffer.Diagnostics
{
    /// <summary>
    /// Лёгкий логгер: вывод только в Debug Output и/или Console.
    /// Никаких файлов, роллинга и работы с ФС.
    /// </summary>
    public static class Logger
    {
        // --- Опции ---
        public sealed class Options
        {
            public bool DebugEcho { get; set; } = true;      // В окно Output (VS)
            public bool ConsoleEcho { get; set; } = false;   // В Console.Out (если есть консоль)
            public bool IncludeTimestamp { get; set; } = true;
            public bool IncludePidTid { get; set; } = true;
            public string AppName { get; set; } = "VisualBuffer";
        }

        private static readonly object _gate = new();
        private static Options _opt = new();

        // --- Инициализация ---
        public static void Init(Options? opt = null)
        {
            try
            {
                _opt = opt ?? new Options();
                Info("=== App start (console/debug only, no files) ===");
            }
            catch { /* логгер не должен кидать исключения */ }
        }

        // --- Базовые уровни ---
        public static void Info(string message) => Write("INFO", message, null);
        public static void Warn(string message) => Write("WARN", message, null);
        public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

        [Conditional("DEBUG")]
        public static void Debug(string message) => Write("DEBUG", message, null);

        // --- Инстанс-логгер с категорией/ID ---
        public readonly struct Log
        {
            private readonly string _cat;
            private readonly string? _id;
            internal Log(string cat, string? id) { _cat = cat; _id = id; }

            private string Pfx() => _id is null ? _cat : $"{_cat}({_id})";
            public void I(string m) => Write("INFO", $"{Pfx()} | {m}", null);
            public void W(string m) => Write("WARN", $"{Pfx()} | {m}", null);
            public void E(string m, Exception? ex = null) => Write("ERROR", $"{Pfx()} | {m}", ex);

            [Conditional("DEBUG")]
            public void D(string m) => Write("DEBUG", $"{Pfx()} | {m}", null);
        }

        public static Log Create(string category, string? instanceId = null) => new(category, instanceId);

        // --- Перф-скоуп ---
        public static IDisposable Perf(string category, string operation, string? id = null)
            => new PerfScope(Create(category, id), operation);

        private sealed class PerfScope : IDisposable
        {
            private readonly Log _log;
            private readonly string _op;
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            internal PerfScope(Log log, string op) { _log = log; _op = op; _log.D($"▶ {_op}"); }
            public void Dispose() { _sw.Stop(); _log.D($"◀ {_op} | {_sw.ElapsedMilliseconds} ms"); }
        }

        // --- Низкоуровневый вывод ---
        private static void Write(string level, string message, Exception? ex)
        {
            try
            {
                var sb = new StringBuilder();

                if (_opt.IncludeTimestamp)
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ');

                sb.Append(level.PadRight(5));

                if (_opt.IncludePidTid)
                    sb.Append(" (pid:").Append(Environment.ProcessId)
                      .Append(", tid:").Append(Environment.CurrentManagedThreadId).Append(")");

                sb.Append(' ').Append(message);

                if (ex != null)
                {
                    sb.AppendLine()
                      .Append(ex.GetType().FullName).Append(": ").Append(ex.Message)
                      .AppendLine()
                      .Append(ex.StackTrace);
                }

                var line = sb.AppendLine().ToString();

                // Немного синхронизации, чтобы не перемешивать строки при многопоточке
                lock (_gate)
                {
                    if (_opt.DebugEcho)
                        System.Diagnostics.Debug.WriteLine(line);

                    if (_opt.ConsoleEcho)
                    {
                        try { Console.Write(line); } catch { /* GUI-процессы без консоли */ }
                    }
                }
            }
            catch { /* никогда не бросаем исключения из логгера */ }
        }
    }
}
