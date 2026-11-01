// Bindings に入れたままの buffer / texture の handle が、作り直された実体を指す。
// 一度だけ Set し、段階 1 → 2 で buffer と texture の version を上げて大きさを変える (handle は同じで実体が変わる)。
// 段階 3 は key の宣言を止めて sweep させ、古い handle の draw が error になること、宣言し直した handle を Set すれば描けることを見る。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBindingsRecreate
{
    const string Fs = @"
StructuredBuffer<float4> item;
LUB_TEXTURE2D(tex);
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target {
  return float4(item[0].r, LUB_SAMPLE(tex, float2(0.5, 0.5)).g, 0.0, 1.0);
}";

    const int sweep = 2;

    static Readback? rb;
    static Bindings? kept;
    static int frames;
    static int stage = 1;
    static int requested;
    static int sweepWait;
    static BufferRef? firstBuffer;
    static TextureRef? firstTexture;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", ResourceSweepAfterFrames = sweep });
        rb = Gfx.Readback("bindings_recreate");
    }

    // 段階 1: R = 1、G = 0 の小さい実体。段階 2 以降: R = 0、G = 1 の大きい実体。
    static BufferRef DeclareBuffer(string key, int v)
    {
        var data = new List<float>();
        int n = v == 1 ? 1 : 64;
        for (int i = 0; i < n; i++)
        {
            data.Add(v == 1 ? 1 : 0);
            data.Add(0);
            data.Add(0);
            data.Add(0);
        }
        return Gfx.UseBuffer(key, Gfx.BufferType.Storage, data, v)!;
    }

    static TextureRef DeclareTexture(string key, int v)
    {
        var px = new List<int>();
        int n = v == 1 ? 1 : 4;
        for (int i = 0; i < n * n; i++)
        {
            px.Add(0);
            px.Add(v == 1 ? 0 : 255);
            px.Add(0);
            px.Add(255);
        }
        return Gfx.UseTexture(key, n, n, Gfx.PixelFormat.Rgba8, px, v, new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;
    }

    static void Expect(Bytes bytes, int r, int g, string what)
    {
        Check.That(BindingsTestRead.Is(bytes, 0, r, g, 0), $"{what}: want {r} {g} 0, got {bytes.Get(0)} {bytes.Get(1)} {bytes.Get(2)}");
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frames++;
        Check.That(frames < 600, "test_bindings_recreate: stage " + stage + " never finished");
        var sh = Gfx.UseShader("sh", BindingsTestUtil.Vs, Fs, 1);
        if (sh == null)
            return;
        var rt = BindingsTestUtil.Target("rt", 1, 1);
        var opts = BindingsTestUtil.Opts(sh);

        if (stage <= 2)
        {
            var buf = DeclareBuffer("item", stage);
            var tex = DeclareTexture("tex", stage);
            if (kept == null)
            {
                kept = new Bindings();
                kept.Set("cell", new Vec4(0, 0, 1, 1));
                kept.Set("item", buf);
                kept.Set("tex", tex);
                firstBuffer = buf;
                firstTexture = tex;
            }
        }
        else if (stage == 3)
        {
            // "item" の宣言を止めて sweep を待つ
            sweepWait++;
            BindingsTestUtil.EmptyMainPass();
            if (sweepWait < sweep + 4)
                return;
            Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
            stage = 4;
            Check.Error("swept buffer in kept Bindings", "buffer 'item' was swept");
            Gfx.Draw(6, kept!, opts);
            return;
        }
        else
        {
            // 同じ key の宣言は別の handle になるので Set し直す
            var buf = DeclareBuffer("item", 2);
            var tex = DeclareTexture("tex", 2);
            kept!.Set("item", buf);
            kept!.Set("tex", tex);
        }

        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(6, kept!, opts);
        Gfx.EndPass();
        BindingsTestUtil.EmptyMainPass();

        int? req = requested < stage ? stage : (int?)null;
        requested = stage;
        Gfx.ReadTexture(rb!, rt, req, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready || id != stage)
            return;
        if (stage == 1)
        {
            Expect(bytes!, 255, 0, "first declaration");
            stage = 2;
        }
        else if (stage == 2)
        {
            Expect(bytes!, 0, 255, "recreated with the same handle");
            stage = 3;
        }
        else
        {
            Expect(bytes!, 0, 255, "declared again after the sweep");
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
