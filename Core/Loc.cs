// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2025-2026 Baba0bab — GoveeScreen

using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace GoveeScreenV2.Core;

/// <summary>
/// Traductions de l'interface. Les textes vivent dans Localization/{code}.json (ressources
/// incorporées, clés à plat "Zone.Cle" → texte). L'anglais est la référence : une clé absente
/// d'une autre langue retombe sur l'anglais, une clé absente partout s'affiche telle quelle.
/// Même fonctionnement que VibeSSHMonitoring.
/// </summary>
public static class Loc
{
    public const string SystemLanguage = "system";
    public const string DefaultLanguage = "en";

    /// <summary>Langues proposées, avec leur nom dans leur propre langue (pour le choix de la langue).</summary>
    public static readonly IReadOnlyList<(string Code, string NativeName)> Languages = new[]
    {
        ("en", "English"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("es", "Español")
    };

    private static Dictionary<string, string>? _strings;
    private static Dictionary<string, string>? _fallback;

    // Langue d'affichage et format régional de Windows, lus avant qu'Initialize ne remplace les
    // cultures des threads : « Système » doit rester la langue de Windows, pas celle choisie
    private static readonly CultureInfo WindowsUICulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo WindowsCulture = CultureInfo.CurrentCulture;

    /// <summary>Langue utilisée (code à deux lettres).</summary>
    public static string Language { get; private set; } = DefaultLanguage;

    /// <summary>Culture des nombres et des dates, assortie à <see cref="Language"/>.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(DefaultLanguage);

    /// <summary>Langue d'une valeur du réglage : "system" (ou vide) suit la langue d'affichage de Windows.</summary>
    public static string Resolve(string? setting)
    {
        string code = string.IsNullOrWhiteSpace(setting) || setting == SystemLanguage
            ? WindowsUICulture.TwoLetterISOLanguageName
            : setting;
        return Languages.Any(l => l.Code == code) ? code : DefaultLanguage;
    }

    /// <summary>
    /// Charge la langue et en fait la culture de tous les threads. À appeler une fois au démarrage,
    /// avant toute fenêtre : l'interface lit ses textes à sa construction.
    /// </summary>
    public static void Initialize(string? setting)
    {
        Language = Resolve(setting);
        _fallback = Load(DefaultLanguage);
        _strings = Language == DefaultLanguage ? _fallback : Load(Language);

        // Garde le format régional de l'utilisateur (fr-CA, de-AT…) s'il correspond à la langue choisie
        Culture = WindowsCulture.TwoLetterISOLanguageName == Language ? WindowsCulture : CultureInfo.GetCultureInfo(Language);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }

    /// <summary>Texte traduit de <paramref name="key"/>.</summary>
    public static string T(string key)
    {
        if (_strings is null)
            Initialize(DefaultLanguage);   // tests et outils qui n'appellent pas Initialize
        return _strings!.TryGetValue(key, out var text) || _fallback!.TryGetValue(key, out text) ? text : key;
    }

    /// <summary>Texte traduit avec des paramètres {0}, {1}…, mis en forme avec la culture courante.</summary>
    public static string T(string key, params object?[] args) => string.Format(Culture, T(key), args);

    /// <summary>Textes bruts d'une langue (tests : toutes les langues doivent avoir les mêmes clés).</summary>
    public static Dictionary<string, string> Load(string code)
    {
        string resource = $"GoveeScreenV2.Localization.{code}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing translation resource: {resource}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }
}
