using System;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Scheduling;
using Xunit;

namespace Nexus.Core.Tests;

public class ArbiterTests
{
    private readonly ManualTime _time = new();
    private ActivityHub NewHub() => new(null, _time);

    private Arbitration Run(ActivityHub hub, string? current = null, ArbitrationPolicy? policy = null)
    {
        var arb = ActivityArbiter.Arbitrate(hub.Snapshot(), current, policy ?? ArbitrationPolicy.Default, _time.Now);
        if (arb.Foreground is { ShownAt: null } fg) hub.MarkShown(fg.Id, _time.Now);
        return arb;
    }

    [Fact]
    public void Empty_hub_is_idle()
    {
        var arb = Run(NewHub());
        Assert.Null(arb.Foreground);
        Assert.Empty(arb.Indicators);
    }

    [Fact]
    public void Volume_temporarily_covers_music_then_music_returns()
    {
        var hub = NewHub();
        hub.Post(A.Make("media", ActivityPriority.Low, c: ActivityCategory.Media));
        Assert.Equal("media", Run(hub).Foreground?.Id);

        hub.Post(A.Make("volume", ActivityPriority.Low, TimeSpan.FromSeconds(1.6), ActivityCategory.Feedback));
        Assert.Equal("volume", Run(hub, "media").Foreground?.Id);

        _time.Advance(TimeSpan.FromSeconds(2));
        hub.Prune(_time.Now);
        Assert.Equal("media", Run(hub, "volume").Foreground?.Id);
    }

    [Fact]
    public void Critical_overrides_everything()
    {
        var hub = NewHub();
        hub.Post(A.Make("call", ActivityPriority.High, TimeSpan.FromSeconds(30)));
        hub.Post(A.Make("battery", ActivityPriority.Critical));
        Assert.Equal("battery", Run(hub, "call").Foreground?.Id);
    }

    [Fact]
    public void Equal_priority_events_queue_fifo_and_do_not_steal_the_stage()
    {
        var hub = NewHub();
        hub.Post(A.Make("n1", ActivityPriority.Medium, TimeSpan.FromSeconds(5), ActivityCategory.Notification));
        Assert.Equal("n1", Run(hub).Foreground?.Id);

        _time.Advance(TimeSpan.FromSeconds(1));
        hub.Post(A.Make("n2", ActivityPriority.Medium, TimeSpan.FromSeconds(5), ActivityCategory.Notification));
        Assert.Equal("n1", Run(hub, "n1").Foreground?.Id); // sticky: n2 waits

        _time.Advance(TimeSpan.FromSeconds(4.5));
        hub.Prune(_time.Now);
        Assert.Equal("n2", Run(hub, "n1").Foreground?.Id); // n1 expired; n2's own countdown starts now

        _time.Advance(TimeSpan.FromSeconds(4));
        hub.Prune(_time.Now);
        Assert.Equal("n2", Run(hub, "n2").Foreground?.Id); // still within its 5 s
    }

    [Fact]
    public void Low_value_events_are_dropped_if_they_cannot_be_shown_quickly()
    {
        var hub = NewHub();
        hub.Post(A.Make("notif", ActivityPriority.Medium, TimeSpan.FromSeconds(5), ActivityCategory.Notification));
        Run(hub);
        hub.Post(A.Make("glance", ActivityPriority.Low, TimeSpan.FromSeconds(2), ActivityCategory.Connectivity));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(hub.Prune(_time.Now));
        Assert.Null(hub.Find("glance"));
    }

    [Fact]
    public void Privacy_is_indicator_only_and_survives_gaming_mode()
    {
        var hub = NewHub();
        var mic = new IndicatorSpec(Glyphs.Microphone, SemanticColors.Privacy, "Microphone");
        hub.Post(A.Make("mic", ActivityPriority.High, c: ActivityCategory.Privacy, canForeground: false, indicator: mic));
        hub.Post(A.Make("media", ActivityPriority.Low, c: ActivityCategory.Media,
            indicator: new IndicatorSpec(Glyphs.Music, SemanticColors.Neutral, "song")));

        var desktop = Run(hub);
        Assert.Equal("media", desktop.Foreground?.Id);
        Assert.Contains(desktop.Indicators, i => i.Id == "mic");

        var gaming = Run(hub, null, new ArbitrationPolicy(FullscreenActive: true, Fullscreen: FullscreenBehavior.Gaming));
        Assert.Null(gaming.Foreground);                 // media never interrupts a game
        var only = Assert.Single(gaming.Indicators);    // ...but the privacy indicator stays
        Assert.Equal("mic", only.Id);
    }

    [Fact]
    public void Hide_policy_hides_everything()
    {
        var hub = NewHub();
        hub.Post(A.Make("battery", ActivityPriority.Critical));
        var arb = Run(hub, null, new ArbitrationPolicy(FullscreenActive: true, Fullscreen: FullscreenBehavior.Hide));
        Assert.True(arb.Hidden);
    }

    [Fact]
    public void Quiet_mode_suppresses_notifications_but_not_feedback()
    {
        var hub = NewHub();
        hub.Post(A.Make("notif", ActivityPriority.Medium, TimeSpan.FromSeconds(5), ActivityCategory.Notification));
        var quiet = new ArbitrationPolicy(QuietMode: true);
        Assert.Null(Run(hub, null, quiet).Foreground);

        hub.Post(A.Make("volume", ActivityPriority.Low, TimeSpan.FromSeconds(2), ActivityCategory.Feedback));
        Assert.Equal("volume", Run(hub, null, quiet).Foreground?.Id);
    }

    [Fact]
    public void Dormant_activities_are_never_presented()
    {
        var hub = NewHub();
        hub.Post(A.Make("battery.status", ActivityPriority.Medium, dormant: true));
        Assert.Null(Run(hub).Foreground);
        Assert.Equal("battery.status", hub.LatestContent<string>());
    }

    [Fact]
    public void Next_wakeup_is_the_earliest_expiry()
    {
        var hub = NewHub();
        hub.Post(A.Make("v", ActivityPriority.Low, TimeSpan.FromSeconds(1.5)));
        var arb = Run(hub);
        var again = ActivityArbiter.Arbitrate(hub.Snapshot(), arb.Foreground?.Id, ArbitrationPolicy.Default, _time.Now);
        Assert.Equal(_time.Now + TimeSpan.FromSeconds(1.5), again.NextWakeup);
    }

    [Fact]
    public void Updating_a_visible_transient_restarts_its_countdown()
    {
        var hub = NewHub();
        hub.Post(A.Make("volume", ActivityPriority.Low, TimeSpan.FromSeconds(1.6)));
        Run(hub);
        _time.Advance(TimeSpan.FromSeconds(1.2));
        hub.Post(A.Make("volume", ActivityPriority.Low, TimeSpan.FromSeconds(1.6)));
        _time.Advance(TimeSpan.FromSeconds(1.0));
        Assert.False(hub.Prune(_time.Now));
        Assert.NotNull(hub.Find("volume"));
    }
}
