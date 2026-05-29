package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import entities.enemies.Enemy;

class World {
  final lists: Map<Faction, Array<Entity>> = new Map();
  // 決定的な反復順のため faction の固定順を持つ
  static final ORDER: Array<Faction> = [Faction.EnemyBullets, Faction.Enemies, Faction.Effects, Faction.PlayerBullets];
  public var player: Player;
  public var bossDefeated: Bool = false;   // ボス撃破で Boss が立てる。Play が遷移判定に使う

  public function new(noGod: Bool) {
    for (f in ORDER) lists.set(f, []);
    player = new Player(noGod);
  }

  public inline function spawn(f: Faction, e: Entity): Void {
    lists.get(f).push(e);
  }

  // 全 entity を update し、false を返したものを除去。
  public function tick(input: InputSnapshot): Void {
    player.update(this, input);
    for (f in ORDER) {
      var arr = lists.get(f);
      var w = 0;
      for (r in 0...arr.length) {
        var e = arr[r];
        if (e.update(this, input)) { arr[w] = e; w++; }
      }
      arr.resize(w);
    }
  }

  public function drawAll(dl: DrawList): Void {
    // 描画順: 敵弾 → 敵 → effects → 自弾 (ORDER と同順)
    for (f in ORDER) for (e in lists.get(f)) e.draw(dl);
    // 自機は最前面
    player.draw(dl);
  }

  public function resolveCollisions(): Void {
    var enemies = lists.get(Faction.Enemies);
    var pbs = lists.get(Faction.PlayerBullets);
    for (b in pbs) {
      var bullet: Bullet = cast b;
      if (bullet.dead) continue;
      for (e in enemies) {
        var en: Enemy = cast e;
        if (en.dead) continue;
        if (overlap(b.bounds(), e.bounds())) {
          bullet.dead = true;
          en.onDamage(this, 1);   // score/explosion は enemy が担う
          break;
        }
      }
    }
    if (player.alive && player.invincible == 0) {
      for (e in enemies) {
        if (overlap(player.bounds(), e.bounds())) { player.hit(); break; }
      }
      for (eb in lists.get(Faction.EnemyBullets)) {
        if (overlap(player.bounds(), eb.bounds())) { player.hit(); break; }
      }
    }
  }

  public static function overlap(a: Rect, b: Rect): Bool {
    return b.x < a.x + a.w && a.x < b.x + b.w && b.y < a.y + a.h && a.y < b.y + b.h;
  }
}
