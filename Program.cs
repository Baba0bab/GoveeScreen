// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2025-2026 Baba0bab — GoveeScreen

using GoveeScreenV2.Core;
using Velopack;

namespace GoveeScreenV2;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var config = AppConfig.Load();

        // Avant tout : l'installateur et le programme de mise à jour relancent l'exe avec leurs
        // propres arguments (installation, mise à jour, désinstallation), traités ici avant de quitter.
        VelopackApp.Build()
            // Désinstallation : retire le lancement avec Windows (la config dans %APPDATA% est conservée)
            .OnBeforeUninstallFastCallback(_ =>
            {
                try { StartupManager.SetEnabled(false, false); } catch { }
            })
            // Une mise à jour téléchargée s'installe au démarrage suivant, sauf si l'utilisateur l'a désactivé
            .SetAutoApplyOnStartup(config.AppUpdateAutoInstall)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
