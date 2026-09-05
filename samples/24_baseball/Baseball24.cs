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
using static Lub;

// 走者。塁 index は 0=本塁(打席) 1..3=各塁 4=生還
public class Runner
{
    public double X;
    public double Z;
    public int AtBase; // 今いる/直前の塁 (Haxe 版の base。C# 予約語のため改名)
    public int To; // 目標の塁
    public double RunPhase = 0;

    public Runner(double x, double z, int atBase, int to)
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
    public double X;
    public double Z;
    public double HomeX;
    public double HomeZ;
    public double Yaw = 0;
    public double RunPhase = 0;
    public int Anim = 0; // Baseball24.AN_*
    public double AnimT = 0;

    public Fielder(double hx, double hz)
    {
        HomeX = hx;
        HomeZ = hz;
        X = hx;
        Z = hz;
    }
}

// predictLanding の結果 (Haxe 版の匿名構造体を class に)
public class Landing
{
    public double X;
    public double Z;
    public double T;
    public double Peak;
}

public static class Baseball24
{
    const int w = 960;
    const int h = 540;
    const double dt = 1.0 / 60.0;

    // --- フィールド寸法 (m) -------------------------------------------------
    const double baseD = 19.4; // 塁間 27.43m の対角成分
    const double moundZ = 18.44;
    const double fenceR = 76.0;
    const double fenceH = 3.0;
    const double grav = 9.8;
    const double ballR = 0.115;
    const double dragCoef = 0.0055; // 打球の空気抵抗 (終端速度 ~42m/s)
    const double runSpd = 7.2; // 走者/野手の走速
    const double catchR = 1.15; // 捕球半径 (グラブの届く範囲)

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
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
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

    static double Rnd()
    {
        var r = rng;
        if (r == null)
        {
            r = new Rand(0x0B5EBA11);
            rng = r;
        }
        return r.NextFloat();
    }

    static double Rrange(double a, double b)
    {
        return a + (b - a) * Rnd();
    }

    // --- キャラメッシュ (SDF + bones) ---------------------------------------
    // 身長 ~1.8m。ユニフォームをチーム色で焼いた 2 メッシュを使い分ける
    static List<Mesh3d>? charMesh = null;
    static int[] teamRgb = new int[] { 0xD94038, 0x4073E0 };

    const double torsoPx = 0.0;
    const double torsoPy = 0.95;
    const double headPy = 1.50;
    const double armPx = 0.24;
    const double armPy = 1.40;
    const double legPx = 0.10;
    const double legPy = 0.92;

