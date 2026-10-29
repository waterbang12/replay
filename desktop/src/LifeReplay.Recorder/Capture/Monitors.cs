using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Graphics.Capture;

namespace LifeReplay.Recorder.Capture;

internal sealed record MonitorInfo(IntPtr Handle, string Id, string DeviceName, string FriendlyName, int Left, int Top, int Width, int Height, bool IsPrimary);

internal static class Monitors
{
    /// <summary>
    /// Connected monitors. The id is a hash of the monitor's device interface path, so it stays the same
    /// across reboots and when monitors are re-plugged into the same port.
    /// </summary>
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(h, ref mi)) return true;
            var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            string stableKey = mi.szDevice;
            string friendly = mi.szDevice.Replace(@"\\.\", "");
            if (Native.EnumDisplayDevices(mi.szDevice, 0, ref dd, Native.EDD_GET_DEVICE_INTERFACE_NAME) && !string.IsNullOrEmpty(dd.DeviceID))
            {
                stableKey = dd.DeviceID;
                if (!string.IsNullOrWhiteSpace(dd.DeviceString)) friendly = $"{friendly} · {dd.DeviceString}";
            }
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableKey.ToUpperInvariant())))[..10].ToLowerInvariant();
            var r = mi.rcMonitor;
            list.Add(new MonitorInfo(h, "d-" + hash, mi.szDevice, friendly, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        // Two identical ids are possible only if the interface lookup failed; make them unique.
        var seen = new HashSet<string>();
        for (int i = 0; i < list.Count; i++)
            if (!seen.Add(list[i].Id)) list[i] = list[i] with { Id = list[i].Id + "-" + i };
        return list.OrderBy(m => m.Left).ThenBy(m => m.Top).ToList();
    }

    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static unsafe GraphicsCaptureItem CreateCaptureItem(IntPtr hmonitor)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(Native.WindowsCreateString(cls, cls.Length, out var hstr));
        try
        {
            var iid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(hstr, ref iid, out var factory));
            try
            {
                // IGraphicsCaptureItemInterop: [3] CreateForWindow, [4] CreateForMonitor
                var vtbl = *(IntPtr**)factory;
                var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[4];
                Guid itemIid = IID_IGraphicsCaptureItem;
                IntPtr item;
                Marshal.ThrowExceptionForHR(createForMonitor(factory, hmonitor, &itemIid, &item));
                try { return GraphicsCaptureItem.FromAbi(item); }
                finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { Native.WindowsDeleteString(hstr); }
    }
}
