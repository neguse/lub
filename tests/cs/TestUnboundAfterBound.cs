// 同じ pass で、texture と storage buffer を全部束縛した draw の後に、何も束縛しない draw を同じ画素に重ねる。
// 束縛しなかった slot は backend の仮の resource で埋まり、後の draw は前の draw の resource を読まない。
// 仮の resource の値は backend ごとに違う (TestUnboundBindings) ので、各 channel が前の draw の値でないことだけを見る。
// shader は TestUnboundBindings と同じで、R = tex_a、G = tex_b (FS)、B = item (VS)。
// 後の draw が描いたことは、clear の A = 0 が draw の A = 1 になったことで見る。
using System;
using System.Collections.Generic;
using Lub;

public static class TestUnboundAfterBound
{
    const int a = 200;
    const int b = 100;
    const int itemValue = 153;

    static Readback? rb;
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("unbound_after_bound");
    }

    static void CheckNotPrevious(int ch, int got, int previous)
    {
        Check.That(Math.Abs(got - previous) > 2, "channel " + ch + " is " + got + ", the value of the previous draw's resource");
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "readback never became ready");

        Io.LoadText("tests/cs/data/test_unbound_bindings.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_unbound_bindings.fs.slang", out var fs, out var verFs, out _, out _);
        Check.That(vs != null && fs != null, "shader source missing");
        var sh = Gfx.UseShader("sh", vs!, fs!, verVs ^ verFs);
        var texOpts = new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var texA = Gfx.UseTexture("tex_a", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { a, 0, 0, 255 }, 1, texOpts);
        var texB = Gfx.UseTexture("tex_b", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 0, b, 0, 255 }, 1, texOpts);
        var item = Gfx.UseBuffer("item", Gfx.BufferType.Storage, new List<float> { 0, 0, itemValue / 255.0f, 0 }, 1);
        var rt = Gfx.UseTexture("rt", 1, 1, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts
        {
            Target = true,
            Filter = Gfx.Filter.Nearest,
            Wrap = Gfx.Wrap.Clamp,
        });

        var all = new Bindings();
        all.Set("tex_a", texA!);
        all.Set("tex_b", texB!);
        all.Set("item", item!);
        var opts = new DrawOpts { Shader = sh!, Depth = false, Cull = Gfx.Cull.None };
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 0 } });
        Gfx.Draw(3, all, opts);
        Gfx.Draw(3, new Bindings(), opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        int? req = requested ? null : 1;
        requested = true;
        Gfx.ReadTexture(rb!, rt!, req, out var st, out var bytes, out var w, out var h, out _, out _, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == 1 && w == 1 && h == 1, "unexpected readback result");
        Check.That(bytes!.Get(3) == 255, "the draw without bindings did not draw (A is " + bytes.Get(3) + ")");
        CheckNotPrevious(1, bytes.Get(0), a);
        CheckNotPrevious(2, bytes.Get(1), b);
        CheckNotPrevious(3, bytes.Get(2), itemValue);
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
