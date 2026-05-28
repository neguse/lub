package game;

import entities.World;
import entities.Faction;
import entities.enemies.Normal;

class Spawner {
  var frame: Int = 0;
  final noGod: Bool;
  public function new(noGod: Bool) { this.noGod = noGod; }

  public function tick(world: World): Void {
    frame++;
    inline function spawn(sx: Int) {
      world.spawn(Faction.Enemies,
        new Normal(sx, 0, world.player.x + 8, world.player.y + 8, noGod));
    }
    switch (frame) {
      case 60:  spawn(280);
      case 120: spawn(350);
      case 400: spawn(280); spawn(360);
      default:
    }
  }
}
