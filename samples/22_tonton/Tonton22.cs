// Entry for lub's samples/22_tonton.
// Run: lub samples/22_tonton/Tonton22.csproj (transpile + watch + hot reload)
//
// Tonton sumo: paper sumo between AI wrestlers.
// The human only taps (tonton) the ring.
// A demo that shows the three layers of game AI in a single bout:
// - L0 feedback control: a wrestler is one capsule rigid body + posture PD (inverted pendulum).
//   It has a muscle strength limit (balMax), and beyond the critical angle it really falls over.
//   The ring is also a dynamic cylinder with its own PD suspension, and the disturbance from a tonton (a downward
//   impulse at the clicked point) reaches the wrestlers through contacts.
// - L1 steering: a velocity servo that closes in on the opponent.
//   While pushing in, forward motion is not
//   braked = the risk of lunging forward when the opponent disappears is carried by the physics.
// - L2 tactics: pushing against each other is a stable structure where "the opponent is the support", so pushing alone does not
//   decide the bout. The bout moves the moment the support is removed by a pull or an inashi. Differences in personality (counter
//   probability, lean depth) become differences in fighting style.
//
// Wrestlers are class Rikishi, the return value of screenPos is class ScreenPos, and the anonymous desc structs of Phys3d are the desc classes of lub_stub.
// end() is End(), char() is Char().

using System;
using System.Collections.Generic;
using Lub;
using Lubx;

/// <summary>One wrestler.
/// </summary>
public class Rikishi
{
    public int Gen; // version for redeclaration (respawn)
    public string Name = ""; // Ring name (shikona, in kana)
    public float HomeX;
    public float[] Color = new float[] { 1.0f, 1.0f, 1.0f, 1.0f };
    public int BodyRgb; // Body color baked into the SDF
    public int DownFrames;
    public int SquashT; // Remaining frames of the landing squash
    public float PrevVy;
    // --- Personality --- Pushing against each other is a stable structure where "the opponent is the support", so pushing alone never topples anyone.
    // The bout is decided at the moment the support is removed (pull / inashi).
    // Personality is how each wrestler mixes these.
    public float PushK; // Push strength
    public float LeanK; // Lean depth (risk: cannot recover if the support is removed)
    public float PulseHz; // Pulse period of the push
    public float Phase;
    public float Pulse; // Pulse waveform (0..1). Tick sets it, and the push force and rendering read the same value
    public float Counter; // Probability of answering a deep lean of the opponent with a pull/inashi
    // --- Tactical state ---
    public int Tactic;
    public int TacticUntil;
    public float SideSign; // Direction to circle around for inashi
}

/// <summary>world coordinates → logical screen coordinates.
/// </summary>
public class ScreenPos
{
    public float X;
    public float Y;
    public bool Ok;
}

public static class Tonton22
{
    const int w = 640;
    const int h = 360;
    const float tickDt = 1.0f / 60.0f;

    const float dohyoR = 2.2f;
    const float dohyoH = 0.4f;
    const float dohyoY = 0.6f; // Center height while suspended
    const float topY = dohyoY + dohyoH * 0.5f;
    const float capR = 0.35f;

    // Mass and tilt inertia of the ring (analytic values for a cylinder).
    // Used to write the suspension PD gains as "resonance frequency Hz and damping ratio".
    const float dohyoMass = 3.14159f * dohyoR * dohyoR * dohyoH;
    const float dohyoITilt =
        dohyoMass * (3.0f * dohyoR * dohyoR + dohyoH * dohyoH) / 12.0f;

    // --- Tuning parameters (tweaked with hot reload) ------------------------------
    static float suspLinHz = 3.0f; // Ring suspension spring (vertical / horizontal return)
    static float suspLinDamp = 0.15f; // Damping ratio. The smaller it is, the more a tonton bounces
    static float suspAngHz = 1.4f; // Tilt return
    static float suspAngDamp = 0.2f;
    static float balKp = 6.0f; // Posture PD: righting torque
    static float balKd = 1.2f; // Posture PD: angular velocity damping
    // Muscle strength limit.
    // Beyond the angle (≈35°) where the gravity toppling torque (≈2.7 sinθ) exceeds this, it really falls over.
    // Infinitely strong balance would not make sumo.
    // The thinner the margin between the push lean (15-25°) and the critical angle, the more the tonton and the pulse become decisive.
    static float balMax = 1.55f;
    static float holdK = 1.5f; // Home position spring during shikiri
    static float holdKd = 0.8f;
    static float walkSpeed = 0.9f; // Speed of closing in on the opponent (m/s)
    static float seekK = 1.5f; // Strength of the velocity servo
    static float tapImpulse = 12.0f; // Strength of one tonton
    static int tapRepeat = 9; // Interval of repeated taps while held down (frame)

    // --- Bout flow ---------------------------------------------------------
    const int stShikiri = 0; // Return to the shikiri position and take a breath
    const int stFight = 1; // The bout (accepts tonton)
    const int stKimari = 2; // Aftermath of the decision
    static int state = stShikiri;
    static int stateT = 45;
    static int winner = -1;
    static int[] stars = new int[] { 0, 0 }; // Win-loss record (stars)
    static string kimarite = ""; // Winning technique (kimarite, in kana)
    static int fightStart = 0; // Frame when ST_FIGHT was entered
    static bool engagedPrev = false; // Edge detection for the collision sound at the tachiai

    static int frame = 0;
    // Tactics
    const int taOsu = 0; // Osu (push): lean forward and push in (safe while there is support)
    const int taHiki = 1; // Hiki (pull): remove the support and drop the leaning opponent
    const int taInashi = 2; // Inashi (deflect): dodge sideways and make the opponent miss
    const int taTame = 3; // Tame (hold): stand upright and endure
    static float hikiK = 5.0f; // Backward force of the pull
    static float inashiK = 5.5f; // Sideways force of inashi
    static float hatakiK = 3.2f; // Hatakikomi (torque that pulls the opponent's upper body down while pulling back)

