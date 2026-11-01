// Set に Gfx.MainTex を渡しても Set の時点では何も確定せず、draw の時点で今までと同じように解決する。
// MainTex は resource table に無い特別な handle なので、draw の binding としては error になる。
// 同じ error を frame をまたいで毎回返し、Set し直せば同じ Bindings で描けることを見る。
// XR の片眼の target も同じ特別な handle だが、XR の session が無いとここでは試せない。
using System;
using System.Collections.Generic;
using Lub;

public static class TestBindingsMainTex
{
    const string Fs = @"
LUB_TEXTURE2D(tex);
struct FSIn { float4 pos : SV_Position; };
[shader(""fragment"")] float4 fs_main(FSIn i) : SV_Target { return LUB_SAMPLE(tex, float2(0.5, 0.5)); }";
    const string Want = "binding 'tex' handle -1 was destroyed or swept";

    static Readback? rb;
    static Bindings? kept;
    static int frame;
    static int phase;
    static bool requested;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        rb = Gfx.Readback("bindings_main_tex");
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        Check.That(frame < 300, "test_bindings_main_tex: never finished");
        var sh = Gfx.UseShader("sh", BindingsTestUtil.Vs, Fs, 1);
        if (sh == null)
            return;
        var green = Gfx.UseTexture("green", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 0, 255, 0, 255 }, 1, new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp })!;
        var rt = BindingsTestUtil.Target("rt", 1, 1);
        var opts = BindingsTestUtil.Opts(sh);
        if (kept == null)
        {
            kept = new Bindings();
            kept.Set("cell", new Vec4(0, 0, 1, 1));
            kept.Set("tex", Gfx.MainTex!);
        }
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        if (phase < 3)
        {
            // Set し直さずに frame をまたいで同じ error
            phase++;
            Check.Error("MainTex in kept Bindings, frame " + phase, Want);
            Gfx.Draw(6, kept, opts);
            return;
        }
        kept.Set("tex", green);
        Gfx.Draw(6, kept, opts);
        Gfx.EndPass();
        BindingsTestUtil.EmptyMainPass();
        int? req = requested ? null : 1;
        requested = true;
        var bytes = BindingsTestRead.Read(rb!, rt, req, out _);
        if (bytes == null)
            return;
        Check.That(BindingsTestRead.Is(bytes, 0, 0, 255, 0), $"want green, got {bytes.Get(0)} {bytes.Get(1)} {bytes.Get(2)}");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
