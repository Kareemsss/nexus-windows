using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Content;
using Nexus.Core.Settings;

namespace Nexus.App.Widgets;

public sealed class MediaWidget : Widget<MediaContent>
{
    protected override IWidgetView CreateCompact(TrackedActivity a, WidgetContext ctx) => new Compact(ctx, a);
    protected override IWidgetView CreateExpanded(TrackedActivity a, WidgetContext ctx) => new Expanded(ctx, a);

    private static bool Animate(WidgetContext ctx) => ctx.Settings.Appearance.AnimatedEqualizer && !ctx.Settings.Appearance.ReduceMotion;

    /// <summary>[art] title / artist ......... [equalizer]</summary>
    private sealed class Compact : WidgetView<MediaContent>
    {
        private readonly Grid _root;
        private readonly ArtworkView _art;
        private readonly TextBlock _title, _artist;
        private readonly Equalizer _eq;
        private readonly TextBlock _paused;

        public Compact(WidgetContext ctx, TrackedActivity a) : base(ctx, a)
        {
            _art = new ArtworkView(26, 7, P);
            _title = Kit.Text("", Kit.Type.Body, P.TextPrimary, FontWeights.SemiBold).WithMaxWidth(196);
            _artist = Kit.Text("", Kit.Type.Caption, P.TextSecondary).WithMaxWidth(196);
            _eq = new Equalizer(ctx.AccentBrushOf(a.Activity), 15, 13);
            _paused = Kit.Glyph(Glyphs.Pause, 11, P.TextTertiary);
            var right = new Grid { Width = 16, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(_eq);
            right.Children.Add(_paused);
            _root = Kit.Columns(
                (_art.WithMargin(6, 0, 10, 0), Kit.Auto),
                (Kit.Column(_title, _artist), Kit.Star),
                (right.WithMargin(12, 0, Layout.CompactPadX, 0), Kit.Auto));
            _root.Height = Layout.CompactHeight;
            Apply((MediaContent)a.Activity.Content);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(MediaContent m)
        {
            _art.Set(m.ArtworkKey, m.Artwork, Ctx.AccentOf(Activity.Activity));
            _title.Text = m.Title;
            _artist.Text = string.IsNullOrEmpty(m.Artist) ? m.SourceApp : m.Artist;
            _artist.Visibility = string.IsNullOrEmpty(_artist.Text) ? Visibility.Collapsed : Visibility.Visible;
            bool playing = m.Playback == MediaPlayback.Playing;
            _eq.SetBrush(Ctx.AccentBrushOf(Activity.Activity));
            _eq.SetRunning(playing, Animate(Ctx));
            _eq.Opacity = playing ? 1 : 0;
            _paused.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        }

        public override void OnHidden() => _eq.SetRunning(false, false);
    }

    /// <summary>Large artwork, metadata, scrubbable progress, transport controls.</summary>
    private sealed class Expanded : WidgetView<MediaContent>
    {
        private readonly StackPanel _root;
        private readonly ArtworkView _art;
        private readonly TextBlock _title, _artist, _source, _elapsed, _remaining;
        private readonly ProgressTrack _progress;
        private readonly NexusButton _prev, _play, _next;
        private readonly TextBlock _playGlyph;
        private readonly Equalizer _eq;
        private readonly DispatcherTimer _tick;
        private MediaContent _m;

        public Expanded(WidgetContext ctx, TrackedActivity a) : base(ctx, a)
        {
            _m = (MediaContent)a.Activity.Content;
            _art = new ArtworkView(64, 14, P);
            _title = Kit.Text("", Kit.Type.Title, P.TextPrimary, FontWeights.SemiBold);
            _artist = Kit.Text("", Kit.Type.Body, P.TextSecondary).WithMargin(0, 2, 0, 0);
            _source = Kit.Text("", Kit.Type.Caption, P.TextTertiary).WithMargin(0, 4, 0, 0);
            _eq = new Equalizer(ctx.AccentBrushOf(a.Activity), 16, 14)
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0),
            };

            var header = Kit.Columns(
                (_art, Kit.Auto),
                (Kit.Column(_title, _artist, _source).WithMargin(14, 0, 8, 0), Kit.Star),
                (_eq, Kit.Auto));

            _progress = new ProgressTrack(interactive: true) { TrackBrush = P.Track, FillBrush = P.TextPrimary, Thickness = 4 };
            _progress.ValueChanged += OnSeek;
            _elapsed = Kit.Numeric("0:00", Kit.Type.Caption, P.TextTertiary);
            _remaining = Kit.Numeric("0:00", Kit.Type.Caption, P.TextTertiary);
            _remaining.HorizontalAlignment = HorizontalAlignment.Right;
            var times = Kit.Columns((_elapsed, Kit.Star), (_remaining, Kit.Star));

            _prev = NexusButton.Icon(Glyphs.Previous, 15, 38, P.TextPrimary, P);
            _playGlyph = Kit.Glyph(Glyphs.Pause, 17, P.TextPrimary);
            _play = new NexusButton(_playGlyph, P.Theme.ControlFill, P.Theme.ControlHover, 22) { Width = 44, Height = 44 };
            _next = NexusButton.Icon(Glyphs.Next, 15, 38, P.TextPrimary, P);
            _prev.Click += (_, _) => _ = _m.Controls.PreviousAsync();
            _next.Click += (_, _) => _ = _m.Controls.NextAsync();
            _play.Click += (_, _) =>
            {
                // Optimistic: flip the glyph immediately, Windows confirms a moment later.
                _playGlyph.Text = _playGlyph.Text == Glyphs.Pause ? Glyphs.Play : Glyphs.Pause;
                _ = _m.Controls.TogglePlayPauseAsync();
            };
            var transport = Kit.Row(_prev, _play.WithMargin(18, 0, 18, 0), _next);
            transport.HorizontalAlignment = HorizontalAlignment.Center;

            _root = new StackPanel { Width = Layout.ExpandedWidth, Margin = new Thickness(Layout.ExpandedPad, 16, Layout.ExpandedPad, 12) };
            _root.Children.Add(header);
            _root.Children.Add(_progress.WithMargin(0, 14, 0, 0));
            _root.Children.Add(times.WithMargin(0, 1, 0, 0));
            _root.Children.Add(transport.WithMargin(0, 4, 0, 0));

            _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _tick.Tick += (_, _) => UpdateTimeline(animate: true);
            Apply(_m);
        }

