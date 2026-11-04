using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.Core.Diagnostics;

namespace Nexus.Platform.Services;

/// <summary>
/// Explorer's own thumbnail or icon for a file or folder (File Shelf). Uses IShellItemImageFactory, the same API
/// Explorer uses, on one background STA thread so a slow thumbnail handler never blocks the island. Results are
/// frozen and kept in a small least-recently-used cache: by path for pictures, videos and per-file icons
/// (.exe, .lnk), by extension for everything else.
/// </summary>
public static class ShellThumbnails
{
    private const int Capacity = 64;

    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".ico", ".svg",
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v",
        ".pdf", ".psd", ".exe", ".lnk", ".url", ".appref-ms", ".msi",
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recent = new();
    private static readonly BlockingCollection<Action> Work = new();

    static ShellThumbnails()
    {
        var worker = new Thread(() =>
        {
            foreach (var job in Work.GetConsumingEnumerable())
            {
                try { job(); } catch (Exception ex) { Log.Debug("Thumbnails", "Job failed: " + ex.GetType().Name); }
            }
        })
        { IsBackground = true, Name = "NEXUS shell thumbnails" };
        worker.SetApartmentState(ApartmentState.STA); // shell extensions expect an STA
        worker.Start();
    }

    /// <summary>Returns the cached image right away when there is one, otherwise null.</summary>
    public static ImageSource? TryGetCached(string path, bool isFolder)
    {
        lock (Gate) return Cache.TryGetValue(KeyOf(path, isFolder, out _), out var image) ? image : null;
    }

    /// <summary>Thumbnail (pictures, videos, documents with a preview handler) or icon, about <paramref name="size"/> pixels.</summary>
    public static Task<ImageSource?> GetAsync(string path, bool isFolder, int size = 64)
    {
        var key = KeyOf(path, isFolder, out bool perFile);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit)) { Touch(key); return Task.FromResult(hit); }
        }
        var done = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() =>
        {
            ImageSource? image = null;
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                    image = Load(path, size, perFile ? SIIGBF.BIGGERSIZEOK : SIIGBF.BIGGERSIZEOK | SIIGBF.ICONONLY);
            }
            catch (Exception ex) { Log.Debug("Thumbnails", "No image: " + ex.GetType().Name); }
            lock (Gate)
            {
                Cache[key] = image; // failures are cached too, so a broken file is not retried every refresh
                Touch(key);
                while (Recent.Count > Capacity)
                {
                    Cache.Remove(Recent.Last!.Value);
                    Recent.RemoveLast();
                }
            }
            done.TrySetResult(image);
        });
        return done.Task;
    }

    private static string KeyOf(string path, bool isFolder, out bool perFile)
    {
        var ext = isFolder ? "" : Path.GetExtension(path);
        perFile = isFolder || string.IsNullOrEmpty(ext) || PerFileExtensions.Contains(ext);
        return perFile ? "path:" + path : "ext:" + ext;
    }

    private static void Touch(string key)
    {
        Recent.Remove(key);
        Recent.AddFirst(key);
    }

    private static ImageSource? Load(string path, int size, SIIGBF flags)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            try { return ToBitmapSource(hbitmap); }
            finally { DeleteObject(hbitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    /// <summary>
    /// Copies the DIB section pixel by pixel so the alpha channel survives (Imaging.CreateBitmapSourceFromHBitmap
    /// turns transparent icon corners black).
    /// </summary>
    private static BitmapSource ToBitmapSource(IntPtr hbitmap)
    {
        BitmapSource result;
        if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), out var ds) != 0 && ds.dsBm.bmBits != IntPtr.Zero && ds.dsBm.bmBitsPixel == 32)
        {
            int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = ds.dsBm.bmWidthBytes;
            var pixels = new byte[stride * h];
            Marshal.Copy(ds.dsBm.bmBits, pixels, 0, pixels.Length);
            if (ds.dsBmih.biHeight > 0) FlipRows(pixels, stride, h); // bottom-up DIB
            bool hasAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
            result = BitmapSource.Create(w, h, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, stride);
        }
        else
        {
            result = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        result.Freeze();
        return result;
    }

    private static void FlipRows(byte[] pixels, int stride, int height)
    {
        var row = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
            Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
            Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
        }
    }

    #region Interop

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        MEMORYONLY = 0x02,
        ICONONLY = 0x04,
        THUMBNAILONLY = 0x08,
        INCACHEONLY = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfield0, dsBitfield1, dsBitfield2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out DIBSECTION ds);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    #endregion
}
