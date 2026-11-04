using System;
using Nexus.Core.Activities;

namespace Nexus.Core.Tests;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal static class A
{
    public static Activity Make(string id, ActivityPriority p, TimeSpan? duration = null, ActivityCategory c = ActivityCategory.System,
        bool canForeground = true, bool dormant = false, bool preferExpanded = false, IndicatorSpec? indicator = null) => new()
    {
        Id = id,
        SourceId = "test",
        Priority = p,
        Category = c,
        Duration = duration,
        CanBeForeground = canForeground,
        IsDormant = dormant,
        PreferExpanded = preferExpanded,
        Indicator = indicator,
        Content = id,
    };
}
