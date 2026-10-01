using System.Diagnostics;

namespace GoveeScreenV2.Core;

/// <summary>
/// Horloge de cadencement à la microseconde (Stopwatch). Environment.TickCount64 avance par
/// pas de ~15,6 ms : un intervalle de 33 ms y est mesuré 31 puis 47 ms, ce qui ramenait
/// « 30 FPS » à ~21 FPS réels et « Auto 60 FPS » à ~32.
/// </summary>
internal static class Pacing
{
    /// <summary>
    /// Marge sur les intervalles : une frame alignée sur le vsync (33,3 ms à 60 Hz) ne doit
    /// pas être refusée parce qu'elle arrive une fraction de milliseconde trop tôt.
    /// </summary>
    private const double Tolerance = 0.9;

    public static long Now => Stopwatch.GetTimestamp();

    public static double ElapsedMs(long since) => Stopwatch.GetElapsedTime(since).TotalMilliseconds;

    /// <summary>Vrai si au moins <paramref name="intervalMs"/> (à la marge près) se sont écoulées.</summary>
    public static bool IsDue(long since, double intervalMs) => ElapsedMs(since) >= intervalMs * Tolerance;

    /// <summary>Intervalle effectivement exigé entre deux traitements, marge comprise.</summary>
    public static TimeSpan MinInterval(double intervalMs) => TimeSpan.FromMilliseconds(intervalMs * Tolerance);
}
