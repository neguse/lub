// 整数列の STORAGE (UseBufferInts と FrameBufferInts) は int のまま詰まり、shader が StructuredBuffer<int> で渡した値を読む。
// 2^24 を超える値と負の値も変わらない。
// shader は UseBufferInts の値が合えば R、FrameBufferInts の値が合えば G を 1 にして描き、読み戻して両方を見る。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBufferInts
{
    static readonly List<int> values = new List<int> { 7, 123456, 16777217, -5 };
    static Readback? rb;
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("buffer_ints");
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_buffer_ints: readback never became ready");
        Io.LoadText("tests/cs/data/test_buffer_ints.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_ints.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var buf = Gfx.UseBufferInts("vals", Gfx.BufferType.Storage, values, 1);
        var rt = Gfx.UseTexture("rt", 2, 2, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        if (sh == null || buf == null || rt == null)
            return;

        var b = new Bindings();
        b.Set("use_vals", buf);
        b.Set("frame_vals", Gfx.FrameBufferInts(Gfx.BufferType.Storage, values));
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(3, b, new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        int? req = requested ? null : 1;
        requested = true;
        Gfx.ReadTexture(rb!, rt, req, out var st, out var bytes, out var w, out var h, out _,
            out var stride, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == 1 && w == 2 && h == 2, "unexpected readback result");
        var off = 1 * stride + 1 * 4;
        var r = bytes!.Get(off);
        var g = bytes.Get(off + 1);
        Check.That(r > 247, "UseBufferInts: the shader read other ints (R " + r + ")");
        Check.That(g > 247, "FrameBufferInts: the shader read other ints (G " + g + ")");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
