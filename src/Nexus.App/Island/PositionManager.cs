using Microsoft.Win32;
using Nexus.Core.Diagnostics;
using Nexus.Core.Settings;
using Nexus.Platform.Services;

namespace Nexus.App.Island;

/// <summary>
/// Places the island canvas in physical pixels on the chosen monitor, honouring each monitor's own DPI.
/// Re-applies on display changes (monitor plugged/unplugged, resolution or scale changes) and after
/// WM_DPICHANGED when the window crosses to a monitor with a different scale factor.
/// </summary>
public sealed class PositionManager : IDisposable
{
    public const double NearMargin = 14, SideMargin = 14; // canvas inset of the island (shared with the presenter)
    private readonly IslandWindow _window;
    private readonly SettingsStore _settings;

    public PositionManager(IslandWindow window, SettingsStore settings)
    {
        _window = window;
        _settings = settings;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _window.DpiScaleChanged += (_, _) => Apply();
    }

    public DisplayMonitor? Monitor { get; private set; }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        // Raised on a SystemEvents thread; also give Windows a moment to finish re-laying out the desktop.
        _window.Dispatcher.InvokeAsync(async () => { await Task.Delay(400); Apply(); _window.ReassertTopmost(); });

    public void Apply()
    {
        try
        {
            var p = _settings.Current.Position;
            var monitor = Choose(p);
            Monitor = monitor;

            var canvas = IslandWindow.CanvasSize;
            double dpi = monitor.Scale;
            double s = dpi * _window.Scale; // canvas units → physical pixels
            int wPx = (int)Math.Round(canvas.Width * s), hPx = (int)Math.Round(canvas.Height * s);
            var wa = monitor.WorkArea;
            double offset = p.EdgeOffset * dpi;

            int x = p.Anchor switch
            {
                IslandAnchor.TopLeft => (int)Math.Round(wa.Left + offset - SideMargin * s),
                IslandAnchor.TopRight => (int)Math.Round(wa.Right - offset + SideMargin * s - wPx),
                IslandAnchor.Custom => (int)Math.Round(wa.Left + wa.Width * p.CustomX - wPx / 2.0),
                _ => (int)Math.Round(wa.Left + wa.Width / 2.0 - wPx / 2.0),
            };
            int y = p.Anchor == IslandAnchor.BottomCenter
                ? (int)Math.Round(wa.Bottom - offset + NearMargin * s - hPx)
                : (int)Math.Round(wa.Top + offset - NearMargin * s);

            _window.PlacePixels(x, y);
        }
        catch (Exception ex) { Log.Error("Position", "Placement failed", ex); }
    }

    private static DisplayMonitor Choose(PositionSettings p)
    {
        switch (p.Monitor)
        {
            case MonitorMode.FollowCursor:
                return MonitorService.AtCursor();
            case MonitorMode.Specific:
                var match = MonitorService.GetAll().FirstOrDefault(m => string.Equals(m.DeviceName, p.MonitorDevice, StringComparison.OrdinalIgnoreCase));
                return match ?? MonitorService.Primary(); // monitor unplugged: fall back gracefully
            default:
                return MonitorService.Primary();
        }
    }

    public void Dispose() => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
}
