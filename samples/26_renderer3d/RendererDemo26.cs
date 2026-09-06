// lub の samples/26_renderer3d 相当を TinyC# で書いた entry。
// 実行: lub samples/26_renderer3d/RendererDemo26.csproj (transpile + watch + hot reload)
//
// lubx.Renderer3d の最小デモ。プリミティブ (床・箱・円柱・球) と
// skinned SDF キャラを投げるだけで、影 + hemispheric ambient + AgX tonemap
// の絵が出ることを見せる。ポーズは時刻からの決定的アニメ (乱数なし)。
//
// options は Camera / Draw3dOpts / Renderer3dFog / Renderer3dOutline の
// class で渡す。Renderer3d や Mesh3d
// は static 初期化子でなく onFrame からの build() で遅延生成する
// (cs-lib クラスは load 順の都合で static 初期化子から呼べない)。

using System;
using System.Collections.Generic;
using static Lub;

public static class RendererDemo26
{
    static float t = 0.0f;

    static Renderer3d? ren = null;
    static Mesh3d? cube = null;
    static Mesh3d? cyl = null;
    static Mesh3d? sph = null;
    static Mesh3d? chara = null;
    static bool built = false;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    // bone 付き SDF 雪だるま。腕を振る。
    static SdfNode CharaModel()
    {
        var body = Sdf.Sphere(0.5f).Move(0, 0.5f, 0)
            .Bone("body", new Vec3(0, 0.3f, 0));
        var head = Sdf.Sphere(0.32f).Move(0, 1.05f, 0)
            .Bone("head", new Vec3(0, 0.8f, 0));
        var armL = Sdf.Capsule(new Vec3(0.42f, 0.75f, 0),
            new Vec3(0.95f, 1.05f, 0), 0.09f)
            .Bone("arm_l", new Vec3(0.42f, 0.75f, 0));
        var armR = Sdf.Capsule(new Vec3(-0.42f, 0.75f, 0),
            new Vec3(-0.95f, 1.05f, 0), 0.09f)
            .Bone("arm_r", new Vec3(-0.42f, 0.75f, 0));
        var trunk = body.Smin(head, 0.08f).Smin(armL, 0.05f).Smin(armR, 0.05f)
            .Paint(0xF2EEE6);
        var eye = Sdf.Sphere(0.045f).Move(0.11f, 1.14f, -0.27f).MirrorX()
            .Paint(0x24211E, 0.0f, 0.3f);
        var nose = Sdf.Capsule(new Vec3(0, 1.02f, -0.30f),
            new Vec3(0, 1.0f, -0.48f), 0.05f).Paint(0xE07830);
        return trunk.Union(eye).Union(nose);
    }

    static void Build()
    {
        if (built) return;
        ren = new Renderer3d("demo26");
        var cubeM = new Mesh3d("demo26_cube");
        cubeM.Rebuild(Shapes3d.Cube());
        var cylM = new Mesh3d("demo26_cyl");
        cylM.Rebuild(Shapes3d.Cylinder(28));
        var sphM = new Mesh3d("demo26_sph");
        sphM.Rebuild(Shapes3d.Sphere(14, 24));
        var charaM = new Mesh3d("demo26_chara");
        charaM.Rebuild(Sdf.Mesh(CharaModel(), 48));
        cube = cubeM;
        cyl = cylM;
        sph = sphM;
        chara = charaM;
        built = true;
    }

    public static void OnFrame(float dt)
    {
        t = t + dt;
        Build();
        var r = ren;
        var cubeM = cube;
        var cylM = cyl;
        var sphM = sph;
        var charaM = chara;
        if (r == null || cubeM == null || cylM == null || sphM == null
            || charaM == null)
            return;

        r.Shadow.Extent = 7.0f;
        if (Environment.GetEnvironmentVariable("LUB_R3D_NOSSAO") != null)
            r.Ssao.Enabled = false;
        r.DebugView = Environment.GetEnvironmentVariable("LUB_R3D_DEBUG");
        if (Environment.GetEnvironmentVariable("LUB_R3D_STYLE") != null)
        {
            r.Fog = new Renderer3dFog(Color.Rgb(0.55f, 0.6f, 0.7f), 0.045f);
            r.Outline = new Renderer3dOutline(Color.Rgb(0.1f, 0.08f, 0.12f), 0.4f);
            r.Vignette = 0.35f;
        }
        r.Begin(new Camera
        {
            Eye = new Vec3((float)Math.Cos(t * 0.3f) * 7.5f, 4.2f,
                (float)Math.Sin(t * 0.3f) * 7.5f),
            Target = new Vec3(0, 0.7f, 0),
            Fov = 42,
        });

        // 床 (薄い箱)
        r.Draw(cubeM, Mat4.Translate(new Vec3(0, -0.1f, 0))
            * Mat4.Scale(new Vec3(5.5f, 0.1f, 5.5f)),
            new Draw3dOpts { Tint = Color.Hex(0x76816F) });
        // 箱・円柱・球
        r.Draw(cubeM, Mat4.Translate(new Vec3(-2.2f, 0.5f, 1.2f))
            * Mat4.RotateY(t * 0.7f) * Mat4.Scale(new Vec3(0.5f, 0.5f, 0.5f)),
            new Draw3dOpts { Tint = Color.Hex(0xE8A33D) });
        r.Draw(cylM, Mat4.Translate(new Vec3(2.1f, 0.6f, 1.4f))
            * Mat4.Scale(new Vec3(0.45f, 1.2f, 0.45f)),
            new Draw3dOpts { Tint = Color.Hex(0x4FB8C4) });
        r.Draw(sphM, Mat4.Translate(new Vec3(1.6f,
            0.55f + Math.Abs((float)Math.Sin(t * 2.0f)) * 0.8f, -1.6f))
            * Mat4.Scale(new Vec3(0.55f, 0.55f, 0.55f)),
            new Draw3dOpts { Tint = Color.Hex(0xE85C5C) });
        // 半透明の板
        r.Draw(cubeM, Mat4.Translate(new Vec3(0, 0.9f, 2.6f))
            * Mat4.Scale(new Vec3(1.6f, 0.9f, 0.04f)),
            new Draw3dOpts
            {
                Tint = Color.Rgb(0.55f, 0.75f, 0.95f, 0.35f),
                Blend = Gfx.Blend.Alpha,
            });

        // 高輝度ランプ (bloom が拾う)
        r.Draw(sphM, Mat4.Translate(new Vec3(-1.9f, 2.6f, -1.9f))
            * Mat4.Scale(new Vec3(0.22f, 0.22f, 0.22f)),
            new Draw3dOpts { Tint = Color.Rgb(5.0f, 4.2f, 2.4f) });

        // skinned キャラ (腕振り)
        float wave = (float)Math.Sin(t * 3.0f) * 0.5f;
        var bones = Bones.Pack(charaM.Data, (name, x, y, z) =>
        {
            if (name == "arm_l")
                return Bones.PivotRot(x, y, z, Mat4.RotateZ(0.3f + wave * 0.6f));
            if (name == "arm_r")
                return Bones.PivotRot(x, y, z, Mat4.RotateZ(-0.3f + wave * 0.6f));
            if (name == "head")
                return Bones.PivotRot(x, y, z,
                    Mat4.RotateX((float)Math.Sin(t * 1.7f) * 0.12f));
            return null;
        });
        r.Draw(charaM, Mat4.Translate(new Vec3(0, 0, 0))
            * Mat4.RotateY((float)Math.PI), new Draw3dOpts { Bones = bones });

        r.End();
    }
}
