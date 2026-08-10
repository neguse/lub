// lub の samples/18_coin_pusher (Haxe 版 CoinPusher18.hx) の TinyC# 版 entry。
// 実行: lub samples/18_coin_pusher/CoinPusher18.csproj (transpile + watch + hot reload)
// Phys3d の即時モード API で shelf/tray/pusher/coin を毎フレーム宣言し、
// step 後の pose を Renderer3d (lit + shadow + bloom) で描く。
// gameplay rule と物理 desc の数値は Haxe 版に忠実。tcs 制約による置換:
// typedef Coin → class Coin、匿名 {coin, body, index} → class LiveCoin、
// 整数除算・剰余 → FloorDiv/Mod (float 経由、対象は非負)、
// Mesh3d/Renderer3d は static 初期化子で作れないため onInit で作る。

using System;
using System.Collections.Generic;

public class Coin
{
    public bool active;
    public int gen;
    public int flash;
    public int value; // 1 = 通常, 5 = ボーナス (大型)
    public int born; // 投入フレーム。プール満杯時は最古を再利用する
    public float spawnX;
    public float spawnY;
    public float spawnZ;
}

/// <summary>declareCoins が返す 1 コイン分 (Haxe 版の匿名構造体相当)。</summary>
public class LiveCoin
{
    public Coin coin;
    public BodyRef3d body;
    public int index;

