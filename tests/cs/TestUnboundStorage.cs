// A draw and a dispatch that do not pass the storage textures and compute resources the shader declared.
// The backend fills the slots that were not passed with placeholder resources, so the frame is not discarded and draws (the remaining kinds from TestUnboundBindings).
// - draw: does not pass the storage texture the fragment shader writes (a draw has no way to pass a storage texture).
//   Draws with the green of the bound col.
// - dispatch: passes no texture, StructuredBuffer, or storage texture, and writes green to the bound dst.
//   Draws with dst as col.
// In both cases the pixels of the left half become green.
using System;
using System.Collections.Generic;
using Lub;

public static class TestUnboundStorage
{
    static readonly string[] cases = new string[] { "draw", "dispatch" };
    static readonly List<Readback> rbs = new List<Readback>();
    static readonly bool[] done = new bool[2];
    static readonly List<string> mismatches = new List<string>();
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        for (var i = 0; i < cases.Length; i++)
            rbs.Add(Gfx.Readback("unbound_storage_" + cases[i])!);
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_unbound_storage: readback never became ready");

        Io.LoadText("tests/cs/data/test_buffer_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        Io.LoadText("tests/cs/data/test_unbound_storage.fs.slang", out var fsS, out var verFsS, out _, out _);
        Io.LoadText("tests/cs/data/test_unbound_storage.cs.slang", out var cs, out var verCs, out _, out _);
        if (vs == null || fs == null || fsS == null || cs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs)!;
        var shS = Gfx.UseShader("sh_storage", vs, fsS, verVs ^ verFsS)!;
        var shC = Gfx.UseShaderCompute("cs", cs, verCs)!;
        var rtOpts = new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var rts = new List<TextureRef>();
        for (var i = 0; i < cases.Length; i++)
            rts.Add(Gfx.UseTexture("rt_" + cases[i], 4, 4, Gfx.PixelFormat.Rgba8, null, 1, rtOpts)!);
        var clear = new float[] { 0, 0, 0, 1 };
        var green = Gfx.UseBuffer("green", Gfx.BufferType.Storage, new List<float> { 0, 1, 0, 1 }, 1)!;

        Gfx.BeginPass(new PassOpts { Target = rts[0], ClearColor = clear });
        var drawn = new Bindings();
        drawn.Set("col", green);
        drawn.Set("side", new Vec4(0, 0, 0, 0));
        Gfx.Draw(6, drawn, new DrawOpts { Shader = shS, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        var dst = Gfx.UseBufferEmpty("dst", Gfx.BufferType.Storage, 4, 1)!;
        var dispatch = new Bindings();
        dispatch.Set("dst", dst);
        Gfx.Dispatch(1, 1, 1, dispatch, new DispatchOpts { Shader = shC });
        Gfx.BeginPass(new PassOpts { Target = rts[1], ClearColor = clear });
        var fromDst = new Bindings();
        fromDst.Set("col", dst);
        fromDst.Set("side", new Vec4(0, 0, 0, 0));
        Gfx.Draw(6, fromDst, new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = clear });
        Gfx.EndPass();

        var allDone = true;
        for (var i = 0; i < cases.Length; i++)
        {
            if (done[i])
                continue;
            int? req = requested ? null : 1;
            Gfx.ReadTexture(rbs[i], rts[i], req, out var st, out var bytes, out _, out _, out _,
                out var stride, out _, out _, out var err);
            Check.That(st != Gfx.ReadbackStatus.Error, cases[i] + ": read_texture failed: " + err);
            if (st == Gfx.ReadbackStatus.Ready)
            {
                var r = bytes!.Get(stride + 4);
                var g = bytes.Get(stride + 5);
                var b = bytes.Get(stride + 6);
                if (!(r == 0 && g == 255 && b == 0))
                    mismatches.Add($"{cases[i]}: ({r}, {g}, {b}) want (0, 255, 0)");
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
