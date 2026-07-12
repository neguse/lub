// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/SpriteBatch.hx と対)。
// Haxe 版の typedef SpriteBucket は class に、Dynamic フィールドは
// ShaderRef / BufferRef の型付きに。untyped の配列直書き (perf イディオム) は
// List<double>.Add の逐次追加に置換 (格納順は Haxe 版の out[i]..out[i+n] と
// 同一)。lua.Table.fromArray は List<double> がそのまま Lua array table
// なので不要。デフォルト引数値は nullable + ?? で受ける (tcs は call site
// 展開しない)。

using System;
using System.Collections.Generic;

/// <summary>アトラスごとの頂点バケット (Haxe 版 typedef SpriteBucket と対)。</summary>
public class SpriteBucket
{
    public Atlas atlas;
    public List<double> verts = new List<double>();
    public bool ready = false;

    public SpriteBucket(Atlas atlas)
    {
        this.atlas = atlas;
    }
}

/// <summary>
/// 2D スプライトのバッチ描画。毎フレーム begin() → sprite()/quad() を積む →
/// pass 内で flush() の順で使う。座標は論理解像度 (logicalW x logicalH) の
/// ピクセル、左上原点。アトラスごとに 1 draw にまとめられる。
/// </summary>
public class SpriteBatch
{
    public const int LEGACY_STRIDE = 8;
    public const int VERTEX_STRIDE = 4;
    public const int INSTANCE_STRIDE = 14;

