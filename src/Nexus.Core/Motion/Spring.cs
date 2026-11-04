using System;

namespace Nexus.Core.Motion;

/// <summary>
/// Damped harmonic spring parameters. DampingRatio &lt; 1 produces the "stretch, small controlled
/// overshoot, settle" feel; 1 is critically damped (no overshoot).
/// </summary>
public readonly record struct SpringConfig(double Stiffness, double DampingRatio, double Mass = 1.0)
{
    public double Damping => 2 * DampingRatio * Math.Sqrt(Stiffness * Mass);

    /// <summary>Container morphs: lively with a ~3-5% overshoot.</summary>
    public static SpringConfig Morph { get; } = new(300, 0.72);
    /// <summary>Primary content: quick and nearly critically damped.</summary>
    public static SpringConfig Content { get; } = new(420, 0.86);
    /// <summary>Glows, colours and ambient light: slow and soft.</summary>
    public static SpringConfig Ambient { get; } = new(90, 1.0);
    /// <summary>Press / hover micro-interactions.</summary>
    public static SpringConfig Micro { get; } = new(700, 0.6);

    /// <summary>Scales perceived speed. speed=2 settles in roughly half the time.</summary>
    public SpringConfig WithSpeed(double speed)
    {
        speed = Math.Clamp(speed, 0.25, 4);
        return this with { Stiffness = Stiffness * speed * speed };
    }

    public SpringConfig WithDamping(double ratio) => this with { DampingRatio = ratio };
}

/// <summary>Integrates a single spring-driven scalar. Pure maths, no timers, fully deterministic.</summary>
public struct SpringState
{
    public double Value;
    public double Velocity;
    public double Target;

    public SpringState(double value) { Value = value; Target = value; Velocity = 0; }

    public readonly bool IsAtRest(double precision) =>
        Math.Abs(Value - Target) < precision && Math.Abs(Velocity) < precision * 10;

    /// <summary>Advances the simulation. Returns true once the spring has settled (and snaps to target).</summary>
    public bool Step(double dt, in SpringConfig config, double precision)
    {
        if (IsAtRest(precision)) { Value = Target; Velocity = 0; return true; }

        // Clamp huge frame gaps (window was hidden, debugger break...) so the spring never explodes.
        dt = Math.Clamp(dt, 0, 0.064);
        const double maxStep = 1.0 / 240.0; // semi-implicit Euler is stable at this step for our stiffness range
        double k = config.Stiffness, c = config.Damping, m = config.Mass;
        while (dt > 1e-9)
        {
            double h = Math.Min(dt, maxStep);
            double accel = (-k * (Value - Target) - c * Velocity) / m;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }

        if (IsAtRest(precision)) { Value = Target; Velocity = 0; return true; }
        return false;
    }
}
