package lub;

/**
	`Mesh.surfaceNets` が返すメッシュ。`load_gltf` と同じ規約なので
	`Io.interleavePn` / `Gfx.useBuffer` にそのまま渡せる。
	`indices` の値は 0-based。
**/
typedef MeshData = {
	var positions:lua.Table<Int, Float>;
	var normals:lua.Table<Int, Float>;
	var indices:lua.Table<Int, Int>;
	var vert_count:Int;
	var index_count:Int;
}

/** CPU メッシュ生成。 **/
extern class Mesh {
	/**
		SDF grid から naive surface nets で三角形メッシュを作る。

		`grid` は `nx*ny*nz` 個の符号付き距離の 1-indexed flat table
		(x が最速で回る)。`grid[1 + x + y*nx + z*nx*ny]` が world 座標
		`(ox + x*cell, oy + y*cell, oz + z*cell)` のサンプルで、負が内側。
		`cell` 省略時 1.0、`ox/oy/oz` 省略時 0。

		法線は grid 勾配 (中心差分 + trilinear) から作るので、粗い grid でも
		滑らかにシェーディングされる。三角形は外から見て CCW。
	**/
	@:native("surface_nets")
	public static function surfaceNets(grid:lua.Table<Int, Float>, nx:Int, ny:Int, nz:Int, ?cell:Float, ?ox:Float, ?oy:Float, ?oz:Float):MeshData;
}
