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

    // Halo de l'orbe : profil d'opacité mesuré d'un disque de 100 px flouté (rayon 36), en
    // distance au centre (px) → opacité. Le dégradé radial le reproduit à 3/255 près, sans la
    // passe d'effet (surface intermédiaire + shader) qu'imposait un BlurEffect à chaque couleur.
    private static readonly (double Distance, byte Alpha)[] GlowProfile =
    {
        (0, 255), (20, 252), (25, 248), (30, 237), (35, 219), (40, 190), (45, 153), (50, 111),
        (55, 72), (60, 42), (65, 21), (70, 9), (75, 3), (80, 1), (86, 0)
    };
    private const double GlowRadius = 86;   // demi-largeur de OrbGlow

    private const string PauseGlyph = "";
    private const string PlayGlyph = "";

    private readonly AppController _app;
    private readonly RadioButton[] _navItems;
    private readonly FrameworkElement[] _pages;
    private readonly SolidColorBrush _liveBrush;
    private readonly RadialGradientBrush _glowBrush = new();
    private readonly Color _idleColor;

    private bool _updatingUi = true;   // vrai pendant les mises à jour programmatiques des contrôles
    private bool _navCompact;
    private bool _wasNarrow;

    /// <summary>
    /// Vue de l'application : l'état (config, moteur, écrans) vit dans <see cref="AppController"/>,
    /// qui crée la fenêtre à l'ouverture et la détruit quand elle part en zone de notification.
    /// </summary>
    public MainWindow(AppController app)
    {
        _app = app;
        InitializeComponent();

        _navItems = new[] { NavHome, NavDevice, NavDisplay, NavSettings };
        _pages = new FrameworkElement[] { HomePage, DevicePage, DisplayPage, SettingsPage };
        foreach (var page in _pages)
            page.RenderTransform = new TranslateTransform();

        // Un seul pinceau partagé par l'orbe, son halo et la pastille du menu
        _idleColor = (Color)FindResource("IdleColor");
        _liveBrush = new SolidColorBrush(_idleColor);
        Orb.Fill = _liveBrush;
        SidebarSwatch.Fill = _liveBrush;
        foreach (var (distance, _) in GlowProfile)
            _glowBrush.GradientStops.Add(new GradientStop(Colors.Transparent, distance / GlowRadius));
        OrbGlow.Fill = _glowBrush;

        IpTextBox.Text = _app.Config.DeviceIp;
        SmoothSwitch.IsChecked = _app.Config.SmoothTransitions;
        StartupSwitch.IsChecked = StartupManager.IsEnabled();
        MinimizedSwitch.IsChecked = _app.Config.StartMinimized;
        CloseToTraySwitch.IsChecked = _app.Config.CloseToTray;
        AboutCard.Description = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · synchronisation écran → lampe Govee";
        ResetIpHint();
        ResetConnectionHint();
        SyncBrightnessSegments();
        SyncFpsSegments();
        SyncPauseState();
        SyncMonitors();

        NavHome.IsChecked = true;
        ShowPage(0, animate: false);
        ApplyStatus();

        _updatingUi = false;

        SourceInitialized += (_, _) => ApplyFrameTheme();
        SizeChanged += OnSizeChanged;
        IsVisibleChanged += (_, _) => NotifyVisibility();
        StateChanged += (_, _) => NotifyVisibility();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void NotifyVisibility() =>
        _app.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);

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
            _app.TogglePause();
            e.Handled = true;
        }
    }

    // ===== Pause =====

    private void PauseButton_Click(object sender, RoutedEventArgs e) => _app.TogglePause();

    internal void SyncPauseState()
    {
        bool paused = _app.Engine.IsPaused;
        PauseButton.Tag = paused ? PlayGlyph : PauseGlyph;
        PauseButton.Content = paused ? "Reprendre" : "Mettre en pause";
        SidebarPauseButton.Content = paused ? PlayGlyph : PauseGlyph;
        SidebarPauseButton.ToolTip = paused ? "Reprendre (Ctrl+P)" : "Mettre en pause (Ctrl+P)";
    }

    // ===== Intensité =====

    private void BrightnessSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || sender is not RadioButton { Tag: string tag })
            return;
        _app.SetBrightnessLevel(int.Parse(tag));
    }

    internal void SyncBrightnessSegments() =>
        CheckSegment(BrightnessSegments, _app.Config.BrightnessLevel.ToString());

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

    /// <summary>Liste des écrans et sélection, depuis l'état de l'application.</summary>
    internal void SyncMonitors()
    {
        var wasUpdating = _updatingUi;
        _updatingUi = true;
        if (!ReferenceEquals(MonitorsList.ItemsSource, _app.Monitors))
            MonitorsList.ItemsSource = _app.Monitors;
        MonitorsList.SelectedItem = _app.SelectedMonitor;
        _updatingUi = wasUpdating;

        bool any = _app.Monitors.Count > 0;
        MonitorsList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        NoMonitorPlaceholder.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        UpdateSummaryTiles();
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => _app.LoadMonitors();

    private void MonitorsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi)
            return;
        if (MonitorsList.SelectedItem is MonitorInfo monitor)
            _app.SelectMonitor(monitor);
        else
            SyncMonitors(); // Ctrl+clic : on ne désélectionne pas
    }

    // ===== Capture =====

    private void FpsSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || sender is not RadioButton { Tag: string tag })
            return;
        _app.SetCaptureFps(int.Parse(tag)); // 0 = Auto
        UpdateSummaryTiles();
    }

    private void SyncFpsSegments() =>
        CheckSegment(FpsSegments, _app.Config.CaptureFpsAuto ? "0" : _app.Config.CaptureFps.ToString());

    private void SmoothSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _app.SetSmoothTransitions(SmoothSwitch.IsChecked == true);
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

        if (_app.SetDevice(ip, model))
        {
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
            IpTextBox.Text = _app.Config.DeviceIp;
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
            var status = await GoveeClient.QueryStatusAsync(_app.Config.DeviceIp, TimeSpan.FromSeconds(2));
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
            var current = devices.FirstOrDefault(d => d.Ip == _app.Config.DeviceIp);

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
        StartupManager.SetEnabled(StartupSwitch.IsChecked == true, _app.Config.StartMinimized);
    }

    private void MinimizedSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _app.Config.StartMinimized = MinimizedSwitch.IsChecked == true;
        _app.Config.Save();
        if (StartupSwitch.IsChecked == true)
            StartupManager.SetEnabled(true, _app.Config.StartMinimized);
    }

    private void CloseToTraySwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _app.Config.CloseToTray = CloseToTraySwitch.IsChecked == true;
        _app.Config.Save();
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppConfig.ConfigDirectory);
        Process.Start(new ProcessStartInfo(AppConfig.ConfigDirectory) { UseShellExecute = true });
    }

    // ===== Zone de notification =====

    /// <summary>La fenêtre est détruite (et sa mémoire rendue) ; l'icône la recrée à la demande.</summary>
    private void HideButton_Click(object sender, RoutedEventArgs e) => _app.HideToTray();

    // ===== État de la synchronisation =====

    /// <summary>Reflète l'état de l'application : statut, écran, couleur en direct.</summary>
    internal void ApplyStatus()
    {
        var status = _app.Status;
        var color = _app.LastColor;
        string monitorName = _app.SelectedMonitor?.Name ?? "Aucun écran sélectionné";

        var (brushKey, heroStatus, detail) = status switch
        {
            EngineStatus.Active => ("SuccessBrush", "Synchronisation active",
                color is not null ? $"#{color.R:X2}{color.G:X2}{color.B:X2}   ·   luminosité {color.Brightness:0} %"
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
        string title = AppController.StatusTitle(status);
        SidebarStatusText.Text = title;
        SidebarStatusDetail.Text = monitorName;
        SidebarSwatchHost.ToolTip = $"{title} — {monitorName}";

        // Orbe : couleur en direct quand actif, éteint sinon
        bool live = status == EngineStatus.Active && color is not null;
        var display = live ? Color.FromRgb(color!.R, color.G, color.B) : _idleColor;
        _liveBrush.Color = display;
        if (live && _glowBrush.GradientStops[0].Color != display)
        {
            for (int i = 0; i < GlowProfile.Length; i++)
                _glowBrush.GradientStops[i].Color = Color.FromArgb(GlowProfile[i].Alpha, display.R, display.G, display.B);
        }
        OrbGlow.Opacity = live ? 0.65 : 0;
        HeroTintStop.Color = live ? Color.FromArgb(0x38, display.R, display.G, display.B) : Colors.Transparent;
    }

    private void UpdateSummaryTiles()
    {
        TileDeviceText.Text = string.IsNullOrEmpty(_app.Config.DeviceModel)
            ? _app.Config.DeviceIp
            : $"{_app.Config.DeviceModel} · {_app.Config.DeviceIp}";
        TileDisplayText.Text = _app.SelectedMonitor?.Name ?? "Aucun écran";
        TileCaptureText.Text = (_app.Config.CaptureFpsAuto ? "Auto" : $"{_app.Config.CaptureFps} FPS")
                               + (_app.Config.SmoothTransitions ? " · lissage" : "");
    }
}
