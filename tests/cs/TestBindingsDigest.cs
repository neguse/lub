// The same declaration makes the same C API calls whether it is new every time, reused, or Cleared again.
// env LUB_TEST_BINDINGS_MODE = new / reuse / clear.
// reuse and clear use the same Bindings; clear Clears it for each draw and Sets everything again.
// The test runner (tests/lub-test, cs.TestBindingsDigest) compares the digests.
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

    // A shader whose extra is a float (for the draw that changes the value length under the same name)
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

    // The k-th draw.
    // Odd draws pass extra as float2; even draws change the length under the same name and draw with the shader whose extra is a float.
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

    // Draws whose set of names differs per draw (no texture is passed; they draw with the default texture).
    // Reusing the same Bindings requires Clear.
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
