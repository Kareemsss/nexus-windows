using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Platform.Services;

public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");
    private static readonly Guid ScreenshotsId = new("B7BEDE81-DF94-4682-A7D8-57A52620B86F");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static string? Downloads => Get(DownloadsId) ?? Fallback(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public static string? Screenshots => Get(ScreenshotsId) ?? Fallback(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    private static string? Get(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
            try { var s = Marshal.PtrToStringUni(p); return Directory.Exists(s) ? s : null; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }

    private static string? Fallback(string root, string child)
    {
        var p = Path.Combine(root, child);
        return Directory.Exists(p) ? p : null;
    }
}
