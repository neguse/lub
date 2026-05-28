package game;

import lub.Lub;
import render.Gfx2d;
import render.Atlas;
import render.Font;
import input.InputSource;
import input.Input;
import input.MockInput;
import scenes.Scene;
import scenes.SceneTransition;

class Game {
  public static inline var W: Int = 640;
  public static inline var H: Int = 480;
  public static var gfx: Gfx2d = null;
  public static var fontAtlas: Atlas = null;
  public static var jikiAtlas: Atlas = null;
  public static var cursorAtlas: Atlas = null;
  public static var font: Font = null;
  static var input: InputSource = null;
  static var scene: Scene = null;
  public static var frameCount: Int = 0;

  public static function init() {
    var backend: String = lua.Os.getenv("LUB_BACKEND");
    if (backend == null) backend = "sokol";
    Lub.config({ backend: backend, width: W, height: H });
  }

  static function boot(): Bool {
    if (gfx == null) gfx = new Gfx2d();
    if (fontAtlas == null) fontAtlas = new Atlas("ngs_font", "samples/ngs/data/font.png");
    if (jikiAtlas == null) jikiAtlas = new Atlas("ngs_jiki", "samples/ngs/data/jiki.png");
    if (cursorAtlas == null) cursorAtlas = new Atlas("ngs_cursor", "samples/ngs/data/cursor.png");
    if (!gfx.ensure() || !fontAtlas.ensure() || !jikiAtlas.ensure() || !cursorAtlas.ensure()) return false;
    if (font == null) font = new Font(fontAtlas);
    if (input == null) {
      input = (lua.Os.getenv("LUB_NGS_MOCK") != null) ? new MockInput() : new Input();
    }
    if (scene == null) scene = new StubScene();
    return true;
  }

  public static function frame() {
    if (!boot()) return;
    input.refresh();
    scene.update(input.current);
    gfx.beginFrame();
    scene.draw(gfx.drawList);
    gfx.endFrame();
    switch (scene.transition()) {
      case Stay:
      case Switch(s): scene = s;
      case Quit: Lub.quit();
    }
    frameCount = frameCount + 1;
  }
}
