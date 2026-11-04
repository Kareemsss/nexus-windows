using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Nexus.Core.Diagnostics;
using static Nexus.Platform.Interop.NativeMethods;

namespace Nexus.Platform.Services;

/// <summary>
/// Detects fullscreen apps (games, video, presentations) without hooks into other processes.
///
/// Signals combined:
///  * SHQueryUserNotificationState – Windows' own "do not disturb the user" verdict (D3D exclusive fullscreen,
///    presentation mode);
///  * the foreground window covering its entire monitor (borderless fullscreen games, F11 browsers).
/// Re-evaluated on foreground/move-size WinEvents (out-of-context, no DLL injection) plus a slow 3 s safety
/// check that catches in-place fullscreen toggles which raise no WinEvent. The check costs two cheap API calls.
/// </summary>
public sealed class FullscreenDetector : IDisposable
{
    private readonly WinEventProc _proc; // must stay referenced: native code holds the callback
    private readonly List<IntPtr> _hooks = new();
    private readonly DispatcherTimer _timer;
    private readonly Func<IntPtr, bool> _isOwnWindow;
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
    };

    public FullscreenDetector(Func<IntPtr, bool> isOwnWindow)
    {
        _isOwnWindow = isOwnWindow;
        _proc = OnWinEvent;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Evaluate();
    }

    public bool IsFullscreen { get; private set; }
    public event EventHandler<bool>? Changed;

    public void Start()
    {
        foreach (var e in new[] { EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MINIMIZEEND })
            _hooks.Add(SetWinEventHook(e, e, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        _timer.Start();
        Evaluate();
    }

    private void OnWinEvent(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0) return; // OBJID_WINDOW only
        Evaluate();
    }

    public void Evaluate()
    {
        bool fullscreen = false;
        try
        {
            if (SHQueryUserNotificationState(out var state) == 0 && state is 3 or 4) fullscreen = true;
            if (!fullscreen) fullscreen = ForegroundCoversMonitor();
        }
        catch (Exception ex) { Log.Debug("Fullscreen", ex.Message); }

        if (fullscreen == IsFullscreen) return;
        IsFullscreen = fullscreen;
        Changed?.Invoke(this, fullscreen);
    }

    private bool ForegroundCoversMonitor()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || _isOwnWindow(fg) || !IsWindowVisible(fg)) return false;
        if (ShellClasses.Contains(ClassNameOf(fg))) return false;
        if (DwmGetWindowAttribute(fg, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var m = MonitorService.FromWindow(fg).Bounds;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var h in _hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        _hooks.Clear();
    }
}
