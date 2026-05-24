import lub.Gfx;

class Buffer00c {
  static var data = lua.Table.fromArray([
    0.0, 0.5, 0.0,
    -0.5, -0.5, 0.0,
    0.5, -0.5, 0.0
  ]);

  public static function main() {}

  public static function onInit() {}

  public static function onEvent(e: Dynamic) {}
  public static function onQuit() {}

  public static function onFrame() {
    var b: Dynamic = Gfx.useBuffer("tri", Gfx.VERTEX, data, 1);
    if (b != null && b.__lub_kind == "buffer") {
      // buffer registered
    }
    Gfx.beginPass({
      target: Gfx.mainTex,
      clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0])
    });
    Gfx.endPass();
  }
}
