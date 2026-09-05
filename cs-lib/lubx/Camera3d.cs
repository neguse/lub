// 実装ライブラリ lubx の Camera3d。
// opts は options class (Camera3dOpts) で受ける (stub の PassOpts / DrawOpts
// と同じ流儀)。
// optional は nullable フィールド + ?? で受ける。Gfx.size の multi-return は
// out 引数で受け、aspect の除算は (double) cast で整数除算を避ける。

/// <summary>Camera3d.vp のオプション。eye / target は必須、他は省略可。</summary>
using static Lub;

public class Camera3dOpts
{
    public Vec3 Eye = new Vec3(0, 0, 0);
    public Vec3 Target = new Vec3(0, 0, 0);
    public Vec3? Up;
    public double? Fov;
    public double? Near;
    public double? Far;
    public double? Aspect;
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
        var fov = opts.Fov ?? 60.0;
        var near = opts.Near ?? 0.1;
        var far = opts.Far ?? 100.0;
        double aspect;
        if (opts.Aspect != null)
        {
            aspect = opts.Aspect ?? 1.0;
        }
        else
        {
            Gfx.Size(out var gw, out var gh);
            aspect = (double)gw / gh;
        }
        var proj = Mat4.PerspectiveLh(fov, aspect, near, far);
        var view = Mat4.LookAtLh(opts.Eye, opts.Target, up);
        return proj * view;
    }
}
