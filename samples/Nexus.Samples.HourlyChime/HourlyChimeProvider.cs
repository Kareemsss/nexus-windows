using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Plugins;

namespace Nexus.Samples.HourlyChime;

/// <summary>
/// Minimal plugin: a soft edge pulse and a short glance at every full hour.
/// Shows the whole provider contract: start/stop, timers, posting a transient activity, pulsing.
/// No UI code is needed – the built-in Glance widget renders the content.
/// </summary>
public sealed class HourlyChimeProvider : ProviderBase
{
    private Timer? _timer;

    public override string Id => "sample.hourly-chime";
    public override string DisplayName => "Hourly chime (sample plugin)";
    public override string Description => "A gentle pulse and the time at every full hour.";

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _timer = new Timer(_ => Guard("chime", Chime), null, UntilNextHour(), Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    protected override ValueTask OnStopAsync()
    {
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Chime()
    {
        var color = NexusColor.FromHex("#9B8CFF");
        Pulse(new PulseSignal(color));
        Post(new Activity
        {
            Id = Id + ".chime",
            SourceId = Id,
            Priority = ActivityPriority.Low,
            Category = ActivityCategory.Notification, // respects Quiet mode automatically
            Duration = TimeSpan.FromSeconds(3),
            Accent = color,
            Content = new GlanceContent(Glyphs.Clock, DateTime.Now.ToString("HH:00"), color, "New hour"),
        });
        _timer?.Change(UntilNextHour(), Timeout.InfiniteTimeSpan);
    }

    private static TimeSpan UntilNextHour()
    {
        var now = DateTime.Now;
        return now.Date.AddHours(now.Hour + 1) - now + TimeSpan.FromMilliseconds(200);
    }
}
