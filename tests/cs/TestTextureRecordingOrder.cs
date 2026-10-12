// 1 フレームの中で同じ key の texture を違う画素で宣言し直し、それぞれの後に
// draw で sample する。draw は宣言を記録した順の内容を読むので、赤で宣言した
// 後の左半分は赤、緑で宣言し直した後の右半分は緑になる。書き込みが記録順を
// 追い越す backend では両方とも最後の緑になる。
// - rewrite: 同じ大きさで書き直す。
// - resize: 大きさの違う画素で宣言し直し、draw が使った texture を作り直す。
// 加えて、使った直後の texture を大きさを変えて宣言し直す。color target、
// depth target、compute が書く storage texture の 3 つ。resize とこれらは
// GPU が使う前の資源を消すので、消すのが早すぎる backend では validation
// error になる。
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

    static Dictionary<string, object> Side(TextureRef tex, float side)
    {
        return new Dictionary<string, object>
        {
            ["tex"] = tex,
            ["uniforms"] = new Dictionary<string, object> { ["side"] = new float[] { side, 0, 0, 0 } },
        };
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
        Gfx.Dispatch(1, 1, 1,
            new Dictionary<string, object> { ["dst"] = storage! },
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
