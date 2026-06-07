package lubx;

import lub.Bytes;

@:multiReturn extern class PngResult {
	var bytes:Bytes;
	var width:Int;
	var height:Int;
	var format:Int;
	var stride:Int;
	var version:Int;
	var status:String;
	var error:String;
}

@:luaRequire("lubx_png")
extern class Png {
	@:native("load") public static function load(path:String):PngResult;
	@:native("write") public static function write(path:String, bytes:Bytes, width:Int, height:Int, ?stride:Int):Bool;
}