    // Red = all-out charge (pushes hard and deep, but is easily caught by a pull) Blue = go no sen (pushes moderately, answers the opponent's lean with a pull/inashi)
    static List<Rikishi> fighters = new List<Rikishi>
    {
        new Rikishi
        {
            Gen = 1,
            Name = "あか",
            HomeX = -0.9f,
            Color = new float[] { 0.86f, 0.28f, 0.24f, 1.0f },
            BodyRgb = 0xC94434,
            DownFrames = 0,
            SquashT = 0,
            PrevVy = 0.0f,
            PushK = 9.0f,
            LeanK = 2.0f,
            PulseHz = 2.2f,
            Phase = 0.0f,
            Counter = 0.25f,
            Tactic = taOsu,
            TacticUntil = 0,
            SideSign = 1.0f,
        },
        new Rikishi
        {
            Gen = 1,
            Name = "あお",
            HomeX = 0.9f,
            Color = new float[] { 0.27f, 0.47f, 0.88f, 1.0f },
            BodyRgb = 0x3E6ED8,
            DownFrames = 0,
            SquashT = 0,
            PrevVy = 0.0f,
            PushK = 7.5f,
            LeanK = 1.2f,
            PulseHz = 1.6f,
            Phase = 2.1f,
            Counter = 0.7f,
            Tactic = taOsu,
            TacticUntil = 0,
            SideSign = -1.0f,
        },
    };

    // LUB_TONTON_AUTO=1 enables automatic tonton (for headless verification and self-running demos)
    static bool auto = Environment.GetEnvironmentVariable("LUB_TONTON_AUTO") != null;

    // Visual feedback for tonton
    static int lastTap = -999;
    static float markerX = 0.0f;
    static float markerY = 0.0f;
    static float markerZ = 0.0f;
    static int markerT = 0;
    static float shake = 0.0f;
    static FixedStep? step = null;
    static WorldRef3d? world = null;
    static int renderFrame = 0;
    static Vec3? renderEye = null;

    // Real input taken on each render.
    // pressed is kept together with its position until the next tick.
    static bool pendingTap = false;
    static float pendingTapX = 0.0f;
    static float pendingTapY = 0.0f;
    static bool pointerDown = false;
    static float pointerX = 0.0f;
    static float pointerY = 0.0f;

    // --- procedural meshes (Shapes3d).
    // Created lazily in onFrame --------------------
    static Mesh3d? cubeMesh = null;
    static Mesh3d? cylMesh = null;
    static Renderer3d? ren = null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        App.Config(new ConfigOpts { Backend = backend, Width = w, Height = h });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    // --- SDF daruma wrestlers ----------------------------------------------------- Meshed from an SDF tree with bones.
    // Bakes two of them that differ only in body color.
    // The model is built facing -Z (toward the opponent).
    // feet are at y=0.
    const int sdfN = 56;
    static Mesh3d[]? darumaMesh = null;
    // native watch resets it to the initial value true by re-running the chunk, and web (module mode) sets it in OnReload.
    // Both trigger a remesh.
    static bool darumaDirty = true;

    public static void OnReload()
    {
        darumaDirty = true;
    }

    static SdfNode DarumaModel(int bodyRgb)
    {
        var body = Sdf.Sphere(0.50f).Move(0, 0.52f, 0)
            .Bone("body", new Vec3(0, 0.25f, 0));
        var head = Sdf.Sphere(0.30f).Move(0, 1.02f, 0)
            .Bone("head", new Vec3(0, 0.80f, 0));
        var armL = Sdf.Capsule(new Vec3(0.40f, 0.76f, -0.06f),
            new Vec3(0.60f, 0.40f, -0.30f), 0.11f)
            .Bone("arm_l", new Vec3(0.40f, 0.76f, -0.06f));
        var armR = Sdf.Capsule(new Vec3(-0.40f, 0.76f, -0.06f),
            new Vec3(-0.60f, 0.40f, -0.30f), 0.11f)
            .Bone("arm_r", new Vec3(-0.40f, 0.76f, -0.06f));
        var trunk = body.Smin(head, 0.12f).Smin(armL, 0.06f).Smin(armR, 0.06f)
            .Paint(bodyRgb);
        // Face: sink a skin-colored sphere into the front of the head and smin it (the face window of a daruma)
        var face = Sdf.Sphere(0.20f).Move(0, 1.02f, -0.17f).Paint(0xF2D1AC);
        // Mawashi: a torus as the white belt
        var mawashi = Sdf.Torus(0.40f, 0.10f).Move(0, 0.24f, 0).Paint(0xF2EEDC);
        var eye = Sdf.Sphere(0.05f).Move(0.10f, 1.08f, -0.30f).MirrorX()
            .Paint(0x241F1F, 0.0f, 0.2f);
        return trunk.Smin(face, 0.04f).Union(mawashi).Union(eye);
    }

    static void EnsureDaruma(Mesh3d[] meshes)
    {
        if (!darumaDirty)
            return;
        for (int i = 0; i < fighters.Count; i++)
            meshes[i].Rebuild(Sdf.Mesh(DarumaModel(fighters[i].BodyRgb), sdfN));
        darumaDirty = false;
    }

