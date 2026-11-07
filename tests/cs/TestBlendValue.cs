// Blend は値の struct なので、Blends の組を変数に写して field を書き換えても、組そのものは変わらない。
// 写しの Dst / DstAlpha を One (加算) に書き換えてから、写しと組 (Blends.Alpha) のそれぞれで描き、組で描いた画素が Blends.Alpha の式のままであることを readback で見る。
// DrawOpts.Blend を省いた draw は混ぜずに上書きする。
// native gate は tcs VM の player と .NET 実行の両方で走らせ、frame ごとの digest (draw の Blend を含む) も比べる。
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
        // 写し: rgb = 描く値 × 0.5 + target、a = 0.5 + 0.25
        Add("copy", copy, new int[] { 191, 64, 0, 191 });
        // 組: rgb = 描く値 × 0.5 + target × 0.5、a = 0.5 + 0.25 × 0.5
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
            // 2 行目の左端 (左半分に描いた quad の中)
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
