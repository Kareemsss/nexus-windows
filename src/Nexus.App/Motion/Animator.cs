using System.Windows.Media;
using Nexus.Core.Motion;

namespace Nexus.App.Motion;

/// <summary>Something driven by the frame clock. Return false from Advance once settled.</summary>
public interface IAnimatable
{
    bool Advance(double dt);
}

/// <summary>
/// The single frame clock for all NEXUS motion. It hooks CompositionTarget.Rendering ONLY while something is
/// moving and unhooks as soon as everything has settled, so an idle island costs zero frames, zero CPU and
/// zero GPU. (A permanently attached Rendering handler would force WPF to render at display rate forever.)
/// </summary>
public static class Animator
{
    private static readonly List<IAnimatable> Active = new();
    private static readonly List<IAnimatable> Scratch = new();
    private static bool _hooked;
    private static TimeSpan _lastTime;

    /// <summary>Global speed multiplier from settings (0.5 .. 2).</summary>
    public static double Speed { get; set; } = 1.0;
    /// <summary>Theme motion personality (Cyber is slightly snappier). Multiplies <see cref="Speed"/>.</summary>
    public static double Personality { get; set; } = 1.0;
    /// <summary>Accessibility: replace springs with short, non-overshooting motion.</summary>
    public static bool ReduceMotion { get; set; }
    /// <summary>Eco mode (see AppearanceSettings.EcoMode).</summary>
    public static bool Eco { get; set; }
    /// <summary>Frame cap. On high-refresh monitors this avoids redrawing the overlay 120-165 times per second.</summary>
    public static int MaxFps => Eco ? 30 : 60;
    public static bool IsRunning => _hooked;
    public static DateTime LastFrameUtc { get; private set; } = DateTime.UtcNow;

    public static SpringConfig Tune(SpringConfig config)
    {
        if (ReduceMotion) return new SpringConfig(config.Stiffness * 2.2, 1.0);
        return config.WithSpeed(Speed * Personality);
    }

    public static void Wake(IAnimatable item)
    {
        if (!Active.Contains(item)) Active.Add(item);
        if (_hooked) return;
        _hooked = true;
        _lastTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var time = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.FromTicks(DateTime.UtcNow.Ticks);
        // WPF can raise Rendering more than once per frame with the same timestamp: ignore duplicates.
        if (time == _lastTime) return;
        // Frame cap: leaving visuals untouched on this tick means WPF neither re-renders nor re-copies the layered window.
        if (_lastTime != TimeSpan.Zero && (time - _lastTime).TotalSeconds < 1.0 / MaxFps - 0.002) return;
        double dt = _lastTime == TimeSpan.Zero ? 1.0 / 60 : (time - _lastTime).TotalSeconds;
        _lastTime = time;
        LastFrameUtc = DateTime.UtcNow;

        Scratch.Clear();
        Scratch.AddRange(Active);
        foreach (var item in Scratch)
        {
            bool alive;
            try { alive = item.Advance(dt); }
            catch (Exception ex) { Core.Diagnostics.Log.Error("Animator", "Animation step failed", ex); alive = false; }
            if (!alive) Active.Remove(item);
        }

        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }
}

/// <summary>A spring-driven scalar using the shared spring maths from Nexus.Core.</summary>
public sealed class Spring
{
    private SpringState _state;
    private readonly SpringConfig _base;

    public Spring(double initial, SpringConfig config, double precision = 0.01)
    {
        _state = new SpringState(initial);
        _base = config;
        Precision = precision;
    }

    public double Precision { get; }
    public double Value => _state.Value;
    public double Velocity => _state.Velocity;
    public double Target
    {
        get => _state.Target;
        set => _state.Target = value;
    }

    public bool IsMoving => !_state.IsAtRest(Precision);

    /// <summary>Jump without animation.</summary>
    public void Snap(double value) { _state.Value = value; _state.Target = value; _state.Velocity = 0; }

    /// <summary>Adds velocity – used for "kick" micro-interactions such as a press bounce.</summary>
    public void Impulse(double velocity) => _state.Velocity += velocity;

    /// <summary>Returns true while still moving.</summary>
    public bool Step(double dt) => !_state.Step(dt, Animator.Tune(_base), Precision);
}

/// <summary>Colour transition driven by a 0→1 spring (perceptually smoother than four independent springs).</summary>
public sealed class ColorSpring
{
    private readonly Spring _progress = new(1, SpringConfig.Ambient, 0.002);
    private Color _from, _to;

    public ColorSpring(Color initial) { _from = _to = initial; }

    public Color Value => Lerp(_from, _to, Math.Clamp(_progress.Value, 0, 1));
    public Color Target => _to;

    public void SetTarget(Color c)
    {
        if (c == _to) return;
        _from = Value;
        _to = c;
        _progress.Snap(0);
        _progress.Target = 1;
    }

    public void Snap(Color c) { _from = _to = c; _progress.Snap(1); }

    public bool Step(double dt) => _progress.Step(dt);

    public static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
