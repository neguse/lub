import lub.Audio;
import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lub.Io;
import lub.Phys2d;
import lubx.Boot;
import lubx.Camera2d;
import lubx.Color;
import lubx.MeshText;
import lubx.Rand;
import lubx.Sfx;
import lubx.SpriteBatch;
import lubx.Text;

private typedef Ball = {
	id:Int,
	level:Int,
	spawnX:Float, // 宣言用: initial は不変でないと body が作り直される
	spawnY:Float,
	x:Float,
	y:Float,
	angle:Float,
	age:Float,
	overT:Float
};

/**
	いろはスイカ: 同じ文字の玉がぶつかると「い→ろ→は→に→ほ→へ→と」の
	順に育つスイカゲーム風サンプル。玉の文字は `font_glyph_mesh` の
	三角形化グリフ (拡大しても輪郭が崩れない)、HUD は `lubx.Text` の
	動的 glyph atlas (bitmap ラスタ)。フォント描画の2レジームを両方使う。
**/
class Iroha21 {
	static inline var W = 640;
	static inline var H = 360;
	static inline var PPM = 100.0; // physics m -> logical px
	static inline var HALF_W = 1.15; // 容器の半幅 (m)
	static inline var WALL_TOP = 3.2; // 壁の上端 (m)
	static inline var LINE_Y = 2.45; // ゲームオーバー線 (m)
	static inline var DROP_Y = 2.85; // 投下位置 (m)

	static var CHARS = ["い", "ろ", "は", "に", "ほ", "へ", "と"];
	static var RADII = [0.13, 0.17, 0.22, 0.28, 0.36, 0.46, 0.58];
	static var COLORS:Array<Color> = [
		Color.rgb(0.91, 0.36, 0.36),
		Color.rgb(0.93, 0.60, 0.34),
		Color.rgb(0.93, 0.83, 0.36),
		Color.rgb(0.49, 0.80, 0.42),
		Color.rgb(0.36, 0.72, 0.91),
		Color.rgb(0.50, 0.45, 0.93),
		Color.rgb(0.83, 0.36, 0.91),
	];

	static var cam = new Camera2d(W, H, PPM, 320.0, 344.0);

	static var batch = new SpriteBatch(W, H);
	// ゲームオーバー表示用。SpriteBatch は atlas バケツ順で描くので、玉の上に
	// 帯を重ねるには flush を分ける必要がある (バッファも別 prefix にする)。
	static var overlay = new SpriteBatch(W, H, "lubx_sprite", "iroha_overlay");
	static var hud:Text = null;
	static var mesh:MeshText = null;
	static var ttf:String = null;
	static var fontVersion = 0;

	static var balls:Array<Ball> = [];
	static var nextId = 0;
	static var nextLevel = 0;
	static var score = 0;
	static var best = 0;
	static var cooldown = 0.0;
	static var over = false;
	static var t = 0.0;
	static var rng = new Rand(0x1234567);

	// LUB_IROHA_AUTO=1 で自動プレイ (ヘッドレス検証・デモ用)
	static var auto = lua.Os.getenv("LUB_IROHA_AUTO") != null;

	public static function main() {}

	public static function onInit() {
		Boot.config({width: W, height: H});
	}

	// --- アセット -----------------------------------------------------------

	static function ensureAssets():Bool {
		var r = Io.loadText("samples/21_iroha/data/MPLUS1p-subset.ttf");
		if (r.text == null)
			return false;
		if (ttf == null || fontVersion != r.version) {
			ttf = r.text;
			fontVersion = r.version;
			hud = new Text("iroha_hud", ttf, 20);
			mesh = new MeshText("iroha_mesh", ttf, fontVersion, W, H);
		}
		return ttf != null && mesh != null;
	}

	// --- ゲーム -------------------------------------------------------------

	static function spawn(x:Float, y:Float, level:Int) {
		balls.push({
			id: nextId++,
			level: level,
			spawnX: x,
			spawnY: y,
			x: x,
			y: y,
			angle: 0.0,
			age: 0.0,
			overT: 0.0
		});
	}

