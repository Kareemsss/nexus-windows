using System.Windows.Media;
using Nexus.Core.Settings;

namespace Nexus.App.Theming;

public enum EdgeStyle { Soft, Luminous }

/// <summary>
/// A fully resolved theme. Every theme renders through the SAME surface, widgets and controls; themes only
/// differ in these values. Adding a theme = adding one preset below.
/// </summary>
public sealed record IslandTheme
{
    public required string Name { get; init; }
    public required Color BaseTop { get; init; }
    public required Color BaseBottom { get; init; }
    /// <summary>0 = opaque glass, 0.6 = very transparent.</summary>
    public double Transparency { get; init; }
    public double Frost { get; init; }
    public double Glow { get; init; }
    public double CornerRoundness { get; init; } = 1;
    public AccentBehavior Accent { get; init; }
    public Color ThemeAccent { get; init; }
    /// <summary>Optional secondary atmospheric colours (Aurora).</summary>
    public Color? AtmosphereA { get; init; }
    public Color? AtmosphereB { get; init; }
    public EdgeStyle Edge { get; init; }
    public Color EdgeA { get; init; } = Colors.White;
    public Color EdgeB { get; init; } = Colors.White;
    public double ShadowOpacity { get; init; } = 0.5;
    /// <summary>Motion personality: &gt;1 snappier (Cyber), &lt;1 softer.</summary>
    public double MotionSnap { get; init; } = 1.0;

    public Color TextPrimary { get; init; } = Color.FromRgb(0xF5, 0xF6, 0xF8);
    public Color TextSecondary { get; init; } = Color.FromArgb(0xB8, 0xE6, 0xE9, 0xF0);
    public Color TextTertiary { get; init; } = Color.FromArgb(0x80, 0xE6, 0xE9, 0xF0);
    public Color ControlFill { get; init; } = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
    public Color ControlHover { get; init; } = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
    public Color CardFill { get; init; } = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
    public Color CardStroke { get; init; } = Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF);
    public Color Track { get; init; } = Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF);

    /// <summary>Corner radius for a surface of the given height.</summary>
    public double RadiusFor(double height)
    {
        double max = 10 + 24 * Math.Clamp(CornerRoundness, 0, 1);
        return Math.Min(height / 2, max);
    }
}

public static class ThemePresets
{
    public static IslandTheme Get(ThemeKind kind) => kind switch
    {
        ThemeKind.Fluent => Fluent,
        ThemeKind.Aurora => Aurora,
        ThemeKind.Cyber => Cyber,
        ThemeKind.Minimal => Minimal,
        _ => Obsidian,
    };

    /// <summary>Default. Smoked black glass, white type, restrained contextual light.</summary>
    public static readonly IslandTheme Obsidian = new()
    {
        Name = "Obsidian",
        BaseTop = Color.FromRgb(0x1C, 0x1D, 0x22),
        BaseBottom = Color.FromRgb(0x0B, 0x0B, 0x0E),
        Transparency = 0.08,
        Frost = 0.35,
        Glow = 0.55,
        Accent = AccentBehavior.Contextual,
        ThemeAccent = Color.FromRgb(0xA9, 0xBC, 0xFF),
        Edge = EdgeStyle.Soft,
        ShadowOpacity = 0.55,
    };

