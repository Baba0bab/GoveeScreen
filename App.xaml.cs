using System.Windows;
using GoveeScreenV2.Core;

namespace GoveeScreenV2;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Instance unique : évite deux boucles de sync qui se battent pour la lampe
        // (par dossier de données : une copie de développement avec GOVEESCREEN_HOME tourne à côté)
        string mutexName = AppConfig.HomeOverride is null
            ? "GoveeScreenV2_SingleInstance"
            : "GoveeScreenV2_SingleInstance_" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppConfig.HomeOverride.ToLowerInvariant())))[..16];
        _instanceMutex = new Mutex(true, mutexName, out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _controller = new AppController();
        _controller.Start();
        // Démarrage masqué : la fenêtre n'est même pas créée
        if (!_controller.Config.StartMinimized && !e.Args.Contains("--minimized"))
            _controller.ShowWindow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
