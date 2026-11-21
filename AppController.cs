using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using GoveeScreenV2.Core;
using GoveeScreenV2.UI;

namespace GoveeScreenV2;

/// <summary>
/// Cœur de l'application, indépendant de la fenêtre : configuration, lampe, moteur de
/// synchronisation et icône de la zone de notification. La fenêtre n'existe que lorsqu'elle
/// est affichée : en zone de notification elle est détruite (arbre visuel, cible de rendu)
/// puis recréée à la demande, ce qui rend sa mémoire pendant que l'application tourne seule.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly TrayIcon _tray;
    private readonly SessionWatcher _session;
    private MainWindow? _window;
    private bool _closingToTray;
    private bool _exiting;

    private volatile bool _windowVisible;             // fenêtre affichée et non réduite
    private volatile EngineUpdate? _pendingColor;     // dernière couleur reçue, affichée au retour de la fenêtre
    private DateTime _lastUiUpdate = DateTime.MinValue;
    private bool _lockPaused;                         // pause posée par le verrouillage, levée au déverrouillage
    private readonly DispatcherTimer _appUpdateTimer;
    private bool _installScheduled;                   // installation confiée au programme de mise à jour
    private bool _appUpdateNotified;

    public AppConfig Config { get; }
    public GoveeClient Govee { get; }
    public SyncEngine Engine { get; }
    public AppUpdater Updater { get; }
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public MonitorInfo? SelectedMonitor { get; private set; }
    public EngineStatus Status { get; private set; } = EngineStatus.Starting;

    /// <summary>Dernière couleur affichée, null tant qu'aucune n'a été envoyée.</summary>
    public EngineUpdate? LastColor { get; private set; }

    public AppController()
    {
        Config = AppConfig.Load();
        Govee = new GoveeClient(Config.DeviceIp);
        Engine = new SyncEngine(Govee)
        {
            BrightnessLevel = Config.BrightnessLevel,
            SmoothTransitions = Config.SmoothTransitions,
            TargetFps = Config.CaptureFps,
            AutoFps = Config.CaptureFpsAuto,
            StaticScreenTimeout = StaticScreenTimeout(Config)
        };
        Engine.Updated += OnEngineUpdated;

        _tray = new TrayIcon(GetTrayState);
        _tray.OpenRequested += ShowWindow;
        _tray.PauseToggled += TogglePause;
        _tray.BrightnessSelected += SetBrightnessLevel;
        _tray.MonitorSelected += SelectMonitor;
        _tray.ExitRequested += Exit;
        _tray.UpdateRequested += RestartToUpdate;
        _tray.NotificationClicked += ShowWindow;

        Updater = new AppUpdater(Config.AppUpdatePrerelease);
        Updater.StateChanged += () => _dispatcher.BeginInvoke(OnAppUpdateChanged);

        // Mises à jour : première recherche peu après le démarrage, puis toutes les 12 h
        _appUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _appUpdateTimer.Tick += (_, _) =>
        {
            _appUpdateTimer.Interval = TimeSpan.FromHours(12);
            if (Config.AppUpdateCheck)
                _ = Updater.CheckAndDownloadAsync();
        };

        _session = new SessionWatcher();
        _session.Locked += OnSessionLocked;
        _session.Unlocked += OnSessionUnlocked;
        _session.DisplayPowerChanged += OnDisplayPowerChanged;

        // Fermeture de session Windows : les fenêtres se ferment sans que ce soit un retour en zone de notification
        Application.Current.SessionEnding += (_, _) => _exiting = true;

        LoadMonitors();
        SetStatus(EngineStatus.Starting);
    }

    public void Start()
    {
        Govee.TurnOn();
        Engine.Start();
        if (Updater.IsInstalled)
            _appUpdateTimer.Start();
    }

    // ===== Fenêtre =====

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(this);
            _window.Closed += OnWindowClosed;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Ferme la fenêtre sans quitter : l'application continue dans la zone de notification.</summary>
    public void HideToTray()
    {
        if (_window is null)
            return;
        _closingToTray = true;
        _window.Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (Application.Current.MainWindow == _window)
            Application.Current.MainWindow = null;
        _window = null;
        _windowVisible = false;
        bool toTray = _closingToTray || Config.CloseToTray;
        _closingToTray = false;
        if (_exiting)
            return;
        if (!toTray)
        {
            Exit();
            return;
        }

        // L'arbre visuel a vécu assez longtemps pour être promu en génération 2, que le GC
        // ne repasserait pas avant longtemps vu le peu d'allocations : on le collecte maintenant
        _dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        });
    }

    /// <summary>La fenêtre signale qu'elle est (ou n'est plus) visible à l'écran.</summary>
    public void SetWindowVisible(bool visible)
    {
        _windowVisible = visible;
        if (visible && Status == EngineStatus.Active && !Engine.IsPaused && _pendingColor is { } update)
        {
            LastColor = update;
            SetStatus(Status);
        }
    }

    public void Exit()
    {
        if (_exiting)
            return;
        _exiting = true;
        _appUpdateTimer.Stop();
        // Mise à jour téléchargée : installée en silence une fois l'application fermée
        if (!_installScheduled && Config.AppUpdateAutoInstall && Updater.State == AppUpdateState.Ready)
            _installScheduled = Updater.ScheduleInstall(restart: false);
        _window?.Close();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Redémarre l'application (pour appliquer un changement de langue). La nouvelle instance
    /// attend la fin de celle-ci avant de prendre le relais.
    /// </summary>
    public void Restart()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
            return;
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            ArgumentList = { App.WaitForArgument, Environment.ProcessId.ToString() }
        })?.Dispose();
        Exit();
    }

    // ===== Mises à jour de l'application =====

    /// <summary>Installe la mise à jour téléchargée, puis relance l'application.</summary>
    public void RestartToUpdate()
    {
        if (Updater.State != AppUpdateState.Ready)
            return;
        if (!Updater.ScheduleInstall(restart: true))
        {
            ShowWindow();
            return;   // l'erreur s'affiche dans les paramètres
        }
        _installScheduled = true;
        Exit();
    }

    public void SetAppUpdatePrerelease(bool include)
    {
        Config.AppUpdatePrerelease = include;
        Config.Save();
        Updater.SetIncludePrerelease(include);
    }

    private void OnAppUpdateChanged()
    {
        _window?.SyncAppUpdate();
        if (Updater.State != AppUpdateState.Ready || _appUpdateNotified)
            return;
        _appUpdateNotified = true;
        _tray.ShowNotification(Loc.T("AppUpdate.NotifyTitle", Updater.AvailableVersion),
            Loc.T(Config.AppUpdateAutoInstall ? "AppUpdate.NotifyAuto" : "AppUpdate.NotifyManual"));
    }

    /// <summary>Quitter éteint la lampe, comme la V1.</summary>
    public void Dispose()
    {
        Config.Save();
        Engine.Stop();
        Govee.Dispose();
        _tray.Dispose();
        _session.Dispose();
    }

    // ===== Pause, intensité =====

    public void TogglePause() => SetPaused(!Engine.IsPaused);

    /// <summary>Pause ou reprise demandée par l'utilisateur : le déverrouillage n'y touchera pas.</summary>
    public void SetPaused(bool paused)
    {
        _lockPaused = false;
        ApplyPause(paused);
    }

    private void ApplyPause(bool paused)
    {
        Engine.SetPaused(paused);
        if (paused)
            Govee.TurnOff();
        else
            Govee.TurnOn();
        _window?.SyncPauseState();

        // Écran statique : le moteur n'enverra rien tant que l'image ne change pas,
        // on repasse donc directement en "actif" avec la dernière couleur connue
        SetStatus(paused ? EngineStatus.Paused : EngineStatus.Active);
    }

    public void SetBrightnessLevel(int level)
    {
        level = Math.Clamp(level, 1, 4);
        Engine.BrightnessLevel = level;
        Config.BrightnessLevel = level;
        Config.Save();
        _window?.SyncBrightnessSegments();
    }

    // ===== Verrouillage de la session =====

    private void OnSessionLocked()
    {
        // Déjà en pause (pause manuelle) : le déverrouillage ne la lèvera pas
        if (!Config.PauseOnLock || Engine.IsPaused)
            return;
        _lockPaused = true;
        ApplyPause(true);
    }

    private void OnSessionUnlocked()
    {
        if (!_lockPaused)
            return;
        _lockPaused = false;
        ApplyPause(false);
    }

    /// <summary>
    /// Écran de verrouillage : la lampe se rallume (sur sa dernière couleur) dès que l'écran se
    /// rallume pour demander le mot de passe, et s'éteint s'il se remet en veille. La capture ne
    /// reprend qu'au déverrouillage : d'ici là, le bureau n'est pas affiché.
    /// </summary>
    private void OnDisplayPowerChanged(bool on)
    {
        if (!_lockPaused || !Config.LampOnLockScreen)
            return;
        if (on)
            Govee.TurnOn();
        else
            Govee.TurnOff();
    }

    // ===== Écrans, capture =====

    public void LoadMonitors()
    {
        Monitors = MonitorService.GetMonitors();
        var selected = Monitors.FirstOrDefault(m => m.DeviceName == Config.MonitorDeviceName)
                       ?? Monitors.FirstOrDefault();
        ApplyMonitor(selected, save: false);
    }

    public void SelectMonitor(MonitorInfo? monitor) => ApplyMonitor(monitor, save: true);

    private void ApplyMonitor(MonitorInfo? monitor, bool save)
    {
        SelectedMonitor = monitor;
        Engine.SetMonitor(monitor);
        if (save && monitor is not null)
        {
            Config.MonitorDeviceName = monitor.DeviceName;
            Config.Save();
        }
        _window?.SyncMonitors();
        SetStatus(Status);   // le nom de l'écran fait partie de l'état affiché
    }

    /// <summary>Fréquence de capture, 0 = Auto.</summary>
    public void SetCaptureFps(int fps)
    {
        bool auto = fps == 0;
        Engine.AutoFps = auto;
        Config.CaptureFpsAuto = auto;
        if (!auto)
        {
            Engine.TargetFps = fps;
            Config.CaptureFps = fps;
        }
        Config.Save();
    }

    /// <summary>Extinction de la lampe quand l'écran reste figé pendant <paramref name="minutes"/>.</summary>
    public void SetStaticScreenOff(bool enabled, int minutes)
    {
        Config.StaticScreenOff = enabled;
        Config.StaticScreenOffMinutes = minutes;
        Config.Save();
        Engine.StaticScreenTimeout = StaticScreenTimeout(Config);
    }

    private static TimeSpan? StaticScreenTimeout(AppConfig config) =>
        config.StaticScreenOff ? TimeSpan.FromMinutes(Math.Clamp(config.StaticScreenOffMinutes, 1, 24 * 60)) : null;

    public void SetSmoothTransitions(bool smooth)
    {
        Engine.SmoothTransitions = smooth;
        Config.SmoothTransitions = smooth;
        Config.Save();
    }

    // ===== Lampe =====

    /// <summary>Change la lampe pilotée (adresse déjà validée) ; true si l'adresse a changé.</summary>
    public bool SetDevice(string ip, string? model)
    {
        bool changed = ip != Config.DeviceIp;
        if (!changed && (model is null || model == Config.DeviceModel))
            return false;

        Config.DeviceIp = ip;
        Config.DeviceModel = model ?? (changed ? null : Config.DeviceModel);
        Govee.DeviceIp = ip;
        Config.Save();
        if (changed && !Engine.IsPaused && !Engine.IsIdle)
            Govee.TurnOn();
        return changed;
    }

    // ===== Retour du moteur =====

    private void OnEngineUpdated(EngineUpdate update)
    {
        if (update.Status == EngineStatus.Active)
        {
            _pendingColor = update;
            // Pas de fenêtre visible : rien à redessiner. Seul un changement d'état passe
            // (info-bulle et menu de l'icône) ; la couleur sera affichée au retour.
            if (!_windowVisible && Status == EngineStatus.Active)
                return;

            // Limite le rafraîchissement UI à ~15 Hz pour ne pas charger le Dispatcher
            var now = DateTime.UtcNow;
            if ((now - _lastUiUpdate).TotalMilliseconds < 66)
                return;
            _lastUiUpdate = now;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (update.Status == EngineStatus.Active)
            {
                if (Engine.IsPaused)
                    return; // frame traitée juste avant la pause
                LastColor = update;
            }
            SetStatus(update.Status);
        });
    }

    private void SetStatus(EngineStatus status)
    {
        Status = status;
        _tray.SetStatus(StatusTitle(status));
        _window?.ApplyStatus();
    }

    public static string StatusTitle(EngineStatus status) => Loc.T(status switch
    {
        EngineStatus.Active => "Status.Active",
        EngineStatus.Paused => "Status.Paused",
        EngineStatus.Idle => "Status.Idle",
        EngineStatus.CaptureError => "Status.CaptureError",
        EngineStatus.NoMonitor => "Status.NoMonitor",
        _ => "Status.Starting"
    });

    private TrayState GetTrayState() =>
        new(StatusTitle(Status), Engine.IsPaused, Engine.BrightnessLevel, Monitors, SelectedMonitor,
            Updater.State == AppUpdateState.Ready ? Updater.AvailableVersion : null);
}