    /// <summary>Closer to Windows 11: more visible translucency, cooler greys, Windows blue.</summary>
    public static readonly IslandTheme Fluent = new()
    {
        Name = "Fluent",
        BaseTop = Color.FromRgb(0x30, 0x33, 0x3C),
        BaseBottom = Color.FromRgb(0x1F, 0x21, 0x28),
        Transparency = 0.24,
        Frost = 0.75,
        Glow = 0.5,
        CornerRoundness = 0.8,
        Accent = AccentBehavior.Contextual,
        ThemeAccent = Color.FromRgb(0x4C, 0xC2, 0xFF),
        Edge = EdgeStyle.Soft,
        ShadowOpacity = 0.45,
        CardFill = Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF),
    };

    /// <summary>Dark glass with a quiet purple-blue-cyan atmosphere.</summary>
    public static readonly IslandTheme Aurora = new()
    {
        Name = "Aurora",
        BaseTop = Color.FromRgb(0x17, 0x16, 0x26),
        BaseBottom = Color.FromRgb(0x0A, 0x0A, 0x14),
        Transparency = 0.12,
        Frost = 0.4,
        Glow = 0.75,
        Accent = AccentBehavior.Contextual,
        ThemeAccent = Color.FromRgb(0x9B, 0x8C, 0xFF),
        AtmosphereA = Color.FromRgb(0x7C, 0x5C, 0xFF),
        AtmosphereB = Color.FromRgb(0x29, 0xD3, 0xE8),
        Edge = EdgeStyle.Soft,
        ShadowOpacity = 0.55,
    };

    /// <summary>Dark, thin luminous cyan/violet rim, sharper geometry and snappier motion. Not RGB-gaming.</summary>
    public static readonly IslandTheme Cyber = new()
    {
        Name = "Cyber",
        BaseTop = Color.FromRgb(0x0D, 0x10, 0x18),
        BaseBottom = Color.FromRgb(0x05, 0x06, 0x0A),
        Transparency = 0.06,
        Frost = 0.15,
        Glow = 0.7,
        CornerRoundness = 0.45,
        Accent = AccentBehavior.Contextual,
        ThemeAccent = Color.FromRgb(0x22, 0xE5, 0xFF),
        Edge = EdgeStyle.Luminous,
        EdgeA = Color.FromRgb(0x22, 0xE5, 0xFF),
        EdgeB = Color.FromRgb(0xA8, 0x55, 0xF7),
        ShadowOpacity = 0.6,
        MotionSnap = 1.35,
    };

    /// <summary>Monochrome, almost no glow. Extremely clean.</summary>
    public static readonly IslandTheme Minimal = new()
    {
        Name = "Minimal",
        BaseTop = Color.FromRgb(0x16, 0x16, 0x16),
        BaseBottom = Color.FromRgb(0x10, 0x10, 0x10),
        Transparency = 0.04,
        Frost = 0.1,
        Glow = 0.0,
        Accent = AccentBehavior.Monochrome,
        ThemeAccent = Color.FromRgb(0xF2, 0xF2, 0xF2),
        Edge = EdgeStyle.Soft,
        ShadowOpacity = 0.4,
    };
}

/// <summary>Resolves the active theme from settings and notifies when it changes.</summary>
public sealed class ThemeEngine
{
    public ThemeEngine(SettingsStore settings)
    {
        Current = Resolve(settings.Current.Appearance);
        Palette = new Palette(Current);
        settings.Changed += (_, section) =>
        {
            if (section != "appearance") return;
            Current = Resolve(settings.Current.Appearance);
            Palette = new Palette(Current);
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public IslandTheme Current { get; private set; }
    public Palette Palette { get; private set; }
    public event EventHandler? Changed;

    public static IslandTheme Resolve(AppearanceSettings a)
    {
        if (a.Theme != ThemeKind.Custom) return ThemePresets.Get(a.Theme);
        var b = ThemePresets.Get(a.CustomBase == ThemeKind.Custom ? ThemeKind.Obsidian : a.CustomBase);
        var t = a.Custom;
        return b with
        {
            Name = "Custom",
            Transparency = t.Transparency,
            Frost = t.Frost,
            Glow = t.Glow,
            CornerRoundness = t.CornerRoundness,
            Accent = t.Accent,
        };
    }

    /// <summary>Copies a preset's tuning into the Custom slot (used when the user starts customising).</summary>
    public static ThemeTuning TuningOf(IslandTheme t) => new()
    {
        Transparency = t.Transparency,
        Frost = t.Frost,
        Glow = t.Glow,
        CornerRoundness = t.CornerRoundness,
        Accent = t.Accent,
    };
}
