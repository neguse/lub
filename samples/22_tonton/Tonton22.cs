// lub の samples/22_tonton (Haxe 版 Tonton22.hx) の TinyC# 版 entry。
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
// Haxe 版との対応: gameplay rule (力士の挙動・投げ判定・土俵) は忠実。
// typedef Rikishi は class、screenPos の匿名構造体戻りは class ScreenPos、
// Phys3d の desc 匿名構造体は lub_stub の desc class。end() は End()、
// char() は Char()。Renderer3d / Mesh3d / MeshText は cs-lib の load 順の
// 都合で static 初期化子から呼べないため onFrame で遅延生成する。
// 整数 % は tcs 未対応なので imod (Math.Floor 分解) で書く。

using System;
using System.Collections.Generic;

/// <summary>力士 1 体 (Haxe 版 typedef Rikishi と対)。</summary>
public class Rikishi
{
    public int gen; // 再宣言 (respawn) 用 version
    public string name = ""; // 四股名 (かな)
    public double homeX;
    public double[] color = new double[] { 1.0, 1.0, 1.0, 1.0 };
    public int bodyRgb; // SDF に焼く体色
    public int downFrames;
    public int squashT; // 着地スカッシュの残りフレーム
    public double prevVy;
    // --- 個性 ---
    // 押し合いは「相手が支え」の安定構造なので、押すだけでは永遠に倒れない。
    // 決着は支えを外す瞬間 (引き・いなし) に生まれる。個性はその使い分け。
    public double pushK; // 押しの強さ
    public double leanK; // 前傾の深さ (リスク: 支えを外されると帰れない)
    public double pulseHz; // 押しの脈動周期
    public double phase;
    public double counter; // 相手の深い前傾に引き/いなしを合わせる確率
    // --- 戦術状態 ---
    public int tactic;
    public int tacticUntil;
    public double sideSign; // いなしの回り込み方向
}

/// <summary>world 座標 → 論理スクリーン座標 (Haxe 版の匿名構造体戻り)。</summary>
public class ScreenPos
{
    public double x;
    public double y;
    public bool ok;
}

public static class Tonton22
{
    const int W = 640;
    const int H = 360;
    const double DT = 1.0 / 60.0;

    const double DOHYO_R = 2.2;
    const double DOHYO_H = 0.4;
    const double DOHYO_Y = 0.6; // 懸架時の中心高
    const double TOP_Y = DOHYO_Y + DOHYO_H * 0.5;
    const double CAP_R = 0.35;

    // 土俵の質量と傾き慣性 (cylinder の解析値)。懸架 PD のゲインを
    // 「共振周波数 Hz と減衰比」で書くために使う。
    const double DOHYO_MASS = 3.14159 * DOHYO_R * DOHYO_R * DOHYO_H;
    const double DOHYO_I_TILT =
        DOHYO_MASS * (3.0 * DOHYO_R * DOHYO_R + DOHYO_H * DOHYO_H) / 12.0;

    // --- 調整パラメータ (hot reload でいじる) ------------------------------
    static double suspLinHz = 3.0; // 土俵懸架ばね (上下・水平の戻り)
    static double suspLinDamp = 0.15; // 減衰比。小さいほどトントンが弾む
    static double suspAngHz = 1.4; // 傾きの戻り
    static double suspAngDamp = 0.2;
    static double balKp = 6.0; // 姿勢 PD: 立て直しトルク
    static double balKd = 1.2; // 姿勢 PD: 角速度ダンピング
    // 筋力上限。重力転倒トルク (≈2.7 sinθ) がこれを超える角度 (≈35°) から
    // 先は本当に倒れる。無限に強いバランスは相撲にならない。押しの前傾
    // (15〜25°) と臨界角の間が薄いほど、トントンと脈動が決定打になる。
    static double balMax = 1.55;
    static double holdK = 1.5; // 仕切り中の定位置ばね
    static double holdKd = 0.8;
    static double walkSpeed = 0.9; // 相手へ詰める速さ (m/s)
    static double seekK = 1.5; // 速度サーボの強さ
    static double tapImpulse = 12.0; // トントン 1 発の強さ
    static int tapRepeat = 9; // 押しっぱなし連打の間隔 (frame)

    // --- 取組フロー ---------------------------------------------------------
    const int ST_SHIKIRI = 0; // 仕切り位置へ戻って一呼吸
    const int ST_FIGHT = 1; // 勝負 (トントン受付)
    const int ST_KIMARI = 2; // 決着の余韻
    static int state = ST_SHIKIRI;
    static int stateT = 45;
    static int winner = -1;
    static int[] stars = new int[] { 0, 0 }; // 星取り
    static string kimarite = ""; // 決まり手 (かな)
    static int fightStart = 0; // ST_FIGHT に入ったフレーム
    static bool engagedPrev = false; // 立ち合いのぶつかり音のエッジ検出

    static int frame = 0;
    // 戦術
    const int TA_OSU = 0; // 押す: 前傾して押し込む (支えがある間は安全)
    const int TA_HIKI = 1; // 引く: 支えを外して前傾の相手を落とす
    const int TA_INASHI = 2; // いなす: 横へかわして空振りさせる
    const int TA_TAME = 3; // ためる: 直立で耐える
    static double hikiK = 5.0; // 引きの後退力
    static double inashiK = 5.5; // いなしの横力
    static double hatakiK = 3.2; // はたき込み (引き際に相手上体を引き倒すトルク)

