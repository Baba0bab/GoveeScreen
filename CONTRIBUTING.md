# Contributing / Contribuer

🇬🇧 [English](#english) · 🇫🇷 [Français](#français)

## English

Thanks for your interest! Bug reports, feature ideas and pull requests are welcome.

### Reporting a bug

Open an [issue](https://github.com/Baba0bab/GoveeScreen/issues/new/choose) with the app version (*Settings → About*), your Windows version, the lamp model, your display setup (resolution, refresh rate, HDR, number of monitors), what was on screen (desktop, video, game in full screen…) and the steps to reproduce. Issues can be written in English or French. Security issues: do **not** open a public issue, see [SECURITY.md](SECURITY.md).

### Development setup

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. `dotnet run` builds and starts the app with your real settings; `dotnet test Tests` runs the tests.
3. Only one copy runs per data folder. To run a development copy next to an installed one, start the built exe (`bin\Debug\…\GoveeScreen.exe`) with `GOVEESCREEN_HOME` set to another folder: it gets its own settings.
4. Changes to the capture or the sync loop: check CPU / GPU usage on a still screen, in a video and in a full-screen game, and on a high refresh rate display if you can.

### Pull requests

- One topic per pull request, branched from `main`; describe what changed and how you tested it.
- Follow the existing style (see [`.editorconfig`](.editorconfig)): file-scoped namespaces, `_camelCase` private fields.
- Never hard-code interface text: add a key to [`Localization/en.json`](Localization/en.json) (the reference) **and** to every other language file, then use `Loc.T("Area.Key")` in C# or `{ui:Tr Area.Key}` in XAML. A test checks that all languages have the same keys and placeholders. If you can't translate a text, copy the English one: native speakers can fix it later.
- Keep `Core/` free of WPF dependencies; add or update tests in `Tests/` for code that can run without a GPU or a lamp.
- `dotnet build` must produce **no warnings**, and `dotnet test Tests` must pass (the CI checks both).
- Add a line to the *Unreleased* section of [CHANGELOG.md](CHANGELOG.md), in English.

By contributing, you agree that your contribution is licensed under the GPL-3.0-or-later, like the project.

## Français

Merci de votre intérêt ! Signalements de bugs, idées et pull requests sont les bienvenus.

### Signaler un bug

Ouvrez une [issue](https://github.com/Baba0bab/GoveeScreen/issues/new/choose) en indiquant la version de l'application (*Paramètres → À propos*), votre version de Windows, le modèle de lampe, votre configuration d'écran (résolution, fréquence, HDR, nombre d'écrans), ce qui était affiché (bureau, vidéo, jeu en plein écran…) et les étapes pour reproduire. Les issues peuvent être rédigées en anglais ou en français. Faille de sécurité : **n'ouvrez pas** d'issue publique, voir [SECURITY.md](SECURITY.md).

### Environnement de développement

1. Installez le [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0).
2. `dotnet run` compile et lance l'application avec vos vrais réglages ; `dotnet test Tests` lance les tests.
3. Une seule copie tourne par dossier de données. Pour lancer une copie de développement à côté de la version installée, démarrez l'exe compilé (`bin\Debug\…\GoveeScreen.exe`) avec `GOVEESCREEN_HOME` pointant vers un autre dossier : elle a ses propres réglages.
4. Modification de la capture ou de la boucle de synchro : vérifiez la charge CPU / GPU sur un écran fixe, dans une vidéo et dans un jeu plein écran, et si possible sur un écran à haute fréquence.

### Pull requests

- Un sujet par pull request, à partir de `main` ; décrivez le changement et comment vous l'avez testé.
- Suivez le style existant (voir [`.editorconfig`](.editorconfig)) : namespaces de fichier, champs privés `_camelCase`.
- N'écrivez jamais de texte d'interface en dur : ajoutez une clé dans [`Localization/en.json`](Localization/en.json) (la référence) **et** dans tous les autres fichiers de langue, puis utilisez `Loc.T("Zone.Cle")` en C# ou `{ui:Tr Zone.Cle}` en XAML. Un test vérifie que toutes les langues ont les mêmes clés et les mêmes paramètres. Si vous ne savez pas traduire un texte, recopiez l'anglais : des locuteurs natifs pourront le corriger.
- Gardez `Core/` sans dépendance à WPF ; ajoutez ou mettez à jour les tests dans `Tests/` pour le code qui peut tourner sans GPU ni lampe.
- `dotnet build` ne doit produire **aucun avertissement** et `dotnet test Tests` doit réussir (la CI vérifie les deux).
- Ajoutez une ligne dans la section *Unreleased* de [CHANGELOG.md](CHANGELOG.md), en anglais.

En contribuant, vous acceptez que votre contribution soit distribuée sous GPL-3.0-or-later, comme le projet.
