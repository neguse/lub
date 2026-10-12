// フレームの外 (OnQuit) で積んだ ReadTexture も、次の poll で結果が届く。
// 後にフレームが来ないので、backend はその場で読み戻しを終える。
using System;
using Lub;

public static class TestReadbackOnQuit
{
    static Readback? rb;
    static TextureRef? rt;
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("on_quit");
    }

    public static void OnFrame(float dt)
    {
        frames++;
        rt = Gfx.UseTexture("rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0.0f, 1.0f, 0.0f, 1.0f } });
        Gfx.EndPass();
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        if (frames == 2)
            App.Quit();
    }

    public static void OnQuit()
    {
        Gfx.ReadTexture(rb!, rt!, 1, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            Gfx.ReadTexture(rb!, rt!, null, out st, out bytes, out _, out _, out _, out _, out id, out _, out err);
        Check.That(st == Gfx.ReadbackStatus.Ready, $"read_texture in on_quit was not ready: {st} {err}");
        if (st != Gfx.ReadbackStatus.Ready) return;
        Check.That(id == 1, "unexpected readback id " + id);
        var p0 = bytes!.Get(0);
        var p1 = bytes.Get(1);
        var p2 = bytes.Get(2);
        var p3 = bytes.Get(3);
        Check.That(p0 == 0 && p1 == 255 && p2 == 0 && p3 == 255, $"pixel ({p0},{p1},{p2},{p3}), want (0,255,0,255)");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
