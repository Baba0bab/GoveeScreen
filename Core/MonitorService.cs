using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace GoveeScreenV2.Core;

public record MonitorInfo(string DeviceName, string FriendlyName, int AdapterIndex, int OutputIndex, int Width, int Height)
{
    public override string ToString() => FriendlyName;
}

/// <summary>
/// Énumère les écrans via DXGI et les associe à leur nom commercial via EnumDisplayDevices.
/// L'association se fait par device path ("\\.\DISPLAY1"), commun aux deux APIs — plus fiable
/// que la correspondance par coordonnées de la V1.
/// </summary>
public static class MonitorService
{
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

    private static Dictionary<string, string> GetFriendlyNames()
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

    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var friendlyNames = GetFriendlyNames();

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
                        int width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                        int height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

                        string friendly = friendlyNames.TryGetValue(deviceName, out var name)
                            ? name
                            : deviceName.Replace(@"\\.\", "");

                        monitors.Add(new MonitorInfo(deviceName, $"{friendly} ({width}×{height})", (int)a, (int)o, width, height));
                    }
                }
            }
        }

        return monitors;
    }
}
