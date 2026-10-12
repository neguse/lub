// Draw の InstanceCount に 0 以下を渡すと draw 自体が skip される
// (stub の記述どおり)。1 なら描く。render target に描いて読み戻して確かめる。
// 1 フレームに 1 通りずつ描いて読み戻しを積み、結果が届くまで poll する。
using System;
using System.Collections.Generic;
using Lub;

public static class TestInstanceCountZero
{
    static readonly int[] counts = new int[] { 0, -3, 1 };
    static readonly int[] wantR = new int[] { 255, 255, 0 };
    static readonly int[] wantG = new int[] { 0, 0, 255 };
    static readonly string[] whats = new string[]
    {
        "instance_count = 0 must not draw",
        "instance_count < 0 must not draw",
        "instance_count = 1 must draw",
    };

    static Readback? rb;
    static int stage = 1;
    static bool requested;
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("test");
    }

    public static void OnFrame(float dt)
    {
        if (stage > counts.Length)
        {
            Check.Pass();
            return;
        }
        frames++;
        Check.That(frames < 300, "readback " + stage + " never became ready");
        Io.LoadText("tests/cs/data/test_instance_count_zero.vs.slang", out var vs, out var verVs, out _, out _);
        Io.LoadText("tests/cs/data/test_instance_count_zero.fs.slang", out var fs, out var verFs, out _, out _);
        Check.That(vs != null && fs != null, "shader source missing");
        var s = Gfx.UseShader("ic_sh", vs!, fs!, verVs ^ verFs);
        // 画面全体を覆う三角形
        var verts = Gfx.UseBuffer("ic_vb", Gfx.BufferType.Storage, new List<float> { -1, -1, 3, -1, -1, 3 }, 1);

        int ci = stage - 1;
        var tex = Gfx.UseTexture("ic_rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        Gfx.BeginPass(new PassOpts { Target = tex, ClearColor = new float[] { 1.0f, 0.0f, 0.0f, 1.0f } });
        Gfx.Draw(3, new Dictionary<string, object> { ["verts"] = verts! }, new DrawOpts { Shader = s!, Cull = Gfx.Cull.None, InstanceCount = counts[ci] });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        int? req = null;
        if (!requested)
            req = stage;
        Gfx.ReadTexture(rb!, tex!, req, out var st, out var bytes, out var w, out var h, out var fmt, out var stride, out var rid, out _, out var err);
        requested = true;
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready)
            return;
        Check.That(rid == stage, "readback id " + rid + ", want " + stage);
        Check.That(w == 4 && h == 4 && fmt == Gfx.PixelFormat.Rgba8, "unexpected readback shape");
        int o = 1 * stride + 1 * 4;
        int r = bytes!.Get(o);
        int g = bytes.Get(o + 1);
        int b = bytes.Get(o + 2);
        int a = bytes.Get(o + 3);
        Check.That(r == wantR[ci] && g == wantG[ci] && b == 0 && a == 255, whats[ci] + " (got " + r + "," + g + "," + b + "," + a + ")");
        stage++;
        requested = false;
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
