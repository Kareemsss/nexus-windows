using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.Core.Color;
using Nexus.Core.Diagnostics;

namespace Nexus.Platform.Services;

/// <summary>Decodes artwork once, off the UI thread, and derives the ambient accent from a 24px thumbnail.</summary>
public static class ArtworkProcessor
{
    public const int MaxBytes = 6 * 1024 * 1024;

    public static NexusColor? ExtractAccent(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 24;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            var bgra = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var buffer = new byte[stride * h];
            bgra.CopyPixels(buffer, stride, 0);
            return ColorExtractor.ExtractDominant(buffer, w, h, stride)?.Ambientize();
        }
        catch (Exception ex)
        {
            Log.Debug("Artwork", "Accent extraction failed: " + ex.Message);
            return null;
        }
    }
}
