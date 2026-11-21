# Releasing / Publier une version

🇬🇧 [English](#english) · 🇫🇷 [Français](#français)

## English

A release is a git tag `vX.Y.Z`. Pushing it runs [`.github/workflows/release.yml`](../.github/workflows/release.yml), which tests, builds the installer and the update packages with [`scripts/pack.ps1`](../scripts/pack.ps1) (Velopack), and publishes them on GitHub Releases. **Every installed copy downloads it automatically**: a published release cannot really be taken back, check before tagging.

### 1. Choose the version ([SemVer](https://semver.org/))

| Change | Bump | Example |
|---|---|---|
| Bug fixes only | patch | 2.1.0 → 2.1.1 |
| New features, compatible | minor | 2.1.1 → 2.2.0 |
| Breaking change (config format, dropped feature) | major | 2.2.0 → 3.0.0 |
| Test build for volunteers | pre-release suffix | 2.2.0-beta.1 |

The version comes **only from the tag**: never edit `<VersionPrefix>` in the `.csproj`. A suffixed tag (`-beta.1`, `-rc.1`) is published as a pre-release.

### 2. Prepare

```powershell
git switch main; git pull
gh run list --workflow ci.yml --branch main --limit 1     # last CI run must be green
dotnet build GoveeScreen.slnx -c Release                   # 0 warnings
dotnet test Tests                                          # all tests pass
```

### 3. Update CHANGELOG.md

Move the *Unreleased* entries into a new section. **Its title must be exactly `## [X.Y.Z] - YYYY-MM-DD`**: `scripts/pack.ps1` extracts it as the release notes (empty notes and a warning otherwise). Group entries under `### Added`, `### Changed`, `### Fixed`, `### Removed`, `### Security`, user-oriented, one line each, **in English**. Update the links at the bottom:

```markdown
## [Unreleased]

## [2.1.0] - 2026-12-01
### Added
- …

[Unreleased]: https://github.com/Baba0bab/GoveeScreen/compare/v2.1.0...HEAD
[2.1.0]: https://github.com/Baba0bab/GoveeScreen/compare/v2.0.0...v2.1.0
```

For the first release, the link is `[2.0.0]: https://github.com/Baba0bab/GoveeScreen/releases/tag/v2.0.0`.

### 4. Optional local dry run

```powershell
./scripts/pack.ps1 -Version 2.1.0 -OutputDir $env:TEMP\gs-pack   # builds Setup.exe and packages, publishes nothing
Get-Content artifacts\release-notes.md                            # the notes that will be shipped
```

### 5. Commit, tag, push

```powershell
git add CHANGELOG.md
git commit -m "Release v2.1.0"
git push origin main
git tag -a v2.1.0 -m "GoveeScreen 2.1.0"
git push origin v2.1.0                                            # ← starts the release
```

### 6. Follow and verify

```powershell
gh run watch (gh run list --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
gh release view v2.1.0
```

Expected assets: `GoveeScreen-win-Setup.exe`, `GoveeScreen-win-Portable.zip`, `GoveeScreen-2.1.0-full.nupkg`, `GoveeScreen-2.1.0-delta.nupkg` (missing only for the very first release), `releases.win.json`, `RELEASES`, `SHA256SUMS.txt`. The release must be published (not a draft) and marked pre-release only for suffixed versions.

### If something goes wrong

- **Workflow failed before *Publish GitHub release***: nothing is public yet. Fix the problem on `main`, then move the tag: `git tag -d v2.1.0; git push origin :refs/tags/v2.1.0`, re-tag and push again.
- **Release published but broken**: do **not** delete it or edit its packages, installed copies may already be updating. Publish a fixed **new** version (2.1.1) right away.
- A draft release left behind by a failed run can be deleted: `gh release delete v2.1.0 --yes` (without `--cleanup-tag` if the tag is still wanted).

### Never change

These identifiers are baked into every installed copy; changing them breaks updates or user data:

- package id `GoveeScreen` (`scripts/pack.ps1`) and the channel `win`;
- `AppUpdater.RepositoryUrl` (`https://github.com/Baba0bab/GoveeScreen`), where installed copies look for updates;
- the config folder `%APPDATA%\GoveeScreen` and the `HKCU\…\Run` value name `GoveeScreen`;
- the asset names produced by Velopack (do not rename files on the release page).

---

## Français

Une version est un tag git `vX.Y.Z`. Le pousser lance [`.github/workflows/release.yml`](../.github/workflows/release.yml) : tests, construction de l'installateur et des paquets de mise à jour avec [`scripts/pack.ps1`](../scripts/pack.ps1) (Velopack), publication sur GitHub Releases. **Toutes les copies installées la téléchargent d'elles-mêmes** : une version publiée ne se retire pas vraiment, vérifiez avant de taguer.

### 1. Choisir le numéro ([SemVer](https://semver.org/lang/fr/))

| Changement | Incrément | Exemple |
|---|---|---|
| Corrections uniquement | correctif | 2.1.0 → 2.1.1 |
| Nouveautés compatibles | mineure | 2.1.1 → 2.2.0 |
| Rupture (format de config, fonction retirée) | majeure | 2.2.0 → 3.0.0 |
| Version de test pour volontaires | suffixe de préversion | 2.2.0-beta.1 |

La version vient **uniquement du tag** : ne modifiez jamais `<VersionPrefix>` dans le `.csproj`. Un tag avec suffixe (`-beta.1`, `-rc.1`) est publié en préversion.

### 2. Préparer

```powershell
git switch main; git pull
gh run list --workflow ci.yml --branch main --limit 1     # la dernière CI doit être verte
dotnet build GoveeScreen.slnx -c Release                   # 0 avertissement
dotnet test Tests                                          # tous les tests passent
```

### 3. Mettre à jour CHANGELOG.md

Déplacez les entrées de *Unreleased* dans une nouvelle section. **Son titre doit être exactement `## [X.Y.Z] - AAAA-MM-JJ`** : `scripts/pack.ps1` l'extrait comme notes de version (sinon notes vides et avertissement). Classez sous `### Added`, `### Changed`, `### Fixed`, `### Removed`, `### Security`, du point de vue de l'utilisateur, une ligne par entrée, **en anglais**. Mettez à jour les liens en bas (voir l'exemple anglais ci-dessus).

### 4. Essai local (facultatif)

```powershell
./scripts/pack.ps1 -Version 2.1.0 -OutputDir $env:TEMP\gs-pack   # construit Setup.exe et les paquets, ne publie rien
Get-Content artifacts\release-notes.md                            # les notes qui seront livrées
```

### 5. Commit, tag, push

```powershell
git add CHANGELOG.md
git commit -m "Release v2.1.0"
git push origin main
git tag -a v2.1.0 -m "GoveeScreen 2.1.0"
git push origin v2.1.0                                            # ← lance la publication
```

### 6. Suivre et vérifier

```powershell
gh run watch (gh run list --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId')
gh release view v2.1.0
```

Fichiers attendus : `GoveeScreen-win-Setup.exe`, `GoveeScreen-win-Portable.zip`, `GoveeScreen-2.1.0-full.nupkg`, `GoveeScreen-2.1.0-delta.nupkg` (absent seulement pour la toute première version), `releases.win.json`, `RELEASES`, `SHA256SUMS.txt`. La version doit être publiée (pas en brouillon), et marquée préversion uniquement si elle a un suffixe.

### En cas de problème

- **Workflow en échec avant *Publish GitHub release*** : rien n'est public. Corrigez sur `main`, puis déplacez le tag : `git tag -d v2.1.0; git push origin :refs/tags/v2.1.0`, retaguez et repoussez.
- **Version publiée mais défectueuse** : ne la supprimez **pas** et ne modifiez pas ses paquets, des copies sont peut-être déjà en train de se mettre à jour. Publiez tout de suite une **nouvelle** version corrigée (2.1.1).
- Un brouillon laissé par une exécution ratée peut être supprimé : `gh release delete v2.1.0 --yes` (sans `--cleanup-tag` si le tag doit rester).

### À ne jamais changer

Ces identifiants sont inscrits dans chaque copie installée ; les changer casse les mises à jour ou les données :

- l'identifiant de paquet `GoveeScreen` (`scripts/pack.ps1`) et le canal `win` ;
- `AppUpdater.RepositoryUrl` (`https://github.com/Baba0bab/GoveeScreen`), où les copies installées cherchent les mises à jour ;
- le dossier de configuration `%APPDATA%\GoveeScreen` et le nom de la valeur `HKCU\…\Run` `GoveeScreen` ;
- les noms des fichiers produits par Velopack (ne renommez rien sur la page de la release).
