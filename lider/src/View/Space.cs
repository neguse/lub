// lider's space: the viewer is at the origin, and code is laid out on the surface of a cylinder of radius R.
// Coordinates are left-handed like lub's 3D world (+X right, +Y up, +Z forward), in meters.
// For the XR view (right-handed, forward -Z), Z is flipped.
// In non-XR you cannot turn your head to read, so the cylinder is unrolled onto the plane z = r. Angle a becomes x = r * a.
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public static class Space
{
    // Cylinder radius (the distance at which the reading surface is placed).
    // TODO: decide by measuring the Frame's focal distance
    public const float R = 1.6f;

    // Whether the cylinder is unrolled flat (outside XR)
    public static bool Flat;
    // Size of panels opened flat. With the default field of view, the panel height is 90% of the display
    public const float FlatScale = 1.5f;
    // Default field of view in non-XR (degrees)
    public const float FlatFov = 50;

    // Point on the surface at angle a, height y, radius r
    public static Vec3 At(float a, float y, float r)
    {
        return Flat ? new Vec3(r * a, y, r) : new Vec3(r * (float)Math.Sin(a), y, r * (float)Math.Cos(a));
    }

    // Facing direction of the surface at angle a (angle around the Y axis)
    public static float Facing(float a)
    {
        return Flat ? 0 : a;
    }

    // Model matrix that places a board of width w and height h at radius r, at cylinder angle a (positive to the right, radians) and height y
    public static Mat4 OnCylinder(float a, float y, float r, float w, float h)
    {
        return Mat4.Translate(At(a, y, r)) * Mat4.RotateY(Facing(a)) * Mat4.Scale(new Vec3(w, h, 1));
    }

    // Position within a board on the cylinder pointed at by a ray (top left 0,0, bottom right 1,1).
    // null if it does not hit
    public static Vec2? Hit(Ray ray, float a, float y, float r, float w, float h)
    {
        var center = At(a, y, r);
        var face = Facing(a);
        var normal = new Vec3(-(float)Math.Sin(face), 0, -(float)Math.Cos(face));
        var t = ray.IntersectPlane(center, normal);
        if (t == null)
            return null;
        var d = ray.At(t.Value) - center;
        var right = new Vec3((float)Math.Cos(face), 0, -(float)Math.Sin(face));
        var u = d.Dot(right) / w + 0.5f;
        var v = 0.5f - d.Y / h;
        if (u < 0 || u > 1 || v < 0 || v > 1)
            return null;
        return new Vec2(u, v);
    }

    // Position within a board curved along the cylinder (radius r) pointed at by a ray (top left 0,0, bottom right 1,1).
    // The board is centered at angle angle and height y, with arc length w and height h.
    // The ray starts inside the cylinder (the viewer is near the origin).
    // null if it does not hit
    public static Vec2? HitCurved(Ray ray, float angle, float y, float r, float w, float h)
    {
        var p = OnCylinderAt(ray, r);
        if (p == null)
            return null;
        var da = p.Angle - angle;
        while (!Flat && da > Math.PI)
            da -= 2 * (float)Math.PI;
        while (!Flat && da < -Math.PI)
            da += 2 * (float)Math.PI;
        var u = da / (w / r) + 0.5f;
        var v = 0.5f - (p.Y - y) / h;
        if (u < 0 || u > 1 || v < 0 || v > 1)
            return null;
        return new Vec2(u, v);
    }

    // When the view x is placed at view on the plane, the nearest view at which a panel with center center and half-width half fits on the display (half-width viewport).
    // If the panel is wider than the display, look at the panel's middle
    public static float FollowX(float view, float center, float half, float viewport)
    {
        if (half >= viewport)
            return center;
        if (center - half < view - viewport)
            return center - half + viewport;
        if (center + half > view + viewport)
            return center + half - viewport;
        return view;
    }

    // Whether, from the look angle gaze, the panel at angle target (half-angle width half) is off the view (half-angle fov) to the left (-1) or right (1). 0 if inside
    public static int OffView(float gaze, float target, float half, float fov)
    {
        var d = target - gaze;
        while (d > Math.PI)
            d -= 2 * (float)Math.PI;
        while (d < -Math.PI)
            d += 2 * (float)Math.PI;
        if (Math.Abs(d) <= fov + half)
            return 0;
        return d > 0 ? 1 : -1;
    }

    // Angle and height of the point where ray hits a cylinder of radius r from inside (null if it does not hit)
    public static CylinderPoint? OnCylinderAt(Ray ray, float r)
    {
        var o = ray.Origin;
        var d = ray.Dir;
        if (Flat)
        {
            if (d.Z < 1e-5f)
                return null;
            var hit = ray.At((r - o.Z) / d.Z);
            return new CylinderPoint(hit.X / r, hit.Y);
        }
        var qa = d.X * d.X + d.Z * d.Z;
        if (qa < 1e-8f)
            return null;
        var qb = 2 * (o.X * d.X + o.Z * d.Z);
        var qc = o.X * o.X + o.Z * o.Z - r * r;
        var disc = qb * qb - 4 * qa * qc;
        if (disc < 0)
            return null;
        var t = (-qb + (float)Math.Sqrt(disc)) / (2 * qa);
        if (t < 0)
            return null;
        var p = ray.At(t);
        return new CylinderPoint((float)Math.Atan2(p.X, p.Z), p.Y);
    }

    // Converts an XR eye matrix (anchor space, right-handed) into a form usable in lider's space
    public static Mat4 FromXr(float[] viewProjection)
    {
        var values = new List<float>();
        for (var i = 0; i < 16; i++)
            values.Add(viewProjection[i]);
        var m = Mat4.FromList(values, 0);
        return m * Mat4.Scale(new Vec3(1, 1, -1));
    }
}

// Panel hit by a ray, and the position within it (top left 0,0, bottom right 1,1)
public class WindowHit
{
    public Window Window;
    public Vec2 Uv;

    public WindowHit(Window window, Vec2 uv)
    {
        Window = window;
        Uv = uv;
    }
}

// Point on the cylinder (angle and height)
public class CylinderPoint
{
    public float Angle;
    public float Y;

    public CylinderPoint(float angle, float y)
    {
        Angle = angle;
        Y = y;
    }
}
