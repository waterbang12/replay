using System.Text;

namespace LifeReplay.Core;

/// <summary>Small thread-safe file logger: logs/{prefix}-yyyyMMdd.log, kept for 14 days.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static string _prefix = "lifereplay";

    public static event Action<string, string>? Written; // level, message

    public static void Init(string logsDir, string prefix)
    {
        _dir = logsDir;
        _prefix = prefix;
        Directory.CreateDirectory(logsDir);
        try
        {
            foreach (var f in Directory.EnumerateFiles(logsDir, "*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
        }
        catch { /* best effort */ }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append(" [").Append(level).Append("] [t")
          .Append(Environment.CurrentManagedThreadId).Append("] ").Append(message);
        if (ex != null) sb.Append(" | ").Append(ex.GetType().Name).Append(" 0x").Append(ex.HResult.ToString("X8")).Append(": ").Append(ex.Message)
                          .AppendLine().Append(ex.StackTrace);
        var line = sb.ToString();
        System.Diagnostics.Debug.WriteLine(line);
        if (_dir != null)
        {
            lock (Gate)
            {
                try { File.AppendAllText(Path.Combine(_dir, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine); }
                catch { /* never throw from logging */ }
            }
        }
        try { Written?.Invoke(level, message); } catch { }
    }
}
