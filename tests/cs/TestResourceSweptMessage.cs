// sweep された resource の ref を渡した error は、key と未使用の frame 数を含む。
using System;
using Lub;

public static class TestResourceSweptMessage
{
    const int sweep = 2;

    static int frame;
    static TextureRef? stale;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", ResourceSweepAfterFrames = sweep });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        if (frame == 1)
        {
            stale = Gfx.UseTexture("swept_rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
            Check.That(stale != null, "the render target must be declared");
            return;
        }
        // 宣言を止めて sweep させる
        if (frame < sweep + 4)
            return;
        if (frame > sweep + 4)
        {
            Check.Pass();
            return;
        }
        Check.Error("swept target", "texture 'swept_rt' was swept (not used for ");
        Gfx.BeginPass(new PassOpts { Target = stale, ClearColor = new float[] { 0, 0, 0, 1 } });
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
