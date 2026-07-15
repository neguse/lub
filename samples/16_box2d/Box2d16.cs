// lub の samples/16_box2d (Haxe 版 Box2d16.hx) の TinyC# 版 entry。
// 実行: lub samples/16_box2d/Box2d16.csproj (transpile + watch + hot reload)
// Phys2d の即時モード API で毎フレーム world/body/shape を宣言し、
// step 後の pose を頂点列に焼いて 1 draw で描く。
using System;
using System.Collections.Generic;

public static class Box2d16
{
    const double DT = 1.0 / 60.0;
    const double PPM_X = 4.0;
    const double PPM_Y = 2.7;
    static int contactFlash = 0;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend, width = 640, height = 360 });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    static void PushVertex(List<double> verts, double x, double y,
        double r, double g, double b, double a)
    {
        verts.Add(x / PPM_X);
        verts.Add(y / PPM_Y);
        verts.Add(0.0);
        verts.Add(r);
        verts.Add(g);
        verts.Add(b);
        verts.Add(a);
    }

    static void PushBox(List<double> verts, Pose pose, double hx, double hy,
        double[] color)
    {
        double c = Math.Cos(pose.angle);
        double s = Math.Sin(pose.angle);
        var cxs = new double[] { -hx, hx, hx, -hx };
        var cys = new double[] { -hy, -hy, hy, hy };
        var wx = new List<double>();
        var wy = new List<double>();
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

    public static void onFrame(double dt)
    {
        var world = Phys2d.phys2d_world("box2d16", new WorldOpts
        {
            gravity = new Vec2d { x = 0.0, y = -10.0 },
            fixedDt = DT,
            substeps = 4,
            maxSteps = 1,
        });
        if (world == null) return;
        Phys2d.phys2d_begin(world);

        var ground = Phys2d.phys2d_body(world, "ground", new BodyDesc
        {
            type = Phys2d.STATIC,
            initial = new InitialState { x = 0.0, y = -1.55 },
        });
        if (ground == null) return;
        Phys2d.phys2d_box(ground, "floor", new BoxDesc
        {
            hx = 3.4,
            hy = 0.18,
            density = 0.0,
            friction = 0.85,
            contact = true,
        });

        var bodies = new List<BodyRef>();
        for (int i = 0; i < 4; i++)
        {
            bool even = i == 0 || i == 2;
            var b = Phys2d.phys2d_body(world, "box:" + i, new BodyDesc
            {
                type = Phys2d.DYNAMIC,
                initial = new InitialState
                {
                    x = even ? -0.18 : 0.18,
                    y = -0.95 + i * 0.58,
                    angle = (i - 1) * 0.08,
                },
            });
            if (b == null) return;
            Phys2d.phys2d_box(b, "solid", new BoxDesc
            {
                hx = 0.26,
                hy = 0.26,
                density = 1.0,
                friction = 0.65,
                contact = true,
            });
            bodies.Add(b);
        }

        Phys2d.phys2d_step(world, DT);

        var contacts = Phys2d.phys2d_contacts(world, "begin");
        if (contacts.Count > 0) contactFlash = 12;
        if (contactFlash > 0) contactFlash = contactFlash - 1;

        var verts = new List<double>();
        var groundPose = Phys2d.phys2d_pose(ground);
        if (groundPose == null) return;
        PushBox(verts, groundPose, 3.4, 0.18,
            new double[] { 0.28, 0.33, 0.36, 1.0 });
        int boxCount = 1;
        foreach (var body in bodies)
        {
            var pose = Phys2d.phys2d_pose(body);
            if (pose == null) continue;
            double hot = contactFlash > 0 ? 0.12 : 0.0;
            PushBox(verts, pose, 0.26, 0.26,
                new double[] { 0.20 + hot, 0.62, 0.88, 1.0 });
            boxCount = boxCount + 1;
        }

        var meshVersion = Gfx.next_version();
        Io.load_text("samples/16_box2d/data/16_color.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.load_text("samples/16_box2d/data/16_color.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return;
        var shader = Gfx.use_shader("box2d16_color", vs, fs, vsv * 31 + fsv);
        var mesh = Gfx.use_buffer("box2d16_mesh", Gfx.VERTEX, verts,
            meshVersion);
        if (shader == null || mesh == null) return;

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.03, 0.04, 0.055, 1.0 },
        });
        Gfx.draw(boxCount * 6,
            new Dictionary<string, object> { ["verts"] = mesh },
            new DrawOpts { shader = shader, depth = false, cull = Gfx.NONE });
        Gfx.end_pass();
    }
}
