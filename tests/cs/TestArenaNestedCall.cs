// 大きな確保で binding の arena が chunk (1 MiB) を移った後でも、interleave の
// 出力が壊れない。positions (12 KB) と normals (約 1 MB) を渡すと次の chunk に移る。
// 外側が読む uvs が normals を上書きしていれば、出力の normal に uvs の値が混ざる。
// (Lua 版は mesh の table の __index から別の binding を呼んで入れ子の呼び出しを
// 起こしていたが、C# では引数の読み取りに割り込めないので入れ子の部分は移していない。)
using System;
using System.Collections.Generic;
using Lub;

public static class TestArenaNestedCall
{
    const int verts = 1000;
    const int normalFloats = 260144; // positions と合わせて 1 MiB の chunk に収まらない大きさ
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
