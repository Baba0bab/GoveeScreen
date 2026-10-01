# GoveeScreen V2

Synchronise une lampe **Govee** (API LAN, UDP port 4003) avec la couleur moyenne de l'écran — réécriture complète de la V1 Python, en **C# / .NET 10 + WPF**.

## Ce qui change par rapport à la V1

| | V1 (Python) | V2 (C#) |
|---|---|---|
| Capture | CPU (`mss` + PIL), 3 captures/frame à 30 FPS | **GPU** (Windows.Graphics.Capture) : la frame reste en VRAM, compatible jeux plein écran |
| Downscale | `Image.resize` CPU | **Pixel shader** : la frame est réduite directement en miniature ~160×90 (moyenne exacte par bloc), sans copie pleine résolution ; seule la miniature revient au CPU |
| Écran statique | Capture en boucle quand même | **~0 % CPU** — le compositeur ne délivre une frame que si l'image change ; 2 réveils/s au repos, aucun en pause (capture libérée) |
| Cadence | 30 FPS fixe | **Auto** (jusqu'à 30 FPS, le rythme d'envoi) ou manuel 10/15/30/60 — limitée à la source (`MinUpdateInterval`, Windows 11 24H2+) puis par saut de frames sans sleep, cadencé même sur écran 144/240 Hz |
| Plein écran exclusif | Non capturé | **Bascule auto en capture de fenêtre** si l'écran capturé reste noir/muet 2 s (ex. Minecraft 1.8.8), retour auto ensuite |
| Appareil | IP codée en dur | IP configurable + **découverte automatique** (multicast Govee) |
| Bandes noires | 2 zones fixes de 40 px | Détection ligne par ligne sur la miniature (letterbox variable) |
| Barre des tâches | Comptée dans la moyenne | **Exclue** hors plein écran (seule la zone de travail est moyennée, même coût) ; un jeu ou une vidéo plein écran est pris en entier |
| Transitions | Brutes | Lissage exponentiel (désactivable) |
| Démarrage Windows | Raccourci dossier Startup | Clé de registre `HKCU\...\Run` |
| UI | Tkinter | WPF façon Windows 11 : menu latéral repliable, couleur en direct, menu d'actions rapides dans la zone de notification |

La config V1 (`~/govee_config.json`) est migrée automatiquement au premier lancement. La nouvelle config vit dans `%APPDATA%\GoveeScreen\config.json`.

## Lancer / compiler

```powershell
dotnet run                # debug
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# → bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\GoveeScreen.exe (nécessite le runtime .NET 10 Desktop, précompilé ReadyToRun)
```

## Architecture

- [Core/ScreenCapturer.cs](Core/ScreenCapturer.cs) — capture GPU (Windows.Graphics.Capture + Vortice/D3D11), downscale par pixel shader
- [Core/FrameAnalyzer.cs](Core/FrameAnalyzer.cs) — bandes noires + couleur moyenne + luminance Rec. 709
- [Core/SyncEngine.cs](Core/SyncEngine.cs) — boucle capture → analyse → envoi (max 30 msg/s, envoi seulement sur changement)
- [Core/GoveeClient.cs](Core/GoveeClient.cs) — protocole LAN Govee (commandes UDP 4003, découverte multicast 4001/4002)
- [Core/MonitorService.cs](Core/MonitorService.cs) — énumération DXGI + noms commerciaux des écrans
- [AppController.cs](AppController.cs) — cœur de l'application (config, lampe, moteur, icône) ; crée la fenêtre à l'ouverture et la détruit en zone de notification
- [MainWindow.xaml](MainWindow.xaml) — fenêtre (simple vue de l'état) : menu latéral + pages Accueil / Lampe / Écran / Paramètres
- [Themes/Theme.xaml](Themes/Theme.xaml) — palette et styles (cartes, interrupteurs, contrôles segmentés, menu)
- [UI/TrayIcon.cs](UI/TrayIcon.cs) — icône de la zone de notification et son menu natif sombre (pause, intensité, écran), en Win32 pur : ni WinForms ni moteur de rendu WPF chargés pour l'icône

## Interface

- **Accueil** : couleur envoyée à la lampe en direct, pause / reprise, intensité, raccourcis vers les autres pages
- **Lampe** : adresse IP, test de connexion (`devStatus`), détection automatique des lampes du réseau
- **Écran** : choix de l'écran source, fréquence de capture, lissage
- **Paramètres** : démarrage avec Windows, démarrage masqué, fermeture dans la zone de notification
- Menu replié en rail d'icônes (☰, automatique sous 760 px de large) ; état de la synchro toujours visible en bas du menu
- Zone de notification : clic gauche = ouvrir, clic droit = actions rapides sans ouvrir la fenêtre. Masquée, la fenêtre est détruite ; au démarrage masqué (lancement avec Windows) elle n'est jamais créée : ~70 Mo de mémoire privée au lieu de ~165 Mo
- Raccourcis : `Ctrl+1` … `Ctrl+4` pour changer de page, `Ctrl+P` pour pause / reprise

## Prérequis côté lampe

Le **mode LAN** doit être activé pour l'appareil dans l'app Govee Home (Paramètres de l'appareil → LAN Control).
