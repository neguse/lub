package entities.enemies;

import entities.Entity;
import entities.World;

interface Enemy extends Entity {
  public var dead(default, default): Bool;   // 命中/撃破/画面外で立つ。World/自身が参照
  // 自機弾による命中。score 加算・撃破時の explosion spawn は実装側が担う。
  // 命中が有効で弾を消費する場合 true。無敵 phase 等で弾が通過する場合 false。
  public function onDamage(world: World, amount: Int): Bool;
}
