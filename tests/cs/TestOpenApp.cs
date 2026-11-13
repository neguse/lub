// App.Use / App.Step: 同じ process に別の app を開き、その絵を texture で受け取る。
// TestOpenApp が TestOpenAppChild を同じ bytecode から開き、毎 frame 進めて、描かせた texture を読み戻す。
// 開かれた app は自分の VM を持つので、static は開いた側と別になる。
// module の隣に置いた build の診断 (.errors) が、開いた app の Status に入る。
using System;
using Lub;

public static class TestOpenAppShared
{
    // 開いた側だけが書く。開かれた app からは初期値のまま見える
    public static int Mark;
}

public static class TestOpenAppChild
{
    static int frame;

    // frame k の main の赤
    public static int Red(int k) { return (k % 4) * 64; }

    public static void OnFrame(float dt)
    {
        Gfx.Size(out var w, out var h);
        // 緑は main の大きさが target の大きさか、青は開いた側の static が見えていないか
        var green = w == 8 && h == 4 ? 1.0f : 0.0f;
        var blue = TestOpenAppShared.Mark == 0 ? 0.0f : 1.0f;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { Red(frame) / 255.0f, green, blue, 1.0f } });
        // 開かれた app の Ui.* は何もしない
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
        // module の隣に置いた build の診断は、次の Step で開いた app の Status に入り、file は消える
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
