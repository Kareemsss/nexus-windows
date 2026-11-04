using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nexus.Core.Diagnostics;
using Nexus.Platform.Interop;

namespace Nexus.Platform.Services;

/// <param name="Key">Stable identity of the app ("spotify", "chrome", "system"). All sessions of one app share it.</param>
public sealed record AudioSessionInfo(string Key, string Name, string? ExePath, float Volume, bool Muted, bool Active, bool IsSystem);

/// <summary>
/// Per-application volume, like the Windows volume mixer, for the default output device.
///
/// * Sessions are grouped per app: Chrome or Discord open several audio sessions; the mixer shows one row each.
/// * Nothing runs in the background. Island Center asks for a snapshot when it opens and every 1.5 s while it
///   stays open; each snapshot is a short COM enumeration on a thread-pool (MTA) thread.
/// * Volume changes are coalesced: while a slider is dragged only the newest value per app is applied, on one
///   worker at a time, so dragging never floods Windows with calls.
/// </summary>
public sealed class AudioSessionService
{
    private static readonly Lazy<AudioSessionService> Instance = new(() => new AudioSessionService());
    public static AudioSessionService Shared => Instance.Value;

    private static Guid _context = Guid.NewGuid();
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_INVALID_PARAMETER = 87;

    private sealed record ProcInfo(string Key, string Name, string? Path);
    private sealed record PendingChange(float? Volume, bool? Muted);
    private sealed class Acc
    {
        public required ProcInfo Info;
        public bool IsSystem, Active, AllMuted = true;
        public float Volume;
    }

    private static readonly ProcInfo SystemSounds = new("system", "System sounds", null);
    private readonly ConcurrentDictionary<uint, ProcInfo?> _procs = new();
    private DateTime _procsSince = DateTime.UtcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingChange> _pending = new(StringComparer.Ordinal);
    private bool _draining;

    private AudioSessionService() { }

    public IReadOnlyList<AudioSessionInfo> LastSnapshot { get; private set; } = Array.Empty<AudioSessionInfo>();

    public Task<IReadOnlyList<AudioSessionInfo>> SnapshotAsync() => Task.Run(Snapshot);

    /// <summary>Snapshot that waits at most <paramref name="timeout"/>; otherwise returns the previous one.</summary>
    public IReadOnlyList<AudioSessionInfo> TrySnapshot(TimeSpan timeout)
    {
        try
        {
            var task = SnapshotAsync();
            return task.Wait(timeout) ? task.Result : LastSnapshot;
        }
        catch { return LastSnapshot; }
    }

    public void SetVolume(string key, float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        // Like the Windows mixer: raising an app's volume also unmutes it.
        Queue(key, old => new PendingChange(level, level > 0.005f ? false : old?.Muted));
    }

    public void SetMuted(string key, bool muted) => Queue(key, old => new PendingChange(old?.Volume, muted));

    private IReadOnlyList<AudioSessionInfo> Snapshot()
    {
        if (DateTime.UtcNow - _procsSince > TimeSpan.FromMinutes(5) || _procs.Count > 128)
        {
            _procs.Clear(); // process ids get reused; never trust old names for long
            _procsSince = DateTime.UtcNow;
        }

        var groups = new Dictionary<string, Acc>(StringComparer.Ordinal);
        ForEachSession((control, volume) =>
        {
            if (control.GetState(out int state) != 0 || state == CoreAudioSessionNative.AudioSessionStateExpired) return;
            var info = Describe(control, out bool system);
            if (info is null) return;
            if (volume.GetMasterVolume(out float level) != 0) return;
            volume.GetMute(out bool muted);

            if (!groups.TryGetValue(info.Key, out var acc)) groups[info.Key] = acc = new Acc { Info = info, IsSystem = system };
            acc.Volume = Math.Max(acc.Volume, level);
            acc.AllMuted &= muted;
            acc.Active |= state == CoreAudioSessionNative.AudioSessionStateActive;
        });

        var list = groups.Values
            .Select(a => new AudioSessionInfo(a.Info.Key, a.Info.Name, a.Info.Path, a.Volume, a.AllMuted, a.Active, a.IsSystem))
            .OrderByDescending(s => s.Active)
            .ThenBy(s => s.IsSystem)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        LastSnapshot = list;
        return list;
    }

