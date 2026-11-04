using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Nexus.App.Composition;
using Nexus.App.Shelf;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Plugins;
using Nexus.Core.Scheduling;
using Nexus.Core.Settings;
using Nexus.Core.State;
using Nexus.Platform.Interop;
using Nexus.Platform.Providers;
using Nexus.Platform.Services;

namespace Nexus.App.SettingsUi;

/// <summary>
/// NEXUS settings. A standard resizable window (it is an app, not an overlay) using Windows 11 Mica with
/// native caption buttons, falling back to a solid dark surface on Windows 10. Every change applies live.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly ProviderManager _providers;
    private readonly PreviewEvents _preview;
    private readonly Action _exit;
    private readonly ContentControl _page = new() { Focusable = false };
    private readonly StackPanel _nav = new();
    private readonly List<(string Key, NexusButton Button, TextBlock Label, TextBlock Glyph)> _navItems = new();
    private readonly ScrollViewer _scroller;
    private Action? _refreshModules;
    private bool _allowClose;

    public SettingsWindow(SettingsStore settings, ProviderManager providers, PreviewEvents preview, Action exit)
    {
        _settings = settings;
        _providers = providers;
        _preview = preview;
        _exit = exit;

        Title = "NEXUS Settings";
        Width = 980;
        Height = 700;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Palette.TextFont;
        Foreground = S.Text;
        Background = Brushes.Transparent;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "nexus.ico");
        if (File.Exists(iconPath)) Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(iconPath));

        // Extend the frame into the whole client area: DWM keeps drawing the native caption buttons, and
        // the system backdrop (Mica) shows through our transparent background.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = true,
        });
        SourceInitialized += (_, _) => ApplyBackdrop();

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildNav());
        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _page,
            Padding = new Thickness(8, 46, 28, 28),
            Focusable = false,
        };
        Grid.SetColumn(_scroller, 1);
        root.Children.Add(_scroller);
        Content = root;

        EventHandler onStatus = (_, _) => Dispatcher.InvokeAsync(() => _refreshModules?.Invoke());
        providers.StatusChanged += onStatus;
        Closed += (_, _) => providers.StatusChanged -= onStatus;
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_settings.Current.General.KeepRunningWhenSettingsClosed) e.Cancel = false; // close for real: frees the settings UI from RAM
            else _exit();
        };
        Navigate("general");
    }

    /// <summary>Closes for real (application exit), bypassing hide-on-close.</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        bool mica = false;
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            int backdrop = 2; // DWMSBT_MAINWINDOW (Mica)
            mica = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
        }
        if (mica)
        {
            // WPF clears its surface to opaque black unless told otherwise.
            if (HwndSource.FromHwnd(hwnd) is { } src) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        else
        {
            Background = Palette.Freeze(Color.FromRgb(0x1C, 0x1C, 0x1F)); // Windows 10 / older 11 builds
        }
    }

    #region Navigation

    private FrameworkElement BuildNav()
    {
        var mark = new Border
        {
            Width = 30, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 12, 0),
            Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x2C, 0x33), Color.FromRgb(0x0C, 0x0C, 0x10), 90),
            BorderBrush = Palette.Freeze(Color.FromArgb(0x60, 0xA9, 0xBC, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        var title = Kit.Row(mark, Kit.Column(
            Kit.Text("NEXUS", 15, S.Text, FontWeights.SemiBold),
            Kit.Text("Your Windows Activity Center", 11, S.TextTertiary)));
        title.Margin = new Thickness(20, 48, 16, 22);
        _nav.Children.Add(title);

        AddNav("general", Glyphs.Settings, "General");
        AddNav("appearance", "\uE790", "Appearance");
        AddNav("center", "\uE8A9", "Island Center");
        AddNav("position", "\uE7F4", "Position & displays");
        AddNav("behavior", "\uE8AB", "Behavior");
        AddNav("modules", Glyphs.Grid, "Modules");
        AddNav("privacy", Glyphs.Shield, "Privacy");
        AddNav("preview", "\uE768", "Preview lab");
        AddNav("about", Glyphs.Info, "About");
        return _nav;
    }

    private void AddNav(string key, string glyph, string label)
    {
        var g = Kit.Glyph(glyph, 14, S.TextSecondary);
        g.Width = 20;
        var t = Kit.Text(label, 13, S.Text);
        var row = Kit.Row(g.WithMargin(0, 0, 12, 0), t);
        var b = new NexusButton(row, Colors.Transparent, S.ControlHot, 7) { Height = 38, Margin = new Thickness(10, 1, 10, 1), Padding = new Thickness(12, 0, 12, 0) };
        b.Click += (_, _) => Navigate(key);
        _navItems.Add((key, b, t, g));
        _nav.Children.Add(b);
    }

    private void Navigate(string key)
    {
        _refreshModules = null;
        foreach (var (k, b, label, glyph) in _navItems)
        {
            bool on = k == key;
            b.SetColors(on ? Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF) : Colors.Transparent, on ? Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF) : S.ControlHot);
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            glyph.Foreground = on ? S.Accent : S.TextSecondary;
        }
        _page.Content = key switch
        {
            "appearance" => AppearancePage(),
            "center" => CenterSettingsPage.Build(_settings, () => Navigate("center")),
            "position" => PositionPage(),
            "behavior" => BehaviorPage(),
            "modules" => ModulesPage(),
            "privacy" => PrivacyPage(),
            "preview" => PreviewPage(),
            "about" => AboutPage(),
            _ => GeneralPage(),
        };
        _scroller.ScrollToTop();
    }

    #endregion

    #region Building blocks

    private static StackPanel Page(string title, string subtitle)
    {
        var p = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(Kit.Text(title, 28, S.Text, FontWeights.SemiBold, Palette.DisplayFont));
        var sub = Kit.Text(subtitle, 13, S.TextSecondary).WithMargin(0, 4, 0, 18);
        sub.TextWrapping = TextWrapping.Wrap;
        p.Children.Add(sub);
        return p;
    }

    private static TextBlock SectionTitle(string text) =>
        Kit.Text(text, 13, S.Text, FontWeights.SemiBold).WithMargin(2, 18, 0, 8);

    /// <summary>A card containing rows separated by hairlines.</summary>
    private static Border Card(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = S.Divider });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            CornerRadius = new CornerRadius(10), Background = S.Card, BorderBrush = S.CardStroke,
            BorderThickness = new Thickness(1), Child = stack, Margin = new Thickness(0, 0, 0, 4),
        };
    }

    private static Grid Row(string label, string? description, FrameworkElement? control, string? glyph = null)
    {
        var g = new Grid { MinHeight = 64, Margin = new Thickness(18, 10, 18, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph is not null) g.Children.Add(Kit.Glyph(glyph, 16, S.TextSecondary).WithMargin(0, 0, 16, 0));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        text.Children.Add(Kit.Text(label, 13.5, S.Text));
        if (description is not null)
        {
            var d = Kit.Text(description, 12, S.TextTertiary).WithMargin(0, 2, 0, 0);
            d.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(d);
        }
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

    /// <summary>Label/description on top, wide control (segmented, text box) underneath.</summary>
    private static Grid StackedRow(string label, string? description, FrameworkElement control)
    {
        var g = Row(label, description, null);
        var text = g.Children.OfType<StackPanel>().First();
        text.Children.Add(control.WithMargin(0, 12, 0, 0));
        return g;
    }

    private ToggleSwitch Toggle(bool value, string section, Action<NexusSettings, bool> set)
    {
        var t = new ToggleSwitch(value);
        t.Toggled += on => _settings.Update(section, s => set(s, on));
        return t;
    }

    private static NexusButton ActionButton(string label, Action onClick, string? glyph = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null) row.Children.Add(Kit.Glyph(glyph, 12, S.Text).WithMargin(0, 0, 8, 0));
        row.Children.Add(Kit.Text(label, 12.5, S.Text));
        var b = new NexusButton(row, S.ControlRest, S.ControlHot, 7) { Height = 32, Padding = new Thickness(14, 0, 14, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    #endregion

    #region Pages

    private FrameworkElement GeneralPage()
    {
        var g = _settings.Current.General;
        var p = Page("General", "NEXUS lives at the top of your screen and stays out of the way until something matters.");
        p.Children.Add(Card(
            Row("Start with Windows", "Launch NEXUS quietly when you sign in.", Toggle(g.StartWithWindows, "general", (s, v) => s.General.StartWithWindows = v), Glyphs.Power),
            Row("Show tray icon", "Left-click opens Island Center, right-click opens the menu.", Toggle(g.ShowTrayIcon, "general", (s, v) => s.General.ShowTrayIcon = v), "\uE9F5"),
            Row("Keep running when this window closes", "Closing Settings minimises NEXUS to the background. Turn off to exit NEXUS instead.",
                Toggle(g.KeepRunningWhenSettingsClosed, "general", (s, v) => s.General.KeepRunningWhenSettingsClosed = v), "\uE921")));
        p.Children.Add(SectionTitle("Updates"));
        p.Children.Add(Card(Row("Update preferences", "NEXUS never phones home: it does not check for updates or send any data. Install new versions manually.", null, "\uE895")));
        p.Children.Add(SectionTitle("Extensibility"));
        var pluginDir = Path.Combine(NexusHost.DataDirectory, "plugins");
        p.Children.Add(Card(
            Row("Load plugins", "Load third-party providers from the plugins folder at next start. Only enable for plugins you trust.",
                Toggle(g.LoadPlugins, "general", (s, v) => s.General.LoadPlugins = v), "\uEA86"),
            Row("Plugins folder", pluginDir, ActionButton("Open", () => { Directory.CreateDirectory(pluginDir); ShellService.Open(pluginDir); }, Glyphs.Folder))));
        p.Children.Add(SectionTitle("Session"));
        p.Children.Add(Card(Row("Exit NEXUS", "Stops all modules and removes the island until you start NEXUS again.", ActionButton("Exit", _exit))));
        return p;
    }

    private FrameworkElement AppearancePage()
    {
        var a = _settings.Current.Appearance;
        var p = Page("Appearance", "Themes share one design system. Changes preview live on the island.");
        var theme = ThemeEngine.Resolve(a);

        var themes = new Segmented<ThemeKind>(
            new[] { (ThemeKind.Obsidian, "Obsidian"), (ThemeKind.Fluent, "Fluent"), (ThemeKind.Aurora, "Aurora"), (ThemeKind.Cyber, "Cyber"), (ThemeKind.Minimal, "Minimal"), (ThemeKind.Custom, "Custom") },
            a.Theme,
            k =>
            {
                _settings.Update("appearance", s =>
                {
                    if (k == ThemeKind.Custom && s.Appearance.Theme != ThemeKind.Custom)
                    {
                        s.Appearance.CustomBase = s.Appearance.Theme;
                        s.Appearance.Custom = ThemeEngine.TuningOf(ThemePresets.Get(s.Appearance.Theme));
                    }
                    s.Appearance.Theme = k;
                });
                
                Navigate("appearance");
            });
        p.Children.Add(Card(StackedRow("Theme", "Obsidian is the default: smoked glass with restrained contextual light. Use Preview lab to try it with sample events.", themes)));

        p.Children.Add(SectionTitle("Glass"));
        p.Children.Add(Card(
            Row("Transparency", "How much of the desktop shows through the glass.", TuningSlider(0, 0.6, theme.Transparency, v => $"{v * 100:0}%", (t, v) => t.Transparency = v)),
            Row("Frost (blur substitute)", "Luminous diffusion inside the glass. True backdrop blur is not available to animated, shaped overlay windows – see README.", TuningSlider(0, 1, theme.Frost, v => $"{v * 100:0}%", (t, v) => t.Frost = v)),
            Row("Glow intensity", "Strength of ambient light from album art, battery state and other context.", TuningSlider(0, 1, theme.Glow, v => $"{v * 100:0}%", (t, v) => t.Glow = v)),
            Row("Corner roundness", "Capsule at 100%, softer rectangle below.", TuningSlider(0.25, 1, theme.CornerRoundness, v => $"{v * 100:0}%", (t, v) => t.CornerRoundness = v)),
            StackedRow("Accent behavior", "Contextual follows what is happening (album art, battery, privacy).", new Segmented<AccentBehavior>(
                new[] { (AccentBehavior.Contextual, "Contextual"), (AccentBehavior.ThemeAccent, "Theme accent"), (AccentBehavior.Monochrome, "Monochrome") },
                theme.Accent, v => UpdateTuning(t => t.Accent = v)))));

        p.Children.Add(SectionTitle("Size & motion"));
        p.Children.Add(Card(
            Row("Island scale", null, new SettingSlider(0.8, 1.5, a.Scale, v => $"{v * 100:0}%", v => _settings.Update("appearance", s => s.Appearance.Scale = Math.Round(v, 2)), liveUpdate: false)),
            Row("Animation speed", null, new SettingSlider(0.5, 2, a.AnimationSpeed, v => $"{v:0.0}×", v => _settings.Update("appearance", s => s.Appearance.AnimationSpeed = Math.Round(v, 2)))),
            Row("Reduce motion", "Replaces springs with short, non-bouncing transitions and removes decorative motion.",
                Toggle(a.ReduceMotion, "appearance", (s, v) => s.Appearance.ReduceMotion = v)),
            Row("Eco mode", "Lower RAM and CPU: 30 FPS animations, static equalizer, lighter shadow and more frequent memory release. Recommended on 8 GB PCs and on battery.",
                Toggle(a.EcoMode, "appearance", (s, v) => s.Appearance.EcoMode = v)),
            Row("Animated equalizer", "Subtle bars while music plays (capped at 24 FPS).",
                Toggle(a.AnimatedEqualizer, "appearance", (s, v) => s.Appearance.AnimatedEqualizer = v)),
            StackedRow("Idle appearance", "How the island looks when nothing is happening.", new Segmented<IdleStyle>(
                new[] { (IdleStyle.Capsule, "Capsule"), (IdleStyle.Line, "Line"), (IdleStyle.Dot, "Dot") },
                a.IdleStyle, v => _settings.Update("appearance", s => s.Appearance.IdleStyle = v)))));
        return p;
    }

    private SettingSlider TuningSlider(double min, double max, double value, Func<double, string> format, Action<ThemeTuning, double> set) =>
        new(min, max, value, format, v => UpdateTuning(t => set(t, Math.Round(v, 3))));

    /// <summary>Editing a preset's tuning forks it into the Custom theme, keeping the preset's colours.</summary>
    private void UpdateTuning(Action<ThemeTuning> change)
    {
        _settings.Update("appearance", s =>
        {
            if (s.Appearance.Theme != ThemeKind.Custom)
            {
                s.Appearance.CustomBase = s.Appearance.Theme;
                s.Appearance.Custom = ThemeEngine.TuningOf(ThemePresets.Get(s.Appearance.Theme));
                s.Appearance.Theme = ThemeKind.Custom;
            }
            change(s.Appearance.Custom);
        });
    }

    private FrameworkElement PositionPage()
    {
        var pos = _settings.Current.Position;
        var p = Page("Position & displays", "NEXUS is DPI-aware on every monitor and re-anchors when displays change.");
        p.Children.Add(Card(
            StackedRow("Placement", null, new Segmented<IslandAnchor>(
                new[] { (IslandAnchor.TopCenter, "Top center"), (IslandAnchor.TopLeft, "Top left"), (IslandAnchor.TopRight, "Top right"), (IslandAnchor.BottomCenter, "Bottom center"), (IslandAnchor.Custom, "Custom") },
                pos.Anchor, v => _settings.Update("position", s => s.Position.Anchor = v))),
            Row("Custom horizontal position", "Used when placement is Custom.", new SettingSlider(0, 1, pos.CustomX, v => $"{v * 100:0}%", v => _settings.Update("position", s => s.Position.CustomX = Math.Round(v, 3)))),
            Row("Distance from screen edge", null, new SettingSlider(0, 40, pos.EdgeOffset, v => $"{v:0} px", v => _settings.Update("position", s => s.Position.EdgeOffset = Math.Round(v))))));

        p.Children.Add(SectionTitle("Display"));
        var monitors = MonitorService.GetAll();
        var stack = new StackPanel();
        stack.Children.Add(new Segmented<MonitorMode>(
            new[] { (MonitorMode.Primary, "Primary display"), (MonitorMode.FollowCursor, "Follow my cursor"), (MonitorMode.Specific, "Specific display") },
            pos.Monitor, v => { _settings.Update("position", s => s.Position.Monitor = v); Navigate("position"); }));
        if (pos.Monitor == MonitorMode.Specific)
        {
            var current = pos.MonitorDevice ?? monitors.FirstOrDefault(m => m.IsPrimary)?.DeviceName ?? string.Empty;
            stack.Children.Add(new Segmented<string>(monitors.Select(m => (m.DeviceName, m.Label)), current,
                v => _settings.Update("position", s => s.Position.MonitorDevice = v)).WithMargin(0, 10, 0, 0));
        }
        p.Children.Add(Card(StackedRow("Show the island on",
            $"{monitors.Count} display(s) detected. If the chosen display is disconnected, NEXUS moves to the primary display.", stack)));
        return p;
    }

    private FrameworkElement BehaviorPage()
    {
        var b = _settings.Current.Behavior;
        var p = Page("Behavior", "NEXUS only interrupts you when the information justifies it.");
        p.Children.Add(Card(
            Row("Auto-collapse delay", "How long an expanded island waits after the pointer leaves.",
                new SettingSlider(0.5, 10, b.AutoCollapseSeconds, v => $"{v:0.0} s", v => _settings.Update("behavior", s => s.Behavior.AutoCollapseSeconds = Math.Round(v, 1)))),
            StackedRow("On hover", "Illuminate gently, expand after a short pause, or do nothing.", new Segmented<HoverBehavior>(
                new[] { (HoverBehavior.Illuminate, "Illuminate"), (HoverBehavior.ExpandOnHover, "Expand on hover"), (HoverBehavior.None, "Nothing") },
                b.Hover, v => _settings.Update("behavior", s => s.Behavior.Hover = v))),
            StackedRow("On click", "Click again or click the idle island to open Island Center.", new Segmented<ClickBehavior>(
                new[] { (ClickBehavior.ExpandThenCenter, "Expand, then Island Center"), (ClickBehavior.OpenCenter, "Open Island Center") },
                b.Click, v => _settings.Update("behavior", s => s.Behavior.Click = v))),
            Row("Scroll to change volume", "Use the mouse wheel over the island to adjust system volume.",
                Toggle(b.ScrollAdjustsVolume, "behavior", (s, v) => s.Behavior.ScrollAdjustsVolume = v)),
            Row("Quiet mode (Do Not Disturb)", "Silences notifications, connectivity and transfer events. Critical alerts, privacy and your own actions still appear.",
                Toggle(b.QuietMode, "behavior", (s, v) => s.Behavior.QuietMode = v))));

        p.Children.Add(SectionTitle("File Shelf"));
        var shelfPrefs = _settings.Current.Shelf;
        var shelf = _providers.Providers.OfType<ShelfProvider>().FirstOrDefault();
        var clearRow = Row("Clear shelf", "Removes every item from the shelf. Your files are not touched.",
            ActionButton("Clear", () => { shelf?.Model.Clear(); Navigate("behavior"); }), Glyphs.Close);
        p.Children.Add(Card(
            Row("Remember shelf after restart", "Saves the shelf's file paths (never the files) in NEXUS's data folder. Off: the shelf empties when NEXUS closes.",
                Toggle(shelfPrefs.Remember, "shelf", (st, v) => st.Shelf.Remember = v), Glyphs.Shelf),
            Row("Keep items after dragging them out", "Off: an item leaves the shelf once you drop it into another app. Moved files always leave.",
                Toggle(shelfPrefs.KeepAfterDragOut, "shelf", (st, v) => st.Shelf.KeepAfterDragOut = v), Glyphs.Copy),
            clearRow));
        var shelfTip = Kit.Text("Drag files or folders onto the island to keep them, then drag them out from Island Center. Switch the whole feature off in Modules › File Shelf.", 12, S.TextTertiary).WithMargin(4, 8, 0, 0);
        shelfTip.TextWrapping = TextWrapping.Wrap;
        p.Children.Add(shelfTip);

        p.Children.Add(SectionTitle("Visibility"));
        p.Children.Add(Card(
            Row("Hide completely", "Keeps the island off-screen all the time, not only during fullscreen apps. Critical alerts and song pop-ups still slide out; with Reveal at screen edge on, push the pointer against the top edge to bring it out. Open Settings from the tray icon.",
                Toggle(b.HideCompletely, "behavior", (s, v) => s.Behavior.HideCompletely = v), "\uED1A"),
            Row("Song pop-ups", "When a new song starts in Spotify, YouTube or any media app, show it briefly and then tuck it away. Off: music stays on the island while it plays.",
                Toggle(b.SongPopup, "behavior", (s, v) => s.Behavior.SongPopup = v), Glyphs.Music),
            Row("Pop-up duration", null,
                new SettingSlider(1, 10, b.SongPopupSeconds, v => $"{v:0.0} s", v => _settings.Update("behavior", s => s.Behavior.SongPopupSeconds = Math.Round(v, 1)), liveUpdate: false), Glyphs.Clock)));

        p.Children.Add(SectionTitle("Fullscreen apps & games"));
        p.Children.Add(Card(StackedRow("When a fullscreen app is in front",
            "Gaming mode allows only critical alerts and high-priority events such as meetings starting. Media and routine updates never interrupt.",
            new Segmented<FullscreenBehavior>(
                new[] { (FullscreenBehavior.Gaming, "Gaming mode"), (FullscreenBehavior.CriticalOnly, "Critical only"), (FullscreenBehavior.Hide, "Hide completely"), (FullscreenBehavior.AlwaysShow, "Always show") },
                b.Fullscreen, v => _settings.Update("behavior", s => s.Behavior.Fullscreen = v)))));
        p.Children.Add(Card(Row("Reveal at screen edge",
            "With Hide completely, push the pointer against the top edge (bottom edge if NEXUS sits at the bottom) to slide the island out. Move away and it tucks back in.",
            Toggle(b.RevealOnEdgeHover, "behavior", (s, v) => s.Behavior.RevealOnEdgeHover = v))).WithMargin(0, 8, 0, 0));
        return p;
    }

    private FrameworkElement ModulesPage()
    {
        var p = Page("Modules", "Each module is an independent provider. A module that fails is isolated and never affects the others.");
        var rows = new List<FrameworkElement>();
        var updaters = new List<Action>();
        foreach (var provider in _providers.Providers)
        {
            var status = Kit.Text("", 11.5, S.TextTertiary);
            status.TextWrapping = TextWrapping.Wrap;
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var toggle = new ToggleSwitch(_settings.Current.Modules.IsEnabled(provider.Id));
            var id = provider.Id;
            toggle.Toggled += on => _ = _providers.SetEnabledAsync(id, on);
            var row = Row(provider.DisplayName, provider.Description, toggle);
            var text = row.Children.OfType<StackPanel>().First();
            var statusRow = Kit.Columns((dot, Kit.Auto), (status, Kit.Star));
            statusRow.Margin = new Thickness(0, 6, 0, 0);
            text.Children.Add(statusRow);
            rows.Add(row);
            updaters.Add(() =>
            {
                var s = provider.Status;
                (Color c, string label) = s.State switch
                {
                    ProviderState.Running => (Color.FromRgb(0x3D, 0xDC, 0x97), "Running"),
                    ProviderState.Starting => (Color.FromRgb(0x4C, 0xC2, 0xFF), "Starting"),
                    ProviderState.Unavailable => (Color.FromRgb(0xF5, 0xB5, 0x47), "Unavailable"),
                    ProviderState.Faulted => (Color.FromRgb(0xFF, 0x5A, 0x5F), "Error"),
                    _ => (Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), "Off"),
                };
                dot.Background = Palette.Freeze(c);
                status.Text = s.Message is null ? label : $"{label} · {s.Message}";
            });
        }
        _refreshModules = () => { foreach (var u in updaters) u(); };
        _refreshModules();
        p.Children.Add(Card(rows.ToArray()));

        p.Children.Add(SectionTitle("Calendar"));
        var box = new TextBox { Text = _settings.Current.Modules.CalendarFeed ?? string.Empty, Width = 420 };
        var apply = ActionButton("Apply", () =>
            _settings.Update("modules", s => s.Modules.CalendarFeed = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim()));
        p.Children.Add(Card(StackedRow("Upcoming meetings (optional)",
            "Paste an ICS link (Outlook: Settings › Calendar › Shared calendars › Publish a calendar) or a local .ics file path. It is fetched every 15 minutes and never sent anywhere else.",
            Kit.Row(box, apply.WithMargin(10, 0, 0, 0)))));
        return p;
    }

    private FrameworkElement PrivacyPage()
    {
        var b = _settings.Current.Behavior;
        var p = Page("Privacy", "Everything stays on this PC. NEXUS has no telemetry, no account and no network features unless you add a calendar link.");
        var info = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
        void Line(string glyph, string title, string text)
        {
            var t = Kit.Text(text, 12, S.TextTertiary).WithMargin(0, 2, 0, 0);
            t.TextWrapping = TextWrapping.Wrap;
            var row = Kit.Columns((Kit.Glyph(glyph, 15, S.TextSecondary).WithMargin(0, 2, 14, 0), Kit.Auto), (Kit.Column(Kit.Text(title, 13, S.Text), t), Kit.Star));
            row.Margin = new Thickness(0, 6, 0, 6);
            info.Children.Add(row);
        }
        Line(Glyphs.Music, "Media", "Title, artist and artwork of what is playing, from Windows media controls. Artwork is kept in memory only (12 most recent).");
        Line(Glyphs.Speakers, "Audio", "Default output/input device names, volume and mute state (Windows Core Audio).");
        Line(Glyphs.Microphone, "Microphone & camera", "Which apps currently use them, from the same Windows registry store that powers the system privacy indicators. Read-only.");
        Line(Glyphs.Wifi, "Network", "Connection type, Wi-Fi name, signal and whether a VPN/tunnel adapter is up.");
        Line(Glyphs.Bluetooth, "Bluetooth", "Names and connection state of paired devices.");
        Line(Glyphs.Ringer, "Notifications", "Only when NEXUS runs packaged and you grant access. Content is shown, never stored.");
        Line(Glyphs.Shelf, "File Shelf", "Paths of the files you drop on the island. Saved in shelf.json only while Remember shelf after restart is on (Behavior). The files themselves are never copied or read.");
        Line(Glyphs.Download, "Downloads & screenshots", "File names appearing in your Downloads and Screenshots folders; clipboard image presence (never its contents).");
        Line(Glyphs.Calendar, "Calendar", "Only the ICS link you provide. Meeting titles are kept in memory, never written to disk.");
        Line(Glyphs.Folder, "Stored on disk", $"Settings and a diagnostic log without personal content, in {NexusHost.DataDirectory}.");
        p.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = S.Card, BorderBrush = S.CardStroke, BorderThickness = new Thickness(1), Child = info });

        p.Children.Add(SectionTitle("Screen sharing safety"));
        p.Children.Add(Card(
            Row("Show notification previews", "Off: notifications show only the app name.", Toggle(b.ShowNotificationPreview, "behavior", (s, v) => s.Behavior.ShowNotificationPreview = v)),
            Row("Show meeting titles", "Off: meetings appear as \"Meeting\".", Toggle(b.ShowMeetingTitles, "behavior", (s, v) => s.Behavior.ShowMeetingTitles = v)),
            Row("Data folder", NexusHost.DataDirectory, ActionButton("Open", () => ShellService.Open(NexusHost.DataDirectory), Glyphs.Folder))));
        return p;
    }

    private FrameworkElement PreviewPage()
    {
        var p = Page("Preview lab", "Simulate events to explore themes, motion and the priority engine. Nothing here changes your system.");
        var wrap = new WrapPanel { Margin = new Thickness(14) };
        foreach (var name in PreviewEvents.Names)
        {
            var n = name;
            wrap.Children.Add(ActionButton(name, () => _preview.Run(n)).WithMargin(4, 4, 4, 4));
        }
        p.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = S.Card, BorderBrush = S.CardStroke, BorderThickness = new Thickness(1), Child = wrap });
        var tip = Kit.Text("Try: Now playing → Volume (volume briefly takes over, then music returns) → Critical battery (overrides everything) → Microphone (persistent satellite indicator).", 12, S.TextTertiary).WithMargin(4, 12, 0, 0);
        tip.TextWrapping = TextWrapping.Wrap;
        p.Children.Add(tip);
        return p;
    }

    private FrameworkElement AboutPage()
    {
        var p = Page("About", "NEXUS – Your Windows Activity Center.");
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var logs = Path.Combine(NexusHost.DataDirectory, "logs");
        p.Children.Add(Card(
            Row("Version", $"{version} · .NET {Environment.Version} · Windows build {Environment.OSVersion.Version.Build}", null, Glyphs.Info),
            Row("Notifications access", NotificationProvider.HasPackageIdentity()
                ? "Running with package identity: notification mirroring can be enabled."
                : "Running unpackaged: Windows does not grant notification access. See README › Known limitations.", null, Glyphs.Ringer),
            Row("Diagnostic log", Path.Combine(logs, "nexus.log"), ActionButton("Open", () => ShellService.Open(logs)), "\uE9D9"),
            Row("Icons & fonts", "System icons from Segoe Fluent Icons / Segoe MDL2 Assets, text in Segoe UI Variable – all part of Windows.", null, "\uE8D2")));
        return p;
    }

    #endregion
}
