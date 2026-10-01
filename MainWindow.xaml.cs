using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GoveeScreenV2.Core;
using GoveeScreenV2.UI;

namespace GoveeScreenV2;

public partial class MainWindow : Window
{
    private const double NavExpandedWidth = 240;
    private const double NavCompactWidth = 56;
    private const double NavAutoCompactBelow = 760;   // largeur de fenêtre sous laquelle le menu se replie

    private const string PauseGlyph = "";
    private const string PlayGlyph = "";

    private readonly AppConfig _config;
    private readonly GoveeClient _govee;
    private readonly SyncEngine _engine;
    private readonly TrayIcon _tray;
    private readonly RadioButton[] _navItems;
    private readonly FrameworkElement[] _pages;
    private readonly SolidColorBrush _liveBrush;
    private readonly Color _idleColor;

    private List<MonitorInfo> _monitors = new();
    private MonitorInfo? _selectedMonitor;
    private EngineStatus _status = EngineStatus.Starting;
    private Color _lastColor;
    private double _lastBrightness;
    private bool _hasColor;
    private bool _updatingUi = true;   // vrai pendant les mises à jour programmatiques des contrôles
    private bool _navCompact;
    private bool _wasNarrow;
    private bool _exitRequested;
    private DateTime _lastUiUpdate = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();
        _govee = new GoveeClient(_config.DeviceIp);
        _engine = new SyncEngine(_govee)
        {
            BrightnessLevel = _config.BrightnessLevel,
            SmoothTransitions = _config.SmoothTransitions,
            TargetFps = _config.CaptureFps,
            AutoFps = _config.CaptureFpsAuto
        };
        _engine.Updated += OnEngineUpdated;

        _navItems = new[] { NavHome, NavDevice, NavDisplay, NavSettings };
        _pages = new FrameworkElement[] { HomePage, DevicePage, DisplayPage, SettingsPage };
        foreach (var page in _pages)
            page.RenderTransform = new TranslateTransform();

        // Un seul pinceau partagé par l'orbe, son halo et la pastille du menu
        _idleColor = (Color)FindResource("IdleColor");
        _liveBrush = new SolidColorBrush(_idleColor);
        Orb.Fill = _liveBrush;
        OrbGlow.Fill = _liveBrush;
        SidebarSwatch.Fill = _liveBrush;

        IpTextBox.Text = _config.DeviceIp;
        SmoothSwitch.IsChecked = _config.SmoothTransitions;
        StartupSwitch.IsChecked = StartupManager.IsEnabled();
        MinimizedSwitch.IsChecked = _config.StartMinimized;
        CloseToTraySwitch.IsChecked = _config.CloseToTray;
        AboutCard.Description = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · synchronisation écran → lampe Govee";
        ResetIpHint();
        ResetConnectionHint();
        SyncBrightnessSegments();
        SyncFpsSegments();

        _tray = new TrayIcon(GetTrayState);
        _tray.OpenRequested += ShowFromTray;
        _tray.PauseToggled += TogglePause;
        _tray.BrightnessSelected += SetBrightnessLevel;
        _tray.MonitorSelected += SelectMonitor;
        _tray.ExitRequested += ExitApplication;

        LoadMonitors();
        UpdateSummaryTiles();
        NavHome.IsChecked = true;
        ShowPage(0, animate: false);
        ApplyStatus(EngineStatus.Starting);

        _updatingUi = false;

        SourceInitialized += (_, _) => ApplyFrameTheme();
        Loaded += (_, _) =>
        {
            _govee.TurnOn();
            _engine.Start();
            if (_config.StartMinimized || Environment.GetCommandLineArgs().Contains("--minimized"))
                HideToTray();
        };
        SizeChanged += OnSizeChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    // ===== Cadre de fenêtre =====

