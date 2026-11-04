using System.Windows;
using System.Windows.Controls;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Activities;
using Nexus.Core.Settings;

namespace Nexus.App.Island;

/// <summary>
/// The satellite: a detached mini-capsule docked beside the island that carries persistent background state
/// (microphone/camera in use, downloads, low battery, offline, a call while music plays...). It keeps the main
/// island free for what matters now while never hiding privacy-relevant state. On hover it reveals labels.
/// This is also the foundation for future detached multi-activity capsules.
/// </summary>
public sealed class SatelliteView
{
    public const double Height = 30;

    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private string _signature = string.Empty;

    public SatelliteView()
    {
        Content = new Border { Child = _items, Padding = new Thickness(10, 0, 10, 0), Height = Height };
    }

    public FrameworkElement Content { get; }
    public int Count => _items.Children.Count;

    /// <summary>Rebuilds only when the set of indicators actually changed.</summary>
    public void Update(IReadOnlyList<TrackedActivity> indicators, Palette palette, bool expanded)
    {
        var sig = string.Join("|", indicators.Select(i => i.Id + ":" + i.Activity.Indicator?.Label + ":" + i.Activity.Indicator?.Color))
                  + "|" + expanded + "|" + palette.Theme.Name;
        if (sig == _signature) return;
        _signature = sig;
        _items.Children.Clear();
        bool first = true;
        foreach (var t in indicators)
        {
            if (t.Activity.Indicator is not { } spec) continue;
            var color = palette.Theme.Accent == AccentBehavior.Monochrome
                ? palette.TextPrimary
                : Palette.Freeze(Palette.Legible(Palette.ToColor(spec.Color)));
            FrameworkElement icon = spec.Indeterminate ? new Spinner(color, 13) : Kit.Glyph(spec.Glyph, 13, color);
            var row = Kit.Row(icon);
            if (expanded)
            {
                row.Children.Add(Kit.Text(spec.Label, Kit.Type.Caption, palette.TextSecondary).WithMaxWidth(150).WithMargin(7, 0, 0, 0));
            }
            else if (t.Activity.Category == ActivityCategory.Privacy)
            {
                // Privacy indicators carry a tiny live dot, echoing the hardware LED convention.
                row.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = color, Margin = new Thickness(4, 0, 0, 0) });
            }
            row.Margin = new Thickness(first ? 0 : 11, 0, 0, 0);
            first = false;
            _items.Children.Add(row);
        }
    }

    public Size Measure()
    {
        Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(Math.Max(Height, Content.DesiredSize.Width), Height);
    }
}
