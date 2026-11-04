using Nexus.Core.Activities;
using Nexus.Core.Content;
using Nexus.Core.Settings;
using Nexus.Platform.Providers;

namespace Nexus.App.Composition;

/// <summary>
/// Song pop-ups. Watches the media activity and, when a new track starts playing (or playback resumes after a
/// real pause), posts a short-lived copy of it. The copy is flagged ShowWhenHidden, so it also slides out of a
/// completely hidden island, then disappears by itself. Works for any app using Windows media controls
/// (Spotify, YouTube in a browser, YouTube Music...).
/// </summary>
public sealed class SongPopupService : IDisposable
{
    public const string ActivityId = "media.popup";
    private static readonly TimeSpan ResumeThreshold = TimeSpan.FromSeconds(10);

    private readonly ActivityHub _hub;
    private readonly SettingsStore _settings;
    private string? _lastKey;
    private bool _wasPlaying;
    private DateTimeOffset _pausedAt;

    public SongPopupService(ActivityHub hub, SettingsStore settings)
    {
        _hub = hub;
        _settings = settings;
        _hub.Changed += OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        var b = _settings.Current.Behavior;
        if (!b.SongPopup)
        {
            _lastKey = null;
            if (_hub.Find(ActivityId) is not null) _hub.Remove(ActivityId);
            return;
        }

        var media = _hub.Find(MediaProvider.ActivityId)?.Activity;
        if (media?.Content is not MediaContent m) { _wasPlaying = false; return; }

        var now = _hub.Now;
        bool playing = m.Playback == MediaPlayback.Playing;
        var key = m.SourceApp + "|" + m.Title + "|" + m.Artist;
        bool newTrack = playing && key != _lastKey;
        bool resumed = playing && !_wasPlaying && key == _lastKey && now - _pausedAt > ResumeThreshold;
        if (!playing && _wasPlaying) _pausedAt = now;
        _wasPlaying = playing;

        if (newTrack || resumed)
        {
            _lastKey = key;
            Post(media, m, restart: true, b.SongPopupSeconds);
            return;
        }

        // Keep a visible pop-up in sync (artwork often arrives a moment after the title) without extending it.
        // Posting the same content instance is what stops this from looping on our own Changed event.
        var current = _hub.Find(ActivityId);
        if (current is not null && key == _lastKey && !ReferenceEquals(current.Activity.Content, m))
            Post(media, m, restart: false, b.SongPopupSeconds);
    }

    private void Post(Activity media, MediaContent m, bool restart, double seconds) => _hub.Post(new Activity
    {
        Id = ActivityId,
        SourceId = media.SourceId,                  // disabling the Media module removes pop-ups too
        Priority = ActivityPriority.Low,
        Category = ActivityCategory.Media,
        Duration = TimeSpan.FromSeconds(seconds),
        RestartOnUpdate = restart,
        ShowWhenHidden = true,
        Accent = media.Accent,
        Content = m,
    });

    public void Dispose() => _hub.Changed -= OnChanged;
}
