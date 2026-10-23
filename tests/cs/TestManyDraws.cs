// 1 フレームに 20,000 回 draw し、各 draw が自分の texture、sampler、uniform で描くこと。draw k は画素 k を 1 つ塗る:
// - R, G は 2 つの texture (k % 256 番目と (k + 2) % 256 番目) を引いた値で、
//   texture ごとの (filter, wrap) で決まる。sampler の取り違えで値が変わる。
// - A は 1 つ目の texture の番号、B は fragment の uniform (k % 251)、位置は
//   vertex の uniform で決まる。
// descriptor が尽きて直前の draw の table を使うと色か位置がずれ、draw を飛ばすと画素が黒のまま残る。
// 最初の重いフレームでは draw の前に compute を 2,500 回 dispatch し (各 dispatch が読む buffer と書く buffer を 1 つずつ持つ)、数え上げた結果を最後の行の画素に描く。D3D12 は frame slot の descriptor heap が初めて埋まるときに dispatch の中で heap を切り替える。2 回目の重いフレームは draw だけ (sdlgpu の Vulkan は uniform を dynamic offset で bind し、lavapipe はその bind ごとに実行時に descriptor set を作る。両方のフレームで dispatch すると CI の lavapipe ではその確保が失敗して落ちる)。
// 重いフレームの後に軽いフレームを挟み、もう一度重いフレームを確かめる (Vulkan は軽いフレームで余った descriptor pool を返す)。
using System;
using System.Collections.Generic;
using Lub;

public static class TestManyDraws
{
    const int w = 200;
    const int h = 100;
    const int n = w * h;
    const int ntex = 256;
    const int ntint = 251;
    const int ndispatch = 2500;
    const int nlight = 16;
    const int lightFrames = 4;
    const int heavyFrames = 2;

    static Readback? rb;
    static int frames;
    static readonly List<float[]> cells = new List<float[]>();
    static readonly List<float[]> tints = new List<float[]>();
    static readonly List<float> codes = new List<float>();
    static readonly float[] counterCell = new float[] { 0, h, w, h + 1 };

    static int heavyDone;
    static int pending;
    static int lightLeft;

    // texture t の (filter, wrap) と、fs の PROBE が返す値 (0..255)。
    static bool Nearest(int t)
    {
        return t % 2 == 0;
    }

    static bool Clamp(int t)
    {
        return (t / 2) % 2 == 0;
    }

    static float Probe(int t)
    {
        var nearest = Nearest(t);
        var clamp = Clamp(t);
        var between = nearest ? 255.0f : 0.75f * 255.0f;
        var outside = clamp ? 255.0f : (nearest ? 0.0f : 0.25f * 255.0f);
        return 0.25f * between + 0.75f * outside;
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("many_draws");
        for (var k = 0; k < n; k++)
        {
            cells.Add(new float[] { k % w, k / w, w, h + 1 });
            tints.Add(new float[] { (k % ntint) / 255.0f, 0, 0, 0 });
        }
        for (var t = 0; t < ntex; t++)
            codes.Add(Probe(t));
    }

    static bool Near(int v, float want)
    {
        return Math.Abs(v - want) <= 3;
    }

    static void Verify(Bytes bytes, int stride, bool dispatched)
    {
        for (var k = 0; k < n; k++)
        {
            var off = (k / w) * stride + (k % w) * 4;
            var t = k % ntex;
            var t2 = (k + 2) % ntex;
            var r = bytes.Get(off);
            var g = bytes.Get(off + 1);
            var b = bytes.Get(off + 2);
            var a = bytes.Get(off + 3);
            if (!Near(r, codes[t]) || !Near(g, codes[t2]) || b != k % ntint || a != t)
            {
                Check.Fail($"test_many_draws: draw {k} of {n} wrote ({r}, {g}, {b}, {a}), want ({codes[t]:F0}, {codes[t2]:F0}, {k % ntint}, {t})");
                return;
            }
        }
        if (!dispatched)
            return;
        var o = h * stride;
        var count = bytes.Get(o) + bytes.Get(o + 1) * 256;
        var last = bytes.Get(o + 2) + bytes.Get(o + 3) * 256;
        Check.That(count == ndispatch && last == ndispatch - 1,
            $"test_many_draws: {ndispatch} dispatches counted {count} (last step {last}), want {ndispatch} (last step {ndispatch - 1})");
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 600, "test_many_draws: readback never became ready");

