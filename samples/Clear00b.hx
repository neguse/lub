import lub.Lub;
import lub.Gfx;
import lua.Lua;

class Clear00b {
  static var t: Float = 0;

  public static function main() {}

  public static function onInit() {
    var b: String = lua.Os.getenv("LUB_BACKEND");
    if (b == null) b = "sokol";
    Lub.config({ backend: b });
    Lua.print("backend = " + b);
    Lua.print("clear demo");
  }

  public static function onEvent(e: Dynamic) {}
  public static function onQuit() {}

  public static function onFrame() {
    t = t + 1.0 / 60.0;
    var r = 0.5 + 0.5 * Math.sin(t);
    var g = 0.5 + 0.5 * Math.sin(t + 2.0);
    var b = 0.5 + 0.5 * Math.sin(t + 4.0);
    Gfx.beginPass({
      target: Gfx.mainTex,
      clear_color: lua.Table.fromArray([r, g, b, 1.0])
    });
    Gfx.endPass();
  }
}
