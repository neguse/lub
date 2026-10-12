// 同じフレームで ReadTexture の後に Dispatch する (neguse/lub#66)。
// 読み戻しの後の dispatch も、そのフレームの描画と同じ順で GPU に届く。
// 毎フレーム: rtA を描く → rtA を読み戻す → dst = src*2+1 を dispatch →
// dst を読む fragment で rtB を描く → rtB を読み戻す。届いた rtB の
// 読み戻し (早くても次のフレーム) の 4 列の画素を確かめる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestDispatchAfterReadback
{
    const int frames = 8;
    static readonly List<float> src = new List<float> { 0.05f, 0.1f, 0.15f, 0.2f };
    static Readback? rbA;
    static Readback? rbB;
    static int frame;
    static int verified;
    static int lastId;

    static void ExpectPx(Bytes bytes, int x)
    {
        var v = src[x] * 2.0f + 1.0f - 1.0f;
        var want = (int)Math.Floor(v * 255.0f + 0.5f);
        var r = bytes.Get(x * 4);
        var g = bytes.Get(x * 4 + 1);
        var b = bytes.Get(x * 4 + 2);
        var a = bytes.Get(x * 4 + 3);
        Check.That(
            Math.Abs(r - want) <= 2 && Math.Abs(g - want) <= 2 && Math.Abs(b - want) <= 2 && a == 255,
            $"frame {frame} px {x}: got ({r},{g},{b},{a}), want ({want},{want},{want},255)");
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rbA = Gfx.Readback("a");
        rbB = Gfx.Readback("b");
    }

    public static void OnFrame(float dt)
    {
        if (verified >= frames)
        {
            Check.Pass();
            return;
        }
        frame++;
        Check.That(frame < 300, $"verified {verified} of {frames} frames");

        Io.LoadText("tests/cs/data/test_dispatch_after_readback.cs.slang", out var cs, out var verCs, out _, out _);
        Io.LoadText("tests/cs/data/test_dispatch_after_readback.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_dispatch_after_readback.fs.slang", out var fs, out var verFs, out _, out _);
        Check.That(cs != null && vs != null && fs != null, "shader source missing");
        if (cs == null || vs == null || fs == null) return;
        var shC = Gfx.UseShaderCompute("cs", cs, verCs);
        var shR = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var opts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var rtA = Gfx.UseTexture("rt_a", 4, 1, Gfx.PixelFormat.Rgba8, null, 1, opts);
        var rtB = Gfx.UseTexture("rt_b", 4, 1, Gfx.PixelFormat.Rgba8, null, 1, opts);
        var srcBuf = Gfx.UseBuffer("src", Gfx.BufferType.Storage, src, 1);
        // dst は毎フレーム作り直し (version = frame) て、前フレームの結果が残らないようにする
        var dst = Gfx.UseBufferEmpty("dst", Gfx.BufferType.Storage, 4, frame);
        var quad = Gfx.UseBuffer("quad", Gfx.BufferType.Storage, new List<float> { -1, -1, 1, -1, 1, 1, -1, 1 }, 1);
        if (shC == null || shR == null || rtA == null || rtB == null || srcBuf == null || dst == null || quad == null) return;

        // 1. rtA を描く
        Gfx.BeginPass(new PassOpts { Target = rtA, ClearColor = new float[] { 1.0f, 0.0f, 0.0f, 1.0f } });
        Gfx.EndPass();
        // 2. 読み戻しを積む (sdlgpu はここでフレームの command buffer を submit する)
        Gfx.ReadTexture(rbA!, rtA, frame, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        // 3. 読み戻しの後に dispatch
        Gfx.Dispatch(1, 1, 1,
            new Dictionary<string, object> { ["src"] = srcBuf, ["dst"] = dst },
            new DispatchOpts { Shader = shC });
        // 4. dst を読んで rtB を描く
        Gfx.BeginPass(new PassOpts { Target = rtB, ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f } });
        Gfx.Draw(6,
            new Dictionary<string, object> { ["verts"] = quad, ["dst"] = dst },
            new DrawOpts { Shader = shR, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        // rtB の読み戻しを毎フレーム積み、届いたものを要求順に確かめる
        Gfx.ReadTexture(rbB!, rtB, frame, out var st, out var bytes, out var w, out var h, out var fmt,
            out var stride, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture(rt_b) failed: " + err);
        if (st == Gfx.ReadbackStatus.Ready)
        {
            Check.That(id > lastId, $"read_texture(rt_b) id {id} after {lastId}");
            lastId = id;
            Check.That(w == 4 && h == 1 && fmt == Gfx.PixelFormat.Rgba8 && stride == 16, "unexpected readback shape");
            for (var x = 0; x < 4; x++)
                ExpectPx(bytes!, x);
            verified++;
        }

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f } });
        Gfx.EndPass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
