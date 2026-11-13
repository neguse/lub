// lider を重ねて動かすための小さいゲーム。
// 毎 frame 数を数え、四角を左右に動かす。
// lider でこの file を書き換えて保存すると、数は巻き戻らずに動きだけが変わる。
// XR では、同じ絵を texture に描いて、目の前 2 m の板に貼る。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class Counter
{
    public static int Frames;
    static float t;
    static SpriteBatch? batch;
    static readonly XrAnchor anchor = new XrAnchor();
    static readonly float[] eyeVp = new float[16];
    static readonly float[] background = new float[] { 0.08f, 0.1f, 0.16f, 1 };

    const string ScreenVs = "struct Uniforms { float4x4 mvp; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  VSOut o;\n"
        + "  o.pos = mul(u.mvp, float4(c.x - 0.5, 0.5 - c.y, 0.0, 1.0));\n"
        + "  o.uv = c;\n"
        + "  return o;\n"
        + "}\n";
    const string ScreenFs = "LUB_TEXTURE2D(screen);\n"
        + "struct FSIn { float2 uv : TEXCOORD0; };\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target { return LUB_SAMPLE(screen, i.uv); }\n";

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
    }

    public static float Speed()
    {
        return 1.0f;
    }

    public static void OnFrame(float dt)
    {
        Frames++;
        t += dt * Speed();
        // XR の間は main_tex が無いので、画面の代わりの texture に描く
        var xr = Xr.Active();
        var target = xr
            ? Gfx.UseTexture("counter_screen", 1280, 720, Gfx.PixelFormat.Rgba8, null, 1,
                new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp, Target = true })
            : Gfx.MainTex;
        if (target == null)
            return;
        var w = 1280;
        var h = 720;
        if (!xr)
            Gfx.Size(out w, out h);
        batch ??= new SpriteBatch("counter", w, h);
        Gfx.BeginPass(new PassOpts { Target = target, ClearColor = background });
        batch.Begin();
        var x = w * 0.5f + (float)Math.Sin(t) * w * 0.35f;
        batch.Rect(x - 40, h * 0.5f - 40, 80, 80, Color.Rgb(0.95f, 0.75f, 0.2f));
        for (var i = 0; i < Frames % 120; i++)
            batch.Rect(20 + i * 6, h - 30, 4, 12, Color.Rgb(0.4f, 0.8f, 0.5f));
        batch.Flush();
        Gfx.EndPass();
        if (xr)
            DrawInXr(target);
    }

    static void DrawInXr(TextureRef screen)
    {
        var left = Xr.View(0, 0.05f, 100);
        var right = Xr.View(1, 0.05f, 100);
        var sh = Gfx.UseShader("counter_screen_quad", ScreenVs, ScreenFs, 1);
        if (left == null || right == null || sh == null)
            return;
        if (!anchor.IsSet())
            anchor.Recenter(left, right);
        // 基準の空間は OpenXR と同じく -z が前。板は 1.6 m x 0.9 m
        var model = Mat4.Translate(new Vec3(0, 0, -2)) * Mat4.Scale(new Vec3(1.6f, 0.9f, 1));
        foreach (var view in new[] { left, right })
        {
            anchor.ViewProjection(view, eyeVp);
            var values = new List<float>();
            foreach (var v in eyeVp)
                values.Add(v);
            var vp = Mat4.FromList(values, 0);
            Gfx.BeginPass(new PassOpts { Target = view.Target, ClearColor = background });
            var bindings = new Bindings();
            bindings.Set("screen", screen);
            bindings.Set("mvp", vp * model);
            Gfx.Draw(4, bindings, new DrawOpts { Shader = sh, Primitive = Gfx.Primitive.TriangleStrip, Cull = Gfx.Cull.None, Depth = false });
            Gfx.EndPass();
        }
    }
}
