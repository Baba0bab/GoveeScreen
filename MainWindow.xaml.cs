using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GoveeScreenV2.Core;
using Forms = System.Windows.Forms;

namespace GoveeScreenV2;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly GoveeClient _govee;
    private readonly SyncEngine _engine;
    private Forms.NotifyIcon? _trayIcon;
    private List<MonitorInfo> _monitors = new();
    private bool _initializing = true;
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
            TargetFps = _config.CaptureFps
        };
        _engine.Updated += OnEngineUpdated;

        IpTextBox.Text = _config.DeviceIp;
        BrightnessSlider.Value = _config.BrightnessLevel;
        SmoothCheck.IsChecked = _config.SmoothTransitions;
        StartupCheck.IsChecked = StartupManager.IsEnabled();
        MinimizedCheck.IsChecked = _config.StartMinimized;
        FpsCombo.ItemsSource = new[] { 10, 15, 30, 60 };
        FpsCombo.SelectedItem = FpsCombo.Items.Contains(_config.CaptureFps) ? _config.CaptureFps : 30;

        LoadMonitors();
        SetupTrayIcon();

        _initializing = false;

        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Loaded += (_, _) =>
        {
            _govee.TurnOn();
            _engine.Start();
            if (_config.StartMinimized || Environment.GetCommandLineArgs().Contains("--minimized"))
                HideToTray();
        };
        Closing += OnWindowClosing;
    }

    // ===== Barre de titre sombre (Windows 10 20H1+) =====

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void EnableDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref enabled, sizeof(int));
    }

    // ===== Écrans =====

    private void LoadMonitors()
    {
        _monitors = MonitorService.GetMonitors();
        MonitorCombo.ItemsSource = _monitors;

        MonitorInfo? selected = _monitors.FirstOrDefault(m => m.DeviceName == _config.MonitorDeviceName)
                                ?? _monitors.FirstOrDefault();
        MonitorCombo.SelectedItem = selected;
        _engine.SetMonitor(selected);
    }

    private void MonitorCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MonitorCombo.SelectedItem is not MonitorInfo monitor)
            return;
        _engine.SetMonitor(monitor);
        if (_initializing)
            return;
        _config.MonitorDeviceName = monitor.DeviceName;
        _config.Save();
    }

    private void RefreshMonitors_Click(object sender, RoutedEventArgs e) => LoadMonitors();

    // ===== Appareil =====

    private void ApplyIp(string ip)
    {
        ip = ip.Trim();
        if (string.IsNullOrEmpty(ip) || ip == _config.DeviceIp)
            return;
        _config.DeviceIp = ip;
        _govee.DeviceIp = ip;
        _config.Save();
        _govee.TurnOn();
    }

    private void IpTextBox_LostFocus(object sender, RoutedEventArgs e) => ApplyIp(IpTextBox.Text);

    private void IpTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyIp(IpTextBox.Text);
            Keyboard.ClearFocus();
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanStatus.Visibility = Visibility.Visible;
        ScanStatus.Text = "Recherche d'appareils Govee sur le réseau…";
        DevicesCombo.Visibility = Visibility.Collapsed;

        try
        {
            var devices = await GoveeClient.DiscoverAsync(TimeSpan.FromSeconds(3));
            if (devices.Count == 0)
            {
                ScanStatus.Text = "Aucun appareil trouvé. Vérifiez que le mode LAN est activé dans l'app Govee Home.";
            }
            else
            {
                ScanStatus.Text = $"{devices.Count} appareil(s) trouvé(s) — sélectionnez-en un :";
                DevicesCombo.ItemsSource = devices;
                DevicesCombo.Visibility = Visibility.Visible;
                if (devices.Count == 1)
                    DevicesCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            ScanStatus.Text = $"Erreur de découverte : {ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void DevicesCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DevicesCombo.SelectedItem is not GoveeDevice device)
            return;
        IpTextBox.Text = device.Ip;
        ApplyIp(device.Ip);
    }

    // ===== Réglages =====

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || _engine is null)
            return;
        _engine.BrightnessLevel = (int)BrightnessSlider.Value;
        _config.BrightnessLevel = _engine.BrightnessLevel;
        _config.Save();
    }

    private void SmoothCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _engine.SmoothTransitions = SmoothCheck.IsChecked == true;
        _config.SmoothTransitions = _engine.SmoothTransitions;
        _config.Save();
    }

    private void FpsCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || FpsCombo.SelectedItem is not int fps)
            return;
        _engine.TargetFps = fps;
        _config.CaptureFps = fps;
        _config.Save();
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        StartupManager.SetEnabled(StartupCheck.IsChecked == true, _config.StartMinimized);
    }

    private void MinimizedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;
        _config.StartMinimized = MinimizedCheck.IsChecked == true;
        _config.Save();
        if (StartupCheck.IsChecked == true)
            StartupManager.SetEnabled(true, _config.StartMinimized);
    }

    // ===== Pause / Tray =====

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        bool pause = !_engine.IsPaused;
        _engine.SetPaused(pause);
        PauseButton.Content = pause ? "▶  Reprendre" : "⏸  Pause";
        if (pause)
            _govee.TurnOff();
        else
            _govee.TurnOn();
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "GoveeScreen",
            Visible = true
        };

        try
        {
            var iconUri = new Uri("pack://application:,,,/icon.ico");
            using var stream = Application.GetResourceStream(iconUri)!.Stream;
            _trayIcon.Icon = new System.Drawing.Icon(stream);
        }
        catch
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Afficher", null, (_, _) => ShowFromTray());
        menu.Items.Add("Quitter", null, (_, _) => Close());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void HideToTray() => Hide();

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Fermer la fenêtre quitte l'application (et éteint la lampe), comme la V1
        _config.Save();
        _engine.Stop();
        _govee.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
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
            switch (update.Status)
            {
                case EngineStatus.Active:
                    ColorPreview.Background = new SolidColorBrush(Color.FromRgb(update.R, update.G, update.B));
                    ColorText.Text = $"RGB {update.R}, {update.G}, {update.B}";
                    BrightnessText.Text = $"Luminosité {update.Brightness:0}%";
                    SetStatus("#FF4BD97C", $"Actif — {update.MonitorName?.Split(' ')[0] ?? "écran"}");
                    break;
                case EngineStatus.Paused:
                    SetStatus("#FF8A8A99", "En pause");
                    break;
                case EngineStatus.CaptureError:
                    SetStatus("#FFFF5C5C", "Erreur de capture");
                    break;
                case EngineStatus.NoMonitor:
                    SetStatus("#FFFFC24B", "Aucun écran");
                    break;
            }
        });
    }

    private void SetStatus(string colorHex, string text)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorHex);
        StatusDot.Fill = new SolidColorBrush(color);
        StatusText.Text = text;
        StatusPill.Background = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B));
    }
}
