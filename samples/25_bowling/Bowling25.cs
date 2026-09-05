// lub の samples/25_bowling (Haxe 版 Bowling25.hx) の TinyC# 版 entry。
// 実行: lub samples/25_bowling/Bowling25.csproj (transpile + watch + hot reload)
//
// 3D ボウリング。実寸レーンと実重量比のボール/ピンを Phys3d に載せ、
// 補助力なしの物理だけで転がす:
//
// - レーン: ファウルライン→1番ピン 18.29m、幅 1.066m。手前 2/3 はオイルで
//   低摩擦、奥 1/3 はドライ。回転 (フックの軸成分) はオイル上では滑るだけで、
//   ドライゾーンに入ると摩擦で横に効き始める = 実物のフックの原理そのまま
// - ボール: 半径 0.108m / 約 6.3kg。リリース時は転がり不足 (スキッド) +
//   進行軸回りの回転を与える。指穴の回転が見えるのはこのため
// - ピン: 高さ 0.38m / 約 1.5kg (ボールの 1/4)。物理は円柱 + 球 + カプセルの
//   複数 shape 近似で、重心が実物並みに低い
// - 入力: ボタン連打の 4 段階 (位置 → 角度 → フック → パワー)。
//   放置でアトラクトモードが自動投球する
// - スコア: 10 フレームの正式ルール (ストライク/スペア/10 フレーム目 3 投)
//
// gameplay rule (投球・ピン配置・スコアリング) と物理 desc の数値は
// Haxe 版に忠実。typedef Pin は class に、アトラクトの Math.random は
// 決定的な lubx.Rand に置き換える。cs-lib のクラス (Mesh3d / Renderer3d /
// Rand 等) は生成 Lua でサンプルより後に定義されるため static 初期化子で
// 参照できず、onFrame / 使用箇所で遅延生成する。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>ピン 1 本 (Haxe 版 typedef Pin と対)。</summary>
public class Pin
{
    public int Gen; // version (ラック再設置で上げる)
    public bool Standing; // ラック上に残っている (倒れた分はスイープ済み)
    public double X; // 定位置 (スポット)
    public double Z;
}

public static class Bowling25
{
    const int w = 960;
    const int h = 540;
    const double dt = 1.0 / 60.0;

    // --- 実寸 (m) ----------------------------------------------------------
    const double laneHw = 0.533; // レーン半幅 (41.5in)
    const double gutterW = 0.235; // ガター幅
    const double pinZ = 18.29; // ファウルライン→1番ピン (60ft)
    const double pinDx = 0.3048; // 隣接ピン間隔 (12in)
    const double rowDz = 0.2639; // 列間 (12in × sin60°)
    const double deckEnd = 19.96; // ピンデッキ末端。ここからピット
    const double pitEnd = 21.0; // ピット奥 (クッション)
    const double oilEnd = 12.2; // オイルパターン終端 (40ft 相当)
    const double ballR = 0.108; // ボール半径 (8.5in 径)

    // 材質。摩擦の合成は sqrt(fA×fB) なので、ボール 0.2 に対して実効摩擦は
    // オイル上 ≈ 0.04、ドライ上 ≈ 0.17 と実物のレンジに合わせている
    const double fricOil = 0.008;
    const double fricDry = 0.15;
    const double ballDensity = 1190.0; // 約 6.3kg (14lb 球)
    const double pinDensity = 620.0; // 約 1.53kg
    const double skid = 0.5; // リリース時の転がり率 (1=完全転がり)

    // --- 状態機械 -----------------------------------------------------------
    const int stAim = 0; // 立ち位置 (マーカーが往復)
    const int stAngle = 1; // 投球角度
    const int stHook = 2; // フック回転量
    const int stPower = 3; // パワー → 投球
    const int stRoll = 4; // ボールが転がっている
    const int stSettle = 5; // ピンが静止するのを待つ
    const int stScore = 6; // 判定表示 (スイープ済み)
    const int stEnd = 7; // ゲーム終了

    static int state = stAim;
    static int stateT = 0;
    static double tAccum = 0.0;
    static FixedStep? step = null;
    static int pendingPresses = 0;

    // 投球パラメータ (各段階でロック)
    static double aimX = 0.0;
    static double angle = 0.0; // rad。+ で右へ
    static double hook = 0.0; // rad/s。+ で左に曲がる
    static double power = 0.0; // 0..1
    static int throwGen = 0;
    static bool ballLive = false;
    static double throwX = 0.0;
    static double ballVX = 0.0;
    static double ballVZ = 0.0;
    static double ballWX = 0.0;
    static double ballWZ = 0.0;
    static int stallFrames = 0;
    static bool inGutter = false;
    static int standingBefore = 10;

