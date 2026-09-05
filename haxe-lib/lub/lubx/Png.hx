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

/**
	PNG の読み書き。`load` は `Io.load*` と同じ status/version 規約
	(web では "pending" があり得る。`bytes` が null の間は待つ)。`bytes` は
	frame 有効の view なので毎フレーム `load` し直す (cache は runtime が持つ)。
**/
@:luaRequire("lubx_png")
extern class Png {
	@:native("load") public static function load(path:String):PngResult;
	@:native("write") public static function write(path:String, bytes:Bytes, width:Int, height:Int, ?stride:Int):Bool;
}
