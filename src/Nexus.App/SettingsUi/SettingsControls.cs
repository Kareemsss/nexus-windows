using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nexus.App.Motion;
using Nexus.App.Theming;
using Nexus.App.Ui;
using Nexus.Core.Motion;

namespace Nexus.App.SettingsUi;

/// <summary>Colour tokens for the settings app (dark, on Mica).</summary>
public static class S
{
    public static readonly SolidColorBrush Text = Palette.Freeze(Color.FromRgb(0xF3, 0xF3, 0xF3));
    public static readonly SolidColorBrush TextSecondary = Palette.Freeze(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush TextTertiary = Palette.Freeze(Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush Card = Palette.Freeze(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush CardStroke = Palette.Freeze(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush Divider = Palette.Freeze(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    public static readonly Color AccentColor = Color.FromRgb(0x4C, 0xC2, 0xFF);
    public static readonly SolidColorBrush Accent = Palette.Freeze(AccentColor);
    public static readonly Color ControlRest = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
    public static readonly Color ControlHot = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
}

/// <summary>Windows 11-style toggle switch with a spring-driven knob.</summary>
public sealed class ToggleSwitch : FrameworkElement, IAnimatable
{
    private readonly Spring _pos = new(0, SpringConfig.Micro, 0.002);
    private readonly Spring _hover = new(0, SpringConfig.Content, 0.002);
    private bool _on;

    public ToggleSwitch(bool on)
    {
        _on = on;
        _pos.Snap(on ? 1 : 0);
        Width = 40;
        Height = 20;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public event Action<bool>? Toggled;

    public bool IsOn
    {
        get => _on;
        set { _on = value; _pos.Target = value ? 1 : 0; Animator.Wake(this); }
    }

    protected override void OnMouseEnter(MouseEventArgs e) { _hover.Target = 1; Animator.Wake(this); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover.Target = 0; Animator.Wake(this); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        IsOn = !_on;
        Toggled?.Invoke(_on);
        e.Handled = true;
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);

    public bool Advance(double dt)
    {
        bool m = _pos.Step(dt) | _hover.Step(dt);
        InvalidateVisual();
        return m;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double p = Math.Clamp(_pos.Value, 0, 1), h = Math.Clamp(_hover.Value, 0, 1);
        var off = Color.FromArgb((byte)(0x10 + 0x10 * h), 255, 255, 255);
        var track = new SolidColorBrush(ColorSpring.Lerp(off, S.AccentColor, p));
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb((byte)(0x90 * (1 - p)), 255, 255, 255)), 1);
        dc.DrawRoundedRectangle(track, stroke, new Rect(0.5, 0.5, Width - 1, Height - 1), 9.5, 9.5);
        double r = 5 + 1 * h + 1 * p;
        double cx = 10 + (Width - 20) * _pos.Value;
        var knob = new SolidColorBrush(p > 0.5 ? Color.FromRgb(0x0B, 0x0B, 0x0E) : Color.FromArgb(0xCC, 255, 255, 255));
        dc.DrawEllipse(knob, null, new Point(cx, Height / 2), r, r);
    }
}

/// <summary>Segmented choice control (theme, anchor, behaviours).</summary>
public sealed class Segmented<T> : Border where T : notnull
{
    private readonly List<(T Value, NexusButton Button, TextBlock Label)> _items = new();

    public Segmented(IEnumerable<(T Value, string Label)> options, T value, Action<T> changed)
    {
        CornerRadius = new CornerRadius(9);
        Background = Palette.Freeze(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF));
        BorderBrush = S.CardStroke;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(3);
        HorizontalAlignment = HorizontalAlignment.Left;
        var panel = new WrapPanel();
        foreach (var (v, label) in options)
        {
            var text = Kit.Text(label, 12.5, S.Text);
            var b = new NexusButton(text, Colors.Transparent, S.ControlHot, 7) { Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(1) };
            var captured = v;
            b.Click += (_, _) => { Select(captured); changed(captured); };
            _items.Add((v, b, text));
            panel.Children.Add(b);
        }
        Child = panel;
        Select(value);
    }

    public void Select(T value)
    {
        foreach (var (v, b, label) in _items)
        {
            bool on = EqualityComparer<T>.Default.Equals(v, value);
            b.SetColors(on ? Color.FromArgb(0xF0, 0xF3, 0xF3, 0xF3) : Colors.Transparent, on ? Colors.White : S.ControlHot);
            label.Foreground = on ? Palette.Freeze(Color.FromRgb(0x0B, 0x0B, 0x0E)) : S.Text;
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}

/// <summary>Slider = interactive progress track + live value label.</summary>
public sealed class SettingSlider : Grid
{
    public SettingSlider(double min, double max, double value, Func<double, string> format, Action<double> changed, bool liveUpdate = true)
    {
        var label = Kit.Numeric(format(value), 12.5, S.TextSecondary);
        label.Width = 56;
        label.TextAlignment = TextAlignment.Right;
        var track = new ProgressTrack(true, 22) { TrackBrush = Palette.Freeze(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), FillBrush = S.Accent, Thickness = 4, Width = 220 };
        track.SetProgress((value - min) / (max - min), animate: false);
        track.ValueChanged += (v, final) =>
        {
            var real = min + v * (max - min);
            label.Text = format(real);
            if (liveUpdate || final) changed(real);
        };
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Children.Add(track);
        SetColumn(label, 1);
        Children.Add(label);
        VerticalAlignment = VerticalAlignment.Center;
    }
}
