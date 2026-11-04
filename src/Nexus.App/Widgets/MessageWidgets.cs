using System.Windows;
using System.Windows.Controls;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Content;
using Nexus.Core.Settings;

namespace Nexus.App.Widgets;

/// <summary>Short-lived "glance": icon + label + detail. Used for connectivity, devices, captures, privacy starts.</summary>
public sealed class GlanceWidget : Widget<GlanceContent>
{
    protected override IWidgetView CreateCompact(TrackedActivity a, WidgetContext ctx) => new View(ctx, a, false);
    protected override IWidgetView CreateExpanded(TrackedActivity a, WidgetContext ctx) => new View(ctx, a, true);

    private sealed class View : WidgetView<GlanceContent>
    {
        private readonly FrameworkElement _root;
        private readonly TextBlock _glyph, _label, _detail;
        private readonly ContentControl? _actions;

        public View(WidgetContext ctx, TrackedActivity a, bool expanded) : base(ctx, a)
        {
            _glyph = Kit.Glyph("", 14, P.TextPrimary);
            _label = Kit.Text("", Kit.Type.Body, P.TextPrimary, FontWeights.SemiBold).WithMaxWidth(expanded ? 260 : 210);
            _detail = Kit.Text("", Kit.Type.Caption, P.TextSecondary).WithMaxWidth(160);
            if (!expanded)
            {
                var g = Kit.Columns(
                    (_glyph.WithMargin(Layout.CompactPadX, 0, 9, 0), Kit.Auto),
                    (_label, Kit.Auto),
                    (_detail.WithMargin(8, 1, Layout.CompactPadX + 2, 0), Kit.Auto));
                g.Height = Layout.GlanceHeight;
                _root = g;
            }
            else
            {
                _glyph.FontSize = 18;
                _actions = new ContentControl { Focusable = false };
                var header = Kit.Row(_glyph.WithMargin(0, 0, 12, 0), Kit.Column(_label, _detail.WithMargin(0, 2, 0, 0)));
                var root = new StackPanel { MinWidth = 300, Margin = new Thickness(Layout.ExpandedPad, 14, Layout.ExpandedPad, 14) };
                root.Children.Add(header);
                root.Children.Add(_actions.WithMargin(0, 12, 0, 0));
                _root = root;
            }
            Apply((GlanceContent)a.Activity.Content);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(GlanceContent g)
        {
            _glyph.Text = g.Glyph;
            _glyph.Foreground = Palette.Freeze(Ctx.Palette.Theme.Accent == AccentBehavior.Monochrome
                ? Ctx.Palette.Theme.ThemeAccent : Palette.Legible(Palette.ToColor(g.Color)));
            _label.Text = g.Label;
            _detail.Text = g.Detail ?? string.Empty;
            _detail.Visibility = string.IsNullOrEmpty(g.Detail) ? Visibility.Collapsed : Visibility.Visible;
            if (_actions is not null) _actions.Content = BuildActions(includeDismiss: true);
        }
    }
}

/// <summary>
/// Notifications: compact "[icon] Sender / message" capsule; expanded card with app header, full title,
/// up to three lines of body and actions. Long content is trimmed, never allowed to resize the island wildly.
/// </summary>
public sealed class NotificationWidget : Widget<NotificationContent>
{
    protected override IWidgetView CreateCompact(TrackedActivity a, WidgetContext ctx) => new Compact(ctx, a);
    protected override IWidgetView CreateExpanded(TrackedActivity a, WidgetContext ctx) => new Expanded(ctx, a);

