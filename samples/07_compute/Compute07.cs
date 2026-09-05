// lub の samples/07_compute の entry。
// 実行: lub samples/07_compute/Compute07.csproj (transpile + watch + hot reload)
//
// Compute writes 3 vertices (vec4 = position.xy + color.rg) into a storage
// buffer. The render pass then rebinds the same buffer as a VERTEX buffer
// and draws a triangle whose vertex positions/colors came from the GPU.

using System.Collections.Generic;
using System;
using static Lub;

public static class Compute07
{
    // 3 vertices * 4 floats (vec2 pos + vec2 col) = 12 floats.
    const int vertFloats = 12;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnFrame(double dt)
    {
        Io.LoadText("samples/07_compute/data/07_gen_verts.cs.slang",
            out var cs, out var csv, out _, out _);
        Io.LoadText("samples/07_compute/data/07_render.vs.slang",
            out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/07_compute/data/07_render.fs.slang",
            out var fs, out var fsv, out _, out _);
        if (cs == null || vs == null || fs == null) return;

        var vbuf = Gfx.UseBufferEmpty("compute_verts", Gfx.BufferType.Storage, vertFloats, 1);
        var shC = Gfx.UseShaderCompute("gen", cs, csv);
        var shR = Gfx.UseShader("render", vs, fs, vsv * 31 + fsv);
        if (vbuf == null || shC == null || shR == null) return;

        Gfx.Dispatch(1, 1, 1,
            new Dictionary<string, object> { ["out_verts"] = vbuf },
            new DispatchOpts { Shader = shC });

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.05, 0.05, 0.1, 1.0 },
        });
        Gfx.Draw(3,
            new Dictionary<string, object> { ["verts"] = vbuf },
            new DrawOpts { Shader = shR, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }
}
