// lub の samples/26_renderer3d 相当を TinyC# で書いた entry。
// 実行: lub samples/26_renderer3d/RendererDemo26.csproj (transpile + watch + hot reload)
//
// lubx.Renderer3d の最小デモ。プリミティブ (床・箱・円柱・球) と
// skinned SDF キャラを投げるだけで、影 + hemispheric ambient + AgX tonemap
// の絵が出ることを見せる。ポーズは時刻からの決定的アニメ (乱数なし)。
//
// Haxe 版との対応: 匿名構造体は Camera / Draw3dOpts / Renderer3dFog /
// Renderer3dOutline の options class、end() は End()。Renderer3d や Mesh3d
// は static 初期化子でなく onFrame からの build() で遅延生成する
// (cs-lib クラスは load 順の都合で static 初期化子から呼べない)。

using System;
using System.Collections.Generic;

public static class RendererDemo26
{
    static double t = 0.0;

    static Renderer3d? ren = null;
    static Mesh3d? cube = null;
    static Mesh3d? cyl = null;
    static Mesh3d? sph = null;
    static Mesh3d? chara = null;
    static bool built = false;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    // bone 付き SDF 雪だるま。腕を振る。
    static SdfNode charaModel()
    {
        var body = Sdf.sphere(0.5).move(0, 0.5, 0)
            .bone("body", new Vec3(0, 0.3, 0));
        var head = Sdf.sphere(0.32).move(0, 1.05, 0)
            .bone("head", new Vec3(0, 0.8, 0));
        var armL = Sdf.capsule(new Vec3(0.42, 0.75, 0),
            new Vec3(0.95, 1.05, 0), 0.09)
            .bone("arm_l", new Vec3(0.42, 0.75, 0));
        var armR = Sdf.capsule(new Vec3(-0.42, 0.75, 0),
            new Vec3(-0.95, 1.05, 0), 0.09)
            .bone("arm_r", new Vec3(-0.42, 0.75, 0));
        var trunk = body.smin(head, 0.08).smin(armL, 0.05).smin(armR, 0.05)
            .paint(0xF2EEE6);
        var eye = Sdf.sphere(0.045).move(0.11, 1.14, -0.27).mirrorX()
            .paint(0x24211E, 0.0, 0.3);
        var nose = Sdf.capsule(new Vec3(0, 1.02, -0.30),
            new Vec3(0, 1.0, -0.48), 0.05).paint(0xE07830);
        return trunk.union(eye).union(nose);
    }

    static void build()
    {
        if (built) return;
        ren = new Renderer3d("demo26");
        var cubeM = new Mesh3d("demo26_cube");
        cubeM.rebuild(Shapes3d.cube());
        var cylM = new Mesh3d("demo26_cyl");
        cylM.rebuild(Shapes3d.cylinder(28));
        var sphM = new Mesh3d("demo26_sph");
        sphM.rebuild(Shapes3d.sphere(14, 24));
        var charaM = new Mesh3d("demo26_chara");
        charaM.rebuild(Sdf.mesh(charaModel(), 48));
        cube = cubeM;
        cyl = cylM;
        sph = sphM;
        chara = charaM;
        built = true;
    }

    public static void onFrame(double dt)
    {
        t = t + dt;
        build();
        var r = ren;
        var cubeM = cube;
        var cylM = cyl;
        var sphM = sph;
        var charaM = chara;
        if (r == null || cubeM == null || cylM == null || sphM == null
            || charaM == null)
            return;

        r.shadow.extent = 7.0;
        if (os.getenv("LUB_R3D_NOSSAO") != null)
            r.ssao.enabled = false;
        r.debugView = os.getenv("LUB_R3D_DEBUG");
        if (os.getenv("LUB_R3D_STYLE") != null)
        {
            r.fog = new Renderer3dFog(Color.rgb(0.55, 0.6, 0.7), 0.045);
            r.outline = new Renderer3dOutline(Color.rgb(0.1, 0.08, 0.12), 0.4);
            r.vignette = 0.35;
        }
        r.begin(new Camera
        {
            eye = new Vec3(Math.Cos(t * 0.3) * 7.5, 4.2,
                Math.Sin(t * 0.3) * 7.5),
            target = new Vec3(0, 0.7, 0),
            fov = 42,
        });

        // 床 (薄い箱)
        r.draw(cubeM, Mat4.translate(new Vec3(0, -0.1, 0))
            * Mat4.scale(new Vec3(5.5, 0.1, 5.5)),
            new Draw3dOpts { tint = Color.hex(0x76816F) });
        // 箱・円柱・球
        r.draw(cubeM, Mat4.translate(new Vec3(-2.2, 0.5, 1.2))
            * Mat4.rotateY(t * 0.7) * Mat4.scale(new Vec3(0.5, 0.5, 0.5)),
            new Draw3dOpts { tint = Color.hex(0xE8A33D) });
        r.draw(cylM, Mat4.translate(new Vec3(2.1, 0.6, 1.4))
            * Mat4.scale(new Vec3(0.45, 1.2, 0.45)),
            new Draw3dOpts { tint = Color.hex(0x4FB8C4) });
        r.draw(sphM, Mat4.translate(new Vec3(1.6,
            0.55 + Math.Abs(Math.Sin(t * 2.0)) * 0.8, -1.6))
            * Mat4.scale(new Vec3(0.55, 0.55, 0.55)),
            new Draw3dOpts { tint = Color.hex(0xE85C5C) });
        // 半透明の板
        r.draw(cubeM, Mat4.translate(new Vec3(0, 0.9, 2.6))
            * Mat4.scale(new Vec3(1.6, 0.9, 0.04)),
            new Draw3dOpts
            {
                tint = Color.rgb(0.55, 0.75, 0.95, 0.35),
                blend = Gfx.ALPHA,
            });

        // 高輝度ランプ (bloom が拾う)
        r.draw(sphM, Mat4.translate(new Vec3(-1.9, 2.6, -1.9))
            * Mat4.scale(new Vec3(0.22, 0.22, 0.22)),
            new Draw3dOpts { tint = Color.rgb(5.0, 4.2, 2.4) });

        // skinned キャラ (腕振り)
        double wave = Math.Sin(t * 3.0) * 0.5;
        var bones = Bones.pack(charaM.data, (name, x, y, z) =>
        {
            if (name == "arm_l")
                return Bones.pivotRot(x, y, z, Mat4.rotateZ(0.3 + wave * 0.6));
            if (name == "arm_r")
                return Bones.pivotRot(x, y, z, Mat4.rotateZ(-0.3 + wave * 0.6));
            if (name == "head")
                return Bones.pivotRot(x, y, z,
                    Mat4.rotateX(Math.Sin(t * 1.7) * 0.12));
            return null;
        });
        r.draw(charaM, Mat4.translate(new Vec3(0, 0, 0))
            * Mat4.rotateY(Math.PI), new Draw3dOpts { bones = bones });

        r.End();
    }
}
