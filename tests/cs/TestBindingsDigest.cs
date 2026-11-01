// 同じ宣言は毎回 new でも使い回しでも Clear し直しても (変更前の Dictionary でも) 同じ C API の呼び出しになる。
// env LUB_TEST_BINDINGS_MODE = new / reuse / clear / dict。
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

    static string mode = "";
    static int frames;
    static Bindings? kept;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        mode = Environment.GetEnvironmentVariable("LUB_TEST_BINDINGS_MODE") ?? "reuse";
    }

    // k 番目の draw。
    // 奇数の draw は extra を渡し、偶数の draw は同じ名前で長さを変える。
    static void DrawOne(int k, ShaderRef sh, TextureRef tex, DrawOpts opts)
    {
        var tint = new Vec4(k * 0.25f, 0, 1, 1);
        var cell = new Vec4(k, 0, 4, 1);
        var extra = new Vec2(1, 2);
        if (mode == "dict")
        {
            var u = new Dictionary<string, object>
            {
                ["cell"] = new List<float> { cell.X, cell.Y, cell.Z, cell.W },
                ["tint"] = new List<float> { tint.X, tint.Y, tint.Z, tint.W },
                ["extra"] = k % 2 == 1 ? new List<float> { extra.X, extra.Y } : new List<float> { extra.X },
            };
            Gfx.Draw(6, new Dictionary<string, object> { ["tex"] = tex, ["uniforms"] = u }, opts);
            return;
        }
        var b = mode == "new" ? new Bindings() : kept!;
        if (mode == "clear")
            b.Clear();
        b.Set("tex", tex);
        b.Set("cell", cell);
        b.Set("tint", tint);
        if (k % 2 == 1)
            b.Set("extra", extra);
        else
            b.Set("extra", extra.X);
        Gfx.DrawBound(6, b, opts);
    }

    // 名前の組が draw ごとに違う draw。
    // 同じ Bindings を使い回すなら Clear が要る。
    static void DrawFewer(ShaderRef sh, TextureRef tex, DrawOpts opts)
    {
        var cell = new Vec4(0, 0, 4, 1);
        if (mode == "dict")
        {
            Gfx.Draw(6, new Dictionary<string, object> { ["tex"] = tex, ["uniforms"] = new Dictionary<string, object> { ["cell"] = new List<float> { cell.X, cell.Y, cell.Z, cell.W } } }, opts);
            return;
        }
        var b = mode == "new" ? new Bindings() : kept!;
        b.Clear();
        b.Set("tex", tex);
        b.Set("cell", cell);
        Gfx.DrawBound(6, b, opts);
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
        if (sh == null)
            return;
        var tex = Gfx.UseTexture("tex", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 255, 255, 255, 255 }, 1, new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;
        var rt = BindingsTestUtil.Target("rt", 4, 1);
        if (kept == null) kept = new Bindings();
        var opts = BindingsTestUtil.Opts(sh);
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        for (int k = 0; k < 4; k++) DrawOne(k, sh, tex, opts);
        DrawFewer(sh, tex, opts);
        for (int k = 0; k < 4; k++) DrawOne(k, sh, tex, opts);
        Gfx.EndPass();
        BindingsTestUtil.EmptyMainPass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
