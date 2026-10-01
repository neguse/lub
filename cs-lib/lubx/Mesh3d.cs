// 実装ライブラリ lubx の Mesh3d。
using System.Collections.Generic;
using static Lub;

/// <summary>
/// MeshData (Mesh.sdf_mesh / Io.load_gltf / Shapes3d) を GPU buffer にして
/// 保持するインスタンス。rebuild() は version 省略の「変更宣言」で upload
/// するので、呼び側が version を管理する必要はない (hot reload や編集のたびに
/// rebuild() を呼べばよい)。bones を持つ MeshData は自動で skinned レイアウト
/// (Io.interleave_pncmw、20 float)、それ以外は Io.interleave_pncm
/// (16 float: pos pad nrm pad albedo pad mr pad)。
/// この頂点レイアウトが Renderer3d の material 契約。
/// buffer は ready() が呼ばれるたびに前回の version で再主張する (data は
/// 読まれない) ので、resource_sweep_after_frames を設定しても描き続ける
/// 限り sweep されない。Renderer3d.Draw が ready() を通るので、描画側が
/// 再主張を意識する必要はない。
/// </summary>
public class Mesh3d
{
    private string key;
    // rebuild() が組んだ interleaved 頂点と index。ready() の再主張と、sweep
    // 後の作り直しに使う。interleave の戻りは frame の終わりまでの view なので
    // table に写して持つ。
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
    }

    /// <summary>メッシュを差し替える (初回含む)。呼ぶたびに GPU へ再アップロード。</summary>
    public void Rebuild(MeshData data)
    {
        this.Data = data;
        Skinned = data.Bones != null;
        var view = Skinned ? Io.InterleavePncmw(data) : Io.InterleavePncm(data);
        Vb = Gfx.UseBuffer(key + "_vb", Gfx.BufferType.Storage, view);
        var v = new List<float>();
        for (int i = 0; i < view.Count; i++)
        {
            v.Add(view[i]);
        }
        // use_buffer は List<float> を取るので indices を詰め替える。
        // Lua 上は同じ整数値の array table になり、wire data は変わらない。
        var idx = new List<float>();
        foreach (var i in data.Indices)
        {
            idx.Add(i);
        }
        Ib = Gfx.UseBuffer(key + "_ib", Gfx.BufferType.Index, idx);
        verts = v;
        indices = idx;
        IndexCount = data.IndexCount;
    }

    /// <summary>rebuild 済みで描画可能か。描く frame に毎回呼ぶ (Renderer3d.Draw
    /// が呼ぶ)。buffer を前回の version で再主張して sweep を防ぐ。</summary>
    public bool Ready()
    {
        if (Vb == null || Ib == null || verts == null || indices == null
            || IndexCount <= 0)
        {
            return false;
        }
        Vb = Gfx.UseBuffer(key + "_vb", Gfx.BufferType.Storage, verts, Vb.Version);
        Ib = Gfx.UseBuffer(key + "_ib", Gfx.BufferType.Index, indices, Ib.Version);
        return Vb != null && Ib != null;
    }
}
