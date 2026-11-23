// If the pixels of an atlas change between Begin and Flush of a SpriteBatch, every Quad of that atlas draws with the changed pixels.
// (Text adds glyphs to the atlas while drawing, so the added glyphs can be drawn even if glyphs were drawn earlier in the same batch)
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
        // A 2 x 1 transparent atlas
        for (int i = 0; i < 2 * 4; i++)
            pixels.Add(0);
        atlas = Atlas.FromPixels("sb_update_atlas", 2, 1, pixels);
        batch = new SpriteBatch("sb_update", 4, 2);
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
            // After drawing the left pixel of the atlas (transparent) on the left, make the right pixel white and draw on the right
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
