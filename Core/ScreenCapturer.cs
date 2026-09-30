using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GoveeScreenV2.Core;

public enum CaptureResult
{
    /// <summary>Nouvelle frame traitée, le buffer est rempli.</summary>
    Frame,
    /// <summary>L'écran a changé mais l'intervalle mini n'est pas écoulé : frame ignorée (aucun travail GPU).</summary>
    Skipped,
    /// <summary>Écran statique : aucune frame pendant le délai.</summary>
    Timeout
}

/// <summary>
/// Capture GPU via Windows.Graphics.Capture, d'un écran entier ou d'une fenêtre
/// (secours pour les jeux en plein écran exclusif que la capture d'écran ne voit pas).
/// La frame reste en VRAM : le downscale est fait par le GPU (chaîne de mipmaps),
/// seule une miniature (~160×90) est rapatriée sur le CPU pour analyse.
/// Le compositeur ne délivre une frame que si l'image a changé ; la cadence de
/// traitement est limitée par saut de frames (minIntervalMs), sans sleep, donc
/// sans jitter et toujours sur l'image la plus récente.
/// La lecture GPU→CPU est double-buffer : on relit la miniature de la frame
/// précédente pendant que le GPU écrit la courante → aucun blocage sur Map().
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private const int MaxSmallDimension = 160;

    // ===== Interop WinRT (GraphicsCaptureItem depuis HMONITOR/HWND, texture D3D depuis une surface) =====

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
    private readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[2];
    private int _stagingWrite;
    private bool _hasPreviousFrame;
    private long _lastProcessTicks;

    private int _mipLevel;
    private int _width;
    private int _height;

    public int SmallWidth { get; private set; }
    public int SmallHeight { get; private set; }

    /// <summary>True si on capture une fenêtre (mode secours plein écran) plutôt que l'écran entier.</summary>
    public bool IsWindowCapture { get; }

    /// <param name="monitor">Écran à capturer (et adaptateur GPU à utiliser).</param>
    /// <param name="captureWindow">Si non nul, capture cette fenêtre au lieu de l'écran.</param>
    public ScreenCapturer(MonitorInfo monitor, IntPtr captureWindow = default)
    {
        IsWindowCapture = captureWindow != IntPtr.Zero;

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

        // GraphicsCaptureItem : fenêtre ou moniteur
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemGuid;
        IntPtr itemAbi;
        if (IsWindowCapture)
        {
            itemAbi = interop.CreateForWindow(captureWindow, ref iid);
        }
        else
        {
            IntPtr hMonitor = MonitorService.GetMonitorHandle(monitor.DeviceName);
            if (hMonitor == IntPtr.Zero)
                throw new InvalidOperationException($"Écran introuvable : {monitor.DeviceName}");
            itemAbi = interop.CreateForMonitor(hMonitor, ref iid);
        }
        try { _item = GraphicsCaptureItem.FromAbi(itemAbi); }
        finally { Marshal.Release(itemAbi); }

        _width = _item.Size.Width;
        _height = _item.Size.Height;
        if (_width < 1 || _height < 1)
            throw new InvalidOperationException("Cible de capture sans surface.");
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

        for (int i = 0; i < 2; i++)
        {
            _staging[i] = _device.CreateTexture2D(new Texture2DDescription
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
    }

    /// <summary>
    /// Attend une nouvelle frame (au plus <paramref name="timeoutMs"/> ms). Si moins de
    /// <paramref name="minIntervalMs"/> ms se sont écoulées depuis le dernier traitement,
    /// la frame est ignorée sans travail GPU (Skipped). Sinon remplit
    /// <paramref name="bgraBuffer"/> (SmallWidth × SmallHeight × 4, format BGRA) avec la
    /// miniature de la frame précédente (lecture double-buffer, décalage d'une frame).
    /// Lève une exception si la session est fermée ou si la résolution a changé :
    /// l'appelant doit alors recréer le capturer.
    /// </summary>
    public CaptureResult TryCaptureFrame(int timeoutMs, int minIntervalMs, byte[] bgraBuffer)
    {
        if (_framePool is null || _context is null)
            throw new InvalidOperationException("Capturer non initialisé.");
        if (_closed)
            throw new InvalidOperationException("Session de capture fermée.");

        if (!_frameReady.WaitOne(timeoutMs))
            return CaptureResult.Timeout;
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
            return CaptureResult.Timeout;

        long now = Environment.TickCount64;
        if (minIntervalMs > 0 && now - _lastProcessTicks < minIntervalMs)
        {
            frame.Dispose();
            return CaptureResult.Skipped;
        }
        _lastProcessTicks = now;

        try
        {
            if (frame.ContentSize.Width != _width || frame.ContentSize.Height != _height)
                throw new InvalidOperationException("Taille de la cible de capture modifiée.");

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
        _context.CopySubresourceRegion(_staging[_stagingWrite]!, 0, 0, 0, 0, _mipTexture!, (uint)_mipLevel);

        // Relit la miniature écrite à la frame précédente : le GPU a eu tout l'intervalle
        // pour la terminer, Map ne bloque pas. (Première frame : on relit la courante.)
        int read = _hasPreviousFrame ? 1 - _stagingWrite : _stagingWrite;
        MappedSubresource mapped = _context.Map(_staging[read]!, 0, MapMode.Read);
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
            _context.Unmap(_staging[read]!, 0);
        }

        _stagingWrite = 1 - _stagingWrite;
        _hasPreviousFrame = true;
        return CaptureResult.Frame;
    }

    public void Dispose()
    {
        _closed = true;
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        _item = null;
        try { _winrtDevice?.Dispose(); } catch { }
        _staging[0]?.Dispose();
        _staging[1]?.Dispose();
        _mipSrv?.Dispose();
        _mipTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _frameReady.Dispose();
    }
}
