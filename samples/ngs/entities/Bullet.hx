package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;

class Bullet implements Entity {
  var x: Int; var y: Int;
  public var dead: Bool = false;   // 命中で World が立てる
  static inline var SPEED = 8;
  static inline var W = 6; static inline var H = 3;

  public function new(px: Int, py: Int) { x = px + 5; y = py; }

  public function update(world: World, input: InputSnapshot): Bool {
    y -= SPEED;
    return !dead && (y + H > Viewport.Y);   // 命中 or 上に抜けたら除去
  }
  public function bounds(): Rect return { x: x, y: y, w: W, h: H };
  public function draw(dl: DrawList): Void {
    dl.sprite(Game.jikiAtlas, Atlases.jiki[11], Viewport.sx(x), Viewport.sy(y)); // 小弾 rect 11
  }
}
