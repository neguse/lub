package game;

import lub.Lub;
import lubx.Boot;
import lubx.FixedStep;
import render.Gfx2d;
import render.Atlas;
import render.Font;
import input.InputSource;
import input.Input;
import input.MockInput;
import input.InputSnapshot;
import scenes.Scene;
import scenes.SceneTransition;
import scenes.Title;
import scenes.Play;
import scenes.GameOver;

class Game {
	public static inline var W:Int = 640;
	public static inline var H:Int = 480;
	static inline var DT:Float = 1.0 / 60.0;
	public static var gfx:Gfx2d = null;
	public static var fontAtlas:Atlas = null;
	public static var jikiAtlas:Atlas = null;
	public static var cursorAtlas:Atlas = null;
	public static var enemyAtlas:Atlas = null;
	public static var font:Font = null;
	static var input:InputSource = null;
	static var scene:Scene = null;
	static var step = new FixedStep();
	public static var frameCount:Int = 0;
	public static var score:Int = 0;
	public static var hiscore:Int = 0;

	public static function init() {
		Boot.config({width: W, height: H});
	}

	static function boot():Bool {
		if (gfx == null)
			gfx = new Gfx2d();
		if (fontAtlas == null)
			fontAtlas = new Atlas("ngs_font", "samples/ngs/data/font.png");
		if (jikiAtlas == null)
			jikiAtlas = new Atlas("ngs_jiki", "samples/ngs/data/jiki.png");
		if (cursorAtlas == null)
			cursorAtlas = new Atlas("ngs_cursor", "samples/ngs/data/cursor.png");
		if (enemyAtlas == null)
			enemyAtlas = new Atlas("ngs_enemy", "samples/ngs/data/enemy.png");
		if (!gfx.ensure() || !fontAtlas.ensure() || !jikiAtlas.ensure() || !cursorAtlas.ensure() || !enemyAtlas.ensure())
			return false;
		if (font == null)
			font = new Font(fontAtlas);
		if (input == null) {
			var mock = lua.Os.getenv("LUB_NGS_MOCK");
			if (mock == "fire") {
				input = new MockInput(function(f) {
					var s = new InputSnapshot();
					if ((f & 1) == 0) {
						s.fire = true;
						s.menu = true;
					}
					return s;
				});
			} else if (mock == "kill") {
				// 左へ 6 frame 寄り enemy#1 (spawn x280) の弾ライン上に陣取り手連射。
				// 連続弾の壁に降下してきた敵が即撃破され explosion が出る (golden 用)。
				input = new MockInput(function(f) {
					var s = new InputSnapshot();
					if ((f & 1) == 0) {
						s.fire = true;
						s.menu = true;
					}
					if (f < 6)
						s.dirX = -1;
					return s;
				});
			} else if (mock != null) {
				input = new MockInput();
			} else {
				input = new Input();
			}
		}
		if (scene == null) {
			var boot = lua.Os.getenv("LUB_NGS_BOOT");
			scene = switch (boot) {
				case "play": new Play(false);
				case "active": new Play(false, false, true); // intro skip (golden/debug 用)
				case "boss": new Play(false, true); // boss 直入り (golden 用)
				case "gameover": new GameOver(12345); // score inject 直入り (golden 用)
				default: new Title();
			};
		}
		return true;
	}

	static function applyTransition(from:Scene):Bool {
		return switch (from.transition()) {
			case Stay:
				true;
			case Switch(s):
				scene = s;
				true;
			case Quit:
				Lub.quit();
				false;
		};
	}

	public static function frame(dt:Float) {
		if (!boot())
			return;

		input.capture();
		var lastDrawScene = scene;
		var transitionScene:Scene = null;
		step.frame(dt, _ -> {
			// catch-up 中間の scene は、省略された draw の後と同じ位置で遷移する。
			if (transitionScene != null) {
				var from = transitionScene;
				transitionScene = null;
				if (!applyTransition(from)) {
					step.stop();
					return;
				}
			}

			input.refresh();
			var updatedScene = scene;
			updatedScene.update(input.current);
			lastDrawScene = updatedScene;
			transitionScene = updatedScene;
			frameCount++;
		});

		gfx.beginFrame();
		lastDrawScene.draw(gfx.drawList);
		gfx.endFrame();

		// 最後の fixed update は従来どおり update → draw → transition。
		if (transitionScene != null)
			applyTransition(transitionScene);
	}
}
