using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Nexus.App.Ui;

/// <summary>
/// Round on/off button for a playback mode such as shuffle or repeat. Off is a quieter glyph; on takes the
/// active color and gets a dot underneath, so the state never rests on color alone.
/// </summary>
public sealed class ModeButton : Decorator
{
    public const double Diameter = 34;
    public const double GlyphSize = 14;

    /// <summary>Space between the button's edge and its glyph, for lining the glyph up with nearby text.</summary>
    public const double Inset = (Diameter - GlyphSize) / 2;

    private readonly NexusButton _button;
    private readonly TextBlock _glyph;
    private readonly Ellipse _dot;
    private readonly Theming.Palette _p;

    public ModeButton(string glyph, Theming.Palette p)
    {
        _p = p;
        _glyph = Kit.Glyph(glyph, GlyphSize, p.TextSecondary);
        _dot = new Ellipse
        {
            Width = 4,
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
            Visibility = Visibility.Hidden,
        };
        var content = new Grid();
        content.Children.Add(_glyph);
        content.Children.Add(_dot);
        _button = new NexusButton(content, Colors.Transparent, p.Theme.ControlHover, Diameter / 2)
        {
            Width = Diameter,
            Height = Diameter,
        };
        Child = _button;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public event EventHandler? Click
    {
        add => _button.Click += value;
        remove => _button.Click -= value;
    }

    /// <param name="name">What a screen reader announces, including the state ("Repeat one").</param>
    public void Set(string glyph, bool on, Brush activeBrush, string name)
    {
        _glyph.Text = glyph;
        _glyph.Foreground = on ? activeBrush : _p.TextSecondary;
        _dot.Fill = activeBrush;
        _dot.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        AutomationProperties.SetName(_button, name);
    }
}
