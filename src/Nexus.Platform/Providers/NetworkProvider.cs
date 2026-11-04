using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Core.Activities;
using Nexus.Core.Color;
using Nexus.Core.Content;
using Nexus.Core.Plugins;
using WinNet = Windows.Networking.Connectivity;

namespace Nexus.Platform.Providers;

/// <summary>
/// Connectivity, Wi-Fi network name, signal and VPN/tunnel state. Driven by NetworkStatusChanged and
/// NetworkAddressChanged events (debounced, because Windows emits bursts while adapters settle).
/// </summary>
public sealed class NetworkProvider : ProviderBase
{
    public const string StatusId = "network.status";
    private static readonly string[] TunnelHints =
    {
        "vpn", "wireguard", "tap-windows", "wintun", "anyconnect", "globalprotect", "pangp", "forti", "openvpn",
        "zscaler", "tailscale", "zerotier", "nordlynx", "proton", "juniper", "pulse secure", "f5 ", "netskope",
    };

    private Timer? _debounce;
    private NetworkContent? _last;

    public override string Id => "network";
    public override string DisplayName => "Network & VPN";
    public override string Description => "Wi-Fi, Ethernet, offline and VPN/tunnel changes shown as subtle edge pulses.";

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _debounce = new Timer(_ => Guard("network refresh", () => Evaluate(false)), null, Timeout.Infinite, Timeout.Infinite);
        WinNet.NetworkInformation.NetworkStatusChanged += OnStatusChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        Evaluate(initial: true);
        return Task.CompletedTask;
    }

    protected override ValueTask OnStopAsync()
    {
        WinNet.NetworkInformation.NetworkStatusChanged -= OnStatusChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _debounce?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnStatusChanged(object sender) => _debounce?.Change(900, Timeout.Infinite);
    private void OnAddressChanged(object? sender, EventArgs e) => _debounce?.Change(900, Timeout.Infinite);

    private void Evaluate(bool initial)
    {
        var next = Read();
        var prev = _last;
        if (prev == next) return;
        _last = next;

        Post(new Activity
        {
            Id = StatusId,
            SourceId = Id,
            Priority = ActivityPriority.Low,
            Category = ActivityCategory.Connectivity,
            Content = next,
            IsDormant = next.Kind != NetworkKind.None,
            CanBeForeground = false,
            Indicator = next.Kind == NetworkKind.None ? new IndicatorSpec(Glyphs.Offline, SemanticColors.Amber, "Offline") : null,
        });

        if (initial || prev is null) return;

        if (prev.Kind == NetworkKind.None && next.Kind != NetworkKind.None)
            Announce(GlyphFor(next), next.Name ?? KindLabel(next.Kind), "Connected", SemanticColors.Network);
        else if (prev.Kind != NetworkKind.None && next.Kind == NetworkKind.None)
            Announce(Glyphs.Offline, "Offline", "Network disconnected", SemanticColors.Amber);
        else if (next.Kind == NetworkKind.WiFi && prev.Name != next.Name && next.Name is not null)
            Announce(GlyphFor(next), next.Name, "Switched network", SemanticColors.Network);
        else if (next.HasInternet && !prev.HasInternet && next.Kind != NetworkKind.None)
            Pulse(new PulseSignal(SemanticColors.Network));

        if (next.VpnActive && !prev.VpnActive)
            Announce(Glyphs.Vpn, next.VpnName ?? "VPN", "Secure tunnel connected", SemanticColors.Meeting);
        else if (!next.VpnActive && prev.VpnActive)
            Announce(Glyphs.Vpn, prev.VpnName ?? "VPN", "Tunnel disconnected", SemanticColors.Neutral);
    }

    private void Announce(string glyph, string label, string detail, NexusColor color)
    {
        Pulse(new PulseSignal(color));
        Post(new Activity
        {
            Id = "network.event",
            SourceId = Id,
            Priority = ActivityPriority.Low,
            Category = ActivityCategory.Connectivity,
            Duration = TimeSpan.FromSeconds(2.4),
            Content = new GlanceContent(glyph, label, color, detail),
            Accent = color,
        });
    }

    private static string GlyphFor(NetworkContent n) => n.Kind switch
    {
        NetworkKind.WiFi => Glyphs.WifiBars(n.SignalBars),
        NetworkKind.Ethernet => Glyphs.Ethernet,
        NetworkKind.Cellular => Glyphs.Cellular,
        NetworkKind.None => Glyphs.Offline,
        _ => Glyphs.Globe,
    };

    private static string KindLabel(NetworkKind k) => k switch
    {
        NetworkKind.WiFi => "Wi-Fi",
        NetworkKind.Ethernet => "Ethernet",
        NetworkKind.Cellular => "Cellular",
        _ => "Network",
    };

    private static NetworkContent Read()
    {
        var (vpn, vpnName) = DetectTunnel();
        WinNet.ConnectionProfile? profile = null;
        try { profile = WinNet.NetworkInformation.GetInternetConnectionProfile(); } catch { }
        if (profile is null) return new NetworkContent(NetworkKind.None, null, false, vpn, vpnName, null);

        var level = profile.GetNetworkConnectivityLevel();
        bool internet = level == WinNet.NetworkConnectivityLevel.InternetAccess;
        var kind = NetworkKind.Other;
        string? name = null;
        int? bars = null;
        try
        {
            if (profile.IsWlanConnectionProfile)
            {
                kind = NetworkKind.WiFi;
                name = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                bars = profile.GetSignalBars();
            }
            else if (profile.IsWwanConnectionProfile)
            {
                kind = NetworkKind.Cellular;
                name = profile.ProfileName;
                bars = profile.GetSignalBars();
            }
            else
            {
                var iana = profile.NetworkAdapter?.IanaInterfaceType ?? 0;
                kind = iana == 6 ? NetworkKind.Ethernet : iana == 71 ? NetworkKind.WiFi : NetworkKind.Other;
                name = profile.ProfileName;
            }
        }
        catch { /* location permission may hide the SSID on newer Windows builds: degrade to no name */ }

        if (string.IsNullOrWhiteSpace(name)) name = null;
        return new NetworkContent(kind, name, internet, vpn, vpnName, bars);
    }

    private static (bool Active, string? Name) DetectTunnel()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var text = (nic.Description + " " + nic.Name).ToLowerInvariant();
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ppp || TunnelHints.Any(text.Contains))
                    return (true, Simplify(nic.Description));
            }
        }
        catch { }
        return (false, null);
    }

    private static string Simplify(string description)
    {
        var d = description.Replace("Virtual Ethernet Adapter", "").Replace("Adapter", "").Replace("Network", "").Trim();
        return d.Length > 28 ? d[..28].TrimEnd() + "…" : d;
    }
}
