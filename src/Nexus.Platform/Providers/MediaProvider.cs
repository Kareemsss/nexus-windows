using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Diagnostics;
using Nexus.Core.Plugins;
using Nexus.Platform.Services;
using Windows.Media;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Nexus.Platform.Providers;

/// <summary>
/// Now-playing information from Windows' Global System Media Transport Controls (the same source as the
/// Windows media flyout): Spotify, browsers (YouTube, YouTube Music...), Media Player and any app that
/// integrates with SystemMediaTransportControls. Fully event-driven.
/// </summary>
public sealed class MediaProvider : ProviderBase
{
    public const string ActivityId = "media";
    private static readonly TimeSpan DormantAfterPause = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private SessionManager? _manager;
    private Session? _session;
    private Timer? _debounce;
    private Timer? _dormancy;
    private string? _artKey;
    private byte[]? _art;
    private NexusColor? _artAccent;
    private DateTimeOffset? _pausedSince;
    private MediaContent? _last;

    public override string Id => "media";
    public override string DisplayName => "Media";
    public override string Description => "Now playing from Spotify, browsers and any app using Windows media controls.";

    protected override async Task OnStartAsync(CancellationToken ct)
    {
        try
        {
            _manager = await SessionManager.RequestAsync().AsTask(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(new ProviderStatus(ProviderState.Unavailable, "Windows media session API is unavailable."));
            Log.Warn(Id, "GSMTC unavailable", ex);
            return;
        }
        _debounce = new Timer(_ => GuardAsync("refresh", RefreshAsync), null, Timeout.Infinite, Timeout.Infinite);
        _dormancy = new Timer(_ => ScheduleRefresh(), null, Timeout.Infinite, Timeout.Infinite);
        _manager.CurrentSessionChanged += (_, _) => ScheduleRefresh();
        _manager.SessionsChanged += (_, _) => ScheduleRefresh();
        ScheduleRefresh();
    }

    protected override ValueTask OnStopAsync()
    {
        DetachSession();
        _debounce?.Dispose();
        _dormancy?.Dispose();
        _manager = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Metadata, playback and thumbnail often change together; coalesce into one refresh.</summary>
    private void ScheduleRefresh() => _debounce?.Change(70, Timeout.Infinite);

    private async Task RefreshAsync()
    {
        if (_manager is null || Stopping.IsCancellationRequested) return;
        await _refreshGate.WaitAsync(Stopping).ConfigureAwait(false);
        try
        {
            var session = PickSession(_manager);
            if (!ReferenceEquals(session, _session))
            {
                DetachSession();
                _session = session;
                if (session is not null)
                {
                    session.MediaPropertiesChanged += OnSessionChanged;
                    session.PlaybackInfoChanged += OnSessionChanged;
                    session.TimelinePropertiesChanged += OnTimelineChanged;
                }
            }

            if (session is null) { Clear(); return; }

            GlobalSystemMediaTransportControlsSessionMediaProperties? props;
            try { props = await session.TryGetMediaPropertiesAsync().AsTask(Stopping).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { props = null; Log.Debug(Id, "Properties unavailable: " + ex.Message); }

            if (props is null || string.IsNullOrWhiteSpace(props.Title)) { Clear(); return; }

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var source = FriendlyAppName(session.SourceAppUserModelId);

            var key = $"{session.SourceAppUserModelId}|{props.Title}|{props.Artist}|{props.AlbumTitle}";
            if (key != _artKey)
            {
                var art = await ReadThumbnailAsync(props).ConfigureAwait(false);
                _art = art;
                _artAccent = art is null ? null : ArtworkProcessor.ExtractAccent(art);
                // Apps often publish metadata first and artwork a moment later: only cache once we have artwork.
                _artKey = art is null ? null : key;
            }

            var state = playback?.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlayback.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlayback.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlayback.Stopped,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlayback.Changing,
                _ => MediaPlayback.Unknown,
            };

            var duration = timeline.EndTime - timeline.StartTime;
            var sampled = timeline.LastUpdatedTime;
            if (sampled.Year < 2000 || sampled > DateTimeOffset.UtcNow.AddMinutes(1)) sampled = DateTimeOffset.UtcNow;
            var controls = playback?.Controls;

            // Shuffle and repeat belong to music players. Browsers do not offer them to Windows at all, and an app
            // that plays both (Media Player) says when the current item is a video.
            bool isVideo = (playback?.PlaybackType ?? props.PlaybackType) == MediaPlaybackType.Video;
            bool canShuffle = !isVideo && (controls?.IsShuffleEnabled ?? false);
            bool canRepeat = !isVideo && (controls?.IsRepeatEnabled ?? false);
            var repeat = playback?.AutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.List => MediaRepeat.List,
                MediaPlaybackAutoRepeatMode.Track => MediaRepeat.Track,
                _ => MediaRepeat.None,
            };

            var content = new MediaContent(
                Title: props.Title.Trim(),
                Artist: string.IsNullOrWhiteSpace(props.Artist) ? (props.AlbumArtist ?? string.Empty) : props.Artist,
                Album: props.AlbumTitle ?? string.Empty,
                SourceApp: source,
                Playback: state,
                Position: timeline.Position - timeline.StartTime,
                Duration: duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                PositionSampledAt: sampled,
                Artwork: _art,
                ArtworkKey: _artKey ?? key,
                ArtworkAccent: _artAccent,
                CanPrevious: controls?.IsPreviousEnabled ?? false,
                CanNext: controls?.IsNextEnabled ?? false,
                CanTogglePlay: (controls?.IsPlayPauseToggleEnabled ?? false) || (controls?.IsPlayEnabled ?? false) || (controls?.IsPauseEnabled ?? false),
                CanSeek: controls?.IsPlaybackPositionEnabled ?? false,
                Controls: new SessionControls(session, ScheduleRefresh),
                CanShuffle: canShuffle,
                Shuffle: canShuffle && (playback?.IsShuffleActive ?? false),
                CanRepeat: canRepeat,
                Repeat: canRepeat ? repeat : MediaRepeat.None);

            Publish(content);
        }
        finally { _refreshGate.Release(); }
    }

    private void Publish(MediaContent content)
    {
        var now = DateTimeOffset.UtcNow;
        bool playing = content.Playback is MediaPlayback.Playing or MediaPlayback.Changing;
        if (playing) _pausedSince = null;
        else _pausedSince ??= now;

        bool dormant = _pausedSince is { } p && now - p >= DormantAfterPause;
        if (!playing && !dormant && _pausedSince is { } since)
            _dormancy?.Change(DormantAfterPause - (now - since) + TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);

        _last = content;
        var accent = content.ArtworkAccent;
        Post(new Activity
        {
            Id = ActivityId,
            SourceId = Id,
            Priority = ActivityPriority.Low,
            Category = ActivityCategory.Media,
            Content = content,
            Accent = accent,
            IsDormant = dormant,
            Indicator = playing ? new IndicatorSpec(Glyphs.Music, accent ?? SemanticColors.Neutral, content.Title) : null,
        });
    }

    private void Clear()
    {
        _last = null;
        _pausedSince = null;
        Remove(ActivityId);
    }

    private void OnSessionChanged(Session sender, object args) => ScheduleRefresh();

    /// <summary>Timeline events can arrive every second; only republish when the position actually jumped (seek).</summary>
    private void OnTimelineChanged(Session sender, TimelinePropertiesChangedEventArgs args) => Guard("timeline", () =>
    {
        var last = _last;
        if (last is null) { ScheduleRefresh(); return; }
        var t = sender.GetTimelineProperties();
        var actual = t.Position - t.StartTime;
        var expected = last.EstimatePosition(DateTimeOffset.UtcNow);
        var duration = t.EndTime - t.StartTime;
        if (Math.Abs((actual - expected).TotalSeconds) > 1.5 || Math.Abs((duration - last.Duration).TotalSeconds) > 1) ScheduleRefresh();
    });

    private void DetachSession()
    {
        if (_session is null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch { /* the source app may already be gone */ }
        _session = null;
    }

    /// <summary>Prefer whatever is actually playing; fall back to Windows' "current" session.</summary>
    private static Session? PickSession(SessionManager manager)
    {
        try
        {
            var current = manager.GetCurrentSession();
            if (IsPlaying(current)) return current;
            var playing = manager.GetSessions().FirstOrDefault(IsPlaying);
            return playing ?? current ?? manager.GetSessions().FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool IsPlaying(Session? s)
    {
        try { return s?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail is null) return null;
        try
        {
            using var stream = await props.Thumbnail.OpenReadAsync().AsTask().ConfigureAwait(false);
            if (stream.Size == 0 || stream.Size > (ulong)ArtworkProcessor.MaxBytes) return null;
            using var s = stream.AsStreamForRead();
            using var ms = new MemoryStream((int)stream.Size);
            await s.CopyToAsync(ms).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Log.Debug("media", "Thumbnail unavailable: " + ex.Message);
            return null;
        }
    }

    internal static string FriendlyAppName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "Media";
        var a = aumid.ToLowerInvariant();
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("msedge")) return "Microsoft Edge";
        if (a.Contains("chrome")) return "Google Chrome";
        if (a.Contains("firefox") || a.Contains("308046b0af4a39cb")) return "Firefox";
        if (a.Contains("zunemusic")) return "Media Player";
        if (a.Contains("zunevideo")) return "Movies & TV";
        if (a.Contains("music.youtube")) return "YouTube Music";
        if (a.Contains("vlc")) return "VLC";
        if (a.Contains("brave")) return "Brave";
        if (a.Contains("opera")) return "Opera";
        var name = aumid.Split('!')[0].Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length == 0 ? "Media" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private sealed class SessionControls : IMediaControls
    {
        private readonly Session _session;
        private readonly Action _refresh;

        public SessionControls(Session session, Action refresh)
        {
            _session = session;
            _refresh = refresh;
        }

        public Task TogglePlayPauseAsync() => Try(() => _session.TryTogglePlayPauseAsync().AsTask());
        public Task NextAsync() => Try(() => _session.TrySkipNextAsync().AsTask());
        public Task PreviousAsync() => Try(() => _session.TrySkipPreviousAsync().AsTask());
        public Task SeekAsync(TimeSpan position) => Try(() => _session.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

        // The widget shows the new mode before the app confirms it. When the app refuses, nothing else would
        // correct that, so read the real state back.
        public Task SetShuffleAsync(bool on) => Try(() => _session.TryChangeShuffleActiveAsync(on).AsTask(), _refresh);

        public Task SetRepeatAsync(MediaRepeat mode) => Try(() => _session.TryChangeAutoRepeatModeAsync(mode switch
        {
            MediaRepeat.List => MediaPlaybackAutoRepeatMode.List,
            MediaRepeat.Track => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None,
        }).AsTask(), _refresh);

        private static async Task Try(Func<Task<bool>> op, Action? onRefused = null)
        {
            bool done = false;
            try { done = await op().ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("media", "Command failed (app may have closed): " + ex.Message); }
            if (!done) onRefused?.Invoke();
        }
    }
}
