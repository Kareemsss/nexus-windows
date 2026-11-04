using System;

namespace Nexus.Core.Color;

/// <summary>
/// Extracts a perceptually dominant "vibrant" colour from artwork. Works on a small BGRA32 buffer
/// (callers should downscale to ~32x32 first; this is O(pixels) and allocation-free).
/// </summary>
public static class ColorExtractor
{
    private const int HueBins = 24;

    /// <returns>The dominant chromatic colour, or null when the image is essentially greyscale.</returns>
    public static NexusColor? ExtractDominant(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || bgra.Length < stride * height) return null;

        Span<double> weight = stackalloc double[HueBins];
        Span<double> sumR = stackalloc double[HueBins];
        Span<double> sumG = stackalloc double[HueBins];
        Span<double> sumB = stackalloc double[HueBins];
        double chromaticWeight = 0, totalWeight = 0;

        for (int y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2], a = row[x * 4 + 3];
                if (a < 128) continue;
                var (h, s, v) = new NexusColor(255, r, g, b).ToHsv();
                totalWeight += 1;
                if (s < 0.18 || v < 0.16) continue; // greys and near-blacks carry no hue information

                // Favour saturated, mid-to-bright pixels; heavily discount washed-out highlights.
                double w = s * s * (0.35 + v) * (v > 0.96 && s < 0.3 ? 0.2 : 1);
                int bin = (int)(h / 360.0 * HueBins) % HueBins;
                weight[bin] += w; sumR[bin] += r * w; sumG[bin] += g * w; sumB[bin] += b * w;
                chromaticWeight += w;
            }
        }

        if (totalWeight == 0 || chromaticWeight / totalWeight < 0.012) return null;

        // Smooth with neighbours so a hue split across two bins is not under-counted.
        int best = 0; double bestScore = -1;
        for (int i = 0; i < HueBins; i++)
        {
            double score = weight[i] + 0.5 * (weight[(i + HueBins - 1) % HueBins] + weight[(i + 1) % HueBins]);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        if (weight[best] <= 0) return null;

        return NexusColor.FromRgb(
            (byte)Math.Clamp(sumR[best] / weight[best], 0, 255),
            (byte)Math.Clamp(sumG[best] / weight[best], 0, 255),
            (byte)Math.Clamp(sumB[best] / weight[best], 0, 255));
    }
}