    // 赤 = 突貫 (強く深く押すが、引きに合わされやすい)
    // 青 = 後の先 (押しは控えめ、相手の前傾に引き/いなしを合わせる)
    static List<Rikishi> fighters = new List<Rikishi>
    {
        new Rikishi
        {
            gen = 1,
            name = "あか",
            homeX = -0.9,
            color = new double[] { 0.86, 0.28, 0.24, 1.0 },
            bodyRgb = 0xC94434,
            downFrames = 0,
            squashT = 0,
            prevVy = 0.0,
            pushK = 9.0,
            leanK = 2.0,
            pulseHz = 2.2,
            phase = 0.0,
            counter = 0.25,
            tactic = TA_OSU,
            tacticUntil = 0,
            sideSign = 1.0,
        },
        new Rikishi
        {
            gen = 1,
            name = "あお",
            homeX = 0.9,
            color = new double[] { 0.27, 0.47, 0.88, 1.0 },
            bodyRgb = 0x3E6ED8,
            downFrames = 0,
            squashT = 0,
            prevVy = 0.0,
            pushK = 7.5,
            leanK = 1.2,
            pulseHz = 1.6,
            phase = 2.1,
            counter = 0.7,
            tactic = TA_OSU,
            tacticUntil = 0,
            sideSign = -1.0,
        },
    };

    // LUB_TONTON_AUTO=1 で自動トントン (ヘッドレス検証・デモ自走用)
    static bool auto = os.getenv("LUB_TONTON_AUTO") != null;

    // トントンの見た目フィードバック
    static int lastTap = -999;
    static double markerX = 0.0;
    static double markerY = 0.0;
    static double markerZ = 0.0;
    static int markerT = 0;
    static double shake = 0.0;
    static FixedStep? step = null;
    static WorldRef3d? world = null;
    static int renderFrame = 0;
    // TinyC# の module static 初期化時点では Vec3 global が未登録なので、
    // 最初の tick / render で遅延生成する。
    static Vec3? renderEye = null;

    // render ごとに取る実入力。pressed は位置とともに次 tick まで保持する。
    static bool pendingTap = false;
    static double pendingTapX = 0.0;
    static double pendingTapY = 0.0;
    static bool pointerDown = false;
    static double pointerX = 0.0;
    static double pointerY = 0.0;

    // --- procedural meshes (Shapes3d)。onFrame で遅延生成 --------------------
    static Mesh3d? cubeMesh = null;
    static Mesh3d? cylMesh = null;
    static Renderer3d? ren = null;

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

    /// <summary>整数剰余 (tcs は % 未対応)。a, b > 0 前提。</summary>
    static int imod(int a, int b)
    {
        return a - (int)Math.Floor((double)a / (double)b) * b;
    }

    // --- SDF だるま力士 -----------------------------------------------------
    // bone 付き SDF ツリーからメッシュ化。体色だけ違う 2 体分を焼く。
    // モデルは -Z (相手の方) を向いて作る。feet が y=0。
    const int SDF_N = 56;
    static Mesh3d[]? darumaMesh = null;
    // native watch は chunk 再実行で初期値 true に戻り、web (module mode) は
    // onReload で立てる。どちらも再メッシュのトリガ。
    static bool darumaDirty = true;

    public static void onReload()
    {
        darumaDirty = true;
    }

    static SdfNode darumaModel(int bodyRgb)
    {
        var body = Sdf.sphere(0.50).move(0, 0.52, 0)
            .bone("body", new Vec3(0, 0.25, 0));
        var head = Sdf.sphere(0.30).move(0, 1.02, 0)
            .bone("head", new Vec3(0, 0.80, 0));
        var armL = Sdf.capsule(new Vec3(0.40, 0.76, -0.06),
            new Vec3(0.60, 0.40, -0.30), 0.11)
            .bone("arm_l", new Vec3(0.40, 0.76, -0.06));
        var armR = Sdf.capsule(new Vec3(-0.40, 0.76, -0.06),
            new Vec3(-0.60, 0.40, -0.30), 0.11)
            .bone("arm_r", new Vec3(-0.40, 0.76, -0.06));
        var trunk = body.smin(head, 0.12).smin(armL, 0.06).smin(armR, 0.06)
            .paint(bodyRgb);
        // 顔: 肌色の球を頭前面に沈めて smin (だるまの顔窓)
        var face = Sdf.sphere(0.20).move(0, 1.02, -0.17).paint(0xF2D1AC);
        // まわし: 白帯の torus
        var mawashi = Sdf.torus(0.40, 0.10).move(0, 0.24, 0).paint(0xF2EEDC);
        var eye = Sdf.sphere(0.05).move(0.10, 1.08, -0.30).mirrorX()
            .paint(0x241F1F, 0.0, 0.2);
        return trunk.smin(face, 0.04).union(mawashi).union(eye);
    }

    static void ensureDaruma(Mesh3d[] meshes)
    {
        if (!darumaDirty)
            return;
        for (int i = 0; i < fighters.Count; i++)
            meshes[i].rebuild(Sdf.mesh(darumaModel(fighters[i].bodyRgb), SDF_N));
        darumaDirty = false;
    }

