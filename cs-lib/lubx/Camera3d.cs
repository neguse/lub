// 実装ライブラリ lubx の Camera3d。
// opts は options class (Camera3dOpts) で受ける (stub の PassOpts / DrawOpts
// と同じ流儀)。
// optional は nullable フィールド + ?? で受ける。Gfx.size の multi-return は
// out 引数で受け、aspect の除算は (float) cast で整数除算を避ける。

using Lub;

namespace Lubx;

/// <summary>Camera3d.vp のオプション。eye / target は必須、他は省略可。</summary>
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

/// <summary>3D カメラ定型。perspective + lookAt から view-projection を
/// 1発で作る。</summary>
public static class Camera3d
{
    /// <summary>fov は度 (default 60)、near 0.1、far 100、up (0,1,0)。
    /// aspect 省略時は Gfx.size() の実比。</summary>
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

    /// <summary>world 点を画面 px に射影する。
    /// X / Y は (0,0) 左上から (screenW, screenH) 右下の px、Z は depth [0, 1]。
    /// vp は Vp の結果。カメラの後ろ (w が 0.01 以下) なら null。</summary>
    public static Vec3? Project(Mat4 vp, float x, float y, float z,
        float screenW, float screenH)
    {
        var c = vp * new Vec4(x, y, z, 1.0f);
        if (c.W <= 0.01f)
        {
            return null;
        }
        return new Vec3((c.X / c.W * 0.5f + 0.5f) * screenW,
            (0.5f - c.Y / c.W * 0.5f) * screenH, c.Z / c.W);
    }

    /// <summary>画面 px (sx, sy) から出る視線。始点は near 面上の点、方向はそこから far 面上の点へ向かう。
    /// vp は Vp の結果。床との交点は ScreenRay(...).IntersectPlane(...) で求める。</summary>
    public static Ray ScreenRay(Mat4 vp, float sx, float sy,
        float screenW, float screenH)
    {
        var inv = vp.Inverse();
        var nx = sx / screenW * 2.0f - 1.0f;
        var ny = 1.0f - sy / screenH * 2.0f;
        var a = inv * new Vec4(nx, ny, 0.0f, 1.0f);
        var b = inv * new Vec4(nx, ny, 1.0f, 1.0f);
        var near = new Vec3(a.X / a.W, a.Y / a.W, a.Z / a.W);
        var far = new Vec3(b.X / b.W, b.Y / b.W, b.Z / b.W);
        return new Ray(near, far - near);
    }
}
