package render;

import lub.Gfx;
import lub.Io;

class Gfx2d {
  public var drawList: DrawList;
  public var white: Atlas;     // quad 用 1x1 白
  var shader: Dynamic;
  var ready: Bool = false;

  public function new() {}

  // 毎フレーム冒頭で呼ぶ。shader/white を確保 (idempotent)。失敗時 false。
  public function ensure(): Bool {
    var vs = Io.loadText("samples/ngs/data/sprite.vs.slang");
    var fs = Io.loadText("samples/ngs/data/sprite.fs.slang");
    if (vs.text == null || fs.text == null) return false;
    shader = Gfx.useShader("ngs_sprite", vs.text, fs.text, vs.version ^ fs.version);
    if (white == null) {
      white = new Atlas("ngs_white", "");   // path 空: ensure() は呼ばず手動確保
      white.w = 1; white.h = 1;
    }
    // 1x1 白を毎フレーム同 version で確保 (cache hit)。
    white.texture = Gfx.useTexture("ngs_white", 1, 1, Gfx.RGBA8,
      lua.Table.fromArray([255, 255, 255, 255]), 1, { filter: Gfx.NEAREST, wrap: Gfx.CLAMP });
    if (drawList == null) drawList = new DrawList(shader);
    else drawList.shader = shader;
    ready = true;
    return true;
  }

  public function beginFrame() {
    Gfx.beginPass({ target: Gfx.mainTex, clear_color: lua.Table.fromArray([0.0, 0.0, 0.0, 1.0]) });
    drawList.begin();
  }

  public function endFrame() {
    drawList.flush();
    Gfx.endPass();
  }
}
