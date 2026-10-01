using System.Runtime.InteropServices;
using System.Text;
using Vortice.DXGI;

namespace GoveeScreenV2.Core;

public record MonitorInfo(string DeviceName, string Name, int AdapterIndex, int OutputIndex, int Width, int Height, bool IsPrimary)
{
    public string FriendlyName => $"{Name} ({Width}×{Height})";
    public string Resolution => $"{Width} × {Height}";

    public override string ToString() => FriendlyName;
}

/// <summary>Rectangle en pixels physiques, relatif au coin haut-gauche de l'écran.</summary>
public readonly record struct ScreenRegion(int X, int Y, int Width, int Height);

/// <summary>
/// Énumère les écrans via DXGI et les associe à leur nom commercial.
/// Le nom vient de l'EDID via QueryDisplayConfig (ex. "LG ULTRAGEAR") — EnumDisplayDevices
/// ne renvoie souvent que le nom du pilote ("Generic PnP Monitor").
/// La résolution vient d'EnumDisplaySettings (pixels physiques, insensible à la virtualisation DPI).
/// L'association DXGI ↔ noms se fait par device path GDI ("\\.\DISPLAY1"), commun aux deux APIs.
/// </summary>
public static class MonitorService
{
    // ===== EnumDisplayDevices (fallback nom + liste des devices GDI) =====

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    // ===== EnumDisplaySettings (résolution physique) =====

    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    // ===== QueryDisplayConfig (nom commercial EDID) =====

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        // union (targetMode / sourceMode / desktopImageInfo) couverte par Size = 64
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    /// <summary>Nom EDID par device GDI ("\\.\DISPLAY1" → "LG ULTRAGEAR").</summary>
    private static Dictionary<string, string> GetEdidNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
                return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            for (int i = 0; i < pathCount; i++)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref source) != 0)
                    continue;

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref target) != 0)
                    continue;

                if (!string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName))
                    map[source.viewGdiDeviceName] = target.monitorFriendlyDeviceName;
            }
        }
        catch
        {
            // On retombe sur EnumDisplayDevices
        }
        return map;
    }

    /// <summary>Fallback : nom du pilote par device GDI (souvent "Generic PnP Monitor").</summary>
    private static Dictionary<string, string> GetDriverNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var adapter = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };

        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            var monitor = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceString))
                map[adapter.DeviceName] = monitor.DeviceString;
        }

        return map;
    }

    /// <summary>Résolution physique de l'écran (indépendante du scaling DPI).</summary>
    private static (int Width, int Height)? GetPhysicalResolution(string deviceName)
    {
        var devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref devMode) && devMode.dmPelsWidth > 0)
            return ((int)devMode.dmPelsWidth, (int)devMode.dmPelsHeight);
        return null;
    }

    // ===== HMONITOR & détection de fenêtre plein écran =====

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>HMONITOR de l'écran dont le device GDI est <paramref name="deviceName"/> ("\\.\DISPLAY1").</summary>
    public static IntPtr GetMonitorHandle(string deviceName)
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(hMonitor, ref info) &&
                string.Equals(info.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = hMonitor;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// État de l'écran pour la capture, en un seul passage (un seul EnumDisplayMonitors) :
    /// <list type="bullet">
    /// <item>FullscreenWindow : fenêtre au premier plan si elle couvre entièrement l'écran (jeu
    /// plein écran), IntPtr.Zero sinon. Sert de cible de secours quand la capture d'écran ne
    /// voit pas le contenu (plein écran exclusif, ex. Minecraft 1.8.8).</item>
    /// <item>WorkArea : zone de travail (sans la barre des tâches ni les barres ancrées),
    /// relative à l'écran.</item>
    /// </list>
    /// Null si l'écran est introuvable.
    /// </summary>
    public static (IntPtr FullscreenWindow, ScreenRegion WorkArea)? GetScreenState(string deviceName)
    {
        IntPtr hMonitor = GetMonitorHandle(deviceName);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (hMonitor == IntPtr.Zero || !GetMonitorInfoW(hMonitor, ref info))
            return null;

        var workArea = new ScreenRegion(
            info.rcWork.Left - info.rcMonitor.Left,
            info.rcWork.Top - info.rcMonitor.Top,
            info.rcWork.Right - info.rcWork.Left,
            info.rcWork.Bottom - info.rcWork.Top);
        return (FindFullscreenWindow(hMonitor, info.rcMonitor), workArea);
    }

    private static IntPtr FindFullscreenWindow(IntPtr hMonitor, RECT monitor)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == GetShellWindow())
            return IntPtr.Zero;
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != hMonitor)
            return IntPtr.Zero;

        if (!GetWindowRect(hwnd, out RECT rect))
            return IntPtr.Zero;
        if (rect.Left > monitor.Left || rect.Top > monitor.Top ||
            rect.Right < monitor.Right || rect.Bottom < monitor.Bottom)
            return IntPtr.Zero;

        // Exclut le bureau lui-même (fond d'écran plein écran)
        var className = new StringBuilder(64);
        GetClassName(hwnd, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW")
            return IntPtr.Zero;

        return hwnd;
    }

    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var edidNames = GetEdidNames();
        var driverNames = GetDriverNames();
        var usedNames = new Dictionary<string, int>();

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        if (!desc.AttachedToDesktop)
                            continue;

                        string deviceName = desc.DeviceName;

                        var resolution = GetPhysicalResolution(deviceName)
                            ?? (desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                                desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top);

                        string friendly = edidNames.TryGetValue(deviceName, out var edid) ? edid
                            : driverNames.TryGetValue(deviceName, out var driver) ? driver
                            : deviceName.Replace(@"\\.\", "");

                        // Distingue deux moniteurs identiques ("LG TV", "LG TV (2)")
                        if (usedNames.TryGetValue(friendly, out int count))
                        {
                            usedNames[friendly] = count + 1;
                            friendly = $"{friendly} ({count + 1})";
                        }
                        else
                        {
                            usedNames[friendly] = 1;
                        }

                        // L'écran principal est toujours à l'origine du bureau virtuel
                        bool isPrimary = desc.DesktopCoordinates.Left == 0 && desc.DesktopCoordinates.Top == 0;

                        monitors.Add(new MonitorInfo(
                            deviceName,
                            friendly,
                            (int)a, (int)o,
                            resolution.Item1, resolution.Item2,
                            isPrimary));
                    }
                }
            }
        }

        return monitors;
    }
}
