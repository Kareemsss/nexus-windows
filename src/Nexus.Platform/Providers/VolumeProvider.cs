using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Plugins;
using Nexus.Platform.Services;

namespace Nexus.Platform.Providers;

/// <summary>System volume and output device changes via Core Audio notifications.</summary>
public sealed class VolumeProvider : ProviderBase, IVolumeControls
{
    public const string StatusId = "volume.status";
    private readonly PlatformServices _services;
    private AudioEndpointMonitor? _monitor;
    private DateTimeOffset _quietUntil;

    public VolumeProvider(PlatformServices services) => _services = services;

    public override string Id => "volume";
    public override string DisplayName => "Volume";
    public override string Description => "Shows volume, mute and output-device changes. Scroll over the island to adjust.";

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _monitor = _services.RenderAudio;
        if (_monitor is null)
        {
            SetStatus(new ProviderStatus(ProviderState.Unavailable, "Windows audio service is not available."));
            return Task.CompletedTask;
        }
        _quietUntil = DateTimeOffset.UtcNow.AddSeconds(1); // ignore the burst of notifications at attach time
        _monitor.Changed += OnChanged;
        PublishStatus(_monitor.Current);
        return Task.CompletedTask;
    }

    protected override ValueTask OnStopAsync()
    {
        if (_monitor is not null) _monitor.Changed -= OnChanged;
        return ValueTask.CompletedTask;
    }

    private void OnChanged(AudioEndpointSnapshot? s, AudioChange reason) => Guard("volume change", () =>
    {
        PublishStatus(s);
        if (s is null)
        {
            Post(Glance("audio.device", Glyphs.Mute, "No audio output", SemanticColors.Amber, null));
            return;
        }
        if (DateTimeOffset.UtcNow < _quietUntil) return;

        if (reason == AudioChange.Volume)
        {
            Post(new Activity
            {
                Id = "volume",
                SourceId = Id,
                Priority = ActivityPriority.Low,
                Category = ActivityCategory.Feedback,
                Duration = TimeSpan.FromSeconds(1.6),
                RestartOnUpdate = true,
                Content = ToContent(s),
            });
        }
        else if (reason == AudioChange.Device)
        {
            bool personal = s.Kind is AudioDeviceKind.Bluetooth or AudioDeviceKind.Headphones or AudioDeviceKind.Headset;
            var color = s.Kind == AudioDeviceKind.Bluetooth ? SemanticColors.Bluetooth : SemanticColors.Neutral;
            Pulse(new PulseSignal(color));
            Post(Glance("audio.device", personal ? Glyphs.Headphones : Glyphs.Speakers, s.Name, color, "Audio output"));
        }
    });

    private Activity Glance(string id, string glyph, string label, NexusColor color, string? detail) => new()
    {
        Id = id,
        SourceId = Id,
        Priority = ActivityPriority.Low,
        Category = ActivityCategory.Connectivity,
        Duration = TimeSpan.FromSeconds(2.4),
        Content = new GlanceContent(glyph, label, color, detail),
        Accent = color,
    };

    private void PublishStatus(AudioEndpointSnapshot? s)
    {
        if (s is null) { Remove(StatusId); return; }
        Post(new Activity
        {
            Id = StatusId,
            SourceId = Id,
            Priority = ActivityPriority.Low,
            IsDormant = true,
            Content = ToContent(s),
        });
    }

    private VolumeContent ToContent(AudioEndpointSnapshot s) => new(s.Level, s.Muted, s.Name, s.Kind, this);

    public void SetLevel(float level) => _monitor?.SetLevel(level);
    public void SetMuted(bool muted) => _monitor?.SetMuted(muted);
    public void Step(int deltaPercent) => _monitor?.Step(deltaPercent);
}
