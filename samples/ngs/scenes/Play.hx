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
  var frame: Int = 0;

  public function new(noGod: Bool, ?bossOnly: Bool = false) {
    Game.score = 0;
    world = new World(noGod);
    spawner = new Spawner(noGod, bossOnly);
  }

  public function update(input: InputSnapshot): Void {
    frame++;
    spawner.tick(world);
    world.tick(input);
    world.resolveCollisions();
  }

  public function draw(dl: DrawList): Void {
    world.drawAll(dl);
    var white: Color = { r: 1, g: 1, b: 1, a: 1 };
    // warning: 原典 game_timer 900..1100 で playfield 内を下スクロール (flavor)
    if (frame > 900 && frame < 1100) {
      Game.font.drawString(dl, 266, frame - 900, "w a r n i n g", white);
    }
    Game.font.drawString(dl, 460, 20, "score", white);
    Game.font.drawInt(dl, 460, 30, Game.score, 6, white);
    Game.font.drawString(dl, 460, 50, "life", white);
    Game.font.drawInt(dl, 500, 50, world.player.lives, 1, white);
  }

  public function transition(): SceneTransition return Stay;  // GameOver 遷移は Task 5
}
