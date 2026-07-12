// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Shapes.hx と対)。
// Haxe の Array<Float> は List<double> がそのまま Lua array table になる。
// 頂点 push 先の引数名は Haxe 版では out だが C# の予約語のため dst
// (Lua 呼び出しは位置引数なので互換)。Int / Int の除算は C# では切り捨てに
// なるので、Haxe と同じ実数除算になるよう (double) キャスト (tcs は型消去で
// Lua 出力は素の `/` のまま) で書く。

using System;
using System.Collections.Generic;

/// <summary>手続き 3D プリミティブの頂点生成。interleaved
/// pos.xyz + normal.xyz + color.rgba (STRIDE=10) を dst に push する。
/// Gfx.use_buffer にそのまま渡せる。</summary>
public static class Shapes
{
    public const int STRIDE = 10; // pos.xyz + normal.xyz + color.rgba

    /// <summary>頂点1つ。</summary>
    public static void vertex(List<double> dst, double x, double y, double z,
        double nx, double ny, double nz, List<double> col)
    {
        dst.Add(x);
        dst.Add(y);
        dst.Add(z);
        dst.Add(nx);
        dst.Add(ny);
        dst.Add(nz);
        dst.Add(col[0]);
        dst.Add(col[1]);
        dst.Add(col[2]);
        dst.Add(col[3]);
    }

    /// <summary>三角形 (a, b, c は [x,y,z]、n は [nx,ny,nz])。</summary>
    public static void tri(List<double> dst, List<double> a, List<double> b,
        List<double> c, List<double> n, List<double> col)
    {
        vertex(dst, a[0], a[1], a[2], n[0], n[1], n[2], col);
        vertex(dst, b[0], b[1], b[2], n[0], n[1], n[2], col);
        vertex(dst, c[0], c[1], c[2], n[0], n[1], n[2], col);
    }

    /// <summary>四角形 = tri(a,b,c) + tri(a,c,d)。</summary>
    public static void quad(List<double> dst, List<double> a, List<double> b,
        List<double> c, List<double> d, List<double> n, List<double> col)
    {
        tri(dst, a, b, c, n, col);
        tri(dst, a, c, d, n, col);
    }

    /// <summary>中心 (cx,cy,cz)、辺長 (sx,sy,sz) の直方体。面の順序・法線は
    /// Haxe 版 (= Shadow11.addBox) と同一。</summary>
    public static void box(List<double> dst, double cx, double cy, double cz,
        double sx, double sy, double sz, List<double> col)
    {
        double x0 = cx - sx * 0.5;
        double x1 = cx + sx * 0.5;
        double y0 = cy - sy * 0.5;
        double y1 = cy + sy * 0.5;
        double z0 = cz - sz * 0.5;
        double z1 = cz + sz * 0.5;

        var p000 = new List<double> { x0, y0, z0 };
        var p100 = new List<double> { x1, y0, z0 };
        var p010 = new List<double> { x0, y1, z0 };
        var p110 = new List<double> { x1, y1, z0 };
        var p001 = new List<double> { x0, y0, z1 };
        var p101 = new List<double> { x1, y0, z1 };
        var p011 = new List<double> { x0, y1, z1 };
        var p111 = new List<double> { x1, y1, z1 };

        quad(dst, p000, p010, p110, p100, new List<double> { 0.0, 0.0, -1.0 }, col);
        quad(dst, p001, p101, p111, p011, new List<double> { 0.0, 0.0, 1.0 }, col);
        quad(dst, p000, p001, p011, p010, new List<double> { -1.0, 0.0, 0.0 }, col);
        quad(dst, p100, p110, p111, p101, new List<double> { 1.0, 0.0, 0.0 }, col);
        quad(dst, p010, p011, p111, p110, new List<double> { 0.0, 1.0, 0.0 }, col);
        quad(dst, p000, p100, p101, p001, new List<double> { 0.0, -1.0, 0.0 }, col);
    }

    private static List<double> spherePoint(double cx, double cy, double cz,
        double r, double u, double vv)
    {
        double cv = Math.Cos(vv);
        double nx = Math.Cos(u) * cv;
        double ny = Math.Sin(vv);
        double nz = Math.Sin(u) * cv;
        return new List<double> { cx + nx * r, cy + ny * r, cz + nz * r, nx, ny, nz };
    }

    /// <summary>UV 球。rings/segs のデフォルトと頂点順は Haxe 版
    /// (= Shadow11.addSphere) と同一 (rings=12, segs=24)。省略時は
    /// Lua の nil が null に落ちるので nullable + ?? で受ける。</summary>
    public static void sphere(List<double> dst, double cx, double cy, double cz,
        double r, List<double> col, int? rings = null, int? segs = null)
    {
        int ringCount = rings ?? 12;
        int segCount = segs ?? 24;
        for (int ring = 0; ring < ringCount; ring++)
        {
            double v0 = -Math.PI * 0.5 + (double)ring / ringCount * Math.PI;
            double v1 = -Math.PI * 0.5 + (double)(ring + 1) / ringCount * Math.PI;
            for (int seg = 0; seg < segCount; seg++)
            {
                double u0 = (double)seg / segCount * Math.PI * 2;
                double u1 = (double)(seg + 1) / segCount * Math.PI * 2;
                var a = spherePoint(cx, cy, cz, r, u0, v0);
                var b = spherePoint(cx, cy, cz, r, u1, v0);
                var c = spherePoint(cx, cy, cz, r, u1, v1);
                var d = spherePoint(cx, cy, cz, r, u0, v1);
                vertex(dst, a[0], a[1], a[2], a[3], a[4], a[5], col);
                vertex(dst, b[0], b[1], b[2], b[3], b[4], b[5], col);
                vertex(dst, c[0], c[1], c[2], c[3], c[4], c[5], col);
                vertex(dst, a[0], a[1], a[2], a[3], a[4], a[5], col);
                vertex(dst, c[0], c[1], c[2], c[3], c[4], c[5], col);
                vertex(dst, d[0], d[1], d[2], d[3], d[4], d[5], col);
            }
        }
    }
}
