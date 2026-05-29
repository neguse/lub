package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.World;
import entities.Faction;
import entities.Explosion;

// 横詰め→降下しレーザーを落とす敵 (原典 type 3)。HP 3。
// 原典フィールド対応: origin_x=phase(0横/1降下), origin_y=cooldown,
//   anim=降下速度(被弾で-3), counter=HP, angle=描画toggle。
class Wave implements Enemy {
  var x: Int; var y: Int;
  var descending: Bool = false;   // origin_x: false=横移動, true=降下
  var cooldown: Int = 0;          // origin_y: レーザー再射出までの間隔
  var vspeed: Int = 5;            // anim: 降下速度。被弾で -3 (反動)
  var animToggle: Int = 0;        // angle: 描画 anim
  var hp: Int = 3;                // counter
  final noGod: Bool;
  public var dead: Bool = false;
  static inline var W = 16; static inline var H = 16;
  static inline var HSPEED = 1;
  static inline var PW = 16;      // 自機幅 (centering 判定用)

  public function new(sx: Int, sy: Int, noGod: Bool) {
    x = sx; y = sy; this.noGod = noGod;
  }

  public function update(world: World, input: InputSnapshot): Bool {
    if (dead) return false;   // 撃破済みは行動しない (墓場 Laser の防止)
    animToggle = (animToggle == 0) ? 1 : 0;
    var px = world.player.x;
    if (!descending) {
      // phase1: 自機 x へ横詰め
      if (x < px) x += HSPEED else x -= HSPEED;
      if (centeredOnPlayer(px)) descending = true;   // 自機 x に重なったら降下へ
    } else {
      // phase2: 降下 (被弾後 vspeed<0 で上昇 = 原典の反動)
      y += vspeed;
      // cooldown==0 のまま未センタリング時は何もせず待機 = 自機 x に (再)整列した frame に即発射
      if (cooldown == 0) {
        if (centeredOnPlayer(px)) {
          world.spawn(Faction.EnemyBullets, new Laser(x + 8, y, noGod));
          cooldown = 0x0f;   // 15 frame の再射出間隔
        }
      } else {
        cooldown--;
      }
    }
    if (!World.overlap(bounds(), { x: Viewport.X, y: Viewport.Y, w: Viewport.W, h: Viewport.H })) dead = true;
    return !dead;
  }

  // 自機 x の左端 px が enemy 列 (±W) に重なっているか。W==PW なので ±W の対称窓。
  inline function centeredOnPlayer(px: Int): Bool
    return x - W < px && px + PW < x + W * 2;

  public function onDamage(world: World, amount: Int): Bool {
    hp -= amount;
    // 原典 anim=0xfffd: 被弾で上へ反動。以後 +5 へは戻らないので、撃ち残した Wave は
    // 上へ流れて画面外 dead となり、+120 kill bonus / explosion は出ない (原典どおりの quirk)。
    vspeed = -3;
    Game.score += 10;
    if (hp < 1) {
      dead = true;
      Game.score += 120;
      world.spawn(Faction.Effects, new Explosion(x + 3, y + 3));
      return true;
    }
    return false;
  }

  public function bounds(): Rect return { x: x, y: y, w: W, h: H };

  public function draw(dl: DrawList): Void {
    dl.sprite(Game.enemyAtlas, Atlases.enemy[2], Viewport.sx(x), Viewport.sy(y));
  }
}
