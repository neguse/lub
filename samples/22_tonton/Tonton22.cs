// lub の samples/22_tonton の entry。
// 実行: lub samples/22_tonton/Tonton22.csproj (transpile + watch + hot reload)
//
// とんとん相撲: AI 力士同士の紙相撲。人間は土俵をトントンするだけ。
// ゲーム AI の 3 層をひとつの取組で見せるデモ:
// - L0 フィードバック制御: 力士は capsule 1 剛体 + 姿勢 PD (倒立振子)。
//   筋力上限 (balMax) があり、臨界角を超えると本当に倒れる。
//   土俵も自前 PD 懸架の dynamic cylinder で、トントン (クリック地点への
//   下向き impulse) の外乱が接触経由で力士に伝わる。
// - L1 操舵: 相手へ詰める速度サーボ。押し込み中は前進にブレーキを
//   かけない = 相手が消えると突っ込むリスクが物理に乗る。
// - L2 戦術: 押し合いは「相手が支え」の安定構造で、押すだけでは決着
//   しない。引き・いなしで支えを外した瞬間に勝負が動く。個性 (counter
//   確率・前傾の深さ) の違いが取り口の違いになる。
//
// 力士は class Rikishi、screenPos の戻りは class ScreenPos、
// Phys3d の desc 匿名構造体は lub_stub の desc class。end() は End()、
// char() は Char()。Renderer3d / Mesh3d / MeshText は cs-lib の load 順の
// 都合で static 初期化子から呼べないため onFrame で遅延生成する。
// 整数 % は tcs 未対応なので imod (Math.Floor 分解) で書く。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>力士 1 体。</summary>
public class Rikishi
{
    public int Gen; // 再宣言 (respawn) 用 version
    public string Name = ""; // 四股名 (かな)
    public float HomeX;
    public float[] Color = new float[] { 1.0f, 1.0f, 1.0f, 1.0f };
    public int BodyRgb; // SDF に焼く体色
    public int DownFrames;
    public int SquashT; // 着地スカッシュの残りフレーム
    public float PrevVy;
    // --- 個性 ---
    // 押し合いは「相手が支え」の安定構造なので、押すだけでは永遠に倒れない。
    // 決着は支えを外す瞬間 (引き・いなし) に生まれる。個性はその使い分け。
    public float PushK; // 押しの強さ
    public float LeanK; // 前傾の深さ (リスク: 支えを外されると帰れない)
    public float PulseHz; // 押しの脈動周期
    public float Phase;
    public float Pulse; // 脈動の波形 (0..1)。Tick が決め、押し込み力と描画が同じ値を読む
    public float Counter; // 相手の深い前傾に引き/いなしを合わせる確率
    // --- 戦術状態 ---
    public int Tactic;
    public int TacticUntil;
    public float SideSign; // いなしの回り込み方向
}

/// <summary>world 座標 → 論理スクリーン座標。</summary>
public class ScreenPos
{
    public float X;
    public float Y;
    public bool Ok;
}

public static class Tonton22
{
    const int w = 640;
    const int h = 360;
    const float tickDt = 1.0f / 60.0f;

    const float dohyoR = 2.2f;
    const float dohyoH = 0.4f;
    const float dohyoY = 0.6f; // 懸架時の中心高
    const float topY = dohyoY + dohyoH * 0.5f;
    const float capR = 0.35f;

    // 土俵の質量と傾き慣性 (cylinder の解析値)。懸架 PD のゲインを
    // 「共振周波数 Hz と減衰比」で書くために使う。
    const float dohyoMass = 3.14159f * dohyoR * dohyoR * dohyoH;
    const float dohyoITilt =
        dohyoMass * (3.0f * dohyoR * dohyoR + dohyoH * dohyoH) / 12.0f;

    // --- 調整パラメータ (hot reload でいじる) ------------------------------
    static float suspLinHz = 3.0f; // 土俵懸架ばね (上下・水平の戻り)
    static float suspLinDamp = 0.15f; // 減衰比。小さいほどトントンが弾む
    static float suspAngHz = 1.4f; // 傾きの戻り
    static float suspAngDamp = 0.2f;
    static float balKp = 6.0f; // 姿勢 PD: 立て直しトルク
    static float balKd = 1.2f; // 姿勢 PD: 角速度ダンピング
    // 筋力上限。重力転倒トルク (≈2.7 sinθ) がこれを超える角度 (≈35°) から
    // 先は本当に倒れる。無限に強いバランスは相撲にならない。押しの前傾
    // (15〜25°) と臨界角の間が薄いほど、トントンと脈動が決定打になる。
    static float balMax = 1.55f;
    static float holdK = 1.5f; // 仕切り中の定位置ばね
    static float holdKd = 0.8f;
    static float walkSpeed = 0.9f; // 相手へ詰める速さ (m/s)
    static float seekK = 1.5f; // 速度サーボの強さ
    static float tapImpulse = 12.0f; // トントン 1 発の強さ
    static int tapRepeat = 9; // 押しっぱなし連打の間隔 (frame)

    // --- 取組フロー ---------------------------------------------------------
    const int stShikiri = 0; // 仕切り位置へ戻って一呼吸
    const int stFight = 1; // 勝負 (トントン受付)
    const int stKimari = 2; // 決着の余韻
    static int state = stShikiri;
    static int stateT = 45;
    static int winner = -1;
    static int[] stars = new int[] { 0, 0 }; // 星取り
    static string kimarite = ""; // 決まり手 (かな)
    static int fightStart = 0; // ST_FIGHT に入ったフレーム
    static bool engagedPrev = false; // 立ち合いのぶつかり音のエッジ検出

