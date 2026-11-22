// Entry for lub's samples/19_sdf.
// Run: lub samples/19_sdf/Sdf19.csproj (transpile + watch + hot reload)
//
// SDF modeling: the tree is built with the Lubx.Sdf builder, and the C side (sdf_mesh) evaluates it → meshes it with surface nets.
// Rewrite Model() and save, and hot reload remeshes it.
// Normals and materials (paint) are baked into the vertices from the SDF, so there are no UVs.
// Rendering uses the built-in shader of Renderer3d (skinned).
// (For SDFs that cannot be written as a tree, there is also a path that passes a hand-filled grid to Mesh.SurfaceNets)

using System;
using System.Collections.Generic;
using Lub;
using Lubx;

public static class Sdf19
{
    // Number of grid cells along the longest axis.
    // The bounds are determined automatically from the AABB of the tree.
    const int n = 64;

    // --- Model: snowman style ------------------------------------------------- Parts with bone(name, pivot) get skinning weights baked in.
    // The moving arms are placed left and right separately, because mirror would put the pivot on one side only (the eyes do not move, so they stay mirrored).
    static SdfNode Model()
    {
        var body = Sdf.Sphere(0.72f).Move(0, -0.42f, 0)
            .Bone("body", new Vec3(0, -0.42f, 0));
        var head = Sdf.Sphere(0.46f).Move(0, 0.48f, 0)
            .Bone("head", new Vec3(0, 0.10f, 0));
        var armL = Sdf.Capsule(new Vec3(0.56f, -0.32f, 0),
            new Vec3(1.04f, 0.24f, 0), 0.13f)
            .Bone("arm_l", new Vec3(0.56f, -0.32f, 0));
        var armR = Sdf.Capsule(new Vec3(-0.56f, -0.32f, 0),
            new Vec3(-1.04f, 0.24f, 0), 0.13f)
            .Bone("arm_r", new Vec3(-0.56f, -0.32f, 0));
        // Eyes: carved out smoothly with spheres (the camera is on the -Z side).
        // The cut surface shows the material of the cutter, so the eye color is set by "the paint of the carving sphere"
        var eye = Sdf.Sphere(0.11f).Move(0.17f, 0.56f, -0.40f).MirrorX()
            .Paint(0x1E2130, 0.0f, 0.15f);
        return body.Smin(head, 0.22f).Smin(armL, 0.10f).Smin(armR, 0.10f)
            .Paint(0xE58B52).Ssub(eye, 0.06f);
    }

    // --- Animation -------------------------------------------------------
    static bool waveOn = true;

    // Pack the joint matrices in mesh.bones order (the convention is in Lubx.Bones)
    static List<float> PackBones(float t, MeshData? data)
    {
        float wave = waveOn ? (float)Math.Sin(t * 4.0f) * 0.5f : 0.0f;
        float nod = waveOn ? (float)Math.Sin(t * 2.0f) * 0.10f : 0.0f;
        return Bones.Pack(data, (name, x, y, z) =>
        {
            switch (name)
            {
                case "arm_l":
                    return Bones.PivotRot(x, y, z, Mat4.RotateZ(-wave));
                case "arm_r":
                    return Bones.PivotRot(x, y, z, Mat4.RotateZ(wave));
                case "head":
                    return Bones.PivotRot(x, y, z, Mat4.RotateZ(-nod));
                default:
                    return null;
            }
        });
    }

    // --- Meshing ---------------------------------------------------------- mesh / ren are created in onInit.
    static Mesh3d? mesh = null;
    static Renderer3d? ren = null;
    static float tAccum = 0.0f;
    // treeDirty = rebuild the tree from code (on reload), meshDirty = re-evaluate (on edits in SdfPanel).
    // Panel edits apply directly to the tree (data), so they live until the next reload.
    // reload keeps statics, so the flag is set explicitly in OnReload(), which is called after the diff is applied.
    static SdfNode? tree = null;
    static bool treeDirty = true;
    static bool meshDirty = true;

    public static void OnReload()
    {
        treeDirty = true;
    }

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        App.Config(new ConfigOpts { Backend = backend });
        mesh = new Mesh3d("sdf19");
        var r = new Renderer3d("sdf19");
        r.Background = Color.Rgb(0.09f, 0.09f, 0.12f);
        ren = r;
    }

    static void Remesh(Mesh3d m, SdfNode t)
    {
        m.Rebuild(Sdf.Mesh(t, n));
        meshDirty = false;
    }

    public static void OnFrame(float dt)
    {
        tAccum = tAccum + dt * 0.96f;

        var m = mesh;
        var r = ren;
        if (m == null || r == null)
            return;

        if (treeDirty)
        {
            tree = Model(); // The code is the source of truth. reload discards panel edits
            treeDirty = false;
            meshDirty = true;
        }
        var t = tree;
        if (t == null)
            return;

        // debug UI: a panel generated automatically from the tree (data).
        // Remesh only on frames with edits (C evaluation takes ~10ms, so it keeps up with dragging).
        Ui.SetNextWindow(10, 10, 300, 460);
        if (Ui.BeginWindow("sdf tuning"))
        {
            if (SdfPanel.Draw(t))
                meshDirty = true;
            Ui.Separator();
            waveOn = Ui.Checkbox("wave", waveOn);
            var md = m.Data;
            if (m.Ready() && md != null)
                Ui.Text("verts: " + md.VertCount);
        }
        Ui.EndWindow();

        if (meshDirty)
            Remesh(m, t);

        var model = Mat4.RotateY(-tAccum * 0.7f);
        r.Begin(new Camera
        {
            Eye = new Vec3(0.0f, 0.55f, -3.1f),
            Target = new Vec3(0, 0.05f, 0),
            Fov = 45.0f,
            Near = 0.1f,
            Far = 100.0f,
        });
        r.Draw(m, model, new Draw3dOpts { Bones = PackBones(tAccum, m.Data) });
        r.End();

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        Ui.Render();
        Gfx.EndPass();
    }
}
