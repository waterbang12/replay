using LifeReplay.Core;

namespace LifeReplay.Recorder.Features;

/// <summary>
/// An optional part of the recorder (the window log, "Waiting on AI", the iPhone iCloud log…). Features are listed
/// in one place, <see cref="RecorderHost.CreateFeatures"/>: to add one, implement this and add it there; to remove
/// one, delete its folder and its line. Screen capture itself is not a feature: it's the RecordingController.
/// </summary>
internal interface IRecorderFeature : IDisposable
{
    /// <summary>Short name for logs.</summary>
    string Name { get; }

    /// <summary>Called once, after recording started.</summary>
    void Start();

    /// <summary>Called when config.json or the Settings window changed the configuration.</summary>
    void ConfigChanged(RecorderConfig cfg) { }
}

/// <summary>
/// What the window log saw this second, for features that react to it (e.g. "Waiting on AI").
/// <see cref="Active"/> is false while not recording, not logging, or idle; <see cref="EndUtcMs"/> is when activity
/// stopped (for idle, the last input or screen change).
/// </summary>
internal sealed record ActivitySample(long NowUtcMs, long InputIdleMs, bool Active, long EndUtcMs, string? Process, string? Title, bool IsPrivate);
