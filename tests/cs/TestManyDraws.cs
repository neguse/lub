// Draws 20,000 times in one frame, and each draw uses its own texture, sampler, and uniforms.
// Draw k fills one pixel, pixel k:
// - R, G are the values sampled from 2 textures (number k % 256 and number (k + 2) % 256), and
//   are determined by the (filter, wrap) of each texture. Mixing up samplers changes the values.
// - A is the number of the first texture, B is the fragment uniform (k % 251), and the position
//   is determined by the vertex uniform.
// If descriptors run out and the table of the previous draw is used, the color or position shifts; if a draw is skipped, the pixel stays black.
// In the first heavy frame, it closes the pass after half of the draws, dispatches compute 2,500 times (each dispatch has one buffer it reads and one it writes), and draws the counted result into the pixels of the last row.
// D3D12 switches heaps inside a dispatch when the descriptor heap of the frame slot fills for the first time, and the later draws draw the same set of textures as the earlier half in the new heap.
// The 2nd heavy frame has draws only (Vulkan in sdlgpu binds uniforms with dynamic offsets, and lavapipe creates a descriptor set at run time for each such bind. Dispatching in both frames makes that allocation fail on the CI lavapipe, and it crashes).
// A light frame is placed after the heavy frame, then another heavy frame is checked (Vulkan returns surplus descriptor pools in the light frame).
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

    // The (filter, wrap) of texture t, and the value returned by PROBE in fs (0..255).
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
        var bindings = new Bindings();
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 0 } });
        var draws = heavy ? n : nlight;
        for (var k = 0; k < draws; k++)
        {
            if (dispatching && k == draws / 2)
            {
                Gfx.EndPass();
                for (var i = 0; i < ndispatch; i++)
                {
                    bindings.Clear();
                    bindings.Set("src", counts[i % 2]);
                    bindings.Set("dst", counts[(i + 1) % 2]);
                    bindings.Set("step", new Vec4(i, 0, 0, 0));
                    Gfx.Dispatch(1, 1, 1, bindings,
                        new DispatchOpts { Shader = shCs });
                }
                Gfx.BeginPass(new PassOpts { Target = rt, Load = Gfx.LoadAction.Load });
            }
            bindings.Clear();
            bindings.Set("tex", texs[k % ntex]);
            bindings.Set("tex2", texs[(k + 2) % ntex]);
            bindings.Set("cell", cells[k]);
            bindings.Set("tint", tints[k]);
            Gfx.Draw(6, bindings, opts);
        }
        if (dispatching)
        {
            bindings.Clear();
            bindings.Set("counter", counts[ndispatch % 2]);
            bindings.Set("cell", counterCell);
            Gfx.Draw(6, bindings,
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
