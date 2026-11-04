using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Nexus.App.Motion;
using Nexus.Core.Diagnostics;

namespace Nexus.App.Composition;

/// <summary>
/// Gives memory back to Windows while NEXUS is idle.
///
/// After the island has been still for a while, it runs one compacting GC (the managed heap is tiny, so this
/// takes a few milliseconds) and asks Windows to trim the process working set. Trimmed pages are not lost:
/// Windows pages them back in on demand when the island next animates. Trims are throttled and only happen
/// after something actually ran, so an idle NEXUS is not repeatedly trimmed.
/// </summary>
public sealed class MemoryTrimmer : IDisposable
{
    private static MemoryTrimmer? _current;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(15) };
    private DateTime _lastTrim = DateTime.MinValue;
    private bool _soon;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    public void Start()
    {
        _current = this;
        _timer.Tick += (_, _) => Check();
        _timer.Start();
    }

    /// <summary>Trim shortly (e.g. right after the Settings window, the largest UI, was closed).</summary>
    public static void RequestSoon()
    {
        if (_current is not null) _current._soon = true;
    }

    private void Check()
    {
        if (Animator.IsRunning) return;
        var now = DateTime.UtcNow;
        var quiet = now - Animator.LastFrameUtc;
        if (quiet < TimeSpan.FromSeconds(_soon ? 3 : 20)) return;
        if (!_soon)
        {
            if (Animator.LastFrameUtc < _lastTrim) return; // nothing new happened since the last trim
            var gap = Animator.Eco ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(3);
            if (now - _lastTrim < gap) return;
        }
        _soon = false;
        _lastTrim = now;
        Trim();
    }

    private static void Trim()
    {
        try
        {
            long before = Environment.WorkingSet;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            Log.Debug("Memory", $"Trimmed working set {before / 1048576} MB → {Environment.WorkingSet / 1048576} MB");
        }
        catch (Exception ex) { Log.Warn("Memory", "Trim failed", ex); }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_current == this) _current = null;
    }
}
