package game;

import lub.Lub;
import render.Gfx2d;
import render.Atlas;
import render.Font;
import assets.Atlases;

class Game {
  public static inline var W: Int = 640;
  public static inline var H: Int = 480;
  public static var frameCount: Int = 0;
  public static var gfx: Gfx2d = null;
  public static var fontAtlas: Atlas = null;
  public static var jikiAtlas: Atlas = null;
  public static var font: Font = null;

  public static function init() {
    var backend: String = lua.Os.getenv("LUB_BACKEND");
    if (backend == null) backend = "sokol";
    Lub.config({ backend: backend, width: W, height: H });
  }

  public static function frame() {
    if (gfx == null) gfx = new Gfx2d();
    if (fontAtlas == null) fontAtlas = new Atlas("ngs_font", "samples/ngs/data/font.png");
    if (jikiAtlas == null) jikiAtlas = new Atlas("ngs_jiki", "samples/ngs/data/jiki.png");
    if (!gfx.ensure() || !fontAtlas.ensure() || !jikiAtlas.ensure()) return;
    if (font == null) font = new Font(fontAtlas);

    gfx.beginFrame();
    font.drawString(gfx.drawList, 100, 100, "no good shooting game");
    gfx.drawList.sprite(jikiAtlas, Atlases.jiki[0], 312, 400);
    gfx.endFrame();
    frameCount = frameCount + 1;
  }
}
