using System;
using Nexus.Core.Activities;
using Nexus.Core.Scheduling;
using Nexus.Core.State;
using Xunit;

namespace Nexus.Core.Tests;

public class StateMachineTests
{
    private readonly DateTimeOffset _t0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static Arbitration With(TrackedActivity? fg) => new(fg, Array.Empty<TrackedActivity>(), false, null);

    private TrackedActivity Tracked(string id, ActivityPriority p, bool preferExpanded = false) =>
        new(A.Make(id, p, preferExpanded: preferExpanded), _t0, _t0, _t0, _t0, 1, 0);

    [Fact]
    public void Idle_without_activities_and_peek_on_hover()
    {
        var m = new IslandStateMachine();
        m.Apply(With(null), _t0);
        Assert.Equal(IslandMode.Idle, m.State.Mode);
        m.PointerEnter(_t0);
        Assert.Equal(IslandMode.Peek, m.State.Mode);
        m.PointerLeave(_t0);
        Assert.Equal(IslandMode.Idle, m.State.Mode);
    }

    [Fact]
    public void Click_expands_then_opens_center_and_collapse_closes()
    {
        var m = new IslandStateMachine();
        m.Apply(With(Tracked("media", ActivityPriority.Low)), _t0);
        Assert.Equal(IslandMode.Compact, m.State.Mode);
        m.PrimaryClick(_t0);
        Assert.Equal(IslandMode.Expanded, m.State.Mode);
        m.PrimaryClick(_t0);
        Assert.Equal(IslandMode.Center, m.State.Mode);
        m.PrimaryClick(_t0);
        Assert.Equal(IslandMode.Center, m.State.Mode); // clicks inside the center operate its cards
        m.Collapse(_t0);
        Assert.Equal(IslandMode.Compact, m.State.Mode);
    }

    [Fact]
    public void Expanded_collapses_after_pointer_leaves_and_delay_passes()
    {
        var m = new IslandStateMachine { AutoCollapseDelay = TimeSpan.FromSeconds(2) };
        m.Apply(With(Tracked("media", ActivityPriority.Low)), _t0);
        m.PointerEnter(_t0);
        m.PrimaryClick(_t0);
        m.PointerLeave(_t0);
        Assert.Equal(_t0.AddSeconds(2), m.Deadline);
        m.Tick(_t0.AddSeconds(1));
        Assert.Equal(IslandMode.Expanded, m.State.Mode);
        m.Tick(_t0.AddSeconds(2.1));
        Assert.Equal(IslandMode.Compact, m.State.Mode);
    }

    [Fact]
    public void Re_entering_cancels_collapse()
    {
        var m = new IslandStateMachine { AutoCollapseDelay = TimeSpan.FromSeconds(2) };
        m.Apply(With(Tracked("media", ActivityPriority.Low)), _t0);
        m.PrimaryClick(_t0);
        m.PointerLeave(_t0);
        m.PointerEnter(_t0.AddSeconds(1));
        m.Tick(_t0.AddSeconds(3));
        Assert.Equal(IslandMode.Expanded, m.State.Mode);
    }

    [Fact]
    public void Critical_is_expanded_and_flagged_as_alert()
    {
        var m = new IslandStateMachine();
        m.Apply(With(Tracked("battery", ActivityPriority.Critical)), _t0);
        Assert.Equal(IslandMode.Expanded, m.State.Mode);
        Assert.True(m.State.Alert);
    }

    [Fact]
    public void User_expansion_belongs_to_one_activity()
    {
        var m = new IslandStateMachine();
        m.Apply(With(Tracked("media", ActivityPriority.Low)), _t0);
        m.PrimaryClick(_t0);
        Assert.Equal(IslandMode.Expanded, m.State.Mode);
        m.Apply(With(Tracked("volume", ActivityPriority.Low)), _t0);
        Assert.Equal(IslandMode.Compact, m.State.Mode);
    }

    [Fact]
    public void Hover_expand_behavior_expands_after_dwell()
    {
        var m = new IslandStateMachine { HoverBehavior = HoverBehavior.ExpandOnHover, HoverExpandDelay = TimeSpan.FromMilliseconds(400) };
        m.Apply(With(Tracked("media", ActivityPriority.Low)), _t0);
        m.PointerEnter(_t0);
        Assert.Equal(IslandMode.Compact, m.State.Mode);
        m.Tick(_t0.AddMilliseconds(450));
        Assert.Equal(IslandMode.Expanded, m.State.Mode);
    }

