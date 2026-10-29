using System.Runtime.InteropServices;
using LifeReplay.Core;

namespace LifeReplay.Recorder.UI;

/// <summary>
/// Reports when the console display turns off or on (GUID_CONSOLE_DISPLAY_STATE). On Modern Standby
/// laptops Windows never sends a "suspend" event, but the display always turns off first, so this is
/// what lets the recorder close its files cleanly before standby.
/// </summary>
internal sealed class DisplayPowerWatcher : NativeWindow, IDisposable
{
    private static Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private IntPtr _registration;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    /// <summary>true = display off, false = on (or dimmed).</summary>
    public event Action<bool>? DisplayOffChanged;

    public DisplayPowerWatcher()
    {
        CreateHandle(new CreateParams());
        _registration = RegisterPowerSettingNotification(Handle, ref ConsoleDisplayState, 0);
        if (_registration == IntPtr.Zero) Log.Warn($"Could not register for display power notifications (error {Marshal.GetLastWin32Error()})");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt32() == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; UCHAR Data[] }
            var setting = Marshal.PtrToStructure<Guid>(m.LParam);
            if (setting == ConsoleDisplayState)
            {
                int state = Marshal.ReadInt32(m.LParam, 20); // 0 = off, 1 = on, 2 = dimmed
                try { DisplayOffChanged?.Invoke(state == 0); } catch (Exception ex) { Log.Warn("Display power handler failed", ex); }
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero) { UnregisterPowerSettingNotification(_registration); _registration = IntPtr.Zero; }
        DestroyHandle();
    }
}
