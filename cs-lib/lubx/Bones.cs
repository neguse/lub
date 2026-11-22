// Bones of the implementation library lubx.
// mesh.Bones is a typed List<SdfBone>.
// Iteration is bounded by List.Count, and resolve is received as a Func<> delegate.

using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>Bone matrix boilerplate for skinned SDF meshes (Sdf bone() nodes).
/// Convention: the shader has float4x4 bones[16], matrices are in mesh.bones order, and missing ones are filled with identity (used together with the Mesh3d skinned layout).
/// Animation (which bone to rotate and how) remains the game's job.
/// </summary>
public static class Bones
{
    /// <summary>Maximum number of bones (paired with float4x4 bones[16] on the shader side).
    /// </summary>
    public const int Max = 16;

    /// <summary>Rotation around pivot (px, py, pz) (model space).
    /// T(p) · R · T(−p).
    /// </summary>
    public static Mat4 PivotRot(float px, float py, float pz, Mat4 rot)
    {
        return Mat4.Translate(new Vec3(px, py, pz))
            * rot * Mat4.Translate(new Vec3(-px, -py, -pz));
    }

    /// <summary>Packs the matrices returned by resolve(name, x, y, z), in mesh.bones order, into mat4 × 16 = 256 floats.
    /// Bones for which resolve returns null get the identity matrix.
    /// (x, y, z) is that bone's pivot (can be passed to pivotRot as is).
    /// </summary>
    public static List<float> Pack(MeshData? mesh,
        Func<string, float, float, float, Mat4?> resolve)
    {
        var arr = new List<float>();
        int count = 0;
        if (mesh != null && mesh.Bones != null)
        {
            var bones = mesh.Bones;
            int n = bones.Count;
            int i = 0;
            while (count < Max && i < n)
            {
                var b = bones[i];
                var m = resolve(b.Name, b.X, b.Y, b.Z) ?? Mat4.Identity();
                foreach (var v in m.ToList())
                    arr.Add(v);
                count++;
                i++;
            }
        }
        while (count < Max)
        {
            var id = Mat4.Identity();
            foreach (var v in id.ToList())
                arr.Add(v);
            count++;
        }
        return arr;
    }
}
