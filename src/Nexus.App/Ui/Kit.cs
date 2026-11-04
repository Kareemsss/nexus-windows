using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Nexus.App.Theming;

namespace Nexus.App.Ui;

/// <summary>
/// Tiny fluent factory for the island's visual vocabulary. Keeps typography and spacing consistent:
/// spacing follows a 4-DIP grid; text sizes come from a fixed type ramp.
/// </summary>
public static class Kit
{
    public static class Space
    {
        public const double XS = 4, S = 8, M = 12, L = 16, XL = 20;
    }

    public static class Type
    {
        public const double Caption = 11, Body = 12.5, BodyStrong = 13, Title = 15, Headline = 20, Display = 34;
    }

    public static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null, FontFamily? family = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = family ?? (size >= 20 ? Palette.DisplayFont : size <= 11 ? Palette.SmallFont : Palette.TextFont),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        TextOptions.SetTextFormattingMode(t, TextFormattingMode.Ideal);
        return t;
    }

    /// <summary>Numeric text with tabular figures so timers and percentages never make the island jitter.</summary>
    public static TextBlock Numeric(string text, double size, Brush brush, FontWeight? weight = null)
    {
        var t = Text(text, size, brush, weight);
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    public static TextBlock Glyph(string glyph, double size, Brush brush)
    {
        var t = new TextBlock
        {
            Text = glyph,
            FontFamily = Palette.IconFont,
            FontSize = size,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        TextOptions.SetTextFormattingMode(t, TextFormattingMode.Ideal);
        return t;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static T WithMargin<T>(this T e, double l, double t, double r, double b) where T : FrameworkElement
    {
        e.Margin = new Thickness(l, t, r, b);
        return e;
    }

    public static T WithWidth<T>(this T e, double w) where T : FrameworkElement { e.Width = w; return e; }
    public static T WithMaxWidth<T>(this T e, double w) where T : FrameworkElement { e.MaxWidth = w; return e; }

    public static Grid Columns(params (UIElement Element, GridLength Width)[] cols)
    {
        var g = new Grid();
        for (int i = 0; i < cols.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = cols[i].Width });
            Grid.SetColumn(cols[i].Element, i);
            g.Children.Add(cols[i].Element);
        }
        return g;
    }

    public static GridLength Auto => GridLength.Auto;
    public static GridLength Star => new(1, GridUnitType.Star);
    public static GridLength Px(double v) => new(v);

    public static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
}
