using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Nexus.App.Ui;

/// <summary>
/// Four-bar "now playing" equalizer. Deliberately subtle (low amplitude, slow sine easing) and capped at
/// 24 FPS via Timeline.DesiredFrameRate so a playing song costs a fraction of a full-rate animation.
/// It is decorative: Windows does not expose per-app audio spectra without capture hooks.
/// </summary>
public sealed class Equalizer : Canvas
{
    private readonly Rectangle[] _bars = new Rectangle[4];
    private readonly ScaleTransform[] _scales = new ScaleTransform[4];
    private static readonly double[] Periods = { 0.62, 0.81, 0.55, 0.73 };
    private static readonly double[] Lows = { 0.30, 0.22, 0.38, 0.26 };
    private bool _running;

    public Equalizer(Brush brush, double width = 16, double height = 14)
    {
        Width = width;
        Height = height;
        IsHitTestVisible = false;
        double barW = 2.4, gap = (width - barW * 4) / 3;
        for (int i = 0; i < 4; i++)
        {
            _scales[i] = new ScaleTransform(1, Lows[i]);
            _bars[i] = new Rectangle
            {
                Width = barW,
                Height = height,
                RadiusX = barW / 2,
                RadiusY = barW / 2,
                Fill = brush,
                RenderTransform = _scales[i],
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            SetLeft(_bars[i], i * (barW + gap));
            Children.Add(_bars[i]);
        }
        Unloaded += (_, _) => Stop();
    }

    public void SetBrush(Brush b) { foreach (var r in _bars) r.Fill = b; }

    public void SetRunning(bool running, bool animated)
    {
        if (!animated || Nexus.App.Motion.Animator.Eco) running = false;
        if (running == _running) return;
        _running = running;
        for (int i = 0; i < 4; i++)
        {
            if (running)
            {
                var a = new DoubleAnimation(Lows[i], 1.0, TimeSpan.FromSeconds(Periods[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    BeginTime = TimeSpan.FromSeconds(i * 0.09),
                };
                Timeline.SetDesiredFrameRate(a, 24);
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
            else
            {
                var settle = new DoubleAnimation(Lows[i] * 0.8, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase() };
                _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, settle);
            }
        }
    }

    private void Stop()
    {
        foreach (var s in _scales) s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _running = false;
    }
}

/// <summary>Indeterminate activity ring (downloads). Also frame-rate capped.</summary>
public sealed class Spinner : FrameworkElement
{
    private readonly RotateTransform _rotate = new();
    private readonly Pen _pen;

    public Spinner(Brush brush, double size = 14, double thickness = 1.6)
    {
        Width = Height = size;
        _pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _pen.Freeze();
        RenderTransform = _rotate;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            var a = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(a, 20);
            _rotate.BeginAnimation(RotateTransform.AngleProperty, a);
        };
        Unloaded += (_, _) => _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double r = Width / 2 - _pen.Thickness;
        var c = new Point(Width / 2, Height / 2);
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(Math.PI * 1.4), c.Y - r * Math.Cos(Math.PI * 1.4));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, true, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, _pen, g);
    }
}
