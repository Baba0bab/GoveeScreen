using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GoveeScreenV2.Core;

/// <summary>
/// Verrouillage de la session et alimentation de l'écran, reçus par une fenêtre Win32 invisible :
/// notifications WTS (verrouillage, déverrouillage) et paramètre d'alimentation
/// GUID_CONSOLE_DISPLAY_STATE (écran allumé, atténué ou éteint). Comme pour l'icône de la zone
/// de notification, pas de HwndSource : il démarrerait le moteur de rendu WPF.
/// </summary>
public sealed class SessionWatcher : IDisposable
{
    // ===== Interop =====

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
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public int DataLength;
        public int Data;   // DWORD pour l'état de l'écran : 0 éteint, 1 allumé, 2 atténué
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(IntPtr name);

    private const string WindowClass = "GoveeScreenSession";
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WM_WTSSESSION_CHANGE = 0x02B1, WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;
    private const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
    private const int NOTIFY_FOR_THIS_SESSION = 0, DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private static Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    // ===== État =====

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly WndProcDelegate _wndProc;    // gardé en champ : appelé depuis le code natif
    private readonly IntPtr _module = GetModuleHandle(IntPtr.Zero);
    private readonly IntPtr _window;
    private readonly IntPtr _displayNotification;
    private bool _displayOn = true;

    public event Action? Locked;
    public event Action? Unlocked;

    /// <summary>L'écran s'allume (true) ou s'éteint (false) ; atténué compte comme allumé.</summary>
    public event Action<bool>? DisplayPowerChanged;

    public SessionWatcher()
    {
        // Fenêtre de premier niveau jamais affichée, hors barre des tâches et Alt+Tab
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

        WTSRegisterSessionNotification(_window, NOTIFY_FOR_THIS_SESSION);
        // Windows envoie aussitôt l'état courant de l'écran, puis chacun de ses changements
        _displayNotification = RegisterPowerSettingNotification(_window, ref ConsoleDisplayState, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    /// <summary>
    /// Appelée depuis le code natif : les événements repassent par le Dispatcher, pour qu'une
    /// exception y suive le chemin normal de WPF au lieu de traverser des frames natives.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_WTSSESSION_CHANGE)
        {
            switch ((int)(long)wParam)
            {
                case WTS_SESSION_LOCK:
                    _dispatcher.BeginInvoke(() => Locked?.Invoke());
                    break;
                case WTS_SESSION_UNLOCK:
                    _dispatcher.BeginInvoke(() => Unlocked?.Invoke());
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == WM_POWERBROADCAST && (int)(long)wParam == PBT_POWERSETTINGCHANGE)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == ConsoleDisplayState)
            {
                // Seuls les vrais changements comptent : allumé ↔ atténué n'en est pas un
                bool on = setting.Data != 0;
                if (on != _displayOn)
                {
                    _displayOn = on;
                    _dispatcher.BeginInvoke(() => DisplayPowerChanged?.Invoke(on));
                }
            }
            return 1;   // TRUE : notification traitée
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_displayNotification != IntPtr.Zero)
            UnregisterPowerSettingNotification(_displayNotification);
        WTSUnRegisterSessionNotification(_window);
        DestroyWindow(_window);
        UnregisterClass(WindowClass, _module);
    }
}
