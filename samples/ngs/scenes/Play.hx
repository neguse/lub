package scenes;

import input.InputSnapshot;
import render.DrawList;
import render.Color;
import game.Game;
import game.Spawner;
import entities.World;

class Play implements Scene {
  final world: World;
  final spawner: Spawner;

  public function new(noGod: Bool) {
    Game.score = 0;
    world = new World(noGod);
    spawner = new Spawner(noGod);
  }

  public function update(input: InputSnapshot): Void {
    spawner.tick(world);
    world.tick(input);
    world.resolveCollisions();
  }

  public function draw(dl: DrawList): Void {
    world.drawAll(dl);
    var white: Color = { r: 1, g: 1, b: 1, a: 1 };
    Game.font.drawString(dl, 460, 20, "score", white);
    Game.font.drawInt(dl, 460, 30, Game.score, 6, white);
    Game.font.drawString(dl, 460, 50, "life", white);
    Game.font.drawInt(dl, 500, 50, world.player.lives, 1, white);
  }

  public function transition(): SceneTransition return Stay;  // GameOver は Plan 4
}
