using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Calendar;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Diagnostics;
using Nexus.Core.Plugins;
using Nexus.Platform.Services;

namespace Nexus.Platform.Providers;

/// <summary>
/// Meeting awareness without vendor lock-in.
///
/// * Active meetings are inferred from microphone usage by a known meeting app (Teams, Zoom, Webex, Slack,
///   Discord, Skype) or by a browser while a calendar meeting is in progress. This works for every platform
///   without credentials. Duration, mic/camera state and a system-wide mic mute are shown.
/// * Upcoming meetings come from an optional ICS feed (Outlook → "Publish calendar" or any ICS URL/file).
///
/// Limitation: Teams has no public local API for its in-app mute button, so "Mute" toggles the Windows
/// microphone endpoint (which silences every app – the safest privacy behaviour anyway).
/// </summary>
public sealed class MeetingProvider : ProviderBase
{
    public const string ActiveId = "meeting.active";
    public const string NextId = "meeting.next";

    private sealed record MeetingApp(string Hint, string Platform, string[] Processes);
    private static readonly MeetingApp[] Apps =
    {
        new("teams", "Microsoft Teams", new[] { "ms-teams", "Teams" }),
        new("zoom", "Zoom", new[] { "Zoom" }),
        new("webex", "Webex", new[] { "CiscoCollabHost", "webex", "atmgr" }),
        new("slack", "Slack", new[] { "slack" }),
        new("discord", "Discord", new[] { "Discord" }),
        new("skype", "Skype", new[] { "Skype" }),
    };
    private static readonly string[] Browsers = { "msedge", "edge", "chrome", "firefox", "brave", "opera", "vivaldi" };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly PlatformServices _services;
    private readonly object _gate = new();
    private readonly HashSet<string> _announcedSoon = new();
    private readonly HashSet<string> _announcedStart = new();
    private IReadOnlyList<CalendarEvent> _events = Array.Empty<CalendarEvent>();
    private DateTimeOffset? _activeSince;
    private Timer? _graceTimer;
    private Timer? _calendarTimer;
    private Timer? _refreshTimer;
    private AudioEndpointMonitor? _capture;
    private MeetingContent? _lastActive;

    public MeetingProvider(PlatformServices services) => _services = services;

