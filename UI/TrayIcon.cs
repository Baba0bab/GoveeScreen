using System.Drawing;
using GoveeScreenV2.Core;
using Forms = System.Windows.Forms;

namespace GoveeScreenV2.UI;

/// <summary>État reflété par le menu de la zone de notification, relu à chaque ouverture.</summary>
public record TrayState(string Status, bool IsPaused, int BrightnessLevel,
    IReadOnlyList<MonitorInfo> Monitors, MonitorInfo? SelectedMonitor);

/// <summary>
/// Icône de la zone de notification : clic gauche = ouvrir la fenêtre,
/// clic droit = actions rapides (pause, intensité, écran) sans ouvrir la fenêtre.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public static readonly (int Level, string Label)[] BrightnessLevels =
        { (1, "50 %"), (2, "100 %"), (3, "200 %"), (4, "Max") };

    private readonly Func<TrayState> _getState;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly DarkMenuRenderer _renderer = new();
    private readonly Font _font = new("Segoe UI", 9.5f);
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem _brightnessItem;
    private readonly Forms.ToolStripMenuItem _monitorItem;

    public event Action? OpenRequested;
    public event Action? PauseToggled;
    public event Action<int>? BrightnessSelected;
    public event Action<MonitorInfo>? MonitorSelected;
    public event Action? ExitRequested;

    public TrayIcon(Func<TrayState> getState)
    {
        _getState = getState;

        _menu = new Forms.ContextMenuStrip();
        StyleDropDown(_menu);
        _menu.Padding = new Forms.Padding(0, _menu.LogicalToDeviceUnits(4), 0, _menu.LogicalToDeviceUnits(4));

        _statusItem = CreateItem("GoveeScreen", null);
        _statusItem.Enabled = false;

        var openItem = CreateItem("Ouvrir GoveeScreen", () => OpenRequested?.Invoke());
        openItem.Font = new Font(_font, FontStyle.Bold);

        _pauseItem = CreateItem("Mettre en pause", () => PauseToggled?.Invoke());

        _brightnessItem = CreateItem("Intensité", null);
        StyleDropDown((Forms.ToolStripDropDownMenu)_brightnessItem.DropDown);
        foreach (var (level, label) in BrightnessLevels)
        {
            var item = CreateItem(label, () => BrightnessSelected?.Invoke(level));
            item.Tag = level;
            _brightnessItem.DropDownItems.Add(item);
        }

        _monitorItem = CreateItem("Écran", null);
        StyleDropDown((Forms.ToolStripDropDownMenu)_monitorItem.DropDown);

        var exitItem = CreateItem("Quitter", () => ExitRequested?.Invoke());

        _menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _statusItem,
            new Forms.ToolStripSeparator(),
            openItem,
            _pauseItem,
            _brightnessItem,
            _monitorItem,
            new Forms.ToolStripSeparator(),
            exitItem
        });
        _menu.Opening += (_, _) => RefreshMenu();

        _icon = new Forms.NotifyIcon
        {
            Text = "GoveeScreen",
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke();
        };
    }

    /// <summary>Info-bulle de l'icône (limitée à 127 caractères par Windows).</summary>
    public void SetStatus(string status)
    {
        string text = $"GoveeScreen — {status}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void RefreshMenu()
    {
        var state = _getState();
        _statusItem.Text = $"GoveeScreen · {state.Status}";
        _pauseItem.Text = state.IsPaused ? "Reprendre la synchronisation" : "Mettre en pause";

        foreach (Forms.ToolStripMenuItem item in _brightnessItem.DropDownItems)
            item.Checked = item.Tag is int level && level == state.BrightnessLevel;

        var previous = _monitorItem.DropDownItems.Cast<Forms.ToolStripItem>().ToList();
        _monitorItem.DropDownItems.Clear();
        previous.ForEach(item => item.Dispose());
        foreach (var monitor in state.Monitors)
        {
            var item = CreateItem(monitor.FriendlyName, () => MonitorSelected?.Invoke(monitor));
            item.Checked = monitor == state.SelectedMonitor;
            _monitorItem.DropDownItems.Add(item);
        }
        _monitorItem.Enabled = state.Monitors.Count > 0;
    }

    private Forms.ToolStripMenuItem CreateItem(string text, Action? onClick)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Padding = new Forms.Padding(0, _menu.LogicalToDeviceUnits(3), 0, _menu.LogicalToDeviceUnits(3))
        };
        if (onClick is not null)
            item.Click += (_, _) => onClick();
        return item;
    }

    private void StyleDropDown(Forms.ToolStripDropDownMenu dropDown)
    {
        dropDown.Renderer = _renderer;
        dropDown.Font = _font;
        dropDown.ShowImageMargin = false;
        dropDown.ShowCheckMargin = true;
        dropDown.HandleCreated += (_, _) =>
        {
            // Windows 11 : coins arrondis et bordure assortie, comme les menus natifs
            Dwm.SetAttribute(dropDown.Handle, Dwm.WindowCornerPreference, Dwm.CornerRoundSmall);
            var border = DarkMenuRenderer.Border;
            Dwm.SetAttribute(dropDown.Handle, Dwm.BorderColor, Dwm.ToColorRef(border.R, border.G, border.B));
        };
    }

    private static Icon LoadIcon()
    {
        try
        {
            var iconUri = new Uri("pack://application:,,,/icon.ico");
            using var stream = System.Windows.Application.GetResourceStream(iconUri)!.Stream;
            return new Icon(stream);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _font.Dispose();
    }
}
