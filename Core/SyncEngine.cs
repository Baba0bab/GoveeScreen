namespace GoveeScreenV2.Core;

public enum EngineStatus { Starting, Active, Paused, CaptureError, NoMonitor }

public record EngineUpdate(EngineStatus Status, byte R, byte G, byte B, double Brightness, string? MonitorName);

/// <summary>
/// Boucle de synchronisation : capture GPU → analyse → envoi UDP Govee.
/// Cadence max ~30 envois/s, mais aucun travail si l'écran est statique
/// (AcquireNextFrame ne rend la main que sur changement d'image).
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private const int FrameTimeoutMs = 100;
    private const int MinSendIntervalMs = 33;
    private const double SmoothingAlpha = 0.35;

    private readonly GoveeClient _govee;
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;

    private MonitorInfo? _monitor;
    private readonly object _monitorLock = new();
    private bool _monitorChanged;

    private (byte r, byte g, byte b) _lastSentColor = (0, 0, 0);
    private double _lastSentBrightness = -1;
    private double _emaR, _emaG, _emaB, _emaBrightness;
    private bool _emaInitialized;

    public int BrightnessLevel { get; set; } = 2;
    public bool SmoothTransitions { get; set; } = true;

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

            if (needRecreate)
            {
                capturer?.Dispose();
                capturer = null;
                try
                {
                    capturer = new ScreenCapturer(wanted);
                    buffer = new byte[capturer.SmallWidth * capturer.SmallHeight * 4];
                    activeMonitor = wanted;
                    _emaInitialized = false;
                }
                catch
                {
                    Updated?.Invoke(new EngineUpdate(EngineStatus.CaptureError, 0, 0, 0, 0, wanted.FriendlyName));
                    Thread.Sleep(1000);
                    lock (_monitorLock) { _monitorChanged = true; }
                    continue;
                }
            }

            try
            {
                if (!capturer!.TryCaptureFrame(FrameTimeoutMs, buffer!))
                    continue; // Écran statique : rien à faire

                var color = FrameAnalyzer.Analyze(buffer!, capturer.SmallWidth, capturer.SmallHeight);
                ProcessColor(color, activeMonitor, ref lastSendTicks);
            }
            catch
            {
                // Session de duplication perdue (changement de résolution, plein écran exclusif...)
                Updated?.Invoke(new EngineUpdate(EngineStatus.CaptureError, 0, 0, 0, 0, activeMonitor?.FriendlyName));
                lock (_monitorLock) { _monitorChanged = true; }
                Thread.Sleep(500);
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
            if (!_emaInitialized)
            {
                (_emaR, _emaG, _emaB, _emaBrightness) = (r, g, b, brightness);
                _emaInitialized = true;
            }
            else
            {
                _emaR += SmoothingAlpha * (r - _emaR);
                _emaG += SmoothingAlpha * (g - _emaG);
                _emaB += SmoothingAlpha * (b - _emaB);
                _emaBrightness += SmoothingAlpha * (brightness - _emaBrightness);
            }
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
