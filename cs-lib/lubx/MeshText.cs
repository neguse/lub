// MeshText of the implementation library lubx.
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>One glyph cache entry of MeshText (internal).
/// Empty glyphs have vb/ib = null, count = 0 and only hold advance.
/// verts / idx are kept to re-assert the buffers every frame (to prevent sweep).
/// </summary>
public class GlyphEntry
{
    public BufferRef? Vb;
    public BufferRef? Ib;
    public List<float>? Verts;
    public List<float>? Idx;
    public int Count;
    public float Advance;
    public float Cx;
    public float Cy;
}

/// <summary>Mesh glyph rendering (large-size regime).
/// Triangulates the TTF outlines and draws them, so the outlines do not break down when scaled up.
/// Use Lubx.Text (bitmap) for small body text.
/// Coordinates are logical resolution px, y points down, and (x, y) is the baseline.
/// </summary>
public class MeshText
{
    private static string vs = "struct Uniforms {\n"
        + "  float4\n"
        + "      psr; // x, y (viewport px), scale (px per em), rotation (rad, CCW in y-up)\n"
        + "  float4 tint;\n"
        + "  float4 viewport; // logical w, h\n"
        + "  float4 center; // rotation/placement center in em (glyph bbox center)\n"
        + "};\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "\n"
        + "struct VSIn {\n"
        + "  float2 pos; // em units, y-up, baseline origin\n"
        + "};\n"
        + "StructuredBuffer<VSIn> verts;\n"
        + "\n"
        + "struct VSOut {\n"
        + "  float4 color : COLOR;\n"
        + "  float4 pos : SV_Position;\n"
        + "};\n"
        + "\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  VSIn i = verts[vid];\n"
        + "  VSOut o;\n"
        + "  float c = cos(u.psr.w);\n"
        + "  float s = sin(u.psr.w);\n"
        + "  float2 l = (i.pos - u.center.xy) * u.psr.z;\n"
        + "  float2 r = float2(l.x * c - l.y * s, l.x * s + l.y * c);\n"
        + "  float2 p = float2(u.psr.x + r.x, u.psr.y - r.y); // y-up -> viewport y-down\n"
        + "  o.pos = float4(p.x / u.viewport.x * 2.0 - 1.0, 1.0 - p.y / u.viewport.y * 2.0,\n"
        + "                 0.0, 1.0);\n"
        + "  o.color = u.tint;\n"
        + "  return o;\n"
        + "}\n";

    private static string fs = "struct FSIn {\n"
        + "  float4 color : COLOR;\n"
        + "};\n"
        + "\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target { return i.color; }\n";

    private string key;
    private string ttfPath;
    private int version;
    private int logicalW;
    private int logicalH;
    private Dictionary<int, GlyphEntry> glyphs = new Dictionary<int, GlyphEntry>();
    private ShaderRef? shader = null;

    /// <summary>ttfPath is the path of a font file readable with Io.LoadBytes.
    /// </summary>
    public MeshText(string key, string ttfPath, int version, int logicalW,
        int logicalH)
    {
        this.key = key;
        this.ttfPath = ttfPath;
        this.version = version;
        this.logicalW = logicalW;
        this.logicalH = logicalH;
    }

    private ShaderRef? Ensure()
    {
        shader = Gfx.UseShader(key + "/shader", vs, fs, 1);
        return shader;
    }

