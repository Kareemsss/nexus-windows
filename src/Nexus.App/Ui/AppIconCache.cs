using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.Core.Diagnostics;

namespace Nexus.App.Ui;

/// <summary>App icons extracted from executables (volume mixer). Small, frozen and cached: 24 most recent.</summary>
public static class AppIconCache
{
    private const int Capacity = 24;
    private static readonly Dictionary<string, ImageSource?> Items = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();

    public static ImageSource? Get(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (Items.TryGetValue(exePath, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon is not null)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                image = source;
            }
        }
        catch (Exception ex) { Log.Debug("Icons", "No icon for app: " + ex.Message); }

        // Failures are cached too, so a missing icon is not retried every refresh.
        Items[exePath] = image;
        Order.Enqueue(exePath);
        while (Order.Count > Capacity) Items.Remove(Order.Dequeue());
        return image;
    }
}
