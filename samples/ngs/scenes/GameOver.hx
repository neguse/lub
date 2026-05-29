package scenes;

import input.InputSnapshot;
import render.DrawList;
import render.Color;
import game.Game;

// 全滅 or ボス撃破後の画面。score / hi-score 表示、Z 押下または数秒で Title へ。
class GameOver implements Scene {
  final score: Int;
  var t: Int = 0;
  var done: Bool = false;
  static inline var TIMEOUT = 300;   // 5 秒で自動的に Title へ

  public function new(score: Int) {
    this.score = score;
    if (score > Game.hiscore) Game.hiscore = score;
  }

  public function update(input: InputSnapshot): Void {
    t++;
    if (input.menu) done = true;   // Z trigger
  }

  public function draw(dl: DrawList): Void {
    var white: Color = { r: 1, g: 1, b: 1, a: 1 };
    Game.font.drawString(dl, 264, 200, "game over", white);
    Game.font.drawString(dl, 264, 230, "score", white);
    Game.font.drawInt(dl, 320, 230, score, 6, white);
    Game.font.drawString(dl, 264, 245, "hi score", white);   // 原典ラベル準拠 (ハイフン無し)
    Game.font.drawInt(dl, 336, 245, Game.hiscore, 6, white);
  }

  public function transition(): SceneTransition
    return (done || t > TIMEOUT) ? Switch(new Title()) : Stay;
}
