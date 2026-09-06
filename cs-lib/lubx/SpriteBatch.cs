// 実装ライブラリ lubx の SpriteBatch。
// バケットは class SpriteBucket、フィールドは ShaderRef / BufferRef の型付き。
// 頂点は List<float>.Add の逐次追加 (そのまま Lua array table)。
// デフォルト引数値は nullable + ?? で受ける (tcs は call site 展開しない)。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>アトラスごとの頂点バケット。</summary>
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
/// 2D スプライトのバッチ描画。毎フレーム begin() → sprite()/quad() を積む →
/// pass 内で flush() の順で使う。座標は論理解像度 (logicalW x logicalH) の
/// ピクセル、左上原点。アトラスごとに 1 draw にまとめられる。
/// </summary>
public class SpriteBatch
{
    public const int LegacyStride = 8;
    public const int VertexStride = 4;
    public const int InstanceStride = 14;

    private static string legacyVs = "struct Uniforms { float4 params; };\n"
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

    private static string instancedVs = "struct Uniforms { float4 params; };\n"
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
    private List<string> order = new List<string>();
    private string shaderKey;
    private string bufferPrefix;
    private bool instanced;
    private ShaderRef? shader = null;
    private BufferRef? quadBuf = null;
    private List<float>? quadData = null;

    /// <summary>shaderKey 省略で "lubx_sprite"、instanced 省略で true。</summary>
    public SpriteBatch(int logicalW, int logicalH, string? shaderKey = null,
        string? bufferPrefix = null, bool? instanced = null)
    {
        this.LogicalW = logicalW;
        this.LogicalH = logicalH;
        bool inst = instanced ?? true;
        this.shaderKey = (shaderKey ?? "lubx_sprite")
            + (inst ? "_instanced" : "_legacy");
        this.bufferPrefix = bufferPrefix ?? "lubx_sprite";
        this.instanced = inst;
    }

    public bool Ensure()
    {
        shader = Gfx.UseShader(shaderKey, instanced ? instancedVs : legacyVs,
            fs, 1);
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
    }

    private List<float>? BucketFor(Atlas a)
    {
        if (!buckets.TryGetValue(a.Key, out var b))
        {
            b = new SpriteBucket(a);
            buckets[a.Key] = b;
            order.Add(a.Key);
        }
        if (!b.Ready)
        {
            if (!a.Ensure())
                return null;
            b.Ready = true;
        }
        return b.Verts;
    }

    private Color ColorOrWhite(Color? c)
    {
        if (c != null)
            return c;
        return Color.Rgb(1.0f, 1.0f, 1.0f, 1.0f);
    }

    private void PushInstance(List<float> verts, float cx, float cy,
        float w, float h, float cr, float sr, float u0, float v0,
        float u1, float v1, Color c)
    {
        PushInstanceColor(verts, cx, cy, w, h, cr, sr, u0, v0, u1, v1,
            c.R, c.G, c.B, c.A);
    }

    // Add の逐次追加は Lua 上では array table への同順 append になる。
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

    /// <summary>アトラスの src 矩形を中心 (cx, cy)・radians 回転で描く。</summary>
    public void Sprite(Atlas a, Rect src, float cx, float cy, float w,
        float h, float radians, Color? tint = null)
    {
        var c = ColorOrWhite(tint);
        SpriteColor(a, src, cx, cy, w, h, (float)Math.Cos(radians), (float)Math.Sin(radians),
            c.R, c.G, c.B, c.A);
    }

    /// <summary>sprite の cos/sin・色成分ばらし版 (Color 生成を避ける hot path 用)。</summary>
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

    /// <summary>アトラスの src 矩形を左上 (x, y) に無回転で描く。</summary>
    public void Quad(Atlas a, Rect src, float x, float y, float w, float h,
        Color? tint = null)
    {
        var verts = BucketFor(a);
        if (verts == null)
            return;

        var c = ColorOrWhite(tint);
        float u0 = src.X / (float)a.W;
        float v0 = src.Y / (float)a.H;
        float u1 = (src.X + src.W) / (float)a.W;
        float v1 = (src.Y + src.H) / (float)a.H;
        if (instanced)
        {
            PushInstance(verts, x + w * 0.5f, y + h * 0.5f, w, h, 1.0f, 0.0f,
                u0, v0, u1, v1, c);
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
            // 64x64 の soft disc。tint で色を付ける。
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

    /// <summary>単色矩形。(x, y) は左上、座標系は quad と同じ論理 px。</summary>
    public void Rect(float x, float y, float w, float h,
        Color? tint = null)
    {
        Quad(EnsureWhiteAtlas(), new Rect(0, 0, 4, 4), x, y, w, h, tint);
    }

    /// <summary>単色の円 (ソフトエッジの disc)。(cx, cy) は中心、r は半径 px。</summary>
    public void Disc(float cx, float cy, float r, Color? tint = null)
    {
        Sprite(EnsureDiscAtlas(), new Rect(0, 0, 64, 64), cx, cy,
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
        quadBuf = Gfx.UseBuffer(bufferPrefix + "_quad", Gfx.BufferType.Vertex, quadData,
            1);
        return quadBuf;
    }

    /// <summary>積んだスプライトをアトラス単位で描画する。blend 省略で ALPHA。</summary>
    public void Flush(Gfx.Blend? blend = null)
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
        var blendMode = blend ?? Gfx.Blend.Alpha;
        foreach (var k in order)
        {
            var b = buckets[k];
            if (b.Verts.Count == 0)
                continue;
            var tex = b.Atlas.Texture;
            if (tex == null)
                continue;
            if (!instanced)
            {
                var vbuf = Gfx.UseBuffer(bufferPrefix + "_" + k + "_verts",
                    Gfx.BufferType.Vertex, b.Verts);
                if (vbuf == null)
                    continue;
                Gfx.Draw((int)Math.Floor(b.Verts.Count / (float)LegacyStride),
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
                        Shader = sh,
                        Depth = false,
                        Cull = Gfx.Cull.None,
                        Blend = blendMode,
                    });
                continue;
            }
            var instances = Gfx.UseBuffer(
                bufferPrefix + "_" + k + "_instances", Gfx.BufferType.Vertex, b.Verts);
            if (instances == null || quadVb == null)
                continue;
            Gfx.Draw(4,
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
                    Shader = sh,
                    Depth = false,
                    Cull = Gfx.Cull.None,
                    Blend = blendMode,
                    Primitive = Gfx.Primitive.TriangleStrip,
                    InstanceCount = (int)Math.Floor(
                        b.Verts.Count / (float)InstanceStride),
                });
        }
    }
}
