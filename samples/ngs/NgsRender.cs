using System;
using System.Collections.Generic;
using static Lub;

/// <summary>playfield の world 座標 → 画面座標。playfield は画面中央の 240×480。</summary>
public static class NgsViewport
{
    public const int X = 200;
    public const int Y = 0;
    public const int W = 240;
    public const int H = 480;

    public static int Sx(int worldX) => worldX - X;

    public static int Sy(int worldY) => worldY - Y;

    public static Rect Bounds() => new Rect(X, Y, W, H);
}

public class NgsBucket
{
    public Atlas Atlas;
    public List<float> Verts = new List<float>();

    public NgsBucket(Atlas atlas)
    {
        Atlas = atlas;
    }
}

/// <summary>atlas ごとに頂点を溜め、frame の終わりに 1 draw ずつ出す。挿入順を
/// 保って draw 順を決定的にする。</summary>
public class NgsDrawList
{
    public const int Stride = 8;

    readonly Dictionary<string, NgsBucket> buckets = new Dictionary<string, NgsBucket>();
    readonly List<string> order = new List<string>();

    public ShaderRef Shader;

    public NgsDrawList(ShaderRef shader)
    {
        Shader = shader;
    }

    public void Begin()
    {
        foreach (var k in order) buckets[k].Verts.Clear();
    }

    List<float> BucketFor(Atlas a)
    {
        if (!buckets.ContainsKey(a.Key))
        {
            buckets[a.Key] = new NgsBucket(a);
            order.Add(a.Key);
        }
        return buckets[a.Key].Verts;
    }

    static void Vtx(List<float> o, float x, float y, float u, float v, Color c)
    {
        o.Add(x);
        o.Add(y);
        o.Add(u);
        o.Add(v);
        o.Add(c.R);
        o.Add(c.G);
        o.Add(c.B);
        o.Add(c.A);
    }

    // src = atlas 内 pixel rect、dst = logical 画面 pixel での左上 (dx, dy)。
    public void Sprite(Atlas a, Rect src, int dx, int dy, Color? tint = null)
    {
        var c = tint ?? Color.Rgb(1.0f, 1.0f, 1.0f, 1.0f);
        float u0 = (float)src.X / a.W;
        float v0 = (float)src.Y / a.H;
        float u1 = (float)(src.X + src.W) / a.W;
        float v1 = (float)(src.Y + src.H) / a.H;
        float x0 = dx;
        float y0 = dy;
        float x1 = dx + src.W;
        float y1 = dy + src.H;
        var o = BucketFor(a);
        Vtx(o, x0, y0, u0, v0, c);
        Vtx(o, x1, y0, u1, v0, c);
        Vtx(o, x1, y1, u1, v1, c);
        Vtx(o, x0, y0, u0, v0, c);
        Vtx(o, x1, y1, u1, v1, c);
        Vtx(o, x0, y1, u0, v1, c);
    }

    public void Flush()
    {
        foreach (var k in order)
        {
            var b = buckets[k];
            if (b.Verts.Count == 0) continue;
            var vbuf = Gfx.UseBuffer("ngs_dl_" + k, Gfx.BufferType.Vertex, b.Verts);
            if (vbuf == null) continue;
            Gfx.Draw(b.Verts.Count / Stride, new Dictionary<string, object>
            {
                ["verts"] = vbuf,
                ["atlas"] = b.Atlas.Texture!,
            }, new DrawOpts
            {
                Shader = Shader,
                Depth = false,
                Cull = Gfx.Cull.None,
                Blend = Gfx.Blend.Alpha,
            });
        }
    }
}

/// <summary>8×8 の ASCII bitmap font (16 列 grid)。</summary>
public class NgsFont
{
    public readonly Atlas Atlas;

    public const int GW = 8;
    public const int GH = 8;
    public const int Cols = 16;

    public NgsFont(Atlas atlas)
    {
        Atlas = atlas;
    }

    // ASCII 0x20..0x7F を 16 列グリッドで引く。それ以外は空白扱い。
    public void DrawString(NgsDrawList dl, int x, int y, string s, Color? tint = null)
    {
        int i = 0;
        foreach (var r in s.EnumerateRunes())
        {
            int ch = r.Value;
            if (ch >= 0x20 && ch <= 0x7f)
            {
                int gi = ch - 0x20;
                int col = gi % Cols;
                int row = gi / Cols;
                dl.Sprite(Atlas, new Rect(col * GW, row * GH, GW, GH), x + i * GW, y, tint);
            }
            i = i + 1;
        }
    }

    // width 桁の先頭ゼロ詰め整数描画。負数は符号を保ったまま数値部だけ詰める。
    public void DrawInt(NgsDrawList dl, int x, int y, int n, int width, Color? tint = null)
    {
        bool neg = n < 0;
        string digits = (neg ? -n : n).ToString();
        while (digits.Length < width) digits = "0" + digits;
        DrawString(dl, x, y, neg ? "-" + digits : digits, tint);
    }
}

/// <summary>sprite shader と 1×1 の白 atlas。毎 frame Ensure で確保する。</summary>
public class NgsGfx2d
{
    public NgsDrawList? DrawList = null;
    public Atlas? White = null; // quad 用 1x1 白

    ShaderRef? shader = null;

    // 毎フレーム冒頭で呼ぶ。shader / white を確保 (idempotent)。失敗時 false。
    public bool Ensure()
    {
        Io.LoadText("samples/ngs/data/sprite.vs.slang", out var vs, out var vsv, out _, out _);
        Io.LoadText("samples/ngs/data/sprite.fs.slang", out var fs, out var fsv, out _, out _);
        if (vs == null || fs == null) return false;
        shader = Gfx.UseShader("ngs_sprite", vs, fs, vsv * 31 + fsv);
        if (shader == null) return false;
        if (White == null)
        {
            White = Atlas.FromPixels("ngs_white", 1, 1, new List<int> { 255, 255, 255, 255 }, 1,
                new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp });
        }
        if (!White.Ensure()) return false;
        if (DrawList == null) DrawList = new NgsDrawList(shader);
        else DrawList.Shader = shader;
        return true;
    }

    public void BeginFrame()
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.0f, 0.0f, 0.0f, 1.0f } });
        DrawList!.Begin();
    }

    public void EndFrame()
    {
        DrawList!.Flush();
        Gfx.EndPass();
    }
}
