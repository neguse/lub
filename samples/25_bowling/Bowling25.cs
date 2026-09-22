// lub の samples/25_bowling の entry。
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
// ピンは class Pin、アトラクトの乱数は決定的な lubx.Rand を使う。cs-lib のクラス (Mesh3d / Renderer3d /
// Rand 等) は生成 Lua でサンプルより後に定義されるため static 初期化子で
// 参照できず、onFrame / 使用箇所で遅延生成する。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>ピン 1 本。</summary>
public class Pin
{
    public int Gen; // version (ラック再設置で上げる)
    public bool Standing; // ラック上に残っている (倒れた分はスイープ済み)
    public float X; // 定位置 (スポット)
    public float Z;
}

public static class Bowling25
{
    const int w = 960;
    const int h = 540;
    const float tickDt = 1.0f / 60.0f;

    // --- 実寸 (m) ----------------------------------------------------------
    const float laneHw = 0.533f; // レーン半幅 (41.5in)
    const float gutterW = 0.235f; // ガター幅
    const float pinZ = 18.29f; // ファウルライン→1番ピン (60ft)
    const float pinDx = 0.3048f; // 隣接ピン間隔 (12in)
    const float rowDz = 0.2639f; // 列間 (12in × sin60°)
    const float deckEnd = 19.96f; // ピンデッキ末端。ここからピット
    const float pitEnd = 21.0f; // ピット奥 (クッション)
    const float oilEnd = 12.2f; // オイルパターン終端 (40ft 相当)
    const float ballR = 0.108f; // ボール半径 (8.5in 径)

    // 材質。摩擦の合成は sqrt(fA×fB) なので、ボール 0.2 に対して実効摩擦は
    // オイル上 ≈ 0.04、ドライ上 ≈ 0.17 と実物のレンジに合わせている
    const float fricOil = 0.008f;
    const float fricDry = 0.15f;
    const float ballDensity = 1190.0f; // 約 6.3kg (14lb 球)
    const float pinDensity = 620.0f; // 約 1.53kg
    const float skid = 0.5f; // リリース時の転がり率 (1=完全転がり)

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
    static float tAccum = 0.0f;
    static FixedStep? step = null;
    static int pendingPresses = 0;

    // 投球パラメータ (各段階でロック)
    static float aimX = 0.0f;
    static float angle = 0.0f; // rad。+ で右へ
    static float hook = 0.0f; // rad/s。+ で左に曲がる
    static float power = 0.0f; // 0..1
    static int throwGen = 0;
    static bool ballLive = false;
    static float throwX = 0.0f;
    static float ballVX = 0.0f;
    static float ballVZ = 0.0f;
    static float ballWX = 0.0f;
    static float ballWZ = 0.0f;
    static int stallFrames = 0;
    static bool inGutter = false;
    static int standingBefore = 10;

    // アトラクトモード (放置で自動投球。ヘッドレス検証兼デモ)
    static bool autoPlay = false;
    static int idleT = 0;
    static float autoAimX = 0.0f;
    static float autoAngle = 0.0f;
    static float autoHook = 0.0f;
    static float autoPower = 0.85f;
    static Rand? rng = null;

