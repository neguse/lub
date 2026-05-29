package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import entities.enemies.Enemy;

typedef Slot = {
  var faction: Faction;
  var entity: Entity;
}

class World {
  final playerBullets: Array<Entity> = [];
  final enemySlots: Array<Slot> = [];
  static inline var MAX_PLAYER_BULLETS = 32;
  public var player: Player;
  public var bossDefeated: Bool = false;   // ボス撃破で Boss が立てる。Play が遷移判定に使う

  public function new(noGod: Bool) {
    player = new Player(noGod);
  }

  public function spawn(f: Faction, e: Entity): Void {
    if (f == Faction.PlayerBullets) {
      if (playerBullets.length < MAX_PLAYER_BULLETS) playerBullets.push(e);
      return;
    }
    enemySlots.push({ faction: f, entity: e });
  }

  // 原典順: player → 自機弾 → enemy slots。enemy update 中に spawn された slot も同 frame で更新される。
  public function tick(input: InputSnapshot): Void {
    player.update(this, input);
    var bi = 0;
    while (bi < playerBullets.length) {
      var b = playerBullets[bi];
      if (b.update(this, input)) bi++ else playerBullets.splice(bi, 1);
    }
    var ei = 0;
    while (ei < enemySlots.length) {
      var s = enemySlots[ei];
      if (s.entity.update(this, input)) ei++ else enemySlots.splice(ei, 1);
    }
  }

  public function drawAll(dl: DrawList): Void {
    // 原典順: enemy slots → player bullets → player。
    for (s in enemySlots) if (!slotDead(s)) s.entity.draw(dl);
    for (b in playerBullets) {
      var bullet: Bullet = cast b;
      if (!bullet.dead) b.draw(dl);
    }
    player.draw(dl);
  }

  public function resolveCollisions(): Void {
    for (s in enemySlots) {
      if (s.faction != Faction.Enemies) continue;
      var en: Enemy = cast s.entity;
      if (en.dead) continue;
      var checked = 0;
      for (b in playerBullets) {
        if (checked >= 16) break; // 原典は先頭 16 bullet slot のみ判定
        checked++;
        var bullet: Bullet = cast b;
        if (bullet.dead) continue;
        if (overlap(s.entity.bounds(), b.bounds())) {
          if (en.onDamage(this, 1)) bullet.dead = true;
          break;
        }
      }
    }
    if (player.alive && player.invincible == 0) {
      for (s in enemySlots) {
        if (s.faction == Faction.Effects || slotDead(s)) continue;
        if (overlap(player.bounds(), s.entity.bounds())) {
          player.hit();
          break;
        }
      }
    }
    cleanupDeadAfterCollision();
  }

  inline function slotDead(s: Slot): Bool {
    if (s.faction != Faction.Enemies) return false;
    var en: Enemy = cast s.entity;
    return en.dead;
  }

  function cleanupDeadAfterCollision(): Void {
    var bi = 0;
    while (bi < playerBullets.length) {
      var bullet: Bullet = cast playerBullets[bi];
      if (bullet.dead) playerBullets.splice(bi, 1) else bi++;
    }
    var ei = 0;
    while (ei < enemySlots.length) {
      if (slotDead(enemySlots[ei])) enemySlots.splice(ei, 1) else ei++;
    }
  }

  public static function overlap(a: Rect, b: Rect): Bool {
    return b.x < a.x + a.w && a.x < b.x + b.w && b.y < a.y + a.h && a.y < b.y + b.h;
  }
}
