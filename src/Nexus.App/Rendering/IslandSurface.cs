using System.Windows;
using System.Windows.Media;
using Nexus.App.Theming;
using Nexus.Core.Activities;

namespace Nexus.App.Rendering;

/// <summary>
/// Draws the island's glass body. Layers, back to front:
///   0. soft outer shadow (outside the body only, so translucent glass is never darkened)
///   1. smoked-glass body (vertical gradient, theme transparency)
///   2. frost: a soft luminous diffusion near the top edge
///   3. atmosphere (Aurora) and ambient light from the contextual accent
///   4. specular rim: 1 DIP edge, bright at the top, fading down (the "thin illuminated edge")
///   5. accent rim / luminous edge (Cyber)
///   6. transient effects: edge pulse bloom, charging energy sweep, capture flash, critical breathing
/// Everything is resolution independent and drawn in OnRender, so a frame is a handful of primitives.
/// </summary>
public sealed class IslandSurface : FrameworkElement
{
    private IslandTheme _theme = ThemePresets.Obsidian;

    public IslandSurface()
    {
        SnapsToDevicePixels = false; // sub-pixel morphing must stay smooth
    }

    public IslandTheme Theme { get => _theme; set { _theme = value; InvalidateVisual(); } }
    public double Radius { get; set; } = 12;
    /// <summary>Multiplier for the soft drop shadow (0 disables it).</summary>
    public double ShadowStrength { get; set; } = 1;
    public Color Accent { get; set; } = Colors.White;
    /// <summary>0..1: how much contextual light the current state deserves.</summary>
    public double AccentStrength { get; set; }
    public double Hover { get; set; }
    public double Alert { get; set; }
    /// <summary>0..1 phase used for the critical "breathing" rim.</summary>
    public double AlertPhase { get; set; }

    public PulseStyle PulseStyle { get; set; }
    public Color PulseColor { get; set; }
    /// <summary>0 = no pulse, (0..1] = progress through the pulse.</summary>
    public double PulseProgress { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1) return;
        var t = _theme;
        double r = Math.Min(Radius, Math.Min(w, h) / 2);
        var rect = new Rect(0, 0, w, h);
        double glow = t.Glow;

