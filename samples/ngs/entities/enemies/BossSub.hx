package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.Entity;
import entities.World;

// ボス分身 (原典 type 8)。ボス phase2 が落とす。EnemyBullets faction (自弾で消えない)。
class BossSub implements Entity {
  var x: Int; var y: Int;
  final speed: Int;
  static inline var W = 26; static inline var H = 32;

  public function new(sx: Int, sy: Int, noGod: Bool) {
    x = sx; y = sy; speed = noGod ? 30 : 10;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    y += speed;
    return World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H });
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[12], Viewport.sx(x), Viewport.sy(y));
  }
}
