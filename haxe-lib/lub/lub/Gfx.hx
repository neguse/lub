package lub;

/** `Gfx.useShader` / `useShaderCompute` が返す不透明ハンドル。 **/
abstract ShaderRef(Dynamic) from Dynamic to Dynamic {}

/** `Gfx.useBuffer` が返す不透明ハンドル。 **/
abstract BufferRef(Dynamic) from Dynamic to Dynamic {}

/** `Gfx.useTexture` が返す不透明ハンドル。`Gfx.mainTex` も同型。 **/
abstract TextureRef(Dynamic) from Dynamic to Dynamic {}

/**
	`Gfx.beginPass` のオプション。フィールド名は Lua 側の wire format
	(snake_case) をそのまま公開している。

	描画先は 3 通り:
	- `target`: 単一の color target (`Gfx.mainTex` か `{target: true}` で
	  作ったテクスチャ)。
	- `targets`: MRT。全て同サイズの offscreen color texture であること。
	  クリア色は `clear_colors` で個別指定 (省略分は {0,0,0,1})。
	- どちらも省略: depth-only パス (`depth_target` 必須)。

	`depth_target` は offscreen パス専用。`Gfx.mainTex` はスワップチェーンの
	depth buffer を暗黙に使う。
**/
typedef PassOpts = {
	?target:TextureRef,
	?targets:lua.Table<Int, TextureRef>,
	?depth_target:TextureRef,
	/** クリア色 [r, g, b, a]。省略時 {0, 0, 0, 1}。 **/
	?clear_color:lua.Table<Int, Float>,
	/** MRT 用。targets[i] に対応するクリア色の配列。 **/
	?clear_colors:lua.Table<Int, lua.Table<Int, Float>>,
	/** 省略時 1.0。 **/
	?clear_depth:Float,
	/** `Gfx.CLEAR`(省略時)/ `Gfx.LOAD`。LOAD は全アタッチメント (color + depth)
		の直前の内容を保持したまま描き足す。同一フレーム内で先行パスが同じ
		ターゲットに描いていることが前提 (フレーム最初のパスで使うと内容は不定)。 **/
	?load:Int,
}

/**
	`Gfx.draw` のオプション。`shader` 以外は省略可能で、既定値は
	blend=NONE / cull=BACK / primitive=TRIANGLES / depth=true /
	depth_write=true / instance_count=1。
**/
typedef DrawOpts = {
	shader:ShaderRef,
	/** `Gfx.NONE` / `ALPHA` / `ADDITIVE` / `MULTIPLY`。 **/
	?blend:Int,
	/** `Gfx.NONE` / `BACK` / `FRONT`。 **/
	?cull:Int,
	/** `Gfx.TRIANGLES` / `TRIANGLE_STRIP` / `LINES` / `LINE_STRIP` / `POINTS`。 **/
	?primitive:Int,
	/** depth test の有効/無効。 **/
	?depth:Bool,
	?depth_write:Bool,
	/** 0 以下を渡すと draw 自体がスキップされる。 **/
	?instance_count:Int,
}

/** `Gfx.dispatch` のオプション。 **/
typedef DispatchOpts = {
	shader:ShaderRef,
}

/**
	`Gfx.useTexture` のオプション。
	`target` / `storage` を立てたテクスチャはピクセルデータ (`px`) を
	渡せない (ランタイムがエラーにする)。
**/
typedef TextureOpts = {
	/** `Gfx.LINEAR` / `NEAREST`。省略時 LINEAR。 **/
	?filter:Int,
	/** `Gfx.REPEAT` / `CLAMP`。省略時 CLAMP。 **/
	?wrap:Int,
	/** render target として使う。 **/
	?target:Bool,
	/** compute の storage image として使う。 **/
	?storage:Bool,
}

@:multiReturn extern class GfxSize {
	var w:Int;
	var h:Int;
}

@:multiReturn extern class ReadTextureResult {
	var status:String;
	var bytes:Bytes;
	var width:Int;
	var height:Int;
	var format:Int;
	var stride:Int;
	var id:Dynamic;
	var dropped:Dynamic;
	var error:String;
}

/**
	render target の GPU → CPU 読み戻し。`Gfx.readback` で handle を作り、
	`id` 付きで request してから次 call 以降で結果を drain する:

	```haxe
	var rb = Gfx.readback();
	var r = rb.readTexture(tex, frame == 30 ? 30 : null);
	if (r.status == "ready" && r.id == 30) {
		lubx.Png.write(path, r.bytes, r.width, r.height, r.stride);
	}
	```

	queue depth は既定 8。必要なら `Lub.config({readback_depth: N})` (1..32)。
**/
extern class Readback {
	@:native("read_texture") public function readTexture(tex:TextureRef, ?id:Dynamic):ReadTextureResult;
}

