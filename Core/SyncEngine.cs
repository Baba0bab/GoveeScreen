namespace GoveeScreenV2.Core;

public enum EngineStatus { Starting, Active, Paused, CaptureError, NoMonitor }

public record EngineUpdate(EngineStatus Status, byte R, byte G, byte B, double Brightness, string? MonitorName);

/// <summary>
/// Boucle de synchronisation : capture GPU → analyse → envoi UDP Govee.
/// En mode Auto, le traitement suit les frames du compositeur au rythme maximal des
/// envois (30 par seconde, uniquement quand l'image change) ; en mode manuel il est
/// plafonné à TargetFps. Le plafonnement se fait à la source (compositeur) et par saut
/// de frames, sans sleep : pas de jitter, et la frame traitée est toujours la plus récente.
/// Hors frames, le thread dort sur des événements : écran statique = 2 réveils par seconde,
/// pause = aucun (la session de capture est alors libérée).
/// Si la capture d'écran ne voit pas un jeu en plein écran exclusif (frames noires
/// ou plus aucune frame alors qu'une fenêtre plein écran est au premier plan),
/// la boucle bascule automatiquement en capture de cette fenêtre, puis revient
/// à la capture d'écran quand le plein écran se termine.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private const int FullscreenCheckIntervalMs = 500;
    private const int FrameTimeoutMs = FullscreenCheckIntervalMs;  // écran statique : juste de quoi surveiller le plein écran
    private const int MinSendIntervalMs = 33;
    private const int AutoIntervalMs = MinSendIntervalMs;  // mode Auto : traiter plus vite qu'on n'envoie ne sert à rien
    private const int ColorDeadband = 2;               // écart par canal en dessous duquel on n'envoie pas (invisible sur la lampe)
    private const double SmoothingAlpha = 0.35;        // par tranche de 33 ms (corrigé du temps réel)
    private const int FallbackAfterMs = 2000;          // bascule en capture fenêtre après 2 s sans image exploitable

    private readonly GoveeClient _govee;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private readonly AutoResetEvent _wake = new(false);  // réveille la boucle : pause, écran, arrêt

    private MonitorInfo? _monitor;
    private readonly object _monitorLock = new();
    private bool _monitorChanged;

    private volatile bool _autoFps = true;
    private volatile int _targetFps = 30;

    private (byte r, byte g, byte b) _lastSentColor = (0, 0, 0);
    private double _lastSentBrightness = -1;
    private double _emaR, _emaG, _emaB, _emaBrightness;
    private long _lastEmaTimestamp;
    private bool _emaInitialized;

    public int BrightnessLevel { get; set; } = 2;
    public bool SmoothTransitions { get; set; } = true;

    /// <summary>Cadence automatique (jusqu'à 30 FPS, le rythme d'envoi, quand l'image change).</summary>
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
        _wake.Set();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _wake.Set();
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
        _wake.Set();
        _thread?.Join(1000);
        _govee.TurnOff();
    }

    private void RunLoop()
    {
        ScreenCapturer? capturer = null;
        byte[]? buffer = null;
        MonitorInfo? activeMonitor = null;
        long lastSend = 0;
        long lastFrame = Pacing.Now;                   // dernière frame reçue (traitée ou sautée)
        long blackSince = -1;                          // début de la période "image entièrement noire"
        long lastFullscreenCheck = 0;
        IntPtr fallbackWindow = IntPtr.Zero;           // fenêtre plein écran capturée en secours

        while (_running)
        {
            if (_paused)
            {
                // Libère la session de capture et sa VRAM, puis dort jusqu'à la reprise
                capturer?.Dispose();
                capturer = null;
                _wake.WaitOne();
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
                _wake.WaitOne(); // SetMonitor réveille la boucle
                continue;
            }

            if (!Equals(wanted, activeMonitor))
                fallbackWindow = IntPtr.Zero;

            // Bascule capture écran ↔ capture fenêtre plein écran
            if (!needRecreate && Pacing.IsDue(lastFullscreenCheck, FullscreenCheckIntervalMs))
            {
                lastFullscreenCheck = Pacing.Now;
                var screen = MonitorService.GetScreenState(wanted.DeviceName);
                IntPtr fullscreen = screen?.FullscreenWindow ?? IntPtr.Zero;
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
                    bool starved = Pacing.ElapsedMs(lastFrame) >= FallbackAfterMs;
                    bool allBlack = blackSince >= 0 && Pacing.ElapsedMs(blackSince) >= FallbackAfterMs;
                    if (starved || allBlack)
                    {
                        fallbackWindow = fullscreen;
                        needRecreate = true;
                    }
                }

                if (!needRecreate)
                    ApplyCaptureRegion(capturer!, screen);
            }

            if (needRecreate)
            {
                capturer?.Dispose();
                capturer = null;
                try
                {
                    capturer = new ScreenCapturer(wanted, fallbackWindow, _wake);
                    ApplyCaptureRegion(capturer, MonitorService.GetScreenState(wanted.DeviceName));
                    buffer = new byte[capturer.SmallWidth * capturer.SmallHeight * 4];
                    activeMonitor = wanted;
                    _emaInitialized = false;
                    lastFrame = Pacing.Now;
                    blackSince = -1;
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
                    _wake.WaitOne(1000);
                    lock (_monitorLock) { _monitorChanged = true; }
                    continue;
                }
            }

            try
            {
                int minInterval = _autoFps ? AutoIntervalMs : 1000 / _targetFps;
                var result = capturer!.TryCaptureFrame(FrameTimeoutMs, minInterval, buffer!);
                if (result == CaptureResult.Timeout)
                    continue; // Écran statique ou réveil (pause, écran) : rien à faire

                lastFrame = Pacing.Now;
                if (result == CaptureResult.Skipped)
                    continue; // Frame ignorée pour respecter la cadence

                var color = FrameAnalyzer.Analyze(buffer!, capturer.SmallWidth, capturer.SmallHeight);

                // Image entièrement noire : possible jeu plein écran que la capture ne voit pas
                if (!capturer.IsWindowCapture && color.R + color.G + color.B <= 3)
                {
                    if (blackSince < 0)
                        blackSince = lastFrame;
                }
                else
                {
                    blackSince = -1;
                }

                ProcessColor(color, activeMonitor, ref lastSend);
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
                    _wake.WaitOne(500);
                }
            }
        }

        capturer?.Dispose();
    }

    /// <summary>
    /// Hors plein écran, la barre des tâches n'entre pas dans la moyenne : seule la zone de
    /// travail est échantillonnée. Un jeu ou une vidéo en plein écran est pris en entier.
    /// </summary>
    private static void ApplyCaptureRegion(ScreenCapturer capturer, (IntPtr FullscreenWindow, ScreenRegion WorkArea)? screen) =>
        capturer.SetRegion(screen is { } s && s.FullscreenWindow == IntPtr.Zero ? s.WorkArea : null);

    private void ProcessColor(FrameColor color, MonitorInfo? monitor, ref long lastSend)
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
            if (!_emaInitialized)
            {
                (_emaR, _emaG, _emaB, _emaBrightness) = (r, g, b, brightness);
                _emaInitialized = true;
            }
            else
            {
                // Alpha corrigé du temps réellement écoulé : lissage identique quelle que soit la cadence
                double dt = Math.Clamp(Pacing.ElapsedMs(_lastEmaTimestamp), 1, 500);
                double alpha = 1 - Math.Pow(1 - SmoothingAlpha, dt / 33.0);
                _emaR += alpha * (r - _emaR);
                _emaG += alpha * (g - _emaG);
                _emaB += alpha * (b - _emaB);
                _emaBrightness += alpha * (brightness - _emaBrightness);
            }
            _lastEmaTimestamp = Pacing.Now;
            (r, g, b, brightness) = (_emaR, _emaG, _emaB, _emaBrightness);
        }

        var final = ((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));

        // Limitation de débit + envoi uniquement sur changement perceptible : les écarts
        // de 1/255 (fin de lissage, bruit d'encodage vidéo) ne valent pas un paquet
        bool colorChanged =
            Math.Abs(final.Item1 - _lastSentColor.r) >= ColorDeadband ||
            Math.Abs(final.Item2 - _lastSentColor.g) >= ColorDeadband ||
            Math.Abs(final.Item3 - _lastSentColor.b) >= ColorDeadband;
        bool brightnessChanged = Math.Abs(brightness - _lastSentBrightness) > 1;
        if ((colorChanged || brightnessChanged) && Pacing.IsDue(lastSend, MinSendIntervalMs))
        {
            if (colorChanged)
            {
                _govee.SetColor(final.Item1, final.Item2, final.Item3);
                _lastSentColor = final;   // référence = ce que la lampe affiche réellement
            }
            if (brightnessChanged)
            {
                _govee.SetBrightness((int)Math.Round(brightness));
                _lastSentBrightness = brightness;
            }
            lastSend = Pacing.Now;

            Updated?.Invoke(new EngineUpdate(EngineStatus.Active, final.Item1, final.Item2, final.Item3, brightness, monitor?.FriendlyName));
        }
    }

    public void Dispose() => Stop();
}
