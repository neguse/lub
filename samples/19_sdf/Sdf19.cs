// lub の samples/19_sdf の entry。
// 実行: lub samples/19_sdf/Sdf19.csproj (transpile + watch + hot reload)
//
// SDF モデリング: lubx.Sdf の builder でツリーを組み、C 側 (sdf_mesh) が
// 評価 → surface nets でメッシュ化する。Model() を書き換えて保存すれば
// hot reload で再メッシュされる。法線と材質 (paint) は SDF から頂点に
// 焼くので、UV は存在しない。
// Space キーでメタル変身 (材質の runtime override + matcap)。ジオメトリと
// 焼いた頂点材質は無傷のまま、uniform だけで見た目が変わる。
// (ツリーで書けない SDF は Mesh.surface_nets に手埋めの grid を渡す経路もある)

using System;
using System.Collections.Generic;
using static Lub;

public static class Sdf19
{
    // 最長軸の grid cell 数。bounds はツリーの AABB から自動で決まる。
    const int n = 64;

    // --- モデル: 雪だるま風 -------------------------------------------------
    // bone(name, pivot) を付けた部位には skinning 重みが焼かれる。動かす腕は
    // mirror だと pivot が片側になるので左右を個別に置く(目は動かないので
    // mirror のまま)。
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
        // 目: 球で smooth にくり抜き (camera は -Z 側)。切断面には cutter の
        // 材質が出るので、目玉の色は「彫る球の paint」で決まる
        var eye = Sdf.Sphere(0.11f).Move(0.17f, 0.56f, -0.40f).MirrorX()
            .Paint(0x1E2130, 0.0f, 0.15f);
        return body.Smin(head, 0.22f).Smin(armL, 0.10f).Smin(armR, 0.10f)
            .Paint(0xE58B52).Ssub(eye, 0.06f);
    }

    // --- アニメーション -------------------------------------------------------
    static bool waveOn = true;

    // mesh.bones の順で 8 本分の行列を詰める (規約は lubx.Bones)
    static List<float> PackBones(float t, MeshData? data)
    {
        float wave = waveOn ? (float)Math.Sin(t * 4.0f) * 0.5f : 0.0f;
        float nod = waveOn ? (float)Math.Sin(t * 2.0f) * 0.10f : 0.0f;
        return Bones.Pack(data, (name, x, y, z) =>
        {
            switch (name)
            {
                case "arm_l":
                    return Bones.PivotRot(x, y, z, Mat4.RotateZ(wave));
                case "arm_r":
                    return Bones.PivotRot(x, y, z, Mat4.RotateZ(-wave));
                case "head":
                    return Bones.PivotRot(x, y, z, Mat4.RotateZ(nod));
                default:
                    return null;
            }
        });
    }

    // --- メッシュ化 ----------------------------------------------------------
    // mesh / ren は onInit で生成 (cs-lib クラスは static 初期化子で作らない)。
    static Mesh3d? mesh = null;
    static Renderer3d? ren = null;
    static float tAccum = 0.0f;
    // メタル変身 (0..1)。target を Space でトグルして毎フレーム補間
    static float metalT = 0.0f;
    static float metalTarget = 0.0f;
    // treeDirty = コードからツリーを再構築 (reload 時)、meshDirty = 再評価
    // (SdfPanel での編集時)。パネル編集はツリー (data) に直接乗るので、
    // リロードするまで生きる。
    // reload 検知は経路で異なる: native watch (lume.hotswap) は chunk 再実行で
    // フラグが初期値 true へ戻る。web playground (module mode) は static を
    // 保持するため、hot apply 後に呼ばれる onReload() で明示的に立てる。
    static SdfNode? tree = null;
    static bool treeDirty = true;
    static bool meshDirty = true;

    public static void OnReload()
    {
        treeDirty = true;
    }

    static List<int>? matcapPx = null;
    static TextureRef? matcapTex = null;
    static bool matcapDirty = true;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend });
        mesh = new Mesh3d("sdf19");
        var r = new Renderer3d("sdf19");
        r.Background = Color.Rgb(0.09f, 0.09f, 0.12f);
        ren = r;
    }

    static void Remesh(Mesh3d m, SdfNode t)
    {
        m.Rebuild(Sdf.Mesh(t, n));
        matcapPx = MakeMatcap(64); // reload と同じタイミングで作り直す
        matcapDirty = true;
        meshDirty = false;
    }

    static int MatcapByte(float v)
    {
        return (int)Math.Floor(MathUtil.Clamp(v, 0.0f, 1.0f) * 255.0f);
    }

    // メタルの映り込み用 matcap (sphere map) を手続き生成する。
    // 空→地面の縦グラデ + キーライトのハイライト + 地面の照り返し。
    // アセットファイル不要で、いじって保存すれば hot reload で反映される。
    static List<int> MakeMatcap(int size)
    {
        var px = new List<int>();
        float lx = -0.45f; // key light (正規化済み)
        float ly = 0.65f;
        float lz = 0.61f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) / size * 2.0f - 1.0f;
                float ny = 1.0f - (y + 0.5f) / size * 2.0f; // 画像上方向 = +y
                float d2 = nx * nx + ny * ny;
                if (d2 > 1.0f)
                { // 円の外周は縁の値で延長 (LINEAR filter の黒縁防止)
                    float d = (float)Math.Sqrt(d2);
                    nx = nx / d;
                    ny = ny / d;
                    d2 = 1.0f;
                }
                float nz = (float)Math.Sqrt(1.0f - d2);
                // chrome 風: 地平線でパキッと分かれる空/地面 + キーライト。
                // 金属の説得力はコントラストで決まる
                float horizon = MathUtil.Smoothstep(-0.08f, 0.12f, ny);
                float zen = Math.Max(0.0f, ny);
                float r = MathUtil.Lerp(0.10f, 0.60f, horizon) + zen * 0.26f;
                float g = MathUtil.Lerp(0.09f, 0.70f, horizon) + zen * 0.20f;
                float bl = MathUtil.Lerp(0.11f, 0.86f, horizon) + zen * 0.13f;
                float ndl = Math.Max(0.0f, nx * lx + ny * ly + nz * lz);
                float spec = (float)Math.Pow(ndl, 48.0f) * 1.2f;
                px.Add(MatcapByte(r + spec));
                px.Add(MatcapByte(g + spec));
                px.Add(MatcapByte(bl + spec));
                px.Add(255);
            }
        }
        return px;
    }

    public static void OnFrame(float dt)
    {
        tAccum = tAccum + dt * 0.96f;
        if (Input.KeyPressed("space"))
            metalTarget = 1.0f - metalTarget;
        var metalBlend = 1.0f - (float)Math.Pow(1.0f - 0.12f, dt * 60.0f);
        metalT = metalT + (metalTarget - metalT) * metalBlend;

        Io.LoadText("samples/19_sdf/data/19_sdf.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/19_sdf/data/19_sdf.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null)
            return;
        var s = Gfx.UseShader("sdf_sh", vs, fs, vsv * 31 + fsv);

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

        // debug UI: ツリー (data) から自動生成したパネル。いじったフレームだけ
        // remesh (C 評価 ~10ms なのでドラッグ追従)。golden capture 中は描画しない
        // (imgui テキストの AA が WARP で ±1 揺れて byte 比較が非決定になるため。
        // 3D シーン本体は決定的)。LUB_GOLDEN は scripts/run-golden.sh がセット。
        if (Environment.GetEnvironmentVariable("LUB_GOLDEN") == null)
        {
            Ui.SetNextWindow(10, 10, 300, 460);
            if (Ui.BeginWindow("sdf tuning"))
            {
                if (SdfPanel.Draw(t))
                    meshDirty = true;
                Ui.Separator();
                metalTarget = Ui.SliderFloat("metal (Space)", metalTarget,
                    0.0f, 1.0f);
                waveOn = Ui.Checkbox("wave", waveOn);
                var md = m.Data;
                if (m.Ready() && md != null)
                    Ui.Text("verts: " + md.VertCount);
            }
            Ui.EndWindow();
        }

        if (meshDirty)
            Remesh(m, t);
        // dirty なら変更宣言 (version 省略)、そうでなければ ref.version で再主張。
        matcapTex = Gfx.UseTexture("sdf_matcap", 64, 64, Gfx.PixelFormat.Rgba8, matcapPx,
            (matcapDirty || matcapTex == null) ? null : (int?)matcapTex.Version);
        matcapDirty = false;
        var matcap = matcapTex;

        var model = Mat4.RotateY(tAccum * 0.7f);
        r.Begin(new Camera
        {
            Eye = new Vec3(0.0f, 0.55f, -3.1f),
            Target = new Vec3(0, 0.05f, 0),
            Fov = 45.0f,
            Near = 0.1f,
            Far = 100.0f,
        });
        // material は matcap shader に差し替え (ファイル編集で hot reload)。
        // 追加の view / params / matcap は opts 経由で渡す。
        var vm = r.ViewMat;
        if (vm != null)
        {
            var opts = new Draw3dOpts
            {
                Uniforms = new Dictionary<string, object>
                {
                    ["view"] = vm.M,
                    // メタル変身の override。ジオメトリにも頂点にも触らない
                    ["params"] = new List<float> { metalT, 0.0f, 0.0f, 0.0f },
                },
                Bones = PackBones(tAccum, m.Data),
            };
            // shader / matcap が null なら組み込み lit shader に
            // フォールバックする (フィールド未設定 = Lua 側でキー無し)
            if (s != null)
                opts.Shader = s;
            if (matcap != null)
                opts.Textures = new Dictionary<string, TextureRef>
                { ["matcap"] = matcap };
            r.Draw(m, model, opts);
        }
        r.End();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        Ui.Render();
        Gfx.EndPass();
    }
}
