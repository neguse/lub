// 実装ライブラリ lubx の Shapes3d。
// List<float>/List<int> はそのまま Lua array table。MeshData は stub
// (cs-lib/lub_stub.cs) の
// MeshData class の object initializer で構築 (--ref 型は plain table に落ちる)。
// Std.int(len / k) は整数除算を避けて Math.Floor(len / k.0)、
// (i + 1) % sides の剰余は wrap 分岐 (i は 0..sides-1) で書く。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>単位プリミティブを MeshData 形式 (indexed、positions + normals +
/// 白色) で生成する。Mesh3d.rebuild() にそのまま渡せて、SDF / glTF メッシュと
/// 同じ描画経路に乗る。着色は draw 側の tint で (白 × tint = tint がそのまま
/// albedo)。既存 Shapes は sfb / 11_shadow 用の非 indexed・stride 10 生成で、
/// 別物。</summary>
public static class Shapes3d
{
    private static MeshData Mesh(List<float> positions, List<float> normals,
        List<int> indices)
    {
        // 頂点色は白 (interleave 既定は 0.8 グレー)。draw 側の tint がそのまま
        // albedo になるように。
        int n = (int)Math.Floor(positions.Count / 3.0f);
        var colors = new List<float>();
        for (int i = 0; i < n * 3; i++)
            colors.Add(1.0f);
        return new MeshData
        {
            Positions = positions,
            Normals = normals,
            Colors = colors,
            Indices = indices,
            VertCount = n,
            IndexCount = indices.Count,
        };
    }

    /// <summary>Shapes (stride 10: pos3 + normal3 + rgba) の生成結果を MeshData
    /// に変換する。既存の Shapes.box/quad/sphere で組んだジオメトリを
    /// Mesh3d / Renderer3d に載せるためのブリッジ。alpha は落ちる。</summary>
    public static MeshData FromInterleaved(List<float> v)
    {
        int n = (int)Math.Floor(v.Count / 10.0f);
        var pos = new List<float>();
        var nrm = new List<float>();
        var col = new List<float>();
        var indices = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int o = i * 10;
            pos.Add(v[o]);
            pos.Add(v[o + 1]);
            pos.Add(v[o + 2]);
            nrm.Add(v[o + 3]);
            nrm.Add(v[o + 4]);
            nrm.Add(v[o + 5]);
            col.Add(v[o + 6]);
            col.Add(v[o + 7]);
            col.Add(v[o + 8]);
            indices.Add(i);
        }
        return new MeshData
        {
            Positions = pos,
            Normals = nrm,
            Colors = col,
            Indices = indices,
            VertCount = n,
            IndexCount = n,
        };
    }

    /// <summary>辺長 2 の立方体 (中心原点、±1)。scale は model 行列で。</summary>
    public static MeshData Cube()
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var indices = new List<int>();
        // 各面の { 法線 n, 面内基底 u, v } を n.xyz, u.xyz, v.xyz の 9 要素で
        // 並べたもの。
        var faces = new List<List<float>>
        {
            new List<float> { 1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f },
            new List<float> { -1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f },
            new List<float> { 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 1.0f, 0.0f, 0.0f },
            new List<float> { 0.0f, -1.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f },
            new List<float> { 0.0f, 0.0f, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f },
            new List<float> { 0.0f, 0.0f, -1.0f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 0.0f },
        };
        foreach (var f in faces)
        {
            int baseIdx = (int)Math.Floor(pos.Count / 3.0f);
            for (int i = 0; i < 4; i++)
            {
                float su = (i == 1 || i == 2) ? 1.0f : -1.0f;
                float sv = (i >= 2) ? 1.0f : -1.0f;
                for (int k = 0; k < 3; k++)
                    pos.Add(f[k] + f[3 + k] * su + f[6 + k] * sv);
                for (int k = 0; k < 3; k++)
                    nrm.Add(f[k]);
            }
            foreach (var idx in new List<int> { 0, 1, 2, 0, 2, 3 })
                indices.Add(baseIdx + idx);
        }
        return Mesh(pos, nrm, indices);
    }

    /// <summary>高さ 1 (y = ±0.5)、半径 1 の円柱。</summary>
    public static MeshData Cylinder(int sides)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var indices = new List<int>();
        for (int i = 0; i < sides; i++)
        {
            float a = (float)i / sides * (float)Math.PI * 2.0f;
            float nx = (float)Math.Cos(a);
            float nz = (float)Math.Sin(a);
            pos.Add(nx);
            pos.Add(-0.5f);
            pos.Add(nz);
            nrm.Add(nx);
            nrm.Add(0.0f);
            nrm.Add(nz);
            pos.Add(nx);
            pos.Add(0.5f);
            pos.Add(nz);
            nrm.Add(nx);
            nrm.Add(0.0f);
            nrm.Add(nz);
        }
        for (int i = 0; i < sides; i++)
        {
            int b0 = i * 2;
            int i1 = i + 1 == sides ? 0 : i + 1;
            int b1 = i1 * 2;
            foreach (var idx in new List<int> { b0, b0 + 1, b1 + 1, b0, b1 + 1, b1 })
                indices.Add(idx);
        }
        for (int side = 0; side < 2; side++)
        {
            float ny = side == 0 ? 1.0f : -1.0f;
            float y = ny * 0.5f;
            int center = (int)Math.Floor(pos.Count / 3.0f);
            pos.Add(0.0f);
            pos.Add(y);
            pos.Add(0.0f);
            nrm.Add(0.0f);
            nrm.Add(ny);
            nrm.Add(0.0f);
            for (int i = 0; i < sides; i++)
            {
                float a = (float)i / sides * (float)Math.PI * 2.0f;
                pos.Add((float)Math.Cos(a));
                pos.Add(y);
                pos.Add((float)Math.Sin(a));
                nrm.Add(0.0f);
                nrm.Add(ny);
                nrm.Add(0.0f);
            }
            for (int i = 0; i < sides; i++)
            {
                int i1 = i + 1 == sides ? 0 : i + 1;
                int r0 = center + 1 + i;
                int r1 = center + 1 + i1;
                if (ny > 0)
                {
                    indices.Add(center);
                    indices.Add(r0);
                    indices.Add(r1);
                }
                else
                {
                    indices.Add(center);
                    indices.Add(r1);
                    indices.Add(r0);
                }
            }
        }
        return Mesh(pos, nrm, indices);
    }

    /// <summary>半径 1 の UV 球。</summary>
    public static MeshData Sphere(int stacks, int slices)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var indices = new List<int>();
        for (int st = 0; st < stacks + 1; st++)
        {
            float phi = (float)st / stacks * (float)Math.PI;
            float y = (float)Math.Cos(phi);
            float r = (float)Math.Sin(phi);
            for (int sl = 0; sl < slices + 1; sl++)
            {
                float th = (float)sl / slices * (float)Math.PI * 2.0f;
                float x = r * (float)Math.Cos(th);
                float z = r * (float)Math.Sin(th);
                pos.Add(x);
                pos.Add(y);
                pos.Add(z);
                nrm.Add(x);
                nrm.Add(y);
                nrm.Add(z);
            }
        }
        for (int st = 0; st < stacks; st++)
        {
            for (int sl = 0; sl < slices; sl++)
            {
                int a = st * (slices + 1) + sl;
                int b = a + slices + 1;
                foreach (var idx in new List<int> { a, b, a + 1, a + 1, b, b + 1 })
                    indices.Add(idx);
            }
        }
        return Mesh(pos, nrm, indices);
    }
}
