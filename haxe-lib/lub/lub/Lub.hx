package lub;

/**
	`Lub.config` のオプション。`onInit` の中でのみ呼べる。
**/
typedef ConfigOpts = {
	/**
		GPU backend。"directx12" (Windows のみ) / "vulkan" (Windows, Linux) /
		"sdlgpu"。未指定ならプラットフォーム既定 — Windows: directx12 /
		Linux: vulkan / web: webgpu。web (WASM) は webgpu のみで、指定は
		無視される。
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
