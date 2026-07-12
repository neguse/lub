// lub の samples/08_gltf (Haxe 版 Gltf08.hx) の TinyC# 版 entry。
// 実行: lub samples/08_gltf/Gltf08.csproj (transpile + watch + hot reload)
// glTF mesh (Box.glb) を法線可視化 shader + Y 軸回転 MVP で描く。
// load_gltf の mesh は動的な Lua table なので、tcs の型消去 cast
// ((Dictionary<string, object>) 等) で素の table アクセスに写す。
using System;
using System.Collections.Generic;

public static class Gltf08
{
    static double tAccum = 0;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    static List<double> MakeMvp(double t)
    {
        // model: Y 軸回転
        var model = Mat4.rotateY(-t);
        // view: translate z = +3 (D3D-style LH: camera at origin looks down +Z;
        // move world +Z so the box sits in front of the camera)
        var view = Mat4.translate(new Vec3(0.0, 0.0, 3.0));
        // proj: perspective with focal length f=2.0 directly (not an fov),
        // aspect=16/9, near=0.1, far=100
        double f = 2.0;
        double aspect = 16.0 / 9.0;
        double nz = 0.1;
        double fz = 100.0;
        var proj = Mat4.zero();
        proj.m[0] = f / aspect;
        proj.m[5] = f;
        proj.m[10] = fz / (fz - nz);
        proj.m[11] = -fz * nz / (fz - nz);
        proj.m[14] = 1.0;
        var pvm = proj.mul(view.mul(model));
        return pvm.m;
    }

    public static void onFrame(double dt)
    {
        tAccum = tAccum + 0.016;

        Io.load_text("samples/08_gltf/data/08_gltf.vs.slang",
            out var vs, out var verVs, out _, out _);
        Io.load_text("samples/08_gltf/data/08_gltf.fs.slang",
            out var fs, out var verFs, out _, out _);
        if (vs == null || fs == null) return;
        var shader = Gfx.use_shader("gltf_sh", vs, fs, verVs * 31 + verFs);

        Io.load_gltf("samples/08_gltf/data/08_box.glb",
            out var mesh, out var meshVer, out _, out _);
        if (mesh == null) return;
        var meshTbl = (Dictionary<string, object>)mesh;

        var verts = Io.interleave_pn(mesh);
        var vb = Gfx.use_buffer("gltf_vb", Gfx.VERTEX, verts, meshVer);
        var ib = Gfx.use_buffer("gltf_ib", Gfx.INDEX,
            (List<double>)meshTbl["indices"], meshVer);
        if (shader == null || vb == null || ib == null) return;

        var mvp = MakeMvp(tAccum);

        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.1, 0.1, 0.15, 1.0 },
        });
        Gfx.draw((int)meshTbl["index_count"],
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
                shader = shader,
                depth = true,
                depth_write = true,
                cull = Gfx.BACK,
            });
        Gfx.end_pass();
    }
}
