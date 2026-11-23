// Even when the shader is replaced, Bindings does not use stale resolution results.
// Sets a = red, b = green only once, then raises the version of the shader with the same key, changing the uniform layout and the values it uses.
// Version 1 reads a, version 2 reorders the layout and reads a (the offset changes), version 3 reads b.
// Version 4 reads a texture name that was not used up to version 3.
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
struct FsUniforms { float4 a; float4 b; };
ConstantBuffer<FsUniforms> fu;
LUB_TEXTURE2D(tex);
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target { return LUB_SAMPLE(tex, float2(0.5, 0.5)) + float4((fu.a.rgb + fu.b.rgb) * 0.0, 0.0); }";

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
