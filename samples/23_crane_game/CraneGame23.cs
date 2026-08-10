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
    public float x;
    public float y;
    public float z;
    public float yaw;
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
    public float speed;
    public float torque;
}

public static class CraneGame23
{
    const float DT = 1.0f / 60.0f;

    // --- 実寸パラメータ (フィールド 750×900mm、実機調査に基づく) ---------
    const float FIELD_HX = 0.375f; // フィールド半幅 (X)
    const float FIELD_HZ = 0.45f; // フィールド半奥行 (Z)。+Z が手前
    const float CARRIAGE_Y = 0.78f;
    const float MOVE_SPEED = 0.15f; // ガントリー移動 (m/s)
    const float WINCH_SPEED = 0.20f; // 昇降 (m/s)
    const float WIRE_MIN = 0.15f;
    const float WIRE_MAX = 0.56f;
    const float OPEN_ANGLE = 0.85f; // 爪の開き角 (rad)
    const float HEAD_TOP = 0.06f; // ヘッド原点→ワイヤー取付点
    const float SHOULDER_X = 0.10f; // 爪の肩関節 (ヘッド原点から)
    const float SHOULDER_Y = -0.01f;
    const float HOME_X = -0.16f; // 待機位置 = 獲得口の真上
    const float HOME_Z = 0.275f;
    const float MAX_X = 0.15f; // 可動範囲 (店側設定。開いた爪がガラスに触れない位置まで)
    const float MIN_Z = -0.30f;
    // 獲得口 (シュート): 手前左の床穴。判定に使う内側 2 辺
    const float CHUTE_X1 = -0.025f;
    const float CHUTE_Z0 = 0.10f;

    // アームパワー。実機の店側パワー設定に相当し、把持はトルク上限 × 摩擦で決まる。
    // 初動 1.2 N·m で約 330g のクマを掴め、保持 0.6 N·m は揺れ・加速で
    // 滑る境界値 (デモ実測でおよそ 4-5 回に 1 回獲得 = 実機並み)
    static float grabTorque = 1.2f;
    static float holdTorque = 0.6f;

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
    static float cx = HOME_X;
    static float cz = HOME_Z;
    static float wireLen = WIRE_MIN;
    static int score = 0;
    static int plays = 0;
    static int payoutFlash = 0;
    static int idleT = 0;
    // attract モード: 放置でクマを狙って自動プレイ (デモ兼ヘッドレス検証用)
    static bool autoPlay = false;
    static int autoIndex = 0;
    static float autoX = 0.0f;
    static float autoZ = 0.0f;
    static int slackFrames = 0;
    static FixedStep? step = null;
    static int pendingPresses = 0;
    static List<int> renderBearIndices = new List<int>();

