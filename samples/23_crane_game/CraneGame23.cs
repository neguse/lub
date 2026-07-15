// lub の samples/23_crane_game (Haxe 版 CraneGame23.hx) の TinyC# 版 entry。
// 実行: lub samples/23_crane_game/CraneGame23.csproj (transpile + watch + hot reload)
//
// gameplay rule (クレーン操作・アーム・景品・実寸パラメータ) は Haxe 版に忠実。
// Haxe 版との対応:
// - typedef Bear / 匿名構造体 {bear, body, index} {head, fr, fl}
//   {speed, torque} は class Bear / LiveBear / Machine / ClawCommand 化。
// - Renderer3d / Mesh3d は static 初期化子でなく onFrame からの build() で
//   遅延生成する (cs-lib クラスは load 順の都合で static 初期化子から呼べない)。
// - 整数剰余 % は Mod() (floor 分解) で代替、switch は if 連鎖。
// - Phys3d の body/world 取得は null ガード (Haxe 版は Dynamic のまま)。
//
// 3D クレーンゲーム (2 本爪プライズ機)。実機の機構を実寸スケールで再現し、
// 補助力なしの現実の物理 (拘束 + モーター + 摩擦 + 重力) だけで成立させる:
//
// - ガントリー: キャリッジ (kinematic) が上部レールを X→Z の順に走る。
//   ボタン 1 押下中に右へ、ボタン 2 押下中に奥へ。離すと戻せない。
// - ワイヤー吊り: ヘッドはキャリッジからワイヤー 1 本吊り。
//   distance joint を「バネ力 0 + 上限 limit」でロープ化し、巻き上げ =
//   maxLength の増減。着地でワイヤーが弛む・移動で振り子揺れするのは実機通り。
//   着地検出も実機と同じ「張力低下」(= 弛み) で行う。
// - 爪: 2 本アームを revolute joint のトルク制限付きモーターで開閉。
//   トルク上限 = アームパワー。実機同様「初動 (掴む瞬間)」と「保持
//   (運搬中)」を別設定できる。把持はモーターの締め付け × 摩擦のみなので、
//   保持が弱いと運搬中に滑り落ちる (=「取れそうで取れない」が物理から創発)。
// - ぬいぐるみ: SDF モデリングしたクマ (約 30cm / 約 330g、密度 50kg/m³)。
//   物理は球 + カプセルの複数 shape 近似。

using System;
using System.Collections.Generic;

public class Bear
{
    public int gen;
    public int variant;
    public int respawn; // >0 = 獲得済み。0 になったら復活 (店員の補充)
    public double x;
    public double y;
    public double z;
    public double yaw;
}

/// <summary>フィールド上に生きているクマ (今フレームの物理 body 付き)。</summary>
public class LiveBear
{
    public Bear bear;
    public BodyRef3d body;
    public int index;

    public LiveBear(Bear bear, BodyRef3d body, int index)
    {
        this.bear = bear;
        this.body = body;
        this.index = index;
    }
}

/// <summary>クレーン機構の可動 body 一式 (declareMachine の戻り値)。</summary>
public class Machine
{
    public BodyRef3d head;
    public BodyRef3d fr;
    public BodyRef3d fl;

    public Machine(BodyRef3d head, BodyRef3d fr, BodyRef3d fl)
    {
        this.head = head;
        this.fr = fr;
        this.fl = fl;
    }
}

/// <summary>爪モーター指示 (右用の符号。左は反転)。</summary>
public class ClawCommand
{
    public double speed;
    public double torque;
}

public static class CraneGame23
{
    const double DT = 1.0 / 60.0;

    // --- 実寸パラメータ (フィールド 750×900mm、実機調査に基づく) ---------
    const double FIELD_HX = 0.375; // フィールド半幅 (X)
    const double FIELD_HZ = 0.45; // フィールド半奥行 (Z)。+Z が手前
    const double CARRIAGE_Y = 0.78;
    const double MOVE_SPEED = 0.15; // ガントリー移動 (m/s)
    const double WINCH_SPEED = 0.20; // 昇降 (m/s)
    const double WIRE_MIN = 0.15;
    const double WIRE_MAX = 0.56;
    const double OPEN_ANGLE = 0.85; // 爪の開き角 (rad)
    const double HEAD_TOP = 0.06; // ヘッド原点→ワイヤー取付点
    const double SHOULDER_X = 0.10; // 爪の肩関節 (ヘッド原点から)
    const double SHOULDER_Y = -0.01;
    const double HOME_X = -0.16; // 待機位置 = 獲得口の真上
    const double HOME_Z = 0.275;
    const double MAX_X = 0.15; // 可動範囲 (店側設定。開いた爪がガラスに触れない位置まで)
    const double MIN_Z = -0.30;
    // 獲得口 (シュート): 手前左の床穴。判定に使う内側 2 辺
    const double CHUTE_X1 = -0.025;
    const double CHUTE_Z0 = 0.10;

