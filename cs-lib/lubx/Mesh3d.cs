// 実装ライブラリ lubx の Mesh3d。
using System.Collections.Generic;
using static Lub;

/// <summary>
/// MeshData (Mesh.sdf_mesh / Io.load_gltf / Shapes3d) を GPU buffer にして
/// 保持するインスタンス。rebuild() は version 省略の「変更宣言」で upload
/// するので、呼び側が version を管理する必要はない (hot reload や編集のたびに
/// rebuild() を呼べばよい)。bones を持つ MeshData は自動で skinned レイアウト
/// (Io.interleave_pncmw、stride 15)、それ以外は Io.interleave_pncm
/// (stride 11: pos.xyz + normal.xyz + albedo.rgb + mr.xy)。
/// この頂点レイアウトが Renderer3d の material 契約。
/// </summary>
public class Mesh3d
{
    private string key;

    public MeshData? Data;
    public BufferRef? Vb;
    public BufferRef? Ib;
    public int IndexCount = 0;
    public bool Skinned = false;

    public Mesh3d(string key)
    {
        this.key = key;
    }

    /// <summary>メッシュを差し替える (初回含む)。呼ぶたびに GPU へ再アップロード。</summary>
    public void Rebuild(MeshData data)
    {
        this.Data = data;
        Skinned = data.Bones != null;
        var verts = Skinned ? Io.InterleavePncmw(data) : Io.InterleavePncm(data);
        Vb = Gfx.UseBuffer(key + "_vb", Gfx.BufferType.Vertex, verts);
        // use_buffer は List<float> を取るので indices を詰め替える。
        // Lua 上は同じ整数値の array table になり、wire data は変わらない。
        var indices = new List<float>();
        foreach (var i in data.Indices)
        {
            indices.Add(i);
        }
        Ib = Gfx.UseBuffer(key + "_ib", Gfx.BufferType.Index, indices);
        IndexCount = data.IndexCount;
    }

    /// <summary>rebuild 済みで描画可能か。</summary>
    public bool Ready()
    {
        return Vb != null && IndexCount > 0;
    }
}
