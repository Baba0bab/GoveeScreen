namespace GoveeScreenV2.Core;

public enum EngineStatus { Starting, Active, Paused, CaptureError, NoMonitor }

public record EngineUpdate(EngineStatus Status, byte R, byte G, byte B, double Brightness, string? MonitorName);

/// <summary>
/// Boucle de synchronisation : capture GPU → analyse → envoi UDP Govee.
/// En mode Auto, le traitement suit les frames du compositeur jusqu'à 60 FPS
/// (uniquement quand l'image change) ; en mode manuel il est plafonné à TargetFps.
/// Le plafonnement se fait par saut de frames, sans sleep : pas de jitter, et la
/// frame traitée est toujours la plus récente.
/// Si la capture d'écran ne voit pas un jeu en plein écran exclusif (frames noires
/// ou plus aucune frame alors qu'une fenêtre plein écran est au premier plan),
/// la boucle bascule automatiquement en capture de cette fenêtre, puis revient
/// à la capture d'écran quand le plein écran se termine.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private const int FrameTimeoutMs = 100;
    private const int MinSendIntervalMs = 33;
    private const int AutoIntervalMs = 16;             // mode Auto : traitement à 60 FPS max
    private const double SmoothingAlpha = 0.35;        // par tranche de 33 ms (corrigé du temps réel)
    private const int FallbackAfterMs = 2000;          // bascule en capture fenêtre après 2 s sans image exploitable
    private const int FullscreenCheckIntervalMs = 500;

    private readonly GoveeClient _govee;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;

    private MonitorInfo? _monitor;
    private readonly object _monitorLock = new();
    private bool _monitorChanged;

    private volatile bool _autoFps = true;
    private volatile int _targetFps = 30;

    private (byte r, byte g, byte b) _lastSentColor = (0, 0, 0);
    private double _lastSentBrightness = -1;
    private double _emaR, _emaG, _emaB, _emaBrightness;
    private long _lastEmaTicks;
    private bool _emaInitialized;

    public int BrightnessLevel { get; set; } = 2;
    public bool SmoothTransitions { get; set; } = true;

    /// <summary>Cadence automatique (jusqu'à 60 FPS quand l'image change).</summary>
    public bool AutoFps
    {
        get => _autoFps;
        set => _autoFps = value;
    }

    /// <summary>Cadence de capture en mode manuel (prise en compte immédiatement).</summary>
    public int TargetFps
    {
        get => _targetFps;
        set => _targetFps = Math.Clamp(value, 5, 120);
    }

    public bool IsPaused => _paused;

    public event Action<EngineUpdate>? Updated;

    public SyncEngine(GoveeClient govee)
    {
        _govee = govee;
    }

    public void SetMonitor(MonitorInfo? monitor)
    {
        lock (_monitorLock)
        {
            _monitor = monitor;
            _monitorChanged = true;
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
            Updated?.Invoke(new EngineUpdate(EngineStatus.Paused, 0, 0, 0, 0, _monitor?.FriendlyName));
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "GoveeSync" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1000);
        _govee.TurnOff();
    }

    private void RunLoop()
    {
        ScreenCapturer? capturer = null;
        byte[]? buffer = null;
        MonitorInfo? activeMonitor = null;
        long lastSendTicks = 0;
        long lastFrameTicks = Environment.TickCount64; // dernière frame reçue (traitée ou sautée)
        long blackSinceTicks = -1;                     // début de la période "image entièrement noire"
        long lastFullscreenCheck = 0;
        IntPtr fallbackWindow = IntPtr.Zero;           // fenêtre plein écran capturée en secours

        while (_running)
        {
            if (_paused)
            {
                Thread.Sleep(100);
                continue;
            }

            // (Re)création du capturer si l'écran sélectionné a changé ou après une erreur
            bool needRecreate;
            MonitorInfo? wanted;
            lock (_monitorLock)
            {
                wanted = _monitor;
                needRecreate = _monitorChanged || capturer is null;
                _monitorChanged = false;
            }

            if (wanted is null)
            {
                capturer?.Dispose();
                capturer = null;
                Updated?.Invoke(new EngineUpdate(EngineStatus.NoMonitor, 0, 0, 0, 0, null));
                Thread.Sleep(1000);
                continue;
            }

            if (!Equals(wanted, activeMonitor))
                fallbackWindow = IntPtr.Zero;

            // Bascule capture écran ↔ capture fenêtre plein écran
            long now = Environment.TickCount64;
            if (!needRecreate && now - lastFullscreenCheck >= FullscreenCheckIntervalMs)
            {
                lastFullscreenCheck = now;
                IntPtr fullscreen = MonitorService.GetFullscreenWindow(wanted.DeviceName);
                if (fallbackWindow != IntPtr.Zero)
                {
                    // La fenêtre plein écran a disparu ou changé
                    if (fullscreen != fallbackWindow)
                    {
                        fallbackWindow = fullscreen;
                        needRecreate = true;
                    }
                }
                else if (fullscreen != IntPtr.Zero)
                {
                    // Une fenêtre plein écran est là mais la capture d'écran ne donne rien
                    // d'exploitable depuis 2 s (plein écran exclusif) → capturer la fenêtre
                    bool starved = now - lastFrameTicks >= FallbackAfterMs;
                    bool allBlack = blackSinceTicks >= 0 && now - blackSinceTicks >= FallbackAfterMs;
                    if (starved || allBlack)
                    {
                        fallbackWindow = fullscreen;
                        needRecreate = true;
                    }
                }
            }

            if (needRecreate)
            {
                capturer?.Dispose();
                capturer = null;
                try
                {
                    capturer = new ScreenCapturer(wanted, fallbackWindow);
                    buffer = new byte[capturer.SmallWidth * capturer.SmallHeight * 4];
                    activeMonitor = wanted;
                    _emaInitialized = false;
                    lastFrameTicks = Environment.TickCount64;
                    blackSinceTicks = -1;
                }
                catch
                {
                    if (fallbackWindow != IntPtr.Zero)
                    {
                        // La capture de la fenêtre a échoué → revenir à la capture d'écran
                        fallbackWindow = IntPtr.Zero;
                        lock (_monitorLock) { _monitorChanged = true; }
                        continue;
                    }
                    Updated?.Invoke(new EngineUpdate(EngineStatus.CaptureError, 0, 0, 0, 0, wanted.FriendlyName));
                    Thread.Sleep(1000);
                    lock (_monitorLock) { _monitorChanged = true; }
                    continue;
                }
            }

            try
            {
                int minInterval = _autoFps ? AutoIntervalMs : 1000 / _targetFps;
                var result = capturer!.TryCaptureFrame(FrameTimeoutMs, minInterval, buffer!);
                if (result == CaptureResult.Timeout)
                    continue; // Écran statique : rien à faire

                lastFrameTicks = Environment.TickCount64;
                if (result == CaptureResult.Skipped)
                    continue; // Frame ignorée pour respecter la cadence

                var color = FrameAnalyzer.Analyze(buffer!, capturer.SmallWidth, capturer.SmallHeight);

                // Image entièrement noire : possible jeu plein écran que la capture ne voit pas
                if (!capturer.IsWindowCapture && color.R + color.G + color.B <= 3)
                {
                    if (blackSinceTicks < 0)
                        blackSinceTicks = lastFrameTicks;
                }
                else
                {
                    blackSinceTicks = -1;
                }

                ProcessColor(color, activeMonitor, ref lastSendTicks);
            }
            catch
            {
                // Session perdue : fenêtre fermée, résolution modifiée, sortie de veille...
                bool wasWindowCapture = fallbackWindow != IntPtr.Zero;
                fallbackWindow = IntPtr.Zero;
                lock (_monitorLock) { _monitorChanged = true; }
                if (!wasWindowCapture)
                {
                    Updated?.Invoke(new EngineUpdate(EngineStatus.CaptureError, 0, 0, 0, 0, activeMonitor?.FriendlyName));
                    Thread.Sleep(500);
                }
            }
        }

        capturer?.Dispose();
    }

    private void ProcessColor(FrameColor color, MonitorInfo? monitor, ref long lastSendTicks)
    {
        double brightness = color.BrightnessPercent * BrightnessLevel switch
        {
            1 => 0.5,
            3 => 2.0,
            _ => 1.0
        };
        if (BrightnessLevel == 4)
            brightness = 100;
        brightness = Math.Clamp(brightness, 0, 100);

        double r = color.R, g = color.G, b = color.B;
        if (SmoothTransitions)
        {
            long nowTicks = Environment.TickCount64;
            if (!_emaInitialized)
            {
                (_emaR, _emaG, _emaB, _emaBrightness) = (r, g, b, brightness);
                _emaInitialized = true;
            }
            else
            {
                // Alpha corrigé du temps réellement écoulé : lissage identique quelle que soit la cadence
                double dt = Math.Clamp(nowTicks - _lastEmaTicks, 1, 500);
                double alpha = 1 - Math.Pow(1 - SmoothingAlpha, dt / 33.0);
                _emaR += alpha * (r - _emaR);
                _emaG += alpha * (g - _emaG);
                _emaB += alpha * (b - _emaB);
                _emaBrightness += alpha * (brightness - _emaBrightness);
            }
            _lastEmaTicks = nowTicks;
            (r, g, b, brightness) = (_emaR, _emaG, _emaB, _emaBrightness);
        }

        var final = ((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));

        // Limitation de débit + envoi uniquement sur changement perceptible
        long now = Environment.TickCount64;
        bool colorChanged = final != _lastSentColor;
        bool brightnessChanged = Math.Abs(brightness - _lastSentBrightness) > 1;
        if ((colorChanged || brightnessChanged) && now - lastSendTicks >= MinSendIntervalMs)
        {
            if (colorChanged)
                _govee.SetColor(final.Item1, final.Item2, final.Item3);
            if (brightnessChanged)
            {
                _govee.SetBrightness((int)Math.Round(brightness));
                _lastSentBrightness = brightness;
            }
            _lastSentColor = final;
            lastSendTicks = now;

            Updated?.Invoke(new EngineUpdate(EngineStatus.Active, final.Item1, final.Item2, final.Item3, brightness, monitor?.FriendlyName));
        }
    }

    public void Dispose() => Stop();
}
