using Nexus.Core.Diagnostics;

namespace Nexus.App.Composition;

/// <summary>
/// One NEXUS per user session. A second launch signals the running instance (which opens Settings) and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // "Local\" objects are already scoped to the current logon session, so fixed names are per-user.
    // (Never derive names from string.GetHashCode(): it is randomised per process in .NET.)
    private const string MutexName = @"Local\NEXUS.Instance";
    private const string SignalName = @"Local\NEXUS.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle signal)
    {
        _mutex = mutex;
        _signal = signal;
        _registration = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, false);
    }

    public event EventHandler? ActivationRequested;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out bool created);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        if (created) return new SingleInstance(mutex, signal);

        try { signal.Set(); } catch (Exception ex) { Log.Warn("Instance", "Could not signal running instance", ex); }
        signal.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
        _signal.Dispose();
    }
}
