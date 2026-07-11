# GoveeScreen V2

Synchronise une lampe **Govee** (API LAN, UDP port 4003) avec la couleur moyenne de l'écran — réécriture complète de la V1 Python, en **C# / .NET 8 + WPF**.

## Ce qui change par rapport à la V1

| | V1 (Python) | V2 (C#) |
|---|---|---|
| Capture | CPU (`mss` + PIL), 3 captures/frame à 30 FPS | **GPU** (Windows.Graphics.Capture) : la frame reste en VRAM, compatible jeux plein écran |
| Downscale | `Image.resize` CPU | **Mipmaps GPU** (`GenerateMips`), seule une miniature ~160×90 revient au CPU |
| Écran statique | Capture en boucle quand même | **0 % CPU** — le compositeur ne délivre une frame que si l'image change |
| Cadence | 30 FPS fixe | FPS configurable (10/15/30/60, défaut 30) — la capture est cadencée même sur écran 144/240 Hz |
| Appareil | IP codée en dur | IP configurable + **découverte automatique** (multicast Govee) |
| Bandes noires | 2 zones fixes de 40 px | Détection ligne par ligne sur la miniature (letterbox variable) |
| Transitions | Brutes | Lissage exponentiel (désactivable) |
| Démarrage Windows | Raccourci dossier Startup | Clé de registre `HKCU\...\Run` |
| UI | Tkinter | WPF thème sombre, barre de titre sombre, tray icon |

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
- [MainWindow.xaml](MainWindow.xaml) — interface

## Prérequis côté lampe

Le **mode LAN** doit être activé pour l'appareil dans l'app Govee Home (Paramètres de l'appareil → LAN Control).
