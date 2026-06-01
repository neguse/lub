package game;

import entities.World;
import entities.Faction;
import entities.enemies.Normal;
import entities.enemies.Wave;
import entities.enemies.Boss;

class Spawner {
	var frame:Int = 0;
	final noGod:Bool;
	final bossOnly:Bool; // golden 用: 通常面を飛ばし boss を frame 1 で出す

	public function new(noGod:Bool, ?bossOnly:Bool = false) {
		this.noGod = noGod;
		this.bossOnly = bossOnly;
	}

	public function tick(world:World):Void {
		frame++;
		inline function normal(sx:Int)
			world.spawn(Faction.Enemies, new Normal(sx, 0, world.player.x + 8, world.player.y + 8, noGod));
		inline function wave(sx:Int)
			world.spawn(Faction.Enemies, new Wave(sx, 0, noGod));
		inline function boss()
			world.spawn(Faction.Enemies, new Boss(320, -40, noGod));
		if (bossOnly) {
			if (frame == 1)
				boss();
			return;
		}
		switch (frame) {
			case 60:
				normal(280);
			case 120:
				normal(350);
			case 180:
				wave(300);
			case 300:
				wave(320);
			case 400:
				normal(280);
				normal(360);
			case 500:
				wave(300);
				wave(340);
			case 700:
				boss();
			default:
		}
	}
}
