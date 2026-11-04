using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Nexus.App.Motion;
using Nexus.Core.Motion;

namespace Nexus.App.Ui;

/// <summary>
/// Thin rounded track with a spring-smoothed fill. Optionally interactive (seek / volume): on hover the track
/// thickens and a knob fades in; dragging reports values live. Drawn in OnRender – no templates, no layout cost.
/// </summary>
public sealed class ProgressTrack : FrameworkElement, IAnimatable
{
    private readonly Spring _value = new(0, SpringConfig.Content, 0.0005);
    private readonly Spring _hover = new(0, SpringConfig.Micro, 0.002);
    private bool _dragging;

    public ProgressTrack(bool interactive, double height = 14)
    {
        Interactive = interactive;
        Height = height;
        if (interactive) Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    public bool Interactive { get; }
    public Brush TrackBrush { get; set; } = Brushes.Gray;
    public Brush FillBrush { get; set; } = Brushes.White;
    public double Thickness { get; set; } = 4;

    /// <summary>Raised continuously while dragging, and once on release with final=true.</summary>
    public event Action<double, bool>? ValueChanged;

    public double Value => _value.Target;

    public void SetProgress(double v, bool animate = true)
    {
        if (_dragging) return; // never fight the user's pointer
        v = Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1);
        if (!animate) { _value.Snap(v); InvalidateVisual(); return; }
        _value.Target = v;
        Animator.Wake(this);
    }

    public bool Advance(double dt)
    {
        bool moving = _value.Step(dt) | _hover.Step(dt);
        InvalidateVisual();
        return moving;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        double hv = Math.Clamp(_hover.Value, 0, 1);
        double t = Thickness + (Interactive ? 2 * hv : 0);
        double y = (h - t) / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, y, w, t), t / 2, t / 2);
        double fw = Math.Clamp(_value.Value, 0, 1) * w;
        if (fw > 0.5) dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, y, Math.Max(fw, t), t), t / 2, t / 2);
        if (Interactive && hv > 0.01)
        {
            double r = 5.5 * hv;
            dc.PushOpacity(hv);
            dc.DrawEllipse(FillBrush, null, new Point(Math.Clamp(fw, r, w - r), h / 2), r, r);
            dc.Pop();
        }
    }

    // A transparent hit area covering the full control height makes the thin track easy to grab.
    protected override HitTestResult HitTestCore(PointHitTestParameters p) =>
        new Rect(RenderSize).Contains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null!;

    protected override void OnMouseEnter(MouseEventArgs e) { if (Interactive) { _hover.Target = 1; Animator.Wake(this); } }
    protected override void OnMouseLeave(MouseEventArgs e) { if (!_dragging) { _hover.Target = 0; Animator.Wake(this); } }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!Interactive) return;
        _dragging = true;
        CaptureMouse();
        Drag(e.GetPosition(this).X, false);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Drag(e.GetPosition(this).X, false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        Drag(e.GetPosition(this).X, true);
        _dragging = false;
        ReleaseMouseCapture();
        if (!IsMouseOver) { _hover.Target = 0; Animator.Wake(this); }
        e.Handled = true;
    }

    private void Drag(double x, bool final)
    {
        var v = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1);
        _value.Snap(v);
        InvalidateVisual();
        ValueChanged?.Invoke(v, final);
    }
}
