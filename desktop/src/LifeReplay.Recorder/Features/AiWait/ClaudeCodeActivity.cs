using LifeReplay.Core;

namespace LifeReplay.Recorder.Features.AiWait;

/// <summary>
/// Whether Claude Code is working right now. It watches the transcripts in %USERPROFILE%\.claude\projects (JSON Lines,
/// written as Claude works) and reads only the end of the file that changed: working = the last message is a prompt,
/// a tool call or a tool result; finished = the last assistant message ended its turn. No content is kept.
/// A transcript that hasn't changed for <see cref="StaleMs"/> counts as finished (e.g. an interrupted turn).
/// </summary>
internal sealed class ClaudeCodeActivity : IDisposable
{
    public const long StaleMs = 3 * 60_000;
    private const int TailBytes = 256 * 1024;

    private readonly FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private string? _lastPath;
    private long _lastWriteMs;
    private long _lastCheckMs;
    private bool _busy;

    public ClaudeCodeActivity()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(dir)) return;
        try
        {
            _watcher = new FileSystemWatcher(dir, "*.jsonl") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Changed += (_, e) => { lock (_gate) { _lastPath = e.FullPath; _lastWriteMs = TimeUtil.NowMs(); } };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Warn("Claude Code activity: can't watch transcripts", ex); }
    }

    /// <summary>Checked once a second by the focus tracker; the file is read at most once per change, at most every 2 s.</summary>
    public bool Busy
    {
        get
        {
            string? path;
            long write, now = TimeUtil.NowMs();
            lock (_gate)
            {
                path = _lastPath;
                write = _lastWriteMs;
                if (path == null || now - write > StaleMs) return false;
                if (_lastCheckMs >= write || now - _lastCheckMs < 2000) return _busy;
                _lastCheckMs = now;
            }
            bool busy = ReadBusy(path) ?? true; // just written and unreadable: probably mid-write, so working
            lock (_gate) _busy = busy;
            return busy;
        }
    }

    private static bool? ReadBusy(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
            return AiTools.TranscriptBusy(reader.ReadToEnd());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose() => _watcher?.Dispose();
}
