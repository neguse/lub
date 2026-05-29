package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.Entity;
import entities.World;

// 自機狙い弾 (原典 type 2)。EnemyBullets faction: 自弾では消えず、自機に当たる。
// 描画は hitbox 6×7 と一致する小弾 sprite (enemy[5]/[6]) を anim で交互。
class Aimed implements Entity {
  var x: Int; var y: Int;
  final originX: Int; final originY: Int;
  final theta: Float;       // 照準方向 (上=0, rad) + spread offset
  var dist: Float = 0;
  var anim: Int = 0;
  final speed: Int;
  static inline var W = 6; static inline var H = 7;

  // spawn 位置 (sx,sy) の中心から自機中心 (pcx,pcy) へ照準し offset(rad) を足す。
  public function new(sx: Int, sy: Int, pcx: Float, pcy: Float, offset: Float, noGod: Bool) {
    x = sx; y = sy; originX = sx; originY = sy;
    var dxw = pcx - (sx + W / 2);
    var dyUp = -(pcy - (sy + H / 2));
    theta = Math.atan2(dxw, dyUp) + offset;   // 上=0 規約
    speed = noGod ? 8 : 4;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    dist += speed;
    x = originX + Std.int(Math.round(Math.sin(theta) * dist));
    y = originY - Std.int(Math.round(Math.cos(theta) * dist));
    anim = (anim + 1) & 7;
    return World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H });
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[5 + (anim & 1)], Viewport.sx(x), Viewport.sy(y));
  }
}
