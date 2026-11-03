// DrawOpts.Blend の式が色と alpha の両方にそのまま効くこと。
// 各 case は 4x4 の target を clear して左半分に 1 回描き、左の画素の rgba を読む。
// alpha の係数に色の係数を使う組と、係数が One でない Min / Max は error になること。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBlend
{
    sealed class Case
    {
        public string Name = "";
        public float[] Clear = new float[4];
        public float[] Color = new float[4];
        public Blend Blend = new Blend();
        public int[] Want = new int[4];
        public Readback? Rb;
        public bool Done;
    }

    static readonly List<Case> cases = new List<Case>();
    static readonly List<string> mismatches = new List<string>();
    static ShaderRef? sh;
    static int frame;
    static bool requested;

    static Blend Make(Gfx.BlendFactor src, Gfx.BlendFactor dst, Gfx.BlendOp op,
        Gfx.BlendFactor srcAlpha, Gfx.BlendFactor dstAlpha, Gfx.BlendOp opAlpha)
    {
        return new Blend { Src = src, Dst = dst, Op = op, SrcAlpha = srcAlpha, DstAlpha = dstAlpha, OpAlpha = opAlpha };
    }

    static void Add(string name, float[] clear, float[] color, Blend blend, int[] want)
    {
        cases.Add(new Case { Name = name, Clear = clear, Color = color, Blend = blend, Want = want, Rb = Gfx.Readback("blend_" + name) });
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
        var zero = new float[] { 0, 0, 0, 0 };
        var half = new float[] { 0.5f, 0.5f, 0.5f, 0.5f };
        // rgb = 1 * 0.5, a = 0.5 + 0 * 0.5
        Add("alpha", zero, new float[] { 1, 0, 0, 0.5f }, Blends.Alpha, new int[] { 128, 0, 0, 128 });
        // alpha にも SrcAlpha を掛けると a = 0.5 * 0.5
        Add("alpha_src_alpha", zero, new float[] { 1, 0, 0, 0.5f },
            Make(Gfx.BlendFactor.SrcAlpha, Gfx.BlendFactor.OneMinusSrcAlpha, Gfx.BlendOp.Add,
                Gfx.BlendFactor.SrcAlpha, Gfx.BlendFactor.OneMinusSrcAlpha, Gfx.BlendOp.Add),
            new int[] { 128, 0, 0, 64 });
        Add("additive", new float[] { 0.25f, 0.25f, 0, 0.25f }, new float[] { 0.5f, 0, 0, 0.5f }, Blends.Additive,
            new int[] { 191, 64, 0, 191 });
        // 色も alpha も target の値を掛ける
        Add("multiply", half, new float[] { 0.5f, 1, 0, 0.5f }, Blends.Multiply, new int[] { 64, 128, 0, 64 });
        Add("max", half, new float[] { 0.25f, 0.75f, 0, 1 },
            Make(Gfx.BlendFactor.One, Gfx.BlendFactor.One, Gfx.BlendOp.Max,
                Gfx.BlendFactor.One, Gfx.BlendFactor.One, Gfx.BlendOp.Max),
            new int[] { 128, 191, 128, 255 });
        // target - 描く値
        Add("reverse_subtract", new float[] { 0.75f, 0.75f, 0.75f, 0.75f }, new float[] { 0.25f, 0.5f, 0, 0.25f },
            Make(Gfx.BlendFactor.One, Gfx.BlendFactor.One, Gfx.BlendOp.ReverseSubtract,
                Gfx.BlendFactor.One, Gfx.BlendFactor.One, Gfx.BlendOp.ReverseSubtract),
            new int[] { 128, 64, 191, 128 });
    }

    static void Draw(TextureRef target, float[] clear, float[] color, Blend blend)
    {
        Gfx.BeginPass(new PassOpts { Target = target, ClearColor = clear });
        var b = new Bindings();
        b.Set("col", Gfx.FrameBufferArray(Gfx.BufferType.Storage, color, 4));
        b.Set("side", new Vec4(0, 0, 0, 0));
        Gfx.Draw(6, b, new DrawOpts { Shader = sh!, Depth = false, Cull = Gfx.Cull.None, Blend = blend });
        Gfx.EndPass();
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Check.That(frame < 300, "test_blend: readback never became ready");
        Io.LoadText("tests/cs/data/test_buffer_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var rtOpts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var zero = new float[] { 0, 0, 0, 0 };
        var red = new float[] { 1, 0, 0, 1 };
        frame++;
        if (frame == 1)
        {
            var rt = Gfx.UseTexture("rt_error", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, rtOpts)!;
            Check.Error("a color factor for alpha must fail", "alpha factors are");
            Draw(rt, zero, red, Make(Gfx.BlendFactor.One, Gfx.BlendFactor.Zero, Gfx.BlendOp.Add,
                Gfx.BlendFactor.Src, Gfx.BlendFactor.Zero, Gfx.BlendOp.Add));
            return;
        }
        if (frame == 2)
        {
            var rt = Gfx.UseTexture("rt_error", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, rtOpts)!;
            Check.Error("Min with a factor other than One must fail", "Min / Max need factor One");
            Draw(rt, zero, red, Make(Gfx.BlendFactor.SrcAlpha, Gfx.BlendFactor.One, Gfx.BlendOp.Min,
                Gfx.BlendFactor.One, Gfx.BlendFactor.Zero, Gfx.BlendOp.Add));
            return;
        }

        var allDone = true;
        foreach (var c in cases)
        {
            var rt = Gfx.UseTexture("rt_" + c.Name, 4, 4, Gfx.PixelFormat.Rgba8, null, 1, rtOpts)!;
            Draw(rt, c.Clear, c.Color, c.Blend);
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
                    mismatches.Add($"{c.Name}: got ({got[0]}, {got[1]}, {got[2]}, {got[3]}) want ({c.Want[0]}, {c.Want[1]}, {c.Want[2]}, {c.Want[3]})");
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
