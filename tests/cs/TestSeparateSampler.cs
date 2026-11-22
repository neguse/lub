// Shaders that declare textures and samplers separately.
// 1. A shader that writes `Texture2D left; SamplerState left_sampler; ...` with the declarations out of order,
//    and also has a lut read only with Load, produces the same image as LUB_TEXTURE2D. left
//    is a REPEAT and right a CLAMP 2x1 texture, and if each texture samples with its own sampler,
//    the 2x1 target becomes (texel 0 of left, texel 1 of right).
// 2. A shader that shares one sampler between 2 textures, and a shader that uses a sampler without
//    a paired texture, produce a shader compile error instead of crashing at draw time,
//    and the error points to LUB_TEXTURE2D.
using System;
using System.Collections.Generic;
using Lub;

public static class TestSeparateSampler
{
    static readonly List<int> left = new List<int> { 255, 0, 0, 255, 0, 255, 0, 255 };
    static readonly List<int> right = new List<int> { 0, 0, 255, 255, 255, 255, 0, 255 };
    static readonly List<int> want = new List<int> { 255, 0, 0, 255, 255, 255, 0, 255 };
    static readonly List<int> white = new List<int> { 255, 255, 255, 255 };

    static readonly string[] names = new string[] { "separate", "macro" };
    static readonly List<Readback> rbs = new List<Readback>();
    static bool[] requested = new bool[2];
    static bool[] got = new bool[2];
    static int frames;
    static bool compared;
    static int errStep;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        for (var i = 0; i < 2; i++)
            rbs.Add(Gfx.Readback(names[i])!);
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frames++;
        Check.That(frames < 300, "test_separate_sampler: read_texture never became ready");
        Io.LoadText("tests/cs/data/test_separate_sampler.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_separate_sampler.fs.slang", out var separateFs, out var verSeparate, out _, out _);
        Io.LoadText("tests/cs/data/test_separate_sampler_macro.fs.slang", out var macroFs, out var verMacro, out _, out _);
        Io.LoadText("tests/cs/data/test_separate_sampler_shared.fs.slang", out var sharedFs, out _, out _, out _);
        Io.LoadText("tests/cs/data/test_separate_sampler_extra.fs.slang", out var extraFs, out _, out _, out _);
        if (vs == null || separateFs == null || macroFs == null || sharedFs == null || extraFs == null)
            return;

        if (compared)
        {
            // One expected error per callback.
            // Advance the stage and check them one by one
            var s = errStep;
            errStep++;
            if (s == 0) { Check.Error("shared", "second"); Gfx.UseShader("shared", vs, sharedFs, 1); }
            else if (s == 1) { Check.Error("shared", "shared"); Gfx.UseShader("shared", vs, sharedFs, 1); }
            else if (s == 2) { Check.Error("shared", "LUB_TEXTURE2D"); Gfx.UseShader("shared", vs, sharedFs, 1); }
            else if (s == 3) { Check.Error("extra", "extra"); Gfx.UseShader("extra", vs, extraFs, 1); }
            else if (s == 4) { Check.Error("extra", "LUB_TEXTURE2D"); Gfx.UseShader("extra", vs, extraFs, 1); }
            else Check.Pass();
            return;
        }

        var shSeparate = Gfx.UseShader("separate", vs, separateFs, verVs * 31 + verSeparate);
        var shMacro = Gfx.UseShader("macro", vs, macroFs, verVs * 31 + verMacro);
        var leftTex = Gfx.UseTexture("left", 2, 1, Gfx.PixelFormat.Rgba8, left, 1,
            new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Repeat });
        var rightTex = Gfx.UseTexture("right", 2, 1, Gfx.PixelFormat.Rgba8, right, 1,
            new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        var lut = Gfx.UseTexture("lut", 1, 1, Gfx.PixelFormat.Rgba8, white, 1);
        if (shSeparate == null || shMacro == null || leftTex == null || rightTex == null || lut == null) return;

        var pending = 0;
        var bindings = new Bindings();
        for (var i = 0; i < 2; i++)
        {
            var rt = Gfx.UseTexture(names[i] + "_rt", 2, 1, Gfx.PixelFormat.Rgba8, null, 1,
                new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
            if (rt == null) return;
            Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f } });
            bindings.Clear();
            bindings.Set("left", leftTex);
            bindings.Set("right", rightTex);
            bindings.Set("lut", lut);
            Gfx.Draw(3, bindings,
                new DrawOpts { Shader = i == 0 ? shSeparate : shMacro, Depth = false, Cull = Gfx.Cull.None });
            Gfx.EndPass();

            if (!got[i])
            {
                // Queue a readback the first time, then poll until the result arrives
                int? req = requested[i] ? null : 1;
                requested[i] = true;
                Gfx.ReadTexture(rbs[i], rt, req, out var st, out var bytes, out var w, out var h, out var fmt,
                    out var stride, out _, out _, out var err);
                Check.That(st != Gfx.ReadbackStatus.Error, names[i] + ": read_texture failed: " + err);
                if (st == Gfx.ReadbackStatus.Ready)
                {
                    Check.That(w == 2 && h == 1 && fmt == Gfx.PixelFormat.Rgba8 && stride == 8,
                        names[i] + ": unexpected readback shape");
                    // bytes cannot be kept across frames, so compare them here
                    for (var j = 0; j < 8; j++)
                    {
                        var g = bytes!.Get(j);
                        Check.That(Math.Abs(g - want[j]) <= 1, $"{names[i]}: byte {j + 1}: got {g}, want {want[j]}");
                    }
                    got[i] = true;
                }
                else
                {
                    pending++;
                }
            }
        }

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f } });
        Gfx.EndPass();
        if (pending > 0)
            return;
        compared = true;
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
