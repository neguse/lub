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

/// <summary>ピン 1 本 (Haxe 版 typedef Pin と対)。</summary>
public class Pin
{
    public int gen; // version (ラック再設置で上げる)
    public bool standing; // ラック上に残っている (倒れた分はスイープ済み)
    public float x; // 定位置 (スポット)
    public float z;
}

public static class Bowling25
{
    const int W = 960;
    const int H = 540;
    const float DT = 1.0f / 60.0f;

    // --- 実寸 (m) ----------------------------------------------------------
    const float LANE_HW = 0.533f; // レーン半幅 (41.5in)
    const float GUTTER_W = 0.235f; // ガター幅
    const float PIN_Z = 18.29f; // ファウルライン→1番ピン (60ft)
    const float PIN_DX = 0.3048f; // 隣接ピン間隔 (12in)
    const float ROW_DZ = 0.2639f; // 列間 (12in × sin60°)
    const float DECK_END = 19.96f; // ピンデッキ末端。ここからピット
    const float PIT_END = 21.0f; // ピット奥 (クッション)
    const float OIL_END = 12.2f; // オイルパターン終端 (40ft 相当)
    const float BALL_R = 0.108f; // ボール半径 (8.5in 径)

    // 材質。摩擦の合成は sqrt(fA×fB) なので、ボール 0.2 に対して実効摩擦は
    // オイル上 ≈ 0.04、ドライ上 ≈ 0.17 と実物のレンジに合わせている
    const float FRIC_OIL = 0.008f;
    const float FRIC_DRY = 0.15f;
    const float BALL_DENSITY = 1190.0f; // 約 6.3kg (14lb 球)
    const float PIN_DENSITY = 620.0f; // 約 1.53kg
    const float SKID = 0.5f; // リリース時の転がり率 (1=完全転がり)

    // --- 状態機械 -----------------------------------------------------------
    const int ST_AIM = 0; // 立ち位置 (マーカーが往復)
    const int ST_ANGLE = 1; // 投球角度
    const int ST_HOOK = 2; // フック回転量
    const int ST_POWER = 3; // パワー → 投球
    const int ST_ROLL = 4; // ボールが転がっている
    const int ST_SETTLE = 5; // ピンが静止するのを待つ
    const int ST_SCORE = 6; // 判定表示 (スイープ済み)
    const int ST_END = 7; // ゲーム終了

    static int state = ST_AIM;
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

