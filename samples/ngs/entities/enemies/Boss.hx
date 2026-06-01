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

// ボス本体 (原典 type 6)。4 phase 状態機械。Enemies faction。
// 原典フィールド対応: angle=phase, timer/anim/counter/hp を phase ごとに転用。
class Boss implements Enemy {
	var x:Int;
	var y:Int;
	var originX:Int = 0;
	var originY:Int = 0; // orbit / sweep 中心
	var phase:Int = 0; // 0降下 1上下動+弾 2横スイープ 3homing投下 4自滅
	var timer:Int = 0;
	var anim:Int = 0;
	var counter:Int = 0;
	var hp:Int = 0;
	final noGod:Bool;

	public var dead:Bool = false;

	static inline var W = 26;
	static inline var H = 32;

	public function new(sx:Int, sy:Int, noGod:Bool) {
		x = sx;
		y = sy;
		this.noGod = noGod;
	}

	public function update(world:World, input:InputSnapshot):Bool {
		if (dead)
			return false;
		switch (phase) {
			case 0: // 降下
				y += 1;
				if (y > 0x28) {
					originX = x;
					originY = y + 0x40;
					phase = 1;
					timer = 0x100;
					anim = 0;
					counter = 0;
					hp = 0x14;
				}
			case 1: // 上下動 + 弾
				var r = noGod ? 64 : 1;
				timer = (timer + (noGod ? 0x14 : 0x3c)) & 0x3ff;
				var th = timer * (2 * Math.PI / 1024);
				x = originX + Std.int(Math.round(Math.sin(th) * r));
				y = originY - Std.int(Math.round(Math.cos(th) * 64));
				if (noGod) {
					anim = 0;
					fireBullet(world);
					anim = 1;
					fireBullet(world);
					anim = 2;
					fireBullet(world);
				} else if ((counter & 1) == 0) {
					fireBullet(world);
					anim = (anim == 0) ? 1 : 0;
				}
				counter++;
				if (hp == 0 && timer == 0) {
					phase = 2;
					timer = 0;
					anim = 0;
					counter = 0;
					hp = 10;
				}
			case 2: // 横スイープ
				if (hp < 1) {
					x = originX;
					phase = 3;
					timer = 0;
					anim = 10;
				} else if (counter < 0x96) {
					x = originX + sweepX(anim, timer);
					anim = (anim + 1) & 3;
					timer = Std.int(counter * 32 / 0x96);
					counter++;
				} else if (counter == 0x96) {
					anim = Std.int((world.player.x - 200) / 0x3c);
					x = originX + sweepX(anim, timer);
					if (noGod)
						world.spawn(Faction.EnemyBullets, new BossSub(x, y, noGod));
					counter++;
				} else if (counter == 0xb4) {
					if (!noGod)
						world.spawn(Faction.EnemyBullets, new BossSub(x, y, noGod));
					counter++;
				} else if (counter > 0xd1) {
					timer = 0;
					anim = 0;
					counter = 0; // スイープ再開 (counter++ しない)
				} else {
					counter++;
				}
			case 3: // homing 投下
				timer++;
				if ((timer >> 4 & 1) == 1) {
					var a = noGod ? 0x2ce : 0x2f6;
					var hx = ((timer << 2) % 0xf0) + 200;
					world.spawn(Faction.Enemies, new Homing(hx, 0, a, noGod));
				}
				if (anim < 1) {
					phase = 4;
					timer = 0x3c;
				}
			case 4: // 自滅 (落下 + 爆発)
				if (noGod) {
					y += 0x0c;
				} else if (timer < 1) {
					y += 0x14;
				} else {
					timer--;
				}
				world.spawn(Faction.Effects, new Explosion(x + 0xe, y + 10));
				if (y > Viewport.Y + Viewport.H) {
					dead = true;
					Game.score += 1000;
					world.bossDefeated = true;
					return false;
				}
			default:
		}
		return !dead;
	}

	// phase1 で boss.anim から spread を決め、自機中心へ BossBullet を撃つ。
	function fireBullet(world:World):Void {
		var off = bulletOffset();
		world.spawn(Faction.EnemyBullets, new BossBullet(x + 5, y + 5, world.player.x + 8.0, world.player.y + 8.0, off, noGod));
	}

	inline function bulletOffset():Float {
		var u = Math.PI / 16; // 0x20
		var w = 0x3c * (2 * Math.PI / 1024); // 0x3c
		if (!noGod)
			return (anim == 0) ? -u : u;
		return switch (anim) {
			case 0: w;
			case 1: -w;
			default: 0.0;
		};
	}

	inline function sweepX(dir:Int, t:Int):Int {
		return switch (dir) {
			case 0: -3 * t;
			case 1: -t;
			case 2: t;
			case 3: 3 * t;
			default: 0;
		};
	}

	// phase 別の被弾。boss は onDamage では除去されない (phase4 で自滅)。
	public function onDamage(world:World, amount:Int):Bool {
		switch (phase) {
			case 1:
				if (hp > 0)
					hp -= amount;
				Game.score += 1;
				return true;
			case 2:
				if (counter > 0x95) {
					if (hp > 0 && counter > 0x96) {
						hp -= amount;
						Game.score += 1;
					}
					return true;
				}
			case 3:
				if (anim > 0)
					anim -= amount;
				Game.score += 1;
				return true;
			default: // phase0/4 は無敵
		}
		return false;
	}

	public function bounds():Rect
		return {
			x: x,
			y: y,
			w: W,
			h: H
		};

	public function draw(dl:DrawList):Void {
		dl.sprite(Game.enemyAtlas, Atlases.enemy[12], Viewport.sx(x), Viewport.sy(y));
	}
}