    [Fact]
    public void Drag_opens_drop_zone_and_leave_returns_to_idle_after_grace()
    {
        var m = new IslandStateMachine { DragLeaveGrace = TimeSpan.FromMilliseconds(150) };
        m.Apply(With(null), _t0);
        m.DragEnter(_t0);
        Assert.Equal(IslandMode.Drop, m.State.Mode);

        m.DragLeave(_t0.AddSeconds(1));
        Assert.Equal(IslandMode.Drop, m.State.Mode); // not yet: the leave may be a move between child elements
        Assert.Equal(_t0.AddMilliseconds(1150), m.Deadline);
        m.Tick(_t0.AddSeconds(1.2));
        Assert.Equal(IslandMode.Idle, m.State.Mode);
        Assert.False(m.IsDragging);
    }

    [Fact]
    public void Leave_followed_by_enter_keeps_the_drop_zone_open()
    {
        var m = new IslandStateMachine();
        m.Apply(With(null), _t0);
        m.DragEnter(_t0);
        m.DragLeave(_t0.AddMilliseconds(500));
        m.DragEnter(_t0.AddMilliseconds(510));
        m.Tick(_t0.AddSeconds(1));
        Assert.Equal(IslandMode.Drop, m.State.Mode);
    }

    [Fact]
    public void Drop_returns_to_the_activity_shown_before()
    {
        var m = new IslandStateMachine();
        m.Apply(With(Tracked("media", ActivityPriority.Low)), _t0);
        Assert.Equal(IslandMode.Compact, m.State.Mode);
        m.DragEnter(_t0);
        Assert.Equal(IslandMode.Drop, m.State.Mode);
        m.Dropped(_t0.AddSeconds(1));
        Assert.Equal(IslandMode.Compact, m.State.Mode);
        Assert.Null(m.Deadline);
    }

    [Fact]
    public void Drag_keeps_a_user_expansion_for_afterwards()
    {
        var m = new IslandStateMachine();
        m.Apply(With(Tracked("media", ActivityPriority.Low)), _t0);
        m.PrimaryClick(_t0);
        Assert.Equal(IslandMode.Expanded, m.State.Mode);
        m.DragEnter(_t0);
        Assert.Equal(IslandMode.Drop, m.State.Mode);
        m.Dropped(_t0);
        Assert.Equal(IslandMode.Expanded, m.State.Mode);
    }

    [Fact]
    public void Center_stays_open_while_files_are_dragged_into_it()
    {
        var m = new IslandStateMachine();
        m.OpenCenter(_t0);
        m.DragEnter(_t0);
        Assert.Equal(IslandMode.Center, m.State.Mode);
        m.Dropped(_t0);
        Assert.Equal(IslandMode.Center, m.State.Mode);
    }

    [Fact]
    public void Hidden_island_ignores_drags()
    {
        var m = new IslandStateMachine();
        m.Apply(new Arbitration(null, Array.Empty<TrackedActivity>(), true, null), _t0);
        m.DragEnter(_t0);
        Assert.Equal(IslandMode.Hidden, m.State.Mode);
        Assert.False(m.IsDragging);
    }

    [Fact]
    public void Safety_timeout_closes_a_drag_whose_leave_was_lost()
    {
        var m = new IslandStateMachine { DragTimeout = TimeSpan.FromSeconds(10) };
        m.Apply(With(null), _t0);
        m.DragEnter(_t0);
        m.DragOver(_t0.AddSeconds(5)); // still over the island: the timeout restarts
        m.Tick(_t0.AddSeconds(12));
        Assert.Equal(IslandMode.Drop, m.State.Mode);
        m.Tick(_t0.AddSeconds(15.1)); // no DragOver for 10 s: the source app is gone
        Assert.Equal(IslandMode.Idle, m.State.Mode);
    }

    [Fact]
    public void Hidden_policy_wins_over_center()
    {
        var m = new IslandStateMachine();
        m.OpenCenter(_t0);
        Assert.Equal(IslandMode.Center, m.State.Mode);
        m.Apply(new Arbitration(null, Array.Empty<TrackedActivity>(), true, null), _t0);
        Assert.Equal(IslandMode.Hidden, m.State.Mode);
    }
}
