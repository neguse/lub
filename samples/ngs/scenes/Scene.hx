package scenes;

import input.InputSnapshot;
import render.DrawList;

interface Scene {
  public function update(input: InputSnapshot): Void;
  public function draw(dl: DrawList): Void;
  public function transition(): SceneTransition;
}
