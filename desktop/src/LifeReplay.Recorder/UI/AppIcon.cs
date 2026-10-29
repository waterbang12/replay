namespace LifeReplay.Recorder.UI;

/// <summary>
/// The LifeReplay icon for windows (lifereplay.ico next to the exe). The tray icon stays the recording-state
/// indicator (red dot / pause), because it has to show at a glance whether the screen is being recorded.
/// </summary>
internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon? Get()
    {
        if (_icon != null) return _icon;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "lifereplay.ico");
            if (File.Exists(path)) return _icon = new Icon(path);
        }
        catch (Exception ex) { Core.Log.Warn("App icon could not be loaded", ex); }
        return null;
    }
}
