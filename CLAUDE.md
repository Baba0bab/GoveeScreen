# GoveeScreen: notes for Claude (and contributors)

Windows application (C# / .NET 10 / WPF) that syncs a Govee lamp (LAN API, UDP) with the average color of the
screen. Display name and identifier **GoveeScreen** (exe, package id), namespace `GoveeScreenV2`, project file
`GoveeScreen.csproj`, repository https://github.com/Baba0bab/GoveeScreen, license GPL-3.0-or-later. Sister project:
VibeSSHMonitoring (`C:\serveurs\sshvibemonitoring`), same workflows, packaging and theme.

Languages: the **interface is translated** (English, French, German, Spanish, see *Localization* below) and code comments are in French. GitHub texts,
commit messages and CHANGELOG / release notes are **in English**. The public documentation is **bilingual**
(README.md English + README.fr.md, CONTRIBUTING, SECURITY and docs/RELEASING.md with an English then a French section).

## Commands

```powershell
dotnet build GoveeScreen.slnx -c Release             # must give 0 warnings (CI uses -warnaserror)
dotnet test Tests                                    # frame analysis, pacing, HLSL shader compilation
dotnet run                                           # real configuration (%APPDATA%\GoveeScreen)
./scripts/pack.ps1 -Version 2.0.0 -OutputDir $env:TEMP\gs-pack  # installer + Velopack packages, publishes nothing
```

The user usually has their own copy running (single instance per data folder): `dotnet run` then exits silently.
Don't kill it. Until v2 is installed with the installer, the user's copy may run **straight from
`bin\Release\net10.0-windows10.0.26100.0\`** (their HKCU Run value points there): building Release overwrites the
exe it runs from, and on 2026-11-21 their copy stopped during such a session. Check
`reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v GoveeScreen` before building Release, and warn them.
For a separate copy, start the built exe with `GOVEESCREEN_HOME` set to a scratch folder (own
config, own single-instance mutex). Set the variable only in the process that starts the exe; never give the user
`$env:` commands to paste, they stay set in their terminal.

## Layout

- `Program.cs`: Velopack hooks (`VelopackApp.Build().Run()`, removes the Run key on uninstall, applies a downloaded
  update at startup when automatic installation is on), then WPF.
- `Core/AppUpdater.cs`: updates from GitHub Releases (Velopack `GithubSource`), checked 20 s after startup then every
  12 h by `AppController`; installed on exit, or on *Redémarrer et mettre à jour* (settings, sidebar, tray menu).
- `Core/`: no WPF. `ScreenCapturer.cs` (Windows.Graphics.Capture + D3D11, HLSL downscale shader), `FrameAnalyzer.cs`,
  `SyncEngine.cs`, `GoveeClient.cs` (UDP 4003 commands, 4001 / 4002 discovery), `MonitorService.cs`, `SessionWatcher.cs`,
  `StartupManager.cs` (HKCU Run), `AppConfig.cs`, `Loc.cs` (translations).
- `AppController.cs` (lifecycle, tray, engine, `Restart()`), `MainWindow.xaml` (pages), `UI/TrayIcon.cs` (Win32 tray and menu).
- `Localization/*.json`: interface texts, flat `"Area.Key": "text"` with `{0}` placeholders, embedded in the exe.
- `Themes/Theme.xaml`: shared with VibeSSHMonitoring.
- `scripts/pack.ps1`: packaging. `tools/ui-test/`: UI automation and window capture.

## Localization

Same system as VibeSSHMonitoring.

- Never hard-code interface text: `Loc.T("Area.Key")` / `Loc.T("Area.Key", args)` in C#, `{ui:Tr Area.Key}` in XAML
  (`UI/TrayIcon.cs` too). `Program.cs` calls `Loc.Initialize` before WPF starts.
- Add every new key to **all** files of `Localization/` (`en.json` is the reference and the fallback). `LocalizationTests`
  fails if a language misses a key or a placeholder, or has a different number of `|` separators (`|…|` = highlighted
  part, see `Device.LanHint`).
- Texts are read when an element is built: a language change needs a restart (`AppController.Restart()`, which starts
  the exe with `--wait-for <pid>`).
- A new language: copy `en.json`, translate it, add it to `Loc.Languages` and a flag to `UI/Flags.cs`.

## Invariants that must never change

- config folder `%APPDATA%\GoveeScreen`, the setting names in `config.json` and the `Language` codes (`"system"`, `"fr"`…);
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value name `GoveeScreen`;
- `%LOCALAPPDATA%\GoveeScreen` belongs to the installer (Velopack): write nothing there;
- package id `GoveeScreen`, channel `win`, repository URL, release file names;
- the single-instance mutex name `GoveeScreenV2_SingleInstance` (older copies use it too).

## Checking a change

- **Interface**: start a copy with `GOVEESCREEN_HOME`, then `tools/ui-test/Invoke-UiElement.ps1 -ProcessId <pid>`
  (click by name, `-AutomationId x -Value y`, `-List`) and `tools/ui-test/Capture-Window.ps1 -ProcessId <pid> -Out shot.png`.
  Always pass `-ProcessId`: without it the scripts pick the user's copy. Desktop screenshots (computer use) hide the
  user's other windows: use these scripts. README screenshots live in `docs/screenshots/`.
- A dev copy with a lamp IP really sends UDP packets to that address: use an empty IP or an address with no device.
- **Updates** (tested end to end on 2026-11-21, without installing anything): `pack.ps1` two versions into the same
  scratch folder (delta), extract the first one's `GoveeScreen-win-Portable.zip`, start its root `GoveeScreen.exe`
  (a launcher: the app runs from `current\`) with `GOVEESCREEN_HOME` and `GOVEESCREEN_UPDATE_SOURCE=<packages folder>`
  set; the automatic check runs after 20 s, then *Redémarrer et mettre à jour*. The portable copy updates in place.
  Against GitHub, the legacy `1.0.0` Python release (no Velopack assets) is skipped: the check reports up to date.
- Element names depend on the interface language: set `"Language": "en"` in the test `config.json` for stable names.
- UI Automation does not expose `SettingCard` descriptions: read them from a capture. Scroll a page with the
  `ScrollPattern` of `PageScroller` (`SetScrollPercent`).
- **Lamp, GPU, games**: anything about the real lamp, capture performance or full-screen games must be validated by
  the user on their machine.

## Publishing a release

Full procedure: [docs/RELEASING.md](docs/RELEASING.md). In short: a `## [X.Y.Z] - YYYY-MM-DD` section in
CHANGELOG.md (exact heading, source of the release notes, in English), commit on `main`, annotated tag `vX.Y.Z`,
push the tag → `.github/workflows/release.yml` builds and publishes.

**Always ask the user for explicit confirmation before** `git push`, creating or pushing a tag,
`gh release create/edit/delete` or `gh repo edit`: the repository is public, and a
release is downloaded by every installed copy. Never delete a published release: publish a fix.

## Known pitfalls

- `.ps1` scripts with non-ASCII characters: save as UTF-8 **with BOM** (Windows PowerShell 5.1), see `.editorconfig`.
- `MainWindow.xaml.cs` and `UI/TrayIcon.cs` contain raw private-use glyph characters (Segoe icons): edit them with
  targeted replacements, never by rewriting the whole file from a copy.
- Bash heredocs containing apostrophes sometimes fail: write the script to a file.
