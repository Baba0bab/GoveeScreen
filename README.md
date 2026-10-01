# GoveeScreen V2

Synchronise une lampe **Govee** (API LAN, UDP port 4003) avec la couleur moyenne de l'écran — réécriture complète de la V1 Python, en **C# / .NET 8 + WPF**.

## Ce qui change par rapport à la V1

| | V1 (Python) | V2 (C#) |
|---|---|---|
| Capture | CPU (`mss` + PIL), 3 captures/frame à 30 FPS | **GPU** (Windows.Graphics.Capture) : la frame reste en VRAM, compatible jeux plein écran |
| Downscale | `Image.resize` CPU | **Mipmaps GPU** (`GenerateMips`), seule une miniature ~160×90 revient au CPU |
| Écran statique | Capture en boucle quand même | **0 % CPU** — le compositeur ne délivre une frame que si l'image change |
| Cadence | 30 FPS fixe | **Auto** (jusqu'à 60 FPS, par saut de frames sans sleep) ou manuel 10/15/30/60 — cadencé même sur écran 144/240 Hz |
| Plein écran exclusif | Non capturé | **Bascule auto en capture de fenêtre** si l'écran capturé reste noir/muet 2 s (ex. Minecraft 1.8.8), retour auto ensuite |
| Appareil | IP codée en dur | IP configurable + **découverte automatique** (multicast Govee) |
| Bandes noires | 2 zones fixes de 40 px | Détection ligne par ligne sur la miniature (letterbox variable) |
| Transitions | Brutes | Lissage exponentiel (désactivable) |
| Démarrage Windows | Raccourci dossier Startup | Clé de registre `HKCU\...\Run` |
| UI | Tkinter | WPF façon Windows 11 : menu latéral repliable, couleur en direct, menu d'actions rapides dans la zone de notification |

La config V1 (`~/govee_config.json`) est migrée automatiquement au premier lancement. La nouvelle config vit dans `%APPDATA%\GoveeScreen\config.json`.

## Lancer / compiler

```powershell
dotnet run                # debug
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# → bin\Release\net8.0-windows10.0.22621.0\win-x64\publish\GoveeScreen.exe (nécessite le runtime .NET 8)
```

## Architecture

- [Core/ScreenCapturer.cs](Core/ScreenCapturer.cs) — capture GPU (Windows.Graphics.Capture + Vortice/D3D11), downscale par mipmaps
- [Core/FrameAnalyzer.cs](Core/FrameAnalyzer.cs) — bandes noires + couleur moyenne + luminance Rec. 709
- [Core/SyncEngine.cs](Core/SyncEngine.cs) — boucle capture → analyse → envoi (max 30 msg/s, envoi seulement sur changement)
- [Core/GoveeClient.cs](Core/GoveeClient.cs) — protocole LAN Govee (commandes UDP 4003, découverte multicast 4001/4002)
- [Core/MonitorService.cs](Core/MonitorService.cs) — énumération DXGI + noms commerciaux des écrans
- [MainWindow.xaml](MainWindow.xaml) — fenêtre : menu latéral + pages Accueil / Lampe / Écran / Paramètres
- [Themes/Theme.xaml](Themes/Theme.xaml) — palette et styles (cartes, interrupteurs, contrôles segmentés, menu)
- [UI/TrayIcon.cs](UI/TrayIcon.cs) — icône de la zone de notification et son menu sombre (pause, intensité, écran)

## Interface

- **Accueil** : couleur envoyée à la lampe en direct, pause / reprise, intensité, raccourcis vers les autres pages
- **Lampe** : adresse IP, test de connexion (`devStatus`), détection automatique des lampes du réseau
- **Écran** : choix de l'écran source, fréquence de capture, lissage
- **Paramètres** : démarrage avec Windows, démarrage masqué, fermeture dans la zone de notification
- Menu replié en rail d'icônes (☰, automatique sous 760 px de large) ; état de la synchro toujours visible en bas du menu
- Zone de notification : clic gauche = ouvrir, clic droit = actions rapides sans ouvrir la fenêtre
- Raccourcis : `Ctrl+1` … `Ctrl+4` pour changer de page, `Ctrl+P` pour pause / reprise

## Prérequis côté lampe

Le **mode LAN** doit être activé pour l'appareil dans l'app Govee Home (Paramètres de l'appareil → LAN Control).
