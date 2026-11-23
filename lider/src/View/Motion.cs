// Motions used for interaction feedback: values that approach a target by spring, panel visual position and size, particles.
// All advance by dt and never make input wait (when a new target arrives, they chase it from the current motion).
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

// Value that approaches a target.
// Damping is a bit below critical (damping ratio 0.67), so it overshoots slightly before settling
public class Spring
{
    public float Value;
    public float Velocity;
    public float Stiffness = 180;
    public float Damping = 18;

    public Spring(float value)
    {
        Value = value;
    }

    public void Step(float target, float dt)
    {
        // Advance in 1/120-second steps so it stays stable even with a large dt
        while (dt > 0)
        {
            var h = Math.Min(dt, 1.0f / 120);
            var a = Stiffness * (target - Value) - Damping * Velocity;
            Velocity += a * h;
            Value += Velocity * h;
            dt -= h;
        }
    }

    public void Snap(float value)
    {
        Value = value;
        Velocity = 0;
    }
}

public static class Ease
{
    // Moves v toward target at a speed that covers the 0-to-1 range in seconds (no overshoot)
    public static float Toward(float v, float target, float dt, float seconds)
    {
        var step = dt / seconds;
        return v < target ? Math.Min(target, v + step) : Math.Max(target, v - step);
    }
}

// Panel visuals.
// When opened, it grows from a small size; when closed, it shrinks in place and disappears
public class WindowMotion
{
    // Size before opening and after closing
    public const float SmallScale = 0.15f;
    public readonly Spring Angle;
    public readonly Spring Y;
    public readonly Spring Scale;
    // Depth (follows the Z of the Window's Position; negative is nearer)
    public readonly Spring Z = new Spring(0);
    // 1 while focused (the edge glows)
    public readonly Spring Glow = new Spring(0);
    // After closing, drawn until fully shrunk
    public bool Closing;
    public float ClosingFor;
    public const float CloseSeconds = 0.35f;
    // Strength of the frame shake when a save is applied, and the mark on the jumped-to line (both decrease from 1 to 0)
    public float Shake;
    public float Arrive;
    // Time of the save, the seconds the apply took, and the remaining seconds for showing it in the title
    public float SavedAt;
    public float LandSeconds;
    public float LandLabel;
    // Save wave (decreases from 1 to 0), the typed-line flash, and the red glow for compile errors
    public float Wave;
    public float Pulse;
    public float Alarm;
    // Wave spreading from where a line finished sinking into the surface (decreases from 1 to 0), and its center (px in the view)
    public float Ripple;
    public float RippleAt;
    // Cursor visual (x is px in the view, y is px from the start of the text).
    // Settles in about 0.2 seconds without overshoot, to keep up with typing speed
    public readonly Spring CaretX = Caret();
    public readonly Spring CaretY = Caret();
    public bool CaretShown;
    // Cursor in the previous frame (line and byte column), the byte length of that line, and the cursor x
    public int TypedLine;
    public int TypedCol;
    public int TypedLength;
    public float TypedX;
    // File the Seen* values were taken from (changes when the tab changes)
    public Document? SeenDoc;
    public int SeenRemoved;
    public int SeenSaved;
    public int SeenFailures;
    public int SeenLanded;
    public int SeenRevision;

    public WindowMotion(float angle, float y, float scale)
    {
        Angle = new Spring(angle);
        Y = new Spring(y);
        Scale = new Spring(scale);
    }

    public void Step(float angle, float y, float z, float scale, bool focused, float dt)
    {
        Angle.Step(angle, dt);
        Y.Step(y, dt);
        Z.Step(z, dt);
        Scale.Step(scale, dt);
        Glow.Step(focused ? 1 : 0, dt);
        Shake = Math.Max(0, Shake - dt / 0.3f);
        Arrive = Math.Max(0, Arrive - dt / 0.4f);
        LandLabel = Math.Max(0, LandLabel - dt);
        Wave = Math.Max(0, Wave - dt / 0.45f);
        Pulse = Math.Max(0, Pulse - dt / 0.25f);
        Alarm = Math.Max(0, Alarm - dt / 0.8f);
        Ripple = Math.Max(0, Ripple - dt / 0.6f);
        if (Closing)
            ClosingFor += dt;
    }

    public bool Gone => Closing && ClosingFor >= CloseSeconds;

    static Spring Caret()
    {
        var s = new Spring(0);
        s.Stiffness = 1600;
        s.Damping = 80;
        return s;
    }
}

// Typed characters (px X0 to X1 on line Line, in the view).
// The same part of the panel texture is drawn lifted in front and larger, and settles into the surface over Seconds
public class Landing
{
    public const float Seconds = 0.2f;
    public Window Window;
    public int Line;
    public float X0;
    public float X1;
    public float Age;

    public Landing(Window window, int line, float x0, float x1)
    {
        Window = window;
        Line = line;
        X0 = x0;
        X1 = x1;
    }
}

// Particle (cylinder angle, height, radius, and their velocities)
public class Particle
{
    public float Angle;
    public float Y;
    public float Radius;
    public float VAngle;
    public float VY;
    public float VRadius;
    public float Life;
    public float MaxLife;
    public Color Color;
    // Size factor
    public float Size = 1;

    public Particle(float angle, float y, float radius, Color color, float life)
    {
        Angle = angle;
        Y = y;
        Radius = radius;
        Color = color;
        Life = life;
        MaxLife = life;
    }
}

// Square ring that spreads from the impact point and disappears (a crisp line, not a glow)
public class Ring
{
    public const float Seconds = 0.32f;
    public float Angle;
    public float Y;
    public float Size;
    public float Age;
    public Color Color = Color.Rgb(1, 1, 1);
}

public class Particles
{
    public readonly List<Particle> Live = new List<Particle>();
    int seed = 12345;

    // Pseudo-random number from 0 to 1 (same values in the same order)
    public float Next()
    {
        seed = seed * 1664525 + 1013904223;
        return (float)((seed >> 8) & 0xffff) / 65535.0f;
    }

    // Scatters n particles from (angle, y, radius) with speeds within speed (m/s).
    // toward is the speed in the radial direction (negative means toward the viewer)
    public void Burst(float angle, float y, float radius, int n, float speed, float toward, Color color, float life, float size = 1)
    {
        for (var i = 0; i < n; i++)
        {
            var p = new Particle(angle, y, radius, color, life * (0.6f + 0.4f * Next()));
            p.Size = size;
            p.VAngle = (Next() - 0.5f) * speed / radius;
            p.VY = (Next() - 0.3f) * speed;
            p.VRadius = toward * (0.5f + Next());
            Live.Add(p);
        }
    }

    public void Step(float dt)
    {
        for (var i = Live.Count - 1; i >= 0; i--)
        {
            var p = Live[i];
            p.Life -= dt;
            if (p.Life <= 0)
            {
                Live.RemoveAt(i);
                continue;
            }
            p.Angle += p.VAngle * dt;
            p.Y += p.VY * dt;
            p.Radius += p.VRadius * dt;
            // Slows down while falling
            p.VY -= 0.6f * dt;
            p.VAngle *= 1 - 2 * dt;
        }
    }
}
