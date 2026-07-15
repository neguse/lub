// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Mesh3d.hx と対)。
using System.Collections.Generic;

/// <summary>
/// MeshData (Mesh.sdf_mesh / Io.load_gltf / Shapes3d) を GPU buffer にして
/// 保持するインスタンス。rebuild() は player 全体で単調増加する revision を
/// 使うので、呼び側で version を捏造する必要はない (hot reload や編集のたびに
/// rebuild() を呼べばよい)。bones を持つ MeshData は自動で skinned レイアウト
/// (Io.interleave_pncmw、stride 15)、それ以外は Io.interleave_pncm
/// (stride 11: pos.xyz + normal.xyz + albedo.rgb + mr.xy)。
/// この頂点レイアウトが Renderer3d の material 契約。
/// </summary>
public class Mesh3d
{
    private string key;

    public MeshData? data;
    public BufferRef? vb;
    public BufferRef? ib;
    public int indexCount = 0;
    public bool skinned = false;

    public Mesh3d(string key)
    {
        this.key = key;
    }

    /// <summary>メッシュを差し替える (初回含む)。呼ぶたびに GPU へ再アップロード。</summary>
    public void rebuild(MeshData data)
    {
        this.data = data;
        skinned = data.bones != null;
        int version = Gfx.next_version();
        var verts = skinned ? Io.interleave_pncmw(data) : Io.interleave_pncm(data);
        vb = Gfx.use_buffer(key + "_vb", Gfx.VERTEX, verts, version);
        // use_buffer は List<double> を取るので indices を詰め替える。
        // Lua 上は同じ整数値の array table になり、wire data は変わらない。
        var indices = new List<double>();
        foreach (var i in data.indices)
        {
            indices.Add(i);
        }
        ib = Gfx.use_buffer(key + "_ib", Gfx.INDEX, indices, version);
        indexCount = data.index_count;
    }

    /// <summary>rebuild 済みで描画可能か。</summary>
    public bool ready()
    {
        return vb != null && indexCount > 0;
    }
}
