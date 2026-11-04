using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Diagnostics;
using Nexus.Core.Settings;

namespace Nexus.Core.Plugins;

public enum ProviderState { Stopped, Starting, Running, Unavailable, Faulted }

public sealed record ProviderStatus(ProviderState State, string? Message = null)
{
    public static ProviderStatus Stopped { get; } = new(ProviderState.Stopped);
}

/// <summary>Read side of the activity hub, for providers that react to other providers (meetings ← privacy).</summary>
public interface IActivityFeed
{
    IReadOnlyList<TrackedActivity> Snapshot();
    event EventHandler? Changed;
}

/// <summary>Everything a provider may touch. Deliberately small: providers never see UI types.</summary>
public interface IProviderContext
{
    IActivitySink Activities { get; }
    IActivityFeed Feed { get; }
    NexusSettings Settings { get; }
    /// <summary>The UI thread context. WinRT/COM callbacks arrive on arbitrary threads.</summary>
    SynchronizationContext UiContext { get; }
}

/// <summary>
/// A provider detects events from one source (Windows API, app, service), turns them into activities with a
/// priority, content, accent and actions, and keeps them updated until they end. Providers are started and
/// stopped independently; a failing provider never takes NEXUS down.
/// </summary>
public interface INexusProvider : IAsyncDisposable
{
    /// <summary>Stable id, also the module key in settings ("media", "volume"...).</summary>
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    ProviderStatus Status { get; }
    event EventHandler? StatusChanged;
    Task StartAsync(IProviderContext context, CancellationToken cancellationToken);
}

/// <summary>Convenience base class handling status reporting, safe posting and cancellation.</summary>
public abstract class ProviderBase : INexusProvider
{
    private ProviderStatus _status = ProviderStatus.Stopped;
    private IProviderContext? _context;
    private CancellationTokenSource? _cts;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }

    public ProviderStatus Status => _status;
    public event EventHandler? StatusChanged;

    protected IProviderContext Context => _context ?? throw new InvalidOperationException("Provider not started.");
    protected CancellationToken Stopping => _cts?.Token ?? CancellationToken.None;
    protected NexusSettings Settings => Context.Settings;

    public async Task StartAsync(IProviderContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetStatus(new ProviderStatus(ProviderState.Starting));
        try
        {
            await OnStartAsync(_cts.Token).ConfigureAwait(true);
            if (_status.State == ProviderState.Starting) SetStatus(new ProviderStatus(ProviderState.Running));
        }
        catch (OperationCanceledException) { SetStatus(ProviderStatus.Stopped); }
        catch (Exception ex)
        {
            Log.Error(Id, "Failed to start", ex);
            SetStatus(new ProviderStatus(ProviderState.Faulted, ex.Message));
        }
    }

    protected abstract Task OnStartAsync(CancellationToken ct);
    protected virtual ValueTask OnStopAsync() => ValueTask.CompletedTask;

    protected void SetStatus(ProviderStatus status)
    {
        _status = status;
        try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch { /* UI subscriber issues are not ours */ }
    }

    protected void Post(Activity activity) => _context?.Activities.Post(activity);
    protected void Remove(string id) => _context?.Activities.Remove(id);
    protected void Pulse(PulseSignal signal) => _context?.Activities.Pulse(signal);

    /// <summary>Runs a callback (typically from a Windows event) without letting exceptions escape.</summary>
    protected void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Warn(Id, what + " failed", ex); }
    }

    protected async void GuardAsync(string what, Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn(Id, what + " failed", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { await OnStopAsync().ConfigureAwait(true); }
        catch (Exception ex) { Log.Warn(Id, "Stop failed", ex); }
        _context?.Activities.RemoveSource(Id);
        _cts?.Dispose();
        _cts = null;
        SetStatus(ProviderStatus.Stopped);
        GC.SuppressFinalize(this);
    }
}