        Io.LoadText("tests/cs/data/test_many_draws.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_many_draws.fs.slang", out var fs, out var verFs, out _, out _);
        Io.LoadText("tests/cs/data/test_many_draws.cs.slang", out var cs, out var verCs, out _, out _);
        Io.LoadText("tests/cs/data/test_many_draws_counter.fs.slang", out var counterFs, out var verCounter, out _, out _);
        if (vs == null || fs == null || cs == null || counterFs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var shCounter = Gfx.UseShader("counter", vs, counterFs, verVs ^ verCounter);
        var shCs = Gfx.UseShaderCompute("cs", cs, verCs);
        var texs = new List<TextureRef>();
        for (var t = 0; t < ntex; t++)
        {
            texs.Add(Gfx.UseTexture("tex" + t, 2, 1, Gfx.PixelFormat.Rgba8, new List<int> { 0, t, 0, 255, 255, t, 0, 255 }, 1,
                new TextureOpts
                {
                    Filter = Nearest(t) ? Gfx.Filter.Nearest : Gfx.Filter.Linear,
                    Wrap = Clamp(t) ? Gfx.Wrap.Clamp : Gfx.Wrap.Repeat,
                })!);
        }
        var counts = new List<BufferRef>();
        counts.Add(Gfx.UseBuffer("count0", Gfx.BufferType.Storage, new List<float> { 0, 0, 0, 0 }, 1)!);
        counts.Add(Gfx.UseBuffer("count1", Gfx.BufferType.Storage, new List<float> { 0, 0, 0, 0 }, 1)!);
        var rt = Gfx.UseTexture("rt", w, h + 1, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        if (sh == null || shCounter == null || shCs == null || rt == null) return;
        var opts = new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None };

        var heavy = pending == 0 && lightLeft == 0;
        var dispatching = heavy && heavyDone == 0;
        if (dispatching)
        {
            for (var i = 0; i < ndispatch; i++)
            {
                Gfx.Dispatch(1, 1, 1,
                    new Dictionary<string, object>
                    {
                        ["src"] = counts[i % 2],
                        ["dst"] = counts[(i + 1) % 2],
                        ["uniforms"] = new Dictionary<string, object> { ["step"] = new float[] { i, 0, 0, 0 } },
                    },
                    new DispatchOpts { Shader = shCs });
            }
        }
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 0 } });
        var draws = heavy ? n : nlight;
        for (var k = 0; k < draws; k++)
        {
            Gfx.Draw(6,
                new Dictionary<string, object>
                {
                    ["tex"] = texs[k % ntex],
                    ["tex2"] = texs[(k + 2) % ntex],
                    ["uniforms"] = new Dictionary<string, object> { ["cell"] = cells[k], ["tint"] = tints[k] },
                },
                opts);
        }
        if (dispatching)
        {
            Gfx.Draw(6,
                new Dictionary<string, object>
                {
                    ["counter"] = counts[ndispatch % 2],
                    ["uniforms"] = new Dictionary<string, object> { ["cell"] = counterCell },
                },
                new DrawOpts { Shader = shCounter, Depth = false, Cull = Gfx.Cull.None });
        }
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        Gfx.ReadbackStatus st;
        Bytes? bytes;
        int rw;
        int rh;
        int stride;
        int id;
        string? err;
        if (heavy)
        {
            pending = heavyDone + 1;
            Gfx.ReadTexture(rb!, rt, pending, out st, out bytes, out rw, out rh, out _, out stride, out id, out _, out err);
        }
        else
        {
            lightLeft = Math.Max(lightLeft - 1, 0);
            Gfx.ReadTexture(rb!, rt, null, out st, out bytes, out rw, out rh, out _, out stride, out id, out _, out err);
        }
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == pending && rw == w && rh == h + 1, "unexpected readback result");
        Verify(bytes!, stride, pending == 1);
        heavyDone = pending;
        pending = 0;
        if (heavyDone == heavyFrames)
        {
            Check.Pass();
            return;
        }
        lightLeft = lightFrames;
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
