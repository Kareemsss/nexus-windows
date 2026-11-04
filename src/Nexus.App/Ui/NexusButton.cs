using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nexus.App.Motion;
using Nexus.Core.Motion;

namespace Nexus.App.Ui;

/// <summary>
/// The island's only button primitive. Hover gently illuminates; press compresses to 92% with a spring and
/// releases with a tiny overshoot. Marks mouse events handled so the island underneath does not react.
/// </summary>
public sealed class NexusButton : Border, IAnimatable
{
    private readonly Spring _hover = new(0, SpringConfig.Micro, 0.002);
    private readonly Spring _press = new(0, SpringConfig.Micro, 0.002);
    private readonly ScaleTransform _scale = new(1, 1);
    private Color _rest, _hot;
    private bool _down;

    public NexusButton(UIElement content, Color rest, Color hot, double cornerRadius)
    {
        Child = content;
        _rest = rest;
        _hot = hot;
        CornerRadius = new CornerRadius(cornerRadius);
        Background = new SolidColorBrush(rest);
        RenderTransform = _scale;
        RenderTransformOrigin = new Point(0.5, 0.5);
        Cursor = Cursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    public event EventHandler? Click;

    private static readonly SolidColorBrush FocusRing = Theming.Palette.Freeze(Color.FromRgb(0xF5, 0xF6, 0xF8));

    /// <summary>
    /// Makes the button reachable with Tab, operable with Enter or Space, visibly focused (a solid ring, not the
    /// dotted default) and announced by screen readers with <paramref name="name"/>.
    /// </summary>
    public NexusButton Accessible(string name)
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
        FocusVisualStyle = null;
        AutomationProperties.SetName(this, name);
        BorderBrush = Brushes.Transparent;
        BorderThickness = new Thickness(1.5);
        return this;
    }

    /// <summary>Resets the pressed look when something else (a drag-and-drop loop) swallowed the mouse-up.</summary>
    public void CancelPress()
    {
        _down = false;
        _press.Target = 0;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Animator.Wake(this);
    }

    public void PerformClick() => Click?.Invoke(this, EventArgs.Empty);

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (Focusable) BorderBrush = FocusRing;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (Focusable) BorderBrush = Brushes.Transparent;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Focusable && IsHitTestVisible && (e.Key == Key.Enter || e.Key == Key.Space))
        {
            e.Handled = true;
            _press.Impulse(-4);
            Animator.Wake(this);
            Click?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NexusButtonPeer(this);

    /// <summary>Exposes the button to UI Automation as a real, invokable button.</summary>
    private sealed class NexusButtonPeer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        public NexusButtonPeer(NexusButton owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => nameof(NexusButton);
        protected override bool IsControlElementCore() => true;
        public override object GetPattern(PatternInterface pattern) =>
            pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
        public void Invoke() => ((NexusButton)Owner).PerformClick();
    }

    public bool IsEnabledVisual
    {
        get => IsHitTestVisible;
        set { IsHitTestVisible = value; Opacity = value ? 1 : 0.35; }
    }

    public void SetColors(Color rest, Color hot) { _rest = rest; _hot = hot; Apply(); }

    protected override void OnMouseEnter(MouseEventArgs e) { _hover.Target = 1; Animator.Wake(this); }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover.Target = 0;
        _press.Target = 0;
        _down = false;
        Animator.Wake(this);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _down = true;
        _press.Target = 1;
        CaptureMouse();
        Animator.Wake(this);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool inside = IsMouseOver;
        ReleaseMouseCapture();
        _press.Target = 0;
        _press.Impulse(-4); // a little "pop" on release
        Animator.Wake(this);
        e.Handled = true;
        if (_down && inside) Click?.Invoke(this, EventArgs.Empty);
        _down = false;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => e.Handled = true;

    public bool Advance(double dt)
    {
        bool a = _hover.Step(dt), b = _press.Step(dt);
        Apply();
        return a || b;
    }

    private void Apply()
    {
        var s = 1 - 0.08 * _press.Value;
        _scale.ScaleX = _scale.ScaleY = s;
        if (Background is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = ColorSpring.Lerp(_rest, _hot, Math.Clamp(_hover.Value, 0, 1));
    }

    /// <summary>Round icon button (media transport, close...).</summary>
    public static NexusButton Icon(string glyph, double glyphSize, double diameter, Brush foreground, Theming.Palette p, bool filled = false)
    {
        var g = Kit.Glyph(glyph, glyphSize, foreground);
        return new NexusButton(g, filled ? p.Theme.ControlFill : Colors.Transparent, p.Theme.ControlHover, diameter / 2)
        {
            Width = diameter,
            Height = diameter,
        };
    }

    /// <summary>Pill-shaped text button with optional glyph ("Join", "Dismiss").</summary>
    public static NexusButton Pill(string label, string? glyph, Theming.Palette p, bool primary = false, Color? accent = null)
    {
        var fg = primary ? Theming.Palette.Freeze(Color.FromRgb(0x0B, 0x0B, 0x0E)) : p.TextPrimary;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        if (glyph is not null) row.Children.Add(Kit.Glyph(glyph, 12, fg).WithMargin(0, 0, 6, 0));
        row.Children.Add(Kit.Text(label, Kit.Type.Body, fg, FontWeights.SemiBold));
        Color rest = primary ? (accent ?? Color.FromRgb(0xF2, 0xF4, 0xF8)) : p.Theme.ControlFill;
        Color hot = primary ? (accent is null ? Colors.White : Theming.Palette.Legible(rest)) : p.Theme.ControlHover;
        return new NexusButton(row, rest, hot, 15) { Height = 30, Padding = new Thickness(14, 0, 14, 0), MinWidth = 72 };
    }
}
