using System.Windows;

namespace GoveeScreenV2;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Instance unique : évite deux boucles de sync qui se battent pour la lampe
        _instanceMutex = new Mutex(true, "GoveeScreenV2_SingleInstance", out bool isNew);
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
