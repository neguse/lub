import lub.Audio;
import lub.Font;
import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lub.Io;
import lub.Lub;
import lub.Phys2d;
import lubx.Atlas;
import lubx.Color;
import lubx.SpriteBatch;
import lubx.Text;

@:native("utf8")
private extern class NativeUtf8 {
	static function codepoint(s:String, i:Int):Int;
	static function offset(s:String, n:Int, i:Int):Null<Int>;
}

@:native("string")
private extern class NativeString {
	static function len(s:String):Int;
}

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

private typedef GlyphEntry = {
	vb:Dynamic,
	ib:Dynamic,
	count:Int,
	advance:Float,
	cx:Float,
	cy:Float
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
	static inline var CX = 320.0; // world x=0 の screen x
	static inline var FLOOR_Y = 344.0; // world y=0 の screen y
	static inline var HALF_W = 1.15; // 容器の半幅 (m)
	static inline var WALL_TOP = 3.2; // 壁の上端 (m)
	static inline var LINE_Y = 2.45; // ゲームオーバー線 (m)
	static inline var DROP_Y = 2.85; // 投下位置 (m)
	static inline var RATE = 44100;

	static var CHARS = ["い", "ろ", "は", "に", "ほ", "へ", "と"];
	static var RADII = [0.13, 0.17, 0.22, 0.28, 0.36, 0.46, 0.58];
	static var COLORS:Array<Color> = [
		{
			r: 0.91,
			g: 0.36,
			b: 0.36,
			a: 1.0
		},
		{
			r: 0.93,
			g: 0.60,
			b: 0.34,
			a: 1.0
		},
		{
			r: 0.93,
			g: 0.83,
			b: 0.36,
			a: 1.0
		},
		{
			r: 0.49,
			g: 0.80,
			b: 0.42,
			a: 1.0
		},
		{
			r: 0.36,
			g: 0.72,
			b: 0.91,
			a: 1.0
		},
		{
			r: 0.50,
			g: 0.45,
			b: 0.93,
			a: 1.0
		},
		{
			r: 0.83,
			g: 0.36,
			b: 0.91,
			a: 1.0
		},
	];

	static var batch = new SpriteBatch(W, H);
	// ゲームオーバー表示用。SpriteBatch は atlas バケツ順で描くので、玉の上に
	// 帯を重ねるには flush を分ける必要がある (バッファも別 prefix にする)。
	static var overlay = new SpriteBatch(W, H, "lubx_sprite", "iroha_overlay");
	static var circleAtlas:Atlas = null;
	static var whiteAtlas:Atlas = null;
	static var hud:Text = null;
	static var ttf:String = null;
	static var fontVersion = 0;
	static var glyphs = new Map<Int, GlyphEntry>();
	static var glyphShader:Dynamic = null;

	static var balls:Array<Ball> = [];
	static var nextId = 0;
	static var nextLevel = 0;
	static var score = 0;
	static var best = 0;
	static var cooldown = 0.0;
	static var over = false;
	static var t = 0.0;
	static var seed = 0x1234567;

	static var sndDrop = 0;
	static var sndMerge = 0;
	static var sndOver = 0;

	// LUB_IROHA_AUTO=1 で自動プレイ (ヘッドレス検証・デモ用)
	static var auto = lua.Os.getenv("LUB_IROHA_AUTO") != null;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend, width: W, height: H});
	}

	// --- 小物 ---------------------------------------------------------------

	static function rand():Float {
		seed ^= seed << 13;
		seed ^= seed >>> 17;
		seed ^= seed << 5;
		return (seed & 0xffff) / 65536.0;
	}

	static function blip(freq0:Float, freq1:Float, dur:Float, vol:Float):lua.Table<Int, Float> {
		var n = Std.int(dur * RATE);
		var out = lua.Table.create();
		var phase = 0.0;
		for (i in 0...n) {
			var u = i / n;
			phase += (freq0 + (freq1 - freq0) * u) / RATE;
			out[i + 1] = ((phase % 1.0) < 0.5 ? 1.0 : -1.0) * Math.exp(-5.0 * u) * vol;
		}
		return out;
	}

	static function noiseBurst(dur:Float, vol:Float):lua.Table<Int, Float> {
		var n = Std.int(dur * RATE);
		var out = lua.Table.create();
		var s = 0x2468ace;
		var hold = 0.0;
		for (i in 0...n) {
			if (i % 16 == 0) {
				s ^= s << 13;
				s ^= s >>> 17;
				s ^= s << 5;
				hold = (s & 0xffff) / 32768.0 - 1.0;
			}
			out[i + 1] = hold * Math.exp(-4.0 * (i / n)) * vol;
		}
		return out;
	}

	static function synth() {
		if (sndDrop != 0)
			return;
		sndDrop = Audio.pcm(blip(420, 260, 0.06, 0.3), 1, RATE);
		sndMerge = Audio.pcm(blip(400, 840, 0.12, 0.35), 1, RATE);
		sndOver = Audio.pcm(noiseBurst(0.4, 0.5), 1, RATE);
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
			glyphs = new Map();
		}

		if (circleAtlas == null) {
			// 64x64 の soft disc。tint で色を付ける。
			var n = 64;
			var px = new Array<Int>();
			for (y in 0...n) {
				for (x in 0...n) {
					var dx = (x + 0.5) / n * 2.0 - 1.0;
					var dy = (y + 0.5) / n * 2.0 - 1.0;
					var d = Math.sqrt(dx * dx + dy * dy);
					var a = Math.max(0.0, Math.min(1.0, (1.0 - d) * n * 0.5));
					var i = (y * n + x) * 4;
					px[i] = 255;
					px[i + 1] = 255;
					px[i + 2] = 255;
					px[i + 3] = Std.int(a * 255);
				}
			}
			circleAtlas = Atlas.fromPixels("iroha_circle", n, n, px, 1);
		}
		if (whiteAtlas == null) {
			var px = new Array<Int>();
			for (i in 0...4 * 4 * 4)
				px[i] = 255;
			whiteAtlas = Atlas.fromPixels("iroha_white", 4, 4, px, 1);
		}

		var vs = Io.loadText("samples/21_iroha/data/glyph.vs.slang");
		var fs = Io.loadText("samples/21_iroha/data/glyph.fs.slang");
		if (vs.text == null || fs.text == null)
			return false;
		glyphShader = Gfx.useShader("iroha_glyph", vs.text, fs.text, vs.version * 31 + fs.version);
		return glyphShader != null;
	}

	static function glyphFor(cp:Int):GlyphEntry {
		var e = glyphs.get(cp);
		if (e != null)
			return e;
		var gm = Font.glyphMesh(ttf, cp);
		if (gm == null || gm.vert_count == 0)
			return null;
		var verts = new Array<Float>();
		var minX = 1e9, minY = 1e9, maxX = -1e9, maxY = -1e9;
		for (i in 0...gm.vert_count) {
			var x:Float = gm.positions[i * 3 + 1];
			var y:Float = gm.positions[i * 3 + 2];
			verts.push(x);
			verts.push(y);
			if (x < minX)
				minX = x;
			if (x > maxX)
				maxX = x;
			if (y < minY)
				minY = y;
			if (y > maxY)
				maxY = y;
		}
		var idx = new Array<Float>();
		for (i in 0...gm.index_count)
			idx.push(gm.indices[i + 1]);
		e = {
			vb: Gfx.useBuffer("iroha_gv:" + cp, Gfx.VERTEX, verts, fontVersion),
			ib: Gfx.useBuffer("iroha_gi:" + cp, Gfx.INDEX, idx, fontVersion),
			count: gm.index_count,
			advance: gm.advance,
			cx: (minX + maxX) * 0.5,
			cy: (minY + maxY) * 0.5,
		};
		glyphs.set(cp, e);
		return e;
	}

	/** メッシュグリフを1つ描く。(x, y) は配置中心 (論理 px)、size は px/em。 **/
	static function drawGlyph(cp:Int, x:Float, y:Float, size:Float, angle:Float, color:Color, centered:Bool) {
		var e = glyphFor(cp);
		if (e == null)
			return;
		Gfx.draw(e.count, {
			verts: e.vb,
			indices: e.ib,
			uniforms: {
				psr: lua.Table.fromArray([x, y, size, angle]),
				tint: lua.Table.fromArray([color.r, color.g, color.b, color.a]),
				screen: lua.Table.fromArray([(W : Float), (H : Float), 0.0, 0.0]),
				center: lua.Table.fromArray(centered ? [e.cx, e.cy, 0.0, 0.0] : [0.0, 0.0, 0.0, 0.0]),
			},
		}, {
			shader: glyphShader,
			depth: false,
			cull: Gfx.NONE,
			blend: Gfx.ALPHA,
		});
	}

	/** メッシュグリフで1行 (ベースライン基準、中央揃え)。大サイズ演出用。 **/
	static function drawGlyphText(s:String, cx:Float, baselineY:Float, size:Float, color:Color) {
		var width = 0.0;
		var n = NativeString.len(s);
		var i:Null<Int> = 1;
		while (i != null) {
			var pos:Int = i;
			if (pos > n)
				break;
			var e = glyphFor(NativeUtf8.codepoint(s, pos));
			if (e != null)
				width += e.advance;
			i = NativeUtf8.offset(s, 2, pos);
		}
		var pen = cx - width * size * 0.5;
		i = 1;
		while (i != null) {
			var pos:Int = i;
			if (pos > n)
				break;
			var cp = NativeUtf8.codepoint(s, pos);
			var e = glyphFor(cp);
			if (e != null) {
				drawGlyph(cp, pen, baselineY, size, 0.0, color, false);
				pen += e.advance * size;
			}
			i = NativeUtf8.offset(s, 2, pos);
		}
	}

	// --- 座標変換 -----------------------------------------------------------

	static inline function sx(wx:Float):Float
		return CX + wx * PPM;

	static inline function sy(wy:Float):Float
		return FLOOR_Y - wy * PPM;

	static function mouseWorldX():Float {
		var g = Gfx.size();
		var mp = Input.mousePos();
		var mx = mp.x * W / g.w;
		return (mx - CX) / PPM;
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
		synth();

		// --- 入力
		var dropR = RADII[nextLevel];
		var dropX = mouseWorldX();
		if (dropX < -(HALF_W - dropR))
			dropX = -(HALF_W - dropR);
		if (dropX > HALF_W - dropR)
			dropX = HALF_W - dropR;
		cooldown -= dt;
		var click = Input.mousePressed() || Input.keyPressed(Key.Space);
		if (auto && cooldown <= 0.0 && !over) {
			click = true;
			dropX = (rand() * 2.0 - 1.0) * (HALF_W - dropR);
		}
		if (over) {
			if (click)
				reset();
		} else if (click && cooldown <= 0.0) {
			spawn(dropX, DROP_Y, nextLevel);
			var pick = [0, 0, 0, 1, 1, 2][Std.int(rand() * 6)];
			nextLevel = pick;
			cooldown = 0.45;
			Audio.play(sndDrop);
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
				Audio.play(sndMerge, {pitch: 1.0 + level * 0.15});
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
					Audio.play(sndOver);
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
		var wallCol:Color = {
			r: 0.35,
			g: 0.32,
			b: 0.42,
			a: 1.0
		};
		var rect = {
			x: 0,
			y: 0,
			w: 4,
			h: 4
		};
		batch.quad(whiteAtlas, rect, sx(-HALF_W) - 8, sy(WALL_TOP), 8, WALL_TOP * PPM, wallCol);
		batch.quad(whiteAtlas, rect, sx(HALF_W), sy(WALL_TOP), 8, WALL_TOP * PPM, wallCol);
		batch.quad(whiteAtlas, rect, sx(-HALF_W) - 8, FLOOR_Y, HALF_W * 2 * PPM + 16, 8, wallCol);

		// ゲームオーバー線
		var lineBlink = over ? 1.0 : 0.25 + 0.15 * Math.sin(t * 4.0);
		batch.quad(whiteAtlas, rect, sx(-HALF_W), sy(LINE_Y), HALF_W * 2 * PPM, 2, {
			r: 0.9,
			g: 0.3,
			b: 0.3,
			a: lineBlink
		});

		// 玉 (sprite は本体、上に mesh グリフ)
		for (ball in balls) {
			var r = RADII[ball.level] * PPM;
			var c = COLORS[ball.level];
			batch.sprite(circleAtlas, {
				x: 0,
				y: 0,
				w: 64,
				h: 64
			}, sx(ball.x), sy(ball.y), r * 2, r * 2, 0.0, c);
		}

		// 投下プレビュー
		if (!over) {
			var r = dropR * PPM;
			var c = COLORS[nextLevel];
			batch.sprite(circleAtlas, {
				x: 0,
				y: 0,
				w: 64,
				h: 64
			}, sx(dropX), sy(DROP_Y), r * 2, r * 2, 0.0, {
				r: c.r,
				g: c.g,
				b: c.b,
				a: 0.5 + 0.2 * Math.sin(t * 6.0)
			});
		}

		// HUD (bitmap 小サイズレジーム)
		hud.draw(batch, "スコア " + score, 12, 26);
		hud.draw(batch, "ベスト " + best, 12, 50, {
			r: 0.8,
			g: 0.8,
			b: 0.8,
			a: 0.8
		});
		hud.draw(batch, "いろはにほへと", 500, 26, {
			r: 0.7,
			g: 0.7,
			b: 0.8,
			a: 0.9
		}, 0.8);

		batch.flush();

		// mesh グリフ (拡大レジーム): 玉の文字は物理の回転ごと描く
		var ink:Color = {
			r: 0.12,
			g: 0.10,
			b: 0.14,
			a: 0.9
		};
		for (ball in balls) {
			var cp = NativeUtf8.codepoint(CHARS[ball.level], 1);
			drawGlyph(cp, sx(ball.x), sy(ball.y), RADII[ball.level] * PPM * 1.3, ball.angle, ink, true);
		}
		if (!over)
			drawGlyph(NativeUtf8.codepoint(CHARS[nextLevel], 1), sx(dropX), sy(DROP_Y), dropR * PPM * 1.3, 0.0, {
				r: ink.r,
				g: ink.g,
				b: ink.b,
				a: 0.6
			}, true);

		if (over) {
			// 帯とメッセージは玉の上に重ねたいので別 batch で flush を分ける
			overlay.begin();
			overlay.quad(whiteAtlas, rect, 0, 108, W, 132, {
				r: 0.05,
				g: 0.04,
				b: 0.07,
				a: 0.85
			});
			var msg = "クリックでもういちど";
			hud.draw(overlay, msg, CX - hud.width(msg) * 0.5, 222);
			overlay.flush();
			drawGlyphText("おしまい", CX, 190, 64, {
				r: 0.95,
				g: 0.92,
				b: 0.85,
				a: 1.0
			});
		}

		Gfx.endPass();
	}
}
