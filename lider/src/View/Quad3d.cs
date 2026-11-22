// Draws a textured board in 3D.
// The board is 1 x 1 centered on the origin (x right, y up, facing +z), placed by the model matrix.
// lider's boards are overlaid on the game image, so depth is not used and they overlap in drawing order (drawn from back to front).
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public static class Quad3d
{
    const string Vs = "struct Uniforms { float4x4 mvp; float4 tint; float4 uv; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float4 tint : COLOR; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  VSOut o;\n"
        + "  o.pos = mul(u.mvp, float4(c.x - 0.5, 0.5 - c.y, 0.0, 1.0));\n"
        + "  o.uv = lerp(u.uv.xy, u.uv.zw, c);\n"
        + "  o.tint = u.tint;\n"
        + "  return o;\n"
        + "}\n";

    const string Fs = "LUB_TEXTURE2D(tex);\n"
        + "struct FSIn { float2 uv : TEXCOORD0; float4 tint : COLOR; };\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
        // Render target colors already have opacity multiplied in, so undo it before blending with Alpha (applying it twice darkens thin lines and glow). White in atlases without it stays 1
        + "  float4 t = LUB_SAMPLE(tex, i.uv);\n"
        + "  float4 c = float4(saturate(t.rgb / max(t.a, 0.004)), t.a) * i.tint;\n"
        + "  if (c.a < 0.004) discard;\n"
        + "  return c;\n"
        + "}\n";

    // Board curved along the cylinder surface.
    // Vertices are built from their index (a triangle strip of Segments vertical bands).
    // shape is (center angle, angle width, center height, height), radius is (radius, v at the texture top edge, v at the bottom edge, 1 to unroll flat)
    const int Segments = 32;
    const string CurvedVs = "struct Uniforms { float4x4 vp; float4 tint; float4 shape; float4 radius; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float4 tint : COLOR; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float s = float(vid >> 1) / 32.0;\n"
        + "  float top = float(vid & 1);\n"
        + "  float a = u.shape.x + u.shape.y * (s - 0.5);\n"
        + "  float2 xz = u.radius.w > 0.5 ? float2(a, 1.0) : float2(sin(a), cos(a));\n"
        + "  float3 p = float3(u.radius.x * xz.x, u.shape.z + u.shape.w * (top - 0.5), u.radius.x * xz.y);\n"
        + "  VSOut o;\n"
        + "  o.pos = mul(u.vp, float4(p, 1.0));\n"
        + "  o.uv = float2(s, lerp(u.radius.y, u.radius.z, 1.0 - top));\n"
        + "  o.tint = u.tint;\n"
        + "  return o;\n"
        + "}\n";

    // Rounded-corner surface. round is (width, height, corner radius, edge blur width)
    const string RoundVs = "struct Uniforms { float4x4 vp; float4 tint; float4 shape; float4 radius; float4 round; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float4 tint : COLOR; float4 round : TEXCOORD1; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float s = float(vid >> 1) / 32.0;\n"
        + "  float top = float(vid & 1);\n"
        + "  float a = u.shape.x + u.shape.y * (s - 0.5);\n"
        + "  float2 xz = u.radius.w > 0.5 ? float2(a, 1.0) : float2(sin(a), cos(a));\n"
        + "  float3 p = float3(u.radius.x * xz.x, u.shape.z + u.shape.w * (top - 0.5), u.radius.x * xz.y);\n"
        + "  VSOut o;\n"
        + "  o.pos = mul(u.vp, float4(p, 1.0));\n"
        + "  o.uv = float2(s, 1.0 - top);\n"
        + "  o.tint = u.tint;\n"
        + "  o.round = u.round;\n"
        + "  return o;\n"
        + "}\n";
    const string RoundFs = "struct FSIn { float2 uv : TEXCOORD0; float4 tint : COLOR; float4 round : TEXCOORD1; };\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
        + "  float2 q = abs((i.uv - 0.5) * i.round.xy) - i.round.xy * 0.5 + i.round.z;\n"
        + "  float d = length(max(q, float2(0, 0))) + min(max(q.x, q.y), 0.0) - i.round.z;\n"
        + "  float edge = max(fwidth(d), i.round.w);\n"
        + "  float4 c = i.tint;\n"
        + "  c.a *= 1.0 - smoothstep(-edge, edge * 0.5, d);\n"
        + "  if (c.a < 0.004) discard;\n"
        + "  return c;\n"
        + "}\n";

    // Multiplied into the opacity of everything drawn
    public static float Opacity = 1;

    static ShaderRef? shader;
    static ShaderRef? curved;
    static ShaderRef? round;
    static Atlas? white;
    // Circle that is dense in the middle and fades to 0 toward the edge
    static Atlas? soft;
    static readonly List<float> fullUv = new List<float> { 0, 0, 1, 1 };

    public static bool Ensure()
    {
        shader = Gfx.UseShader("lider_quad", Vs, Fs, 1);
        curved = Gfx.UseShader("lider_curved", CurvedVs, Fs, 1);
        round = Gfx.UseShader("lider_round", RoundVs, RoundFs, 1);
        white ??= Atlas.FromPixels("lider_white", 4, 4, WhitePixels());
        white.Ensure();
        soft ??= Atlas.FromPixels("lider_soft", 32, 32, SoftPixels());
        soft.Ensure();
        return shader != null && curved != null && white.Texture != null;
    }

    static List<int> SoftPixels()
    {
        var px = new List<int>();
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
            {
                var dx = (x + 0.5f) / 16 - 1;
                var dy = (y + 0.5f) / 16 - 1;
                var d = Math.Min(1, (float)Math.Sqrt(dx * dx + dy * dy));
                px.Add(255);
                px.Add(255);
                px.Add(255);
                px.Add((int)(255 * (1 - d) * (1 - d)));
            }
        return px;
    }

    static List<int> WhitePixels()
    {
        var px = new List<int>();
        for (var i = 0; i < 4 * 4 * 4; i++)
            px.Add(255);
        return px;
    }

    public static void Textured(Mat4 mvp, TextureRef tex, Color tint)
    {
        Part(mvp, tex, tint, fullUv);
    }

    // Pastes only the part of the texture at uv (top left u0, v0, bottom right u1, v1)
    public static void Part(Mat4 mvp, TextureRef tex, Color tint, List<float> uv)
    {
        if (shader == null)
            return;
        var bindings = new Bindings();
        bindings.Set("tex", tex);
        bindings.Set("mvp", mvp);
        bindings.Set("tint", new List<float> { tint.R, tint.G, tint.B, tint.A * Opacity });
        bindings.Set("uv", uv);
        Gfx.Draw(4, bindings, new DrawOpts
        {
            Shader = shader,
            Blend = Blends.Alpha,
            Cull = Gfx.Cull.None,
            Primitive = Gfx.Primitive.TriangleStrip,
            Depth = false,
        });
    }

    // Curved board centered at angle angle and height y on the cylinder (radius r), with arc length w and height h
    // v0 to v1 is the vertical part of the texture to paste (top edge 0, bottom edge 1)
    public static void Curved(Mat4 vp, TextureRef tex, Color tint, float angle, float y, float r, float w, float h, float v0, float v1)
    {
        if (curved == null)
            return;
        var bindings = new Bindings();
        bindings.Set("tex", tex);
        bindings.Set("vp", vp);
        bindings.Set("tint", new List<float> { tint.R, tint.G, tint.B, tint.A * Opacity });
        bindings.Set("shape", new List<float> { angle, w / r, y, h });
        bindings.Set("radius", new List<float> { r, v0, v1, Space.Flat ? 1 : 0 });
        Gfx.Draw(2 * (Segments + 1), bindings, new DrawOpts
        {
            Shader = curved,
            Blend = Blends.Alpha,
            Cull = Gfx.Cull.None,
            Primitive = Gfx.Primitive.TriangleStrip,
            Depth = false,
        });
    }

    // Rounded-corner surface along the cylinder. soft is the edge blur width (used for shadows)
    public static void Round(Mat4 vp, Color color, float angle, float y, float r, float w, float h, float corner, float soft)
    {
        if (round == null)
            return;
        var bindings = new Bindings();
        bindings.Set("vp", vp);
        bindings.Set("tint", new List<float> { color.R, color.G, color.B, color.A * Opacity });
        bindings.Set("shape", new List<float> { angle, w / r, y, h });
        bindings.Set("radius", new List<float> { r, 0, 1, Space.Flat ? 1 : 0 });
        bindings.Set("round", new List<float> { w, h, corner, soft });
        Gfx.Draw(2 * (Segments + 1), bindings, new DrawOpts
        {
            Shader = round,
            Blend = Blends.Alpha,
            Cull = Gfx.Cull.None,
            Primitive = Gfx.Primitive.TriangleStrip,
            Depth = false,
        });
    }

    public static void CurvedSolid(Mat4 vp, Color color, float angle, float y, float r, float w, float h)
    {
        if (white?.Texture != null)
            Curved(vp, white.Texture, color, angle, y, r, w, h, 0, 1);
    }

    // Round glow fading toward the edge
    public static void Soft(Mat4 mvp, Color color)
    {
        if (soft?.Texture != null)
            Textured(mvp, soft.Texture, color);
    }

    public static void Solid(Mat4 mvp, Color color)
    {
        if (white?.Texture != null)
            Textured(mvp, white.Texture, color);
    }
}
