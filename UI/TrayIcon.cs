using System.Runtime.InteropServices;
using System.Windows.Threading;
using GoveeScreenV2.Core;

namespace GoveeScreenV2.UI;

/// <summary>État reflété par le menu de la zone de notification, relu à chaque ouverture.</summary>
/// <param name="UpdateVersion">Version téléchargée et prête à installer, sinon null.</param>
public record TrayState(string Status, bool IsPaused, int BrightnessLevel,
    IReadOnlyList<MonitorInfo> Monitors, MonitorInfo? SelectedMonitor, string? UpdateVersion);

/// <summary>
/// Icône de la zone de notification : clic gauche = ouvrir la fenêtre,
/// clic droit = actions rapides (pause, intensité, écran) sans ouvrir la fenêtre.
/// Entièrement en Win32 (Shell_NotifyIcon, fenêtre de messages, menu natif sombre) :
/// ni WinForms ni le moteur de rendu WPF (~65 Mo, jamais rendus une fois démarrés) ne sont
/// chargés pour l'icône ou son menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Niveaux d'intensité du sous-menu (1 = 50 %, 4 = Max).</summary>
    private static readonly int[] BrightnessLevels = { 1, 2, 3, 4 };

    // ===== Interop : icône, fenêtre de messages, menu =====

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate int SetPreferredAppModeDelegate(int mode);
    private delegate void FlushMenuThemesDelegate();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    // Unicode comme la classe de fenêtre : la version ANSI tronquerait les textes (titre réduit à « G »)
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, IntPtr idOrSubmenu, string? text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, int type, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("comctl32.dll")]
    private static extern int LoadIconWithScaleDown(IntPtr instance, IntPtr name, int cx, int cy, out IntPtr icon);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(IntPtr name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, int flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    private const string WindowClass = "GoveeScreenTray";
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const int NIIF_INFO = 1, NIIF_RESPECT_QUIET_TIME = 0x80;
    private const int NIN_BALLOONUSERCLICK = 0x0405;
    private const int WM_NULL = 0, WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int SM_CXSMICON = 49, SM_MENUDROPALIGNMENT = 40;
    private const int IMAGE_ICON = 1;
    private const int MF_STRING = 0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    private const int TPM_RIGHTALIGN = 0x8, TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    private const int LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;
    private static readonly IntPtr AppIconId = 32512;   // IDI_APPLICATION : icône embarquée par l'apphost .NET
    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    // Identifiants des commandes du menu
    private const int CmdOpen = 1, CmdPause = 2, CmdExit = 3, CmdUpdate = 4, CmdBrightness = 100, CmdMonitor = 200;

    // ===== État =====

    private readonly Func<TrayState> _getState;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly WndProcDelegate _wndProc;    // gardé en champ : appelé depuis le code natif
    private readonly IntPtr _module = GetModuleHandle(IntPtr.Zero);
    private readonly IntPtr _window;              // fenêtre invisible qui reçoit les clics sur l'icône
    private readonly IntPtr _icon;
    private string _tooltip = "GoveeScreen";

    public event Action? OpenRequested;
    public event Action? PauseToggled;
    public event Action<int>? BrightnessSelected;
    public event Action<MonitorInfo>? MonitorSelected;
    public event Action? ExitRequested;
    public event Action? UpdateRequested;
    public event Action? NotificationClicked;

    public TrayIcon(Func<TrayState> getState)
    {
        _getState = getState;
        EnableDarkMenus();

        // Fenêtre Win32 jamais affichée, hors barre des tâches et Alt+Tab. De premier niveau,
        // et non « message-only », pour recevoir TaskbarCreated. Pas de HwndSource : il
        // démarrerait le moteur de rendu WPF dès le lancement.
        _wndProc = WndProc;
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = _module,
            lpszClassName = WindowClass
        };
        RegisterClassEx(ref windowClass);
        _window = CreateWindowEx(WS_EX_TOOLWINDOW, WindowClass, WindowClass, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);

        _icon = LoadAppIcon();
        Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP);
    }

    /// <summary>
    /// Menus natifs en sombre, assortis à l'interface : SetPreferredAppMode(ForceDark) et
    /// FlushMenuThemes de uxtheme (n° 135 et 136). Non documentées mais stables depuis
    /// Windows 10 1903 ; absentes, le menu reste simplement clair.
    /// </summary>
    private static void EnableDarkMenus()
    {
        IntPtr uxtheme = LoadLibraryEx("uxtheme.dll", IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (uxtheme == IntPtr.Zero)
            return;
        IntPtr setMode = GetProcAddress(uxtheme, 135);
        IntPtr flush = GetProcAddress(uxtheme, 136);
        if (setMode != IntPtr.Zero)
            Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(setMode)(2);   // ForceDark
        if (flush != IntPtr.Zero)
            Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(flush)();
    }

    /// <summary>
    /// Icône de l'exe réduite par Windows à la taille exacte de la zone de notification
    /// (nette en HiDPI). LoadIconWithScaleDown vient des Common Controls v6 (cf. app.manifest) ;
    /// à défaut, LoadImage fait la même chose avec une réduction moins soignée.
    /// </summary>
    private IntPtr LoadAppIcon()
    {
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        try
        {
            if (LoadIconWithScaleDown(_module, AppIconId, size, size, out IntPtr icon) == 0)
                return icon;
        }
        catch (EntryPointNotFoundException)
        {
        }
        return LoadImage(_module, AppIconId, IMAGE_ICON, size, size, 0);
    }

    /// <summary>Info-bulle de l'icône (limitée à 127 caractères par Windows).</summary>
    public void SetStatus(string status)
    {
        string text = $"GoveeScreen — {status}";
        text = text.Length > 127 ? text[..127] : text;
        if (text == _tooltip)
            return;
        _tooltip = text;
        Notify(NIM_MODIFY, NIF_TIP);
    }

    /// <summary>Notification Windows (respecte le mode Concentration / Ne pas déranger).</summary>
    public void ShowNotification(string title, string text)
    {
        Notify(NIM_MODIFY, NIF_INFO, title.Length > 63 ? title[..63] : title, text.Length > 255 ? text[..255] : text,
            NIIF_INFO | NIIF_RESPECT_QUIET_TIME);
    }

    private void Notify(int message, int flags, string infoTitle = "", string info = "", int infoFlags = 0)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = info,
            szInfoTitle = infoTitle,
            dwInfoFlags = infoFlags
        };
        Shell_NotifyIcon(message, ref data);
    }

    /// <summary>
    /// Appelée depuis le code natif : les actions repassent par le Dispatcher, pour qu'une
    /// exception y suive le chemin normal de WPF au lieu de traverser des frames natives.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(long)lParam)   // version d'origine du protocole : lParam = message souris
            {
                case WM_LBUTTONUP:
                    _dispatcher.BeginInvoke(() => OpenRequested?.Invoke());
                    break;
                case WM_RBUTTONUP:
                    _dispatcher.BeginInvoke(ShowMenu);
                    break;
                case NIN_BALLOONUSERCLICK:
                    _dispatcher.BeginInvoke(() => NotificationClicked?.Invoke());
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == TaskbarCreatedMessage)
        {
            // Explorateur redémarré : la zone de notification est vide, on y remet l'icône
            Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Menu reconstruit à chaque ouverture depuis l'état courant, puis détruit.</summary>
    private void ShowMenu()
    {
        var state = _getState();
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING | MF_GRAYED, IntPtr.Zero, $"GoveeScreen · {state.Status}");
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, CmdOpen, Loc.T("Tray.Open"));
            AppendMenu(menu, MF_STRING, CmdPause, Loc.T(state.IsPaused ? "Tray.Resume" : "Pause.Pause"));

            IntPtr brightness = CreatePopupMenu();
            foreach (int level in BrightnessLevels)
                AppendMenu(brightness, MF_STRING | (level == state.BrightnessLevel ? MF_CHECKED : 0), CmdBrightness + level,
                    Loc.T($"Brightness.Level{level}"));
            AppendMenu(menu, MF_POPUP, brightness, Loc.T("Home.Intensity"));

            IntPtr monitors = CreatePopupMenu();
            for (int i = 0; i < state.Monitors.Count; i++)
                AppendMenu(monitors, MF_STRING | (state.Monitors[i] == state.SelectedMonitor ? MF_CHECKED : 0),
                    CmdMonitor + i, state.Monitors[i].FriendlyName);
            AppendMenu(menu, MF_POPUP | (state.Monitors.Count == 0 ? MF_GRAYED : 0), monitors, Loc.T("Nav.Display"));

            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            if (state.UpdateVersion is not null)
                AppendMenu(menu, MF_STRING, CmdUpdate, Loc.T("Tray.RestartUpdate", state.UpdateVersion));
            AppendMenu(menu, MF_STRING, CmdExit, Loc.T("Tray.Exit"));
            SetMenuDefaultItem(menu, CmdOpen, 0);   // en gras, comme le clic gauche

            // Premier plan pendant le menu, sinon il ne se ferme pas quand on clique ailleurs ;
            // le WM_NULL qui suit est la séquence documentée pour les menus de zone de notification
            SetForegroundWindow(_window);
            GetCursorPos(out POINT cursor);
            int flags = TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY |
                        (GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : 0);
            int command = TrackPopupMenuEx(menu, flags, cursor.X, cursor.Y, _window, IntPtr.Zero);
            PostMessage(_window, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            Execute(command, state);
        }
        finally
        {
            DestroyMenu(menu);   // détruit aussi les sous-menus attachés
        }
    }

    private void Execute(int command, TrayState state)
    {
        switch (command)
        {
            case CmdOpen:
                OpenRequested?.Invoke();
                break;
            case CmdPause:
                PauseToggled?.Invoke();
                break;
            case CmdExit:
                ExitRequested?.Invoke();
                break;
            case CmdUpdate:
                UpdateRequested?.Invoke();
                break;
            case > CmdBrightness and <= CmdBrightness + 4:
                BrightnessSelected?.Invoke(command - CmdBrightness);
                break;
            case >= CmdMonitor when command - CmdMonitor < state.Monitors.Count:
                MonitorSelected?.Invoke(state.Monitors[command - CmdMonitor]);
                break;
        }
    }

    public void Dispose()
    {
        Notify(NIM_DELETE, 0);
        DestroyWindow(_window);
        UnregisterClass(WindowClass, _module);
        if (_icon != IntPtr.Zero)
            DestroyIcon(_icon);
    }
}
