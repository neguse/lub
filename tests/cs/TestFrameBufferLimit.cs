// FrameBuffer の 1 回の上限は 128 MiB で、上限までは描け、超えると error で止まること。
// 128 MiB の float の配列と、それを写した FrameBuffer を持つので、web の A9 では走らせない。
using System;
using Lub;

public static class TestFrameBufferLimit
{
    const int MaxFloats = 128 * 1024 * 1024 / 4;
    static float[]? data;
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
        data = new float[MaxFloats + 4];
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Io.LoadText("tests/cs/data/test_buffer_recording_order.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_recording_order.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs)!;
        frame++;
        if (frame == 1)
        {
            var bindings = new Bindings();
            bindings.Set("col", Gfx.FrameBufferArray(Gfx.BufferType.Storage, data!, MaxFloats));
            bindings.Set("side", new Vec4(0, 0, 0, 0));
            Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
            Gfx.Draw(6, bindings, new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None });
            Gfx.EndPass();
        }
        else if (frame == 2)
        {
            Check.Error("a range over the limit must fail", "over the limit");
            Gfx.FrameBufferArray(Gfx.BufferType.Storage, data!, MaxFloats + 4);
        }
        else
        {
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
