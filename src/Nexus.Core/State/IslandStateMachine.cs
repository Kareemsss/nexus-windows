using System;
using System.Collections.Generic;
using Nexus.Core.Activities;
using Nexus.Core.Scheduling;

namespace Nexus.Core.State;

public enum IslandMode
{
    Hidden,   // policy (fullscreen) says invisible
    Idle,     // tiny capsule
    Peek,     // idle + hover: glanceable clock & status
    Compact,  // contextual info for the foreground activity
    Expanded, // rich interactive presentation of the foreground activity
    Center,   // the island unfolded into a mini command center
    Drop,     // files are being dragged over the island: it opens into the File Shelf drop zone
}

public enum ClickBehavior { ExpandThenCenter, OpenCenter }
public enum HoverBehavior { Illuminate, ExpandOnHover, None }

/// <summary>What the renderer should draw. <see cref="Alert"/> marks the CriticalAlert presentation.</summary>
public sealed record IslandState(
    IslandMode Mode,
    TrackedActivity? Foreground,
    IReadOnlyList<TrackedActivity> Indicators,
    bool Hovered,
    bool Alert)
{
    public static IslandState Initial { get; } = new(IslandMode.Idle, null, Array.Empty<TrackedActivity>(), false, false);
}

/// <summary>
/// Explicit island state model. Combines the arbiter's verdict (what matters) with user interaction (what the
/// user asked for) into a single <see cref="IslandState"/>. Time is passed in, never read, so it is fully testable.
/// </summary>
public sealed class IslandStateMachine
{
    private enum UserIntent { None, Expanded, Center }

    private Arbitration _arbitration = Arbitration.Empty;
    private UserIntent _intent;
    private string? _intentActivityId;
    private bool _hovered;
    private DateTimeOffset? _collapseAt;
    private DateTimeOffset? _hoverExpandAt;
    private bool _dragging;
    private DateTimeOffset? _dragEndAt;

    public TimeSpan AutoCollapseDelay { get; set; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan CenterAwayTimeout { get; set; } = TimeSpan.FromSeconds(8);
    public TimeSpan HoverExpandDelay { get; set; } = TimeSpan.FromMilliseconds(450);
    /// <summary>
    /// A drag-leave is confirmed only after this grace period. Moving between child elements raises a leave
    /// immediately followed by an enter, which must not close and reopen the drop zone.
    /// </summary>
    public TimeSpan DragLeaveGrace { get; set; } = TimeSpan.FromMilliseconds(150);
    /// <summary>Safety net: OLE can lose the drag-leave when the source app dies mid-drag.</summary>
    public TimeSpan DragTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public ClickBehavior ClickBehavior { get; set; } = ClickBehavior.ExpandThenCenter;
    public HoverBehavior HoverBehavior { get; set; } = HoverBehavior.Illuminate;

    public IslandState State { get; private set; } = IslandState.Initial;

    /// <summary>True while files are being dragged over the island (including the leave grace period).</summary>
    public bool IsDragging => _dragging;

    /// <summary>Earliest time at which <see cref="Tick"/> must be called.</summary>
    public DateTimeOffset? Deadline => Earliest(Earliest(_collapseAt, _hoverExpandAt), _dragEndAt);

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a < b ? a : b);

    public string? ForegroundId => _arbitration.Foreground?.Id;

    public bool Apply(Arbitration arbitration, DateTimeOffset now)
    {
        _arbitration = arbitration;
        return Recompute(now);
    }

    public bool PointerEnter(DateTimeOffset now)
    {
        _hovered = true;
        _collapseAt = null;
        if (HoverBehavior == HoverBehavior.ExpandOnHover && State.Mode == IslandMode.Compact)
            _hoverExpandAt = now + HoverExpandDelay;
        return Recompute(now);
    }

    public bool PointerLeave(DateTimeOffset now)
    {
        _hovered = false;
        _hoverExpandAt = null;
        if (_intent == UserIntent.Expanded) _collapseAt = now + AutoCollapseDelay;
        else if (_intent == UserIntent.Center) _collapseAt = now + CenterAwayTimeout;
        return Recompute(now);
    }

    public bool PrimaryClick(DateTimeOffset now)
    {
        _collapseAt = null;
        _hoverExpandAt = null;
        switch (State.Mode)
        {
            case IslandMode.Idle:
            case IslandMode.Peek:
                _intent = UserIntent.Center;
                break;
            case IslandMode.Compact:
                if (ClickBehavior == ClickBehavior.OpenCenter) _intent = UserIntent.Center;
                else { _intent = UserIntent.Expanded; _intentActivityId = _arbitration.Foreground?.Id; }
                break;
            case IslandMode.Expanded:
                _intent = UserIntent.Center;
                break;
            case IslandMode.Center:
                // Clicks inside Island Center operate its cards; it closes via Esc, outside click or the menu.
                break;
        }
        return Recompute(now);
    }

