package lub;

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

@:multiReturn extern class IoPngResult {
	var pixels:Dynamic;
	var width:Int;
	var height:Int;
	var format:Int;
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

@:luaRequire("lub_io")
extern class Io {
	@:native("load_text") public static function loadText(path:String):IoTextResult;
	@:native("load_floats") public static function loadFloats(path:String):IoFloatsResult;
	@:native("load_png") public static function loadPng(path:String):IoPngResult;
	@:native("load_gltf") public static function loadGltf(path:String):IoGltfResult;
	@:native("interleave_pn") public static function interleavePn(mesh:Dynamic):lua.Table<Int, Float>;
	@:native("interleave_pnu") public static function interleavePnu(mesh:Dynamic):lua.Table<Int, Float>;
}
