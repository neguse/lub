// lub の samples/24_baseball (Haxe 版 Baseball24.hx) の TinyC# 版 entry。
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
// gameplay rule (投球・打撃・守備・走塁・スコア) は Haxe 版に忠実。
// boneSlot 表 + 手詰めの packBones は cs-lib の Bones.pack (mesh.bones 順の
// resolve callback) に置き換え、乱数は Math.random でなく決定的な Rand。
// cs-lib のクラスは生成 Lua でサンプルより後に定義されるため static 初期化子
// では参照できず、reloaded ブロックで遅延生成する (Iroha21 と同じ扱い)。

using System;
using System.Collections.Generic;

// 走者。塁 index は 0=本塁(打席) 1..3=各塁 4=生還
public class Runner
{
    public float x;
    public float z;
    public int atBase; // 今いる/直前の塁 (Haxe 版の base。C# 予約語のため改名)
    public int to; // 目標の塁
    public float runPhase = 0;

    public Runner(float x, float z, int atBase, int to)
    {
        this.x = x;
        this.z = z;
        this.atBase = atBase;
        this.to = to;
    }
}

// 野手。home* は定位置、(x,z) が現在地
public class Fielder
{
    public float x;
    public float z;
    public float homeX;
    public float homeZ;
    public float yaw = 0;
    public float runPhase = 0;
    public int anim = 0; // Baseball24.AN_*
    public float animT = 0;

    public Fielder(float hx, float hz)
    {
        homeX = hx;
        homeZ = hz;
        x = hx;
        z = hz;
    }
}

// predictLanding の結果 (Haxe 版の匿名構造体を class に)
public class Landing
{
    public float x;
    public float z;
    public float t;
    public float peak;
}

public static class Baseball24
{
    const int W = 960;
    const int H = 540;
    const float DT = 1.0f / 60.0f;

    // --- フィールド寸法 (m) -------------------------------------------------
    const float BASE_D = 19.4f; // 塁間 27.43m の対角成分
    const float MOUND_Z = 18.44f;
    const float FENCE_R = 76.0f;
    const float FENCE_H = 3.0f;
    const float GRAV = 9.8f;
    const float BALL_R = 0.115f;
    const float DRAG = 0.0055f; // 打球の空気抵抗 (終端速度 ~42m/s)
    const float RUN_SPD = 7.2f; // 走者/野手の走速
    const float CATCH_R = 1.15f; // 捕球半径 (グラブの届く範囲)

    // --- 試合 state ---------------------------------------------------------
    const int ST_INTRO = 0;
    const int ST_PREPITCH = 1;
    const int ST_WINDUP = 2;
    const int ST_PITCH = 3;
    const int ST_LIVE = 4;
    const int ST_CALL = 5;
    const int ST_CHANGE = 6;
    const int ST_END = 7;

    // キャラアニメ
    public const int AN_IDLE = 0;
    public const int AN_READY = 1;
    public const int AN_RUN = 2;
    public const int AN_WINDUP = 3;
    public const int AN_SWING = 4;
    public const int AN_REACH = 5;
    public const int AN_CROUCH = 6;
    public const int AN_THROW = 7;

