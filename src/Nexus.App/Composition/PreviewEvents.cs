using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;

namespace Nexus.App.Composition;

/// <summary>
/// Design lab: posts clearly-labelled simulated activities so themes, motion and the priority engine can be
/// tried without waiting for real events. Nothing here touches the system.
/// </summary>
public sealed class PreviewEvents
{
    private const string Source = "preview";
    private readonly ActivityHub _hub;
    private bool _mic, _download;

    public PreviewEvents(ActivityHub hub) => _hub = hub;

    /// <summary>Shows the File Shelf drop zone for a few seconds (no real drag involved).</summary>
    public Action? ShowDropZone { get; init; }
    /// <summary>Adds sample items that exist on every PC to the shelf. They are never saved to shelf.json.</summary>
    public Action? AddShelfSamples { get; init; }

    public static readonly string[] Names =
    {
        "Now playing", "Volume", "Notification", "Charging", "Critical battery", "Meeting starting",
        "Download complete", "Bluetooth", "Screenshot", "Microphone (toggle)", "Download running (toggle)",
        "Shelf: drop zone", "Shelf: sample items",
    };

    public void Run(string name)
    {
        switch (name)
        {
            case "Shelf: drop zone":
                ShowDropZone?.Invoke();
                break;
            case "Shelf: sample items":
                AddShelfSamples?.Invoke();
                break;
            case "Notification":
                _hub.Pulse(new PulseSignal(SemanticColors.Notification));
                _hub.Post(new Activity
                {
                    Id = "preview.notification." + Environment.TickCount64, SourceId = Source, Priority = ActivityPriority.Medium,
                    Category = ActivityCategory.Notification, Duration = TimeSpan.FromSeconds(5.5), Accent = SemanticColors.Notification,
                    Content = new NotificationContent("Microsoft Teams", null, "Aldi", "Meeting moved to 15:00", DateTimeOffset.Now, Glyphs.People),
                    Actions = new[] { new ActivityAction("open", "Open", Glyphs.Open, () => Task.CompletedTask, IsPrimary: true) },
                });
                break;
            case "Volume":
                _hub.Post(new Activity
                {
                    Id = "preview.volume", SourceId = Source, Priority = ActivityPriority.Low, Category = ActivityCategory.Feedback,
                    Duration = TimeSpan.FromSeconds(1.6),
                    Content = new VolumeContent(0.25f + Random.Shared.NextSingle() * 0.6f, false, "Preview speakers", AudioDeviceKind.Speakers, NoVolume.Instance),
                });
                break;
            case "Charging":
                _hub.Pulse(new PulseSignal(SemanticColors.Energy, PulseStyle.Sweep));
                _hub.Post(new Activity
                {
                    Id = "preview.battery", SourceId = Source, Priority = ActivityPriority.Low, Category = ActivityCategory.Power,
                    Duration = TimeSpan.FromSeconds(3.2), Accent = SemanticColors.Energy,
                    Content = new BatteryContent(83, PowerState.Charging, BatteryBand.Normal, TimeSpan.FromMinutes(47), false),
                });
                break;
            case "Critical battery":
                _hub.Post(new Activity
                {
                    Id = "preview.critical", SourceId = Source, Priority = ActivityPriority.Critical, Category = ActivityCategory.Power,
                    PreferExpanded = true, Duration = TimeSpan.FromSeconds(10), Accent = SemanticColors.Critical,
                    Content = new BatteryContent(4, PowerState.Discharging, BatteryBand.Critical, TimeSpan.FromMinutes(6), false),
                    Actions = new[] { new ActivityAction("dismiss", "Dismiss", Glyphs.Close, () => { _hub.Remove("preview.critical"); return Task.CompletedTask; }, IsPrimary: true) },
                });
                break;
            case "Meeting starting":
                _hub.Pulse(new PulseSignal(SemanticColors.Meeting));
                _hub.Post(new Activity
                {
                    Id = "preview.meeting", SourceId = Source, Priority = ActivityPriority.High, Category = ActivityCategory.Meeting,
                    PreferExpanded = true, Duration = TimeSpan.FromSeconds(10), Accent = SemanticColors.Meeting,
                    Content = new MeetingContent("Infrastructure Weekly", "Microsoft Teams", MeetingPhase.Starting,
                        DateTimeOffset.Now.AddMinutes(1), DateTimeOffset.Now.AddMinutes(31), null, null, false, false, false),
                    Actions = new[] { new ActivityAction("join", "Join", Glyphs.Video, () => Task.CompletedTask, IsPrimary: true) },
                });
                break;
            case "Download complete":
                _hub.Pulse(new PulseSignal(SemanticColors.Transfer));
                _hub.Post(new Activity
                {
                    Id = "preview.download", SourceId = Source, Priority = ActivityPriority.Medium, Category = ActivityCategory.Transfer,
                    Duration = TimeSpan.FromSeconds(4.5), Accent = SemanticColors.Transfer,
                    Content = new GenericContent(Glyphs.Download, "Download complete", "Q4-infrastructure-review.pdf", 1, false, SemanticColors.Transfer),
                });
                break;
            case "Bluetooth":
                _hub.Pulse(new PulseSignal(SemanticColors.Bluetooth));
                _hub.Post(Glance("preview.bt", Glyphs.Headphones, "WH-1000XM5", SemanticColors.Bluetooth, "Connected"));
                break;
            case "Screenshot":
                _hub.Pulse(new PulseSignal(SemanticColors.Capture, PulseStyle.Flash));
                _hub.Post(Glance("preview.capture", Glyphs.Screenshot, "Screenshot captured", SemanticColors.Capture, "Copied to clipboard"));
                break;
            case "Microphone (toggle)":
                _mic = !_mic;
                if (!_mic) { _hub.Remove("preview.mic"); break; }
                _hub.Pulse(new PulseSignal(SemanticColors.Privacy));
                _hub.Post(new Activity
                {
                    Id = "preview.mic", SourceId = Source, Priority = ActivityPriority.High, Category = ActivityCategory.Privacy,
                    CanBeForeground = false, Accent = SemanticColors.Privacy,
                    Content = new PrivacyContent(PrivacyResource.Microphone, new[] { new PrivacyUser("Microsoft Teams", null) }),
                    Indicator = new IndicatorSpec(Glyphs.Microphone, SemanticColors.Privacy, "Microphone · Microsoft Teams"),
                });
                _hub.Post(Glance("preview.mic.event", Glyphs.Microphone, "Microsoft Teams", SemanticColors.Privacy, "Microphone in use"));
                break;
            case "Download running (toggle)":
                _download = !_download;
                if (!_download) { _hub.Remove("preview.downloading"); break; }
                _hub.Post(new Activity
                {
                    Id = "preview.downloading", SourceId = Source, Priority = ActivityPriority.Low, Category = ActivityCategory.Transfer,
                    CanBeForeground = false,
                    Content = new GenericContent(Glyphs.Download, "Downloading", null, null, true, SemanticColors.Transfer),
                    Indicator = new IndicatorSpec(Glyphs.Download, SemanticColors.Transfer, "Downloading setup.msi", null, true),
                });
                break;
            case "Now playing":
                var red = NexusColor.FromHex("#E0465A").Ambientize();
                _hub.Post(new Activity
                {
                    Id = "preview.media", SourceId = Source, Priority = ActivityPriority.Low, Category = ActivityCategory.Media,
                    Duration = TimeSpan.FromSeconds(6), Accent = red,
                    Content = new MediaContent("Blinding Lights", "The Weeknd", "After Hours", "NEXUS Preview lab", MediaPlayback.Playing,
                        TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(200), DateTimeOffset.UtcNow, null, "preview",
                        red, true, true, true, false, NoMedia.Instance,
                        CanShuffle: true, Shuffle: true, CanRepeat: true, Repeat: MediaRepeat.None),
                });
                break;
        }
    }

    private static Activity Glance(string id, string glyph, string label, NexusColor color, string detail) => new()
    {
        Id = id, SourceId = Source, Priority = ActivityPriority.Low, Category = ActivityCategory.Connectivity,
        Duration = TimeSpan.FromSeconds(2.4), Accent = color, Content = new GlanceContent(glyph, label, color, detail),
    };

    private sealed class NoVolume : IVolumeControls
    {
        public static readonly NoVolume Instance = new();
        public void SetLevel(float level) { }
        public void SetMuted(bool muted) { }
        public void Step(int deltaPercent) { }
    }

    private sealed class NoMedia : IMediaControls
    {
        public static readonly NoMedia Instance = new();
        public Task TogglePlayPauseAsync() => Task.CompletedTask;
        public Task NextAsync() => Task.CompletedTask;
        public Task PreviousAsync() => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
    }
}
