package lubx;

import lub.Gfx;
import lub.Io;
import lub.Mesh.MeshData;

/**
	`MeshData`(`Sdf.mesh` / `Io.loadGltf` / `Shapes3d`)を GPU buffer にして
	保持するインスタンス。`rebuild()` は version 省略の「変更宣言」で upload
	するので、呼び側が version を管理する必要はない(hot reload や SdfPanel
	編集のたびに `rebuild()` を呼べばよい)。

	`bones` を持つ MeshData は自動で skinned レイアウト
	(`Io.interleavePncmw`、stride 15)になり、それ以外は
	`Io.interleavePncm`(stride 11: pos.xyz + normal.xyz + albedo.rgb + mr.xy)。
	この頂点レイアウトが `Renderer3d` の material 契約。
**/
class Mesh3d {
	final key:String;

	public var data(default, null):MeshData;
	public var vb(default, null):Dynamic;
	public var ib(default, null):Dynamic;
	public var indexCount(default, null):Int = 0;
	public var skinned(default, null):Bool = false;

	public function new(key:String) {
		this.key = key;
	}

	/** メッシュを差し替える(初回含む)。呼ぶたびに GPU へ再アップロード。 **/
	public function rebuild(data:MeshData):Void {
		this.data = data;
		skinned = data.bones != null;
		var verts:Dynamic = skinned ? Io.interleavePncmw(data) : Io.interleavePncm(data);
		vb = Gfx.useBuffer(key + "_vb", Gfx.VERTEX, verts);
		ib = Gfx.useBuffer(key + "_ib", Gfx.INDEX, data.indices);
		indexCount = data.index_count;
	}

	/** rebuild 済みで描画可能か。 **/
	public inline function ready():Bool {
		return vb != null && indexCount > 0;
	}
}