    // アームパワー。実機の店側パワー設定に相当し、把持はトルク上限 × 摩擦で決まる。
    // 初動 1.2 N·m で約 330g のクマを掴め、保持 0.6 N·m は揺れ・加速で
    // 滑る境界値 (デモ実測でおよそ 4-5 回に 1 回獲得 = 実機並み)
    static double grabTorque = 1.2;
    static double holdTorque = 0.6;

    // --- 状態機械 (実機の自動シーケンス) ---------------------------------
    const int ST_IDLE = 0;
    const int ST_MOVE_X = 1;
    const int ST_WAIT2 = 2;
    const int ST_MOVE_Z = 3;
    const int ST_DESCEND = 4;
    const int ST_GRAB = 5;
    const int ST_LIFT = 6;
    const int ST_CARRY = 7;
    const int ST_RELEASE = 8;
    const int ST_RESET = 9;
    static List<string> STATE_NAMES = new List<string>
    {
        "idle",
        "move right",
        "ready",
        "move back",
        "descend",
        "grab",
        "lift",
        "carry",
        "release",
        "reset",
    };

    static int frame = 0;
    static int state = ST_IDLE;
    static int stateT = 0;
    static double cx = HOME_X;
    static double cz = HOME_Z;
    static double wireLen = WIRE_MIN;
    static int score = 0;
    static int plays = 0;
    static int payoutFlash = 0;
    static int idleT = 0;
    // attract モード: 放置でクマを狙って自動プレイ (デモ兼ヘッドレス検証用)
    static bool autoPlay = false;
    static int autoIndex = 0;
    static double autoX = 0.0;
    static double autoZ = 0.0;
    static int slackFrames = 0;
    static FixedStep? step = null;
    static int pendingPresses = 0;
    static List<int> renderBearIndices = new List<int>();

