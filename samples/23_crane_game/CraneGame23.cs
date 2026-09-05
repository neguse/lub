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
using static Lub;

public class Bear
{
    public int Gen;
    public int Variant;
    public int Respawn; // >0 = 獲得済み。0 になったら復活 (店員の補充)
    public double X;
    public double Y;
    public double Z;
    public double Yaw;
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
    public double Speed;
    public double Torque;
}

public static class CraneGame23
{
    const double dt = 1.0 / 60.0;

    // --- 実寸パラメータ (フィールド 750×900mm、実機調査に基づく) ---------
    const double fieldHx = 0.375; // フィールド半幅 (X)
    const double fieldHz = 0.45; // フィールド半奥行 (Z)。+Z が手前
    const double carriageY = 0.78;
    const double moveSpeed = 0.15; // ガントリー移動 (m/s)
    const double winchSpeed = 0.20; // 昇降 (m/s)
    const double wireMin = 0.15;
    const double wireMax = 0.56;
    const double openAngle = 0.85; // 爪の開き角 (rad)
    const double headTop = 0.06; // ヘッド原点→ワイヤー取付点
    const double shoulderX = 0.10; // 爪の肩関節 (ヘッド原点から)
    const double shoulderY = -0.01;
    const double homeX = -0.16; // 待機位置 = 獲得口の真上
    const double homeZ = 0.275;
    const double maxX = 0.15; // 可動範囲 (店側設定。開いた爪がガラスに触れない位置まで)
    const double minZ = -0.30;
    // 獲得口 (シュート): 手前左の床穴。判定に使う内側 2 辺
    const double chuteX1 = -0.025;
    const double chuteZ0 = 0.10;

