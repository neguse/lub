// Phys2d の world callbacks (filter / pre_solve / friction / restitution)。
// custom filter と pre_solve が衝突を無効にできること、friction / restitution の
// mixer が呼ばれること、callback に渡る shape の metadata、callback の中での
// 物理の変更が error になること、callback が error になったときの既定の動き、
// callbacks を省略した宣言が前の callback を外すこと。
//
// callback の中の error は callback の呼び出し元 (binding) が捕まえて log に出し、
// 既定値 (filter / pre_solve は true、mixer は Box2D の既定の混ぜ方) で続ける。
// OnFrame / OnError には届かないので、error の検出は「error になるはずの呼び出しの
// 後ろに置いた印が立たないこと」で行う。
using System;
using Lub;

public static class TestPhysicsBox2dCallbacks
{
    static int frame;
    static int filterCalls;
    static int preSolveCalls;
    static int frictionCalls;
    static int restitutionCalls;
    static bool mutationAttempted;
    static bool mutationPassed;
    static bool sawCallbackMetadata;
    static bool sawEventMetadata;
    static int fallbackFilterErrors;
    static int fallbackPreSolveErrors;
    static int fallbackFrictionErrors;
    static int fallbackRestitutionErrors;
    static bool fallbackErrorsPassed;
    static bool fallbackClearChecked;

    static bool Pair(ShapeView a, ShapeView b, string lhs, string rhs)
    {
        return (a.Body == lhs && b.Body == rhs) || (a.Body == rhs && b.Body == lhs);
    }

    // 物理の変更は callback の中では error になる。error なら後ろの印は立たない。
    static void IllegalMutation(string key)
    {
        Phys2d.World(key, new WorldOpts());
        mutationPassed = true;
    }

    static bool OnFilter(ShapeView a, ShapeView b)
    {
        filterCalls++;
        // error になった呼び出しは既定値 (true) で続くので、ghost と wall の
        // 組では試さない (ghost が wall を通る検査を壊す)。
        if (!mutationAttempted && !Pair(a, b, "ghost", "wall"))
        {
            mutationAttempted = true;
            IllegalMutation("callback_illegal");
        }
        if (Pair(a, b, "ghost", "wall"))
        {
            var ghost = a.Body == "ghost" ? a : b;
            var wall = a.Body == "wall" ? a : b;
            sawCallbackMetadata = sawCallbackMetadata
                || (ghost.Tag == "ghost_shape"
                    && ghost.MaterialName == "ghost_material"
                    && ghost.MaterialId == 5
                    && ghost.CategoryBits == "0000000000000020"
                    && ghost.MaskBits == "ffffffffffffffff"
                    && wall.Tag == "wall_shape"
                    && wall.MaterialName == "wall_material"
                    && wall.MaterialId == 3
                    && wall.CategoryBits == "0000000000000001");
            return false;
        }
        return true;
    }

    static bool OnPreSolve(PreSolveContact contact)
    {
        preSolveCalls++;
        if (Pair(contact.A, contact.B, "pass", "wall"))
            return false;
        return true;
    }

    static float MixFriction(MaterialView a, MaterialView b)
    {
        frictionCalls++;
        if (a.MaterialId == 7 || b.MaterialId == 7)
            return 0.25f;
        return (float)Math.Sqrt((a.Friction ?? 0) * (b.Friction ?? 0));
    }

    static float MixRestitution(MaterialView a, MaterialView b)
    {
        restitutionCalls++;
        if (a.MaterialId == 7 || b.MaterialId == 7)
            return 0.1f;
        return Math.Max(a.Restitution ?? 0, b.Restitution ?? 0);
    }

    static bool FallbackErrorFilter(ShapeView a, ShapeView b)
    {
        fallbackFilterErrors++;
        IllegalMutation("callback_fallback_illegal");
        fallbackErrorsPassed = true;
        return true;
    }

    static bool FallbackErrorPreSolve(PreSolveContact c)
    {
        fallbackPreSolveErrors++;
        IllegalMutation("callback_fallback_illegal");
        fallbackErrorsPassed = true;
        return true;
    }

