// Gfx.DispatchBound と float[] の Set と Clear。
// TestComputeUniformBlock と同じ shader で、同じ Bindings を Clear して使い回す。
// 期待: 中央の pixel が (128, 128, 255)。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBindingsDispatch
{
    static Readback? rb;
    static Bindings? kept;
    static int frames;
    static bool requested;

    static readonly List<float> zeros = new List<float> { 0, 0, 0, 0, 0, 0, 0, 0 };

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("bindings_dispatch");
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_bindings_dispatch: readback never became ready");

        Io.LoadText("tests/cs/data/test_compute_uniform_block.cs.slang", out var cs, out var verCs, out _, out _);
        Io.LoadText("tests/cs/data/test_compute_uniform_block.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_compute_uniform_block.fs.slang", out var fs, out var verFs, out _, out _);
        if (cs == null || vs == null || fs == null)
            return;
        var shC = Gfx.UseShaderCompute("cs", cs, verCs);
        var shR = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var mark = Gfx.UseBuffer("mark", Gfx.BufferType.Storage, zeros, 1);
        var val = Gfx.UseBuffer("val", Gfx.BufferType.Storage, zeros, 1);
        var rt = BindingsTestUtil.Target("rt", 4, 4);
        if (shC == null || shR == null || mark == null || val == null) return;

        if (kept == null) kept = new Bindings();
        kept.Clear();
        kept.Set("out_buf", mark);
        Gfx.DispatchBound(2, 1, 1, kept, new DispatchOpts { Shader = shC });
        kept.Clear();
        kept.Set("out_buf", val);
        kept.Set("value", new float[] { 0, 0.5f, 1, 0 });
        Gfx.DispatchBound(1, 1, 1, kept, new DispatchOpts { Shader = shC });

        kept.Clear();
        kept.Set("mark", mark);
        kept.Set("val", val);
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.DrawBound(6, kept, BindingsTestUtil.Opts(shR));
        Gfx.EndPass();
        BindingsTestUtil.EmptyMainPass();

        int? req = requested ? null : 1;
        requested = true;
        var bytes = BindingsTestRead.Read(rb!, rt, req, out var stride);
        if (bytes == null)
            return;
        var off = 2 * stride + 2 * 4;
        Check.That(Math.Abs(bytes.Get(off) - 128) <= 3 && Math.Abs(bytes.Get(off + 1) - 128) <= 3 && Math.Abs(bytes.Get(off + 2) - 255) <= 3,
            $"test_bindings_dispatch: pixel ({bytes.Get(off)}, {bytes.Get(off + 1)}, {bytes.Get(off + 2)}), want (128, 128, 255)");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
