// 実装ライブラリ lubx の MeshText。
// utf8 / string 関数は stub の utf8 / @string class で呼ぶ (@string は
// using static で取り込み len(s) と裸で呼ぶ)。
// gm.positions / gm.indices は List<float> を 0-based で書く (tcs の
// indexer が +1 して Lua の 1-based に写る)。
// char は C# 予約語で、@char は tcs が宣言をそのまま `function MeshText:@char`
// と emit して不正 Lua になるため Char にしている。
using System.Collections.Generic;
using static Lub;

/// <summary>MeshText の glyph キャッシュ 1 エントリ (内部用)。空グリフは
/// vb/ib = null, count = 0 で advance だけ持つ。</summary>
public class GlyphEntry
{
    public BufferRef? Vb;
    public BufferRef? Ib;
    public int Count;
    public float Advance;
    public float Cx;
    public float Cy;
}

/// <summary>メッシュグリフ描画 (大サイズレジーム)。TTF 輪郭を三角形化して
/// 描くので拡大しても輪郭が崩れない。小サイズ本文は lubx.Text (bitmap) を
/// 使うこと。座標は論理解像度 px、y は下向き、(x, y) はベースライン。</summary>
public class MeshText
{
    private static string vs = "struct Uniforms {\n"
        + "  float4\n"
        + "      psr; // x, y (screen px), scale (px per em), rotation (rad, CCW in y-up)\n"
        + "  float4 tint;\n"
        + "  float4 screen; // logical w, h\n"
        + "  float4 center; // rotation/placement center in em (glyph bbox center)\n"
        + "};\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "\n"
        + "struct VSIn {\n"
        + "  float2 pos : POSITION; // em units, y-up, baseline origin\n"
        + "};\n"
        + "\n"
        + "struct VSOut {\n"
        + "  float4 color : COLOR;\n"
        + "  float4 pos : SV_Position;\n"
        + "};\n"
        + "\n"
        + "[shader(\"vertex\")] VSOut vs_main(VSIn i) {\n"
        + "  VSOut o;\n"
        + "  float c = cos(u.psr.w);\n"
        + "  float s = sin(u.psr.w);\n"
        + "  float2 l = (i.pos - u.center.xy) * u.psr.z;\n"
        + "  float2 r = float2(l.x * c - l.y * s, l.x * s + l.y * c);\n"
        + "  float2 p = float2(u.psr.x + r.x, u.psr.y - r.y); // y-up -> screen y-down\n"
        + "  o.pos = float4(p.x / u.screen.x * 2.0 - 1.0, 1.0 - p.y / u.screen.y * 2.0,\n"
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

    /// <summary>ttfPath は Io.LoadBytes で読める font file の path。</summary>
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
        shader = Gfx.UseShader(key + "_shader", vs, fs, 1);
        return shader;
    }

    private GlyphEntry? GlyphFor(int cp)
    {
        if (glyphs.TryGetValue(cp, out var cached))
            return cached;
        Io.LoadBytes(ttfPath, out var ttf, out _, out _, out _);
        if (ttf == null)
            return null;
        var gm = Font.GlyphMesh(ttf, cp);
        if (gm == null)
            return null;
        if (gm.VertCount == 0)
        {
            // 空グリフ (スペース等) も advance を持つのでキャッシュする
            // (vb/ib は field 既定の null のまま)
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
        for (int i = 0; i < gm.VertCount; i++)
        {
            // vertex i の x, y (stride 3、z は捨てる)
            float x = gm.Positions[i * 3];
            float y = gm.Positions[i * 3 + 1];
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
        // use_buffer は List<float> を取るので indices を詰め替える
        var idx = new List<float>();
        for (int i = 0; i < gm.IndexCount; i++)
            idx.Add(gm.Indices[i]);
        var e = new GlyphEntry
        {
            Vb = Gfx.UseBuffer(key + "_v:" + cp, Gfx.BufferType.Vertex, verts, version),
            Ib = Gfx.UseBuffer(key + "_i:" + cp, Gfx.BufferType.Index, idx, version),
            Count = gm.IndexCount,
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

    /// <summary>グリフ 1 つ。(x, y) は centered=false ならベースライン原点、
    /// true なら bbox 中心を (x, y) に置く。size は px/em、angle は CCW
    /// ラジアン。</summary>
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
        Gfx.Draw(e.Count, new Dictionary<string, object>
        {
            ["verts"] = vb,
            ["indices"] = ib,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["psr"] = new List<float> { x, y, size, angle ?? 0.0f },
                ["tint"] = new List<float> { c.R, c.G, c.B, c.A },
                ["screen"] = new List<float>
                    { (float)logicalW, (float)logicalH, 0.0f, 0.0f },
                ["center"] = ctr
                    ? new List<float> { e.Cx, e.Cy, 0.0f, 0.0f }
                    : new List<float> { 0.0f, 0.0f, 0.0f, 0.0f },
            },
        }, new DrawOpts
        {
            Shader = sh,
            Depth = false,
            Cull = Gfx.Cull.None,
            Blend = Gfx.Blend.Alpha,
        });
    }

    /// <summary>文字列の先頭グリフ 1 つを描く。glyph() の String 版
    /// (char は C# の予約語のため Char)。</summary>
    public void Char(string s, float x, float y, float size,
        float? angle = null, Color? tint = null, bool? centered = null)
    {
        foreach (var r in s.EnumerateRunes())
        {
            Glyph(r.Value, x, y, size, angle, tint, centered);
            return;
        }
    }

    /// <summary>1 行をベースライン左端から。</summary>
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

    /// <summary>1 行を中央揃えで (cx は中心)。</summary>
    public void TextCentered(string s, float cx, float baselineY, float size,
        Color? tint = null)
    {
        Text(s, cx - Width(s, size) * 0.5f, baselineY, size, tint);
    }

    /// <summary>1 行の幅 (px)。advance の合計 × size。</summary>
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
