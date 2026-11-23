// Entry of lub's samples/18_coin_pusher.
// Run: lub samples/18_coin_pusher/CoinPusher18.csproj (transpile + watch + hot reload) Declares shelf/tray/pusher/coin every frame with the Phys3d immediate-mode API, and draws the poses after step with Renderer3d (lit + shadow + bloom).

using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public class Coin
{
    public bool Active;
    public int Gen;
    public int Flash;
    public int Value; // 1 = normal, 5 = bonus (large)
    public int Born; // Drop frame. When the pool is full, the oldest is reused
    public float SpawnX;
    public float SpawnY;
    public float SpawnZ;
}

/// <summary>One coin returned by declareCoins.
/// </summary>
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

// Coin pusher.
// Game structure:
// - The front 1/3 of the tray has open sides, and coins that spill off the sides are forfeited (risk).
//   Only coins dropped off the front edge pay out (return).
// - The score is shown as piles of coins stacked in the tray in front (tens + ones).
// - One in every few periodic drops is a large bonus coin (5 points).
// - Coins drop directly at the pointer position (click/tap = drop right below it).
//   Unlimited drops.
public static class CoinPusher18
{
    const float tickDt = 1.0f / 60.0f;
    const int maxCoins = 80;
    const float coinR = 0.17f;
    const float coinH = 0.07f;
    const float bonusR = 0.27f;
    const float bonusH = 0.1f;
    const int autoInterval = 75;
    const int bonusEvery = 6; // One in how many auto drops is a bonus
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

    // Walls {x, y, z, hx, hy, hz}.
    // Shared by physics and rendering.
    // The tray sides are open at the front (z > 0.4), and that is the gutter = the forfeit zone.
    static List<float[]> walls = new List<float[]>
    {
        new float[] { -1.58f, 0.5f, -1.7f, 0.08f, 0.5f, 0.9f }, // shelf side L
        new float[] { 1.58f, 0.5f, -1.7f, 0.08f, 0.5f, 0.9f }, // shelf side R
        new float[] { -1.58f, 0.28f, -0.35f, 0.08f, 0.28f, 0.45f }, // tray side L (open at the front)
        new float[] { 1.58f, 0.28f, -0.35f, 0.08f, 0.28f, 0.45f }, // tray side R (open at the front)
        new float[] { 0.0f, 0.8f, -2.68f, 1.5f, 0.8f, 0.08f }, // back
    };

    // --- procedural unit meshes (Shapes3d) -----------------------------------
    static Mesh3d? cubeMesh = null;
    static Mesh3d? cylMesh = null;
    static Renderer3d? ren = null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        App.Config(new ConfigOpts { Backend = backend, Width = 640, Height = 360 });
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

