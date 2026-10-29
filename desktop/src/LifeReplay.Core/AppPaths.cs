namespace LifeReplay.Core;

/// <summary>
/// Locations of everything LifeReplay stores. All recordings, the database, config and logs
/// live under one data directory (default: %LOCALAPPDATA%\LifeReplay).
/// </summary>
public sealed class AppPaths
{
    public const string DataDirEnvVar = "LIFEREPLAY_DATA";

    public string DataDir { get; }
    public string RecordingsDir => Path.Combine(DataDir, "recordings");
    public string DatabaseDir => Path.Combine(DataDir, "database");
    public string DatabasePath => Path.Combine(DatabaseDir, "lifereplay.db");
    public string ConfigPath => Path.Combine(DataDir, "config.json");
    public string StatusPath => Path.Combine(DataDir, "status.json");
    public string DeviceIdPath => Path.Combine(DataDir, "device-id.txt");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string SyncDir => Path.Combine(DataDir, "sync");
    public string IncomingDir => Path.Combine(RecordingsDir, ".incoming");

    public AppPaths(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
    }

    /// <summary>Command-line override, then LIFEREPLAY_DATA, then %LOCALAPPDATA%\LifeReplay.</summary>
    public static AppPaths Resolve(string? overrideDir = null)
    {
        var dir = overrideDir;
        if (string.IsNullOrWhiteSpace(dir)) dir = Environment.GetEnvironmentVariable(DataDirEnvVar);
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LifeReplay");
        return new AppPaths(dir);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(RecordingsDir);
        Directory.CreateDirectory(DatabaseDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>Paths are stored relative to the data dir so the folder can be moved.</summary>
    public string ToRelative(string fullPath) =>
        Path.GetRelativePath(DataDir, fullPath).Replace('\\', '/');

    public string ToFull(string storedPath) =>
        Path.IsPathRooted(storedPath) ? storedPath : Path.GetFullPath(Path.Combine(DataDir, storedPath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>recordings/YYYY-MM-DD/{platform} using the local date of the recording.</summary>
    public string DayPlatformDir(DateTime localTime, string platformFolder) =>
        Path.Combine(RecordingsDir, localTime.ToString("yyyy-MM-dd"), platformFolder);

    /// <summary>Stable per-installation device id (created on first use).</summary>
    public string GetOrCreateDeviceId()
    {
        Directory.CreateDirectory(DataDir);
        if (File.Exists(DeviceIdPath))
        {
            var text = File.ReadAllText(DeviceIdPath).Trim();
            if (Guid.TryParse(text, out var g)) return g.ToString();
        }
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(DeviceIdPath, id);
        return id;
    }
}
