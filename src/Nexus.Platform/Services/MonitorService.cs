using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Platform.Interop;
using static Nexus.Platform.Interop.NativeMethods;

namespace Nexus.Platform.Services;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <param name="Scale">Effective DPI scale (1.0 = 96 DPI, 1.5 = 144 DPI...).</param>
public sealed record DisplayMonitor(IntPtr Handle, string DeviceName, PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary, int Index)
{
    public string Label => $"Display {Index + 1}{(IsPrimary ? " (primary)" : "")} · {Bounds.Width}×{Bounds.Height} · {Scale * 100:0}%";
}

/// <summary>Physical-pixel monitor geometry and per-monitor DPI. Never assumes 1920×1080 or 100% scaling.</summary>
public static class MonitorService
{
    public static IReadOnlyList<DisplayMonitor> GetAll()
    {
        var handles = new List<IntPtr>();
        MonitorEnumProc proc = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) => { handles.Add(h); return true; };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        var list = new List<DisplayMonitor>();
        foreach (var h in handles)
        {
            var m = Describe(h, list.Count);
            if (m is not null) list.Add(m);
        }
        // Stable, human-friendly order: left to right, then top to bottom.
        return list.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)
            .Select((m, i) => m with { Index = i }).ToList();
    }

    public static DisplayMonitor Primary() =>
        GetAll().FirstOrDefault(m => m.IsPrimary) ?? FromPoint(0, 0);

    public static DisplayMonitor FromPoint(int x, int y)
    {
        var h = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        return Describe(h, 0) ?? Fallback();
    }

    public static DisplayMonitor AtCursor()
    {
        GetCursorPos(out var p);
        return FromPoint(p.X, p.Y);
    }

    public static DisplayMonitor FromWindow(IntPtr hwnd) =>
        Describe(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), 0) ?? Fallback();

    private static DisplayMonitor? Describe(IntPtr h, int index)
    {
        if (h == IntPtr.Zero) return null;
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
        if (!GetMonitorInfo(h, ref info)) return null;
        double scale = 1.0;
        try
        {
            if (GetDpiForMonitor(h, 0 /* MDT_EFFECTIVE_DPI */, out var dx, out _) == 0 && dx > 0) scale = dx / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return new DisplayMonitor(h, info.szDevice,
            new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
            new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
            scale, (info.dwFlags & MONITORINFOF_PRIMARY) != 0, index);
    }

    // Only reached if every Win32 call failed (e.g. session teardown); keeps callers alive rather than throwing.
    private static DisplayMonitor Fallback() =>
        new(IntPtr.Zero, "DISPLAY", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true, 0);
}
