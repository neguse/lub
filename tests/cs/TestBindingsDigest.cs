// 同じ宣言は毎回 new でも使い回しでも Clear し直しても同じ C API の呼び出しになる。
// env LUB_TEST_BINDINGS_MODE = new / reuse / clear。
// reuse と clear は同じ Bindings を使い、clear は draw ごとに Clear して全部 Set し直す。
// digest の比較は scripts/test-bindings-digest.sh が行う。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBindingsDigest
{
    const string Fs = @"
struct FsUniforms { float4 tint; float2 extra; };
ConstantBuffer<FsUniforms> fu;
LUB_TEXTURE2D(tex);
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target {
  return float4(fu.tint.rgb + float3(fu.extra, 0.0) * 0.0, 1.0) * LUB_SAMPLE(tex, float2(0.5, 0.5));
}";

    // extra が float の shader (同じ名前で値の長さを変える draw 用)
    const string Fs1 = @"
struct FsUniforms { float4 tint; float extra; };
ConstantBuffer<FsUniforms> fu;
LUB_TEXTURE2D(tex);
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target {
  return float4(fu.tint.rgb + fu.extra * 0.0, 1.0) * LUB_SAMPLE(tex, float2(0.5, 0.5));
}";

    static string mode = "";
    static int frames;
    static Bindings? kept;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        mode = Environment.GetEnvironmentVariable("LUB_TEST_BINDINGS_MODE") ?? "reuse";
    }

    // k 番目の draw。
    // 奇数の draw は extra を float2 で渡し、偶数の draw は同じ名前で長さを変えて、extra が float の shader で描く。
    static void DrawOne(int k, DrawOpts opts2, DrawOpts opts1, TextureRef tex)
    {
        var tint = new Vec4(k * 0.25f, 0, 1, 1);
        var cell = new Vec4(k, 0, 4, 1);
        var extra = new Vec2(1, 2);
        var b = mode == "new" ? new Bindings() : kept!;
        if (mode == "clear")
            b.Clear();
        b.Set("tex", tex);
        b.Set("cell", cell);
        b.Set("tint", tint);
        if (k % 2 == 1)
        {
            b.Set("extra", extra);
            Gfx.Draw(6, b, opts2);
        }
        else
        {
            b.Set("extra", extra.X);
            Gfx.Draw(6, b, opts1);
        }
    }

    // 名前の組が draw ごとに違う draw (texture を渡さず、既定の texture で描く)。
    // 同じ Bindings を使い回すなら Clear が要る。
    static void DrawFewer(DrawOpts opts2)
    {
        var b = mode == "new" ? new Bindings() : kept!;
        b.Clear();
        b.Set("cell", new Vec4(0, 0, 4, 1));
        b.Set("tint", new Vec4(0, 0, 1, 1));
        b.Set("extra", new Vec2(1, 2));
        Gfx.Draw(6, b, opts2);
    }

    public static void OnFrame(float dt)
    {
        frames++;
        if (frames > 20)
        {
            Check.Pass();
            return;
        }
        var sh = Gfx.UseShader("sh", BindingsTestUtil.Vs, Fs, 1);
        var sh1 = Gfx.UseShader("sh1", BindingsTestUtil.Vs, Fs1, 1);
        if (sh == null || sh1 == null)
            return;
        var tex = Gfx.UseTexture("tex", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 255, 255, 255, 255 }, 1, new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;
        var rt = BindingsTestUtil.Target("rt", 4, 1);
        if (kept == null) kept = new Bindings();
        var opts2 = BindingsTestUtil.Opts(sh);
        var opts1 = BindingsTestUtil.Opts(sh1);
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        for (int k = 0; k < 4; k++) DrawOne(k, opts2, opts1, tex);
        DrawFewer(opts2);
        for (int k = 0; k < 4; k++) DrawOne(k, opts2, opts1, tex);
        Gfx.EndPass();
        BindingsTestUtil.EmptyMainPass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