    static List<Bear> bears = new List<Bear>();

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend, width = 640, height = 360 });
        // 初期配置: 可動範囲内 (x <= MAX_X) に散らす。座標は固定 (決定論)
        bears = new List<Bear>
        {
            new Bear { gen = 1, variant = 0, respawn = 0, x = 0.08, y = 0.02, z = -0.05, yaw = 0.4 },
            new Bear { gen = 1, variant = 1, respawn = 0, x = -0.14, y = 0.02, z = -0.26, yaw = -0.7 },
            new Bear { gen = 1, variant = 2, respawn = 0, x = 0.15, y = 0.02, z = 0.18, yaw = 2.6 },
            new Bear { gen = 1, variant = 0, respawn = 0, x = 0.06, y = 0.02, z = 0.18, yaw = 1.8 },
            new Bear { gen = 1, variant = 1, respawn = 0, x = 0.16, y = 0.02, z = -0.24, yaw = -2.2 },
        };
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    /// <summary>非負 int の剰余 (tcs は % を使わない規約なので floor 分解)。</summary>
    static int Mod(int a, int n)
    {
        return (int)(a - Math.Floor((double)a / n) * n);
    }

    // --- SDF モデル -------------------------------------------------------
    // クマ (約 30cm)。物理 compound (declareBearShapes) と寸法を揃えている
    static SdfNode bearModel(int fur, int belly)
    {
        var body = Sdf.sphere(0.100).move(0, 0.100, 0);
        var head = Sdf.sphere(0.072).move(0, 0.220, 0);
        var ear = Sdf.sphere(0.026).move(0.050, 0.284, 0).mirrorX();
        var arm = Sdf.capsule(new Vec3(0.080, 0.150, 0.010),
            new Vec3(0.130, 0.075, 0.030), 0.027).mirrorX();
        var leg = Sdf.capsule(new Vec3(0.050, 0.045, 0.020),
            new Vec3(0.100, 0.035, 0.105), 0.033).mirrorX();
        var muzzle = Sdf.sphere(0.030).move(0, 0.198, 0.058)
            .paint(belly, 0.0, 0.9);
        var tummy = Sdf.sphere(0.052).move(0, 0.090, 0.062)
            .paint(belly, 0.0, 0.9);
        var eye = Sdf.sphere(0.010).move(0.028, 0.238, 0.062).mirrorX()
            .paint(0x1E2130, 0.0, 0.2);
        return body.smin(head, 0.02)
            .smin(ear, 0.012)
            .smin(arm, 0.015)
            .smin(leg, 0.015)
            .paint(fur, 0.0, 0.9)
            .smin(muzzle, 0.010)
            .smin(tummy, 0.012)
            .ssub(eye, 0.004);
    }

    // 爪 1 本 (右用)。肩 (原点) → 肘 → 爪先の「反り 120°」形状。
    // 左は描画・物理とも X 反転 (rotateY(π))
    static SdfNode fingerModel()
    {
        var upper = Sdf.capsule(new Vec3(0, 0, 0),
            new Vec3(0.050, -0.110, 0), 0.009);
        var lower = Sdf.capsule(new Vec3(0.050, -0.110, 0),
            new Vec3(-0.085, -0.215, 0), 0.008);
        return upper.smin(lower, 0.010).paint(0xC9CED8, 0.9, 0.25);
    }

    // ヘッド: ドーム + リング。原点はリング面の中心
    static SdfNode headModel()
    {
        var dome = Sdf.sphere(0.105)
            .intersect(Sdf.box(0.11, 0.055, 0.11).move(0, 0.055, 0))
            .paint(0xF2F2F4, 0.1, 0.4);
        var rim = Sdf.torus(0.095, 0.032).paint(0xE0405A, 0.2, 0.5);
        return dome.smin(rim, 0.015);
    }

    // --- メッシュ (hot reload 対応: dirty フラグで再メッシュ。native watch は
    // chunk 再実行で初期値 true に戻り、web の module mode は onReload で立てる) --
    static bool meshDirty = true;

    public static void onReload()
    {
        meshDirty = true;
    }
    static Renderer3d? ren = null;
    static List<Mesh3d>? bearMeshes = null;
    static Mesh3d? fingerMesh = null;
    static Mesh3d? headMesh = null;
    static Mesh3d? cubeMesh = null;

    static void build()
    {
        if (ren != null) return;
        ren = new Renderer3d("cg23");
        var bm = new List<Mesh3d>();
        for (int i = 0; i < 3; i++)
        {
            bm.Add(new Mesh3d("cg_bear" + i));
        }
        bearMeshes = bm;
        fingerMesh = new Mesh3d("cg_finger");
        headMesh = new Mesh3d("cg_head");
        cubeMesh = new Mesh3d("cg_cube");
    }

    static void remesh()
    {
        var bm = bearMeshes;
        var fm = fingerMesh;
        var hm = headMesh;
        var cm = cubeMesh;
        if (bm == null || fm == null || hm == null || cm == null) return;
        var furs = new List<int> { 0xB07A4A, 0xE8A0B4, 0xF0E5CE };
        var bellies = new List<int> { 0xF2E3C8, 0xF7D9E2, 0xE0CFA8 };
        for (int i = 0; i < 3; i++)
        {
            bm[i].rebuild(Sdf.mesh(bearModel(furs[i], bellies[i]), 56));
        }
        fm.rebuild(Sdf.mesh(fingerModel(), 48));
        hm.rebuild(Sdf.mesh(headModel(), 56));
        if (!cm.ready())
            cm.rebuild(Shapes3d.cube());
        meshDirty = false;
    }

    // --- 物理: クマは球 + カプセルの複数 shape で近似 (SDF と同じ寸法)。
    // compound は static 専用なので、dynamic body には shape を複数ぶら下げる
    static void declareBearShapes(BodyRef3d body, int ver)
    {
        double density = 50.0; // 密度 50kg/m³ → 約 330g
        double friction = 0.6;
        double restitution = 0.02;
        Phys3d.phys3d_sphere(body, "torso", new SphereDesc3d
        {
            version = ver,
            r = 0.100,
            offset = new Vec3d { x = 0.0, y = 0.100, z = 0.0 },
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_sphere(body, "head", new SphereDesc3d
        {
            version = ver,
            r = 0.072,
            offset = new Vec3d { x = 0.0, y = 0.220, z = 0.0 },
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_capsule(body, "arm_r", new CapsuleDesc3d
        {
            version = ver,
            a = new Vec3d { x = 0.080, y = 0.150, z = 0.010 },
            b = new Vec3d { x = 0.130, y = 0.075, z = 0.030 },
            r = 0.027,
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_capsule(body, "arm_l", new CapsuleDesc3d
        {
            version = ver,
            a = new Vec3d { x = -0.080, y = 0.150, z = 0.010 },
            b = new Vec3d { x = -0.130, y = 0.075, z = 0.030 },
            r = 0.027,
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_capsule(body, "leg_r", new CapsuleDesc3d
        {
            version = ver,
            a = new Vec3d { x = 0.050, y = 0.045, z = 0.020 },
            b = new Vec3d { x = 0.100, y = 0.035, z = 0.105 },
            r = 0.033,
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_capsule(body, "leg_l", new CapsuleDesc3d
        {
            version = ver,
            a = new Vec3d { x = -0.050, y = 0.045, z = 0.020 },
            b = new Vec3d { x = -0.100, y = 0.035, z = 0.105 },
            r = 0.033,
            density = density,
            friction = friction,
            restitution = restitution,
        });
    }

    // 爪 1 本の物理 (右用。左は sign = -1 で X 反転)
    static void declareFingerShapes(BodyRef3d body, double sign)
    {
        Phys3d.phys3d_capsule(body, "upper", new CapsuleDesc3d
        {
            a = new Vec3d { x = 0.0, y = 0.0, z = 0.0 },
            b = new Vec3d { x = sign * 0.050, y = -0.110, z = 0.0 },
            r = 0.009,
            density = 2000.0,
            friction = 0.6,
        });
        Phys3d.phys3d_capsule(body, "lower", new CapsuleDesc3d
        {
            a = new Vec3d { x = sign * 0.050, y = -0.110, z = 0.0 },
            b = new Vec3d { x = sign * -0.085, y = -0.215, z = 0.0 },
            r = 0.008,
            density = 2000.0,
            friction = 0.6,
        });
    }

    // 静物: 床 (獲得口の穴あき) + アクリルフェンス + ガラス壁 + シュート筒
    static List<double[]> STATICS = new List<double[]>
    {
        // x, y, z, hx, hy, hz
        new double[] { 0.0, -0.02, -0.175, FIELD_HX, 0.02, 0.275 }, // 床 (奥側)
        new double[] { 0.175, -0.02, 0.275, 0.20, 0.02, 0.175 }, // 床 (手前右)
        new double[] { -0.20, 0.07, 0.10, 0.175, 0.07, 0.006 }, // フェンス (穴の奥側)
        new double[] { -0.025, 0.07, 0.275, 0.006, 0.07, 0.175 }, // フェンス (穴の右側)
        new double[] { -FIELD_HX - 0.006, 0.31, 0.0, 0.006, 0.31, FIELD_HZ }, // ガラス左
        new double[] { FIELD_HX + 0.006, 0.31, 0.0, 0.006, 0.31, FIELD_HZ }, // ガラス右
        new double[] { 0.0, 0.31, -FIELD_HZ - 0.006, FIELD_HX, 0.31, 0.006 }, // ガラス奥
        new double[] { 0.0, 0.31, FIELD_HZ + 0.006, FIELD_HX, 0.31, 0.006 }, // ガラス手前
        new double[] { -0.025, -0.25, 0.275, 0.006, 0.25, 0.175 }, // シュート筒 右
        new double[] { -0.20, -0.25, 0.10, 0.175, 0.25, 0.006 }, // シュート筒 奥
        new double[] { -FIELD_HX - 0.006, -0.25, 0.275, 0.006, 0.25, 0.175 }, // シュート筒 左
        new double[] { -0.20, -0.25, FIELD_HZ + 0.006, 0.175, 0.25, 0.006 }, // シュート筒 手前
    };

    static void declareStatics(WorldRef3d world)
    {
        for (int i = 0; i < STATICS.Count; i++)
        {
            var s = STATICS[i];
            var body = Phys3d.phys3d_body(world, "static:" + i, new BodyDesc3d
            {
                type = Phys3d.STATIC,
                initial = new InitialState3d { x = s[0], y = s[1], z = s[2] },
            });
            if (body == null) continue;
            Phys3d.phys3d_box(body, "solid", new BoxDesc3d
            {
                hx = s[3],
                hy = s[4],
                hz = s[5],
                friction = 0.5,
                restitution = 0.05,
            });
        }
    }

    static Machine? declareMachine(WorldRef3d world)
    {
        // キャリッジ: レール上を走るユニット。kinematic + setTarget で速度を持つ
        var carriage = Phys3d.phys3d_body(world, "carriage", new BodyDesc3d
        {
            type = Phys3d.KINEMATIC,
            initial = new InitialState3d { x = HOME_X, y = CARRIAGE_Y, z = HOME_Z },
        });
        if (carriage == null) return null;
        Phys3d.phys3d_set_target(carriage, new TargetDesc3d
        {
            x = cx,
            y = CARRIAGE_Y,
            z = cz,
            dt = DT,
        });

        // ヘッド: ワイヤー 1 本吊り (実機の主流はワイヤー巻き取り式)。
        // damping は空力とワイヤー内部摩擦・捩り抵抗による実在の損失の近似
        double headY0 = CARRIAGE_Y - WIRE_MIN - HEAD_TOP;
        var head = Phys3d.phys3d_body(world, "head", new BodyDesc3d
        {
            type = Phys3d.DYNAMIC,
            linearDamping = 0.15,
            angularDamping = 0.5,
            initial = new InitialState3d { x = HOME_X, y = headY0, z = HOME_Z },
        });
        if (head == null) return null;
        Phys3d.phys3d_cylinder(head, "solid", new CylinderDesc3d
        {
            height = 0.08,
            radius = 0.105,
            yOffset = 0.02,
            density = 400.0, // ヘッド質量 ≈ 1.1kg
            friction = 0.3,
        });

        // ワイヤー: バネ力 0 のバネ + 上限 limit = 引けるが押せないロープ。
        // 巻き上げ機は maxLength を増減するだけ (実機のスプール相当)
        Phys3d.phys3d_joint(world, "wire", new JointDesc3d
        {
            type = "distance",
            a = carriage,
            b = head,
            anchorA = new Vec3d { x = HOME_X, y = CARRIAGE_Y, z = HOME_Z },
            anchorB = new Vec3d { x = HOME_X, y = headY0 + HEAD_TOP, z = HOME_Z },
            enableSpring = true,
            hertz = 0.0,
            dampingRatio = 0.0,
            enableLimit = true,
            minLength = 0.02,
            maxLength = wireLen,
            length = wireLen,
        });

        // ハーネス: 実機のヘッドはワイヤーに加えて電源ケーブル束でも
        // つながっており、その曲げ・捩り剛性が回転を抑える。motor joint の
        // 姿勢バネ (トルク上限つき) でモデル化する。並進には作用しない。
        // 上限を超える外力ではちゃんと傾く (着地時など)
        Phys3d.phys3d_joint(world, "harness", new JointDesc3d
        {
            type = "motor",
            a = carriage,
            b = head,
            maxVelocityForce = 0.0,
            maxVelocityTorque = 0.0,
            linearHertz = 0.0,
            maxSpringForce = 0.0,
            angularHertz = 1.2,
            angularDampingRatio = 1.0,
            maxSpringTorque = 2.5,
        });

        // 爪 2 本: 肩の revolute joint。モーターのトルク上限がアームパワー。
        // 開閉指示は状態機械から (clawCommand)。angularDamping は関節部の摩擦損失
        var fr = Phys3d.phys3d_body(world, "finger:r", new BodyDesc3d
        {
            type = Phys3d.DYNAMIC,
            angularDamping = 1.0,
            initial = new InitialState3d
            {
                x = HOME_X + SHOULDER_X,
                y = headY0 + SHOULDER_Y,
                z = HOME_Z,
            },
        });
        if (fr == null) return null;
        declareFingerShapes(fr, 1.0);
        var fl = Phys3d.phys3d_body(world, "finger:l", new BodyDesc3d
        {
            type = Phys3d.DYNAMIC,
            angularDamping = 1.0,
            initial = new InitialState3d
            {
                x = HOME_X - SHOULDER_X,
                y = headY0 + SHOULDER_Y,
                z = HOME_Z,
            },
        });
        if (fl == null) return null;
        declareFingerShapes(fl, -1.0);

        var claw = clawCommand();
        Phys3d.phys3d_joint(world, "claw:r", new JointDesc3d
        {
            type = "revolute",
            a = head,
            b = fr,
            anchorA = new Vec3d
            { x = HOME_X + SHOULDER_X, y = headY0 + SHOULDER_Y, z = HOME_Z },
            anchorB = new Vec3d
            { x = HOME_X + SHOULDER_X, y = headY0 + SHOULDER_Y, z = HOME_Z },
            axis = new Vec3d { x = 0.0, y = 0.0, z = 1.0 },
            enableLimit = true,
            lower = 0.0,
            upper = OPEN_ANGLE,
            enableMotor = true,
            motorSpeed = claw.speed,
            maxTorque = claw.torque,
        });
        Phys3d.phys3d_joint(world, "claw:l", new JointDesc3d
        {
            type = "revolute",
            a = head,
            b = fl,
            anchorA = new Vec3d
            { x = HOME_X - SHOULDER_X, y = headY0 + SHOULDER_Y, z = HOME_Z },
            anchorB = new Vec3d
            { x = HOME_X - SHOULDER_X, y = headY0 + SHOULDER_Y, z = HOME_Z },
            axis = new Vec3d { x = 0.0, y = 0.0, z = 1.0 },
            enableLimit = true,
            lower = -OPEN_ANGLE,
            upper = 0.0,
            enableMotor = true,
            motorSpeed = -claw.speed,
            maxTorque = claw.torque,
        });
        return new Machine(head, fr, fl);
    }

    // 状態ごとの爪モーター指示 (右用の符号。左は反転)。
    // speed > 0 = 開く。実機の位相別パワー (初動/保持) をここで切り替える。
    // 速度は実機並みにゆっくり (速いとリミット衝突の反動でヘッドが暴れる)
    static ClawCommand clawCommand()
    {
        // プレイ開始 (移動) から降下まで開きっぱなし (実機と同じ)
        if (state == ST_MOVE_X || state == ST_WAIT2 || state == ST_MOVE_Z
            || state == ST_DESCEND)
            return new ClawCommand { speed = 1.8, torque = 0.9 };
        if (state == ST_GRAB || state == ST_LIFT)
            return new ClawCommand { speed = -2.0, torque = grabTorque }; // 初動 (掴む〜持ち上げ)
        if (state == ST_CARRY)
            return new ClawCommand { speed = -2.0, torque = holdTorque }; // 保持 (運搬中に弱まる)
        if (state == ST_RELEASE)
            return new ClawCommand { speed = 1.8, torque = 0.9 }; // 獲得口で開放
        return new ClawCommand { speed = -1.5, torque = 0.5 }; // 待機は閉じ
    }

    static List<LiveBear> declareBears(WorldRef3d world)
    {
        var live = new List<LiveBear>();
        for (int i = 0; i < bears.Count; i++)
        {
            var b = bears[i];
            if (b.respawn > 0)
                continue;
            var body = Phys3d.phys3d_body(world, "bear:" + i, new BodyDesc3d
            {
                type = Phys3d.DYNAMIC,
                version = b.gen,
                linearDamping = 0.05,
                angularDamping = 0.5, // 布と詰め物の内部損失の近似
                initial = new InitialState3d
                {
                    x = b.x,
                    y = b.y,
                    z = b.z,
                    euler = new Vec3d { x = 0.0, y = b.yaw, z = 0.0 },
                },
            });
            if (body == null) continue;
            declareBearShapes(body, b.gen);
            live.Add(new LiveBear(b, body, i));
        }
        return live;
    }

    // --- 状態機械 ----------------------------------------------------------
    static bool buttonHeld()
    {
        if (autoPlay)
        {
            // attract: 目標座標に届くまで押し続ける動作を合成
            if (state == ST_MOVE_X) return cx < autoX - 0.005;
            if (state == ST_MOVE_Z) return cz > autoZ + 0.005;
            return false;
        }
        return Input.key_down("space")
            || (Input.mouse_down() && !Ui.ui_want_capture_mouse());
    }

    static bool buttonPressed(bool tickPressed)
    {
        if (autoPlay)
            return state == ST_IDLE || state == ST_WAIT2;
        return tickPressed;
    }

    static void enter(int s)
    {
        state = s;
        stateT = 0;
        slackFrames = 0;
    }

    static void updateSequence(WorldRef3d world, BodyRef3d head,
        bool tickPressed)
    {
        stateT++;
        if (state == ST_IDLE)
        {
            wireLen = WIRE_MIN;
            if (buttonPressed(tickPressed))
            {
                plays++;
                enter(ST_MOVE_X);
            }
            else
            {
                idleT++;
                if (idleT > 240)
                {
                    // attract: 生きているクマを順繰りに狙う
                    Bear? target = null;
                    int targetIndex = 0;
                    for (int k = 0; k < bears.Count; k++)
                    {
                        int idx = Mod(autoIndex + k, bears.Count);
                        var b = bears[idx];
                        if (b.respawn == 0)
                        {
                            target = b;
                            targetIndex = idx;
                            autoIndex = Mod(autoIndex + k + 1, bears.Count);
                            break;
                        }
                    }
                    if (target != null)
                    {
                        var pose = Phys3d.phys3d_pose(world, "bear:" + targetIndex);
                        autoX = MathUtil.clamp(pose != null ? pose.x : target.x,
                            HOME_X, MAX_X);
                        autoZ = MathUtil.clamp(pose != null ? pose.z : target.z,
                            MIN_Z, HOME_Z);
                        autoPlay = true;
                        plays++;
                        enter(ST_MOVE_X);
                    }
                    idleT = 0;
                }
            }
        }
        else if (state == ST_MOVE_X)
        {
            if (buttonHeld())
                cx = Math.Min(cx + MOVE_SPEED * DT, MAX_X);
            else if (stateT > 5)
                enter(ST_WAIT2);
        }
        else if (state == ST_WAIT2)
        {
            if (buttonPressed(tickPressed))
                enter(ST_MOVE_Z);
            else if (stateT > 420) // 実機同様、放置でも自動で降下へ
                enter(ST_DESCEND);
        }
        else if (state == ST_MOVE_Z)
        {
            if (buttonHeld())
                cz = Math.Max(cz - MOVE_SPEED * DT, MIN_Z);
            else if (stateT > 5)
                enter(ST_DESCEND);
        }
        else if (state == ST_DESCEND)
        {
            // 移動直後の振り子揺れが収まるまで一呼吸置いてから繰り出す
            if (stateT > 15)
                wireLen = Math.Min(wireLen + WINCH_SPEED * DT, WIRE_MAX);
            // 着地検出 = ワイヤー張力低下 (実機はテンションセンサー)。
            // 繰り出し量に対して実距離が短い = 弛み。揺れによる瞬間的な
            // 弛みを拾わないよう連続フレームでデバウンスする
            var pose = Phys3d.phys3d_pose(head);
            if (pose != null)
            {
                var anchor = new Vec3(pose.x, pose.y, pose.z)
                    + new Quat(pose.qx, pose.qy, pose.qz, pose.qw)
                        .rotateVec3(new Vec3(0, HEAD_TOP, 0));
                double dist = new Vec3(cx, CARRIAGE_Y, cz).distance(anchor);
                slackFrames = (wireLen - dist > 0.03) ? slackFrames + 1 : 0;
                if ((slackFrames >= 8 && stateT > 40) || wireLen >= WIRE_MAX)
                {
                    enter(ST_GRAB);
                }
            }
        }
        else if (state == ST_GRAB)
        {
            if (stateT > 50)
                enter(ST_LIFT);
        }
        else if (state == ST_LIFT)
        {
            wireLen = Math.Max(wireLen - WINCH_SPEED * DT, WIRE_MIN);
            if (wireLen <= WIRE_MIN)
                enter(ST_CARRY);
        }
        else if (state == ST_CARRY)
        {
            double dx = HOME_X - cx;
            double dz = HOME_Z - cz;
            cx += MathUtil.clamp(dx, -MOVE_SPEED * DT, MOVE_SPEED * DT);
            cz += MathUtil.clamp(dz, -MOVE_SPEED * DT, MOVE_SPEED * DT);
            if (Math.Abs(dx) < 0.002 && Math.Abs(dz) < 0.002)
                enter(ST_RELEASE);
        }
        else if (state == ST_RELEASE)
        {
            if (stateT > 70)
                enter(ST_RESET);
        }
        else if (state == ST_RESET)
        {
            if (stateT > 40)
            {
                autoPlay = false;
                idleT = 0;
                enter(ST_IDLE);
            }
        }
    }

    // 獲得判定: シュート筒の中に落ちたら得点、それ以外の転落は保険で回収
    static void updatePrizes(List<LiveBear> live)
    {
        foreach (var entry in live)
        {
            var pose = Phys3d.phys3d_pose(entry.body);
            if (pose == null)
                continue;
            if (pose.y < -0.32)
            {
                entry.bear.respawn = 150;
                if (pose.x < CHUTE_X1 && pose.z > CHUTE_Z0)
                {
                    score++;
                    payoutFlash = 60;
                }
            }
        }
        for (int i = 0; i < bears.Count; i++)
        {
            var b = bears[i];
            if (b.respawn > 0)
            {
                b.respawn--;
                if (b.respawn == 0)
                {
                    // 補充: フィールド奥へ落とす。位置は決定論的にずらす
                    b.gen++;
                    b.x = 0.02 + Mod(b.gen * 53, 13) * 0.012;
                    b.y = 0.35;
                    b.z = -0.15 + Mod(b.gen * 31, 11) * 0.02;
                    b.yaw = Mod(b.gen * 137, 63) * 0.1;
                }
            }
        }
    }

    // --- 描画 --------------------------------------------------------------
    static Mat4 boxMat(double x, double y, double z, double sx, double sy,
        double sz)
    {
        return Mat4.translate(new Vec3(x, y, z))
            * Mat4.scale(new Vec3(sx, sy, sz));
    }

    // 2 点間に渡す細い箱 (ワイヤーとレール用)
    static Mat4 segmentMat(Vec3 a, Vec3 b, double r)
    {
        var d = b - a;
        double len = d.length();
        var mid = (a + b) * 0.5;
        var rot = new Mat4();
        if (len > 1e-6)
        {
            var dir = d * (1.0 / len);
            var axis = Vec3.up().cross(dir);
            double s = axis.length();
            if (s > 1e-6)
                rot = Quat.fromAxisAngle(axis * (1.0 / s),
                    Math.Atan2(s, dir.y)).toMat4();
            else if (dir.y < 0)
                rot = Mat4.rotateX(Math.PI);
        }
        return Mat4.translate(mid) * rot * Mat4.scale(new Vec3(r, len * 0.5, r));
    }

    static void drawBox(Mat4 model, Color color, int? blend)
    {
        var r = ren;
        var cube = cubeMesh;
        if (r == null || cube == null) return;
        r.draw(cube, model, new Draw3dOpts { tint = color, blend = blend });
    }

    static void simulateTick(WorldRef3d world)
    {
        // render 側で保持した edge は次の logical tick だけで有効。
        // 受付外 state の押下を数秒後の IDLE / WAIT2 へ持ち越さない。
        bool tickPressed = pendingPresses > 0;
        pendingPresses = 0;
        if (payoutFlash > 0)
            payoutFlash--;
        Phys3d.phys3d_begin(world);
        declareStatics(world);
        var machine = declareMachine(world);
        if (machine == null) return;
        var live = declareBears(world);
        renderBearIndices = new List<int>();
        foreach (var entry in live)
            renderBearIndices.Add(entry.index);
        updateSequence(world, machine.head, tickPressed);
        Phys3d.phys3d_step(world, DT);
        updatePrizes(live);
        frame++;
    }

    public static void onFrame(double dt)
    {
        build();
        if (meshDirty)
            remesh();
        var r = ren;
        var bm = bearMeshes;
        var fm = fingerMesh;
        var hm = headMesh;
        if (r == null || bm == null || fm == null || hm == null)
            return;
        if (!autoPlay && (Input.key_pressed("space")
            || (Input.mouse_pressed() && !Ui.ui_want_capture_mouse())))
            pendingPresses = pendingPresses + 1;

        var world = Phys3d.phys3d_world("crane_game", new WorldOpts3d
        {
            gravity = new Vec3d { x = 0.0, y = -9.81, z = 0.0 },
            fixedDt = DT,
            substeps = 8,
            maxSteps = 1,
        });
        if (world == null) return;
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.frame(dt, _ => simulateTick(world));

        // --- draw ---
        // ゲームセンターの薄暗い環境 + 筐体上部からの光
        r.light.dir = new Vec3(-0.3, 1.0, 0.45);
        r.light.intensity = 1.2;
        r.sky.top = Color.rgb(0.35, 0.36, 0.45);
        r.sky.bottom = Color.rgb(0.12, 0.11, 0.12);
        r.sky.intensity = 0.45;
        r.background = Color.rgb(0.10, 0.10, 0.13);
        r.shadow.center = new Vec3(0, 0.3, 0);
        r.shadow.extent = 1.2;
        r.begin(new Camera
        {
            eye = new Vec3(0.02, 1.02, 1.95),
            target = new Vec3(0.0, 0.30, 0.0),
            fov = 40,
            near = 0.1,
            far = 50.0,
        });

        // 筐体 (描画のみ): 本体・上部飾り・柱・レール
        var body = Color.rgb(0.93, 0.93, 0.95);
        var accent = Color.rgb(0.88, 0.25, 0.42);
        var dark = Color.rgb(0.22, 0.23, 0.27);
        var felt = Color.rgb(0.32, 0.62, 0.46);
        drawBox(boxMat(0.0, -0.33, 0.0, 0.42, 0.29, FIELD_HZ + 0.05), body, null);
        drawBox(boxMat(0.0, -0.06, 0.0, 0.42, 0.022, FIELD_HZ + 0.05), accent, null);
        drawBox(boxMat(0.0, 0.86, 0.0, 0.42, 0.075, FIELD_HZ + 0.05), accent, null);
        foreach (var sx in new List<int> { -1, 1 })
        {
            foreach (var sz in new List<int> { -1, 1 })
            {
                drawBox(boxMat(sx * (FIELD_HX + 0.022), 0.31,
                    sz * (FIELD_HZ + 0.028), 0.016, 0.315, 0.016), body, null);
            }
        }
        // 床 (フェルト) と穴の縁
        drawBox(boxMat(0.0, -0.02, -0.175, FIELD_HX, 0.02, 0.275), felt, null);
        drawBox(boxMat(0.175, -0.02, 0.275, 0.20, 0.02, 0.175), felt, null);
        drawBox(boxMat(-0.20, -0.05, 0.275, 0.175, 0.05, 0.175), dark, null); // シュート内部
        // 払い出しの褒め演出: 獲得口の縁が光る (HDR 高輝度で bloom に乗せる)
        if (payoutFlash > 0)
        {
            double k = payoutFlash / 60.0;
            drawBox(boxMat(-0.20, 0.005, 0.275, 0.178, 0.006 + 0.02 * k, 0.178),
                Color.rgb(1.6, 1.5, 0.7 + 0.7 * k), null);
        }
        // レール: 固定 2 本 + キャリッジと動く梁
        drawBox(boxMat(-0.34, 0.76, 0.0, 0.012, 0.012, FIELD_HZ), dark, null);
        drawBox(boxMat(0.34, 0.76, 0.0, 0.012, 0.012, FIELD_HZ), dark, null);
        drawBox(boxMat(0.0, 0.76, cz, 0.34, 0.010, 0.010), dark, null);
        drawBox(boxMat(cx, 0.775, cz, 0.05, 0.025, 0.05), accent, null);

        // ワイヤー + ヘッド + 爪 (物理の実 pose で描く)
        var headPose = Phys3d.phys3d_pose(world, "head");
        if (headPose != null)
        {
            var anchor = new Vec3(headPose.x, headPose.y, headPose.z)
                + new Quat(headPose.qx, headPose.qy, headPose.qz, headPose.qw)
                    .rotateVec3(new Vec3(0, HEAD_TOP, 0));
            drawBox(segmentMat(new Vec3(cx, CARRIAGE_Y, cz), anchor, 0.005),
                dark, null);
            r.draw(hm, Renderer3d.poseMat(headPose));
        }
        var frPose = Phys3d.phys3d_pose(world, "finger:r");
        if (frPose != null)
            r.draw(fm, Renderer3d.poseMat(frPose));
        var flPose = Phys3d.phys3d_pose(world, "finger:l");
        if (flPose != null)
            r.draw(fm, Renderer3d.poseMat(flPose) * Mat4.rotateY(Math.PI));

        // ぬいぐるみ
        foreach (var i in renderBearIndices)
        {
            var pose = Phys3d.phys3d_pose(world, "bear:" + i);
            if (pose != null)
                r.draw(bm[bears[i].variant], Renderer3d.poseMat(pose));
        }

        // ガラスとフェンス (半透明は opaque の後に自動で回る)
        var glass = Color.rgb(0.75, 0.85, 0.95, 0.12);
        var fence = Color.rgb(0.85, 0.9, 1.0, 0.25);
        drawBox(boxMat(-FIELD_HX - 0.006, 0.31, 0.0, 0.005, 0.31, FIELD_HZ),
            glass, Gfx.ALPHA);
        drawBox(boxMat(FIELD_HX + 0.006, 0.31, 0.0, 0.005, 0.31, FIELD_HZ),
            glass, Gfx.ALPHA);
        drawBox(boxMat(0.0, 0.31, -FIELD_HZ - 0.006, FIELD_HX, 0.31, 0.005),
            glass, Gfx.ALPHA);
        drawBox(boxMat(-0.20, 0.07, 0.10, 0.175, 0.07, 0.005), fence, Gfx.ALPHA);
        drawBox(boxMat(-0.025, 0.07, 0.275, 0.005, 0.07, 0.175), fence, Gfx.ALPHA);
        drawBox(boxMat(0.0, 0.31, FIELD_HZ + 0.006, FIELD_HX, 0.31, 0.005),
            glass, Gfx.ALPHA);

        r.End();

        // UI は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.begin_pass(new PassOpts { target = Gfx.main_tex, load = Gfx.LOAD });
        Ui.ui_set_next_window(10, 10, 240, 150);
        if (Ui.ui_begin("crane game"))
        {
            Ui.ui_text("prizes: " + score + "  plays: " + plays);
            Ui.ui_text("state: " + STATE_NAMES[state]
                + (autoPlay ? " (auto)" : ""));
            Ui.ui_text("hold Space/click: right, then back");
            Ui.ui_separator();
            grabTorque = Ui.ui_slider_float("grab power", grabTorque, 0.0, 2.0);
            holdTorque = Ui.ui_slider_float("hold power", holdTorque, 0.0, 2.0);
        }
        Ui.ui_end();
        Ui.ui_render();
        Gfx.end_pass();
    }
}
