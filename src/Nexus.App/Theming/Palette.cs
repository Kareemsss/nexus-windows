using System.Windows.Media;

namespace Nexus.App.Theming;

/// <summary>Frozen brushes and typography derived from the theme, shared by every widget.</summary>
public sealed class Palette
{
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily SmallFont = new("Segoe UI Variable Small, Segoe UI");
    /// <summary>System vector icon font: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10.</summary>
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public Palette(IslandTheme theme)
    {
        Theme = theme;
        TextPrimary = Freeze(theme.TextPrimary);
        TextSecondary = Freeze(theme.TextSecondary);
        TextTertiary = Freeze(theme.TextTertiary);
        ControlFill = Freeze(theme.ControlFill);
        ControlHover = Freeze(theme.ControlHover);
        CardFill = Freeze(theme.CardFill);
        CardStroke = Freeze(theme.CardStroke);
        Track = Freeze(theme.Track);
        Accent = Freeze(theme.ThemeAccent);
    }

    public IslandTheme Theme { get; }
    public SolidColorBrush TextPrimary { get; }
    public SolidColorBrush TextSecondary { get; }
    public SolidColorBrush TextTertiary { get; }
    public SolidColorBrush ControlFill { get; }
    public SolidColorBrush ControlHover { get; }
    public SolidColorBrush CardFill { get; }
    public SolidColorBrush CardStroke { get; }
    public SolidColorBrush Track { get; }
    public SolidColorBrush Accent { get; }

    public static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Color ToColor(Core.Color.NexusColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Accent that stays legible as text/icon colour on dark glass.</summary>
    public static Color Legible(Color c)
    {
        var n = new Core.Color.NexusColor(c.A, c.R, c.G, c.B).Ambientize(0.7, 0.78, 1.0);
        return Color.FromArgb(255, n.R, n.G, n.B);
    }
}