    private void Queue(string key, Func<PendingChange?, PendingChange> merge)
    {
        lock (_gate)
        {
            _pending[key] = merge(_pending.TryGetValue(key, out var old) ? old : null);
            if (_draining) return;
            _draining = true;
        }
        Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            Dictionary<string, PendingChange> batch;
            lock (_gate)
            {
                if (_pending.Count == 0) { _draining = false; return; }
                batch = new Dictionary<string, PendingChange>(_pending, StringComparer.Ordinal);
                _pending.Clear();
            }
            try
            {
                ForEachSession((control, volume) =>
                {
                    var info = Describe(control, out _);
                    if (info is null || !batch.TryGetValue(info.Key, out var change)) return;
                    if (change.Volume is { } v) volume.SetMasterVolume(v, ref _context);
                    if (change.Muted is { } m) volume.SetMute(m, ref _context);
                });
            }
            catch (Exception ex) { Log.Warn("Mixer", "Applying volume failed", ex); }
        }
    }

    /// <summary>Walks every session of the default output device. COM objects are released before returning.</summary>
    private static void ForEachSession(Action<IAudioSessionControl2, ISimpleAudioVolume> visit)
    {
        IMMDeviceEnumerator? devices = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (devices.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var d) != 0 || d is null) return;
            device = d;
            var iid = CoreAudioSessionNative.IID_IAudioSessionManager2;
            if (device.Activate(ref iid, CoreAudioNative.CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) return;
            manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out var e) != 0 || e is null) return;
            sessions = e;
            if (sessions.GetCount(out int count) != 0) return;
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var raw) != 0 || raw is null) continue;
                try
                {
                    if (raw is IAudioSessionControl2 control && raw is ISimpleAudioVolume volume) visit(control, volume);
                }
                catch (Exception ex) { Log.Debug("Mixer", "Session skipped: " + ex.Message); }
                finally { Release(raw); }
            }
        }
        catch (Exception ex) { Log.Debug("Mixer", "Enumeration failed: " + ex.Message); }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
            Release(devices);
        }
    }

    private static void Release(object? o)
    {
        if (o is null || !Marshal.IsComObject(o)) return;
        try { Marshal.ReleaseComObject(o); } catch { }
    }

    private ProcInfo? Describe(IAudioSessionControl2 control, out bool system)
    {
        system = control.IsSystemSoundsSession() == 0;
        if (system) return SystemSounds;
        if (control.GetProcessId(out uint pid) < 0 || pid == 0 || pid == (uint)Environment.ProcessId) return null;
        return _procs.GetOrAdd(pid, p => Lookup(p, control));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] buffer, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Resolves the app behind a session. Uses PROCESS_QUERY_LIMITED_INFORMATION, which also works for elevated
    /// apps, instead of Process.MainModule (slower, and denied for elevated processes).
    /// Returns null for sessions whose process has already exited (Windows keeps them around for a while).
    /// </summary>
    private static ProcInfo? Lookup(uint pid, IAudioSessionControl2 control)
    {
        string? path = null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            if (Marshal.GetLastWin32Error() == ERROR_INVALID_PARAMETER) return null; // process is gone
        }
        else
        {
            try
            {
                var buffer = new char[1024];
                int size = buffer.Length;
                if (QueryFullProcessImageName(h, 0, buffer, ref size)) path = new string(buffer, 0, size);
            }
            finally { CloseHandle(h); }
        }

        if (path is null)
        {
            string name = "App";
            try
            {
                if (control.GetDisplayName(out var dn) == 0 && !string.IsNullOrWhiteSpace(dn) && !dn.StartsWith("@")) name = dn.Trim();
            }
            catch { }
            return new ProcInfo("pid:" + pid, name, null);
        }

        var exe = Path.GetFileNameWithoutExtension(path);
        string display = Pretty(exe);
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc) && desc.Trim().Length <= 32) display = desc.Trim();
        }
        catch { }
        return new ProcInfo(exe.ToLowerInvariant(), display, path);
    }

    private static string Pretty(string exe)
    {
        var s = exe.Replace('_', ' ').Replace('-', ' ').Trim();
        return s.Length == 0 ? "App" : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
