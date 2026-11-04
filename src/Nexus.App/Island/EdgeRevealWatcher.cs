using System.Windows.Threading;
using Nexus.Core.Settings;
using Nexus.Platform.Interop;
using Nexus.Platform.Services;

namespace Nexus.App.Island;

/// <summary>
/// "Peek from the edge" for Hide-completely fullscreen mode. While armed it checks the cursor 10x per second;
/// pushing the pointer against the island's screen edge reveals it, moving away tucks it back in.
/// Polling (only while a fullscreen app is in front) is used instead of an invisible hot strip, because a
/// strip would sit on top of the game and steal clicks.
/// </summary>
public sealed class EdgeRevealWatcher : IDisposable
{
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(650);

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly IslandWindow _window;
    private readonly SettingsStore _settings;
    private readonly Func<bool> _holding;
    private DateTime? _hotSince, _leftAt;

    public EdgeRevealWatcher(IslandWindow window, SettingsStore settings, Func<bool> holding)
    {
        _window = window;
        _settings = settings;
        _holding = holding;
        _timer.Tick += (_, _) => OnTick();
    }

    public bool IsArmed => _timer.IsEnabled;
    public bool IsRevealed { get; private set; }
    public event EventHandler? RevealChanged;

    public void SetArmed(bool armed)
    {
        if (armed)
        {
            if (!_timer.IsEnabled) _timer.Start();
            return;
        }
        _timer.Stop();
        _hotSince = _leftAt = null;
        if (IsRevealed) { IsRevealed = false; RevealChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void OnTick()
    {
        if (_window.Handle == IntPtr.Zero || !NativeMethods.GetCursorPos(out var p)) return;
        // A hidden window keeps its position, so its rectangle still tells us where the island lives.
        if (!NativeMethods.GetWindowRect(_window.Handle, out var r)) return;
        var mon = MonitorService.FromWindow(_window.Handle).Bounds;

        double s = Math.Max(0.1, (r.Right - r.Left) / IslandWindow.CanvasSize.Width);
        var anchor = _settings.Current.Position.Anchor;
        bool bottom = anchor == IslandAnchor.BottomCenter;
        double centerX = anchor switch
        {
            IslandAnchor.TopLeft => r.Left + (PositionManager.SideMargin + 60) * s,
            IslandAnchor.TopRight => r.Right - (PositionManager.SideMargin + 60) * s,
            _ => (r.Left + r.Right) / 2.0,
        };
        double dx = Math.Abs(p.X - centerX);
        int edgeDistance = bottom ? (mon.Bottom - 1) - p.Y : p.Y - mon.Top;
        bool onMonitor = p.X >= mon.Left && p.X < mon.Right && p.Y >= mon.Top && p.Y < mon.Bottom;
        var now = DateTime.UtcNow;

        if (!IsRevealed)
        {
            bool hot = onMonitor && edgeDistance <= 1 && dx <= 140 * s;
            if (!hot) { _hotSince = null; return; }
            _hotSince ??= now;
            if (now - _hotSince.Value < Dwell) return;
            _hotSince = null;
            _leftAt = null;
            IsRevealed = true;
            RevealChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        double depth = (_settings.Current.Position.EdgeOffset + 90) * s;
        bool inside = _holding() || (onMonitor && edgeDistance <= depth && dx <= 260 * s);
        if (inside) { _leftAt = null; return; }
        _leftAt ??= now;
        if (now - _leftAt.Value < Linger) return;
        _leftAt = null;
        IsRevealed = false;
        RevealChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer.Stop();
}