    static SdfNode CharModel(int jersey)
    {
        var white = jersey;
        var skin = 0xF5C29A;
        var pants = 0x3A3E4C;
        var torso = Sdf.Capsule(new Vec3(0, 0.92, 0), new Vec3(0, 1.42, 0), 0.19)
            .Paint(white)
            .Bone("torso", new Vec3(torsoPx, torsoPy, 0));
        var head = Sdf.Sphere(0.15)
            .Move(0, 1.62, 0)
            .Paint(skin)
            .Smin(Sdf.Sphere(0.115).Move(0, 1.72, 0).Paint(white), 0.03)
            .Smin(Sdf.Sphere(0.035).Move(0, 1.60, 0.15).Paint(skin), 0.02)
            .Bone("head", new Vec3(0, headPy, 0));
        var armL = Sdf.Capsule(new Vec3(0.24, 1.40, 0),
            new Vec3(0.31, 1.00, 0.05), 0.065)
            .Paint(skin)
            .Bone("arm_l", new Vec3(armPx, armPy, 0));
        var armR = Sdf.Capsule(new Vec3(-0.24, 1.40, 0),
            new Vec3(-0.31, 1.00, 0.05), 0.065)
            .Paint(skin)
            .Bone("arm_r", new Vec3(-armPx, armPy, 0));
        var legL = Sdf.Capsule(new Vec3(0.10, 0.92, 0),
            new Vec3(0.11, 0.10, 0), 0.085)
            .Smin(Sdf.Sphere(0.07).Move(0.11, 0.07, 0.07), 0.05)
            .Paint(pants)
            .Bone("leg_l", new Vec3(legPx, legPy, 0));
        var legR = Sdf.Capsule(new Vec3(-0.10, 0.92, 0),
            new Vec3(-0.11, 0.10, 0), 0.085)
            .Smin(Sdf.Sphere(0.07).Move(-0.11, 0.07, 0.07), 0.05)
            .Paint(pants)
            .Bone("leg_r", new Vec3(-legPx, legPy, 0));
        return torso.Smin(head, 0.05).Smin(armL, 0.04).Smin(armR, 0.04)
            .Smin(legL, 0.05).Smin(legR, 0.05);
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
            cm[t].Rebuild(Sdf.Mesh(CharModel(teamRgb[t]), 56));
        }
    }

    // --- ポーズ → ボーン行列 ------------------------------------------------
    // torso が親、head/arms が子、legs は独立。回転はすべて pivot 回り。
    // Haxe 版の boneSlot 表 + 手詰めは Bones.pack (mesh.bones 順の resolve) に
    // 置き換え。pose: [twist, lean, tilt, toy, hx, hy, alx, alz, arx, arz,
    // llx, lrx]
    static List<double> PackBones(List<double> p)
    {
        var rTorso = Mat4.RotateY(p[0]) * (Mat4.RotateX(p[1]) * Mat4.RotateZ(p[2]));
        var mTorso = Mat4.Translate(new Vec3(0, p[3], 0))
            * Bones.PivotRot(torsoPx, torsoPy, 0, rTorso);
        var mHead = mTorso
            * Bones.PivotRot(0, headPy, 0, Mat4.RotateY(p[5]) * Mat4.RotateX(p[4]));
        var mArmL = mTorso
            * Bones.PivotRot(armPx, armPy, 0, Mat4.RotateZ(p[7]) * Mat4.RotateX(p[6]));
        var mArmR = mTorso
            * Bones.PivotRot(-armPx, armPy, 0, Mat4.RotateZ(p[9]) * Mat4.RotateX(p[8]));
        var mLegL = Bones.PivotRot(legPx, legPy, 0, Mat4.RotateX(p[10]));
        var mLegR = Bones.PivotRot(-legPx, legPy, 0, Mat4.RotateX(p[11]));
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

    static List<double> ZeroPose()
    {
        return new List<double> { 0, 0, 0, 0, 0, 0, 0, -0.08, 0, 0.08, 0, 0 };
    }

    // クリップ。桜井メソッド: 構え/攻撃ポーズは極端に、中割りはほぼゼロ
    static List<double> PoseIdle(double t)
    {
        var p = ZeroPose();
        var b = Math.Sin(t * 2.1);
        p[1] = 0.04 + b * 0.015;
        p[7] = 0.10 + b * 0.02;
        p[9] = -0.10 - b * 0.02;
        return p;
    }

    static List<double> PoseReady(double t)
    {
        var p = ZeroPose();
        p[1] = 0.42; // 前傾
        p[3] = -0.08;
        p[4] = -0.35; // 顔は上げる
        p[6] = 0.85;
        p[8] = 0.85; // 両腕前
        p[7] = 0.35;
        p[9] = -0.35;
        p[10] = 0.25;
        p[11] = -0.25;
        return p;
    }

    static List<double> PoseCrouch(double t)
    {
        var p = ZeroPose();
        p[3] = -0.30;
        p[1] = 0.38;
        p[4] = -0.55;
        p[6] = 1.05;
        p[8] = 1.05; // 両腕前
        p[10] = 1.05;
        p[11] = 1.05;
        return p;
    }

    static List<double> PoseRun(double ph)
    {
        var p = ZeroPose();
        var s = Math.Sin(ph);
        p[1] = 0.30;
        p[10] = s * 1.0;
        p[11] = -s * 1.0;
        p[6] = -s * 0.9;
        p[8] = s * 0.9;
        p[3] = Math.Abs(Math.Cos(ph)) * 0.04;
        return p;
    }

    // 投球。ph 0..1、リリースは REL_PH
    public const double RelPh = 0.60;

    static List<double> PoseWindup(double ph)
    {
        var p = ZeroPose();
        // 1) 振りかぶり + 足上げ (前 = +Z = rotateX 正)
        var k1 = MathUtil.Smoothstep(0.0, 0.34, ph);
        p[6] = -2.1 * k1;
        p[8] = -2.1 * k1;
        p[10] = 1.35 * k1;
        p[1] = -0.28 * k1;
        // 2) 踏み込み + 腕を極端に引き絞る
        var k2 = MathUtil.Smoothstep(0.38, 0.54, ph);
        p[10] = MathUtil.Lerp(p[10], 0.55, k2);
        p[8] = MathUtil.Lerp(p[8], -2.95, k2); // 右腕を頭の後ろまで
        p[6] = MathUtil.Lerp(p[6], -0.4, k2);
        p[1] = MathUtil.Lerp(p[1], -0.38, k2);
        // 3) リリース: 一気に振り抜く (中割りなし)
        var k3 = MathUtil.Smoothstep(0.56, 0.62, ph);
        p[8] = MathUtil.Lerp(p[8], 0.9, k3);
        p[1] = MathUtil.Lerp(p[1], 0.62, k3);
        p[0] = -0.45 * k3;
        p[10] = MathUtil.Lerp(p[10], 0.35, k3);
        p[11] = -0.3 * k3;
        // 4) フォロースルーの余韻
        var k4 = MathUtil.Smoothstep(0.66, 1.0, ph);
        p[8] = MathUtil.Lerp(p[8], 0.5, k4);
        p[1] = MathUtil.Lerp(p[1], 0.45, k4);
        return p;
    }

    // スイング。ph 0..1、ミートは SWING_HIT_PH
    public const double SwingHitPh = 0.52;

    static List<double> PoseSwing(double ph)
    {
        var p = ZeroPose();
        p[5] = 0.9; // 顔は投手へ
        p[4] = -0.15;
        // 1) 溜め: 捕手側へ捻る
        var k1 = MathUtil.Smoothstep(0.0, 0.40, ph);
        p[0] = -0.55 * k1;
        p[6] = -1.5 * k1;
        p[8] = -1.7 * k1;
        p[7] = 0.9 * k1;
        p[9] = -0.4 * k1;
        p[10] = -0.35 * k1;
        // 2) 爆発: 1-2 フレームで振り抜く
        var k2 = MathUtil.Smoothstep(0.47, 0.54, ph);
        p[0] = MathUtil.Lerp(p[0], 1.55, k2);
        p[6] = MathUtil.Lerp(p[6], 0.6, k2);
        p[8] = MathUtil.Lerp(p[8], 0.6, k2);
        p[7] = MathUtil.Lerp(p[7], 0.3, k2);
        p[9] = MathUtil.Lerp(p[9], -1.1, k2);
        p[1] = 0.12 * k2;
        p[10] = MathUtil.Lerp(p[10], 0.4, k2);
        p[11] = -0.5 * k2;
        // 3) フォロースルー: ウェイト破綻気味に大きく
        var k3 = MathUtil.Smoothstep(0.6, 1.0, ph);
        p[0] = MathUtil.Lerp(p[0], 1.85, k3);
        p[4] = MathUtil.Lerp(p[4], -0.3, k3);
        return p;
    }

    static List<double> PoseReach(double t)
    {
        var p = ZeroPose();
        p[6] = 2.9;
        p[8] = 2.9;
        p[7] = 0.25;
        p[9] = -0.25;
        p[4] = -0.8;
        return p;
    }

    static List<double> PoseThrow(double ph)
    {
        var p = ZeroPose();
        var k1 = MathUtil.Smoothstep(0.0, 0.4, ph);
        p[8] = -2.6 * k1;
        p[0] = -0.4 * k1;
        var k2 = MathUtil.Smoothstep(0.45, 0.58, ph);
        p[8] = MathUtil.Lerp(p[8], 0.8, k2);
        p[0] = MathUtil.Lerp(p[0], 0.35, k2);
        p[1] = 0.35 * k2;
        return p;
    }

    static List<double> PoseFor(int anim, double t, double runPhase)
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

    static void Fan(List<double> dst, double cx, double cy, double cz,
        double r, double a0, double a1, int segs, List<double> col)
    {
        for (int i = 0; i < segs; i++)
        {
            var t0 = a0 + (a1 - a0) * i / segs;
            var t1 = a0 + (a1 - a0) * (i + 1) / segs;
            Shapes.Tri(dst, new List<double> { cx, cy, cz },
                new List<double>
                    { cx + Math.Sin(t0) * r, cy, cz + Math.Cos(t0) * r },
                new List<double>
                    { cx + Math.Sin(t1) * r, cy, cz + Math.Cos(t1) * r },
                new List<double> { 0, 1, 0 }, col);
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

        var v = new List<double>();
        var grass = new List<double> { 0.24, 0.47, 0.24, 1.0 };
        var grassIn = new List<double> { 0.28, 0.54, 0.27, 1.0 };
        var dirt = new List<double> { 0.63, 0.46, 0.31, 1.0 };
        var lineW = new List<double> { 0.95, 0.95, 0.92, 1.0 };
        var wall = new List<double> { 0.26, 0.42, 0.58, 1.0 };
        var wallTop = new List<double> { 0.88, 0.82, 0.35, 1.0 };
        var up = new List<double> { 0, 1, 0 };

        // 地面 (ファウルグラウンド込みの外周)
        Shapes.Quad(v, new List<double> { -95, 0, -20 },
            new List<double> { -95, 0, 95 }, new List<double> { 95, 0, 95 },
            new List<double> { 95, 0, -20 }, up, grass);
        // フェアグラウンドの扇形 (少し明るい緑)
        Fan(v, 0, 0.012, 0, fenceR, -Math.PI / 4, Math.PI / 4, 24, grassIn);
        // 内野ダート (ひし形)
        Shapes.Quad(v, new List<double> { 0, 0.024, -2.2 },
            new List<double> { 24.5, 0.024, baseD },
            new List<double> { 0, 0.024, 43.0 },
            new List<double> { -24.5, 0.024, baseD }, up, dirt);
        // 内野の芝 (ダートの内側)
        Shapes.Quad(v, new List<double> { 0, 0.036, 4.2 },
            new List<double> { 15.5, 0.036, baseD },
            new List<double> { 0, 0.036, 34.6 },
            new List<double> { -15.5, 0.036, baseD }, up, grassIn);
        // マウンド (つぶれたドーム + ダート円)
        Fan(v, 0, 0.048, moundZ, 2.9, -Math.PI, Math.PI, 16, dirt);
        Shapes.Sphere(v, 0, -2.35, moundZ, 2.6, dirt, 8, 16);
        // 本塁と各塁
        Shapes.Box(v, 0, 0.03, 0, 0.55, 0.05, 0.55, lineW);
        Shapes.Box(v, baseD, 0.07, baseD, 0.55, 0.13, 0.55, lineW);
        Shapes.Box(v, 0, 0.07, baseD * 2, 0.55, 0.13, 0.55, lineW);
        Shapes.Box(v, -baseD, 0.07, baseD, 0.55, 0.13, 0.55, lineW);
        // プレート (マウンド上)
        Shapes.Box(v, 0, 0.30, moundZ, 0.61, 0.05, 0.15, lineW);
        // ファウルライン
        var d = 0.70710678;
        foreach (var s in new List<double> { -1.0, 1.0 })
        {
            var nx = -s * d; // ライン直交方向
            var nz = d;
            var half = 0.09;
            var x0 = s * 1.2 * d;
            var z0 = 1.2 * d;
            var x1 = s * (fenceR - 0.6) * d;
            var z1 = (fenceR - 0.6) * d;
            Shapes.Quad(v,
                new List<double> { x0 - nx * half, 0.045, z0 - nz * half },
                new List<double> { x1 - nx * half, 0.045, z1 - nz * half },
                new List<double> { x1 + nx * half, 0.045, z1 + nz * half },
                new List<double> { x0 + nx * half, 0.045, z0 + nz * half },
                up, lineW);
        }
        // 外野フェンス (内向きの壁 + 黄色いトップ)
        int segs = 26;
        for (int i = 0; i < segs; i++)
        {
            var a0 = -Math.PI / 4 + Math.PI / 2 * i / segs;
            var a1 = -Math.PI / 4 + Math.PI / 2 * (i + 1) / segs;
            var x0 = Math.Sin(a0) * fenceR;
            var z0 = Math.Cos(a0) * fenceR;
            var x1 = Math.Sin(a1) * fenceR;
            var z1 = Math.Cos(a1) * fenceR;
            var am = (a0 + a1) * 0.5;
            var n = new List<double> { -Math.Sin(am), 0, -Math.Cos(am) };
            Shapes.Quad(v, new List<double> { x0, 0, z0 },
                new List<double> { x0, fenceH, z0 },
                new List<double> { x1, fenceH, z1 },
                new List<double> { x1, 0, z1 }, n, wall);
            Shapes.Quad(v, new List<double> { x0, fenceH, z0 },
                new List<double> { x0, fenceH + 0.18, z0 },
                new List<double> { x1, fenceH + 0.18, z1 },
                new List<double> { x1, fenceH, z1 }, n, wallTop);
        }
        // バックストップ (本塁後方の低い壁)
        int bsegs = 10;
        var bsCol = new List<double> { 0.48, 0.51, 0.55, 1.0 };
        for (int i = 0; i < bsegs; i++)
        {
            var a0 = Math.PI * 0.75 + Math.PI * 0.5 * i / bsegs;
            var a1 = Math.PI * 0.75 + Math.PI * 0.5 * (i + 1) / bsegs;
            var r = 11.5;
            var x0 = Math.Sin(a0) * r;
            var z0 = Math.Cos(a0) * r;
            var x1 = Math.Sin(a1) * r;
            var z1 = Math.Cos(a1) * r;
            var am = (a0 + a1) * 0.5;
            var n = new List<double> { -Math.Sin(am), 0, -Math.Cos(am) };
            Shapes.Quad(v, new List<double> { x0, 0, z0 },
                new List<double> { x0, 1.6, z0 },
                new List<double> { x1, 1.6, z1 },
                new List<double> { x1, 0, z1 }, n, bsCol);
        }
        fm.Rebuild(Shapes3d.FromInterleaved(v));

        var ballVerts = new List<double>();
        Shapes.Sphere(ballVerts, 0, 0, 0, ballR,
            new List<double> { 0.96, 0.96, 0.94, 1.0 }, 8, 12);
        bm.Rebuild(Shapes3d.FromInterleaved(ballVerts));

        var batVerts = new List<double>();
        Shapes.Box(batVerts, 0, 0, 0.44, 0.075, 0.075, 0.88,
            new List<double> { 0.85, 0.66, 0.40, 1.0 });
        btm.Rebuild(Shapes3d.FromInterleaved(batVerts));
    }

    // --- ボール ---------------------------------------------------------------
    static double bx = 0.0;
    static double by = 0.0;
    static double bz = 0.0;
    static double bvx = 0.0;
    static double bvy = 0.0;
    static double bvz = 0.0;
    static bool ballVisible = false;
    static int ballBounces = 0;
    static bool ballRolling = false;
    static bool isHomeRun = false;

    // 打球の 1 step (共通 integrator)。返り値: バウンドしたか
    static bool StepBall(double dt, bool drag)
    {
        if (drag)
        {
            var sp = Math.Sqrt(bvx * bvx + bvy * bvy + bvz * bvz);
            var f = 1.0 / (1.0 + dragCoef * sp * dt);
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
            if (Math.Abs(bvy) < 1.0)
            {
                ballRolling = true;
                bvy = 0;
            }
            else
            {
                bvy = -bvy * 0.42;
                bounced = true;
                ballBounces++;
            }
            bvx *= 0.72;
            bvz *= 0.72;
        }
        if (ballRolling)
        {
            by = ballR;
            bvy = 0;
            var sp = Math.Sqrt(bvx * bvx + bvz * bvz);
            if (sp > 0)
            {
                var dec = Math.Max(0.0, sp - 2.6 * dt);
                bvx *= dec / sp;
                bvz *= dec / sp;
            }
        }
        // フェンス (フェア扇形内の円筒壁)。越えたら本塁打
        var hr = Math.Sqrt(bx * bx + bz * bz);
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
                    bvx -= 1.4 * vr * nx;
                    bvz -= 1.4 * vr * nz;
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
        var t = 0.0;
        var peak = by;
        while (t < 12.0)
        {
            StepBall(dt, true);
            t += dt;
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

    static List<List<double>> FielderHomes()
    {
        return new List<List<double>>
        {
            new List<double> { 0.0, moundZ }, // P
            new List<double> { 0.0, -2.4 }, // C
            new List<double> { 21.0, 18.5 }, // 1B
            new List<double> { 11.0, 31.0 }, // 2B
            new List<double> { -21.0, 18.5 }, // 3B
            new List<double> { -11.0, 31.0 }, // SS
            new List<double> { -27.0, 56.0 }, // LF
            new List<double> { 0.0, 63.0 }, // CF
            new List<double> { 27.0, 56.0 }, // RF
        };
    }

    static List<double> BasePos(int i)
    {
        if (i == 1) return new List<double> { baseD, baseD };
        if (i == 2) return new List<double> { 0.0, baseD * 2 };
        if (i == 3) return new List<double> { -baseD, baseD };
        return new List<double> { 0.0, 0.0 }; // 0 と 4 は本塁
    }

    static void ResetActors()
    {
        var fs = new List<Fielder>();
        foreach (var h in FielderHomes())
            fs.Add(new Fielder(h[0], h[1]));
        fielders = fs;
        batter = new Fielder(-0.85, 0.0);
        runners = new List<Runner>();
    }

    // --- 試合状態 ---------------------------------------------------------------
    static int state = stIntro;
    static double stateT = 0.0;
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
    static double pitchTX = 0.0; // 到達点 (x, y)
    static double pitchTY = 0.0;
    static bool pitchInZone = false;
    static bool willSwing = false;
    static int swingOutcome = 0; // 0=空振り 1=ファウル 2=インプレー
    static double exitSpeed = 0.0;
    static double exitLaunch = 0.0;
    static double exitSpray = 0.0;
    static bool swingStarted = false;

    // ST_LIVE の進行
    static int playPhase = plFly;
    static int chaser = -1;
    static int ballHeldBy = -1; // 野手 index (-1 = フリー)
    static double liveT = 0.0;
    static double throwT = 0.0;
    static double throwDur = 0.0;
    static double throwFromX = 0.0;
    static double throwFromY = 0.0;
    static double throwFromZ = 0.0;
    static Runner? batterRunner = null;
    static Landing? landing = null;

    // 演出
    static double hitstopT = 0.0;
    static double shakeAmp = 0.0;
    static string eventText = "";
    static double eventT = 99.0;
    static Color? eventCol = null;
    static double tAccum = 0.0;

    static void ShowEvent(string s, Color? c)
    {
        eventText = s;
        eventT = 0.0;
        eventCol = c ?? Color.Rgb(1.0, 0.98, 0.9);
    }

    static void SetState(int s)
    {
        state = s;
        stateT = 0.0;
    }

    // --- 投球開始 -----------------------------------------------------------------
    static void StartPitch()
    {
        var fs = fielders;
        if (fs == null)
            return;
        // 目標: ゾーン内/外を先に決めてから座標を出す
        pitchInZone = Rnd() < 0.62;
        if (pitchInZone)
        {
            pitchTX = Rrange(-0.20, 0.20);
            pitchTY = Rrange(0.60, 1.10);
        }
        else
        {
            // ゾーンの少し外
            if (Rnd() < 0.5)
            {
                pitchTX = (Rnd() < 0.5 ? -1.0 : 1.0) * Rrange(0.28, 0.45);
                pitchTY = Rrange(0.45, 1.25);
            }
            else
            {
                pitchTX = Rrange(-0.35, 0.35);
                pitchTY = Rnd() < 0.5 ? Rrange(0.15, 0.42) : Rrange(1.28, 1.55);
            }
        }
        willSwing = Rnd() < (pitchInZone ? 0.80 : 0.26);
        if (willSwing)
        {
            var r = Rnd();
            if (r < 0.24)
                swingOutcome = 0;
            else if (r < 0.58)
                swingOutcome = 1;
            else
            {
                swingOutcome = 2;
                exitSpeed = 23.0 + 23.0 * Math.Pow(Rnd(), 0.7);
                exitLaunch = Rrange(-6.0, 42.0);
                exitSpray = Rrange(-38.0, 38.0);
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
        bx = 0.35;
        by = 1.9;
        bz = moundZ - 0.55;
        var speed = Rrange(31.0, 40.0);
        var dz = 0.42 - bz;
        var t = Math.Abs(dz) / speed;
        bvx = (pitchTX - bx) / t;
        bvy = (pitchTY - by) / t + 0.5 * grav * t;
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
        hitstopT = 0.09;
        shakeAmp = 0.5;
        var launch = exitLaunch;
        var spray = exitSpray;
        var speed = exitSpeed;
        if (swingOutcome == 1)
        {
            // ファウル: 打球はラインの外か後方へ
            speed = Rrange(16.0, 34.0);
            launch = Rrange(15.0, 70.0);
            spray = (Rnd() < 0.5 ? -1.0 : 1.0) * Rrange(50.0, 130.0);
        }
        var la = MathUtil.Radians(launch);
        var sa = MathUtil.Radians(spray);
        bx = 0.0;
        by = 1.0;
        bz = 0.35;
        bvx = speed * Math.Cos(la) * Math.Sin(sa);
        bvy = speed * Math.Sin(la);
        bvz = speed * Math.Cos(la) * Math.Cos(sa);
        ballBounces = 0;
        ballRolling = false;
        isHomeRun = false;
        var land = PredictLanding();
        landing = land;
        liveT = 0.0;
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

    static int NearestFielder(double x, double z)
    {
        var fs = fielders;
        if (fs == null)
            return -1;
        var best = -1;
        var bd = 1e9;
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
            ShowEvent("STRIKE OUT!", Color.Rgb(1.0, 0.5, 0.3));
            outs++;
            newBatterPending = true;
        }
        else if (balls >= 4)
        {
            ShowEvent("WALK", Color.Rgb(0.5, 0.9, 1.0));
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
    static bool MoveTowards(Fielder f, double tx, double tz, double dt,
        double spd)
    {
        var dx = tx - f.X;
        var dz = tz - f.Z;
        var d = Math.Sqrt(dx * dx + dz * dz);
        if (d < 0.15)
        {
            if (f.Anim == AnRun)
                f.Anim = AnReady;
            return true;
        }
        var mv = Math.Min(d, spd * dt);
        f.X += dx / d * mv;
        f.Z += dz / d * mv;
        f.Yaw = Math.Atan2(dx, dz);
        f.Anim = AnRun;
        f.RunPhase += dt * 11.0;
        return false;
    }

    static void UpdateLive(double dt)
    {
        var fs = fielders;
        var rns = runners;
        if (fs == null || rns == null)
            return;
        liveT += dt;
        StepBall(dt, true);

        if (playPhase == plFoul)
        {
            if (liveT > 1.25)
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
            ShowEvent("HOME RUN!", Color.Rgb(1.0, 0.85, 0.25));
            shakeAmp = 0.35;
            foreach (var r in rns)
                r.To = 4;
            playPhase = plSettle;
        }

        // 走者更新
        UpdateRunners(dt, 1.0);

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
                var tx = flying && land != null ? land.X : bx + bvx * 0.35;
                var tz = flying && land != null ? land.Z : bz + bvz * 0.35;
                var arrived = MoveTowards(f, tx, tz, dt, runSpd);
                var dx = f.X - bx;
                var dz = f.Z - bz;
                var dist = Math.Sqrt(dx * dx + dz * dz);
                if (flying && by < 2.6 && bvy < 0 && dist < catchR)
                {
                    // ノーバウンド捕球 → アウト
                    FielderCaught(i, true);
                }
                else if ((ballBounces > 0 || ballRolling)
                    && dist < catchR * 0.8 && by < 1.2)
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
                    MoveTowards(f, baseD - 0.4, baseD - 0.4, dt, runSpd);
            }
            else if (i != chaser)
            {
                MoveTowards(f, f.HomeX, f.HomeZ, dt, runSpd * 0.8);
            }
        }

        // 一塁送球の到達判定
        if (playPhase == plThrow1b)
        {
            throwT += dt;
            var k = Math.Min(1.0, throwT / throwDur);
            // 送球は放物線 (見た目用に手計算)
            bx = MathUtil.Lerp(throwFromX, baseD, k);
            bz = MathUtil.Lerp(throwFromZ, baseD, k);
            by = MathUtil.Lerp(throwFromY, 1.2, k) + Math.Sin(k * Math.PI) * 1.4;
            if (k >= 1.0)
            {
                // 封殺 or セーフ: 走者の進塁具合と競争
                var br = batterRunner;
                if (br != null && br.AtBase < 1)
                {
                    outs++;
                    ShowEvent("OUT!", Color.Rgb(1.0, 0.5, 0.3));
                    rns.Remove(br);
                    // 他の走者は 1 つ進む
                    foreach (var r in rns)
                        if (r.To < 3)
                            r.To++;
                }
                else
                {
                    ShowEvent("SAFE!", Color.Rgb(0.5, 1.0, 0.6));
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
            if (settled && liveT > 1.0)
            {
                ballVisible = false;
                newBatterPending = true;
                SetState(stCall);
            }
        }
        // 保険: 異常に長引いたら打ち切り
        if (liveT > 14.0)
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
            ShowEvent("CAUGHT!", Color.Rgb(1.0, 0.6, 0.3));
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
        var gatherDist = Math.Sqrt(f.X * f.X + f.Z * f.Z);
        var brg = batterRunner;
        if (brg != null && brg.AtBase < 1 && gatherDist < 34)
        {
            f.Anim = AnThrow;
            f.AnimT = 0;
            playPhase = plThrow1b;
            throwFromX = bx;
            throwFromY = Math.Max(by, 1.3);
            throwFromZ = bz;
            var d = Math.Sqrt((baseD - bx) * (baseD - bx)
                + (baseD - bz) * (baseD - bz));
            throwDur = Math.Max(0.25, d / 30.0);
            throwT = 0.0;
            return;
        }
        // ヒット: 深さと経過時間で進塁数を決める
        var bases = 1;
        if (gatherDist > 62 || liveT > 4.6)
            bases = 2;
        if (gatherDist > 72 && liveT > 5.5)
            bases = 3;
        ShowEvent(bases == 1 ? "HIT!" : bases == 2 ? "DOUBLE!" : "TRIPLE!",
            Color.Rgb(0.55, 1.0, 0.6));
        foreach (var r in rns)
            r.To = r == batterRunner ? bases : Math.Min(4, r.AtBase + bases);
        batterRunner = null;
        ballVisible = false;
        playPhase = plSettle;
    }

    static void UpdateRunners(double dt, double spdScale)
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
                var d = Math.Sqrt(dx * dx + dz * dz);
                var mv = runSpd * spdScale * dt;
                if (d <= mv)
                {
                    r.X = np[0];
                    r.Z = np[1];
                    r.AtBase = nextBase;
                    if (nextBase >= 4)
                    {
                        score[BattingTeam()] = score[BattingTeam()] + 1;
                        ShowEvent("RUN SCORED!", Color.Rgb(1.0, 0.9, 0.4));
                        rns.RemoveAt(i);
                    }
                }
                else
                {
                    r.X += dx / d * mv;
                    r.Z += dz / d * mv;
                }
                r.RunPhase += dt * 11.0;
            }
            else if (r.AtBase > r.To)
            {
                // 帰塁
                var np = BasePos(r.To);
                var dx = np[0] - r.X;
                var dz = np[1] - r.Z;
                var d = Math.Sqrt(dx * dx + dz * dz);
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
                r.RunPhase += dt * 11.0;
            }
            i--;
        }
    }

    // --- state machine 本体 ----------------------------------------------------------
    static void UpdateGame(double dt)
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
            if (stateT > 1.8)
            {
                ShowEvent("PLAY BALL!", Color.Rgb(1.0, 0.95, 0.5));
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
                    MoveTowards(f, f.HomeX, f.HomeZ, dt, runSpd * 0.8);
            }
            UpdateRunners(dt, 1.0);
            if (newBatterPending)
            {
                b.X = -0.85;
                b.Z = 0.0;
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
            if (stateT > 1.05 && (settled || stateT > 6.0))
            {
                if (outs >= 3)
                {
                    SetState(stChange);
                    ShowEvent("CHANGE", Color.Rgb(0.9, 0.9, 0.95));
                }
                else
                {
                    StartPitch();
                }
            }
        }
        else if (state == stWindup)
        {
            fs[0].AnimT += dt / 1.1;
            if (fs[0].AnimT >= RelPh)
                ReleaseBall();
        }
        else if (state == stPitch)
        {
            fs[0].AnimT = Math.Min(1.0, fs[0].AnimT + dt / 1.1);
            // 投球は無抵抗の放物線 (短距離なので誤差は無視できる)
            bvy -= grav * dt;
            bx += bvx * dt;
            by += bvy * dt;
            bz += bvz * dt;
            // スイング開始タイミング (ミートの瞬間に SWING_HIT_PH が来るよう逆算)
            var tToPlate = bvz != 0 ? (0.42 - bz) / bvz : 0.0;
            if (willSwing && !swingStarted && tToPlate < SwingHitPh * 0.55)
            {
                b.Anim = AnSwing;
                b.AnimT = 0;
                swingStarted = true;
            }
            if (b.Anim == AnSwing)
                b.AnimT = Math.Min(1.0, b.AnimT + dt / 0.55);
            if (bz <= 0.42)
                ResolveContact();
        }
        else if (state == stLive)
        {
            if (b.Anim == AnSwing)
            {
                b.AnimT = Math.Min(1.0, b.AnimT + dt / 0.55);
                if (b.AnimT >= 1.0)
                    b.Anim = AnIdle;
            }
            foreach (var f in fs)
                if (f.Anim == AnThrow || f.Anim == AnReach)
                    f.AnimT = Math.Min(1.0, f.AnimT + dt / 0.45);
            UpdateLive(dt);
        }
        else if (state == stCall)
        {
            if (b.Anim == AnSwing)
            {
                b.AnimT = Math.Min(1.0, b.AnimT + dt / 0.55);
                if (b.AnimT >= 1.0)
                    b.Anim = AnIdle;
            }
            UpdateRunners(dt, 1.0);
            if (stateT > 0.95)
                SetState(stPrepitch);
        }
        else if (state == stChange)
        {
            if (stateT > 1.8)
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
                        ShowEvent("DRAW", Color.Rgb(0.9, 0.9, 0.95));
                    else
                    {
                        var w = score[0] > score[1] ? 0 : 1;
                        ShowEvent("GAME SET  " + teamName[w] + " WINS!",
                            Color.Rgb(1.0, 0.9, 0.4));
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
            if (stateT > 5.0)
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
                ShowEvent("PLAY BALL!", Color.Rgb(1.0, 0.95, 0.5));
                SetState(stPrepitch);
            }
        }
    }

    // --- カメラ ---------------------------------------------------------------------
    static Vec3? camEye = null;
    static Vec3? camTarget = null;
    static double camFov = 34.0;
    static bool camCut = false;

    static void UpdateCamera(double dt)
    {
        var eye = camEye;
        var tgt = camTarget;
        if (eye == null || tgt == null)
            return;
        var de = new Vec3(4.6, 3.1, 29.0); // センター後方の中継カメラ
        var dtg = new Vec3(-0.3, 1.1, 1.2);
        var dfov = 30.0;
        if (state == stLive && playPhase != plFoul)
        {
            var land = landing;
            if (land != null && (land.Peak > 7.0 || isHomeRun)
                && (ballBounces == 0 && !ballRolling || isHomeRun))
            {
                // フライ追従: 打球の後方上空から
                var hv = Math.Sqrt(bvx * bvx + bvz * bvz);
                var dirx = hv > 0.5 ? bvx / hv : 0.0;
                var dirz = hv > 0.5 ? bvz / hv : 1.0;
                de = new Vec3(bx - dirx * 13.0,
                    Math.Max(by * 0.55 + 3.5, 2.2), bz - dirz * 13.0);
                dtg = new Vec3(bx + bvx * 0.22, Math.Max(by, 0.5),
                    bz + bvz * 0.22);
                dfov = 42.0;
            }
            else
            {
                // 内野俯瞰
                de = new Vec3(0, 15.0, -14.0);
                dtg = new Vec3(0, 0.0, 20.0);
                dfov = 50.0;
            }
        }
        else if (state == stIntro || state == stChange || state == stEnd)
        {
            var a = tAccum * 0.12;
            de = new Vec3(Math.Sin(a) * 46.0, 17.0, 24.0 + Math.Cos(a) * 30.0);
            dtg = new Vec3(0, 1.0, 22.0);
            dfov = 42.0;
        }
        var k = camCut ? 1.0 : Math.Min(1.0, 7.0 * dt);
        camCut = false;
        eye = eye.Lerp(de, k);
        tgt = tgt.Lerp(dtg, k);
        camFov = MathUtil.Lerp(camFov, dfov, k);
        // 画面振動 (ヒットの手応え)。減衰付きで eye だけ揺らす
        if (shakeAmp > 0.003)
        {
            var s = shakeAmp;
            eye = new Vec3(eye.X + Math.Sin(tAccum * 71.0) * s * 0.25,
                eye.Y + Math.Sin(tAccum * 93.0 + 1.7) * s * 0.2, eye.Z);
            shakeAmp *= Math.Pow(0.001, dt); // ~0.7s で収束
        }
        camEye = eye;
        camTarget = tgt;
    }

    // --- 描画 -----------------------------------------------------------------------
    static bool reloaded = true; // hot reload で true に戻る (19_sdf と同じトリック)
    static FixedStep? step = null;

    static Renderer3d? ren = null;

    static void DrawChar(double x, double z, double yaw, int team,
        List<double> pose)
    {
        var renNow = ren;
        var cm = charMesh;
        if (renNow == null || cm == null)
            return;
        var model = Mat4.Translate(new Vec3(x, 0, z)) * Mat4.RotateY(yaw);
        renNow.Draw(cm[team], model, new Draw3dOpts { Bones = PackBones(pose) });
    }

    // バット。スイング位相から向きを決める (打者ローカル)
    static Mat4 BatMatrix(double ph)
    {
        // 溜め → 一気に振り抜き → フォロー (rotateX は +θ で +Z が下向きに回る)
        var ang = -2.35; // 構え: 後方上
        var tilt = 1.05;
        var k2 = MathUtil.Smoothstep(0.47, 0.56, ph);
        ang = MathUtil.Lerp(ang, 1.15, k2);
        tilt = MathUtil.Lerp(tilt, -0.05, k2);
        var k3 = MathUtil.Smoothstep(0.6, 1.0, ph);
        ang = MathUtil.Lerp(ang, 1.9, k3);
        tilt = MathUtil.Lerp(tilt, 0.45, k3);
        // Haxe 版の変数名 local は Lua キーワードで emit が不正になるため改名
        var batLocal = Mat4.Translate(new Vec3(-0.12, 1.45, -0.15))
            * (Mat4.RotateY(ang) * Mat4.RotateX(tilt));
        var b = batter;
        var px = b != null ? b.X : 0.0;
        var pz = b != null ? b.Z : 0.0;
        return Mat4.Translate(new Vec3(px, 0, pz))
            * (Mat4.RotateY(Math.PI / 2) * batLocal);
    }

    // --- HUD ------------------------------------------------------------------------
    const string fontPath = "samples/24_baseball/data/MPLUS1p-subset.ttf";
    static bool fontLoaded = false;
    static int fontVersion = 0;
    static MeshText? mtext = null;

    static bool EnsureText()
    {
        Io.LoadBytes(fontPath, out var bytes, out var version, out _, out _);
        if (bytes == null)
            return false;
        if (!fontLoaded || fontVersion != version)
        {
            fontLoaded = true;
            fontVersion = version;
            mtext = new MeshText("bb24_text", fontPath, version, w, h);
        }
        return mtext != null;
    }

    static void DrawHud()
    {
        if (!EnsureText())
            return;
        var mt = mtext;
        if (mt == null)
            return;
        var cream = Color.Rgb(0.97, 0.96, 0.9);
        var red = Color.Rgb(1.0, 0.5, 0.45);
        var blue = Color.Rgb(0.55, 0.7, 1.0);
        // スコア (チーム名は各チーム色)
        var sL = teamName[0] + " ";
        var sM = score[0] + " - " + score[1];
        var sR = " " + teamName[1];
        var size = 26.0;
        var total = mt.Width(sL, size) + mt.Width(sM, size)
            + mt.Width(sR, size);
        var x = w * 0.5 - total * 0.5;
        mt.Text(sL, x, 38, size, red);
        mt.Text(sM, x + mt.Width(sL, size), 38, size, cream);
        mt.Text(sR, x + mt.Width(sL, size) + mt.Width(sM, size), 38, size,
            blue);
        // イニングとカウント
        var halfMark = half == 0 ? "TOP" : "BOT";
        mt.TextCentered("INN " + inning + " " + halfMark + "   B" + balls
            + " S" + strikes + " O" + outs, w * 0.5, 64, 15,
            Color.Rgb(0.85, 0.87, 0.9));
        // イベントテキスト (出現時にスケールが弾む)
        if (eventText != "" && eventT < 1.6)
        {
            var ec = eventCol;
            if (ec == null)
                return;
            var pop = 1.0 + 0.6 * Math.Exp(-eventT * 9.0);
            var a = eventT > 1.25 ? 1.0 - (eventT - 1.25) / 0.35 : 1.0;
            var c = Color.Rgb(ec.R, ec.G, ec.B, a);
            mt.TextCentered(eventText, w * 0.5, 190, 52 * pop, c);
        }
    }

    static void SimulateTick()
    {
        tAccum += dt;
        // ヒットストップ: その間シミュレーションだけ止める
        if (hitstopT > 0)
        {
            hitstopT -= dt;
        }
        else
        {
            UpdateGame(dt);
        }
        UpdateCamera(dt);

        var fs = fielders;
        if (fs == null) return;
        // 捕手は基本しゃがみ。捕球リアクションだけ一瞬立つ
        if (fs[1].Anim == AnReach)
        {
            fs[1].AnimT += dt;
            if (fs[1].AnimT > 0.5)
                fs[1].Anim = AnCrouch;
        }
        else
        {
            fs[1].Anim = AnCrouch;
        }
    }

    // --- main loop --------------------------------------------------------------------
    public static void OnFrame(double dt)
    {
        if (reloaded)
        {
            rng = new Rand(0x0B5EBA11);
            ren = new Renderer3d("bb24");
            camEye = new Vec3(5.5, 3.4, 30.0);
            camTarget = new Vec3(0, 1.3, 0);
            camFov = 34.0;
            BuildCharMesh();
            BuildField();
            ResetActors();
            state = stIntro;
            stateT = 0;
            reloaded = false;
            ShowEvent("PLAY BALL!", Color.Rgb(1.0, 0.95, 0.5));
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
        renNow.Light.Dir = new Vec3(0.35, 1.0, -0.25);
        renNow.Light.Intensity = 1.3;
        renNow.Light.Color = Color.Rgb(1.0, 0.98, 0.92);
        renNow.Sky.Top = Color.Rgb(0.55, 0.65, 0.80);
        renNow.Sky.Bottom = Color.Rgb(0.22, 0.28, 0.20);
        renNow.Sky.Intensity = 0.55;
        renNow.Background = Color.Rgb(0.50, 0.68, 0.87);
        // 影はカメラターゲット周辺 (フィールド全体 100m は 1 枚に入れない)
        renNow.Shadow.Center = new Vec3(tgtNow.X, 0, tgtNow.Z);
        renNow.Shadow.Extent = 30.0;
        renNow.Begin(new Camera
        {
            Eye = eyeNow,
            Target = tgtNow,
            Fov = camFov,
            Near = 0.1,
            Far = 400.0,
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
                : Math.Atan2(0 - f.X, 0 - f.Z); // 待機中は本塁を向く
            if (i == 0)
                yaw = Math.PI; // 投手は打者へ正対
            if (i == 1)
                yaw = 0; // 捕手は投手へ
            DrawChar(f.X, f.Z, f.Anim == AnRun ? f.Yaw : yaw, ft, pose);
        }
        // 打者 (攻撃側チーム色)。走者に切り替わっていない間だけ打席に立つ
        var bt = BattingTeam();
        var b = batter;
        if (batterRunner == null && b != null)
        {
            // 構え = スイングの溜め位相を静止で使う (バットの持ち手と一致する)
            var stance = b.Anim == AnSwing ? b.AnimT : 0.30;
            var inSwingPose = b.Anim == AnSwing
                || state == stPrepitch
                || state == stWindup
                || state == stPitch
                || state == stCall;
            DrawChar(b.X, b.Z, Math.PI / 2, bt,
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
                        ? Math.Atan2(np[0] - r.X, np[1] - r.Z)
                        : Math.Atan2(-r.X, -r.Z),
                    bt, moving ? PoseRun(r.RunPhase) : PoseIdle(t));
            }
        }

        // ボール
        if (ballVisible)
            renNow.Draw(ballMesh, Mat4.Translate(new Vec3(bx, by, bz)));

        renNow.End();

        // HUD は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        DrawHud();
        Gfx.EndPass();
    }
}