    public override string Id => "meetings";
    public override string DisplayName => "Meetings";
    public override string Description => "Active call timer and mic state for Teams, Zoom and others; upcoming meetings from an optional ICS calendar.";

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _graceTimer = new Timer(_ => Guard("grace", () => Evaluate(graceExpired: true)), null, Timeout.Infinite, Timeout.Infinite);
        _calendarTimer = new Timer(_ => Guard("calendar", EvaluateCalendar), null, Timeout.Infinite, Timeout.Infinite);
        _refreshTimer = new Timer(_ => GuardAsync("calendar refresh", LoadCalendarAsync), null, TimeSpan.Zero, TimeSpan.FromMinutes(15));
        Context.Feed.Changed += OnFeedChanged;
        _capture = _services.CaptureAudio;
        if (_capture is not null) _capture.Changed += OnCaptureChanged;
        Evaluate(graceExpired: false);
        return Task.CompletedTask;
    }

    protected override ValueTask OnStopAsync()
    {
        Context.Feed.Changed -= OnFeedChanged;
        if (_capture is not null) _capture.Changed -= OnCaptureChanged;
        _graceTimer?.Dispose(); _calendarTimer?.Dispose(); _refreshTimer?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnFeedChanged(object? sender, EventArgs e) => Guard("feed", () => Evaluate(graceExpired: false));
    private void OnCaptureChanged(AudioEndpointSnapshot? s, AudioChange c) => Guard("capture", () => Evaluate(graceExpired: false));

    #region Active meeting

    private void Evaluate(bool graceExpired)
    {
        var snapshot = Context.Feed.Snapshot();
        var mic = snapshot.FirstOrDefault(a => a.Id == "privacy.mic")?.Activity.Content as PrivacyContent;
        var cam = snapshot.FirstOrDefault(a => a.Id == "privacy.cam")?.Activity.Content as PrivacyContent;
        var now = DateTimeOffset.Now;
        var current = CurrentCalendarMeeting(now);

        MeetingApp? app = null;
        string? platform = null;
        foreach (var user in mic?.Users ?? Array.Empty<PrivacyUser>())
        {
            var text = (user.AppName + " " + user.ExecutablePath).ToLowerInvariant();
            app = Apps.FirstOrDefault(a => text.Contains(a.Hint));
            if (app is not null) { platform = app.Platform; break; }
            if (current is not null && Browsers.Any(text.Contains)) { platform = current.Platform; break; }
        }

        lock (_gate)
        {
            if (platform is null)
            {
                if (_activeSince is null) return;
                if (!graceExpired)
                {
                    // Apps briefly release the microphone when switching devices or muting: wait before ending.
                    _graceTimer?.Change(TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan);
                    return;
                }
                _activeSince = null; _lastActive = null;
                Remove(ActiveId);
                return;
            }

            _graceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            bool started = _activeSince is null;
            _activeSince ??= now;
            if (started)
            {
                Pulse(new PulseSignal(SemanticColors.Meeting));
                if (current is not null) { _announcedSoon.Add(Key(current)); _announcedStart.Add(Key(current)); }
                Remove("meeting.starting");
                Remove("meeting.countdown");
            }
        }

        bool camOn = cam?.Users.Count > 0;
        bool muted = _capture?.Current?.Muted ?? false;
        var title = current is not null && Settings.Behavior.ShowMeetingTitles ? current.Title : $"{platform} call";
        var content = new MeetingContent(title, platform, MeetingPhase.Active, current?.Start, current?.End, _activeSince,
            current?.JoinUrl, true, muted, camOn);

        // The feed also reports our own posts: only republish when something visible changed (no feedback loop).
        lock (_gate)
        {
            if (content == _lastActive) return;
            _lastActive = content;
        }

        var actions = new List<ActivityAction>
        {
            new("mute", muted ? "Unmute mic" : "Mute mic", muted ? Glyphs.MicOff : Glyphs.Microphone, () =>
            {
                _capture?.SetMuted(!muted);
                return Task.CompletedTask;
            }, IsPrimary: true),
        };
        if (app is not null)
        {
            var procs = app.Processes;
            actions.Add(new ActivityAction("open", "Open", Glyphs.Open, () => { ShellService.ActivateProcess(procs); return Task.CompletedTask; }));
        }

        Post(new Activity
        {
            Id = ActiveId,
            SourceId = Id,
            Priority = ActivityPriority.High,
            Category = ActivityCategory.Meeting,
            Content = content,
            Accent = SemanticColors.Meeting,
            Indicator = new IndicatorSpec(Glyphs.People, SemanticColors.Meeting, title),
            Actions = actions,
        });
    }

    #endregion

    #region Calendar

    private async Task LoadCalendarAsync()
    {
        var feed = Settings.Modules.CalendarFeed?.Trim();
        if (string.IsNullOrEmpty(feed))
        {
            lock (_gate) _events = Array.Empty<CalendarEvent>();
            EvaluateCalendar();
            return;
        }

        string ics;
        if (feed.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) feed = "https://" + feed[9..];
        if (feed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            ics = await Http.GetStringAsync(feed, Stopping).ConfigureAwait(false);
        else if (File.Exists(feed))
            ics = await File.ReadAllTextAsync(feed, Stopping).ConfigureAwait(false);
        else
        {
            SetStatus(new ProviderStatus(ProviderState.Running, "Calendar file not found – active call detection still works."));
            return;
        }

        var now = DateTimeOffset.Now;
        var events = IcsCalendar.Occurrences(ics, now.AddHours(-12), now.AddHours(36));
        lock (_gate) _events = events;
        SetStatus(new ProviderStatus(ProviderState.Running, $"Calendar connected · {events.Count(e => e.Start.Date == now.Date)} meetings today"));
        Log.Info(Id, $"Calendar loaded ({events.Count} occurrences in window)");
        EvaluateCalendar();
    }

    private CalendarEvent? CurrentCalendarMeeting(DateTimeOffset now)
    {
        lock (_gate) return _events.FirstOrDefault(e => e.Start - TimeSpan.FromMinutes(5) <= now && now <= e.End + TimeSpan.FromMinutes(10));
    }

    private static string Key(CalendarEvent e) => e.Title + "|" + e.Start.ToUnixTimeSeconds();

    private void EvaluateCalendar()
    {
        var now = DateTimeOffset.Now;
        CalendarEvent? next;
        bool inMeeting;
        lock (_gate)
        {
            next = _events.FirstOrDefault(e => e.End > now && e.Start > now - TimeSpan.FromMinutes(3));
            inMeeting = _activeSince is not null;
        }

        bool showTitle = Settings.Behavior.ShowMeetingTitles;
        string TitleOf(CalendarEvent e) => showTitle ? e.Title : "Meeting";

        if (next is null || next.Start - now > TimeSpan.FromHours(14))
        {
            Remove(NextId); Remove("meeting.countdown");
            _calendarTimer?.Change(TimeSpan.FromMinutes(30), Timeout.InfiniteTimeSpan);
            return;
        }

        var content = new MeetingContent(TitleOf(next), next.Platform, MeetingPhase.Upcoming, next.Start, next.End, null, next.JoinUrl, false, false, false);
        var join = next.JoinUrl is { } url
            ? new[] { new ActivityAction("join", "Join", Glyphs.Video, () => { ShellService.Open(url); return Task.CompletedTask; }, IsPrimary: true) }
            : Array.Empty<ActivityAction>();

        Post(new Activity { Id = NextId, SourceId = Id, Priority = ActivityPriority.Medium, Category = ActivityCategory.Meeting, IsDormant = true, Content = content, Actions = join });

        var until = next.Start - now;
        var key = Key(next);
        if (!inMeeting && until <= TimeSpan.FromMinutes(10) && until > TimeSpan.FromMinutes(1))
        {
            int mins = (int)Math.Ceiling(until.TotalMinutes);
            Post(new Activity
            {
                Id = "meeting.countdown", SourceId = Id, Priority = ActivityPriority.Medium, Category = ActivityCategory.Meeting,
                CanBeForeground = false, Content = content, Actions = join,
                Indicator = new IndicatorSpec(Glyphs.Calendar, SemanticColors.Meeting, $"{TitleOf(next)} · in {mins} min"),
            });
            if (_announcedSoon.Add(key))
            {
                Post(new Activity
                {
                    Id = "meeting.soon", SourceId = Id, Priority = ActivityPriority.Medium, Category = ActivityCategory.Meeting,
                    Duration = TimeSpan.FromSeconds(6), Content = content, Accent = SemanticColors.Meeting, Actions = join,
                });
            }
        }
        else if (!inMeeting && until <= TimeSpan.FromMinutes(1) && until > TimeSpan.FromMinutes(-3))
        {
            Remove("meeting.countdown");
            if (_announcedStart.Add(key))
            {
                Pulse(new PulseSignal(SemanticColors.Meeting));
                Post(new Activity
                {
                    Id = "meeting.starting", SourceId = Id, Priority = ActivityPriority.High, Category = ActivityCategory.Meeting,
                    Duration = TimeSpan.FromSeconds(15), PreferExpanded = true,
                    Content = content with { Phase = MeetingPhase.Starting }, Accent = SemanticColors.Meeting, Actions = join,
                });
            }
        }
        else
        {
            Remove("meeting.countdown");
        }

        // Wake exactly when something can change: every minute inside the 10-minute window, otherwise at T-10.
        var wake = until > TimeSpan.FromMinutes(10) ? until - TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(60 - now.Second + 1);
        if (wake < TimeSpan.FromSeconds(1)) wake = TimeSpan.FromSeconds(1);
        if (wake > TimeSpan.FromMinutes(30)) wake = TimeSpan.FromMinutes(30);
        _calendarTimer?.Change(wake, Timeout.InfiniteTimeSpan);
    }

    #endregion
}