    // Procedural bone animation.
    // Arms = the stance changes with the tactic + flailing when falling, head = nods with the push pulse.
    // Physics (tilt / bounce) is on the model matrix side.
    static List<float> PackBones(int mi, Rikishi f, bool falling, float pulse,
        int logicalFrame, MeshData? data)
    {
        float t = logicalFrame * tickDt;
        float armSwing;
        if (falling)
            armSwing = (float)Math.Sin(t * 16.0f + mi * 2.1f) * 0.9f;
        else if (f.Tactic == taHiki || f.Tactic == taInashi)
            armSwing = 0.7f;
        else
            armSwing = -0.55f * pulse + (float)Math.Sin(t * 2.3f + mi) * 0.08f;
        float nod = falling ? (float)Math.Sin(t * 12.0f) * 0.25f : pulse * 0.16f;
        return Bones.Pack(data, (name, x, y, z) =>
        {
            if (name == "arm_l")
                return Bones.PivotRot(x, y, z, Mat4.RotateX(-armSwing)
                    .Mul(Mat4.RotateZ(
                        falling ? -(float)Math.Sin(t * 13.0f) * 0.5f : -0.12f * pulse)));
            if (name == "arm_r")
                return Bones.PivotRot(x, y, z, Mat4.RotateX(-armSwing)
                    .Mul(Mat4.RotateZ(
                        falling ? (float)Math.Sin(t * 13.0f) * 0.5f : 0.12f * pulse)));
            if (name == "head")
                return Bones.PivotRot(x, y, z, Mat4.RotateX(-nod));
            return null;
        });
    }

    // --- Text (kana subset TTF) --------------------------------------
    const string fontPath = "samples/data/fonts/MPLUS1p-subset.ttf";
    static bool fontLoaded = false;
    static int fontVersion = 0;
    static MeshText? mtext = null;

    static MeshText? EnsureText()
    {
        Io.LoadBytes(fontPath, out var bytes, out var version, out _, out _);
        if (bytes == null)
            return null;
        if (!fontLoaded || fontVersion != version)
        {
            fontLoaded = true;
            fontVersion = version;
            mtext = new MeshText("tonton_mtext", fontPath, version, w, h);
        }
        return mtext;
    }

    // world coordinates → logical screen coordinates
    static ScreenPos ScreenPos(Mat4 vp, float wx, float wy, float wz)
    {
        var c = vp.MulVec4(new Vec4(wx, wy, wz, 1.0f));
        if (c.W <= 0.001f)
            return new ScreenPos { X = 0.0f, Y = 0.0f, Ok = false };
        return new ScreenPos
        {
            X = (c.X / c.W + 1.0f) * 0.5f * w,
            Y = (1.0f - c.Y / c.W) * 0.5f * h,
            Ok = true,
        };
    }

    // --- physics -------------------------------------------------------------

    static WorldRef3d? DeclareWorld()
    {
        var world = Phys3d.World("tonton", new WorldOpts3d
        {
            Gravity = new Vec3d { X = 0.0f, Y = -10.0f, Z = 0.0f },
            FixedDt = tickDt,
            Substeps = 4,
            MaxSteps = 1,
        });
        if (world == null)
            return null;
        Phys3d.Begin(world);
        return world;
    }

