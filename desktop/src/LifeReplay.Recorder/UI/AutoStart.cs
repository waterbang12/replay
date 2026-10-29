using Microsoft.Win32;

namespace LifeReplay.Recorder.UI;

/// <summary>Start-with-Windows via the per-user Run key (no admin rights needed).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LifeReplay";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Contains(Environment.ProcessPath ?? "LifeReplay.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled, string? dataDirArg)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var cmd = $"\"{Environment.ProcessPath}\" --autostart" + (dataDirArg != null ? $" --data-dir \"{dataDirArg}\"" : "");
            key.SetValue(ValueName, cmd);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
