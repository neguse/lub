// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/MeshText.hx と対)。
// Haxe 版の @:native("utf8")/("string") extern は stub の utf8 / @string class
// で置き換える (@string は using static で取り込み len(s) と裸で呼ぶ)。
// gm.positions / gm.indices は Haxe 版が Lua の 1-based 添字を直書きするが、
// C# の List<double> は tcs の indexer が +1 するので 0-based で書く
// (Haxe の positions[i*3+1] と C# の positions[i*3] が同じ要素)。
// typedef GlyphEntry は class に、lua.Table.fromArray は List<double> 直に。
// Haxe 版の char() だけは同名にできない: char は C# 予約語で、@char は tcs が
// 宣言をそのまま `function MeshText:@char` と emit して不正 Lua になるため
// Char に改名している。
using System.Collections.Generic;
using static @string;

/// <summary>MeshText の glyph キャッシュ 1 エントリ (内部用)。空グリフは
/// vb/ib = null, count = 0 で advance だけ持つ。</summary>
public class GlyphEntry
{
    public BufferRef? vb;
    public BufferRef? ib;
    public int count;
    public double advance;
    public double cx;
    public double cy;
}

/// <summary>メッシュグリフ描画 (大サイズレジーム)。TTF 輪郭を三角形化して
/// 描くので拡大しても輪郭が崩れない。小サイズ本文は lubx.Text (bitmap) を
/// 使うこと。座標は論理解像度 px、y は下向き、(x, y) はベースライン。</summary>
public class MeshText
{
    private static string VS = "struct Uniforms {\n"
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

    private static string FS = "struct FSIn {\n"
        + "  float4 color : COLOR;\n"
        + "};\n"
        + "\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target { return i.color; }\n";

    private string key;
    private string ttf;
    private int version;
    private int logicalW;
    private int logicalH;
    private Dictionary<int, GlyphEntry> glyphs = new Dictionary<int, GlyphEntry>();
    private ShaderRef? shader = null;

    public MeshText(string key, string ttf, int version, int logicalW,
        int logicalH)
    {
        this.key = key;
        this.ttf = ttf;
        this.version = version;
        this.logicalW = logicalW;
        this.logicalH = logicalH;
    }

    private ShaderRef? ensure()
    {
        shader = Gfx.use_shader(key + "_shader", VS, FS, 1);
        return shader;
    }

    private GlyphEntry? glyphFor(int cp)
    {
        if (glyphs.TryGetValue(cp, out var cached))
            return cached;
        var gm = Font.font_glyph_mesh(ttf, cp);
        if (gm == null)
            return null;
        if (gm.vert_count == 0)
        {
            // 空グリフ (スペース等) も advance を持つのでキャッシュする
            // (vb/ib は field 既定の null のまま)
            var empty = new GlyphEntry
            {
                count = 0,
                advance = gm.advance,
                cx = 0.0,
                cy = 0.0,
            };
            glyphs[cp] = empty;
            return empty;
        }
        var verts = new List<double>();
        double minX = 1e9;
        double minY = 1e9;
        double maxX = -1e9;
        double maxY = -1e9;
        for (int i = 0; i < gm.vert_count; i++)
        {
            // vertex i の x, y (stride 3、z は捨てる)
            double x = gm.positions[i * 3];
            double y = gm.positions[i * 3 + 1];
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
        // use_buffer は List<double> を取るので indices を詰め替える
        var idx = new List<double>();
        for (int i = 0; i < gm.index_count; i++)
            idx.Add(gm.indices[i]);
        var e = new GlyphEntry
        {
            vb = Gfx.use_buffer(key + "_v:" + cp, Gfx.VERTEX, verts, version),
            ib = Gfx.use_buffer(key + "_i:" + cp, Gfx.INDEX, idx, version),
            count = gm.index_count,
            advance = gm.advance,
            cx = (minX + maxX) * 0.5,
            cy = (minY + maxY) * 0.5,
        };
        glyphs[cp] = e;
        return e;
    }

    private static Color colorOrWhite(Color? c)
    {
        return c ?? Color.rgb(1.0, 1.0, 1.0);
    }

    /// <summary>グリフ 1 つ。(x, y) は centered=false ならベースライン原点、
    /// true なら bbox 中心を (x, y) に置く。size は px/em、angle は CCW
    /// ラジアン。</summary>
    public void glyph(int cp, double x, double y, double size,
        double? angle = null, Color? tint = null, bool? centered = null)
    {
        var sh = ensure();
        if (sh == null)
            return;
        var e = glyphFor(cp);
        if (e == null || e.count == 0)
            return;
        var vb = e.vb;
        var ib = e.ib;
        if (vb == null || ib == null)
            return;
        var c = colorOrWhite(tint);
        bool ctr = centered ?? false;
        Gfx.draw(e.count, new Dictionary<string, object>
        {
            ["verts"] = vb,
            ["indices"] = ib,
            ["uniforms"] = new Dictionary<string, object>
            {
                ["psr"] = new List<double> { x, y, size, angle ?? 0.0 },
                ["tint"] = new List<double> { c.r, c.g, c.b, c.a },
                ["screen"] = new List<double>
                    { (double)logicalW, (double)logicalH, 0.0, 0.0 },
                ["center"] = ctr
                    ? new List<double> { e.cx, e.cy, 0.0, 0.0 }
                    : new List<double> { 0.0, 0.0, 0.0, 0.0 },
            },
        }, new DrawOpts
        {
            shader = sh,
            depth = false,
            cull = Gfx.NONE,
            blend = Gfx.ALPHA,
        });
    }

    /// <summary>文字列の先頭グリフ 1 つを描く。glyph() の String 版
    /// (Haxe 版の char。C# では予約語のため Char)。</summary>
    public void Char(string s, double x, double y, double size,
        double? angle = null, Color? tint = null, bool? centered = null)
    {
        glyph(utf8.codepoint(s, 1), x, y, size, angle, tint, centered);
    }

    /// <summary>1 行をベースライン左端から。</summary>
    public void text(string s, double x, double baselineY, double size,
        Color? tint = null)
    {
        var pen = x;
        int n = len(s);
        int? i = 1;
        while (i != null)
        {
            int pos = i ?? 0;
            if (pos > n)
                break;
            int cp = utf8.codepoint(s, pos);
            var e = glyphFor(cp);
            if (e != null)
            {
                glyph(cp, pen, baselineY, size, 0.0, tint, false);
                pen += e.advance * size;
            }
            i = utf8.offset(s, 2, pos);
        }
    }

    /// <summary>1 行を中央揃えで (cx は中心)。</summary>
    public void textCentered(string s, double cx, double baselineY, double size,
        Color? tint = null)
    {
        text(s, cx - width(s, size) * 0.5, baselineY, size, tint);
    }

    /// <summary>1 行の幅 (px)。advance の合計 × size。</summary>
    public double width(string s, double size)
    {
        var sum = 0.0;
        int n = len(s);
        int? i = 1;
        while (i != null)
        {
            int pos = i ?? 0;
            if (pos > n)
                break;
            var e = glyphFor(utf8.codepoint(s, pos));
            if (e != null)
                sum += e.advance;
            i = utf8.offset(s, 2, pos);
        }
        return sum * size;
    }
}
