// A draw where both VS and FS read a StructuredBuffer (item) and a texture (tex) with the same names.
// If the resources are bound to both stages, the center pixel is (255, 128, 255).
// On a backend that binds them to only one stage, the draw itself fails or colors are missing.
// Expected: R = item[0] (VS), B = item[1] (FS), G = half of tex x 2 (VS + FS).
using System;
using System.Collections.Generic;
using Lub;

public static class TestDupBinding
{
    static Readback? rb;
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("dup_binding");
    }

    static bool Near(int v, int want, int tol)
    {
        return Math.Abs(v - want) <= tol;
    }

    // lub export draws 1 frame and records the shaders and files used
    public static void OnExport() { OnFrame(0); }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_dup_binding: readback never became ready");

        Io.LoadText("tests/cs/data/test_dup_binding.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_dup_binding.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var item = Gfx.UseBuffer("item", Gfx.BufferType.Storage, new List<float> { 1, 0, 0, 0, 0, 0, 1, 0 }, 1);
        var tex = Gfx.UseTexture("tex", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 0, 128, 0, 255 }, 1, new TextureOpts
        {
            Filter = Gfx.Filter.Nearest,
            Wrap = Gfx.Wrap.Clamp,
        });
        var rt = Gfx.UseTexture("rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts
        {
            Target = true,
            Filter = Gfx.Filter.Nearest,
            Wrap = Gfx.Wrap.Clamp,
        });

        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        var bindings = new Bindings();
        bindings.Set("item", item!);
        bindings.Set("tex", tex!);
        Gfx.Draw(6, bindings, new DrawOpts { Shader = sh!, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        int? req = null;
        if (!requested)
            req = 1;
        requested = true;
        Gfx.ReadTexture(rb!, rt!, req, out var st, out var bytes, out var w, out var h, out _, out var stride, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == 1 && w == 4 && h == 4, "unexpected readback result");
        int off = 2 * stride + 2 * 4;
        int r = bytes!.Get(off);
        int g = bytes.Get(off + 1);
        int b = bytes.Get(off + 2);
        Check.That(Near(r, 255, 3) && Near(g, 128, 3) && Near(b, 255, 3), "test_dup_binding: pixel (" + r + ", " + g + ", " + b + "), want (255, 128, 255)");
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
