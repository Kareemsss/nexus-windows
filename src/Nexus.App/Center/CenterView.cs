using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nexus.App.Motion;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.App.Widgets;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Diagnostics;
using Nexus.Core.Motion;
using Nexus.Core.Settings;
using Nexus.Platform.Providers;
using Nexus.Platform.Services;

namespace Nexus.App.Center;

/// <summary>
/// Island Center – the island unfolded into a compact command center. The header (clock) is fixed; every other
/// section, its order, the system tiles and the quick actions come from <see cref="CenterSettings"/>.
/// Sections enter with a short stagger after the container has morphed (container → primary → secondary).
/// </summary>
public sealed class CenterView : IWidgetView, IAnimatable, IResizableView
{
    public const double ContentWidth = 428;

    private readonly WidgetContext _ctx;
    private readonly Palette _p;
    private readonly CenterSettings _cfg;
    private readonly StackPanel _root;
    private readonly TextBlock _time, _date, _statusNet, _statusBattery, _statusBatteryGlyph;
    private Border? _privacy;
    private TextBlock? _privacyText;
    private NowPlayingCard? _nowPlaying;
    private MixerCard? _mixer;
    private MeetingCard? _meeting;
    private SystemCard? _system;
    private QuickActionBar? _actions;
    private ShelfCard? _shelf;
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Background);
    private readonly List<(FrameworkElement Element, TranslateTransform Shift, Spring Progress, double Delay)> _stagger = new();
    private double _elapsed;

    public CenterView(WidgetContext ctx)
    {
        _ctx = ctx;
        _p = ctx.Palette;
        _cfg = ctx.Settings.Center;

        _time = Kit.Numeric("", Kit.Type.Display, _p.TextPrimary, FontWeights.Light);
        _date = Kit.Text("", Kit.Type.Body, _p.TextSecondary);
        _date.Visibility = _cfg.ShowDate ? Visibility.Visible : Visibility.Collapsed;
        _statusNet = Kit.Glyph("", 14, _p.TextSecondary);
        _statusBatteryGlyph = Kit.Glyph("", 14, _p.TextSecondary);
        _statusBattery = Kit.Numeric("", Kit.Type.Body, _p.TextSecondary, FontWeights.SemiBold);
        var status = Kit.Row(_statusNet.WithMargin(0, 0, 14, 0), _statusBatteryGlyph.WithMargin(0, 0, 6, 0), _statusBattery);
        status.VerticalAlignment = VerticalAlignment.Top;
        status.Margin = new Thickness(0, 10, 2, 0);
        var header = Kit.Columns((Kit.Column(_time, _date.WithMargin(2, -4, 0, 0)), Kit.Star), (status, Kit.Auto));

        _root = new StackPanel { Width = ContentWidth, Margin = new Thickness(18, 14, 18, 16) };
        Add(header, 0);

        int order = 1;
        foreach (var item in _cfg.Sections.Where(s => s.Visible))
        {
            FrameworkElement? element = item.Section switch
            {
                CenterSection.Privacy => BuildPrivacy(),
                CenterSection.NowPlaying => (_nowPlaying = new NowPlayingCard(_p)).Element,
                CenterSection.Shelf => ctx.Commands.Shelf is { } shelf
                    ? (_shelf = new ShelfCard(_p, ctx, shelf, () => ContentResized?.Invoke(this, EventArgs.Empty))).Element
                    : null, // module switched off
                CenterSection.Mixer => (_mixer = new MixerCard(_p, _cfg, ctx, () => ContentResized?.Invoke(this, EventArgs.Empty))).Element,
                CenterSection.Meeting => (_meeting = new MeetingCard(_p, ctx)).Element,
                CenterSection.SystemStatus => (_system = new SystemCard(_p, _cfg.SystemTiles)).Element,
                CenterSection.QuickActions => _cfg.QuickActions.Count > 0
                    ? (_actions = new QuickActionBar(_p, ctx, _cfg.QuickActions)).Element
                    : null,
                _ => null,
            };
            if (element is null) continue;
            double top = item.Section == CenterSection.QuickActions ? 14 : order == 1 ? 12 : 10;
            Add(element.WithMargin(0, top, 0, 0), order++);
        }

        _clock.Tick += (_, _) => RefreshClock();
        Refresh();
    }

    public FrameworkElement Element => _root;

    /// <summary>Raised when the mixer gains or loses rows, so the island can re-morph to the new height.</summary>
    public event EventHandler? ContentResized;

    private FrameworkElement BuildPrivacy()
    {
        var brush = Palette.Freeze(Palette.ToColor(SemanticColors.Privacy));
        _privacyText = Kit.Text("", Kit.Type.Body, brush, FontWeights.SemiBold);
        _privacy = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Background = Palette.Freeze(Color.FromArgb(0x24, 0xFF, 0x8A, 0x3D)),
            BorderBrush = Palette.Freeze(Color.FromArgb(0x40, 0xFF, 0x8A, 0x3D)),
            BorderThickness = new Thickness(1),
            Child = Kit.Row(Kit.Glyph(Glyphs.Shield, 13, brush).WithMargin(0, 0, 10, 0), _privacyText),
        };
        return _privacy;
    }

    private void Add(FrameworkElement e, int order)
    {
        var shift = new TranslateTransform(0, 10);
        e.RenderTransform = shift;
        e.Opacity = 0;
        _stagger.Add((e, shift, new Spring(0, SpringConfig.Content, 0.002), 0.07 + order * 0.035));
        _root.Children.Add(e);
    }

    public void Update(TrackedActivity? activity) => Refresh();

    private void Refresh()
    {
        RefreshClock();
        var hub = _ctx.Commands.Hub;
        bool hideEmpty = _cfg.HideEmptyCards;

        if (_nowPlaying is not null)
        {
            var media = hub.Find(MediaProvider.ActivityId)?.Activity;
            var content = media?.Content as MediaContent;
            _nowPlaying.Apply(content, media is null ? _p.Theme.ThemeAccent : _ctx.AccentOf(media));
            _nowPlaying.Element.Visibility = hideEmpty && content is null ? Visibility.Collapsed : Visibility.Visible;
        }

        _shelf?.Refresh(hideEmpty);

        if (_meeting is not null)
        {
            var meeting = hub.Find(MeetingProvider.ActiveId) ?? hub.Find(MeetingProvider.NextId);
            _meeting.Apply(meeting);
            _meeting.Element.Visibility = hideEmpty && meeting is null ? Visibility.Collapsed : Visibility.Visible;
        }

        var battery = hub.Find(BatteryProvider.StatusId)?.Activity.Content as BatteryContent;
        var network = hub.Find(NetworkProvider.StatusId)?.Activity.Content as NetworkContent;
        _system?.Apply(
            hub.LatestContent<VolumeContent>(),
            battery,
            network,
            hub.Find(BluetoothProvider.StatusId)?.Activity.Content as GenericContent);

        bool hasBattery = battery is not null && battery.State != PowerState.NoBattery;
        _statusBatteryGlyph.Visibility = _statusBattery.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (battery is not null && hasBattery)
        {
            _statusBatteryGlyph.Text = Glyphs.Battery(battery.Percent, battery.OnExternalPower);
            _statusBattery.Text = battery.Percent + "%";
        }
        _statusNet.Text = network is null ? "" : PeekView.NetworkGlyph(network);

        if (_privacy is not null && _privacyText is not null)
        {
            var users = new[] { "privacy.mic", "privacy.cam" }
                .Select(id => hub.Find(id)?.Activity.Content as PrivacyContent)
                .Where(c => c is not null)
                .Select(c => (c!.Resource == PrivacyResource.Microphone ? "Microphone" : "Camera") + " · " + string.Join(", ", c.Users.Select(u => u.AppName).Distinct()))
                .ToList();
            _privacy.Visibility = users.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _privacyText.Text = string.Join("   ", users);
        }

        _actions?.Refresh();
    }

    private void RefreshClock()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString(_cfg.Use24HourClock ? "HH:mm" : "h:mm tt");
        _date.Text = now.ToString("dddd, d MMMM");
        _clock.Interval = TimeSpan.FromSeconds(60 - now.Second + 0.05);
    }

    public void OnShown()
    {
        _clock.Start();
        _mixer?.Start();
        _shelf?.Start();
        _elapsed = 0;
        foreach (var s in _stagger) { s.Progress.Snap(0); s.Element.Opacity = 0; }
        Animator.Wake(this);
    }

    public void OnHidden()
    {
        _clock.Stop();
        _mixer?.Stop();
        _nowPlaying?.Stop();
        _shelf?.Stop();
    }

    public bool Advance(double dt)
    {
        _elapsed += dt;
        bool moving = false;
        foreach (var (element, shift, progress, delay) in _stagger)
        {
            if (_elapsed >= delay) progress.Target = 1;
            moving |= progress.Step(dt) || progress.Target < 1;
            var v = Math.Clamp(progress.Value, 0, 1.2);
            element.Opacity = Math.Clamp(v, 0, 1);
            shift.Y = 10 * (1 - v);
        }
        return moving;
    }

    #region Cards

    internal static Border Card(Palette p, UIElement child) => new()
    {
        CornerRadius = new CornerRadius(18),
        Background = p.CardFill,
        BorderBrush = p.CardStroke,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(14, 12, 14, 12),
        Child = child,
    };

    internal static TextBlock Kicker(string text, Palette p)
    {
        var t = Kit.Text(text, 10.5, p.TextTertiary, FontWeights.SemiBold);
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    /// <summary>
    /// Per-app volume. Rows keep their position while the card is open (no jumping when an app pauses), the
    /// list refreshes every 1.5 s only while Island Center is visible, and nothing runs once it closes.
    /// </summary>
    private sealed class MixerCard
    {
        private readonly Palette _p;
        private readonly CenterSettings _cfg;
        private readonly Action _resized;
        private readonly StackPanel _rows = new();
        private readonly FrameworkElement _empty;
        private readonly TextBlock _more;
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        private readonly Dictionary<string, MixerRow> _byKey = new(StringComparer.Ordinal);
        private List<string> _order = new();
        private (int Rows, bool Visible) _shape = (-1, true);
        private bool _busy, _live;

        public MixerCard(Palette p, CenterSettings cfg, WidgetContext ctx, Action resized)
        {
            _p = p;
            _cfg = cfg;
            _resized = resized;

            _more = Kit.Text("", 10.5, p.TextTertiary).WithMargin(0, 0, 6, 8);
            _more.VerticalAlignment = VerticalAlignment.Top;
            var allLabel = Kit.Text("All apps", 10.5, p.TextSecondary, FontWeights.SemiBold);
            var all = new NexusButton(allLabel, Colors.Transparent, p.Theme.ControlHover, 9)
            {
                Height = 20, Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -6, 0),
            };
            all.Click += (_, _) => { ctx.Commands.Collapse(); ShellService.Open("ms-settings:apps-volume"); };
            var header = Kit.Columns((Kicker("VOLUME MIXER", p), Kit.Star), (_more, Kit.Auto), (all, Kit.Auto));

            _empty = Kit.Row(
                Kit.Glyph(Glyphs.Speakers, 14, p.TextTertiary).WithMargin(0, 0, 10, 0),
                Kit.Text("No apps are using audio right now", Kit.Type.Caption, p.TextTertiary));
            _empty.Margin = new Thickness(0, 2, 0, 4);

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(_rows);
            stack.Children.Add(_empty);
            Element = Card(p, stack);

            // Fill the card before the first frame so the island unfolds at the right height. The wait is capped;
            // if Windows is slow the previous list is used and corrected a moment later.
            Apply(AudioSessionService.Shared.TrySnapshot(TimeSpan.FromMilliseconds(120)), notify: false);
        }

        public FrameworkElement Element { get; }

        public void Start()
        {
            _live = true;
            _ = RefreshAsync();
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public void Stop()
        {
            _live = false;
            _timer.Stop();
        }

        private void OnTick(object? sender, EventArgs e) => _ = RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var sessions = await AudioSessionService.Shared.SnapshotAsync(); // resumes on the UI thread
                if (_live) Apply(sessions, notify: true);
            }
            catch (Exception ex) { Log.Debug("Mixer", "Refresh failed: " + ex.Message); }
            finally { _busy = false; }
        }

        private void Apply(IReadOnlyList<AudioSessionInfo> sessions, bool notify)
        {
            int max = Math.Clamp(_cfg.MixerMaxApps, 2, 8);
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _order.Count; i++) rank[_order[i]] = i;
            int RankOf(AudioSessionInfo s) => rank.TryGetValue(s.Key, out var r) ? r : int.MaxValue;

            // Which apps get a row: playing apps first, then apps already listed. Where they appear: rows keep
            // their place, newcomers go to the bottom.
            var chosen = sessions
                .OrderByDescending(s => s.Active)
                .ThenBy(RankOf)
                .ThenBy(s => s.IsSystem)
                .Take(max)
                .ToList();
            var display = chosen.OrderBy(RankOf).ToList(); // OrderBy is stable: newcomers keep their relative order
            var keys = display.Select(d => d.Key).ToList();

            foreach (var s in display)
            {
                if (!_byKey.TryGetValue(s.Key, out var row)) _byKey[s.Key] = row = new MixerRow(_p, s.Key);
                row.Apply(s);
            }
            foreach (var gone in _byKey.Keys.Except(keys).ToList()) _byKey.Remove(gone);

            if (!keys.SequenceEqual(_order))
            {
                _rows.Children.Clear();
                foreach (var k in keys) _rows.Children.Add(_byKey[k].Element);
                _order = keys;
            }

            int hidden = sessions.Count - display.Count;
            _more.Text = hidden > 0 ? $"+{hidden} more" : "";
            _empty.Visibility = display.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool visible = !(_cfg.HideEmptyCards && display.Count == 0);
            Element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            var shape = (display.Count, visible);
            if (shape == _shape) return;
            _shape = shape;
            if (notify) _resized();
        }
    }

    /// <summary>[icon] App name  ━━━━━━━──── 64  [mute]. Scroll over a row to nudge that app's volume.</summary>
    private sealed class MixerRow
    {
        private const double RowHeight = 32;
        private readonly Palette _p;
        private readonly string _key;
        private readonly ContentControl _icon = new() { Focusable = false, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name, _percent, _muteGlyph;
        private readonly ProgressTrack _track;
        private string? _iconPath = "\0";
        private bool _muted;
        private DateTime _touchedAt;

        public MixerRow(Palette p, string key)
        {
            _p = p;
            _key = key;
            _name = Kit.Text("", Kit.Type.Body, p.TextPrimary).WithWidth(112);
            _track = new ProgressTrack(interactive: true, height: 22) { TrackBrush = p.Track, FillBrush = p.TextPrimary, Thickness = 4 };
            _track.ValueChanged += (v, _) => SetFromUser(v);
            _percent = Kit.Numeric("", Kit.Type.Caption, p.TextSecondary).WithWidth(30);
            _percent.TextAlignment = TextAlignment.Right;
            _muteGlyph = Kit.Glyph(Glyphs.Volume2, 12, p.TextSecondary);
            var mute = new NexusButton(_muteGlyph, Colors.Transparent, p.Theme.ControlHover, 13) { Width = 26, Height = 26 };
            mute.Click += (_, _) =>
            {
                _touchedAt = DateTime.UtcNow;
                SetMutedVisual(!_muted);
                AudioSessionService.Shared.SetMuted(_key, _muted);
            };

            var grid = Kit.Columns(
                (_icon.WithMargin(0, 0, 10, 0), Kit.Auto),
                (_name.WithMargin(0, 0, 10, 0), Kit.Auto),
                (_track, Kit.Star),
                (_percent.WithMargin(6, 0, 4, 0), Kit.Auto),
                (mute, Kit.Auto));
            grid.Height = RowHeight;
            grid.Background = Brushes.Transparent; // makes the whole row a scroll target
            grid.MouseWheel += OnWheel;
            Element = grid;
        }

        public FrameworkElement Element { get; }

        public void Apply(AudioSessionInfo s)
        {
            _name.Text = s.Name;
            _name.ToolTip = s.Name;
            _name.Foreground = s.Active ? _p.TextPrimary : _p.TextSecondary;

            if (_iconPath != s.ExePath)
            {
                _iconPath = s.ExePath;
                var image = AppIconCache.Get(s.ExePath);
                _icon.Content = image is not null
                    ? new Image { Source = image, Width = 18, Height = 18, Stretch = Stretch.Uniform }
                    : Kit.Glyph(s.IsSystem ? Glyphs.Speakers : "\uE71D", 14, _p.TextSecondary);
            }

            // Right after the user moved the slider, Windows may still report the old value: don't snap back.
            if (DateTime.UtcNow - _touchedAt < TimeSpan.FromSeconds(1.2)) return;
            _track.SetProgress(s.Volume);
            SetMutedVisual(s.Muted);
            _percent.Text = s.Muted ? "Off" : Percent(s.Volume);
        }

        private void SetFromUser(double v)
        {
            _touchedAt = DateTime.UtcNow;
            AudioSessionService.Shared.SetVolume(_key, (float)v);
            if (_muted && v > 0.005) SetMutedVisual(false);
            _percent.Text = Percent(v);
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            var v = Math.Clamp(_track.Value + Math.Sign(e.Delta) * 0.02, 0, 1);
            _track.SetProgress(v);
            SetFromUser(v);
            e.Handled = true; // keep the island's own wheel handler from changing the system volume
        }

        private void SetMutedVisual(bool muted)
        {
            _muted = muted;
            _muteGlyph.Text = muted ? Glyphs.Mute : Glyphs.Volume2;
            _muteGlyph.Foreground = muted ? Palette.Freeze(Palette.ToColor(SemanticColors.Critical)) : _p.TextSecondary;
            _track.FillBrush = muted ? _p.TextTertiary : _p.TextPrimary;
            _track.InvalidateVisual();
            if (muted) _percent.Text = "Off";
        }

        private static string Percent(double v) => ((int)Math.Round(v * 100)).ToString();
    }

    private sealed class NowPlayingCard
    {
        private readonly ArtworkView _art;
        private readonly TextBlock _title, _artist, _playGlyph;
        private readonly NexusButton _prev, _play, _next;
        private readonly Grid _content;
        private readonly StackPanel _empty;
        private readonly Equalizer _eq;
        private MediaContent? _m;

        public NowPlayingCard(Palette p)
        {
            _art = new ArtworkView(52, 12, p);
            _title = Kit.Text("", Kit.Type.BodyStrong, p.TextPrimary, FontWeights.SemiBold).WithMaxWidth(200);
            _artist = Kit.Text("", Kit.Type.Caption, p.TextSecondary).WithMaxWidth(200);
            _eq = new Equalizer(p.TextSecondary, 13, 11) { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            _prev = NexusButton.Icon(Glyphs.Previous, 13, 32, p.TextPrimary, p);
            _playGlyph = Kit.Glyph(Glyphs.Pause, 14, p.TextPrimary);
            _play = new NexusButton(_playGlyph, p.Theme.ControlFill, p.Theme.ControlHover, 18) { Width = 36, Height = 36 };
            _next = NexusButton.Icon(Glyphs.Next, 13, 32, p.TextPrimary, p);
            _prev.Click += (_, _) => { if (_m is not null) _ = _m.Controls.PreviousAsync(); };
            _next.Click += (_, _) => { if (_m is not null) _ = _m.Controls.NextAsync(); };
            _play.Click += (_, _) =>
            {
                if (_m is null) return;
                _playGlyph.Text = _playGlyph.Text == Glyphs.Pause ? Glyphs.Play : Glyphs.Pause;
                _ = _m.Controls.TogglePlayPauseAsync();
            };
            _content = Kit.Columns(
                (_art.WithMargin(0, 0, 12, 0), Kit.Auto),
                (Kit.Column(_title, _artist, _eq), Kit.Star),
                (Kit.Row(_prev, _play.WithMargin(4, 0, 4, 0), _next), Kit.Auto));

            var emptyTitle = Kit.Text("Nothing playing", Kit.Type.BodyStrong, p.TextSecondary, FontWeights.SemiBold);
            var emptyHint = Kit.Text("Music and video from Spotify, browsers and other apps appear here.", Kit.Type.Caption, p.TextTertiary);
            emptyHint.TextWrapping = TextWrapping.Wrap;
            var emptyIcon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(11), Background = p.ControlFill, Child = Kit.Glyph(Glyphs.Music, 16, p.TextTertiary) };
            _empty = Kit.Row(emptyIcon.WithMargin(0, 0, 12, 0), Kit.Column(emptyTitle, emptyHint).WithMaxWidth(320));

            var stack = new StackPanel();
            stack.Children.Add(Kicker("NOW PLAYING", p));
            var host = new Grid();
            host.Children.Add(_content);
            host.Children.Add(_empty);
            stack.Children.Add(host);
            Element = Card(p, stack);
        }

        public FrameworkElement Element { get; }

        public void Apply(MediaContent? m, Color accent)
        {
            _m = m;
            _content.Visibility = m is null ? Visibility.Collapsed : Visibility.Visible;
            _empty.Visibility = m is null ? Visibility.Visible : Visibility.Collapsed;
            if (m is null) { _eq.SetRunning(false, false); return; }
            _art.Set(m.ArtworkKey, m.Artwork, accent);
            _title.Text = m.Title;
            _artist.Text = string.IsNullOrEmpty(m.Artist) ? m.SourceApp : m.Artist;
            bool playing = m.Playback == MediaPlayback.Playing;
            _playGlyph.Text = playing ? Glyphs.Pause : Glyphs.Play;
            _prev.IsEnabledVisual = m.CanPrevious;
            _next.IsEnabledVisual = m.CanNext;
            _eq.SetBrush(Palette.Freeze(accent));
            _eq.SetRunning(playing, !Animator.ReduceMotion);
            _eq.Visibility = playing ? Visibility.Visible : Visibility.Hidden;
        }

        public void Stop() => _eq.SetRunning(false, false);
    }

    private sealed class MeetingCard
    {
        private readonly WidgetContext _ctx;
        private readonly TextBlock _kicker, _title, _meta;
        private readonly ContentControl _action = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid _content;
        private readonly StackPanel _empty;
        private readonly Border _accentBar;

        public MeetingCard(Palette p, WidgetContext ctx)
        {
            _ctx = ctx;
            _kicker = Kicker("NEXT", p);
            _title = Kit.Text("", Kit.Type.BodyStrong, p.TextPrimary, FontWeights.SemiBold).WithMaxWidth(270);
            _meta = Kit.Text("", Kit.Type.Caption, p.TextSecondary).WithMargin(0, 2, 0, 0);
            _accentBar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 2, 12, 2) };
            _content = Kit.Columns((_accentBar, Kit.Auto), (Kit.Column(_title, _meta), Kit.Star), (_action, Kit.Auto));
            var emptyTitle = Kit.Text("No upcoming meetings", Kit.Type.BodyStrong, p.TextSecondary, FontWeights.SemiBold);
            var emptyHint = Kit.Text("Calls in Teams, Zoom and others are detected automatically.", Kit.Type.Caption, p.TextTertiary);
            _empty = Kit.Column(emptyTitle, emptyHint.WithMargin(0, 2, 0, 0));
            var stack = new StackPanel();
            stack.Children.Add(_kicker);
            var host = new Grid();
            host.Children.Add(_content);
            host.Children.Add(_empty);
            stack.Children.Add(host);
            Element = Card(p, stack);
        }

        public FrameworkElement Element { get; }

        public void Apply(TrackedActivity? t)
        {
            var m = t?.Activity.Content as MeetingContent;
            _content.Visibility = m is null ? Visibility.Collapsed : Visibility.Visible;
            _empty.Visibility = m is null ? Visibility.Visible : Visibility.Collapsed;
            if (m is null || t is null) { _kicker.Text = "NEXT"; return; }
            bool active = m.Phase == MeetingPhase.Active;
            _kicker.Text = active ? "IN CALL" : "NEXT";
            _accentBar.Background = _ctx.AccentBrushOf(t.Activity);
            _title.Text = m.Title;
            _meta.Text = active
                ? $"{m.Platform}  ·  {MeetingWidget.Headline(m, DateTimeOffset.Now)}"
                : $"{MeetingWidget.TimeRange(m)}  ·  {m.Platform}";
            var primary = t.Activity.Actions.FirstOrDefault(a => a.IsPrimary);
            if (primary is null) { _action.Content = null; return; }
            var b = NexusButton.Pill(primary.Label, primary.Glyph, _ctx.Palette, true, _ctx.AccentOf(t.Activity));
            b.Click += (_, _) => _ctx.Commands.RunAction(primary);
            _action.Content = b;
        }
    }

    private sealed class SystemCard
    {
        private readonly Dictionary<SystemTile, Tile> _tiles = new();

        public SystemCard(Palette p, IReadOnlyList<SystemTile> tiles)
        {
            var grid = new Grid();
            for (int i = 0; i < tiles.Count; i++)
            {
                var tile = new Tile(p);
                _tiles[tiles[i]] = tile;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(tile.Element, i);
                tile.Element.Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0);
                grid.Children.Add(tile.Element);
            }
            var stack = new StackPanel();
            stack.Children.Add(Kicker("SYSTEM", p));
            stack.Children.Add(grid);
            Element = Card(p, stack);
        }

        public FrameworkElement Element { get; }

        public void Apply(VolumeContent? v, BatteryContent? b, NetworkContent? n, GenericContent? bt)
        {
            if (_tiles.TryGetValue(SystemTile.Audio, out var audio))
            {
                if (v is null) audio.Set(Glyphs.Speakers, "No output", "Audio");
                else audio.Set(VolumeWidget.GlyphFor(v), v.DeviceName, v.Muted ? "Muted" : $"{Math.Round(v.Level * 100)}%");
            }
            if (_tiles.TryGetValue(SystemTile.Battery, out var battery))
            {
                if (b is null || b.State == PowerState.NoBattery) battery.Set(Glyphs.Power, "AC power", "Power");
                else battery.Set(Glyphs.Battery(b.Percent, b.OnExternalPower), $"{b.Percent}%", BatteryWidget.StatusText(b));
            }
            if (_tiles.TryGetValue(SystemTile.Network, out var network))
            {
                if (n is null) network.Set(Glyphs.Globe, "Unknown", "Network");
                else network.Set(PeekView.NetworkGlyph(n),
                    n.Kind == NetworkKind.None ? "Offline" : n.Name ?? (n.Kind == NetworkKind.Ethernet ? "Ethernet" : "Connected"),
                    n.VpnActive ? $"VPN · {n.VpnName}" : n.HasInternet ? "Internet" : n.Kind == NetworkKind.None ? "No connection" : "No internet");
            }
            if (_tiles.TryGetValue(SystemTile.Bluetooth, out var bluetooth))
            {
                if (bt is null) bluetooth.Set(Glyphs.Bluetooth, "Bluetooth", "Unavailable");
                else bluetooth.Set(Glyphs.Bluetooth, bt.Title, bt.Subtitle ?? "Bluetooth");
            }
        }
    }

    private sealed class Tile
    {
        private readonly TextBlock _glyph, _value, _label;

        public Tile(Palette p)
        {
            _glyph = Kit.Glyph("", 15, p.TextPrimary);
            _glyph.HorizontalAlignment = HorizontalAlignment.Left;
            _value = Kit.Text("", Kit.Type.Body, p.TextPrimary, FontWeights.SemiBold).WithMargin(0, 8, 0, 0);
            _label = Kit.Text("", 10.5, p.TextTertiary);
            Element = Kit.Column(_glyph, _value, _label);
        }

        public FrameworkElement Element { get; }

        public void Set(string glyph, string value, string label)
        {
            _glyph.Text = glyph;
            _value.Text = value;
            _label.Text = label;
        }
    }

    private sealed class QuickActionBar
    {
        private readonly WidgetContext _ctx;
        private readonly Palette _p;
        private (NexusButton Button, TextBlock Glyph)? _quiet, _mic;

        public QuickActionBar(Palette p, WidgetContext ctx, IReadOnlyList<QuickAction> actions)
        {
            _p = p;
            _ctx = ctx;
            var grid = new Grid();
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                var (glyph, label) = QuickActionCatalog.Describe(action);
                var made = Make(glyph, label, () => Execute(action), out var column);
                if (action == QuickAction.Quiet) _quiet = made;
                if (action == QuickAction.Mic) _mic = made;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(column, i);
                grid.Children.Add(column);
            }
            Element = grid;
        }

        public FrameworkElement Element { get; }

        private void Execute(QuickAction action)
        {
            switch (action)
            {
                case QuickAction.Quiet:
                    _ctx.Commands.ToggleQuietMode();
                    Refresh();
                    return;
                case QuickAction.Mic:
                    _ctx.Commands.ToggleMicMute();
                    Refresh();
                    return;
                case QuickAction.Settings:
                    _ctx.Commands.Collapse();
                    _ctx.Commands.OpenSettings();
                    return;
            }
            if (QuickActionCatalog.Target(action) is not { } target) return;
            _ctx.Commands.Collapse();
            if (action == QuickAction.Screenshot)
                _ = Task.Delay(350).ContinueWith(_ => ShellService.Open(target)); // let the island fold away first
            else
                ShellService.Open(target);
        }

        private (NexusButton, TextBlock) Make(string glyph, string label, Action onClick, out FrameworkElement column)
        {
            var g = Kit.Glyph(glyph, 15, _p.TextPrimary);
            var b = new NexusButton(g, _p.Theme.ControlFill, _p.Theme.ControlHover, 20) { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => onClick();
            var caption = Kit.Text(label, 10.5, _p.TextTertiary);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Margin = new Thickness(0, 6, 0, 0);
            column = Kit.Column(b, caption);
            return (b, g);
        }

        public void Refresh()
        {
            if (_quiet is { } q) Highlight(q, _ctx.Commands.QuietMode, Glyphs.QuietHours, Glyphs.QuietHours);
            if (_mic is { } m)
            {
                var muted = _ctx.Commands.MicMuted;
                Highlight(m, muted == true, Glyphs.MicOff, Glyphs.Microphone);
                m.Button.IsEnabledVisual = muted is not null;
            }
        }

        private void Highlight((NexusButton Button, TextBlock Glyph) item, bool on, string onGlyph, string offGlyph)
        {
            item.Glyph.Text = on ? onGlyph : offGlyph;
            item.Glyph.Foreground = on ? Palette.Freeze(Color.FromRgb(0x0B, 0x0B, 0x0E)) : _p.TextPrimary;
            item.Button.SetColors(on ? _p.Theme.TextPrimary : _p.Theme.ControlFill, on ? Colors.White : _p.Theme.ControlHover);
        }
    }

    #endregion
}
