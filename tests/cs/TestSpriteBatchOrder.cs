// SpriteBatch の atlas ごとの draw は、Begin の後にその atlas が初めて出た順に重なる。
// 前の frame で先に出た atlas の順は持ち越さない。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class TestSpriteBatchOrder
{
    static int frame;
    static Atlas? red;
    static Atlas? blue;
    static SpriteBatch? batch;
    static Readback? rb;

    static Atlas Solid(string key, int r, int g, int b)
    {
        return Atlas.FromPixels(key, 1, 1, new List<int> { r, g, b, 255 });
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 320,
            Height = 180,
        });
        red = Solid("sb_order_red", 255, 0, 0);
        blue = Solid("sb_order_blue", 0, 0, 255);
        batch = new SpriteBatch("sb_order", 4, 4);
        rb = Gfx.Readback("sb_order_rb");
    }

    public static void OnFrame(float dt)
    {
        frame++;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        var rt = Gfx.UseTexture("sb_order_rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        Check.That(rt != null, "UseTexture failed");
        if (rt == null)
            return;
        var one = new Rect(0, 0, 1, 1);
        int? id = null;
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        batch!.Begin();
        if (frame == 1)
        {
            // 青だけを描く frame
            batch.Quad(blue!, one, 0, 0, 4, 4);
        }
        else if (frame == 2)
        {
            // 赤を全面に、その上に青を真ん中に
            batch.Quad(red!, one, 0, 0, 4, 4);
            batch.Quad(blue!, one, 1, 1, 2, 2);
            id = 2;
        }
        batch.Flush();
        Gfx.EndPass();
        Gfx.ReadTexture(rb!, rt, id, out var st, out var bytes, out _, out _, out _, out var stride, out _, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "readback failed: " + err);
        if (st == Gfx.ReadbackStatus.Ready && bytes != null)
        {
            var center = 2 * stride + 2 * 4;
            var corner = 0;
            Check.That(bytes.Get(corner) == 255 && bytes.Get(corner + 2) == 0,
                "the corner is " + bytes.Get(corner) + "," + bytes.Get(corner + 1) + "," + bytes.Get(corner + 2) + ", want red");
            Check.That(bytes.Get(center) == 0 && bytes.Get(center + 2) == 255,
                "the center is " + bytes.Get(center) + "," + bytes.Get(center + 1) + "," + bytes.Get(center + 2) + ", want blue (drawn after red in this frame)");
            Check.Pass();
            return;
        }
        Check.That(frame < 10, "the readback did not arrive");
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
