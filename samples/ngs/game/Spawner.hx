package game;

import entities.World;
import entities.Faction;
import entities.enemies.Normal;
import entities.enemies.Wave;

class Spawner {
  var frame: Int = 0;
  final noGod: Bool;
  public function new(noGod: Bool) { this.noGod = noGod; }

  public function tick(world: World): Void {
    frame++;
    inline function normal(sx: Int)
      world.spawn(Faction.Enemies, new Normal(sx, 0, world.player.x + 8, world.player.y + 8, noGod));
    inline function wave(sx: Int)
      world.spawn(Faction.Enemies, new Wave(sx, 0, noGod));
    switch (frame) {
      case 60:  normal(280);
      case 120: normal(350);
      case 180: wave(300);
      case 300: wave(320);
      case 400: normal(280); normal(360);
      case 500: wave(300); wave(340);
      // case 700: Boss — Plan 4
      default:
    }
  }
}
