using System.IO;
using System.Text.Json;

namespace GoveeScreenV2.Core;

public class AppConfig
{
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    public string DeviceIp { get; set; } = "";   // vide au premier lancement : à choisir via la détection automatique
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
    public bool StaticScreenOff { get; set; }          // lampe éteinte quand l'écran ne change plus, rallumée au prochain changement
    public int StaticScreenOffMinutes { get; set; } = 10;
    public bool AppUpdateCheck { get; set; } = true;         // recherche au démarrage puis toutes les 12 h
    public bool AppUpdateAutoInstall { get; set; } = true;   // installation à la fermeture (ou au démarrage suivant)
    public bool AppUpdatePrerelease { get; set; }            // inclure les préversions (bêta)
    /// <summary>Langue de l'interface : "system" (langue d'affichage de Windows) ou un code de <see cref="Loc.Languages"/>.</summary>
    public string Language { get; set; } = Loc.SystemLanguage;

    /// <summary>
    /// Dossier de données distinct (développement, captures d'écran) : la config réelle
    /// n'est pas touchée et une seconde instance peut tourner à côté de la copie installée.
    /// </summary>
    public static string? HomeOverride { get; } =
        Environment.GetEnvironmentVariable("GOVEESCREEN_HOME") is { Length: > 0 } home ? Path.GetFullPath(home) : null;

    public static string ConfigDirectory => HomeOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GoveeScreen");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();

            // Migration depuis la config V1 (~/govee_config.json)
            if (HomeOverride is not null)
                return new AppConfig();
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
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, SaveOptions));
        }
        catch
        {
            // Sauvegarde non critique
        }
    }
}
