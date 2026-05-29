package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;

// 撃破時の死亡演出。Effects faction (当たり判定対象外)。
// 原典 explosion_draw は `7+frame` で boss/laser sprite を指す復元バグなので、
// hitbox 寸法と無関係な cosmetic として火花 sprite (enemy[18..21]) を 4 frame で回す。
// 寿命は原典 (origin_x が 0x1f で消滅) どおり 31 frame。
class Explosion implements Entity {
  var x: Int; var y: Int;
  var timer: Int = 0;
  static inline var LIFE = 31;
  static inline var BASE = 18;   // enemy[18..21] = 火花 4 frame

  public function new(sx: Int, sy: Int) { x = sx; y = sy; }

  public function update(world: World, input: InputSnapshot): Bool {
    timer++;
    return timer < LIFE;
  }

  public function bounds(): Rect return { x: x, y: y, w: 0, h: 0 };

  public function draw(dl: DrawList): Void {
    var frame = timer >> 3;        // 0..3 (8 frame ごと)
    if (frame > 3) frame = 3;
    dl.sprite(Game.enemyAtlas, Atlases.enemy[BASE + frame], Viewport.sx(x), Viewport.sy(y));
  }
}
