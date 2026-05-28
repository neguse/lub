package game;

import lub.Lub;
import render.Gfx2d;

class Game {
  public static inline var W: Int = 640;
  public static inline var H: Int = 480;
  public static var frameCount: Int = 0;
  public static var gfx: Gfx2d = null;

  public static function init() {
    var backend: String = lua.Os.getenv("LUB_BACKEND");
    if (backend == null) backend = "sokol";
    Lub.config({ backend: backend, width: W, height: H });
  }

  public static function frame() {
    if (gfx == null) gfx = new Gfx2d();
    if (!gfx.ensure()) return;
    gfx.beginFrame();
    gfx.drawList.quad(gfx.white, 280, 200, 80, 80, { r: 1.0, g: 0.3, b: 0.2, a: 1.0 });
    gfx.endFrame();
    frameCount = frameCount + 1;
  }
}
