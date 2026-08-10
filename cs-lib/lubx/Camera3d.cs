// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Camera3d.hx と対)。
// Haxe 版の匿名 opts 引数 (optional 7 フィールド) は options class
// (Camera3dOpts) にする (stub の PassOpts / DrawOpts と同じ流儀)。
// optional は nullable フィールド + ?? で受ける。Gfx.size の multi-return は
// out 引数で受け、aspect の除算は (float) cast で整数除算を避ける。

/// <summary>Camera3d.vp のオプション。eye / target は必須、他は省略可。</summary>
public class Camera3dOpts
{
    public Vec3 eye = new Vec3(0, 0, 0);
    public Vec3 target = new Vec3(0, 0, 0);
    public Vec3? up;
    public float? fov;
    public float? near;
    public float? far;
    public float? aspect;
}

/// <summary>3D カメラ定型。perspective + lookAt から view-projection を
/// 1発で作る。</summary>
public static class Camera3d
{
    /// <summary>fov は度 (default 60)、near 0.1、far 100、up (0,1,0)。
    /// aspect 省略時は Gfx.size() の実比。</summary>
    public static Mat4 vp(Camera3dOpts opts)
    {
        var up = opts.up ?? new Vec3(0, 1, 0);
        var fov = opts.fov ?? 60.0f;
        var near = opts.near ?? 0.1f;
        var far = opts.far ?? 100.0f;
        float aspect;
        if (opts.aspect != null)
        {
            aspect = opts.aspect ?? 1.0f;
        }
        else
        {
            Gfx.size(out var gw, out var gh);
            aspect = (float)gw / gh;
        }
        var proj = Mat4.perspectiveLh(fov, aspect, near, far);
        var view = Mat4.lookAtLh(opts.eye, opts.target, up);
        return proj * view;
    }
}
