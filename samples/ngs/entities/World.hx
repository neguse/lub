package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;

class World {
  final lists: Map<Faction, Array<Entity>> = new Map();
  // 決定的な反復順のため faction の固定順を持つ
  static final ORDER: Array<Faction> = [Faction.EnemyBullets, Faction.Enemies, Faction.Effects, Faction.PlayerBullets];

  public function new() {
    for (f in ORDER) lists.set(f, []);
  }

  public inline function spawn(f: Faction, e: Entity): Void {
    lists.get(f).push(e);
  }

  public inline function each(f: Faction, fn: (Entity) -> Void): Void {
    for (e in lists.get(f)) fn(e);
  }

  // 全 entity を update し、false を返したものを除去。
  public function tick(input: InputSnapshot): Void {
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
  }

  public static function overlap(a: Rect, b: Rect): Bool {
    return b.x < a.x + a.w && a.x < b.x + b.w && b.y < a.y + a.h && a.y < b.y + b.h;
  }
}
