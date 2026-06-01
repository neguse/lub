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

// 直進弾敵 (原典 type 5。名前に反し追尾しない)。Enemies faction (自弾で破壊可)。
// boss phase3 が一定間隔で spawn。1 発で撃破 → explosion (score 無し)。
class Homing implements Enemy {
	var x:Int;
	var y:Int;
	final originX:Int;
	final originY:Int;
	final theta:Float; // spawn 時に与えられた角度 (1024単位→rad, 上=0)
	var dist:Float = 0;
	final speed:Int;

	public var dead:Bool = false;

	static inline var W = 16;
	static inline var H = 16;

	// angle1024: 原典 1024 単位の角度 (boss が 0x2f6 / 0x2ce を渡す)。
	public function new(sx:Int, sy:Int, angle1024:Int, noGod:Bool) {
		x = sx;
		y = sy;
		originX = sx;
		originY = sy;
		theta = angle1024 * (2 * Math.PI / 1024);
		speed = noGod ? 14 : 6;
	}

	public function update(world:World, input:InputSnapshot):Bool {
		if (dead)
			return false;
		x = originX + Std.int(Math.round(Math.sin(theta) * dist));
		y = originY - Std.int(Math.round(Math.cos(theta) * dist));
		dist += speed;
		if (!World.overlap(bounds(), {
			x: Viewport.X,
			y: Viewport.Y,
			w: Viewport.W,
			h: Viewport.H
		}))
			dead = true;
		return !dead;
	}

	// 1 発で撃破 + explosion。原典 homing は score を与えない。
	public function onDamage(world:World, amount:Int):Bool {
		dead = true;
		world.spawn(Faction.Effects, new Explosion(x + 5, y + 5));
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
		dl.sprite(Game.enemyAtlas, Atlases.enemy[4], Viewport.sx(x), Viewport.sy(y));
	}
}
