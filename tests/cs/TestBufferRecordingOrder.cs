// 1 フレームの中で同じ key の buffer を違う内容で宣言し直し、それぞれの後に draw する。
// draw は宣言を記録した順の内容を読むので、どの case も左半分は宣言し直す前の内容、右半分は後の内容で描かれ、左が赤、右が緑になる。
// 書き込みが記録順を追い越す backend では左も後の内容で描かれる。
// - storage: draw が読む STORAGE buffer を書き直す。
// - index: INDEX buffer を書き直す。
//   前の index は左の quad、後は右の quad を
//   指す。
// - compute: compute が赤を書いた buffer を、draw の後に CPU が緑で書き直す。
// - resize: 大きさの違う内容で宣言し直し、draw が使った buffer を作り直す。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBufferRecordingOrder
{
    static readonly string[] cases = new string[] { "storage", "index", "compute", "resize" };
    static readonly List<Readback> rbs = new List<Readback>();
    static readonly bool[] done = new bool[4];
    static readonly List<string> mismatches = new List<string>();
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        for (var i = 0; i < 4; i++)
            rbs.Add(Gfx.Readback("buffer_recording_order_" + cases[i])!);
    }

    static List<int> QuadIndices(int first)
    {
        var t = new List<int>();
        for (var i = 0; i < 6; i++)
            t.Add(first + i);
        return t;
    }

    static Dictionary<string, object> Uniforms(float side)
    {
        return new Dictionary<string, object> { ["side"] = new float[] { side, 0, 0, 0 } };
    }

    static Dictionary<string, object> Col(BufferRef col, float side)
    {
        return new Dictionary<string, object> { ["col"] = col, ["uniforms"] = Uniforms(side) };
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_buffer_recording_order: readback never became ready");

        Io.LoadText("tests/cs/data/test_buffer_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.cs.slang", out var cs, out var verCs, out _, out _);
        if (vs == null || fs == null || cs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var shC = Gfx.UseShaderCompute("cs", cs, verCs);
        if (sh == null || shC == null) return;
        var opts = new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None };
        var before = frames * 2;
        var after = frames * 2 + 1;
        var red = Gfx.UseBuffer("red", Gfx.BufferType.Storage, new List<float> { 1, 0, 0, 1 }, 1)!;
        var green = Gfx.UseBuffer("green", Gfx.BufferType.Storage, new List<float> { 0, 1, 0, 1 }, 1)!;

        var rtOpts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var rts = new List<TextureRef>();
        for (var i = 0; i < 4; i++)
            rts.Add(Gfx.UseTexture("rt_" + cases[i], 4, 4, Gfx.PixelFormat.Rgba8, null, 1, rtOpts)!);

        Gfx.BeginPass(new PassOpts { Target = rts[0], ClearColor = new float[] { 0, 0, 0, 1 } });
        var col = Gfx.UseBuffer("col", Gfx.BufferType.Storage, new List<float> { 1, 0, 0, 1 }, before)!;
        Gfx.Draw(6, Col(col, 0), opts);
        col = Gfx.UseBuffer("col", Gfx.BufferType.Storage, new List<float> { 0, 1, 0, 1 }, after)!;
        Gfx.Draw(6, Col(col, 1), opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rts[1], ClearColor = new float[] { 0, 0, 0, 1 } });
        var idx = Gfx.UseBufferInts("idx", Gfx.BufferType.Index, QuadIndices(0), before)!;
        Gfx.Draw(6,
            new Dictionary<string, object> { ["indices"] = idx, ["col"] = red, ["uniforms"] = Uniforms(0) },
            opts);
        idx = Gfx.UseBufferInts("idx", Gfx.BufferType.Index, QuadIndices(6), after)!;
        Gfx.Draw(6,
            new Dictionary<string, object> { ["indices"] = idx, ["col"] = green, ["uniforms"] = Uniforms(0) },
            opts);
        Gfx.EndPass();

        var outBuf = Gfx.UseBuffer("out", Gfx.BufferType.Storage, new List<float> { 0, 0, 0, 1 }, before)!;
        Gfx.Dispatch(1, 1, 1,
            new Dictionary<string, object> { ["col"] = outBuf },
            new DispatchOpts { Shader = shC });
        Gfx.BeginPass(new PassOpts { Target = rts[2], ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(6, Col(outBuf, 0), opts);
        outBuf = Gfx.UseBuffer("out", Gfx.BufferType.Storage, new List<float> { 0, 1, 0, 1 }, after)!;
        Gfx.Draw(6, Col(outBuf, 1), opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rts[3], ClearColor = new float[] { 0, 0, 0, 1 } });
        var sized = Gfx.UseBuffer("sized", Gfx.BufferType.Storage, new List<float> { 1, 0, 0, 1 }, before)!;
        Gfx.Draw(6, Col(sized, 0), opts);
        sized = Gfx.UseBuffer("sized", Gfx.BufferType.Storage, new List<float> { 0, 1, 0, 1, 0, 0, 0, 0 }, after)!;
        Gfx.Draw(6, Col(sized, 1), opts);
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        var allDone = true;
        for (var i = 0; i < 4; i++)
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
                    mismatches.Add($"test_buffer_recording_order {cases[i]}: left ({lr}, {lg}, {lb}) want (255, 0, 0), right ({rr}, {rg}, {rb}) want (0, 255, 0)");
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