        // 0. Soft shadow. Stacked fills approximate a gaussian falloff without a bitmap effect
        //    (effects force extra offscreen passes on every frame of a morph).
        if (t.ShadowOpacity * ShadowStrength > 0.01)
        {
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-40, -40, w + 80, h + 80)),
                new RectangleGeometry(rect, r, r));
            outside.Freeze();
            dc.PushClip(outside);
            int layers = Nexus.App.Motion.Animator.Eco ? 3 : 7;
            for (int i = layers; i >= 1; i--)
            {
                double spread = i * 2.2, dy = 2 + i * 0.9;
                double a = t.ShadowOpacity * ShadowStrength * 0.085 * Math.Pow(1 - (i - 1) / (double)layers, 1.6);
                var b = new SolidColorBrush(Color.FromArgb((byte)(a * 255), 0, 0, 0));
                b.Freeze();
                dc.DrawRoundedRectangle(b, null, new Rect(-spread, -spread + dy, w + spread * 2, h + spread * 2), r + spread, r + spread);
            }
            dc.Pop();
        }

        // 1. Body
        double alphaTop = Math.Clamp(1 - t.Transparency, 0.3, 1);
        double alphaBottom = Math.Clamp(alphaTop + 0.04, 0.3, 1);
        var body = new LinearGradientBrush(WithA(t.BaseTop, alphaTop), WithA(t.BaseBottom, alphaBottom), 90);
        body.Freeze();
        dc.DrawRoundedRectangle(body, null, rect, r, r);

        // Clip interior light layers to the rounded body.
        var clip = new RectangleGeometry(rect, r, r);
        clip.Freeze();
        dc.PushClip(clip);

        // 2. Frost – a soft top sheen that reads as depth in the glass.
        if (t.Frost > 0.01)
        {
            var frost = new RadialGradientBrush
            {
                Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.75, RadiusY = Math.Min(1.4, 46 / h),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)Math.Clamp(t.Frost * 34 + Hover * 14, 0, 255), 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            };
            frost.Freeze();
            dc.DrawRectangle(frost, null, rect);
        }

        // 3a. Atmosphere (Aurora): two very soft colour fields drifting in from the corners.
        if (t.AtmosphereA is { } aA && t.AtmosphereB is { } aB && glow > 0)
        {
            DrawField(dc, rect, aA, new Point(0.08, 1.0), 0.75, 0.10 * glow);
            DrawField(dc, rect, aB, new Point(0.95, 0.0), 0.70, 0.09 * glow);
        }

        // 3b. Ambient contextual light (album art colour, battery state...). Low saturation, low alpha.
        double ambient = AccentStrength * glow;
        if (ambient > 0.005)
        {
            DrawField(dc, rect, Accent, new Point(0.12, 0.55), 0.85, 0.20 * ambient);
            DrawField(dc, rect, Accent, new Point(0.5, 1.15), 0.9, 0.10 * ambient);
        }

        // Capture flash fills the glass briefly.
        if (PulseProgress > 0 && PulseStyle == PulseStyle.Flash)
        {
            double f = Math.Pow(1 - PulseProgress, 2.2);
            var flash = new SolidColorBrush(Color.FromArgb((byte)(f * 70), 255, 255, 255));
            flash.Freeze();
            dc.DrawRectangle(flash, null, rect);
        }
        dc.Pop();

        // 4. Specular rim.
        var inner = new Rect(0.5, 0.5, w - 1, h - 1);
        double ri = Math.Max(0, r - 0.5);
        var rim = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(48 + 40 * Hover), 255, 255, 255), 0),
                new GradientStop(Color.FromArgb((byte)(14 + 10 * Hover), 255, 255, 255), Math.Min(0.5, 18 / h)),
                new GradientStop(Color.FromArgb(8, 255, 255, 255), 1),
            },
        };
        rim.Freeze();
        dc.DrawRoundedRectangle(null, new Pen(rim, 1), inner, ri, ri);

        // 5. Accent rim / luminous edge.
        if (t.Edge == EdgeStyle.Luminous)
        {
            var edge = new LinearGradientBrush(WithA(t.EdgeA, 0.55 + 0.3 * Hover), WithA(t.EdgeB, 0.45 + 0.3 * Hover), 0);
            edge.Freeze();
            dc.DrawRoundedRectangle(null, new Pen(edge, 1), inner, ri, ri);
        }
        if (ambient > 0.01)
        {
            var accentRim = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(WithA(Accent, 0.55 * ambient), 0),
                    new GradientStop(WithA(Accent, 0.0), 0.6),
                },
            };
            accentRim.Freeze();
            dc.DrawRoundedRectangle(null, new Pen(accentRim, 1), inner, ri, ri);
        }

        // 6. Transient effects.
        if (Alert > 0.01)
        {
            double breath = 0.45 + 0.35 * (0.5 + 0.5 * Math.Sin(AlertPhase * Math.PI * 2));
            var alertPen = new Pen(new SolidColorBrush(WithA(Accent, breath * Alert)), 1.4);
            alertPen.Freeze();
            dc.DrawRoundedRectangle(null, alertPen, inner, ri, ri);
        }

        if (PulseProgress > 0 && PulseProgress < 1) DrawPulse(dc, inner, ri, w, h);
    }

    // Only the glass body itself is interactive – never the shadow or bloom drawn around it.
    protected override HitTestResult HitTestCore(PointHitTestParameters p)
    {
        var r = Math.Min(Radius, Math.Min(ActualWidth, ActualHeight) / 2);
        var body = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), r, r);
        return body.FillContains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null!;
    }

    private void DrawPulse(DrawingContext dc, Rect inner, double r, double w, double h)
    {
        double p = PulseProgress;
        switch (PulseStyle)
        {
            case PulseStyle.Sweep:
            {
                // A band of energy travelling left → right along the rim, with a soft outer halo.
                double env = Math.Sin(Math.PI * p); // fade in and out
                double pos = -0.25 + 1.5 * p;
                var band = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
                    GradientStops =
                    {
                        new GradientStop(WithA(PulseColor, 0), pos - 0.22),
                        new GradientStop(WithA(PulseColor, 0.95 * env), pos),
                        new GradientStop(WithA(PulseColor, 0), pos + 0.22),
                    },
                };
                band.Freeze();
                dc.DrawRoundedRectangle(null, new Pen(band, 1.6), inner, r, r);
                var halo = band.CloneCurrentValue();
                halo.Opacity = 0.28;
                halo.Freeze();
                dc.DrawRoundedRectangle(null, new Pen(halo, 4), new Rect(-2, -2, w + 4, h + 4), r + 2, r + 2);
                break;
            }
            default:
            {
                // Edge pulse: the rim lights up, then a bloom expands outward and fades.
                double rimA = Math.Pow(1 - p, 1.6) * (PulseStyle == PulseStyle.Flash ? 1.0 : 0.9);
                var rimPen = new Pen(new SolidColorBrush(WithA(PulseColor, rimA)), 1.5);
                rimPen.Freeze();
                dc.DrawRoundedRectangle(null, rimPen, inner, r, r);

                double spread = 1 + 9 * EaseOut(p);
                var bloomPen = new Pen(new SolidColorBrush(WithA(PulseColor, 0.35 * Math.Pow(1 - p, 2))), 2.5 * (1 - p) + 0.5);
                bloomPen.Freeze();
                dc.DrawRoundedRectangle(null, bloomPen, new Rect(-spread, -spread, w + spread * 2, h + spread * 2), r + spread, r + spread);
                break;
            }
        }
    }

    private static void DrawField(DrawingContext dc, Rect rect, Color color, Point center, double radius, double alpha)
    {
        var field = new RadialGradientBrush
        {
            Center = center, GradientOrigin = center, RadiusX = radius, RadiusY = radius * 1.6,
            GradientStops =
            {
                new GradientStop(WithA(color, Math.Clamp(alpha, 0, 1)), 0),
                new GradientStop(WithA(color, 0), 1),
            },
        };
        field.Freeze();
        dc.DrawRectangle(field, null, rect);
    }

    private static double EaseOut(double x) => 1 - Math.Pow(1 - x, 3);

    public static Color WithA(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);
}
