// lub の samples/24_baseball の entry。
// 実行: lub samples/24_baseball/Baseball24.csproj (transpile + watch + hot reload)
//
// 全自動野球シミュレーション。ユーザーは観るだけ。
// - キャラ: SDF モデリング (bone 付き) + vertex shader LBS。1 体のメッシュを
//   ボーン行列とチーム色 uniform で全員に使い回す
// - 物理: 手書き弾道 (重力 + 空気抵抗 + バウンド + フェンス反射)。
//   球 vs 平面/円筒の解析判定なのですり抜けしない
// - 試合: state machine で投球→打撃→守備→走塁を全自動進行。捕球・封殺は
//   野手と走者の実際の位置と時間で決まる (結果の先取りをしない)
// - 演出: バット接触ヒットストップ + 画面振動、状況別自動カメラ
//
// boneSlot 表 + 手詰めの packBones は cs-lib の Bones.pack (mesh.bones 順の
// resolve callback) に置き換え、乱数は Math.random でなく決定的な Rand。
// cs-lib のクラスは生成 Lua でサンプルより後に定義されるため static 初期化子
// では参照できず、reloaded ブロックで遅延生成する (Iroha21 と同じ扱い)。

using System;
using System.Collections.Generic;
using static Lub;

// 走者。塁 index は 0=本塁(打席) 1..3=各塁 4=生還
public class Runner
{
    public float X;
    public float Z;
    public int AtBase; // 今いる/直前の塁 (base は C# 予約語のため AtBase)
    public int To; // 目標の塁
    public float RunPhase = 0;

    public Runner(float x, float z, int atBase, int to)
    {
        this.X = x;
        this.Z = z;
        this.AtBase = atBase;
        this.To = to;
    }
}

// 野手。home* は定位置、(x,z) が現在地
public class Fielder
{
    public float X;
    public float Z;
    public float HomeX;
    public float HomeZ;
    public float Yaw = 0;
    public float RunPhase = 0;
    public int Anim = 0; // Baseball24.AN_*
    public float AnimT = 0;

    public Fielder(float hx, float hz)
    {
        HomeX = hx;
        HomeZ = hz;
        X = hx;
        Z = hz;
    }
}

// predictLanding の結果
public class Landing
{
    public float X;
    public float Z;
    public float T;
    public float Peak;
}

public static class Baseball24
{
    const int w = 960;
    const int h = 540;
    const float tickDt = 1.0f / 60.0f;

    // --- フィールド寸法 (m) -------------------------------------------------
    const float baseD = 19.4f; // 塁間 27.43m の対角成分
    const float moundZ = 18.44f;
    const float fenceR = 76.0f;
    const float fenceH = 3.0f;
    const float grav = 9.8f;
    const float ballR = 0.115f;
    const float dragCoef = 0.0055f; // 打球の空気抵抗 (終端速度 ~42m/s)
    const float runSpd = 7.2f; // 走者/野手の走速
    const float catchR = 1.15f; // 捕球半径 (グラブの届く範囲)

    // --- 試合 state ---------------------------------------------------------
    const int stIntro = 0;
    const int stPrepitch = 1;
    const int stWindup = 2;
    const int stPitch = 3;
    const int stLive = 4;
    const int stCall = 5;
    const int stChange = 6;
    const int stEnd = 7;

    // キャラアニメ
    public const int AnIdle = 0;
    public const int AnReady = 1;
    public const int AnRun = 2;
    public const int AnWindup = 3;
    public const int AnSwing = 4;
    public const int AnReach = 5;
    public const int AnCrouch = 6;
    public const int AnThrow = 7;

