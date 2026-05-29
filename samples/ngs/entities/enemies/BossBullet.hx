package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.Entity;
import entities.World;

// ボス弾 (原典 type 7)。EnemyBullets faction。spawn 位置から自機中心へ照準 + spread。
// noGod 時は遅い (2) が密 (毎 frame 3 発)、通常は速い (10) が疎 (隔 frame 1 発)。
class BossBullet implements Entity {
  var x: Int; var y: Int;
  final originX: Int; final originY: Int;
  final theta: Float;
  var dist: Float = 0;
  final speed: Int;
  static inline var W = 3; static inline var H = 3;

  public function new(sx: Int, sy: Int, pcx: Float, pcy: Float, offset: Float, noGod: Bool) {
    x = sx; y = sy; originX = sx; originY = sy;
    var dxw = pcx - (sx + W / 2);
    var dyUp = -(pcy - (sy + H / 2));
    theta = Math.atan2(dxw, dyUp) + offset;
    speed = noGod ? 2 : 10;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    x = originX + Std.int(Math.round(Math.sin(theta) * dist));
    y = originY - Std.int(Math.round(Math.cos(theta) * dist));
    dist += speed;
    return World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H });
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[6], Viewport.sx(x), Viewport.sy(y));
  }
}
