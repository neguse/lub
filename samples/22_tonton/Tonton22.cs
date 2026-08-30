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

using System;
using System.Collections.Generic;

/// <summary>力士 1 体 (Haxe 版 typedef Rikishi と対)。</summary>
public class Rikishi
{
    public int gen; // 再宣言 (respawn) 用 version
    public string name = ""; // 四股名 (かな)
    public float homeX;
    public float[] color = new float[] { 1.0f, 1.0f, 1.0f, 1.0f };
    public int bodyRgb; // SDF に焼く体色
    public int downFrames;
    public int squashT; // 着地スカッシュの残りフレーム
    public float prevVy;
    // --- 個性 ---
    // 押し合いは「相手が支え」の安定構造なので、押すだけでは永遠に倒れない。
    // 決着は支えを外す瞬間 (引き・いなし) に生まれる。個性はその使い分け。
    public float pushK; // 押しの強さ
    public float leanK; // 前傾の深さ (リスク: 支えを外されると帰れない)
    public float pulseHz; // 押しの脈動周期
    public float phase;
    public float counter; // 相手の深い前傾に引き/いなしを合わせる確率
    // --- 戦術状態 ---
    public int tactic;
    public int tacticUntil;
    public float sideSign; // いなしの回り込み方向
}

/// <summary>world 座標 → 論理スクリーン座標 (Haxe 版の匿名構造体戻り)。</summary>
public class ScreenPos
{
    public float x;
    public float y;
    public bool ok;
}

public static class Tonton22
{
    const int W = 640;
    const int H = 360;
    const float DT = 1.0f / 60.0f;

    const float DOHYO_R = 2.2f;
    const float DOHYO_H = 0.4f;
    const float DOHYO_Y = 0.6f; // 懸架時の中心高
    const float TOP_Y = DOHYO_Y + DOHYO_H * 0.5f;
    const float CAP_R = 0.35f;

    // 土俵の質量と傾き慣性 (cylinder の解析値)。懸架 PD のゲインを
    // 「共振周波数 Hz と減衰比」で書くために使う。
    const float DOHYO_MASS = 3.14159f * DOHYO_R * DOHYO_R * DOHYO_H;
    const float DOHYO_I_TILT =
        DOHYO_MASS * (3.0f * DOHYO_R * DOHYO_R + DOHYO_H * DOHYO_H) / 12.0f;

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
    static float hikiK = 5.0f; // 引きの後退力
    static float inashiK = 5.5f; // いなしの横力
    static float hatakiK = 3.2f; // はたき込み (引き際に相手上体を引き倒すトルク)

    // 赤 = 突貫 (強く深く押すが、引きに合わされやすい)
    // 青 = 後の先 (押しは控えめ、相手の前傾に引き/いなしを合わせる)
    static List<Rikishi> fighters = new List<Rikishi>
    {
        new Rikishi
        {
            gen = 1,
            name = "あか",
            homeX = -0.9f,
            color = new float[] { 0.86f, 0.28f, 0.24f, 1.0f },
            bodyRgb = 0xC94434,
            downFrames = 0,
            squashT = 0,
            prevVy = 0.0f,
            pushK = 9.0f,
            leanK = 2.0f,
            pulseHz = 2.2f,
            phase = 0.0f,
            counter = 0.25f,
            tactic = TA_OSU,
            tacticUntil = 0,
            sideSign = 1.0f,
        },
        new Rikishi
        {
            gen = 1,
            name = "あお",
            homeX = 0.9f,
            color = new float[] { 0.27f, 0.47f, 0.88f, 1.0f },
            bodyRgb = 0x3E6ED8,
            downFrames = 0,
            squashT = 0,
            prevVy = 0.0f,
            pushK = 7.5f,
            leanK = 1.2f,
            pulseHz = 1.6f,
            phase = 2.1f,
            counter = 0.7f,
            tactic = TA_OSU,
            tacticUntil = 0,
            sideSign = -1.0f,
        },
    };

