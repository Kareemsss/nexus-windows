using Nexus.Core.Activities;
using Nexus.Core.Settings;

namespace Nexus.App.Center;

/// <summary>Glyph, label and launch target of every quick action. Shared by Island Center and Settings.</summary>
public static class QuickActionCatalog
{
    public static (string Glyph, string Label) Describe(QuickAction a) => a switch
    {
        QuickAction.Quiet => (Glyphs.QuietHours, "Quiet"),
        QuickAction.Mic => (Glyphs.Microphone, "Mic"),
        QuickAction.Sound => (Glyphs.Speakers, "Sound"),
        QuickAction.Network => (Glyphs.Wifi, "Network"),
        QuickAction.Bluetooth => (Glyphs.Bluetooth, "Bluetooth"),
        QuickAction.Display => (Glyphs.Display, "Display"),
        QuickAction.Battery => (Glyphs.BatterySaver, "Battery"),
        QuickAction.Screenshot => (Glyphs.Screenshot, "Snip"),
        QuickAction.Clipboard => (Glyphs.Copy, "Clipboard"),
        QuickAction.TaskManager => ("\uE9D9", "Task Mgr"),
        QuickAction.Calculator => ("\uE8EF", "Calculator"),
        _ => (Glyphs.Settings, "Settings"),
    };

    public static string LongLabel(QuickAction a) => a switch
    {
        QuickAction.Quiet => "Quiet mode (Do Not Disturb)",
        QuickAction.Mic => "Mute / unmute microphone",
        QuickAction.Sound => "Sound settings",
        QuickAction.Network => "Network settings",
        QuickAction.Bluetooth => "Bluetooth settings",
        QuickAction.Display => "Display settings",
        QuickAction.Battery => "Battery & energy saver",
        QuickAction.Screenshot => "Snip a screenshot",
        QuickAction.Clipboard => "Clipboard settings",
        QuickAction.TaskManager => "Task Manager",
        QuickAction.Calculator => "Calculator",
        _ => "NEXUS settings",
    };

    /// <summary>Shell target for launch-type actions; null for actions NEXUS handles itself.</summary>
    public static string? Target(QuickAction a) => a switch
    {
        QuickAction.Sound => "ms-settings:sound",
        QuickAction.Network => "ms-settings:network",
        QuickAction.Bluetooth => "ms-settings:bluetooth",
        QuickAction.Display => "ms-settings:display",
        QuickAction.Battery => "ms-settings:batterysaver",
        QuickAction.Screenshot => "ms-screenclip:",
        QuickAction.Clipboard => "ms-settings:clipboard",
        QuickAction.TaskManager => "taskmgr.exe",
        QuickAction.Calculator => "calculator:",
        _ => null,
    };
}
