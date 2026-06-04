package lub;

extern class Profiler {
	public static function enabled():Bool;
	@:native("begin_scope") public static function beginScope(name:String):Void;
	@:native("end_scope") public static function endScope(name:String):Void;
	public static function reset():Void;
	public static function report(label:String):Void;
}