    // 手続きボーンアニメ。腕 = 戦術で構えが変わる + 転倒でバタバタ、
    // 頭 = 押しの脈動でうなずく。物理 (傾き・跳ね) は model 行列側。
    static List<double> packBones(int mi, Rikishi f, bool falling, double pulse,
        int logicalFrame, MeshData? data)
    {
        double t = logicalFrame * DT;
        double armSwing;
        if (falling)
            armSwing = Math.Sin(t * 16.0 + mi * 2.1) * 0.9;
        else if (f.tactic == TA_HIKI || f.tactic == TA_INASHI)
            armSwing = 0.7;
        else
            armSwing = -0.55 * pulse + Math.Sin(t * 2.3 + mi) * 0.08;
        double nod = falling ? Math.Sin(t * 12.0) * 0.25 : pulse * 0.16;
        return Bones.pack(data, (name, x, y, z) =>
        {
            if (name == "arm_l")
                return Bones.pivotRot(x, y, z, Mat4.rotateX(armSwing)
                    .mul(Mat4.rotateZ(
                        falling ? Math.Sin(t * 13.0) * 0.5 : 0.12 * pulse)));
            if (name == "arm_r")
                return Bones.pivotRot(x, y, z, Mat4.rotateX(armSwing)
                    .mul(Mat4.rotateZ(
                        falling ? -Math.Sin(t * 13.0) * 0.5 : -0.12 * pulse)));
            if (name == "head")
                return Bones.pivotRot(x, y, z, Mat4.rotateX(nod));
            return null;
        });
    }

    // --- テキスト (かなサブセット TTF) --------------------------------------
    static string? ttf = null;
    static int fontVersion = 0;
    static MeshText? mtext = null;

    static MeshText? ensureText()
    {
        Io.load_text("samples/22_tonton/data/MPLUS1p-subset.ttf",
            out var text, out var version, out _, out _);
        if (text == null)
            return null;
        if (ttf == null || fontVersion != version)
        {
            ttf = text;
            fontVersion = version;
            mtext = new MeshText("tonton_mtext", text, version, W, H);
        }
        return mtext;
    }

    // world 座標 → 論理スクリーン座標
    static ScreenPos screenPos(Mat4 vp, double wx, double wy, double wz)
    {
        var c = vp.mulVec4(new Vec4(wx, wy, wz, 1.0));
        if (c.w <= 0.001)
            return new ScreenPos { x = 0.0, y = 0.0, ok = false };
        return new ScreenPos
        {
            x = (c.x / c.w + 1.0) * 0.5 * W,
            y = (1.0 - c.y / c.w) * 0.5 * H,
            ok = true,
        };
    }

    // --- physics -------------------------------------------------------------

    static WorldRef3d? declareWorld()
    {
        var world = Phys3d.phys3d_world("tonton", new WorldOpts3d
        {
            gravity = new Vec3d { x = 0.0, y = -10.0, z = 0.0 },
            fixedDt = DT,
            substeps = 4,
            maxSteps = 1,
        });
        if (world == null)
            return null;
        Phys3d.phys3d_begin(world);
        return world;
    }

    // 地面と台座。落ちた力士は地面に転がる (respawn は y で検出)。
    static void declareStatics(WorldRef3d world)
    {
        var ground = Phys3d.phys3d_body(world, "ground", new BodyDesc3d
        {
            type = Phys3d.STATIC,
            initial = new InitialState3d { x = 0.0, y = -0.5, z = 0.0 },
        });
        if (ground != null)
            Phys3d.phys3d_box(ground, "solid", new BoxDesc3d
            {
                hx = 8.0,
                hy = 0.5,
                hz = 8.0,
                friction = 0.7,
            });
        var baseBody = Phys3d.phys3d_body(world, "base", new BodyDesc3d
        {
            type = Phys3d.STATIC,
            initial = new InitialState3d { x = 0.0, y = 0.15, z = 0.0 },
        });
        if (baseBody != null)
            Phys3d.phys3d_cylinder(baseBody, "solid", new CylinderDesc3d
            {
                height = 0.3,
                radius = 1.7,
                sides = 24,
                friction = 0.6,
            });
    }

    // 土俵: 懸架ばね付きの dynamic cylinder。トントンはここに impulse を打つ。
    // 懸架は joint ではなく自前 PD (力士のバランスと同じ流儀)。gravityScale 0
    // なので rest 位置はぴったり home に決まる。
    static BodyRef3d? declareDohyo(WorldRef3d world)
    {
        var dohyo = Phys3d.phys3d_body(world, "dohyo", new BodyDesc3d
        {
            type = Phys3d.DYNAMIC,
            gravityScale = 0.0,
            initial = new InitialState3d { x = 0.0, y = DOHYO_Y, z = 0.0 },
        });
        if (dohyo == null)
            return null;
        Phys3d.phys3d_cylinder(dohyo, "solid", new CylinderDesc3d
        {
            height = DOHYO_H,
            radius = DOHYO_R,
            sides = 28,
            density = 1.0,
            friction = 0.9,
            contact = true,
        });
        return dohyo;
    }

    // 懸架 PD。位置 (xyz) は home へ、傾きは水平へ、周波数 ω と減衰比 ζ で戻す。
    // F = m (ω² Δx − 2ζω v)、τ = I (ω² lean − 2ζω w)。
    static void controlDohyo(BodyRef3d dohyo)
    {
        var pose = Phys3d.phys3d_pose(dohyo);
        if (pose == null)
            return;
        double wl = 2.0 * Math.PI * suspLinHz;
        double cl = 2.0 * suspLinDamp * wl;
        Phys3d.phys3d_add_force_center(dohyo, new Vec3d
        {
            x = DOHYO_MASS * (wl * wl * (0.0 - pose.x) - cl * pose.vx),
            y = DOHYO_MASS * (wl * wl * (DOHYO_Y - pose.y) - cl * pose.vy),
            z = DOHYO_MASS * (wl * wl * (0.0 - pose.z) - cl * pose.vz),
        });
        var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
        var up = q * Vec3.up();
        var lean = up.cross(Vec3.up());
        double wa = 2.0 * Math.PI * suspAngHz;
        double ca = 2.0 * suspAngDamp * wa;
        Phys3d.phys3d_add_torque(dohyo, new Vec3d
        {
            x = DOHYO_I_TILT * (wa * wa * lean.x - ca * pose.wx),
            y = DOHYO_I_TILT * (-ca * pose.wy),
            z = DOHYO_I_TILT * (wa * wa * lean.z - ca * pose.wz),
        });
    }

