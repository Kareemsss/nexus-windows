using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Core.Diagnostics;
using Nexus.Core.Plugins;

namespace Nexus.Core.Activities;

/// <summary>Immutable snapshot of a live activity plus the scheduling metadata the hub tracks for it.</summary>
public sealed record TrackedActivity(
    Activity Activity,
    DateTimeOffset PostedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset QueuedSince,
    DateTimeOffset? ShownAt,
    long Sequence,
    int Revision)
{
    public string Id => Activity.Id;
    public ActivityPriority Priority => Activity.Priority;

    /// <summary>
    /// Transient activities wait in the queue for a bounded time (low-value events are simply dropped if
    /// something more important is on screen), then live for <see cref="Activity.Duration"/> once shown.
    /// </summary>
    public DateTimeOffset? ExpiresAt
    {
        get
        {
            if (Activity.Duration is not { } duration) return null;
            if (ShownAt is { } shown) return shown + duration;
            var wait = MaxQueueWait(Activity.Priority);
            return wait is null ? null : QueuedSince + wait.Value;
        }
    }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && now >= e;

    public static TimeSpan? MaxQueueWait(ActivityPriority p) => p switch
    {
        ActivityPriority.Low => TimeSpan.FromSeconds(2),
        ActivityPriority.Medium => TimeSpan.FromSeconds(30),
        ActivityPriority.High => TimeSpan.FromMinutes(2),
        _ => null,
    };
}

/// <summary>Write side of the hub, handed to providers.</summary>
public interface IActivitySink
{
    void Post(Activity activity);
    void Remove(string activityId);
    /// <summary>Removes every activity posted by a provider (used when a module is disabled).</summary>
    void RemoveSource(string sourceId);
    void Pulse(PulseSignal signal);
}

/// <summary>
/// Thread-safe registry of every live activity (the event bus + event queue). Providers post from any
/// thread; change notifications are coalesced and marshalled to the UI synchronisation context so a burst
/// of updates causes one re-render.
/// </summary>
public sealed class ActivityHub : IActivitySink, IActivityFeed
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedActivity> _items = new(StringComparer.Ordinal);
    private readonly SynchronizationContext? _context;
    private readonly TimeProvider _time;
    private long _sequence;
    private int _changePending;

    public ActivityHub(SynchronizationContext? context, TimeProvider? time = null)
    {
        _context = context;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler? Changed;
    public event EventHandler<PulseSignal>? Pulsed;

    public DateTimeOffset Now => _time.GetUtcNow();

    public void Post(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var now = Now;
        lock (_gate)
        {
            if (_items.TryGetValue(activity.Id, out var existing))
            {
                var shownAt = existing.ShownAt;
                if (activity.IsTransient && activity.RestartOnUpdate && shownAt is not null) shownAt = now;
                _items[activity.Id] = existing with
                {
                    Activity = activity,
                    UpdatedAt = now,
                    ShownAt = shownAt,
                    Revision = existing.Revision + 1,
                };
            }
            else
            {
                _items[activity.Id] = new TrackedActivity(activity, now, now, now, null, ++_sequence, 0);
            }
        }
        RaiseChanged();
    }

    public void Remove(string activityId)
    {
        bool removed;
        lock (_gate) removed = _items.Remove(activityId);
        if (removed) RaiseChanged();
    }

    public void RemoveSource(string sourceId)
    {
        bool removed = false;
        lock (_gate)
        {
            foreach (var key in _items.Where(p => p.Value.Activity.SourceId == sourceId).Select(p => p.Key).ToList())
                removed |= _items.Remove(key);
        }
        if (removed) RaiseChanged();
    }

    public void Pulse(PulseSignal signal)
    {
        if (_context is null) { Pulsed?.Invoke(this, signal); return; }
        _context.Post(_ => SafeInvoke(() => Pulsed?.Invoke(this, signal)), null);
    }

    public IReadOnlyList<TrackedActivity> Snapshot()
    {
        lock (_gate) return _items.Values.ToArray();
    }

    public TrackedActivity? Find(string id)
    {
        lock (_gate) return _items.TryGetValue(id, out var t) ? t : null;
    }

    /// <summary>Returns the most recently updated activity whose content is of type T (dormant ones included).</summary>
    public T? LatestContent<T>() where T : class
    {
        lock (_gate)
        {
            return _items.Values.Where(t => t.Activity.Content is T)
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => (T)t.Activity.Content)
                .FirstOrDefault();
        }
    }

    /// <summary>Removes expired transient activities. Returns true when anything was removed.</summary>
    public bool Prune(DateTimeOffset now)
    {
        lock (_gate)
        {
            var dead = _items.Values.Where(t => t.IsExpired(now)).Select(t => t.Id).ToList();
            foreach (var id in dead) _items.Remove(id);
            return dead.Count > 0;
        }
    }

    /// <summary>Starts the display countdown of a transient activity the first time it becomes visible.</summary>
    public void MarkShown(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(id, out var t) && t.ShownAt is null) _items[id] = t with { ShownAt = now };
        }
    }

    /// <summary>
    /// Keeps a visible transient activity alive (restarts its countdown). Used while the pointer rests on the
    /// island: NEXUS never dismisses something the user is reading or interacting with.
    /// </summary>
    public void Hold(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(id, out var t) && t.Activity.IsTransient && t.ShownAt is not null)
                _items[id] = t with { ShownAt = now };
        }
    }

    /// <summary>Puts a pre-empted transient activity back into the queue so it can be shown in full later.</summary>
    public void Requeue(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(id, out var t) && t.Activity.IsTransient && t.ShownAt is not null)
                _items[id] = t with { ShownAt = null, QueuedSince = now };
        }
    }

    private void RaiseChanged()
    {
        if (_context is null) { Changed?.Invoke(this, EventArgs.Empty); return; }
        // Coalesce bursts (e.g. timeline + metadata + playback events arriving together).
        if (Interlocked.Exchange(ref _changePending, 1) == 1) return;
        _context.Post(_ =>
        {
            Interlocked.Exchange(ref _changePending, 0);
            SafeInvoke(() => Changed?.Invoke(this, EventArgs.Empty));
        }, null);
    }

    private static void SafeInvoke(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error("ActivityHub", "Subscriber threw", ex); }
    }
}