    private static FrameworkElement IconTile(NotificationContent n, double size, Palette p) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28),
        Background = p.ControlFill, ClipToBounds = true,
        Child = IconImage.Create("notif-icon:" + n.AppName, n.AppIcon, n.Glyph ?? Glyphs.Ringer, size * 0.78, p.TextPrimary),
    };

    private sealed class Compact : WidgetView<NotificationContent>
    {
        private readonly Grid _root;
        private readonly ContentControl _icon = new() { Focusable = false };
        private readonly TextBlock _title, _body;

        public Compact(WidgetContext ctx, TrackedActivity a) : base(ctx, a)
        {
            _title = Kit.Text("", Kit.Type.Body, P.TextPrimary, FontWeights.SemiBold).WithMaxWidth(270);
            _body = Kit.Text("", Kit.Type.Caption, P.TextSecondary).WithMaxWidth(270);
            _root = Kit.Columns(
                (_icon.WithMargin(8, 0, 10, 0), Kit.Auto),
                (Kit.Column(_title, _body).WithMargin(0, 0, Layout.CompactPadX + 4, 0), Kit.Star));
            _root.Height = 44;
            Apply((NotificationContent)a.Activity.Content);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(NotificationContent n)
        {
            _icon.Content = IconTile(n, 28, P);
            _title.Text = n.Title;
            _body.Text = string.IsNullOrWhiteSpace(n.Body) ? n.AppName : n.Body;
        }
    }

    private sealed class Expanded : WidgetView<NotificationContent>
    {
        private readonly StackPanel _root;
        private readonly ContentControl _icon = new() { Focusable = false };
        private readonly TextBlock _app, _time, _title, _body;
        private readonly ContentControl _actions = new() { Focusable = false };

        public Expanded(WidgetContext ctx, TrackedActivity a) : base(ctx, a)
        {
            _app = Kit.Text("", Kit.Type.Caption, P.TextTertiary);
            _time = Kit.Text("now", Kit.Type.Caption, P.TextTertiary);
            _time.HorizontalAlignment = HorizontalAlignment.Right;
            _title = Kit.Text("", Kit.Type.Title - 1, P.TextPrimary, FontWeights.SemiBold);
            _body = Kit.Text("", Kit.Type.Body, P.TextSecondary);
            _body.TextWrapping = TextWrapping.Wrap;
            _body.LineHeight = 17.5;
            _body.MaxHeight = 3 * 17.5; // three lines, then ellipsis
            _body.TextTrimming = TextTrimming.WordEllipsis;

            var header = Kit.Columns((_icon.WithMargin(0, 0, 8, 0), Kit.Auto), (_app, Kit.Star), (_time, Kit.Auto));
            _root = new StackPanel { Width = 360, Margin = new Thickness(Layout.ExpandedPad, 14, Layout.ExpandedPad, 14) };
            _root.Children.Add(header);
            _root.Children.Add(_title.WithMargin(0, 10, 0, 0));
            _root.Children.Add(_body.WithMargin(0, 3, 0, 0));
            _root.Children.Add(_actions.WithMargin(0, 12, 0, 0));
            Apply((NotificationContent)a.Activity.Content);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(NotificationContent n)
        {
            _icon.Content = IconTile(n, 18, P);
            _app.Text = n.AppName.ToUpperInvariant();
            var age = DateTimeOffset.Now - n.Time;
            _time.Text = age < TimeSpan.FromMinutes(1) ? "now" : $"{(int)age.TotalMinutes} min ago";
            _title.Text = n.Title;
            _body.Text = n.Body;
            _body.Visibility = string.IsNullOrWhiteSpace(n.Body) ? Visibility.Collapsed : Visibility.Visible;
            _actions.Content = BuildActions(includeDismiss: true);
        }
    }
}

/// <summary>Fallback presentation for plugins and generic events (downloads, timers, CI status...).</summary>
public sealed class GenericWidget : Widget<GenericContent>
{
    protected override IWidgetView CreateCompact(TrackedActivity a, WidgetContext ctx) => new View(ctx, a, false);
    protected override IWidgetView CreateExpanded(TrackedActivity a, WidgetContext ctx) => new View(ctx, a, true);

    private sealed class View : WidgetView<GenericContent>
    {
        private readonly FrameworkElement _root;
        private readonly ContentControl _icon = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _title, _subtitle;
        private readonly ProgressTrack _progress;
        private readonly ContentControl? _actions;
        private readonly bool _expanded;

        public View(WidgetContext ctx, TrackedActivity a, bool expanded) : base(ctx, a)
        {
            _expanded = expanded;
            _title = Kit.Text("", Kit.Type.Body, P.TextPrimary, FontWeights.SemiBold).WithMaxWidth(expanded ? 280 : 200);
            _subtitle = Kit.Text("", Kit.Type.Caption, P.TextSecondary).WithMaxWidth(expanded ? 280 : 170);
            _progress = new ProgressTrack(false, 6) { TrackBrush = P.Track, Thickness = 3 };
            if (!expanded)
            {
                var g = Kit.Columns(
                    (_icon.WithMargin(Layout.CompactPadX, 0, 10, 0), Kit.Auto),
                    (Kit.Column(_title, _subtitle).WithMargin(0, 0, Layout.CompactPadX + 2, 0), Kit.Star));
                g.Height = Layout.CompactHeight;
                _root = g;
            }
            else
            {
                _actions = new ContentControl { Focusable = false };
                var root = new StackPanel { MinWidth = 320, Margin = new Thickness(Layout.ExpandedPad, 14, Layout.ExpandedPad, 14) };
                root.Children.Add(Kit.Row(_icon.WithMargin(0, 0, 12, 0), Kit.Column(_title, _subtitle)));
                root.Children.Add(_progress.WithMargin(0, 12, 0, 0));
                root.Children.Add(_actions.WithMargin(0, 12, 0, 0));
                _root = root;
            }
            Apply((GenericContent)a.Activity.Content);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(GenericContent c)
        {
            var color = c.Color is { } nc ? Palette.Freeze(Palette.Legible(Palette.ToColor(nc))) : Ctx.AccentBrushOf(Activity.Activity);
            _icon.Content = c.Indeterminate ? new Spinner(color, _expanded ? 18 : 15) : Kit.Glyph(c.Glyph, _expanded ? 18 : 15, color);
            _title.Text = c.Title;
            _subtitle.Text = c.Subtitle ?? string.Empty;
            _subtitle.Visibility = string.IsNullOrEmpty(c.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
            _progress.FillBrush = color;
            _progress.Visibility = _expanded && c.Progress is not null && !c.Indeterminate ? Visibility.Visible : Visibility.Collapsed;
            if (c.Progress is { } p) _progress.SetProgress(p);
            if (_actions is not null) _actions.Content = BuildActions(includeDismiss: Activity.Activity.IsTransient);
        }
    }
}