    // On an empty table nothing happens for the first few minutes, so the table is filled at startup.
    // For determinism, the placement uses fixed pseudo-random numbers.
    static void PrefillTable()
    {
        int n = 0;
        // 2 rows on the shelf (in front of the pusher)
        for (int i = 0; i < 10; i++)
        {
            var c = coins[n];
            n = n + 1;
            c.Active = true;
            c.Gen = c.Gen + 1;
            c.SpawnX = -1.2f + (i % 5) * 0.6f + (i * 137 % 23) * 0.01f;
            c.SpawnY = 0.75f + (i / 5) * 0.12f;
            c.SpawnZ = -1.15f + (i / 5) * 0.28f;
        }
        // The tray is covered like a carpet (3 rows x 6 coins + 4 near the front edge).
        // The denser it is, the more a single drop propagates to the front edge, so payouts happen from early on.
        for (int i = 0; i < 18; i++)
        {
            var c = coins[n];
            n = n + 1;
            c.Active = true;
            c.Gen = c.Gen + 1;
            c.SpawnX = -1.1f + (i % 6) * 0.44f + (i * 251 % 17) * 0.01f;
            c.SpawnY = 0.25f + (i / 6) * 0.02f;
            c.SpawnZ = -0.42f + (i / 6) * 0.36f + (i * 89 % 13) * 0.01f;
        }
        for (int i = 0; i < 4; i++)
        {
            var c = coins[n];
            n = n + 1;
            c.Active = true;
            c.Gen = c.Gen + 1;
            c.SpawnX = -0.85f + i * 0.57f + (i * 173 % 11) * 0.01f;
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

        // Lower tray; its front edge (z = 0.5) is the payout drop. It is made shallow to speed up the tempo until the pile reaches the front edge.
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

    // Drop.
    // If the pool is full, the oldest coin is reused = any number of coins can be dropped.
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

    // --- input --------------------------------------------------------------- Screen x → world x on the drop line (y=DROP_Y, z=DROP_Z).
    // Projects world x=±1 to NDC and inverts linearly, so coins drop "right below the pointer" regardless of the camera orientation (no left-right flip).
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
        // Make the marker follow the pointer when it moves.
        // Conversion into key operations (screen-relative:
        // with this camera, world +X is screen left) is done every render.
        bool mousePressed = Input.MousePressed();
        Input.MouseDelta(out var dx, out var dy);
        if (dx != 0 || dy != 0 || mousePressed)
        {
            Input.MousePos(out var mx, out _);
            spawnX = ScreenToSpawnX(mx, vp, screenW);
        }

        // Keep it as a count so the edge is not lost even on a render frame with 0 ticks.
        // A click + space in the same render frame counts as 1, as before.
        if (mousePressed || Input.KeyPressed("space"))
            pendingSpawns = pendingSpawns + 1;
    }

    static void UpdateTickInput()
    {
        // held input is applied every 60 Hz tick.
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
        // Advance the effects started in the previous tick at 60 Hz.
        // Drop pulses and payout flashes that occur after this are at full strength on the first draw.
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

        // Periodic auto drop.
        // The demo (and golden capture) runs by itself, and the large bonus coin (5 points), one in every few, becomes a short-term goal.
        if (frame % autoInterval == 10)
        {
            autoCount = autoCount + 1;
            int value = autoCount % bonusEvery == 0 ? 5 : 1;
            SpawnCoin(-1.0f + (frame * 7919 % 2000) / 1000.0f, value);
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
        // Decay in the same order as before, when it ran just before draw.
        foreach (var entry in live)
        {
            if (entry.Coin.Flash > 0)
                entry.Coin.Flash = entry.Coin.Flash - 1;
        }

        // Detection of coins that fell off the table.
        // Falling from the front edge (z > 0.45) pays out, and spilling from anywhere else, such as the gutter, is forfeited (no score).
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

    static Mat4 ModelMat(Pose3d pose, float sx, float sy, float sz) =>
        Renderer3d.PoseMat(pose) * Mat4.Scale(new Vec3(sx, sy, sz));

    static Mat4 StaticModel(float x, float y, float z,
        float sx, float sy, float sz)
    {
        return Mat4.Translate(new Vec3(x, y, z)) * Mat4.Scale(new Vec3(sx, sy, sz));
    }

    // Show the score as piles of coins in the tray.
    // Top row = 10-point coins (large), bottom row = 1-point coins (small).
    static void DrawScoreCoins(Renderer3d rn, Mesh3d cyl)
    {
        int tens = score / 10;
        if (tens > 13)
            tens = 13;
        int units = score % 10;
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

        // Arcade darkness + lighting above the cabinet
        rn.Light.Dir = new Vec3(-0.25f, 1.0f, 0.5f);
        rn.Light.Intensity = 1.2f;
        rn.Sky.Top = Color.Rgb(0.32f, 0.35f, 0.44f);
        rn.Sky.Bottom = Color.Rgb(0.10f, 0.10f, 0.12f);
        rn.Sky.Intensity = 0.45f;
        rn.Background = Color.Rgb(0.035f, 0.045f, 0.06f);
        rn.Shadow.Center = new Vec3(0, 0, 0);
        rn.Shadow.Extent = 3.0f;
        // Smaller than the default, to keep the contact shadows of thin coins.
        rn.Shadow.Bias = 0.0001f;
        rn.Begin(new Camera
        {
            Eye = new Vec3(0.0f, 3.3f, 4.4f),
            Target = new Vec3(0.0f, -0.35f, -0.35f),
            Fov = 44,
            Near = 0.1f,
            Far = 50.0f,
        });
        if (rn.ViewProj == null) return;
        var vp = rn.ViewProj.Value;

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
        // Cabinet (render only): the front panel, and the tray holding the score piles.
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

        // Payout reward effect: the front-edge bar glows (high HDR brightness, so it picks up bloom).
        if (payoutFlash > 0)
        {
            float k = payoutFlash / 45.0f;
            rn.Draw(cube, StaticModel(0.0f, 0.13f, 0.53f, 1.5f, 0.025f + 0.06f * k, 0.05f),
                new Draw3dOpts { Tint = Color.Rgb(1.4f, 1.3f, 0.7f + 0.6f * k) });
        }

        DrawScoreCoins(rn, cyl);

        // Drop marker: a ghost coin that follows the pointer.
        // Pulses on drop.
        float pulse = 1.0f + markerPulse * 0.07f;
        rn.Draw(cyl, StaticModel(spawnX, dropY, dropZ,
            coinR * pulse, coinH * pulse, coinR * pulse),
            new Draw3dOpts { Tint = Color.Rgb(0.55f, 0.78f, 0.95f, 0.55f), Blend = Blends.Alpha });

        rn.End();
    }
}