    // LUB_TONTON_AUTO=1 で自動トントン (ヘッドレス検証・デモ自走用)
    static bool auto = os.getenv("LUB_TONTON_AUTO") != null;

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
        var body = Sdf.sphere(0.50f).move(0, 0.52f, 0)
            .bone("body", new Vec3(0, 0.25f, 0));
        var head = Sdf.sphere(0.30f).move(0, 1.02f, 0)
            .bone("head", new Vec3(0, 0.80f, 0));
        var armL = Sdf.capsule(new Vec3(0.40f, 0.76f, -0.06f),
            new Vec3(0.60f, 0.40f, -0.30f), 0.11f)
            .bone("arm_l", new Vec3(0.40f, 0.76f, -0.06f));
        var armR = Sdf.capsule(new Vec3(-0.40f, 0.76f, -0.06f),
            new Vec3(-0.60f, 0.40f, -0.30f), 0.11f)
            .bone("arm_r", new Vec3(-0.40f, 0.76f, -0.06f));
        var trunk = body.smin(head, 0.12f).smin(armL, 0.06f).smin(armR, 0.06f)
            .paint(bodyRgb);
        // 顔: 肌色の球を頭前面に沈めて smin (だるまの顔窓)
        var face = Sdf.sphere(0.20f).move(0, 1.02f, -0.17f).paint(0xF2D1AC);
        // まわし: 白帯の torus
        var mawashi = Sdf.torus(0.40f, 0.10f).move(0, 0.24f, 0).paint(0xF2EEDC);
        var eye = Sdf.sphere(0.05f).move(0.10f, 1.08f, -0.30f).mirrorX()
            .paint(0x241F1F, 0.0f, 0.2f);
        return trunk.smin(face, 0.04f).union(mawashi).union(eye);
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
    static List<float> packBones(int mi, Rikishi f, bool falling, float pulse,
        int logicalFrame, MeshData? data)
    {
        float t = logicalFrame * DT;
        float armSwing;
        if (falling)
            armSwing = (float)Math.Sin(t * 16.0f + mi * 2.1f) * 0.9f;
        else if (f.tactic == TA_HIKI || f.tactic == TA_INASHI)
            armSwing = 0.7f;
        else
            armSwing = -0.55f * pulse + (float)Math.Sin(t * 2.3f + mi) * 0.08f;
        float nod = falling ? (float)Math.Sin(t * 12.0f) * 0.25f : pulse * 0.16f;
        return Bones.pack(data, (name, x, y, z) =>
        {
            if (name == "arm_l")
                return Bones.pivotRot(x, y, z, Mat4.rotateX(armSwing)
                    .mul(Mat4.rotateZ(
                        falling ? (float)Math.Sin(t * 13.0f) * 0.5f : 0.12f * pulse)));
            if (name == "arm_r")
                return Bones.pivotRot(x, y, z, Mat4.rotateX(armSwing)
                    .mul(Mat4.rotateZ(
                        falling ? -(float)Math.Sin(t * 13.0f) * 0.5f : -0.12f * pulse)));
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
    static ScreenPos screenPos(Mat4 vp, float wx, float wy, float wz)
    {
        var c = vp.mulVec4(new Vec4(wx, wy, wz, 1.0f));
        if (c.w <= 0.001f)
            return new ScreenPos { x = 0.0f, y = 0.0f, ok = false };
        return new ScreenPos
        {
            x = (c.x / c.w + 1.0f) * 0.5f * W,
            y = (1.0f - c.y / c.w) * 0.5f * H,
            ok = true,
        };
    }

    // --- physics -------------------------------------------------------------

    static WorldRef3d? declareWorld()
    {
        var world = Phys3d.phys3d_world("tonton", new WorldOpts3d
        {
            gravity = new Vec3d { x = 0.0f, y = -10.0f, z = 0.0f },
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
            initial = new InitialState3d { x = 0.0f, y = -0.5f, z = 0.0f },
        });
        if (ground != null)
            Phys3d.phys3d_box(ground, "solid", new BoxDesc3d
            {
                hx = 8.0f,
                hy = 0.5f,
                hz = 8.0f,
                friction = 0.7f,
            });
        var baseBody = Phys3d.phys3d_body(world, "base", new BodyDesc3d
        {
            type = Phys3d.STATIC,
            initial = new InitialState3d { x = 0.0f, y = 0.15f, z = 0.0f },
        });
        if (baseBody != null)
            Phys3d.phys3d_cylinder(baseBody, "solid", new CylinderDesc3d
            {
                height = 0.3f,
                radius = 1.7f,
                sides = 24,
                friction = 0.6f,
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
            gravityScale = 0.0f,
            initial = new InitialState3d { x = 0.0f, y = DOHYO_Y, z = 0.0f },
        });
        if (dohyo == null)
            return null;
        Phys3d.phys3d_cylinder(dohyo, "solid", new CylinderDesc3d
        {
            height = DOHYO_H,
            radius = DOHYO_R,
            sides = 28,
            density = 1.0f,
            friction = 0.9f,
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
        float wl = 2.0f * (float)Math.PI * suspLinHz;
        float cl = 2.0f * suspLinDamp * wl;
        Phys3d.phys3d_add_force_center(dohyo, new Vec3d
        {
            x = DOHYO_MASS * (wl * wl * (0.0f - pose.x) - cl * pose.vx),
            y = DOHYO_MASS * (wl * wl * (DOHYO_Y - pose.y) - cl * pose.vy),
            z = DOHYO_MASS * (wl * wl * (0.0f - pose.z) - cl * pose.vz),
        });
        var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
        var up = q * Vec3.up();
        var lean = up.cross(Vec3.up());
        float wa = 2.0f * (float)Math.PI * suspAngHz;
        float ca = 2.0f * suspAngDamp * wa;
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
            linearDamping = 0.1f,
            angularDamping = 0.5f,
            // yaw を封じる: capsule は回転対称なので物理には影響せず、
            // 見た目の向き (相手に正対) をレンダリング側で自由に決められる
            motionLocks = new MotionLocks3d { angular_y = true },
            initial = new InitialState3d { x = f.homeX, y = TOP_Y + 0.02f, z = 0.0f },
        });
        if (body == null)
            return null;
        Phys3d.phys3d_capsule(body, "solid", new CapsuleDesc3d
        {
            version = f.gen,
            a = new Vec3d { x = 0.0f, y = CAP_R, z = 0.0f },
            b = new Vec3d { x = 0.0f, y = 0.95f, z = 0.0f },
            r = CAP_R,
            density = 1.0f,
            // 高摩擦: 足が滑るより先に体が傾くように (押し倒しが決まる条件)。
            // 移動の自由は空中 (トントンで跳ねた瞬間) にある。
            friction = 0.85f,
            contact = true,
        });
        return body;
    }

    // 決定論ハッシュ乱数 (シードは frame と力士 index)。リプレイ可能。
    static float rand01(int n)
    {
        float x = (float)Math.Sin(n * 12.9898f) * 43758.5453f;
        return x - (float)Math.Floor(x);
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
        float leanToMe = -(opUp.x * dir.x + opUp.z * dir.z); // 相手の前傾のこちら成分
        float pushedBack = -(pose.vx * dir.x + pose.vz * dir.z); // 押し込まれ速度
        float rr = (float)Math.Sqrt(pose.x * pose.x + pose.z * pose.z);
        float backToEdge =
            rr > 0.01f ? -(pose.x * dir.x + pose.z * dir.z) / rr : 0.0f;
        float edgeDanger = backToEdge > 0.0f ? rr / DOHYO_R * backToEdge : 0.0f;
        float r = rand01(frame * 97 + i * 1013);
        // 引き/いなしの好機: 相手が前傾している、押し込まれている、または賭け
        float chance = (leanToMe > 0.10f ? f.counter : 0.0f)
            + (pushedBack > 0.12f ? f.counter * 0.8f : 0.0f) + f.counter * 0.15f;
        if (engaged && r < chance && edgeDanger < 0.55f)
        {
            f.tactic = rand01(frame * 131 + i * 71) < 0.5f ? TA_HIKI : TA_INASHI;
            f.tacticUntil = frame + 18;
            f.sideSign = rand01(frame * 193 + i * 37) < 0.5f ? -1.0f : 1.0f;
        }
        else if (edgeDanger > 0.62f && r < 0.8f)
        {
            f.tactic = TA_INASHI;
            f.tacticUntil = frame + 16;
            f.sideSign = rand01(frame * 193 + i * 37) < 0.5f ? -1.0f : 1.0f;
        }
        else if (r > 0.9f)
        {
            f.tactic = TA_TAME;
            f.tacticUntil = frame + 12;
        }
        else
        {
            f.tactic = TA_OSU;
            f.tacticUntil = frame + 18 + (int)Math.Floor(r * 22.0f);
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
        float mag = spring.length();
        // 筋力上限 (超えた傾きは救えない)。「ため」中は腰を落として踏ん張る
        float maxEff = f.tactic == TA_TAME && state == ST_FIGHT
            ? balMax * 1.5f : balMax;
        if (mag > maxEff)
            spring = spring * (maxEff / mag);
        Phys3d.phys3d_add_torque(body, new Vec3d
        {
            x = spring.x - pose.wx * balKd,
            y = 0.0f, // yaw は motionLocks で封じている
            z = spring.z - pose.wz * balKd,
        });
        // 倒れている間は操舵しない (勝敗の余韻でジタバタさせない)
        if (up.y < 0.5f)
            return;
        if (state == ST_FIGHT)
        {
            var op = Phys3d.phys3d_pose(opp);
            if (op == null)
                return;
            var toOpp = new Vec3(op.x - pose.x, 0.0f, op.z - pose.z);
            float dist = toOpp.length();
            var dir = toOpp.normalize();
            bool engaged = dist < 2.0f * CAP_R + 0.14f;
            decide(i, f, pose, op, dir, engaged);
            if (f.tactic == TA_HIKI)
            {
                // 支えを外す。前傾した相手はつんのめって落ちる (引き落とし)
                Phys3d.phys3d_add_force_center(body, new Vec3d
                {
                    x = (-dir.x * 1.8f - pose.vx) * hikiK,
                    y = 0.0f,
                    z = (-dir.z * 1.8f - pose.vz) * hikiK,
                });
                // はたき込み: 組んだまま引くときは相手の上体をこちらへ引き倒す
                if (dist < 2.0f * CAP_R + 0.55f)
                {
                    var opQ = new Quat(op.qx, op.qy, op.qz, op.qw);
                    var opUp = opQ * Vec3.up();
                    var pullAxis = opUp.cross(-1.0f * dir); // 相手の up をこちらへ倒す軸
                    Phys3d.phys3d_add_torque(opp, new Vec3d
                    {
                        x = pullAxis.x * hatakiK,
                        y = 0.0f,
                        z = pullAxis.z * hatakiK,
                    });
                }
            }
            else if (f.tactic == TA_INASHI)
            {
                // 横へかわす。押しの軸を外して空振りさせる
                var side = new Vec3(-dir.z * f.sideSign, 0.0f, dir.x * f.sideSign);
                Phys3d.phys3d_add_force_center(body, new Vec3d
                {
                    x = (side.x * 2.3f - pose.vx) * inashiK,
                    y = 0.0f,
                    z = (side.z * 2.3f - pose.vz) * inashiK,
                });
                // かわしながら相手の突進を前へ転がす (突き落とし)
                if (dist < 2.0f * CAP_R + 0.55f)
                {
                    var opQ = new Quat(op.qx, op.qy, op.qz, op.qw);
                    var opUp = opQ * Vec3.up();
                    var rollAxis = opUp.cross(-1.0f * dir);
                    Phys3d.phys3d_add_torque(opp, new Vec3d
                    {
                        x = rollAxis.x * hatakiK * 0.7f,
                        y = 0.0f,
                        z = rollAxis.z * hatakiK * 0.7f,
                    });
                }
            }
            else if (f.tactic == TA_TAME)
            {
                // 直立で耐える。詰めも押しもしない
                Phys3d.phys3d_add_force_center(body, new Vec3d
                {
                    x = -pose.vx * seekK,
                    y = 0.0f,
                    z = -pose.vz * seekK,
                });
            }
            else
            {
                // 押す: 前進方向にはブレーキをかけない速度サーボ。押し込み中に
                // 相手が消えても止まれない = 突っ込むリスクが物理に乗る
                float vAlong = pose.vx * dir.x + pose.vz * dir.z;
                float drive =
                    vAlong < walkSpeed ? (walkSpeed - vAlong) * seekK : 0.0f;
                float perpX = pose.vx - dir.x * vAlong;
                float perpZ = pose.vz - dir.z * vAlong;
                Phys3d.phys3d_add_force_center(body, new Vec3d
                {
                    x = dir.x * drive - perpX * seekK,
                    y = 0.0f,
                    z = dir.z * drive - perpZ * seekK,
                });
                if (engaged)
                {
                    // 「のこった」の脈動で前傾して押し込む。重心を相手に預ける
                    float pulse = Math.Max(0.0f, (float)Math.Sin(
                        frame * DT * f.pulseHz * 2.0f * (float)Math.PI + f.phase));
                    Phys3d.phys3d_add_force_center(body, new Vec3d
                    {
                        x = dir.x * f.pushK * pulse,
                        y = 0.0f,
                        z = dir.z * f.pushK * pulse,
                    });
                    var leanAxis = up.cross(dir); // up を dir へ倒す = 前傾
                    Phys3d.phys3d_add_torque(body, new Vec3d
                    {
                        x = leanAxis.x * f.leanK * pulse,
                        y = 0.0f,
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
                y = 0.0f,
                z = (0.0f - pose.z) * holdK - pose.vz * holdKd,
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
                Audio.audio_play(Sfx.blip(2400, 2100, 0.05f, 0.35f)); // 拍子木
            }
        }
        else if (state == ST_FIGHT)
        {
            if (frame == fightStart + 9)
                Audio.audio_play(Sfx.blip(2400, 2100, 0.05f, 0.35f),
                    new PlayOpts { pitch = 0.93f });
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
                if (up.y < 0.5f) // 60° = 筋力上限の臨界角より深い。もう戻れない
                    f.downFrames++;
                else
                    f.downFrames = 0;
                lostOut[i] = pose.y < 0.35f
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
                shake = 1.0f;
                Audio.audio_play(Sfx.noise(0.7f, 0.28f, 0xbeef)); // 歓声がわり
                Audio.audio_play(Sfx.blip(520, 780, 0.22f, 0.22f));
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
    static void tapAt(BodyRef3d dohyo, float px, float pz)
    {
        lastTap = frame;
        Audio.audio_play(Sfx.blip(150, 45, 0.09f, 0.5f)); // トントンの「ドンッ」
        Audio.audio_play(Sfx.noise(0.05f, 0.18f));
        Phys3d.phys3d_add_impulse(dohyo,
            new Vec3d { x = 0.0f, y = -tapImpulse, z = 0.0f },
            new CommandOpts3d
            {
                point = new Vec3d { x = px, y = TOP_Y, z = pz },
            });
        markerX = px;
        markerY = TOP_Y;
        markerZ = pz;
        markerT = 10;
        shake = 1.0f;
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

    static void updateTap(BodyRef3d dohyo, Vec3 eye, Vec3 target, float fovDeg,
        float aspect, float w, float h)
    {
        // 自動トントン: 決定論の擬似乱数で縁寄りを叩き続ける (勝負中のみ)
        if (auto)
        {
            if (state != ST_FIGHT || frame % 24 != 12)
                return;
            float aa = (frame * 7919) % 628 / 100.0f;
            float rr = DOHYO_R * (0.45f + (frame * 337) % 50 / 100.0f);
            tapAt(dohyo, (float)Math.Cos(aa) * rr, (float)Math.Sin(aa) * rr);
            return;
        }
        if (state != ST_FIGHT)
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
        var fwd = (target - eye).normalize();
        var right = Vec3.up().cross(fwd).normalize();
        var upv = fwd.cross(right);
        float tanH = (float)Math.Tan(fovDeg * (float)Math.PI / 360.0f);
        var dir = (fwd + right * (ndcX * tanH * aspect) + upv * (ndcY * tanH))
            .normalize();
        if (dir.y > -0.001f)
            return; // 上を向いた ray は土俵に届かない
        float t = (TOP_Y - eye.y) / dir.y;
        var p = eye + dir * t;
        if (p.x * p.x + p.z * p.z > DOHYO_R * DOHYO_R * 1.1f)
            return;
        tapAt(dohyo, p.x, p.z);
    }

    static void tick(float aspect, float w, float h)
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
                float ddx = p1.x - p0.x;
                float ddz = p1.z - p0.z;
                float lim = 2.0f * CAP_R + 0.14f;
                bool eng = ddx * ddx + ddz * ddz < lim * lim;
                if (eng && !engagedPrev)
                {
                    Audio.audio_play(Sfx.noise(0.12f, 0.45f));
                    Audio.audio_play(Sfx.blip(90, 55, 0.07f, 0.3f));
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
            var pose = Phys3d.phys3d_pose(bodies[i]);
            if (pose == null)
                continue;
            if (f.prevVy < -1.2f && pose.vy > f.prevVy + 0.8f)
                f.squashT = 8;
            f.prevVy = pose.vy;
            if (f.squashT > 0)
                f.squashT = f.squashT - 1;
        }

        frame = frame + 1;
    }

    // --- rendering -------------------------------------------------------------

    public static void onFrame(float dt)
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
        float aspect = (float)sw / sh;
        float fovDeg = 40.0f;
        var lookAt = new Vec3(0.0f, 0.4f, 0.0f);

        captureTapInput();
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.frame(dt, _ => tick(aspect, sw, sh));

        var eyeNow = renderEye;
        if (eyeNow == null)
        {
            eyeNow = new Vec3(0.0f, 3.6f, -5.4f);
            renderEye = eyeNow;
        }

        // --- draw ---
        // 屋外の明るい昼 (だるまの色がよく出るように空色強め)
        renNow.light.dir = new Vec3(-0.4f, 1.0f, -0.55f);
        renNow.sky.top = Color.rgb(0.45f, 0.52f, 0.62f);
        renNow.sky.bottom = Color.rgb(0.16f, 0.14f, 0.13f);
        renNow.background = Color.rgb(0.05f, 0.05f, 0.08f);
        renNow.shadow.center = new Vec3(0, 0.3f, 0);
        renNow.shadow.extent = 3.5f;
        renNow.begin(new Camera
        {
            eye = eyeNow,
            target = lookAt,
            fov = fovDeg,
            near = 0.1f,
            far = 50.0f,
        });

        // 地面と台座
        renNow.draw(cubeNow, Mat4.translate(new Vec3(0, -0.5f, 0))
            * Mat4.scale(new Vec3(8, 0.5f, 8)),
            new Draw3dOpts { tint = Color.rgb(0.10f, 0.10f, 0.13f) });
        renNow.draw(cylNow, Mat4.translate(new Vec3(0, 0.15f, 0))
            * Mat4.scale(new Vec3(1.7f, 0.3f, 1.7f)),
            new Draw3dOpts { tint = Color.rgb(0.16f, 0.15f, 0.19f) });

        // 土俵 (懸架で傾く)。上面に俵の白リングと仕切り線を重ねる。
        var drawWorld = world;
        Pose3d? dp = null;
        if (drawWorld != null)
            dp = Phys3d.phys3d_pose(drawWorld, "dohyo");
        if (dp != null)
        {
            var dm = Renderer3d.poseMat(dp);
            renNow.draw(cylNow, dm * Mat4.scale(new Vec3(DOHYO_R, DOHYO_H, DOHYO_R)),
                new Draw3dOpts { tint = Color.rgb(0.72f, 0.55f, 0.38f) });
            float topLocal = DOHYO_H * 0.5f;
            renNow.draw(cylNow, dm * Mat4.translate(new Vec3(0, topLocal + 0.005f, 0))
                * Mat4.scale(new Vec3(DOHYO_R * 0.98f, 0.01f, DOHYO_R * 0.98f)),
                new Draw3dOpts { tint = Color.rgb(0.92f, 0.88f, 0.78f) });
            renNow.draw(cylNow, dm * Mat4.translate(new Vec3(0, topLocal + 0.015f, 0))
                * Mat4.scale(new Vec3(DOHYO_R * 0.86f, 0.01f, DOHYO_R * 0.86f)),
                new Draw3dOpts { tint = Color.rgb(0.72f, 0.55f, 0.38f) });
            foreach (var sx in new float[] { -0.22f, 0.22f })
                renNow.draw(cubeNow,
                    dm * Mat4.translate(new Vec3(sx, topLocal + 0.025f, 0))
                    * Mat4.scale(new Vec3(0.02f, 0.004f, 0.3f)),
                    new Draw3dOpts { tint = Color.rgb(0.92f, 0.88f, 0.78f) });
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
            float sq = f.squashT / 8.0f * 0.22f;
            var op = Phys3d.phys3d_pose(drawWorld, "rikishi:" + (1 - i));
            float fx = op != null ? op.x - pose.x : -pose.x;
            float fz = op != null ? op.z - pose.z : -pose.z;
            float yaw = (float)Math.Atan2(fx, fz); // model の -Z を相手へ向ける
            var q = new Quat(pose.qx, pose.qy, pose.qz, pose.qw);
            var upv = q * Vec3.up();
            bool falling = upv.y < 0.6f;
            float pulse = f.tactic == TA_OSU
                ? Math.Max(0.0f, (float)Math.Sin(
                    renderFrame * DT * f.pulseHz * 2.0f * (float)Math.PI + f.phase))
                : 0.0f;
            var model = Renderer3d.poseMat(pose) * Mat4.rotateY(yaw)
                * Mat4.scale(new Vec3(1.0f + sq * 0.6f, 1.0f - sq, 1.0f + sq * 0.6f));
            renNow.draw(mesh, model, new Draw3dOpts
            {
                bones = packBones(i, f, falling, pulse, renderFrame, mesh.data),
            });
        }

        // トントンのマーカー (打った場所に一瞬リング。高輝度で bloom に乗る)
        if (markerT > 0)
        {
            float k = markerT / 10.0f;
            renNow.draw(cylNow,
                Mat4.translate(new Vec3(markerX, markerY + 0.03f, markerZ))
                * Mat4.scale(new Vec3(0.22f * (2.0f - k), 0.01f, 0.22f * (2.0f - k))),
                new Draw3dOpts { tint = Color.rgb(1.6f, 1.5f, 0.9f) });
        }

        renNow.End();

        // --- テキスト (かな): tonemap 後の swapchain に重ね描き ---
        Gfx.begin_pass(new PassOpts { target = Gfx.main_tex, load = Gfx.LOAD });
        var mt = ensureText();
        if (mt != null)
        {
            var cream = Color.rgb(0.95f, 0.92f, 0.85f);
            mt.textCentered("あか　" + stars[0] + " - " + stars[1] + "　あお",
                W * 0.5f, 348, 20, cream);
            if (state == ST_FIGHT)
            {
                if (renderFrame - fightStart < 50)
                    mt.textCentered("はっけよい", W * 0.5f, 120, 44,
                        Color.rgb(0.98f, 0.85f, 0.4f));
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
                    var sp = screenPos(vp, pose.x, pose.y + 1.5f, pose.z);
                    if (!sp.ok)
                        continue;
                    string label;
                    Color tint;
                    if (f.tactic == TA_HIKI)
                    {
                        label = "ひく";
                        tint = Color.rgb(0.35f, 0.9f, 0.9f);
                    }
                    else if (f.tactic == TA_INASHI)
                    {
                        label = "いなす";
                        tint = Color.rgb(0.45f, 0.9f, 0.45f);
                    }
                    else if (f.tactic == TA_TAME)
                    {
                        label = "ためる";
                        tint = Color.rgb(0.75f, 0.75f, 0.78f);
                    }
                    else
                    {
                        label = "おす";
                        tint = Color.rgb(1.0f, 0.66f, 0.25f);
                    }
                    mt.textCentered(label, sp.x, sp.y, 16, tint);
                }
            }
            if (state == ST_KIMARI)
            {
                if (winner >= 0)
                {
                    var wf = fighters[winner];
                    mt.textCentered(wf.name + "のかち", W * 0.5f, 112, 36,
                        Color.rgb(wf.color[0] * 0.4f + 0.6f,
                            wf.color[1] * 0.4f + 0.6f, wf.color[2] * 0.4f + 0.6f));
                    mt.textCentered(kimarite, W * 0.5f, 150, 24, cream);
                }
                else
                {
                    mt.textCentered("とりなおし", W * 0.5f, 124, 32, cream);
                }
            }
        }

        Gfx.end_pass();
    }
}