    // Ground and base.
    // Wrestlers that fall off roll on the ground (respawn is detected by y).
    static void DeclareStatics(WorldRef3d world)
    {
        var ground = Phys3d.Body(world, "ground", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Static,
            Initial = new InitialState3d { X = 0.0f, Y = -0.5f, Z = 0.0f },
        });
        if (ground != null)
            Phys3d.Box(ground, "solid", new BoxDesc3d
            {
                Hx = 8.0f,
                Hy = 0.5f,
                Hz = 8.0f,
                Friction = 0.7f,
            });
        var baseBody = Phys3d.Body(world, "base", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Static,
            Initial = new InitialState3d { X = 0.0f, Y = 0.15f, Z = 0.0f },
        });
        if (baseBody != null)
            Phys3d.Cylinder(baseBody, "solid", new CylinderDesc3d
            {
                Height = 0.3f,
                Radius = 1.7f,
                Sides = 24,
                Friction = 0.6f,
            });
    }

    // Ring: a dynamic cylinder with suspension springs.
    // A tonton applies an impulse here.
    // The suspension is a custom PD, not a joint (the same approach as the wrestlers' balance).
    // gravityScale is 0, so the rest position lands exactly at home.
    static BodyRef3d? DeclareDohyo(WorldRef3d world)
    {
        var dohyo = Phys3d.Body(world, "dohyo", new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            GravityScale = 0.0f,
            Initial = new InitialState3d { X = 0.0f, Y = dohyoY, Z = 0.0f },
        });
        if (dohyo == null)
            return null;
        Phys3d.Cylinder(dohyo, "solid", new CylinderDesc3d
        {
            Height = dohyoH,
            Radius = dohyoR,
            Sides = 28,
            Density = 1.0f,
            Friction = 0.9f,
            Contact = true,
        });
        return dohyo;
    }

    // Suspension PD.
    // Brings position (xyz) back to home and tilt back to level, with frequency ω and damping ratio ζ.
    // F = m (ω² Δx − 2ζω v), τ = I (ω² lean − 2ζω w).
    static void ControlDohyo(BodyRef3d dohyo)
    {
        var pose = Phys3d.Pose(dohyo);
        if (pose == null)
            return;
        float wl = 2.0f * (float)Math.PI * suspLinHz;
        float cl = 2.0f * suspLinDamp * wl;
        Phys3d.AddForceCenter(dohyo, new Vec3d
        {
            X = dohyoMass * (wl * wl * (0.0f - pose.X) - cl * pose.Vx),
            Y = dohyoMass * (wl * wl * (dohyoY - pose.Y) - cl * pose.Vy),
            Z = dohyoMass * (wl * wl * (0.0f - pose.Z) - cl * pose.Vz),
        });
        var q = new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
        var up = q * Vec3.Up();
        var lean = up.Cross(Vec3.Up());
        float wa = 2.0f * (float)Math.PI * suspAngHz;
        float ca = 2.0f * suspAngDamp * wa;
        Phys3d.AddTorque(dohyo, new Vec3d
        {
            X = dohyoITilt * (wa * wa * lean.X - ca * pose.Wx),
            Y = dohyoITilt * (-ca * pose.Wy),
            Z = dohyoITilt * (wa * wa * lean.Z - ca * pose.Wz),
        });
    }

    static BodyRef3d? DeclareRikishi(WorldRef3d world, int i, Rikishi f)
    {
        var body = Phys3d.Body(world, "rikishi:" + i, new BodyDesc3d
        {
            Type = Phys3d.BodyType.Dynamic,
            Version = f.Gen,
            LinearDamping = 0.1f,
            AngularDamping = 0.5f,
            // Lock yaw: the capsule is rotationally symmetric, so this does not affect physics, and the rendering side can freely choose the visual facing (squarely toward the opponent)
            MotionLocks = new MotionLocks3d { AngularY = true },
            Initial = new InitialState3d { X = f.HomeX, Y = topY + 0.02f, Z = 0.0f },
        });
        if (body == null)
            return null;
        Phys3d.Capsule(body, "solid", new CapsuleDesc3d
        {
            Version = f.Gen,
            A = new Vec3d { X = 0.0f, Y = capR, Z = 0.0f },
            B = new Vec3d { X = 0.0f, Y = 0.95f, Z = 0.0f },
            R = capR,
            Density = 1.0f,
            // High friction: so the body tilts before the feet slide (the condition for a push-down to work).
            // Freedom of movement comes in the air (the moment a tonton bounces them).
            Friction = 0.85f,
            Contact = true,
        });
        return body;
    }

    // Deterministic hash random numbers (the seed is the frame and the wrestler index).
    // Replayable.
    static float Rand01(int n)
    {
        float x = (float)Math.Sin(n * 12.9898f) * 43758.5453f;
        return x - (float)Math.Floor(x);
    }

    // Tactic selection.
    // Re-decided at every reaction interval (tacticUntil).
    // - Opponent leans deeply → pull/inashi with the counter probability (removes the support)
    // - Back at the ring edge → shift the axis with inashi
    // - Rarely "tame", push by default
    static void Decide(int i, Rikishi f, Pose3d pose, Pose3d op, Vec3 dir,
        bool engaged)
    {
        if (frame < f.TacticUntil)
            return;
        var opQ = new Quat(op.Qx, op.Qy, op.Qz, op.Qw);
        var opUp = opQ * Vec3.Up();
        float leanToMe = -(opUp.X * dir.X + opUp.Z * dir.Z); // Component of the opponent's lean toward us
        float pushedBack = -(pose.Vx * dir.X + pose.Vz * dir.Z); // Speed of being pushed back
        float rr = (float)Math.Sqrt(pose.X * pose.X + pose.Z * pose.Z);
        float backToEdge =
            rr > 0.01f ? -(pose.X * dir.X + pose.Z * dir.Z) / rr : 0.0f;
        float edgeDanger = backToEdge > 0.0f ? rr / dohyoR * backToEdge : 0.0f;
        float r = Rand01(frame * 97 + i * 1013);
        // Chance for a pull/inashi: the opponent is leaning, we are being pushed in, or a gamble
        float chance = (leanToMe > 0.10f ? f.Counter : 0.0f)
            + (pushedBack > 0.12f ? f.Counter * 0.8f : 0.0f) + f.Counter * 0.15f;
        if (engaged && r < chance && edgeDanger < 0.55f)
        {
            f.Tactic = Rand01(frame * 131 + i * 71) < 0.5f ? taHiki : taInashi;
            f.TacticUntil = frame + 18;
            f.SideSign = Rand01(frame * 193 + i * 37) < 0.5f ? -1.0f : 1.0f;
        }
        else if (edgeDanger > 0.62f && r < 0.8f)
        {
            f.Tactic = taInashi;
            f.TacticUntil = frame + 16;
            f.SideSign = Rand01(frame * 193 + i * 37) < 0.5f ? -1.0f : 1.0f;
        }
        else if (r > 0.9f)
        {
            f.Tactic = taTame;
            f.TacticUntil = frame + 12;
        }
        else
        {
            f.Tactic = taOsu;
            f.TacticUntil = frame + 18 + (int)Math.Floor(r * 22.0f);
        }
    }

    // Posture PD (inverted pendulum).
    // Torque that rights up toward world up + angular velocity damping.
    // On top of that, per-state steering: a home position spring during shikiri, and the tactic during the bout.
    static void ControlRikishi(int i, Rikishi f, BodyRef3d body, BodyRef3d opp)
    {
        // The "nokotta" pulse.
        // The push force and the arm drawing read the same phase
        f.Pulse = Math.Max(0.0f, (float)Math.Sin(
            frame * tickDt * f.PulseHz * 2.0f * (float)Math.PI + f.Phase));
        var pose = Phys3d.Pose(body);
        if (pose == null)
            return;
        var q = new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
        var up = q * Vec3.Up();
        var lean = up.Cross(Vec3.Up()); // |lean| = sin(tilt), direction = righting rotation axis
        var spring = lean * balKp;
        float mag = spring.Length();
        // Muscle strength limit (tilt beyond it cannot be saved).
        // During "tame", lower the hips and brace
        float maxEff = f.Tactic == taTame && state == stFight
            ? balMax * 1.5f : balMax;
        if (mag > maxEff)
            spring = spring * (maxEff / mag);
        Phys3d.AddTorque(body, new Vec3d
        {
            X = spring.X - pose.Wx * balKd,
            Y = 0.0f, // yaw is locked with motionLocks
            Z = spring.Z - pose.Wz * balKd,
        });
        // No steering while down (no flailing during the aftermath of the result)
        if (up.Y < 0.5f)
            return;
        if (state == stFight)
        {
            var op = Phys3d.Pose(opp);
            if (op == null)
                return;
            var toOpp = new Vec3(op.X - pose.X, 0.0f, op.Z - pose.Z);
            float dist = toOpp.Length();
            var dir = toOpp.Normalize();
            bool engaged = dist < 2.0f * capR + 0.14f;
            Decide(i, f, pose, op, dir, engaged);
            if (f.Tactic == taHiki)
            {
                // Remove the support.
                // A leaning opponent stumbles forward and falls (hikiotoshi)
                Phys3d.AddForceCenter(body, new Vec3d
                {
                    X = (-dir.X * 1.8f - pose.Vx) * hikiK,
                    Y = 0.0f,
                    Z = (-dir.Z * 1.8f - pose.Vz) * hikiK,
                });
                // Hatakikomi: when pulling while engaged, pull the opponent's upper body down toward us
                if (dist < 2.0f * capR + 0.55f)
                {
                    var opQ = new Quat(op.Qx, op.Qy, op.Qz, op.Qw);
                    var opUp = opQ * Vec3.Up();
                    var pullAxis = opUp.Cross(-1.0f * dir); // Axis that tips the opponent's up toward us
                    Phys3d.AddTorque(opp, new Vec3d
                    {
                        X = pullAxis.X * hatakiK,
                        Y = 0.0f,
                        Z = pullAxis.Z * hatakiK,
                    });
                }
            }
            else if (f.Tactic == taInashi)
            {
                // Dodge sideways.
                // Move off the push axis and make the opponent miss
                var side = new Vec3(-dir.Z * f.SideSign, 0.0f, dir.X * f.SideSign);
                Phys3d.AddForceCenter(body, new Vec3d
                {
                    X = (side.X * 2.3f - pose.Vx) * inashiK,
                    Y = 0.0f,
                    Z = (side.Z * 2.3f - pose.Vz) * inashiK,
                });
                // While dodging, roll the opponent's charge forward (tsukiotoshi)
                if (dist < 2.0f * capR + 0.55f)
                {
                    var opQ = new Quat(op.Qx, op.Qy, op.Qz, op.Qw);
                    var opUp = opQ * Vec3.Up();
                    var rollAxis = opUp.Cross(-1.0f * dir);
                    Phys3d.AddTorque(opp, new Vec3d
                    {
                        X = rollAxis.X * hatakiK * 0.7f,
                        Y = 0.0f,
                        Z = rollAxis.Z * hatakiK * 0.7f,
                    });
                }
            }
            else if (f.Tactic == taTame)
            {
                // Stand upright and endure.
                // Neither close in nor push
                Phys3d.AddForceCenter(body, new Vec3d
                {
                    X = -pose.Vx * seekK,
                    Y = 0.0f,
                    Z = -pose.Vz * seekK,
                });
            }
            else
            {
                // Push: a velocity servo that does not brake in the forward direction.
                // Cannot stop even if the opponent disappears while pushing in = the risk of lunging forward is carried by the physics
                float vAlong = pose.Vx * dir.X + pose.Vz * dir.Z;
                float drive =
                    vAlong < walkSpeed ? (walkSpeed - vAlong) * seekK : 0.0f;
                float perpX = pose.Vx - dir.X * vAlong;
                float perpZ = pose.Vz - dir.Z * vAlong;
                Phys3d.AddForceCenter(body, new Vec3d
                {
                    X = dir.X * drive - perpX * seekK,
                    Y = 0.0f,
                    Z = dir.Z * drive - perpZ * seekK,
                });
                if (engaged)
                {
                    // Lean forward with the pulse and push in.
                    // Put the center of mass on the opponent
                    float pulse = f.Pulse;
                    Phys3d.AddForceCenter(body, new Vec3d
                    {
                        X = dir.X * f.PushK * pulse,
                        Y = 0.0f,
                        Z = dir.Z * f.PushK * pulse,
                    });
                    var leanAxis = up.Cross(dir); // Tip up toward dir = lean forward
                    Phys3d.AddTorque(body, new Vec3d
                    {
                        X = leanAxis.X * f.LeanK * pulse,
                        Y = 0.0f,
                        Z = leanAxis.Z * f.LeanK * pulse,
                    });
                }
            }
        }
        else
        {
            Phys3d.AddForceCenter(body, new Vec3d
            {
                X = (f.HomeX - pose.X) * holdK - pose.Vx * holdKd,
                Y = 0.0f,
                Z = (0.0f - pose.Z) * holdK - pose.Vz * holdKd,
            });
        }
    }

    // Win/loss judgment and bout flow.
    // Loss = fell off the top of the ring, or stays down without getting up.
    static void Judge(List<BodyRef3d> bodies)
    {
        if (state == stShikiri)
        {
            stateT--;
            if (stateT <= 0)
            {
                state = stFight;
                fightStart = frame;
                engagedPrev = false;
                Audio.Play(Sfx.Blip(2400, 2100, 0.05f, 0.35f)); // Hyoshigi (wooden clappers)
            }
        }
        else if (state == stFight)
        {
            if (frame == fightStart + 9)
                Audio.Play(Sfx.Blip(2400, 2100, 0.05f, 0.35f),
                    new PlayOpts { Pitch = 0.93f });
            var lost = new bool[] { false, false };
            var lostOut = new bool[] { false, false };
            for (int i = 0; i < fighters.Count; i++)
            {
                var f = fighters[i];
                var pose = Phys3d.Pose(bodies[i]);
                if (pose == null)
                    continue;
                var q = new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
                var up = q * Vec3.Up();
                if (up.Y < 0.5f) // 60° = deeper than the critical angle of the muscle strength limit. No way back
                    f.DownFrames++;
                else
                    f.DownFrames = 0;
                lostOut[i] = pose.Y < 0.35f
                    || pose.X * pose.X + pose.Z * pose.Z > dohyoR * dohyoR;
                lost[i] = lostOut[i] || f.DownFrames > 20;
            }
            if (lost[0] || lost[1])
            {
                winner = lost[0] && lost[1] ? -1 : (lost[0] ? 1 : 0);
                // Kimarite: the winner's last tactic × how the loser lost (out of the ring or fallen)
                if (winner < 0)
                {
                    kimarite = "とりなおし";
                }
                else
                {
                    bool wentOut = lostOut[1 - winner];
                    int wt = fighters[winner].Tactic;
                    if (wt == taHiki)
                        kimarite = wentOut ? "ひきおとし" : "はたきこみ";
                    else if (wt == taInashi)
                        kimarite = wentOut ? "おくりだし" : "つきおとし";
                    else
                        kimarite = wentOut ? "おしだし" : "おしたおし";
                }
                state = stKimari;
                stateT = 90;
                shake = 1.0f;
                Audio.Play(Sfx.Noise(0.7f, 0.28f, 0xbeef)); // Stand-in for cheering
                Audio.Play(Sfx.Blip(520, 780, 0.22f, 0.22f));
                if (auto)
                    Console.WriteLine("tonton: winner=" + winner
                        + " kimarite=" + kimarite + " stars=[" + stars[0] + ","
                        + stars[1] + "] frame=" + frame);
            }
        }
        else if (state == stKimari)
        {
            stateT--;
            if (stateT <= 0)
            {
                if (winner >= 0)
                    stars[winner]++;
                foreach (var f in fighters)
                {
                    f.Gen++;
                    f.DownFrames = 0;
                }
                winner = -1;
                state = stShikiri;
                stateT = 45;
            }
        }
    }

    // --- input ---------------------------------------------------------------- Click (holding repeats) = tonton.
    // Intersect the camera ray with the plane of the ring top, and apply a downward impulse if it is inside the ring.
    // Even if the ring is tilted, hitting "around" the top surface is enough, so a plane approximation is used.
    static void TapAt(BodyRef3d dohyo, float px, float pz)
    {
        lastTap = frame;
        Audio.Play(Sfx.Blip(150, 45, 0.09f, 0.5f)); // The "thud" of a tonton
        Audio.Play(Sfx.Noise(0.05f, 0.18f));
        Phys3d.AddImpulse(dohyo,
            new Vec3d { X = 0.0f, Y = -tapImpulse, Z = 0.0f },
            new CommandOpts3d
            {
                Point = new Vec3d { X = px, Y = topY, Z = pz },
            });
        markerX = px;
        markerY = topY;
        markerZ = pz;
        markerT = 10;
        shake = 1.0f;
    }

    static void CaptureTapInput()
    {
        if (auto)
            return;
        bool pressed = Input.MousePressed();
        pointerDown = Input.MouseDown();
        if (pressed || pointerDown)
        {
            Input.MousePos(out var mx, out var my);
            pointerX = mx;
            pointerY = my;
            if (pressed)
            {
                pendingTap = true;
                pendingTapX = mx;
                pendingTapY = my;
            }
        }
    }

    static void UpdateTap(BodyRef3d dohyo, Vec3 eye, Vec3 target, float fovDeg,
        float aspect, float w, float h)
    {
        // Automatic tonton: keeps hitting near the edge with deterministic pseudo-random numbers (only during the bout)
        if (auto)
        {
            if (state != stFight || frame % 24 != 12)
                return;
            float aa = frame * 7919 % 628 / 100.0f;
            float rr = dohyoR * (0.45f + frame * 337 % 50 / 100.0f);
            TapAt(dohyo, (float)Math.Cos(aa) * rr, (float)Math.Sin(aa) * rr);
            return;
        }
        if (state != stFight)
        {
            // pressed during shikiri / aftermath is not carried over to the next bout.
            pendingTap = false;
            return;
        }
        bool pressed = pendingTap;
        float sx = pressed ? pendingTapX : pointerX;
        float sy = pressed ? pendingTapY : pointerY;
        pendingTap = false;
        bool tap = pressed || (pointerDown && frame - lastTap >= tapRepeat);
        if (!tap)
            return;
        float ndcX = sx / w * 2.0f - 1.0f;
        float ndcY = 1.0f - sy / h * 2.0f;
        var fwd = (target - eye).Normalize();
        var right = Vec3.Up().Cross(fwd).Normalize();
        var upv = fwd.Cross(right);
        float tanH = (float)Math.Tan(fovDeg * (float)Math.PI / 360.0f);
        var dir = (fwd + right * (ndcX * tanH * aspect) + upv * (ndcY * tanH))
            .Normalize();
        if (dir.Y > -0.001f)
            return; // A ray pointing up does not reach the ring
        float t = (topY - eye.Y) / dir.Y;
        var p = eye + dir * t;
        if (p.X * p.X + p.Z * p.Z > dohyoR * dohyoR * 1.1f)
            return;
        TapAt(dohyo, p.X, p.Z);
    }

    static void Tick(float aspect, float w, float h)
    {
        // Advance at 60 Hz the effect counters that used to be at the start / end of the render frame.
        // Effects started by the following tap / judge are drawn at full strength.
        if (shake > 0.001f)
            shake = shake * 0.85f;
        if (markerT > 0)
            markerT = markerT - 1;
        renderFrame = frame;
        var tickEye = new Vec3((float)Math.Sin(frame * 1.7f) * 0.05f * shake,
            3.6f + (float)Math.Sin(frame * 2.3f) * 0.03f * shake, -5.4f);
        renderEye = tickEye;
        var lookAt = new Vec3(0.0f, 0.4f, 0.0f);
        float fovDeg = 40.0f;

        var nextWorld = DeclareWorld();
        if (nextWorld == null)
            return;
        world = nextWorld;
        DeclareStatics(nextWorld);
        var dohyo = DeclareDohyo(nextWorld);
        if (dohyo == null)
            return;
        ControlDohyo(dohyo);
        var bodies = new List<BodyRef3d>();
        for (int i = 0; i < fighters.Count; i++)
        {
            var body = DeclareRikishi(nextWorld, i, fighters[i]);
            if (body == null)
                return;
            bodies.Add(body);
        }
        for (int i = 0; i < fighters.Count; i++)
            ControlRikishi(i, fighters[i], bodies[i], bodies[1 - i]);
        Judge(bodies);
        UpdateTap(dohyo, tickEye, lookAt, fovDeg, aspect, w, h);

        Phys3d.Step(nextWorld, tickDt);

        // Collision at the tachiai (sound and shake on the contact edge)
        {
            var p0 = Phys3d.Pose(bodies[0]);
            var p1 = Phys3d.Pose(bodies[1]);
            if (state == stFight && p0 != null && p1 != null)
            {
                float ddx = p1.X - p0.X;
                float ddz = p1.Z - p0.Z;
                float lim = 2.0f * capR + 0.14f;
                bool eng = ddx * ddx + ddz * ddz < lim * lim;
                if (eng && !engagedPrev)
                {
                    Audio.Play(Sfx.Noise(0.12f, 0.45f));
                    Audio.Play(Sfx.Blip(90, 55, 0.07f, 0.3f));
                    if (shake < 0.6f)
                        shake = 0.6f;
                }
                engagedPrev = eng;
            }
        }

        // Landing squash detection and decay also advance in logical frames.
        for (int i = 0; i < fighters.Count; i++)
        {
            var f = fighters[i];
            var pose = Phys3d.Pose(bodies[i]);
            if (pose == null)
                continue;
            if (f.PrevVy < -1.2f && pose.Vy > f.PrevVy + 0.8f)
                f.SquashT = 8;
            f.PrevVy = pose.Vy;
            if (f.SquashT > 0)
                f.SquashT = f.SquashT - 1;
        }

        frame = frame + 1;
    }

    // --- rendering -------------------------------------------------------------

    public static void OnFrame(float dt)
    {
        var renNow = ren ?? new Renderer3d("tt22");
        ren = renNow;
        var cubeNow = cubeMesh;
        var cylNow = cylMesh;
        if (cubeNow == null || cylNow == null)
        {
            cubeNow = new Mesh3d("tt_cube");
            cubeNow.Rebuild(Shapes3d.Cube());
            cylNow = new Mesh3d("tt_cyl");
            cylNow.Rebuild(Shapes3d.Cylinder(28));
            cubeMesh = cubeNow;
            cylMesh = cylNow;
        }
        var darumaNow = darumaMesh
            ?? new Mesh3d[] { new Mesh3d("tt_daruma0"), new Mesh3d("tt_daruma1") };
        darumaMesh = darumaNow;

        Gfx.Size(out var sw, out var sh);
        float aspect = (float)sw / sh;
        float fovDeg = 40.0f;
        var lookAt = new Vec3(0.0f, 0.4f, 0.0f);

        CaptureTapInput();
        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.Frame(dt, _ => Tick(aspect, sw, sh));

        var eyeNow = renderEye ?? new Vec3(0.0f, 3.6f, -5.4f);
        renderEye = eyeNow;

        // --- draw --- A bright outdoor day (strong sky color so the daruma colors stand out)
        renNow.Light.Dir = new Vec3(-0.4f, 1.0f, -0.55f);
        renNow.Sky.Top = Color.Rgb(0.45f, 0.52f, 0.62f);
        renNow.Sky.Bottom = Color.Rgb(0.16f, 0.14f, 0.13f);
        renNow.Background = Color.Rgb(0.05f, 0.05f, 0.08f);
        renNow.Shadow.Center = new Vec3(0, 0.3f, 0);
        renNow.Shadow.Extent = 3.5f;
        renNow.Begin(new Camera
        {
            Eye = eyeNow,
            Target = lookAt,
            Fov = fovDeg,
            Near = 0.1f,
            Far = 50.0f,
        });

        // Ground and base
        renNow.Draw(cubeNow, Mat4.Translate(new Vec3(0, -0.5f, 0))
            * Mat4.Scale(new Vec3(8, 0.5f, 8)),
            new Draw3dOpts { Tint = Color.Rgb(0.10f, 0.10f, 0.13f) });
        renNow.Draw(cylNow, Mat4.Translate(new Vec3(0, 0.15f, 0))
            * Mat4.Scale(new Vec3(1.7f, 0.3f, 1.7f)),
            new Draw3dOpts { Tint = Color.Rgb(0.16f, 0.15f, 0.19f) });

        // Ring (tilts on the suspension).
        // Overlay the white tawara ring and the shikiri lines on the top surface.
        var drawWorld = world;
        Pose3d? dp = null;
        if (drawWorld != null)
            dp = Phys3d.PoseByKey(drawWorld, "dohyo");
        if (dp != null)
        {
            var dm = Renderer3d.PoseMat(dp);
            renNow.Draw(cylNow, dm * Mat4.Scale(new Vec3(dohyoR, dohyoH, dohyoR)),
                new Draw3dOpts { Tint = Color.Rgb(0.72f, 0.55f, 0.38f) });
            float topLocal = dohyoH * 0.5f;
            renNow.Draw(cylNow, dm * Mat4.Translate(new Vec3(0, topLocal + 0.005f, 0))
                * Mat4.Scale(new Vec3(dohyoR * 0.98f, 0.01f, dohyoR * 0.98f)),
                new Draw3dOpts { Tint = Color.Rgb(0.92f, 0.88f, 0.78f) });
            renNow.Draw(cylNow, dm * Mat4.Translate(new Vec3(0, topLocal + 0.015f, 0))
                * Mat4.Scale(new Vec3(dohyoR * 0.86f, 0.01f, dohyoR * 0.86f)),
                new Draw3dOpts { Tint = Color.Rgb(0.72f, 0.55f, 0.38f) });
            foreach (var sx in new float[] { -0.22f, 0.22f })
                renNow.Draw(cubeNow,
                    dm * Mat4.Translate(new Vec3(sx, topLocal + 0.025f, 0))
                    * Mat4.Scale(new Vec3(0.02f, 0.004f, 0.3f)),
                    new Draw3dOpts { Tint = Color.Rgb(0.92f, 0.88f, 0.78f) });
        }

        // Wrestlers: SDF daruma (skinning + procedural bone animation).
        // Layer the yaw that squarely faces the opponent and the landing squash on top of the physics pose.
        EnsureDaruma(darumaNow);
        for (int i = 0; i < fighters.Count; i++)
        {
            var f = fighters[i];
            var mesh = darumaNow[i];
            if (drawWorld == null)
                continue;
            var pose = Phys3d.PoseByKey(drawWorld, "rikishi:" + i);
            if (pose == null || !mesh.Ready())
                continue;
            float sq = f.SquashT / 8.0f * 0.22f;
            var op = Phys3d.PoseByKey(drawWorld, "rikishi:" + (1 - i));
            float fx = op != null ? op.X - pose.X : -pose.X;
            float fz = op != null ? op.Z - pose.Z : -pose.Z;
            float yaw = (float)Math.Atan2(fx, fz); // Point -Z of the model at the opponent
            var q = new Quat(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
            var upv = q * Vec3.Up();
            bool falling = upv.Y < 0.6f;
            float pulse = f.Tactic == taOsu ? f.Pulse : 0.0f;
            var model = Renderer3d.PoseMat(pose) * Mat4.RotateY(-yaw)
                * Mat4.Scale(new Vec3(1.0f + sq * 0.6f, 1.0f - sq, 1.0f + sq * 0.6f));
            renNow.Draw(mesh, model, new Draw3dOpts
            {
                Bones = PackBones(i, f, falling, pulse, renderFrame, mesh.Data),
            });
        }

        // Tonton marker (a brief ring where it was hit. High brightness so it catches bloom)
        if (markerT > 0)
        {
            float k = markerT / 10.0f;
            renNow.Draw(cylNow,
                Mat4.Translate(new Vec3(markerX, markerY + 0.03f, markerZ))
                * Mat4.Scale(new Vec3(0.22f * (2.0f - k), 0.01f, 0.22f * (2.0f - k))),
                new Draw3dOpts { Tint = Color.Rgb(1.6f, 1.5f, 0.9f) });
        }

        renNow.End();

        // --- Text (kana): drawn over the swapchain after tonemap ---
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        var mt = EnsureText();
        if (mt != null)
        {
            var cream = Color.Rgb(0.95f, 0.92f, 0.85f);
            mt.TextCentered("あか　" + stars[0] + " - " + stars[1] + "　あお",
                w * 0.5f, 348, 20, cream);
            if (state == stFight)
            {
                if (renderFrame - fightStart < 50)
                    mt.TextCentered("はっけよい", w * 0.5f, 120, 44,
                        Color.Rgb(0.98f, 0.85f, 0.4f));
                // Visualizing thought: the current tactic above the head
                for (int i = 0; i < fighters.Count; i++)
                {
                    if (renNow.ViewProj == null)
                        continue;
                    var f = fighters[i];
                    if (drawWorld == null)
                        continue;
                    var pose = Phys3d.PoseByKey(drawWorld, "rikishi:" + i);
                    if (pose == null)
                        continue;
                    var sp = ScreenPos(renNow.ViewProj.Value, pose.X, pose.Y + 1.5f, pose.Z);
                    if (!sp.Ok)
                        continue;
                    string label;
                    Color tint;
                    if (f.Tactic == taHiki)
                    {
                        label = "ひく";
                        tint = Color.Rgb(0.35f, 0.9f, 0.9f);
                    }
                    else if (f.Tactic == taInashi)
                    {
                        label = "いなす";
                        tint = Color.Rgb(0.45f, 0.9f, 0.45f);
                    }
                    else if (f.Tactic == taTame)
                    {
                        label = "ためる";
                        tint = Color.Rgb(0.75f, 0.75f, 0.78f);
                    }
                    else
                    {
                        label = "おす";
                        tint = Color.Rgb(1.0f, 0.66f, 0.25f);
                    }
                    mt.TextCentered(label, sp.X, sp.Y, 16, tint);
                }
            }
            if (state == stKimari)
            {
                if (winner >= 0)
                {
                    var wf = fighters[winner];
                    mt.TextCentered(wf.Name + "のかち", w * 0.5f, 112, 36,
                        Color.Rgb(wf.Color[0] * 0.4f + 0.6f,
                            wf.Color[1] * 0.4f + 0.6f, wf.Color[2] * 0.4f + 0.6f));
                    mt.TextCentered(kimarite, w * 0.5f, 150, 24, cream);
                }
                else
                {
                    mt.TextCentered("とりなおし", w * 0.5f, 124, 32, cream);
                }
            }
        }

        Gfx.EndPass();
    }
}
