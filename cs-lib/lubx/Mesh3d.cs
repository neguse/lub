// Mesh3d of the implementation library lubx.
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>
/// Instance that turns MeshData (Mesh.sdf_mesh / Io.load_gltf / Shapes3d) into GPU buffers and holds them.
/// rebuild() uploads with a "change declaration" that omits version, so the caller does not need to manage version (just call rebuild() on every hot reload or edit).
/// MeshData with bones automatically uses the skinned layout (Io.interleave_pncmw, 20 floats), otherwise Io.interleave_pncm (16 floats: pos pad nrm pad albedo pad mr pad).
/// This vertex layout is the material contract of Renderer3d.
/// The buffers are re-asserted with the previous version each time ready() is called (data is not read), so they are not swept as long as they keep being drawn.
/// Renderer3d.Draw goes through ready(), so the drawing side does not need to think about re-asserting.
/// </summary>
public class Mesh3d
{
    private string key;
    // UseBuffer keys (not recreated by every frame's Ready)
    private string vbKey;
    private string ibKey;
    // Interleaved vertices and indices built by rebuild().
    // Used for re-asserting in ready() and for recreating after a sweep.
    private List<float>? verts = null;
    private List<float>? indices = null;

    public MeshData? Data;
    public BufferRef? Vb;
    public BufferRef? Ib;
    public int IndexCount = 0;
    public bool Skinned = false;

    public Mesh3d(string key)
    {
        this.key = key;
        vbKey = key + "/vb";
        ibKey = key + "/ib";
    }

    /// <summary>Replaces the mesh (including the first time).
    /// Re-uploads to the GPU on every call.
    /// </summary>
    public void Rebuild(MeshData data)
    {
        this.Data = data;
        Skinned = data.Bones != null;
        var v = Skinned ? Io.InterleavePncmw(data) : Io.InterleavePncm(data);
        Vb = Gfx.UseBuffer(vbKey, Gfx.BufferType.Storage, v);
        // use_buffer takes a List<float>, so indices are repacked.
        // The runtime copies the INDEX float sequence into uint32, so the GPU indices are the same as the original integers.
        var idx = new List<float>();
        foreach (var i in data.Indices)
        {
            idx.Add(i);
        }
        Ib = Gfx.UseBuffer(ibKey, Gfx.BufferType.Index, idx);
        verts = v;
        indices = idx;
        IndexCount = data.IndexCount;
    }

    /// <summary>Whether it has been rebuilt and can be drawn.
    /// Call it in every frame that draws (Renderer3d.Draw calls it).
    /// Re-asserts the buffers with the previous version to prevent sweep.
    /// </summary>
    public bool Ready()
    {
        if (Vb == null || Ib == null || verts == null || indices == null
            || IndexCount <= 0)
        {
            return false;
        }
        Vb = Gfx.UseBuffer(vbKey, Gfx.BufferType.Storage, verts, Vb.Version);
        Ib = Gfx.UseBuffer(ibKey, Gfx.BufferType.Index, indices, Ib.Version);
        return Vb != null && Ib != null;
    }
}
