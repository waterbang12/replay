using LifeReplay.Core;

namespace LifeReplay.Recorder.UI;

/// <summary>System-wide hotkey (default Ctrl+Alt+Shift+P) delivered through a hidden message window.</summary>
internal sealed class Hotkey : NativeWindow, IDisposable
{
    private const int Id = 0x4C52;
    private bool _registered;
    public event Action? Pressed;
    public string? Description { get; private set; }

    public Hotkey(string spec)
    {
        CreateHandle(new CreateParams());
        if (!TryParse(spec, out var mods, out var key))
        {
            Log.Warn($"Pause hotkey \"{spec}\" not understood; hotkey disabled");
            return;
        }
        _registered = Native.RegisterHotKey(Handle, Id, mods | Native.MOD_NOREPEAT, (uint)key);
        Description = spec;
        if (!_registered) Log.Warn($"Pause hotkey {spec} is already used by another app; hotkey disabled");
    }

    public bool Registered => _registered;

    public static bool TryParse(string spec, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win": modifiers |= Native.MOD_WIN; break;
                default:
                    if (!Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        return key != Keys.None && modifiers != 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == Id) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered) Native.UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }
}