    // 打球フェーズ (ST_LIVE 中)
    const int PL_FLY = 0; // 打球が空中 (ノーバウンド)
    const int PL_THROW1B = 2; // 一塁送球中
    const int PL_SETTLE = 3; // 判定確定、走者が到達するのを待つ
    const int PL_FOUL = 4;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend, width = W, height = H });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    // --- 乱数 (決定的 xorshift。Math.random は run ごとに列が変わる) ---------
    static Rand? rng = null;

    static float rnd()
    {
        var r = rng;
        if (r == null)
        {
            r = new Rand(0x0B5EBA11);
            rng = r;
        }
        return r.nextFloat();
    }

    static float rrange(float a, float b)
    {
        return a + (b - a) * rnd();
    }

    // --- キャラメッシュ (SDF + bones) ---------------------------------------
    // 身長 ~1.8m。ユニフォームをチーム色で焼いた 2 メッシュを使い分ける
    static List<Mesh3d>? charMesh = null;
    static int[] teamRgb = new int[] { 0xD94038, 0x4073E0 };

    const float TORSO_PX = 0.0f;
    const float TORSO_PY = 0.95f;
    const float HEAD_PY = 1.50f;
    const float ARM_PX = 0.24f;
    const float ARM_PY = 1.40f;
    const float LEG_PX = 0.10f;
    const float LEG_PY = 0.92f;

    static SdfNode charModel(int jersey)
    {
        var white = jersey;
        var skin = 0xF5C29A;
        var pants = 0x3A3E4C;
        var torso = Sdf.capsule(new Vec3(0, 0.92f, 0), new Vec3(0, 1.42f, 0), 0.19f)
            .paint(white)
            .bone("torso", new Vec3(TORSO_PX, TORSO_PY, 0));
        var head = Sdf.sphere(0.15f)
            .move(0, 1.62f, 0)
            .paint(skin)
            .smin(Sdf.sphere(0.115f).move(0, 1.72f, 0).paint(white), 0.03f)
            .smin(Sdf.sphere(0.035f).move(0, 1.60f, 0.15f).paint(skin), 0.02f)
            .bone("head", new Vec3(0, HEAD_PY, 0));
        var armL = Sdf.capsule(new Vec3(0.24f, 1.40f, 0),
            new Vec3(0.31f, 1.00f, 0.05f), 0.065f)
            .paint(skin)
            .bone("arm_l", new Vec3(ARM_PX, ARM_PY, 0));
        var armR = Sdf.capsule(new Vec3(-0.24f, 1.40f, 0),
            new Vec3(-0.31f, 1.00f, 0.05f), 0.065f)
            .paint(skin)
            .bone("arm_r", new Vec3(-ARM_PX, ARM_PY, 0));
        var legL = Sdf.capsule(new Vec3(0.10f, 0.92f, 0),
            new Vec3(0.11f, 0.10f, 0), 0.085f)
            .smin(Sdf.sphere(0.07f).move(0.11f, 0.07f, 0.07f), 0.05f)
            .paint(pants)
            .bone("leg_l", new Vec3(LEG_PX, LEG_PY, 0));
        var legR = Sdf.capsule(new Vec3(-0.10f, 0.92f, 0),
            new Vec3(-0.11f, 0.10f, 0), 0.085f)
            .smin(Sdf.sphere(0.07f).move(-0.11f, 0.07f, 0.07f), 0.05f)
            .paint(pants)
            .bone("leg_r", new Vec3(-LEG_PX, LEG_PY, 0));
        return torso.smin(head, 0.05f).smin(armL, 0.04f).smin(armR, 0.04f)
            .smin(legL, 0.05f).smin(legR, 0.05f);
    }

    static void buildCharMesh()
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
            cm[t].rebuild(Sdf.mesh(charModel(teamRgb[t]), 56));
        }
    }

    // --- ポーズ → ボーン行列 ------------------------------------------------
    // torso が親、head/arms が子、legs は独立。回転はすべて pivot 回り。
    // Haxe 版の boneSlot 表 + 手詰めは Bones.pack (mesh.bones 順の resolve) に
    // 置き換え。pose: [twist, lean, tilt, toy, hx, hy, alx, alz, arx, arz,
    // llx, lrx]
    static List<float> packBones(List<float> p)
    {
        var rTorso = Mat4.rotateY(p[0]) * (Mat4.rotateX(p[1]) * Mat4.rotateZ(p[2]));
        var mTorso = Mat4.translate(new Vec3(0, p[3], 0))
            * Bones.pivotRot(TORSO_PX, TORSO_PY, 0, rTorso);
        var mHead = mTorso
            * Bones.pivotRot(0, HEAD_PY, 0, Mat4.rotateY(p[5]) * Mat4.rotateX(p[4]));
        var mArmL = mTorso
            * Bones.pivotRot(ARM_PX, ARM_PY, 0, Mat4.rotateZ(p[7]) * Mat4.rotateX(p[6]));
        var mArmR = mTorso
            * Bones.pivotRot(-ARM_PX, ARM_PY, 0, Mat4.rotateZ(p[9]) * Mat4.rotateX(p[8]));
        var mLegL = Bones.pivotRot(LEG_PX, LEG_PY, 0, Mat4.rotateX(p[10]));
        var mLegR = Bones.pivotRot(-LEG_PX, LEG_PY, 0, Mat4.rotateX(p[11]));
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
        var mesh = cm != null ? cm[0].data : null;
        return Bones.pack(mesh, (name, px, py, pz) =>
            mats.ContainsKey(name) ? mats[name] : null);
    }

    static List<float> zeroPose()
    {
        return new List<float> { 0, 0, 0, 0, 0, 0, 0, -0.08f, 0, 0.08f, 0, 0 };
    }

    // クリップ。桜井メソッド: 構え/攻撃ポーズは極端に、中割りはほぼゼロ
    static List<float> poseIdle(float t)
    {
        var p = zeroPose();
        var b = (float)Math.Sin(t * 2.1f);
        p[1] = 0.04f + b * 0.015f;
        p[7] = 0.10f + b * 0.02f;
        p[9] = -0.10f - b * 0.02f;
        return p;
    }

    static List<float> poseReady(float t)
    {
        var p = zeroPose();
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

    static List<float> poseCrouch(float t)
    {
        var p = zeroPose();
        p[3] = -0.30f;
        p[1] = 0.38f;
        p[4] = -0.55f;
        p[6] = 1.05f;
        p[8] = 1.05f; // 両腕前
        p[10] = 1.05f;
        p[11] = 1.05f;
        return p;
    }

    static List<float> poseRun(float ph)
    {
        var p = zeroPose();
        var s = (float)Math.Sin(ph);
        p[1] = 0.30f;
        p[10] = s * 1.0f;
        p[11] = -s * 1.0f;
        p[6] = -s * 0.9f;
        p[8] = s * 0.9f;
        p[3] = Math.Abs((float)Math.Cos(ph)) * 0.04f;
        return p;
    }

    // 投球。ph 0..1、リリースは REL_PH
    public const float REL_PH = 0.60f;

    static List<float> poseWindup(float ph)
    {
        var p = zeroPose();
        // 1) 振りかぶり + 足上げ (前 = +Z = rotateX 正)
        var k1 = MathUtil.smoothstep(0.0f, 0.34f, ph);
        p[6] = -2.1f * k1;
        p[8] = -2.1f * k1;
        p[10] = 1.35f * k1;
        p[1] = -0.28f * k1;
        // 2) 踏み込み + 腕を極端に引き絞る
        var k2 = MathUtil.smoothstep(0.38f, 0.54f, ph);
        p[10] = MathUtil.lerp(p[10], 0.55f, k2);
        p[8] = MathUtil.lerp(p[8], -2.95f, k2); // 右腕を頭の後ろまで
        p[6] = MathUtil.lerp(p[6], -0.4f, k2);
        p[1] = MathUtil.lerp(p[1], -0.38f, k2);
        // 3) リリース: 一気に振り抜く (中割りなし)
        var k3 = MathUtil.smoothstep(0.56f, 0.62f, ph);
        p[8] = MathUtil.lerp(p[8], 0.9f, k3);
        p[1] = MathUtil.lerp(p[1], 0.62f, k3);
        p[0] = -0.45f * k3;
        p[10] = MathUtil.lerp(p[10], 0.35f, k3);
        p[11] = -0.3f * k3;
        // 4) フォロースルーの余韻
        var k4 = MathUtil.smoothstep(0.66f, 1.0f, ph);
        p[8] = MathUtil.lerp(p[8], 0.5f, k4);
        p[1] = MathUtil.lerp(p[1], 0.45f, k4);
        return p;
    }

    // スイング。ph 0..1、ミートは SWING_HIT_PH
    public const float SWING_HIT_PH = 0.52f;

    static List<float> poseSwing(float ph)
    {
        var p = zeroPose();
        p[5] = 0.9f; // 顔は投手へ
        p[4] = -0.15f;
        // 1) 溜め: 捕手側へ捻る
        var k1 = MathUtil.smoothstep(0.0f, 0.40f, ph);
        p[0] = -0.55f * k1;
        p[6] = -1.5f * k1;
        p[8] = -1.7f * k1;
        p[7] = 0.9f * k1;
        p[9] = -0.4f * k1;
        p[10] = -0.35f * k1;
        // 2) 爆発: 1-2 フレームで振り抜く
        var k2 = MathUtil.smoothstep(0.47f, 0.54f, ph);
        p[0] = MathUtil.lerp(p[0], 1.55f, k2);
        p[6] = MathUtil.lerp(p[6], 0.6f, k2);
        p[8] = MathUtil.lerp(p[8], 0.6f, k2);
        p[7] = MathUtil.lerp(p[7], 0.3f, k2);
        p[9] = MathUtil.lerp(p[9], -1.1f, k2);
        p[1] = 0.12f * k2;
        p[10] = MathUtil.lerp(p[10], 0.4f, k2);
        p[11] = -0.5f * k2;
        // 3) フォロースルー: ウェイト破綻気味に大きく
        var k3 = MathUtil.smoothstep(0.6f, 1.0f, ph);
        p[0] = MathUtil.lerp(p[0], 1.85f, k3);
        p[4] = MathUtil.lerp(p[4], -0.3f, k3);
        return p;
    }

    static List<float> poseReach(float t)
    {
        var p = zeroPose();
        p[6] = 2.9f;
        p[8] = 2.9f;
        p[7] = 0.25f;
        p[9] = -0.25f;
        p[4] = -0.8f;
        return p;
    }

    static List<float> poseThrow(float ph)
    {
        var p = zeroPose();
        var k1 = MathUtil.smoothstep(0.0f, 0.4f, ph);
        p[8] = -2.6f * k1;
        p[0] = -0.4f * k1;
        var k2 = MathUtil.smoothstep(0.45f, 0.58f, ph);
        p[8] = MathUtil.lerp(p[8], 0.8f, k2);
        p[0] = MathUtil.lerp(p[0], 0.35f, k2);
        p[1] = 0.35f * k2;
        return p;
    }

    static List<float> poseFor(int anim, float t, float runPhase)
    {
        if (anim == AN_READY) return poseReady(t);
        if (anim == AN_RUN) return poseRun(runPhase);
        if (anim == AN_WINDUP) return poseWindup(t);
        if (anim == AN_SWING) return poseSwing(t);
        if (anim == AN_REACH) return poseReach(t);
        if (anim == AN_CROUCH) return poseCrouch(t);
        if (anim == AN_THROW) return poseThrow(t);
        return poseIdle(t);
    }

    // --- 静的メッシュ (Shapes) ----------------------------------------------
    static Mesh3d? fieldMesh = null;
    static Mesh3d? ballMesh = null;
    static Mesh3d? batMesh = null;

    static void fan(List<float> dst, float cx, float cy, float cz,
        float r, float a0, float a1, int segs, List<float> col)
    {
        for (int i = 0; i < segs; i++)
        {
            var t0 = a0 + (a1 - a0) * i / segs;
            var t1 = a0 + (a1 - a0) * (i + 1) / segs;
            Shapes.tri(dst, new List<float> { cx, cy, cz },
                new List<float>
                    { cx + (float)Math.Sin(t0) * r, cy, cz + (float)Math.Cos(t0) * r },
                new List<float>
                    { cx + (float)Math.Sin(t1) * r, cy, cz + (float)Math.Cos(t1) * r },
                new List<float> { 0, 1, 0 }, col);
        }
    }

    static void buildField()
    {
        var fm = fieldMesh ?? new Mesh3d("bb24_field");
        fieldMesh = fm;
        var bm = ballMesh ?? new Mesh3d("bb24_ball");
        ballMesh = bm;
        var btm = batMesh ?? new Mesh3d("bb24_bat");
        batMesh = btm;

        var v = new List<float>();
        var grass = new List<float> { 0.24f, 0.47f, 0.24f, 1.0f };
        var grassIn = new List<float> { 0.28f, 0.54f, 0.27f, 1.0f };
        var dirt = new List<float> { 0.63f, 0.46f, 0.31f, 1.0f };
        var lineW = new List<float> { 0.95f, 0.95f, 0.92f, 1.0f };
        var wall = new List<float> { 0.26f, 0.42f, 0.58f, 1.0f };
        var wallTop = new List<float> { 0.88f, 0.82f, 0.35f, 1.0f };
        var up = new List<float> { 0, 1, 0 };

        // 地面 (ファウルグラウンド込みの外周)
        Shapes.quad(v, new List<float> { -95, 0, -20 },
            new List<float> { -95, 0, 95 }, new List<float> { 95, 0, 95 },
            new List<float> { 95, 0, -20 }, up, grass);
        // フェアグラウンドの扇形 (少し明るい緑)
        fan(v, 0, 0.012f, 0, FENCE_R, -(float)Math.PI / 4, (float)Math.PI / 4, 24, grassIn);
        // 内野ダート (ひし形)
        Shapes.quad(v, new List<float> { 0, 0.024f, -2.2f },
            new List<float> { 24.5f, 0.024f, BASE_D },
            new List<float> { 0, 0.024f, 43.0f },
            new List<float> { -24.5f, 0.024f, BASE_D }, up, dirt);
        // 内野の芝 (ダートの内側)
        Shapes.quad(v, new List<float> { 0, 0.036f, 4.2f },
            new List<float> { 15.5f, 0.036f, BASE_D },
            new List<float> { 0, 0.036f, 34.6f },
            new List<float> { -15.5f, 0.036f, BASE_D }, up, grassIn);
        // マウンド (つぶれたドーム + ダート円)
        fan(v, 0, 0.048f, MOUND_Z, 2.9f, -(float)Math.PI, (float)Math.PI, 16, dirt);
        Shapes.sphere(v, 0, -2.35f, MOUND_Z, 2.6f, dirt, 8, 16);
        // 本塁と各塁
        Shapes.box(v, 0, 0.03f, 0, 0.55f, 0.05f, 0.55f, lineW);
        Shapes.box(v, BASE_D, 0.07f, BASE_D, 0.55f, 0.13f, 0.55f, lineW);
        Shapes.box(v, 0, 0.07f, BASE_D * 2, 0.55f, 0.13f, 0.55f, lineW);
        Shapes.box(v, -BASE_D, 0.07f, BASE_D, 0.55f, 0.13f, 0.55f, lineW);
        // プレート (マウンド上)
        Shapes.box(v, 0, 0.30f, MOUND_Z, 0.61f, 0.05f, 0.15f, lineW);
        // ファウルライン
        var d = 0.70710678f;
        foreach (var s in new List<float> { -1.0f, 1.0f })
        {
            var nx = -s * d; // ライン直交方向
            var nz = d;
            var half = 0.09f;
            var x0 = s * 1.2f * d;
            var z0 = 1.2f * d;
            var x1 = s * (FENCE_R - 0.6f) * d;
            var z1 = (FENCE_R - 0.6f) * d;
            Shapes.quad(v,
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
            var x0 = (float)Math.Sin(a0) * FENCE_R;
            var z0 = (float)Math.Cos(a0) * FENCE_R;
            var x1 = (float)Math.Sin(a1) * FENCE_R;
            var z1 = (float)Math.Cos(a1) * FENCE_R;
            var am = (a0 + a1) * 0.5f;
            var n = new List<float> { -(float)Math.Sin(am), 0, -(float)Math.Cos(am) };
            Shapes.quad(v, new List<float> { x0, 0, z0 },
                new List<float> { x0, FENCE_H, z0 },
                new List<float> { x1, FENCE_H, z1 },
                new List<float> { x1, 0, z1 }, n, wall);
            Shapes.quad(v, new List<float> { x0, FENCE_H, z0 },
                new List<float> { x0, FENCE_H + 0.18f, z0 },
                new List<float> { x1, FENCE_H + 0.18f, z1 },
                new List<float> { x1, FENCE_H, z1 }, n, wallTop);
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
            Shapes.quad(v, new List<float> { x0, 0, z0 },
                new List<float> { x0, 1.6f, z0 },
                new List<float> { x1, 1.6f, z1 },
                new List<float> { x1, 0, z1 }, n, bsCol);
        }
        fm.rebuild(Shapes3d.fromInterleaved(v));

        var ballVerts = new List<float>();
        Shapes.sphere(ballVerts, 0, 0, 0, BALL_R,
            new List<float> { 0.96f, 0.96f, 0.94f, 1.0f }, 8, 12);
        bm.rebuild(Shapes3d.fromInterleaved(ballVerts));

        var batVerts = new List<float>();
        Shapes.box(batVerts, 0, 0, 0.44f, 0.075f, 0.075f, 0.88f,
            new List<float> { 0.85f, 0.66f, 0.40f, 1.0f });
        btm.rebuild(Shapes3d.fromInterleaved(batVerts));
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
    static bool stepBall(float dt, bool drag)
    {
        if (drag)
        {
            var sp = (float)Math.Sqrt(bvx * bvx + bvy * bvy + bvz * bvz);
            var f = 1.0f / (1.0f + DRAG * sp * dt);
            bvx *= f;
            bvy *= f;
            bvz *= f;
        }
        bvy -= GRAV * dt;
        bx += bvx * dt;
        by += bvy * dt;
        bz += bvz * dt;
        var bounced = false;
        // 地面
        if (by < BALL_R && bvy < 0)
        {
            by = BALL_R;
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
            by = BALL_R;
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
        if (bz > 0 && Math.Abs(bx) < bz + 2 && hr > FENCE_R - BALL_R)
        {
            if (by > FENCE_H)
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
                    bx = nx * (FENCE_R - BALL_R);
                    bz = nz * (FENCE_R - BALL_R);
                    ballBounces++;
                }
            }
        }
        return bounced;
    }

    // 着地予測 (状態を退避してシミュレート)
    static Landing predictLanding()
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
            stepBall(DT, true);
            t += DT;
            if (by > peak)
                peak = by;
            if (ballBounces > sb || ballRolling)
                break;
        }
        var r = new Landing { x = bx, z = bz, t = t, peak = peak };
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

    static List<List<float>> fielderHomes()
    {
        return new List<List<float>>
        {
            new List<float> { 0.0f, MOUND_Z }, // P
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

    static List<float> basePos(int i)
    {
        if (i == 1) return new List<float> { BASE_D, BASE_D };
        if (i == 2) return new List<float> { 0.0f, BASE_D * 2 };
        if (i == 3) return new List<float> { -BASE_D, BASE_D };
        return new List<float> { 0.0f, 0.0f }; // 0 と 4 は本塁
    }

    static void resetActors()
    {
        var fs = new List<Fielder>();
        foreach (var h in fielderHomes())
            fs.Add(new Fielder(h[0], h[1]));
        fielders = fs;
        batter = new Fielder(-0.85f, 0.0f);
        runners = new List<Runner>();
    }

    // --- 試合状態 ---------------------------------------------------------------
    static int state = ST_INTRO;
    static float stateT = 0.0f;
    static int inning = 1;
    static int half = 0; // 0=表 (RED 攻撃) 1=裏
    static List<int> score = new List<int> { 0, 0 };
    static int balls = 0;
    static int strikes = 0;
    static int outs = 0;

    static int battingTeam()
    {
        return half == 0 ? 0 : 1;
    }

    static int fieldingTeam()
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
    static int playPhase = PL_FLY;
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

    static void showEvent(string s, Color? c)
    {
        eventText = s;
        eventT = 0.0f;
        eventCol = c ?? Color.rgb(1.0f, 0.98f, 0.9f);
    }

    static void setState(int s)
    {
        state = s;
        stateT = 0.0f;
    }

    // --- 投球開始 -----------------------------------------------------------------
    static void startPitch()
    {
        var fs = fielders;
        if (fs == null)
            return;
        // 目標: ゾーン内/外を先に決めてから座標を出す
        pitchInZone = rnd() < 0.62f;
        if (pitchInZone)
        {
            pitchTX = rrange(-0.20f, 0.20f);
            pitchTY = rrange(0.60f, 1.10f);
        }
        else
        {
            // ゾーンの少し外
            if (rnd() < 0.5f)
            {
                pitchTX = (rnd() < 0.5f ? -1.0f : 1.0f) * rrange(0.28f, 0.45f);
                pitchTY = rrange(0.45f, 1.25f);
            }
            else
            {
                pitchTX = rrange(-0.35f, 0.35f);
                pitchTY = rnd() < 0.5f ? rrange(0.15f, 0.42f) : rrange(1.28f, 1.55f);
            }
        }
        willSwing = rnd() < (pitchInZone ? 0.80f : 0.26f);
        if (willSwing)
        {
            var r = rnd();
            if (r < 0.24f)
                swingOutcome = 0;
            else if (r < 0.58f)
                swingOutcome = 1;
            else
            {
                swingOutcome = 2;
                exitSpeed = 23.0f + 23.0f * (float)Math.Pow(rnd(), 0.7f);
                exitLaunch = rrange(-6.0f, 42.0f);
                exitSpray = rrange(-38.0f, 38.0f);
            }
        }
        swingStarted = false;
        setState(ST_WINDUP);
        fs[0].anim = AN_WINDUP;
        fs[0].animT = 0;
    }

    // リリース: ボールに初速を与える (重力補償で目標へ届ける)
    static void releaseBall()
    {
        bx = 0.35f;
        by = 1.9f;
        bz = MOUND_Z - 0.55f;
        var speed = rrange(31.0f, 40.0f);
        var dz = 0.42f - bz;
        var t = Math.Abs(dz) / speed;
        bvx = (pitchTX - bx) / t;
        bvy = (pitchTY - by) / t + 0.5f * GRAV * t;
        bvz = dz / t;
        ballVisible = true;
        ballBounces = 0;
        ballRolling = false;
        isHomeRun = false;
        setState(ST_PITCH);
    }

    // --- 打撃結果の解決 -------------------------------------------------------------
    static void resolveContact()
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
                showEvent("SWING & MISS", null);
            }
            else if (pitchInZone)
            {
                strikes++;
                showEvent("STRIKE", null);
            }
            else
            {
                balls++;
                showEvent("BALL", null);
            }
            fs[1].anim = AN_REACH;
            fs[1].animT = 0;
            ballVisible = false;
            afterCall();
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
            speed = rrange(16.0f, 34.0f);
            launch = rrange(15.0f, 70.0f);
            spray = (rnd() < 0.5f ? -1.0f : 1.0f) * rrange(50.0f, 130.0f);
        }
        var la = MathUtil.radians(launch);
        var sa = MathUtil.radians(spray);
        bx = 0.0f;
        by = 1.0f;
        bz = 0.35f;
        bvx = speed * (float)Math.Cos(la) * (float)Math.Sin(sa);
        bvy = speed * (float)Math.Sin(la);
        bvz = speed * (float)Math.Cos(la) * (float)Math.Cos(sa);
        ballBounces = 0;
        ballRolling = false;
        isHomeRun = false;
        var land = predictLanding();
        landing = land;
        liveT = 0.0f;
        ballHeldBy = -1;
        if (swingOutcome == 1)
        {
            playPhase = PL_FOUL;
            setState(ST_LIVE);
            return;
        }
        // 打者走者スタート
        var br = new Runner(b.x, b.z, 0, 1);
        batterRunner = br;
        rns.Add(br);
        b.anim = AN_SWING; // 走り出しはスイングの続きから
        // 最寄りの野手が追う
        chaser = nearestFielder(land.x, land.z);
        playPhase = PL_FLY;
        camCut = true;
        setState(ST_LIVE);
    }

    static int nearestFielder(float x, float z)
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
            var d = (f.x - x) * (f.x - x) + (f.z - z) * (f.z - z);
            if (d < bd)
            {
                bd = d;
                best = i;
            }
        }
        return best;
    }

    // 打席の結果が確定 (カウント系)。四球/三振/次打者を処理
    static void afterCall()
    {
        setState(ST_CALL);
        var b = batter;
        var rns = runners;
        if (b == null || rns == null)
            return;
        if (strikes >= 3)
        {
            showEvent("STRIKE OUT!", Color.rgb(1.0f, 0.5f, 0.3f));
            outs++;
            newBatterPending = true;
        }
        else if (balls >= 4)
        {
            showEvent("WALK", Color.rgb(0.5f, 0.9f, 1.0f));
            // 押し出し: 1塁から連続で埋まっている走者だけ 1 つ進む
            var occ = new Dictionary<int, Runner>();
            foreach (var r in rns)
                occ[r.to] = r;
            int free = 1;
            while (occ.ContainsKey(free))
                free++;
            for (int bs = 1; bs < free; bs++)
                occ[bs].to = bs + 1;
            rns.Add(new Runner(b.x, b.z, 0, 1));
            newBatterPending = true;
        }
    }

    static bool newBatterPending = false;

    // --- 野手 AI (ST_LIVE) ------------------------------------------------------------
    static bool moveTowards(Fielder f, float tx, float tz, float dt,
        float spd)
    {
        var dx = tx - f.x;
        var dz = tz - f.z;
        var d = (float)Math.Sqrt(dx * dx + dz * dz);
        if (d < 0.15f)
        {
            if (f.anim == AN_RUN)
                f.anim = AN_READY;
            return true;
        }
        var mv = Math.Min(d, spd * dt);
        f.x += dx / d * mv;
        f.z += dz / d * mv;
        f.yaw = (float)Math.Atan2(dx, dz);
        f.anim = AN_RUN;
        f.runPhase += dt * 11.0f;
        return false;
    }

    static void updateLive(float dt)
    {
        var fs = fielders;
        var rns = runners;
        if (fs == null || rns == null)
            return;
        liveT += dt;
        stepBall(dt, true);

        if (playPhase == PL_FOUL)
        {
            if (liveT > 1.25f)
            {
                if (strikes < 2)
                    strikes++;
                showEvent("FOUL", null);
                ballVisible = false;
                var brf = batterRunner;
                if (brf != null)
                    rns.Remove(brf);
                batterRunner = null;
                afterCall();
            }
            return;
        }

        if (isHomeRun && playPhase == PL_FLY)
        {
            showEvent("HOME RUN!", Color.rgb(1.0f, 0.85f, 0.25f));
            shakeAmp = 0.35f;
            foreach (var r in rns)
                r.to = 4;
            playPhase = PL_SETTLE;
        }

        // 走者更新
        updateRunners(dt, 1.0f);

        // 野手: 追走者は打球へ、一塁手はベースカバー、他は定位置へ
        for (int i = 0; i < 9; i++)
        {
            var f = fs[i];
            if (state != ST_LIVE)
                break;
            if (i == chaser && ballHeldBy < 0 && playPhase != PL_SETTLE)
            {
                // 落下点 (フライ) or 転がるボールの少し先 (ゴロ)
                var land = landing;
                var flying = ballBounces == 0 && !ballRolling;
                var tx = flying && land != null ? land.x : bx + bvx * 0.35f;
                var tz = flying && land != null ? land.z : bz + bvz * 0.35f;
                var arrived = moveTowards(f, tx, tz, dt, RUN_SPD);
                var dx = f.x - bx;
                var dz = f.z - bz;
                var dist = (float)Math.Sqrt(dx * dx + dz * dz);
                if (flying && by < 2.6f && bvy < 0 && dist < CATCH_R)
                {
                    // ノーバウンド捕球 → アウト
                    fielderCaught(i, true);
                }
                else if ((ballBounces > 0 || ballRolling)
                    && dist < CATCH_R * 0.8f && by < 1.2f)
                {
                    fielderCaught(i, false);
                }
                else if (arrived && (ballBounces > 0 || ballRolling))
                {
                    f.anim = AN_READY;
                }
            }
            else if (i == 2 && playPhase != PL_SETTLE && batterRunner != null)
            {
                // 一塁手はベースへ (自分が追走者でなければ)
                if (i != chaser)
                    moveTowards(f, BASE_D - 0.4f, BASE_D - 0.4f, dt, RUN_SPD);
            }
            else if (i != chaser)
            {
                moveTowards(f, f.homeX, f.homeZ, dt, RUN_SPD * 0.8f);
            }
        }

        // 一塁送球の到達判定
        if (playPhase == PL_THROW1B)
        {
            throwT += dt;
            var k = Math.Min(1.0f, throwT / throwDur);
            // 送球は放物線 (見た目用に手計算)
            bx = MathUtil.lerp(throwFromX, BASE_D, k);
            bz = MathUtil.lerp(throwFromZ, BASE_D, k);
            by = MathUtil.lerp(throwFromY, 1.2f, k) + (float)Math.Sin(k * (float)Math.PI) * 1.4f;
            if (k >= 1.0f)
            {
                // 封殺 or セーフ: 走者の進塁具合と競争
                var br = batterRunner;
                if (br != null && br.atBase < 1)
                {
                    outs++;
                    showEvent("OUT!", Color.rgb(1.0f, 0.5f, 0.3f));
                    rns.Remove(br);
                    // 他の走者は 1 つ進む
                    foreach (var r in rns)
                        if (r.to < 3)
                            r.to++;
                }
                else
                {
                    showEvent("SAFE!", Color.rgb(0.5f, 1.0f, 0.6f));
                }
                batterRunner = null;
                ballHeldBy = 2;
                ballVisible = false;
                playPhase = PL_SETTLE;
            }
        }

        // 決着: 走者が全員目標に着いたら打席交代
        if (playPhase == PL_SETTLE)
        {
            var settled = true;
            foreach (var r in rns)
                if (r.atBase < r.to)
                    settled = false;
            if (settled && liveT > 1.0f)
            {
                ballVisible = false;
                newBatterPending = true;
                setState(ST_CALL);
            }
        }
        // 保険: 異常に長引いたら打ち切り
        if (liveT > 14.0f)
        {
            ballVisible = false;
            newBatterPending = true;
            setState(ST_CALL);
        }
    }

    // 捕球した。fly=ノーバウンド (アウト)
    static void fielderCaught(int i, bool fly)
    {
        var fs = fielders;
        var rns = runners;
        if (fs == null || rns == null)
            return;
        ballHeldBy = i;
        var f = fs[i];
        f.anim = fly ? AN_REACH : AN_READY;
        f.animT = 0;
        if (fly)
        {
            outs++;
            showEvent("CAUGHT!", Color.rgb(1.0f, 0.6f, 0.3f));
            // 打者アウト。走者は帰塁 (簡略: その場から戻る)
            var br = batterRunner;
            if (br != null)
                rns.Remove(br);
            batterRunner = null;
            foreach (var r in rns)
                r.to = r.atBase;
            ballVisible = false;
            playPhase = PL_SETTLE;
            return;
        }
        // ゴロ/落ちたフライ: 一塁封殺が間に合いそうなら送球、無理ならヒット確定
        var gatherDist = (float)Math.Sqrt(f.x * f.x + f.z * f.z);
        var brg = batterRunner;
        if (brg != null && brg.atBase < 1 && gatherDist < 34)
        {
            f.anim = AN_THROW;
            f.animT = 0;
            playPhase = PL_THROW1B;
            throwFromX = bx;
            throwFromY = Math.Max(by, 1.3f);
            throwFromZ = bz;
            var d = (float)Math.Sqrt((BASE_D - bx) * (BASE_D - bx)
                + (BASE_D - bz) * (BASE_D - bz));
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
        showEvent(bases == 1 ? "HIT!" : bases == 2 ? "DOUBLE!" : "TRIPLE!",
            Color.rgb(0.55f, 1.0f, 0.6f));
        foreach (var r in rns)
            r.to = r == batterRunner ? bases : Math.Min(4, r.atBase + bases);
        batterRunner = null;
        ballVisible = false;
        playPhase = PL_SETTLE;
    }

    static void updateRunners(float dt, float spdScale)
    {
        var rns = runners;
        if (rns == null)
            return;
        int i = rns.Count - 1;
        while (i >= 0)
        {
            var r = rns[i];
            if (r.atBase < r.to)
            {
                int nextBase = r.atBase + 1;
                var np = basePos(nextBase == 4 ? 0 : nextBase);
                var dx = np[0] - r.x;
                var dz = np[1] - r.z;
                var d = (float)Math.Sqrt(dx * dx + dz * dz);
                var mv = RUN_SPD * spdScale * dt;
                if (d <= mv)
                {
                    r.x = np[0];
                    r.z = np[1];
                    r.atBase = nextBase;
                    if (nextBase >= 4)
                    {
                        score[battingTeam()] = score[battingTeam()] + 1;
                        showEvent("RUN SCORED!", Color.rgb(1.0f, 0.9f, 0.4f));
                        rns.RemoveAt(i);
                    }
                }
                else
                {
                    r.x += dx / d * mv;
                    r.z += dz / d * mv;
                }
                r.runPhase += dt * 11.0f;
            }
            else if (r.atBase > r.to)
            {
                // 帰塁
                var np = basePos(r.to);
                var dx = np[0] - r.x;
                var dz = np[1] - r.z;
                var d = (float)Math.Sqrt(dx * dx + dz * dz);
                var mv = RUN_SPD * dt;
                if (d <= mv)
                {
                    r.x = np[0];
                    r.z = np[1];
                    r.atBase = r.to;
                }
                else
                {
                    r.x += dx / d * mv;
                    r.z += dz / d * mv;
                }
                r.runPhase += dt * 11.0f;
            }
            i--;
        }
    }

    // --- state machine 本体 ----------------------------------------------------------
    static void updateGame(float dt)
    {
        stateT += dt;
        eventT += dt;
        var fs = fielders;
        var rns = runners;
        var b = batter;
        if (fs == null || rns == null || b == null)
            return;
        if (state == ST_INTRO)
        {
            if (stateT > 1.8f)
            {
                showEvent("PLAY BALL!", Color.rgb(1.0f, 0.95f, 0.5f));
                setState(ST_PREPITCH);
            }
        }
        else if (state == ST_PREPITCH)
        {
            // 全員が定位置に戻るのを待つ (テンポ優先で上限 1.4s)
            for (int i = 0; i < 9; i++)
            {
                var f = fs[i];
                if (i != 1)
                    moveTowards(f, f.homeX, f.homeZ, dt, RUN_SPD * 0.8f);
            }
            updateRunners(dt, 1.0f);
            if (newBatterPending)
            {
                b.x = -0.85f;
                b.z = 0.0f;
                b.anim = AN_IDLE;
                balls = 0;
                strikes = 0;
                newBatterPending = false;
            }
            // 走者が塁に着くまでは投げない (四球の押し出し等)。上限つき
            var settled = true;
            foreach (var r in rns)
                if (r.atBase != r.to)
                    settled = false;
            if (stateT > 1.05f && (settled || stateT > 6.0f))
            {
                if (outs >= 3)
                {
                    setState(ST_CHANGE);
                    showEvent("CHANGE", Color.rgb(0.9f, 0.9f, 0.95f));
                }
                else
                {
                    startPitch();
                }
            }
        }
        else if (state == ST_WINDUP)
        {
            fs[0].animT += dt / 1.1f;
            if (fs[0].animT >= REL_PH)
                releaseBall();
        }
        else if (state == ST_PITCH)
        {
            fs[0].animT = Math.Min(1.0f, fs[0].animT + dt / 1.1f);
            // 投球は無抵抗の放物線 (短距離なので誤差は無視できる)
            bvy -= GRAV * dt;
            bx += bvx * dt;
            by += bvy * dt;
            bz += bvz * dt;
            // スイング開始タイミング (ミートの瞬間に SWING_HIT_PH が来るよう逆算)
            var tToPlate = bvz != 0 ? (0.42f - bz) / bvz : 0.0f;
            if (willSwing && !swingStarted && tToPlate < SWING_HIT_PH * 0.55f)
            {
                b.anim = AN_SWING;
                b.animT = 0;
                swingStarted = true;
            }
            if (b.anim == AN_SWING)
                b.animT = Math.Min(1.0f, b.animT + dt / 0.55f);
            if (bz <= 0.42f)
                resolveContact();
        }
        else if (state == ST_LIVE)
        {
            if (b.anim == AN_SWING)
            {
                b.animT = Math.Min(1.0f, b.animT + dt / 0.55f);
                if (b.animT >= 1.0f)
                    b.anim = AN_IDLE;
            }
            foreach (var f in fs)
                if (f.anim == AN_THROW || f.anim == AN_REACH)
                    f.animT = Math.Min(1.0f, f.animT + dt / 0.45f);
            updateLive(dt);
        }
        else if (state == ST_CALL)
        {
            if (b.anim == AN_SWING)
            {
                b.animT = Math.Min(1.0f, b.animT + dt / 0.55f);
                if (b.animT >= 1.0f)
                    b.anim = AN_IDLE;
            }
            updateRunners(dt, 1.0f);
            if (stateT > 0.95f)
                setState(ST_PREPITCH);
        }
        else if (state == ST_CHANGE)
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
                    setState(ST_END);
                    if (score[0] == score[1])
                        showEvent("DRAW", Color.rgb(0.9f, 0.9f, 0.95f));
                    else
                    {
                        var w = score[0] > score[1] ? 0 : 1;
                        showEvent("GAME SET  " + teamName[w] + " WINS!",
                            Color.rgb(1.0f, 0.9f, 0.4f));
                    }
                }
                else
                {
                    resetActors();
                    newBatterPending = true;
                    setState(ST_PREPITCH);
                }
            }
        }
        else if (state == ST_END)
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
                resetActors();
                newBatterPending = true;
                showEvent("PLAY BALL!", Color.rgb(1.0f, 0.95f, 0.5f));
                setState(ST_PREPITCH);
            }
        }
    }

    // --- カメラ ---------------------------------------------------------------------
    static Vec3? camEye = null;
    static Vec3? camTarget = null;
    static float camFov = 34.0f;
    static bool camCut = false;

    static void updateCamera(float dt)
    {
        var eye = camEye;
        var tgt = camTarget;
        if (eye == null || tgt == null)
            return;
        var de = new Vec3(4.6f, 3.1f, 29.0f); // センター後方の中継カメラ
        var dtg = new Vec3(-0.3f, 1.1f, 1.2f);
        var dfov = 30.0f;
        if (state == ST_LIVE && playPhase != PL_FOUL)
        {
            var land = landing;
            if (land != null && (land.peak > 7.0f || isHomeRun)
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
                de = new Vec3(0, 15.0f, -14.0f);
                dtg = new Vec3(0, 0.0f, 20.0f);
                dfov = 50.0f;
            }
        }
        else if (state == ST_INTRO || state == ST_CHANGE || state == ST_END)
        {
            var a = tAccum * 0.12f;
            de = new Vec3((float)Math.Sin(a) * 46.0f, 17.0f, 24.0f + (float)Math.Cos(a) * 30.0f);
            dtg = new Vec3(0, 1.0f, 22.0f);
            dfov = 42.0f;
        }
        var k = camCut ? 1.0f : Math.Min(1.0f, 7.0f * dt);
        camCut = false;
        eye = eye.lerp(de, k);
        tgt = tgt.lerp(dtg, k);
        camFov = MathUtil.lerp(camFov, dfov, k);
        // 画面振動 (ヒットの手応え)。減衰付きで eye だけ揺らす
        if (shakeAmp > 0.003f)
        {
            var s = shakeAmp;
            eye = new Vec3(eye.x + (float)Math.Sin(tAccum * 71.0f) * s * 0.25f,
                eye.y + (float)Math.Sin(tAccum * 93.0f + 1.7f) * s * 0.2f, eye.z);
            shakeAmp *= (float)Math.Pow(0.001f, dt); // ~0.7s で収束
        }
        camEye = eye;
        camTarget = tgt;
    }

    // --- 描画 -----------------------------------------------------------------------
    static bool reloaded = true; // hot reload で true に戻る (19_sdf と同じトリック)
    static FixedStep? step = null;

    static Renderer3d? ren = null;

    static void drawChar(float x, float z, float yaw, int team,
        List<float> pose)
    {
        var renNow = ren;
        var cm = charMesh;
        if (renNow == null || cm == null)
            return;
        var model = Mat4.translate(new Vec3(x, 0, z)) * Mat4.rotateY(yaw);
        renNow.draw(cm[team], model, new Draw3dOpts { bones = packBones(pose) });
    }

    // バット。スイング位相から向きを決める (打者ローカル)
    static Mat4 batMatrix(float ph)
    {
        // 溜め → 一気に振り抜き → フォロー (rotateX は +θ で +Z が下向きに回る)
        var ang = -2.35f; // 構え: 後方上
        var tilt = 1.05f;
        var k2 = MathUtil.smoothstep(0.47f, 0.56f, ph);
        ang = MathUtil.lerp(ang, 1.15f, k2);
        tilt = MathUtil.lerp(tilt, -0.05f, k2);
        var k3 = MathUtil.smoothstep(0.6f, 1.0f, ph);
        ang = MathUtil.lerp(ang, 1.9f, k3);
        tilt = MathUtil.lerp(tilt, 0.45f, k3);
        // Haxe 版の変数名 local は Lua キーワードで emit が不正になるため改名
        var batLocal = Mat4.translate(new Vec3(-0.12f, 1.45f, -0.15f))
            * (Mat4.rotateY(ang) * Mat4.rotateX(tilt));
        var b = batter;
        var px = b != null ? b.x : 0.0f;
        var pz = b != null ? b.z : 0.0f;
        return Mat4.translate(new Vec3(px, 0, pz))
            * (Mat4.rotateY((float)Math.PI / 2) * batLocal);
    }

    // --- HUD ------------------------------------------------------------------------
    static string? ttf = null;
    static int fontVersion = 0;
    static MeshText? mtext = null;

    static bool ensureText()
    {
        Io.load_text("samples/24_baseball/data/MPLUS1p-subset.ttf",
            out var text, out var version, out _, out _);
        if (text == null)
            return false;
        if (ttf == null || fontVersion != version)
        {
            ttf = text;
            fontVersion = version;
            mtext = new MeshText("bb24_text", text, version, W, H);
        }
        return mtext != null;
    }

    static void drawHud()
    {
        if (!ensureText())
            return;
        var mt = mtext;
        if (mt == null)
            return;
        var cream = Color.rgb(0.97f, 0.96f, 0.9f);
        var red = Color.rgb(1.0f, 0.5f, 0.45f);
        var blue = Color.rgb(0.55f, 0.7f, 1.0f);
        // スコア (チーム名は各チーム色)
        var sL = teamName[0] + " ";
        var sM = score[0] + " - " + score[1];
        var sR = " " + teamName[1];
        var size = 26.0f;
        var total = mt.width(sL, size) + mt.width(sM, size)
            + mt.width(sR, size);
        var x = W * 0.5f - total * 0.5f;
        mt.text(sL, x, 38, size, red);
        mt.text(sM, x + mt.width(sL, size), 38, size, cream);
        mt.text(sR, x + mt.width(sL, size) + mt.width(sM, size), 38, size,
            blue);
        // イニングとカウント
        var halfMark = half == 0 ? "TOP" : "BOT";
        mt.textCentered("INN " + inning + " " + halfMark + "   B" + balls
            + " S" + strikes + " O" + outs, W * 0.5f, 64, 15,
            Color.rgb(0.85f, 0.87f, 0.9f));
        // イベントテキスト (出現時にスケールが弾む)
        if (eventText != "" && eventT < 1.6f)
        {
            var ec = eventCol;
            if (ec == null)
                return;
            var pop = 1.0f + 0.6f * (float)Math.Exp(-eventT * 9.0f);
            var a = eventT > 1.25f ? 1.0f - (eventT - 1.25f) / 0.35f : 1.0f;
            var c = Color.rgb(ec.r, ec.g, ec.b, a);
            mt.textCentered(eventText, W * 0.5f, 190, 52f * pop, c);
        }
    }

    static void simulateTick()
    {
        tAccum += DT;
        // ヒットストップ: その間シミュレーションだけ止める
        if (hitstopT > 0)
        {
            hitstopT -= DT;
        }
        else
        {
            updateGame(DT);
        }
        updateCamera(DT);

        var fs = fielders;
        if (fs == null) return;
        // 捕手は基本しゃがみ。捕球リアクションだけ一瞬立つ
        if (fs[1].anim == AN_REACH)
        {
            fs[1].animT += DT;
            if (fs[1].animT > 0.5f)
                fs[1].anim = AN_CROUCH;
        }
        else
        {
            fs[1].anim = AN_CROUCH;
        }
    }

    // --- main loop --------------------------------------------------------------------
    public static void onFrame(float dt)
    {
        if (reloaded)
        {
            rng = new Rand(0x0B5EBA11);
            ren = new Renderer3d("bb24");
            camEye = new Vec3(5.5f, 3.4f, 30.0f);
            camTarget = new Vec3(0, 1.3f, 0);
            camFov = 34.0f;
            buildCharMesh();
            buildField();
            resetActors();
            state = ST_INTRO;
            stateT = 0;
            reloaded = false;
            showEvent("PLAY BALL!", Color.rgb(1.0f, 0.95f, 0.5f));
        }

        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.frame(dt, _ => simulateTick());

        var fs = fielders;
        var renNow = ren;
        var eyeNow = camEye;
        var tgtNow = camTarget;
        if (fs == null || renNow == null || eyeNow == null || tgtNow == null)
            return;

        var t = tAccum;

        // --- 描画 ---
        // 屋外デーゲーム: 高い太陽 + 空色の環境光
        renNow.light.dir = new Vec3(0.35f, 1.0f, -0.25f);
        renNow.light.intensity = 1.3f;
        renNow.light.color = Color.rgb(1.0f, 0.98f, 0.92f);
        renNow.sky.top = Color.rgb(0.55f, 0.65f, 0.80f);
        renNow.sky.bottom = Color.rgb(0.22f, 0.28f, 0.20f);
        renNow.sky.intensity = 0.55f;
        renNow.background = Color.rgb(0.50f, 0.68f, 0.87f);
        // 影はカメラターゲット周辺 (フィールド全体 100m は 1 枚に入れない)
        renNow.shadow.center = new Vec3(tgtNow.x, 0, tgtNow.z);
        renNow.shadow.extent = 30.0f;
        renNow.begin(new Camera
        {
            eye = eyeNow,
            target = tgtNow,
            fov = camFov,
            near = 0.1f,
            far = 400.0f,
        });

        renNow.draw(fieldMesh, new Mat4());

        // 野手 (守備側チーム色)
        var ft = fieldingTeam();
        for (int i = 0; i < 9; i++)
        {
            var f = fs[i];
            var pose = poseFor(f.anim,
                f.anim == AN_WINDUP || f.anim == AN_THROW || f.anim == AN_REACH
                    ? f.animT
                    : t,
                f.runPhase);
            var yaw = f.anim == AN_RUN
                ? f.yaw
                : (float)Math.Atan2(0 - f.x, 0 - f.z); // 待機中は本塁を向く
            if (i == 0)
                yaw = (float)Math.PI; // 投手は打者へ正対
            if (i == 1)
                yaw = 0; // 捕手は投手へ
            drawChar(f.x, f.z, f.anim == AN_RUN ? f.yaw : yaw, ft, pose);
        }
        // 打者 (攻撃側チーム色)。走者に切り替わっていない間だけ打席に立つ
        var bt = battingTeam();
        var b = batter;
        if (batterRunner == null && b != null)
        {
            // 構え = スイングの溜め位相を静止で使う (バットの持ち手と一致する)
            var stance = b.anim == AN_SWING ? b.animT : 0.30f;
            var inSwingPose = b.anim == AN_SWING
                || state == ST_PREPITCH
                || state == ST_WINDUP
                || state == ST_PITCH
                || state == ST_CALL;
            drawChar(b.x, b.z, (float)Math.PI / 2, bt,
                inSwingPose ? poseSwing(stance) : poseIdle(t));
            // バット
            if (state == ST_PREPITCH || state == ST_WINDUP || state == ST_PITCH
                || state == ST_CALL || b.anim == AN_SWING)
                renNow.draw(batMesh, batMatrix(stance));
        }
        // 走者 (塁上で止まっているときは待機ポーズ)
        var rns = runners;
        if (rns != null)
        {
            foreach (var r in rns)
            {
                var np = basePos(r.to == 4 ? 0 : r.to);
                var moving = r.atBase != r.to;
                drawChar(r.x, r.z,
                    moving
                        ? (float)Math.Atan2(np[0] - r.x, np[1] - r.z)
                        : (float)Math.Atan2(-r.x, -r.z),
                    bt, moving ? poseRun(r.runPhase) : poseIdle(t));
            }
        }

        // ボール
        if (ballVisible)
            renNow.draw(ballMesh, Mat4.translate(new Vec3(bx, by, bz)));

        renNow.End();

        // HUD は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.begin_pass(new PassOpts { target = Gfx.main_tex, load = Gfx.LOAD });
        drawHud();
        Gfx.end_pass();
    }
}
