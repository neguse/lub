// StepInput.XrTargets / XrViewProjections / Xr of App.Step: the opener prepares the XR eyes shown to the opened app.
// Even when advanced with Lubx.AppTimeline, the passed eyes are visible to the opened app as is.
// If XR is running, TestAppXrChild fills each eye with a color made from the first value of the view-projection and the eye width; if not, it fills main with blue.
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class TestAppXrChild
{
    public static void OnFrame(float dt)
    {
        if (!Xr.Active())
        {
            Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 1, 1 } });
            Gfx.EndPass();
            return;
        }
        for (var eye = 0; eye < 2; eye++)
        {
            var view = Xr.View(eye, 0.05f, 100);
            if (view == null)
                return;
            Gfx.BeginPass(new PassOpts { Target = view.Target, ClearColor = new float[] { view.ViewProjection[0], view.Width / 16.0f, view.Orientation[3] * 0.25f, 1 } });
            Gfx.EndPass();
        }
    }
}

public static class TestAppXr
{
    // 0..3: no eyes passed, 4..7: eyes passed, 8..11: eyes passed but Xr = false, 12..15: eyes passed while advancing with AppTimeline
    const int frames = 18;
    static readonly AppTimeline timeline = new AppTimeline(4, 200);
    static Readback? rb;
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            ReadbackFixedLatency = true,
        });
        rb = Gfx.Readback("app_xr");
    }

    static float[] Vp(float first)
    {
        var m = new float[16];
        m[0] = first;
        m[5] = 1;
        m[10] = 1;
        m[15] = 1;
        return m;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var opts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest };
        var rt = Gfx.UseTexture("child", 8, 8, Gfx.PixelFormat.Rgba8, null, 1, opts);
        var left = Gfx.UseTexture("left", 8, 8, Gfx.PixelFormat.Rgba8, null, 1, opts);
        var right = Gfx.UseTexture("right", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, opts);
        Check.That(rt != null && left != null && right != null, "UseTexture failed");
        if (rt == null || left == null || right == null) return;
        var child = App.Use("child", "tests/cs/.lub/all.tcsb", "TestAppXrChild");
        var input = new StepInput();
        if (frame >= 4)
        {
            input.XrTargets = new List<TextureRef> { left, right };
            input.XrViewProjections = new List<float[]> { Vp(0.5f), Vp(0.25f) };
        }
        if (frame >= 8 && frame < 12)
            input.Xr = false;
        var stepped = frame >= 12 ? timeline.Step(child, rt, dt, input) : App.Step(child, rt, dt, input);
        Check.That(stepped, $"frame {frame}: Step failed");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        // While no eyes are passed and while Xr = false, reads the main image; while eyes are passed, reads the left and right eyes per frame
        var given = (frame >= 4 && frame < 8) || frame >= 12;
        var read = !given ? rt : (frame % 2 == 0 ? left : right);
        Gfx.ReadTexture(rb!, read, frame, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        if (frame >= 2)
        {
            Check.That(st == Gfx.ReadbackStatus.Ready, $"frame {frame}: got {st} {err}, want Ready");
            if (st != Gfx.ReadbackStatus.Ready) return;
            var want = new int[] { 0, 0, 255, 255 };
            if ((id >= 4 && id < 8) || id >= 12)
                want = id % 2 == 0 ? new int[] { 128, 128, 64, 255 } : new int[] { 64, 64, 64, 255 };
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
