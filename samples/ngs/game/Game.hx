package game;

import lub.Lub;
import lub.Gfx;

class Game {
  public static inline var W: Int = 640;
  public static inline var H: Int = 480;
  public static var frameCount: Int = 0;

  public static function init() {
    var backend: String = lua.Os.getenv("LUB_BACKEND");
    if (backend == null) backend = "sokol";
    Lub.config({ backend: backend, width: W, height: H });
  }

  public static function frame() {
    Gfx.beginPass({
      target: Gfx.mainTex,
      clear_color: lua.Table.fromArray([0.0, 0.0, 0.0, 1.0])
    });
    Gfx.endPass();
    frameCount = frameCount + 1;
  }
}
