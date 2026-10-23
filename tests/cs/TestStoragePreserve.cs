// compute が読み書きする storage buffer と storage texture は、前の dispatch が書いた内容を保つ (neguse/lub#184)。
// 3 フレーム続けて 1 フレームに 2 回 dispatch し、各 dispatch は acc[0] を読んで 1 足して書き、img の自分の列 (dispatch の番号) に 1 を書く。
// 最後のフレームで acc と img を rt に描いて読み戻す。
// 期待: acc は 6、img は 6 列とも 1。
// 前の内容を捨てて別の領域に差し替える backend では acc が 6 に届かず、前の dispatch の列が消える。
using System;
using System.Collections.Generic;
using Lub;

public static class TestStoragePreserve
{
    const int frameCount = 3;
    const int perFrame = 2;
    const int n = frameCount * perFrame;

    static Readback? rb;
    static int frames;

    static readonly List<float> zeros = new List<float> { 0, 0, 0, 0 };

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("storage_preserve");
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_storage_preserve: readback never became ready");

        Io.LoadText("tests/cs/data/test_storage_preserve.cs.slang", out var cs, out var verCs, out _, out _);
        Io.LoadText("tests/cs/data/test_storage_preserve.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_storage_preserve.fs.slang", out var fs, out var verFs, out _, out _);
        Check.That(cs != null && vs != null && fs != null, "test_storage_preserve: shader source missing");
        if (cs == null || vs == null || fs == null) return;
        var shC = Gfx.UseShaderCompute("cs", cs, verCs);
        var shR = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var acc = Gfx.UseBuffer("acc", Gfx.BufferType.Storage, zeros, 1);
        var img = Gfx.UseTexture("img", n, 1, Gfx.PixelFormat.Rgba32f, null, 1,
            new TextureOpts { Target = true, Storage = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        var rt = Gfx.UseTexture("rt", n, 1, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        if (shC == null || shR == null || acc == null || img == null || rt == null) return;

        if (frames == 1)
        {
            Gfx.BeginPass(new PassOpts { Target = img, ClearColor = new float[] { 0, 0, 0, 0 } });
            Gfx.EndPass();
        }
        if (frames <= frameCount)
        {
            for (var k = 0; k < perFrame; k++)
            {
                var texel = (frames - 1) * perFrame + k;
                Gfx.Dispatch(1, 1, 1,
                    new Dictionary<string, object>
                    {
                        ["acc"] = acc,
                        ["img"] = img,
                        ["uniforms"] = new Dictionary<string, object> { ["texel"] = new float[] { texel, 0, 0, 0 } },
                    },
                    new DispatchOpts { Shader = shC });
            }
        }
        if (frames == frameCount)
        {
            Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
            Gfx.Draw(3,
                new Dictionary<string, object> { ["img"] = img, ["acc"] = acc },
                new DrawOpts { Shader = shR, Depth = false, Cull = Gfx.Cull.None });
            Gfx.EndPass();
        }

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        if (frames < frameCount)
            return;
        int? req = frames == frameCount ? 1 : null;
        Gfx.ReadTexture(rb!, rt, req, out var st, out var bytes, out var w, out var h, out _,
            out _, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "test_storage_preserve: read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == 1 && w == n && h == 1, "test_storage_preserve: unexpected readback result");
        var bad = new List<string>();
        for (var x = 0; x < n; x++)
        {
            var r = bytes!.Get(x * 4);
            var g = bytes.Get(x * 4 + 1);
            if (r != 255 || g != n)
                bad.Add($"px {x}: img {r} want 255, acc {g} want {n}");
        }
        Check.That(bad.Count == 0, "test_storage_preserve: " + string.Join("; ", bad));
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