    public bool OpenCenter(DateTimeOffset now)
    {
        _intent = UserIntent.Center;
        _collapseAt = null;
        return Recompute(now);
    }

    /// <summary>Drops any user-requested expansion (Esc, click outside, explicit dismiss).</summary>
    public bool Collapse(DateTimeOffset now)
    {
        _intent = UserIntent.None;
        _intentActivityId = null;
        _collapseAt = null;
        _hoverExpandAt = null;
        return Recompute(now);
    }

    /// <summary>Files entered the island: open the drop zone (unless Island Center is open, which keeps its layout).</summary>
    public bool DragEnter(DateTimeOffset now)
    {
        _dragging = true;
        _dragEndAt = now + DragTimeout;
        return Recompute(now);
    }

    /// <summary>The drag is still over the island: keeps the safety timeout from firing.</summary>
    public bool DragOver(DateTimeOffset now)
    {
        if (!_dragging) return DragEnter(now);
        _dragEndAt = now + DragTimeout;
        return false;
    }

    /// <summary>The drag left the island. Confirmed by <see cref="Tick"/> after <see cref="DragLeaveGrace"/>.</summary>
    public bool DragLeave(DateTimeOffset now)
    {
        if (!_dragging) return false;
        var leaveAt = now + DragLeaveGrace;
        if (_dragEndAt is null || leaveAt < _dragEndAt) _dragEndAt = leaveAt;
        return false;
    }

    /// <summary>The files were dropped: close the drop zone and return to what the island showed before.</summary>
    public bool Dropped(DateTimeOffset now)
    {
        _dragging = false;
        _dragEndAt = null;
        return Recompute(now);
    }

    public bool Tick(DateTimeOffset now)
    {
        if (_dragEndAt is { } d && now >= d)
        {
            _dragging = false;
            _dragEndAt = null;
        }
        if (_collapseAt is { } c && now >= c)
        {
            _collapseAt = null;
            if (!_hovered) { _intent = UserIntent.None; _intentActivityId = null; }
        }
        if (_hoverExpandAt is { } h && now >= h)
        {
            _hoverExpandAt = null;
            if (_hovered && State.Mode == IslandMode.Compact)
            {
                _intent = UserIntent.Expanded;
                _intentActivityId = _arbitration.Foreground?.Id;
            }
        }
        return Recompute(now);
    }

    private bool Recompute(DateTimeOffset now)
    {
        var fg = _arbitration.Foreground;
        IslandMode mode;

        if (_arbitration.Hidden)
        {
            _intent = UserIntent.None;
            _dragging = false; // an invisible island cannot be a drop target
            _dragEndAt = null;
            mode = IslandMode.Hidden;
        }
        else if (_intent == UserIntent.Center)
        {
            mode = IslandMode.Center; // a drop while Center is open goes straight into its Shelf section
        }
        else if (_dragging)
        {
            // The user is acting on the island right now, so the drop zone wins over every activity for the few
            // seconds a drag lasts. The previous intent is kept and restored when the drag ends.
            mode = IslandMode.Drop;
        }
        else
        {
            if (_intent == UserIntent.Expanded && (fg is null || fg.Id != _intentActivityId))
            {
                // A different activity took over: the user's expansion belonged to the previous one.
                _intent = UserIntent.None;
                _intentActivityId = null;
                _collapseAt = null;
            }

            if (fg is null) mode = _hovered && HoverBehavior != HoverBehavior.None ? IslandMode.Peek : IslandMode.Idle;
            else if (_intent == UserIntent.Expanded || fg.Activity.PreferExpanded || fg.Priority == ActivityPriority.Critical) mode = IslandMode.Expanded;
            else mode = IslandMode.Compact;
        }

        bool alert = fg?.Priority == ActivityPriority.Critical && mode is IslandMode.Compact or IslandMode.Expanded;
        var next = new IslandState(mode, fg, _arbitration.Indicators, _hovered, alert);

        bool changed = next.Mode != State.Mode
            || next.Hovered != State.Hovered
            || next.Alert != State.Alert
            || !ReferenceEquals(next.Foreground, State.Foreground)
            || !SameIndicators(next.Indicators, State.Indicators);
        State = next;
        return changed;
    }

    private static bool SameIndicators(IReadOnlyList<TrackedActivity> a, IReadOnlyList<TrackedActivity> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }
}
