// FrameBuffer の上限を超える bucket の Flush は、黙って描かずに error で止まること。
// 次のフレームは普通に描ける。
// FrameBuffer の上限 (128 MiB) を超える頂点を List<float> に積むので、web の A9 では走らせない。
using System;
using Lub;
using Lubx;

public static class TestSpriteBatchLimit
{
    const int OverLimit = 128 * 1024 * 1024 / (SpriteBatch.InstanceStride * 4) + 1;
    static SpriteBatch? batch;
    static int step;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        batch ??= new SpriteBatch("limit", 64, 64);
        if (!batch.Ensure())
            return;
        step++;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        batch.Begin();
        var sprites = step == 1 ? OverLimit : 4;
        for (var i = 0; i < sprites; i++)
            batch.Rect(0, 0, 1, 1);
        if (step == 1)
            Check.Error("a bucket over the limit must fail", "over the limit");
        batch.Flush();
        Gfx.EndPass();
        if (step == 2)
            Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
