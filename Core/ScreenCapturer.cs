using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GoveeScreenV2.Core;

/// <summary>
/// Capture GPU via Windows.Graphics.Capture (compatible avec les jeux en plein écran
/// exclusif, contrairement à DXGI Desktop Duplication qui perd sa session).
/// La frame reste en VRAM : le downscale est fait par le GPU (chaîne de mipmaps),
/// seule une miniature (~160×90) est rapatriée sur le CPU pour analyse.
/// Le compositeur ne délivre une frame que si l'écran a changé ; la cadence de
/// traitement est limitée par la boucle appelante (SyncEngine.TargetFps).
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private const int MaxSmallDimension = 160;

    // ===== Interop WinRT (GraphicsCaptureItem depuis un HMONITOR, texture D3D depuis une surface) =====

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // ===== HMONITOR depuis le device GDI ("\\.\DISPLAY1") =====

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    private static IntPtr FindMonitorHandle(string deviceName)
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMonitor, ref info) &&
                string.Equals(info.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = hMonitor;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ===== État =====

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private readonly AutoResetEvent _frameReady = new(false);
    private volatile bool _closed;

    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipSrv;
    private ID3D11Texture2D? _staging;

    private int _mipLevel;
    private int _width;
    private int _height;

    public int SmallWidth { get; private set; }
    public int SmallHeight { get; private set; }

    public ScreenCapturer(MonitorInfo monitor)
    {
        IntPtr hMonitor = FindMonitorHandle(monitor.DeviceName);
        if (hMonitor == IntPtr.Zero)
            throw new InvalidOperationException($"Écran introuvable : {monitor.DeviceName}");

        // Device D3D11 sur l'adaptateur qui pilote l'écran
        using (IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
        {
            factory.EnumAdapters1((uint)monitor.AdapterIndex, out IDXGIAdapter1 adapter).CheckError();
            using (adapter)
            {
                D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                    out _device,
                    out _context).CheckError();
            }
        }

        // Wrapper WinRT du device pour le frame pool
        using (IDXGIDevice dxgiDevice = _device!.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr deviceAbi));
            try { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(deviceAbi); }
            finally { Marshal.Release(deviceAbi); }
        }

        // GraphicsCaptureItem du moniteur
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemGuid;
        IntPtr itemAbi = interop.CreateForMonitor(hMonitor, ref iid);
        try { _item = GraphicsCaptureItem.FromAbi(itemAbi); }
        finally { Marshal.Release(itemAbi); }

        _width = _item.Size.Width;
        _height = _item.Size.Height;
        CreateScalingTextures(_width, _height);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _framePool.FrameArrived += (_, _) => _frameReady.Set();
        _item.Closed += (_, _) => { _closed = true; _frameReady.Set(); };

        _session = _framePool.CreateCaptureSession(_item);
        TrySet(() => _session.IsCursorCaptureEnabled = false);
        TrySet(() => _session.IsBorderRequired = false);
        _session.StartCapture();
    }

    /// <summary>Applique un réglage optionnel de session, absent sur les vieilles versions de Windows.</summary>
    private static void TrySet(Action set)
    {
        try { set(); } catch { }
    }

    private void CreateScalingTextures(int width, int height)
    {
        // Niveau de mip tel que la plus grande dimension passe sous MaxSmallDimension
        _mipLevel = 0;
        int w = width, h = height;
        while (Math.Max(w, h) > MaxSmallDimension)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            _mipLevel++;
        }
        SmallWidth = w;
        SmallHeight = h;

        _mipTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = (uint)(_mipLevel + 1),
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.GenerateMips
        });
        _mipSrv = _device.CreateShaderResourceView(_mipTexture);

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)SmallWidth,
            Height = (uint)SmallHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        });
    }

    /// <summary>
    /// Attend une nouvelle frame (au plus <paramref name="timeoutMs"/> ms) et remplit
    /// <paramref name="bgraBuffer"/> (SmallWidth × SmallHeight × 4, format BGRA).
    /// Renvoie false si l'écran n'a pas changé pendant le délai.
    /// Lève une exception si la session est fermée ou si la résolution a changé :
    /// l'appelant doit alors recréer le capturer.
    /// </summary>
    public bool TryCaptureFrame(int timeoutMs, byte[] bgraBuffer)
    {
        if (_framePool is null || _context is null)
            throw new InvalidOperationException("Capturer non initialisé.");
        if (_closed)
            throw new InvalidOperationException("Session de capture fermée.");

        if (!_frameReady.WaitOne(timeoutMs))
            return false;
        if (_closed)
            throw new InvalidOperationException("Session de capture fermée.");

        // Ne garde que la frame la plus récente si plusieurs se sont accumulées
        Direct3D11CaptureFrame? frame = null;
        while (_framePool.TryGetNextFrame() is { } next)
        {
            frame?.Dispose();
            frame = next;
        }
        if (frame is null)
            return false;

        try
        {
            if (frame.ContentSize.Width != _width || frame.ContentSize.Height != _height)
                throw new InvalidOperationException("Résolution de l'écran modifiée.");

            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            Guid iid = typeof(ID3D11Texture2D).GUID;
            IntPtr texturePtr = access.GetInterface(ref iid);
            using var frameTexture = new ID3D11Texture2D(texturePtr);
            _context.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, frameTexture, 0);
        }
        finally
        {
            frame.Dispose();
        }

        _context.GenerateMips(_mipSrv!);
        _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTexture!, (uint)_mipLevel);

        MappedSubresource mapped = _context.Map(_staging!, 0, MapMode.Read);
        try
        {
            int rowBytes = SmallWidth * 4;
            for (int y = 0; y < SmallHeight; y++)
            {
                IntPtr rowPtr = IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch);
                Marshal.Copy(rowPtr, bgraBuffer, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        return true;
    }

    public void Dispose()
    {
        _closed = true;
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        _item = null;
        try { _winrtDevice?.Dispose(); } catch { }
        _staging?.Dispose();
        _mipSrv?.Dispose();
        _mipTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _frameReady.Dispose();
    }
}