	static function reset() {
		balls = [];
		score = 0;
		over = false;
		cooldown = 0.3;
	}

	public static function onFrame(dt:Float) {
		if (auto)
			dt = 1.0 / 60.0; // 自動プレイは決定的に進める (headless 検証用)
		t += dt;
		if (dt > 0.1)
			dt = 0.1;
		if (!ensureAssets())
			return;

		// --- 入力
		var dropR = RADII[nextLevel];
		var dropX = cam.mouseWorld().x;
		if (dropX < -(HALF_W - dropR))
			dropX = -(HALF_W - dropR);
		if (dropX > HALF_W - dropR)
			dropX = HALF_W - dropR;
		cooldown -= dt;
		var click = Input.mousePressed() || Input.keyPressed(Key.Space);
		if (auto && cooldown <= 0.0 && !over) {
			click = true;
			dropX = (rng.float() * 2.0 - 1.0) * (HALF_W - dropR);
		}
		if (over) {
			if (click)
				reset();
		} else if (click && cooldown <= 0.0) {
			spawn(dropX, DROP_Y, nextLevel);
			var pick = [0, 0, 0, 1, 1, 2][Std.int(rng.float() * 6)];
			nextLevel = pick;
			cooldown = 0.45;
			Audio.play(Sfx.blip(420, 260, 0.06, 0.3));
		}

		// --- 物理 (immediate mode: 生きている玉だけ毎フレーム宣言する)
		var world = Phys2d.world("iroha", {
			gravity: {x: 0.0, y: -10.0},
			fixedDt: 1.0 / 120.0,
			substeps: 4,
			maxSteps: 4,
		});
		Phys2d.begin(world);

		var arena = Phys2d.body(world, "arena", {type: Phys2d.STATIC, initial: {x: 0.0, y: 0.0}});
		Phys2d.box(arena, "floor", {
			hx: HALF_W + 0.3,
			hy: 0.1,
			cy: -0.1,
			friction: 0.5
		});
		Phys2d.box(arena, "wall_l", {
			hx: 0.1,
			hy: WALL_TOP * 0.5,
			cx: -(HALF_W + 0.1),
			cy: WALL_TOP * 0.5,
			friction: 0.3
		});
		Phys2d.box(arena, "wall_r", {
			hx: 0.1,
			hy: WALL_TOP * 0.5,
			cx: HALF_W + 0.1,
			cy: WALL_TOP * 0.5,
			friction: 0.3
		});

		var refs = new Map<Int, Dynamic>();
		for (ball in balls) {
			var b = Phys2d.body(world, "ball:" + ball.id, {
				type: Phys2d.DYNAMIC,
				initial: {x: ball.spawnX, y: ball.spawnY},
			});
			Phys2d.circle(b, "c", {
				r: RADII[ball.level],
				density: 1.0,
				friction: 0.35,
				restitution: 0.12,
				contact: true,
			});
			refs.set(ball.id, b);
		}

		Phys2d.step(world, dt);

		var byId = new Map<Int, Ball>();
		for (ball in balls) {
			var p = Phys2d.pose(refs.get(ball.id));
			ball.x = p.x;
			ball.y = p.y;
			ball.angle = p.angle;
			ball.age += dt;
			byId.set(ball.id, ball);
		}

		// --- 合体: 同じ文字同士の contact begin で次の文字へ
		if (!over) {
			var contacts:Dynamic = Phys2d.contacts(world, "begin");
			var merged = new Map<Int, Bool>();
			var i = 1;
			while (contacts[i] != null) {
				var c:Dynamic = contacts[i];
				i++;
				var ka:String = c.a.body;
				var kb:String = c.b.body;
				if (ka.substr(0, 5) != "ball:" || kb.substr(0, 5) != "ball:")
					continue;
				var b1 = byId.get(Std.parseInt(ka.substr(5)));
				var b2 = byId.get(Std.parseInt(kb.substr(5)));
				if (b1 == null || b2 == null || b1.level != b2.level)
					continue;
				if (merged.exists(b1.id) || merged.exists(b2.id))
					continue;
				merged.set(b1.id, true);
				merged.set(b2.id, true);
				var level = b1.level;
				if (level < CHARS.length - 1)
					spawn((b1.x + b2.x) * 0.5, (b1.y + b2.y) * 0.5, level + 1);
				score += (level + 1) * (level + 1);
				if (score > best)
					best = score;
				Audio.play(Sfx.blip(400, 840, 0.12, 0.35), {pitch: 1.0 + level * 0.15});
			}
			if (merged.iterator().hasNext())
				balls = balls.filter(b -> !merged.exists(b.id));
		}

		// --- ゲームオーバー判定: 落下直後を除き、線より上に居座ったら終わり
		if (!over) {
			for (ball in balls) {
				var top = ball.y + RADII[ball.level];
				if (top > LINE_Y && ball.age > 1.0)
					ball.overT += dt;
				else
					ball.overT = 0.0;
				if (ball.overT > 1.0) {
					over = true;
					Audio.play(Sfx.noise(0.4, 0.5, 0x2468ace));
				}
			}
		}

		// --- 描画
		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.10, 0.09, 0.13, 1.0]),
		});
		batch.begin();

		// 容器
		var wallCol = Color.rgb(0.35, 0.32, 0.42);
		batch.rect(cam.sx(-HALF_W) - 8, cam.sy(WALL_TOP), 8, WALL_TOP * PPM, wallCol);
		batch.rect(cam.sx(HALF_W), cam.sy(WALL_TOP), 8, WALL_TOP * PPM, wallCol);
		batch.rect(cam.sx(-HALF_W) - 8, cam.originY, HALF_W * 2 * PPM + 16, 8, wallCol);

		// ゲームオーバー線
		var lineBlink = over ? 1.0 : 0.25 + 0.15 * Math.sin(t * 4.0);
		batch.rect(cam.sx(-HALF_W), cam.sy(LINE_Y), HALF_W * 2 * PPM, 2, Color.rgb(0.9, 0.3, 0.3, lineBlink));

		// 玉 (sprite は本体、上に mesh グリフ)
		for (ball in balls) {
			var r = RADII[ball.level] * PPM;
			var c = COLORS[ball.level];
			batch.disc(cam.sx(ball.x), cam.sy(ball.y), r, c);
		}

		// 投下プレビュー
		if (!over) {
			var r = dropR * PPM;
			var c = COLORS[nextLevel];
			batch.disc(cam.sx(dropX), cam.sy(DROP_Y), r, Color.rgb(c.r, c.g, c.b, 0.5 + 0.2 * Math.sin(t * 6.0)));
		}

		// HUD (bitmap 小サイズレジーム)
		hud.draw(batch, "スコア " + score, 12, 26);
		hud.draw(batch, "ベスト " + best, 12, 50, Color.rgb(0.8, 0.8, 0.8, 0.8));
		hud.draw(batch, "いろはにほへと", 500, 26, Color.rgb(0.7, 0.7, 0.8, 0.9), 0.8);

		batch.flush();

		// mesh グリフ (拡大レジーム): 玉の文字は物理の回転ごと描く
		var ink = Color.rgb(0.12, 0.10, 0.14, 0.9);
		for (ball in balls) {
			mesh.char(CHARS[ball.level], cam.sx(ball.x), cam.sy(ball.y), RADII[ball.level] * PPM * 1.3, ball.angle, ink, true);
		}
		if (!over)
			mesh.char(CHARS[nextLevel], cam.sx(dropX), cam.sy(DROP_Y), dropR * PPM * 1.3, 0.0, Color.rgb(ink.r, ink.g, ink.b, 0.6), true);

		if (over) {
			// 帯とメッセージは玉の上に重ねたいので別 batch で flush を分ける
			overlay.begin();
			overlay.rect(0, 108, W, 132, Color.rgb(0.05, 0.04, 0.07, 0.85));
			var msg = "クリックでもういちど";
			hud.draw(overlay, msg, cam.originX - hud.width(msg) * 0.5, 222);
			overlay.flush();
			mesh.textCentered("おしまい", cam.originX, 190, 64, Color.rgb(0.95, 0.92, 0.85));
		}

		Gfx.endPass();
	}
}