    // ピン配置: 1番ピンを頂点に 4 列の三角形
    static void ensurePins()
    {
        if (pins.Count > 0) return;
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < r + 1; c++)
            {
                pins.Add(new Pin
                {
                    gen = 1,
                    standing = true,
                    x = (c - r * 0.5f) * PIN_DX,
                    z = PIN_Z + r * ROW_DZ,
                });
            }
        }
    }

    static void rerack()
    {
        foreach (var p in pins)
        {
            p.gen++;
            p.standing = true;
        }
    }

    // --- SDF モデル ----------------------------------------------------------
    // ピン: 物理 shape (declarePinShapes) と寸法を揃えた回転体近似
    static SdfNode pinModel()
    {
        var white = 0xF2EFE6;
        var base_ = Sdf.capsule(new Vec3(0, 0.030f, 0), new Vec3(0, 0.090f, 0),
            0.051f);
        var belly = Sdf.sphere(0.0605f).move(0, 0.155f, 0);
        var neck = Sdf.capsule(new Vec3(0, 0.20f, 0), new Vec3(0, 0.30f, 0),
            0.032f);
        var head = Sdf.sphere(0.040f).move(0, 0.335f, 0);
        var body = base_.smin(belly, 0.03f).smin(neck, 0.035f).smin(head, 0.02f)
            .paint(white, 0.0f, 0.35f);
        var stripe1 = Sdf.torus(0.034f, 0.006f).move(0, 0.265f, 0)
            .paint(0xC2263D, 0.0f, 0.4f);
        var stripe2 = Sdf.torus(0.035f, 0.006f).move(0, 0.298f, 0)
            .paint(0xC2263D, 0.0f, 0.4f);
        return body.smin(stripe1, 0.006f).smin(stripe2, 0.006f);
    }

    // ボール: 指穴 3 つ + 飾りリング (回転が見えるように)
    static SdfNode ballModel()
    {
        var body = Sdf.sphere(BALL_R).paint(0x2B55A8, 0.15f, 0.25f);
        var ring = Sdf.torus(BALL_R, 0.0035f)
            .rotate(new Vec3(1, 0, 0.35f).normalize(), 1.0f)
            .paint(0xD9A441, 0.3f, 0.3f);
        var withRing = body.smin(ring, 0.002f);
        var h1 = Sdf.sphere(0.015f).move(0.024f, 0.098f, 0.027f);
        var h2 = Sdf.sphere(0.015f).move(-0.024f, 0.098f, 0.027f);
        var h3 = Sdf.sphere(0.018f).move(0.0f, 0.102f, -0.020f);
        return withRing.ssub(h1, 0.002f).ssub(h2, 0.002f).ssub(h3, 0.002f);
    }

    // native watch は chunk 再実行で初期値 true に戻り、web (module mode) は
    // onReload で立てる。どちらも再メッシュのトリガ。
    static bool meshDirty = true;

    public static void onReload()
    {
        meshDirty = true;
    }
    static Mesh3d? pinMesh = null;
    static Mesh3d? ballMesh = null;
    static Mesh3d? cubeMesh = null;

    // --- 物理宣言 ------------------------------------------------------------
    // 静物: x, y, z, hx, hy, hz, friction, restitution
    static List<float[]> STATICS = new List<float[]>
    {
        // アプローチ
        new float[]
            { 0, -0.06f, -1.25f, LANE_HW + GUTTER_W + 0.12f, 0.06f, 1.25f, 0.3f, 0.1f },
        // レーン (オイル)
        new float[]
            { 0, -0.06f, OIL_END * 0.5f, LANE_HW, 0.06f, OIL_END * 0.5f, FRIC_OIL, 0.08f },
        // レーン (ドライ) + ピンデッキ
        new float[]
        {
            0, -0.06f, (OIL_END + DECK_END) * 0.5f, LANE_HW, 0.06f,
            (DECK_END - OIL_END) * 0.5f, FRIC_DRY, 0.08f,
        },
        // ガター左
        new float[]
        {
            -(LANE_HW + GUTTER_W * 0.5f), -0.104f, DECK_END * 0.5f, GUTTER_W * 0.5f,
            0.05f, DECK_END * 0.5f, 0.3f, 0.1f,
        },
        // ガター右
        new float[]
        {
            LANE_HW + GUTTER_W * 0.5f, -0.104f, DECK_END * 0.5f, GUTTER_W * 0.5f,
            0.05f, DECK_END * 0.5f, 0.3f, 0.1f,
        },
        // 側壁左
        new float[]
        {
            -(LANE_HW + GUTTER_W + 0.03f), 0.08f, PIT_END * 0.5f, 0.03f, 0.22f,
            PIT_END * 0.5f, 0.2f, 0.3f,
        },
        // 側壁右
        new float[]
        {
            LANE_HW + GUTTER_W + 0.03f, 0.08f, PIT_END * 0.5f, 0.03f, 0.22f,
            PIT_END * 0.5f, 0.2f, 0.3f,
        },
        // ピット床
        new float[]
        {
            0, -0.58f, (DECK_END + PIT_END) * 0.5f, LANE_HW + GUTTER_W + 0.06f,
            0.05f, (PIT_END - DECK_END) * 0.5f + 0.2f, 0.9f, 0.02f,
        },
        // ピットクッション
        new float[]
        {
            0, -0.15f, PIT_END + 0.05f, LANE_HW + GUTTER_W + 0.06f, 0.45f, 0.05f,
            0.6f, 0.05f,
        },
        // マスキング (跳ねたピンが当たる)
        new float[]
            { 0, 0.95f, 19.3f, LANE_HW + GUTTER_W + 0.06f, 0.35f, 1.0f, 0.3f, 0.1f },
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
                friction = s[6],
                restitution = s[7],
            });
        }
    }

    // ピンの物理: 円柱の台座 + 腹の球 + 首カプセル + 頭球。均一密度でも
    // 台座が太いぶん重心は実物並み (床から約 0.16m) に落ちる
    static void declarePinShapes(BodyRef3d body, int ver)
    {
        var f = 0.35f;
        var rest = 0.3f;
        Phys3d.phys3d_cylinder(body, "base", new CylinderDesc3d
        {
            version = ver,
            height = 0.10f,
            radius = 0.051f,
            yOffset = 0.0f,
            density = PIN_DENSITY,
            friction = f,
            restitution = rest,
        });
        Phys3d.phys3d_sphere(body, "belly", new SphereDesc3d
        {
            version = ver,
            r = 0.0605f,
            offset = new Vec3d { x = 0.0f, y = 0.155f, z = 0.0f },
            density = PIN_DENSITY,
            friction = f,
            restitution = rest,
        });
        Phys3d.phys3d_capsule(body, "neck", new CapsuleDesc3d
        {
            version = ver,
            a = new Vec3d { x = 0.0f, y = 0.21f, z = 0.0f },
            b = new Vec3d { x = 0.0f, y = 0.31f, z = 0.0f },
            r = 0.032f,
            density = PIN_DENSITY,
            friction = f,
            restitution = rest,
        });
        Phys3d.phys3d_sphere(body, "head", new SphereDesc3d
        {
            version = ver,
            r = 0.040f,
            offset = new Vec3d { x = 0.0f, y = 0.335f, z = 0.0f },
            density = PIN_DENSITY,
            friction = f,
            restitution = rest,
        });
    }

    static void declarePins(WorldRef3d world)
    {
        for (int i = 0; i < pins.Count; i++)
        {
            var p = pins[i];
            if (!p.standing) continue;
            var body = Phys3d.phys3d_body(world, "pin:" + i, new BodyDesc3d
            {
                type = Phys3d.DYNAMIC,
                version = p.gen,
                linearDamping = 0.02f,
                angularDamping = 0.05f,
                initial = new InitialState3d { x = p.x, y = 0.001f, z = p.z },
            });
            if (body == null) continue;
            declarePinShapes(body, p.gen);
        }
    }

    static void declareBall(WorldRef3d world)
    {
        if (!ballLive) return;
        var body = Phys3d.phys3d_body(world, "ball", new BodyDesc3d
        {
            type = Phys3d.DYNAMIC,
            version = throwGen,
            bullet = true,
            angularDamping = 0.02f,
            initial = new InitialState3d
            {
                x = throwX,
                y = BALL_R + 0.001f,
                z = 0.0f,
                vx = ballVX,
                vz = ballVZ,
                wx = ballWX,
                wz = ballWZ,
            },
        });
        if (body == null) return;
        Phys3d.phys3d_sphere(body, "solid", new SphereDesc3d
        {
            version = throwGen,
            r = BALL_R,
            density = BALL_DENSITY,
            friction = 0.2f,
            restitution = 0.03f,
        });
    }

    // --- 投球 ----------------------------------------------------------------
    static void throwBall()
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
        var roll = SKID * spd / BALL_R;
        ballWX = roll * dz + hook * dx;
        ballWZ = -roll * dx + hook * dz;
        standingBefore = countStanding();
        stallFrames = 0;
        inGutter = false;
        enter(ST_ROLL);
    }

    static int countStanding()
    {
        var n = 0;
        foreach (var p in pins)
        {
            if (p.standing) n++;
        }
        return n;
    }

    static void updateRoll(WorldRef3d world)
    {
        var pose = Phys3d.phys3d_pose(world, "ball");
        var done = false;
        if (pose == null)
        {
            done = stateT > 10;
        }
        else
        {
            if (Math.Abs(pose.x) > LANE_HW + 0.02f && pose.y < 0.09f)
                inGutter = true;
            var sp = (float)Math.Sqrt(pose.vx * pose.vx + pose.vz * pose.vz);
            if (pose.y < -0.25f) // ピットに落ちた
            {
                done = true;
            }
            else if (sp < 0.12f && pose.z < DECK_END - 0.6f)
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
        if (done) enter(ST_SETTLE);
    }

    static void updateSettle(WorldRef3d world)
    {
        var maxSp = 0.0f;
        for (int i = 0; i < pins.Count; i++)
        {
            if (!pins[i].standing) continue;
            var pose = Phys3d.phys3d_pose(world, "pin:" + i);
            if (pose == null) continue;
            var sp = (float)Math.Sqrt(pose.vx * pose.vx + pose.vy * pose.vy
                + pose.vz * pose.vz);
            if (sp > maxSp) maxSp = sp;
        }
        if ((stateT > 45 && maxSp < 0.08f) || stateT > 300)
            countAndScore(world);
    }

    // 倒れた判定 → スイープ → 記録。ピンは傾き (up ベクトル) と高さで判定し、
    // 滑って立ったままのピンは実機同様その場に残す (オフスポット)
    static void countAndScore(WorldRef3d world)
    {
        var stand = 0;
        var knocked = 0;
        for (int i = 0; i < pins.Count; i++)
        {
            var p = pins[i];
            if (!p.standing) continue;
            var pose = Phys3d.phys3d_pose(world, "pin:" + i);
            var upY = pose != null
                ? 1.0f - 2.0f * (pose.qx * pose.qx + pose.qz * pose.qz)
                : -1.0f;
            if (pose == null || upY < 0.72f || pose.y < -0.05f || pose.y > 0.15f)
            {
                p.standing = false;
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
            showEvent("STRIKE!", Color.rgb(1.0f, 0.85f, 0.3f));
        else if (stand == 0 && knocked > 0)
            showEvent("SPARE!", Color.rgb(0.5f, 0.9f, 1.0f));
        else if (knocked == 0)
            showEvent(inGutter ? "GUTTER" : "NO PINS",
                Color.rgb(0.7f, 0.72f, 0.78f));
        else
            showEvent(knocked + " PINS", Color.rgb(0.95f, 0.93f, 0.85f));

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
        enter(ST_SCORE);
    }

    // --- スコア計算 (正式ルール) ----------------------------------------------
    static List<int> flatRolls()
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

    static int at(List<int> a, int i)
    {
        return i < a.Count ? a[i] : 0;
    }

    static int totalScore()
    {
        var flat = flatRolls();
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
                score += 10 + at(flat, i + 1) + at(flat, i + 2);
                i += 1;
            }
            else if (i + 1 < flat.Count && flat[i] + flat[i + 1] == 10)
            {
                score += 10 + at(flat, i + 2);
                i += 2;
            }
            else
            {
                score += flat[i] + at(flat, i + 1);
                i += 2;
            }
        }
        return score;
    }

    static string rollChar(int n)
    {
        return n == 0 ? "-" : "" + n;
    }

    static string markStr(int f)
    {
        if (f >= fRolls.Count) return "";
        var r = fRolls[f];
        if (f < 9)
        {
            if (r.Count >= 1 && r[0] == 10) return "X";
            var s = r.Count >= 1 ? rollChar(r[0]) : "";
            if (r.Count >= 2)
                s += r[0] + r[1] == 10 ? "/" : rollChar(r[1]);
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
                    s10 += rollChar(n);
                    rackFull = false;
                    prev = n;
                }
            }
            else
            {
                s10 += prev + n == 10 ? "/" : rollChar(n);
                rackFull = true;
            }
        }
        return s10;
    }

    // --- 状態機械 --------------------------------------------------------------
    static void enter(int s)
    {
        state = s;
        stateT = 0;
    }

    static bool buttonPressed()
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

    static void simulateTick(WorldRef3d world)
    {
        tAccum += DT;
        eventT += DT;
        Phys3d.phys3d_begin(world);
        updateSequence(world);
        declareStatics(world);
        declarePins(world);
        declareBall(world);
        Phys3d.phys3d_step(world, DT);
        updateCamera(world);
    }

    static void startAuto()
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
        var side = r.nextFloat() < 0.5f ? 1.0f : -1.0f;
        autoAimX = side * (0.12f + r.nextFloat() * 0.06f);
        autoHook = side * (18.0f + r.nextFloat() * 4.0f);
        autoPower = 0.82f + r.nextFloat() * 0.08f;
        // バックエンドの曲がり量 (ヘッドレス実測: 約 0.40m @ 球速 8.9m/s、
        // 遅いほど増える) から狙い角を逆算し、人間らしい誤差を足す
        var spd = 5.6f + 3.9f * autoPower;
        var drift = 0.40f + (8.9f - spd) * 0.3f;
        autoAngle = (side * (0.075f + drift) - autoAimX) / 18.3f
            + side * (r.nextFloat() * 0.006f - 0.003f);
    }

    static bool autoNear(float v, float target, float eps)
    {
        return autoPlay && Math.Abs(v - target) < eps;
    }

    static void updateSequence(WorldRef3d world)
    {
        stateT++;
        var pressed = buttonPressed();
        if (state == ST_AIM)
        {
            if (autoPlay && stateT == 1)
                startAuto(); // 投球ごとにラインを再抽選
            aimX = 0.42f * (float)Math.Sin(stateT * 0.030f);
            if (autoNear(aimX, autoAimX, 0.02f)) pressed = true;
            if (pressed)
            {
                angle = 0.0f;
                hook = 0.0f;
                enter(ST_ANGLE);
            }
            else if (!autoPlay)
            {
                idleT++;
                if (idleT > 240) startAuto();
            }
        }
        else if (state == ST_ANGLE)
        {
            angle = 0.10f * (float)Math.Sin(stateT * 0.045f);
            if (autoNear(angle, autoAngle, 0.006f)) pressed = true;
            if (pressed && stateT > 8) enter(ST_HOOK);
        }
        else if (state == ST_HOOK)
        {
            hook = 38.0f * (float)Math.Sin(stateT * 0.05f);
            if (autoNear(hook, autoHook, 2.5f)) pressed = true;
            if (pressed && stateT > 8) enter(ST_POWER);
        }
        else if (state == ST_POWER)
        {
            power = 0.5f - 0.5f * (float)Math.Cos(stateT * 0.055f);
            if (autoNear(power, autoPower, 0.04f)) pressed = true;
            if (pressed && stateT > 8) throwBall();
        }
        else if (state == ST_ROLL)
        {
            updateRoll(world);
        }
        else if (state == ST_SETTLE)
        {
            updateSettle(world);
        }
        else if (state == ST_SCORE)
        {
            if (stateT > 90)
            {
                if (gameOverPending)
                {
                    gameOverPending = false;
                    enter(ST_END);
                }
                else
                {
                    if (rerackPending) rerack();
                    enter(ST_AIM);
                }
            }
        }
        else if (state == ST_END)
        {
            if (stateT > 360)
            {
                fRolls = new List<List<int>>();
                fi = 0;
                rerack();
                enter(ST_AIM);
            }
        }
    }

    // --- カメラ ------------------------------------------------------------------
    static Vec3? camEye = null; // 初期値は updateCamera が補う (遅延生成)
    static Vec3? camTgt = null;
    static float camFov = 38.0f;

    static void updateCamera(WorldRef3d world)
    {
        var eye = camEye ?? new Vec3(0, 0.62f, -2.4f);
        var tgt = camTgt ?? new Vec3(0, 0.28f, 6.0f);
        var de = new Vec3(aimX * 0.55f, 0.62f, -2.4f);
        var dtg = new Vec3(aimX * 0.25f, 0.28f, 6.0f);
        var dfov = 38.0f;
        if (state == ST_ROLL)
        {
            var pose = Phys3d.phys3d_pose(world, "ball");
            if (pose != null && pose.z < 14.0f)
            {
                de = new Vec3(pose.x * 0.45f, 1.0f, pose.z - 3.2f);
                dtg = new Vec3(pose.x * 0.8f, 0.12f, pose.z + 4.5f);
                dfov = 42.0f;
            }
            else
            {
                de = new Vec3(-1.05f, 0.85f, 15.2f);
                dtg = new Vec3(0.05f, 0.25f, PIN_Z + 0.3f);
                dfov = 30.0f;
            }
        }
        else if (state == ST_SETTLE || state == ST_SCORE)
        {
            de = new Vec3(-1.05f, 0.8f, 15.6f);
            dtg = new Vec3(0.0f, 0.22f, PIN_Z + 0.3f);
            dfov = 28.0f;
        }
        else if (state == ST_END)
        {
            var a = tAccum * 0.25f;
            de = new Vec3((float)Math.Sin(a) * 2.8f, 1.5f,
                PIN_Z - 1.2f + (float)Math.Cos(a) * 2.8f);
            dtg = new Vec3(0, 0.2f, PIN_Z);
            dfov = 45.0f;
        }
        var k = Math.Min(1.0f, 5.0f * DT);
        camEye = eye.lerp(de, k);
        camTgt = tgt.lerp(dtg, k);
        camFov = MathUtil.lerp(camFov, dfov, k);
    }

    // --- 描画 --------------------------------------------------------------------
    static Renderer3d? ren = null;

    static Mat4 boxMat(float x, float y, float z, float sx, float sy,
        float sz)
    {
        return Mat4.translate(new Vec3(x, y, z))
            * Mat4.scale(new Vec3(sx, sy, sz));
    }

    static Mat4 boxMatR(float x, float y, float z, float ry, float sx,
        float sy, float sz)
    {
        return Mat4.translate(new Vec3(x, y, z)) * Mat4.rotateY(ry)
            * Mat4.scale(new Vec3(sx, sy, sz));
    }

    static void drawBox(Mat4 model, Color color, int? blend)
    {
        var r = ren;
        var cube = cubeMesh;
        if (r == null || cube == null) return;
        r.draw(cube, model, new Draw3dOpts { tint = color, blend = blend });
    }

    // 静的な舞台 (物理 STATICS と目視で寸法を揃える)
    static void drawStage()
    {
        var wood = Color.rgb(0.76f, 0.60f, 0.40f);
        var woodOil = Color.rgb(0.70f, 0.57f, 0.41f);
        var dark = Color.rgb(0.16f, 0.17f, 0.19f);
        var accentRed = Color.rgb(0.52f, 0.15f, 0.20f);
        var mark = Color.rgb(0.35f, 0.20f, 0.12f);

        // 周辺の床 (見た目のみ)
        drawBox(boxMat(0, -0.7f, 9.0f, 6.0f, 0.05f, 14.0f),
            Color.rgb(0.10f, 0.10f, 0.13f), null);
        // アプローチ
        drawBox(boxMat(0, -0.06f, -1.25f, LANE_HW + GUTTER_W + 0.12f, 0.06f, 1.25f),
            Color.rgb(0.62f, 0.51f, 0.36f), null);
        // レーン (オイル / ドライ)
        drawBox(boxMat(0, -0.06f, OIL_END * 0.5f, LANE_HW, 0.06f, OIL_END * 0.5f),
            woodOil, null);
        drawBox(boxMat(0, -0.06f, (OIL_END + DECK_END) * 0.5f, LANE_HW, 0.06f,
            (DECK_END - OIL_END) * 0.5f), wood, null);
        // ガター
        drawBox(boxMat(-(LANE_HW + GUTTER_W * 0.5f), -0.104f, DECK_END * 0.5f,
            GUTTER_W * 0.5f, 0.05f, DECK_END * 0.5f), dark, null);
        drawBox(boxMat(LANE_HW + GUTTER_W * 0.5f, -0.104f, DECK_END * 0.5f,
            GUTTER_W * 0.5f, 0.05f, DECK_END * 0.5f), dark, null);
        // 側壁
        drawBox(boxMat(-(LANE_HW + GUTTER_W + 0.03f), 0.08f, PIT_END * 0.5f, 0.03f,
            0.22f, PIT_END * 0.5f), Color.rgb(0.30f, 0.31f, 0.36f), null);
        drawBox(boxMat(LANE_HW + GUTTER_W + 0.03f, 0.08f, PIT_END * 0.5f, 0.03f,
            0.22f, PIT_END * 0.5f), Color.rgb(0.30f, 0.31f, 0.36f), null);
        // ピット (奥の暗がり) とマスキング
        drawBox(boxMat(0, -0.58f, (DECK_END + PIT_END) * 0.5f,
            LANE_HW + GUTTER_W + 0.06f, 0.05f, (PIT_END - DECK_END) * 0.5f + 0.2f),
            Color.rgb(0.05f, 0.05f, 0.07f), null);
        drawBox(boxMat(0, -0.15f, PIT_END + 0.05f, LANE_HW + GUTTER_W + 0.06f,
            0.45f, 0.05f), Color.rgb(0.08f, 0.08f, 0.10f), null);
        drawBox(boxMat(0, 0.95f, 19.3f, LANE_HW + GUTTER_W + 0.06f, 0.35f, 1.0f),
            accentRed, null);
        // ファウルライン
        drawBox(boxMat(0, 0.001f, 0, LANE_HW, 0.0015f, 0.012f),
            Color.rgb(0.15f, 0.15f, 0.17f), null);
        // ガイド: ドット (2.13m) とアロー (V 字に並ぶひし形)
        for (int i = 0; i < 7; i++)
        {
            var x = (i - 3) * 0.1365f;
            drawBox(boxMatR(x, 0.001f, 2.13f, (float)Math.PI / 4, 0.014f, 0.0015f, 0.014f),
                mark, null);
            drawBox(boxMatR(x, 0.001f, 4.88f - Math.Abs(i - 3.0f) * 0.406f,
                (float)Math.PI / 4, 0.026f, 0.0015f, 0.026f), mark, null);
        }
    }

    // 投球ガイド (目安の点線。物理予測ではなく初速と曲がりの傾向を図示)
    static void drawGuide()
    {
        if (state != ST_ANGLE && state != ST_HOOK && state != ST_POWER)
            return;
        var n = state == ST_POWER ? 5 + (int)Math.Floor(power * 8.0f) : 12;
        for (int k = 0; k < n; k++)
        {
            var d = 1.0f + k * 1.15f;
            var x = aimX + (float)Math.Sin(angle) * d
                - hook * 1.3e-4f * (float)Math.Pow(Math.Max(0.0f, d - 6.0f), 2.0f);
            if (Math.Abs(x) > LANE_HW) break;
            drawBox(boxMat(x, 0.004f, d, 0.016f, 0.002f, 0.028f),
                Color.rgb(1.0f, 1.0f, 1.0f, 0.4f), Gfx.ALPHA);
        }
        // パワーメーター (レーン右脇の柱)
        if (state == ST_POWER)
        {
            drawBox(boxMat(0.95f, 0.30f, -0.2f, 0.035f, 0.28f, 0.035f),
                Color.rgb(0.12f, 0.12f, 0.15f), null);
            var h = 0.26f * power;
            drawBox(boxMat(0.95f, 0.02f + h, -0.2f, 0.026f, h, 0.026f),
                Color.rgb(0.9f, 0.25f + 0.5f * (1 - power), 0.15f), null);
        }
    }

    // --- HUD ------------------------------------------------------------------
    static string? ttf = null;
    static int fontVersion = 0;
    static MeshText? mtext = null;
    static string eventText = "";
    static float eventT = 99.0f;
    static Color? eventCol = null;

    static void showEvent(string s, Color c)
    {
        eventText = s;
        eventT = 0.0f;
        eventCol = c;
    }

    static bool ensureText()
    {
        Io.load_text("samples/25_bowling/data/MPLUS1p-subset.ttf",
            out var text, out var version, out _, out _);
        if (text == null) return false;
        if (ttf == null || fontVersion != version)
        {
            ttf = text;
            fontVersion = version;
            mtext = new MeshText("bw25_text", text, version, W, H);
        }
        return mtext != null;
    }

    static void drawHud()
    {
        if (!ensureText()) return;
        var mt = mtext;
        if (mt == null) return;
        var cream = Color.rgb(0.96f, 0.95f, 0.9f);
        var gray = Color.rgb(0.55f, 0.57f, 0.62f);
        var gold = Color.rgb(1.0f, 0.85f, 0.3f);
        // スコアボード: 10 フレームのマーク列 + 合計
        var colW = 56.0f;
        var x0 = W * 0.5f - 4.5f * colW;
        for (int f = 0; f < 10; f++)
        {
            var cx = x0 + f * colW;
            var cur = f == fi && state != ST_END;
            mt.textCentered("" + (f + 1), cx, 24, 11, cur ? gold : gray);
            mt.textCentered(markStr(f), cx, 46, 20, cream);
        }
        mt.textCentered("SCORE " + totalScore(), W * 0.5f, 76, 16, cream);
        // イベント (出現時にスケールが弾む)
        var ec = eventCol;
        if (eventText != "" && eventT < 1.6f && ec != null)
        {
            var pop = 1.0f + 0.5f * (float)Math.Exp(-eventT * 9.0f);
            var a = eventT > 1.25f ? 1.0f - (eventT - 1.25f) / 0.35f : 1.0f;
            mt.textCentered(eventText, W * 0.5f, 205, 48 * pop,
                Color.rgb(ec.r, ec.g, ec.b, a));
        }
        // ゲーム終了
        if (state == ST_END)
        {
            mt.textCentered("GAME SET", W * 0.5f, 220, 44, gold);
            mt.textCentered("SCORE " + totalScore(), W * 0.5f, 268, 28, cream);
        }
        // 操作プロンプト
        var prompt = "";
        if (state == ST_AIM) prompt = "PRESS: SET POSITION";
        else if (state == ST_ANGLE) prompt = "PRESS: SET ANGLE";
        else if (state == ST_HOOK) prompt = "PRESS: SET HOOK";
        else if (state == ST_POWER) prompt = "PRESS: THROW";
        if (prompt != "")
        {
            if (autoPlay) prompt = "AUTO PLAY - PRESS TO TAKE OVER";
            mt.textCentered(prompt, W * 0.5f, H - 28, 15,
                Color.rgb(0.8f, 0.82f, 0.88f));
            mt.textCentered("SPACE / CLICK", W * 0.5f, H - 10, 11, gray);
        }
    }

    // --- main loop ---------------------------------------------------------------
    public static void onFrame(float dt)
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
            pinM.rebuild(Sdf.mesh(pinModel(), 48, null));
            ballM.rebuild(Sdf.mesh(ballModel(), 48, null));
            if (!cubeM.ready()) cubeM.rebuild(Shapes3d.cube());
            meshDirty = false;
        }
        ensurePins();
        if (Input.key_pressed("space") || Input.mouse_pressed())
            pendingPresses = pendingPresses + 1;

        var world = Phys3d.phys3d_world("bowling", new WorldOpts3d
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
        var eyeNow = camEye;
        var tgtNow = camTgt;
        if (eyeNow == null || tgtNow == null) return;

        // --- 描画 ---
        // 暗めの場内 + レーン主体のライティング
        renNow.light.dir = new Vec3(-0.35f, 1.0f, -0.3f);
        renNow.light.intensity = 1.15f;
        renNow.sky.top = Color.rgb(0.30f, 0.33f, 0.42f);
        renNow.sky.bottom = Color.rgb(0.10f, 0.09f, 0.09f);
        renNow.sky.intensity = 0.38f;
        renNow.background = Color.rgb(0.05f, 0.06f, 0.09f);
        // 影のオルソ範囲は注視点 (カメラターゲット) 周辺に寄せて解像度を稼ぐ
        renNow.shadow.center = new Vec3(0, 0,
            MathUtil.clamp(tgtNow.z, 3.0f, PIN_Z));
        renNow.shadow.extent = 7.0f;
        renNow.begin(new Camera
        {
            eye = eyeNow,
            target = tgtNow,
            fov = camFov,
            near = 0.05f,
            far = 80.0f,
        });

        drawStage();

        // ピン
        for (int i = 0; i < pins.Count; i++)
        {
            if (!pins[i].standing) continue;
            var pose = Phys3d.phys3d_pose(world, "pin:" + i);
            if (pose != null)
                renNow.draw(pinM, Renderer3d.poseMat(pose));
        }
        // ボール (投球前は構え位置のプレビュー)
        if (ballLive)
        {
            var pose = Phys3d.phys3d_pose(world, "ball");
            if (pose != null)
                renNow.draw(ballM, Renderer3d.poseMat(pose));
        }
        else if (state <= ST_POWER)
        {
            renNow.draw(ballM, Mat4.translate(new Vec3(aimX, BALL_R, 0)));
        }

        drawGuide();
        renNow.End();

        // HUD は tonemap 後の swapchain に重ね描き (load = LOAD)
        Gfx.begin_pass(new PassOpts { target = Gfx.main_tex, load = Gfx.LOAD });
        drawHud();
        Gfx.end_pass();
    }
}
