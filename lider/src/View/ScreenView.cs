// Draws screens on the display.
// Fills the display with black and, starting from the farthest screen, copies each window's content image into the window's rectangle on the display.
// Image colors are copied as opaque and blended with the screen's opacity.
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class ScreenView
{
    // rect is the destination rectangle (top-left x, y and bottom-right x, y in clip space), tint.a is the opacity
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

    // viewportW and viewportH are the display size (display coordinates).
    // behind is the game image placed in Space (null if none). Copied full-screen, behind every screen
    public static void Draw(List<Screen> screens, float viewportW, float viewportH, TextureRef? behind)
    {
        var shader = Gfx.UseShader("lider_screen", Vs, Fs, 1);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        if (shader != null && behind != null)
        {
            var bindings = new Bindings();
            bindings.Set("tex", behind);
            bindings.Set("rect", new List<float> { -1, 1, 1, -1 });
            bindings.Set("tint", new List<float> { 1, 1, 1, 1 });
            Gfx.Draw(4, bindings, new DrawOpts
            {
                Shader = shader,
                Blend = Blends.Alpha,
                Cull = Gfx.Cull.None,
                Primitive = Gfx.Primitive.TriangleStrip,
                Depth = false,
            });
        }
        if (shader != null)
            foreach (var screen in FarFirst(screens))
            {
                // Draw only flat screens (the cylinder of code panels is drawn by DrawScene)
                if (screen.Shape != ScreenShape.Plane)
                    continue;
                foreach (var window in screen.Windows)
                {
                    var at = window.ContentRect;
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
            }
        Gfx.EndPass();
    }

    // Farthest from the view first
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
