// 大量の Bindings を作って捨てても、保持している Bindings の中身は GC をまたいで残る。
// 内蔵の大きさに収まらない Bindings も作り、block を大きくする経路も通す。
// 中身は host の GC が持つ block だけなので、手放した分の解放は host の GC に任せる。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBindingsLifetime
{
    const string Fs = @"
struct FsUniforms { float4 tint; float4 big[50]; };
ConstantBuffer<FsUniforms> fu;
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target { return float4(fu.tint.rgb + fu.big[49].rgb, 1.0); }";

    const int PerFrame = 2000;
    const int Frames = 50;

    static Readback? rb;
    static Bindings? kept;
    static List<float>? big;
    static int frames;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("bindings_lifetime");
        big = new List<float>();
        // big[49] だけが green で、block を大きくしたときに値が写っていれば見える
        for (int i = 0; i < 200; i++) big.Add(i == 197 ? 1 : 0);
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < Frames + 300, "test_bindings_lifetime: readback never became ready");
        var sh = Gfx.UseShader("sh", BindingsTestUtil.Vs, Fs, 1);
        if (sh == null)
            return;
        var rt = BindingsTestUtil.Target("rt", 1, 1);
        if (kept == null)
        {
            kept = new Bindings();
            kept.Set("cell", new Vec4(0, 0, 1, 1));
            kept.Set("tint", new Vec4(1, 0, 0, 1));
            kept.Set("big", big!);
            for (int j = 0; j < 8; j++) kept.Set("name_longer_than_the_inline_space_" + j, 1.0f);
        }
        for (int i = 0; i < PerFrame; i++)
        {
            var b = new Bindings();
            b.Set("v", new Vec4(1, 2, 3, 4));
            if (i % 2 == 0)
                continue;
            b.Set("big", big!);
            for (int j = 0; j < 8; j++) b.Set("name_longer_than_the_inline_space_" + j, 1.0f);
        }
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.DrawBound(6, kept, BindingsTestUtil.Opts(sh));
        Gfx.EndPass();
        BindingsTestUtil.EmptyMainPass();
        if (frames < Frames)
            return;
        int? req = requested ? null : 1;
        requested = true;
        var bytes = BindingsTestRead.Read(rb!, rt, req, out _);
        if (bytes == null)
            return;
        Check.That(BindingsTestRead.Is(bytes, 0, 255, 255, 0), $"kept Bindings changed: {bytes.Get(0)} {bytes.Get(1)} {bytes.Get(2)}");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
