package entities;

import input.InputSnapshot;
import render.DrawList;
import render.Rect;
import render.Viewport;
import game.Game;
import assets.Atlases;

// 撃破時の死亡演出。Effects faction (当たり判定対象外)。
// 寿命は原典 (origin_x が 0x1f で消滅) どおり 31 frame。
class Explosion implements Entity {
	var x:Int;
	var y:Int;
	var timer:Int = 0;

	static inline var LIFE = 31;

	public function new(sx:Int, sy:Int) {
		x = sx;
		y = sy;
	}

	public function update(world:World, input:InputSnapshot):Bool {
		timer++;
		return timer < LIFE;
	}

	public function bounds():Rect
		return {
			x: x,
			y: y,
			w: 0,
			h: 0
		};

	public function draw(dl:DrawList):Void {
		var frame = timer >> 2; // 原典: enemy[7 + (origin_x >> 2)]
		if (frame > 7)
			frame = 7;
		dl.sprite(Game.enemyAtlas, Atlases.enemy[7 + frame], Viewport.sx(x), Viewport.sy(y));
	}
}
