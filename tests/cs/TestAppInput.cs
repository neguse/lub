// App.Step の StepInput と App.SendEvent: 開いた app に見せる mouse の位置を決め、event を渡す。
// TestAppInputChild は、mouse の位置と、最後に届いた KeyDown の key を色にして main を塗る。
using System;
using Lub;

public static class TestAppInputChild
{
    static int key;

    public static void OnEvent(EventData e)
    {
        if (e.Kind == EventKind.KeyDown) key = e.Key;
    }

    public static void OnFrame(float dt)
    {
        Input.MousePos(out var x, out var y);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { x * 16 / 255.0f, y * 16 / 255.0f, key * 8 / 255.0f, 1 } });
        Gfx.EndPass();
    }
}

public static class TestAppInput
{
    const int sendAt = 4, frames = 10;
    static Readback? rb;
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            ReadbackFixedLatency = true,
        });
        rb = Gfx.Readback("app_input");
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var rt = Gfx.UseTexture("child", 8, 8, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        Check.That(rt != null, "UseTexture failed");
        if (rt == null) return;
        var child = App.Use("child", "tests/cs/.lub/all.tcsb", "TestAppInputChild");
        if (frame == 0)
        {
            // まだ開いていない app への event は届かず、失敗もしない
            Check.That(App.SendEvent(child, new EventData { Kind = EventKind.KeyDown, Key = 9 }), "SendEvent before the first Step failed");
        }
        if (frame == sendAt)
            Check.That(App.SendEvent(child, new EventData { Kind = EventKind.KeyDown, Key = 5 }), "SendEvent failed");
        // frame ごとに違う位置を見せる
        var mx = 1 + frame % 4;
        var my = 2 + frame % 3;
        Check.That(App.Step(child, rt, dt, new StepInput { MouseX = mx, MouseY = my }), $"frame {frame}: Step failed");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        Gfx.ReadTexture(rb!, rt, frame, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        if (frame >= 2)
        {
            Check.That(st == Gfx.ReadbackStatus.Ready, $"frame {frame}: got {st} {err}, want Ready");
            if (st != Gfx.ReadbackStatus.Ready) return;
            var want = new int[] { (1 + id % 4) * 16, (2 + id % 3) * 16, id >= sendAt ? 40 : 0, 255 };
            for (var i = 0; i < 4; i++)
            {
                var got = bytes!.Get(i);
                Check.That(Math.Abs(got - want[i]) <= 1, $"frame {id} byte {i}: got {got}, want {want[i]}");
            }
        }
        frame++;
        if (frame == frames)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
