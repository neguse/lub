package lub;

extern class Lub {
  @:native("config") public static function config(opts: Dynamic): Void;
  @:native("quit")   public static function quit(): Void;
}
