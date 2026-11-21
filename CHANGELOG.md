# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

First public release of GoveeScreen 2, a complete rewrite in C# / .NET 10 + WPF of the original Python app (kept on the `v1-python` branch).

### Added
- GPU screen capture (Windows.Graphics.Capture + Direct3D 11) with a pixel shader that reduces each frame to a ~160×90 thumbnail before it reaches the CPU; compatible with full-screen games and videos.
- Almost no CPU on a still screen: a frame is only processed when the image changes, and capture is released while paused.
- Frame rate *Auto* (up to 30 FPS) or 10 / 15 / 30 / 60 FPS, accurately paced on high refresh rate displays.
- Automatic switch to window capture when an exclusive full-screen game leaves the display black, and back.
- Letterbox detection, taskbar excluded outside full screen, optional color smoothing.
- Govee lamp discovery on the local network and connection test.
- Choice of the captured monitor, shown with its model name.
- Intensity multiplier: 50 %, 100 %, 200 % or Max.
- Optional pause when the Windows session is locked, with the lamp turned back on as soon as the lock screen lights up.
- Notification area icon with quick actions (pause, intensity, display); the window is destroyed while hidden to save memory.
- Start with Windows, optionally hidden.
- Windows installer (per user, no administrator rights, Start menu and desktop shortcuts, clean uninstall) and portable version.
- Automatic updates from GitHub Releases: checked at startup and every 12 hours, downloaded in the background (delta packages), installed when the app closes or right away with *Redémarrer et mettre à jour* (settings, side menu or tray menu). Automatic checks, automatic installation and pre-releases can be turned on or off in *Paramètres → Mises à jour*.
- Settings of the Python version (`~/govee_config.json`) imported on the first launch.

[Unreleased]: https://github.com/Baba0bab/GoveeScreen/commits/main
