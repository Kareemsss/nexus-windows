using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Core.Settings;

public enum CenterSection { Privacy, NowPlaying, Meeting, SystemStatus, QuickActions, Mixer, Shelf }
public enum SystemTile { Audio, Battery, Network, Bluetooth }
public enum QuickAction { Quiet, Mic, Sound, Network, Bluetooth, Display, Battery, Screenshot, Clipboard, TaskManager, Calculator, Settings }

public sealed class CenterSectionItem
{
    public CenterSection Section { get; set; }
    public bool Visible { get; set; } = true;
}

/// <summary>Layout of Island Center: which cards appear, their order, tiles and quick actions.</summary>
public sealed class CenterSettings
{
    public const int MaxQuickActions = 6;

    /// <summary>Default order. New sections added in later versions are slotted in at their default position.</summary>
    public static readonly CenterSection[] DefaultOrder =
    {
        CenterSection.Privacy, CenterSection.NowPlaying, CenterSection.Shelf, CenterSection.Mixer,
        CenterSection.Meeting, CenterSection.SystemStatus, CenterSection.QuickActions,
    };

    public List<CenterSectionItem> Sections { get; set; } = DefaultSections();
    public List<SystemTile> SystemTiles { get; set; } = new() { SystemTile.Audio, SystemTile.Battery, SystemTile.Network, SystemTile.Bluetooth };
    public List<QuickAction> QuickActions { get; set; } = new()
        { QuickAction.Quiet, QuickAction.Mic, QuickAction.Sound, QuickAction.Network, QuickAction.Bluetooth, QuickAction.Settings };
    public bool Use24HourClock { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    /// <summary>Hide Now playing / Shelf / Volume mixer / Next meeting when they have nothing to show.</summary>
    public bool HideEmptyCards { get; set; }
    /// <summary>How many apps the volume mixer lists (the rest are one click away in Windows settings).</summary>
    public int MixerMaxApps { get; set; } = 3;

    public static List<CenterSectionItem> DefaultSections() =>
        DefaultOrder.Select(s => new CenterSectionItem { Section = s }).ToList();

    public bool IsVisible(CenterSection s) => Sections.FirstOrDefault(x => x.Section == s)?.Visible ?? true;

    /// <summary>Repairs hand-edited or older files: every section exactly once, no duplicates, limits respected.</summary>
    public void Normalize()
    {
        Sections ??= new();
        SystemTiles ??= new();
        QuickActions ??= new();
        var seen = new HashSet<CenterSection>();
        Sections = Sections.Where(s => s is not null && Enum.IsDefined(s.Section) && seen.Add(s.Section)).ToList();

        // Insert sections that are missing (e.g. Mixer for settings saved by an older version) right after the
        // section that precedes them in the default order, instead of dumping them at the end.
        for (int i = 0; i < DefaultOrder.Length; i++)
        {
            var s = DefaultOrder[i];
            if (seen.Contains(s)) continue;
            int at = 0;
            for (int j = i - 1; j >= 0; j--)
            {
                int idx = Sections.FindIndex(x => x.Section == DefaultOrder[j]);
                if (idx >= 0) { at = idx + 1; break; }
            }
            Sections.Insert(at, new CenterSectionItem { Section = s });
            seen.Add(s);
        }

        SystemTiles = SystemTiles.Where(t => Enum.IsDefined(t)).Distinct().ToList();
        if (SystemTiles.Count == 0) SystemTiles.Add(SystemTile.Battery);
        QuickActions = QuickActions.Where(a => Enum.IsDefined(a)).Distinct().Take(MaxQuickActions).ToList();
        MixerMaxApps = Math.Clamp(MixerMaxApps, 2, 8);
    }

    public static void Move<T>(List<T> list, int index, int delta)
    {
        int j = index + delta;
        if (index < 0 || index >= list.Count || j < 0 || j >= list.Count) return;
        (list[index], list[j]) = (list[j], list[index]);
    }
}
