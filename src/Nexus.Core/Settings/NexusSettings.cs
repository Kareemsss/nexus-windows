using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Core.Scheduling;
using Nexus.Core.State;

namespace Nexus.Core.Settings;

public enum ThemeKind { Obsidian, Fluent, Aurora, Cyber, Minimal, Custom }
public enum AccentBehavior { Contextual, ThemeAccent, Monochrome }
public enum IdleStyle { Capsule, Line, Dot }
public enum IslandAnchor { TopCenter, TopLeft, TopRight, BottomCenter, Custom }
public enum MonitorMode { Primary, Specific, FollowCursor }

/// <summary>Every user preference. Stored locally as JSON; nothing ever leaves the machine.</summary>
public sealed class NexusSettings
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public PositionSettings Position { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
    public ModuleSettings Modules { get; set; } = new();
    public CenterSettings Center { get; set; } = new();
    public ShelfSettings Shelf { get; set; } = new();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public NexusSettings Clone() =>
        JsonSerializer.Deserialize<NexusSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions) ?? new NexusSettings();

    /// <summary>Repairs out-of-range values from hand-edited or older files.</summary>
    public void Normalize()
    {
        General ??= new(); Appearance ??= new(); Position ??= new(); Behavior ??= new(); Modules ??= new();
        Appearance.Custom ??= new();
        Modules.Enabled ??= new(StringComparer.OrdinalIgnoreCase);
        Center ??= new();
        Center.Normalize();
        Shelf ??= new();
        var c = Appearance.Custom;
        c.Transparency = Math.Clamp(c.Transparency, 0, 0.6);
        c.Frost = Math.Clamp(c.Frost, 0, 1);
        c.Glow = Math.Clamp(c.Glow, 0, 1);
        c.CornerRoundness = Math.Clamp(c.CornerRoundness, 0.25, 1);
        Appearance.Scale = Math.Clamp(Appearance.Scale, 0.8, 1.5);
        Appearance.AnimationSpeed = Math.Clamp(Appearance.AnimationSpeed, 0.5, 2.0);
        Position.CustomX = Math.Clamp(Position.CustomX, 0, 1);
        Position.EdgeOffset = Math.Clamp(Position.EdgeOffset, 0, 64);
        Behavior.AutoCollapseSeconds = Math.Clamp(Behavior.AutoCollapseSeconds, 0.5, 15);
        Behavior.SongPopupSeconds = Math.Clamp(Behavior.SongPopupSeconds, 1, 10);
    }
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    /// <summary>Closing the settings window keeps NEXUS running in the background.</summary>
    public bool KeepRunningWhenSettingsClosed { get; set; } = true;
    /// <summary>Load third-party provider assemblies from %LOCALAPPDATA%\Nexus\plugins. Off by default.</summary>
    public bool LoadPlugins { get; set; }
}

/// <summary>Theme-specific tuning. Presets supply their own values; editing them produces the Custom theme.</summary>
public sealed class ThemeTuning
{
    /// <summary>0 = nearly opaque smoked glass, 0.6 = very see-through.</summary>
    public double Transparency { get; set; } = 0.14;
    /// <summary>Strength of the luminous frosted diffusion layer inside the glass.</summary>
    public double Frost { get; set; } = 0.45;
    public double Glow { get; set; } = 0.6;
    /// <summary>1 = full capsule, lower values produce a softer rounded rectangle.</summary>
    public double CornerRoundness { get; set; } = 1.0;
    public AccentBehavior Accent { get; set; } = AccentBehavior.Contextual;
}

public sealed class AppearanceSettings
{
    public ThemeKind Theme { get; set; } = ThemeKind.Obsidian;
    /// <summary>Preset the Custom theme is derived from (colours, edge style).</summary>
    public ThemeKind CustomBase { get; set; } = ThemeKind.Obsidian;
    public ThemeTuning Custom { get; set; } = new();
    public double Scale { get; set; } = 1.0;
    public double AnimationSpeed { get; set; } = 1.0;
    public bool ReduceMotion { get; set; }
    public bool AnimatedEqualizer { get; set; } = true;
    public IdleStyle IdleStyle { get; set; } = IdleStyle.Capsule;
    /// <summary>Lower RAM/CPU: 30 FPS cap, static equalizer, lighter shadow, more frequent memory trims.</summary>
    public bool EcoMode { get; set; }
}

public sealed class PositionSettings
{
    public IslandAnchor Anchor { get; set; } = IslandAnchor.TopCenter;
    /// <summary>Horizontal position (0..1 of the monitor width) for <see cref="IslandAnchor.Custom"/>.</summary>
    public double CustomX { get; set; } = 0.5;
    /// <summary>Distance from the screen edge in DIPs.</summary>
    public double EdgeOffset { get; set; } = 8;
    public MonitorMode Monitor { get; set; } = MonitorMode.Primary;
    /// <summary>Device name (\\.\DISPLAY2) for <see cref="MonitorMode.Specific"/>.</summary>
    public string? MonitorDevice { get; set; }
}

public sealed class BehaviorSettings
{
    public double AutoCollapseSeconds { get; set; } = 2.5;
    public HoverBehavior Hover { get; set; } = HoverBehavior.Illuminate;
    public ClickBehavior Click { get; set; } = ClickBehavior.ExpandThenCenter;
    public FullscreenBehavior Fullscreen { get; set; } = FullscreenBehavior.Gaming;
    public bool ScrollAdjustsVolume { get; set; } = true;
    public bool RevealOnEdgeHover { get; set; } = true;
    public bool QuietMode { get; set; }
    /// <summary>Keep the island hidden at all times (critical alerts, song pop-ups and edge reveal still work).</summary>
    public bool HideCompletely { get; set; }
    /// <summary>Show new songs briefly, then tuck away, instead of keeping music on the island while it plays.</summary>
    public bool SongPopup { get; set; }
    public double SongPopupSeconds { get; set; } = 3;
    /// <summary>When off, notifications show only the app name – useful when screen sharing.</summary>
    public bool ShowNotificationPreview { get; set; } = true;
    public bool ShowMeetingTitles { get; set; } = true;
}

/// <summary>File Shelf preferences. The shelf itself is switched on and off as a module ("shelf").</summary>
public sealed class ShelfSettings
{
    /// <summary>Save the shelf's paths to shelf.json so it survives a restart. Off: memory only.</summary>
    public bool Remember { get; set; } = true;
    /// <summary>Keep an item after it was dragged into another app (by default it leaves the shelf).</summary>
    public bool KeepAfterDragOut { get; set; }
}

public sealed class ModuleSettings
{
    public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Optional ICS calendar feed (local file path or https URL) for upcoming meetings.</summary>
    public string? CalendarFeed { get; set; }

    public bool IsEnabled(string providerId) => !Enabled.TryGetValue(providerId, out var on) || on;
}
