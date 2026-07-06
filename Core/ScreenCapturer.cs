using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GoveeScreenV2.Core;

/// <summary>
/// Capture GPU via DXGI Desktop Duplication.
/// La frame reste en VRAM : le downscale est fait par le GPU (chaîne de mipmaps),
/// seule une miniature (~160×90) est rapatriée sur le CPU pour analyse.
/// AcquireNextFrame ne renvoie une frame que si l'écran a changé → 0 travail sur image statique.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private const int MaxSmallDimension = 160;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipSrv;
    private ID3D11Texture2D? _staging;

    private int _mipLevel;

    public int SmallWidth { get; private set; }
    public int SmallHeight { get; private set; }

    public ScreenCapturer(MonitorInfo monitor)
    {
        Initialize(monitor);
    }

    private void Initialize(MonitorInfo monitor)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        factory.EnumAdapters1((uint)monitor.AdapterIndex, out IDXGIAdapter1 adapter).CheckError();
        using (adapter)
        {
            adapter.EnumOutputs((uint)monitor.OutputIndex, out IDXGIOutput output).CheckError();
            using (output)
            {
                D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                    out _device,
                    out _context).CheckError();

                using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                _duplication = output1.DuplicateOutput(_device);
            }
        }

        var mode = _duplication.Description.ModeDescription;
        int width = (int)mode.Width;
        int height = (int)mode.Height;

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
    /// Lève une exception si la session de duplication est perdue (changement de mode/plein écran) :
    /// l'appelant doit alors recréer le capturer.
    /// </summary>
    public bool TryCaptureFrame(int timeoutMs, byte[] bgraBuffer)
    {
        if (_duplication is null || _context is null)
            throw new InvalidOperationException("Capturer non initialisé.");

        Result result = _duplication.AcquireNextFrame((uint)timeoutMs, out _, out IDXGIResource? desktopResource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        result.CheckError();

        try
        {
            using ID3D11Texture2D frameTexture = desktopResource!.QueryInterface<ID3D11Texture2D>();
            _context.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, frameTexture, 0);
        }
        finally
        {
            desktopResource?.Dispose();
            _duplication.ReleaseFrame();
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
        _staging?.Dispose();
        _mipSrv?.Dispose();
        _mipTexture?.Dispose();
        _duplication?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
