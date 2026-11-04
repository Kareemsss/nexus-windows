using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nexus.App.Shelf;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Settings;
using Nexus.Core.State;

namespace Nexus.App.Widgets;

/// <summary>Operations widgets may request. Implemented by the island controller.</summary>
public interface IIslandCommands
{
    ActivityHub Hub { get; }
    bool QuietMode { get; }
    /// <summary>System microphone mute state, or null when no capture device exists.</summary>
    bool? MicMuted { get; }
    void RunAction(ActivityAction action);
    void Dismiss(TrackedActivity activity);
    void OpenCenter();
    void Collapse();
    void OpenSettings();
    void ToggleQuietMode();
    void ToggleMicMute();
    /// <summary>The File Shelf, or null while its module is switched off.</summary>
    ShelfProvider? Shelf { get; }
    /// <summary>An item is being dragged out of Island Center: it must stay open until the drag ends.</summary>
    void DragOutStarted();
    void DragOutEnded();
}

public sealed class WidgetContext
{
    public WidgetContext(Palette palette, IIslandCommands commands, NexusSettings settings)
    {
        Palette = palette;
        Commands = commands;
        Settings = settings;
    }

    public Palette Palette { get; }
    public IIslandCommands Commands { get; }
    public NexusSettings Settings { get; }

    /// <summary>Accent for icons/text of an activity: its contextual colour made legible, or the theme accent.</summary>
    public Color AccentOf(Activity? a)
    {
        var t = Palette.Theme;
        if (t.Accent == AccentBehavior.Monochrome || a?.Accent is null) return t.ThemeAccent;
        return Palette.Legible(Palette.ToColor(a.Accent.Value));
    }

    public SolidColorBrush AccentBrushOf(Activity? a) => Palette.Freeze(AccentOf(a));
}

/// <summary>A live view of one activity in one presentation mode.</summary>
public interface IWidgetView
{
    FrameworkElement Element { get; }
    /// <summary>Called with a newer revision of the same activity (same id, same mode). Must not rebuild.</summary>
    void Update(TrackedActivity? activity);
    /// <summary>The view became the visible one (start timers/animations).</summary>
    void OnShown();
    /// <summary>The view is leaving (stop timers). It may still fade out for a few frames.</summary>
    void OnHidden();
}

public interface IWidget
{
    bool Supports(object content);
    IWidgetView Create(TrackedActivity activity, IslandMode mode, WidgetContext context);
}

/// <summary>Base for typed widget views: handles content casting and shared action-bar construction.</summary>
public abstract class WidgetView<T> : IWidgetView where T : class
{
    protected WidgetView(WidgetContext ctx, TrackedActivity activity)
    {
        Ctx = ctx;
        Activity = activity;
    }

    protected WidgetContext Ctx { get; }
    protected Palette P => Ctx.Palette;
    protected TrackedActivity Activity { get; private set; }
    public abstract FrameworkElement Element { get; }

    public void Update(TrackedActivity? activity)
    {
        if (activity?.Activity.Content is not T content) return;
        Activity = activity;
        Apply(content);
    }

    protected abstract void Apply(T content);
    public virtual void OnShown() { }
    public virtual void OnHidden() { }

    /// <summary>Row of pill buttons for the activity's actions, plus Dismiss for transient items.</summary>
    protected StackPanel BuildActions(bool includeDismiss)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var actions = Activity.Activity.Actions;
        bool hasOwnDismiss = actions.Any(a => a.Id == "dismiss");
        if (includeDismiss && !hasOwnDismiss)
        {
            var d = NexusButton.Pill("Dismiss", null, P);
            var snapshot = Activity;
            d.Click += (_, _) => Ctx.Commands.Dismiss(snapshot);
            row.Children.Add(d.WithMargin(0, 0, 8, 0));
        }
        foreach (var a in actions.OrderBy(a => a.IsPrimary ? 1 : 0))
        {
            var b = NexusButton.Pill(a.Label, a.Glyph, P, a.IsPrimary, a.IsPrimary ? Ctx.AccentOf(Activity.Activity) : null);
            var action = a;
            b.Click += (_, _) => Ctx.Commands.RunAction(action);
            row.Children.Add(b.WithMargin(0, 0, 8, 0));
        }
        if (row.Children.Count > 0 && row.Children[^1] is FrameworkElement last) last.Margin = new Thickness(0);
        return row;
    }
}

public sealed class WidgetRegistry
{
    private readonly List<IWidget> _widgets = new();
    private readonly IWidget _fallback;

    public WidgetRegistry(IWidget fallback) => _fallback = fallback;

    /// <summary>Later registrations win, so plugins can override built-in presentations.</summary>
    public void Register(IWidget widget) => _widgets.Insert(0, widget);

    public IWidget Resolve(object content) => _widgets.FirstOrDefault(w => w.Supports(content)) ?? _fallback;
}

/// <summary>Helper for simple widgets: one class per content type, two factory methods.</summary>
public abstract class Widget<T> : IWidget where T : class
{
    public bool Supports(object content) => content is T;

    public IWidgetView Create(TrackedActivity activity, IslandMode mode, WidgetContext context) =>
        mode == IslandMode.Expanded ? CreateExpanded(activity, context) : CreateCompact(activity, context);

    protected abstract IWidgetView CreateCompact(TrackedActivity activity, WidgetContext ctx);
    protected abstract IWidgetView CreateExpanded(TrackedActivity activity, WidgetContext ctx);
}

/// <summary>Shared layout constants. Heights are fixed per mode so text never resizes the island vertically.</summary>
public static class Layout
{
    public const double CompactHeight = 38;
    public const double GlanceHeight = 34;
    public const double CompactPadX = 14;
    public const double ExpandedPad = 16;
    public const double ExpandedWidth = 392;
}
