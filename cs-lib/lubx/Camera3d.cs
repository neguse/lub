// Camera3d of the implementation library lubx.
// opts are received as an options class (Camera3dOpts) (same style as the stub's PassOpts / DrawOpts).
// Optional values are received as nullable fields + ??.
// The multi-return of Gfx.size is received through out arguments, and the aspect division uses a (float) cast to avoid integer division.

using Lub;

namespace Lubx;

/// <summary>Options for Camera3d.vp.
/// eye / target are required; the rest are optional.
/// </summary>
public class Camera3dOpts
{
    public Vec3 Eye = new Vec3(0, 0, 0);
    public Vec3 Target = new Vec3(0, 0, 0);
    public Vec3? Up;
    public float? Fov;
    public float? Near;
    public float? Far;
    public float? Aspect;
}

/// <summary>3D camera boilerplate.
/// Builds the view-projection from perspective + lookAt in one go.
/// </summary>
public static class Camera3d
{
    /// <summary>fov is in degrees (default 60), near 0.1, far 100, up (0,1,0).
    /// If aspect is omitted, the actual ratio from Gfx.size() is used.
    /// </summary>
    public static Mat4 Vp(Camera3dOpts opts)
    {
        var up = opts.Up ?? new Vec3(0, 1, 0);
        var fov = opts.Fov ?? 60.0f;
        var near = opts.Near ?? 0.1f;
        var far = opts.Far ?? 100.0f;
        float aspect;
        if (opts.Aspect != null)
        {
            aspect = opts.Aspect ?? 1.0f;
        }
        else
        {
            Gfx.Size(out var gw, out var gh);
            aspect = (float)gw / gh;
        }
        var proj = Mat4.PerspectiveLh(fov, aspect, near, far);
        var view = Mat4.LookAtLh(opts.Eye, opts.Target, up);
        return proj * view;
    }

    /// <summary>Projects a world point to screen px.
    /// X / Y are px from (0,0) top-left to (viewportW, viewportH) bottom-right, Z is depth [0, 1].
    /// vp is the result of Vp.
    /// null if behind the camera (w is 0.01 or less).
    /// </summary>
    public static Vec3? Project(Mat4 vp, float x, float y, float z,
        float viewportW, float viewportH)
    {
        var c = vp * new Vec4(x, y, z, 1.0f);
        if (c.W <= 0.01f)
        {
            return null;
        }
        return new Vec3((c.X / c.W * 0.5f + 0.5f) * viewportW,
            (0.5f - c.Y / c.W * 0.5f) * viewportH, c.Z / c.W);
    }

    /// <summary>Line of sight from screen px (x, y).
    /// The origin is a point on the near plane, and the direction points from there to a point on the far plane.
    /// vp is the result of Vp.
    /// Get the intersection with the floor with ViewportRay(...).IntersectPlane(...).
    /// </summary>
    public static Ray ViewportRay(Mat4 vp, float x, float y,
        float viewportW, float viewportH)
    {
        var inv = vp.Inverse();
        var nx = x / viewportW * 2.0f - 1.0f;
        var ny = 1.0f - y / viewportH * 2.0f;
        var a = inv * new Vec4(nx, ny, 0.0f, 1.0f);
        var b = inv * new Vec4(nx, ny, 1.0f, 1.0f);
        var near = new Vec3(a.X / a.W, a.Y / a.W, a.Z / a.W);
        var far = new Vec3(b.X / b.W, b.Y / b.W, b.Z / b.W);
        return new Ray(near, far - near);
    }
}