    private void ApplyFrameTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var baseColor = (Color)FindResource("BaseColor");
        var textColor = (Color)FindResource("TextColor");
        Dwm.SetAttribute(hwnd, Dwm.UseImmersiveDarkMode, 1);
        // Windows 11 : barre de titre de la couleur du fond, la fenêtre paraît d'un seul tenant
        Dwm.SetAttribute(hwnd, Dwm.CaptionColor, Dwm.ToColorRef(baseColor.R, baseColor.G, baseColor.B));
        Dwm.SetAttribute(hwnd, Dwm.TextColor, Dwm.ToColorRef(textColor.R, textColor.G, textColor.B));
    }

    // ===== Navigation =====

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        int index = Array.IndexOf(_navItems, sender);
        if (index >= 0)
            ShowPage(index);
    }

    private void NavigateTo(int index) => _navItems[index].IsChecked = true;

    private void ShowPage(int index, bool animate = true)
    {
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        PageScroller.ScrollToTop();

        if (!animate)
            return;
        var page = _pages[index];
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        page.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int index))
            NavigateTo(index);
    }

    private void NavToggleButton_Click(object sender, RoutedEventArgs e) => SetNavCompact(!_navCompact);

    private void SetNavCompact(bool compact)
    {
        _navCompact = compact;
        double width = compact ? NavCompactWidth : NavExpandedWidth;
        var animation = new DoubleAnimation(width, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            // Fige la valeur finale, sauf si un autre basculement a eu lieu entre-temps
            if (_navCompact != compact)
                return;
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = width;
        };
        Sidebar.BeginAnimation(WidthProperty, animation);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = ActualWidth < NavAutoCompactBelow;
        if (narrow == _wasNarrow)
            return;
        _wasNarrow = narrow;
        SetNavCompact(narrow);
        PageHost.Margin = narrow ? new Thickness(20, 20, 20, 24) : new Thickness(36, 28, 36, 36);
        SummaryTiles.Columns = narrow ? 1 : 3;   // évite de tronquer l'IP et le nom d'écran
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        int page = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            _ => -1
        };
        if (page >= 0)
        {
            NavigateTo(page);
            e.Handled = true;
        }
        else if (e.Key == Key.P)
        {
            TogglePause();
            e.Handled = true;
        }
    }

    // ===== Pause =====

    private void PauseButton_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause() => SetPaused(!_engine.IsPaused);

    private void SetPaused(bool paused)
    {
        _engine.SetPaused(paused);
        if (paused)
            _govee.TurnOff();
        else
            _govee.TurnOn();

        PauseButton.Tag = paused ? PlayGlyph : PauseGlyph;
        PauseButton.Content = paused ? "Reprendre" : "Mettre en pause";
        SidebarPauseButton.Content = paused ? PlayGlyph : PauseGlyph;
        SidebarPauseButton.ToolTip = paused ? "Reprendre (Ctrl+P)" : "Mettre en pause (Ctrl+P)";

        // Écran statique : le moteur n'enverra rien tant que l'image ne change pas,
        // on repasse donc directement en "actif" avec la dernière couleur connue
        ApplyStatus(paused ? EngineStatus.Paused : EngineStatus.Active);
    }

    // ===== Intensité =====

    private void BrightnessSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || sender is not RadioButton { Tag: string tag })
            return;
        SetBrightnessLevel(int.Parse(tag));
    }

    private void SetBrightnessLevel(int level)
    {
        level = Math.Clamp(level, 1, 4);
        _engine.BrightnessLevel = level;
        _config.BrightnessLevel = level;
        _config.Save();
        SyncBrightnessSegments();
    }

    private void SyncBrightnessSegments() =>
        CheckSegment(BrightnessSegments, _config.BrightnessLevel.ToString());

    private void CheckSegment(Panel segments, string tag)
    {
        var wasUpdating = _updatingUi;
        _updatingUi = true;
        var match = segments.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tag)
                    ?? segments.Children.OfType<RadioButton>().First();
        match.IsChecked = true;
        _updatingUi = wasUpdating;
    }

    // ===== Écrans =====

    private void LoadMonitors()
    {
        _monitors = MonitorService.GetMonitors();
        var selected = _monitors.FirstOrDefault(m => m.DeviceName == _config.MonitorDeviceName)
                       ?? _monitors.FirstOrDefault();

        var wasUpdating = _updatingUi;
        _updatingUi = true;
        MonitorsList.ItemsSource = _monitors;
        MonitorsList.SelectedItem = selected;
        _updatingUi = wasUpdating;

        MonitorsList.Visibility = _monitors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMonitorPlaceholder.Visibility = _monitors.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyMonitor(selected, save: false);
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => LoadMonitors();

    private void MonitorsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi)
            return;
        if (MonitorsList.SelectedItem is MonitorInfo monitor)
            ApplyMonitor(monitor, save: true);
        else
            SelectMonitor(_selectedMonitor); // Ctrl+clic : on ne désélectionne pas
    }

    private void SelectMonitor(MonitorInfo? monitor)
    {
        var wasUpdating = _updatingUi;
        _updatingUi = true;
        MonitorsList.SelectedItem = monitor;
        _updatingUi = wasUpdating;
        ApplyMonitor(monitor, save: true);
    }

    private void ApplyMonitor(MonitorInfo? monitor, bool save)
    {
        _selectedMonitor = monitor;
        _engine.SetMonitor(monitor);
        if (save && monitor is not null)
        {
            _config.MonitorDeviceName = monitor.DeviceName;
            _config.Save();
        }
        UpdateSummaryTiles();
        ApplyStatus(_status);
    }

    // ===== Capture =====

    private void FpsSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || sender is not RadioButton { Tag: string tag })
            return;

        int fps = int.Parse(tag); // 0 = Auto
        bool auto = fps == 0;
        _engine.AutoFps = auto;
        _config.CaptureFpsAuto = auto;
        if (!auto)
        {
            _engine.TargetFps = fps;
            _config.CaptureFps = fps;
        }
        _config.Save();
        UpdateSummaryTiles();
    }

    private void SyncFpsSegments() =>
        CheckSegment(FpsSegments, _config.CaptureFpsAuto ? "0" : _config.CaptureFps.ToString());

    private void SmoothSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _engine.SmoothTransitions = SmoothSwitch.IsChecked == true;
        _config.SmoothTransitions = _engine.SmoothTransitions;
        _config.Save();
        UpdateSummaryTiles();
    }

    // ===== Lampe =====

    private static bool IsValidIPv4(string ip) =>
        ip.Count(c => c == '.') == 3 &&
        IPAddress.TryParse(ip, out var address) &&
        address.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>Applique l'adresse saisie ; false si elle est invalide (le champ passe en erreur).</summary>
    private bool ApplyIp(string ip, string? model = null)
    {
        ip = ip.Trim();
        if (!IsValidIPv4(ip))
        {
            IpTextBox.Tag = "error";
            IpCard.Description = "Adresse invalide — format attendu : 192.168.1.42";
            return false;
        }
        IpTextBox.Tag = null;
        ResetIpHint();

        bool changed = ip != _config.DeviceIp;
        if (!changed && (model is null || model == _config.DeviceModel))
            return true;

        _config.DeviceIp = ip;
        _config.DeviceModel = model ?? (changed ? null : _config.DeviceModel);
        _govee.DeviceIp = ip;
        _config.Save();
        if (changed)
        {
            if (!_engine.IsPaused)
                _govee.TurnOn();
            ResetConnectionHint();

            // Garde la liste des lampes détectées cohérente avec une IP saisie à la main
            _updatingUi = true;
            DevicesList.SelectedItem = DevicesList.Items.OfType<GoveeDevice>().FirstOrDefault(d => d.Ip == ip);
            _updatingUi = false;
        }
        UpdateSummaryTiles();
        return true;
    }

    private void ResetIpHint() =>
        IpCard.Description = "Adresse de la lampe sur le réseau local. Entrée pour appliquer.";

    private void ResetConnectionHint()
    {
        ConnectionCard.Description = "Vérifie que la lampe répond sur le réseau.";
        TestResultIcon.Visibility = Visibility.Collapsed;
    }

    private void IpTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyIp(IpTextBox.Text);

    private void IpTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (ApplyIp(IpTextBox.Text))
                Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Escape)
        {
            // Annule la saisie
            IpTextBox.Text = _config.DeviceIp;
            IpTextBox.Tag = null;
            ResetIpHint();
            Keyboard.ClearFocus();
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyIp(IpTextBox.Text))
            return;

        SetNetworkBusy(true, TestSpinner);
        TestResultIcon.Visibility = Visibility.Collapsed;
        ConnectionCard.Description = "Interrogation de la lampe…";
        try
        {
            var status = await GoveeClient.QueryStatusAsync(_config.DeviceIp, TimeSpan.FromSeconds(2));
            bool ok = status is not null;
            ConnectionCard.Description = ok
                ? $"La lampe répond · {(status!.IsOn ? "allumée" : "éteinte")} · luminosité {status.Brightness} %"
                : "Pas de réponse. Vérifiez l'adresse IP et que le contrôle LAN est activé.";
            TestResultIcon.Text = ok ? "" : "";
            TestResultIcon.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
            TestResultIcon.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ConnectionCard.Description = $"Test impossible : {ex.Message}";
        }
        finally
        {
            SetNetworkBusy(false, TestSpinner);
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        SetNetworkBusy(true, ScanSpinner);
        ScanStatus.Text = "Recherche des lampes Govee sur le réseau…";
        ScanPlaceholder.Visibility = Visibility.Visible;
        DevicesList.Visibility = Visibility.Collapsed;

        try
        {
            var devices = await GoveeClient.DiscoverAsync(TimeSpan.FromSeconds(3));
            if (devices.Count == 0)
            {
                ScanStatus.Text = "Aucune lampe trouvée. Vérifiez que le contrôle LAN est activé dans l'app Govee Home.";
                return;
            }

            ScanPlaceholder.Visibility = Visibility.Collapsed;
            DevicesList.Visibility = Visibility.Visible;
            var current = devices.FirstOrDefault(d => d.Ip == _config.DeviceIp);

            _updatingUi = true;
            DevicesList.ItemsSource = devices;
            DevicesList.SelectedItem = current;
            _updatingUi = false;

            if (current is not null)
                ApplyIp(current.Ip, current.Model);   // mémorise le modèle pour l'affichage
            else if (devices.Count == 1)
                DevicesList.SelectedIndex = 0;         // une seule lampe : sélection directe
        }
        catch (Exception ex)
        {
            ScanStatus.Text = $"Erreur de découverte : {ex.Message}";
        }
        finally
        {
            SetNetworkBusy(false, ScanSpinner);
        }
    }

    /// <summary>Découverte et test écoutent le même port : une seule opération réseau à la fois.</summary>
    private void SetNetworkBusy(bool busy, Control spinner)
    {
        ScanButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || DevicesList.SelectedItem is not GoveeDevice device)
            return;
        IpTextBox.Text = device.Ip;
        ApplyIp(device.Ip, device.Model);
    }

    // ===== Paramètres =====

    private void StartupSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        StartupManager.SetEnabled(StartupSwitch.IsChecked == true, _config.StartMinimized);
    }

    private void MinimizedSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _config.StartMinimized = MinimizedSwitch.IsChecked == true;
        _config.Save();
        if (StartupSwitch.IsChecked == true)
            StartupManager.SetEnabled(true, _config.StartMinimized);
    }

    private void CloseToTraySwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _config.CloseToTray = CloseToTraySwitch.IsChecked == true;
        _config.Save();
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppConfig.ConfigDirectory);
        Process.Start(new ProcessStartInfo(AppConfig.ConfigDirectory) { UseShellExecute = true });
    }

    // ===== Zone de notification / fermeture =====

    private TrayState GetTrayState() =>
        new(StatusTitle(_status), _engine.IsPaused, _engine.BrightnessLevel, _monitors, _selectedMonitor);

    private void HideButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray() => Hide();

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_config.CloseToTray && !_exitRequested)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Quitter éteint la lampe, comme la V1
        _config.Save();
        _engine.Stop();
        _govee.Dispose();
        _tray.Dispose();
    }

    // ===== Retour du moteur vers l'UI =====

    private void OnEngineUpdated(EngineUpdate update)
    {
        // Limite le rafraîchissement UI à ~15 Hz pour ne pas charger le Dispatcher
        var now = DateTime.UtcNow;
        if (update.Status == EngineStatus.Active && (now - _lastUiUpdate).TotalMilliseconds < 66)
            return;
        _lastUiUpdate = now;

        Dispatcher.BeginInvoke(() =>
        {
            if (update.Status == EngineStatus.Active)
            {
                if (_engine.IsPaused)
                    return; // frame traitée juste avant la pause
                _lastColor = Color.FromRgb(update.R, update.G, update.B);
                _lastBrightness = update.Brightness;
                _hasColor = true;
            }
            ApplyStatus(update.Status);
        });
    }

    private static string StatusTitle(EngineStatus status) => status switch
    {
        EngineStatus.Active => "Actif",
        EngineStatus.Paused => "En pause",
        EngineStatus.CaptureError => "Erreur de capture",
        EngineStatus.NoMonitor => "Aucun écran",
        _ => "Démarrage…"
    };

    private void ApplyStatus(EngineStatus status)
    {
        _status = status;
        string monitorName = _selectedMonitor?.Name ?? "Aucun écran sélectionné";

        var (brushKey, heroStatus, detail) = status switch
        {
            EngineStatus.Active => ("SuccessBrush", "Synchronisation active",
                _hasColor ? $"#{_lastColor.R:X2}{_lastColor.G:X2}{_lastColor.B:X2}   ·   luminosité {_lastBrightness:0} %"
                          : "En attente d'un changement à l'écran…"),
            EngineStatus.Paused => ("NeutralBrush", "En pause", "La lampe est éteinte. Reprenez pour la rallumer."),
            EngineStatus.CaptureError => ("DangerBrush", "Erreur de capture", "Nouvelle tentative en cours…"),
            EngineStatus.NoMonitor => ("WarningBrush", "Aucun écran", "Choisissez un écran dans la page Écran."),
            _ => ("WarningBrush", "Démarrage…", " ")
        };

        var statusBrush = (Brush)FindResource(brushKey);
        HeroStatusDot.Fill = statusBrush;
        SidebarStatusDot.Fill = statusBrush;
        HeroStatusText.Text = heroStatus;
        HeroMonitorText.Text = monitorName;
        HeroDetailText.Text = detail;
        SidebarStatusText.Text = StatusTitle(status);
        SidebarStatusDetail.Text = monitorName;
        SidebarSwatchHost.ToolTip = $"{StatusTitle(status)} — {monitorName}";
        _tray.SetStatus(StatusTitle(status));

        // Orbe : couleur en direct quand actif, éteint sinon
        bool live = status == EngineStatus.Active && _hasColor;
        var color = live ? _lastColor : _idleColor;
        _liveBrush.Color = color;
        OrbGlow.Opacity = live ? 0.65 : 0;
        HeroTintStop.Color = live ? Color.FromArgb(0x38, color.R, color.G, color.B) : Colors.Transparent;
    }

    private void UpdateSummaryTiles()
    {
        TileDeviceText.Text = string.IsNullOrEmpty(_config.DeviceModel)
            ? _config.DeviceIp
            : $"{_config.DeviceModel} · {_config.DeviceIp}";
        TileDisplayText.Text = _selectedMonitor?.Name ?? "Aucun écran";
        TileCaptureText.Text = (_config.CaptureFpsAuto ? "Auto" : $"{_config.CaptureFps} FPS")
                               + (_config.SmoothTransitions ? " · lissage" : "");
    }
}
