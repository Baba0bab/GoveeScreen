using System.IO;
using Microsoft.Win32;

namespace GoveeScreenV2.Core;

/// <summary>
/// Démarrage automatique avec Windows via la clé de registre Run (HKCU),
/// plus fiable que le raccourci dans le dossier Startup de la V1.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GoveeScreen";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    public static void SetEnabled(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
            return;

        if (enabled)
        {
            string exePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "GoveeScreen.exe");
            string args = startMinimized ? " --minimized" : "";
            key.SetValue(ValueName, $"\"{exePath}\"{args}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
