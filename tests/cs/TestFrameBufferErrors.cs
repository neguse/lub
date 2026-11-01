// FrameBuffer の範囲はそのフレームの中でだけ使え、次のフレームの draw / dispatch に渡すと error になること。
// error で終わったフレームの次でも、古い範囲は古いままで、作り直した範囲は使えること。
using System;
using System.Collections.Generic;
using Lub;

public static class TestFrameBufferErrors
{
    static readonly Bindings kept = new Bindings();
    static ShaderRef? sh;
    static ShaderRef? shC;
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    static List<float> Red()
    {
        return new List<float> { 1, 0, 0, 1 };
    }

    static void Draw(Bindings b)
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(6, b, new DrawOpts { Shader = sh!, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Io.LoadText("tests/cs/data/test_buffer_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        Io.LoadText("tests/cs/data/test_frame_buffer_write.cs.slang", out var cs, out var verCs, out _, out _);
        if (vs == null || fs == null || cs == null)
            return;
        sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs);
        shC = Gfx.UseShaderCompute("cs", cs, verCs);
        frame++;
        kept.Set("side", new Vec4(0, 0, 0, 0));
        switch (frame)
        {
            case 1:
                kept.Set("col", Gfx.FrameBuffer(Gfx.BufferType.Storage, Red()));
                Draw(kept);
                break;
            case 2:
                Check.Error("a range of an earlier frame must fail", "earlier frame");
                Draw(kept);
                break;
            case 3:
                // error で終わったフレームの次も、前のフレームの範囲は使えない。
                Check.Error("a range must stay invalid after a failed frame", "earlier frame");
                Draw(kept);
                break;
            case 4:
                kept.Set("col", Gfx.FrameBuffer(Gfx.BufferType.Storage, Red()));
                Draw(kept);
                break;
            case 5:
                {
                    var write = new Bindings();
                    write.Set("col", Gfx.FrameBuffer(Gfx.BufferType.Storage, Red()));
                    Check.Error("a shader that writes the buffer must fail", "read-only");
                    Gfx.Dispatch(1, 1, 1, write, new DispatchOpts { Shader = shC! });
                    break;
                }
            case 6:
                kept.Set("col", Gfx.FrameBuffer(Gfx.BufferType.Storage, new List<float> { 1, 0, 0 }));
                Check.Error("a size that is not a multiple of the element must fail", "multiple");
                Draw(kept);
                break;
            case 7:
                Check.Error("empty data must fail", "empty data");
                Gfx.FrameBuffer(Gfx.BufferType.Storage, new List<float>());
                break;
            case 8:
                Check.Error("a uniform buffer must fail", "only INDEX/STORAGE");
                Gfx.FrameBuffer(Gfx.BufferType.Uniform, Red());
                break;
            case 9:
                kept.Set("col", Gfx.FrameBuffer(Gfx.BufferType.Storage, Red()));
                kept.Set("indices", Gfx.FrameBuffer(Gfx.BufferType.Storage, Red()));
                Check.Error("a STORAGE range as indices must fail", "'indices' must be an INDEX buffer");
                Draw(kept);
                break;
            case 10:
                Check.Error("a count past the end of the array must fail", "out of range");
                Gfx.FrameBufferArray(Gfx.BufferType.Storage, new float[] { 1, 0, 0, 1 }, 5);
                break;
            default:
                Check.Pass();
                break;
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
