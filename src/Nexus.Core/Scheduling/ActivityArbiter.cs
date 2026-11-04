using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Core.Activities;

namespace Nexus.Core.Scheduling;

public enum FullscreenBehavior
{
    /// <summary>Hide the island entirely while a fullscreen app is in front.</summary>
    Hide,
    /// <summary>Only critical events may appear.</summary>
    CriticalOnly,
    /// <summary>Critical and high-priority events (calls, meeting start); no media, no routine updates.</summary>
    Gaming,
    /// <summary>Behave exactly as on the desktop.</summary>
    AlwaysShow,
}

/// <param name="HideAll">"Hide completely": the island stays hidden except for critical alerts and
/// activities flagged <see cref="Activity.ShowWhenHidden"/> (song pop-ups).</param>
/// <param name="MediaPopupsOnly">Song pop-ups: ongoing media never holds the island; only the short pop-up does.</param>
public sealed record ArbitrationPolicy(
    bool FullscreenActive = false,
    FullscreenBehavior Fullscreen = FullscreenBehavior.Gaming,
    bool QuietMode = false,
    IReadOnlySet<string>? DisabledSources = null,
    bool HideAll = false,
    bool MediaPopupsOnly = false)
{
    public static ArbitrationPolicy Default { get; } = new();
}

/// <param name="Foreground">The activity that owns the island right now, or null for idle.</param>
/// <param name="Indicators">Other live activities shown as compact satellite indicators.</param>
/// <param name="Hidden">True when policy says the island must not be visible at all.</param>
/// <param name="NextWakeup">When the result may change purely due to time passing (expiry).</param>
public sealed record Arbitration(
    TrackedActivity? Foreground,
    IReadOnlyList<TrackedActivity> Indicators,
    bool Hidden,
    DateTimeOffset? NextWakeup)
{
    public static Arbitration Empty { get; } = new(null, Array.Empty<TrackedActivity>(), false, null);
}

/// <summary>
/// The priority manager. Decides which activity deserves visual dominance. Pure and deterministic.
///
/// Ranking (highest first):
///   1. Critical activities (critical battery, security) – always win.
///   2. Transient events (volume, notifications, glances, song pop-ups) – news beats steady state.
///   3. Persistent activities (media, active meeting) – steady state.
/// Within a tier: priority, then stickiness to the current foreground (no ping-pong), then FIFO for
/// transient events (a real queue) or most-recently-updated for persistent ones.
/// </summary>
public static class ActivityArbiter
{
    public const int MaxIndicators = 4;

    public static Arbitration Arbitrate(
        IReadOnlyCollection<TrackedActivity> activities,
        string? currentForegroundId,
        ArbitrationPolicy policy,
        DateTimeOffset now)
    {
        if (policy.FullscreenActive && policy.Fullscreen == FullscreenBehavior.Hide)
            return new Arbitration(null, Array.Empty<TrackedActivity>(), true, null);

        var live = activities
            .Where(a => !a.IsExpired(now))
            .Where(a => policy.DisabledSources is null || !policy.DisabledSources.Contains(a.Activity.SourceId))
            .ToList();

        var candidates = live
            .Where(a => a.Activity.CanBeForeground && !a.Activity.IsDormant && IsAllowed(a, policy))
            .ToList();
        if (policy.HideAll)
            candidates = candidates.Where(a => a.Priority == ActivityPriority.Critical || a.Activity.ShowWhenHidden).ToList();

        TrackedActivity? foreground = null;
        if (candidates.Count > 0)
        {
            var topTier = candidates.Max(Tier);
            var tierItems = candidates.Where(a => Tier(a) == topTier).ToList();
            var topPriority = tierItems.Max(a => a.Priority);
            var group = tierItems.Where(a => a.Priority == topPriority).ToList();

            foreground = group.FirstOrDefault(a => a.Id == currentForegroundId)
                ?? (topTier == 0
                    ? group.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Sequence).First()
                    : group.OrderBy(a => a.QueuedSince).ThenBy(a => a.Sequence).First());
        }

        DateTimeOffset? wake = null;
        foreach (var a in live)
        {
            if (a.ExpiresAt is { } e && (wake is null || e < wake)) wake = e;
        }

        // Hide completely: nothing worth surfacing → stay hidden (but keep the wake-up so pop-ups still expire).
        if (policy.HideAll && foreground is null)
            return new Arbitration(null, Array.Empty<TrackedActivity>(), true, wake);

        bool allowIndicators = !policy.FullscreenActive || policy.Fullscreen == FullscreenBehavior.AlwaysShow;
        var indicators = policy.HideAll
            ? new List<TrackedActivity>()
            : live
                .Where(a => a.Activity.Indicator is not null && !a.Activity.IsDormant && a.Id != foreground?.Id)
                .Where(a => allowIndicators || a.Activity.Category == ActivityCategory.Privacy)
                .Where(a => !(policy.MediaPopupsOnly && a.Activity.Category == ActivityCategory.Media))
                .OrderByDescending(a => a.Priority)
                .ThenBy(a => a.Sequence)
                .Take(MaxIndicators)
                .ToList();

        return new Arbitration(foreground, indicators, false, wake);
    }

    /// <summary>2 = critical, 1 = transient event, 0 = persistent state.</summary>
    internal static int Tier(TrackedActivity a) =>
        a.Priority == ActivityPriority.Critical ? 2 : a.Activity.IsTransient ? 1 : 0;

    internal static bool IsAllowed(TrackedActivity a, ArbitrationPolicy policy)
    {
        if (policy.MediaPopupsOnly && a.Activity.Category == ActivityCategory.Media && !a.Activity.IsTransient)
            return false;

        if (policy.FullscreenActive)
        {
            switch (policy.Fullscreen)
            {
                case FullscreenBehavior.CriticalOnly when a.Priority < ActivityPriority.Critical:
                case FullscreenBehavior.Gaming when a.Priority < ActivityPriority.High:
                    return false;
            }
        }

        if (policy.QuietMode && a.Activity.IsTransient && a.Priority < ActivityPriority.High)
        {
            switch (a.Activity.Category)
            {
                case ActivityCategory.Notification:
                case ActivityCategory.Communication:
                case ActivityCategory.Connectivity:
                case ActivityCategory.Transfer:
                    return false;
            }
        }
        return true;
    }
}
