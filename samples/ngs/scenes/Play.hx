package scenes;

import input.InputSnapshot;
import render.DrawList;
import render.Color;
import game.Game;
import game.Spawner;
import entities.World;
import assets.Atlases;

class Play implements Scene {
	final world:World;
	final spawner:Spawner;
	var timer:Int = 0;
	var phase:Int = 0; // 0=intro, 1=active
	final skipIntro:Bool;

	public function new(noGod:Bool, ?bossOnly:Bool = false, ?skipIntro:Bool = false) {
		Game.score = 0;
		world = new World(noGod);
		spawner = new Spawner(noGod, bossOnly);
		this.skipIntro = skipIntro || bossOnly;
		if (this.skipIntro)
			phase = 1;
	}

	public function update(input:InputSnapshot):Void {
		timer++;
		if (phase == 0) {
			if (timer > 0xf0) {
				phase = 1;
				timer = 0;
			}
			return;
		}
		world.tick(input);
		world.resolveCollisions();
		spawner.tick(world);
	}

	public function draw(dl:DrawList):Void {
		var white:Color = {
			r: 1,
			g: 1,
			b: 1,
			a: 1
		};
		if (phase == 0) {
			Game.font.drawString(dl, 0x11e - 200, 100, "n", white);
			if (timer > 0x3c)
				Game.font.drawString(dl, 0x13c - 200, 100, "g", white);
			if (timer > 0x78)
				Game.font.drawString(dl, 0x15a - 200, 100, "s", white);
			if (timer > 0xb4)
				Game.font.drawString(dl, 0x12e - 200, 300, "g.o.!", white);
			drawHud(dl, white);
			return;
		}
		// warning: 原典 game_timer 900..1100 で playfield 内を下スクロール (flavor)
		if (timer > 900 && timer < 0x44c) {
			Game.font.drawString(dl, 0x10a - 200, timer - 900, "w a r n i n g", white);
		}
		drawHud(dl, white);
		world.drawAll(dl);
	}

	function drawHud(dl:DrawList, white:Color):Void {
		Game.font.drawString(dl, 0x1cc - 200, 0x1e, "score:", white);
		Game.font.drawString(dl, 0x1cc - 200, 0x32, Std.string(Game.score), white);
		Game.font.drawString(dl, 0x1cc - 200, 0x50, "hi score:", white);
		Game.font.drawString(dl, 0x1cc - 200, 100, Std.string(Game.hiscore), white);
		Game.font.drawString(dl, 0x1cc - 200, 400, "life", white);
		for (i in 0...world.player.lives) {
			dl.sprite(Game.jikiAtlas, Atlases.jiki[0], 0x1cc - 200 + 40 + i * 18, 400);
		}
	}

	public function transition():SceneTransition {
		if (world.bossDefeated || world.player.isFinished())
			return Switch(new GameOver(Game.score));
		return Stay;
	}
}
