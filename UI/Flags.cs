// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2025-2026 Baba0bab — GoveeScreen

using System.Windows;
using System.Windows.Media;

namespace GoveeScreenV2.UI;

/// <summary>Entrée du choix de la langue (son nom sert aussi de nom UI Automation).</summary>
public record LanguageChoice(string Code, string Name, ImageSource? Flag)
{
    public override string ToString() => Name;
}

/// <summary>
/// Drapeaux des langues de l'interface, dessinés en vectoriel (Windows affiche les émojis drapeaux
/// en deux lettres). Tous dans un cadre 4:3 (640 × 480), arrondis et cernés d'un léger contour clair
/// pour que les bords sombres restent visibles sur le thème sombre. Les armoiries de l'Espagne sont
/// omises : à cette taille, ce serait une tache. Mêmes drapeaux que VibeSSHMonitoring.
/// </summary>
public static class Flags
{
    private const double Width = 640, Height = 480;

    private static readonly Dictionary<string, ImageSource> Cache = new();

    /// <summary>Drapeau d'un code de langue de <see cref="Core.Loc.Languages"/>, ou null s'il n'y en a pas.</summary>
    public static ImageSource? For(string code)
    {
        if (Cache.TryGetValue(code, out var cached))
            return cached;
        var parts = Parts(code);
        if (parts == null)
            return null;

        var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, Width, Height), 48, 48) };
        foreach (var (color, path) in parts)
            group.Children.Add(new GeometryDrawing(Brush(color), null, Geometry.Parse(path)));
        group.Children.Add(new GeometryDrawing(null, new Pen(Brush("#38FFFFFF"), 24),
            new RectangleGeometry(new Rect(0, 0, Width, Height), 48, 48)));

        var image = new DrawingImage(group);
        image.Freeze();
        return Cache[code] = image;
    }

    private static (string Color, string Path)[]? Parts(string code) => code switch
    {
        // Royaume-Uni (Union Jack, sautoir rouge décalé)
        "en" => new[]
        {
            ("#012169", "M0 0h640v480H0z"),
            ("#FFFFFF", "M75 0l244 181L562 0h78v62L400 241l240 178v61h-80L320 301L81 480H0v-60l239-178L0 64V0z"),
            ("#C8102E", "M424 281l216 159v40L369 281z M240 301l6 35L54 480H0z M640 0v3L391 191l2-44L590 0z M0 0l239 176h-60L0 42z"),
            ("#FFFFFF", "M241 0v480h160V0z M0 160v160h640V160z"),
            ("#C8102E", "M0 193v96h640v-96z M273 0v480h96V0z")
        },
        "fr" => new[]
        {
            ("#FFFFFF", "M0 0h640v480H0z"),
            ("#000091", "M0 0h213.3v480H0z"),
            ("#E1000F", "M426.7 0H640v480H426.7z")
        },
        "de" => new[]
        {
            ("#FFCE00", "M0 0h640v480H0z"),
            ("#000000", "M0 0h640v160H0z"),
            ("#DD0000", "M0 160h640v160H0z")
        },
        "es" => new[]
        {
            ("#AA151B", "M0 0h640v480H0z"),
            ("#F1BF00", "M0 120h640v240H0z")
        },
        _ => null
    };

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