    // アームパワー。実機の店側パワー設定に相当し、把持はトルク上限 × 摩擦で決まる。
    // 初動 1.2 N·m で約 330g のクマを掴め、保持 0.6 N·m は揺れ・加速で
    // 滑る境界値 (デモ実測でおよそ 4-5 回に 1 回獲得 = 実機並み)
    static double grabTorque = 1.2;
    static double holdTorque = 0.6;

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
    static double cx = homeX;
    static double cz = homeZ;
    static double wireLen = wireMin;
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

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend, Width = 640, Height = 360 });
        // 初期配置: 可動範囲内 (x <= MAX_X) に散らす。座標は固定 (決定論)
        bears = new List<Bear>
        {
            new Bear { Gen = 1, Variant = 0, Respawn = 0, X = 0.08, Y = 0.02, Z = -0.05, Yaw = 0.4 },
            new Bear { Gen = 1, Variant = 1, Respawn = 0, X = -0.14, Y = 0.02, Z = -0.26, Yaw = -0.7 },
            new Bear { Gen = 1, Variant = 2, Respawn = 0, X = 0.15, Y = 0.02, Z = 0.18, Yaw = 2.6 },
            new Bear { Gen = 1, Variant = 0, Respawn = 0, X = 0.06, Y = 0.02, Z = 0.18, Yaw = 1.8 },
            new Bear { Gen = 1, Variant = 1, Respawn = 0, X = 0.16, Y = 0.02, Z = -0.24, Yaw = -2.2 },
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
        return (int)(a - Math.Floor((double)a / n) * n);
    }

    // --- SDF モデル -------------------------------------------------------
    // クマ (約 30cm)。物理 compound (declareBearShapes) と寸法を揃えている
    static SdfNode BearModel(int fur, int belly)
    {
        var body = Sdf.Sphere(0.100).Move(0, 0.100, 0);
        var head = Sdf.Sphere(0.072).Move(0, 0.220, 0);
        var ear = Sdf.Sphere(0.026).Move(0.050, 0.284, 0).MirrorX();
        var arm = Sdf.Capsule(new Vec3(0.080, 0.150, 0.010),
            new Vec3(0.130, 0.075, 0.030), 0.027).MirrorX();
        var leg = Sdf.Capsule(new Vec3(0.050, 0.045, 0.020),
            new Vec3(0.100, 0.035, 0.105), 0.033).MirrorX();
        var muzzle = Sdf.Sphere(0.030).Move(0, 0.198, 0.058)
            .Paint(belly, 0.0, 0.9);
        var tummy = Sdf.Sphere(0.052).Move(0, 0.090, 0.062)
            .Paint(belly, 0.0, 0.9);
        var eye = Sdf.Sphere(0.010).Move(0.028, 0.238, 0.062).MirrorX()
            .Paint(0x1E2130, 0.0, 0.2);
        return body.Smin(head, 0.02)
            .Smin(ear, 0.012)
            .Smin(arm, 0.015)
            .Smin(leg, 0.015)
            .Paint(fur, 0.0, 0.9)
            .Smin(muzzle, 0.010)
            .Smin(tummy, 0.012)
            .Ssub(eye, 0.004);
    }

    // 爪 1 本 (右用)。肩 (原点) → 肘 → 爪先の「反り 120°」形状。
    // 左は描画・物理とも X 反転 (rotateY(π))
    static SdfNode FingerModel()
    {
        var upper = Sdf.Capsule(new Vec3(0, 0, 0),
            new Vec3(0.050, -0.110, 0), 0.009);
        var lower = Sdf.Capsule(new Vec3(0.050, -0.110, 0),
            new Vec3(-0.085, -0.215, 0), 0.008);
        return upper.Smin(lower, 0.010).Paint(0xC9CED8, 0.9, 0.25);
    }

    // ヘッド: ドーム + リング。原点はリング面の中心
    static SdfNode HeadModel()
    {
        var dome = Sdf.Sphere(0.105)
            .Intersect(Sdf.Box(0.11, 0.055, 0.11).Move(0, 0.055, 0))
            .Paint(0xF2F2F4, 0.1, 0.4);
        var rim = Sdf.Torus(0.095, 0.032).Paint(0xE0405A, 0.2, 0.5);
        return dome.Smin(rim, 0.015);
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
        double density = 50.0; // 密度 50kg/m³ → 約 330g
        double friction = 0.6;
        double restitution = 0.02;
        Phys3d.Sphere(body, "torso", new SphereDesc3d
        {
            Version = ver,
            R = 0.100,
            Offset = new Vec3d { X = 0.0, Y = 0.100, Z = 0.0 },
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Sphere(body, "head", new SphereDesc3d
        {
            Version = ver,
            R = 0.072,
            Offset = new Vec3d { X = 0.0, Y = 0.220, Z = 0.0 },
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Capsule(body, "arm_r", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = 0.080, Y = 0.150, Z = 0.010 },
            B = new Vec3d { X = 0.130, Y = 0.075, Z = 0.030 },
            R = 0.027,
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Capsule(body, "arm_l", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = -0.080, Y = 0.150, Z = 0.010 },
            B = new Vec3d { X = -0.130, Y = 0.075, Z = 0.030 },
            R = 0.027,
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Capsule(body, "leg_r", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = 0.050, Y = 0.045, Z = 0.020 },
            B = new Vec3d { X = 0.100, Y = 0.035, Z = 0.105 },
            R = 0.033,
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
        Phys3d.Capsule(body, "leg_l", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = -0.050, Y = 0.045, Z = 0.020 },
            B = new Vec3d { X = -0.100, Y = 0.035, Z = 0.105 },
            R = 0.033,
            Density = density,
            Friction = friction,
            Restitution = restitution,
        });
    }

    // 爪 1 本の物理 (右用。左は sign = -1 で X 反転)
    static void DeclareFingerShapes(BodyRef3d body, double sign)
    {
        Phys3d.Capsule(body, "upper", new CapsuleDesc3d
        {
            A = new Vec3d { X = 0.0, Y = 0.0, Z = 0.0 },
            B = new Vec3d { X = sign * 0.050, Y = -0.110, Z = 0.0 },
            R = 0.009,
            Density = 2000.0,
            Friction = 0.6,
        });
        Phys3d.Capsule(body, "lower", new CapsuleDesc3d
        {
            A = new Vec3d { X = sign * 0.050, Y = -0.110, Z = 0.0 },
            B = new Vec3d { X = sign * -0.085, Y = -0.215, Z = 0.0 },
            R = 0.008,
            Density = 2000.0,
            Friction = 0.6,
        });
    }

    // 静物: 床 (獲得口の穴あき) + アクリルフェンス + ガラス壁 + シュート筒
    static List<double[]> statics = new List<double[]>
    {
        // x, y, z, hx, hy, hz
        new double[] { 0.0, -0.02, -0.175, fieldHx, 0.02, 0.275 }, // 床 (奥側)
        new double[] { 0.175, -0.02, 0.275, 0.20, 0.02, 0.175 }, // 床 (手前右)
        new double[] { -0.20, 0.07, 0.10, 0.175, 0.07, 0.006 }, // フェンス (穴の奥側)
        new double[] { -0.025, 0.07, 0.275, 0.006, 0.07, 0.175 }, // フェンス (穴の右側)
        new double[] { -fieldHx - 0.006, 0.31, 0.0, 0.006, 0.31, fieldHz }, // ガラス左
        new double[] { fieldHx + 0.006, 0.31, 0.0, 0.006, 0.31, fieldHz }, // ガラス右
        new double[] { 0.0, 0.31, -fieldHz - 0.006, fieldHx, 0.31, 0.006 }, // ガラス奥
        new double[] { 0.0, 0.31, fieldHz + 0.006, fieldHx, 0.31, 0.006 }, // ガラス手前
        new double[] { -0.025, -0.25, 0.275, 0.006, 0.25, 0.175 }, // シュート筒 右
        new double[] { -0.20, -0.25, 0.10, 0.175, 0.25, 0.006 }, // シュート筒 奥
        new double[] { -fieldHx - 0.006, -0.25, 0.275, 0.006, 0.25, 0.175 }, // シュート筒 左
        new double[] { -0.20, -0.25, fieldHz + 0.006, 0.175, 0.25, 0.006 }, // シュート筒 手前
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
                Friction = 0.5,
                Restitution = 0.05,
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
            TimeStep = dt,
        });

        // ヘッド: ワイヤー 1 本吊り (実機の主流はワイヤー巻き取り式)。
        // damping は空力とワイヤー内部摩擦・捩り抵抗による実在の損失の近似
        double headY0 = carriageY - wireMin - headTop;
        var head = Phys3d.Body(world, "head", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            LinearDamping = 0.15,
            AngularDamping = 0.5,
            Initial = new InitialState3d { X = homeX, Y = headY0, Z = homeZ },
        });
        if (head == null) return null;
        Phys3d.Cylinder(head, "solid", new CylinderDesc3d
        {
            Height = 0.08,
            Radius = 0.105,
            YOffset = 0.02,
            Density = 400.0, // ヘッド質量 ≈ 1.1kg
            Friction = 0.3,
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
            Hertz = 0.0,
            DampingRatio = 0.0,
            EnableLimit = true,
            MinLength = 0.02,
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
            MaxVelocityForce = 0.0,
            MaxVelocityTorque = 0.0,
            LinearHertz = 0.0,
            MaxSpringForce = 0.0,
            AngularHertz = 1.2,
            AngularDampingRatio = 1.0,
            MaxSpringTorque = 2.5,
        });

        // 爪 2 本: 肩の revolute joint。モーターのトルク上限がアームパワー。
        // 開閉指示は状態機械から (clawCommand)。angularDamping は関節部の摩擦損失
        var fr = Phys3d.Body(world, "finger:r", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            AngularDamping = 1.0,
            Initial = new InitialState3d
            {
                X = homeX + shoulderX,
                Y = headY0 + shoulderY,
                Z = homeZ,
            },
        });
        if (fr == null) return null;
        DeclareFingerShapes(fr, 1.0);
        var fl = Phys3d.Body(world, "finger:l", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            AngularDamping = 1.0,
            Initial = new InitialState3d
            {
                X = homeX - shoulderX,
                Y = headY0 + shoulderY,
                Z = homeZ,
            },
        });
        if (fl == null) return null;
        DeclareFingerShapes(fl, -1.0);

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
            Axis = new Vec3d { X = 0.0, Y = 0.0, Z = 1.0 },
            EnableLimit = true,
            Lower = 0.0,
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
            Axis = new Vec3d { X = 0.0, Y = 0.0, Z = 1.0 },
            EnableLimit = true,
            Lower = -openAngle,
            Upper = 0.0,
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
            return new ClawCommand { Speed = 1.8, Torque = 0.9 };
        if (state == stGrab || state == stLift)
            return new ClawCommand { Speed = -2.0, Torque = grabTorque }; // 初動 (掴む〜持ち上げ)
        if (state == stCarry)
            return new ClawCommand { Speed = -2.0, Torque = holdTorque }; // 保持 (運搬中に弱まる)
        if (state == stRelease)
            return new ClawCommand { Speed = 1.8, Torque = 0.9 }; // 獲得口で開放
        return new ClawCommand { Speed = -1.5, Torque = 0.5 }; // 待機は閉じ
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
                LinearDamping = 0.05,
                AngularDamping = 0.5, // 布と詰め物の内部損失の近似
                Initial = new InitialState3d
                {
                    X = b.X,
                    Y = b.Y,
                    Z = b.Z,
                    Euler = new Vec3d { X = 0.0, Y = b.Yaw, Z = 0.0 },
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
            if (state == stMoveX) return cx < autoX - 0.005;
            if (state == stMoveZ) return cz > autoZ + 0.005;
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
                cx = Math.Min(cx + moveSpeed * dt, maxX);
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
                cz = Math.Max(cz - moveSpeed * dt, minZ);
            else if (stateT > 5)
                Enter(stDescend);
        }
        else if (state == stDescend)
        {
            // 移動直後の振り子揺れが収まるまで一呼吸置いてから繰り出す
            if (stateT > 15)
                wireLen = Math.Min(wireLen + winchSpeed * dt, wireMax);
            // 着地検出 = ワイヤー張力低下 (実機はテンションセンサー)。
            // 繰り出し量に対して実距離が短い = 弛み。揺れによる瞬間的な
            // 弛みを拾わないよう連続フレームでデバウンスする
            var pose = Phys3d.Pose(head);
            if (pose != null)
            {
                var anchor = new Vec3(pose.X, pose.Y, pose.Z)
                    + new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw)
                        .RotateVec3(new Vec3(0, headTop, 0));
                double dist = new Vec3(cx, carriageY, cz).Distance(anchor);
                slackFrames = (wireLen - dist > 0.03) ? slackFrames + 1 : 0;
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
            wireLen = Math.Max(wireLen - winchSpeed * dt, wireMin);
            if (wireLen <= wireMin)
                Enter(stCarry);
        }
        else if (state == stCarry)
        {
            double dx = homeX - cx;
            double dz = homeZ - cz;
            cx += MathUtil.Clamp(dx, -moveSpeed * dt, moveSpeed * dt);
            cz += MathUtil.Clamp(dz, -moveSpeed * dt, moveSpeed * dt);
            if (Math.Abs(dx) < 0.002 && Math.Abs(dz) < 0.002)
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
            if (pose.Y < -0.32)
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
                    b.X = 0.02 + Mod(b.Gen * 53, 13) * 0.012;
                    b.Y = 0.35;
                    b.Z = -0.15 + Mod(b.Gen * 31, 11) * 0.02;
                    b.Yaw = Mod(b.Gen * 137, 63) * 0.1;
                }
            }
        }
    }

    // --- 描画 --------------------------------------------------------------
    static Mat4 BoxMat(double x, double y, double z, double sx, double sy,
        double sz)
    {
        return Mat4.Translate(new Vec3(x, y, z))
            * Mat4.Scale(new Vec3(sx, sy, sz));
    }

    // 2 点間に渡す細い箱 (ワイヤーとレール用)
    static Mat4 SegmentMat(Vec3 a, Vec3 b, double r)
    {
        var d = b - a;
        double len = d.Length();
        var mid = (a + b) * 0.5;
        var rot = new Mat4();
        if (len > 1e-6)
        {
            var dir = d * (1.0 / len);
            var axis = Vec3.Up().Cross(dir);
            double s = axis.Length();
            if (s > 1e-6)
                rot = Quat.FromAxisAngle(axis * (1.0 / s),
                    Math.Atan2(s, dir.Y)).ToMat4();
            else if (dir.Y < 0)
                rot = Mat4.RotateX(Math.PI);
        }
        return Mat4.Translate(mid) * rot * Mat4.Scale(new Vec3(r, len * 0.5, r));
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
        Phys3d.Step(world, dt);
        UpdatePrizes(live);
        frame++;
    }

    public static void OnFrame(double dt)
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
            Gravity = new Vec3d { X = 0.0, Y = -9.81, Z = 0.0 },
            FixedDt = dt,
            Substeps = 8,
            MaxSteps = 1,
        });
        if (world == null) return;
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => SimulateTick(world));

        // --- draw ---
        // ゲームセンターの薄暗い環境 + 筐体上部からの光
        r.Light.Dir = new Vec3(-0.3, 1.0, 0.45);
        r.Light.Intensity = 1.2;
        r.Sky.Top = Color.Rgb(0.35, 0.36, 0.45);
        r.Sky.Bottom = Color.Rgb(0.12, 0.11, 0.12);
        r.Sky.Intensity = 0.45;
        r.Background = Color.Rgb(0.10, 0.10, 0.13);
        r.Shadow.Center = new Vec3(0, 0.3, 0);
        r.Shadow.Extent = 1.2;
        r.Begin(new Camera
        {
            Eye = new Vec3(0.02, 1.02, 1.95),
            Target = new Vec3(0.0, 0.30, 0.0),
            Fov = 40,
            Near = 0.1,
            Far = 50.0,
        });

        // 筐体 (描画のみ): 本体・上部飾り・柱・レール
        var body = Color.Rgb(0.93, 0.93, 0.95);
        var accent = Color.Rgb(0.88, 0.25, 0.42);
        var dark = Color.Rgb(0.22, 0.23, 0.27);
        var felt = Color.Rgb(0.32, 0.62, 0.46);
        DrawBox(BoxMat(0.0, -0.33, 0.0, 0.42, 0.29, fieldHz + 0.05), body, null);
        DrawBox(BoxMat(0.0, -0.06, 0.0, 0.42, 0.022, fieldHz + 0.05), accent, null);
        DrawBox(BoxMat(0.0, 0.86, 0.0, 0.42, 0.075, fieldHz + 0.05), accent, null);
        foreach (var sx in new List<int> { -1, 1 })
        {
            foreach (var sz in new List<int> { -1, 1 })
            {
                DrawBox(BoxMat(sx * (fieldHx + 0.022), 0.31,
                    sz * (fieldHz + 0.028), 0.016, 0.315, 0.016), body, null);
            }
        }
        // 床 (フェルト) と穴の縁
        DrawBox(BoxMat(0.0, -0.02, -0.175, fieldHx, 0.02, 0.275), felt, null);
        DrawBox(BoxMat(0.175, -0.02, 0.275, 0.20, 0.02, 0.175), felt, null);
        DrawBox(BoxMat(-0.20, -0.05, 0.275, 0.175, 0.05, 0.175), dark, null); // シュート内部
        // 払い出しの褒め演出: 獲得口の縁が光る (HDR 高輝度で bloom に乗せる)
        if (payoutFlash > 0)
        {
            double k = payoutFlash / 60.0;
            DrawBox(BoxMat(-0.20, 0.005, 0.275, 0.178, 0.006 + 0.02 * k, 0.178),
                Color.Rgb(1.6, 1.5, 0.7 + 0.7 * k), null);
        }
        // レール: 固定 2 本 + キャリッジと動く梁
        DrawBox(BoxMat(-0.34, 0.76, 0.0, 0.012, 0.012, fieldHz), dark, null);
        DrawBox(BoxMat(0.34, 0.76, 0.0, 0.012, 0.012, fieldHz), dark, null);
        DrawBox(BoxMat(0.0, 0.76, cz, 0.34, 0.010, 0.010), dark, null);
        DrawBox(BoxMat(cx, 0.775, cz, 0.05, 0.025, 0.05), accent, null);

        // ワイヤー + ヘッド + 爪 (物理の実 pose で描く)
        var headPose = Phys3d.PoseByKey(world, "head");
        if (headPose != null)
        {
            var anchor = new Vec3(headPose.X, headPose.Y, headPose.Z)
                + new Quat(headPose.Qx, headPose.Qy, headPose.Qz, headPose.Qw)
                    .RotateVec3(new Vec3(0, headTop, 0));
            DrawBox(SegmentMat(new Vec3(cx, carriageY, cz), anchor, 0.005),
                dark, null);
            r.Draw(hm, Renderer3d.PoseMat(headPose));
        }
        var frPose = Phys3d.PoseByKey(world, "finger:r");
        if (frPose != null)
            r.Draw(fm, Renderer3d.PoseMat(frPose));
        var flPose = Phys3d.PoseByKey(world, "finger:l");
        if (flPose != null)
            r.Draw(fm, Renderer3d.PoseMat(flPose) * Mat4.RotateY(Math.PI));

        // ぬいぐるみ
        foreach (var i in renderBearIndices)
        {
            var pose = Phys3d.PoseByKey(world, "bear:" + i);
            if (pose != null)
                r.Draw(bm[bears[i].Variant], Renderer3d.PoseMat(pose));
        }

        // ガラスとフェンス (半透明は opaque の後に自動で回る)
        var glass = Color.Rgb(0.75, 0.85, 0.95, 0.12);
        var fence = Color.Rgb(0.85, 0.9, 1.0, 0.25);
        DrawBox(BoxMat(-fieldHx - 0.006, 0.31, 0.0, 0.005, 0.31, fieldHz),
            glass, Gfx.Blend.Alpha);
        DrawBox(BoxMat(fieldHx + 0.006, 0.31, 0.0, 0.005, 0.31, fieldHz),
            glass, Gfx.Blend.Alpha);
        DrawBox(BoxMat(0.0, 0.31, -fieldHz - 0.006, fieldHx, 0.31, 0.005),
            glass, Gfx.Blend.Alpha);
        DrawBox(BoxMat(-0.20, 0.07, 0.10, 0.175, 0.07, 0.005), fence, Gfx.Blend.Alpha);
        DrawBox(BoxMat(-0.025, 0.07, 0.275, 0.005, 0.07, 0.175), fence, Gfx.Blend.Alpha);
        DrawBox(BoxMat(0.0, 0.31, fieldHz + 0.006, fieldHx, 0.31, 0.005),
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
            grabTorque = Ui.SliderFloat("grab power", grabTorque, 0.0, 2.0);
            holdTorque = Ui.SliderFloat("hold power", holdTorque, 0.0, 2.0);
        }
        Ui.EndWindow();
        Ui.Render();
        Gfx.EndPass();
    }
}
