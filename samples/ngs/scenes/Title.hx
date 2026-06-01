package scenes;

import input.InputSnapshot;
import render.DrawList;
import render.Color;
import game.Game;
import assets.Atlases;

class Title implements Scene {
	static inline var SEL_START:Int = 0;
	static inline var SEL_END:Int = 1;

	var sel:Int = SEL_START;
	var anim:Int = 0;
	var next:SceneTransition = Stay;
	var noGodHeld:Bool = false;

	public function new() {}

	public function update(input:InputSnapshot):Void {
		if (input.dirY < 0)
			sel = SEL_START; // up
		else if (input.dirY > 0)
			sel = SEL_END; // down
		noGodHeld = input.noGod;
		next = Stay;
		if (input.menu) {
			if (sel == SEL_START)
				next = Switch(new Play(input.noGod));
			else
				next = Quit;
		} else if (input.cancel) {
			next = Quit;
		}
	}

	public function draw(dl:DrawList):Void {
		var white:Color = {
			r: 1,
			g: 1,
			b: 1,
			a: 1
		};
		var title = noGodHeld ? "no god shooting game" : "no good shooting game";
		Game.font.drawString(dl, 220, 80, title, white);
		Game.font.drawString(dl, 315, 90, "presented by ngs 2004", white);
		Game.font.drawString(dl, 220, 390, "start", white);
		Game.font.drawString(dl, 220, 420, "end", white);

		var frame = (anim >> 3) & 3;
		var cursorY = (sel == SEL_START) ? 390 : 420;
		dl.sprite(Game.cursorAtlas, Atlases.cursor[frame], 210, cursorY);

		anim = anim + 1; // draw 後に進める: frame 0 capture で cursor frame 0
	}

	public function transition():SceneTransition
		return next;
}
