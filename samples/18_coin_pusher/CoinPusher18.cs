// lub の samples/18_coin_pusher の entry。
// 実行: lub samples/18_coin_pusher/CoinPusher18.csproj (transpile + watch + hot reload)
// Phys3d の即時モード API で shelf/tray/pusher/coin を毎フレーム宣言し、
// step 後の pose を Renderer3d (lit + shadow + bloom) で描く。
// tcs 制約による置換:
// typedef Coin → class Coin、匿名 {coin, body, index} → class LiveCoin、
// 整数除算・剰余 → FloorDiv/Mod (float 経由、対象は非負)、
// Mesh3d/Renderer3d は static 初期化子で作れないため onInit で作る。

using System;
using System.Collections.Generic;
using static Lub;

public class Coin
{
    public bool Active;
    public int Gen;
    public int Flash;
    public int Value; // 1 = 通常, 5 = ボーナス (大型)
    public int Born; // 投入フレーム。プール満杯時は最古を再利用する
    public float SpawnX;
    public float SpawnY;
    public float SpawnZ;
}

/// <summary>declareCoins が返す 1 コイン分。</summary>
public class LiveCoin
{
    public Coin Coin;
    public BodyRef3d Body;
    public int Index;

    public LiveCoin(Coin coin, BodyRef3d body, int index)
    {
        this.Coin = coin;
        this.Body = body;
        this.Index = index;
    }
}

// コインプッシャー。遊びの構造:
// - トレイ前方 1/3 は側面が開いていて、横からこぼれたコインは没収 (リスク)。
//   前縁から落としたときだけ払い出し (リターン)。
// - スコアは手前の受け皿に積まれるコインの山で見せる (10 の位 + 1 の位)。
// - 定期投入の数枚に 1 枚は大型ボーナスコイン (5 点)。
// - 投入はポインタ位置へ直接 (クリック/タップ = その真下に投入)。無限に出せる。
public static class CoinPusher18
{
    const float tickDt = 1.0f / 60.0f;
    const int maxCoins = 80;
    const float coinR = 0.17f;
    const float coinH = 0.07f;
    const float bonusR = 0.27f;
    const float bonusH = 0.1f;
    const int autoInterval = 75;
    const int bonusEvery = 6; // 自動投入の何枚に 1 枚がボーナスか
    const float dropY = 1.35f;
    const float dropZ = -1.0f;

    static int frame = 0;
    static List<Coin> coins = new List<Coin>();
    static int score = 0;
    static int autoCount = 0;
    static float spawnX = 0.0f;
    static int payoutFlash = 0;
    static int markerPulse = 0;
    static FixedStep? step = null;
    static int pendingSpawns = 0;
    static WorldRef3d? world = null;
    static List<int> renderCoinIndices = new List<int>();

    // 壁 {x, y, z, hx, hy, hz}。物理と描画で共有する。
    // トレイ側面は前方 (z > 0.4) が開いていて、そこが側溝 = 没収ゾーン。
    static List<float[]> walls = new List<float[]>
    {
        new float[] { -1.58f, 0.5f, -1.7f, 0.08f, 0.5f, 0.9f }, // shelf 側面 L
        new float[] { 1.58f, 0.5f, -1.7f, 0.08f, 0.5f, 0.9f }, // shelf 側面 R
        new float[] { -1.58f, 0.28f, -0.35f, 0.08f, 0.28f, 0.45f }, // tray 側面 L (前方は開放)
        new float[] { 1.58f, 0.28f, -0.35f, 0.08f, 0.28f, 0.45f }, // tray 側面 R (前方は開放)
        new float[] { 0.0f, 0.8f, -2.68f, 1.5f, 0.8f, 0.08f }, // 背面
    };

