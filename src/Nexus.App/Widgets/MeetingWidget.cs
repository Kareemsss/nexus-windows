using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;

namespace Nexus.App.Widgets;

/// <summary>
/// Upcoming:  [calendar] Meeting in 8 min · Infrastructure Weekly
/// Starting:  expanded card with Join
/// Active:    [people] 12:04  [mic]  – a live call timer with privacy state
/// </summary>
public sealed class MeetingWidget : Widget<MeetingContent>
{
    protected override IWidgetView CreateCompact(TrackedActivity a, WidgetContext ctx) => new Compact(ctx, a);
    protected override IWidgetView CreateExpanded(TrackedActivity a, WidgetContext ctx) => new Expanded(ctx, a);

    internal static string Headline(MeetingContent m, DateTimeOffset now)
    {
        if (m.Phase == MeetingPhase.Active) return Kit.FormatTime(now - (m.ActiveSince ?? now));
        if (m.Phase == MeetingPhase.Starting || m.Start is null) return "Starting now";
        var mins = (int)Math.Ceiling((m.Start.Value - now).TotalMinutes);
        return mins <= 0 ? "Starting now" : mins >= 60 ? $"At {m.Start.Value.ToLocalTime():HH:mm}" : $"Meeting in {mins} min";
    }

    internal static string TimeRange(MeetingContent m) =>
        m.Start is { } s ? $"{s.ToLocalTime():HH:mm}{(m.End is { } e ? " – " + e.ToLocalTime().ToString("HH:mm") : "")}" : "";

    private sealed class Compact : WidgetView<MeetingContent>
    {
        private readonly Grid _root;
        private readonly TextBlock _glyph, _headline, _title, _mic, _cam;
        private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        private MeetingContent _m;

        public Compact(WidgetContext ctx, TrackedActivity a) : base(ctx, a)
        {
            _m = (MeetingContent)a.Activity.Content;
            _glyph = Kit.Glyph("", 15, ctx.AccentBrushOf(a.Activity));
            _headline = Kit.Numeric("", Kit.Type.Body, P.TextPrimary, FontWeights.SemiBold);
            _title = Kit.Text("", Kit.Type.Caption, P.TextSecondary).WithMaxWidth(170);
            _mic = Kit.Glyph(Glyphs.Microphone, 13, P.TextSecondary);
            _cam = Kit.Glyph(Glyphs.Camera, 13, Palette.Freeze(Palette.ToColor(SemanticColors.Camera)));
            _root = Kit.Columns(
                (_glyph.WithMargin(Layout.CompactPadX, 0, 10, 0), Kit.Auto),
                (_headline, Kit.Auto),
                (_title.WithMargin(8, 1, 0, 0), Kit.Star),
                (Kit.Row(_cam.WithMargin(10, 0, 0, 0), _mic.WithMargin(10, 0, 0, 0)).WithMargin(0, 0, Layout.CompactPadX + 2, 0), Kit.Auto));
            _root.Height = Layout.CompactHeight;
            _tick.Tick += (_, _) => _headline.Text = Headline(_m, DateTimeOffset.Now);
            Apply(_m);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(MeetingContent m)
        {
            _m = m;
            bool active = m.Phase == MeetingPhase.Active;
            _glyph.Text = active ? Glyphs.People : Glyphs.Calendar;
            _headline.Text = Headline(m, DateTimeOffset.Now);
            _title.Text = active ? m.Platform : m.Title;
            _mic.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            _mic.Text = m.MicMuted ? Glyphs.MicOff : Glyphs.Microphone;
            _mic.Foreground = Palette.Freeze(Palette.ToColor(m.MicMuted ? SemanticColors.Critical : SemanticColors.Privacy));
            _cam.Visibility = active && m.CameraActive ? Visibility.Visible : Visibility.Collapsed;
        }

        public override void OnShown() => _tick.Start();
        public override void OnHidden() => _tick.Stop();
    }

    private sealed class Expanded : WidgetView<MeetingContent>
    {
        private readonly StackPanel _root;
        private readonly TextBlock _kicker, _title, _meta, _timer;
        private readonly ContentControl _actions = new() { Focusable = false };
        private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        private MeetingContent _m;

        public Expanded(WidgetContext ctx, TrackedActivity a) : base(ctx, a)
        {
            _m = (MeetingContent)a.Activity.Content;
            _kicker = Kit.Text("", Kit.Type.Caption, ctx.AccentBrushOf(a.Activity), FontWeights.SemiBold);
            _title = Kit.Text("", Kit.Type.Title, P.TextPrimary, FontWeights.SemiBold).WithMaxWidth(300);
            _meta = Kit.Text("", Kit.Type.Caption, P.TextSecondary);
            _timer = Kit.Numeric("", Kit.Type.Headline, P.TextPrimary, FontWeights.Light);
            _timer.HorizontalAlignment = HorizontalAlignment.Right;
            var top = Kit.Columns(
                (Kit.Column(_kicker, _title.WithMargin(0, 3, 0, 0), _meta.WithMargin(0, 3, 0, 0)), Kit.Star),
                (_timer.WithMargin(12, 0, 0, 0), Kit.Auto));
            _root = new StackPanel { Width = 372, Margin = new Thickness(Layout.ExpandedPad + 2, 14, Layout.ExpandedPad + 2, 14) };
            _root.Children.Add(top);
            _root.Children.Add(_actions.WithMargin(0, 14, 0, 0));
            _tick.Tick += (_, _) => { if (_m.Phase == MeetingPhase.Active) _timer.Text = Headline(_m, DateTimeOffset.Now); };
            Apply(_m);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(MeetingContent m)
        {
            _m = m;
            bool active = m.Phase == MeetingPhase.Active;
            _kicker.Text = (active ? "IN CALL" : m.Phase == MeetingPhase.Starting ? "STARTING NOW" : "UP NEXT") + "  ·  " + m.Platform.ToUpperInvariant();
            _title.Text = m.Title;
            var range = TimeRange(m);
            _meta.Text = active
                ? (m.MicMuted ? "Microphone muted (system)" : "Microphone live") + (m.CameraActive ? "  ·  Camera on" : "")
                : string.IsNullOrEmpty(range) ? m.Platform : range;
            _timer.Text = active ? Headline(m, DateTimeOffset.Now) : m.Start is { } s ? s.ToLocalTime().ToString("HH:mm") : "";
            _actions.Content = BuildActions(includeDismiss: Activity.Activity.IsTransient);
        }

        public override void OnShown() => _tick.Start();
        public override void OnHidden() => _tick.Stop();
    }
}
