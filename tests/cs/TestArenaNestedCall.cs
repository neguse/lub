// The interleave output is not corrupted even after a large allocation moves the binding arena to another chunk (1 MiB).
// Passing positions (12 KB) and normals (about 1 MB) moves it to the next chunk.
// If the uvs read by the outer side overwrote normals, uvs values would mix into the output normals.
using System;
using System.Collections.Generic;
using Lub;

public static class TestArenaNestedCall
{
    const int verts = 1000;
    const int normalFloats = 260144; // A size that does not fit in a 1 MiB chunk together with positions
    const float normal = 0.25f;
    const float uv = 777.0f;
    const int stride = 8; // PN: position 3 + pad 1 + normal 3 + pad 1

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    public static void OnFrame(float dt)
    {
        var mesh = new MeshData { Positions = new List<float>(), Normals = new List<float>() };
        for (int i = 0; i < verts * 3; i++)
            mesh.Positions.Add(0.0f);
        for (int i = 0; i < normalFloats; i++)
            mesh.Normals.Add(normal);
        mesh.Uvs = new List<float>();
        for (int i = 0; i < verts * 2; i++)
            mesh.Uvs.Add(uv);
        mesh.VertCount = verts;

        var o = Io.InterleavePn(mesh);
        Check.That(o.Count == verts * stride, "unexpected output size " + o.Count);
        int corrupted = 0;
        for (int v = 0; v < verts; v++)
        {
            for (int k = 4; k <= 6; k++)
            {
                if (o[v * stride + k] != normal)
                    corrupted++;
            }
        }
        Check.That(corrupted == 0, "corrupted_normal_floats=" + corrupted);
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
