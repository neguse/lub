// SpriteBatch の Begin から Flush の間に atlas の pixel を変えると、その atlas の Quad はどれも変えた後の pixel で描く。
// (Text は描く途中で glyph を atlas に足すので、同じ batch で先に描いた glyph があっても、足した glyph が描ける)
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class TestSpriteBatchAtlasUpdate
{
    static int frame;
    static List<int> pixels = new List<int>();
    static Atlas? atlas;
    static SpriteBatch? batch;
    static Readback? rb;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 320,
            Height = 180,
        });
        // 2 x 1 の透明な atlas
        for (int i = 0; i < 2 * 4; i++)
            pixels.Add(0);
        atlas = Atlas.FromPixels("sb_update_atlas", 2, 1, pixels);
        batch = new SpriteBatch(4, 2, null, "sb_update");
        rb = Gfx.Readback("sb_update_rb");
    }

    public static void OnFrame(float dt)
    {
        frame++;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        var rt = Gfx.UseTexture("sb_update_rt", 4, 2, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        Check.That(rt != null, "UseTexture failed");
        if (rt == null)
            return;
        int? id = null;
        if (frame == 1)
        {
            // 左に atlas の左の画素 (透明) を描いた後で、右の画素を白にして、右に描く
            Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
            batch!.Begin();
            batch.Quad(atlas!, new Rect(0, 0, 1, 1), 0, 0, 2, 2);
            for (int i = 4; i < 8; i++)
                pixels[i] = 255;
            atlas!.UpdatePixels(pixels);
            batch.Quad(atlas, new Rect(1, 0, 1, 1), 2, 0, 2, 2);
            batch.Flush();
            Gfx.EndPass();
            id = 1;
        }
        Gfx.ReadTexture(rb!, rt, id, out var st, out var bytes, out _, out _, out _, out var stride, out _, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "readback failed: " + err);
        if (st == Gfx.ReadbackStatus.Ready && bytes != null)
        {
            var at = 1 * stride + 3 * 4;
            Check.That(bytes.Get(at) == 255 && bytes.Get(at + 1) == 255 && bytes.Get(at + 2) == 255,
                "the quad drawn after UpdatePixels is " + bytes.Get(at) + "," + bytes.Get(at + 1) + "," + bytes.Get(at + 2) + ", want white");
            Check.Pass();
            return;
        }
        Check.That(frame < 10, "the readback did not arrive");
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
