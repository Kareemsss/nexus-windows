using System;
using System.Globalization;

namespace Nexus.Core.Color;

/// <summary>Platform-neutral 32-bit ARGB colour with the small amount of colour maths NEXUS needs.</summary>
public readonly record struct NexusColor(byte A, byte R, byte G, byte B)
{
    public static NexusColor FromRgb(byte r, byte g, byte b) => new(255, r, g, b);

    public static NexusColor FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8) throw new FormatException($"Invalid colour '{hex}'.");
        var v = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public NexusColor WithAlpha(double alpha) => this with { A = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255) };

    public static NexusColor Lerp(NexusColor a, NexusColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static byte L(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        return new(L(a.A, b.A, t), L(a.R, b.R, t), L(a.G, b.G, t), L(a.B, b.B, t));
    }

    public (double H, double S, double V) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min, h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max <= 0 ? 0 : d / max, max);
    }

    public static NexusColor FromHsv(double h, double s, double v, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1); v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte B8(double f) => (byte)Math.Round(Math.Clamp(f, 0, 1) * 255);
        return new(alpha, B8(r + m), B8(g + m), B8(b + m));
    }

    /// <summary>Relative luminance (sRGB, WCAG definition).</summary>
    public double Luminance
    {
        get
        {
            static double Lin(byte c) { var s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
        }
    }

    /// <summary>
    /// Tames an arbitrary colour into something suitable for ambient lighting on dark glass:
    /// saturation is capped and brightness is pulled into a narrow, luminous band.
    /// </summary>
    public NexusColor Ambientize(double maxSaturation = 0.62, double minValue = 0.72, double maxValue = 0.94)
    {
        var (h, s, v) = ToHsv();
        return FromHsv(h, Math.Min(s, maxSaturation), Math.Clamp(v, minValue, maxValue), A);
    }

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>Semantic colours shared by providers and themes. Saturation is deliberately restrained.</summary>
public static class SemanticColors
{
    public static readonly NexusColor Neutral = NexusColor.FromHex("#C9D1E0");
    public static readonly NexusColor Energy = NexusColor.FromHex("#3DDC97");    // charging / healthy battery
    public static readonly NexusColor Amber = NexusColor.FromHex("#F5B547");     // low battery
    public static readonly NexusColor Critical = NexusColor.FromHex("#FF5A5F");  // critical battery / errors
    public static readonly NexusColor Privacy = NexusColor.FromHex("#FF8A3D");   // microphone
    public static readonly NexusColor Camera = NexusColor.FromHex("#3DDC97");    // camera (green, as on Windows)
    public static readonly NexusColor Bluetooth = NexusColor.FromHex("#4C9BFF");
    public static readonly NexusColor Network = NexusColor.FromHex("#4CC9F0");
    public static readonly NexusColor Meeting = NexusColor.FromHex("#7B83EB");   // Teams-adjacent indigo
    public static readonly NexusColor Notification = NexusColor.FromHex("#8AB4FF");
    public static readonly NexusColor Capture = NexusColor.FromHex("#F2F4F8");   // screenshots
    public static readonly NexusColor Transfer = NexusColor.FromHex("#5EC8FF");
}
