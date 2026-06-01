package entities.enemies;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;
import entities.World;
import entities.Faction;
import entities.Explosion;
import entities.enemies.Aimed;

class Normal implements Enemy {
	var x:Int;
	var y:Int;
	final originX:Int;
	final originY:Int;
	final theta:Float; // 照準方向 (上=0, ラジアン)
	var counter:Int = 0;
	var anim:Int = 0;
	var hp:Int;
	final noGod:Bool;

	public var dead:Bool = false;

	static inline var W = 16;
	static inline var H = 16;

	// spawn 時の自機 world 位置で照準を固定。
	public function new(sx:Int, sy:Int, playerCx:Float, playerCy:Float, noGod:Bool) {
		x = sx;
		y = sy;
		originX = sx;
		originY = sy;
		var dxw = playerCx - (sx + W / 2);
		var dyUp = -((playerCy) - (sy + H / 2));
		theta = Math.atan2(dxw, dyUp); // 上=0 規約
		hp = noGod ? 2 : 1;
		this.noGod = noGod;
	}

	public function update(world:World, input:InputSnapshot):Bool {
		if (dead)
			return false; // 撃破済みは行動しない (墓場発砲の防止)
		// 原典: x = origin_x + sin(θ)*counter, y = origin_y - cos(θ)*counter (上=θ0)
		x = originX + Std.int(Math.round(Math.sin(theta) * counter));
		y = originY - Std.int(Math.round(Math.cos(theta) * counter));
		counter += 2;
		if (counter == 10)
			fireAimed(world);
		if (counter == 22 && noGod)
			fireAimed(world); // 原典: 2 波目 (NO_GOD のみ)
		anim = (anim + 1) & 7;
		// viewport 外で除去 (bounds が playfield と重ならない)
		if (!World.overlap(bounds(), {
			x: Viewport.X,
			y: Viewport.Y,
			w: Viewport.W,
			h: Viewport.H
		}))
			dead = true;
		return !dead;
	}

	// 自機中心へ照準した spread 弾。原典 spread 単位 0x20=π/16, 0x80=π/4。
	// noGod=false: 3-way (+π/16, -π/16, 0)。noGod=true: 5-way (±π/4 を先に)。
	// spawn 順は原典の射出順に一致させる (EnemyBullets list の draw 順を決定的に)。
	function fireAimed(world:World):Void {
		var pcx = world.player.x + 8.0, pcy = world.player.y + 8.0;
		inline function shot(off:Float)
			world.spawn(Faction.EnemyBullets, new Aimed(x, y, pcx, pcy, off, noGod));
		var u = Math.PI / 16; // 0x20
		if (noGod) {
			var q = Math.PI / 4; // 0x80
			shot(q);
			shot(-q);
		}
		shot(u);
		shot(-u);
		shot(0);
	}

	public function onDamage(world:World, amount:Int):Bool {
		hp -= amount;
		Game.score += 10;
		if (hp < 1) {
			dead = true;
			Game.score += 100;
			world.spawn(Faction.Effects, new Explosion(x + 3, y + 3));
		}
		return true;
	}

	public function bounds():Rect
		return {
			x: x,
			y: y,
			w: W,
			h: H
		};

	public function draw(dl:DrawList):Void {
		dl.sprite(Game.enemyAtlas, Atlases.enemy[0], Viewport.sx(x), Viewport.sy(y));
	}
}
