// SpriteBatch in the implementation library lubx.
// Buckets are the class SpriteBucket, and fields are typed as ShaderRef / BufferRef.
// Vertices are appended one by one with List<float>.Add.

using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>Vertex bucket per atlas.
/// </summary>
public class SpriteBucket
{
    public Atlas Atlas;
    public List<float> Verts = new List<float>();
    public bool Ready = false;

    public SpriteBucket(Atlas atlas)
    {
        this.Atlas = atlas;
    }
}

/// <summary>
/// Batched drawing of 2D sprites.
/// Use it every frame in this order: begin() → queue sprite()/quad() → flush() inside a pass.
/// Coordinates are pixels in the logical resolution (logicalW x logicalH), with the origin at the top left.
/// Sprites are combined into 1 draw per atlas.
/// Overlap follows not the call order but the order in which each atlas first appeared after Begin (call order within the same atlas).
/// To layer different atlases in call order, use a separate Begin to Flush for each layer.
/// </summary>
public class SpriteBatch
{
    // Number of floats per element.
    // Same layout as the struct of the StructuredBuffer on the shader side (pad is inserted because float2 is placed on 8 and float4 on 16 byte boundaries).
    public const int LegacyStride = 8; // float2 pos, uv; float4 color
    public const int VertexStride = 4; // float2 corner, uv01
    public const int InstanceStride = 16; // float2 pos, size, rot_cs, pad; float4 uv_rect, color

