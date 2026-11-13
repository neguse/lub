// スクリーンを画面に描く。
// 画面を黒で塗り、奥のスクリーンから順に、ウィンドウの中身の絵をウィンドウの画面の上の矩形に写す。
// 絵の色は透けないものとして写し、スクリーンの透過度で重ねる。
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class ScreenView
{
    // rect は写す先の矩形 (clip 空間の左上の x, y と右下の x, y)、tint.a は透過度
    const string Vs = "struct Uniforms { float4 rect; float4 tint; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float alpha : COLOR; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  VSOut o;\n"
        + "  o.pos = float4(lerp(u.rect.x, u.rect.z, c.x), lerp(u.rect.y, u.rect.w, c.y), 0.0, 1.0);\n"
        + "  o.uv = c;\n"
        + "  o.alpha = u.tint.a;\n"
        + "  return o;\n"
        + "}\n";
    const string Fs = "LUB_TEXTURE2D(tex);\n"
        + "struct FSIn { float2 uv : TEXCOORD0; float alpha : COLOR; };\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
        + "  return float4(LUB_SAMPLE(tex, i.uv).rgb, i.alpha);\n"
        + "}\n";

    // viewportW と viewportH は画面の大きさ (画面の座標)
    public static void Draw(List<Screen> screens, float viewportW, float viewportH)
    {
        var shader = Gfx.UseShader("lider_screen", Vs, Fs, 1);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        if (shader != null)
            foreach (var screen in FarFirst(screens))
                foreach (var window in screen.Windows)
                {
                    var at = window.Viewport;
                    var tex = window.Content.Texture();
                    if (at == null || tex == null)
                        continue;
                    var bindings = new Bindings();
                    bindings.Set("tex", tex);
                    bindings.Set("rect", new List<float>
                    {
                        at.X / viewportW * 2 - 1,
                        1 - at.Y / viewportH * 2,
                        (at.X + at.W) / viewportW * 2 - 1,
                        1 - (at.Y + at.H) / viewportH * 2,
                    });
                    bindings.Set("tint", new List<float> { 1, 1, 1, screen.Opacity });
                    Gfx.Draw(4, bindings, new DrawOpts
                    {
                        Shader = shader,
                        Blend = Blends.Alpha,
                        Cull = Gfx.Cull.None,
                        Primitive = Gfx.Primitive.TriangleStrip,
                        Depth = false,
                    });
                }
        Gfx.EndPass();
    }

    // 視点から遠い順
    static List<Screen> FarFirst(List<Screen> screens)
    {
        var sorted = new List<Screen>(screens);
        for (var i = 1; i < sorted.Count; i++)
        {
            var s = sorted[i];
            var j = i - 1;
            while (j >= 0 && sorted[j].Distance < s.Distance)
            {
                sorted[j + 1] = sorted[j];
                j--;
            }
            sorted[j + 1] = s;
        }
        return sorted;
    }
}
