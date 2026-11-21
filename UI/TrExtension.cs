// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2025-2026 Baba0bab — GoveeScreen

using System.Windows.Markup;
using GoveeScreenV2.Core;

namespace GoveeScreenV2.UI;

/// <summary>
/// Texte traduit en XAML : <c>Text="{ui:Tr Home.Subtitle}"</c>. Le texte est lu à la construction
/// de l'élément : un changement de langue s'applique après un redémarrage (voir <see cref="Loc"/>).
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
