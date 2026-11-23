// Within one frame, redeclares the texture with the same key with different pixels, and samples it in a draw after each declaration.
// A draw reads the contents in the order the declarations were recorded, so the left half, drawn after declaring red, is red, and the right half, drawn after redeclaring green, is green.
// On a backend where writes overtake the recording order, both become the final green.
// - rewrite: rewrites with the same size.
// - resize: redeclares with pixels of a different size, recreating the texture the draw used.
// In addition, redeclares textures with a different size right after they are used.
// The 3 kinds: color target, depth target, and storage texture written by compute.
// resize and these destroy resources the GPU has not used yet, so a backend that destroys them too early gets a validation error.
using System;
using System.Collections.Generic;
using Lub;

public static class TestTextureRecordingOrder
{
    static readonly string[] cases = new string[] { "rewrite", "resize" };
    static readonly List<Readback> rbs = new List<Readback>();
    static readonly bool[] done = new bool[2];
    static readonly List<string> mismatches = new List<string>();
    static int frames;
    static bool requested;

    static readonly List<int> red = new List<int> { 255, 0, 0, 255 };
    static readonly List<int> green = new List<int> { 0, 255, 0, 255 };

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        for (var i = 0; i < 2; i++)
            rbs.Add(Gfx.Readback("texture_recording_order_" + cases[i])!);
    }

    static Bindings Side(TextureRef tex, float side)
    {
        var bindings = new Bindings();
        bindings.Set("tex", tex);
        bindings.Set("side", new Vec4(side, 0, 0, 0));
        return bindings;
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_texture_recording_order: readback never became ready");

        Io.LoadText("tests/cs/data/test_texture_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_texture_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        Io.LoadText("tests/cs/data/test_texture_recording_order.cs.slang", out var cs, out var verCs, out _, out _);
        if (vs == null || fs == null || cs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var shC = Gfx.UseShaderCompute("cs", cs, verCs);
        var texOpts = new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var before = frames * 2;
        var after = frames * 2 + 1;
        if (sh == null || shC == null) return;
        var opts = new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None };

        var rtOpts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var rts = new List<TextureRef>();
        for (var i = 0; i < 2; i++)
            rts.Add(Gfx.UseTexture("rt_" + cases[i], 4, 4, Gfx.PixelFormat.Rgba8, null, 1, rtOpts)!);

        Gfx.BeginPass(new PassOpts { Target = rts[0], ClearColor = new float[] { 0, 0, 0, 1 } });
        var tex = Gfx.UseTexture("tex", 1, 1, Gfx.PixelFormat.Rgba8, red, before, texOpts)!;
        Gfx.Draw(6, Side(tex, 0), opts);
        tex = Gfx.UseTexture("tex", 1, 1, Gfx.PixelFormat.Rgba8, green, after, texOpts)!;
        Gfx.Draw(6, Side(tex, 1), opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rts[1], ClearColor = new float[] { 0, 0, 0, 1 } });
        var sized = Gfx.UseTexture("sized", 1, 1, Gfx.PixelFormat.Rgba8, red, before, texOpts)!;
        Gfx.Draw(6, Side(sized, 0), opts);
        var green4 = new List<int>();
        for (var i = 0; i < 16; i++)
            green4.Add(green[i % 4]);
        sized = Gfx.UseTexture("sized", 2, 2, Gfx.PixelFormat.Rgba8, green4, after, texOpts)!;
        Gfx.Draw(6, Side(sized, 1), opts);
        Gfx.EndPass();

        var target = Gfx.UseTexture("target", 2, 2, Gfx.PixelFormat.Rgba8, null, before, rtOpts);
        Gfx.BeginPass(new PassOpts { Target = target, ClearColor = new float[] { 0, 0, 1, 1 } });
        Gfx.EndPass();
        Gfx.UseTexture("target", 4, 4, Gfx.PixelFormat.Rgba8, null, after, rtOpts);

        var depth = Gfx.UseTexture("depth", 2, 2, Gfx.PixelFormat.Depth32f, null, before, rtOpts);
        Gfx.BeginPass(new PassOpts { DepthTarget = depth, ClearDepth = 1.0f });
        Gfx.EndPass();
        Gfx.UseTexture("depth", 4, 4, Gfx.PixelFormat.Depth32f, null, after, rtOpts);

        var storageOpts = new TextureOpts { Target = true, Storage = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var storage = Gfx.UseTexture("storage", 2, 2, Gfx.PixelFormat.Rgba32f, null, before, storageOpts);
        var bindings = new Bindings();
        bindings.Set("dst", storage!);
        Gfx.Dispatch(1, 1, 1, bindings,
            new DispatchOpts { Shader = shC });
        Gfx.UseTexture("storage", 4, 4, Gfx.PixelFormat.Rgba32f, null, after, storageOpts);

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        var allDone = true;
        for (var i = 0; i < 2; i++)
        {
            if (done[i])
                continue;
            int? req = requested ? null : 1;
            Gfx.ReadTexture(rbs[i], rts[i], req, out var st, out var bytes, out var w, out var h, out _,
                out var stride, out var id, out _, out var err);
            Check.That(st != Gfx.ReadbackStatus.Error, cases[i] + ": read_texture failed: " + err);
            if (st == Gfx.ReadbackStatus.Ready)
            {
                Check.That(id == 1 && w == 4 && h == 4, cases[i] + ": unexpected readback result");
                var lr = bytes!.Get(stride + 4);
                var lg = bytes.Get(stride + 5);
                var lb = bytes.Get(stride + 6);
                var rr = bytes.Get(stride + 8);
                var rg = bytes.Get(stride + 9);
                var rb = bytes.Get(stride + 10);
                if (!(lr == 255 && lg == 0 && lb == 0 && rr == 0 && rg == 255 && rb == 0))
                {
                    mismatches.Add($"test_texture_recording_order {cases[i]}: left ({lr}, {lg}, {lb}) want (255, 0, 0), right ({rr}, {rg}, {rb}) want (0, 255, 0)");
                }
                done[i] = true;
            }
            else
            {
                allDone = false;
            }
        }
        requested = true;
        if (allDone)
        {
            Check.That(mismatches.Count == 0, string.Join("; ", mismatches));
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