    private GlyphEntry? GlyphFor(int cp)
    {
        if (glyphs.TryGetValue(cp, out var cached))
        {
            // version is the same, so data is not read.
            // Recreate it if it has been swept.
            if (cached.Vb != null && cached.Ib != null && cached.Verts != null
                && cached.Idx != null)
            {
                cached.Vb = Gfx.UseBuffer(key + "/v:" + cp, Gfx.BufferType.Storage,
                    cached.Verts, cached.Vb.Version);
                cached.Ib = Gfx.UseBuffer(key + "/i:" + cp, Gfx.BufferType.Index,
                    cached.Idx, cached.Ib.Version);
            }
            return cached;
        }
        Io.LoadBytes(ttfPath, out var ttf, out _, out _, out _);
        if (ttf == null)
            return null;
        var gm = Font.GlyphMesh(ttf, cp);
        if (gm == null)
            return null;
        if (gm.Mesh.VertCount == 0)
        {
            // Empty glyphs (spaces etc.) also have an advance, so cache them (vb/ib stay at the field default null)
            var empty = new GlyphEntry
            {
                Count = 0,
                Advance = gm.Advance,
                Cx = 0.0f,
                Cy = 0.0f,
            };
            glyphs[cp] = empty;
            return empty;
        }
        var verts = new List<float>();
        float minX = 1e9f;
        float minY = 1e9f;
        float maxX = -1e9f;
        float maxY = -1e9f;
        for (int i = 0; i < gm.Mesh.VertCount; i++)
        {
            // x, y of vertex i (stride 3, z is dropped)
            float x = gm.Mesh.Positions[i * 3];
            float y = gm.Mesh.Positions[i * 3 + 1];
            verts.Add(x);
            verts.Add(y);
            if (x < minX)
                minX = x;
            if (x > maxX)
                maxX = x;
            if (y < minY)
                minY = y;
            if (y > maxY)
                maxY = y;
        }
        // use_buffer takes a List<float>, so indices are repacked
        var idx = new List<float>();
        for (int i = 0; i < gm.Mesh.IndexCount; i++)
            idx.Add(gm.Mesh.Indices[i]);
        var e = new GlyphEntry
        {
            Vb = Gfx.UseBuffer(key + "/v:" + cp, Gfx.BufferType.Storage, verts, version),
            Ib = Gfx.UseBuffer(key + "/i:" + cp, Gfx.BufferType.Index, idx, version),
            Verts = verts,
            Idx = idx,
            Count = gm.Mesh.IndexCount,
            Advance = gm.Advance,
            Cx = (minX + maxX) * 0.5f,
            Cy = (minY + maxY) * 0.5f,
        };
        glyphs[cp] = e;
        return e;
    }

    private static Color ColorOrWhite(Color? c)
    {
        return c ?? Color.Rgb(1.0f, 1.0f, 1.0f);
    }

    /// <summary>One glyph.
    /// (x, y) is the baseline origin if centered=false; if true, the bbox center is placed at (x, y).
    /// size is px/em, angle is CCW radians.
    /// </summary>
    public void Glyph(int cp, float x, float y, float size,
        float? angle = null, Color? tint = null, bool? centered = null)
    {
        var sh = Ensure();
        if (sh == null)
            return;
        var e = GlyphFor(cp);
        if (e == null || e.Count == 0)
            return;
        var vb = e.Vb;
        var ib = e.Ib;
        if (vb == null || ib == null)
            return;
        var c = ColorOrWhite(tint);
        bool ctr = centered ?? false;
        var bindings = new Bindings();
        bindings.Set("verts", vb);
        bindings.Set("indices", ib);
        bindings.Set("psr", new Vec4(x, y, size, angle ?? 0.0f));
        bindings.Set("tint", new Vec4(c.R, c.G, c.B, c.A));
        bindings.Set("viewport", new Vec4((float)logicalW, (float)logicalH, 0.0f, 0.0f));
        bindings.Set("center", ctr
                    ? new List<float> { e.Cx, e.Cy, 0.0f, 0.0f }
                    : new List<float> { 0.0f, 0.0f, 0.0f, 0.0f });
        Gfx.Draw(e.Count, bindings, new DrawOpts
        {
            Shader = sh,
            Depth = false,
            Cull = Gfx.Cull.None,
            Blend = Blends.Alpha,
        });
    }

    /// <summary>Draws the first glyph of a string.
    /// String version of glyph() (Char because char is a C# reserved word).
    /// </summary>
    public void Char(string s, float x, float y, float size,
        float? angle = null, Color? tint = null, bool? centered = null)
    {
        foreach (var r in s.EnumerateRunes())
        {
            Glyph(r.Value, x, y, size, angle, tint, centered);
            return;
        }
    }

    /// <summary>One line from the left end of the baseline.
    /// </summary>
    public void Text(string s, float x, float baselineY, float size,
        Color? tint = null)
    {
        var pen = x;
        foreach (var r in s.EnumerateRunes())
        {
            int cp = r.Value;
            var e = GlyphFor(cp);
            if (e != null)
            {
                Glyph(cp, pen, baselineY, size, 0.0f, tint, false);
                pen += e.Advance * size;
            }
        }
    }

    /// <summary>One line, centered (cx is the center).
    /// </summary>
    public void TextCentered(string s, float cx, float baselineY, float size,
        Color? tint = null)
    {
        Text(s, cx - Width(s, size) * 0.5f, baselineY, size, tint);
    }

    /// <summary>Width of one line (px).
    /// Sum of advances × size.
    /// </summary>
    public float Width(string s, float size)
    {
        var sum = 0.0f;
        foreach (var r in s.EnumerateRunes())
        {
            var e = GlyphFor(r.Value);
            if (e != null)
                sum += e.Advance;
        }
        return sum * size;
    }
}