        public override FrameworkElement Element => _root;

        protected override void Apply(MediaContent m)
        {
            _m = m;
            _art.Set(m.ArtworkKey, m.Artwork, Ctx.AccentOf(Activity.Activity));
            _title.Text = m.Title;
            _artist.Text = m.Artist;
            _artist.Visibility = string.IsNullOrEmpty(m.Artist) ? Visibility.Collapsed : Visibility.Visible;
            _source.Text = string.IsNullOrEmpty(m.Album) ? m.SourceApp : $"{m.SourceApp}  ·  {m.Album}";
            bool playing = m.Playback == MediaPlayback.Playing;
            _playGlyph.Text = playing ? Glyphs.Pause : Glyphs.Play;
            _prev.IsEnabledVisual = m.CanPrevious;
            _next.IsEnabledVisual = m.CanNext;
            _play.IsEnabledVisual = m.CanTogglePlay;
            _eq.SetBrush(Ctx.AccentBrushOf(Activity.Activity));
            _eq.SetRunning(playing, Animate(Ctx));
            _progress.FillBrush = Ctx.Palette.Theme.Accent == AccentBehavior.Monochrome ? P.TextPrimary : Ctx.AccentBrushOf(Activity.Activity);
            bool hasTimeline = m.Duration > TimeSpan.Zero;
            var vis = hasTimeline ? Visibility.Visible : Visibility.Hidden;
            _progress.Visibility = vis; _elapsed.Visibility = vis; _remaining.Visibility = vis;
            UpdateTimeline(animate: true);
            if (playing && hasTimeline) _tick.Start(); else _tick.Stop();
        }

        private void UpdateTimeline(bool animate)
        {
            if (_m.Duration <= TimeSpan.Zero) return;
            var pos = _m.EstimatePosition(DateTimeOffset.UtcNow);
            _progress.SetProgress(pos.TotalSeconds / _m.Duration.TotalSeconds, animate);
            _elapsed.Text = Kit.FormatTime(pos);
            _remaining.Text = "-" + Kit.FormatTime(_m.Duration - pos);
        }

        private void OnSeek(double v, bool final)
        {
            var target = TimeSpan.FromSeconds(_m.Duration.TotalSeconds * v);
            _elapsed.Text = Kit.FormatTime(target);
            _remaining.Text = "-" + Kit.FormatTime(_m.Duration - target);
            if (final && _m.CanSeek) _ = _m.Controls.SeekAsync(target);
        }

        public override void OnShown() { if (_m.Playback == MediaPlayback.Playing && _m.Duration > TimeSpan.Zero) _tick.Start(); }
        public override void OnHidden() { _tick.Stop(); _eq.SetRunning(false, false); }
    }
}
