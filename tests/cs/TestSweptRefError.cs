// 宣言が途切れて sweep された resource の参照 (古い ref) を draw の bindings や
// BeginPass の target に渡すと、黙って束縛が外れるのではなく error になる。
// 宣言し直せば同じ ref で (key から引き直して) また使える。
using System;
using System.Collections.Generic;
using Lub;

public static class TestSweptRefError
{
    const int sweep = 2;

    static int frame;
    static BufferRef? buf;
    static TextureRef? tex;
    static ShaderRef? shader;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", ResourceSweepAfterFrames = sweep });
    }

    static void DrawSwept()
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(3, new Dictionary<string, object> { ["verts"] = buf! }, new DrawOpts { Shader = shader!, Cull = Gfx.Cull.None });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        Io.LoadText("tests/cs/data/test_swept_ref_error.vs.slang", out var vs, out _, out _, out _);
        Io.LoadText("tests/cs/data/test_swept_ref_error.fs.slang", out var fs, out _, out _, out _);
        if (vs == null || fs == null)
            return;
        if (frame == 1)
        {
            buf = Gfx.UseBuffer("swept_buf", Gfx.BufferType.Storage, new List<float> { -1, -1, 3, -1, -1, 3 }, 1);
            tex = Gfx.UseTexture("swept_tex", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
            return;
        }
        // frame 2..5: 宣言しない (shader だけ毎 frame 宣言して生かす)
        shader = Gfx.UseShader("swept_sh", vs, fs, 1);
        if (frame < 6)
            return;

        // 期待するエラーは 1 回の callback に 1 つ。error / key 名 / frame 数を別々に見る
        if (frame == 6)
        {
            Check.Error("draw with a swept buffer must say swept", "swept");
            DrawSwept();
        }
        else if (frame == 7)
        {
            Check.Error("draw with a swept buffer must name the key", "swept_buf");
            DrawSwept();
        }
        else if (frame == 8)
        {
            Check.Error("draw with a swept buffer must say how many frames", sweep.ToString());
            DrawSwept();
        }
        else if (frame == 9)
        {
            Check.Error("begin_pass with a swept texture must say swept", "swept");
            Gfx.BeginPass(new PassOpts { Target = tex, ClearColor = new float[] { 0, 0, 0, 1 } });
        }
        else if (frame == 10)
        {
            Check.Error("begin_pass with a swept texture must name the key", "swept_tex");
            Gfx.BeginPass(new PassOpts { Target = tex, ClearColor = new float[] { 0, 0, 0, 1 } });
        }
        else
        {
            // 宣言し直すと古い ref も key から引き直せる
            Gfx.UseBuffer("swept_buf", Gfx.BufferType.Storage, new List<float> { -1, -1, 3, -1, -1, 3 }, 1);
            Gfx.UseTexture("swept_tex", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
            Gfx.BeginPass(new PassOpts { Target = tex, ClearColor = new float[] { 0, 0, 0, 1 } });
            Gfx.Draw(3, new Dictionary<string, object> { ["verts"] = buf! }, new DrawOpts { Shader = shader!, Cull = Gfx.Cull.None });
            Gfx.EndPass();
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
