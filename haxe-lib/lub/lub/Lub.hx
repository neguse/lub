package lub;

/**
	`Lub.config` のオプション。`onInit` の中でのみ呼べる。
**/
typedef ConfigOpts = {
	/**
		GPU backend。native では "sokol" (既定) か "sdlgpu"。
		web (WASM) では "webgpu" のみで、"sokol" と省略は "webgpu" に
		読み替えられる。
	**/
	?backend:String,
	/** ウィンドウ幅 (px)。`height` とセットで指定する。 **/
	?width:Int,
	/** ウィンドウ高さ (px)。`width` とセットで指定する。 **/
	?height:Int,
	/** `use*` されなくなったリソースを何フレーム後に破棄するか。 **/
	?resource_sweep_after_frames:Int,
	/** readback リングの深さ (1..)。 **/
	?readback_depth:Int,
}

extern class Lub {
	/** ランタイム設定。`onInit` 内でのみ有効。 **/
	@:native("config") public static function config(opts:ConfigOpts):Void;

	/** アプリ終了を要求する。 **/
	@:native("quit") public static function quit():Void;
}
