package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;

interface Entity {
  // false を返したら World が次フレームで除去
  public function update(world: World, input: InputSnapshot): Bool;
  public function draw(dl: DrawList): Void;
  public function bounds(): Rect;   // hitbox (world px)
}