    static float FallbackErrorFriction(MaterialView a, MaterialView b)
    {
        fallbackFrictionErrors++;
        IllegalMutation("callback_fallback_illegal");
        fallbackErrorsPassed = true;
        return 0;
    }

    static float FallbackErrorRestitution(MaterialView a, MaterialView b)
    {
        fallbackRestitutionErrors++;
        IllegalMutation("callback_fallback_illegal");
        fallbackErrorsPassed = true;
        return 0;
    }

    static int FallbackErrorCount()
    {
        return fallbackFilterErrors + fallbackPreSolveErrors + fallbackFrictionErrors + fallbackRestitutionErrors;
    }

    static void DeclareFallbackPair(WorldRef world)
    {
        Phys2d.Begin(world, new BeginOpts { Prune = false });
        var wall = Phys2d.Body(world, "wall", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = 0, Y = 0 } });
        Phys2d.Box(wall!, "solid", new BoxDesc { Hx = 0.25f, Hy = 0.25f, Contact = true, PreSolve = true });
        var box = Phys2d.Body(world, "box", new BodyDesc { Type = Phys2d.BodyType.Dynamic, Initial = new InitialState { X = 0, Y = 0 } });
        Phys2d.Box(box!, "solid", new BoxDesc { Hx = 0.2f, Hy = 0.2f, Density = 1, Contact = true, PreSolve = true });
        Phys2d.Step(world, 1.0f / 60.0f);
    }

    static WorldOpts FallbackOpts(WorldCallbacks? callbacks)
    {
        return new WorldOpts
        {
            Gravity = new Vec2d { X = 0, Y = 0 },
            FixedDt = 1.0f / 60.0f,
            Substeps = 4,
            MaxSteps = 1,
            Sleep = false,
            Callbacks = callbacks,
        };
    }

    static void RunFallbackSmoke()
    {
        if (frame == 1)
        {
            var world = Phys2d.World("callback_fallback", FallbackOpts(new WorldCallbacks
            {
                Filter = (a, b) => FallbackErrorFilter(a, b),
                PreSolve = c => FallbackErrorPreSolve(c),
                Friction = (a, b) => FallbackErrorFriction(a, b),
                Restitution = (a, b) => FallbackErrorRestitution(a, b),
            }));
            DeclareFallbackPair(world!);
            if (fallbackFilterErrors < 1 || fallbackPreSolveErrors < 1 || fallbackFrictionErrors < 1 || fallbackRestitutionErrors < 1)
                Check.Fail("callback fallback errors were not exercised");
            Check.That(!fallbackErrorsPassed, "callback fallback errors did not abort the callback");
        }
        else if (frame == 2)
        {
            var before = FallbackErrorCount();
            var world = Phys2d.World("callback_fallback", FallbackOpts(null));
            DeclareFallbackPair(world!);
            if (FallbackErrorCount() != before)
                Check.Fail("omitted callbacks did not clear previous callback refs");
            fallbackClearChecked = true;
        }
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        frame++;
        RunFallbackSmoke();

        var world = Phys2d.World("callbacks", FallbackOpts(new WorldCallbacks
        {
            Filter = (a, b) => OnFilter(a, b),
            PreSolve = c => OnPreSolve(c),
            Friction = (a, b) => MixFriction(a, b),
            Restitution = (a, b) => MixRestitution(a, b),
        }));
        Phys2d.Begin(world!);

        var wall = Phys2d.Body(world!, "wall", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = 0, Y = 0 } });
        Phys2d.Box(wall!, "solid", new BoxDesc
        {
            Hx = 0.08f,
            Hy = 2.0f,
            Tag = "wall_shape",
            Friction = 0.8f,
            Restitution = 0.0f,
            MaterialName = "wall_material",
            MaterialId = 3,
            Contact = true,
            PreSolve = true,
        });

        var ghost = Phys2d.Body(world!, "ghost", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            FixedRotation = true,
            Initial = new InitialState { X = -1.0f, Y = 1.2f, Vx = 3.0f, Vy = 0 },
        });
        Phys2d.Box(ghost!, "solid", new BoxDesc
        {
            Hx = 0.08f,
            Hy = 0.08f,
            Tag = "ghost_shape",
            Density = 1,
            MaterialName = "ghost_material",
            MaterialId = 5,
            PreSolve = true,
            Filter = new FilterDesc { CategoryBits = "0000000000000020", MaskBits = "ffffffffffffffff" },
        });

        var pass = Phys2d.Body(world!, "pass", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            FixedRotation = true,
            Initial = new InitialState { X = -1.0f, Y = 0.0f, Vx = 3.0f, Vy = 0 },
        });
        Phys2d.Box(pass!, "solid", new BoxDesc
        {
            Hx = 0.08f,
            Hy = 0.08f,
            Tag = "pass_shape",
            Density = 1,
            MaterialName = "pass_material",
            MaterialId = 6,
            PreSolve = true,
        });

        var mix = Phys2d.Body(world!, "mix", new BodyDesc
        {
            Type = Phys2d.BodyType.Dynamic,
            FixedRotation = true,
            Initial = new InitialState { X = -1.0f, Y = -1.2f, Vx = 3.0f, Vy = 0 },
        });
        Phys2d.Box(mix!, "solid", new BoxDesc
        {
            Hx = 0.08f,
            Hy = 0.08f,
            Tag = "mix_shape",
            Density = 1,
            Friction = 0.2f,
            Restitution = 0.3f,
            MaterialName = "mix_material",
            MaterialId = 7,
            Contact = true,
            PreSolve = true,
        });

        Phys2d.Step(world!, 1.0f / 60.0f);

        foreach (var e in Phys2d.Contacts(world!, Phys2d.EventKind.Begin))
        {
            if (Pair(e.A, e.B, "mix", "wall"))
            {
                var mixShape = e.A.Body == "mix" ? e.A : e.B;
                sawEventMetadata = sawEventMetadata
                    || (mixShape.Tag == "mix_shape" && mixShape.MaterialName == "mix_material" && mixShape.MaterialId == 7);
            }
        }

        if (frame < 48)
        {
            Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.015f, 0.015f, 0.02f, 1.0f } });
            Gfx.EndPass();
            return;
        }

        var ghostPose = Phys2d.Pose(ghost!);
        var passPose = Phys2d.Pose(pass!);
        Check.That(filterCalls >= 1, "custom filter did not run");
        Check.That(preSolveCalls >= 1, "pre_solve did not run");
        Check.That(frictionCalls >= 1, "friction mixer did not run");
        Check.That(restitutionCalls >= 1, "restitution mixer did not run");
        Check.That(mutationAttempted && !mutationPassed, "callback mutation guard did not trigger");
        Check.That(sawCallbackMetadata, "callback shape metadata was not exposed");
        Check.That(sawEventMetadata, "event shape metadata was not copied");
        Check.That(fallbackClearChecked, "callback clearing smoke did not run");
        var overlaps = Phys2d.OverlapAabb(world!, new AabbDesc { MinX = -0.1f, MinY = -2.0f, MaxX = 0.1f, MaxY = 2.0f });
        var sawQueryMetadata = false;
        foreach (var hit in overlaps)
        {
            if (hit.Body == "wall" && hit.Tag == "wall_shape" && hit.MaterialName == "wall_material" && hit.MaterialId == 3)
                sawQueryMetadata = true;
        }
        Check.That(sawQueryMetadata, "query shape metadata was not exposed");
        Check.That(ghostPose != null && ghostPose.X > 0.2f, "custom filter did not let ghost pass wall: x=" + (ghostPose == null ? 0 : ghostPose.X));
        Check.That(passPose != null && passPose.X > 0.2f, "pre_solve did not disable wall solve: x=" + (passPose == null ? 0 : passPose.X));

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.015f, 0.015f, 0.02f, 1.0f } });
        Gfx.EndPass();
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