    static List<Bear> bears = new List<Bear>();

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend, width = 640, height = 360 });
        // 初期配置: 可動範囲内 (x <= MAX_X) に散らす。座標は固定 (決定論)
        bears = new List<Bear>
        {
            new Bear { gen = 1, variant = 0, respawn = 0, x = 0.08f, y = 0.02f, z = -0.05f, yaw = 0.4f },
            new Bear { gen = 1, variant = 1, respawn = 0, x = -0.14f, y = 0.02f, z = -0.26f, yaw = -0.7f },
            new Bear { gen = 1, variant = 2, respawn = 0, x = 0.15f, y = 0.02f, z = 0.18f, yaw = 2.6f },
            new Bear { gen = 1, variant = 0, respawn = 0, x = 0.06f, y = 0.02f, z = 0.18f, yaw = 1.8f },
            new Bear { gen = 1, variant = 1, respawn = 0, x = 0.16f, y = 0.02f, z = -0.24f, yaw = -2.2f },
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
        return (int)(a - (float)Math.Floor((float)a / n) * n);
    }

    // --- SDF モデル -------------------------------------------------------
    // クマ (約 30cm)。物理 compound (declareBearShapes) と寸法を揃えている
    static SdfNode bearModel(int fur, int belly)
    {
        var body = Sdf.sphere(0.100f).move(0, 0.100f, 0);
        var head = Sdf.sphere(0.072f).move(0, 0.220f, 0);
        var ear = Sdf.sphere(0.026f).move(0.050f, 0.284f, 0).mirrorX();
        var arm = Sdf.capsule(new Vec3(0.080f, 0.150f, 0.010f),
            new Vec3(0.130f, 0.075f, 0.030f), 0.027f).mirrorX();
        var leg = Sdf.capsule(new Vec3(0.050f, 0.045f, 0.020f),
            new Vec3(0.100f, 0.035f, 0.105f), 0.033f).mirrorX();
        var muzzle = Sdf.sphere(0.030f).move(0, 0.198f, 0.058f)
            .paint(belly, 0.0f, 0.9f);
        var tummy = Sdf.sphere(0.052f).move(0, 0.090f, 0.062f)
            .paint(belly, 0.0f, 0.9f);
        var eye = Sdf.sphere(0.010f).move(0.028f, 0.238f, 0.062f).mirrorX()
            .paint(0x1E2130, 0.0f, 0.2f);
        return body.smin(head, 0.02f)
            .smin(ear, 0.012f)
            .smin(arm, 0.015f)
            .smin(leg, 0.015f)
            .paint(fur, 0.0f, 0.9f)
            .smin(muzzle, 0.010f)
            .smin(tummy, 0.012f)
            .ssub(eye, 0.004f);
    }

    // 爪 1 本 (右用)。肩 (原点) → 肘 → 爪先の「反り 120°」形状。
    // 左は描画・物理とも X 反転 (rotateY(π))
    static SdfNode fingerModel()
    {
        var upper = Sdf.capsule(new Vec3(0, 0, 0),
            new Vec3(0.050f, -0.110f, 0), 0.009f);
        var lower = Sdf.capsule(new Vec3(0.050f, -0.110f, 0),
            new Vec3(-0.085f, -0.215f, 0), 0.008f);
        return upper.smin(lower, 0.010f).paint(0xC9CED8, 0.9f, 0.25f);
    }

    // ヘッド: ドーム + リング。原点はリング面の中心
    static SdfNode headModel()
    {
        var dome = Sdf.sphere(0.105f)
            .intersect(Sdf.box(0.11f, 0.055f, 0.11f).move(0, 0.055f, 0))
            .paint(0xF2F2F4, 0.1f, 0.4f);
        var rim = Sdf.torus(0.095f, 0.032f).paint(0xE0405A, 0.2f, 0.5f);
        return dome.smin(rim, 0.015f);
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
        float density = 50.0f; // 密度 50kg/m³ → 約 330g
        float friction = 0.6f;
        float restitution = 0.02f;
        Phys3d.phys3d_sphere(body, "torso", new SphereDesc3d
        {
            version = ver,
            r = 0.100f,
            offset = new Vec3d { x = 0.0f, y = 0.100f, z = 0.0f },
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_sphere(body, "head", new SphereDesc3d
        {
            version = ver,
            r = 0.072f,
            offset = new Vec3d { x = 0.0f, y = 0.220f, z = 0.0f },
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_capsule(body, "arm_r", new CapsuleDesc3d
        {
            version = ver,
            a = new Vec3d { x = 0.080f, y = 0.150f, z = 0.010f },
            b = new Vec3d { x = 0.130f, y = 0.075f, z = 0.030f },
            r = 0.027f,
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_capsule(body, "arm_l", new CapsuleDesc3d
        {
            version = ver,
            a = new Vec3d { x = -0.080f, y = 0.150f, z = 0.010f },
            b = new Vec3d { x = -0.130f, y = 0.075f, z = 0.030f },
            r = 0.027f,
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_capsule(body, "leg_r", new CapsuleDesc3d
        {
            version = ver,
            a = new Vec3d { x = 0.050f, y = 0.045f, z = 0.020f },
            b = new Vec3d { x = 0.100f, y = 0.035f, z = 0.105f },
            r = 0.033f,
            density = density,
            friction = friction,
            restitution = restitution,
        });
        Phys3d.phys3d_capsule(body, "leg_l", new CapsuleDesc3d
        {
            version = ver,
            a = new Vec3d { x = -0.050f, y = 0.045f, z = 0.020f },
            b = new Vec3d { x = -0.100f, y = 0.035f, z = 0.105f },
            r = 0.033f,
            density = density,
            friction = friction,
            restitution = restitution,
        });
    }

    // 爪 1 本の物理 (右用。左は sign = -1 で X 反転)
    static void declareFingerShapes(BodyRef3d body, float sign)
    {
        Phys3d.phys3d_capsule(body, "upper", new CapsuleDesc3d
        {
            a = new Vec3d { x = 0.0f, y = 0.0f, z = 0.0f },
            b = new Vec3d { x = sign * 0.050f, y = -0.110f, z = 0.0f },
            r = 0.009f,
            density = 2000.0f,
            friction = 0.6f,
        });
        Phys3d.phys3d_capsule(body, "lower", new CapsuleDesc3d
        {
            a = new Vec3d { x = sign * 0.050f, y = -0.110f, z = 0.0f },
            b = new Vec3d { x = sign * -0.085f, y = -0.215f, z = 0.0f },
            r = 0.008f,
            density = 2000.0f,
            friction = 0.6f,
        });
    }

    // 静物: 床 (獲得口の穴あき) + アクリルフェンス + ガラス壁 + シュート筒
    static List<float[]> STATICS = new List<float[]>
    {
        // x, y, z, hx, hy, hz
        new float[] { 0.0f, -0.02f, -0.175f, FIELD_HX, 0.02f, 0.275f }, // 床 (奥側)
        new float[] { 0.175f, -0.02f, 0.275f, 0.20f, 0.02f, 0.175f }, // 床 (手前右)
        new float[] { -0.20f, 0.07f, 0.10f, 0.175f, 0.07f, 0.006f }, // フェンス (穴の奥側)
        new float[] { -0.025f, 0.07f, 0.275f, 0.006f, 0.07f, 0.175f }, // フェンス (穴の右側)
        new float[] { -FIELD_HX - 0.006f, 0.31f, 0.0f, 0.006f, 0.31f, FIELD_HZ }, // ガラス左
        new float[] { FIELD_HX + 0.006f, 0.31f, 0.0f, 0.006f, 0.31f, FIELD_HZ }, // ガラス右
        new float[] { 0.0f, 0.31f, -FIELD_HZ - 0.006f, FIELD_HX, 0.31f, 0.006f }, // ガラス奥
        new float[] { 0.0f, 0.31f, FIELD_HZ + 0.006f, FIELD_HX, 0.31f, 0.006f }, // ガラス手前
        new float[] { -0.025f, -0.25f, 0.275f, 0.006f, 0.25f, 0.175f }, // シュート筒 右
        new float[] { -0.20f, -0.25f, 0.10f, 0.175f, 0.25f, 0.006f }, // シュート筒 奥
        new float[] { -FIELD_HX - 0.006f, -0.25f, 0.275f, 0.006f, 0.25f, 0.175f }, // シュート筒 左
        new float[] { -0.20f, -0.25f, FIELD_HZ + 0.006f, 0.175f, 0.25f, 0.006f }, // シュート筒 手前
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
                friction = 0.5f,
                restitution = 0.05f,
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
        float headY0 = CARRIAGE_Y - WIRE_MIN - HEAD_TOP;
        var head = Phys3d.phys3d_body(world, "head", new BodyDesc3d
        {
            type = Phys3d.DYNAMIC,
            linearDamping = 0.15f,
            angularDamping = 0.5f,
            initial = new InitialState3d { x = HOME_X, y = headY0, z = HOME_Z },
        });
        if (head == null) return null;
        Phys3d.phys3d_cylinder(head, "solid", new CylinderDesc3d
        {
            height = 0.08f,
            radius = 0.105f,
            yOffset = 0.02f,
            density = 400.0f, // ヘッド質量 ≈ 1.1kg
            friction = 0.3f,
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
            hertz = 0.0f,
            dampingRatio = 0.0f,
            enableLimit = true,
            minLength = 0.02f,
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
            maxVelocityForce = 0.0f,
            maxVelocityTorque = 0.0f,
            linearHertz = 0.0f,
            maxSpringForce = 0.0f,
            angularHertz = 1.2f,
            angularDampingRatio = 1.0f,
            maxSpringTorque = 2.5f,
        });

        // 爪 2 本: 肩の revolute joint。モーターのトルク上限がアームパワー。
        // 開閉指示は状態機械から (clawCommand)。angularDamping は関節部の摩擦損失
        var fr = Phys3d.phys3d_body(world, "finger:r", new BodyDesc3d
        {
            type = Phys3d.DYNAMIC,
            angularDamping = 1.0f,
            initial = new InitialState3d
            {
                x = HOME_X + SHOULDER_X,
                y = headY0 + SHOULDER_Y,
                z = HOME_Z,
            },
        });
        if (fr == null) return null;
        declareFingerShapes(fr, 1.0f);
        var fl = Phys3d.phys3d_body(world, "finger:l", new BodyDesc3d
        {
            type = Phys3d.DYNAMIC,
            angularDamping = 1.0f,
            initial = new InitialState3d
            {
                x = HOME_X - SHOULDER_X,
                y = headY0 + SHOULDER_Y,
                z = HOME_Z,
            },
        });
        if (fl == null) return null;
        declareFingerShapes(fl, -1.0f);

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
            axis = new Vec3d { x = 0.0f, y = 0.0f, z = 1.0f },
            enableLimit = true,
            lower = 0.0f,
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
            axis = new Vec3d { x = 0.0f, y = 0.0f, z = 1.0f },
            enableLimit = true,
            lower = -OPEN_ANGLE,
            upper = 0.0f,
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
            return new ClawCommand { speed = 1.8f, torque = 0.9f };
        if (state == ST_GRAB || state == ST_LIFT)
            return new ClawCommand { speed = -2.0f, torque = grabTorque }; // 初動 (掴む〜持ち上げ)
        if (state == ST_CARRY)
            return new ClawCommand { speed = -2.0f, torque = holdTorque }; // 保持 (運搬中に弱まる)
        if (state == ST_RELEASE)
            return new ClawCommand { speed = 1.8f, torque = 0.9f }; // 獲得口で開放
        return new ClawCommand { speed = -1.5f, torque = 0.5f }; // 待機は閉じ
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
                linearDamping = 0.05f,
                angularDamping = 0.5f, // 布と詰め物の内部損失の近似
                initial = new InitialState3d
                {
                    x = b.x,
                    y = b.y,
                    z = b.z,
                    euler = new Vec3d { x = 0.0f, y = b.yaw, z = 0.0f },
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
            if (state == ST_MOVE_X) return cx < autoX - 0.005f;
            if (state == ST_MOVE_Z) return cz > autoZ + 0.005f;
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
                float dist = new Vec3(cx, CARRIAGE_Y, cz).distance(anchor);
                slackFrames = (wireLen - dist > 0.03f) ? slackFrames + 1 : 0;
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
            float dx = HOME_X - cx;
            float dz = HOME_Z - cz;
            cx += MathUtil.clamp(dx, -MOVE_SPEED * DT, MOVE_SPEED * DT);
            cz += MathUtil.clamp(dz, -MOVE_SPEED * DT, MOVE_SPEED * DT);
            if (Math.Abs(dx) < 0.002f && Math.Abs(dz) < 0.002f)
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
            if (pose.y < -0.32f)
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
                    b.x = 0.02f + Mod(b.gen * 53, 13) * 0.012f;
                    b.y = 0.35f;
                    b.z = -0.15f + Mod(b.gen * 31, 11) * 0.02f;
                    b.yaw = Mod(b.gen * 137, 63) * 0.1f;
                }
            }
        }
    }

    // --- 描画 --------------------------------------------------------------
    static Mat4 boxMat(float x, float y, float z, float sx, float sy,
        float sz)
    {
        return Mat4.translate(new Vec3(x, y, z))
            * Mat4.scale(new Vec3(sx, sy, sz));
    }

    // 2 点間に渡す細い箱 (ワイヤーとレール用)
    static Mat4 segmentMat(Vec3 a, Vec3 b, float r)
    {
        var d = b - a;
        float len = d.length();
        var mid = (a + b) * 0.5f;
        var rot = new Mat4();
        if (len > 1e-6f)
        {
            var dir = d * (1.0f / len);
            var axis = Vec3.up().cross(dir);
            float s = axis.length();
            if (s > 1e-6f)
                rot = Quat.fromAxisAngle(axis * (1.0f / s),
                    (float)Math.Atan2(s, dir.y)).toMat4();
            else if (dir.y < 0)
                rot = Mat4.rotateX((float)Math.PI);
        }
        return Mat4.translate(mid) * rot * Mat4.scale(new Vec3(r, len * 0.5f, r));
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

    public static void onFrame(float dt)
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
            gravity = new Vec3d { x = 0.0f, y = -9.81f, z = 0.0f },
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
        r.light.dir = new Vec3(-0.3f, 1.0f, 0.45f);
        r.light.intensity = 1.2f;
        r.sky.top = Color.rgb(0.35f, 0.36f, 0.45f);
        r.sky.bottom = Color.rgb(0.12f, 0.11f, 0.12f);
        r.sky.intensity = 0.45f;
        r.background = Color.rgb(0.10f, 0.10f, 0.13f);
        r.shadow.center = new Vec3(0, 0.3f, 0);
        r.shadow.extent = 1.2f;
        r.begin(new Camera
        {
            eye = new Vec3(0.02f, 1.02f, 1.95f),
            target = new Vec3(0.0f, 0.30f, 0.0f),
            fov = 40,
            near = 0.1f,
            far = 50.0f,
        });

        // 筐体 (描画のみ): 本体・上部飾り・柱・レール
        var body = Color.rgb(0.93f, 0.93f, 0.95f);
        var accent = Color.rgb(0.88f, 0.25f, 0.42f);
        var dark = Color.rgb(0.22f, 0.23f, 0.27f);
        var felt = Color.rgb(0.32f, 0.62f, 0.46f);
        drawBox(boxMat(0.0f, -0.33f, 0.0f, 0.42f, 0.29f, FIELD_HZ + 0.05f), body, null);
        drawBox(boxMat(0.0f, -0.06f, 0.0f, 0.42f, 0.022f, FIELD_HZ + 0.05f), accent, null);
        drawBox(boxMat(0.0f, 0.86f, 0.0f, 0.42f, 0.075f, FIELD_HZ + 0.05f), accent, null);
        foreach (var sx in new List<int> { -1, 1 })
        {
            foreach (var sz in new List<int> { -1, 1 })
            {
                drawBox(boxMat(sx * (FIELD_HX + 0.022f), 0.31f,
                    sz * (FIELD_HZ + 0.028f), 0.016f, 0.315f, 0.016f), body, null);
            }
        }
        // 床 (フェルト) と穴の縁
        drawBox(boxMat(0.0f, -0.02f, -0.175f, FIELD_HX, 0.02f, 0.275f), felt, null);
        drawBox(boxMat(0.175f, -0.02f, 0.275f, 0.20f, 0.02f, 0.175f), felt, null);
        drawBox(boxMat(-0.20f, -0.05f, 0.275f, 0.175f, 0.05f, 0.175f), dark, null); // シュート内部
        // 払い出しの褒め演出: 獲得口の縁が光る (HDR 高輝度で bloom に乗せる)
        if (payoutFlash > 0)
        {
            float k = payoutFlash / 60.0f;
            drawBox(boxMat(-0.20f, 0.005f, 0.275f, 0.178f, 0.006f + 0.02f * k, 0.178f),
                Color.rgb(1.6f, 1.5f, 0.7f + 0.7f * k), null);
        }
        // レール: 固定 2 本 + キャリッジと動く梁
        drawBox(boxMat(-0.34f, 0.76f, 0.0f, 0.012f, 0.012f, FIELD_HZ), dark, null);
        drawBox(boxMat(0.34f, 0.76f, 0.0f, 0.012f, 0.012f, FIELD_HZ), dark, null);
        drawBox(boxMat(0.0f, 0.76f, cz, 0.34f, 0.010f, 0.010f), dark, null);
        drawBox(boxMat(cx, 0.775f, cz, 0.05f, 0.025f, 0.05f), accent, null);

        // ワイヤー + ヘッド + 爪 (物理の実 pose で描く)
        var headPose = Phys3d.phys3d_pose(world, "head");
        if (headPose != null)
        {
            var anchor = new Vec3(headPose.x, headPose.y, headPose.z)
                + new Quat(headPose.qx, headPose.qy, headPose.qz, headPose.qw)
                    .rotateVec3(new Vec3(0, HEAD_TOP, 0));
            drawBox(segmentMat(new Vec3(cx, CARRIAGE_Y, cz), anchor, 0.005f),
                dark, null);
            r.draw(hm, Renderer3d.poseMat(headPose));
        }
        var frPose = Phys3d.phys3d_pose(world, "finger:r");
        if (frPose != null)
            r.draw(fm, Renderer3d.poseMat(frPose));
        var flPose = Phys3d.phys3d_pose(world, "finger:l");
        if (flPose != null)
            r.draw(fm, Renderer3d.poseMat(flPose) * Mat4.rotateY((float)Math.PI));

        // ぬいぐるみ
        foreach (var i in renderBearIndices)
        {
            var pose = Phys3d.phys3d_pose(world, "bear:" + i);
            if (pose != null)
                r.draw(bm[bears[i].variant], Renderer3d.poseMat(pose));
        }

        // ガラスとフェンス (半透明は opaque の後に自動で回る)
        var glass = Color.rgb(0.75f, 0.85f, 0.95f, 0.12f);
        var fence = Color.rgb(0.85f, 0.9f, 1.0f, 0.25f);
        drawBox(boxMat(-FIELD_HX - 0.006f, 0.31f, 0.0f, 0.005f, 0.31f, FIELD_HZ),
            glass, Gfx.ALPHA);
        drawBox(boxMat(FIELD_HX + 0.006f, 0.31f, 0.0f, 0.005f, 0.31f, FIELD_HZ),
            glass, Gfx.ALPHA);
        drawBox(boxMat(0.0f, 0.31f, -FIELD_HZ - 0.006f, FIELD_HX, 0.31f, 0.005f),
            glass, Gfx.ALPHA);
        drawBox(boxMat(-0.20f, 0.07f, 0.10f, 0.175f, 0.07f, 0.005f), fence, Gfx.ALPHA);
        drawBox(boxMat(-0.025f, 0.07f, 0.275f, 0.005f, 0.07f, 0.175f), fence, Gfx.ALPHA);
        drawBox(boxMat(0.0f, 0.31f, FIELD_HZ + 0.006f, FIELD_HX, 0.31f, 0.005f),
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
            grabTorque = Ui.ui_slider_float("grab power", grabTorque, 0.0f, 2.0f);
            holdTorque = Ui.ui_slider_float("hold power", holdTorque, 0.0f, 2.0f);
        }
        Ui.ui_end();
        Ui.ui_render();
        Gfx.end_pass();
    }
}
