package render;

import lub.Gfx;
import lub.Io;

class Atlas {
  public var texture: Dynamic;
  public var w: Int;
  public var h: Int;
  public final key: String;
  final path: String;
  var version: Int = -1;

  public function new(key: String, path: String) {
    this.key = key;
    this.path = path;
    this.texture = null;
    this.w = 0;
    this.h = 0;
  }

  // 毎フレーム呼ぶ。PNG version が変わったら useTexture を再呼出 (hot reload 対応)。
  public function ensure(): Bool {
    var r = Io.loadPng(path);
    if (r.pixels == null) return false;
    this.w = r.width;
    this.h = r.height;
    this.texture = Gfx.useTexture(key, r.width, r.height, r.format, r.pixels, r.version,
      { filter: Gfx.NEAREST, wrap: Gfx.CLAMP });
    this.version = r.version;
    return true;
  }
}
