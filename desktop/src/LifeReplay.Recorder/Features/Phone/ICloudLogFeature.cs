using LifeReplay.Core;

namespace LifeReplay.Recorder.Features.Phone;

/// <summary>
/// Reads the log file(s) that the iPhone's Shortcuts automations append to in iCloud Drive
/// ("LifeReplay*.txt" in the Shortcuts folder, which iCloud for Windows shows as iCloud~is~workflow~my~workflows),
/// and stores the events. Checks every 30 s and only reads a file again when its size or time changed.
/// Re-reading is harmless: event ids come from app + event + second, so known lines are duplicates.
/// The phone writes the time, so events made while the PC was off arrive with their real times.
/// </summary>
internal sealed class ICloudLogFeature : IRecorderFeature
{
    private readonly RecorderHost _host;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<string, (long Size, DateTime Write)> _seen = new(StringComparer.OrdinalIgnoreCase);
    private int _running;

    public string? LastFile { get; private set; }
    public long? LastReadUtcMs { get; private set; }
    public string? LastError { get; private set; }

    public string Name => "iPhone iCloud log";

    public ICloudLogFeature(RecorderHost host)
    {
        _host = host;
        _timer = new System.Threading.Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(5_000, 30_000);

    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "iCloudDrive", "iCloud~is~workflow~my~workflows");

    private string Folder() => string.IsNullOrWhiteSpace(_host.Config.ICloudLog.Folder) ? DefaultFolder() : _host.Config.ICloudLog.Folder;

    public void Check()
    {
        if (!_host.Config.ICloudLog.Enabled || Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var dir = Folder();
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "LifeReplay*.txt", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }))
                Import(file);
        }
        catch (Exception ex) { LastError = ex.Message; Log.Warn("iCloud log: check failed", ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private void Import(string file)
    {
        var info = new FileInfo(file);
        var stamp = (info.Length, info.LastWriteTimeUtc);
        if (_seen.TryGetValue(file, out var old) && old == stamp) return;
        string text;
        try
        {
            // Opening a cloud-only file makes iCloud download it first.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (IOException ex) { LastError = $"{Path.GetFileName(file)}: {ex.Message}"; return; } // still downloading: next time
        var errors = new List<string>();
        var events = AppEvents.ParseLog(text, TimeUtil.NowMs(), errors);
        int added = events.Count > 0 ? _host.Store.InsertAppEvents(events) : 0;
        _seen[file] = stamp;
        LastFile = file;
        LastReadUtcMs = TimeUtil.NowMs();
        LastError = errors.Count > 0 ? $"{errors.Count} line(s) not understood, first: {errors[0]}" : null;
        if (added > 0 || errors.Count > 0)
            Log.Info($"iCloud log: {Path.GetFileName(file)}: {added} new, {events.Count - added} known" +
                     (errors.Count > 0 ? $", {errors.Count} not understood (first: {errors[0]})" : ""));
    }

    public void Dispose() => _timer.Dispose();
}
