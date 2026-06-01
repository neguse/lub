package lub;

extern class Sys {
	@:native("file_mtime") public static function fileMtime(path:String):Null<Float>;
	@:native("fnv1a64") public static function fnv1a64(s:String):Int;
	@:native("load_png") public static function loadPng(path:String):Dynamic;
	@:native("load_gltf") public static function loadGltf(path:String):Dynamic;
}