    private static string legacyVs = "struct Uniforms { float4 params; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSIn  { float2 pos; float2 uv; float4 color; };\n"
        + "StructuredBuffer<VSIn> verts;\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float4 color : COLOR; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")]\n"
        + "VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "    VSIn i = verts[vid];\n"
        + "    VSOut o;\n"
        + "    float2 p = float2(i.pos.x / u.params.x * 2.0 - 1.0, 1.0 - i.pos.y / u.params.y * 2.0);\n"
        + "    o.pos = float4(p, 0.0, 1.0);\n"
        + "    o.uv = i.uv;\n"
        + "    o.color = i.color;\n"
        + "    return o;\n"
        + "}\n";

    private static string instancedVs = "struct Uniforms { float4 params; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSVertex { float2 corner; float2 uv01; };\n"
        + "struct VSInstance { float2 pos; float2 size; float2 rot_cs; float2 pad0; float4 uv_rect; float4 color; };\n"
        + "StructuredBuffer<VSVertex> verts;\n"
        + "StructuredBuffer<VSInstance> insts;\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float4 color : COLOR; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")]\n"
        + "VSOut vs_main(uint vid : LUB_VERTEX_ID, uint iid : LUB_INSTANCE_ID) {\n"
        + "    VSVertex v = verts[vid];\n"
        + "    VSInstance i = insts[iid];\n"
        + "    VSOut o;\n"
        + "    float2 local = v.corner * i.size;\n"
        + "    float2 p2 = i.pos + float2(local.x * i.rot_cs.x - local.y * i.rot_cs.y, local.x * i.rot_cs.y + local.y * i.rot_cs.x);\n"
        + "    float2 p = float2(p2.x / u.params.x * 2.0 - 1.0, 1.0 - p2.y / u.params.y * 2.0);\n"
        + "    o.pos = float4(p, 0.0, 1.0);\n"
        + "    o.uv = lerp(i.uv_rect.xy, i.uv_rect.zw, v.uv01);\n"
        + "    o.color = i.color;\n"
        + "    return o;\n"
        + "}\n";

    private static string fs = "LUB_TEXTURE2D(atlas);\n"
        + "struct FSIn { float2 uv : TEXCOORD0; float4 color : COLOR; };\n"
        + "[shader(\"fragment\")]\n"
        + "float4 fs_main(FSIn i) : SV_Target {\n"
        + "    float4 c = LUB_SAMPLE(atlas, i.uv) * i.color;\n"
        + "    if (c.a < 0.004) discard;\n"
        + "    return c;\n"
        + "}\n";

    private static Atlas? whiteAtlas = null;
    private static Atlas? discAtlas = null;

    public int LogicalW;
    public int LogicalH;

    private Dictionary<string, SpriteBucket> buckets =
        new Dictionary<string, SpriteBucket>();
    // Keys of the atlases in the order they first appeared after Begin (the order Flush draws in)
    private List<string> order = new List<string>();
    private string key;
    private bool instanced;
    private ShaderRef? shader = null;
    private BufferRef? quadBuf = null;
    private List<float>? quadData = null;

    /// <summary>Declares the buffer under key.
    /// The contents are constant, so SpriteBatches with the same key share the resource.
    /// All SpriteBatches share one shader regardless of key.
    /// instanced defaults to true when omitted.
    /// </summary>
    public SpriteBatch(string key, int logicalW, int logicalH,
        bool? instanced = null)
    {
        this.key = key;
        this.LogicalW = logicalW;
        this.LogicalH = logicalH;
        this.instanced = instanced ?? true;
    }

    public bool Ensure()
    {
        // The shader source is constant, so it is not created per batch key (each creation stalls for the duration of the compile)
        shader = Gfx.UseShader(instanced ? "lubx/sprite/instanced" : "lubx/sprite/legacy",
            instanced ? instancedVs : legacyVs, fs, 1);
        return shader != null;
    }

    public void Begin()
    {
        foreach (var k in order)
        {
            var b = buckets[k];
            b.Verts.Clear();
            b.Ready = false;
        }
        order.Clear();
        lastAtlas = null;
        lastVerts = null;
    }

    // Atlas of the previous Quad and the bucket of its vertices (no dictionary lookup while the same atlas continues)
    private Atlas? lastAtlas;
    private List<float>? lastVerts;
    private static Color white = Color.Rgb(1.0f, 1.0f, 1.0f, 1.0f);
    // Ranges in the atlas for solid rectangles and circles (not created on every call)
    private static readonly Rect whiteSrc = new Rect(0, 0, 4, 4);
    private static readonly Rect discSrc = new Rect(0, 0, 64, 64);

    private List<float>? BucketFor(Atlas a)
    {
        if (!buckets.TryGetValue(a.Key, out var b))
        {
            b = new SpriteBucket(a);
            buckets[a.Key] = b;
        }
        if (!b.Ready)
        {
            if (!a.Ensure())
                return null;
            b.Ready = true;
            order.Add(a.Key);
        }
        return b.Verts;
    }

    private Color ColorOrWhite(Color? c)
    {
        if (c != null)
            return c.Value;
        return Color.Rgb(1.0f, 1.0f, 1.0f, 1.0f);
    }

    private void PushInstance(List<float> verts, float cx, float cy,
        float w, float h, float cr, float sr, float u0, float v0,
        float u1, float v1, Color c)
    {
        PushInstanceColor(verts, cx, cy, w, h, cr, sr, u0, v0, u1, v1,
            c.R, c.G, c.B, c.A);
    }

    private void PushInstanceColor(List<float> verts, float cx, float cy,
        float w, float h, float cr, float sr, float u0, float v0,
        float u1, float v1, float r, float g, float b, float alpha)
    {
        verts.Add(cx);
        verts.Add(cy);
        verts.Add(w);
        verts.Add(h);
        verts.Add(cr);
        verts.Add(sr);
        verts.Add(0.0f); // pad0 (float4 uv_rect is on a 16 byte boundary)
        verts.Add(0.0f);
        verts.Add(u0);
        verts.Add(v0);
        verts.Add(u1);
        verts.Add(v1);
        verts.Add(r);
        verts.Add(g);
        verts.Add(b);
        verts.Add(alpha);
    }

    private void PushVertex(List<float> verts, float x, float y, float u,
        float v, Color c)
    {
        PushVertexColor(verts, x, y, u, v, c.R, c.G, c.B, c.A);
    }

    private void PushVertexColor(List<float> verts, float x, float y,
        float u, float v, float r, float g, float b, float alpha)
    {
        verts.Add(x);
        verts.Add(y);
        verts.Add(u);
        verts.Add(v);
        verts.Add(r);
        verts.Add(g);
        verts.Add(b);
        verts.Add(alpha);
    }

    private void PushRot(List<float> verts, float cx, float cy, float ox,
        float oy, float cr, float sr, float u, float v, Color c)
    {
        PushVertex(verts, cx + ox * cr - oy * sr, cy + ox * sr + oy * cr,
            u, v, c);
    }

    private void PushRotColor(List<float> verts, float cx, float cy,
        float ox, float oy, float cr, float sr, float u, float v,
        float r, float g, float b, float alpha)
    {
        PushVertexColor(verts, cx + ox * cr - oy * sr, cy + ox * sr + oy * cr,
            u, v, r, g, b, alpha);
    }

    /// <summary>Draws the src rectangle of the atlas centered at (cx, cy), rotated by radians.
    /// </summary>
    public void Sprite(Atlas a, Rect src, float cx, float cy, float w,
        float h, float radians, Color? tint = null)
    {
        var c = ColorOrWhite(tint);
        SpriteColor(a, src, cx, cy, w, h, (float)Math.Cos(radians), (float)Math.Sin(radians),
            c.R, c.G, c.B, c.A);
    }

    /// <summary>Version of sprite with cos/sin and color components passed separately (for hot paths that avoid creating a Color).
    /// </summary>
    public void SpriteColor(Atlas a, Rect src, float cx, float cy, float w,
        float h, float cr, float sr, float r, float g, float b,
        float alpha)
    {
        var verts = BucketFor(a);
        if (verts == null)
            return;

        float u0 = src.X / (float)a.W;
        float v0 = src.Y / (float)a.H;
        float u1 = (src.X + src.W) / (float)a.W;
        float v1 = (src.Y + src.H) / (float)a.H;
        if (instanced)
        {
            PushInstanceColor(verts, cx, cy, w, h, cr, sr, u0, v0, u1, v1,
                r, g, b, alpha);
            return;
        }

        float hw = w * 0.5f;
        float hh = h * 0.5f;
        PushRotColor(verts, cx, cy, -hw, -hh, cr, sr, u0, v0, r, g, b, alpha);
        PushRotColor(verts, cx, cy, hw, -hh, cr, sr, u1, v0, r, g, b, alpha);
        PushRotColor(verts, cx, cy, hw, hh, cr, sr, u1, v1, r, g, b, alpha);
        PushRotColor(verts, cx, cy, -hw, -hh, cr, sr, u0, v0, r, g, b, alpha);
        PushRotColor(verts, cx, cy, hw, hh, cr, sr, u1, v1, r, g, b, alpha);
        PushRotColor(verts, cx, cy, -hw, hh, cr, sr, u0, v1, r, g, b, alpha);
    }

    /// <summary>Draws the src rectangle of the atlas with its top left at (x, y), without rotation.
    /// </summary>
    public void Quad(Atlas a, Rect src, float x, float y, float w, float h,
        Color? tint = null)
    {
        var verts = a == lastAtlas ? lastVerts : null;
        if (verts == null)
        {
            verts = BucketFor(a);
            if (verts == null)
                return;
            lastAtlas = a;
            lastVerts = verts;
        }

        var c = tint ?? white;
        float u0 = src.X / (float)a.W;
        float v0 = src.Y / (float)a.H;
        float u1 = (src.X + src.W) / (float)a.W;
        float v1 = (src.Y + src.H) / (float)a.H;
        if (instanced)
        {
            PushInstanceColor(verts, x + w * 0.5f, y + h * 0.5f, w, h, 1.0f, 0.0f,
                u0, v0, u1, v1, c.R, c.G, c.B, c.A);
            return;
        }

        float x1 = x + w;
        float y1 = y + h;
        PushVertex(verts, x, y, u0, v0, c);
        PushVertex(verts, x1, y, u1, v0, c);
        PushVertex(verts, x1, y1, u1, v1, c);
        PushVertex(verts, x, y, u0, v0, c);
        PushVertex(verts, x1, y1, u1, v1, c);
        PushVertex(verts, x, y1, u0, v1, c);
    }

    private static Atlas EnsureWhiteAtlas()
    {
        if (whiteAtlas == null)
        {
            var px = new List<int>();
            for (int i = 0; i < 4 * 4 * 4; i++)
                px.Add(255);
            whiteAtlas = Atlas.FromPixels("lubx_white", 4, 4, px, 1);
        }
        return whiteAtlas;
    }

    private static Atlas EnsureDiscAtlas()
    {
        if (discAtlas == null)
        {
            // 64x64 soft disc.
            // The tint gives it color.
            int n = 64;
            var px = new List<int>();
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2.0f - 1.0f;
                    float dy = (y + 0.5f) / n * 2.0f - 1.0f;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    float a = Math.Max(0.0f,
                        Math.Min(1.0f, (1.0f - d) * n * 0.5f));
                    px.Add(255);
                    px.Add(255);
                    px.Add(255);
                    px.Add((int)Math.Floor(a * 255));
                }
            }
            discAtlas = Atlas.FromPixels("lubx_disc", n, n, px, 1);
        }
        return discAtlas;
    }

    /// <summary>Solid-color rectangle.
    /// (x, y) is the top left; the coordinate system is the same logical px as quad.
    /// </summary>
    public void Rect(float x, float y, float w, float h,
        Color? tint = null)
    {
        Quad(EnsureWhiteAtlas(), whiteSrc, x, y, w, h, tint);
    }

    /// <summary>Solid-color circle (a soft-edged disc).
    /// (cx, cy) is the center, r is the radius in px.
    /// </summary>
    public void Disc(float cx, float cy, float r, Color? tint = null)
    {
        Sprite(EnsureDiscAtlas(), discSrc, cx, cy,
            r * 2.0f, r * 2.0f, 0.0f, tint);
    }

    private BufferRef? EnsureQuad()
    {
        if (quadData == null)
            quadData = new List<float>
            {
                -0.5f, -0.5f, 0.0f, 0.0f,
                0.5f, -0.5f, 1.0f, 0.0f,
                -0.5f, 0.5f, 0.0f, 1.0f,
                0.5f, 0.5f, 1.0f, 1.0f,
            };
        quadBuf = Gfx.UseBuffer(key + "/quad", Gfx.BufferType.Storage, quadData,
            1);
        return quadBuf;
    }

    /// <summary>Draws the queued sprites per atlas.
    /// blend defaults to Blends.Alpha when omitted.
    /// </summary>
    public void Flush(Blend? blend = null)
    {
        if (!Ensure())
            return;
        var sh = shader;
        if (sh == null)
            return;
        var quadVb = instanced ? EnsureQuad() : null;
        if (instanced && quadVb == null)
            return;

        var uniformParams = new List<float> { LogicalW, LogicalH, 0.0f, 0.0f };
        var blendMode = blend ?? Blends.Alpha;
        var bindings = new Bindings();
        foreach (var k in order)
        {
            var b = buckets[k];
            if (b.Verts.Count == 0)
                continue;
            // Atlases whose pixels changed after Begin (glyphs added by Text while drawing) are uploaded again before drawing
            if (b.Atlas.Changed)
                b.Atlas.Ensure();
            var tex = b.Atlas.Texture;
            if (tex == null)
                continue;
            if (!instanced)
            {
                bindings.Clear();
                bindings.Set("verts", Gfx.FrameBuffer(Gfx.BufferType.Storage, b.Verts));
                bindings.Set("atlas", tex);
                bindings.Set("params", uniformParams);
                Gfx.Draw((int)Math.Floor(b.Verts.Count / (float)LegacyStride), bindings,
                    new DrawOpts
                    {
                        Shader = sh,
                        Depth = false,
                        Cull = Gfx.Cull.None,
                        Blend = blendMode,
                    });
                continue;
            }
            if (quadVb == null)
                continue;
            bindings.Clear();
            bindings.Set("verts", quadVb);
            bindings.Set("insts", Gfx.FrameBuffer(Gfx.BufferType.Storage, b.Verts));
            bindings.Set("atlas", tex);
            bindings.Set("params", uniformParams);
            Gfx.Draw(4, bindings,
                new DrawOpts
                {
                    Shader = sh,
                    Depth = false,
                    Cull = Gfx.Cull.None,
                    Blend = blendMode,
                    Primitive = Gfx.Primitive.TriangleStrip,
                },
                (int)Math.Floor(b.Verts.Count / (float)InstanceStride));
        }
    }
}
