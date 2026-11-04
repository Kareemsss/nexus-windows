using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Core.Color;

namespace Nexus.Core.Activities;

/// <summary>How strongly an activity is allowed to claim the island.</summary>
public enum ActivityPriority
{
    Low = 1,      // media, volume, brightness, routine connectivity
    Medium = 2,   // messages, calendar, device changes, transfers
    High = 3,     // incoming calls, meeting starting, urgent system state
    Critical = 4, // critical battery, privacy/security alerts
}

/// <summary>Semantic grouping used by policies (quiet mode, gaming mode, module filters).</summary>
public enum ActivityCategory
{
    System,
    Media,
    Feedback,      // direct response to a user action (volume keys) – never suppressed by quiet mode
    Notification,
    Communication,
    Privacy,
    Connectivity,
    Transfer,
    Power,
    Meeting,
}

/// <summary>A small glyph shown in the satellite capsule when the activity is alive but not in the foreground.</summary>
public sealed record IndicatorSpec(string Glyph, NexusColor Color, string Label, double? Progress = null, bool Indeterminate = false);

/// <summary>An action the user can trigger from the expanded presentation of an activity.</summary>
public sealed record ActivityAction(string Id, string Label, string Glyph, Func<Task> Execute, bool IsPrimary = false);

/// <summary>
/// The unit of everything NEXUS shows. Providers create and update activities; the arbiter decides which
/// one dominates; widgets render the <see cref="Content"/>. Activities are immutable: update with <c>with</c>.
/// </summary>
public sealed record Activity
{
    /// <summary>Stable identity. Posting the same id again updates the activity in place.</summary>
    public required string Id { get; init; }
    /// <summary>Provider id (used by module filters).</summary>
    public required string SourceId { get; init; }
    public required ActivityPriority Priority { get; init; }
    public ActivityCategory Category { get; init; } = ActivityCategory.System;

    /// <summary>Content model consumed by a widget (MediaContent, VolumeContent, GenericContent...).</summary>
    public required object Content { get; init; }

    /// <summary>
    /// Display time for transient activities. Null means persistent: alive until the provider removes it.
    /// The countdown starts when the activity is first shown, not when it is posted (queue semantics).
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>For transient activities: an update while visible restarts the display countdown (volume).</summary>
    public bool RestartOnUpdate { get; init; } = true;

    /// <summary>Persistent activity that exists only as state for Island Center / peek, never presented on its own.</summary>
    public bool IsDormant { get; init; }

    /// <summary>False for activities that must only appear as satellite indicators (privacy).</summary>
    public bool CanBeForeground { get; init; } = true;

    /// <summary>Show expanded immediately (incoming call, critical alert).</summary>
    public bool PreferExpanded { get; init; }

    /// <summary>May appear even when the island is set to Hide completely (song pop-ups).</summary>
    public bool ShowWhenHidden { get; init; }

    public IndicatorSpec? Indicator { get; init; }

    /// <summary>Contextual accent fed into the ambient colour engine. Null = theme accent.</summary>
    public NexusColor? Accent { get; init; }

    public IReadOnlyList<ActivityAction> Actions { get; init; } = Array.Empty<ActivityAction>();

    public bool IsTransient => Duration is not null;
}

public enum PulseStyle
{
    /// <summary>Thin edge glow that blooms and fades.</summary>
    Edge,
    /// <summary>A travelling energy band around the border (charging).</summary>
    Sweep,
    /// <summary>A quick bright flash of the whole rim (screenshot).</summary>
    Flash,
}

/// <summary>A lightweight, non-interrupting visual acknowledgement of a minor event.</summary>
public sealed record PulseSignal(NexusColor Color, PulseStyle Style = PulseStyle.Edge);
