using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
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
    /// <summary>Écran statique (aucune frame pendant le délai) ou attente interrompue.</summary>
    Timeout
}

/// <summary>
/// Capture GPU via Windows.Graphics.Capture, d'un écran entier ou d'une fenêtre
/// (secours pour les jeux en plein écran exclusif que la capture d'écran ne voit pas).
/// La frame reste en VRAM : un pixel shader la réduit directement en miniature (~160×90),
/// chaque pixel de sortie étant la moyenne du bloc d'écran qu'il couvre — sans copie
/// pleine résolution ni chaîne de mipmaps (dont la dernière réduction impaire, en 4K,
/// échantillonnait au lieu de moyenner). Seule la miniature est rapatriée sur le CPU.
/// Le compositeur ne délivre une frame que si l'image a changé. La cadence est limitée
/// à la source (MinUpdateInterval, Windows 11 24H2+ : le compositeur ne copie même pas
/// les frames superflues) et, à défaut, par saut de frames, sans sleep, donc sans jitter
/// et toujours sur l'image la plus récente.
/// La lecture GPU→CPU est double-buffer : on relit la miniature de la frame
/// précédente pendant que le GPU écrit la courante → aucun blocage sur Map().
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private const int MaxSmallDimension = 160;
    private const int MaxTapsPerAxis = 16;   // au-delà (écrans > 5K), le bloc est sous-échantillonné

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
    private static readonly Guid Texture2DGuid = typeof(ID3D11Texture2D).GUID;

    private static readonly bool SupportsMinUpdateInterval = Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
        "Windows.Graphics.Capture.GraphicsCaptureSession", nameof(GraphicsCaptureSession.MinUpdateInterval));

    // ===== Shader de réduction =====

    internal const string DownscaleShader = """
        // Triangle couvrant toute la cible, sans vertex buffer
        float4 VS(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        Texture2D Source : register(t0);
        SamplerState LinearClamp : register(s0);

        cbuffer Params : register(b0)
        {
            float2 InvSourceSize;   // 1 / taille de la source en texels
            float2 Offset;          // coin haut-gauche de la zone moyennée, en texels
            float2 Block;           // texels de la source couverts par un pixel de sortie (par axe)
            float2 Step;            // pas entre deux échantillons = Block / Taps
            uint2 Taps;             // échantillons par axe
        };

        // Moyenne du bloc de la source couvert par ce pixel de sortie. Les blocs pavent toute
        // la zone ; avec un pas de 2 texels, chaque échantillon bilinéaire tombe au coin
        // commun de 2×2 texels et en renvoie la moyenne exacte.
        float4 PS(float4 pos : SV_Position) : SV_Target
        {
            float2 origin = Offset + floor(pos.xy) * Block + Step * 0.5;
            float4 sum = 0;
            [loop] for (uint y = 0; y < Taps.y; y++)
                [loop] for (uint x = 0; x < Taps.x; x++)
                    sum += Source.SampleLevel(LinearClamp, (origin + float2(x, y) * Step) * InvSourceSize, 0);
            return sum / (Taps.x * Taps.y);
        }
        """;

    /// <summary>Bytecode indépendant du GPU : compilé une seule fois pour toute l'application.</summary>
    private static readonly Lazy<(byte[] Vertex, byte[] Pixel)> ShaderBytecode = new(() => (
        Compiler.Compile(DownscaleShader, "VS", "Downscale", "vs_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray(),
        Compiler.Compile(DownscaleShader, "PS", "Downscale", "ps_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray()));

    [StructLayout(LayoutKind.Sequential, Size = 48)]   // constant buffer : multiple de 16 octets
    private struct ShaderParams
    {
        public float InvWidth, InvHeight;
        public float OffsetX, OffsetY;
        public float BlockX, BlockY;
        public float StepX, StepY;
        public uint TapsX, TapsY;
    }

    // ===== État =====

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly WaitHandle[] _waitHandles;
    private volatile bool _closed;

    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11SamplerState? _sampler;
    private ID3D11Buffer? _params;
    private ID3D11Texture2D? _smallTexture;
    private ID3D11RenderTargetView? _smallTarget;
    private readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[2];
    private int _stagingWrite;
    private bool _hasPreviousFrame;
    private long _lastProcessTimestamp;
    private int _appliedMinIntervalMs = -1;
    private ScreenRegion _region;                     // zone moyennée (toute la surface par défaut)

    // Vues shader sur les textures du pool (recyclées par le compositeur), créées une seule fois chacune
    private readonly Dictionary<IntPtr, ID3D11ShaderResourceView> _sourceViews = new();
    private bool? _canSampleFrames;
    private ID3D11Texture2D? _copySource;            // secours si les frames ne sont pas échantillonnables
    private ID3D11ShaderResourceView? _copySourceView;

    private int _width;
    private int _height;

    public int SmallWidth { get; private set; }
    public int SmallHeight { get; private set; }

    /// <summary>True si on capture une fenêtre (mode secours plein écran) plutôt que l'écran entier.</summary>
    public bool IsWindowCapture { get; }

    /// <param name="monitor">Écran à capturer (et adaptateur GPU à utiliser).</param>
    /// <param name="captureWindow">Si non nul, capture cette fenêtre au lieu de l'écran.</param>
    /// <param name="interrupt">Événement qui interrompt l'attente d'une frame (pause, changement d'écran, arrêt).</param>
    public ScreenCapturer(MonitorInfo monitor, IntPtr captureWindow = default, WaitHandle? interrupt = null)
    {
        IsWindowCapture = captureWindow != IntPtr.Zero;
        _waitHandles = interrupt is null ? new WaitHandle[] { _frameReady } : new[] { _frameReady, interrupt };

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
        CreateScalingResources(_width, _height);

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

    private void CreateScalingResources(int width, int height)
    {
        // Même miniature qu'avec une chaîne de mipmaps : la taille est divisée par deux
        // jusqu'à passer sous MaxSmallDimension ; ses pixels pavent tout l'écran
        int w = width, h = height;
        while (Math.Max(w, h) > MaxSmallDimension)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        SmallWidth = w;
        SmallHeight = h;

        var (vertexCode, pixelCode) = ShaderBytecode.Value;
        _vertexShader = _device!.CreateVertexShader(vertexCode);
        _pixelShader = _device.CreatePixelShader(pixelCode);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);

        _region = new ScreenRegion(0, 0, width, height);
        var shaderParams = ShaderParamsFor(_region);
        _params = _device.CreateBuffer(in shaderParams,
            new BufferDescription((uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer));

        _smallTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)SmallWidth,
            Height = (uint)SmallHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget
        });
        _smallTarget = _device.CreateRenderTargetView(_smallTexture);

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

    /// <summary>Paramètres du shader pour que la miniature pave exactement <paramref name="region"/>.</summary>
    private ShaderParams ShaderParamsFor(ScreenRegion region)
    {
        float blockX = (float)region.Width / SmallWidth, blockY = (float)region.Height / SmallHeight;
        uint tapsX = (uint)Math.Clamp((int)MathF.Round(blockX / 2), 1, MaxTapsPerAxis);
        uint tapsY = (uint)Math.Clamp((int)MathF.Round(blockY / 2), 1, MaxTapsPerAxis);
        return new ShaderParams
        {
            InvWidth = 1f / _width,
            InvHeight = 1f / _height,
            OffsetX = region.X,
            OffsetY = region.Y,
            BlockX = blockX,
            BlockY = blockY,
            StepX = blockX / tapsX,
            StepY = blockY / tapsY,
            TapsX = tapsX,
            TapsY = tapsY
        };
    }

    /// <summary>
    /// Restreint la moyenne à une zone de l'écran (null = écran entier), par exemple pour
    /// exclure la barre des tâches. Même coût par frame : seule la correspondance
    /// miniature → écran change, mise à jour (48 octets) uniquement quand la zone change.
    /// Sans effet en capture de fenêtre.
    /// </summary>
    public void SetRegion(ScreenRegion? region)
    {
        if (IsWindowCapture || _context is null || _params is null)
            return;
        var full = new ScreenRegion(0, 0, _width, _height);
        var target = region is { } r && r.Width > 0 && r.Height > 0 && r.X >= 0 && r.Y >= 0 &&
                     r.X + r.Width <= _width && r.Y + r.Height <= _height ? r : full;
        if (target == _region)
            return;
        _region = target;
        var shaderParams = ShaderParamsFor(target);
        _context.UpdateSubresource(in shaderParams, _params);
    }

    /// <summary>Vue shader sur la texture de la frame (ou sur sa copie si elle n'est pas échantillonnable).</summary>
    private ID3D11ShaderResourceView GetSourceView(ID3D11Texture2D frameTexture)
    {
        _canSampleFrames ??= (frameTexture.Description.BindFlags & BindFlags.ShaderResource) != 0;
        if (_canSampleFrames == false)
        {
            if (_copySource is null)
            {
                var desc = frameTexture.Description;
                desc.BindFlags = BindFlags.ShaderResource;
                desc.MiscFlags = ResourceOptionFlags.None;
                desc.Usage = ResourceUsage.Default;
                desc.CPUAccessFlags = CpuAccessFlags.None;
                _copySource = _device!.CreateTexture2D(desc);
                _copySourceView = _device.CreateShaderResourceView(_copySource);
            }
            _context!.CopyResource(_copySource, frameTexture);
            return _copySourceView!;
        }

        if (!_sourceViews.TryGetValue(frameTexture.NativePointer, out var view))
        {
            // La vue garde la texture en vie : son adresse ne peut pas être réutilisée par une autre
            if (_sourceViews.Count >= 8)
                ClearSourceViews();
            view = _device!.CreateShaderResourceView(frameTexture);
            _sourceViews[frameTexture.NativePointer] = view;
        }
        return view;
    }

    private void ClearSourceViews()
    {
        foreach (var view in _sourceViews.Values)
            view.Dispose();
        _sourceViews.Clear();
    }

    /// <summary>Réduit la texture de la frame en miniature dans _smallTexture (un seul draw).</summary>
    private void Downscale(ID3D11ShaderResourceView source)
    {
        var context = _context!;
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetSampler(0, _sampler);
        context.PSSetConstantBuffer(0, _params);
        context.PSSetShaderResource(0, source);
        context.RSSetViewport(0, 0, SmallWidth, SmallHeight);
        context.OMSetRenderTargets(_smallTarget!);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null!);   // ne retient pas la texture du pool dans le contexte
    }

    /// <summary>
    /// Attend une nouvelle frame (au plus <paramref name="timeoutMs"/> ms, ou jusqu'au signal
    /// d'interruption). Si moins de <paramref name="minIntervalMs"/> ms se sont écoulées depuis
    /// le dernier traitement, la frame est ignorée sans travail GPU (Skipped). Sinon remplit
    /// <paramref name="bgraBuffer"/> (SmallWidth × SmallHeight × 4, format BGRA) avec la
    /// miniature de la frame précédente (lecture double-buffer, décalage d'une frame).
    /// Lève une exception si la session est fermée ou si la résolution a changé :
    /// l'appelant doit alors recréer le capturer.
    /// </summary>
    public CaptureResult TryCaptureFrame(int timeoutMs, int minIntervalMs, byte[] bgraBuffer)
    {
        if (_framePool is null || _context is null || _session is null)
            throw new InvalidOperationException("Capturer non initialisé.");
        if (_closed)
            throw new InvalidOperationException("Session de capture fermée.");

        // Cadence imposée au compositeur : il ne copie plus les frames qu'on jetterait
        if (minIntervalMs != _appliedMinIntervalMs)
        {
            _appliedMinIntervalMs = minIntervalMs;
            if (SupportsMinUpdateInterval)
                TrySet(() => _session.MinUpdateInterval = Pacing.MinInterval(minIntervalMs));
        }

        if (WaitHandle.WaitAny(_waitHandles, timeoutMs) != 0)
            return CaptureResult.Timeout; // délai écoulé ou attente interrompue
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

        if (minIntervalMs > 0 && !Pacing.IsDue(_lastProcessTimestamp, minIntervalMs))
        {
            frame.Dispose();
            return CaptureResult.Skipped;
        }
        _lastProcessTimestamp = Pacing.Now;

        try
        {
            if (frame.ContentSize.Width != _width || frame.ContentSize.Height != _height)
                throw new InvalidOperationException("Taille de la cible de capture modifiée.");

            // Wrappers WinRT/COM libérés tout de suite : sinon ils retiennent les textures du
            // pool (~32 Mo chacune en 4K) jusqu'au passage du GC, même après Dispose()
            using var surface = frame.Surface;
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();
            IntPtr texturePtr;
            try
            {
                Guid iid = Texture2DGuid;
                texturePtr = access.GetInterface(ref iid);
            }
            finally
            {
                if (Marshal.IsComObject(access))
                    Marshal.ReleaseComObject(access);
            }
            using var frameTexture = new ID3D11Texture2D(texturePtr);
            Downscale(GetSourceView(frameTexture));
        }
        finally
        {
            frame.Dispose();
        }

        _context.CopyResource(_staging[_stagingWrite]!, _smallTexture!);

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
        ClearSourceViews();
        _copySourceView?.Dispose();
        _copySource?.Dispose();
        _staging[0]?.Dispose();
        _staging[1]?.Dispose();
        _smallTarget?.Dispose();
        _smallTexture?.Dispose();
        _params?.Dispose();
        _sampler?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _frameReady.Dispose();
    }
}