    // スコア (10 フレーム正式ルール)
    static List<Pin> pins = new List<Pin>();
    static List<List<int>> fRolls = new List<List<int>>();
    static int fi = 0; // 現在フレーム (0..9)
    static bool rerackPending = false;
    static bool gameOverPending = false;

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
                    X = (c - r * 0.5f) * pinDx,
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
        var white = 0xFFF8E9;
        var baseShape = Sdf.Capsule(new Vec3(0, 0.030f, 0), new Vec3(0, 0.090f, 0),
            0.051f);
        var belly = Sdf.Sphere(0.0605f).Move(0, 0.155f, 0);
        var shoulder = Sdf.Sphere(0.046f).Move(0, 0.20f, 0);
        var neck = Sdf.Capsule(new Vec3(0, 0.22f, 0), new Vec3(0, 0.30f, 0),
            0.034f);
        var head = Sdf.Sphere(0.040f).Move(0, 0.335f, 0);
        var body = baseShape.Smin(belly, 0.03f).Smin(shoulder, 0.035f)
            .Smin(neck, 0.045f).Smin(head, 0.025f);
        var stripe1 = body.Intersect(Sdf.Box(0.1f, 0.008f, 0.1f).Move(0, 0.265f, 0))
            .Paint(0xD53242, 0.0f, 0.18f);
        var stripe2 = body.Intersect(Sdf.Box(0.1f, 0.008f, 0.1f).Move(0, 0.298f, 0))
            .Paint(0xD53242, 0.0f, 0.18f);
        return stripe1.Union(stripe2).Union(body.Paint(white, 0.0f, 0.18f));
    }

    // ボール: 指穴 3 つ + 飾りリング (回転が見えるように)
    static SdfNode BallModel()
    {
        var body = Sdf.Sphere(ballR).Paint(0x194878, 0.35f, 0.12f);
        var ring = Sdf.Torus(ballR, 0.0035f)
            .Rotate(new Vec3(1, 0, 0.35f).Normalize(), 1.0f)
            .Paint(0xEAC780, 0.5f, 0.18f);
        var withRing = body.Smin(ring, 0.002f);
        var h1 = Sdf.Sphere(0.015f).Move(0.024f, 0.098f, 0.027f);
        var h2 = Sdf.Sphere(0.015f).Move(-0.024f, 0.098f, 0.027f);
        var h3 = Sdf.Sphere(0.018f).Move(0.0f, 0.102f, -0.020f);
        return withRing.Ssub(h1, 0.002f).Ssub(h2, 0.002f).Ssub(h3, 0.002f);
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
    static Mesh3d? hallMesh = null;

    // --- 物理宣言 ------------------------------------------------------------
    // 静物: x, y, z, hx, hy, hz, friction, restitution
    static List<float[]> statics = new List<float[]>
    {
        // アプローチ
        new float[]
            { 0, -0.06f, -1.25f, laneHw + gutterW + 0.12f, 0.06f, 1.25f, 0.3f, 0.1f },
        // レーン (オイル)
        new float[]
            { 0, -0.06f, oilEnd * 0.5f, laneHw, 0.06f, oilEnd * 0.5f, fricOil, 0.08f },
        // レーン (ドライ) + ピンデッキ
        new float[]
        {
            0, -0.06f, (oilEnd + deckEnd) * 0.5f, laneHw, 0.06f,
            (deckEnd - oilEnd) * 0.5f, fricDry, 0.08f,
        },
        // ガター左
        new float[]
        {
            -(laneHw + gutterW * 0.5f), -0.104f, deckEnd * 0.5f, gutterW * 0.5f,
            0.05f, deckEnd * 0.5f, 0.3f, 0.1f,
        },
        // ガター右
        new float[]
        {
            laneHw + gutterW * 0.5f, -0.104f, deckEnd * 0.5f, gutterW * 0.5f,
            0.05f, deckEnd * 0.5f, 0.3f, 0.1f,
        },
        // 側壁左
        new float[]
        {
            -(laneHw + gutterW + 0.03f), 0.08f, pitEnd * 0.5f, 0.03f, 0.22f,
            pitEnd * 0.5f, 0.2f, 0.3f,
        },
        // 側壁右
        new float[]
        {
            laneHw + gutterW + 0.03f, 0.08f, pitEnd * 0.5f, 0.03f, 0.22f,
            pitEnd * 0.5f, 0.2f, 0.3f,
        },
        // ピット床
        new float[]
        {
            0, -0.58f, (deckEnd + pitEnd) * 0.5f, laneHw + gutterW + 0.06f,
            0.05f, (pitEnd - deckEnd) * 0.5f + 0.2f, 0.9f, 0.02f,
        },
        // ピットクッション
        new float[]
        {
            0, -0.15f, pitEnd + 0.05f, laneHw + gutterW + 0.06f, 0.45f, 0.05f,
            0.6f, 0.05f,
        },
        // マスキング (跳ねたピンが当たる)
        new float[]
            { 0, 0.95f, 19.3f, laneHw + gutterW + 0.06f, 0.35f, 1.0f, 0.3f, 0.1f },
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
        var f = 0.35f;
        var rest = 0.3f;
        Phys3d.Cylinder(body, "base", new CylinderDesc3d
        {
            Version = ver,
            Height = 0.10f,
            Radius = 0.051f,
            YOffset = 0.0f,
            Density = pinDensity,
            Friction = f,
            Restitution = rest,
        });
        Phys3d.Sphere(body, "belly", new SphereDesc3d
        {
            Version = ver,
            R = 0.0605f,
            Offset = new Vec3d { X = 0.0f, Y = 0.155f, Z = 0.0f },
            Density = pinDensity,
            Friction = f,
            Restitution = rest,
        });
        Phys3d.Capsule(body, "neck", new CapsuleDesc3d
        {
            Version = ver,
            A = new Vec3d { X = 0.0f, Y = 0.21f, Z = 0.0f },
            B = new Vec3d { X = 0.0f, Y = 0.31f, Z = 0.0f },
            R = 0.032f,
            Density = pinDensity,
            Friction = f,
            Restitution = rest,
        });
        Phys3d.Sphere(body, "head", new SphereDesc3d
        {
            Version = ver,
            R = 0.040f,
            Offset = new Vec3d { X = 0.0f, Y = 0.335f, Z = 0.0f },
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
                LinearDamping = 0.02f,
                AngularDamping = 0.05f,
                Initial = new InitialState3d { X = p.X, Y = 0.001f, Z = p.Z },
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
            AngularDamping = 0.02f,
            Initial = new InitialState3d
            {
                X = throwX,
                Y = ballR + 0.001f,
                Z = 0.0f,
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
            Friction = 0.2f,
            Restitution = 0.03f,
        });
    }

    // --- 投球 ----------------------------------------------------------------
    static void ThrowBall()
    {
        throwGen++;
        ballLive = true;
        throwX = aimX;
        var spd = 5.6f + 3.9f * power;
        var dx = (float)Math.Sin(angle);
        var dz = (float)Math.Cos(angle);
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
        var pose = Phys3d.PoseByKey(world, "ball");
        var done = false;
        if (pose == null)
        {
            done = stateT > 10;
        }
        else
        {
            if (Math.Abs(pose.X) > laneHw + 0.02f && pose.Y < 0.09f)
                inGutter = true;
            var sp = (float)Math.Sqrt(pose.Vx * pose.Vx + pose.Vz * pose.Vz);
            if (pose.Y < -0.25f) // ピットに落ちた
            {
                done = true;
            }
            else if (sp < 0.12f && pose.Z < deckEnd - 0.6f)
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
        var maxSp = 0.0f;
        for (int i = 0; i < pins.Count; i++)
        {
            if (!pins[i].Standing) continue;
            var pose = Phys3d.PoseByKey(world, "pin:" + i);
            if (pose == null) continue;
            var sp = (float)Math.Sqrt(pose.Vx * pose.Vx + pose.Vy * pose.Vy
                + pose.Vz * pose.Vz);
            if (sp > maxSp) maxSp = sp;
        }
        if ((stateT > 45 && maxSp < 0.08f) || stateT > 300)
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
            var pose = Phys3d.PoseByKey(world, "pin:" + i);
            var upY = pose != null
                ? 1.0f - 2.0f * (pose.Qx * pose.Qx + pose.Qz * pose.Qz)
                : -1.0f;
            if (pose == null || upY < 0.72f || pose.Y < -0.05f || pose.Y > 0.15f)
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
            ShowEvent("STRIKE!", Color.Rgb(1.0f, 0.85f, 0.3f));
        else if (stand == 0 && knocked > 0)
            ShowEvent("SPARE!", Color.Rgb(0.5f, 0.9f, 1.0f));
        else if (knocked == 0)
            ShowEvent(inGutter ? "GUTTER" : "NO PINS",
                Color.Rgb(0.7f, 0.72f, 0.78f));
        else
            ShowEvent(knocked + " PINS", Color.Rgb(0.95f, 0.93f, 0.85f));

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
        tAccum += tickDt;
        eventT += tickDt;
        Phys3d.Begin(world);
        UpdateSequence(world);
        DeclareStatics(world);
        DeclarePins(world);
        DeclareBall(world);
        Phys3d.Step(world, tickDt);
        UpdateCamera(world);
    }

    static void StartAuto()
    {
        // アトラクトの乱数は決定的な Rand
        var r = rng;
        if (r == null)
        {
            r = new Rand(0x25B0713);
            rng = r;
        }
        autoPlay = true;
        idleT = 0;
        // 外に出してポケット (±0.075) へ曲げ戻すライン。乱数で毎回散らす
        var side = r.NextFloat() < 0.5f ? 1.0f : -1.0f;
        autoAimX = side * (0.12f + r.NextFloat() * 0.06f);
        autoHook = side * (18.0f + r.NextFloat() * 4.0f);
        autoPower = 0.82f + r.NextFloat() * 0.08f;
        // バックエンドの曲がり量 (ヘッドレス実測: 約 0.40m @ 球速 8.9m/s、
        // 遅いほど増える) から狙い角を逆算し、人間らしい誤差を足す
        var spd = 5.6f + 3.9f * autoPower;
        var drift = 0.40f + (8.9f - spd) * 0.3f;
        autoAngle = (side * (0.075f + drift) - autoAimX) / 18.3f
            + side * (r.NextFloat() * 0.006f - 0.003f);
    }

    static bool AutoNear(float v, float target, float eps)
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
            aimX = 0.42f * (float)Math.Sin(stateT * 0.030f);
            if (AutoNear(aimX, autoAimX, 0.02f)) pressed = true;
            if (pressed)
            {
                angle = 0.0f;
                hook = 0.0f;
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
            angle = 0.10f * (float)Math.Sin(stateT * 0.045f);
            if (AutoNear(angle, autoAngle, 0.006f)) pressed = true;
            if (pressed && stateT > 8) Enter(stHook);
        }
        else if (state == stHook)
        {
            hook = 38.0f * (float)Math.Sin(stateT * 0.05f);
            if (AutoNear(hook, autoHook, 2.5f)) pressed = true;
            if (pressed && stateT > 8) Enter(stPower);
        }
        else if (state == stPower)
        {
            power = 0.5f - 0.5f * (float)Math.Cos(stateT * 0.055f);
            if (AutoNear(power, autoPower, 0.04f)) pressed = true;
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
    static float camFov = 38.0f;

    static void UpdateCamera(WorldRef3d world)
    {
        var eye = camEye ?? new Vec3(0, 0.46f, -1.8f);
        var tgt = camTgt ?? new Vec3(0, 0.20f, 8.0f);
        var de = new Vec3(aimX * 0.35f, 0.46f, -1.8f);
        var dtg = new Vec3(aimX * 0.25f, 0.20f, 8.0f);
        var dfov = 43.0f;
        if (state == stRoll)
        {
            var pose = Phys3d.PoseByKey(world, "ball");
            if (pose != null && pose.Z < 14.0f)
            {
                de = new Vec3(pose.X * 0.7f, 0.40f, pose.Z - 1.8f);
                dtg = new Vec3(pose.X * 0.8f, 0.16f, pose.Z + 3.5f);
                dfov = 42.0f;
            }
            else
            {
                de = new Vec3(-0.82f, 0.64f, 15.9f);
                dtg = new Vec3(0.05f, 0.25f, pinZ + 0.3f);
                dfov = 30.0f;
            }
        }
        else if (state == stSettle || state == stScore)
        {
            de = new Vec3(-0.82f, 0.64f, 15.9f);
            dtg = new Vec3(0.0f, 0.22f, pinZ + 0.3f);
            dfov = 28.0f;
        }
        else if (state == stEnd)
        {
            var a = tAccum * 0.25f;
            de = new Vec3((float)Math.Sin(a) * 2.8f, 1.5f,
                pinZ - 1.2f + (float)Math.Cos(a) * 2.8f);
            dtg = new Vec3(0, 0.2f, pinZ);
            dfov = 45.0f;
        }
        var k = Math.Min(1.0f, (state >= stRoll ? 9.0f : 5.0f) * tickDt);
        camEye = eye.Lerp(de, k);
        camTgt = tgt.Lerp(dtg, k);
        camFov = MathUtil.Lerp(camFov, dfov, k);
    }

    // --- 描画 --------------------------------------------------------------------
    static Renderer3d? ren = null;

    static Mat4 BoxMat(float x, float y, float z, float sx, float sy,
        float sz)
    {
        return Mat4.Translate(new Vec3(x, y, z))
            * Mat4.Scale(new Vec3(sx, sy, sz));
    }

    static Mat4 BoxMatR(float x, float y, float z, float ry, float sx,
        float sy, float sz)
    {
        return Mat4.Translate(new Vec3(x, y, z)) * Mat4.RotateY(-ry)
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
    static ShaderRef? SurfaceShader()
    {
        return Gfx.UseShader("bw25_surface", """
            struct U { float4x4 mvp; float4x4 model; float4x4 light_mvp; float4 tint; };
            ConstantBuffer<U> u;
            struct V { float3 pos; float pad0; float3 normal; float pad1; float3 color; float pad2; float2 mr; float2 pad3; };
            StructuredBuffer<V> verts;
            struct Out {
                float3 normal : TEXCOORD0; float3 world : TEXCOORD1;
                float4 shadow : TEXCOORD2; float2 mr : TEXCOORD3;
                float4 color : COLOR0; float4 pos : SV_Position;
            };
            [shader("vertex")] Out vs_main(uint id : LUB_VERTEX_ID) {
                V v = verts[id]; Out o;
                float4 world = mul(u.model, float4(v.pos, 1));
                o.pos = mul(u.mvp, float4(v.pos, 1));
                o.world = world.xyz; o.normal = mul(u.model, float4(v.normal, 0)).xyz;
                o.shadow = mul(u.light_mvp, world); o.mr = v.mr;
                o.color = float4(pow(v.color * u.tint.rgb, float3(2.2, 2.2, 2.2)), u.tint.a);
                return o;
            }
            """, """
            LUB_TEXTURE2D(shadow_map);
            struct F {
                float4 light_dir; float4 light_col; float4 sky_col;
                float4 ground_col; float4 cam_pos; float4 shadow_p;
            };
            ConstantBuffer<F> f;
            struct In {
                float3 normal : TEXCOORD0; float3 world : TEXCOORD1;
                float4 shadow : TEXCOORD2; float2 mr : TEXCOORD3; float4 color : COLOR0;
            };
            [shader("fragment")] float4 fs_main(In i) : SV_Target {
                float3 n = normalize(i.normal);
                float3 v = normalize(f.cam_pos.xyz - i.world);
                float3 p = i.shadow.xyz / i.shadow.w;
                p.xy = p.xy * float2(0.5, -0.5) + 0.5;
                float3 dx = ddx(p), dy = ddy(p);
                float det = dx.x * dy.y - dx.y * dy.x;
                float2 gradient = abs(det) < 1e-15 ? float2(0, 0)
                    : float2(dx.z * dy.y - dy.z * dx.y, dx.x * dy.z - dy.x * dx.z) / det;
                float2 uv = p.xy;
                float shadow = 1;
                if (f.shadow_p.z > 0.5 && all(uv > 0) && all(uv < 1) && p.z > 0 && p.z < 1) {
                    shadow = 0;
                    float texel = f.shadow_p.x;
                    float2 coord = uv / texel - 0.5;
                    float2 base = floor(coord), fraction = frac(coord);
                    for (int y = -1; y <= 2; y++)
                        for (int x = -1; x <= 2; x++) {
                            float2 at = clamp((base + float2(x, y) + 0.5) * texel, texel * 0.5, 1 - texel * 0.5);
                            float depth = LUB_SAMPLE_LOD(shadow_map, at).r;
                            float receiver = p.z + dot(gradient, at - uv);
                            float wx = x == -1 ? 1 - fraction.x : (x == 2 ? fraction.x : 1);
                            float wy = y == -1 ? 1 - fraction.y : (y == 2 ? fraction.y : 1);
                            shadow += receiver - f.shadow_p.y <= depth ? wx * wy / 9 : 0;
                        }
                }
                float3 ambient = lerp(f.ground_col.rgb, f.sky_col.rgb, n.y * 0.5 + 0.5) * f.sky_col.w;
                float3 diffuse = f.light_col.rgb * saturate(dot(n, f.light_dir.xyz)) * shadow;
                float3 deckLight = float3(0, 0.57, 18.30) - i.world;
                float deckDistance = max(dot(deckLight, deckLight), 0.01);
                diffuse += float3(1.6, 1.4, 1.1) * saturate(dot(n, deckLight * rsqrt(deckDistance)))
                    / (1 + deckDistance * 2) * smoothstep(17, 18, i.world.z);
                float3 r = reflect(-v, n);
                float3 ceiling = i.world + r * ((4.54 - i.world.y) / max(r.y, 0.001));
                float across = abs(frac(ceiling.x / 2.25 + 0.5) - 0.5) * 2.25;
                float along = abs(frac((ceiling.z - 1) / 5 + 0.5) - 0.5) * 5;
                float soft = 0.025 + i.mr.y * i.mr.y * 0.65;
                float lamps = (1 - smoothstep(0.135, 0.135 + soft, across))
                    * (1 - smoothstep(1.5, 1.5 + soft, along))
                    * step(0.01, r.y) * step(abs(ceiling.x), 5.6)
                    * step(-0.5, ceiling.z) * step(ceiling.z, 17.5);
                float fresnel = 0.06 + 0.94 * pow(1 - saturate(dot(n, v)), 5);
                float3 specular = float3(4.8, 3.8, 2.5) * lamps * fresnel
                    * (1 - smoothstep(0.35, 0.55, i.mr.y));
                float highlight = pow(saturate(dot(n, normalize(v + f.light_dir.xyz))), 64);
                specular += f.light_col.rgb * highlight * (1 - i.mr.y) * 0.3 * shadow;
                return float4(i.color.rgb * (ambient + diffuse) + specular, i.color.a);
            }
            """, 1);
    }

    static void BuildHall()
    {
        var v = new List<float>();
        var navy = new List<float> { 0.095f, 0.15f, 0.23f, 1 };
        var brass = new List<float> { 0.70f, 0.53f, 0.29f, 1 };
        var lamp = new List<float> { 2.1f, 1.75f, 1.20f, 1 };
        var blue = new List<float> { 0.35f, 0.72f, 0.90f, 1 };
        var up = new List<float> { 0, 1, 0 };
        Shapes.Box(v, 0, 2.5f, 22, 16, 6, 0.3f, navy);
        for (int lane = -2; lane <= 2; lane++)
        {
            float center = lane * 2.25f;
            for (int board = 0; board < 39; board++)
            {
                float x = center - laneHw + (board + 0.5f) * (laneHw * 2 / 39);
                for (int section = 0; section < 9; section++)
                {
                    float tone = ((board * 13 + section * 7) % 11) * 0.007f;
                    var wood = new List<float> { 0.73f + tone, 0.53f + tone, 0.31f + tone, 1 };
                    float half = laneHw / 39;
                    float length = (deckEnd + 2.5f) / 9;
                    float z0 = -2.5f + section * length;
                    float z1 = z0 + length;
                    Shapes.Quad(v, new List<float> { x - half, 0, z0 },
                        new List<float> { x - half, 0, z1 }, new List<float> { x + half, 0, z1 },
                        new List<float> { x + half, 0, z0 }, up, wood);
                }
            }
            if (lane != 0)
            {
                Shapes.Box(v, center - 0.7f, -0.06f, 9, 0.31f, 0.06f, 23, navy);
                Shapes.Box(v, center + 0.7f, -0.06f, 9, 0.31f, 0.06f, 23, navy);
            }
            Shapes.Box(v, center, 1.15f, 20.8f, 2.05f, 1.55f, 0.18f, navy);
            Shapes.Box(v, center, 1.86f, 20.68f, 1.90f, 0.04f, 0.05f, brass);
            Shapes.Box(v, center, 0.45f, 20.68f, 1.90f, 0.025f, 0.05f, blue);
            for (int bar = 0; bar < 5; bar++)
                Shapes.Box(v, center - 0.64f + bar * 0.32f, 1.15f, 20.67f,
                    0.055f, 0.4f + (2 - Math.Abs(bar - 2)) * 0.18f, 0.03f, brass);
            for (int light = 0; light < 4; light++)
            {
                float z = 1 + light * 5;
                Shapes.Box(v, center, 4.65f, z, 0.42f, 0.16f, 3.2f, brass);
                Shapes.Box(v, center, 4.54f, z, 0.27f, 0.035f, 3.0f, lamp);
            }
        }
        Shapes.Box(v, 0, 0.57f, 18.30f, 1.2f, 0.025f, 0.045f, lamp);
        for (int side = -1; side <= 1; side += 2)
        {
            Shapes.Box(v, side * 6.3f, 2.2f, 9, 0.22f, 4.4f, 26, navy);
            for (int rib = 0; rib < 14; rib++)
                Shapes.Box(v, side * 6.16f, 2.2f, -3 + rib * 1.9f, 0.10f, 4.4f, 0.10f, brass);
            Shapes.Box(v, side * (laneHw + gutterW + 0.04f), 0.31f, 10,
                0.025f, 0.018f, 20, lamp);
        }
        var mesh = hallMesh ?? new Mesh3d("bw25_hall");
        hallMesh = mesh;
        var data = Shapes3d.FromInterleaved(v);
        var mr = new List<float>();
        for (int i = 0; i < data.VertCount; i++)
        {
            mr.Add(0.0f);
            mr.Add(data.Positions[i * 3 + 1] < 0.03f ? 0.22f : 0.65f);
        }
        data.MetalRough = mr;
        mesh.Rebuild(data);
    }

    static void DrawStage()
    {
        var dark = Color.Rgb(0.16f, 0.17f, 0.19f);
        var accentRed = Color.Rgb(0.10f, 0.17f, 0.25f);
        var mark = Color.Rgb(0.35f, 0.20f, 0.12f);

        if (hallMesh != null && ren != null)
            ren.Draw(hallMesh, new Mat4(), new Draw3dOpts { Shader = SurfaceShader() });
        // 周辺の床 (見た目のみ)
        DrawBox(BoxMat(0, -0.7f, 9.0f, 6.0f, 0.05f, 14.0f),
            Color.Rgb(0.10f, 0.10f, 0.13f), null);
        // ガター
        DrawBox(BoxMat(-(laneHw + gutterW * 0.5f), -0.104f, deckEnd * 0.5f,
            gutterW * 0.5f, 0.05f, deckEnd * 0.5f), dark, null);
        DrawBox(BoxMat(laneHw + gutterW * 0.5f, -0.104f, deckEnd * 0.5f,
            gutterW * 0.5f, 0.05f, deckEnd * 0.5f), dark, null);
        // 側壁
        DrawBox(BoxMat(-(laneHw + gutterW + 0.03f), 0.08f, pitEnd * 0.5f, 0.03f,
            0.22f, pitEnd * 0.5f), Color.Rgb(0.11f, 0.18f, 0.25f), null);
        DrawBox(BoxMat(laneHw + gutterW + 0.03f, 0.08f, pitEnd * 0.5f, 0.03f,
            0.22f, pitEnd * 0.5f), Color.Rgb(0.11f, 0.18f, 0.25f), null);
        // ピット (奥の暗がり) とマスキング
        DrawBox(BoxMat(0, -0.58f, (deckEnd + pitEnd) * 0.5f,
            laneHw + gutterW + 0.06f, 0.05f, (pitEnd - deckEnd) * 0.5f + 0.2f),
            Color.Rgb(0.05f, 0.05f, 0.07f), null);
        DrawBox(BoxMat(0, -0.15f, pitEnd + 0.05f, laneHw + gutterW + 0.06f,
            0.45f, 0.05f), Color.Rgb(0.08f, 0.08f, 0.10f), null);
        DrawBox(BoxMat(0, 0.95f, 19.3f, laneHw + gutterW + 0.06f, 0.35f, 1.0f),
            accentRed, null);
        // ファウルライン
        DrawBox(BoxMat(0, 0.001f, 0, laneHw, 0.0015f, 0.012f),
            Color.Rgb(0.15f, 0.15f, 0.17f), null);
        // ガイド: ドット (2.13m) とアロー (V 字に並ぶひし形)
        for (int i = 0; i < 7; i++)
        {
            var x = (i - 3) * 0.1365f;
            DrawBox(BoxMatR(x, 0.001f, 2.13f, (float)Math.PI / 4, 0.014f, 0.0015f, 0.014f),
                mark, null);
            DrawBox(BoxMatR(x, 0.001f, 4.88f - Math.Abs(i - 3.0f) * 0.406f,
                (float)Math.PI / 4, 0.026f, 0.0015f, 0.026f), mark, null);
        }
    }

    // 投球ガイド (目安の点線。物理予測ではなく初速と曲がりの傾向を図示)
    static void DrawGuide()
    {
        if (state != stAngle && state != stHook && state != stPower)
            return;
        var n = state == stPower ? 5 + (int)Math.Floor(power * 8.0f) : 12;
        for (int k = 0; k < n; k++)
        {
            var d = 1.0f + k * 1.15f;
            var x = aimX + (float)Math.Sin(angle) * d
                - hook * 1.3e-4f * (float)Math.Pow(Math.Max(0.0f, d - 6.0f), 2.0f);
            if (Math.Abs(x) > laneHw) break;
            DrawBox(BoxMat(x, 0.004f, d, 0.016f, 0.002f, 0.028f),
                Color.Rgb(1.0f, 1.0f, 1.0f, 0.4f), Gfx.Blend.Alpha);
        }
        // パワーメーター (レーン右脇の柱)
        if (state == stPower)
        {
            DrawBox(BoxMat(0.95f, 0.30f, -0.2f, 0.035f, 0.28f, 0.035f),
                Color.Rgb(0.12f, 0.12f, 0.15f), null);
            var h = 0.26f * power;
            DrawBox(BoxMat(0.95f, 0.02f + h, -0.2f, 0.026f, h, 0.026f),
                Color.Rgb(0.9f, 0.25f + 0.5f * (1 - power), 0.15f), null);
        }
    }

    // --- HUD ------------------------------------------------------------------
    const string fontPath = "samples/data/fonts/MPLUS1p-subset.ttf";
    static bool fontLoaded = false;
    static int fontVersion = 0;
    static Text? mtext = null;
    static SpriteBatch? hud = null;
    static string eventText = "";
    static float eventT = 99.0f;
    static Color? eventCol = null;

    static void ShowEvent(string s, Color c)
    {
        eventText = s;
        eventT = 0.0f;
        eventCol = c;
    }

    static bool EnsureText()
    {
        Io.LoadBytes(fontPath, out var bytes, out var version, out _, out _);
        if (bytes == null) return false;
        if (!fontLoaded || fontVersion != version)
        {
            fontLoaded = true;
            fontVersion = version;
            mtext = new Text("bw25_text", fontPath, 40, 1024);
            hud = new SpriteBatch(w, h, "bw25_hud", "bw25_hud");
        }
        return mtext != null;
    }

    static void HudText(string text, float x, float y, float size, Color color, bool center = false)
    {
        var mt = mtext;
        var batch = hud;
        if (mt == null || batch == null) return;
        float scale = size / mt.Px;
        mt.Draw(batch, text, center ? x - mt.Width(text, scale) * 0.5f : x, y, color, scale);
    }

    static void DrawHud()
    {
        if (!EnsureText()) return;
        var batch = hud;
        if (batch == null) return;
        var cream = Color.Hex(0xFFF3DB);
        var muted = Color.Hex(0xA8BCC6);
        var gold = Color.Hex(0xEDC782);
        var ink = Color.Rgb(0.035f, 0.08f, 0.13f, 0.94f);
        batch.Begin();
        batch.Rect(24, 24, 120, 76, ink);
        batch.Rect(148, 24, 652, 76, ink);
        batch.Rect(804, 24, 132, 76, ink);
        HudText("TEN PIN", 84, 55, 20, cream, true);
        HudText("BOWLING CLUB", 84, 78, 11, gold, true);
        for (int f = 0; f < 10; f++)
        {
            float x = 151 + f * 65;
            bool current = f == fi && state != stEnd;
            if (current)
            {
                batch.Rect(x, 24, 62, 76, Color.Rgb(0.16f, 0.25f, 0.30f));
                batch.Rect(x, 97, 62, 3, gold);
            }
            HudText("" + (f + 1), x + 31, 46, 13, current ? gold : muted, true);
            HudText(MarkStr(f), x + 31, 80, 22, cream, true);
        }
        HudText("TOTAL", 870, 45, 12, gold, true);
        HudText("" + TotalScore(), 870, 84, 36, cream, true);
        if (state <= stPower)
        {
            batch.Rect(232, 441, 496, 79, ink);
            var labels = new string[] { "POSITION", "ANGLE", "HOOK", "POWER" };
            for (int i = 0; i < 4; i++)
            {
                float x = 246 + i * 119;
                batch.Rect(x, 453, 111, 3, i <= state ? gold : Color.Hex(0x304653));
                HudText(labels[i], x + 55, 478, 14, i == state ? cream : muted, true);
            }
            HudText(autoPlay ? "AUTO PLAY  /  PRESS TO TAKE OVER" : state == stPower ? "SPACE / CLICK  /  THROW" : "SPACE / CLICK  /  LOCK IN",
                w * 0.5f, 505, 14, gold, true);
            if (state == stPower)
            {
                batch.Rect(316, 414, 328, 10, ink);
                batch.Rect(320, 417, 320 * power, 4, gold);
            }
        }
        batch.Flush();
        var ec = eventCol;
        if (eventText != "" && eventT < 1.6f && ec != null || state == stEnd)
        {
            float a = state == stEnd ? 1 : MathUtil.Clamp((1.6f - eventT) / 0.3f, 0, 1);
            float slide = state == stEnd ? 0 : (float)Math.Exp(-eventT * 14) * 26;
            batch.Begin();
            batch.Rect(240, 369 + slide, 480, 129, Color.Rgb(0.035f, 0.08f, 0.13f, 0.96f * a));
            batch.Rect(240, 369 + slide, 480, 3, Color.Rgb(gold.R, gold.G, gold.B, a));
            HudText(state == stEnd ? "GAME SET" : eventText, w * 0.5f, 432 + slide, 42,
                Color.Rgb(cream.R, cream.G, cream.B, a), true);
            HudText("TOTAL  " + TotalScore(), w * 0.5f, 473 + slide, 20,
                Color.Rgb(gold.R, gold.G, gold.B, a), true);
            batch.Flush();
        }
    }

    // --- main loop ---------------------------------------------------------------
    public static void OnFrame(float dt)
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
            pinM.Rebuild(Sdf.Mesh(PinModel(), 64, null));
            var ballData = Sdf.Mesh(BallModel(), 64, null);
            var colors = ballData.Colors;
            if (colors != null)
            {
                var marble = new List<float>();
                for (int i = 0; i < ballData.VertCount; i++)
                {
                    float x = ballData.Positions[i * 3] / ballR;
                    float y = ballData.Positions[i * 3 + 1] / ballR;
                    float z = ballData.Positions[i * 3 + 2] / ballR;
                    float vein = (float)Math.Pow(0.5f + 0.5f * (float)Math.Sin(x * 15 + y * 9 + 3 * (float)Math.Sin(z * 7)), 5);
                    if (colors[i * 3 + 2] > colors[i * 3])
                    {
                        marble.Add(0.10f + vein * 0.22f);
                        marble.Add(0.23f + vein * 0.40f);
                        marble.Add(0.43f + vein * 0.34f);
                    }
                    else
                    {
                        marble.Add(colors[i * 3]);
                        marble.Add(colors[i * 3 + 1]);
                        marble.Add(colors[i * 3 + 2]);
                    }
                }
                ballData.Colors = marble;
            }
            ballM.Rebuild(ballData);
            BuildHall();
            if (!cubeM.Ready()) cubeM.Rebuild(Shapes3d.Cube());
            meshDirty = false;
        }
        EnsurePins();
        if (Input.KeyPressed("space") || Input.MousePressed())
            pendingPresses = pendingPresses + 1;

        var world = Phys3d.World("bowling", new WorldOpts3d
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
        var eyeNow = camEye;
        var tgtNow = camTgt;
        if (eyeNow == null || tgtNow == null) return;

        // --- 描画 ---
        // 暗めの場内 + レーン主体のライティング
        renNow.Light.Dir = new Vec3(-0.20f, 1.0f, -0.45f);
        renNow.Light.Color = Color.Rgb(1.0f, 0.88f, 0.70f);
        renNow.Light.Intensity = 2.0f;
        renNow.Sky.Top = Color.Rgb(0.32f, 0.45f, 0.66f);
        renNow.Sky.Bottom = Color.Rgb(0.10f, 0.09f, 0.09f);
        renNow.Sky.Intensity = 0.65f;
        renNow.Background = Color.Rgb(0.045f, 0.075f, 0.12f);
        renNow.Ssao.Radius = 0.12f;
        renNow.Ssao.Strength = 0.65f;
        renNow.Bloom.Strength = 0.22f;
        renNow.Vignette = 0.22f;
        // 影のオルソ範囲は注視点 (カメラターゲット) 周辺に寄せて解像度を稼ぐ
        renNow.Shadow.Center = new Vec3(0, 0,
            MathUtil.Clamp(tgtNow.Z, 3.0f, pinZ));
        renNow.Shadow.Extent = 7.0f;
        renNow.Begin(new Camera
        {
            Eye = eyeNow,
            Target = tgtNow,
            Fov = camFov,
            Near = 0.05f,
            Far = 80.0f,
        });

        DrawStage();

        // ピン
        for (int i = 0; i < pins.Count; i++)
        {
            if (!pins[i].Standing) continue;
            var pose = Phys3d.PoseByKey(world, "pin:" + i);
            if (pose != null)
                renNow.Draw(pinM, Renderer3d.PoseMat(pose), new Draw3dOpts { Shader = SurfaceShader() });
        }
        // ボール (投球前は構え位置のプレビュー)
        if (ballLive)
        {
            var pose = Phys3d.PoseByKey(world, "ball");
            if (pose != null)
                renNow.Draw(ballM, Renderer3d.PoseMat(pose), new Draw3dOpts { Shader = SurfaceShader() });
        }
        else if (state <= stPower)
        {
            renNow.Draw(ballM, Mat4.Translate(new Vec3(aimX, ballR, 0)),
                new Draw3dOpts { Shader = SurfaceShader() });
        }

        DrawGuide();
        renNow.End();

        // HUD は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        DrawHud();
        Gfx.EndPass();
    }
}
