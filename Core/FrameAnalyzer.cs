namespace GoveeScreenV2.Core;

public readonly record struct FrameColor(byte R, byte G, byte B, double BrightnessPercent);

/// <summary>Couleur envoyée à la lampe : moyenne de l'image ou teinte dominante.</summary>
public enum ColorMode
{
    Average,
    Dominant
}

/// <summary>
/// Analyse la miniature BGRA : détection des bandes noires (letterbox) haut/bas,
/// puis couleur (moyenne ou dominante) et luminance (Rec. 709) sur la zone utile.
/// </summary>
public static class FrameAnalyzer
{
    private const int DarkThreshold = 22;      // Canal < 22/255 = pixel sombre
    private const double DarkRowTolerance = 0.97; // Une ligne est "bande noire" si ≥97% de pixels sombres

    // Couleur dominante
    private const int HueBins = 36;               // Teintes regroupées par tranches de 10°
    private const int MinChroma = 32;             // max - min < 32/255 : pixel gris, sans teinte
    private const int MinValue = 40;              // max < 40/255 : trop sombre pour une teinte fiable
    private const double MinColorShare = 0.02;    // Moins de 2 % de pixels colorés : couleur moyenne
    private const double FullColorShare = 0.15;   // À partir de 15 % : couleur dominante seule

    public static FrameColor Analyze(byte[] bgra, int width, int height, ColorMode mode = ColorMode.Average) =>
        Analyze(bgra, width, height, mode, out _);

    /// <param name="average">
    /// Couleur moyenne de l'image, quel que soit le mode : c'est elle qui dit si l'image a changé
    /// (écran figé, écran noir). La teinte dominante, plus stable, resterait identique pendant
    /// un film à la teinte constante.
    /// </param>
    public static FrameColor Analyze(byte[] bgra, int width, int height, ColorMode mode, out FrameColor average)
    {
        // Détection des lignes sombres contiguës en haut et en bas (au plus 1/3 de l'image chacune)
        int maxBar = height / 3;
        int skipTop = CountDarkRows(bgra, width, 0, 1, maxBar);
        int skipBottom = CountDarkRows(bgra, width, height - 1, -1, maxBar);

        int startY = skipTop;
        int endY = height - skipBottom;
        if (endY <= startY)
        {
            // Image entièrement noire
            average = new FrameColor(0, 0, 0, 0);
            return average;
        }

        long sumR = 0, sumG = 0, sumB = 0;
        for (int y = startY; y < endY; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                sumB += bgra[i];
                sumG += bgra[i + 1];
                sumR += bgra[i + 2];
            }
        }

        long count = (long)(endY - startY) * width;
        byte r = (byte)(sumR / count);
        byte g = (byte)(sumG / count);
        byte b = (byte)(sumB / count);

        // La luminosité suit toujours l'image entière : seule la teinte change d'un mode à l'autre
        double brightness = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 * 100.0;
        average = new FrameColor(r, g, b, brightness);
        if (mode != ColorMode.Dominant)
            return average;
        (r, g, b) = Dominant(bgra, width, startY, endY, (r, g, b));
        return new FrameColor(r, g, b, brightness);
    }

    /// <summary>
    /// Teinte vive la plus présente : histogramme des teintes des pixels colorés, pondéré par
    /// leur saturation (chroma), lu par fenêtres de trois tranches voisines pour ne pas couper
    /// une teinte en deux. Peu de pixels colorés (document, image grise) : on glisse
    /// progressivement vers la moyenne, sans saut d'une image à l'autre.
    /// </summary>
    private static (byte R, byte G, byte B) Dominant(byte[] bgra, int width, int startY, int endY, (byte R, byte G, byte B) average)
    {
        Span<double> weight = stackalloc double[HueBins];
        Span<double> sumR = stackalloc double[HueBins];
        Span<double> sumG = stackalloc double[HueBins];
        Span<double> sumB = stackalloc double[HueBins];
        int colorful = 0;

        for (int y = startY; y < endY; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int max = Math.Max(r, Math.Max(g, b));
                int chroma = max - Math.Min(r, Math.Min(g, b));
                if (chroma < MinChroma || max < MinValue)
                    continue;

                // Teinte dans [0, 6) (secteurs de 60°)
                double hue = max == r ? (g - b) / (double)chroma
                           : max == g ? (b - r) / (double)chroma + 2
                           : (r - g) / (double)chroma + 4;
                if (hue < 0)
                    hue += 6;
                int bin = (int)(hue * HueBins / 6) % HueBins;

                weight[bin] += chroma;
                sumR[bin] += r * chroma;
                sumG[bin] += g * chroma;
                sumB[bin] += b * chroma;
                colorful++;
            }
        }
        if (colorful == 0)
            return average;

        int best = 0;
        double bestScore = -1;
        for (int bin = 0; bin < HueBins; bin++)
        {
            double score = weight[(bin + HueBins - 1) % HueBins] + weight[bin] + weight[(bin + 1) % HueBins];
            if (score > bestScore)
                (best, bestScore) = (bin, score);
        }

        double w = 0, dr = 0, dg = 0, db = 0;
        for (int k = -1; k <= 1; k++)
        {
            int bin = (best + k + HueBins) % HueBins;
            w += weight[bin];
            dr += sumR[bin];
            dg += sumG[bin];
            db += sumB[bin];
        }

        double share = colorful / (double)((long)(endY - startY) * width);
        double t = Math.Clamp((share - MinColorShare) / (FullColorShare - MinColorShare), 0, 1);
        return (Mix(average.R, dr / w, t), Mix(average.G, dg / w, t), Mix(average.B, db / w, t));
    }

    private static byte Mix(byte from, double to, double t) =>
        (byte)Math.Clamp(Math.Round(from + (to - from) * t), 0, 255);

    private static int CountDarkRows(byte[] bgra, int width, int startRow, int step, int maxRows)
    {
        int dark = 0;
        for (int n = 0; n < maxRows; n++)
        {
            int y = startRow + n * step;
            int row = y * width * 4;
            int darkPixels = 0;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                if (bgra[i] < DarkThreshold && bgra[i + 1] < DarkThreshold && bgra[i + 2] < DarkThreshold)
                    darkPixels++;
            }
            if (darkPixels / (double)width < DarkRowTolerance)
                break;
            dark++;
        }
        return dark;
    }
}