    // 打球フェーズ (ST_LIVE 中)
    const int plFly = 0; // 打球が空中 (ノーバウンド)
    const int plThrow1b = 2; // 一塁送球中
    const int plSettle = 3; // 判定確定、走者が到達するのを待つ
    const int plFoul = 4;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend, Width = w, Height = h });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    // --- 乱数 (決定的 xorshift。Math.random は run ごとに列が変わる) ---------
    static Rand? rng = null;

    static float Rnd()
    {
        var r = rng;
        if (r == null)
        {
            r = new Rand(0x0B5EBA11);
            rng = r;
        }
        return r.NextFloat();
    }

    static float Rrange(float a, float b)
    {
        return a + (b - a) * Rnd();
    }

    // --- キャラメッシュ (SDF + bones) ---------------------------------------
    // 身長 ~1.8m。ユニフォームをチーム色で焼いた 2 メッシュを使い分ける
    static List<Mesh3d>? charMesh = null;
    static int[] teamRgb = new int[] { 0xD94038, 0x4073E0 };

    const float torsoPx = 0.0f;
    const float torsoPy = 0.95f;
    const float headPy = 1.50f;
    const float armPx = 0.24f;
    const float armPy = 1.40f;
    const float legPx = 0.10f;
    const float legPy = 0.92f;

    static SdfNode CharModel(int jersey)
    {
        var skin = 0xEFB68A;
        var ink = 0x172C43;
        var torso = Sdf.Capsule(new Vec3(0, 0.98f, 0), new Vec3(0, 1.35f, 0), 0.23f)
            .Paint(jersey, 0, 0.7f)
            .Union(Sdf.Box(0.027f, 0.22f, 0.012f).Move(0, 1.20f, 0.226f).Paint(0xFFF1D7))
            .Union(Sdf.Box(0.23f, 0.045f, 0.18f).Move(0, 0.98f, 0).Paint(ink))
            .Bone("torso", new Vec3(torsoPx, torsoPy, 0));
        var head = Sdf.Sphere(0.225f).Move(0, 1.66f, 0).Paint(skin, 0, 0.65f)
            .Union(Sdf.Sphere(0.233f).Move(0, 1.73f, -0.018f)
                .Subtract(Sdf.Box(0.4f, 0.3f, 0.4f).Move(0, 1.38f, 0))
                .Paint(jersey, 0, 0.45f))
            .Union(Sdf.Box(0.21f, 0.028f, 0.14f).Move(0, 1.77f, 0.19f).Paint(jersey))
            .Union(Sdf.Sphere(0.030f).Move(0.084f, 1.66f, 0.202f).MirrorX().Paint(ink))
            .Union(Sdf.Sphere(0.047f).Move(0, 1.60f, 0.216f).Paint(skin))
            .Union(Sdf.Sphere(0.058f).Move(0.222f, 1.63f, 0).MirrorX().Paint(skin))
            .Bone("head", new Vec3(0, headPy, 0));
        var armL = Sdf.Capsule(new Vec3(0.25f, 1.37f, 0), new Vec3(0.32f, 1.05f, 0.04f), 0.085f)
            .Paint(skin)
            .Smin(Sdf.Sphere(0.11f).Move(0.32f, 1.01f, 0.045f).Paint(skin), 0.03f)
            .Union(Sdf.Capsule(new Vec3(0.24f, 1.38f, 0), new Vec3(0.28f, 1.25f, 0), 0.105f).Paint(jersey))
            .Bone("arm_l", new Vec3(armPx, armPy, 0));
        var armR = Sdf.Capsule(new Vec3(-0.25f, 1.37f, 0), new Vec3(-0.32f, 1.05f, 0.04f), 0.085f)
            .Paint(skin)
            .Smin(Sdf.Sphere(0.11f).Move(-0.32f, 1.01f, 0.045f).Paint(skin), 0.03f)
            .Union(Sdf.Capsule(new Vec3(-0.24f, 1.38f, 0), new Vec3(-0.28f, 1.25f, 0), 0.105f).Paint(jersey))
            .Bone("arm_r", new Vec3(-armPx, armPy, 0));
        var legL = Sdf.Capsule(new Vec3(0.12f, 0.90f, 0), new Vec3(0.14f, 0.30f, 0), 0.11f)
            .Paint(0xF5EDDA)
            .Union(Sdf.Capsule(new Vec3(0.14f, 0.30f, 0), new Vec3(0.14f, 0.13f, 0), 0.09f).Paint(jersey))
            .Smin(Sdf.Capsule(new Vec3(0.14f, 0.10f, -0.02f), new Vec3(0.14f, 0.10f, 0.13f), 0.10f).Paint(ink), 0.025f)
            .Bone("leg_l", new Vec3(legPx, legPy, 0));
        var legR = Sdf.Capsule(new Vec3(-0.12f, 0.90f, 0), new Vec3(-0.14f, 0.30f, 0), 0.11f)
            .Paint(0xF5EDDA)
            .Union(Sdf.Capsule(new Vec3(-0.14f, 0.30f, 0), new Vec3(-0.14f, 0.13f, 0), 0.09f).Paint(jersey))
            .Smin(Sdf.Capsule(new Vec3(-0.14f, 0.10f, -0.02f), new Vec3(-0.14f, 0.10f, 0.13f), 0.10f).Paint(ink), 0.025f)
            .Bone("leg_r", new Vec3(-legPx, legPy, 0));
        return torso.Smin(head, 0.025f).Smin(armL, 0.025f).Smin(armR, 0.025f)
            .Smin(legL, 0.025f).Smin(legR, 0.025f);
    }

    static void BuildCharMesh()
    {
        var cm = charMesh;
        if (cm == null)
        {
            cm = new List<Mesh3d>
            {
                new Mesh3d("bb24_char0"),
                new Mesh3d("bb24_char1"),
            };
            charMesh = cm;
        }
        for (int t = 0; t < 2; t++)
        {
            cm[t].Rebuild(Sdf.Mesh(CharModel(teamRgb[t]), 72));
        }
    }

    // --- ポーズ → ボーン行列 ------------------------------------------------
    // torso が親、head/arms が子、legs は独立。回転はすべて pivot 回り。
    // bone 行列の詰めは Bones.pack (mesh.bones 順の resolve) に
    // 任せる。pose: [twist, lean, tilt, toy, hx, hy, alx, alz, arx, arz,
    // llx, lrx]
    static List<float> PackBones(List<float> p)
    {
        var rTorso = Mat4.RotateY(-p[0]) * (Mat4.RotateX(-p[1]) * Mat4.RotateZ(-p[2]));
        var mTorso = Mat4.Translate(new Vec3(0, p[3], 0))
            * Bones.PivotRot(torsoPx, torsoPy, 0, rTorso);
        var mHead = mTorso
            * Bones.PivotRot(0, headPy, 0, Mat4.RotateY(-p[5]) * Mat4.RotateX(-p[4]));
        var mArmL = mTorso
            * Bones.PivotRot(armPx, armPy, 0, Mat4.RotateZ(-p[7]) * Mat4.RotateX(-p[6]));
        var mArmR = mTorso
            * Bones.PivotRot(-armPx, armPy, 0, Mat4.RotateZ(-p[9]) * Mat4.RotateX(-p[8]));
        var legScale = Mat4.Scale(new Vec3(1, 1 + Math.Min(p[3], 0) / legPy, 1));
        var mLegL = legScale * Bones.PivotRot(legPx, legPy, 0, Mat4.RotateX(-p[10]));
        var mLegR = legScale * Bones.PivotRot(-legPx, legPy, 0, Mat4.RotateX(-p[11]));
        var mats = new Dictionary<string, Mat4>
        {
            ["torso"] = mTorso,
            ["head"] = mHead,
            ["arm_l"] = mArmL,
            ["arm_r"] = mArmR,
            ["leg_l"] = mLegL,
            ["leg_r"] = mLegR,
        };
        var cm = charMesh;
        var mesh = cm != null ? cm[0].Data : null;
        return Bones.Pack(mesh, (name, px, py, pz) =>
            mats.ContainsKey(name) ? mats[name] : null);
    }

    static List<float> ZeroPose()
    {
        return new List<float> { 0, 0, 0, 0, 0, 0, 0, -0.08f, 0, 0.08f, 0, 0 };
    }

    // クリップ。桜井メソッド: 構え/攻撃ポーズは極端に、中割りはほぼゼロ
    static List<float> PoseIdle(float t)
    {
        var p = ZeroPose();
        var b = (float)Math.Sin(t * 2.1f);
        p[1] = 0.04f + b * 0.015f;
        p[7] = 0.10f + b * 0.02f;
        p[9] = -0.10f - b * 0.02f;
        return p;
    }

    static List<float> PoseReady(float t)
    {
        var p = ZeroPose();
        p[1] = 0.42f; // 前傾
        p[3] = -0.08f;
        p[4] = -0.35f; // 顔は上げる
        p[6] = 0.85f;
        p[8] = 0.85f; // 両腕前
        p[7] = 0.35f;
        p[9] = -0.35f;
        p[10] = 0.25f;
        p[11] = -0.25f;
        return p;
    }

    static List<float> PoseCrouch(float t)
    {
        var p = ZeroPose();
        p[3] = -0.30f;
        p[1] = 0.38f;
        p[4] = -0.55f;
        p[6] = 1.05f;
        p[8] = 1.05f; // 両腕前
        p[10] = 0.18f;
        p[11] = -0.18f;
        return p;
    }

    static List<float> PoseRun(float ph)
    {
        var p = ZeroPose();
        var s = (float)Math.Sin(ph);
        p[1] = 0.30f;
        p[10] = s * 1.0f;
        p[11] = -s * 1.0f;
        p[6] = -s * 0.9f;
        p[8] = s * 0.9f;
        p[3] = Math.Abs((float)Math.Cos(ph)) * 0.075f;
        return p;
    }

    // 投球。ph 0..1、リリースは REL_PH
    public const float RelPh = 0.60f;

    static List<float> PoseWindup(float ph)
    {
        var p = ZeroPose();
        // 1) 振りかぶり + 足上げ (前 = +Z = rotateX 正)
        var k1 = MathUtil.Smoothstep(0.0f, 0.34f, ph);
        p[6] = -2.1f * k1;
        p[8] = -2.1f * k1;
        p[10] = 1.55f * k1;
        p[1] = -0.28f * k1;
        // 2) 踏み込み + 腕を極端に引き絞る
        var k2 = MathUtil.Smoothstep(0.38f, 0.54f, ph);
        p[10] = MathUtil.Lerp(p[10], 0.55f, k2);
        p[8] = MathUtil.Lerp(p[8], -2.95f, k2); // 右腕を頭の後ろまで
        p[6] = MathUtil.Lerp(p[6], -0.4f, k2);
        p[1] = MathUtil.Lerp(p[1], -0.38f, k2);
        // 3) リリース: 一気に振り抜く (中割りなし)
        var k3 = MathUtil.Smoothstep(0.56f, 0.62f, ph);
        p[8] = MathUtil.Lerp(p[8], 0.9f, k3);
        p[1] = MathUtil.Lerp(p[1], 0.62f, k3);
        p[0] = -0.60f * k3;
        p[10] = MathUtil.Lerp(p[10], 0.35f, k3);
        p[11] = -0.3f * k3;
        // 4) フォロースルーの余韻
        var k4 = MathUtil.Smoothstep(0.66f, 1.0f, ph);
        p[8] = MathUtil.Lerp(p[8], 0.5f, k4);
        p[1] = MathUtil.Lerp(p[1], 0.45f, k4);
        return p;
    }

    // スイング。ph 0..1、ミートは SWING_HIT_PH
    public const float SwingHitPh = 0.52f;

    static List<float> PoseSwing(float ph)
    {
        var p = ZeroPose();
        p[5] = 0.9f; // 顔は投手へ
        p[4] = -0.15f;
        // 1) 溜め: 捕手側へ捻る
        var k1 = MathUtil.Smoothstep(0.0f, 0.40f, ph);
        p[0] = -0.55f * k1;
        p[6] = -1.5f * k1;
        p[8] = -1.7f * k1;
        p[7] = 0.9f * k1;
        p[9] = -0.4f * k1;
        p[10] = -0.35f * k1;
        // 2) 爆発: 1-2 フレームで振り抜く
        var k2 = MathUtil.Smoothstep(0.47f, 0.54f, ph);
        p[0] = MathUtil.Lerp(p[0], 1.55f, k2);
        p[6] = MathUtil.Lerp(p[6], 0.6f, k2);
        p[8] = MathUtil.Lerp(p[8], 0.6f, k2);
        p[7] = MathUtil.Lerp(p[7], 0.3f, k2);
        p[9] = MathUtil.Lerp(p[9], -1.1f, k2);
        p[1] = 0.12f * k2;
        p[10] = MathUtil.Lerp(p[10], 0.4f, k2);
        p[11] = -0.5f * k2;
        // 3) フォロースルー: ウェイト破綻気味に大きく
        var k3 = MathUtil.Smoothstep(0.6f, 1.0f, ph);
        p[0] = MathUtil.Lerp(p[0], 1.85f, k3);
        p[4] = MathUtil.Lerp(p[4], -0.3f, k3);
        return p;
    }

    static List<float> PoseReach(float t)
    {
        var p = ZeroPose();
        p[6] = 2.9f;
        p[8] = 2.9f;
        p[7] = 0.25f;
        p[9] = -0.25f;
        p[4] = -0.8f;
        return p;
    }

    static List<float> PoseThrow(float ph)
    {
        var p = ZeroPose();
        var k1 = MathUtil.Smoothstep(0.0f, 0.4f, ph);
        p[8] = -2.6f * k1;
        p[0] = -0.4f * k1;
        var k2 = MathUtil.Smoothstep(0.45f, 0.58f, ph);
        p[8] = MathUtil.Lerp(p[8], 0.8f, k2);
        p[0] = MathUtil.Lerp(p[0], 0.35f, k2);
        p[1] = 0.35f * k2;
        return p;
    }

    static List<float> PoseFor(int anim, float t, float runPhase)
    {
        if (anim == AnReady) return PoseReady(t);
        if (anim == AnRun) return PoseRun(runPhase);
        if (anim == AnWindup) return PoseWindup(t);
        if (anim == AnSwing) return PoseSwing(t);
        if (anim == AnReach) return PoseReach(t);
        if (anim == AnCrouch) return PoseCrouch(t);
        if (anim == AnThrow) return PoseThrow(t);
        return PoseIdle(t);
    }

    // --- 静的メッシュ (Shapes) ----------------------------------------------
    static Mesh3d? fieldMesh = null;
    static Mesh3d? ballMesh = null;
    static Mesh3d? batMesh = null;
    static Mesh3d? gloveMesh = null;

    static void Fan(List<float> dst, float cx, float cy, float cz,
        float r, float a0, float a1, int segs, List<float> col)
    {
        for (int i = 0; i < segs; i++)
        {
            var t0 = a0 + (a1 - a0) * i / segs;
            var t1 = a0 + (a1 - a0) * (i + 1) / segs;
            Shapes.Tri(dst, new List<float> { cx, cy, cz },
                new List<float>
                    { cx + (float)Math.Sin(t0) * r, cy, cz + (float)Math.Cos(t0) * r },
                new List<float>
                    { cx + (float)Math.Sin(t1) * r, cy, cz + (float)Math.Cos(t1) * r },
                new List<float> { 0, 1, 0 }, col);
        }
    }

    static void BuildField()
    {
        var fm = fieldMesh ?? new Mesh3d("bb24_field");
        fieldMesh = fm;
        var bm = ballMesh ?? new Mesh3d("bb24_ball");
        ballMesh = bm;
        var btm = batMesh ?? new Mesh3d("bb24_bat");
        batMesh = btm;

        var v = new List<float>();
        var grass = new List<float> { 0.19f, 0.37f, 0.29f, 1.0f };
        var grassIn = new List<float> { 0.31f, 0.55f, 0.34f, 1.0f };
        var dirt = new List<float> { 0.73f, 0.49f, 0.32f, 1.0f };
        var lineW = new List<float> { 0.95f, 0.95f, 0.92f, 1.0f };
        var wall = new List<float> { 0.12f, 0.26f, 0.29f, 1.0f };
        var wallTop = new List<float> { 0.88f, 0.82f, 0.35f, 1.0f };
        var up = new List<float> { 0, 1, 0 };

        // 地面 (ファウルグラウンド込みの外周)
        Shapes.Quad(v, new List<float> { -95, 0, -20 },
            new List<float> { -95, 0, 95 }, new List<float> { 95, 0, 95 },
            new List<float> { 95, 0, -20 }, up, grass);
        // フェアグラウンドの扇形 (少し明るい緑)
        Fan(v, 0, 0.012f, 0, fenceR, -(float)Math.PI / 4, (float)Math.PI / 4, 24, grassIn);
        for (int band = 0; band < 12; band++)
        {
            var z0 = band * 6.0f;
            var z1 = z0 + 3.0f;
            var edge0 = Math.Min(z0, (float)Math.Sqrt(fenceR * fenceR - z0 * z0));
            var edge1 = Math.Min(z1, (float)Math.Sqrt(fenceR * fenceR - z1 * z1));
            Shapes.Quad(v, new List<float> { -edge0, 0.016f, z0 },
                new List<float> { -edge1, 0.016f, z1 }, new List<float> { edge1, 0.016f, z1 },
                new List<float> { edge0, 0.016f, z0 }, up,
                new List<float> { 0.27f, 0.48f, 0.30f, 1 });
        }
        // 内野ダート (ひし形)
        Shapes.Quad(v, new List<float> { 0, 0.024f, -2.2f },
            new List<float> { 24.5f, 0.024f, baseD },
            new List<float> { 0, 0.024f, 43.0f },
            new List<float> { -24.5f, 0.024f, baseD }, up, dirt);
        // 内野の芝 (ダートの内側)
        Shapes.Quad(v, new List<float> { 0, 0.036f, 4.2f },
            new List<float> { 15.5f, 0.036f, baseD },
            new List<float> { 0, 0.036f, 34.6f },
            new List<float> { -15.5f, 0.036f, baseD }, up, grassIn);
        // マウンド (つぶれたドーム + ダート円)
        Fan(v, 0, 0.048f, moundZ, 2.9f, -(float)Math.PI, (float)Math.PI, 16, dirt);
        Shapes.Sphere(v, 0, -2.35f, moundZ, 2.6f, dirt, 8, 16);
        // 本塁と各塁
        Shapes.Box(v, 0, 0.03f, 0, 0.55f, 0.05f, 0.55f, lineW);
        Shapes.Box(v, baseD, 0.07f, baseD, 0.55f, 0.13f, 0.55f, lineW);
        Shapes.Box(v, 0, 0.07f, baseD * 2, 0.55f, 0.13f, 0.55f, lineW);
        Shapes.Box(v, -baseD, 0.07f, baseD, 0.55f, 0.13f, 0.55f, lineW);
        // プレート (マウンド上)
        Shapes.Box(v, 0, 0.30f, moundZ, 0.61f, 0.05f, 0.15f, lineW);
        // ファウルライン
        var d = 0.70710678f;
        foreach (var s in new List<float> { -1.0f, 1.0f })
        {
            var nx = -s * d; // ライン直交方向
            var nz = d;
            var half = 0.09f;
            var x0 = s * 1.2f * d;
            var z0 = 1.2f * d;
            var x1 = s * (fenceR - 0.6f) * d;
            var z1 = (fenceR - 0.6f) * d;
            Shapes.Quad(v,
                new List<float> { x0 - nx * half, 0.045f, z0 - nz * half },
                new List<float> { x1 - nx * half, 0.045f, z1 - nz * half },
                new List<float> { x1 + nx * half, 0.045f, z1 + nz * half },
                new List<float> { x0 + nx * half, 0.045f, z0 + nz * half },
                up, lineW);
        }
        // 外野フェンス (内向きの壁 + 黄色いトップ)
        int segs = 26;
        for (int i = 0; i < segs; i++)
        {
            var a0 = -(float)Math.PI / 4 + (float)Math.PI / 2 * i / segs;
            var a1 = -(float)Math.PI / 4 + (float)Math.PI / 2 * (i + 1) / segs;
            var x0 = (float)Math.Sin(a0) * fenceR;
            var z0 = (float)Math.Cos(a0) * fenceR;
            var x1 = (float)Math.Sin(a1) * fenceR;
            var z1 = (float)Math.Cos(a1) * fenceR;
            var am = (a0 + a1) * 0.5f;
            var n = new List<float> { -(float)Math.Sin(am), 0, -(float)Math.Cos(am) };
            Shapes.Quad(v, new List<float> { x0, 0, z0 },
                new List<float> { x0, fenceH, z0 },
                new List<float> { x1, fenceH, z1 },
                new List<float> { x1, 0, z1 }, n, wall);
            Shapes.Quad(v, new List<float> { x0, fenceH, z0 },
                new List<float> { x0, fenceH + 0.18f, z0 },
                new List<float> { x1, fenceH + 0.18f, z1 },
                new List<float> { x1, fenceH, z1 }, n, wallTop);
        }
        // バックストップ (本塁後方の低い壁)
        int bsegs = 10;
        var bsCol = new List<float> { 0.48f, 0.51f, 0.55f, 1.0f };
        for (int i = 0; i < bsegs; i++)
        {
            var a0 = (float)Math.PI * 0.75f + (float)Math.PI * 0.5f * i / bsegs;
            var a1 = (float)Math.PI * 0.75f + (float)Math.PI * 0.5f * (i + 1) / bsegs;
            var r = 11.5f;
            var x0 = (float)Math.Sin(a0) * r;
            var z0 = (float)Math.Cos(a0) * r;
            var x1 = (float)Math.Sin(a1) * r;
            var z1 = (float)Math.Cos(a1) * r;
            var am = (a0 + a1) * 0.5f;
            var n = new List<float> { -(float)Math.Sin(am), 0, -(float)Math.Cos(am) };
            Shapes.Quad(v, new List<float> { x0, 0, z0 },
                new List<float> { x0, 1.6f, z0 },
                new List<float> { x1, 1.6f, z1 },
                new List<float> { x1, 0, z1 }, n, bsCol);
        }
        var seat = new List<float> { 0.16f, 0.30f, 0.36f, 1 };
        var concrete = new List<float> { 0.60f, 0.64f, 0.60f, 1 };
        for (int side = -1; side <= 1; side += 2)
        {
            for (int row = 0; row < 7; row++)
            {
                float z = -14 - row * 1.5f;
                float y = 1.6f + row * 0.8f;
                Shapes.Box(v, side * 11, y - 0.5f, z, 19, 0.6f, 1.5f, concrete);
                for (int j = 0; j < 22; j++)
                {
                    float x = side * 11 - 8.8f + j * 0.84f;
                    Shapes.Box(v, x, y, z, 0.64f, 0.7f, 0.25f, seat);
                    var crowd = (j * 7 + row * 3) % 5;
                    var shirt = crowd == 0 ? new List<float> { 0.84f, 0.40f, 0.28f, 1 }
                        : crowd == 1 ? lineW : crowd == 2 ? wallTop : seat;
                    Shapes.Box(v, x, y + 0.16f, z + 0.36f, 0.43f, 0.58f, 0.30f, shirt);
                    Shapes.Sphere(v, x, y + 0.64f, z + 0.36f, 0.19f,
                        new List<float> { 0.80f, 0.62f, 0.46f, 1 }, 4, 6);
                }
            }
            Shapes.Box(v, side * 11, 9.5f, -23, 20, 0.22f, 8, wall);
            for (int j = 0; j < 3; j++)
                Shapes.Box(v, side * 11 - 9 + j * 9, 4.75f, -25, 0.22f, 9.5f, 0.22f, seat);
            Shapes.Box(v, side * 34, 10, -8, 0.36f, 20, 0.36f, concrete);
            Shapes.Box(v, side * 34, 20, -8, 6, 2, 0.6f, seat);
            for (int j = 0; j < 6; j++)
                Shapes.Box(v, side * 34 - 2.5f + j, 20, -7.66f, 0.65f, 1.4f, 0.05f, lineW);
        }
        for (int side = -1; side <= 1; side += 2)
        {
            Shapes.Box(v, side * 1.20f, 0.055f, 0, 0.045f, 0.02f, 2.2f, lineW);
            Shapes.Box(v, side * 2.45f, 0.055f, 0, 0.045f, 0.02f, 2.2f, lineW);
            Shapes.Box(v, side * 1.82f, 0.055f, -1.1f, 1.3f, 0.02f, 0.045f, lineW);
            Shapes.Box(v, side * 1.82f, 0.055f, 1.1f, 1.3f, 0.02f, 0.045f, lineW);
        }
        fm.Rebuild(Shapes3d.FromInterleaved(v));

        var ballVerts = new List<float>();
        Shapes.Sphere(ballVerts, 0, 0, 0, ballR,
            new List<float> { 0.96f, 0.96f, 0.94f, 1.0f }, 8, 12);
        bm.Rebuild(Shapes3d.FromInterleaved(ballVerts));

        btm.Rebuild(Sdf.Mesh(Sdf.Capsule(new Vec3(0, 0, 0.08f), new Vec3(0, 0, 0.45f), 0.035f)
            .Paint(0x283648)
            .Smin(Sdf.Capsule(new Vec3(0, 0, 0.40f), new Vec3(0, 0, 0.94f), 0.075f)
                .Paint(0xDDA75F, 0, 0.4f), 0.06f), 40));
        var gm = gloveMesh ?? new Mesh3d("bb24_glove");
        gloveMesh = gm;
        gm.Rebuild(Sdf.Mesh(Sdf.Sphere(0.17f)
            .Subtract(Sdf.Sphere(0.14f).Move(0, 0, 0.11f))
            .Smin(Sdf.Capsule(new Vec3(-0.12f, -0.08f, 0), new Vec3(-0.15f, 0.10f, 0.04f), 0.06f), 0.04f)
            .Paint(0xAD683B, 0, 0.75f), 32));
    }

    // --- ボール ---------------------------------------------------------------
    static float bx = 0.0f;
    static float by = 0.0f;
    static float bz = 0.0f;
    static float bvx = 0.0f;
    static float bvy = 0.0f;
    static float bvz = 0.0f;
    static bool ballVisible = false;
    static int ballBounces = 0;
    static bool ballRolling = false;
    static bool isHomeRun = false;

    // 打球の 1 step (共通 integrator)。返り値: バウンドしたか
    static bool StepBall(float dt, bool drag)
    {
        if (drag)
        {
            var sp = (float)Math.Sqrt(bvx * bvx + bvy * bvy + bvz * bvz);
            var f = 1.0f / (1.0f + dragCoef * sp * dt);
            bvx *= f;
            bvy *= f;
            bvz *= f;
        }
        bvy -= grav * dt;
        bx += bvx * dt;
        by += bvy * dt;
        bz += bvz * dt;
        var bounced = false;
        // 地面
        if (by < ballR && bvy < 0)
        {
            by = ballR;
            if (Math.Abs(bvy) < 1.0f)
            {
                ballRolling = true;
                bvy = 0;
            }
            else
            {
                bvy = -bvy * 0.42f;
                bounced = true;
                ballBounces++;
            }
            bvx *= 0.72f;
            bvz *= 0.72f;
        }
        if (ballRolling)
        {
            by = ballR;
            bvy = 0;
            var sp = (float)Math.Sqrt(bvx * bvx + bvz * bvz);
            if (sp > 0)
            {
                var dec = Math.Max(0.0f, sp - 2.6f * dt);
                bvx *= dec / sp;
                bvz *= dec / sp;
            }
        }
        // フェンス (フェア扇形内の円筒壁)。越えたら本塁打
        var hr = (float)Math.Sqrt(bx * bx + bz * bz);
        if (bz > 0 && Math.Abs(bx) < bz + 2 && hr > fenceR - ballR)
        {
            if (by > fenceH)
            {
                if (!isHomeRun && ballBounces == 0)
                    isHomeRun = true;
            }
            else
            {
                // 半径方向の反射
                var nx = bx / hr;
                var nz = bz / hr;
                var vr = bvx * nx + bvz * nz;
                if (vr > 0)
                {
                    bvx -= 1.4f * vr * nx;
                    bvz -= 1.4f * vr * nz;
                    bx = nx * (fenceR - ballR);
                    bz = nz * (fenceR - ballR);
                    ballBounces++;
                }
            }
        }
        return bounced;
    }

    // 着地予測 (状態を退避してシミュレート)
    static Landing PredictLanding()
    {
        var sx = bx;
        var sy = by;
        var sz = bz;
        var svx = bvx;
        var svy = bvy;
        var svz = bvz;
        var sb = ballBounces;
        var sr = ballRolling;
        var shr = isHomeRun;
        var t = 0.0f;
        var peak = by;
        while (t < 12.0f)
        {
            StepBall(tickDt, true);
            t += tickDt;
            if (by > peak)
                peak = by;
            if (ballBounces > sb || ballRolling)
                break;
        }
        var r = new Landing { X = bx, Z = bz, T = t, Peak = peak };
        bx = sx;
        by = sy;
        bz = sz;
        bvx = svx;
        bvy = svy;
        bvz = svz;
        ballBounces = sb;
        ballRolling = sr;
        isHomeRun = shr;
        return r;
    }

    // --- チームと選手 -----------------------------------------------------------
    static string[] teamName = new string[] { "RED", "BLUE" };

    // 9人 (0=P 1=C 2=1B 3=2B 4=3B 5=SS 6=LF 7=CF 8=RF)
    static List<Fielder>? fielders = null;
    static Fielder? batter = null;
    static List<Runner>? runners = null;

    static List<List<float>> FielderHomes()
    {
        return new List<List<float>>
        {
            new List<float> { 0.0f, moundZ }, // P
            new List<float> { 0.0f, -2.4f }, // C
            new List<float> { 21.0f, 18.5f }, // 1B
            new List<float> { 11.0f, 31.0f }, // 2B
            new List<float> { -21.0f, 18.5f }, // 3B
            new List<float> { -11.0f, 31.0f }, // SS
            new List<float> { -27.0f, 56.0f }, // LF
            new List<float> { 0.0f, 63.0f }, // CF
            new List<float> { 27.0f, 56.0f }, // RF
        };
    }

    static List<float> BasePos(int i)
    {
        if (i == 1) return new List<float> { baseD, baseD };
        if (i == 2) return new List<float> { 0.0f, baseD * 2 };
        if (i == 3) return new List<float> { -baseD, baseD };
        return new List<float> { 0.0f, 0.0f }; // 0 と 4 は本塁
    }

    static void ResetActors()
    {
        var fs = new List<Fielder>();
        foreach (var h in FielderHomes())
            fs.Add(new Fielder(h[0], h[1]));
        fielders = fs;
        batter = new Fielder(-0.85f, 0.0f);
        runners = new List<Runner>();
    }

    // --- 試合状態 ---------------------------------------------------------------
    static int state = stIntro;
    static float stateT = 0.0f;
    static int inning = 1;
    static int half = 0; // 0=表 (RED 攻撃) 1=裏
    static List<int> score = new List<int> { 0, 0 };
    static int balls = 0;
    static int strikes = 0;
    static int outs = 0;

    static int BattingTeam()
    {
        return half == 0 ? 0 : 1;
    }

    static int FieldingTeam()
    {
        return half == 0 ? 1 : 0;
    }

    // 投球ごとの判定材料
    static float pitchTX = 0.0f; // 到達点 (x, y)
    static float pitchTY = 0.0f;
    static bool pitchInZone = false;
    static bool willSwing = false;
    static int swingOutcome = 0; // 0=空振り 1=ファウル 2=インプレー
    static float exitSpeed = 0.0f;
    static float exitLaunch = 0.0f;
    static float exitSpray = 0.0f;
    static bool swingStarted = false;

    // ST_LIVE の進行
    static int playPhase = plFly;
    static int chaser = -1;
    static int ballHeldBy = -1; // 野手 index (-1 = フリー)
    static float liveT = 0.0f;
    static float throwT = 0.0f;
    static float throwDur = 0.0f;
    static float throwFromX = 0.0f;
    static float throwFromY = 0.0f;
    static float throwFromZ = 0.0f;
    static Runner? batterRunner = null;
    static Landing? landing = null;

    // 演出
    static float hitstopT = 0.0f;
    static float shakeAmp = 0.0f;
    static string eventText = "";
    static float eventT = 99.0f;
    static Color? eventCol = null;
    static float tAccum = 0.0f;

    static void ShowEvent(string s, Color? c)
    {
        eventText = s;
        eventT = 0.0f;
        eventCol = c ?? Color.Rgb(1.0f, 0.98f, 0.9f);
    }

    static void SetState(int s)
    {
        state = s;
        stateT = 0.0f;
    }

    // --- 投球開始 -----------------------------------------------------------------
    static void StartPitch()
    {
        var fs = fielders;
        if (fs == null)
            return;
        // 目標: ゾーン内/外を先に決めてから座標を出す
        pitchInZone = Rnd() < 0.62f;
        if (pitchInZone)
        {
            pitchTX = Rrange(-0.20f, 0.20f);
            pitchTY = Rrange(0.60f, 1.10f);
        }
        else
        {
            // ゾーンの少し外
            if (Rnd() < 0.5f)
            {
                pitchTX = (Rnd() < 0.5f ? -1.0f : 1.0f) * Rrange(0.28f, 0.45f);
                pitchTY = Rrange(0.45f, 1.25f);
            }
            else
            {
                pitchTX = Rrange(-0.35f, 0.35f);
                pitchTY = Rnd() < 0.5f ? Rrange(0.15f, 0.42f) : Rrange(1.28f, 1.55f);
            }
        }
        willSwing = Rnd() < (pitchInZone ? 0.80f : 0.26f);
        if (willSwing)
        {
            var r = Rnd();
            if (r < 0.24f)
                swingOutcome = 0;
            else if (r < 0.58f)
                swingOutcome = 1;
            else
            {
                swingOutcome = 2;
                exitSpeed = 23.0f + 23.0f * (float)Math.Pow(Rnd(), 0.7f);
                exitLaunch = Rrange(-6.0f, 42.0f);
                exitSpray = Rrange(-38.0f, 38.0f);
            }
        }
        swingStarted = false;
        SetState(stWindup);
        fs[0].Anim = AnWindup;
        fs[0].AnimT = 0;
    }

    // リリース: ボールに初速を与える (重力補償で目標へ届ける)
    static void ReleaseBall()
    {
        bx = 0.35f;
        by = 1.9f;
        bz = moundZ - 0.55f;
        var speed = Rrange(31.0f, 40.0f);
        var dz = 0.42f - bz;
        var t = Math.Abs(dz) / speed;
        bvx = (pitchTX - bx) / t;
        bvy = (pitchTY - by) / t + 0.5f * grav * t;
        bvz = dz / t;
        ballVisible = true;
        ballBounces = 0;
        ballRolling = false;
        isHomeRun = false;
        SetState(stPitch);
    }

    // --- 打撃結果の解決 -------------------------------------------------------------
    static void ResolveContact()
    {
        var fs = fielders;
        var b = batter;
        var rns = runners;
        if (fs == null || b == null || rns == null)
            return;
        if (!willSwing || swingOutcome == 0)
        {
            // 見送り or 空振り → カウント
            if (willSwing)
            {
                strikes++;
                ShowEvent("SWING & MISS", null);
            }
            else if (pitchInZone)
            {
                strikes++;
                ShowEvent("STRIKE", null);
            }
            else
            {
                balls++;
                ShowEvent("BALL", null);
            }
            fs[1].Anim = AnReach;
            fs[1].AnimT = 0;
            ballVisible = false;
            AfterCall();
            return;
        }
        // バットに当たった。ヒットストップ + 画面振動
        hitstopT = 0.09f;
        shakeAmp = 0.5f;
        var launch = exitLaunch;
        var spray = exitSpray;
        var speed = exitSpeed;
        if (swingOutcome == 1)
        {
            // ファウル: 打球はラインの外か後方へ
            speed = Rrange(16.0f, 34.0f);
            launch = Rrange(15.0f, 70.0f);
            spray = (Rnd() < 0.5f ? -1.0f : 1.0f) * Rrange(50.0f, 130.0f);
        }
        var la = MathUtil.Radians(launch);
        var sa = MathUtil.Radians(spray);
        bx = 0.0f;
        by = 1.0f;
        bz = 0.35f;
        bvx = speed * (float)Math.Cos(la) * (float)Math.Sin(sa);
        bvy = speed * (float)Math.Sin(la);
        bvz = speed * (float)Math.Cos(la) * (float)Math.Cos(sa);
        ballBounces = 0;
        ballRolling = false;
        isHomeRun = false;
        var land = PredictLanding();
        landing = land;
        liveT = 0.0f;
        ballHeldBy = -1;
        if (swingOutcome == 1)
        {
            playPhase = plFoul;
            SetState(stLive);
            return;
        }
        // 打者走者スタート
        var br = new Runner(b.X, b.Z, 0, 1);
        batterRunner = br;
        rns.Add(br);
        b.Anim = AnSwing; // 走り出しはスイングの続きから
        // 最寄りの野手が追う
        chaser = NearestFielder(land.X, land.Z);
        playPhase = plFly;
        camCut = true;
        SetState(stLive);
    }

    static int NearestFielder(float x, float z)
    {
        var fs = fielders;
        if (fs == null)
            return -1;
        var best = -1;
        var bd = 1e9f;
        // 投手と捕手は追走から除外 (定位置が近すぎて何でも取ってしまう)
        for (int i = 2; i < 9; i++)
        {
            var f = fs[i];
            var d = (f.X - x) * (f.X - x) + (f.Z - z) * (f.Z - z);
            if (d < bd)
            {
                bd = d;
                best = i;
            }
        }
        return best;
    }

    // 打席の結果が確定 (カウント系)。四球/三振/次打者を処理
    static void AfterCall()
    {
        SetState(stCall);
        var b = batter;
        var rns = runners;
        if (b == null || rns == null)
            return;
        if (strikes >= 3)
        {
            ShowEvent("STRIKE OUT!", Color.Rgb(1.0f, 0.5f, 0.3f));
            outs++;
            newBatterPending = true;
        }
        else if (balls >= 4)
        {
            ShowEvent("WALK", Color.Rgb(0.5f, 0.9f, 1.0f));
            // 押し出し: 1塁から連続で埋まっている走者だけ 1 つ進む
            var occ = new Dictionary<int, Runner>();
            foreach (var r in rns)
                occ[r.To] = r;
            int free = 1;
            while (occ.ContainsKey(free))
                free++;
            for (int bs = 1; bs < free; bs++)
                occ[bs].To = bs + 1;
            rns.Add(new Runner(b.X, b.Z, 0, 1));
            newBatterPending = true;
        }
    }

    static bool newBatterPending = false;

    // --- 野手 AI (ST_LIVE) ------------------------------------------------------------
    static bool MoveTowards(Fielder f, float tx, float tz, float dt,
        float spd)
    {
        var dx = tx - f.X;
        var dz = tz - f.Z;
        var d = (float)Math.Sqrt(dx * dx + dz * dz);
        if (d < 0.15f)
        {
            if (f.Anim == AnRun)
                f.Anim = AnReady;
            return true;
        }
        var mv = Math.Min(d, spd * dt);
        f.X += dx / d * mv;
        f.Z += dz / d * mv;
        f.Yaw = (float)Math.Atan2(dx, dz);
        f.Anim = AnRun;
        f.RunPhase += dt * 11.0f;
        return false;
    }

    static void UpdateLive(float dt)
    {
        var fs = fielders;
        var rns = runners;
        if (fs == null || rns == null)
            return;
        liveT += dt;
        StepBall(dt, true);

        if (playPhase == plFoul)
        {
            if (liveT > 1.25f)
            {
                if (strikes < 2)
                    strikes++;
                ShowEvent("FOUL", null);
                ballVisible = false;
                var brf = batterRunner;
                if (brf != null)
                    rns.Remove(brf);
                batterRunner = null;
                AfterCall();
            }
            return;
        }

        if (isHomeRun && playPhase == plFly)
        {
            ShowEvent("HOME RUN!", Color.Rgb(1.0f, 0.85f, 0.25f));
            shakeAmp = 0.35f;
            foreach (var r in rns)
                r.To = 4;
            playPhase = plSettle;
        }

        // 走者更新
        UpdateRunners(dt, 1.0f);

        // 野手: 追走者は打球へ、一塁手はベースカバー、他は定位置へ
        for (int i = 0; i < 9; i++)
        {
            var f = fs[i];
            if (state != stLive)
                break;
            if (i == chaser && ballHeldBy < 0 && playPhase != plSettle)
            {
                // 落下点 (フライ) or 転がるボールの少し先 (ゴロ)
                var land = landing;
                var flying = ballBounces == 0 && !ballRolling;
                var tx = flying && land != null ? land.X : bx + bvx * 0.35f;
                var tz = flying && land != null ? land.Z : bz + bvz * 0.35f;
                var arrived = MoveTowards(f, tx, tz, dt, runSpd);
                var dx = f.X - bx;
                var dz = f.Z - bz;
                var dist = (float)Math.Sqrt(dx * dx + dz * dz);
                if (flying && by < 2.6f && bvy < 0 && dist < catchR)
                {
                    // ノーバウンド捕球 → アウト
                    FielderCaught(i, true);
                }
                else if ((ballBounces > 0 || ballRolling)
                    && dist < catchR * 0.8f && by < 1.2f)
                {
                    FielderCaught(i, false);
                }
                else if (arrived && (ballBounces > 0 || ballRolling))
                {
                    f.Anim = AnReady;
                }
            }
            else if (i == 2 && playPhase != plSettle && batterRunner != null)
            {
                // 一塁手はベースへ (自分が追走者でなければ)
                if (i != chaser)
                    MoveTowards(f, baseD - 0.4f, baseD - 0.4f, dt, runSpd);
            }
            else if (i != chaser)
            {
                MoveTowards(f, f.HomeX, f.HomeZ, dt, runSpd * 0.8f);
            }
        }

        // 一塁送球の到達判定
        if (playPhase == plThrow1b)
        {
            throwT += dt;
            var k = Math.Min(1.0f, throwT / throwDur);
            // 送球は放物線 (見た目用に手計算)
            bx = MathUtil.Lerp(throwFromX, baseD, k);
            bz = MathUtil.Lerp(throwFromZ, baseD, k);
            by = MathUtil.Lerp(throwFromY, 1.2f, k) + (float)Math.Sin(k * (float)Math.PI) * 1.4f;
            if (k >= 1.0f)
            {
                // 封殺 or セーフ: 走者の進塁具合と競争
                var br = batterRunner;
                if (br != null && br.AtBase < 1)
                {
                    outs++;
                    ShowEvent("OUT!", Color.Rgb(1.0f, 0.5f, 0.3f));
                    rns.Remove(br);
                    // 他の走者は 1 つ進む
                    foreach (var r in rns)
                        if (r.To < 3)
                            r.To++;
                }
                else
                {
                    ShowEvent("SAFE!", Color.Rgb(0.5f, 1.0f, 0.6f));
                }
                batterRunner = null;
                ballHeldBy = 2;
                ballVisible = false;
                playPhase = plSettle;
            }
        }

        // 決着: 走者が全員目標に着いたら打席交代
        if (playPhase == plSettle)
        {
            var settled = true;
            foreach (var r in rns)
                if (r.AtBase < r.To)
                    settled = false;
            if (settled && liveT > 1.0f)
            {
                ballVisible = false;
                newBatterPending = true;
                SetState(stCall);
            }
        }
        // 保険: 異常に長引いたら打ち切り
        if (liveT > 14.0f)
        {
            ballVisible = false;
            newBatterPending = true;
            SetState(stCall);
        }
    }

    // 捕球した。fly=ノーバウンド (アウト)
    static void FielderCaught(int i, bool fly)
    {
        var fs = fielders;
        var rns = runners;
        if (fs == null || rns == null)
            return;
        ballHeldBy = i;
        var f = fs[i];
        f.Anim = fly ? AnReach : AnReady;
        f.AnimT = 0;
        if (fly)
        {
            outs++;
            ShowEvent("CAUGHT!", Color.Rgb(1.0f, 0.6f, 0.3f));
            // 打者アウト。走者は帰塁 (簡略: その場から戻る)
            var br = batterRunner;
            if (br != null)
                rns.Remove(br);
            batterRunner = null;
            foreach (var r in rns)
                r.To = r.AtBase;
            ballVisible = false;
            playPhase = plSettle;
            return;
        }
        // ゴロ/落ちたフライ: 一塁封殺が間に合いそうなら送球、無理ならヒット確定
        var gatherDist = (float)Math.Sqrt(f.X * f.X + f.Z * f.Z);
        var brg = batterRunner;
        if (brg != null && brg.AtBase < 1 && gatherDist < 34)
        {
            f.Anim = AnThrow;
            f.AnimT = 0;
            playPhase = plThrow1b;
            throwFromX = bx;
            throwFromY = Math.Max(by, 1.3f);
            throwFromZ = bz;
            var d = (float)Math.Sqrt((baseD - bx) * (baseD - bx)
                + (baseD - bz) * (baseD - bz));
            throwDur = Math.Max(0.25f, d / 30.0f);
            throwT = 0.0f;
            return;
        }
        // ヒット: 深さと経過時間で進塁数を決める
        var bases = 1;
        if (gatherDist > 62 || liveT > 4.6f)
            bases = 2;
        if (gatherDist > 72 && liveT > 5.5f)
            bases = 3;
        ShowEvent(bases == 1 ? "HIT!" : bases == 2 ? "DOUBLE!" : "TRIPLE!",
            Color.Rgb(0.55f, 1.0f, 0.6f));
        foreach (var r in rns)
            r.To = r == batterRunner ? bases : Math.Min(4, r.AtBase + bases);
        batterRunner = null;
        ballVisible = false;
        playPhase = plSettle;
    }

    static void UpdateRunners(float dt, float spdScale)
    {
        var rns = runners;
        if (rns == null)
            return;
        int i = rns.Count - 1;
        while (i >= 0)
        {
            var r = rns[i];
            if (r.AtBase < r.To)
            {
                int nextBase = r.AtBase + 1;
                var np = BasePos(nextBase == 4 ? 0 : nextBase);
                var dx = np[0] - r.X;
                var dz = np[1] - r.Z;
                var d = (float)Math.Sqrt(dx * dx + dz * dz);
                var mv = runSpd * spdScale * dt;
                if (d <= mv)
                {
                    r.X = np[0];
                    r.Z = np[1];
                    r.AtBase = nextBase;
                    if (nextBase >= 4)
                    {
                        score[BattingTeam()] = score[BattingTeam()] + 1;
                        ShowEvent("RUN SCORED!", Color.Rgb(1.0f, 0.9f, 0.4f));
                        rns.RemoveAt(i);
                    }
                }
                else
                {
                    r.X += dx / d * mv;
                    r.Z += dz / d * mv;
                }
                r.RunPhase += dt * 11.0f;
            }
            else if (r.AtBase > r.To)
            {
                // 帰塁
                var np = BasePos(r.To);
                var dx = np[0] - r.X;
                var dz = np[1] - r.Z;
                var d = (float)Math.Sqrt(dx * dx + dz * dz);
                var mv = runSpd * dt;
                if (d <= mv)
                {
                    r.X = np[0];
                    r.Z = np[1];
                    r.AtBase = r.To;
                }
                else
                {
                    r.X += dx / d * mv;
                    r.Z += dz / d * mv;
                }
                r.RunPhase += dt * 11.0f;
            }
            i--;
        }
    }

    // --- state machine 本体 ----------------------------------------------------------
    static void UpdateGame(float dt)
    {
        stateT += dt;
        eventT += dt;
        var fs = fielders;
        var rns = runners;
        var b = batter;
        if (fs == null || rns == null || b == null)
            return;
        if (state == stIntro)
        {
            if (stateT > 1.8f)
            {
                ShowEvent("PLAY BALL!", Color.Rgb(1.0f, 0.95f, 0.5f));
                SetState(stPrepitch);
            }
        }
        else if (state == stPrepitch)
        {
            // 全員が定位置に戻るのを待つ (テンポ優先で上限 1.4s)
            for (int i = 0; i < 9; i++)
            {
                var f = fs[i];
                if (i != 1)
                    MoveTowards(f, f.HomeX, f.HomeZ, dt, runSpd * 0.8f);
            }
            UpdateRunners(dt, 1.0f);
            if (newBatterPending)
            {
                b.X = -0.85f;
                b.Z = 0.0f;
                b.Anim = AnIdle;
                balls = 0;
                strikes = 0;
                newBatterPending = false;
            }
            // 走者が塁に着くまでは投げない (四球の押し出し等)。上限つき
            var settled = true;
            foreach (var r in rns)
                if (r.AtBase != r.To)
                    settled = false;
            if (stateT > 1.05f && (settled || stateT > 6.0f))
            {
                if (outs >= 3)
                {
                    SetState(stChange);
                    ShowEvent("CHANGE", Color.Rgb(0.9f, 0.9f, 0.95f));
                }
                else
                {
                    StartPitch();
                }
            }
        }
        else if (state == stWindup)
        {
            fs[0].AnimT += dt / 1.1f;
            if (fs[0].AnimT >= RelPh)
                ReleaseBall();
        }
        else if (state == stPitch)
        {
            fs[0].AnimT = Math.Min(1.0f, fs[0].AnimT + dt / 1.1f);
            // 投球は無抵抗の放物線 (短距離なので誤差は無視できる)
            bvy -= grav * dt;
            bx += bvx * dt;
            by += bvy * dt;
            bz += bvz * dt;
            // スイング開始タイミング (ミートの瞬間に SWING_HIT_PH が来るよう逆算)
            var tToPlate = bvz != 0 ? (0.42f - bz) / bvz : 0.0f;
            if (willSwing && !swingStarted && tToPlate < SwingHitPh * 0.55f)
            {
                b.Anim = AnSwing;
                b.AnimT = 0;
                swingStarted = true;
            }
            if (b.Anim == AnSwing)
                b.AnimT = Math.Min(1.0f, b.AnimT + dt / 0.55f);
            if (bz <= 0.42f)
                ResolveContact();
        }
        else if (state == stLive)
        {
            if (b.Anim == AnSwing)
            {
                b.AnimT = Math.Min(1.0f, b.AnimT + dt / 0.55f);
                if (b.AnimT >= 1.0f)
                    b.Anim = AnIdle;
            }
            foreach (var f in fs)
                if (f.Anim == AnThrow || f.Anim == AnReach)
                    f.AnimT = Math.Min(1.0f, f.AnimT + dt / 0.45f);
            UpdateLive(dt);
        }
        else if (state == stCall)
        {
            if (b.Anim == AnSwing)
            {
                b.AnimT = Math.Min(1.0f, b.AnimT + dt / 0.55f);
                if (b.AnimT >= 1.0f)
                    b.Anim = AnIdle;
            }
            UpdateRunners(dt, 1.0f);
            if (stateT > 0.95f)
                SetState(stPrepitch);
        }
        else if (state == stChange)
        {
            if (stateT > 1.8f)
            {
                runners = new List<Runner>();
                outs = 0;
                balls = 0;
                strikes = 0;
                if (half == 1)
                {
                    inning++;
                    half = 0;
                }
                else
                {
                    half = 1;
                }
                // 3回終了 (かつ同点でない) で試合終了。延長は 5 回まで
                var over = (inning > 3 && score[0] != score[1]) || inning > 5;
                if (over)
                {
                    SetState(stEnd);
                    if (score[0] == score[1])
                        ShowEvent("DRAW", Color.Rgb(0.9f, 0.9f, 0.95f));
                    else
                    {
                        var w = score[0] > score[1] ? 0 : 1;
                        ShowEvent("GAME SET  " + teamName[w] + " WINS!",
                            Color.Rgb(1.0f, 0.9f, 0.4f));
                    }
                }
                else
                {
                    ResetActors();
                    newBatterPending = true;
                    SetState(stPrepitch);
                }
            }
        }
        else if (state == stEnd)
        {
            if (stateT > 5.0f)
            {
                // 新しい試合を自動で始める
                inning = 1;
                half = 0;
                score = new List<int> { 0, 0 };
                outs = 0;
                balls = 0;
                strikes = 0;
                ResetActors();
                newBatterPending = true;
                ShowEvent("PLAY BALL!", Color.Rgb(1.0f, 0.95f, 0.5f));
                SetState(stPrepitch);
            }
        }
    }

    // --- カメラ ---------------------------------------------------------------------
    static Vec3? camEye = null;
    static Vec3? camTarget = null;
    static float camFov = 34.0f;
    static bool camCut = false;

    static void UpdateCamera(float dt)
    {
        var eye = camEye;
        var tgt = camTarget;
        if (eye == null || tgt == null)
            return;
        var de = new Vec3(4.8f, 3.3f, 28.5f); // センター後方の中継カメラ
        var dtg = new Vec3(-0.3f, 1.1f, 1.2f);
        var dfov = 29.0f;
        if (state == stLive && playPhase != plFoul)
        {
            var land = landing;
            if (land != null && (land.Peak > 7.0f || isHomeRun)
                && (ballBounces == 0 && !ballRolling || isHomeRun))
            {
                // フライ追従: 打球の後方上空から
                var hv = (float)Math.Sqrt(bvx * bvx + bvz * bvz);
                var dirx = hv > 0.5f ? bvx / hv : 0.0f;
                var dirz = hv > 0.5f ? bvz / hv : 1.0f;
                de = new Vec3(bx - dirx * 13.0f,
                    Math.Max(by * 0.55f + 3.5f, 2.2f), bz - dirz * 13.0f);
                dtg = new Vec3(bx + bvx * 0.22f, Math.Max(by, 0.5f),
                    bz + bvz * 0.22f);
                dfov = 42.0f;
            }
            else
            {
                // 内野俯瞰
                de = new Vec3(8, 19.0f, -13.0f);
                dtg = new Vec3(0, 0.0f, 20.0f);
                dfov = 50.0f;
            }
        }
        else if (state == stPrepitch && stateT < 0.65f)
        {
            de = new Vec3(5.2f, 2.5f, 5.0f);
            dtg = new Vec3(-0.6f, 1.1f, 0);
            dfov = 34.0f;
        }
        else if (state == stIntro || state == stChange || state == stEnd)
        {
            var a = tAccum * 0.12f;
            de = new Vec3((float)Math.Sin(a) * 46.0f, 17.0f, 24.0f + (float)Math.Cos(a) * 30.0f);
            dtg = new Vec3(0, 1.0f, 22.0f);
            dfov = 42.0f;
        }
        var k = camCut ? 1.0f : Math.Min(1.0f, 7.0f * dt);
        camCut = false;
        eye = eye.Lerp(de, k);
        tgt = tgt.Lerp(dtg, k);
        camFov = MathUtil.Lerp(camFov, dfov, k);
        // 画面振動 (ヒットの手応え)。減衰付きで eye だけ揺らす
        if (shakeAmp > 0.003f)
        {
            var s = shakeAmp;
            eye = new Vec3(eye.X + (float)Math.Sin(tAccum * 71.0f) * s * 0.25f,
                eye.Y + (float)Math.Sin(tAccum * 93.0f + 1.7f) * s * 0.2f, eye.Z);
            shakeAmp *= (float)Math.Pow(0.001f, dt); // ~0.7s で収束
        }
        camEye = eye;
        camTarget = tgt;
    }

    // --- 描画 -----------------------------------------------------------------------
    static bool reloaded = true; // hot reload で true に戻る (19_sdf と同じトリック)
    static FixedStep? step = null;

    static Renderer3d? ren = null;

    static void DrawChar(float x, float z, float yaw, int team,
        List<float> pose, bool glove = false)
    {
        var renNow = ren;
        var cm = charMesh;
        if (renNow == null || cm == null)
            return;
        var model = Mat4.Translate(new Vec3(x, 0, z)) * Mat4.RotateY(-yaw);
        renNow.Draw(cm[team], model, new Draw3dOpts { Bones = PackBones(pose) });
        if (glove && gloveMesh != null)
        {
            var torso = Mat4.Translate(new Vec3(0, pose[3], 0))
                * Bones.PivotRot(torsoPx, torsoPy, 0,
                    Mat4.RotateY(-pose[0]) * Mat4.RotateX(-pose[1]) * Mat4.RotateZ(-pose[2]));
            var arm = Bones.PivotRot(armPx, armPy, 0,
                Mat4.RotateZ(-pose[7]) * Mat4.RotateX(-pose[6]));
            renNow.Draw(gloveMesh, model * torso * arm * Mat4.Translate(new Vec3(0.32f, 1.01f, 0.12f)));
        }
    }

    // バット。スイング位相から向きを決める (打者ローカル)
    static Mat4 BatMatrix(float ph)
    {
        // 溜め → 一気に振り抜き → フォロー (角度は右ねじの逆向きで調整してある)
        var ang = -2.35f; // 構え: 後方上
        var tilt = 1.05f;
        var k2 = MathUtil.Smoothstep(0.47f, 0.56f, ph);
        ang = MathUtil.Lerp(ang, 1.15f, k2);
        tilt = MathUtil.Lerp(tilt, -0.05f, k2);
        var k3 = MathUtil.Smoothstep(0.6f, 1.0f, ph);
        ang = MathUtil.Lerp(ang, 1.9f, k3);
        tilt = MathUtil.Lerp(tilt, 0.45f, k3);
        var pose = PoseSwing(ph);
        var torso = Mat4.Translate(new Vec3(0, pose[3], 0))
            * Bones.PivotRot(torsoPx, torsoPy, 0,
                Mat4.RotateY(-pose[0]) * Mat4.RotateX(-pose[1]) * Mat4.RotateZ(-pose[2]));
        var arm = Bones.PivotRot(-armPx, armPy, 0,
            Mat4.RotateZ(-pose[9]) * Mat4.RotateX(-pose[8]));
        var grip = (torso * arm).MulPoint(new Vec3(-0.32f, 1.01f, 0.045f));
        // local は Lua キーワードで emit が不正になるため batLocal
        var batLocal = Mat4.Translate(grip)
            * (Mat4.RotateY(-ang) * Mat4.RotateX(-tilt));
        var b = batter;
        var px = b != null ? b.X : 0.0f;
        var pz = b != null ? b.Z : 0.0f;
        return Mat4.Translate(new Vec3(px, 0, pz))
            * (Mat4.RotateY(-(float)Math.PI / 2) * batLocal);
    }

    // --- HUD ------------------------------------------------------------------------
    const string fontPath = "samples/data/fonts/MPLUS1p-subset.ttf";
    static bool fontLoaded = false;
    static int fontVersion = 0;
    static Text? mtext = null;
    static SpriteBatch? hud = null;

    static bool EnsureText()
    {
        Io.LoadBytes(fontPath, out var bytes, out var version, out _, out _);
        if (bytes == null)
            return false;
        if (!fontLoaded || fontVersion != version)
        {
            fontLoaded = true;
            fontVersion = version;
            mtext = new Text("bb24_text", fontPath, 40, 1024);
            hud = new SpriteBatch(w, h, "bb24_hud", "bb24_hud");
        }
        return mtext != null;
    }

    static void HudText(string text, float x, float y, float size, Color color, bool center = false)
    {
        var mt = mtext;
        var batch = hud;
        if (mt == null || batch == null) return;
        float scale = size / mt.Px;
        mt.Draw(batch, text, center ? x - mt.Width(text, scale) * 0.5f : x, y, color, scale);
    }

    static void DrawHud()
    {
        if (!EnsureText()) return;
        var batch = hud;
        if (batch == null) return;
        var cream = Color.Hex(0xFFF3DB);
        var ink = Color.Rgb(0.04f, 0.10f, 0.15f, 0.95f);
        var gold = Color.Hex(0xF5C46B);
        batch.Begin();
        batch.Rect(24, 24, 274, 106, ink);
        batch.Rect(24, 24, 5, 50, Color.Hex(teamRgb[0]));
        batch.Rect(24, 77, 5, 50, Color.Hex(teamRgb[1]));
        batch.Rect(40, 75, 240, 1, Color.Rgb(0.3f, 0.4f, 0.45f));
        batch.Rect(298, 24, 124, 106, Color.Rgb(0.07f, 0.16f, 0.21f, 0.95f));
        for (int i = 0; i < 3; i++)
            batch.Disc(335 + i * 24, 107, 5, i < outs ? gold : Color.Hex(0x304653));
        var occupied = new bool[] { false, false, false };
        var rns = runners;
        if (rns != null)
            foreach (var runner in rns)
            {
                if (runner.AtBase >= 1 && runner.AtBase <= 3) occupied[runner.AtBase - 1] = true;
            }
        for (int i = 0; i < 3; i++)
        {
            float x = i == 0 ? 391 : i == 1 ? 378 : 365;
            float y = i == 1 ? 59 : 72;
            batch.Disc(x, y, 6, occupied[i] ? gold : Color.Hex(0x304653));
        }
        HudText(teamName[0], 44, 57, 23, cream);
        HudText(teamName[1], 44, 110, 23, cream);
        HudText("" + score[0], 258, 61, 35, cream, true);
        HudText("" + score[1], 258, 114, 35, cream, true);
        HudText((half == 0 ? "TOP " : "BOT ") + inning, 313, 47, 16, gold);
        HudText(balls + " - " + strikes, 313, 78, 20, cream);
        HudText("LUB  /  BASEBALL", w - 118, 42, 17, ink, true);
        batch.Flush();
        if (eventText != "" && eventT < 1.6f && eventCol != null)
        {
            float a = MathUtil.Clamp((1.6f - eventT) / 0.3f, 0, 1);
            float slide = (float)Math.Exp(-eventT * 15) * 32;
            float size = Math.Min(39, 790 / Math.Max(1, mtext!.Width(eventText, 1.0f)) * 40);
            batch.Begin();
            batch.Rect(0, 408 + slide, w, 88, Color.Rgb(0.04f, 0.10f, 0.15f, 0.93f * a));
            batch.Rect(0, 408 + slide, w, 3, Color.Rgb(eventCol.R, eventCol.G, eventCol.B, a));
            HudText(eventText, w * 0.5f, 464 + slide, size,
                Color.Rgb(cream.R, cream.G, cream.B, a), true);
            batch.Flush();
        }
    }

    static void SimulateTick()
    {
        tAccum += tickDt;
        // ヒットストップ: その間シミュレーションだけ止める
        if (hitstopT > 0)
        {
            hitstopT -= tickDt;
        }
        else
        {
            UpdateGame(tickDt);
        }
        UpdateCamera(tickDt);

        var fs = fielders;
        if (fs == null) return;
        // 捕手は基本しゃがみ。捕球リアクションだけ一瞬立つ
        if (fs[1].Anim == AnReach)
        {
            fs[1].AnimT += tickDt;
            if (fs[1].AnimT > 0.5f)
                fs[1].Anim = AnCrouch;
        }
        else
        {
            fs[1].Anim = AnCrouch;
        }
    }

    // --- main loop --------------------------------------------------------------------
    public static void OnFrame(float dt)
    {
        if (reloaded)
        {
            rng = new Rand(0x0B5EBA11);
            ren = new Renderer3d("bb24");
            camEye = new Vec3(5.5f, 3.4f, 30.0f);
            camTarget = new Vec3(0, 1.3f, 0);
            camFov = 34.0f;
            BuildCharMesh();
            BuildField();
            ResetActors();
            state = stIntro;
            stateT = 0;
            reloaded = false;
            ShowEvent("PLAY BALL!", Color.Rgb(1.0f, 0.95f, 0.5f));
        }

        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => SimulateTick());

        var fs = fielders;
        var renNow = ren;
        var eyeNow = camEye;
        var tgtNow = camTarget;
        if (fs == null || renNow == null || eyeNow == null || tgtNow == null)
            return;

        var t = tAccum;

        // --- 描画 ---
        // 屋外デーゲーム: 高い太陽 + 空色の環境光
        renNow.Light.Dir = new Vec3(-0.65f, 1.0f, 0.45f);
        renNow.Light.Intensity = 2.2f;
        renNow.Light.Color = Color.Rgb(1.0f, 0.88f, 0.70f);
        renNow.Sky.Top = Color.Rgb(0.43f, 0.61f, 0.85f);
        renNow.Sky.Bottom = Color.Rgb(0.22f, 0.28f, 0.20f);
        renNow.Sky.Intensity = 0.7f;
        renNow.Background = Color.Rgb(0.57f, 0.73f, 0.83f);
        renNow.Ssao.Radius = 0.35f;
        renNow.Ssao.Strength = 0.6f;
        renNow.Bloom.Strength = 0.12f;
        renNow.Vignette = 0.12f;
        // 影はカメラターゲット周辺 (フィールド全体 100m は 1 枚に入れない)
        renNow.Shadow.Center = new Vec3(tgtNow.X, 0, tgtNow.Z);
        renNow.Shadow.Extent = 30.0f;
        renNow.Begin(new Camera
        {
            Eye = eyeNow,
            Target = tgtNow,
            Fov = camFov,
            Near = 0.1f,
            Far = 400.0f,
        });

        renNow.Draw(fieldMesh, new Mat4());

        // 野手 (守備側チーム色)
        var ft = FieldingTeam();
        for (int i = 0; i < 9; i++)
        {
            var f = fs[i];
            var pose = PoseFor(f.Anim,
                f.Anim == AnWindup || f.Anim == AnThrow || f.Anim == AnReach
                    ? f.AnimT
                    : t,
                f.RunPhase);
            var yaw = f.Anim == AnRun
                ? f.Yaw
                : (float)Math.Atan2(0 - f.X, 0 - f.Z); // 待機中は本塁を向く
            if (i == 0)
                yaw = (float)Math.PI; // 投手は打者へ正対
            if (i == 1)
                yaw = 0; // 捕手は投手へ
            DrawChar(f.X, f.Z, f.Anim == AnRun ? f.Yaw : yaw, ft, pose, true);
        }
        // 打者 (攻撃側チーム色)。走者に切り替わっていない間だけ打席に立つ
        var bt = BattingTeam();
        var b = batter;
        if (batterRunner == null && b != null)
        {
            // 構え = スイングの溜め位相を静止で使う (バットの持ち手と一致する)
            var stance = b.Anim == AnSwing ? b.AnimT : 0.30f;
            var inSwingPose = b.Anim == AnSwing
                || state == stPrepitch
                || state == stWindup
                || state == stPitch
                || state == stCall;
            DrawChar(b.X, b.Z, (float)Math.PI / 2, bt,
                inSwingPose ? PoseSwing(stance) : PoseIdle(t));
            // バット
            if (state == stPrepitch || state == stWindup || state == stPitch
                || state == stCall || b.Anim == AnSwing)
                renNow.Draw(batMesh, BatMatrix(stance));
        }
        // 走者 (塁上で止まっているときは待機ポーズ)
        var rns = runners;
        if (rns != null)
        {
            foreach (var r in rns)
            {
                var np = BasePos(r.To == 4 ? 0 : r.To);
                var moving = r.AtBase != r.To;
                DrawChar(r.X, r.Z,
                    moving
                        ? (float)Math.Atan2(np[0] - r.X, np[1] - r.Z)
                        : (float)Math.Atan2(-r.X, -r.Z),
                    bt, moving ? PoseRun(r.RunPhase) : PoseIdle(t));
            }
        }

        // ボール
        if (ballVisible)
        {
            if (state == stPitch || state == stLive && !ballRolling)
            {
                for (int i = 5; i >= 1; i--)
                {
                    float lag = i * 0.009f;
                    float scale = 1.0f - i * 0.13f;
                    renNow.Draw(ballMesh,
                        Mat4.Translate(new Vec3(bx - bvx * lag, by - bvy * lag, bz - bvz * lag))
                            * Mat4.Scale(new Vec3(scale, scale, scale)),
                        new Draw3dOpts { Tint = Color.Rgb(1, 0.90f, 0.64f, 0.28f - i * 0.035f), Blend = Gfx.Blend.Alpha });
                }
            }
            renNow.Draw(ballMesh, Mat4.Translate(new Vec3(bx, by, bz)));
        }

        renNow.End();

        // HUD は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        DrawHud();
        Gfx.EndPass();
    }
}
