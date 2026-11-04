using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Core.Color;

namespace Nexus.Core.Content;

// Content models are plain immutable records. Widgets render them; providers produce them.
// They hold no UI types so they can be produced off the UI thread and unit-tested.

#region Media

public enum MediaPlayback { Playing, Paused, Stopped, Changing, Unknown }

public interface IMediaControls
{
    Task TogglePlayPauseAsync();
    Task NextAsync();
    Task PreviousAsync();
    Task SeekAsync(TimeSpan position);
}

public sealed record MediaContent(
    string Title,
    string Artist,
    string Album,
    string SourceApp,
    MediaPlayback Playback,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionSampledAt,
    byte[]? Artwork,
    string ArtworkKey,
    NexusColor? ArtworkAccent,
    bool CanPrevious,
    bool CanNext,
    bool CanTogglePlay,
    bool CanSeek,
    IMediaControls Controls)
{
    /// <summary>Extrapolated playback position so the UI can tick smoothly without asking Windows.</summary>
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        var p = Position;
        if (Playback == MediaPlayback.Playing) p += now - PositionSampledAt;
        if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }
}

#endregion

#region Audio

public enum AudioDeviceKind { Speakers, Headphones, Headset, Bluetooth, Display, Other }

public interface IVolumeControls
{
    void SetLevel(float level);
    void SetMuted(bool muted);
    void Step(int deltaPercent);
}

public sealed record VolumeContent(float Level, bool Muted, string DeviceName, AudioDeviceKind DeviceKind, IVolumeControls Controls);

#endregion

#region Power

public enum PowerState { Discharging, Charging, Full, PluggedIn, NoBattery }
public enum BatteryBand { Normal, Low, Critical }

public sealed record BatteryContent(int Percent, PowerState State, BatteryBand Band, TimeSpan? Remaining, bool EnergySaverOn)
{
    public bool OnExternalPower => State is PowerState.Charging or PowerState.Full or PowerState.PluggedIn;
}

#endregion

#region Network

public enum NetworkKind { None, WiFi, Ethernet, Cellular, Other }

public sealed record NetworkContent(NetworkKind Kind, string? Name, bool HasInternet, bool VpnActive, string? VpnName, int? SignalBars);

#endregion

#region Privacy

public enum PrivacyResource { Microphone, Camera }

public sealed record PrivacyUser(string AppName, string? ExecutablePath);

public sealed record PrivacyContent(PrivacyResource Resource, IReadOnlyList<PrivacyUser> Users);

#endregion

#region Meetings

public enum MeetingPhase { Upcoming, Starting, Active }

public sealed record MeetingContent(
    string Title,
    string Platform,
    MeetingPhase Phase,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    DateTimeOffset? ActiveSince,
    string? JoinUrl,
    bool MicActive,
    bool MicMuted,
    bool CameraActive);

#endregion

#region Notifications & generic

public sealed record NotificationContent(string AppName, byte[]? AppIcon, string Title, string Body, DateTimeOffset Time, string? Glyph = null);

/// <summary>Short-lived label shown together with an edge pulse ("Connected · WH-1000XM5").</summary>
public sealed record GlanceContent(string Glyph, string Label, NexusColor Color, string? Detail = null);

/// <summary>
/// General purpose content for plugins that do not ship their own widget (Docker, GitHub, AWS, timers...).
/// Rendered by the built-in generic widget with optional determinate/indeterminate progress.
/// </summary>
public sealed record GenericContent(string Glyph, string Title, string? Subtitle = null, double? Progress = null, bool Indeterminate = false, NexusColor? Color = null);

#endregion
