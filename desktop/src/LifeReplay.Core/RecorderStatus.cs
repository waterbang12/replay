using System.Text.Json;

namespace LifeReplay.Core;

/// <summary>Snapshot the recorder writes to status.json every few seconds so the viewer can show what it is doing.</summary>
public sealed class RecorderStatus
{
    public string State { get; set; } = "stopped"; // recording | paused | stopped | error
    public string? Reason { get; set; }
    public long SinceUtcMs { get; set; }
    public long UpdatedUtcMs { get; set; }
    public int ProcessId { get; set; }
    public List<string> Displays { get; set; } = [];
    public long StorageBytes { get; set; }
    public long EstimatedMaxBytesPerHour { get; set; }
    public string? LastError { get; set; }
    public bool Blanked { get; set; }
    public bool SyncEnabled { get; set; }

    public void Save(string path)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, RecorderConfig.JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* the viewer copes with a stale file */ }
    }

    public static RecorderStatus? TryLoad(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<RecorderStatus>(File.ReadAllText(path), RecorderConfig.JsonOptions) : null; }
        catch { return null; }
    }

    /// <summary>The recorder updates the file every 5 s; anything older than 30 s means it isn't running.</summary>
    public bool IsFresh(long nowMs) => nowMs - UpdatedUtcMs < 30_000;
}
