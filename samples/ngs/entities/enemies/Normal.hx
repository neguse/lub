package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.World;

class Normal implements Enemy {
  var x: Int; var y: Int;
  final originX: Int; final originY: Int;
  final theta: Float;       // 照準方向 (上=0, ラジアン)
  var counter: Int = 0;
  var anim: Int = 0;
  var hp: Int;
  public var dead: Bool = false;
  static inline var W = 16; static inline var H = 16;

  // spawn 時の自機 world 位置で照準を固定。
  public function new(sx: Int, sy: Int, playerCx: Float, playerCy: Float, noGod: Bool) {
    x = sx; y = sy; originX = sx; originY = sy;
    var dxw = playerCx - (sx + W / 2);
    var dyUp = -((playerCy) - (sy + H / 2));
    theta = Math.atan2(dxw, dyUp);   // 上=0 規約
    hp = noGod ? 2 : 1;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    // 原典: x = origin_x + sin(θ)*counter, y = origin_y - cos(θ)*counter (上=θ0)
    x = originX + Std.int(Math.round(Math.sin(theta) * counter));
    y = originY - Std.int(Math.round(Math.cos(theta) * counter));
    counter += 2;
    anim = (anim + 1) & 7;
    // viewport 外で除去 (bounds が playfield と重ならない)
    if (!World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H })) dead = true;
    return !dead;
  }

  public function onDamage(amount: Int): Bool {
    hp -= amount;
    if (hp < 1) { dead = true; return true; }
    return false;
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[0], Viewport.sx(x), Viewport.sy(y));
  }
}