    static BodyRef3d? declareRikishi(WorldRef3d world, int i, Rikishi f)
    {
        var body = Phys3d.phys3d_body(world, "rikishi:" + i, new BodyDesc3d
        {
            type = Phys3d.DYNAMIC,
            version = f.gen,
            linearDamping = 0.1,
            angularDamping = 0.5,
            // yaw を封じる: capsule は回転対称なので物理には影響せず、
            // 見た目の向き (相手に正対) をレンダリング側で自由に決められる
            motionLocks = new MotionLocks3d { angular_y = true },
            initial = new InitialState3d { x = f.homeX, y = TOP_Y + 0.02, z = 0.0 },
        });
        if (body == null)
            return null;
        Phys3d.phys3d_capsule(body, "solid", new CapsuleDesc3d
        {
            version = f.gen,
            a = new Vec3d { x = 0.0, y = CAP_R, z = 0.0 },
            b = new Vec3d { x = 0.0, y = 0.95, z = 0.0 },
            r = CAP_R,
            density = 1.0,
            // 高摩擦: 足が滑るより先に体が傾くように (押し倒しが決まる条件)。
            // 移動の自由は空中 (トントンで跳ねた瞬間) にある。
            friction = 0.85,
            contact = true,
        });
        return body;
    }

    // 決定論ハッシュ乱数 (シードは frame と力士 index)。リプレイ可能。
    static double rand01(int n)
    {
        double x = Math.Sin(n * 12.9898) * 43758.5453;
        return x - Math.Floor(x);
    }

    // 戦術選択。反応間隔 (tacticUntil) ごとに再判断する。
    // - 相手が深く前傾 → counter 確率で引き/いなし (支えを外す)
    // - 背中が土俵際 → いなしで軸をずらす
    // - まれに「ため」、基本は押し
    static void decide(int i, Rikishi f, Pose3d pose, Pose3d op, Vec3 dir,
        bool engaged)
    {
        if (frame < f.tacticUntil)
            return;
        var opQ = new Quat(op.qx, op.qy, op.qz, op.qw);
        var opUp = opQ * Vec3.up();
        double leanToMe = -(opUp.x * dir.x + opUp.z * dir.z); // 相手の前傾のこちら成分
        double pushedBack = -(pose.vx * dir.x + pose.vz * dir.z); // 押し込まれ速度
        double rr = Math.Sqrt(pose.x * pose.x + pose.z * pose.z);
        double backToEdge =
            rr > 0.01 ? -(pose.x * dir.x + pose.z * dir.z) / rr : 0.0;
        double edgeDanger = backToEdge > 0.0 ? rr / DOHYO_R * backToEdge : 0.0;
        double r = rand01(frame * 97 + i * 1013);
        // 引き/いなしの好機: 相手が前傾している、押し込まれている、または賭け
        double chance = (leanToMe > 0.10 ? f.counter : 0.0)
            + (pushedBack > 0.12 ? f.counter * 0.8 : 0.0) + f.counter * 0.15;
        if (engaged && r < chance && edgeDanger < 0.55)
        {
            f.tactic = rand01(frame * 131 + i * 71) < 0.5 ? TA_HIKI : TA_INASHI;
            f.tacticUntil = frame + 18;
            f.sideSign = rand01(frame * 193 + i * 37) < 0.5 ? -1.0 : 1.0;
        }
        else if (edgeDanger > 0.62 && r < 0.8)
        {
            f.tactic = TA_INASHI;
            f.tacticUntil = frame + 16;
            f.sideSign = rand01(frame * 193 + i * 37) < 0.5 ? -1.0 : 1.0;
        }
        else if (r > 0.9)
        {
            f.tactic = TA_TAME;
            f.tacticUntil = frame + 12;
        }
        else
        {
            f.tactic = TA_OSU;
            f.tacticUntil = frame + 18 + (int)Math.Floor(r * 22.0);
        }
    }

