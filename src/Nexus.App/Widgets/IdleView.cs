using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Content;
using Nexus.Core.Settings;

namespace Nexus.App.Widgets;

/// <summary>Idle shape sizes per idle style. The idle island has no content: it is pure glass.</summary>
public static class IdleGeometry
{
    public static Size For(IdleStyle style) => style switch
    {
        IdleStyle.Line => new Size(76, 6),
        IdleStyle.Dot => new Size(34, 10),
        _ => new Size(104, 26),
    };
}

public sealed class IdleView : IWidgetView
{
    private readonly Border _root;
    public IdleView(Size size) => _root = new Border { Width = size.Width, Height = size.Height };
    public FrameworkElement Element => _root;
    public void Update(TrackedActivity? activity) { }
    public void OnShown() { }
    public void OnHidden() { }
}

/// <summary>Idle + hover: a glanceable clock with battery and network state. Never covers anything important.</summary>
public sealed class PeekView : IWidgetView
{
    private readonly Grid _root;
    private readonly TextBlock _time, _date, _battery, _batteryGlyph, _net;
    private readonly WidgetContext _ctx;
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Background);

    public PeekView(WidgetContext ctx)
    {
        _ctx = ctx;
        var p = ctx.Palette;
        _time = Kit.Numeric("", Kit.Type.BodyStrong, p.TextPrimary, FontWeights.SemiBold);
        _date = Kit.Text("", Kit.Type.Caption, p.TextSecondary);
        _batteryGlyph = Kit.Glyph("", 13, p.TextSecondary);
        _battery = Kit.Numeric("", Kit.Type.Caption, p.TextSecondary);
        _net = Kit.Glyph("", 13, p.TextSecondary);
        _root = Kit.Columns(
            (_time.WithMargin(Layout.CompactPadX, 0, 8, 0), Kit.Auto),
            (_date, Kit.Star),
            (Kit.Row(_net.WithMargin(14, 0, 10, 0), _batteryGlyph.WithMargin(0, 0, 5, 0), _battery).WithMargin(0, 0, Layout.CompactPadX, 0), Kit.Auto));
        _root.Height = 32;
        _root.MinWidth = 210;
        _clock.Tick += (_, _) => Refresh();
        Refresh();
    }

    public FrameworkElement Element => _root;

    public void Update(TrackedActivity? activity) => Refresh();

    internal static string NetworkGlyph(NetworkContent n) => n.VpnActive ? Glyphs.Vpn : n.Kind switch
    {
        NetworkKind.WiFi => Glyphs.WifiBars(n.SignalBars),
        NetworkKind.Ethernet => Glyphs.Ethernet,
        NetworkKind.Cellular => Glyphs.Cellular,
        NetworkKind.None => Glyphs.Offline,
        _ => Glyphs.Globe,
    };

    private void Refresh()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString("HH:mm");
        _date.Text = now.ToString("ddd d MMM");
        var b = _ctx.Commands.Hub.LatestContent<BatteryContent>();
        bool showBattery = b is not null && b.State != PowerState.NoBattery;
        _batteryGlyph.Visibility = _battery.Visibility = showBattery ? Visibility.Visible : Visibility.Collapsed;
        if (b is not null && showBattery)
        {
            _batteryGlyph.Text = Glyphs.Battery(b.Percent, b.OnExternalPower);
            _battery.Text = $"{b.Percent}%";
        }
        var n = _ctx.Commands.Hub.LatestContent<NetworkContent>();
        _net.Text = n is null ? "" : NetworkGlyph(n);
        // Re-arm exactly at the next minute boundary: one tick per minute, no polling.
        _clock.Interval = TimeSpan.FromSeconds(60 - now.Second + 0.05);
    }

    public void OnShown() => _clock.Start();
    public void OnHidden() => _clock.Stop();
}
