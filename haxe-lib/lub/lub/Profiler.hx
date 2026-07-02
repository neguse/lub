package lub;

/**
	汎用 CPU profiler (docs/profile.md)。`LUB_PROFILE=1` 環境変数で有効化。
	`beginScope` / `endScope` は同じ `name` を対で呼ぶこと。
**/
extern class Profiler {
	/** profiler が有効か (`LUB_PROFILE=1`)。 **/
	public static function enabled():Bool;

	@:native("begin_scope") public static function beginScope(name:String):Void;
	@:native("end_scope") public static function endScope(name:String):Void;

	/** 集計をリセットする。 **/
	public static function reset():Void;

	/** `label` 付きで集計をログ出力する。 **/
	public static function report(label:String):Void;
}
