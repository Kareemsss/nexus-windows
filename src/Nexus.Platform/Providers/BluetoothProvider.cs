using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Plugins;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Nexus.Platform.Providers;

/// <summary>
/// Bluetooth connect/disconnect via DeviceWatcher over paired Classic and LE devices, tracking the
/// System.Devices.Aep.IsConnected property. Minor event → edge pulse + short glance, never an expansion.
/// </summary>
public sealed class BluetoothProvider : ProviderBase
{
    public const string StatusId = "bluetooth.status";
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string MajorClassKey = "System.Devices.Aep.Bluetooth.Cod.Major";

    private readonly object _gate = new();
    private readonly Dictionary<string, Device> _devices = new();
    private readonly List<DeviceWatcher> _watchers = new();
    private int _enumerationsPending;

    private sealed class Device
    {
        public required string Name;
        public bool Connected;
        public bool IsAudio;
    }

    public override string Id => "bluetooth";
    public override string DisplayName => "Bluetooth";
    public override string Description => "Bluetooth headphones, mice and other devices connecting or disconnecting.";

    protected override Task OnStartAsync(CancellationToken ct)
    {
        var props = new[] { IsConnectedKey, MajorClassKey };
        foreach (var selector in new[]
                 {
                     BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                     BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                 })
        {
            var w = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
            w.Added += OnAdded;
            w.Updated += OnUpdated;
            w.Removed += OnRemoved;
            w.EnumerationCompleted += OnEnumerationCompleted;
            _watchers.Add(w);
        }
        _enumerationsPending = _watchers.Count;
        foreach (var w in _watchers) w.Start();
        return Task.CompletedTask;
    }

    protected override ValueTask OnStopAsync()
    {
        foreach (var w in _watchers)
        {
            try
            {
                w.Added -= OnAdded; w.Updated -= OnUpdated; w.Removed -= OnRemoved; w.EnumerationCompleted -= OnEnumerationCompleted;
                if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop();
            }
            catch { }
        }
        _watchers.Clear();
        return ValueTask.CompletedTask;
    }

    private bool Live => Volatile.Read(ref _enumerationsPending) <= 0;

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        Interlocked.Decrement(ref _enumerationsPending);
        Guard("status", PublishStatus);
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info) => Guard("added", () =>
    {
        lock (_gate)
        {
            _devices[info.Id] = new Device
            {
                Name = string.IsNullOrWhiteSpace(info.Name) ? "Bluetooth device" : info.Name,
                Connected = ReadBool(info.Properties, IsConnectedKey),
                IsAudio = ReadInt(info.Properties, MajorClassKey) == 4, // Class-of-Device major 4 = Audio/Video
            };
        }
        if (Live) PublishStatus();
    });

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update) => Guard("updated", () =>
    {
        if (!update.Properties.ContainsKey(IsConnectedKey)) return;
        bool connected = ReadBool(update.Properties, IsConnectedKey);
        Device? device;
        bool changed;
        lock (_gate)
        {
            if (!_devices.TryGetValue(update.Id, out device)) return;
            changed = device.Connected != connected;
            device.Connected = connected;
        }
        if (!changed || !Live) return;
        PublishStatus();

        var color = connected ? SemanticColors.Bluetooth : SemanticColors.Neutral;
        Pulse(new PulseSignal(color));
        Post(new Activity
        {
            Id = "bluetooth.event",
            SourceId = Id,
            Priority = ActivityPriority.Low,
            Category = ActivityCategory.Connectivity,
            Duration = TimeSpan.FromSeconds(2.4),
            Content = new GlanceContent(device.IsAudio ? Glyphs.Headphones : Glyphs.Bluetooth, device.Name, color,
                connected ? "Connected" : "Disconnected"),
            Accent = color,
        });
    });

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update) => Guard("removed", () =>
    {
        lock (_gate) _devices.Remove(update.Id);
        if (Live) PublishStatus();
    });

    private void PublishStatus()
    {
        List<string> connected;
        lock (_gate) connected = _devices.Values.Where(d => d.Connected).Select(d => d.Name).Distinct().ToList();
        string title = connected.Count switch
        {
            0 => "No devices",
            1 => connected[0],
            _ => $"{connected.Count} devices",
        };
        Post(new Activity
        {
            Id = StatusId,
            SourceId = Id,
            Priority = ActivityPriority.Low,
            Category = ActivityCategory.Connectivity,
            IsDormant = true,
            Content = new GenericContent(Glyphs.Bluetooth, title, connected.Count > 0 ? "Connected" : "Bluetooth"),
        });
    }

    private static bool ReadBool(IReadOnlyDictionary<string, object> p, string key) =>
        p.TryGetValue(key, out var v) && v is bool b && b;

    private static int? ReadInt(IReadOnlyDictionary<string, object> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return null;
        try { return Convert.ToInt32(v); } catch { return null; }
    }
}
