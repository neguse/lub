package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;

class Bullet implements Entity {
  var x: Int; var y: Int;
  var timer: Int = 0;
  var hitW: Int = 6;
  public var dead: Bool = false;   // 命中で World が立てる
  static inline var SPEED = 8;
  static inline var H = 3;

  public function new(px: Int, py: Int) { x = px + 5; y = py; }

  public function update(world: World, input: InputSnapshot): Bool {
    timer++;
    if (timer == 10) {
      x -= 2;
      hitW = 5;
    }
    if (timer == 20) {
      x -= 3;
      hitW = 8;
    }
    y -= SPEED;
    return !dead && World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H });
  }
  public function bounds(): Rect return { x: x, y: y, w: hitW, h: H };
  public function draw(dl: DrawList): Void {
    var rect = (timer < 10) ? 11 : ((timer < 20) ? 10 : 9);
    dl.sprite(Game.jikiAtlas, Atlases.jiki[rect], Viewport.sx(x), Viewport.sy(y));
  }
}
