// Shapes3d of the implementation library lubx.
// MeshData is built with an object initializer of the MeshData class in the stub (cs-lib/lub_stub.cs).

using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>Generates unit primitives in MeshData form (indexed, positions + normals + white).
/// Can be passed to Mesh3d.rebuild() as is, and goes through the same rendering path as SDF / glTF meshes.
/// Coloring is done with the tint on the draw side (white × tint = the tint is the albedo as is).
/// The existing Shapes is a separate thing: non-indexed, Stride 12 generation for sfb / 11_shadow.
/// </summary>
public static class Shapes3d
{
    private static MeshData Mesh(List<float> positions, List<float> normals,
        List<int> indices)
    {
        // Vertex color is white (the interleave default is 0.8 gray).
        // So that the draw-side tint becomes the albedo as is.
        int n = positions.Count / 3;
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

    /// <summary>Converts the output of Shapes (Stride 12: pos3 pad normal3 pad rgba) into MeshData.
    /// Bridge for putting geometry built with the existing Shapes.box/quad/sphere onto Mesh3d / Renderer3d.
    /// alpha is dropped.
    /// </summary>
    public static MeshData FromInterleaved(List<float> v)
    {
        int n = v.Count / Shapes.Stride;
        var pos = new List<float>();
        var nrm = new List<float>();
        var col = new List<float>();
        var indices = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int o = i * Shapes.Stride;
            pos.Add(v[o]);
            pos.Add(v[o + 1]);
            pos.Add(v[o + 2]);
            nrm.Add(v[o + 4]);
            nrm.Add(v[o + 5]);
            nrm.Add(v[o + 6]);
            col.Add(v[o + 8]);
            col.Add(v[o + 9]);
            col.Add(v[o + 10]);
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

    /// <summary>Cube with side length 2 (centered at the origin, ±1).
    /// Scale it with the model matrix.
    /// </summary>
    public static MeshData Cube()
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var indices = new List<int>();
        // { normal n, in-plane basis u, v } of each face, laid out as the 9 elements n.xyz, u.xyz, v.xyz.
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
            int baseIdx = pos.Count / 3;
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

    /// <summary>Cylinder of height 1 (y = ±0.5) and radius 1.
    /// </summary>
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
            int i1 = (i + 1) % sides;
            int b1 = i1 * 2;
            foreach (var idx in new List<int> { b0, b0 + 1, b1 + 1, b0, b1 + 1, b1 })
                indices.Add(idx);
        }
        for (int side = 0; side < 2; side++)
        {
            float ny = side == 0 ? 1.0f : -1.0f;
            float y = ny * 0.5f;
            int center = pos.Count / 3;
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
                int i1 = (i + 1) % sides;
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

    /// <summary>UV sphere of radius 1.
    /// </summary>
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
