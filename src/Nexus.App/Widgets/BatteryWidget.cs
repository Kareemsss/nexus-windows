using System.Windows;
using System.Windows.Controls;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Content;

namespace Nexus.App.Widgets;

public sealed class BatteryWidget : Widget<BatteryContent>
{
    protected override IWidgetView CreateCompact(TrackedActivity a, WidgetContext ctx) => new Compact(ctx, a);
    protected override IWidgetView CreateExpanded(TrackedActivity a, WidgetContext ctx) => new Expanded(ctx, a);

    internal static string StatusText(BatteryContent b) => b.State switch
    {
        PowerState.Charging => "Charging",
        PowerState.Full => "Fully charged",
        PowerState.PluggedIn => "Plugged in",
        PowerState.NoBattery => "AC power",
        _ when b.Band == BatteryBand.Critical => "Battery critically low",
        _ when b.Band == BatteryBand.Low => "Battery low",
        _ => "On battery",
    };

    internal static string? EstimateText(BatteryContent b) => b.Remaining is not { } r ? null
        : b.State == PowerState.Charging ? $"{Kit.FormatDuration(r)} until full"
        : $"{Kit.FormatDuration(r)} remaining";

    private sealed class Compact : WidgetView<BatteryContent>
    {
        private readonly Grid _root;
        private readonly TextBlock _glyph, _status, _percent;

        public Compact(WidgetContext ctx, TrackedActivity a) : base(ctx, a)
        {
            _glyph = Kit.Glyph("", 16, P.TextPrimary);
            _status = Kit.Text("", Kit.Type.Body, P.TextPrimary, FontWeights.SemiBold);
            _percent = Kit.Numeric("", Kit.Type.BodyStrong, P.TextPrimary, FontWeights.SemiBold);
            _root = Kit.Columns(
                (_glyph.WithMargin(Layout.CompactPadX, 0, 10, 0), Kit.Auto),
                (_status.WithMaxWidth(200), Kit.Star),
                (_percent.WithMargin(18, 0, Layout.CompactPadX + 2, 0), Kit.Auto));
            _root.Height = Layout.CompactHeight;
            Apply((BatteryContent)a.Activity.Content);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(BatteryContent b)
        {
            var accent = Ctx.AccentBrushOf(Activity.Activity);
            _glyph.Text = Glyphs.Battery(b.Percent, b.OnExternalPower);
            _glyph.Foreground = accent;
            _status.Text = StatusText(b);
            _percent.Text = $"{b.Percent}%";
            _percent.Foreground = b.Band == BatteryBand.Normal ? P.TextPrimary : accent;
        }
    }

    private sealed class Expanded : WidgetView<BatteryContent>
    {
        private readonly StackPanel _root;
        private readonly TextBlock _percent, _status, _estimate, _glyph;
        private readonly ProgressTrack _bar;
        private readonly ContentControl _actions = new() { Focusable = false };

        public Expanded(WidgetContext ctx, TrackedActivity a) : base(ctx, a)
        {
            _glyph = Kit.Glyph("", 22, P.TextPrimary);
            _percent = Kit.Numeric("", Kit.Type.Display, P.TextPrimary, FontWeights.Light);
            _status = Kit.Text("", Kit.Type.BodyStrong, P.TextPrimary, FontWeights.SemiBold);
            _estimate = Kit.Text("", Kit.Type.Caption, P.TextSecondary).WithMargin(0, 2, 0, 0);
            _bar = new ProgressTrack(false, 8) { TrackBrush = P.Track, Thickness = 5 };

            var top = Kit.Columns(
                (Kit.Column(_status, _estimate), Kit.Star),
                (Kit.Row(_glyph.WithMargin(0, 0, 10, 0), _percent), Kit.Auto));
            _root = new StackPanel { Width = 340, Margin = new Thickness(Layout.ExpandedPad + 2, 14, Layout.ExpandedPad + 2, 14) };
            _root.Children.Add(top);
            _root.Children.Add(_bar.WithMargin(0, 10, 0, 0));
            _root.Children.Add(_actions.WithMargin(0, 12, 0, 0));
            Apply((BatteryContent)a.Activity.Content);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(BatteryContent b)
        {
            var accent = Ctx.AccentBrushOf(Activity.Activity);
            _glyph.Text = Glyphs.Battery(b.Percent, b.OnExternalPower);
            _glyph.Foreground = accent;
            _percent.Text = $"{b.Percent}%";
            _status.Text = b.Band == BatteryBand.Critical && !b.OnExternalPower ? "Plug in your PC now" : StatusText(b);
            _estimate.Text = EstimateText(b) ?? (b.EnergySaverOn ? "Energy saver is on" : StatusText(b));
            _bar.FillBrush = accent;
            _bar.SetProgress(b.Percent / 100.0);
            bool hasActions = Activity.Activity.Actions.Count > 0;
            _actions.Content = hasActions ? BuildActions(includeDismiss: false) : null;
            _actions.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
