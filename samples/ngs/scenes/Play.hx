package scenes;

import input.InputSnapshot;
import render.DrawList;
import game.Game;
import assets.Atlases;

class Play implements Scene {
  final noGod: Bool;
  var t: Int = 0;

  public function new(noGod: Bool) {
    this.noGod = noGod;
  }

  public function update(input: InputSnapshot): Void {
    t = t + 1;
  }

  public function draw(dl: DrawList): Void {
    // 自機 (jiki atlas rect 0) を画面中央下に。Plan 2 で Player entity に置換。
    dl.sprite(Game.jikiAtlas, Atlases.jiki[0], 312, 400);
  }

  public function transition(): SceneTransition return Stay;
}