    // 姿勢 PD (倒立振子)。up を world up へ立て直すトルク + 角速度ダンピング。
    // その上に状態別の操舵: 仕切り中は定位置ばね、勝負中は戦術に従う。
    static void controlRikishi(int i, Rikishi f, BodyRef3d body, BodyRef3d opp)
    {
        var pose = Phys3d.phys3d_pose(body);
        if (pose == null)
            return;
        var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
        var up = q * Vec3.up();
        var lean = up.cross(Vec3.up()); // |lean| = sin(傾き)、方向 = 立て直す回転軸
        var spring = lean * balKp;
        double mag = spring.length();
        // 筋力上限 (超えた傾きは救えない)。「ため」中は腰を落として踏ん張る
        double maxEff = f.tactic == TA_TAME && state == ST_FIGHT
            ? balMax * 1.5 : balMax;
        if (mag > maxEff)
            spring = spring * (maxEff / mag);
        Phys3d.phys3d_add_torque(body, new Vec3d
        {
            x = spring.x - pose.wx * balKd,
            y = 0.0, // yaw は motionLocks で封じている
            z = spring.z - pose.wz * balKd,
        });
        // 倒れている間は操舵しない (勝敗の余韻でジタバタさせない)
        if (up.y < 0.5)
            return;
        if (state == ST_FIGHT)
        {
            var op = Phys3d.phys3d_pose(opp);
            if (op == null)
                return;
            var toOpp = new Vec3(op.x - pose.x, 0.0, op.z - pose.z);
            double dist = toOpp.length();
            var dir = toOpp.normalize();
            bool engaged = dist < 2.0 * CAP_R + 0.14;
            decide(i, f, pose, op, dir, engaged);
            if (f.tactic == TA_HIKI)
            {
                // 支えを外す。前傾した相手はつんのめって落ちる (引き落とし)
                Phys3d.phys3d_add_force_center(body, new Vec3d
                {
                    x = (-dir.x * 1.8 - pose.vx) * hikiK,
                    y = 0.0,
                    z = (-dir.z * 1.8 - pose.vz) * hikiK,
                });
                // はたき込み: 組んだまま引くときは相手の上体をこちらへ引き倒す
                if (dist < 2.0 * CAP_R + 0.55)
                {
                    var opQ = new Quat(op.qx, op.qy, op.qz, op.qw);
                    var opUp = opQ * Vec3.up();
                    var pullAxis = opUp.cross(-1.0 * dir); // 相手の up をこちらへ倒す軸
                    Phys3d.phys3d_add_torque(opp, new Vec3d
                    {
                        x = pullAxis.x * hatakiK,
                        y = 0.0,
                        z = pullAxis.z * hatakiK,
                    });
                }
            }
            else if (f.tactic == TA_INASHI)
            {
                // 横へかわす。押しの軸を外して空振りさせる
                var side = new Vec3(-dir.z * f.sideSign, 0.0, dir.x * f.sideSign);
                Phys3d.phys3d_add_force_center(body, new Vec3d
                {
                    x = (side.x * 2.3 - pose.vx) * inashiK,
                    y = 0.0,
                    z = (side.z * 2.3 - pose.vz) * inashiK,
                });
                // かわしながら相手の突進を前へ転がす (突き落とし)
                if (dist < 2.0 * CAP_R + 0.55)
                {
                    var opQ = new Quat(op.qx, op.qy, op.qz, op.qw);
                    var opUp = opQ * Vec3.up();
                    var rollAxis = opUp.cross(-1.0 * dir);
                    Phys3d.phys3d_add_torque(opp, new Vec3d
                    {
                        x = rollAxis.x * hatakiK * 0.7,
                        y = 0.0,
                        z = rollAxis.z * hatakiK * 0.7,
                    });
                }
            }
            else if (f.tactic == TA_TAME)
            {
                // 直立で耐える。詰めも押しもしない
                Phys3d.phys3d_add_force_center(body, new Vec3d
                {
                    x = -pose.vx * seekK,
                    y = 0.0,
                    z = -pose.vz * seekK,
                });
            }
            else
            {
                // 押す: 前進方向にはブレーキをかけない速度サーボ。押し込み中に
                // 相手が消えても止まれない = 突っ込むリスクが物理に乗る
                double vAlong = pose.vx * dir.x + pose.vz * dir.z;
                double drive =
                    vAlong < walkSpeed ? (walkSpeed - vAlong) * seekK : 0.0;
                double perpX = pose.vx - dir.x * vAlong;
                double perpZ = pose.vz - dir.z * vAlong;
                Phys3d.phys3d_add_force_center(body, new Vec3d
                {
                    x = dir.x * drive - perpX * seekK,
                    y = 0.0,
                    z = dir.z * drive - perpZ * seekK,
                });
                if (engaged)
                {
                    // 「のこった」の脈動で前傾して押し込む。重心を相手に預ける
                    double pulse = Math.Max(0.0, Math.Sin(
                        frame * DT * f.pulseHz * 2.0 * Math.PI + f.phase));
                    Phys3d.phys3d_add_force_center(body, new Vec3d
                    {
                        x = dir.x * f.pushK * pulse,
                        y = 0.0,
                        z = dir.z * f.pushK * pulse,
                    });
                    var leanAxis = up.cross(dir); // up を dir へ倒す = 前傾
                    Phys3d.phys3d_add_torque(body, new Vec3d
                    {
                        x = leanAxis.x * f.leanK * pulse,
                        y = 0.0,
                        z = leanAxis.z * f.leanK * pulse,
                    });
                }
            }
        }
        else
        {
            Phys3d.phys3d_add_force_center(body, new Vec3d
            {
                x = (f.homeX - pose.x) * holdK - pose.vx * holdKd,
                y = 0.0,
                z = (0.0 - pose.z) * holdK - pose.vz * holdKd,
            });
        }
    }

