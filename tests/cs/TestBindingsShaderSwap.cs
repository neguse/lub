// shader を差し替えても Bindings は古い解決結果を使わない。
// a = red、b = green を一度だけ Set し、同じ key の shader の version を上げて uniform の並びと使う値を変える。
// version 1 は a を読み、version 2 は並びを入れ替えて a を読み (offset が変わる)、version 3 は b を読む。
// version 4 は version 3 まで使っていなかった texture の名前を読む。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBindingsShaderSwap
{
    const string FsAB = @"
struct FsUniforms { float4 a; float4 b; };
ConstantBuffer<FsUniforms> fu;
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target { return float4(fu.a.rgb + fu.b.rgb * 0.0, 1.0); }";
    const string FsBA = @"
struct FsUniforms { float4 b; float4 a; };
ConstantBuffer<FsUniforms> fu;
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target { return float4(fu.a.rgb + fu.b.rgb * 0.0, 1.0); }";
    const string FsReadB = @"
struct FsUniforms { float4 a; float4 b; };
ConstantBuffer<FsUniforms> fu;
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target { return float4(fu.b.rgb + fu.a.rgb * 0.0, 1.0); }";
    const string FsTex = @"
LUB_TEXTURE2D(tex);
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target { return LUB_SAMPLE(tex, float2(0.5, 0.5)); }";

    static Readback? rb;
    static Bindings? kept;
    static int frames;
    static int version = 1;
    static int requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("bindings_shader_swap");
    }

    static string Source(int v)
    {
        if (v == 1) return FsAB;
        if (v == 2) return FsBA;
        if (v == 3) return FsReadB;
        return FsTex;
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 600, "test_bindings_shader_swap: readback never became ready");
        var sh = Gfx.UseShader("sh", BindingsTestUtil.Vs, Source(version), version);
        if (sh == null)
            return;
        var blue = Gfx.UseTexture("blue", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 0, 0, 255, 255 }, 1, new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;
        var rt = BindingsTestUtil.Target("rt", 1, 1);
        if (kept == null)
        {
            kept = new Bindings();
            kept.Set("cell", new Vec4(0, 0, 1, 1));
            kept.Set("a", new Vec4(1, 0, 0, 1));
            kept.Set("b", new Vec4(0, 1, 0, 1));
            kept.Set("tex", blue);
        }
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.Draw(6, kept, BindingsTestUtil.Opts(sh));
        Gfx.EndPass();
        BindingsTestUtil.EmptyMainPass();

        int? req = requested < version ? version : (int?)null;
        requested = version;
        Gfx.ReadTexture(rb!, rt, req, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready || id != version)
            return;
        var got = $"{bytes!.Get(0)} {bytes.Get(1)} {bytes.Get(2)}";
        if (version <= 2)
            Check.That(BindingsTestRead.Is(bytes, 0, 255, 0, 0), $"shader version {version} should read a (red): {got}");
        else if (version == 3)
            Check.That(BindingsTestRead.Is(bytes, 0, 0, 255, 0), $"shader version 3 should read b (green): {got}");
        else
            Check.That(BindingsTestRead.Is(bytes, 0, 0, 0, 255), $"shader version 4 should read tex (blue): {got}");
        if (version == 4)
        {
            Check.Pass();
            return;
        }
        version++;
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