    public LiveCoin(Coin coin, BodyRef3d body, int index)
    {
        this.coin = coin;
        this.body = body;
        this.index = index;
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
    const float DT = 1.0f / 60.0f;
    const int MAX_COINS = 80;
    const float COIN_R = 0.17f;
    const float COIN_H = 0.07f;
    const float BONUS_R = 0.27f;
    const float BONUS_H = 0.1f;
    const int AUTO_INTERVAL = 75;
    const int BONUS_EVERY = 6; // 自動投入の何枚に 1 枚がボーナスか
    const float DROP_Y = 1.35f;
    const float DROP_Z = -1.0f;

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
    static List<float[]> WALLS = new List<float[]>
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

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend, width = 640, height = 360 });
        cubeMesh = new Mesh3d("cp_cube");
        cylMesh = new Mesh3d("cp_cyl");
        ren = new Renderer3d("cp18");
        for (int i = 0; i < MAX_COINS; i++)
        {
            coins.Add(new Coin
            {
                active = false,
                gen = 0,
                flash = 0,
                value = 1,
                born = 0,
                spawnX = 0.0f,
                spawnY = DROP_Y,
                spawnZ = DROP_Z,
            });
        }
        PrefillTable();
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
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
            c.active = true;
            c.gen = c.gen + 1;
            c.spawnX = -1.2f + Mod(i, 5) * 0.6f + Mod(i * 137, 23) * 0.01f;
            c.spawnY = 0.75f + FloorDiv(i, 5) * 0.12f;
            c.spawnZ = -1.15f + FloorDiv(i, 5) * 0.28f;
        }
        // tray はカーペット状に敷き詰める (3 列 x 6 枚 + 前縁ぎわ 4 枚)。
        // 密度があるほど 1 回の落下が前縁まで伝わり、序盤から払い出しが出る。
        for (int i = 0; i < 18; i++)
        {
            var c = coins[n];
            n = n + 1;
            c.active = true;
            c.gen = c.gen + 1;
            c.spawnX = -1.1f + Mod(i, 6) * 0.44f + Mod(i * 251, 17) * 0.01f;
            c.spawnY = 0.25f + FloorDiv(i, 6) * 0.02f;
            c.spawnZ = -0.42f + FloorDiv(i, 6) * 0.36f + Mod(i * 89, 13) * 0.01f;
        }
        for (int i = 0; i < 4; i++)
        {
            var c = coins[n];
            n = n + 1;
            c.active = true;
            c.gen = c.gen + 1;
            c.spawnX = -0.85f + i * 0.57f + Mod(i * 173, 11) * 0.01f;
            c.spawnY = 0.22f;
            c.spawnZ = 0.34f;
        }
    }

    // --- physics declaration -------------------------------------------------

    static void DeclareStatics(WorldRef3d world)
    {
        // Upper shelf the pusher slides on; coins get shoved off its front
        // edge (z = -0.8) down onto the lower tray.
        var shelf = Phys3d.phys3d_body(world, "shelf", new BodyDesc3d
        {
            type = Phys3d.STATIC,
            initial = new InitialState3d { x = 0.0f, y = 0.3f, z = -1.7f },
        });
        if (shelf == null) return;
        Phys3d.phys3d_box(shelf, "solid", new BoxDesc3d
        {
            hx = 1.5f,
            hy = 0.3f,
            hz = 0.9f,
            friction = 0.45f,
            contact = true,
        });

        // Lower tray; its front edge (z = 0.5) is the payout drop. 浅くして
        // 山が前縁に届くまでのテンポを上げている。
        var tray = Phys3d.phys3d_body(world, "tray", new BodyDesc3d
        {
            type = Phys3d.STATIC,
            initial = new InitialState3d { x = 0.0f, y = 0.05f, z = -0.15f },
        });
        if (tray == null) return;
        Phys3d.phys3d_box(tray, "solid", new BoxDesc3d
        {
            hx = 1.5f,
            hy = 0.05f,
            hz = 0.65f,
            friction = 0.22f,
            contact = true,
        });

        for (int i = 0; i < WALLS.Count; i++)
        {
            var w = WALLS[i];
            var body = Phys3d.phys3d_body(world, "wall:" + i, new BodyDesc3d
            {
                type = Phys3d.STATIC,
                initial = new InitialState3d { x = w[0], y = w[1], z = w[2] },
            });
            if (body == null) continue;
            Phys3d.phys3d_box(body, "solid", new BoxDesc3d
            {
                hx = w[3],
                hy = w[4],
                hz = w[5],
                friction = 0.2f,
            });
        }
    }

    static float PusherZ(float t)
    {
        return -1.7f + 0.38f * (float)Math.Sin(t * 1.35f);
    }

    static void DeclarePusher(WorldRef3d world)
    {
        var pusher = Phys3d.phys3d_body(world, "pusher", new BodyDesc3d
        {
            type = Phys3d.KINEMATIC,
            initial = new InitialState3d { x = 0.0f, y = 0.82f, z = PusherZ(0.0f) },
        });
        if (pusher == null) return;
        Phys3d.phys3d_box(pusher, "solid", new BoxDesc3d
        {
            hx = 1.45f,
            hy = 0.22f,
            hz = 0.55f,
            friction = 0.7f,
            contact = true,
        });
        Phys3d.phys3d_set_target(pusher, new TargetDesc3d
        {
            x = 0.0f,
            y = 0.82f,
            z = PusherZ(frame * DT),
            dt = DT,
        });
    }

    // 投入。プールが満杯なら最古のコインを再利用する = 何枚でも出せる。
    static void SpawnCoin(float x, int value)
    {
        Coin? slot = null;
        foreach (var c in coins)
        {
            if (!c.active)
            {
                slot = c;
                break;
            }
        }
        if (slot == null)
        {
            foreach (var c in coins)
            {
                if (slot == null || c.born < slot.born)
                    slot = c;
            }
        }
        if (slot == null) return;
        slot.active = true;
        slot.gen = slot.gen + 1;
        slot.flash = 0;
        slot.value = value;
        slot.born = frame;
        slot.spawnX = x;
        slot.spawnY = DROP_Y;
        slot.spawnZ = DROP_Z;
    }

    static List<LiveCoin> DeclareCoins(WorldRef3d world)
    {
        var live = new List<LiveCoin>();
        for (int i = 0; i < MAX_COINS; i++)
        {
            var c = coins[i];
            if (!c.active) continue;
            var body = Phys3d.phys3d_body(world, "coin:" + i, new BodyDesc3d
            {
                type = Phys3d.DYNAMIC,
                version = c.gen,
                initial = new InitialState3d
                {
                    x = c.spawnX,
                    y = c.spawnY,
                    z = c.spawnZ,
                    euler = new Vec3d { x = 0.0f, y = (i * 0.61803f) % 6.283f, z = 0.0f },
                },
            });
            if (body == null) continue;
            Phys3d.phys3d_cylinder(body, "solid", new CylinderDesc3d
            {
                version = c.gen,
                height = c.value > 1 ? BONUS_H : COIN_H,
                radius = c.value > 1 ? BONUS_R : COIN_R,
                sides = 20,
                density = 1.0f,
                friction = 0.22f,
                contact = true,
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
        var a = vp.mulVec4(new Vec4(-1.0f, DROP_Y, DROP_Z, 1.0f));
        var b = vp.mulVec4(new Vec4(1.0f, DROP_Y, DROP_Z, 1.0f));
        float na = a.x / a.w;
        float nb = b.x / b.w;
        float n = px / screenW * 2.0f - 1.0f;
        float t = (n - na) / (nb - na);
        return MathUtil.clamp(-1.0f + 2.0f * t, -1.3f, 1.3f);
    }

    static void CaptureInput(Mat4 vp, float screenW)
    {
        // ポインタが動いたらマーカーを追従させる。キー操作 (画面基準:
        // このカメラでは world +X が画面左) への変換は render ごとに行う。
        bool mousePressed = Input.mouse_pressed();
        Input.mouse_delta(out var dx, out var dy);
        if (dx != 0 || dy != 0 || mousePressed)
        {
            Input.mouse_pos(out var mx, out _);
            spawnX = ScreenToSpawnX(mx, vp, screenW);
        }

        // 0 tick の render frame でも edge を失わないよう、回数で保持する。
        // 同じ render frame のクリック + スペースは従来どおり 1 回とする。
        if (mousePressed || Input.key_pressed("space"))
            pendingSpawns = pendingSpawns + 1;
    }

    static void UpdateTickInput()
    {
        // held input は 60 Hz tick ごとに適用する。
        if (Input.key_down("left") || Input.key_down("a"))
            spawnX = MathUtil.clamp(spawnX + 0.04f, -1.3f, 1.3f);
        if (Input.key_down("right") || Input.key_down("d"))
            spawnX = MathUtil.clamp(spawnX - 0.04f, -1.3f, 1.3f);

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

        var nextWorld = Phys3d.phys3d_world("coin_pusher", new WorldOpts3d
        {
            gravity = new Vec3d { x = 0.0f, y = -10.0f, z = 0.0f },
            fixedDt = DT,
            substeps = 4,
            maxSteps = 1,
        });
        if (nextWorld == null) return;
        world = nextWorld;
        Phys3d.phys3d_begin(nextWorld);

        DeclareStatics(nextWorld);
        DeclarePusher(nextWorld);
        UpdateTickInput();

        // 定期自動投入。デモ (と golden capture) が自走し、数枚に 1 枚の
        // 大型ボーナスコイン (5 点) が短期目標になる。
        if (Mod(frame, AUTO_INTERVAL) == 10)
        {
            autoCount = autoCount + 1;
            int value = Mod(autoCount, BONUS_EVERY) == 0 ? 5 : 1;
            SpawnCoin(-1.0f + Mod(frame * 7919, 2000) / 1000.0f, value);
        }

        var live = DeclareCoins(nextWorld);
        renderCoinIndices = new List<int>();
        foreach (var entry in live)
            renderCoinIndices.Add(entry.index);

        Phys3d.phys3d_step(nextWorld, DT);

        // Contact begin events light coins up for a few frames.
        foreach (var contact in Phys3d.phys3d_contacts(nextWorld, "begin"))
        {
            foreach (var entry in live)
            {
                var key = "coin:" + entry.index;
                if (contact.a.body == key || contact.b.body == key)
                    entry.coin.flash = 10;
            }
        }
        // 従来の draw 直前と同じ順序で減衰させる。
        foreach (var entry in live)
        {
            if (entry.coin.flash > 0)
                entry.coin.flash = entry.coin.flash - 1;
        }

        // 台から落ちたコインの判定。前縁 (z > 0.45) から落ちたら払い出し、
        // 側溝など他の場所からこぼれたら没収 (スコアなし)。
        foreach (var entry in live)
        {
            var pose = Phys3d.phys3d_pose(entry.body);
            if (pose == null) continue;
            if (pose.y < -1.2f)
            {
                entry.coin.active = false;
                if (pose.z > 0.45f)
                {
                    score = score + entry.coin.value;
                    int f = entry.coin.value > 1 ? 45 : 16;
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
        var rot = new Quat(pose.qx, pose.qy, pose.qz, pose.qw).toMat4();
        return Mat4.translate(new Vec3(pose.x, pose.y, pose.z)) * rot
            * Mat4.scale(new Vec3(sx, sy, sz));
    }

    static Mat4 StaticModel(float x, float y, float z,
        float sx, float sy, float sz)
    {
        return Mat4.translate(new Vec3(x, y, z)) * Mat4.scale(new Vec3(sx, sy, sz));
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
            rn.draw(cyl, StaticModel(-1.25f + i * 0.21f, -0.62f, 1.08f, 0.14f, 0.05f, 0.14f),
                new Draw3dOpts { tint = Color.rgb(1.0f, 0.82f, 0.25f) });
        }
        for (int i = 0; i < units; i++)
        {
            rn.draw(cyl, StaticModel(-1.25f + i * 0.19f, -0.68f, 1.45f, 0.09f, 0.035f, 0.09f),
                new Draw3dOpts { tint = Color.rgb(0.85f, 0.68f, 0.2f) });
        }
    }

    public static void onFrame(float dt)
    {
        var rn = ren;
        var cube = cubeMesh;
        var cyl = cylMesh;
        if (rn == null || cube == null || cyl == null) return;
        if (!cube.ready())
        {
            cube.rebuild(Shapes3d.cube());
            cyl.rebuild(Shapes3d.cylinder(24));
        }

        // ゲームセンターの暗がり + 筐体上の照明
        rn.light.dir = new Vec3(-0.25f, 1.0f, 0.5f);
        rn.light.intensity = 1.2f;
        rn.sky.top = Color.rgb(0.32f, 0.35f, 0.44f);
        rn.sky.bottom = Color.rgb(0.10f, 0.10f, 0.12f);
        rn.sky.intensity = 0.45f;
        rn.background = Color.rgb(0.035f, 0.045f, 0.06f);
        rn.shadow.center = new Vec3(0, 0, 0);
        rn.shadow.extent = 3.0f;
        rn.begin(new Camera
        {
            eye = new Vec3(0.0f, 3.3f, 4.4f),
            target = new Vec3(0.0f, -0.35f, -0.35f),
            fov = 44,
            near = 0.1f,
            far = 50.0f,
        });
        var vp = rn.viewProj;
        if (vp == null) return;

        Gfx.size(out var sw, out _);
        CaptureInput(vp, sw);
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.frame(dt, _ => Tick());

        // --- draw ---
        var gray = Color.rgb(0.42f, 0.45f, 0.5f);
        var dark = Color.rgb(0.22f, 0.24f, 0.28f);
        var cabinet = Color.rgb(0.16f, 0.17f, 0.21f);
        rn.draw(cube, StaticModel(0.0f, 0.3f, -1.7f, 1.5f, 0.3f, 0.9f),
            new Draw3dOpts { tint = gray });
        rn.draw(cube, StaticModel(0.0f, 0.05f, -0.15f, 1.5f, 0.05f, 0.65f),
            new Draw3dOpts { tint = gray });
        foreach (var w in WALLS)
        {
            rn.draw(cube, StaticModel(w[0], w[1], w[2], w[3], w[4], w[5]),
                new Draw3dOpts { tint = dark });
        }
        // 筐体 (描画のみ): 前面パネルと、スコアの山を置く受け皿。
        rn.draw(cube, StaticModel(0.0f, -0.55f, 0.53f, 1.58f, 0.62f, 0.05f),
            new Draw3dOpts { tint = cabinet });
        rn.draw(cube, StaticModel(-0.82f, -0.78f, 1.28f, 0.8f, 0.05f, 0.42f),
            new Draw3dOpts { tint = dark });

        var drawWorld = world;
        if (drawWorld != null)
        {
            var pusherPose = Phys3d.phys3d_pose(drawWorld, "pusher");
            if (pusherPose != null)
            {
                rn.draw(cube, ModelMat(pusherPose, 1.45f, 0.22f, 0.55f),
                    new Draw3dOpts { tint = Color.rgb(0.85f, 0.45f, 0.15f) });
            }

            foreach (var index in renderCoinIndices)
            {
                var coin = coins[index];
                var pose = Phys3d.phys3d_pose(drawWorld, "coin:" + index);
                if (pose == null) continue;
                float hot = coin.flash > 0 ? 0.25f : 0.0f;
                bool bonus = coin.value > 1;
                var color = bonus
                    ? Color.rgb(1.0f + hot, 0.9f + hot, 0.35f + hot)
                    : Color.rgb(0.85f + hot, 0.68f + hot, 0.2f + hot);
                float r = bonus ? BONUS_R : COIN_R;
                float h = bonus ? BONUS_H : COIN_H;
                rn.draw(cyl, ModelMat(pose, r, h, r),
                    new Draw3dOpts { tint = color });
            }
        }

        // 払い出しの褒め演出: 前縁のバーが光る (HDR 高輝度で bloom に乗る)。
        if (payoutFlash > 0)
        {
            float k = payoutFlash / 45.0f;
            rn.draw(cube, StaticModel(0.0f, 0.13f, 0.53f, 1.5f, 0.025f + 0.06f * k, 0.05f),
                new Draw3dOpts { tint = Color.rgb(1.4f, 1.3f, 0.7f + 0.6f * k) });
        }

        DrawScoreCoins(rn, cyl);

        // 投入マーカー: ポインタ追従のゴーストコイン。投入時にパルスする。
        float pulse = 1.0f + markerPulse * 0.07f;
        rn.draw(cyl, StaticModel(spawnX, DROP_Y, DROP_Z,
            COIN_R * pulse, COIN_H * pulse, COIN_R * pulse),
            new Draw3dOpts { tint = Color.rgb(0.55f, 0.78f, 0.95f, 0.55f), blend = Gfx.ALPHA });

        rn.End();
    }
}
