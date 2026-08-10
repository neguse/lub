// lub の samples/19_sdf (Haxe 版 Sdf19.hx) の TinyC# 版 entry。
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

public static class Sdf19
{
    // 最長軸の grid cell 数。bounds はツリーの AABB から自動で決まる。
    const int N = 64;

    // --- モデル: 雪だるま風 -------------------------------------------------
    // bone(name, pivot) を付けた部位には skinning 重みが焼かれる。動かす腕は
    // mirror だと pivot が片側になるので左右を個別に置く(目は動かないので
    // mirror のまま)。
    static SdfNode Model()
    {
        var body = Sdf.sphere(0.72f).move(0, -0.42f, 0)
            .bone("body", new Vec3(0, -0.42f, 0));
        var head = Sdf.sphere(0.46f).move(0, 0.48f, 0)
            .bone("head", new Vec3(0, 0.10f, 0));
        var armL = Sdf.capsule(new Vec3(0.56f, -0.32f, 0),
            new Vec3(1.04f, 0.24f, 0), 0.13f)
            .bone("arm_l", new Vec3(0.56f, -0.32f, 0));
        var armR = Sdf.capsule(new Vec3(-0.56f, -0.32f, 0),
            new Vec3(-1.04f, 0.24f, 0), 0.13f)
            .bone("arm_r", new Vec3(-0.56f, -0.32f, 0));
        // 目: 球で smooth にくり抜き (camera は -Z 側)。切断面には cutter の
        // 材質が出るので、目玉の色は「彫る球の paint」で決まる
        var eye = Sdf.sphere(0.11f).move(0.17f, 0.56f, -0.40f).mirrorX()
            .paint(0x1E2130, 0.0f, 0.15f);
        return body.smin(head, 0.22f).smin(armL, 0.10f).smin(armR, 0.10f)
            .paint(0xE58B52).ssub(eye, 0.06f);
    }

    // --- アニメーション -------------------------------------------------------
    static bool waveOn = true;

    // mesh.bones の順で 8 本分の行列を詰める (規約は lubx.Bones)
    static List<float> PackBones(float t, MeshData? data)
    {
        float wave = waveOn ? (float)Math.Sin(t * 4.0f) * 0.5f : 0.0f;
        float nod = waveOn ? (float)Math.Sin(t * 2.0f) * 0.10f : 0.0f;
        return Bones.pack(data, (name, x, y, z) =>
        {
            switch (name)
            {
                case "arm_l":
                    return Bones.pivotRot(x, y, z, Mat4.rotateZ(wave));
                case "arm_r":
                    return Bones.pivotRot(x, y, z, Mat4.rotateZ(-wave));
                case "head":
                    return Bones.pivotRot(x, y, z, Mat4.rotateZ(nod));
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

    public static void onReload()
    {
        treeDirty = true;
    }

    static List<int>? matcapPx = null;
    static TextureRef? matcapTex = null;
    static bool matcapDirty = true;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend });
        mesh = new Mesh3d("sdf19");
        var r = new Renderer3d("sdf19");
        r.background = Color.rgb(0.09f, 0.09f, 0.12f);
        ren = r;
    }

    static void Remesh(Mesh3d m, SdfNode t)
    {
        m.rebuild(Sdf.mesh(t, N));
        matcapPx = MakeMatcap(64); // reload と同じタイミングで作り直す
        matcapDirty = true;
        meshDirty = false;
    }

    static int MatcapByte(float v)
    {
        return (int)Math.Floor(MathUtil.clamp(v, 0.0f, 1.0f) * 255.0f);
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
                float horizon = MathUtil.smoothstep(-0.08f, 0.12f, ny);
                float zen = Math.Max(0.0f, ny);
                float r = MathUtil.lerp(0.10f, 0.60f, horizon) + zen * 0.26f;
                float g = MathUtil.lerp(0.09f, 0.70f, horizon) + zen * 0.20f;
                float bl = MathUtil.lerp(0.11f, 0.86f, horizon) + zen * 0.13f;
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

    public static void onFrame(float dt)
    {
        tAccum = tAccum + dt * 0.96f;
        if (Input.key_pressed("space"))
            metalTarget = 1.0f - metalTarget;
        var metalBlend = 1.0f - (float)Math.Pow(1.0f - 0.12f, dt * 60.0f);
        metalT = metalT + (metalTarget - metalT) * metalBlend;

        Io.load_text("samples/19_sdf/data/19_sdf.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/19_sdf/data/19_sdf.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null)
            return;
        var s = Gfx.use_shader("sdf_sh", vs, fs, vsv * 31 + fsv);

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
        if (os.getenv("LUB_GOLDEN") == null)
        {
            Ui.ui_set_next_window(10, 10, 300, 460);
            if (Ui.ui_begin("sdf tuning"))
            {
                if (SdfPanel.draw(t))
                    meshDirty = true;
                Ui.ui_separator();
                metalTarget = Ui.ui_slider_float("metal (Space)", metalTarget,
                    0.0f, 1.0f);
                waveOn = Ui.ui_checkbox("wave", waveOn);
                var md = m.data;
                if (m.ready() && md != null)
                    Ui.ui_text("verts: " + md.vert_count);
            }
            Ui.ui_end();
        }

        if (meshDirty)
            Remesh(m, t);
        // dirty なら変更宣言 (version 省略)、そうでなければ ref.version で再主張。
        matcapTex = Gfx.use_texture("sdf_matcap", 64, 64, Gfx.RGBA8, matcapPx,
            (matcapDirty || matcapTex == null) ? null : (int?)matcapTex.version);
        matcapDirty = false;
        var matcap = matcapTex;

        var model = Mat4.rotateY(tAccum * 0.7f);
        r.begin(new Camera
        {
            eye = new Vec3(0.0f, 0.55f, -3.1f),
            target = new Vec3(0, 0.05f, 0),
            fov = 45.0f,
            near = 0.1f,
            far = 100.0f,
        });
        // material は matcap shader に差し替え (ファイル編集で hot reload)。
        // 追加の view / params / matcap は opts 経由で渡す。
        var vm = r.viewMat;
        if (vm != null)
        {
            var opts = new Draw3dOpts
            {
                uniforms = new Dictionary<string, object>
                {
                    ["view"] = vm.m,
                    // メタル変身の override。ジオメトリにも頂点にも触らない
                    ["params"] = new List<float> { metalT, 0.0f, 0.0f, 0.0f },
                },
                bones = PackBones(tAccum, m.data),
            };
            // shader / matcap が null なら Haxe 版同様、組み込み lit shader に
            // フォールバックする (フィールド未設定 = Lua 側でキー無し)
            if (s != null)
                opts.shader = s;
            if (matcap != null)
                opts.textures = new Dictionary<string, TextureRef>
                { ["matcap"] = matcap };
            r.draw(m, model, opts);
        }
        r.End();

        Gfx.begin_pass(new PassOpts { target = Gfx.main_tex, load = Gfx.LOAD });
        Ui.ui_render();
        Gfx.end_pass();
    }
}
