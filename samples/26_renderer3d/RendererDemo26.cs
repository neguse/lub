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
    static float t = 0.0f;

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
        var body = Sdf.sphere(0.5f).move(0, 0.5f, 0)
            .bone("body", new Vec3(0, 0.3f, 0));
        var head = Sdf.sphere(0.32f).move(0, 1.05f, 0)
            .bone("head", new Vec3(0, 0.8f, 0));
        var armL = Sdf.capsule(new Vec3(0.42f, 0.75f, 0),
            new Vec3(0.95f, 1.05f, 0), 0.09f)
            .bone("arm_l", new Vec3(0.42f, 0.75f, 0));
        var armR = Sdf.capsule(new Vec3(-0.42f, 0.75f, 0),
            new Vec3(-0.95f, 1.05f, 0), 0.09f)
            .bone("arm_r", new Vec3(-0.42f, 0.75f, 0));
        var trunk = body.smin(head, 0.08f).smin(armL, 0.05f).smin(armR, 0.05f)
            .paint(0xF2EEE6);
        var eye = Sdf.sphere(0.045f).move(0.11f, 1.14f, -0.27f).mirrorX()
            .paint(0x24211E, 0.0f, 0.3f);
        var nose = Sdf.capsule(new Vec3(0, 1.02f, -0.30f),
            new Vec3(0, 1.0f, -0.48f), 0.05f).paint(0xE07830);
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

    public static void onFrame(float dt)
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

        r.shadow.extent = 7.0f;
        if (os.getenv("LUB_R3D_NOSSAO") != null)
            r.ssao.enabled = false;
        r.debugView = os.getenv("LUB_R3D_DEBUG");
        if (os.getenv("LUB_R3D_STYLE") != null)
        {
            r.fog = new Renderer3dFog(Color.rgb(0.55f, 0.6f, 0.7f), 0.045f);
            r.outline = new Renderer3dOutline(Color.rgb(0.1f, 0.08f, 0.12f), 0.4f);
            r.vignette = 0.35f;
        }
        r.begin(new Camera
        {
            eye = new Vec3((float)Math.Cos(t * 0.3f) * 7.5f, 4.2f,
                (float)Math.Sin(t * 0.3f) * 7.5f),
            target = new Vec3(0, 0.7f, 0),
            fov = 42,
        });

        // 床 (薄い箱)
        r.draw(cubeM, Mat4.translate(new Vec3(0, -0.1f, 0))
            * Mat4.scale(new Vec3(5.5f, 0.1f, 5.5f)),
            new Draw3dOpts { tint = Color.hex(0x76816F) });
        // 箱・円柱・球
        r.draw(cubeM, Mat4.translate(new Vec3(-2.2f, 0.5f, 1.2f))
            * Mat4.rotateY(t * 0.7f) * Mat4.scale(new Vec3(0.5f, 0.5f, 0.5f)),
            new Draw3dOpts { tint = Color.hex(0xE8A33D) });
        r.draw(cylM, Mat4.translate(new Vec3(2.1f, 0.6f, 1.4f))
            * Mat4.scale(new Vec3(0.45f, 1.2f, 0.45f)),
            new Draw3dOpts { tint = Color.hex(0x4FB8C4) });
        r.draw(sphM, Mat4.translate(new Vec3(1.6f,
            0.55f + Math.Abs((float)Math.Sin(t * 2.0f)) * 0.8f, -1.6f))
            * Mat4.scale(new Vec3(0.55f, 0.55f, 0.55f)),
            new Draw3dOpts { tint = Color.hex(0xE85C5C) });
        // 半透明の板
        r.draw(cubeM, Mat4.translate(new Vec3(0, 0.9f, 2.6f))
            * Mat4.scale(new Vec3(1.6f, 0.9f, 0.04f)),
            new Draw3dOpts
            {
                tint = Color.rgb(0.55f, 0.75f, 0.95f, 0.35f),
                blend = Gfx.ALPHA,
            });

        // 高輝度ランプ (bloom が拾う)
        r.draw(sphM, Mat4.translate(new Vec3(-1.9f, 2.6f, -1.9f))
            * Mat4.scale(new Vec3(0.22f, 0.22f, 0.22f)),
            new Draw3dOpts { tint = Color.rgb(5.0f, 4.2f, 2.4f) });

        // skinned キャラ (腕振り)
        float wave = (float)Math.Sin(t * 3.0f) * 0.5f;
        var bones = Bones.pack(charaM.data, (name, x, y, z) =>
        {
            if (name == "arm_l")
                return Bones.pivotRot(x, y, z, Mat4.rotateZ(0.3f + wave * 0.6f));
            if (name == "arm_r")
                return Bones.pivotRot(x, y, z, Mat4.rotateZ(-0.3f + wave * 0.6f));
            if (name == "head")
                return Bones.pivotRot(x, y, z,
                    Mat4.rotateX((float)Math.Sin(t * 1.7f) * 0.12f));
            return null;
        });
        r.draw(charaM, Mat4.translate(new Vec3(0, 0, 0))
            * Mat4.rotateY((float)Math.PI), new Draw3dOpts { bones = bones });

        r.End();
    }
}
