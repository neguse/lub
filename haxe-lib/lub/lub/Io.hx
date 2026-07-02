package lub;

/**
	`Io.load*` の共通戻り値パターン (Lua multi-return)。

	- `status`: "ready" / "pending" / "error"。web では取得が非同期なので
	  "pending" が返り得る。native は "ready" か "error"。
	- 本体 (`text` / `data` / `mesh`) は ready になるまで null。
	  呼び出し側は null の間そのフレームの処理をスキップするのが定型。
	- `version`: 内容の FNV-1a ハッシュ。`Gfx.use*` の version にそのまま
	  渡せる。複数ファイルを 1 リソースに束ねるときは
	  `a.version * 31 + b.version` のような順序依存の結合を使う
	  (XOR は同一内容や入れ替えで打ち消し合う)。
**/
@:multiReturn extern class IoTextResult {
	var text:String;
	var version:Int;
	var status:String;
	var error:String;
}

@:multiReturn extern class IoFloatsResult {
	var data:Dynamic;
	var version:Int;
	var status:String;
	var error:String;
}

@:multiReturn extern class IoGltfResult {
	var mesh:Dynamic;
	var version:Int;
	var status:String;
	var error:String;
}

/**
	ファイル入力。mtime の fast-path + コンテンツハッシュで毎フレーム
	呼んでも安い (hot reload 前提の即時モード API)。
**/
@:luaRequire("lub_io")
extern class Io {
	/** テキストファイルを読む (シェーダソースなど)。 **/
	@:native("load_text") public static function loadText(path:String):IoTextResult;

	/** `return { ... }` 形式の Lua ファイルを Float 配列として読む。 **/
	@:native("load_floats") public static function loadFloats(path:String):IoFloatsResult;

	/** glTF (.gltf / .glb) を読む。結果の mesh は interleave 系に渡す。 **/
	@:native("load_gltf") public static function loadGltf(path:String):IoGltfResult;

	/** mesh を position + normal で interleave した頂点列にする。 **/
	@:native("interleave_pn") public static function interleavePn(mesh:Dynamic):lua.Table<Int, Float>;

	/** position + normal + uv。 **/
	@:native("interleave_pnu") public static function interleavePnu(mesh:Dynamic):lua.Table<Int, Float>;

	/** position + normal + uv + tangent。 **/
	@:native("interleave_pnut") public static function interleavePnut(mesh:Dynamic):lua.Table<Int, Float>;
}
