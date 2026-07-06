namespace GoveeScreenV2.Core;

public readonly record struct FrameColor(byte R, byte G, byte B, double BrightnessPercent);

/// <summary>
/// Analyse la miniature BGRA : détection des bandes noires (letterbox) haut/bas,
/// puis couleur moyenne et luminance (Rec. 709) sur la zone utile.
/// </summary>
public static class FrameAnalyzer
{
    private const int DarkThreshold = 22;      // Canal < 22/255 = pixel sombre
    private const double DarkRowTolerance = 0.97; // Une ligne est "bande noire" si ≥97% de pixels sombres

    public static FrameColor Analyze(byte[] bgra, int width, int height)
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
            return new FrameColor(0, 0, 0, 0);
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

        double brightness = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 * 100.0;
        return new FrameColor(r, g, b, brightness);
    }

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