    static int frame = 0;
    // 戦術
    const int taOsu = 0; // 押す: 前傾して押し込む (支えがある間は安全)
    const int taHiki = 1; // 引く: 支えを外して前傾の相手を落とす
    const int taInashi = 2; // いなす: 横へかわして空振りさせる
    const int taTame = 3; // ためる: 直立で耐える
    static float hikiK = 5.0f; // 引きの後退力
    static float inashiK = 5.5f; // いなしの横力
    static float hatakiK = 3.2f; // はたき込み (引き際に相手上体を引き倒すトルク)

    // 赤 = 突貫 (強く深く押すが、引きに合わされやすい)
    // 青 = 後の先 (押しは控えめ、相手の前傾に引き/いなしを合わせる)
    static List<Rikishi> fighters = new List<Rikishi>
    {
        new Rikishi
        {
            Gen = 1,
            Name = "あか",
            HomeX = -0.9f,
            Color = new float[] { 0.86f, 0.28f, 0.24f, 1.0f },
            BodyRgb = 0xC94434,
            DownFrames = 0,
            SquashT = 0,
            PrevVy = 0.0f,
            PushK = 9.0f,
            LeanK = 2.0f,
            PulseHz = 2.2f,
            Phase = 0.0f,
            Counter = 0.25f,
            Tactic = taOsu,
            TacticUntil = 0,
            SideSign = 1.0f,
        },
        new Rikishi
        {
            Gen = 1,
            Name = "あお",
            HomeX = 0.9f,
            Color = new float[] { 0.27f, 0.47f, 0.88f, 1.0f },
            BodyRgb = 0x3E6ED8,
            DownFrames = 0,
            SquashT = 0,
            PrevVy = 0.0f,
            PushK = 7.5f,
            LeanK = 1.2f,
            PulseHz = 1.6f,
            Phase = 2.1f,
            Counter = 0.7f,
            Tactic = taOsu,
            TacticUntil = 0,
            SideSign = -1.0f,
        },
    };

    // LUB_TONTON_AUTO=1 で自動トントン (ヘッドレス検証・デモ自走用)
    static bool auto = Environment.GetEnvironmentVariable("LUB_TONTON_AUTO") != null;

    // トントンの見た目フィードバック
    static int lastTap = -999;
    static float markerX = 0.0f;
    static float markerY = 0.0f;
    static float markerZ = 0.0f;
    static int markerT = 0;
    static float shake = 0.0f;
    static FixedStep? step = null;
    static WorldRef3d? world = null;
    static int renderFrame = 0;
    // TinyC# の module static 初期化時点では Vec3 global が未登録なので、
    // 最初の tick / render で遅延生成する。
    static Vec3? renderEye = null;

    // render ごとに取る実入力。pressed は位置とともに次 tick まで保持する。
    static bool pendingTap = false;
    static float pendingTapX = 0.0f;
    static float pendingTapY = 0.0f;
    static bool pointerDown = false;
    static float pointerX = 0.0f;
    static float pointerY = 0.0f;

    // --- procedural meshes (Shapes3d)。onFrame で遅延生成 --------------------
    static Mesh3d? cubeMesh = null;
    static Mesh3d? cylMesh = null;
    static Renderer3d? ren = null;

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

    /// <summary>整数剰余 (tcs は % 未対応)。a, b > 0 前提。</summary>
    static int Imod(int a, int b)
    {
        return a - (int)Math.Floor((float)a / (float)b) * b;
    }

    // --- SDF だるま力士 -----------------------------------------------------
    // bone 付き SDF ツリーからメッシュ化。体色だけ違う 2 体分を焼く。
    // モデルは -Z (相手の方) を向いて作る。feet が y=0。
    const int sdfN = 56;
    static Mesh3d[]? darumaMesh = null;
    // native watch は chunk 再実行で初期値 true に戻り、web (module mode) は
    // onReload で立てる。どちらも再メッシュのトリガ。
    static bool darumaDirty = true;

    public static void OnReload()
    {
        darumaDirty = true;
    }

    static SdfNode DarumaModel(int bodyRgb)
    {
        var body = Sdf.Sphere(0.50f).Move(0, 0.52f, 0)
            .Bone("body", new Vec3(0, 0.25f, 0));
        var head = Sdf.Sphere(0.30f).Move(0, 1.02f, 0)
            .Bone("head", new Vec3(0, 0.80f, 0));
        var armL = Sdf.Capsule(new Vec3(0.40f, 0.76f, -0.06f),
            new Vec3(0.60f, 0.40f, -0.30f), 0.11f)
            .Bone("arm_l", new Vec3(0.40f, 0.76f, -0.06f));
        var armR = Sdf.Capsule(new Vec3(-0.40f, 0.76f, -0.06f),
            new Vec3(-0.60f, 0.40f, -0.30f), 0.11f)
            .Bone("arm_r", new Vec3(-0.40f, 0.76f, -0.06f));
        var trunk = body.Smin(head, 0.12f).Smin(armL, 0.06f).Smin(armR, 0.06f)
            .Paint(bodyRgb);
        // 顔: 肌色の球を頭前面に沈めて smin (だるまの顔窓)
        var face = Sdf.Sphere(0.20f).Move(0, 1.02f, -0.17f).Paint(0xF2D1AC);
        // まわし: 白帯の torus
        var mawashi = Sdf.Torus(0.40f, 0.10f).Move(0, 0.24f, 0).Paint(0xF2EEDC);
        var eye = Sdf.Sphere(0.05f).Move(0.10f, 1.08f, -0.30f).MirrorX()
            .Paint(0x241F1F, 0.0f, 0.2f);
        return trunk.Smin(face, 0.04f).Union(mawashi).Union(eye);
    }

    static void EnsureDaruma(Mesh3d[] meshes)
    {
        if (!darumaDirty)
            return;
        for (int i = 0; i < fighters.Count; i++)
            meshes[i].Rebuild(Sdf.Mesh(DarumaModel(fighters[i].BodyRgb), sdfN));
        darumaDirty = false;
    }

