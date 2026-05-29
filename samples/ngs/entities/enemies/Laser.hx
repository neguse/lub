package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.Entity;
import entities.World;

// Wave が落とす直下レーザー (原典 type 4)。EnemyBullets faction (自弾では消えない)。
// 描画は hitbox 2×16 と一致する細ビーム sprite (enemy[7]/[8]) を交互。
class Laser implements Entity {
  var x: Int; var y: Int;
  var anim: Int = 0;
  final speed: Int;
  static inline var W = 2; static inline var H = 16;

  public function new(sx: Int, sy: Int, noGod: Bool) {
    x = sx; y = sy; speed = noGod ? 18 : 10;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    y += speed;
    anim = (anim + 1) & 1;
    return World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H });
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[7 + anim], Viewport.sx(x), Viewport.sy(y));
  }
}
