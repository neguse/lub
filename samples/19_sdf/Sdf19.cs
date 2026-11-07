// lub の samples/19_sdf の entry。
// 実行: lub samples/19_sdf/Sdf19.csproj (transpile + watch + hot reload)
//
// SDF モデリング: Lubx.Sdf の builder でツリーを組み、C 側 (sdf_mesh) が評価 → surface nets でメッシュ化する。
// Model() を書き換えて保存すれば hot reload で再メッシュされる。
// 法線と材質 (paint) は SDF から頂点に焼くので、UV は存在しない。
// 描画は Renderer3d の組み込みの shader (skinned)。
// (ツリーで書けない SDF は Mesh.SurfaceNets に手埋めの grid を渡す経路もある)

using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class Sdf19
{
    // 最長軸の grid cell 数。
    // bounds はツリーの AABB から自動で決まる。
    const int n = 64;

    // --- モデル: 雪だるま風 ------------------------------------------------- bone(name, pivot) を付けた部位には skinning 重みが焼かれる。
    // 動かす腕は mirror だと pivot が片側になるので左右を個別に置く(目は動かないので mirror のまま)。
    static SdfNode Model()
    {
        var body = Sdf.Sphere(0.72f).Move(0, -0.42f, 0)
            .Bone("body", new Vec3(0, -0.42f, 0));
        var head = Sdf.Sphere(0.46f).Move(0, 0.48f, 0)
            .Bone("head", new Vec3(0, 0.10f, 0));
        var armL = Sdf.Capsule(new Vec3(0.56f, -0.32f, 0),
            new Vec3(1.04f, 0.24f, 0), 0.13f)
            .Bone("arm_l", new Vec3(0.56f, -0.32f, 0));
        var armR = Sdf.Capsule(new Vec3(-0.56f, -0.32f, 0),
            new Vec3(-1.04f, 0.24f, 0), 0.13f)
            .Bone("arm_r", new Vec3(-0.56f, -0.32f, 0));
        // 目: 球で smooth にくり抜き (camera は -Z 側)。
        // 切断面には cutter の材質が出るので、目玉の色は「彫る球の paint」で決まる
        var eye = Sdf.Sphere(0.11f).Move(0.17f, 0.56f, -0.40f).MirrorX()
            .Paint(0x1E2130, 0.0f, 0.15f);
        return body.Smin(head, 0.22f).Smin(armL, 0.10f).Smin(armR, 0.10f)
            .Paint(0xE58B52).Ssub(eye, 0.06f);
    }

    // --- アニメーション -------------------------------------------------------
    static bool waveOn = true;

    // mesh.bones の順で関節の行列を詰める (規約は Lubx.Bones)
    static List<float> PackBones(float t, MeshData? data)
    {
        float wave = waveOn ? (float)Math.Sin(t * 4.0f) * 0.5f : 0.0f;
        float nod = waveOn ? (float)Math.Sin(t * 2.0f) * 0.10f : 0.0f;
        return Bones.Pack(data, (name, x, y, z) =>
        {
            switch (name)
            {
                case "arm_l":
                    return Bones.PivotRot(x, y, z, Mat4.RotateZ(-wave));
                case "arm_r":
                    return Bones.PivotRot(x, y, z, Mat4.RotateZ(wave));
                case "head":
                    return Bones.PivotRot(x, y, z, Mat4.RotateZ(-nod));
                default:
                    return null;
            }
        });
    }

    // --- メッシュ化 ---------------------------------------------------------- mesh / ren は onInit で生成 (cs-lib クラスは static 初期化子で作らない)。
    static Mesh3d? mesh = null;
    static Renderer3d? ren = null;
    static float tAccum = 0.0f;
    // treeDirty = コードからツリーを再構築 (reload 時)、meshDirty = 再評価 (SdfPanel での編集時)。
    // パネル編集はツリー (data) に直接乗るので、リロードするまで生きる。
    // reload は static を保持するため、差分を当てた後に呼ばれる OnReload() で明示的に立てる。
    static SdfNode? tree = null;
    static bool treeDirty = true;
    static bool meshDirty = true;

    public static void OnReload()
    {
        treeDirty = true;
    }

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        App.Config(new ConfigOpts { Backend = backend });
        mesh = new Mesh3d("sdf19");
        var r = new Renderer3d("sdf19");
        r.Background = Color.Rgb(0.09f, 0.09f, 0.12f);
        ren = r;
    }

    static void Remesh(Mesh3d m, SdfNode t)
    {
        m.Rebuild(Sdf.Mesh(t, n));
        meshDirty = false;
    }

    public static void OnFrame(float dt)
    {
        tAccum = tAccum + dt * 0.96f;

        var m = mesh;
        var r = ren;
        if (m == null || r == null)
            return;

        if (treeDirty)
        {
            tree = Model(); // コードが source of truth。reload でパネル編集は破棄
            treeDirty = false;
            meshDirty = true;
        }
        var t = tree;
        if (t == null)
            return;

        // debug UI: ツリー (data) から自動生成したパネル。
        // いじったフレームだけ remesh (C 評価 ~10ms なのでドラッグ追従)。
        // golden capture 中は描画しない (imgui テキストの AA が WARP で ±1 揺れて byte 比較が非決定になるため。
        // 3D シーン本体は決定的)。
        // LUB_GOLDEN は scripts/run-golden.sh がセット。
        if (Environment.GetEnvironmentVariable("LUB_GOLDEN") == null)
        {
            Ui.SetNextWindow(10, 10, 300, 460);
            if (Ui.BeginWindow("sdf tuning"))
            {
                if (SdfPanel.Draw(t))
                    meshDirty = true;
                Ui.Separator();
                waveOn = Ui.Checkbox("wave", waveOn);
                var md = m.Data;
                if (m.Ready() && md != null)
                    Ui.Text("verts: " + md.VertCount);
            }
            Ui.EndWindow();
        }

        if (meshDirty)
            Remesh(m, t);

        var model = Mat4.RotateY(-tAccum * 0.7f);
        r.Begin(new Camera
        {
            Eye = new Vec3(0.0f, 0.55f, -3.1f),
            Target = new Vec3(0, 0.05f, 0),
            Fov = 45.0f,
            Near = 0.1f,
            Far = 100.0f,
        });
        r.Draw(m, model, new Draw3dOpts { Bones = PackBones(tAccum, m.Data) });
        r.End();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        Ui.Render();
        Gfx.EndPass();
    }
}
