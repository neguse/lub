// App.Use / App.Step: opens another app in the same process and receives its image as a texture.
// TestOpenApp opens TestOpenAppChild from the same bytecode, advances it every frame, and reads back the texture it drew.
// The opened app has its own VM, so its statics are separate from those of the opener.
// Build diagnostics (.errors) placed next to the module go into the Status of the opened app.
using System;
using Lub;

public static class TestOpenAppShared
{
    // Only the opener writes it. The opened app sees its initial value
    public static int Mark;
}

public static class TestOpenAppChild
{
    static int frame;

    // The red of main at frame k
    public static int Red(int k) { return (k % 4) * 64; }

    public static void OnFrame(float dt)
    {
        Gfx.Size(out var w, out var h);
        // Green tells whether the size of main is the target size, blue whether the opener's static is invisible
        var green = w == 8 && h == 4 ? 1.0f : 0.0f;
        var blue = TestOpenAppShared.Mark == 0 ? 0.0f : 1.0f;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { Red(frame) / 255.0f, green, blue, 1.0f } });
        // Ui.* in the opened app does nothing
        Ui.Text("ignored");
        Ui.Render();
        Gfx.EndPass();
        frame++;
    }
}

public static class TestOpenApp
{
    const int frames = 8;
    static Readback? rb;
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            ReadbackFixedLatency = true,
        });
        rb = Gfx.Readback("open_app");
        TestOpenAppShared.Mark = 1;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var rt = Gfx.UseTexture("child", 8, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        Check.That(rt != null, "UseTexture failed");
        if (rt == null) return;
        var child = App.Use("child", "tests/cs/.lub/all.tcsb", "TestOpenAppChild");
        // Build diagnostics placed next to the module go into the Status of the opened app at the next Step, and the file is removed
        if (frame == 3)
            Io.SaveText("tests/cs/.lub/all.errors", "Child.cs(1,1): error: boom");
        Check.That(App.Step(child, rt, dt), $"frame {frame}: Step failed");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        Gfx.ReadTexture(rb!, rt, frame, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        if (frame >= 2)
        {
            Check.That(st == Gfx.ReadbackStatus.Ready, $"frame {frame}: got {st} {err}, want Ready");
            if (st != Gfx.ReadbackStatus.Ready) return;
            var want = new int[] { TestOpenAppChild.Red(id), 255, 0, 255 };
            for (var i = 0; i < 4; i++)
            {
                var got = bytes!.Get(i);
                Check.That(Math.Abs(got - want[i]) <= 1, $"frame {id} byte {i}: got {got}, want {want[i]}");
            }
        }
        if (frame == 3)
        {
            var status = App.Status(child);
            Check.That(status.Running && status.BuildError.Contains("boom"), $"the build error did not arrive: {status.BuildError}");
            Io.LoadText("tests/cs/.lub/all.errors", out var left, out _, out _, out _);
            Check.That(left == null, "the build error file was not removed");
        }
        frame++;
        if (frame == frames)
        {
            Check.Error("Step inside a pass", "outside a pass");
            Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
            App.Step(child, rt, dt);
        }
        if (frame == frames + 1)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
