using System.IO;
using System.Text.Json;

namespace GoveeScreenV2.Core;

public class AppConfig
{
    public string DeviceIp { get; set; } = "192.168.1.175";
    public string? MonitorDeviceName { get; set; }
    public int BrightnessLevel { get; set; } = 2; // 1=50%, 2=100%, 3=200%, 4=Max
    public bool StartMinimized { get; set; }
    public bool SmoothTransitions { get; set; } = true;
    public int CaptureFps { get; set; } = 30;
    public bool CaptureFpsAuto { get; set; } = true;
    public string? DeviceModel { get; set; }   // SKU renvoyé par la découverte (ex. "H6008"), affichage seulement
    public bool CloseToTray { get; set; }      // ✕ masque la fenêtre au lieu de quitter
    public bool PauseOnLock { get; set; }      // pause au verrouillage de la session, reprise au déverrouillage
    public bool LampOnLockScreen { get; set; } // avec PauseOnLock : lampe rallumée quand l'écran de verrouillage s'allume

    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoveeScreen");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();

            // Migration depuis la config V1 (~/govee_config.json)
            string v1Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "govee_config.json");
            if (File.Exists(v1Path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(v1Path));
                var config = new AppConfig();
                if (doc.RootElement.TryGetProperty("brightness_level", out var bl))
                    config.BrightnessLevel = bl.GetInt32();
                if (doc.RootElement.TryGetProperty("start_minimized", out var sm))
                    config.StartMinimized = sm.GetBoolean();
                return config;
            }
        }
        catch
        {
            // Config corrompue → valeurs par défaut
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Sauvegarde non critique
        }
    }
}