/**
	即時モードの GPU API。リソースは `use*` 系を毎フレーム同じ `key` で
	呼んで宣言する。`version` が前フレームと同じならキャッシュが返り、
	変わっていれば作り直される。

	`draw` / `dispatch` の bindings はシェーダ依存の自由なテーブルなので
	型付けしない。予約名だけ決まっている:
	- `indices`: INDEX バッファ (indexed draw になる)
	- `instances`: インスタンス用 VERTEX/STORAGE バッファ
	- `uniforms`: `{ メンバ名 = {Float...} }` の uniform 値テーブル
	- それ以外のバッファ値: 頂点バッファ
	- それ以外のテクスチャ値: キー名でシェーダのテクスチャに束縛
**/
extern class Gfx {
	// pass
	@:native("begin_pass") public static function beginPass(opts:PassOpts):Void;
	@:native("end_pass") public static function endPass():Void;
	// resources
	@:native("use_shader") public static function useShader(key:String, vs:String, fs:String, version:Int):ShaderRef;
	@:native("use_shader_compute") public static function useShaderCompute(key:String, src:String, version:Int):ShaderRef;
	// `data` is `lua.Table<Int, Float>` for VERTEX/INDEX/STORAGE-with-data,
	// or an `Int` float-count for STORAGE-allocate-empty (compute output buffers).
	@:native("use_buffer") public static function useBuffer(key:String, type:Int, data:Dynamic, version:Int):BufferRef;
	@:native("use_texture") public static function useTexture(key:String, w:Int, h:Int, fmt:Int, px:Dynamic, version:Int, ?opts:TextureOpts):TextureRef;

	/**
		手続き生成データを変更した時点で呼ぶ、player 内で単調増加する revision。
		entry の hot reload を跨いで値を保つ。毎フレームではなく、同じ key の
		内容を実際に変更するときだけ呼び、その `use*` の version に渡す。
	**/
	@:native("next_version") public static function nextVersion():Int;

	@:native("readback") public static function readback():Readback;
	// commands
	@:native("draw") public static function draw(count:Int, bindings:Dynamic, opts:DrawOpts):Void;
	@:native("dispatch") public static function dispatch(x:Int, y:Int, z:Int, bindings:Dynamic, opts:DispatchOpts):Void;
	// current drawable size in pixels (swapchain / canvas) -> w, h
	@:native("size") public static function size():GfxSize;

	// globals
	@:native("main_tex") public static var mainTex(default, null):TextureRef;

	// buffer type
	@:native("VERTEX") public static var VERTEX(default, null):Int;
	@:native("INDEX") public static var INDEX(default, null):Int;
	@:native("UNIFORM") public static var UNIFORM(default, null):Int;
	@:native("STORAGE") public static var STORAGE(default, null):Int;
	// pixel format
	@:native("RGBA8") public static var RGBA8(default, null):Int;
	@:native("R8") public static var R8(default, null):Int;
	@:native("RG8") public static var RG8(default, null):Int;
	@:native("R16F") public static var R16F(default, null):Int;
	@:native("RG16F") public static var RG16F(default, null):Int;
	@:native("R32F") public static var R32F(default, null):Int;
	@:native("RGBA16F") public static var RGBA16F(default, null):Int;
	@:native("RGBA32F") public static var RGBA32F(default, null):Int;
	@:native("DEPTH16") public static var DEPTH16(default, null):Int;
	@:native("DEPTH24_STENCIL8") public static var DEPTH24_STENCIL8(default, null):Int;
	@:native("DEPTH32F") public static var DEPTH32F(default, null):Int;
	// load / store
	@:native("CLEAR") public static var CLEAR(default, null):Int;
	@:native("LOAD") public static var LOAD(default, null):Int;
	@:native("DONTCARE") public static var DONTCARE(default, null):Int;
	@:native("STORE") public static var STORE(default, null):Int;
	// blend / cull
	@:native("NONE") public static var NONE(default, null):Int;
	@:native("ALPHA") public static var ALPHA(default, null):Int;
	@:native("ADDITIVE") public static var ADDITIVE(default, null):Int;
	@:native("MULTIPLY") public static var MULTIPLY(default, null):Int;
	@:native("BACK") public static var BACK(default, null):Int;
	@:native("FRONT") public static var FRONT(default, null):Int;
	// primitive
	@:native("TRIANGLES") public static var TRIANGLES(default, null):Int;
	@:native("TRIANGLE_STRIP") public static var TRIANGLE_STRIP(default, null):Int;
	@:native("LINES") public static var LINES(default, null):Int;
	@:native("LINE_STRIP") public static var LINE_STRIP(default, null):Int;
	@:native("POINTS") public static var POINTS(default, null):Int;
	// sampler
	@:native("LINEAR") public static var LINEAR(default, null):Int;
	@:native("NEAREST") public static var NEAREST(default, null):Int;
	@:native("REPEAT") public static var REPEAT(default, null):Int;
	@:native("CLAMP") public static var CLAMP(default, null):Int;
}
