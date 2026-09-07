// lub の samples/23_crane_game の entry。
// 実行: lub samples/23_crane_game/CraneGame23.csproj (transpile + watch + hot reload)
//
// 型と tcs 制約:
// - typedef Bear / 匿名構造体 {bear, body, index} {head, fr, fl}
//   {speed, torque} は class Bear / LiveBear / Machine / ClawCommand 化。
// - Renderer3d / Mesh3d は static 初期化子でなく onFrame からの build() で
//   遅延生成する (cs-lib クラスは load 順の都合で static 初期化子から呼べない)。
// - 整数剰余 % は Mod() (floor 分解) で代替、switch は if 連鎖。
// - Phys3d の body/world 取得は null ガード。
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
using static Lub;

public class Bear
{
    public int Gen;
    public int Variant;
    public int Respawn; // >0 = 獲得済み。0 になったら復活 (店員の補充)
    public float X;
    public float Y;
    public float Z;
    public float Yaw;
}

/// <summary>フィールド上に生きているクマ (今フレームの物理 body 付き)。</summary>
public class LiveBear
{
    public Bear Bear;
    public BodyRef3d Body;
    public int Index;

    public LiveBear(Bear bear, BodyRef3d body, int index)
    {
        this.Bear = bear;
        this.Body = body;
        this.Index = index;
    }
}

/// <summary>クレーン機構の可動 body 一式 (declareMachine の戻り値)。</summary>
public class Machine
{
    public BodyRef3d Head;
    public BodyRef3d Fr;
    public BodyRef3d Fl;

    public Machine(BodyRef3d head, BodyRef3d fr, BodyRef3d fl)
    {
        this.Head = head;
        this.Fr = fr;
        this.Fl = fl;
    }
}

/// <summary>爪モーター指示 (右用の符号。左は反転)。</summary>
public class ClawCommand
{
    public float Speed;
    public float Torque;
}

public static class CraneGame23
{
    const float tickDt = 1.0f / 60.0f;

    // --- 実寸パラメータ (フィールド 750×900mm、実機調査に基づく) ---------
    const float fieldHx = 0.375f; // フィールド半幅 (X)
    const float fieldHz = 0.45f; // フィールド半奥行 (Z)。+Z が手前
    const float carriageY = 0.78f;
    const float moveSpeed = 0.15f; // ガントリー移動 (m/s)
    const float winchSpeed = 0.20f; // 昇降 (m/s)
    const float wireMin = 0.15f;
    const float wireMax = 0.56f;
    const float openAngle = 0.85f; // 爪の開き角 (rad)
    const float headTop = 0.06f; // ヘッド原点→ワイヤー取付点
    const float shoulderX = 0.10f; // 爪の肩関節 (ヘッド原点から)
    const float shoulderY = -0.01f;
    const float homeX = -0.16f; // 待機位置 = 獲得口の真上
    const float homeZ = 0.275f;
    const float maxX = 0.15f; // 可動範囲 (店側設定。開いた爪がガラスに触れない位置まで)
    const float minZ = -0.30f;
    // 獲得口 (シュート): 手前左の床穴。判定に使う内側 2 辺
    const float chuteX1 = -0.025f;
    const float chuteZ0 = 0.10f;

    // アームパワー。実機の店側パワー設定に相当し、把持はトルク上限 × 摩擦で決まる。
    // 初動 1.2 N·m で約 330g のクマを掴め、保持 0.6 N·m は揺れ・加速で
    // 滑る境界値 (デモ実測でおよそ 4-5 回に 1 回獲得 = 実機並み)
    static float grabTorque = 1.2f;
    static float holdTorque = 0.6f;

