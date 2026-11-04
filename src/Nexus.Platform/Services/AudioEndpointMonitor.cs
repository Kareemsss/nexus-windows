using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nexus.Core.Content;
using Nexus.Core.Diagnostics;
using Nexus.Platform.Interop;

namespace Nexus.Platform.Services;

public enum AudioChange { Initial, Volume, Device }

public sealed record AudioEndpointSnapshot(string Id, string Name, AudioDeviceKind Kind, float Level, bool Muted);

/// <summary>
/// Tracks the default render or capture endpoint through Core Audio notifications (no polling): volume/mute
/// via IAudioEndpointVolumeCallback, default-device switches via IMMNotificationClient.
/// Events are raised on Core Audio worker threads; consumers must marshal to their own context.
/// </summary>
public sealed class AudioEndpointMonitor : IDisposable
{
    private static Guid _ownContext = Guid.NewGuid();
    private readonly object _gate = new();
    private readonly EDataFlow _flow;
    private readonly ERole _role;
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationClient _client;
    private IMMDevice? _device;
    private IAudioEndpointVolume? _volume;
    private VolumeCallback? _callback;
    private bool _disposed;

    public AudioEndpointMonitor(bool capture)
    {
        _flow = capture ? EDataFlow.Capture : EDataFlow.Render;
        _role = capture ? ERole.Console : ERole.Multimedia;
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _client = new NotificationClient(this);
        Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_client));
        Attach(AudioChange.Initial);
    }

    public AudioEndpointSnapshot? Current { get; private set; }

    public event Action<AudioEndpointSnapshot?, AudioChange>? Changed;

    public void SetLevel(float level)
    {
        lock (_gate) _volume?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _ownContext);
    }

    public void SetMuted(bool muted)
    {
        lock (_gate) _volume?.SetMute(muted, ref _ownContext);
    }

    public void Step(int deltaPercent)
    {
        lock (_gate)
        {
            if (_volume is null) return;
            if (_volume.GetMasterVolumeLevelScalar(out var level) != 0) return;
            var next = Math.Clamp(level + deltaPercent / 100f, 0f, 1f);
            // Snap to whole percents so wheel steps land on the same values as the Windows flyout.
            next = MathF.Round(next * 100f) / 100f;
            _volume.SetMasterVolumeLevelScalar(next, ref _ownContext);
            if (next > 0 && _volume.GetMute(out var muted) == 0 && muted) _volume.SetMute(false, ref _ownContext);
        }
    }

    private void Attach(AudioChange reason)
    {
        AudioEndpointSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_disposed) return;
            Detach();
            try
            {
                if (_enumerator.GetDefaultAudioEndpoint(_flow, _role, out var device) == 0 && device is not null)
                {
                    _device = device;
                    device.GetId(out var id);
                    var (name, kind) = Describe(device);
                    var iid = CoreAudioNative.IID_IAudioEndpointVolume;
                    if (device.Activate(ref iid, CoreAudioNative.CLSCTX_ALL, IntPtr.Zero, out var obj) == 0)
                    {
                        _volume = (IAudioEndpointVolume)obj;
                        _callback = new VolumeCallback(this);
                        _volume.RegisterControlChangeNotify(_callback);
                        _volume.GetMasterVolumeLevelScalar(out var level);
                        _volume.GetMute(out var muted);
                        snapshot = new AudioEndpointSnapshot(id ?? string.Empty, name, kind, level, muted);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Audio", $"Attach to default {_flow} endpoint failed", ex);
                Detach();
            }
            Current = snapshot;
        }
        Raise(snapshot, reason);
    }

    private void Detach()
    {
        try { if (_volume is not null && _callback is not null) _volume.UnregisterControlChangeNotify(_callback); } catch { }
        if (_volume is not null) { try { Marshal.ReleaseComObject(_volume); } catch { } }
        if (_device is not null) { try { Marshal.ReleaseComObject(_device); } catch { } }
        _volume = null; _device = null; _callback = null;
    }

    private static (string Name, AudioDeviceKind Kind) Describe(IMMDevice device)
    {
        string name = "Audio device";
        var kind = AudioDeviceKind.Other;
        try
        {
            if (device.OpenPropertyStore(CoreAudioNative.STGM_READ, out var store) == 0 && store is not null)
            {
                try
                {
                    name = CoreAudioNative.ReadString(store, CoreAudioNative.PKEY_Device_FriendlyName) ?? name;
                    var enumerator = CoreAudioNative.ReadString(store, CoreAudioNative.PKEY_Device_EnumeratorName) ?? string.Empty;
                    var formFactor = CoreAudioNative.ReadUInt(store, CoreAudioNative.PKEY_AudioEndpoint_FormFactor);
                    if (enumerator.Contains("BTH", StringComparison.OrdinalIgnoreCase)) kind = AudioDeviceKind.Bluetooth;
                    else kind = formFactor switch
                    {
                        1 => AudioDeviceKind.Speakers,
                        3 => AudioDeviceKind.Headphones,
                        5 => AudioDeviceKind.Headset,
                        9 => AudioDeviceKind.Display,
                        _ => AudioDeviceKind.Other,
                    };
                }
                finally { Marshal.ReleaseComObject(store); }
            }
        }
        catch (Exception ex) { Log.Debug("Audio", "Describe failed: " + ex.Message); }

        // Friendly names look like "Headphones (WH-1000XM5 Stereo)": show the product part, it is what users recognise.
        int open = name.IndexOf('('), close = name.LastIndexOf(')');
        if (open > 0 && close > open + 1) name = name[(open + 1)..close].Replace(" Stereo", "").Replace(" Hands-Free", "").Trim();
        return (name, kind);
    }

    private void OnVolumeNotify(IntPtr data)
    {
        if (data == IntPtr.Zero) return;
        var n = Marshal.PtrToStructure<AudioVolumeNotificationData>(data);
        AudioEndpointSnapshot? snap;
        lock (_gate)
        {
            if (Current is null) return;
            snap = Current = Current with { Level = n.fMasterVolume, Muted = n.bMuted != 0 };
        }
        Raise(snap, AudioChange.Volume);
    }

    private void Raise(AudioEndpointSnapshot? s, AudioChange reason)
    {
        try { Changed?.Invoke(s, reason); }
        catch (Exception ex) { Log.Warn("Audio", "Subscriber threw", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
            try { _enumerator.UnregisterEndpointNotificationCallback(_client); } catch { }
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
        }
    }

    [ComVisible(true)]
    public sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly AudioEndpointMonitor _owner;
        internal VolumeCallback(AudioEndpointMonitor owner) => _owner = owner;
        public int OnNotify(IntPtr notifyData) { _owner.OnVolumeNotify(notifyData); return 0; }
    }

    [ComVisible(true)]
    public sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioEndpointMonitor _owner;
        internal NotificationClient(AudioEndpointMonitor owner) => _owner = owner;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            // Core Audio forbids (un)registering callbacks from inside a notification: hop to the pool.
            if (flow == _owner._flow && role == _owner._role) Task.Run(() => _owner.Attach(AudioChange.Device));
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
