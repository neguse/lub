// 値は Draw の時点で確定する: Draw の後の Set は前の draw の結果を変えない。
// 画素 0 は red の uniform と red の texture、画素 1 は Set し直した green で描き、最後に blue を Set するが draw しない。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBindingsImmediate
{
    const string Fs = @"
struct FsUniforms { float4 tint; };
ConstantBuffer<FsUniforms> fu;
LUB_TEXTURE2D(tex);
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target {
  return float4(fu.tint.rgb, 1.0) * LUB_SAMPLE(tex, float2(0.5, 0.5));
}";

    static Readback? rb;
    static int frames;
    static int phase;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("bindings_immediate");
    }

    static TextureRef Solid(string key, int r, int g, int b)
    {
        return Gfx.UseTexture(key, 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { r, g, b, 255 }, 1, new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(frames < 300, "test_bindings_immediate: readback never became ready");
        var sh = Gfx.UseShader("sh", BindingsTestUtil.Vs, Fs, 1);
        if (sh == null)
            return;
        var white = Solid("white", 255, 255, 255);
        var cyan = Solid("cyan", 0, 255, 255);
        var rt = BindingsTestUtil.Target("rt", 4, 1);
        var opts = BindingsTestUtil.Opts(sh);

        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        var b = new Bindings();
        b.Set("tex", white);
        b.Set("cell", new Vec4(0, 0, 4, 1));
        b.Set("tint", new Vec4(1, 0, 0, 1));
        Gfx.Draw(6, b, opts);
        // 前の draw の uniform を書き換える
        b.Set("cell", new Vec4(1, 0, 4, 1));
        b.Set("tint", new Vec4(0, 1, 0, 1));
        Gfx.Draw(6, b, opts);
        // 前の draw の texture を差し替える
        b.Set("cell", new Vec4(2, 0, 4, 1));
        b.Set("tint", new Vec4(1, 1, 1, 1));
        b.Set("tex", cyan);
        Gfx.Draw(6, b, opts);
        // draw しない Set は何も描かない
        b.Set("cell", new Vec4(3, 0, 4, 1));
        b.Set("tint", new Vec4(0, 0, 1, 1));
        b.Set("tex", white);
        Gfx.EndPass();
        BindingsTestUtil.EmptyMainPass();

        var req = phase == 0 ? 1 : (int?)null;
        phase = 1;
        var bytes = BindingsTestRead.Read(rb!, rt, req, out var stride);
        if (bytes == null)
            return;
        Check.That(BindingsTestRead.Is(bytes, 0, 255, 0, 0), $"pixel 0 changed by a later Set: {bytes.Get(0)} {bytes.Get(1)} {bytes.Get(2)}");
        Check.That(BindingsTestRead.Is(bytes, 4, 0, 255, 0), $"pixel 1: {bytes.Get(4)} {bytes.Get(5)} {bytes.Get(6)}");
        Check.That(BindingsTestRead.Is(bytes, 8, 0, 255, 255), $"pixel 2: {bytes.Get(8)} {bytes.Get(9)} {bytes.Get(10)}");
        Check.That(BindingsTestRead.Is(bytes, 12, 0, 0, 0), $"pixel 3 drawn without a draw: {bytes.Get(12)} {bytes.Get(13)} {bytes.Get(14)}");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
