package game;

import lub.Lub;
import render.Gfx2d;
import render.Atlas;
import render.Font;
import input.InputSource;
import input.Input;
import input.MockInput;
import input.InputSnapshot;
import scenes.Scene;
import scenes.SceneTransition;
import scenes.Title;
import scenes.Play;
import scenes.GameOver;

class Game {
  public static inline var W: Int = 640;
  public static inline var H: Int = 480;
  public static var gfx: Gfx2d = null;
  public static var fontAtlas: Atlas = null;
  public static var jikiAtlas: Atlas = null;
  public static var cursorAtlas: Atlas = null;
  public static var enemyAtlas: Atlas = null;
  public static var font: Font = null;
  static var input: InputSource = null;
  static var scene: Scene = null;
  public static var frameCount: Int = 0;
  public static var score: Int = 0;
  public static var hiscore: Int = 0;

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
    if (enemyAtlas == null) enemyAtlas = new Atlas("ngs_enemy", "samples/ngs/data/enemy.png");
    if (!gfx.ensure() || !fontAtlas.ensure() || !jikiAtlas.ensure() || !cursorAtlas.ensure() || !enemyAtlas.ensure()) return false;
    if (font == null) font = new Font(fontAtlas);
    if (input == null) {
      var mock = lua.Os.getenv("LUB_NGS_MOCK");
      if (mock == "fire") {
        input = new MockInput(function(f) { var s = new InputSnapshot(); s.fire = true; return s; }); // 全 frame 射撃保持
      } else if (mock == "kill") {
        // 左へ 6 frame 寄り enemy#1 (spawn x280) の弾ライン上に陣取り射撃保持。
        // 連続弾の壁に降下してきた敵が即撃破され explosion が出る (golden 用)。
        input = new MockInput(function(f) { var s = new InputSnapshot(); s.fire = true; if (f < 6) s.dirX = -1; return s; });
      } else if (mock != null) {
        input = new MockInput();
      } else {
        input = new Input();
      }
    }
    if (scene == null) {
      var boot = lua.Os.getenv("LUB_NGS_BOOT");
      scene = switch (boot) {
        case "play":     new Play(false);
        case "boss":     new Play(false, true);   // boss 直入り (golden 用)
        case "gameover": new GameOver(12345);      // score inject 直入り (golden 用)
        default:         new Title();
      };
    }
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
