// lub の samples/23_crane_game の entry。
// 実行: lub samples/23_crane_game/CraneGame23.csproj (transpile + watch + hot reload)
//
// 型と tcs 制約:
// - Renderer3d / Mesh3d は static 初期化子でなく onFrame からの build() で
//   遅延生成する (cs-lib クラスは load 順の都合で static 初期化子から呼べない)。
// - 整数剰余 % は Mod() (floor 分解) で代替、switch は if 連鎖。
// - Phys3d の body/world 取得は null ガード。
//
// 3D クレーンゲーム (2 本爪プライズ機)。景品には接触・摩擦・重力だけが作用する。
// 中央のソレノイド、戻しバネ、リンクで開閉する機構を簡略化している。
//
// - ガントリー: キャリッジ (kinematic) が上部レールを X→Z の順に走る。
//   ボタン 1 押下中に右へ、ボタン 2 押下中に奥へ。離すと戻せない。
// - ワイヤー吊り: ヘッドはキャリッジからワイヤー 1 本吊り。
//   distance joint を「バネ力 0 + 上限 limit」でロープ化し、巻き上げ =
//   maxLength の増減。着地でワイヤーが弛む・移動で振り子揺れするのは実機通り。
//   着地検出も実機と同じ「張力低下」(= 弛み) で行う。
// - 爪: 肩の回転関節と 2 本の等長リンクを、中央の可動軸で同時に駆動する。
//   ソレノイドは可動軸とヘッドに等大反対向きの力を加える。無通電ではバネで開く。
//   板状の爪先が景品を下から支える。景品への吸着・固定用の拘束は作らない。
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

public static class CraneGame23
{
    const float tickDt = 1.0f / 60.0f;
    const int physicsSubsteps = 8;

    // --- メートル・秒・kg で定義した機構 (フィールド 750×900mm) ---------
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

    // ソレノイドの吸引力 (N)。保持中も通電し、弱めると荷重で爪が開く。
    static float grabForce = 40.0f;
    static float holdForce = 40.0f;
    const int grabTicks = 100; // 全開から閉じる時間 + 接触が落ち着く時間

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
    static bool demoEnabled = false;
    static bool sideView = false;
    static bool settingsOpen = false;
    static bool controlHeld = false;
    static int pointerControl = 0;
    static int screenW = 960, screenH = 720;
    static float hudScale = 1;
    static SpriteBatch? hud = null;
    static Text? hudFont = null;
    static int autoIndex = 0;
    static float autoX = 0.0f;
    static float autoZ = 0.0f;
    static int slackFrames = 0;
    static FixedStep? step = null;
    static int pendingPresses = 0;
    static List<int> renderBearIndices = new List<int>();
    static Machine? renderMachine = null;

