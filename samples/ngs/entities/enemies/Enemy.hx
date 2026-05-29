package entities.enemies;

import entities.Entity;
import entities.World;

interface Enemy extends Entity {
  public var dead(default, default): Bool;   // 命中/撃破/画面外で立つ。World/自身が参照
  // 与ダメージ。score 加算・撃破時の explosion spawn は実装側が担う。
  // 撃破されたら true を返す (World は弾消去のみ、除去は dead 経由)。
  public function onDamage(world: World, amount: Int): Bool;
}
