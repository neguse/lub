package lub;

extern class Sys {
	@:native("file_mtime") public static function fileMtime(path:String):Null<Float>;
	@:native("is_web") public static function isWeb():Bool;
	@:native("fnv1a64") public static function fnv1a64(s:String):Int;
	@:native("actual_fps") public static function actualFps():Float;
	@:native("load_gltf") public static function loadGltf(path:String):Dynamic;
}
