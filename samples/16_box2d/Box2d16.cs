// lub の samples/16_box2d (Haxe 版 Box2d16.hx) の TinyC# 版 entry。
// 実行: lub samples/16_box2d/Box2d16.csproj (transpile + watch + hot reload)
// Phys2d の即時モード API で simulation tick ごとに world/body/shape を宣言し、
// 最新 pose を render frame ごとに頂点列へ焼いて 1 draw で描く。
using System;
using System.Collections.Generic;

public static class Box2d16
{
    const float DT = 1.0f / 60.0f;
    const float PPM_X = 4.0f;
    const float PPM_Y = 2.7f;
    static int contactFlash = 0;
    static FixedStep? step = null;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend, width = 640, height = 360 });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    static void PushVertex(List<float> verts, float x, float y,
        float r, float g, float b, float a)
    {
        verts.Add(x / PPM_X);
        verts.Add(y / PPM_Y);
        verts.Add(0.0f);
        verts.Add(r);
        verts.Add(g);
        verts.Add(b);
        verts.Add(a);
    }

    static void PushBox(List<float> verts, Pose pose, float hx, float hy,
        float[] color)
    {
        float c = (float)Math.Cos(pose.angle);
        float s = (float)Math.Sin(pose.angle);
        var cxs = new float[] { -hx, hx, hx, -hx };
        var cys = new float[] { -hy, -hy, hy, hy };
        var wx = new List<float>();
        var wy = new List<float>();
        for (int i = 0; i < 4; i++)
        {
            wx.Add(pose.x + c * cxs[i] - s * cys[i]);
            wy.Add(pose.y + s * cxs[i] + c * cys[i]);
        }
        var idx = new int[] { 0, 1, 2, 0, 2, 3 };
        foreach (var i in idx)
        {
            PushVertex(verts, wx[i], wy[i],
                color[0], color[1], color[2], color[3]);
        }
    }

    static void Simulate(WorldRef world)
    {
        Phys2d.phys2d_begin(world);

        var ground = Phys2d.phys2d_body(world, "ground", new BodyDesc
        {
            type = Phys2d.STATIC,
            initial = new InitialState { x = 0.0f, y = -1.55f },
        });
        if (ground == null) return;
        Phys2d.phys2d_box(ground, "floor", new BoxDesc
        {
            hx = 3.4f,
            hy = 0.18f,
            density = 0.0f,
            friction = 0.85f,
            contact = true,
        });

        for (int i = 0; i < 4; i++)
        {
            bool even = i == 0 || i == 2;
            var b = Phys2d.phys2d_body(world, "box:" + i, new BodyDesc
            {
                type = Phys2d.DYNAMIC,
                initial = new InitialState
                {
                    x = even ? -0.18f : 0.18f,
                    y = -0.95f + i * 0.58f,
                    angle = (i - 1) * 0.08f,
                },
            });
            if (b == null) return;
            Phys2d.phys2d_box(b, "solid", new BoxDesc
            {
                hx = 0.26f,
                hy = 0.26f,
                density = 1.0f,
                friction = 0.65f,
                contact = true,
            });
        }

        Phys2d.phys2d_step(world, DT);

        var contacts = Phys2d.phys2d_contacts(world, "begin");
        if (contacts.Count > 0) contactFlash = 12;
        if (contactFlash > 0) contactFlash = contactFlash - 1;
    }

    public static void onFrame(float dt)
    {
        var world = Phys2d.phys2d_world("box2d16", new WorldOpts
        {
            gravity = new Vec2d { x = 0.0f, y = -10.0f },
            fixedDt = DT,
            substeps = 4,
            maxSteps = 1,
        });
        if (world == null) return;

        var stepNow = step ?? new FixedStep();
        step = stepNow;
        stepNow.frame(dt, _ => Simulate(world));

        var verts = new List<float>();
        var groundPose = Phys2d.phys2d_pose(world, "ground");
        if (groundPose == null) return;
        PushBox(verts, groundPose, 3.4f, 0.18f,
            new float[] { 0.28f, 0.33f, 0.36f, 1.0f });
        int boxCount = 1;
        for (int i = 0; i < 4; i++)
        {
            var pose = Phys2d.phys2d_pose(world, "box:" + i);
            if (pose == null) continue;
            float hot = contactFlash > 0 ? 0.12f : 0.0f;
            PushBox(verts, pose, 0.26f, 0.26f,
                new float[] { 0.20f + hot, 0.62f, 0.88f, 1.0f });
            boxCount = boxCount + 1;
        }

        Io.load_text("samples/16_box2d/data/16_color.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/16_box2d/data/16_color.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return;
        var shader = Gfx.use_shader("box2d16_color", vs, fs, vsv * 31 + fsv);
        var mesh = Gfx.use_buffer("box2d16_mesh", Gfx.VERTEX, verts);
        if (shader == null || mesh == null) return;

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.03f, 0.04f, 0.055f, 1.0f },
        });
        Gfx.draw(boxCount * 6,
            new Dictionary<string, object> { ["verts"] = mesh },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }
}