    // アトラクトモード (放置で自動投球。ヘッドレス検証兼デモ)
    static bool autoPlay = false;
    static int idleT = 0;
    static double autoAimX = 0.0;
    static double autoAngle = 0.0;
    static double autoHook = 0.0;
    static double autoPower = 0.85;
    static Rand? rng = null;

    // スコア (10 フレーム正式ルール)
    static List<Pin> pins = new List<Pin>();
    static List<List<int>> fRolls = new List<List<int>>();
    static int fi = 0; // 現在フレーム (0..9)
    static bool rerackPending = false;
    static bool gameOverPending = false;

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

    // ピン配置: 1番ピンを頂点に 4 列の三角形
    static void EnsurePins()
    {
        if (pins.Count > 0) return;
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < r + 1; c++)
            {
                pins.Add(new Pin
                {
                    Gen = 1,
                    Standing = true,
                    X = (c - r * 0.5) * pinDx,
                    Z = pinZ + r * rowDz,
                });
            }
        }
    }

    static void Rerack()
    {
        foreach (var p in pins)
        {
            p.Gen++;
            p.Standing = true;
        }
    }

    // --- SDF モデル ----------------------------------------------------------
    // ピン: 物理 shape (declarePinShapes) と寸法を揃えた回転体近似
    static SdfNode PinModel()
    {
        var white = 0xF2EFE6;
        var baseShape = Sdf.Capsule(new Vec3(0, 0.030, 0), new Vec3(0, 0.090, 0),
            0.051);
        var belly = Sdf.Sphere(0.0605).Move(0, 0.155, 0);
        var neck = Sdf.Capsule(new Vec3(0, 0.20, 0), new Vec3(0, 0.30, 0),
            0.032);
        var head = Sdf.Sphere(0.040).Move(0, 0.335, 0);
        var body = baseShape.Smin(belly, 0.03).Smin(neck, 0.035).Smin(head, 0.02)
            .Paint(white, 0.0, 0.35);
        var stripe1 = Sdf.Torus(0.034, 0.006).Move(0, 0.265, 0)
            .Paint(0xC2263D, 0.0, 0.4);
        var stripe2 = Sdf.Torus(0.035, 0.006).Move(0, 0.298, 0)
            .Paint(0xC2263D, 0.0, 0.4);
        return body.Smin(stripe1, 0.006).Smin(stripe2, 0.006);
    }

    // ボール: 指穴 3 つ + 飾りリング (回転が見えるように)
    static SdfNode BallModel()
    {
        var body = Sdf.Sphere(ballR).Paint(0x2B55A8, 0.15, 0.25);
        var ring = Sdf.Torus(ballR, 0.0035)
            .Rotate(new Vec3(1, 0, 0.35).Normalize(), 1.0)
            .Paint(0xD9A441, 0.3, 0.3);
        var withRing = body.Smin(ring, 0.002);
        var h1 = Sdf.Sphere(0.015).Move(0.024, 0.098, 0.027);
        var h2 = Sdf.Sphere(0.015).Move(-0.024, 0.098, 0.027);
        var h3 = Sdf.Sphere(0.018).Move(0.0, 0.102, -0.020);
        return withRing.Ssub(h1, 0.002).Ssub(h2, 0.002).Ssub(h3, 0.002);
    }

    // native watch は chunk 再実行で初期値 true に戻り、web (module mode) は
    // onReload で立てる。どちらも再メッシュのトリガ。
    static bool meshDirty = true;

    public static void OnReload()
    {
        meshDirty = true;
    }
    static Mesh3d? pinMesh = null;
    static Mesh3d? ballMesh = null;
    static Mesh3d? cubeMesh = null;

    // --- 物理宣言 ------------------------------------------------------------
    // 静物: x, y, z, hx, hy, hz, friction, restitution
    static List<double[]> statics = new List<double[]>
    {
        // アプローチ
        new double[]
            { 0, -0.06, -1.25, laneHw + gutterW + 0.12, 0.06, 1.25, 0.3, 0.1 },
        // レーン (オイル)
        new double[]
            { 0, -0.06, oilEnd * 0.5, laneHw, 0.06, oilEnd * 0.5, fricOil, 0.08 },
        // レーン (ドライ) + ピンデッキ
        new double[]
        {
            0, -0.06, (oilEnd + deckEnd) * 0.5, laneHw, 0.06,
            (deckEnd - oilEnd) * 0.5, fricDry, 0.08,
        },
        // ガター左
        new double[]
        {
            -(laneHw + gutterW * 0.5), -0.104, deckEnd * 0.5, gutterW * 0.5,
            0.05, deckEnd * 0.5, 0.3, 0.1,
        },
        // ガター右
        new double[]
        {
            laneHw + gutterW * 0.5, -0.104, deckEnd * 0.5, gutterW * 0.5,
            0.05, deckEnd * 0.5, 0.3, 0.1,
        },
        // 側壁左
        new double[]
        {
            -(laneHw + gutterW + 0.03), 0.08, pitEnd * 0.5, 0.03, 0.22,
            pitEnd * 0.5, 0.2, 0.3,
        },
        // 側壁右
        new double[]
        {
            laneHw + gutterW + 0.03, 0.08, pitEnd * 0.5, 0.03, 0.22,
            pitEnd * 0.5, 0.2, 0.3,
        },
        // ピット床
        new double[]
        {
            0, -0.58, (deckEnd + pitEnd) * 0.5, laneHw + gutterW + 0.06,
            0.05, (pitEnd - deckEnd) * 0.5 + 0.2, 0.9, 0.02,
        },
        // ピットクッション
        new double[]
        {
            0, -0.15, pitEnd + 0.05, laneHw + gutterW + 0.06, 0.45, 0.05,
            0.6, 0.05,
        },
        // マスキング (跳ねたピンが当たる)
        new double[]
            { 0, 0.95, 19.3, laneHw + gutterW + 0.06, 0.35, 1.0, 0.3, 0.1 },
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
                Friction = s[6],
                Restitution = s[7],
            });
        }
    }

    // ピンの物理: 円柱の台座 + 腹の球 + 首カプセル + 頭球。均一密度でも
    // 台座が太いぶん重心は実物並み (床から約 0.16m) に落ちる
    static void DeclarePinShapes(BodyRef3d body, int ver)
    {
        var f = 0.35;
        var rest = 0.3;
        Phys3d.Cylinder(body, "base", new CylinderDesc3d
        {
            Version = ver,
            Height = 0.10,
            Radius = 0.051,
            YOffset = 0.0,
            Density = pinDensity,
            Friction = f,
            Restitution = rest,
        });
        Phys3d.Sphere(body, "belly", new SphereDesc3d
        {
            Version = ver,
            R = 0.0605,
            Offset = new Vec3d { X = 0.0, Y = 0.155, Z = 0.0 },
            Density = pinDensity,
            Friction = f,
            Restitution = rest,
        });
        Phys3d.Capsule(body, "neck", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = 0.0, Y = 0.21, Z = 0.0 },
            B = new Vec3d { X = 0.0, Y = 0.31, Z = 0.0 },
            R = 0.032,
            Density = pinDensity,
            Friction = f,
            Restitution = rest,
        });
        Phys3d.Sphere(body, "head", new SphereDesc3d
        {
            Version = ver,
            R = 0.040,
            Offset = new Vec3d { X = 0.0, Y = 0.335, Z = 0.0 },
            Density = pinDensity,
            Friction = f,
            Restitution = rest,
        });
    }

    static void DeclarePins(WorldRef3d world)
    {
        for (int i = 0; i < pins.Count; i++)
        {
            var p = pins[i];
            if (!p.Standing) continue;
            var body = Phys3d.Body(world, "pin:" + i, new BodyDesc3d
            {
                Type = Phys3d.BodyType.Dynamic,
                Version = p.Gen,
                LinearDamping = 0.02,
                AngularDamping = 0.05,
                Initial = new InitialState3d { X = p.X, Y = 0.001, Z = p.Z },
            });
            if (body == null) continue;
            DeclarePinShapes(body, p.Gen);
        }
    }

    static void DeclareBall(WorldRef3d world)
    {
        if (!ballLive) return;
        var body = Phys3d.Body(world, "ball", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            Version = throwGen,
            Bullet = true,
            AngularDamping = 0.02,
            Initial = new InitialState3d
            {
                X = throwX,
                Y = ballR + 0.001,
                Z = 0.0,
                Vx = ballVX,
                Vz = ballVZ,
                Wx = ballWX,
                Wz = ballWZ,
            },
        });
        if (body == null) return;
        Phys3d.Sphere(body, "solid", new SphereDesc3d
        {
            Version = throwGen,
            R = ballR,
            Density = ballDensity,
            Friction = 0.2,
            Restitution = 0.03,
        });
    }

    // --- 投球 ----------------------------------------------------------------
    static void ThrowBall()
    {
        throwGen++;
        ballLive = true;
        throwX = aimX;
        var spd = 5.6 + 3.9 * power;
        var dx = Math.Sin(angle);
        var dz = Math.Cos(angle);
        ballVX = dx * spd;
        ballVZ = dz * spd;
        // 転がり不足 (スキッド) + フック軸回転。フックはオイル上ではほぼ
        // 横滑りのままで、ドライゾーンの摩擦で初めて曲がりに変わる
        var roll = skid * spd / ballR;
        ballWX = roll * dz + hook * dx;
        ballWZ = -roll * dx + hook * dz;
        standingBefore = CountStanding();
        stallFrames = 0;
        inGutter = false;
        Enter(stRoll);
    }

    static int CountStanding()
    {
        var n = 0;
        foreach (var p in pins)
        {
            if (p.Standing) n++;
        }
        return n;
    }

    static void UpdateRoll(WorldRef3d world)
    {
        var pose = Phys3d.Pose(world, "ball");
        var done = false;
        if (pose == null)
        {
            done = stateT > 10;
        }
        else
        {
            if (Math.Abs(pose.X) > laneHw + 0.02 && pose.Y < 0.09)
                inGutter = true;
            var sp = Math.Sqrt(pose.Vx * pose.Vx + pose.Vz * pose.Vz);
            if (pose.Y < -0.25) // ピットに落ちた
            {
                done = true;
            }
            else if (sp < 0.12 && pose.Z < deckEnd - 0.6)
            {
                stallFrames++; // レーン上で失速 (デッドボール)
                if (stallFrames > 50) done = true;
            }
            else
            {
                stallFrames = 0;
            }
        }
        if (stateT > 780) done = true;
        if (done) Enter(stSettle);
    }

    static void UpdateSettle(WorldRef3d world)
    {
        var maxSp = 0.0;
        for (int i = 0; i < pins.Count; i++)
        {
            if (!pins[i].Standing) continue;
            var pose = Phys3d.Pose(world, "pin:" + i);
            if (pose == null) continue;
            var sp = Math.Sqrt(pose.Vx * pose.Vx + pose.Vy * pose.Vy
                + pose.Vz * pose.Vz);
            if (sp > maxSp) maxSp = sp;
        }
        if ((stateT > 45 && maxSp < 0.08) || stateT > 300)
            CountAndScore(world);
    }

    // 倒れた判定 → スイープ → 記録。ピンは傾き (up ベクトル) と高さで判定し、
    // 滑って立ったままのピンは実機同様その場に残す (オフスポット)
    static void CountAndScore(WorldRef3d world)
    {
        var stand = 0;
        var knocked = 0;
        for (int i = 0; i < pins.Count; i++)
        {
            var p = pins[i];
            if (!p.Standing) continue;
            var pose = Phys3d.Pose(world, "pin:" + i);
            var upY = pose != null
                ? 1.0 - 2.0 * (pose.Qx * pose.Qx + pose.Qz * pose.Qz)
                : -1.0;
            if (pose == null || upY < 0.72 || pose.Y < -0.05 || pose.Y > 0.15)
            {
                p.Standing = false;
                knocked++;
            }
            else
            {
                stand++;
            }
        }
        if (fRolls.Count <= fi) fRolls.Add(new List<int>());
        var fr = fRolls[fi];
        fr.Add(knocked);

        if (knocked == 10 && standingBefore == 10)
            ShowEvent("STRIKE!", Color.Rgb(1.0, 0.85, 0.3));
        else if (stand == 0 && knocked > 0)
            ShowEvent("SPARE!", Color.Rgb(0.5, 0.9, 1.0));
        else if (knocked == 0)
            ShowEvent(inGutter ? "GUTTER" : "NO PINS",
                Color.Rgb(0.7, 0.72, 0.78));
        else
            ShowEvent(knocked + " PINS", Color.Rgb(0.95, 0.93, 0.85));

        rerackPending = false;
        gameOverPending = false;
        if (fi < 9)
        {
            if (fr[0] == 10 || fr.Count >= 2)
            {
                fi++;
                rerackPending = true;
            }
        }
        else
        {
            // 10 フレーム目: ストライク/スペアで最大 3 投。全倒で再設置
            var canThird = fr.Count >= 2 && (fr[0] == 10 || fr[0] + fr[1] == 10);
            if (fr.Count >= 3 || (fr.Count == 2 && !canThird))
                gameOverPending = true;
            else if (stand == 0)
                rerackPending = true;
        }
        ballLive = false;
        Enter(stScore);
    }

    // --- スコア計算 (正式ルール) ----------------------------------------------
    static List<int> FlatRolls()
    {
        var a = new List<int>();
        foreach (var f in fRolls)
        {
            foreach (var r in f)
            {
                a.Add(r);
            }
        }
        return a;
    }

    static int At(List<int> a, int i)
    {
        return i < a.Count ? a[i] : 0;
    }

    static int TotalScore()
    {
        var flat = FlatRolls();
        var score = 0;
        var i = 0;
        for (int f = 0; f < 10; f++)
        {
            if (i >= flat.Count) break;
            if (f == 9)
            {
                // 10 フレーム目はボーナス投球込みの単純加算
                for (int k = i; k < flat.Count; k++)
                    score += flat[k];
                break;
            }
            if (flat[i] == 10)
            {
                score += 10 + At(flat, i + 1) + At(flat, i + 2);
                i += 1;
            }
            else if (i + 1 < flat.Count && flat[i] + flat[i + 1] == 10)
            {
                score += 10 + At(flat, i + 2);
                i += 2;
            }
            else
            {
                score += flat[i] + At(flat, i + 1);
                i += 2;
            }
        }
        return score;
    }

    static string RollChar(int n)
    {
        return n == 0 ? "-" : "" + n;
    }

    static string MarkStr(int f)
    {
        if (f >= fRolls.Count) return "";
        var r = fRolls[f];
        if (f < 9)
        {
            if (r.Count >= 1 && r[0] == 10) return "X";
            var s = r.Count >= 1 ? RollChar(r[0]) : "";
            if (r.Count >= 2)
                s += r[0] + r[1] == 10 ? "/" : RollChar(r[1]);
            return s;
        }
        // 10 フレーム目: ラックが満杯だった投球の 10 は X、残りを取れば /
        var s10 = "";
        var rackFull = true;
        var prev = 0;
        foreach (var n in r)
        {
            if (rackFull)
            {
                if (n == 10)
                {
                    s10 += "X";
                }
                else
                {
                    s10 += RollChar(n);
                    rackFull = false;
                    prev = n;
                }
            }
            else
            {
                s10 += prev + n == 10 ? "/" : RollChar(n);
                rackFull = true;
            }
        }
        return s10;
    }

    // --- 状態機械 --------------------------------------------------------------
    static void Enter(int s)
    {
        state = s;
        stateT = 0;
    }

    static bool ButtonPressed()
    {
        var real = pendingPresses > 0;
        if (real)
        {
            pendingPresses = pendingPresses - 1;
            idleT = 0;
            if (autoPlay)
            {
                autoPlay = false; // 手動に引き継ぎ (この押下は消費)
                return false;
            }
        }
        return real;
    }

    static void SimulateTick(WorldRef3d world)
    {
        tAccum += dt;
        eventT += dt;
        Phys3d.Begin(world);
        UpdateSequence(world);
        DeclareStatics(world);
        DeclarePins(world);
        DeclareBall(world);
        Phys3d.Step(world, dt);
        UpdateCamera(world);
    }

    static void StartAuto()
    {
        // Haxe 版の Math.random() は決定的な Rand に置き換える
        var r = rng;
        if (r == null)
        {
            r = new Rand(0x25B0713);
            rng = r;
        }
        autoPlay = true;
        idleT = 0;
        // 外に出してポケット (±0.075) へ曲げ戻すライン。乱数で毎回散らす
        var side = r.NextFloat() < 0.5 ? 1.0 : -1.0;
        autoAimX = side * (0.12 + r.NextFloat() * 0.06);
        autoHook = side * (18.0 + r.NextFloat() * 4.0);
        autoPower = 0.82 + r.NextFloat() * 0.08;
        // バックエンドの曲がり量 (ヘッドレス実測: 約 0.40m @ 球速 8.9m/s、
        // 遅いほど増える) から狙い角を逆算し、人間らしい誤差を足す
        var spd = 5.6 + 3.9 * autoPower;
        var drift = 0.40 + (8.9 - spd) * 0.3;
        autoAngle = (side * (0.075 + drift) - autoAimX) / 18.3
            + side * (r.NextFloat() * 0.006 - 0.003);
    }

    static bool AutoNear(double v, double target, double eps)
    {
        return autoPlay && Math.Abs(v - target) < eps;
    }

    static void UpdateSequence(WorldRef3d world)
    {
        stateT++;
        var pressed = ButtonPressed();
        if (state == stAim)
        {
            if (autoPlay && stateT == 1)
                StartAuto(); // 投球ごとにラインを再抽選
            aimX = 0.42 * Math.Sin(stateT * 0.030);
            if (AutoNear(aimX, autoAimX, 0.02)) pressed = true;
            if (pressed)
            {
                angle = 0.0;
                hook = 0.0;
                Enter(stAngle);
            }
            else if (!autoPlay)
            {
                idleT++;
                if (idleT > 240) StartAuto();
            }
        }
        else if (state == stAngle)
        {
            angle = 0.10 * Math.Sin(stateT * 0.045);
            if (AutoNear(angle, autoAngle, 0.006)) pressed = true;
            if (pressed && stateT > 8) Enter(stHook);
        }
        else if (state == stHook)
        {
            hook = 38.0 * Math.Sin(stateT * 0.05);
            if (AutoNear(hook, autoHook, 2.5)) pressed = true;
            if (pressed && stateT > 8) Enter(stPower);
        }
        else if (state == stPower)
        {
            power = 0.5 - 0.5 * Math.Cos(stateT * 0.055);
            if (AutoNear(power, autoPower, 0.04)) pressed = true;
            if (pressed && stateT > 8) ThrowBall();
        }
        else if (state == stRoll)
        {
            UpdateRoll(world);
        }
        else if (state == stSettle)
        {
            UpdateSettle(world);
        }
        else if (state == stScore)
        {
            if (stateT > 90)
            {
                if (gameOverPending)
                {
                    gameOverPending = false;
                    Enter(stEnd);
                }
                else
                {
                    if (rerackPending) Rerack();
                    Enter(stAim);
                }
            }
        }
        else if (state == stEnd)
        {
            if (stateT > 360)
            {
                fRolls = new List<List<int>>();
                fi = 0;
                Rerack();
                Enter(stAim);
            }
        }
    }

    // --- カメラ ------------------------------------------------------------------
    static Vec3? camEye = null; // 初期値は updateCamera が補う (遅延生成)
    static Vec3? camTgt = null;
    static double camFov = 38.0;

    static void UpdateCamera(WorldRef3d world)
    {
        var eye = camEye ?? new Vec3(0, 0.62, -2.4);
        var tgt = camTgt ?? new Vec3(0, 0.28, 6.0);
        var de = new Vec3(aimX * 0.55, 0.62, -2.4);
        var dtg = new Vec3(aimX * 0.25, 0.28, 6.0);
        var dfov = 38.0;
        if (state == stRoll)
        {
            var pose = Phys3d.Pose(world, "ball");
            if (pose != null && pose.Z < 14.0)
            {
                de = new Vec3(pose.X * 0.45, 1.0, pose.Z - 3.2);
                dtg = new Vec3(pose.X * 0.8, 0.12, pose.Z + 4.5);
                dfov = 42.0;
            }
            else
            {
                de = new Vec3(-1.05, 0.85, 15.2);
                dtg = new Vec3(0.05, 0.25, pinZ + 0.3);
                dfov = 30.0;
            }
        }
        else if (state == stSettle || state == stScore)
        {
            de = new Vec3(-1.05, 0.8, 15.6);
            dtg = new Vec3(0.0, 0.22, pinZ + 0.3);
            dfov = 28.0;
        }
        else if (state == stEnd)
        {
            var a = tAccum * 0.25;
            de = new Vec3(Math.Sin(a) * 2.8, 1.5,
                pinZ - 1.2 + Math.Cos(a) * 2.8);
            dtg = new Vec3(0, 0.2, pinZ);
            dfov = 45.0;
        }
        var k = Math.Min(1.0, 5.0 * dt);
        camEye = eye.Lerp(de, k);
        camTgt = tgt.Lerp(dtg, k);
        camFov = MathUtil.Lerp(camFov, dfov, k);
    }

    // --- 描画 --------------------------------------------------------------------
    static Renderer3d? ren = null;

    static Mat4 BoxMat(double x, double y, double z, double sx, double sy,
        double sz)
    {
        return Mat4.Translate(new Vec3(x, y, z))
            * Mat4.Scale(new Vec3(sx, sy, sz));
    }

    static Mat4 BoxMatR(double x, double y, double z, double ry, double sx,
        double sy, double sz)
    {
        return Mat4.Translate(new Vec3(x, y, z)) * Mat4.RotateY(ry)
            * Mat4.Scale(new Vec3(sx, sy, sz));
    }

    static void DrawBox(Mat4 model, Color color, Gfx.Blend? blend)
    {
        var r = ren;
        var cube = cubeMesh;
        if (r == null || cube == null) return;
        r.Draw(cube, model, new Draw3dOpts { Tint = color, Blend = blend });
    }

    // 静的な舞台 (物理 STATICS と目視で寸法を揃える)
    static void DrawStage()
    {
        var wood = Color.Rgb(0.76, 0.60, 0.40);
        var woodOil = Color.Rgb(0.70, 0.57, 0.41);
        var dark = Color.Rgb(0.16, 0.17, 0.19);
        var accentRed = Color.Rgb(0.52, 0.15, 0.20);
        var mark = Color.Rgb(0.35, 0.20, 0.12);

        // 周辺の床 (見た目のみ)
        DrawBox(BoxMat(0, -0.7, 9.0, 6.0, 0.05, 14.0),
            Color.Rgb(0.10, 0.10, 0.13), null);
        // アプローチ
        DrawBox(BoxMat(0, -0.06, -1.25, laneHw + gutterW + 0.12, 0.06, 1.25),
            Color.Rgb(0.62, 0.51, 0.36), null);
        // レーン (オイル / ドライ)
        DrawBox(BoxMat(0, -0.06, oilEnd * 0.5, laneHw, 0.06, oilEnd * 0.5),
            woodOil, null);
        DrawBox(BoxMat(0, -0.06, (oilEnd + deckEnd) * 0.5, laneHw, 0.06,
            (deckEnd - oilEnd) * 0.5), wood, null);
        // ガター
        DrawBox(BoxMat(-(laneHw + gutterW * 0.5), -0.104, deckEnd * 0.5,
            gutterW * 0.5, 0.05, deckEnd * 0.5), dark, null);
        DrawBox(BoxMat(laneHw + gutterW * 0.5, -0.104, deckEnd * 0.5,
            gutterW * 0.5, 0.05, deckEnd * 0.5), dark, null);
        // 側壁
        DrawBox(BoxMat(-(laneHw + gutterW + 0.03), 0.08, pitEnd * 0.5, 0.03,
            0.22, pitEnd * 0.5), Color.Rgb(0.30, 0.31, 0.36), null);
        DrawBox(BoxMat(laneHw + gutterW + 0.03, 0.08, pitEnd * 0.5, 0.03,
            0.22, pitEnd * 0.5), Color.Rgb(0.30, 0.31, 0.36), null);
        // ピット (奥の暗がり) とマスキング
        DrawBox(BoxMat(0, -0.58, (deckEnd + pitEnd) * 0.5,
            laneHw + gutterW + 0.06, 0.05, (pitEnd - deckEnd) * 0.5 + 0.2),
            Color.Rgb(0.05, 0.05, 0.07), null);
        DrawBox(BoxMat(0, -0.15, pitEnd + 0.05, laneHw + gutterW + 0.06,
            0.45, 0.05), Color.Rgb(0.08, 0.08, 0.10), null);
        DrawBox(BoxMat(0, 0.95, 19.3, laneHw + gutterW + 0.06, 0.35, 1.0),
            accentRed, null);
        // ファウルライン
        DrawBox(BoxMat(0, 0.001, 0, laneHw, 0.0015, 0.012),
            Color.Rgb(0.15, 0.15, 0.17), null);
        // ガイド: ドット (2.13m) とアロー (V 字に並ぶひし形)
        for (int i = 0; i < 7; i++)
        {
            var x = (i - 3) * 0.1365;
            DrawBox(BoxMatR(x, 0.001, 2.13, Math.PI / 4, 0.014, 0.0015, 0.014),
                mark, null);
            DrawBox(BoxMatR(x, 0.001, 4.88 - Math.Abs(i - 3.0) * 0.406,
                Math.PI / 4, 0.026, 0.0015, 0.026), mark, null);
        }
    }

    // 投球ガイド (目安の点線。物理予測ではなく初速と曲がりの傾向を図示)
    static void DrawGuide()
    {
        if (state != stAngle && state != stHook && state != stPower)
            return;
        var n = state == stPower ? 5 + (int)Math.Floor(power * 8.0) : 12;
        for (int k = 0; k < n; k++)
        {
            var d = 1.0 + k * 1.15;
            var x = aimX + Math.Sin(angle) * d
                - hook * 1.3e-4 * Math.Pow(Math.Max(0.0, d - 6.0), 2.0);
            if (Math.Abs(x) > laneHw) break;
            DrawBox(BoxMat(x, 0.004, d, 0.016, 0.002, 0.028),
                Color.Rgb(1.0, 1.0, 1.0, 0.4), Gfx.Blend.Alpha);
        }
        // パワーメーター (レーン右脇の柱)
        if (state == stPower)
        {
            DrawBox(BoxMat(0.95, 0.30, -0.2, 0.035, 0.28, 0.035),
                Color.Rgb(0.12, 0.12, 0.15), null);
            var h = 0.26 * power;
            DrawBox(BoxMat(0.95, 0.02 + h, -0.2, 0.026, h, 0.026),
                Color.Rgb(0.9, 0.25 + 0.5 * (1 - power), 0.15), null);
        }
    }

    // --- HUD ------------------------------------------------------------------
    static string? ttf = null;
    static int fontVersion = 0;
    static MeshText? mtext = null;
    static string eventText = "";
    static double eventT = 99.0;
    static Color? eventCol = null;

    static void ShowEvent(string s, Color c)
    {
        eventText = s;
        eventT = 0.0;
        eventCol = c;
    }

    static bool EnsureText()
    {
        Io.LoadText("samples/25_bowling/data/MPLUS1p-subset.ttf",
            out var text, out var version, out _, out _);
        if (text == null) return false;
        if (ttf == null || fontVersion != version)
        {
            ttf = text;
            fontVersion = version;
            mtext = new MeshText("bw25_text", text, version, w, h);
        }
        return mtext != null;
    }

    static void DrawHud()
    {
        if (!EnsureText()) return;
        var mt = mtext;
        if (mt == null) return;
        var cream = Color.Rgb(0.96, 0.95, 0.9);
        var gray = Color.Rgb(0.55, 0.57, 0.62);
        var gold = Color.Rgb(1.0, 0.85, 0.3);
        // スコアボード: 10 フレームのマーク列 + 合計
        var colW = 56.0;
        var x0 = w * 0.5 - 4.5 * colW;
        for (int f = 0; f < 10; f++)
        {
            var cx = x0 + f * colW;
            var cur = f == fi && state != stEnd;
            mt.TextCentered("" + (f + 1), cx, 24, 11, cur ? gold : gray);
            mt.TextCentered(MarkStr(f), cx, 46, 20, cream);
        }
        mt.TextCentered("SCORE " + TotalScore(), w * 0.5, 76, 16, cream);
        // イベント (出現時にスケールが弾む)
        var ec = eventCol;
        if (eventText != "" && eventT < 1.6 && ec != null)
        {
            var pop = 1.0 + 0.5 * Math.Exp(-eventT * 9.0);
            var a = eventT > 1.25 ? 1.0 - (eventT - 1.25) / 0.35 : 1.0;
            mt.TextCentered(eventText, w * 0.5, 205, 48 * pop,
                Color.Rgb(ec.R, ec.G, ec.B, a));
        }
        // ゲーム終了
        if (state == stEnd)
        {
            mt.TextCentered("GAME SET", w * 0.5, 220, 44, gold);
            mt.TextCentered("SCORE " + TotalScore(), w * 0.5, 268, 28, cream);
        }
        // 操作プロンプト
        var prompt = "";
        if (state == stAim) prompt = "PRESS: SET POSITION";
        else if (state == stAngle) prompt = "PRESS: SET ANGLE";
        else if (state == stHook) prompt = "PRESS: SET HOOK";
        else if (state == stPower) prompt = "PRESS: THROW";
        if (prompt != "")
        {
            if (autoPlay) prompt = "AUTO PLAY - PRESS TO TAKE OVER";
            mt.TextCentered(prompt, w * 0.5, h - 28, 15,
                Color.Rgb(0.8, 0.82, 0.88));
            mt.TextCentered("SPACE / CLICK", w * 0.5, h - 10, 11, gray);
        }
    }

    // --- main loop ---------------------------------------------------------------
    public static void OnFrame(double dt)
    {
        // cs-lib クラスは load 順の都合で遅延生成 (hot reload で null に戻り
        // 作り直し → meshDirty 再メッシュも走る)
        var pinM = pinMesh ?? new Mesh3d("bw25_pin");
        pinMesh = pinM;
        var ballM = ballMesh ?? new Mesh3d("bw25_ball");
        ballMesh = ballM;
        var cubeM = cubeMesh ?? new Mesh3d("bw25_cube");
        cubeMesh = cubeM;
        var renNow = ren ?? new Renderer3d("bw25");
        ren = renNow;
        if (meshDirty)
        {
            pinM.Rebuild(Sdf.Mesh(PinModel(), 48, null));
            ballM.Rebuild(Sdf.Mesh(BallModel(), 48, null));
            if (!cubeM.Ready()) cubeM.Rebuild(Shapes3d.Cube());
            meshDirty = false;
        }
        EnsurePins();
        if (Input.KeyPressed("space") || Input.MousePressed())
            pendingPresses = pendingPresses + 1;

        var world = Phys3d.World("bowling", new WorldOpts3d
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
        var eyeNow = camEye;
        var tgtNow = camTgt;
        if (eyeNow == null || tgtNow == null) return;

        // --- 描画 ---
        // 暗めの場内 + レーン主体のライティング
        renNow.Light.Dir = new Vec3(-0.35, 1.0, -0.3);
        renNow.Light.Intensity = 1.15;
        renNow.Sky.Top = Color.Rgb(0.30, 0.33, 0.42);
        renNow.Sky.Bottom = Color.Rgb(0.10, 0.09, 0.09);
        renNow.Sky.Intensity = 0.38;
        renNow.Background = Color.Rgb(0.05, 0.06, 0.09);
        // 影のオルソ範囲は注視点 (カメラターゲット) 周辺に寄せて解像度を稼ぐ
        renNow.Shadow.Center = new Vec3(0, 0,
            MathUtil.Clamp(tgtNow.Z, 3.0, pinZ));
        renNow.Shadow.Extent = 7.0;
        renNow.Begin(new Camera
        {
            Eye = eyeNow,
            Target = tgtNow,
            Fov = camFov,
            Near = 0.05,
            Far = 80.0,
        });

        DrawStage();

        // ピン
        for (int i = 0; i < pins.Count; i++)
        {
            if (!pins[i].Standing) continue;
            var pose = Phys3d.Pose(world, "pin:" + i);
            if (pose != null)
                renNow.Draw(pinM, Renderer3d.PoseMat(pose));
        }
        // ボール (投球前は構え位置のプレビュー)
        if (ballLive)
        {
            var pose = Phys3d.Pose(world, "ball");
            if (pose != null)
                renNow.Draw(ballM, Renderer3d.PoseMat(pose));
        }
        else if (state <= stPower)
        {
            renNow.Draw(ballM, Mat4.Translate(new Vec3(aimX, ballR, 0)));
        }

        DrawGuide();
        renNow.End();

        // HUD は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        DrawHud();
        Gfx.EndPass();
    }
}
