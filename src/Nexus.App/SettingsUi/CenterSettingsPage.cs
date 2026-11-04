using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nexus.App.Center;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Settings;

namespace Nexus.App.SettingsUi;

/// <summary>
/// Settings › Island Center: header options, section order and visibility, volume mixer, system tiles and quick
/// actions. Edits are saved immediately and apply the next time Island Center opens.
/// </summary>
public static class CenterSettingsPage
{
    private const string Up = "\uE70E", Down = "\uE70D", Add = "\uE710";

    /// <param name="rebuild">Re-creates the page (used after reordering so arrows and indexes stay correct).</param>
    public static FrameworkElement Build(SettingsStore store, Action rebuild)
    {
        var c = store.Current.Center;
        void Change(Action<CenterSettings> mutate, bool refresh) =>
            Dispatch(() => { store.Update("center", s => mutate(s.Center)); if (refresh) rebuild(); });

        var page = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(Kit.Text("Island Center", 28, S.Text, FontWeights.SemiBold, Palette.DisplayFont));
        page.Children.Add(Wrapped("Choose what the unfolded island shows and in which order. Changes apply the next time you open Island Center.", 13, S.TextSecondary).WithMargin(0, 4, 0, 18));

        // Header
        page.Children.Add(SectionTitle("Header"));
        page.Children.Add(Card(
            Row("24-hour clock", "Off shows times like 2:42 PM.", Toggle(c.Use24HourClock, v => Change(x => x.Use24HourClock = v, false)), Glyphs.Clock),
            Row("Show date", null, Toggle(c.ShowDate, v => Change(x => x.ShowDate = v, false)), Glyphs.Calendar)));

        // Sections
        page.Children.Add(SectionTitle("Sections"));
        var sectionRows = new List<FrameworkElement>();
        for (int i = 0; i < c.Sections.Count; i++)
        {
            int index = i;
            var item = c.Sections[i];
            var controls = Kit.Row(
                IconButton(Up, index > 0, () => Change(x => CenterSettings.Move(x.Sections, index, -1), true)),
                IconButton(Down, index < c.Sections.Count - 1, () => Change(x => CenterSettings.Move(x.Sections, index, +1), true)).WithMargin(4, 0, 14, 0),
                Toggle(item.Visible, v => Change(x => x.Sections[index].Visible = v, false)));
            var (glyph, label, hint) = Describe(item.Section);
            sectionRows.Add(Row(label, hint, controls, glyph));
        }
        page.Children.Add(Card(sectionRows.ToArray()));
        page.Children.Add(Card(Row("Hide empty cards", "Hide Now playing, Volume mixer and Next meeting when there is nothing to show, instead of an empty state.",
            Toggle(c.HideEmptyCards, v => Change(x => x.HideEmptyCards = v, false)), "\uE7B3")).WithMargin(0, 8, 0, 0));

        // Volume mixer
        page.Children.Add(SectionTitle("Volume mixer"));
        page.Children.Add(Card(Row("Apps shown",
            "Apps playing sound are listed first. Use \"All apps\" in the mixer to open the full Windows volume mixer. Scroll over a row to adjust that app.",
            new SettingSlider(2, 8, c.MixerMaxApps, v => $"{Math.Round(v):0}", v => Change(x => x.MixerMaxApps = (int)Math.Round(v), false), liveUpdate: false),
            Glyphs.Speakers)));

        // System tiles
        page.Children.Add(SectionTitle("System tiles"));
        var tileRows = new List<FrameworkElement>();
        foreach (var tile in Enum.GetValues<SystemTile>())
        {
            var t = tile;
            bool on = c.SystemTiles.Contains(t);
            bool last = on && c.SystemTiles.Count == 1;
            var (glyph, label) = Describe(t);
            var toggle = Toggle(on, v => Change(x => { if (v) { if (!x.SystemTiles.Contains(t)) x.SystemTiles.Add(t); } else x.SystemTiles.Remove(t); }, true));
            if (last) { toggle.IsHitTestVisible = false; toggle.Opacity = 0.4; } // at least one tile keeps the card meaningful
            tileRows.Add(Row(label, last ? "At least one tile stays visible. Hide the whole System section above instead." : null, toggle, glyph));
        }
        page.Children.Add(Card(tileRows.ToArray()));

        // Quick actions
        page.Children.Add(SectionTitle($"Quick actions ({c.QuickActions.Count} of {CenterSettings.MaxQuickActions})"));
        var shown = new List<FrameworkElement>();
        for (int i = 0; i < c.QuickActions.Count; i++)
        {
            int index = i;
            var action = c.QuickActions[i];
            var controls = Kit.Row(
                IconButton(Up, index > 0, () => Change(x => CenterSettings.Move(x.QuickActions, index, -1), true)),
                IconButton(Down, index < c.QuickActions.Count - 1, () => Change(x => CenterSettings.Move(x.QuickActions, index, +1), true)).WithMargin(4, 0, 4, 0),
                IconButton(Glyphs.Close, true, () => Change(x => x.QuickActions.Remove(action), true)));
            shown.Add(Row(QuickActionCatalog.LongLabel(action), null, controls, QuickActionCatalog.Describe(action).Glyph));
        }
        if (shown.Count == 0) shown.Add(Row("No quick actions", "Add some below, or hide the Quick actions section.", null, Glyphs.Info));
        page.Children.Add(Card(shown.ToArray()));

        var available = Enum.GetValues<QuickAction>().Where(a => !c.QuickActions.Contains(a)).ToList();
        if (available.Count > 0)
        {
            bool full = c.QuickActions.Count >= CenterSettings.MaxQuickActions;
            var wrap = new WrapPanel { Margin = new Thickness(12) };
            foreach (var a in available)
            {
                var action = a;
                var (_, label) = QuickActionCatalog.Describe(action);
                var b = TextButton(label, Add, () => Change(x => { if (x.QuickActions.Count < CenterSettings.MaxQuickActions) x.QuickActions.Add(action); }, true));
                b.IsEnabledVisual = !full;
                b.ToolTip = QuickActionCatalog.LongLabel(action);
                wrap.Children.Add(b.WithMargin(4, 4, 4, 4));
            }
            page.Children.Add(Wrapped(full ? "Remove an action to add another." : "Add an action:", 12, S.TextTertiary).WithMargin(2, 12, 0, 4));
            page.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = S.Card, BorderBrush = S.CardStroke, BorderThickness = new Thickness(1), Child = wrap });
        }

        page.Children.Add(TextButton("Reset Island Center to default", "\uE72C", () => Change(x =>
        {
            var d = new CenterSettings();
            x.Sections = d.Sections; x.SystemTiles = d.SystemTiles; x.QuickActions = d.QuickActions;
            x.Use24HourClock = d.Use24HourClock; x.ShowDate = d.ShowDate; x.HideEmptyCards = d.HideEmptyCards;
            x.MixerMaxApps = d.MixerMaxApps;
        }, true)).WithMargin(0, 18, 0, 0));
        return page;
    }

    // Rebuilding the page from inside a click handler of one of its own buttons is safe once the handler returns.
    private static void Dispatch(Action a) => Application.Current.Dispatcher.InvokeAsync(a);

    private static (string Glyph, string Label, string Hint) Describe(CenterSection s) => s switch
    {
        CenterSection.Privacy => (Glyphs.Shield, "Privacy", "Shows which apps use your microphone or camera – only while they do."),
        CenterSection.NowPlaying => (Glyphs.Music, "Now playing", "Artwork, track and playback controls."),
        CenterSection.Shelf => (Glyphs.Shelf, "File Shelf", "Files you dropped on the island, ready to drag into any app."),
        CenterSection.Mixer => (Glyphs.Speakers, "Volume mixer", "Volume and mute for each app, like the Windows volume mixer."),
        CenterSection.Meeting => (Glyphs.Calendar, "Next meeting", "Upcoming meeting with Join, or the call in progress."),
        CenterSection.SystemStatus => (Glyphs.Display, "System", "Audio, battery, network and Bluetooth tiles."),
        _ => (Glyphs.Grid, "Quick actions", "One-click toggles and shortcuts."),
    };

    private static (string Glyph, string Label) Describe(SystemTile t) => t switch
    {
        SystemTile.Audio => (Glyphs.Speakers, "Audio output"),
        SystemTile.Battery => (Glyphs.Battery(80, false), "Battery / power"),
        SystemTile.Network => (Glyphs.Wifi, "Network & VPN"),
        _ => (Glyphs.Bluetooth, "Bluetooth"),
    };

    #region Building blocks (match the rest of Settings)

    private static TextBlock Wrapped(string text, double size, Brush brush)
    {
        var t = Kit.Text(text, size, brush);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    private static TextBlock SectionTitle(string text) => Kit.Text(text, 13, S.Text, FontWeights.SemiBold).WithMargin(2, 18, 0, 8);

    private static Border Card(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = S.Divider });
            stack.Children.Add(rows[i]);
        }
        return new Border { CornerRadius = new CornerRadius(10), Background = S.Card, BorderBrush = S.CardStroke, BorderThickness = new Thickness(1), Child = stack };
    }

    private static Grid Row(string label, string? description, FrameworkElement? control, string? glyph)
    {
        var g = new Grid { MinHeight = 56, Margin = new Thickness(18, 8, 18, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph is not null) g.Children.Add(Kit.Glyph(glyph, 16, S.TextSecondary).WithWidth(20).WithMargin(0, 0, 16, 0));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(Kit.Text(label, 13.5, S.Text));
        if (description is not null) text.Children.Add(Wrapped(description, 12, S.TextTertiary).WithMargin(0, 2, 0, 0));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2);
            g.Children.Add(control);
        }
        return g;
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> changed)
    {
        var t = new ToggleSwitch(value);
        t.Toggled += changed;
        return t;
    }

    private static NexusButton IconButton(string glyph, bool enabled, Action onClick)
    {
        var b = new NexusButton(Kit.Glyph(glyph, 11, S.Text), S.ControlRest, S.ControlHot, 7) { Width = 30, Height = 30 };
        b.Click += (_, _) => onClick();
        b.IsEnabledVisual = enabled;
        return b;
    }

    private static NexusButton TextButton(string label, string glyph, Action onClick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Kit.Glyph(glyph, 11, S.Text).WithMargin(0, 0, 8, 0));
        row.Children.Add(Kit.Text(label, 12.5, S.Text));
        var b = new NexusButton(row, S.ControlRest, S.ControlHot, 7) { Height = 32, Padding = new Thickness(12, 0, 14, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    #endregion
}