    // 勝敗判定と取組フロー。負け = 土俵上面から落ちた or 倒れたまま起きない。
    static void judge(List<BodyRef3d> bodies)
    {
        if (state == ST_SHIKIRI)
        {
            stateT--;
            if (stateT <= 0)
            {
                state = ST_FIGHT;
                fightStart = frame;
                engagedPrev = false;
                Audio.audio_play(Sfx.blip(2400, 2100, 0.05, 0.35)); // 拍子木
            }
        }
        else if (state == ST_FIGHT)
        {
            if (frame == fightStart + 9)
                Audio.audio_play(Sfx.blip(2400, 2100, 0.05, 0.35),
                    new PlayOpts { pitch = 0.93 });
            var lost = new bool[] { false, false };
            var lostOut = new bool[] { false, false };
            for (int i = 0; i < fighters.Count; i++)
            {
                var f = fighters[i];
                var pose = Phys3d.phys3d_pose(bodies[i]);
                if (pose == null)
                    continue;
                var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
                var up = q * Vec3.up();
                if (up.y < 0.5) // 60° = 筋力上限の臨界角より深い。もう戻れない
                    f.downFrames++;
                else
                    f.downFrames = 0;
                lostOut[i] = pose.y < 0.35
                    || pose.x * pose.x + pose.z * pose.z > DOHYO_R * DOHYO_R;
                lost[i] = lostOut[i] || f.downFrames > 20;
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
                    int wt = fighters[winner].tactic;
                    if (wt == TA_HIKI)
                        kimarite = wentOut ? "ひきおとし" : "はたきこみ";
                    else if (wt == TA_INASHI)
                        kimarite = wentOut ? "おくりだし" : "つきおとし";
                    else
                        kimarite = wentOut ? "おしだし" : "おしたおし";
                }
                state = ST_KIMARI;
                stateT = 90;
                shake = 1.0;
                Audio.audio_play(Sfx.noise(0.7, 0.28, 0xbeef)); // 歓声がわり
                Audio.audio_play(Sfx.blip(520, 780, 0.22, 0.22));
                if (auto)
                    Console.WriteLine("tonton: winner=" + winner
                        + " kimarite=" + kimarite + " stars=[" + stars[0] + ","
                        + stars[1] + "] frame=" + frame);
            }
        }
        else if (state == ST_KIMARI)
        {
            stateT--;
            if (stateT <= 0)
            {
                if (winner >= 0)
                    stars[winner]++;
                foreach (var f in fighters)
                {
                    f.gen++;
                    f.downFrames = 0;
                }
                winner = -1;
                state = ST_SHIKIRI;
                stateT = 45;
            }
        }
    }

    // --- input ----------------------------------------------------------------
    // クリック (押しっぱなしは連打) = トントン。カメラ ray を土俵上面の平面と
    // 交差させ、土俵の中なら下向き impulse。土俵が傾いていても上面 "あたり" に
    // 打てれば十分なので平面近似で済ませる。
    static void tapAt(BodyRef3d dohyo, double px, double pz)
    {
        lastTap = frame;
        Audio.audio_play(Sfx.blip(150, 45, 0.09, 0.5)); // トントンの「ドンッ」
        Audio.audio_play(Sfx.noise(0.05, 0.18));
        Phys3d.phys3d_add_impulse(dohyo,
            new Vec3d { x = 0.0, y = -tapImpulse, z = 0.0 },
            new CommandOpts3d
            {
                point = new Vec3d { x = px, y = TOP_Y, z = pz },
            });
        markerX = px;
        markerY = TOP_Y;
        markerZ = pz;
        markerT = 10;
        shake = 1.0;
    }

    static void captureTapInput()
    {
        if (auto)
            return;
        bool pressed = Input.mouse_pressed();
        pointerDown = Input.mouse_down();
        if (pressed || pointerDown)
        {
            Input.mouse_pos(out var mx, out var my);
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

    static void updateTap(BodyRef3d dohyo, Vec3 eye, Vec3 target, double fovDeg,
        double aspect, double w, double h)
    {
        // 自動トントン: 決定論の擬似乱数で縁寄りを叩き続ける (勝負中のみ)
        if (auto)
        {
            if (state != ST_FIGHT || imod(frame, 24) != 12)
                return;
            double aa = imod(frame * 7919, 628) / 100.0;
            double rr = DOHYO_R * (0.45 + imod(frame * 337, 50) / 100.0);
            tapAt(dohyo, Math.Cos(aa) * rr, Math.Sin(aa) * rr);
            return;
        }
        if (state != ST_FIGHT)
        {
            // 仕切り・余韻中の pressed は次の取組に持ち越さない。
            pendingTap = false;
            return;
        }
        bool pressed = pendingTap;
        double sx = pressed ? pendingTapX : pointerX;
        double sy = pressed ? pendingTapY : pointerY;
        pendingTap = false;
        bool tap = pressed || (pointerDown && frame - lastTap >= tapRepeat);
        if (!tap)
            return;
        double ndcX = sx / w * 2.0 - 1.0;
        double ndcY = 1.0 - sy / h * 2.0;
        var fwd = (target - eye).normalize();
        var right = Vec3.up().cross(fwd).normalize();
        var upv = fwd.cross(right);
        double tanH = Math.Tan(fovDeg * Math.PI / 360.0);
        var dir = (fwd + right * (ndcX * tanH * aspect) + upv * (ndcY * tanH))
            .normalize();
        if (dir.y > -0.001)
            return; // 上を向いた ray は土俵に届かない
        double t = (TOP_Y - eye.y) / dir.y;
        var p = eye + dir * t;
        if (p.x * p.x + p.z * p.z > DOHYO_R * DOHYO_R * 1.1)
            return;
        tapAt(dohyo, p.x, p.z);
    }

    static void tick(double aspect, double w, double h)
    {
        // 従来の render frame 冒頭 / 末尾にあった演出カウントを
        // 60 Hz で進める。この後の tap / judge で始まる演出は全強度で描く。
        if (shake > 0.001)
            shake = shake * 0.85;
        if (markerT > 0)
            markerT = markerT - 1;
        renderFrame = frame;
        var tickEye = new Vec3(Math.Sin(frame * 1.7) * 0.05 * shake,
            3.6 + Math.Sin(frame * 2.3) * 0.03 * shake, -5.4);
        renderEye = tickEye;
        var lookAt = new Vec3(0.0, 0.4, 0.0);
        double fovDeg = 40.0;

        var nextWorld = declareWorld();
        if (nextWorld == null)
            return;
        world = nextWorld;
        declareStatics(nextWorld);
        var dohyo = declareDohyo(nextWorld);
        if (dohyo == null)
            return;
        controlDohyo(dohyo);
        var bodies = new List<BodyRef3d>();
        for (int i = 0; i < fighters.Count; i++)
        {
            var body = declareRikishi(nextWorld, i, fighters[i]);
            if (body == null)
                return;
            bodies.Add(body);
        }
        for (int i = 0; i < fighters.Count; i++)
            controlRikishi(i, fighters[i], bodies[i], bodies[1 - i]);
        judge(bodies);
        updateTap(dohyo, tickEye, lookAt, fovDeg, aspect, w, h);

        Phys3d.phys3d_step(nextWorld, DT);

        // 立ち合いのぶつかり (接触のエッジで音と振動)
        {
            var p0 = Phys3d.phys3d_pose(bodies[0]);
            var p1 = Phys3d.phys3d_pose(bodies[1]);
            if (state == ST_FIGHT && p0 != null && p1 != null)
            {
                double ddx = p1.x - p0.x;
                double ddz = p1.z - p0.z;
                double lim = 2.0 * CAP_R + 0.14;
                bool eng = ddx * ddx + ddz * ddz < lim * lim;
                if (eng && !engagedPrev)
                {
                    Audio.audio_play(Sfx.noise(0.12, 0.45));
                    Audio.audio_play(Sfx.blip(90, 55, 0.07, 0.3));
                    if (shake < 0.6)
                        shake = 0.6;
                }
                engagedPrev = eng;
            }
        }

        // 着地スカッシュの検出と減衰も logical frame で進める。
        for (int i = 0; i < fighters.Count; i++)
        {
            var f = fighters[i];
            var pose = Phys3d.phys3d_pose(bodies[i]);
            if (pose == null)
                continue;
            if (f.prevVy < -1.2 && pose.vy > f.prevVy + 0.8)
                f.squashT = 8;
            f.prevVy = pose.vy;
            if (f.squashT > 0)
                f.squashT = f.squashT - 1;
        }

        frame = frame + 1;
    }

    // --- rendering -------------------------------------------------------------

    public static void onFrame(double dt)
    {
        var renNow = ren ?? new Renderer3d("tt22");
        ren = renNow;
        var cubeNow = cubeMesh;
        var cylNow = cylMesh;
        if (cubeNow == null || cylNow == null)
        {
            cubeNow = new Mesh3d("tt_cube");
            cubeNow.rebuild(Shapes3d.cube());
            cylNow = new Mesh3d("tt_cyl");
            cylNow.rebuild(Shapes3d.cylinder(28));
            cubeMesh = cubeNow;
            cylMesh = cylNow;
        }
        var darumaNow = darumaMesh
            ?? new Mesh3d[] { new Mesh3d("tt_daruma0"), new Mesh3d("tt_daruma1") };
        darumaMesh = darumaNow;

        Gfx.size(out var sw, out var sh);
        double aspect = (double)sw / sh;
        double fovDeg = 40.0;
        var lookAt = new Vec3(0.0, 0.4, 0.0);

        captureTapInput();
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.frame(dt, _ => tick(aspect, sw, sh));

        var eyeNow = renderEye;
        if (eyeNow == null)
        {
            eyeNow = new Vec3(0.0, 3.6, -5.4);
            renderEye = eyeNow;
        }

        // --- draw ---
        // 屋外の明るい昼 (だるまの色がよく出るように空色強め)
        renNow.light.dir = new Vec3(-0.4, 1.0, -0.55);
        renNow.sky.top = Color.rgb(0.45, 0.52, 0.62);
        renNow.sky.bottom = Color.rgb(0.16, 0.14, 0.13);
        renNow.background = Color.rgb(0.05, 0.05, 0.08);
        renNow.shadow.center = new Vec3(0, 0.3, 0);
        renNow.shadow.extent = 3.5;
        renNow.begin(new Camera
        {
            eye = eyeNow,
            target = lookAt,
            fov = fovDeg,
            near = 0.1,
            far = 50.0,
        });

        // 地面と台座
        renNow.draw(cubeNow, Mat4.translate(new Vec3(0, -0.5, 0))
            * Mat4.scale(new Vec3(8, 0.5, 8)),
            new Draw3dOpts { tint = Color.rgb(0.10, 0.10, 0.13) });
        renNow.draw(cylNow, Mat4.translate(new Vec3(0, 0.15, 0))
            * Mat4.scale(new Vec3(1.7, 0.3, 1.7)),
            new Draw3dOpts { tint = Color.rgb(0.16, 0.15, 0.19) });

        // 土俵 (懸架で傾く)。上面に俵の白リングと仕切り線を重ねる。
        var drawWorld = world;
        Pose3d? dp = null;
        if (drawWorld != null)
            dp = Phys3d.phys3d_pose(drawWorld, "dohyo");
        if (dp != null)
        {
            var dm = Renderer3d.poseMat(dp);
            renNow.draw(cylNow, dm * Mat4.scale(new Vec3(DOHYO_R, DOHYO_H, DOHYO_R)),
                new Draw3dOpts { tint = Color.rgb(0.72, 0.55, 0.38) });
            double topLocal = DOHYO_H * 0.5;
            renNow.draw(cylNow, dm * Mat4.translate(new Vec3(0, topLocal + 0.005, 0))
                * Mat4.scale(new Vec3(DOHYO_R * 0.98, 0.01, DOHYO_R * 0.98)),
                new Draw3dOpts { tint = Color.rgb(0.92, 0.88, 0.78) });
            renNow.draw(cylNow, dm * Mat4.translate(new Vec3(0, topLocal + 0.015, 0))
                * Mat4.scale(new Vec3(DOHYO_R * 0.86, 0.01, DOHYO_R * 0.86)),
                new Draw3dOpts { tint = Color.rgb(0.72, 0.55, 0.38) });
            foreach (var sx in new double[] { -0.22, 0.22 })
                renNow.draw(cubeNow,
                    dm * Mat4.translate(new Vec3(sx, topLocal + 0.025, 0))
                    * Mat4.scale(new Vec3(0.02, 0.004, 0.3)),
                    new Draw3dOpts { tint = Color.rgb(0.92, 0.88, 0.78) });
        }

        // 力士: SDF だるま (skinning + 手続きボーンアニメ)。物理の pose に
        // 相手への正対 yaw と着地スカッシュを重ねる。
        ensureDaruma(darumaNow);
        for (int i = 0; i < fighters.Count; i++)
        {
            var f = fighters[i];
            var mesh = darumaNow[i];
            if (drawWorld == null)
                continue;
            var pose = Phys3d.phys3d_pose(drawWorld, "rikishi:" + i);
            if (pose == null || !mesh.ready())
                continue;
            double sq = f.squashT / 8.0 * 0.22;
            var op = Phys3d.phys3d_pose(drawWorld, "rikishi:" + (1 - i));
            double fx = op != null ? op.x - pose.x : -pose.x;
            double fz = op != null ? op.z - pose.z : -pose.z;
            double yaw = Math.Atan2(fx, fz); // model の -Z を相手へ向ける
            var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
            var upv = q * Vec3.up();
            bool falling = upv.y < 0.6;
            double pulse = f.tactic == TA_OSU
                ? Math.Max(0.0, Math.Sin(
                    renderFrame * DT * f.pulseHz * 2.0 * Math.PI + f.phase))
                : 0.0;
            var model = Renderer3d.poseMat(pose) * Mat4.rotateY(yaw)
                * Mat4.scale(new Vec3(1.0 + sq * 0.6, 1.0 - sq, 1.0 + sq * 0.6));
            renNow.draw(mesh, model, new Draw3dOpts
            {
                bones = packBones(i, f, falling, pulse, renderFrame, mesh.data),
            });
        }

        // トントンのマーカー (打った場所に一瞬リング。高輝度で bloom に乗る)
        if (markerT > 0)
        {
            double k = markerT / 10.0;
            renNow.draw(cylNow,
                Mat4.translate(new Vec3(markerX, markerY + 0.03, markerZ))
                * Mat4.scale(new Vec3(0.22 * (2.0 - k), 0.01, 0.22 * (2.0 - k))),
                new Draw3dOpts { tint = Color.rgb(1.6, 1.5, 0.9) });
        }

        renNow.End();

        // --- テキスト (かな): tonemap 後の swapchain に重ね描き ---
        Gfx.begin_pass(new PassOpts { target = Gfx.main_tex, load = Gfx.LOAD });
        var mt = ensureText();
        if (mt != null)
        {
            var cream = Color.rgb(0.95, 0.92, 0.85);
            mt.textCentered("あか　" + stars[0] + " - " + stars[1] + "　あお",
                W * 0.5, 348, 20, cream);
            if (state == ST_FIGHT)
            {
                if (renderFrame - fightStart < 50)
                    mt.textCentered("はっけよい", W * 0.5, 120, 44,
                        Color.rgb(0.98, 0.85, 0.4));
                // 思考の可視化: 頭上に現在の戦術
                var vp = renNow.viewProj;
                for (int i = 0; i < fighters.Count; i++)
                {
                    if (vp == null)
                        continue;
                    var f = fighters[i];
                    if (drawWorld == null)
                        continue;
                    var pose = Phys3d.phys3d_pose(drawWorld, "rikishi:" + i);
                    if (pose == null)
                        continue;
                    var sp = screenPos(vp, pose.x, pose.y + 1.5, pose.z);
                    if (!sp.ok)
                        continue;
                    string label;
                    Color tint;
                    if (f.tactic == TA_HIKI)
                    {
                        label = "ひく";
                        tint = Color.rgb(0.35, 0.9, 0.9);
                    }
                    else if (f.tactic == TA_INASHI)
                    {
                        label = "いなす";
                        tint = Color.rgb(0.45, 0.9, 0.45);
                    }
                    else if (f.tactic == TA_TAME)
                    {
                        label = "ためる";
                        tint = Color.rgb(0.75, 0.75, 0.78);
                    }
                    else
                    {
                        label = "おす";
                        tint = Color.rgb(1.0, 0.66, 0.25);
                    }
                    mt.textCentered(label, sp.x, sp.y, 16, tint);
                }
            }
            if (state == ST_KIMARI)
            {
                if (winner >= 0)
                {
                    var wf = fighters[winner];
                    mt.textCentered(wf.name + "のかち", W * 0.5, 112, 36,
                        Color.rgb(wf.color[0] * 0.4 + 0.6,
                            wf.color[1] * 0.4 + 0.6, wf.color[2] * 0.4 + 0.6));
                    mt.textCentered(kimarite, W * 0.5, 150, 24, cream);
                }
                else
                {
                    mt.textCentered("とりなおし", W * 0.5, 124, 32, cream);
                }
            }
        }

        Gfx.end_pass();
    }
}