    // 手続きボーンアニメ。腕 = 戦術で構えが変わる + 転倒でバタバタ、
    // 頭 = 押しの脈動でうなずく。物理 (傾き・跳ね) は model 行列側。
    static List<float> PackBones(int mi, Rikishi f, bool falling, float pulse,
        int logicalFrame, MeshData? data)
    {
        float t = logicalFrame * tickDt;
        float armSwing;
        if (falling)
            armSwing = (float)Math.Sin(t * 16.0f + mi * 2.1f) * 0.9f;
        else if (f.Tactic == taHiki || f.Tactic == taInashi)
            armSwing = 0.7f;
        else
            armSwing = -0.55f * pulse + (float)Math.Sin(t * 2.3f + mi) * 0.08f;
        float nod = falling ? (float)Math.Sin(t * 12.0f) * 0.25f : pulse * 0.16f;
        return Bones.Pack(data, (name, x, y, z) =>
        {
            if (name == "arm_l")
                return Bones.PivotRot(x, y, z, Mat4.RotateX(armSwing)
                    .Mul(Mat4.RotateZ(
                        falling ? (float)Math.Sin(t * 13.0f) * 0.5f : 0.12f * pulse)));
            if (name == "arm_r")
                return Bones.PivotRot(x, y, z, Mat4.RotateX(armSwing)
                    .Mul(Mat4.RotateZ(
                        falling ? -(float)Math.Sin(t * 13.0f) * 0.5f : -0.12f * pulse)));
            if (name == "head")
                return Bones.PivotRot(x, y, z, Mat4.RotateX(nod));
            return null;
        });
    }

    // --- テキスト (かなサブセット TTF) --------------------------------------
    const string fontPath = "samples/22_tonton/data/MPLUS1p-subset.ttf";
    static bool fontLoaded = false;
    static int fontVersion = 0;
    static MeshText? mtext = null;

    static MeshText? EnsureText()
    {
        Io.LoadBytes(fontPath, out var bytes, out var version, out _, out _);
        if (bytes == null)
            return null;
        if (!fontLoaded || fontVersion != version)
        {
            fontLoaded = true;
            fontVersion = version;
            mtext = new MeshText("tonton_mtext", fontPath, version, w, h);
        }
        return mtext;
    }

    // world 座標 → 論理スクリーン座標
    static ScreenPos ScreenPos(Mat4 vp, float wx, float wy, float wz)
    {
        var c = vp.MulVec4(new Vec4(wx, wy, wz, 1.0f));
        if (c.W <= 0.001f)
            return new ScreenPos { X = 0.0f, Y = 0.0f, Ok = false };
        return new ScreenPos
        {
            X = (c.X / c.W + 1.0f) * 0.5f * w,
            Y = (1.0f - c.Y / c.W) * 0.5f * h,
            Ok = true,
        };
    }

    // --- physics -------------------------------------------------------------

    static WorldRef3d? DeclareWorld()
    {
        var world = Phys3d.World("tonton", new WorldOpts3d
        {
            Gravity = new Vec3d { X = 0.0f, Y = -10.0f, Z = 0.0f },
            FixedDt = tickDt,
            Substeps = 4,
            MaxSteps = 1,
        });
        if (world == null)
            return null;
        Phys3d.Begin(world);
        return world;
    }

