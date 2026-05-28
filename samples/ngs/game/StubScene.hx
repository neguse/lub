package game;

import input.InputSnapshot;
import render.DrawList;
import scenes.Scene;
import scenes.SceneTransition;

class StubScene implements Scene {
  public function new() {}
  public function update(input: InputSnapshot): Void {}
  public function draw(dl: DrawList): Void {
    dl.quad(Game.gfx.white, 280, 200, 80, 80, { r: 1.0, g: 0.3, b: 0.2, a: 1.0 });
  }
  public function transition(): SceneTransition return Stay;
}
