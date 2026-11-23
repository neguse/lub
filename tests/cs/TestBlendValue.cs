// Blend is a value struct, so copying a Blends preset into a variable and rewriting its fields does not change the preset itself.
// After rewriting Dst / DstAlpha of the copy to One (additive), draws with the copy and with the preset (Blends.Alpha), and checks via readback that the pixel drawn with the preset still follows the Blends.Alpha equation.
// A draw that omits DrawOpts.Blend overwrites without blending.
// The native gate runs on both the tcs VM player and .NET execution, and also compares the per-frame digest (including the Blend of each draw).
using System;
using System.Collections.Generic;
using Lub;

public static class TestBlendValue
{
    sealed class Case
    {
        public string Name = "";
        public Blend? Blend;
        public int[] Want = new int[4];
        public Readback? Rb;
        public bool Done;
    }

    static readonly float[] clear = new float[] { 0.25f, 0.25f, 0, 0.25f };
    static readonly float[] color = new float[] { 1, 0, 0, 0.5f };
    static readonly List<Case> cases = new List<Case>();
    static readonly List<string> mismatches = new List<string>();
    static ShaderRef? sh;
    static int frame;
    static bool requested;

    static void Add(string name, Blend? blend, int[] want)
    {
        cases.Add(new Case { Name = name, Blend = blend, Want = want, Rb = Gfx.Readback("blend_value_" + name) });
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
        var copy = Blends.Alpha;
        copy.Dst = Gfx.BlendFactor.One;
        copy.DstAlpha = Gfx.BlendFactor.One;
        // Copy: rgb = drawn value × 0.5 + target, a = 0.5 + 0.25
        Add("copy", copy, new int[] { 191, 64, 0, 191 });
        // Preset: rgb = drawn value × 0.5 + target × 0.5, a = 0.5 + 0.25 × 0.5
        Add("preset", Blends.Alpha, new int[] { 159, 32, 0, 159 });
        Add("none", null, new int[] { 255, 0, 0, 128 });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Check.That(frame < 300, "test_blend_value: readback never became ready");
        Io.LoadText("tests/cs/data/test_buffer_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var rtOpts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        frame++;
        var allDone = true;
        foreach (var c in cases)
        {
            var rt = Gfx.UseTexture("rt_" + c.Name, 4, 4, Gfx.PixelFormat.Rgba8, null, 1, rtOpts)!;
            Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = clear });
            var b = new Bindings();
            b.Set("col", Gfx.FrameBufferArray(Gfx.BufferType.Storage, color, 4));
            b.Set("side", new Vec4(0, 0, 0, 0));
            Gfx.Draw(6, b, new DrawOpts { Shader = sh!, Depth = false, Cull = Gfx.Cull.None, Blend = c.Blend });
            Gfx.EndPass();
            if (c.Done)
                continue;
            int? req = requested ? null : 1;
            Gfx.ReadTexture(c.Rb!, rt, req, out var st, out var bytes, out _, out _, out _,
                out var stride, out _, out _, out var err);
            Check.That(st != Gfx.ReadbackStatus.Error, c.Name + ": read_texture failed: " + err);
            if (st != Gfx.ReadbackStatus.Ready)
            {
                allDone = false;
                continue;
            }
            // Left end of the 2nd row (inside the quad drawn on the left half)
            var got = new int[4];
            for (var i = 0; i < 4; i++)
                got[i] = bytes!.Get(stride + i);
            for (var i = 0; i < 4; i++)
                if (got[i] < c.Want[i] - 1 || got[i] > c.Want[i] + 1)
                {
                    mismatches.Add(c.Name + ": got (" + got[0] + ", " + got[1] + ", " + got[2] + ", " + got[3]
                        + ") want (" + c.Want[0] + ", " + c.Want[1] + ", " + c.Want[2] + ", " + c.Want[3] + ")");
                    break;
                }
            c.Done = true;
        }
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
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
