// lub の samples/08_gltf (Haxe 版 Gltf08.hx) の TinyC# 版 entry。
// 実行: lub samples/08_gltf/Gltf08.csproj (transpile + watch + hot reload)
// glTF mesh (Box.glb) を法線可視化 shader + Y 軸回転 MVP で描く。
// load_gltf の mesh は動的な Lua table なので、tcs の型消去 cast
// ((Dictionary<string, object>) 等) で素の table アクセスに写す。
using System;
using System.Collections.Generic;
using static Lub;

public static class Gltf08
{
    static double tAccum = 0;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    static List<double> MakeMvp(double t)
    {
        // model: Y 軸回転
        var model = Mat4.RotateY(-t);
        // view: translate z = +3 (D3D-style LH: camera at origin looks down +Z;
        // move world +Z so the box sits in front of the camera)
        var view = Mat4.Translate(new Vec3(0.0, 0.0, 3.0));
        // proj: perspective with focal length f=2.0 directly (not an fov),
        // aspect=16/9, near=0.1, far=100
        double f = 2.0;
        double aspect = 16.0 / 9.0;
        double nz = 0.1;
        double fz = 100.0;
        var proj = Mat4.Zero();
        proj.M[0] = f / aspect;
        proj.M[5] = f;
        proj.M[10] = fz / (fz - nz);
        proj.M[11] = -fz * nz / (fz - nz);
        proj.M[14] = 1.0;
        var pvm = proj.Mul(view.Mul(model));
        return pvm.M;
    }

    public static void OnFrame(double dt)
    {
        tAccum = tAccum + dt * 0.96;

        Io.LoadText("samples/08_gltf/data/08_gltf.vs.slang",
            out var vs, out var verVs, out _, out _);
        Io.LoadText("samples/08_gltf/data/08_gltf.fs.slang",
            out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null) return;
        var shader = Gfx.UseShader("gltf_sh", vs, fs, verVs * 31 + verFs);

        Io.LoadGltf("samples/08_gltf/data/08_box.glb",
            out var mesh, out var meshVer, out _, out _);
        if (mesh == null) return;

        var verts = Io.InterleavePn(mesh);
        var vb = Gfx.UseBuffer("gltf_vb", Gfx.BufferType.Vertex, verts, meshVer);
        var ib = Gfx.UseBufferInts("gltf_ib", Gfx.BufferType.Index, mesh.Indices, meshVer);
        if (shader == null || vb == null || ib == null) return;

        var mvp = MakeMvp(tAccum);

        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.1, 0.1, 0.15, 1.0 },
        });
        Gfx.Draw(mesh.IndexCount,
            new Dictionary<string, object>
            {
                ["verts"] = vb,
                ["indices"] = ib,
                ["uniforms"] = new Dictionary<string, object>
                {
                    ["mvp"] = mvp,
                },
            },
            new DrawOpts
            {
                Shader = shader,
                Depth = true,
                DepthWrite = true,
                Cull = Gfx.Cull.Back,
            });
        Gfx.EndPass();
    }
}
