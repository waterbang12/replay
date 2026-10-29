using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LifeReplay.Recorder.Capture;

/// <summary>A Direct3D 11 device plus its WinRT wrapper (needed by Windows.Graphics.Capture and Media Foundation).</summary>
internal sealed class D3DContext : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDirect3DDevice WinRtDevice { get; }
    public bool IsWarp { get; }
    private readonly ID3D11Multithread _multithread;

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public D3DContext()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        ID3D11Device? device = null;
        // BGRA is required by capture; VideoSupport lets the hardware encoder share the device.
        foreach (var (driver, flags) in new[]
                 {
                     (DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport),
                     (DriverType.Hardware, DeviceCreationFlags.BgraSupport),
                     (DriverType.Warp, DeviceCreationFlags.BgraSupport),
                 })
        {
            var hr = D3D11.D3D11CreateDevice(null, driver, flags, levels, out device);
            if (hr.Success && device != null)
            {
                IsWarp = driver == DriverType.Warp;
                break;
            }
        }
        Device = device ?? throw new InvalidOperationException("Could not create a Direct3D 11 device");
        Context = Device.ImmediateContext;
        _multithread = Context.QueryInterface<ID3D11Multithread>();
        _multithread.SetMultithreadProtected(true);

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var raw));
        try { WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(raw); }
        finally { Marshal.Release(raw); }
    }

    public void Lock() => _multithread.Enter();
    public void Unlock() => _multithread.Leave();

    public ID3D11Texture2D CreateTexture(int width, int height) => Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    });

    public void Clear(ID3D11Texture2D texture)
    {
        using var rtv = Device.CreateRenderTargetView(texture);
        Context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f));
    }

    /// <summary>
    /// Copies a BGRA texture to the CPU and box-downscales it to <paramref name="outWidth"/> pixels wide.
    /// Returns BGRA pixels (stride = outWidth * 4). Everything is released deterministically, so taking
    /// thumbnails doesn't depend on the garbage collector freeing full-size bitmaps.
    /// </summary>
    public unsafe byte[] ReadDownscaled(ID3D11Texture2D source, int width, int height, int outWidth, out int outHeight)
    {
        outHeight = Math.Max(1, (int)Math.Round(height * (double)outWidth / width));
        var result = new byte[outWidth * outHeight * 4];
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
        });
        Lock();
        try
        {
            Context.CopyResource(staging, source);
            var map = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte* src = (byte*)map.DataPointer;
                long pitch = map.RowPitch;
                int oh = outHeight;
                for (int oy = 0; oy < oh; oy++)
                {
                    int y0 = oy * height / oh, y1 = Math.Max(y0 + 1, (oy + 1) * height / oh);
                    for (int ox = 0; ox < outWidth; ox++)
                    {
                        int x0 = ox * width / outWidth, x1 = Math.Max(x0 + 1, (ox + 1) * width / outWidth);
                        // Average a sparse grid of up to 4x4 samples from the block (plenty for a preview).
                        int sx = Math.Max(1, (x1 - x0) / 4), sy = Math.Max(1, (y1 - y0) / 4);
                        int b = 0, g = 0, r = 0, n = 0;
                        for (int y = y0; y < y1; y += sy)
                        {
                            byte* row = src + y * pitch;
                            for (int x = x0; x < x1; x += sx)
                            {
                                byte* p = row + x * 4;
                                b += p[0]; g += p[1]; r += p[2]; n++;
                            }
                        }
                        int o = (oy * outWidth + ox) * 4;
                        result[o] = (byte)(b / n); result[o + 1] = (byte)(g / n); result[o + 2] = (byte)(r / n); result[o + 3] = 255;
                    }
                }
            }
            finally { Context.Unmap(staging, 0); }
        }
        finally { Unlock(); }
        return result;
    }

    /// <summary>Gets the D3D texture behind a WinRT capture surface (caller disposes).</summary>
    public static unsafe ID3D11Texture2D TextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Guid accessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"); // IDirect3DDxgiInterfaceAccess
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in accessIid, out var access));
            try
            {
                var vtbl = *(IntPtr**)access;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                Guid texIid = IID_ID3D11Texture2D;
                IntPtr tex;
                Marshal.ThrowExceptionForHR(getInterface(access, &texIid, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(unknown); }
    }

    /// <summary>Wraps a texture we own as a WinRT surface for MediaStreamSample / SoftwareBitmap.</summary>
    public static IDirect3DSurface SurfaceFromTexture(ID3D11Texture2D texture)
    {
        using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out var raw));
        try { return MarshalInterface<IDirect3DSurface>.FromAbi(raw); }
        finally { Marshal.Release(raw); }
    }

    public void Dispose()
    {
        try { (WinRtDevice as IDisposable)?.Dispose(); } catch { }
        _multithread.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
