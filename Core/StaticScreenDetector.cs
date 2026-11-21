using System.Runtime.InteropServices;

namespace GoveeScreenV2.Core;

public enum StaticScreenTransition { None, WentIdle, WokeUp }

/// <summary>
/// Détecte un écran figé : couleur moyenne quasi constante et aucune saisie (clavier, souris)
/// pendant le délai choisi. Une vidéo en pause, un bureau abandonné ou un écran mis en veille
/// sans verrouillage éteignent la lampe ; un film qui se regarde sans toucher au clavier
/// change de couleur, et un document qu'on lit en faisant défiler reçoit des saisies.
/// La comparaison se fait avec la couleur du début de la période statique (pas avec la frame
/// précédente) : une dérive lente finit par compter comme un changement.
/// Pure (horloge fournie par l'appelant) pour être testable.
/// </summary>
public sealed class StaticScreenDetector
{
    /// <summary>
    /// Écart par canal (sur 255) au-delà duquel l'image a changé. La moyenne d'un écran
    /// entier ne bouge pas pour un curseur qui clignote ou une horloge, mais bouge dès
    /// qu'une scène de vidéo ou une fenêtre change.
    /// </summary>
    public const int ColorTolerance = 3;

    private FrameColor? _reference;
    private double _staticSinceMs;

    public bool IsIdle { get; private set; }

    /// <summary>Repart de zéro (reprise après pause, nouvel écran) : la période statique commence maintenant.</summary>
    public void Reset(double nowMs)
    {
        _reference = null;
        _staticSinceMs = nowMs;
        IsIdle = false;
    }

    /// <param name="nowMs">Horloge en millisecondes.</param>
    /// <param name="frame">Couleur de la frame analysée, null si aucune (écran statique, frame sautée).</param>
    /// <param name="userInput">Saisie clavier ou souris depuis le dernier appel.</param>
    /// <param name="timeoutMs">Délai avant extinction, 0 ou moins = option désactivée.</param>
    public StaticScreenTransition Update(double nowMs, FrameColor? frame, bool userInput, double timeoutMs)
    {
        bool changed = userInput;
        if (frame is { } color)
        {
            if (_reference is not { } reference)
                _reference = color;
            else if (Differs(color, reference))
            {
                _reference = color;
                changed = true;
            }
        }

        if (changed || timeoutMs <= 0)
        {
            if (changed)
                _staticSinceMs = nowMs;
            if (!IsIdle)
                return StaticScreenTransition.None;
            IsIdle = false;
            return StaticScreenTransition.WokeUp;
        }

        if (!IsIdle && nowMs - _staticSinceMs >= timeoutMs)
        {
            IsIdle = true;
            return StaticScreenTransition.WentIdle;
        }
        return StaticScreenTransition.None;
    }

    private static bool Differs(FrameColor a, FrameColor b) =>
        Math.Abs(a.R - b.R) > ColorTolerance ||
        Math.Abs(a.G - b.G) > ColorTolerance ||
        Math.Abs(a.B - b.B) > ColorTolerance;
}

/// <summary>Dernière saisie clavier ou souris de la session (GetLastInputInfo).</summary>
internal static class UserInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Horodatage (GetTickCount) de la dernière saisie : il change à chaque saisie.</summary>
    public static uint LastInputTick
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? info.dwTime : 0;
        }
    }
}