    // --- 状態機械 (実機の自動シーケンス) ---------------------------------
    const int stIdle = 0;
    const int stMoveX = 1;
    const int stWait2 = 2;
    const int stMoveZ = 3;
    const int stDescend = 4;
    const int stGrab = 5;
    const int stLift = 6;
    const int stCarry = 7;
    const int stRelease = 8;
    const int stReset = 9;
    static List<string> stateNames = new List<string>
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
    static int state = stIdle;
    static int stateT = 0;
    static float cx = homeX;
    static float cz = homeZ;
    static float wireLen = wireMin;
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

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend, Width = 640, Height = 360 });
        // 初期配置: 可動範囲内 (x <= MAX_X) に散らす。座標は固定 (決定論)
        bears = new List<Bear>
        {
            new Bear { Gen = 1, Variant = 0, Respawn = 0, X = 0.08f, Y = 0.02f, Z = -0.05f, Yaw = 0.4f },
            new Bear { Gen = 1, Variant = 1, Respawn = 0, X = -0.14f, Y = 0.02f, Z = -0.26f, Yaw = -0.7f },
            new Bear { Gen = 1, Variant = 2, Respawn = 0, X = 0.15f, Y = 0.02f, Z = 0.18f, Yaw = 2.6f },
            new Bear { Gen = 1, Variant = 0, Respawn = 0, X = 0.06f, Y = 0.02f, Z = 0.18f, Yaw = 1.8f },
            new Bear { Gen = 1, Variant = 1, Respawn = 0, X = 0.16f, Y = 0.02f, Z = -0.24f, Yaw = -2.2f },
        };
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    /// <summary>非負 int の剰余 (tcs は % を使わない規約なので floor 分解)。</summary>
    static int Mod(int a, int n)
    {
        return (int)(a - (float)Math.Floor((float)a / n) * n);
    }

    // --- SDF モデル -------------------------------------------------------
    // クマ (約 30cm)。物理 compound (declareBearShapes) と寸法を揃えている
    static SdfNode BearModel(int fur, int belly)
    {
        var body = Sdf.Sphere(0.100f).Move(0, 0.100f, 0);
        var head = Sdf.Sphere(0.072f).Move(0, 0.220f, 0);
        var ear = Sdf.Sphere(0.026f).Move(0.050f, 0.284f, 0).MirrorX();
        var arm = Sdf.Capsule(new Vec3(0.080f, 0.150f, 0.010f),
            new Vec3(0.130f, 0.075f, 0.030f), 0.027f).MirrorX();
        var leg = Sdf.Capsule(new Vec3(0.050f, 0.045f, 0.020f),
            new Vec3(0.100f, 0.035f, 0.105f), 0.033f).MirrorX();
        var muzzle = Sdf.Sphere(0.030f).Move(0, 0.198f, 0.058f)
            .Paint(belly, 0.0f, 0.9f);
        var tummy = Sdf.Sphere(0.052f).Move(0, 0.090f, 0.062f)
            .Paint(belly, 0.0f, 0.9f);
        var eye = Sdf.Sphere(0.010f).Move(0.028f, 0.238f, 0.062f).MirrorX()
            .Paint(0x1E2130, 0.0f, 0.2f);
        return body.Smin(head, 0.02f)
            .Smin(ear, 0.012f)
            .Smin(arm, 0.015f)
            .Smin(leg, 0.015f)
            .Paint(fur, 0.0f, 0.9f)
            .Smin(muzzle, 0.010f)
            .Smin(tummy, 0.012f)
            .Ssub(eye, 0.004f);
    }

    // 爪 1 本 (右用)。肩 (原点) → 肘 → 爪先の「反り 120°」形状。
    // 左は描画・物理とも X 反転 (rotateY(π))
    static SdfNode FingerModel()
    {
        var upper = Sdf.Capsule(new Vec3(0, 0, 0),
            new Vec3(0.050f, -0.110f, 0), 0.009f);
        var lower = Sdf.Capsule(new Vec3(0.050f, -0.110f, 0),
            new Vec3(-0.085f, -0.215f, 0), 0.008f);
        return upper.Smin(lower, 0.010f).Paint(0xC9CED8, 0.9f, 0.25f);
    }

    // ヘッド: ドーム + リング。原点はリング面の中心
    static SdfNode HeadModel()
    {
        var dome = Sdf.Sphere(0.105f)
            .Intersect(Sdf.Box(0.11f, 0.055f, 0.11f).Move(0, 0.055f, 0))
            .Paint(0xF2F2F4, 0.1f, 0.4f);
        var rim = Sdf.Torus(0.095f, 0.032f).Paint(0xE0405A, 0.2f, 0.5f);
        return dome.Smin(rim, 0.015f);
    }

    // --- メッシュ (hot reload 対応: dirty フラグで再メッシュ。native watch は
    // chunk 再実行で初期値 true に戻り、web の module mode は onReload で立てる) --
    static bool meshDirty = true;

    public static void OnReload()
    {
        meshDirty = true;
    }
    static Renderer3d? ren = null;
    static List<Mesh3d>? bearMeshes = null;
    static Mesh3d? fingerMesh = null;
    static Mesh3d? headMesh = null;
    static Mesh3d? cubeMesh = null;

    static void Build()
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

    static void Remesh()
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
            bm[i].Rebuild(Sdf.Mesh(BearModel(furs[i], bellies[i]), 56));
        }
        fm.Rebuild(Sdf.Mesh(FingerModel(), 48));
        hm.Rebuild(Sdf.Mesh(HeadModel(), 56));
        if (!cm.Ready())
            cm.Rebuild(Shapes3d.Cube());
        meshDirty = false;
    }

    // --- 物理: クマは球 + カプセルの複数 shape で近似 (SDF と同じ寸法)。
    // compound は static 専用なので、dynamic body には shape を複数ぶら下げる
    static void DeclareBearShapes(BodyRef3d body, int ver)
    {
        float density = 50.0f; // 密度 50kg/m³ → 約 330g
        float friction = 0.6f;
        float restitution = 0.02f;
        Phys3d.Sphere(body, "torso", new SphereDesc3d
        {
            Version = ver,
            R = 0.100f,
            Offset = new Vec3d { X = 0.0f, Y = 0.100f, Z = 0.0f },
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Sphere(body, "head", new SphereDesc3d
        {
            Version = ver,
            R = 0.072f,
            Offset = new Vec3d { X = 0.0f, Y = 0.220f, Z = 0.0f },
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Capsule(body, "arm_r", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = 0.080f, Y = 0.150f, Z = 0.010f },
            B = new Vec3d { X = 0.130f, Y = 0.075f, Z = 0.030f },
            R = 0.027f,
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Capsule(body, "arm_l", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = -0.080f, Y = 0.150f, Z = 0.010f },
            B = new Vec3d { X = -0.130f, Y = 0.075f, Z = 0.030f },
            R = 0.027f,
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Capsule(body, "leg_r", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = 0.050f, Y = 0.045f, Z = 0.020f },
            B = new Vec3d { X = 0.100f, Y = 0.035f, Z = 0.105f },
            R = 0.033f,
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Capsule(body, "leg_l", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = -0.050f, Y = 0.045f, Z = 0.020f },
            B = new Vec3d { X = -0.100f, Y = 0.035f, Z = 0.105f },
            R = 0.033f,
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
    }

    // 爪 1 本の物理 (右用。左は sign = -1 で X 反転)
    static void DeclareFingerShapes(BodyRef3d body, float sign)
    {
        Phys3d.Capsule(body, "upper", new CapsuleDesc3d
        {
            A = new Vec3d { X = 0.0f, Y = 0.0f, Z = 0.0f },
            B = new Vec3d { X = sign * 0.050f, Y = -0.110f, Z = 0.0f },
            R = 0.009f,
            Density = 2000.0f,
            Friction = 0.6f,
        });
        Phys3d.Capsule(body, "lower", new CapsuleDesc3d
        {
            A = new Vec3d { X = sign * 0.050f, Y = -0.110f, Z = 0.0f },
            B = new Vec3d { X = sign * -0.085f, Y = -0.215f, Z = 0.0f },
            R = 0.008f,
            Density = 2000.0f,
            Friction = 0.6f,
        });
    }

    // 静物: 床 (獲得口の穴あき) + アクリルフェンス + ガラス壁 + シュート筒
    static List<float[]> statics = new List<float[]>
    {
        // x, y, z, hx, hy, hz
        new float[] { 0.0f, -0.02f, -0.175f, fieldHx, 0.02f, 0.275f }, // 床 (奥側)
        new float[] { 0.175f, -0.02f, 0.275f, 0.20f, 0.02f, 0.175f }, // 床 (手前右)
        new float[] { -0.20f, 0.07f, 0.10f, 0.175f, 0.07f, 0.006f }, // フェンス (穴の奥側)
        new float[] { -0.025f, 0.07f, 0.275f, 0.006f, 0.07f, 0.175f }, // フェンス (穴の右側)
        new float[] { -fieldHx - 0.006f, 0.31f, 0.0f, 0.006f, 0.31f, fieldHz }, // ガラス左
        new float[] { fieldHx + 0.006f, 0.31f, 0.0f, 0.006f, 0.31f, fieldHz }, // ガラス右
        new float[] { 0.0f, 0.31f, -fieldHz - 0.006f, fieldHx, 0.31f, 0.006f }, // ガラス奥
        new float[] { 0.0f, 0.31f, fieldHz + 0.006f, fieldHx, 0.31f, 0.006f }, // ガラス手前
        new float[] { -0.025f, -0.25f, 0.275f, 0.006f, 0.25f, 0.175f }, // シュート筒 右
        new float[] { -0.20f, -0.25f, 0.10f, 0.175f, 0.25f, 0.006f }, // シュート筒 奥
        new float[] { -fieldHx - 0.006f, -0.25f, 0.275f, 0.006f, 0.25f, 0.175f }, // シュート筒 左
        new float[] { -0.20f, -0.25f, fieldHz + 0.006f, 0.175f, 0.25f, 0.006f }, // シュート筒 手前
    };

    static void DeclareStatics(WorldRef3d world)
    {
        for (int i = 0; i < statics.Count; i++)
        {
            var s = statics[i];
            var body = Phys3d.Body(world, "static:" + i, new BodyDesc3d
            {
                Type = Phys3d.BodyType.Static,
                Initial = new InitialState3d { X = s[0], Y = s[1], Z = s[2] },
            });
            if (body == null) continue;
            Phys3d.Box(body, "solid", new BoxDesc3d
            {
                Hx = s[3],
                Hy = s[4],
                Hz = s[5],
                Friction = 0.5f,
                Restitution = 0.05f,
            });
        }
    }

    static Machine? DeclareMachine(WorldRef3d world)
    {
        // キャリッジ: レール上を走るユニット。kinematic + setTarget で速度を持つ
        var carriage = Phys3d.Body(world, "carriage", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Kinematic,
            Initial = new InitialState3d { X = homeX, Y = carriageY, Z = homeZ },
        });
        if (carriage == null) return null;
        Phys3d.SetTarget(carriage, new TargetDesc3d
        {
            X = cx,
            Y = carriageY,
            Z = cz,
            TimeStep = tickDt,
        });

        // ヘッド: ワイヤー 1 本吊り (実機の主流はワイヤー巻き取り式)。
        // damping は空力とワイヤー内部摩擦・捩り抵抗による実在の損失の近似
        float headY0 = carriageY - wireMin - headTop;
        var head = Phys3d.Body(world, "head", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            LinearDamping = 0.15f,
            AngularDamping = 0.5f,
            Initial = new InitialState3d { X = homeX, Y = headY0, Z = homeZ },
        });
        if (head == null) return null;
        Phys3d.Cylinder(head, "solid", new CylinderDesc3d
        {
            Height = 0.08f,
            Radius = 0.105f,
            YOffset = 0.02f,
            Density = 400.0f, // ヘッド質量 ≈ 1.1kg
            Friction = 0.3f,
        });

        // ワイヤー: バネ力 0 のバネ + 上限 limit = 引けるが押せないロープ。
        // 巻き上げ機は maxLength を増減するだけ (実機のスプール相当)
        Phys3d.Joint(world, "wire", new JointDesc3d
        {
            Type = Phys3d.JointType.Distance,
            BodyA = carriage,
            BodyB = head,
            AnchorA = new Vec3d { X = homeX, Y = carriageY, Z = homeZ },
            AnchorB = new Vec3d { X = homeX, Y = headY0 + headTop, Z = homeZ },
            EnableSpring = true,
            Hertz = 0.0f,
            DampingRatio = 0.0f,
            EnableLimit = true,
            MinLength = 0.02f,
            MaxLength = wireLen,
            Length = wireLen,
        });

        // ハーネス: 実機のヘッドはワイヤーに加えて電源ケーブル束でも
        // つながっており、その曲げ・捩り剛性が回転を抑える。motor joint の
        // 姿勢バネ (トルク上限つき) でモデル化する。並進には作用しない。
        // 上限を超える外力ではちゃんと傾く (着地時など)
        Phys3d.Joint(world, "harness", new JointDesc3d
        {
            Type = Phys3d.JointType.Motor,
            BodyA = carriage,
            BodyB = head,
            MaxVelocityForce = 0.0f,
            MaxVelocityTorque = 0.0f,
            LinearHertz = 0.0f,
            MaxSpringForce = 0.0f,
            AngularHertz = 1.2f,
            AngularDampingRatio = 1.0f,
            MaxSpringTorque = 2.5f,
        });

        // 爪 2 本: 肩の revolute joint。モーターのトルク上限がアームパワー。
        // 開閉指示は状態機械から (clawCommand)。angularDamping は関節部の摩擦損失
        var fr = Phys3d.Body(world, "finger:r", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            AngularDamping = 1.0f,
            Initial = new InitialState3d
            {
                X = homeX + shoulderX,
                Y = headY0 + shoulderY,
                Z = homeZ,
            },
        });
        if (fr == null) return null;
        DeclareFingerShapes(fr, 1.0f);
        var fl = Phys3d.Body(world, "finger:l", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            AngularDamping = 1.0f,
            Initial = new InitialState3d
            {
                X = homeX - shoulderX,
                Y = headY0 + shoulderY,
                Z = homeZ,
            },
        });
        if (fl == null) return null;
        DeclareFingerShapes(fl, -1.0f);

        var claw = ClawCommand();
        Phys3d.Joint(world, "claw:r", new JointDesc3d
        {
            Type = Phys3d.JointType.Revolute,
            BodyA = head,
            BodyB = fr,
            AnchorA = new Vec3d
            { X = homeX + shoulderX, Y = headY0 + shoulderY, Z = homeZ },
            AnchorB = new Vec3d
            { X = homeX + shoulderX, Y = headY0 + shoulderY, Z = homeZ },
            Axis = new Vec3d { X = 0.0f, Y = 0.0f, Z = 1.0f },
            EnableLimit = true,
            Lower = 0.0f,
            Upper = openAngle,
            EnableMotor = true,
            MotorSpeed = claw.Speed,
            MaxTorque = claw.Torque,
        });
        Phys3d.Joint(world, "claw:l", new JointDesc3d
        {
            Type = Phys3d.JointType.Revolute,
            BodyA = head,
            BodyB = fl,
            AnchorA = new Vec3d
            { X = homeX - shoulderX, Y = headY0 + shoulderY, Z = homeZ },
            AnchorB = new Vec3d
            { X = homeX - shoulderX, Y = headY0 + shoulderY, Z = homeZ },
            Axis = new Vec3d { X = 0.0f, Y = 0.0f, Z = 1.0f },
            EnableLimit = true,
            Lower = -openAngle,
            Upper = 0.0f,
            EnableMotor = true,
            MotorSpeed = -claw.Speed,
            MaxTorque = claw.Torque,
        });
        return new Machine(head, fr, fl);
    }

    // 状態ごとの爪モーター指示 (右用の符号。左は反転)。
    // speed > 0 = 開く。実機の位相別パワー (初動/保持) をここで切り替える。
    // 速度は実機並みにゆっくり (速いとリミット衝突の反動でヘッドが暴れる)
    static ClawCommand ClawCommand()
    {
        // プレイ開始 (移動) から降下まで開きっぱなし (実機と同じ)
        if (state == stMoveX || state == stWait2 || state == stMoveZ
            || state == stDescend)
            return new ClawCommand { Speed = 1.8f, Torque = 0.9f };
        if (state == stGrab || state == stLift)
            return new ClawCommand { Speed = -2.0f, Torque = grabTorque }; // 初動 (掴む〜持ち上げ)
        if (state == stCarry)
            return new ClawCommand { Speed = -2.0f, Torque = holdTorque }; // 保持 (運搬中に弱まる)
        if (state == stRelease)
            return new ClawCommand { Speed = 1.8f, Torque = 0.9f }; // 獲得口で開放
        return new ClawCommand { Speed = -1.5f, Torque = 0.5f }; // 待機は閉じ
    }

    static List<LiveBear> DeclareBears(WorldRef3d world)
    {
        var live = new List<LiveBear>();
        for (int i = 0; i < bears.Count; i++)
        {
            var b = bears[i];
            if (b.Respawn > 0)
                continue;
            var body = Phys3d.Body(world, "bear:" + i, new BodyDesc3d
            {
                Type = Phys3d.BodyType.Dynamic,
                Version = b.Gen,
                LinearDamping = 0.05f,
                AngularDamping = 0.5f, // 布と詰め物の内部損失の近似
                Initial = new InitialState3d
                {
                    X = b.X,
                    Y = b.Y,
                    Z = b.Z,
                    Euler = new Vec3d { X = 0.0f, Y = b.Yaw, Z = 0.0f },
                },
            });
            if (body == null) continue;
            DeclareBearShapes(body, b.Gen);
            live.Add(new LiveBear(b, body, i));
        }
        return live;
    }

    // --- 状態機械 ----------------------------------------------------------
    static bool ButtonHeld()
    {
        if (autoPlay)
        {
            // attract: 目標座標に届くまで押し続ける動作を合成
            if (state == stMoveX) return cx < autoX - 0.005f;
            if (state == stMoveZ) return cz > autoZ + 0.005f;
            return false;
        }
        return Input.KeyDown("space")
            || (Input.MouseDown() && !Ui.WantCaptureMouse());
    }

    static bool ButtonPressed(bool tickPressed)
    {
        if (autoPlay)
            return state == stIdle || state == stWait2;
        return tickPressed;
    }

    static void Enter(int s)
    {
        state = s;
        stateT = 0;
        slackFrames = 0;
    }

    static void UpdateSequence(WorldRef3d world, BodyRef3d head,
        bool tickPressed)
    {
        stateT++;
        if (state == stIdle)
        {
            wireLen = wireMin;
            if (ButtonPressed(tickPressed))
            {
                plays++;
                Enter(stMoveX);
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
                        if (b.Respawn == 0)
                        {
                            target = b;
                            targetIndex = idx;
                            autoIndex = Mod(autoIndex + k + 1, bears.Count);
                            break;
                        }
                    }
                    if (target != null)
                    {
                        var pose = Phys3d.PoseByKey(world, "bear:" + targetIndex);
                        autoX = MathUtil.Clamp(pose != null ? pose.X : target.X,
                            homeX, maxX);
                        autoZ = MathUtil.Clamp(pose != null ? pose.Z : target.Z,
                            minZ, homeZ);
                        autoPlay = true;
                        plays++;
                        Enter(stMoveX);
                    }
                    idleT = 0;
                }
            }
        }
        else if (state == stMoveX)
        {
            if (ButtonHeld())
                cx = Math.Min(cx + moveSpeed * tickDt, maxX);
            else if (stateT > 5)
                Enter(stWait2);
        }
        else if (state == stWait2)
        {
            if (ButtonPressed(tickPressed))
                Enter(stMoveZ);
            else if (stateT > 420) // 実機同様、放置でも自動で降下へ
                Enter(stDescend);
        }
        else if (state == stMoveZ)
        {
            if (ButtonHeld())
                cz = Math.Max(cz - moveSpeed * tickDt, minZ);
            else if (stateT > 5)
                Enter(stDescend);
        }
        else if (state == stDescend)
        {
            // 移動直後の振り子揺れが収まるまで一呼吸置いてから繰り出す
            if (stateT > 15)
                wireLen = Math.Min(wireLen + winchSpeed * tickDt, wireMax);
            // 着地検出 = ワイヤー張力低下 (実機はテンションセンサー)。
            // 繰り出し量に対して実距離が短い = 弛み。揺れによる瞬間的な
            // 弛みを拾わないよう連続フレームでデバウンスする
            var pose = Phys3d.Pose(head);
            if (pose != null)
            {
                var anchor = new Vec3(pose.X, pose.Y, pose.Z)
                    + new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw)
                        .RotateVec3(new Vec3(0, headTop, 0));
                float dist = new Vec3(cx, carriageY, cz).Distance(anchor);
                slackFrames = (wireLen - dist > 0.03f) ? slackFrames + 1 : 0;
                if ((slackFrames >= 8 && stateT > 40) || wireLen >= wireMax)
                {
                    Enter(stGrab);
                }
            }
        }
        else if (state == stGrab)
        {
            if (stateT > 50)
                Enter(stLift);
        }
        else if (state == stLift)
        {
            wireLen = Math.Max(wireLen - winchSpeed * tickDt, wireMin);
            if (wireLen <= wireMin)
                Enter(stCarry);
        }
        else if (state == stCarry)
        {
            float dx = homeX - cx;
            float dz = homeZ - cz;
            cx += MathUtil.Clamp(dx, -moveSpeed * tickDt, moveSpeed * tickDt);
            cz += MathUtil.Clamp(dz, -moveSpeed * tickDt, moveSpeed * tickDt);
            if (Math.Abs(dx) < 0.002f && Math.Abs(dz) < 0.002f)
                Enter(stRelease);
        }
        else if (state == stRelease)
        {
            if (stateT > 70)
                Enter(stReset);
        }
        else if (state == stReset)
        {
            if (stateT > 40)
            {
                autoPlay = false;
                idleT = 0;
                Enter(stIdle);
            }
        }
    }

    // 獲得判定: シュート筒の中に落ちたら得点、それ以外の転落は保険で回収
    static void UpdatePrizes(List<LiveBear> live)
    {
        foreach (var entry in live)
        {
            var pose = Phys3d.Pose(entry.Body);
            if (pose == null)
                continue;
            if (pose.Y < -0.32f)
            {
                entry.Bear.Respawn = 150;
                if (pose.X < chuteX1 && pose.Z > chuteZ0)
                {
                    score++;
                    payoutFlash = 60;
                }
            }
        }
        for (int i = 0; i < bears.Count; i++)
        {
            var b = bears[i];
            if (b.Respawn > 0)
            {
                b.Respawn--;
                if (b.Respawn == 0)
                {
                    // 補充: フィールド奥へ落とす。位置は決定論的にずらす
                    b.Gen++;
                    b.X = 0.02f + Mod(b.Gen * 53, 13) * 0.012f;
                    b.Y = 0.35f;
                    b.Z = -0.15f + Mod(b.Gen * 31, 11) * 0.02f;
                    b.Yaw = Mod(b.Gen * 137, 63) * 0.1f;
                }
            }
        }
    }

    // --- 描画 --------------------------------------------------------------
    static Mat4 BoxMat(float x, float y, float z, float sx, float sy,
        float sz)
    {
        return Mat4.Translate(new Vec3(x, y, z))
            * Mat4.Scale(new Vec3(sx, sy, sz));
    }

    // 2 点間に渡す細い箱 (ワイヤーとレール用)
    static Mat4 SegmentMat(Vec3 a, Vec3 b, float r)
    {
        var d = b - a;
        float len = d.Length();
        var mid = (a + b) * 0.5f;
        var rot = new Mat4();
        if (len > 1e-6f)
        {
            var dir = d * (1.0f / len);
            var axis = Vec3.Up().Cross(dir);
            float s = axis.Length();
            if (s > 1e-6f)
                rot = Quat.FromAxisAngle(axis * (1.0f / s),
                    (float)Math.Atan2(s, dir.Y)).ToMat4();
            else if (dir.Y < 0)
                rot = Mat4.RotateX((float)Math.PI);
        }
        return Mat4.Translate(mid) * rot * Mat4.Scale(new Vec3(r, len * 0.5f, r));
    }

    static void DrawBox(Mat4 model, Color color, Gfx.Blend? blend)
    {
        var r = ren;
        var cube = cubeMesh;
        if (r == null || cube == null) return;
        r.Draw(cube, model, new Draw3dOpts { Tint = color, Blend = blend });
    }

    static void SimulateTick(WorldRef3d world)
    {
        // render 側で保持した edge は次の logical tick だけで有効。
        // 受付外 state の押下を数秒後の IDLE / WAIT2 へ持ち越さない。
        bool tickPressed = pendingPresses > 0;
        pendingPresses = 0;
        if (payoutFlash > 0)
            payoutFlash--;
        Phys3d.Begin(world);
        DeclareStatics(world);
        var machine = DeclareMachine(world);
        if (machine == null) return;
        var live = DeclareBears(world);
        renderBearIndices = new List<int>();
        foreach (var entry in live)
            renderBearIndices.Add(entry.Index);
        UpdateSequence(world, machine.Head, tickPressed);
        Phys3d.Step(world, tickDt);
        UpdatePrizes(live);
        frame++;
    }

    public static void OnFrame(float dt)
    {
        Build();
        if (meshDirty)
            Remesh();
        var r = ren;
        var bm = bearMeshes;
        var fm = fingerMesh;
        var hm = headMesh;
        if (r == null || bm == null || fm == null || hm == null)
            return;
        if (!autoPlay && (Input.KeyPressed("space")
            || (Input.MousePressed() && !Ui.WantCaptureMouse())))
            pendingPresses = pendingPresses + 1;

        var world = Phys3d.World("crane_game", new WorldOpts3d
        {
            Gravity = new Vec3d { X = 0.0f, Y = -9.81f, Z = 0.0f },
            FixedDt = tickDt,
            Substeps = 8,
            MaxSteps = 1,
        });
        if (world == null) return;
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => SimulateTick(world));

        // --- draw ---
        // ゲームセンターの薄暗い環境 + 筐体上部からの光
        r.Light.Dir = new Vec3(-0.3f, 1.0f, 0.45f);
        r.Light.Intensity = 1.2f;
        r.Sky.Top = Color.Rgb(0.35f, 0.36f, 0.45f);
        r.Sky.Bottom = Color.Rgb(0.12f, 0.11f, 0.12f);
        r.Sky.Intensity = 0.45f;
        r.Background = Color.Rgb(0.10f, 0.10f, 0.13f);
        r.Shadow.Center = new Vec3(0, 0.3f, 0);
        r.Shadow.Extent = 1.2f;
        r.Begin(new Camera
        {
            Eye = new Vec3(0.02f, 1.02f, 1.95f),
            Target = new Vec3(0.0f, 0.30f, 0.0f),
            Fov = 40,
            Near = 0.1f,
            Far = 50.0f,
        });

        // 筐体 (描画のみ): 本体・上部飾り・柱・レール
        var body = Color.Rgb(0.93f, 0.93f, 0.95f);
        var accent = Color.Rgb(0.88f, 0.25f, 0.42f);
        var dark = Color.Rgb(0.22f, 0.23f, 0.27f);
        var felt = Color.Rgb(0.32f, 0.62f, 0.46f);
        DrawBox(BoxMat(0.0f, -0.33f, 0.0f, 0.42f, 0.29f, fieldHz + 0.05f), body, null);
        DrawBox(BoxMat(0.0f, -0.06f, 0.0f, 0.42f, 0.022f, fieldHz + 0.05f), accent, null);
        DrawBox(BoxMat(0.0f, 0.86f, 0.0f, 0.42f, 0.075f, fieldHz + 0.05f), accent, null);
        foreach (var sx in new List<int> { -1, 1 })
        {
            foreach (var sz in new List<int> { -1, 1 })
            {
                DrawBox(BoxMat(sx * (fieldHx + 0.022f), 0.31f,
                    sz * (fieldHz + 0.028f), 0.016f, 0.315f, 0.016f), body, null);
            }
        }
        // 床 (フェルト) と穴の縁
        DrawBox(BoxMat(0.0f, -0.02f, -0.175f, fieldHx, 0.02f, 0.275f), felt, null);
        DrawBox(BoxMat(0.175f, -0.02f, 0.275f, 0.20f, 0.02f, 0.175f), felt, null);
        DrawBox(BoxMat(-0.20f, -0.05f, 0.275f, 0.175f, 0.05f, 0.175f), dark, null); // シュート内部
        // 払い出しの褒め演出: 獲得口の縁が光る (HDR 高輝度で bloom に乗せる)
        if (payoutFlash > 0)
        {
            float k = payoutFlash / 60.0f;
            DrawBox(BoxMat(-0.20f, 0.005f, 0.275f, 0.178f, 0.006f + 0.02f * k, 0.178f),
                Color.Rgb(1.6f, 1.5f, 0.7f + 0.7f * k), null);
        }
        // レール: 固定 2 本 + キャリッジと動く梁
        DrawBox(BoxMat(-0.34f, 0.76f, 0.0f, 0.012f, 0.012f, fieldHz), dark, null);
        DrawBox(BoxMat(0.34f, 0.76f, 0.0f, 0.012f, 0.012f, fieldHz), dark, null);
        DrawBox(BoxMat(0.0f, 0.76f, cz, 0.34f, 0.010f, 0.010f), dark, null);
        DrawBox(BoxMat(cx, 0.775f, cz, 0.05f, 0.025f, 0.05f), accent, null);

        // ワイヤー + ヘッド + 爪 (物理の実 pose で描く)
        var headPose = Phys3d.PoseByKey(world, "head");
        if (headPose != null)
        {
            var anchor = new Vec3(headPose.X, headPose.Y, headPose.Z)
                + new Quat(headPose.Qx, headPose.Qy, headPose.Qz, headPose.Qw)
                    .RotateVec3(new Vec3(0, headTop, 0));
            DrawBox(SegmentMat(new Vec3(cx, carriageY, cz), anchor, 0.005f),
                dark, null);
            r.Draw(hm, Renderer3d.PoseMat(headPose));
        }
        var frPose = Phys3d.PoseByKey(world, "finger:r");
        if (frPose != null)
            r.Draw(fm, Renderer3d.PoseMat(frPose));
        var flPose = Phys3d.PoseByKey(world, "finger:l");
        if (flPose != null)
            r.Draw(fm, Renderer3d.PoseMat(flPose) * Mat4.RotateY((float)Math.PI));

        // ぬいぐるみ
        foreach (var i in renderBearIndices)
        {
            var pose = Phys3d.PoseByKey(world, "bear:" + i);
            if (pose != null)
                r.Draw(bm[bears[i].Variant], Renderer3d.PoseMat(pose));
        }

        // ガラスとフェンス (半透明は opaque の後に自動で回る)
        var glass = Color.Rgb(0.75f, 0.85f, 0.95f, 0.12f);
        var fence = Color.Rgb(0.85f, 0.9f, 1.0f, 0.25f);
        DrawBox(BoxMat(-fieldHx - 0.006f, 0.31f, 0.0f, 0.005f, 0.31f, fieldHz),
            glass, Gfx.Blend.Alpha);
        DrawBox(BoxMat(fieldHx + 0.006f, 0.31f, 0.0f, 0.005f, 0.31f, fieldHz),
            glass, Gfx.Blend.Alpha);
        DrawBox(BoxMat(0.0f, 0.31f, -fieldHz - 0.006f, fieldHx, 0.31f, 0.005f),
            glass, Gfx.Blend.Alpha);
        DrawBox(BoxMat(-0.20f, 0.07f, 0.10f, 0.175f, 0.07f, 0.005f), fence, Gfx.Blend.Alpha);
        DrawBox(BoxMat(-0.025f, 0.07f, 0.275f, 0.005f, 0.07f, 0.175f), fence, Gfx.Blend.Alpha);
        DrawBox(BoxMat(0.0f, 0.31f, fieldHz + 0.006f, fieldHx, 0.31f, 0.005f),
            glass, Gfx.Blend.Alpha);

        r.End();

        // UI は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        Ui.SetNextWindow(10, 10, 240, 150);
        if (Ui.BeginWindow("crane game"))
        {
            Ui.Text("prizes: " + score + "  plays: " + plays);
            Ui.Text("state: " + stateNames[state]
                + (autoPlay ? " (auto)" : ""));
            Ui.Text("hold Space/click: right, then back");
            Ui.Separator();
            grabTorque = Ui.SliderFloat("grab power", grabTorque, 0.0f, 2.0f);
            holdTorque = Ui.SliderFloat("hold power", holdTorque, 0.0f, 2.0f);
        }
        Ui.EndWindow();
        Ui.Render();
        Gfx.EndPass();
    }
}
