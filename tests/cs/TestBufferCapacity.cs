// 同じ key の buffer を大きさを変えて宣言し直しても、shader から見える数は宣言した数で、内容は宣言した内容であること。
// 容量を再利用する backend で範囲を束縛し損ねると、shader の見る数が容量になって末尾の緑を読めない。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBufferCapacity
{
    static readonly int[] sizes = new int[] { 8, 9, 12, 4, 16, 2, 64, 3, 65 };
    static Readback? rb;
    static int step;
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("buffer_capacity");
    }

    // 最後の要素だけ緑で、他は赤。
    static List<float> Colors(int n)
    {
        var data = new List<float>();
        for (var i = 0; i < n; i++)
        {
            data.Add(i == n - 1 ? 0 : 1);
            data.Add(i == n - 1 ? 1 : 0);
            data.Add(0);
            data.Add(1);
        }
        return data;
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 600, "test_buffer_capacity: readback never became ready");
        Io.LoadText("tests/cs/data/test_buffer_capacity.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_buffer_capacity.fs.slang", out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null)
            return;
        var sh = Gfx.UseShader("sh", vs, fs, verVs ^ verFs)!;
        var rt = Gfx.UseTexture("rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;
        var col = Gfx.UseBuffer("col", Gfx.BufferType.Storage, Colors(sizes[step]), step + 1)!;

        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        var bindings = new Bindings();
        bindings.Set("col", col);
        bindings.Set("declared", new Vec4(sizes[step], 0, 0, 0));
        Gfx.Draw(3, bindings, new DrawOpts { Shader = sh, Depth = false, Cull = Gfx.Cull.None });
        Gfx.EndPass();
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        int? req = requested ? null : step + 1;
        Gfx.ReadTexture(rb!, rt, req, out var st, out var bytes, out _, out _, out _,
            out _, out var id, out _, out var err);
        requested = true;
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(id == step + 1, "unexpected readback id");
        Check.That(bytes!.Get(0) == 0 && bytes.Get(1) == 255,
            $"step {step}: {sizes[step]} elements: got ({bytes.Get(0)}, {bytes.Get(1)}, {bytes.Get(2)})");
        step++;
        requested = false;
        if (step == sizes.Length)
            Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
