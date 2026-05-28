package entities.enemies;

import entities.Entity;

interface Enemy extends Entity {
  public var dead(default, default): Bool;   // 命中/撃破/画面外で立つ。World/自身が参照
  // 与ダメージ。撃破されたら true を返す (World が score/除去を処理)。
  public function onDamage(amount: Int): Bool;
}