    // --- procedural unit meshes (Shapes3d) -----------------------------------
    static Mesh3d? cubeMesh = null;
    static Mesh3d? cylMesh = null;
    static Renderer3d? ren = null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend, Width = 640, Height = 360 });
        cubeMesh = new Mesh3d("cp_cube");
        cylMesh = new Mesh3d("cp_cyl");
        ren = new Renderer3d("cp18");
        for (int i = 0; i < maxCoins; i++)
        {
            coins.Add(new Coin
            {
                Active = false,
                Gen = 0,
                Flash = 0,
                Value = 1,
                Born = 0,
                SpawnX = 0.0f,
                SpawnY = dropY,
                SpawnZ = dropZ,
            });
        }
        PrefillTable();
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    // tcs は整数除算・剰余を出せないので float 経由で書く (v, n は非負前提)。
    static int FloorDiv(int v, int n)
    {
        return (int)Math.Floor(v / (float)n);
    }

    static int Mod(int v, int n)
    {
        return v - (int)Math.Floor(v / (float)n) * n;
    }

    // 空の台では最初の数分間なにも起きないので、起動時に台を埋めておく。
    // 決定論のため配置は固定の擬似乱数。
    static void PrefillTable()
    {
        int n = 0;
        // shelf 上 (プッシャーの前) に 2 列
        for (int i = 0; i < 10; i++)
        {
            var c = coins[n];
            n = n + 1;
            c.Active = true;
            c.Gen = c.Gen + 1;
            c.SpawnX = -1.2f + Mod(i, 5) * 0.6f + Mod(i * 137, 23) * 0.01f;
            c.SpawnY = 0.75f + FloorDiv(i, 5) * 0.12f;
            c.SpawnZ = -1.15f + FloorDiv(i, 5) * 0.28f;
        }
        // tray はカーペット状に敷き詰める (3 列 x 6 枚 + 前縁ぎわ 4 枚)。
        // 密度があるほど 1 回の落下が前縁まで伝わり、序盤から払い出しが出る。
        for (int i = 0; i < 18; i++)
        {
            var c = coins[n];
            n = n + 1;
            c.Active = true;
            c.Gen = c.Gen + 1;
            c.SpawnX = -1.1f + Mod(i, 6) * 0.44f + Mod(i * 251, 17) * 0.01f;
            c.SpawnY = 0.25f + FloorDiv(i, 6) * 0.02f;
            c.SpawnZ = -0.42f + FloorDiv(i, 6) * 0.36f + Mod(i * 89, 13) * 0.01f;
        }
        for (int i = 0; i < 4; i++)
        {
            var c = coins[n];
            n = n + 1;
            c.Active = true;
            c.Gen = c.Gen + 1;
            c.SpawnX = -0.85f + i * 0.57f + Mod(i * 173, 11) * 0.01f;
            c.SpawnY = 0.22f;
            c.SpawnZ = 0.34f;
        }
    }

    // --- physics declaration -------------------------------------------------

    static void DeclareStatics(WorldRef3d world)
    {
        // Upper shelf the pusher slides on; coins get shoved off its front
        // edge (z = -0.8) down onto the lower tray.
        var shelf = Phys3d.Body(world, "shelf", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Static,
            Initial = new InitialState3d { X = 0.0f, Y = 0.3f, Z = -1.7f },
        });
        if (shelf == null) return;
        Phys3d.Box(shelf, "solid", new BoxDesc3d
        {
            Hx = 1.5f,
            Hy = 0.3f,
            Hz = 0.9f,
            Friction = 0.45f,
            Contact = true,
        });

        // Lower tray; its front edge (z = 0.5) is the payout drop. 浅くして
        // 山が前縁に届くまでのテンポを上げている。
        var tray = Phys3d.Body(world, "tray", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Static,
            Initial = new InitialState3d { X = 0.0f, Y = 0.05f, Z = -0.15f },
        });
        if (tray == null) return;
        Phys3d.Box(tray, "solid", new BoxDesc3d
        {
            Hx = 1.5f,
            Hy = 0.05f,
            Hz = 0.65f,
            Friction = 0.22f,
            Contact = true,
        });

        for (int i = 0; i < walls.Count; i++)
        {
            var w = walls[i];
            var body = Phys3d.Body(world, "wall:" + i, new BodyDesc3d
            {
                Type = Phys3d.BodyType.Static,
                Initial = new InitialState3d { X = w[0], Y = w[1], Z = w[2] },
            });
            if (body == null) continue;
            Phys3d.Box(body, "solid", new BoxDesc3d
            {
                Hx = w[3],
                Hy = w[4],
                Hz = w[5],
                Friction = 0.2f,
            });
        }
    }

    static float PusherZ(float t)
    {
        return -1.7f + 0.38f * (float)Math.Sin(t * 1.35f);
    }

    static void DeclarePusher(WorldRef3d world)
    {
        var pusher = Phys3d.Body(world, "pusher", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Kinematic,
            Initial = new InitialState3d { X = 0.0f, Y = 0.82f, Z = PusherZ(0.0f) },
        });
        if (pusher == null) return;
        Phys3d.Box(pusher, "solid", new BoxDesc3d
        {
            Hx = 1.45f,
            Hy = 0.22f,
            Hz = 0.55f,
            Friction = 0.7f,
            Contact = true,
        });
        Phys3d.SetTarget(pusher, new TargetDesc3d
        {
            X = 0.0f,
            Y = 0.82f,
            Z = PusherZ(frame * tickDt),
            TimeStep = tickDt,
        });
    }

    // 投入。プールが満杯なら最古のコインを再利用する = 何枚でも出せる。
    static void SpawnCoin(float x, int value)
    {
        Coin? slot = null;
        foreach (var c in coins)
        {
            if (!c.Active)
            {
                slot = c;
                break;
            }
        }
        if (slot == null)
        {
            foreach (var c in coins)
            {
                if (slot == null || c.Born < slot.Born)
                    slot = c;
            }
        }
        if (slot == null) return;
        slot.Active = true;
        slot.Gen = slot.Gen + 1;
        slot.Flash = 0;
        slot.Value = value;
        slot.Born = frame;
        slot.SpawnX = x;
        slot.SpawnY = dropY;
        slot.SpawnZ = dropZ;
    }

    static List<LiveCoin> DeclareCoins(WorldRef3d world)
    {
        var live = new List<LiveCoin>();
        for (int i = 0; i < maxCoins; i++)
        {
            var c = coins[i];
            if (!c.Active) continue;
            var body = Phys3d.Body(world, "coin:" + i, new BodyDesc3d
            {
                Type = Phys3d.BodyType.Dynamic,
                Version = c.Gen,
                Initial = new InitialState3d
                {
                    X = c.SpawnX,
                    Y = c.SpawnY,
                    Z = c.SpawnZ,
                    Euler = new Vec3d { X = 0.0f, Y = (i * 0.61803f) % 6.283f, Z = 0.0f },
                },
            });
            if (body == null) continue;
            Phys3d.Cylinder(body, "solid", new CylinderDesc3d
            {
                Version = c.Gen,
                Height = c.Value > 1 ? bonusH : coinH,
                Radius = c.Value > 1 ? bonusR : coinR,
                Sides = 20,
                Density = 1.0f,
                Friction = 0.22f,
                Contact = true,
            });
            live.Add(new LiveCoin(c, body, i));
        }
        return live;
    }

    // --- input ---------------------------------------------------------------
    // スクリーン x → 投入ライン (y=DROP_Y, z=DROP_Z) 上の world x。
    // world x=±1 を NDC へ射影して線形逆写像するので、カメラの向きに
    // 依らず「ポインタの真下」に投入される (左右反転しない)。
    static float ScreenToSpawnX(float px, Mat4 vp, float screenW)
    {
        var a = vp.MulVec4(new Vec4(-1.0f, dropY, dropZ, 1.0f));
        var b = vp.MulVec4(new Vec4(1.0f, dropY, dropZ, 1.0f));
        float na = a.X / a.W;
        float nb = b.X / b.W;
        float n = px / screenW * 2.0f - 1.0f;
        float t = (n - na) / (nb - na);
        return MathUtil.Clamp(-1.0f + 2.0f * t, -1.3f, 1.3f);
    }

    static void CaptureInput(Mat4 vp, float screenW)
    {
        // ポインタが動いたらマーカーを追従させる。キー操作 (画面基準:
        // このカメラでは world +X が画面左) への変換は render ごとに行う。
        bool mousePressed = Input.MousePressed();
        Input.MouseDelta(out var dx, out var dy);
        if (dx != 0 || dy != 0 || mousePressed)
        {
            Input.MousePos(out var mx, out _);
            spawnX = ScreenToSpawnX(mx, vp, screenW);
        }

        // 0 tick の render frame でも edge を失わないよう、回数で保持する。
        // 同じ render frame のクリック + スペースは従来どおり 1 回とする。
        if (mousePressed || Input.KeyPressed("space"))
            pendingSpawns = pendingSpawns + 1;
    }

    static void UpdateTickInput()
    {
        // held input は 60 Hz tick ごとに適用する。
        if (Input.KeyDown("left") || Input.KeyDown("a"))
            spawnX = MathUtil.Clamp(spawnX + 0.04f, -1.3f, 1.3f);
        if (Input.KeyDown("right") || Input.KeyDown("d"))
            spawnX = MathUtil.Clamp(spawnX - 0.04f, -1.3f, 1.3f);

        for (int i = 0; i < pendingSpawns; i++)
            SpawnCoin(spawnX, 1);
        if (pendingSpawns > 0)
            markerPulse = 8;
        pendingSpawns = 0;
    }

    static void Tick()
    {
        // 前 tick で開始した演出を 60 Hz で進める。この後に発生した
        // 投入パルスと払い出しフラッシュは、最初の描画で全強度になる。
        if (markerPulse > 0)
            markerPulse = markerPulse - 1;
        if (payoutFlash > 0)
            payoutFlash = payoutFlash - 1;

        var nextWorld = Phys3d.World("coin_pusher", new WorldOpts3d
        {
            Gravity = new Vec3d { X = 0.0f, Y = -10.0f, Z = 0.0f },
            FixedDt = tickDt,
            Substeps = 4,
            MaxSteps = 1,
        });
        if (nextWorld == null) return;
        world = nextWorld;
        Phys3d.Begin(nextWorld);

        DeclareStatics(nextWorld);
        DeclarePusher(nextWorld);
        UpdateTickInput();

        // 定期自動投入。デモ (と golden capture) が自走し、数枚に 1 枚の
        // 大型ボーナスコイン (5 点) が短期目標になる。
        if (Mod(frame, autoInterval) == 10)
        {
            autoCount = autoCount + 1;
            int value = Mod(autoCount, bonusEvery) == 0 ? 5 : 1;
            SpawnCoin(-1.0f + Mod(frame * 7919, 2000) / 1000.0f, value);
        }

        var live = DeclareCoins(nextWorld);
        renderCoinIndices = new List<int>();
        foreach (var entry in live)
            renderCoinIndices.Add(entry.Index);

        Phys3d.Step(nextWorld, tickDt);

        // Contact begin events light coins up for a few frames.
        foreach (var contact in Phys3d.Contacts(nextWorld, Phys3d.EventKind.Begin))
        {
            foreach (var entry in live)
            {
                var key = "coin:" + entry.Index;
                if (contact.A.Body == key || contact.B.Body == key)
                    entry.Coin.Flash = 10;
            }
        }
        // 従来の draw 直前と同じ順序で減衰させる。
        foreach (var entry in live)
        {
            if (entry.Coin.Flash > 0)
                entry.Coin.Flash = entry.Coin.Flash - 1;
        }

        // 台から落ちたコインの判定。前縁 (z > 0.45) から落ちたら払い出し、
        // 側溝など他の場所からこぼれたら没収 (スコアなし)。
        foreach (var entry in live)
        {
            var pose = Phys3d.Pose(entry.Body);
            if (pose == null) continue;
            if (pose.Y < -1.2f)
            {
                entry.Coin.Active = false;
                if (pose.Z > 0.45f)
                {
                    score = score + entry.Coin.Value;
                    int f = entry.Coin.Value > 1 ? 45 : 16;
                    if (f > payoutFlash)
                        payoutFlash = f;
                }
            }
        }

        frame = frame + 1;
    }

    // --- rendering -----------------------------------------------------------

    static Mat4 ModelMat(Pose3d pose, float sx, float sy, float sz)
    {
        var rot = new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw).ToMat4();
        return Mat4.Translate(new Vec3(pose.X, pose.Y, pose.Z)) * rot
            * Mat4.Scale(new Vec3(sx, sy, sz));
    }

    static Mat4 StaticModel(float x, float y, float z,
        float sx, float sy, float sz)
    {
        return Mat4.Translate(new Vec3(x, y, z)) * Mat4.Scale(new Vec3(sx, sy, sz));
    }

    // スコアを受け皿のコインの山として見せる。上段 = 10 点コイン (大)、
    // 下段 = 1 点コイン (小)。
    static void DrawScoreCoins(Renderer3d rn, Mesh3d cyl)
    {
        int tens = FloorDiv(score, 10);
        if (tens > 13)
            tens = 13;
        int units = Mod(score, 10);
        for (int i = 0; i < tens; i++)
        {
            rn.Draw(cyl, StaticModel(-1.25f + i * 0.21f, -0.62f, 1.08f, 0.14f, 0.05f, 0.14f),
                new Draw3dOpts { Tint = Color.Rgb(1.0f, 0.82f, 0.25f) });
        }
        for (int i = 0; i < units; i++)
        {
            rn.Draw(cyl, StaticModel(-1.25f + i * 0.19f, -0.68f, 1.45f, 0.09f, 0.035f, 0.09f),
                new Draw3dOpts { Tint = Color.Rgb(0.85f, 0.68f, 0.2f) });
        }
    }

    public static void OnFrame(float dt)
    {
        var rn = ren;
        var cube = cubeMesh;
        var cyl = cylMesh;
        if (rn == null || cube == null || cyl == null) return;
        if (!cube.Ready())
        {
            cube.Rebuild(Shapes3d.Cube());
            cyl.Rebuild(Shapes3d.Cylinder(24));
        }

        // ゲームセンターの暗がり + 筐体上の照明
        rn.Light.Dir = new Vec3(-0.25f, 1.0f, 0.5f);
        rn.Light.Intensity = 1.2f;
        rn.Sky.Top = Color.Rgb(0.32f, 0.35f, 0.44f);
        rn.Sky.Bottom = Color.Rgb(0.10f, 0.10f, 0.12f);
        rn.Sky.Intensity = 0.45f;
        rn.Background = Color.Rgb(0.035f, 0.045f, 0.06f);
        rn.Shadow.Center = new Vec3(0, 0, 0);
        rn.Shadow.Extent = 3.0f;
        rn.Begin(new Camera
        {
            Eye = new Vec3(0.0f, 3.3f, 4.4f),
            Target = new Vec3(0.0f, -0.35f, -0.35f),
            Fov = 44,
            Near = 0.1f,
            Far = 50.0f,
        });
        var vp = rn.ViewProj;
        if (vp == null) return;

        Gfx.Size(out var sw, out _);
        CaptureInput(vp, sw);
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => Tick());

        // --- draw ---
        var gray = Color.Rgb(0.42f, 0.45f, 0.5f);
        var dark = Color.Rgb(0.22f, 0.24f, 0.28f);
        var cabinet = Color.Rgb(0.16f, 0.17f, 0.21f);
        rn.Draw(cube, StaticModel(0.0f, 0.3f, -1.7f, 1.5f, 0.3f, 0.9f),
            new Draw3dOpts { Tint = gray });
        rn.Draw(cube, StaticModel(0.0f, 0.05f, -0.15f, 1.5f, 0.05f, 0.65f),
            new Draw3dOpts { Tint = gray });
        foreach (var w in walls)
        {
            rn.Draw(cube, StaticModel(w[0], w[1], w[2], w[3], w[4], w[5]),
                new Draw3dOpts { Tint = dark });
        }
        // 筐体 (描画のみ): 前面パネルと、スコアの山を置く受け皿。
        rn.Draw(cube, StaticModel(0.0f, -0.55f, 0.53f, 1.58f, 0.62f, 0.05f),
            new Draw3dOpts { Tint = cabinet });
        rn.Draw(cube, StaticModel(-0.82f, -0.78f, 1.28f, 0.8f, 0.05f, 0.42f),
            new Draw3dOpts { Tint = dark });

        var drawWorld = world;
        if (drawWorld != null)
        {
            var pusherPose = Phys3d.PoseByKey(drawWorld, "pusher");
            if (pusherPose != null)
            {
                rn.Draw(cube, ModelMat(pusherPose, 1.45f, 0.22f, 0.55f),
                    new Draw3dOpts { Tint = Color.Rgb(0.85f, 0.45f, 0.15f) });
            }

            foreach (var index in renderCoinIndices)
            {
                var coin = coins[index];
                var pose = Phys3d.PoseByKey(drawWorld, "coin:" + index);
                if (pose == null) continue;
                float hot = coin.Flash > 0 ? 0.25f : 0.0f;
                bool bonus = coin.Value > 1;
                var color = bonus
                    ? Color.Rgb(1.0f + hot, 0.9f + hot, 0.35f + hot)
                    : Color.Rgb(0.85f + hot, 0.68f + hot, 0.2f + hot);
                float r = bonus ? bonusR : coinR;
                float h = bonus ? bonusH : coinH;
                rn.Draw(cyl, ModelMat(pose, r, h, r),
                    new Draw3dOpts { Tint = color });
            }
        }

        // 払い出しの褒め演出: 前縁のバーが光る (HDR 高輝度で bloom に乗る)。
        if (payoutFlash > 0)
        {
            float k = payoutFlash / 45.0f;
            rn.Draw(cube, StaticModel(0.0f, 0.13f, 0.53f, 1.5f, 0.025f + 0.06f * k, 0.05f),
                new Draw3dOpts { Tint = Color.Rgb(1.4f, 1.3f, 0.7f + 0.6f * k) });
        }

        DrawScoreCoins(rn, cyl);

        // 投入マーカー: ポインタ追従のゴーストコイン。投入時にパルスする。
        float pulse = 1.0f + markerPulse * 0.07f;
        rn.Draw(cyl, StaticModel(spawnX, dropY, dropZ,
            coinR * pulse, coinH * pulse, coinR * pulse),
            new Draw3dOpts { Tint = Color.Rgb(0.55f, 0.78f, 0.95f, 0.55f), Blend = Gfx.Blend.Alpha });

        rn.End();
    }
}