    private static string LEGACY_VS = "struct Uniforms { float4 params; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float4 color : COLOR; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")]\n"
        + "VSOut vs_main(VSIn i) {\n"
        + "    VSOut o;\n"
        + "    float2 p = float2(i.pos.x / u.params.x * 2.0 - 1.0, 1.0 - i.pos.y / u.params.y * 2.0);\n"
        + "    o.pos = float4(p, 0.0, 1.0);\n"
        + "    o.uv = i.uv;\n"
        + "    o.color = i.color;\n"
        + "    return o;\n"
        + "}\n";

    private static string INSTANCED_VS = "struct Uniforms { float4 params; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSVertex { float2 corner : TEXCOORD0; float2 uv01 : TEXCOORD1; };\n"
        + "struct VSInstance { float2 pos : TEXCOORD2; float2 size : TEXCOORD3; float2 rot_cs : TEXCOORD4; float4 uv_rect : TEXCOORD5; float4 color : TEXCOORD6; };\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float4 color : COLOR; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")]\n"
        + "VSOut vs_main(VSVertex v, VSInstance i) {\n"
        + "    VSOut o;\n"
        + "    float2 local = v.corner * i.size;\n"
        + "    float2 p2 = i.pos + float2(local.x * i.rot_cs.x - local.y * i.rot_cs.y, local.x * i.rot_cs.y + local.y * i.rot_cs.x);\n"
        + "    float2 p = float2(p2.x / u.params.x * 2.0 - 1.0, 1.0 - p2.y / u.params.y * 2.0);\n"
        + "    o.pos = float4(p, 0.0, 1.0);\n"
        + "    o.uv = lerp(i.uv_rect.xy, i.uv_rect.zw, v.uv01);\n"
        + "    o.color = i.color;\n"
        + "    return o;\n"
        + "}\n";

    private static string FS = "LUB_TEXTURE2D(atlas);\n"
        + "struct FSIn { float2 uv : TEXCOORD0; float4 color : COLOR; };\n"
        + "[shader(\"fragment\")]\n"
        + "float4 fs_main(FSIn i) : SV_Target {\n"
        + "    float4 c = LUB_SAMPLE(atlas, i.uv) * i.color;\n"
        + "    if (c.a < 0.004) discard;\n"
        + "    return c;\n"
        + "}\n";

    private static Atlas? whiteAtlas = null;
    private static Atlas? discAtlas = null;

    public int logicalW;
    public int logicalH;

    private Dictionary<string, SpriteBucket> buckets =
        new Dictionary<string, SpriteBucket>();
    private List<string> order = new List<string>();
    private string shaderKey;
    private string bufferPrefix;
    private bool instanced;
    private ShaderRef? shader = null;
    private BufferRef? quadBuf = null;
    private List<double>? quadData = null;
    private int meshVersion = 0;

    /// <summary>shaderKey 省略で "lubx_sprite"、instanced 省略で true。</summary>
    public SpriteBatch(int logicalW, int logicalH, string? shaderKey = null,
        string? bufferPrefix = null, bool? instanced = null)
    {
        this.logicalW = logicalW;
        this.logicalH = logicalH;
        bool inst = instanced ?? true;
        this.shaderKey = (shaderKey ?? "lubx_sprite")
            + (inst ? "_instanced" : "_legacy");
        this.bufferPrefix = bufferPrefix ?? "lubx_sprite";
        this.instanced = inst;
    }

    public bool ensure()
    {
        shader = Gfx.use_shader(shaderKey, instanced ? INSTANCED_VS : LEGACY_VS,
            FS, 1);
        return shader != null;
    }

    public void begin()
    {
        foreach (var k in order)
        {
            var b = buckets[k];
            b.verts.Clear();
            b.ready = false;
        }
    }

    private List<double>? bucketFor(Atlas a)
    {
        if (!buckets.TryGetValue(a.key, out var b))
        {
            b = new SpriteBucket(a);
            buckets[a.key] = b;
            order.Add(a.key);
        }
        if (!b.ready)
        {
            if (!a.ensure())
                return null;
            b.ready = true;
        }
        return b.verts;
    }

    private Color colorOrWhite(Color? c)
    {
        if (c != null)
            return c;
        return Color.rgb(1.0, 1.0, 1.0, 1.0);
    }

    private void pushInstance(List<double> verts, double cx, double cy,
        double w, double h, double cr, double sr, double u0, double v0,
        double u1, double v1, Color c)
    {
        pushInstanceColor(verts, cx, cy, w, h, cr, sr, u0, v0, u1, v1,
            c.r, c.g, c.b, c.a);
    }

    // Haxe 版は untyped out[i]..out[i+13] の直書き。Add の逐次追加でも
    // Lua 上は同じ array table への同順 append になる。
    private void pushInstanceColor(List<double> verts, double cx, double cy,
        double w, double h, double cr, double sr, double u0, double v0,
        double u1, double v1, double r, double g, double b, double alpha)
    {
        verts.Add(cx);
        verts.Add(cy);
        verts.Add(w);
        verts.Add(h);
        verts.Add(cr);
        verts.Add(sr);
        verts.Add(u0);
        verts.Add(v0);
        verts.Add(u1);
        verts.Add(v1);
        verts.Add(r);
        verts.Add(g);
        verts.Add(b);
        verts.Add(alpha);
    }

    private void pushVertex(List<double> verts, double x, double y, double u,
        double v, Color c)
    {
        pushVertexColor(verts, x, y, u, v, c.r, c.g, c.b, c.a);
    }

    private void pushVertexColor(List<double> verts, double x, double y,
        double u, double v, double r, double g, double b, double alpha)
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

    private void pushRot(List<double> verts, double cx, double cy, double ox,
        double oy, double cr, double sr, double u, double v, Color c)
    {
        pushVertex(verts, cx + ox * cr - oy * sr, cy + ox * sr + oy * cr,
            u, v, c);
    }

    private void pushRotColor(List<double> verts, double cx, double cy,
        double ox, double oy, double cr, double sr, double u, double v,
        double r, double g, double b, double alpha)
    {
        pushVertexColor(verts, cx + ox * cr - oy * sr, cy + ox * sr + oy * cr,
            u, v, r, g, b, alpha);
    }

    /// <summary>アトラスの src 矩形を中心 (cx, cy)・radians 回転で描く。</summary>
    public void sprite(Atlas a, Rect src, double cx, double cy, double w,
        double h, double radians, Color? tint = null)
    {
        var c = colorOrWhite(tint);
        spriteColor(a, src, cx, cy, w, h, Math.Cos(radians), Math.Sin(radians),
            c.r, c.g, c.b, c.a);
    }

    /// <summary>sprite の cos/sin・色成分ばらし版 (Color 生成を避ける hot path 用)。</summary>
    public void spriteColor(Atlas a, Rect src, double cx, double cy, double w,
        double h, double cr, double sr, double r, double g, double b,
        double alpha)
    {
        var verts = bucketFor(a);
        if (verts == null)
            return;

        double u0 = src.x / (double)a.w;
        double v0 = src.y / (double)a.h;
        double u1 = (src.x + src.w) / (double)a.w;
        double v1 = (src.y + src.h) / (double)a.h;
        if (instanced)
        {
            pushInstanceColor(verts, cx, cy, w, h, cr, sr, u0, v0, u1, v1,
                r, g, b, alpha);
            return;
        }

        double hw = w * 0.5;
        double hh = h * 0.5;
        pushRotColor(verts, cx, cy, -hw, -hh, cr, sr, u0, v0, r, g, b, alpha);
        pushRotColor(verts, cx, cy, hw, -hh, cr, sr, u1, v0, r, g, b, alpha);
        pushRotColor(verts, cx, cy, hw, hh, cr, sr, u1, v1, r, g, b, alpha);
        pushRotColor(verts, cx, cy, -hw, -hh, cr, sr, u0, v0, r, g, b, alpha);
        pushRotColor(verts, cx, cy, hw, hh, cr, sr, u1, v1, r, g, b, alpha);
        pushRotColor(verts, cx, cy, -hw, hh, cr, sr, u0, v1, r, g, b, alpha);
    }

    /// <summary>アトラスの src 矩形を左上 (x, y) に無回転で描く。</summary>
    public void quad(Atlas a, Rect src, double x, double y, double w, double h,
        Color? tint = null)
    {
        var verts = bucketFor(a);
        if (verts == null)
            return;

        var c = colorOrWhite(tint);
        double u0 = src.x / (double)a.w;
        double v0 = src.y / (double)a.h;
        double u1 = (src.x + src.w) / (double)a.w;
        double v1 = (src.y + src.h) / (double)a.h;
        if (instanced)
        {
            pushInstance(verts, x + w * 0.5, y + h * 0.5, w, h, 1.0, 0.0,
                u0, v0, u1, v1, c);
            return;
        }

        double x1 = x + w;
        double y1 = y + h;
        pushVertex(verts, x, y, u0, v0, c);
        pushVertex(verts, x1, y, u1, v0, c);
        pushVertex(verts, x1, y1, u1, v1, c);
        pushVertex(verts, x, y, u0, v0, c);
        pushVertex(verts, x1, y1, u1, v1, c);
        pushVertex(verts, x, y1, u0, v1, c);
    }

    private static Atlas ensureWhiteAtlas()
    {
        if (whiteAtlas == null)
        {
            var px = new List<int>();
            for (int i = 0; i < 4 * 4 * 4; i++)
                px.Add(255);
            whiteAtlas = Atlas.fromPixels("lubx_white", 4, 4, px, 1);
        }
        return whiteAtlas;
    }

    private static Atlas ensureDiscAtlas()
    {
        if (discAtlas == null)
        {
            // 64x64 の soft disc。tint で色を付ける。
            int n = 64;
            var px = new List<int>();
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    double dx = (x + 0.5) / n * 2.0 - 1.0;
                    double dy = (y + 0.5) / n * 2.0 - 1.0;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    double a = Math.Max(0.0,
                        Math.Min(1.0, (1.0 - d) * n * 0.5));
                    px.Add(255);
                    px.Add(255);
                    px.Add(255);
                    px.Add((int)Math.Floor(a * 255));
                }
            }
            discAtlas = Atlas.fromPixels("lubx_disc", n, n, px, 1);
        }
        return discAtlas;
    }

    /// <summary>単色矩形。(x, y) は左上、座標系は quad と同じ論理 px。</summary>
    public void rect(double x, double y, double w, double h,
        Color? tint = null)
    {
        quad(ensureWhiteAtlas(), new Rect(0, 0, 4, 4), x, y, w, h, tint);
    }

    /// <summary>単色の円 (ソフトエッジの disc)。(cx, cy) は中心、r は半径 px。</summary>
    public void disc(double cx, double cy, double r, Color? tint = null)
    {
        sprite(ensureDiscAtlas(), new Rect(0, 0, 64, 64), cx, cy,
            r * 2.0, r * 2.0, 0.0, tint);
    }

    private BufferRef? ensureQuad()
    {
        if (quadData == null)
            quadData = new List<double>
            {
                -0.5, -0.5, 0.0, 0.0,
                0.5, -0.5, 1.0, 0.0,
                -0.5, 0.5, 0.0, 1.0,
                0.5, 0.5, 1.0, 1.0,
            };
        quadBuf = Gfx.use_buffer(bufferPrefix + "_quad", Gfx.VERTEX, quadData,
            1);
        return quadBuf;
    }

    /// <summary>積んだスプライトをアトラス単位で描画する。blend 省略で ALPHA。</summary>
    public void flush(int? blend = null)
    {
        if (!ensure())
            return;
        var sh = shader;
        if (sh == null)
            return;
        var quadVb = instanced ? ensureQuad() : null;
        if (instanced && quadVb == null)
            return;

        meshVersion = meshVersion + 1;
        var uniformParams = new List<double> { logicalW, logicalH, 0.0, 0.0 };
        int blendArg = blend ?? -1;
        int blendMode = blendArg < 0 ? Gfx.ALPHA : blendArg;
        foreach (var k in order)
        {
            var b = buckets[k];
            if (b.verts.Count == 0)
                continue;
            var tex = b.atlas.texture;
            if (tex == null)
                continue;
            if (!instanced)
            {
                var vbuf = Gfx.use_buffer(bufferPrefix + "_" + k + "_verts",
                    Gfx.VERTEX, b.verts, meshVersion);
                if (vbuf == null)
                    continue;
                Gfx.draw((int)Math.Floor(b.verts.Count / (double)LEGACY_STRIDE),
                    new Dictionary<string, object>
                    {
                        ["verts"] = vbuf,
                        ["atlas"] = tex,
                        ["uniforms"] = new Dictionary<string, object>
                        {
                            ["params"] = uniformParams,
                        },
                    },
                    new DrawOpts
                    {
                        shader = sh,
                        depth = false,
                        cull = Gfx.NONE,
                        blend = blendMode,
                    });
                continue;
            }
            var instances = Gfx.use_buffer(
                bufferPrefix + "_" + k + "_instances", Gfx.VERTEX, b.verts,
                meshVersion);
            if (instances == null || quadVb == null)
                continue;
            Gfx.draw(4,
                new Dictionary<string, object>
                {
                    ["verts"] = quadVb,
                    ["instances"] = instances,
                    ["atlas"] = tex,
                    ["uniforms"] = new Dictionary<string, object>
                    {
                        ["params"] = uniformParams,
                    },
                },
                new DrawOpts
                {
                    shader = sh,
                    depth = false,
                    cull = Gfx.NONE,
                    blend = blendMode,
                    primitive = Gfx.TRIANGLE_STRIP,
                    instance_count = (int)Math.Floor(
                        b.verts.Count / (double)INSTANCE_STRIDE),
                });
        }
    }
}
