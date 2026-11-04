using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.App.Theming;
using Nexus.Core.Activities;

namespace Nexus.App.Ui;

/// <summary>Small LRU cache of decoded, frozen artwork so track changes and re-renders never re-decode.</summary>
public static class ArtworkCache
{
    private const int Capacity = 12;
    private static readonly LinkedList<(string Key, ImageSource Image)> Items = new();

    public static ImageSource? Get(string key, byte[]? bytes, int decodeWidth = 192)
    {
        if (bytes is null || bytes.Length == 0) return null;
        var cacheKey = key + "@" + decodeWidth + ":" + bytes.Length;
        for (var n = Items.First; n is not null; n = n.Next)
        {
            if (n.Value.Key != cacheKey) continue;
            Items.Remove(n);
            Items.AddFirst(n);
            return n.Value.Image;
        }
        try
        {
            var img = new BitmapImage();
            using (var ms = new MemoryStream(bytes, writable: false))
            {
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = decodeWidth;
                img.StreamSource = ms;
                img.EndInit();
            }
            img.Freeze();
            Items.AddFirst((cacheKey, img));
            while (Items.Count > Capacity) Items.RemoveLast();
            return img;
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Debug("Artwork", "Decode failed: " + ex.Message);
            return null;
        }
    }
}

/// <summary>Rounded artwork tile with a tasteful fallback (gradient + glyph) when no art is available.</summary>
public sealed class ArtworkView : Border
{
    private readonly Palette _palette;
    private readonly double _size;
    private string? _key;

    public ArtworkView(double size, double radius, Palette palette)
    {
        _palette = palette;
        _size = size;
        Width = Height = size;
        CornerRadius = new CornerRadius(radius);
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public void Set(string key, byte[]? bytes, Color accent)
    {
        // Apps often publish metadata before artwork: the identity includes whether bytes are present.
        var identity = key + (bytes is null ? ":none" : ":" + bytes.Length);
        if (identity == _key) return;
        _key = identity;
        var image = ArtworkCache.Get(key, bytes, (int)(_size * 2.5));
        if (image is not null)
        {
            Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            Child = null;
            return;
        }
        var g = new LinearGradientBrush(
            Color.FromArgb(0xFF, (byte)(accent.R * 0.55), (byte)(accent.G * 0.55), (byte)(accent.B * 0.55)),
            Color.FromArgb(0xFF, (byte)(accent.R * 0.22), (byte)(accent.G * 0.22), (byte)(accent.B * 0.22)),
            new Point(0, 0), new Point(1, 1));
        g.Freeze();
        Background = g;
        Child = Kit.Glyph(Glyphs.Music, _size * 0.42, _palette.TextSecondary);
    }
}

/// <summary>App icon from raw bytes (notifications) with glyph fallback.</summary>
public static class IconImage
{
    public static FrameworkElement Create(string key, byte[]? bytes, string glyph, double size, Brush glyphBrush)
    {
        var src = ArtworkCache.Get(key, bytes, (int)(size * 2.5));
        if (src is null) return Kit.Glyph(glyph, size * 0.8, glyphBrush);
        return new Image { Source = src, Width = size, Height = size, Stretch = Stretch.Uniform };
    }
}
