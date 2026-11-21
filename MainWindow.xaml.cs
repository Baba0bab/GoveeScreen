using System.Diagnostics;
using System.Globalization;
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

    /// <summary>Version complète, suffixe de pré-version compris (2.1.0-beta.1).</summary>
    private static string AppVersion =>
        typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
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
        for (int i = 0; i < _navItems.Length; i++)
            _navItems[i].ToolTip = Loc.T("Common.Shortcut", _navItems[i].Content, i + 1);
        SetHighlightedText(LanHintText, Loc.T("Device.LanHint"));
        FillLanguages();
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
        PauseOnLockSwitch.IsChecked = _app.Config.PauseOnLock;
        LampOnLockScreenSwitch.IsChecked = _app.Config.LampOnLockScreen;
        AppUpdateCheckSwitch.IsChecked = _app.Config.AppUpdateCheck;
        AppUpdateAutoInstallSwitch.IsChecked = _app.Config.AppUpdateAutoInstall;
        AppUpdatePrereleaseSwitch.IsChecked = _app.Config.AppUpdatePrerelease;
        SyncAppUpdate();
        AboutCard.Description = Loc.T("Settings.AboutDesc", AppVersion);
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
        PauseButton.Content = Loc.T(paused ? "Pause.Resume" : "Pause.Pause");
        SidebarPauseButton.Content = paused ? PlayGlyph : PauseGlyph;
        SidebarPauseButton.ToolTip = Loc.T("Common.Shortcut", PauseButton.Content, "P");
    }

    // ===== Intensité =====

    private void BrightnessSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || sender is not RadioButton { Tag: string tag })
            return;
        _app.SetBrightnessLevel(int.Parse(tag, CultureInfo.InvariantCulture));
    }

    internal void SyncBrightnessSegments() =>
        CheckSegment(BrightnessSegments, _app.Config.BrightnessLevel.ToString(CultureInfo.InvariantCulture));

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
        _app.SetCaptureFps(int.Parse(tag, CultureInfo.InvariantCulture)); // 0 = Auto
        UpdateSummaryTiles();
    }

    private void SyncFpsSegments() =>
        CheckSegment(FpsSegments, _app.Config.CaptureFpsAuto ? "0" : _app.Config.CaptureFps.ToString(CultureInfo.InvariantCulture));

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
            IpCard.Description = Loc.T("Device.IpInvalid");
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
        IpCard.Description = Loc.T("Device.IpHint");

    private void ResetConnectionHint()
    {
        ConnectionCard.Description = Loc.T("Device.ConnectionHint");
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
        ConnectionCard.Description = Loc.T("Device.Testing");
        try
        {
            var status = await GoveeClient.QueryStatusAsync(_app.Config.DeviceIp, TimeSpan.FromSeconds(2));
            bool ok = status is not null;
            ConnectionCard.Description = ok
                ? Loc.T("Device.TestOk", Loc.T(status!.IsOn ? "Device.On" : "Device.Off"), status.Brightness)
                : Loc.T("Device.TestNoReply");
            TestResultIcon.Text = ok ? "" : "";
            TestResultIcon.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
            TestResultIcon.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ConnectionCard.Description = Loc.T("Device.TestError", ex.Message);
        }
        finally
        {
            SetNetworkBusy(false, TestSpinner);
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        SetNetworkBusy(true, ScanSpinner);
        ScanStatus.Text = Loc.T("Device.Scanning");
        ScanPlaceholder.Visibility = Visibility.Visible;
        DevicesList.Visibility = Visibility.Collapsed;

        try
        {
            var devices = await GoveeClient.DiscoverAsync(TimeSpan.FromSeconds(3));
            if (devices.Count == 0)
            {
                ScanStatus.Text = Loc.T("Device.ScanNone");
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
            ScanStatus.Text = Loc.T("Device.ScanError", ex.Message);
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

    private void PauseOnLockSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _app.Config.PauseOnLock = PauseOnLockSwitch.IsChecked == true;
        _app.Config.Save();
    }

    private void LampOnLockScreenSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _app.Config.LampOnLockScreen = LampOnLockScreenSwitch.IsChecked == true;
        _app.Config.Save();
    }

    /// <summary>
    /// Choix de la langue : « Système » d'abord (avec le drapeau de la langue de Windows), puis
    /// chaque langue dans sa propre langue. Le changement s'applique au redémarrage.
    /// </summary>
    private void FillLanguages()
    {
        var system = Loc.Languages.First(l => l.Code == Loc.Resolve(Loc.SystemLanguage));
        LanguageCombo.Items.Add(new LanguageChoice(Loc.SystemLanguage,
            Loc.T("Settings.LanguageSystem", system.NativeName), Flags.For(system.Code)));
        foreach (var (code, name) in Loc.Languages)
            LanguageCombo.Items.Add(new LanguageChoice(code, name, Flags.For(code)));
        LanguageCombo.SelectedItem = LanguageCombo.Items.OfType<LanguageChoice>()
            .FirstOrDefault(c => c.Code == _app.Config.Language) ?? LanguageCombo.Items[0];
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || LanguageCombo.SelectedItem is not LanguageChoice { Code: var code })
            return;
        _app.Config.Language = code;
        _app.Config.Save();
        // Redémarrer n'a d'intérêt que si les textes affichés changent vraiment
        LanguageRestartButton.Visibility = Loc.Resolve(code) != Loc.Language ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LanguageRestart_Click(object sender, RoutedEventArgs e) => _app.Restart();

    /// <summary>Texte dont les parties entre | (« Le |contrôle LAN| doit… ») sont mises en avant.</summary>
    private void SetHighlightedText(TextBlock block, string text)
    {
        block.Inlines.Clear();
        var parts = text.Split('|');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;
            var run = new System.Windows.Documents.Run(parts[i]);
            if (i % 2 == 1)
                run.Foreground = (Brush)FindResource("TextBrush");
            block.Inlines.Add(run);
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppConfig.ConfigDirectory);
        Process.Start(new ProcessStartInfo(AppConfig.ConfigDirectory) { UseShellExecute = true });
    }

    // ===== Mises à jour de l'application =====

    internal void SyncAppUpdate()
    {
        var updater = _app.Updater;
        string? version = updater.AvailableVersion;
        AppUpdateCard.Description = updater.State switch
        {
            AppUpdateState.Unavailable => Loc.T("AppUpdate.Unavailable"),
            AppUpdateState.Checking => Loc.T("AppUpdate.Checking"),
            AppUpdateState.Downloading => Loc.T("AppUpdate.Downloading", version, updater.Progress),
            AppUpdateState.Ready => Loc.T(_app.Config.AppUpdateAutoInstall ? "AppUpdate.ReadyAuto" : "AppUpdate.Ready", version),
            AppUpdateState.UpToDate => Loc.T("AppUpdate.UpToDate", updater.CurrentVersion, Ago(updater.LastCheckUtc ?? DateTime.UtcNow)),
            AppUpdateState.Error => Loc.T("AppUpdate.Error", updater.Error),
            _ => Loc.T(updater.IsPortable ? "AppUpdate.IdlePortable" : "AppUpdate.Idle", updater.CurrentVersion)
        };

        bool installed = updater.IsInstalled;
        bool busy = updater.State is AppUpdateState.Checking or AppUpdateState.Downloading;
        bool ready = updater.State == AppUpdateState.Ready;
        AppUpdateAutoCheckCard.IsEnabled = installed;
        AppUpdateAutoInstallCard.IsEnabled = installed;
        AppUpdatePrereleaseCard.IsEnabled = installed;
        AppUpdateSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateCheckButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        AppUpdateCheckButton.IsEnabled = installed && !busy;
        AppUpdateInstallButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;

        SidebarUpdateButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        SidebarUpdateButton.Content = Loc.T("AppUpdate.Sidebar", version);
        SidebarUpdateButton.ToolTip = Loc.T("AppUpdate.SidebarTooltip", version);

        string notes = PlainNotes(updater.ReleaseNotes);
        AppUpdateNotesCard.Visibility = ready && notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateNotesTitle.Text = Loc.T("AppUpdate.NotesTitle", version);
        AppUpdateNotesText.Text = notes;
    }

    /// <summary>« à l'instant », « il y a 5 min », « il y a 3 h », « le 21 novembre » (selon la langue).</summary>
    private static string Ago(DateTime utc)
    {
        var elapsed = DateTime.UtcNow - utc;
        if (elapsed < TimeSpan.FromMinutes(1))
            return Loc.T("Ago.Now");
        if (elapsed < TimeSpan.FromHours(1))
            return Loc.T("Ago.Minutes", (int)elapsed.TotalMinutes);
        if (elapsed < TimeSpan.FromDays(1))
            return Loc.T("Ago.Hours", (int)elapsed.TotalHours);
        return Loc.T("Ago.Date", utc.ToLocalTime().ToString(Loc.Culture.DateTimeFormat.MonthDayPattern, Loc.Culture));
    }

    /// <summary>Notes de version (Markdown, en anglais) en texte brut, pour l'aperçu dans les paramètres.</summary>
    private static string PlainNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd().TrimStart('#', ' ').Replace("**", "").Replace("`", ""))
            // Titre « [2.1.0] - date » (déjà affiché) et liens de référence « [2.1.0]: https://… »
            .Where(l => !l.StartsWith('[') && !l.StartsWith('|') && !l.StartsWith("---"))
            .Select(l => l.StartsWith("- ") ? "•  " + l[2..] : l);
        return string.Join("\n", lines).Trim();
    }

    private void AppUpdateCheck_Click(object sender, RoutedEventArgs e) => _ = _app.Updater.CheckAndDownloadAsync();

    private void AppUpdateInstall_Click(object sender, RoutedEventArgs e) => _app.RestartToUpdate();

    private void AppUpdateCheckSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _app.Config.AppUpdateCheck = AppUpdateCheckSwitch.IsChecked == true;
        _app.Config.Save();
    }

    private void AppUpdateAutoInstallSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _app.Config.AppUpdateAutoInstall = AppUpdateAutoInstallSwitch.IsChecked == true;
        _app.Config.Save();
        SyncAppUpdate();
    }

    private void AppUpdatePrereleaseSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
            return;
        _app.SetAppUpdatePrerelease(AppUpdatePrereleaseSwitch.IsChecked == true);
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
        string monitorName = _app.SelectedMonitor?.Name ?? Loc.T("Status.NoMonitorSelected");

        var (brushKey, heroStatus, detail) = status switch
        {
            EngineStatus.Active => ("SuccessBrush", Loc.T("Status.SyncActive"),
                color is not null ? Loc.T("Status.Color", $"#{color.R:X2}{color.G:X2}{color.B:X2}", Math.Round(color.Brightness))
                                  : Loc.T("Status.Waiting")),
            EngineStatus.Paused => ("NeutralBrush", Loc.T("Status.Paused"), Loc.T("Status.PausedDetail")),
            EngineStatus.CaptureError => ("DangerBrush", Loc.T("Status.CaptureError"), Loc.T("Status.Retrying")),
            EngineStatus.NoMonitor => ("WarningBrush", Loc.T("Status.NoMonitor"), Loc.T("Status.NoMonitorDetail")),
            _ => ("WarningBrush", Loc.T("Status.Starting"), " ")
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
        TileDeviceText.Text = string.IsNullOrEmpty(_app.Config.DeviceIp)
            ? Loc.T("Home.NoDevice")
            : string.IsNullOrEmpty(_app.Config.DeviceModel)
            ? _app.Config.DeviceIp
            : $"{_app.Config.DeviceModel} · {_app.Config.DeviceIp}";
        TileDisplayText.Text = _app.SelectedMonitor?.Name ?? Loc.T("Status.NoMonitor");
        string fps = _app.Config.CaptureFpsAuto ? Loc.T("Display.Auto") : Loc.T("Home.Fps", _app.Config.CaptureFps);
        TileCaptureText.Text = _app.Config.SmoothTransitions ? Loc.T("Home.WithSmoothing", fps) : fps;
    }
}
