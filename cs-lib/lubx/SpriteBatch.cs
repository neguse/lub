// 実装ライブラリ lubx の SpriteBatch。
// バケットは class SpriteBucket、フィールドは ShaderRef / BufferRef の型付き。
// 頂点は使い回す List<float> に添字で書く。List は伸ばすだけで縮めず、
// 足りなくなったときだけ倍に伸ばす (List.Clear() は Lua でクロージャを作り、
// Add を毎回呼ぶと table.insert になるので、どちらも毎フレームは使わない)。
// flush は先頭の Count 個だけを TransientBuffer に写して描く。
// デフォルト引数値は nullable + ?? で受ける (tcs は call site 展開しない)。
// 色を省略した sprite は Color を作らずに白の成分で積む。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>アトラスごとの頂点バケット。</summary>
public class SpriteBucket
{
    public Atlas Atlas;

    /// <summary>頂点 (instance) の float 列。先頭の Count 個が今のフレームの
    /// 中身で、その後ろは前のフレームの残り。</summary>
    public List<float> Verts = new List<float>();

    public int Count = 0;
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
    // 1 要素の float 数。shader 側 StructuredBuffer の struct と同じ並び
    // (float2 は 8、float4 は 16 byte 境界に置くため pad が入る)。
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
            b.Count = 0;
            b.Ready = false;
        }
    }

    private SpriteBucket? BucketFor(Atlas a)
    {
        // TryGetValue の out は Lua でクロージャになるので、ContainsKey と
        // 添字で引く
        SpriteBucket b;
        if (buckets.ContainsKey(a.Key))
        {
            b = buckets[a.Key];
        }
        else
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
        return b;
    }

    // bucket に n 個の float を書ける場所を空け、書き始めの添字を返す。
    // Verts は足りなくなったときだけ倍に伸ばす。
    private int Reserve(SpriteBucket bucket, int n)
    {
        int o = bucket.Count;
        var verts = bucket.Verts;
        if (o + n > verts.Count)
        {
            int cap = verts.Count * 2;
            if (cap < o + n)
                cap = o + n;
            while (verts.Count < cap)
                verts.Add(0.0f);
        }
        bucket.Count = o + n;
        return o;
    }

    private void PushInstanceColor(SpriteBucket bucket, float cx, float cy,
        float w, float h, float cr, float sr, float u0, float v0,
        float u1, float v1, float r, float g, float b, float alpha)
    {
        int o = Reserve(bucket, InstanceStride);
        var verts = bucket.Verts;
        verts[o] = cx;
        verts[o + 1] = cy;
        verts[o + 2] = w;
        verts[o + 3] = h;
        verts[o + 4] = cr;
        verts[o + 5] = sr;
        verts[o + 6] = 0.0f; // pad0 (float4 uv_rect は 16 byte 境界)
        verts[o + 7] = 0.0f;
        verts[o + 8] = u0;
        verts[o + 9] = v0;
        verts[o + 10] = u1;
        verts[o + 11] = v1;
        verts[o + 12] = r;
        verts[o + 13] = g;
        verts[o + 14] = b;
        verts[o + 15] = alpha;
    }

    // 1 頂点 (LegacyStride 個) を書く。o は書き始めの添字。
    private void PutVertexColor(List<float> verts, int o, float x, float y,
        float u, float v, float r, float g, float b, float alpha)
    {
        verts[o] = x;
        verts[o + 1] = y;
        verts[o + 2] = u;
        verts[o + 3] = v;
        verts[o + 4] = r;
        verts[o + 5] = g;
        verts[o + 6] = b;
        verts[o + 7] = alpha;
    }

    private void PutRotColor(List<float> verts, int o, float cx, float cy,
        float ox, float oy, float cr, float sr, float u, float v,
        float r, float g, float b, float alpha)
    {
        PutVertexColor(verts, o, cx + ox * cr - oy * sr,
            cy + ox * sr + oy * cr, u, v, r, g, b, alpha);
    }

    /// <summary>アトラスの src 矩形を中心 (cx, cy)・radians 回転で描く。</summary>
    public void Sprite(Atlas a, Rect src, float cx, float cy, float w,
        float h, float radians, Color? tint = null)
    {
        float r = 1.0f;
        float g = 1.0f;
        float b = 1.0f;
        float alpha = 1.0f;
        if (tint != null)
        {
            r = tint.R;
            g = tint.G;
            b = tint.B;
            alpha = tint.A;
        }
        SpriteColor(a, src, cx, cy, w, h, (float)Math.Cos(radians), (float)Math.Sin(radians),
            r, g, b, alpha);
    }

    /// <summary>sprite の cos/sin・色成分ばらし版 (Color 生成を避ける hot path 用)。</summary>
    public void SpriteColor(Atlas a, Rect src, float cx, float cy, float w,
        float h, float cr, float sr, float r, float g, float b,
        float alpha)
    {
        // uv は bucket を引いた後に計算する (PNG の atlas は Ensure() で
        // 大きさが決まる)
        var bucket = BucketFor(a);
        if (bucket == null)
            return;

        float u0 = src.X / (float)a.W;
        float v0 = src.Y / (float)a.H;
        float u1 = (src.X + src.W) / (float)a.W;
        float v1 = (src.Y + src.H) / (float)a.H;
        PutSprite(bucket, cx, cy, w, h, cr, sr, u0, v0, u1, v1, r, g, b, alpha);
    }

    private void PutSprite(SpriteBucket bucket, float cx, float cy, float w,
        float h, float cr, float sr, float u0, float v0, float u1, float v1,
        float r, float g, float b, float alpha)
    {
        if (instanced)
        {
            PushInstanceColor(bucket, cx, cy, w, h, cr, sr, u0, v0, u1, v1,
                r, g, b, alpha);
            return;
        }

        float hw = w * 0.5f;
        float hh = h * 0.5f;
        int o = Reserve(bucket, LegacyStride * 6);
        var verts = bucket.Verts;
        PutRotColor(verts, o, cx, cy, -hw, -hh, cr, sr, u0, v0, r, g, b, alpha);
        PutRotColor(verts, o + 8, cx, cy, hw, -hh, cr, sr, u1, v0, r, g, b, alpha);
        PutRotColor(verts, o + 16, cx, cy, hw, hh, cr, sr, u1, v1, r, g, b, alpha);
        PutRotColor(verts, o + 24, cx, cy, -hw, -hh, cr, sr, u0, v0, r, g, b, alpha);
        PutRotColor(verts, o + 32, cx, cy, hw, hh, cr, sr, u1, v1, r, g, b, alpha);
        PutRotColor(verts, o + 40, cx, cy, -hw, hh, cr, sr, u0, v1, r, g, b, alpha);
    }

    /// <summary>アトラスの src 矩形を左上 (x, y) に無回転で描く。</summary>
    public void Quad(Atlas a, Rect src, float x, float y, float w, float h,
        Color? tint = null)
    {
        var bucket = BucketFor(a);
        if (bucket == null)
            return;

        float u0 = src.X / (float)a.W;
        float v0 = src.Y / (float)a.H;
        float u1 = (src.X + src.W) / (float)a.W;
        float v1 = (src.Y + src.H) / (float)a.H;
        PutQuad(bucket, x, y, w, h, u0, v0, u1, v1, tint);
    }

    private void PutQuad(SpriteBucket bucket, float x, float y, float w,
        float h, float u0, float v0, float u1, float v1, Color? tint)
    {
        float r = 1.0f;
        float g = 1.0f;
        float b = 1.0f;
        float alpha = 1.0f;
        if (tint != null)
        {
            r = tint.R;
            g = tint.G;
            b = tint.B;
            alpha = tint.A;
        }
        if (instanced)
        {
            PushInstanceColor(bucket, x + w * 0.5f, y + h * 0.5f, w, h,
                1.0f, 0.0f, u0, v0, u1, v1, r, g, b, alpha);
            return;
        }

        float x1 = x + w;
        float y1 = y + h;
        int o = Reserve(bucket, LegacyStride * 6);
        var verts = bucket.Verts;
        PutVertexColor(verts, o, x, y, u0, v0, r, g, b, alpha);
        PutVertexColor(verts, o + 8, x1, y, u1, v0, r, g, b, alpha);
        PutVertexColor(verts, o + 16, x1, y1, u1, v1, r, g, b, alpha);
        PutVertexColor(verts, o + 24, x, y, u0, v0, r, g, b, alpha);
        PutVertexColor(verts, o + 32, x1, y1, u1, v1, r, g, b, alpha);
        PutVertexColor(verts, o + 40, x, y1, u0, v1, r, g, b, alpha);
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

    // Rect / Disc は atlas の全面を使う (uv 0..1)。全面の src 矩形から
    // Quad / Sprite が計算する uv と同じ値になる。

    /// <summary>単色矩形。(x, y) は左上、座標系は quad と同じ論理 px。</summary>
    public void Rect(float x, float y, float w, float h,
        Color? tint = null)
    {
        var bucket = BucketFor(EnsureWhiteAtlas());
        if (bucket == null)
            return;
        PutQuad(bucket, x, y, w, h, 0.0f, 0.0f, 1.0f, 1.0f, tint);
    }

    /// <summary>単色の円 (ソフトエッジの disc)。(cx, cy) は中心、r は半径 px。</summary>
    public void Disc(float cx, float cy, float r, Color? tint = null)
    {
        var bucket = BucketFor(EnsureDiscAtlas());
        if (bucket == null)
            return;
        float cr = 1.0f;
        float cg = 1.0f;
        float cb = 1.0f;
        float ca = 1.0f;
        if (tint != null)
        {
            cr = tint.R;
            cg = tint.G;
            cb = tint.B;
            ca = tint.A;
        }
        PutSprite(bucket, cx, cy, r * 2.0f, r * 2.0f, 1.0f, 0.0f, 0.0f, 0.0f,
            1.0f, 1.0f, cr, cg, cb, ca);
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
        quadBuf = Gfx.UseBuffer(bufferPrefix + "_quad", Gfx.BufferType.Storage, quadData,
            1);
        return quadBuf;
    }

    /// <summary>積んだスプライトをアトラス単位で描画する。blend 省略で ALPHA。
    /// 頂点はアトラスごとに TransientBuffer に写すので、同じフレームに
    /// Begin() から積み直して何度 flush してもよい。</summary>
    public void Flush(Gfx.Blend? blend = null)
    {
        if (!Ensure())
            return;
        var sh = shader;
        if (sh == null)
            return;
        BufferRef? quadVb = null;
        if (instanced)
        {
            quadVb = EnsureQuad();
            if (quadVb == null)
                return;
        }

        var uniformParams = new List<float> { LogicalW, LogicalH, 0.0f, 0.0f };
        var blendMode = blend ?? Gfx.Blend.Alpha;
        foreach (var k in order)
        {
            var b = buckets[k];
            if (b.Count == 0)
                continue;
            var tex = b.Atlas.Texture;
            if (tex == null)
                continue;
            var data = Gfx.TransientBuffer(Gfx.BufferType.Storage, b.Verts, b.Count);
            if (data == null)
                continue;
            if (!instanced)
            {
                Gfx.Draw((int)Math.Floor(b.Count / (float)LegacyStride),
                    new Dictionary<string, object>
                    {
                        ["verts"] = data,
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
            if (quadVb == null)
                continue;
            Gfx.Draw(4,
                new Dictionary<string, object>
                {
                    ["verts"] = quadVb,
                    ["insts"] = data,
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
                        b.Count / (float)InstanceStride),
                });
        }
    }
}