    static List<Bear> bears = new List<Bear>();

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend, Width = 960, Height = 720 });
        // 初期配置: 可動範囲内 (x <= MAX_X) に散らす。座標は固定 (決定論)
        bears = new List<Bear>
        {
            new Bear { Gen = 1, Variant = 0, Respawn = 0, X = 0.08f, Y = 0.02f, Z = -0.05f, Yaw = 0.4f },
            new Bear { Gen = 1, Variant = 1, Respawn = 0, X = -0.14f, Y = 0.02f, Z = -0.26f, Yaw = -0.7f },
            new Bear { Gen = 1, Variant = 2, Respawn = 0, X = 0.15f, Y = 0.02f, Z = 0.18f, Yaw = 2.6f },
            new Bear { Gen = 1, Variant = 0, Respawn = 0, X = -0.14f, Y = 0.02f, Z = -0.02f, Yaw = 1.8f },
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

    // 描画と物理が共有する右爪の寸法。左は両方で X を反転する。
    const float padHx = 0.025f, padHy = 0.004f, padHz = 0.065f;
    const float padX = -0.07f, padY = -0.211f;
    const float leverY = 0.06f;
    static List<float[]> fingerRods = new List<float[]>
    {
        // ax, ay, bx, by, radius
        new float[] { 0, 0, 0.050f, -0.110f, 0.009f },
        new float[] { 0.050f, -0.110f, -0.085f, -0.215f, 0.008f },
        new float[] { 0, 0, 0, leverY, 0.006f },
    };

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
    static bool showLinkage = false;

    public static void OnReload()
    {
        meshDirty = true;
    }
    static Renderer3d? ren = null;
    static List<Mesh3d>? bearMeshes = null;
    static Mesh3d? rodCylinder = null;
    static Mesh3d? rodSphere = null;
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
        rodCylinder = new Mesh3d("cg_rod_cylinder");
        rodSphere = new Mesh3d("cg_rod_sphere");
        headMesh = new Mesh3d("cg_head");
        cubeMesh = new Mesh3d("cg_cube");
    }

    static void Remesh()
    {
        var bm = bearMeshes;
        var cylinder = rodCylinder;
        var sphere = rodSphere;
        var hm = headMesh;
        var cm = cubeMesh;
        if (bm == null || cylinder == null || sphere == null || hm == null || cm == null) return;
        var furs = new List<int> { 0xB07A4A, 0xE8A0B4, 0xF0E5CE };
        var bellies = new List<int> { 0xF2E3C8, 0xF7D9E2, 0xE0CFA8 };
        for (int i = 0; i < 3; i++)
        {
            bm[i].Rebuild(Sdf.Mesh(BearModel(furs[i], bellies[i]), 56));
        }
        cylinder.Rebuild(Shapes3d.Cylinder(32));
        sphere.Rebuild(Shapes3d.Sphere(16, 32));
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
        Phys3d.Box(body, "pad", new BoxDesc3d
        {
            Hx = padHx,
            Hy = padHy,
            Hz = padHz,
            Offset = new Vec3d { X = sign * padX, Y = padY, Z = 0 },
            Density = 2000.0f,
            Friction = 0.9f,
        });
        for (int i = 0; i < fingerRods.Count; i++)
        {
            var rod = fingerRods[i];
            Phys3d.Capsule(body, "rod:" + i, new CapsuleDesc3d
            {
                A = new Vec3d { X = sign * rod[0], Y = rod[1], Z = 0 },
                B = new Vec3d { X = sign * rod[2], Y = rod[3], Z = 0 },
                R = rod[4],
                Density = 2000.0f,
                Friction = 0.6f,
            });
        }
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
            // 巻き取り長や爪モーターの宣言更新だけでは、休止した拘束は起きない。
            // 駆動するヘッドは休止させず、つながる機構の巻き上げを毎 tick 解く。
            Sleep = false,
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

        // 肩の回転関節は受動。開閉力は中央軸からリンクを通して伝わる。
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

        Phys3d.Joint(world, "claw:r", new JointDesc3d
        {
            ConstraintHertz = 120.0f,
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
        });
        Phys3d.Joint(world, "claw:l", new JointDesc3d
        {
            ConstraintHertz = 120.0f,
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
        });
        // One sliding actuator closes both arms through fixed-length links.
        // With no drive force, the return spring and plunger weight open them.
        var plunger = Phys3d.Body(world, "plunger", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            Initial = new InitialState3d { X = homeX, Y = headY0 + 0.02f, Z = homeZ },
        });
        if (plunger == null) return null;
        Phys3d.Box(plunger, "solid", new BoxDesc3d
        {
            Hx = 0.02f,
            Hy = 0.02f,
            Hz = 0.02f,
            Density = 3125.0f, // 可動軸の等価質量 0.20 kg
            Filter = new FilterDesc3d { MaskBits = "0" }, // inside the head housing
        });
        Phys3d.Joint(world, "actuator", new JointDesc3d
        {
            ConstraintHertz = 120.0f,
            Type = Phys3d.JointType.Prismatic,
            BodyA = head,
            BodyB = plunger,
            AnchorA = new Vec3d { X = homeX, Y = headY0 + 0.02f, Z = homeZ },
            AnchorB = new Vec3d { X = homeX, Y = headY0 + 0.02f, Z = homeZ },
            Axis = new Vec3d { X = 0, Y = 1, Z = 0 },
            EnableLimit = true,
            Lower = -0.079f,
            Upper = 0.0f,
            EnableSpring = true,
            Hertz = 4.0f,
            DampingRatio = 1.0f,
            TargetTranslation = -0.079f,
        });
        // The solenoid pulls on its plunger and housing with equal opposite forces.
        // No force or constraint is ever applied directly to a prize.
        var headPose = Phys3d.Pose(head);
        var plungerPose = Phys3d.Pose(plunger);
        if (headPose != null && plungerPose != null)
        {
            var force = new Quat(headPose.Qx, headPose.Qy, headPose.Qz, headPose.Qw)
                .RotateVec3(new Vec3(0, ClawForce(), 0));
            Phys3d.AddForceCenter(plunger, new Vec3d { X = force.X, Y = force.Y, Z = force.Z });
            Phys3d.AddForce(head, new Vec3d { X = -force.X, Y = -force.Y, Z = -force.Z },
                new CommandOpts3d { Point = new Vec3d { X = plungerPose.X, Y = plungerPose.Y, Z = plungerPose.Z } });
        }
        DeclareLink(world, plunger, fr, headY0, 1.0f, "link:r");
        DeclareLink(world, plunger, fl, headY0, -1.0f, "link:l");
        return new Machine(head, fr, fl);
    }

    static void DeclareLink(WorldRef3d world, BodyRef3d plunger, BodyRef3d finger,
        float headY0, float sign, string key)
    {
        Phys3d.Joint(world, key, new JointDesc3d
        {
            ConstraintHertz = 120.0f,
            Type = Phys3d.JointType.Distance,
            BodyA = plunger,
            BodyB = finger,
            AnchorA = new Vec3d { X = homeX, Y = headY0 + 0.02f, Z = homeZ },
            AnchorB = new Vec3d
            {
                X = homeX + sign * shoulderX,
                Y = headY0 + shoulderY + leverY,
                Z = homeZ
            },
            Length = 0.104403f,
        });
    }

    // Coil pull in newtons. With power off, the return spring opens the fingers.
    static float ClawForce()
    {
        if (state == stGrab || state == stLift)
            return grabForce;
        if (state == stCarry)
            return holdForce;
        return 0.0f;
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
        return controlHeld;
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
                if (demoEnabled && idleT > 240)
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
                        // 原点は足元。倒れた景品も胴体の中心を狙う。
                        var aim = new Vec3(target.X, target.Y + 0.10f, target.Z);
                        if (pose != null)
                            aim = new Vec3(pose.X, pose.Y, pose.Z)
                                + new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw)
                                    .RotateVec3(new Vec3(0, 0.10f, 0));
                        autoX = MathUtil.Clamp(aim.X, homeX, maxX);
                        autoZ = MathUtil.Clamp(aim.Z, minZ, homeZ);
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
            if (stateT > grabTicks)
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

    // 円柱 + 両端球の和集合で、物理のカプセルを直接描く。
    // 円周 32 分割・球 16 段の輪郭誤差は最大半径 9 mm に対して 0.1 mm 未満。
    static void DrawRod(Mat4 pose, Vec3 a, Vec3 b, float radius)
    {
        var r = ren;
        var cylinder = rodCylinder;
        var sphere = rodSphere;
        if (r == null || cylinder == null || sphere == null) return;
        var opts = new Draw3dOpts { Tint = Color.Rgb(0.72f, 0.74f, 0.78f) };
        r.Draw(cylinder, pose * SegmentMat(a, b, radius)
            * Mat4.Scale(new Vec3(1, 2, 1)), opts);
        r.Draw(sphere, pose * Mat4.Translate(a)
            * Mat4.Scale(new Vec3(radius, radius, radius)), opts);
        r.Draw(sphere, pose * Mat4.Translate(b)
            * Mat4.Scale(new Vec3(radius, radius, radius)), opts);
    }

    static void DrawFinger(Pose3d bodyPose, float sign)
    {
        var pose = Renderer3d.PoseMat(bodyPose);
        DrawBox(pose * BoxMat(sign * padX, padY, 0, padHx, padHy, padHz),
            Color.Rgb(0.27f, 0.29f, 0.35f), null);
        foreach (var rod in fingerRods)
            DrawRod(pose, new Vec3(sign * rod[0], rod[1], 0),
                new Vec3(sign * rod[2], rod[3], 0), rod[4]);
    }

    // 機構確認用の破線。距離拘束を示す記号であり、金属部品の描画ではない。
    static void DrawLink(Pose3d plunger, Pose3d finger)
    {
        var a = new Vec3(plunger.X, plunger.Y, plunger.Z);
        var b = new Vec3(finger.X, finger.Y, finger.Z)
            + new Quat(finger.Qx, finger.Qy, finger.Qz, finger.Qw)
                .RotateVec3(new Vec3(0, leverY, 0));
        for (int i = 0; i < 6; i++)
            DrawBox(SegmentMat(a + (b - a) * (i / 6.0f),
                a + (b - a) * ((i + 0.5f) / 6.0f), 0.0015f),
                Color.Rgb(2.0f, 1.1f, 0.1f), null);
    }

    static void DrawContacts(BodyRef3d finger)
    {
        foreach (var contact in Phys3d.BodyContacts(finger))
        {
            if (contact.PointCount > 0 && contact.X != null && contact.Y != null && contact.Z != null
                && (contact.Separation ?? 0) <= 0.002f)
                DrawBox(BoxMat(contact.X.Value, contact.Y.Value, contact.Z.Value,
                    0.003f, 0.003f, 0.003f), Color.Rgb(2.0f, 1.1f, 0.1f), null);
        }
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
        renderMachine = machine;
        var live = DeclareBears(world);
        renderBearIndices = new List<int>();
        foreach (var entry in live)
            renderBearIndices.Add(entry.Index);
        UpdateSequence(world, machine.Head, tickPressed);
        Phys3d.Step(world, tickDt);
        UpdatePrizes(live);
        frame++;
    }

    static Rect ControlRect(int button)
    {
        float s = hudScale;
        return new Rect((int)(screenW * 0.5f + (button == 1 ? -248 : 12) * s),
            (int)(screenH - 94 * s), (int)(236 * s), (int)(58 * s));
    }

    static bool Inside(Rect rect, float x, float y)
    {
        return x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H;
    }

    static int ActiveControl()
    {
        if (state == stIdle || state == stMoveX) return 1;
        if (state == stWait2 || state == stMoveZ) return 2;
        return 0;
    }

    static void ReadControls()
    {
        Gfx.Size(out var width, out var height);
        screenW = width;
        screenH = height;
        hudScale = Math.Min(screenW / 960.0f, screenH / 720.0f);
        Input.MousePos(out var mx, out var my);
        bool click = Input.MousePressed() && !Ui.WantCaptureMouse();
        if (Input.KeyPressed("f2")) settingsOpen = !settingsOpen;
        if (Input.KeyPressed("tab") || (click && mx > screenW - 180 * hudScale && my < 54 * hudScale))
            sideView = !sideView;
        if (click && Inside(new Rect((int)(24 * hudScale), (int)(screenH - 94 * hudScale), (int)(128 * hudScale), (int)(58 * hudScale)), mx, my))
        {
            demoEnabled = !demoEnabled;
            if (demoEnabled && state == stIdle) idleT = 241;
        }
        int active = ActiveControl();
        if (!Input.MouseDown()) pointerControl = 0;
        if (click && active != 0 && Inside(ControlRect(active), mx, my) && !autoPlay)
            pointerControl = active;
        controlHeld = !autoPlay && active != 0 && (Input.KeyDown("space")
            || (pointerControl == active && Input.MouseDown() && Inside(ControlRect(active), mx, my)));
        if (!autoPlay && active != 0 && (Input.KeyPressed("space") || (click && pointerControl == active)))
        {
            demoEnabled = false;
            pendingPresses++;
        }
    }

    static void HudLabel(string text, float x, float y, int color, float scale)
    {
        var b = hud;
        var f = hudFont;
        if (b != null && f != null) f.Draw(b, text, x, y, Color.Hex(color), scale * hudScale);
    }

    static void DrawHud()
    {
        const string fontPath = "samples/23_crane_game/data/MPLUS1p-subset.ttf";
        Io.LoadBytes(fontPath, out var bytes, out _, out _, out _);
        if (bytes == null) return;
        hud ??= new SpriteBatch(screenW, screenH, "crane_hud", "crane_hud");
        hudFont ??= new Text("crane_hud_font", fontPath, 24, 512);
        var b = hud;
        b.LogicalW = screenW;
        b.LogicalH = screenH;
        float s = hudScale;
        int active = autoPlay ? 0 : ActiveControl();
        b.Begin();
        b.Rect(0, 0, screenW, 54 * s, Color.Hex(0x14242F, 0.96f));
        b.Rect(0, screenH - 126 * s, screenW, 126 * s, Color.Hex(0xF5F1EB));
        b.Rect(0, screenH - 126 * s, screenW, 3 * s, Color.Hex(0xD8536C));
        b.Rect(screenW - 180 * s, 14 * s, 160 * s, 34 * s, Color.Hex(0x223544));
        b.Rect(24 * s, screenH - 94 * s, 128 * s, 58 * s, Color.Hex(0xE7E3DD));
        for (int i = 1; i <= 2; i++)
        {
            var rect = ControlRect(i);
            int color = active == i ? (controlHeld ? 0xAD354F : 0xD8536C) : 0xDDDAD5;
            b.Rect(rect.X, rect.Y + 5 * s, rect.W, rect.H, Color.Hex(active == i ? 0x9F374D : 0xC3BFBA));
            b.Rect(rect.X, rect.Y + (controlHeld && active == i ? 3 * s : 0), rect.W, rect.H, Color.Hex(color));
        }
        b.Flush();
        b.Begin();
        HudLabel("CLAW CLUB", 26 * s, 37 * s, 0xF5F1EB, 1.0f);
        HudLabel(sideView ? "FRONT VIEW  / TAB" : "SIDE VIEW  / TAB", screenW - 165 * s, 36 * s, 0xE1E9EF, 0.60f);
        string hint = active == 1 ? "Hold 1 to move right. Release to stop."
            : active == 2 ? "Hold 2 to move back. Release to grab."
            : autoPlay ? (demoEnabled ? "DEMO  /  " : "Finishing this play  /  ") + stateNames[state] : "The crane is " + stateNames[state] + ".";
        HudLabel(hint, screenW * 0.5f - 248 * s, screenH - 105 * s, 0x45515A, 0.66f);
        HudLabel(demoEnabled ? "STOP DEMO" : "WATCH DEMO", 36 * s, screenH - 59 * s, 0x5B6267, 0.64f);
        for (int i = 1; i <= 2; i++)
        {
            var rect = ControlRect(i);
            HudLabel(i == 1 ? "1   MOVE RIGHT" : "2   MOVE BACK", rect.X + 22 * s, rect.Y + 37 * s,
                active == i ? 0xFFFFFF : 0x7C8081, 0.83f);
        }
        HudLabel("PRIZES  " + score, screenW - 170 * s, screenH - 68 * s, 0x34464E, 0.78f);
        HudLabel("PLAYS   " + plays, screenW - 170 * s, screenH - 42 * s, 0x81888B, 0.62f);
        HudLabel("Hold SPACE for the lit button", screenW * 0.5f - 128 * s, screenH - 12 * s, 0x81888B, 0.60f);
        HudLabel("F2  Settings", 26 * s, screenH - 12 * s, 0x81888B, 0.55f);
        b.Flush();
    }

    public static void OnFrame(float dt)
    {
        Build();
        if (meshDirty)
            Remesh();
        var r = ren;
        var bm = bearMeshes;
        var cylinder = rodCylinder;
        var sphere = rodSphere;
        var hm = headMesh;
        if (r == null || bm == null || cylinder == null || sphere == null || hm == null)
            return;
        ReadControls();

        var world = Phys3d.World("crane_game", new WorldOpts3d
        {
            Gravity = new Vec3d { X = 0.0f, Y = -9.81f, Z = 0.0f },
            FixedDt = tickDt,
            Substeps = physicsSubsteps,
            MaxSteps = 1,
        });
        if (world == null) return;
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => SimulateTick(world));

        // --- draw ---
        // ゲームセンターの薄暗い環境 + 筐体上部からの光
        r.Light.Dir = new Vec3(-0.3f, 0.85f, 0.65f);
        r.Light.Intensity = 1.8f;
        r.Sky.Top = Color.Rgb(0.82f, 0.88f, 0.94f);
        r.Sky.Bottom = Color.Rgb(0.42f, 0.40f, 0.43f);
        r.Sky.Intensity = 0.8f;
        r.Ssao.Radius = 0.06f;
        r.Ssao.Strength = 0.45f;
        r.Background = Color.Rgb(0.055f, 0.075f, 0.10f);
        r.Shadow.Center = new Vec3(0, 0.3f, 0);
        r.Shadow.Extent = 1.2f;
        var viewPose = Phys3d.PoseByKey(world, "head");
        r.Begin(new Camera
        {
            Eye = showLinkage && viewPose != null
                ? new Vec3(viewPose.X + 0.25f, viewPose.Y + 0.10f, viewPose.Z + 0.85f)
                : sideView ? new Vec3(2.05f, 0.60f, 0.05f) : new Vec3(0.015f, 0.60f, 2.05f),
            Target = showLinkage && viewPose != null
                ? new Vec3(viewPose.X, viewPose.Y - 0.10f, viewPose.Z)
                : new Vec3(0.0f, 0.25f, 0.0f),
            MirrorX = true,
            Fov = Math.Min(80.0f, Math.Max(42.0f, 42.0f * (4.0f / 3.0f) * screenH / screenW)),
            Near = 0.1f,
            Far = 50.0f,
        });

        // 筐体 (描画のみ): 本体・上部飾り・柱・レール
        var body = Color.Rgb(0.93f, 0.92f, 0.89f);
        var accent = Color.Rgb(0.83f, 0.22f, 0.35f);
        var dark = Color.Rgb(0.17f, 0.20f, 0.24f);
        var felt = Color.Rgb(0.78f, 0.84f, 0.83f);
        // ガラスの外側の背面パネル。明るい売り場の背景として奥行きの基準を作る。
        DrawBox(BoxMat(0, 0.39f, -0.48f, 0.38f, 0.40f, 0.01f), Color.Rgb(0.40f, 0.56f, 0.60f), null);
        DrawBox(BoxMat(0, 0.61f, -0.465f, 0.37f, 0.006f, 0.003f), body, null);
        DrawBox(BoxMat(0.0f, -0.33f, 0.0f, 0.42f, 0.29f, fieldHz + 0.05f), body, null);
        DrawBox(BoxMat(0.0f, -0.06f, 0.0f, 0.425f, 0.022f, fieldHz + 0.055f), accent, null);
        DrawBox(BoxMat(0.0f, 0.85f, 0.0f, 0.42f, 0.055f, fieldHz + 0.05f), body, null);
        DrawBox(BoxMat(0.0f, 0.80f, fieldHz + 0.046f, 0.42f, 0.008f, 0.006f), accent, null);
        // 天井照明と操作台。人が向き合う筐体の高さ・前後を見分ける手掛かり。
        DrawBox(BoxMat(0, 0.792f, 0, 0.32f, 0.003f, 0.35f), Color.Rgb(2.1f, 2.0f, 1.85f), null);
        DrawBox(BoxMat(0, -0.105f, 0.49f, 0.435f, 0.035f, 0.13f), body, null);
        DrawBox(BoxMat(0, -0.065f, 0.49f, 0.39f, 0.005f, 0.115f), accent, null);
        // 獲得口の外側の扉。景品の落下経路の手前に枠だけを描く。
        DrawBox(BoxMat(-0.20f, -0.285f, 0.506f, 0.15f, 0.095f, 0.003f), dark, null);
        DrawBox(BoxMat(-0.20f, -0.19f, 0.512f, 0.16f, 0.006f, 0.006f), accent, null);
        foreach (var sx in new List<int> { -1, 1 })
        {
            foreach (var sz in new List<int> { -1, 1 })
            {
                DrawBox(BoxMat(sx * (fieldHx + 0.022f), 0.39f,
                    sz * (fieldHz + 0.028f), 0.009f, 0.40f, 0.009f), body, null);
            }
        }
        foreach (var sx in new List<int> { -1, 1 })
            DrawBox(BoxMat(sx * 0.375f, 0.39f, -0.455f, 0.003f, 0.38f, 0.004f),
                Color.Rgb(1.3f, 1.7f, 1.8f), null);
        // 物理フェンスの上端を細い枠で示し、透明な板の所在を読み取れるようにする。
        DrawBox(BoxMat(-0.20f, 0.14f, 0.10f, 0.175f, 0.002f, 0.006f), body, null);
        DrawBox(BoxMat(-0.025f, 0.14f, 0.275f, 0.006f, 0.002f, 0.175f), body, null);
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
            if (!showLinkage)
                r.Draw(hm, Renderer3d.PoseMat(headPose));
        }
        var frPose = Phys3d.PoseByKey(world, "finger:r");
        if (frPose != null)
            DrawFinger(frPose, 1.0f);
        var flPose = Phys3d.PoseByKey(world, "finger:l");
        if (flPose != null)
            DrawFinger(flPose, -1.0f);
        var plungerPose = Phys3d.PoseByKey(world, "plunger");
        if (showLinkage && plungerPose != null)
        {
            // 非衝突の内部アクチュエーターも、確認表示では橙色の記号にする。
            DrawBox(Renderer3d.PoseMat(plungerPose) * Mat4.Scale(new Vec3(0.01f, 0.01f, 0.01f)),
                Color.Rgb(2.0f, 1.1f, 0.1f), null);
            if (frPose != null) DrawLink(plungerPose, frPose);
            if (flPose != null) DrawLink(plungerPose, flPose);
        }

        // ぬいぐるみ
        foreach (var i in renderBearIndices)
        {
            var pose = Phys3d.PoseByKey(world, "bear:" + i);
            if (pose != null)
                r.Draw(bm[bears[i].Variant], Renderer3d.PoseMat(pose));
        }
        var machineView = renderMachine;
        if (showLinkage && machineView != null)
        {
            DrawContacts(machineView.Fr);
            DrawContacts(machineView.Fl);
        }

        // ガラスとフェンス (半透明は opaque の後に自動で回る)
        if (!showLinkage)
        {
            var glass = Color.Rgb(0.75f, 0.85f, 0.95f, 0.006f);
            var fence = Color.Rgb(0.85f, 0.9f, 1.0f, 0.045f);
            DrawBox(BoxMat(-fieldHx - 0.006f, 0.31f, 0.0f, 0.005f, 0.31f, fieldHz),
                glass, Gfx.Blend.Alpha);
            DrawBox(BoxMat(fieldHx + 0.006f, 0.31f, 0.0f, 0.005f, 0.31f, fieldHz),
                glass, Gfx.Blend.Alpha);
            DrawBox(BoxMat(-0.20f, 0.07f, 0.10f, 0.175f, 0.07f, 0.005f), fence, Gfx.Blend.Alpha);
            DrawBox(BoxMat(-0.025f, 0.07f, 0.275f, 0.005f, 0.07f, 0.175f), fence, Gfx.Blend.Alpha);
            // 正面と背面の無色ガラスは面を塗らず、縁と筐体の支柱で見せる。
        }

        r.End();

        // UI は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        DrawHud();
        if (settingsOpen)
        {
            Ui.SetNextWindow(20, 65, 260, 155);
            if (Ui.BeginWindow("Machine settings [F2]"))
            {
                grabForce = Ui.SliderFloat("grab (N)", grabForce, 0.0f, 80.0f);
                holdForce = Ui.SliderFloat("hold (N)", holdForce, 0.0f, 80.0f);
                showLinkage = Ui.Checkbox("inspect mechanism", showLinkage);
                if (showLinkage) Ui.Text("orange: guides / contacts");
            }
            Ui.EndWindow();
        }
        Ui.Render();
        Gfx.EndPass();
    }
}
