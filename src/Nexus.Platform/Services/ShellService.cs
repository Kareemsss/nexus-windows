using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
using Nexus.Core.Diagnostics;
using static Nexus.Platform.Interop.NativeMethods;

namespace Nexus.Platform.Services;

/// <summary>Launching URIs, files and bringing existing app windows to the front.</summary>
public static class ShellService
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn("Shell", "Open failed", ex); }
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Open(dir);
        }
        catch (Exception ex) { Log.Warn("Shell", "Show in folder failed", ex); }
    }

    /// <summary>Puts a path on the clipboard as text. Call on the UI thread. Returns false when the clipboard stayed locked.</summary>
    public static bool CopyPath(string path) =>
        WithClipboard(() => System.Windows.Clipboard.SetText(path));

    /// <summary>
    /// Puts files on the clipboard the way Explorer's Copy does, so Ctrl+V in Explorer, mail or chat pastes the
    /// files themselves. Call on the UI thread.
    /// </summary>
    public static bool CopyFiles(IEnumerable<string> paths)
    {
        var list = new System.Collections.Specialized.StringCollection();
        foreach (var p in paths) list.Add(p);
        if (list.Count == 0) return false;
        return WithClipboard(() => System.Windows.Clipboard.SetFileDropList(list));
    }

    /// <summary>The clipboard can be held open by another app for a moment (CLIPBRD_E_CANT_OPEN): retry briefly.</summary>
    private static bool WithClipboard(Action write)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { write(); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); }
            catch (Exception ex) { Log.Warn("Shell", "Clipboard write failed", ex); return false; }
        }
        Log.Warn("Shell", "Clipboard stayed locked by another app");
        return false;
    }

    /// <summary>Restores and focuses the main window of the first running process whose name matches.</summary>
    public static bool ActivateProcess(params string[] processNames)
    {
        foreach (var name in processNames)
        {
            Process[] procs = Array.Empty<Process>();
            try
            {
                procs = Process.GetProcessesByName(name);
                var p = procs.FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero);
                if (p is null) continue;
                var h = p.MainWindowHandle;
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
                return true;
            }
            catch (Exception ex) { Log.Debug("Shell", "Activate failed: " + ex.Message); }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        return false;
    }

    public static string? ProcessNameOfWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }
}
