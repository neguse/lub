package lub;

extern class Sys {
	/** WASM (web) 上で動いているか。 **/
	@:native("is_web") public static function isWeb():Bool;

	/** 文字列の FNV-1a 64bit ハッシュ。コンテンツベースの version 生成用。 **/
	@:native("fnv1a64") public static function fnv1a64(s:String):Int;

	/** 実測 FPS。約 1 秒ごとに更新される平滑値 (フレーム dt ではない)。 **/
	@:native("actual_fps") public static function actualFps():Float;
}