    // 地面と台座。落ちた力士は地面に転がる (respawn は y で検出)。
    static void DeclareStatics(WorldRef3d world)
    {
        var ground = Phys3d.Body(world, "ground", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Static,
            Initial = new InitialState3d { X = 0.0f, Y = -0.5f, Z = 0.0f },
        });
        if (ground != null)
            Phys3d.Box(ground, "solid", new BoxDesc3d
            {
                Hx = 8.0f,
                Hy = 0.5f,
                Hz = 8.0f,
                Friction = 0.7f,
            });
        var baseBody = Phys3d.Body(world, "base", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Static,
            Initial = new InitialState3d { X = 0.0f, Y = 0.15f, Z = 0.0f },
        });
        if (baseBody != null)
            Phys3d.Cylinder(baseBody, "solid", new CylinderDesc3d
            {
                Height = 0.3f,
                Radius = 1.7f,
                Sides = 24,
                Friction = 0.6f,
            });
    }

    // 土俵: 懸架ばね付きの dynamic cylinder。トントンはここに impulse を打つ。
    // 懸架は joint ではなく自前 PD (力士のバランスと同じ流儀)。gravityScale 0
    // なので rest 位置はぴったり home に決まる。
    static BodyRef3d? DeclareDohyo(WorldRef3d world)
    {
        var dohyo = Phys3d.Body(world, "dohyo", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            GravityScale = 0.0f,
            Initial = new InitialState3d { X = 0.0f, Y = dohyoY, Z = 0.0f },
        });
        if (dohyo == null)
            return null;
        Phys3d.Cylinder(dohyo, "solid", new CylinderDesc3d
        {
            Height = dohyoH,
            Radius = dohyoR,
            Sides = 28,
            Density = 1.0f,
            Friction = 0.9f,
            Contact = true,
        });
        return dohyo;
    }

    // 懸架 PD。位置 (xyz) は home へ、傾きは水平へ、周波数 ω と減衰比 ζ で戻す。
    // F = m (ω² Δx − 2ζω v)、τ = I (ω² lean − 2ζω w)。
    static void ControlDohyo(BodyRef3d dohyo)
    {
        var pose = Phys3d.Pose(dohyo);
        if (pose == null)
            return;
        float wl = 2.0f * (float)Math.PI * suspLinHz;
        float cl = 2.0f * suspLinDamp * wl;
        Phys3d.AddForceCenter(dohyo, new Vec3d
        {
            X = dohyoMass * (wl * wl * (0.0f - pose.X) - cl * pose.Vx),
            Y = dohyoMass * (wl * wl * (dohyoY - pose.Y) - cl * pose.Vy),
            Z = dohyoMass * (wl * wl * (0.0f - pose.Z) - cl * pose.Vz),
        });
        var q = new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
        var up = q * Vec3.Up();
        var lean = up.Cross(Vec3.Up());
        float wa = 2.0f * (float)Math.PI * suspAngHz;
        float ca = 2.0f * suspAngDamp * wa;
        Phys3d.AddTorque(dohyo, new Vec3d
        {
            X = dohyoITilt * (wa * wa * lean.X - ca * pose.Wx),
            Y = dohyoITilt * (-ca * pose.Wy),
            Z = dohyoITilt * (wa * wa * lean.Z - ca * pose.Wz),
        });
    }

    static BodyRef3d? DeclareRikishi(WorldRef3d world, int i, Rikishi f)
    {
        var body = Phys3d.Body(world, "rikishi:" + i, new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            Version = f.Gen,
            LinearDamping = 0.1f,
            AngularDamping = 0.5f,
            // yaw を封じる: capsule は回転対称なので物理には影響せず、
            // 見た目の向き (相手に正対) をレンダリング側で自由に決められる
            MotionLocks = new MotionLocks3d { AngularY = true },
            Initial = new InitialState3d { X = f.HomeX, Y = topY + 0.02f, Z = 0.0f },
        });
        if (body == null)
            return null;
        Phys3d.Capsule(body, "solid", new CapsuleDesc3d
        {
            Version = f.Gen,
            A = new Vec3d { X = 0.0f, Y = capR, Z = 0.0f },
            B = new Vec3d { X = 0.0f, Y = 0.95f, Z = 0.0f },
            R = capR,
            Density = 1.0f,
            // 高摩擦: 足が滑るより先に体が傾くように (押し倒しが決まる条件)。
            // 移動の自由は空中 (トントンで跳ねた瞬間) にある。
            Friction = 0.85f,
            Contact = true,
        });
        return body;
    }

    // 決定論ハッシュ乱数 (シードは frame と力士 index)。リプレイ可能。
    static float Rand01(int n)
    {
        float x = (float)Math.Sin(n * 12.9898f) * 43758.5453f;
        return x - (float)Math.Floor(x);
    }

    // 戦術選択。反応間隔 (tacticUntil) ごとに再判断する。
    // - 相手が深く前傾 → counter 確率で引き/いなし (支えを外す)
    // - 背中が土俵際 → いなしで軸をずらす
    // - まれに「ため」、基本は押し
    static void Decide(int i, Rikishi f, Pose3d pose, Pose3d op, Vec3 dir,
        bool engaged)
    {
        if (frame < f.TacticUntil)
            return;
        var opQ = new Quat(op.Qx, op.Qy, op.Qz, op.Qw);
        var opUp = opQ * Vec3.Up();
        float leanToMe = -(opUp.X * dir.X + opUp.Z * dir.Z); // 相手の前傾のこちら成分
        float pushedBack = -(pose.Vx * dir.X + pose.Vz * dir.Z); // 押し込まれ速度
        float rr = (float)Math.Sqrt(pose.X * pose.X + pose.Z * pose.Z);
        float backToEdge =
            rr > 0.01f ? -(pose.X * dir.X + pose.Z * dir.Z) / rr : 0.0f;
        float edgeDanger = backToEdge > 0.0f ? rr / dohyoR * backToEdge : 0.0f;
        float r = Rand01(frame * 97 + i * 1013);
        // 引き/いなしの好機: 相手が前傾している、押し込まれている、または賭け
        float chance = (leanToMe > 0.10f ? f.Counter : 0.0f)
            + (pushedBack > 0.12f ? f.Counter * 0.8f : 0.0f) + f.Counter * 0.15f;
        if (engaged && r < chance && edgeDanger < 0.55f)
        {
            f.Tactic = Rand01(frame * 131 + i * 71) < 0.5f ? taHiki : taInashi;
            f.TacticUntil = frame + 18;
            f.SideSign = Rand01(frame * 193 + i * 37) < 0.5f ? -1.0f : 1.0f;
        }
        else if (edgeDanger > 0.62f && r < 0.8f)
        {
            f.Tactic = taInashi;
            f.TacticUntil = frame + 16;
            f.SideSign = Rand01(frame * 193 + i * 37) < 0.5f ? -1.0f : 1.0f;
        }
        else if (r > 0.9f)
        {
            f.Tactic = taTame;
            f.TacticUntil = frame + 12;
        }
        else
        {
            f.Tactic = taOsu;
            f.TacticUntil = frame + 18 + (int)Math.Floor(r * 22.0f);
        }
    }

    // 姿勢 PD (倒立振子)。up を world up へ立て直すトルク + 角速度ダンピング。
    // その上に状態別の操舵: 仕切り中は定位置ばね、勝負中は戦術に従う。
    static void ControlRikishi(int i, Rikishi f, BodyRef3d body, BodyRef3d opp)
    {
        // 「のこった」の脈動。押し込み力と腕の絵が同じ位相を読む
        f.Pulse = Math.Max(0.0f, (float)Math.Sin(
            frame * tickDt * f.PulseHz * 2.0f * (float)Math.PI + f.Phase));
        var pose = Phys3d.Pose(body);
        if (pose == null)
            return;
        var q = new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
        var up = q * Vec3.Up();
        var lean = up.Cross(Vec3.Up()); // |lean| = sin(傾き)、方向 = 立て直す回転軸
        var spring = lean * balKp;
        float mag = spring.Length();
        // 筋力上限 (超えた傾きは救えない)。「ため」中は腰を落として踏ん張る
        float maxEff = f.Tactic == taTame && state == stFight
            ? balMax * 1.5f : balMax;
        if (mag > maxEff)
            spring = spring * (maxEff / mag);
        Phys3d.AddTorque(body, new Vec3d
        {
            X = spring.X - pose.Wx * balKd,
            Y = 0.0f, // yaw は motionLocks で封じている
            Z = spring.Z - pose.Wz * balKd,
        });
        // 倒れている間は操舵しない (勝敗の余韻でジタバタさせない)
        if (up.Y < 0.5f)
            return;
        if (state == stFight)
        {
            var op = Phys3d.Pose(opp);
            if (op == null)
                return;
            var toOpp = new Vec3(op.X - pose.X, 0.0f, op.Z - pose.Z);
            float dist = toOpp.Length();
            var dir = toOpp.Normalize();
            bool engaged = dist < 2.0f * capR + 0.14f;
            Decide(i, f, pose, op, dir, engaged);
            if (f.Tactic == taHiki)
            {
                // 支えを外す。前傾した相手はつんのめって落ちる (引き落とし)
                Phys3d.AddForceCenter(body, new Vec3d
                {
                    X = (-dir.X * 1.8f - pose.Vx) * hikiK,
                    Y = 0.0f,
                    Z = (-dir.Z * 1.8f - pose.Vz) * hikiK,
                });
                // はたき込み: 組んだまま引くときは相手の上体をこちらへ引き倒す
                if (dist < 2.0f * capR + 0.55f)
                {
                    var opQ = new Quat(op.Qx, op.Qy, op.Qz, op.Qw);
                    var opUp = opQ * Vec3.Up();
                    var pullAxis = opUp.Cross(-1.0f * dir); // 相手の up をこちらへ倒す軸
                    Phys3d.AddTorque(opp, new Vec3d
                    {
                        X = pullAxis.X * hatakiK,
                        Y = 0.0f,
                        Z = pullAxis.Z * hatakiK,
                    });
                }
            }
            else if (f.Tactic == taInashi)
            {
                // 横へかわす。押しの軸を外して空振りさせる
                var side = new Vec3(-dir.Z * f.SideSign, 0.0f, dir.X * f.SideSign);
                Phys3d.AddForceCenter(body, new Vec3d
                {
                    X = (side.X * 2.3f - pose.Vx) * inashiK,
                    Y = 0.0f,
                    Z = (side.Z * 2.3f - pose.Vz) * inashiK,
                });
                // かわしながら相手の突進を前へ転がす (突き落とし)
                if (dist < 2.0f * capR + 0.55f)
                {
                    var opQ = new Quat(op.Qx, op.Qy, op.Qz, op.Qw);
                    var opUp = opQ * Vec3.Up();
                    var rollAxis = opUp.Cross(-1.0f * dir);
                    Phys3d.AddTorque(opp, new Vec3d
                    {
                        X = rollAxis.X * hatakiK * 0.7f,
                        Y = 0.0f,
                        Z = rollAxis.Z * hatakiK * 0.7f,
                    });
                }
            }
            else if (f.Tactic == taTame)
            {
                // 直立で耐える。詰めも押しもしない
                Phys3d.AddForceCenter(body, new Vec3d
                {
                    X = -pose.Vx * seekK,
                    Y = 0.0f,
                    Z = -pose.Vz * seekK,
                });
            }
            else
            {
                // 押す: 前進方向にはブレーキをかけない速度サーボ。押し込み中に
                // 相手が消えても止まれない = 突っ込むリスクが物理に乗る
                float vAlong = pose.Vx * dir.X + pose.Vz * dir.Z;
                float drive =
                    vAlong < walkSpeed ? (walkSpeed - vAlong) * seekK : 0.0f;
                float perpX = pose.Vx - dir.X * vAlong;
                float perpZ = pose.Vz - dir.Z * vAlong;
                Phys3d.AddForceCenter(body, new Vec3d
                {
                    X = dir.X * drive - perpX * seekK,
                    Y = 0.0f,
                    Z = dir.Z * drive - perpZ * seekK,
                });
                if (engaged)
                {
                    // 脈動で前傾して押し込む。重心を相手に預ける
                    float pulse = f.Pulse;
                    Phys3d.AddForceCenter(body, new Vec3d
                    {
                        X = dir.X * f.PushK * pulse,
                        Y = 0.0f,
                        Z = dir.Z * f.PushK * pulse,
                    });
                    var leanAxis = up.Cross(dir); // up を dir へ倒す = 前傾
                    Phys3d.AddTorque(body, new Vec3d
                    {
                        X = leanAxis.X * f.LeanK * pulse,
                        Y = 0.0f,
                        Z = leanAxis.Z * f.LeanK * pulse,
                    });
                }
            }
        }
        else
        {
            Phys3d.AddForceCenter(body, new Vec3d
            {
                X = (f.HomeX - pose.X) * holdK - pose.Vx * holdKd,
                Y = 0.0f,
                Z = (0.0f - pose.Z) * holdK - pose.Vz * holdKd,
            });
        }
    }

    // 勝敗判定と取組フロー。負け = 土俵上面から落ちた or 倒れたまま起きない。
    static void Judge(List<BodyRef3d> bodies)
    {
        if (state == stShikiri)
        {
            stateT--;
            if (stateT <= 0)
            {
                state = stFight;
                fightStart = frame;
                engagedPrev = false;
                Audio.Play(Sfx.Blip(2400, 2100, 0.05f, 0.35f)); // 拍子木
            }
        }
        else if (state == stFight)
        {
            if (frame == fightStart + 9)
                Audio.Play(Sfx.Blip(2400, 2100, 0.05f, 0.35f),
                    new PlayOpts { Pitch = 0.93f });
            var lost = new bool[] { false, false };
            var lostOut = new bool[] { false, false };
            for (int i = 0; i < fighters.Count; i++)
            {
                var f = fighters[i];
                var pose = Phys3d.Pose(bodies[i]);
                if (pose == null)
                    continue;
                var q = new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
                var up = q * Vec3.Up();
                if (up.Y < 0.5f) // 60° = 筋力上限の臨界角より深い。もう戻れない
                    f.DownFrames++;
                else
                    f.DownFrames = 0;
                lostOut[i] = pose.Y < 0.35f
                    || pose.X * pose.X + pose.Z * pose.Z > dohyoR * dohyoR;
                lost[i] = lostOut[i] || f.DownFrames > 20;
            }
            if (lost[0] || lost[1])
            {
                winner = lost[0] && lost[1] ? -1 : (lost[0] ? 1 : 0);
                // 決まり手: 勝者の直前の戦術 × 負け方 (土俵外 or 転倒)
                if (winner < 0)
                {
                    kimarite = "とりなおし";
                }
                else
                {
                    bool wentOut = lostOut[1 - winner];
                    int wt = fighters[winner].Tactic;
                    if (wt == taHiki)
                        kimarite = wentOut ? "ひきおとし" : "はたきこみ";
                    else if (wt == taInashi)
                        kimarite = wentOut ? "おくりだし" : "つきおとし";
                    else
                        kimarite = wentOut ? "おしだし" : "おしたおし";
                }
                state = stKimari;
                stateT = 90;
                shake = 1.0f;
                Audio.Play(Sfx.Noise(0.7f, 0.28f, 0xbeef)); // 歓声がわり
                Audio.Play(Sfx.Blip(520, 780, 0.22f, 0.22f));
                if (auto)
                    Console.WriteLine("tonton: winner=" + winner
                        + " kimarite=" + kimarite + " stars=[" + stars[0] + ","
                        + stars[1] + "] frame=" + frame);
            }
        }
        else if (state == stKimari)
        {
            stateT--;
            if (stateT <= 0)
            {
                if (winner >= 0)
                    stars[winner]++;
                foreach (var f in fighters)
                {
                    f.Gen++;
                    f.DownFrames = 0;
                }
                winner = -1;
                state = stShikiri;
                stateT = 45;
            }
        }
    }

    // --- input ----------------------------------------------------------------
    // クリック (押しっぱなしは連打) = トントン。カメラ ray を土俵上面の平面と
    // 交差させ、土俵の中なら下向き impulse。土俵が傾いていても上面 "あたり" に
    // 打てれば十分なので平面近似で済ませる。
    static void TapAt(BodyRef3d dohyo, float px, float pz)
    {
        lastTap = frame;
        Audio.Play(Sfx.Blip(150, 45, 0.09f, 0.5f)); // トントンの「ドンッ」
        Audio.Play(Sfx.Noise(0.05f, 0.18f));
        Phys3d.AddImpulse(dohyo,
            new Vec3d { X = 0.0f, Y = -tapImpulse, Z = 0.0f },
            new CommandOpts3d
            {
                Point = new Vec3d { X = px, Y = topY, Z = pz },
            });
        markerX = px;
        markerY = topY;
        markerZ = pz;
        markerT = 10;
        shake = 1.0f;
    }

    static void CaptureTapInput()
    {
        if (auto)
            return;
        bool pressed = Input.MousePressed();
        pointerDown = Input.MouseDown();
        if (pressed || pointerDown)
        {
            Input.MousePos(out var mx, out var my);
            pointerX = mx;
            pointerY = my;
            if (pressed)
            {
                pendingTap = true;
                pendingTapX = mx;
                pendingTapY = my;
            }
        }
    }

    static void UpdateTap(BodyRef3d dohyo, Vec3 eye, Vec3 target, float fovDeg,
        float aspect, float w, float h)
    {
        // 自動トントン: 決定論の擬似乱数で縁寄りを叩き続ける (勝負中のみ)
        if (auto)
        {
            if (state != stFight || Imod(frame, 24) != 12)
                return;
            float aa = Imod(frame * 7919, 628) / 100.0f;
            float rr = dohyoR * (0.45f + Imod(frame * 337, 50) / 100.0f);
            TapAt(dohyo, (float)Math.Cos(aa) * rr, (float)Math.Sin(aa) * rr);
            return;
        }
        if (state != stFight)
        {
            // 仕切り・余韻中の pressed は次の取組に持ち越さない。
            pendingTap = false;
            return;
        }
        bool pressed = pendingTap;
        float sx = pressed ? pendingTapX : pointerX;
        float sy = pressed ? pendingTapY : pointerY;
        pendingTap = false;
        bool tap = pressed || (pointerDown && frame - lastTap >= tapRepeat);
        if (!tap)
            return;
        float ndcX = sx / w * 2.0f - 1.0f;
        float ndcY = 1.0f - sy / h * 2.0f;
        var fwd = (target - eye).Normalize();
        var right = Vec3.Up().Cross(fwd).Normalize();
        var upv = fwd.Cross(right);
        float tanH = (float)Math.Tan(fovDeg * (float)Math.PI / 360.0f);
        var dir = (fwd + right * (ndcX * tanH * aspect) + upv * (ndcY * tanH))
            .Normalize();
        if (dir.Y > -0.001f)
            return; // 上を向いた ray は土俵に届かない
        float t = (topY - eye.Y) / dir.Y;
        var p = eye + dir * t;
        if (p.X * p.X + p.Z * p.Z > dohyoR * dohyoR * 1.1f)
            return;
        TapAt(dohyo, p.X, p.Z);
    }

    static void Tick(float aspect, float w, float h)
    {
        // 従来の render frame 冒頭 / 末尾にあった演出カウントを
        // 60 Hz で進める。この後の tap / judge で始まる演出は全強度で描く。
        if (shake > 0.001f)
            shake = shake * 0.85f;
        if (markerT > 0)
            markerT = markerT - 1;
        renderFrame = frame;
        var tickEye = new Vec3((float)Math.Sin(frame * 1.7f) * 0.05f * shake,
            3.6f + (float)Math.Sin(frame * 2.3f) * 0.03f * shake, -5.4f);
        renderEye = tickEye;
        var lookAt = new Vec3(0.0f, 0.4f, 0.0f);
        float fovDeg = 40.0f;

        var nextWorld = DeclareWorld();
        if (nextWorld == null)
            return;
        world = nextWorld;
        DeclareStatics(nextWorld);
        var dohyo = DeclareDohyo(nextWorld);
        if (dohyo == null)
            return;
        ControlDohyo(dohyo);
        var bodies = new List<BodyRef3d>();
        for (int i = 0; i < fighters.Count; i++)
        {
            var body = DeclareRikishi(nextWorld, i, fighters[i]);
            if (body == null)
                return;
            bodies.Add(body);
        }
        for (int i = 0; i < fighters.Count; i++)
            ControlRikishi(i, fighters[i], bodies[i], bodies[1 - i]);
        Judge(bodies);
        UpdateTap(dohyo, tickEye, lookAt, fovDeg, aspect, w, h);

        Phys3d.Step(nextWorld, tickDt);

        // 立ち合いのぶつかり (接触のエッジで音と振動)
        {
            var p0 = Phys3d.Pose(bodies[0]);
            var p1 = Phys3d.Pose(bodies[1]);
            if (state == stFight && p0 != null && p1 != null)
            {
                float ddx = p1.X - p0.X;
                float ddz = p1.Z - p0.Z;
                float lim = 2.0f * capR + 0.14f;
                bool eng = ddx * ddx + ddz * ddz < lim * lim;
                if (eng && !engagedPrev)
                {
                    Audio.Play(Sfx.Noise(0.12f, 0.45f));
                    Audio.Play(Sfx.Blip(90, 55, 0.07f, 0.3f));
                    if (shake < 0.6f)
                        shake = 0.6f;
                }
                engagedPrev = eng;
            }
        }

        // 着地スカッシュの検出と減衰も logical frame で進める。
        for (int i = 0; i < fighters.Count; i++)
        {
            var f = fighters[i];
            var pose = Phys3d.Pose(bodies[i]);
            if (pose == null)
                continue;
            if (f.PrevVy < -1.2f && pose.Vy > f.PrevVy + 0.8f)
                f.SquashT = 8;
            f.PrevVy = pose.Vy;
            if (f.SquashT > 0)
                f.SquashT = f.SquashT - 1;
        }

        frame = frame + 1;
    }

    // --- rendering -------------------------------------------------------------

    public static void OnFrame(float dt)
    {
        var renNow = ren ?? new Renderer3d("tt22");
        ren = renNow;
        var cubeNow = cubeMesh;
        var cylNow = cylMesh;
        if (cubeNow == null || cylNow == null)
        {
            cubeNow = new Mesh3d("tt_cube");
            cubeNow.Rebuild(Shapes3d.Cube());
            cylNow = new Mesh3d("tt_cyl");
            cylNow.Rebuild(Shapes3d.Cylinder(28));
            cubeMesh = cubeNow;
            cylMesh = cylNow;
        }
        var darumaNow = darumaMesh
            ?? new Mesh3d[] { new Mesh3d("tt_daruma0"), new Mesh3d("tt_daruma1") };
        darumaMesh = darumaNow;

        Gfx.Size(out var sw, out var sh);
        float aspect = (float)sw / sh;
        float fovDeg = 40.0f;
        var lookAt = new Vec3(0.0f, 0.4f, 0.0f);

        CaptureTapInput();
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => Tick(aspect, sw, sh));

        var eyeNow = renderEye;
        if (eyeNow == null)
        {
            eyeNow = new Vec3(0.0f, 3.6f, -5.4f);
            renderEye = eyeNow;
        }

        // --- draw ---
        // 屋外の明るい昼 (だるまの色がよく出るように空色強め)
        renNow.Light.Dir = new Vec3(-0.4f, 1.0f, -0.55f);
        renNow.Sky.Top = Color.Rgb(0.45f, 0.52f, 0.62f);
        renNow.Sky.Bottom = Color.Rgb(0.16f, 0.14f, 0.13f);
        renNow.Background = Color.Rgb(0.05f, 0.05f, 0.08f);
        renNow.Shadow.Center = new Vec3(0, 0.3f, 0);
        renNow.Shadow.Extent = 3.5f;
        renNow.Begin(new Camera
        {
            Eye = eyeNow,
            Target = lookAt,
            Fov = fovDeg,
            Near = 0.1f,
            Far = 50.0f,
        });

        // 地面と台座
        renNow.Draw(cubeNow, Mat4.Translate(new Vec3(0, -0.5f, 0))
            * Mat4.Scale(new Vec3(8, 0.5f, 8)),
            new Draw3dOpts { Tint = Color.Rgb(0.10f, 0.10f, 0.13f) });
        renNow.Draw(cylNow, Mat4.Translate(new Vec3(0, 0.15f, 0))
            * Mat4.Scale(new Vec3(1.7f, 0.3f, 1.7f)),
            new Draw3dOpts { Tint = Color.Rgb(0.16f, 0.15f, 0.19f) });

        // 土俵 (懸架で傾く)。上面に俵の白リングと仕切り線を重ねる。
        var drawWorld = world;
        Pose3d? dp = null;
        if (drawWorld != null)
            dp = Phys3d.PoseByKey(drawWorld, "dohyo");
        if (dp != null)
        {
            var dm = Renderer3d.PoseMat(dp);
            renNow.Draw(cylNow, dm * Mat4.Scale(new Vec3(dohyoR, dohyoH, dohyoR)),
                new Draw3dOpts { Tint = Color.Rgb(0.72f, 0.55f, 0.38f) });
            float topLocal = dohyoH * 0.5f;
            renNow.Draw(cylNow, dm * Mat4.Translate(new Vec3(0, topLocal + 0.005f, 0))
                * Mat4.Scale(new Vec3(dohyoR * 0.98f, 0.01f, dohyoR * 0.98f)),
                new Draw3dOpts { Tint = Color.Rgb(0.92f, 0.88f, 0.78f) });
            renNow.Draw(cylNow, dm * Mat4.Translate(new Vec3(0, topLocal + 0.015f, 0))
                * Mat4.Scale(new Vec3(dohyoR * 0.86f, 0.01f, dohyoR * 0.86f)),
                new Draw3dOpts { Tint = Color.Rgb(0.72f, 0.55f, 0.38f) });
            foreach (var sx in new float[] { -0.22f, 0.22f })
                renNow.Draw(cubeNow,
                    dm * Mat4.Translate(new Vec3(sx, topLocal + 0.025f, 0))
                    * Mat4.Scale(new Vec3(0.02f, 0.004f, 0.3f)),
                    new Draw3dOpts { Tint = Color.Rgb(0.92f, 0.88f, 0.78f) });
        }

        // 力士: SDF だるま (skinning + 手続きボーンアニメ)。物理の pose に
        // 相手への正対 yaw と着地スカッシュを重ねる。
        EnsureDaruma(darumaNow);
        for (int i = 0; i < fighters.Count; i++)
        {
            var f = fighters[i];
            var mesh = darumaNow[i];
            if (drawWorld == null)
                continue;
            var pose = Phys3d.PoseByKey(drawWorld, "rikishi:" + i);
            if (pose == null || !mesh.Ready())
                continue;
            float sq = f.SquashT / 8.0f * 0.22f;
            var op = Phys3d.PoseByKey(drawWorld, "rikishi:" + (1 - i));
            float fx = op != null ? op.X - pose.X : -pose.X;
            float fz = op != null ? op.Z - pose.Z : -pose.Z;
            float yaw = (float)Math.Atan2(fx, fz); // model の -Z を相手へ向ける
            var q = new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
            var upv = q * Vec3.Up();
            bool falling = upv.Y < 0.6f;
            float pulse = f.Tactic == taOsu ? f.Pulse : 0.0f;
            var model = Renderer3d.PoseMat(pose) * Mat4.RotateY(yaw)
                * Mat4.Scale(new Vec3(1.0f + sq * 0.6f, 1.0f - sq, 1.0f + sq * 0.6f));
            renNow.Draw(mesh, model, new Draw3dOpts
            {
                Bones = PackBones(i, f, falling, pulse, renderFrame, mesh.Data),
            });
        }

        // トントンのマーカー (打った場所に一瞬リング。高輝度で bloom に乗る)
        if (markerT > 0)
        {
            float k = markerT / 10.0f;
            renNow.Draw(cylNow,
                Mat4.Translate(new Vec3(markerX, markerY + 0.03f, markerZ))
                * Mat4.Scale(new Vec3(0.22f * (2.0f - k), 0.01f, 0.22f * (2.0f - k))),
                new Draw3dOpts { Tint = Color.Rgb(1.6f, 1.5f, 0.9f) });
        }

        renNow.End();

        // --- テキスト (かな): tonemap 後の swapchain に重ね描き ---
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        var mt = EnsureText();
        if (mt != null)
        {
            var cream = Color.Rgb(0.95f, 0.92f, 0.85f);
            mt.TextCentered("あか　" + stars[0] + " - " + stars[1] + "　あお",
                w * 0.5f, 348, 20, cream);
            if (state == stFight)
            {
                if (renderFrame - fightStart < 50)
                    mt.TextCentered("はっけよい", w * 0.5f, 120, 44,
                        Color.Rgb(0.98f, 0.85f, 0.4f));
                // 思考の可視化: 頭上に現在の戦術
                var vp = renNow.ViewProj;
                for (int i = 0; i < fighters.Count; i++)
                {
                    if (vp == null)
                        continue;
                    var f = fighters[i];
                    if (drawWorld == null)
                        continue;
                    var pose = Phys3d.PoseByKey(drawWorld, "rikishi:" + i);
                    if (pose == null)
                        continue;
                    var sp = ScreenPos(vp, pose.X, pose.Y + 1.5f, pose.Z);
                    if (!sp.Ok)
                        continue;
                    string label;
                    Color tint;
                    if (f.Tactic == taHiki)
                    {
                        label = "ひく";
                        tint = Color.Rgb(0.35f, 0.9f, 0.9f);
                    }
                    else if (f.Tactic == taInashi)
                    {
                        label = "いなす";
                        tint = Color.Rgb(0.45f, 0.9f, 0.45f);
                    }
                    else if (f.Tactic == taTame)
                    {
                        label = "ためる";
                        tint = Color.Rgb(0.75f, 0.75f, 0.78f);
                    }
                    else
                    {
                        label = "おす";
                        tint = Color.Rgb(1.0f, 0.66f, 0.25f);
                    }
                    mt.TextCentered(label, sp.X, sp.Y, 16, tint);
                }
            }
            if (state == stKimari)
            {
                if (winner >= 0)
                {
                    var wf = fighters[winner];
                    mt.TextCentered(wf.Name + "のかち", w * 0.5f, 112, 36,
                        Color.Rgb(wf.Color[0] * 0.4f + 0.6f,
                            wf.Color[1] * 0.4f + 0.6f, wf.Color[2] * 0.4f + 0.6f));
                    mt.TextCentered(kimarite, w * 0.5f, 150, 24, cream);
                }
                else
                {
                    mt.TextCentered("とりなおし", w * 0.5f, 124, 32, cream);
                }
            }
        }

        Gfx.EndPass();
    }
}
