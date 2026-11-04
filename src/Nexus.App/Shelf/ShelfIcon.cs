using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Shelf;
using Nexus.Platform.Services;

namespace Nexus.App.Shelf;

/// <summary>
/// The Explorer thumbnail or icon of a shelf item. Shows a folder or document glyph at once and swaps in the
/// real image when the background thumbnail thread delivers it.
/// </summary>
public static class ShelfIcon
{
    public static FrameworkElement Create(ShelfItem item, double size, Palette p)
    {
        var host = new ContentControl
        {
            Width = size,
            Height = size,
            Focusable = false,
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var cached = ShellThumbnails.TryGetCached(item.Path, item.IsFolder);
        if (cached is not null)
        {
            host.Content = Picture(cached, size);
        }
        else
        {
            host.Content = Kit.Glyph(item.IsFolder ? Glyphs.Folder : Glyphs.Document, Math.Round(size * 0.6), p.TextSecondary);
            _ = LoadAsync(host, item, size);
        }
        return host;
    }

    private static async Task LoadAsync(ContentControl host, ShelfItem item, double size)
    {
        try
        {
            var image = await ShellThumbnails.GetAsync(item.Path, item.IsFolder, 96); // resumes on the UI thread
            if (image is not null) host.Content = Picture(image, size);
        }
        catch (Exception) { /* the glyph stays */ }
    }

    private static Image Picture(ImageSource source, double size)
    {
        var image = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }
}
