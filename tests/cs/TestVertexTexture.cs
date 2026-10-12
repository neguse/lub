// vertex shader が LUB_TEXTURE2D を sample する (neguse/lub#68)。
// 2x1 の texture を vertex shader で引き、左半分の quad は texel 0、右半分の
// quad は texel 1 の色を頂点色として出す。2x1 の target を読み戻して確かめる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestVertexTexture
{
    static Readback? rb;
    static bool done;
    static int frames;

    static readonly List<int> texels = new List<int> { 255, 64, 0, 255, 0, 128, 255, 255 };

    // 2 三角形 (6 頂点)。全頂点が同じ uv なので quad 内は一色になる
    static void Quad(List<float> v, float x0, float x1, float u)
    {
        var xs = new float[] { x0, x1, x1, x0, x1, x0 };
        var ys = new float[] { -1, -1, 1, -1, 1, 1 };
        for (var i = 0; i < 6; i++)
        {
            v.Add(xs[i]);
            v.Add(ys[i]);
            v.Add(u);
            v.Add(0.5f);
        }
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("test");
    }

    public static void OnFrame(float dt)
    {
        if (done)
        {
            Check.Pass();
            return;
        }
        frames++;
        Check.That(frames < 300, "read_texture never became ready");
        Io.LoadText("tests/cs/data/test_vertex_texture.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_vertex_texture.fs.slang", out var fs, out var verFs, out _, out _);
        Check.That(vs != null && fs != null, "shader source missing");
        if (vs == null || fs == null) return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        var texOpts = new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
        var tex = Gfx.UseTexture("tex", 2, 1, Gfx.PixelFormat.Rgba8, texels, 1, texOpts);
        var rt = Gfx.UseTexture("rt", 2, 1, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        var verts = new List<float>();
        Quad(verts, -1, 0, 0.25f);
        Quad(verts, 0, 1, 0.75f);
        var vb = Gfx.UseBuffer("vb", Gfx.BufferType.Storage, verts, 1);
        if (sh == null || tex == null || rt == null || vb == null) return;

        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f } });
        Gfx.Draw(12,
            new Dictionary<string, object> { ["verts"] = vb, ["tex"] = tex },
            new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f } });
        Gfx.EndPass();

        // 1 フレーム目に積み、結果が届くまで poll する
        int? req = frames == 1 ? 1 : null;
        Gfx.ReadTexture(rb!, rt, req, out var st, out var bytes, out var w, out var h, out var fmt,
            out var stride, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(bytes != null, "read_texture returned nil");
        if (bytes == null) return;
        Check.That(id == 1 && w == 2 && h == 1 && fmt == Gfx.PixelFormat.Rgba8 && stride == 8, "unexpected readback shape");
        for (var i = 0; i < 8; i++)
        {
            var got = bytes.Get(i);
            Check.That(Math.Abs(got - texels[i]) <= 1, $"byte {i + 1}: got {got}, want {texels[i]}");
        }
        done = true;
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
