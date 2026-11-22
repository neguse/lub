// Passing a reference (old ref) to a resource that was swept after its declarations stopped to draw bindings or as a BeginPass target is an error, instead of silently dropping the binding.
// Even after redeclaring with the same key, the old ref stays an error; use the new ref returned by the declaration.
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
    static readonly List<float> verts = new List<float> { -1, -1, 3, -1, -1, 3 };

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", ResourceSweepAfterFrames = sweep });
    }

    static void DrawSwept()
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        var bindings = new Bindings();
        bindings.Set("verts", buf!);
        Gfx.Draw(3, bindings, new DrawOpts { Shader = shader!, Cull = Gfx.Cull.None });
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
            buf = Gfx.UseBuffer("swept_buf", Gfx.BufferType.Storage, verts, 1);
            tex = Gfx.UseTexture("swept_tex", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
            return;
        }
        // frame 2..5: do not declare (only the shader is declared every frame to keep it alive)
        shader = Gfx.UseShader("swept_sh", vs, fs, 1);
        if (frame < 6)
            return;

        // One expected error per callback.
        // Check the error and the binding name separately
        if (frame == 6)
        {
            Check.Error("draw with a swept buffer must say swept", "swept");
            DrawSwept();
        }
        else if (frame == 7)
        {
            Check.Error("draw with a swept buffer must name the binding", "verts");
            DrawSwept();
        }
        else if (frame == 8)
        {
            Check.Error("begin_pass with a swept texture must say swept", "swept");
            Gfx.BeginPass(new PassOpts { Target = tex, ClearColor = new float[] { 0, 0, 0, 1 } });
        }
        else if (frame == 9)
        {
            Check.Error("begin_pass with a swept texture must name the target", "target");
            Gfx.BeginPass(new PassOpts { Target = tex, ClearColor = new float[] { 0, 0, 0, 1 } });
        }
        else if (frame == 10)
        {
            Redeclare();
            Check.Error("draw with the old buffer ref after redeclaring must say swept", "swept");
            DrawSwept();
        }
        else if (frame == 11)
        {
            Redeclare();
            Check.Error("begin_pass with the old texture ref after redeclaring must say swept", "swept");
            Gfx.BeginPass(new PassOpts { Target = tex, ClearColor = new float[] { 0, 0, 0, 1 } });
        }
        else
        {
            // The new ref returned by the declaration can be used
            var newBuf = Gfx.UseBuffer("swept_buf", Gfx.BufferType.Storage, verts, 1);
            var newTex = Gfx.UseTexture("swept_tex", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
            Gfx.BeginPass(new PassOpts { Target = newTex, ClearColor = new float[] { 0, 0, 0, 1 } });
            var bindings = new Bindings();
            bindings.Set("verts", newBuf!);
            Gfx.Draw(3, bindings, new DrawOpts { Shader = shader!, Cull = Gfx.Cull.None });
            Gfx.EndPass();
            Check.Pass();
        }
    }

    static void Redeclare()
    {
        Gfx.UseBuffer("swept_buf", Gfx.BufferType.Storage, verts, 1);
        Gfx.UseTexture("swept_tex", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
