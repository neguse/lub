import lub.Gfx;
import lub.Lub;
import lub.Profiler;
import lubx.Atlas;
import lubx.Boot;
import lubx.Color;
import lubx.FpsMeter;
import lubx.Rect;
import lubx.SpriteBatch;
import lua.Lua;

class BenchSprite13 {
	public var x:Float;
	public var y:Float;
	public var timeInit:Float;
	public var timeLeft:Float;
	public var r:Float;
	public var dr:Float;
	public var cr:Float;
	public var sr:Float;
	public var stepCr:Float;
	public var stepSr:Float;
	public var tintS:Float;
	public var tintC:Float;
	public var tintStepS:Float;
	public var tintStepC:Float;
	public var scale:Float;
	public var kind:Int;

	public function new(x:Float, y:Float, timeInit:Float, r:Float, dr:Float, scale:Float, kind:Int) {
		this.x = x;
		this.y = y;
		this.timeInit = timeInit;
		this.timeLeft = timeInit;
		this.r = r;
		this.dr = dr;
		this.cr = Math.cos(r);
		this.sr = Math.sin(r);
		var step = dr / 60.0;
		this.stepCr = Math.cos(step);
		this.stepSr = Math.sin(step);
		var phase = r * 3.0;
		this.tintS = Math.sin(phase);
		this.tintC = Math.cos(phase);
		var tintStep = step * 3.0;
		this.tintStepS = Math.sin(tintStep);
		this.tintStepC = Math.cos(tintStep);
		this.scale = scale;
		this.kind = kind;
	}

	public function update(dt:Float) {
		r = r + dt * dr;
		timeLeft = timeLeft - dt;
		var nextCr = cr * stepCr - sr * stepSr;
		sr = cr * stepSr + sr * stepCr;
		cr = nextCr;
		var nextTintS = tintS * tintStepC + tintC * tintStepS;
		tintC = tintC * tintStepC - tintS * tintStepS;
		tintS = nextTintS;
	}

	public inline function dead():Bool {
		return timeLeft < 0;
	}
}

class Sprites13 {
	static inline var W:Int = 640;
	static inline var H:Int = 480;
	static inline var DT:Float = 1.0 / 60.0;
	static inline var MAX_STEPS:Int = 8;
	static inline var TEX_W:Int = 80;
	static inline var TEX_H:Int = 16;
	static inline var CELL:Int = 16;
	static inline var SQRT3_HALF:Float = 0.8660254037844386;

	static var sprites:Array<BenchSprite13> = [];
	static var batch:SpriteBatch = null;
	static var atlas:Atlas = null;
	static var meter:FpsMeter = null;
	static var tick:Int = 0;
	static var rng:Float = 305419896.0;
	static var timeMultiply:Float = 4.0;
	static var targetFps:Float = 60.0;
	static var maxSprites:Int = 200000;
	static var burst:Int = 1;
	static var scoreFrame:Int = 0;
	static var scorePrinted:Bool = false;
	static var useInstancing:Bool = true;
	static var accumulator:Float = 0.0;

	static var spriteRects:Array<Rect> = [
		{
			x: 0,
			y: 0,
			w: CELL,
			h: CELL
		},
		{
			x: 16,
			y: 0,
			w: CELL,
			h: CELL
		},
		{
			x: 32,
			y: 0,
			w: CELL,
			h: CELL
		},
		{
			x: 48,
			y: 0,
			w: CELL,
			h: CELL
		},
	];
	static var whiteRect:Rect = {
		x: 64,
		y: 0,
		w: 1,
		h: 1
	};

	public static function main() {}

	public static function onInit() {
		Boot.config({width: W, height: H});

		targetFps = envFloat("LUB_SPRITE_TARGET_FPS", 60.0);
		maxSprites = envInt("LUB_SPRITE_MAX", 200000);
		burst = envInt("LUB_SPRITE_BURST", 1);
		scoreFrame = envInt("LUB_SPRITE_SCORE_FRAME", 0);
		useInstancing = envBool("LUB_SPRITE_INSTANCED", true);
		if (burst < 1)
			burst = 1;

		batch = new SpriteBatch(W, H, "sprites13_shader", "sprites13_batch", useInstancing);
		atlas = Atlas.fromPixels("sprites13_atlas", TEX_W, TEX_H, buildAtlas(), 1, {filter: Gfx.LINEAR, wrap: Gfx.CLAMP});
		meter = new FpsMeter(targetFps);
	}

	static function envFloat(name:String, fallback:Float):Float {
		var s = lua.Os.getenv(name);
		if (s == null)
			return fallback;
		var v = Std.parseFloat(s);
		return Math.isNaN(v) ? fallback : v;
	}

	static function envInt(name:String, fallback:Int):Int {
		var s = lua.Os.getenv(name);
		if (s == null)
			return fallback;
		var v = Std.parseInt(s);
		return v == null ? fallback : v;
	}

	static function envBool(name:String, fallback:Bool):Bool {
		var s = lua.Os.getenv(name);
		if (s == null)
			return fallback;
		return s != "0" && s != "false" && s != "FALSE";
	}

	static function rand01():Float {
		rng = rng * 1664525.0 + 1013904223.0;
		rng = rng - Math.floor(rng / 4294967296.0) * 4294967296.0;
		return rng / 4294967296.0;
	}

	static function spawnOne() {
		var life = rand01() * timeMultiply + 2.0;
		sprites.push(new BenchSprite13(rand01(), rand01(), life, rand01() * Math.PI * 2.0, rand01() * Math.PI * 2.0, rand01() * 120.0 + 80.0,
			Std.int(rand01() * spriteRects.length)));
	}

	static function updateSprites(fps:Float) {
		tick = tick + 1;

		var write = 0;
		for (s in sprites) {
			s.update(DT);
			if (!s.dead()) {
				sprites[write] = s;
				write = write + 1;
			}
		}
		sprites.resize(write);

		var spawn = false;
		if (sprites.length < maxSprites) {
			if (fps > targetFps) {
				spawn = true;
				timeMultiply = timeMultiply + 0.7 * DT;
			} else if (fps > targetFps * 0.5 && tick % 2 == 0) {
				spawn = true;
				timeMultiply = timeMultiply + 0.2 * DT;
			} else if (fps > targetFps * 0.25 && tick % 4 == 0) {
				spawn = true;
				timeMultiply = timeMultiply - 0.3 * DT;
			}
		}

		if (timeMultiply < 1.0)
			timeMultiply = 1.0;

		if (spawn) {
			var n = burst;
			while (n > 0 && sprites.length < maxSprites) {
				spawnOne();
				n = n - 1;
			}
		}
	}

	static inline function setPx(px:Array<Int>, x:Int, y:Int, r:Int, g:Int, b:Int, a:Int) {
		var i = (y * TEX_W + x) * 4;
		px[i] = r;
		px[i + 1] = g;
		px[i + 2] = b;
		px[i + 3] = a;
	}

	static function buildAtlas():Array<Int> {
		var px = [for (_ in 0...TEX_W * TEX_H * 4) 0];
		for (y in 0...CELL) {
			for (x in 0...CELL) {
				var nx = (x + 0.5 - CELL * 0.5) / (CELL * 0.5);
				var ny = (y + 0.5 - CELL * 0.5) / (CELL * 0.5);
				var d = Math.sqrt(nx * nx + ny * ny);
				if (d < 0.92)
					setPx(px, x, y, 255, 255, 255, 255);
				if (Math.abs(nx) + Math.abs(ny) < 1.1)
					setPx(px, 16 + x, y, 255, 255, 255, 255);
				if (Math.max(Math.abs(nx), Math.abs(ny)) < 0.78)
					setPx(px, 32 + x, y, 255, 255, 255, 255);
				if (Math.abs(nx) < 0.24 || Math.abs(ny) < 0.24 || Math.abs(nx - ny) < 0.16 || Math.abs(nx + ny) < 0.16)
					setPx(px, 48 + x, y, 255, 255, 255, 255);
			}
		}
		setPx(px, whiteRect.x, whiteRect.y, 255, 255, 255, 255);
		return px;
	}

	static function drawSprites() {
		for (s in sprites) {
			var age = (s.timeInit - s.timeLeft) / s.timeInit;
			var pulse = Math.sin(Math.PI * age);
			if (pulse <= 0)
				continue;
			var size = pulse * s.scale;
			var a = pulse < 0.18 ? pulse / 0.18 : 1.0;
			var ts = s.tintS;
			var tc = s.tintC;
			var red = 0.58 + 0.42 * ts;
			var green = 0.58 + 0.42 * (-0.5 * ts + SQRT3_HALF * tc);
			var blue = 0.58 + 0.42 * (-0.5 * ts - SQRT3_HALF * tc);
			batch.spriteColor(atlas, spriteRects[s.kind], s.x * W, s.y * H, size, size, s.cr, s.sr, red, green, blue, a);
		}
	}

	static function glyphRows(ch:String):Array<Int> {
		return switch (ch) {
			case "0": [7, 5, 5, 5, 7];
			case "1": [2, 6, 2, 2, 7];
			case "2": [7, 1, 7, 4, 7];
			case "3": [7, 1, 7, 1, 7];
			case "4": [5, 5, 7, 1, 1];
			case "5": [7, 4, 7, 1, 7];
			case "6": [7, 4, 7, 5, 7];
			case "7": [7, 1, 2, 2, 2];
			case "8": [7, 5, 7, 5, 7];
			case "9": [7, 5, 7, 1, 7];
			case "A": [2, 5, 7, 5, 5];
			case "E": [7, 4, 6, 4, 7];
			case "F": [7, 4, 6, 4, 4];
			case "G": [7, 4, 5, 5, 7];
			case "I": [7, 2, 2, 2, 7];
			case "P": [6, 5, 6, 4, 4];
			case "R": [6, 5, 6, 5, 5];
			case "S": [7, 4, 7, 1, 7];
			case "T": [7, 2, 2, 2, 2];
			case ":": [0, 2, 0, 2, 0];
			default: [0, 0, 0, 0, 0];
		}
	}

	static function drawText(x:Int, y:Int, text:String, scale:Int, color:Color) {
		var cursor = x;
		for (i in 0...text.length) {
			var ch = text.charAt(i);
			var rows = glyphRows(ch);
			for (row in 0...5) {
				var bits = rows[row];
				for (col in 0...3) {
					if ((bits & (1 << (2 - col))) != 0)
						batch.quad(atlas, whiteRect, cursor + col * scale, y + row * scale, scale, scale, color);
				}
			}
			cursor = cursor + 4 * scale;
		}
	}

	static function drawHud(fps:Float) {
		batch.quad(atlas, whiteRect, 8, 8, 248, 46, {
			r: 0.0,
			g: 0.0,
			b: 0.0,
			a: 0.56
		});
		drawText(16, 16, "SPRITES:" + sprites.length, 3, {
			r: 0.90,
			g: 0.96,
			b: 1.0,
			a: 1.0
		});
		drawText(16, 36, "FPS:" + Std.int(fps + 0.5) + " TARGET:" + Std.int(targetFps + 0.5), 2, {
			r: 0.78,
			g: 1.0,
			b: 0.70,
			a: 1.0
		});
	}

	static function fpsText(fps:Float):String {
		return Std.string(Std.int(fps * 100.0 + 0.5) / 100.0);
	}

	static function maybePrintScore(fps:Float) {
		if (scoreFrame <= 0 || scorePrinted || tick < scoreFrame)
			return;
		scorePrinted = true;
		Lua.print("SPRITES13_SCORE frame=" + tick + " sprites=" + sprites.length + " fps=" + fpsText(fps) + " target=" + fpsText(targetFps)
			+ " time_multiply=" + fpsText(timeMultiply) + " burst=" + burst + " instanced=" + useInstancing);
		Lub.quit();
	}

	public static function onFrame(dt:Float) {
		var fps = meter.tick();
		Profiler.beginScope("sprites.update");
		if (scoreFrame > 0) {
			// The canonical score is intentionally one workload tick per rendered
			// frame. Interactive mode below uses a real-time 60 Hz simulation.
			updateSprites(fps);
		} else {
			accumulator += Math.max(0.0, Math.min(dt, DT * MAX_STEPS));
			var steps = 0;
			while (accumulator + 1e-9 >= DT && steps < MAX_STEPS) {
				updateSprites(fps);
				accumulator -= DT;
				steps++;
			}
			if (accumulator < 0.0)
				accumulator = 0.0;
			if (accumulator >= DT)
				accumulator %= DT;
		}
		Profiler.endScope("sprites.update");

		Profiler.beginScope("gfx.begin_pass");
		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.03, 0.035, 0.045, 1.0])
		});
		Profiler.endScope("gfx.begin_pass");

		batch.begin();
		Profiler.beginScope("sprites.draw");
		drawSprites();
		Profiler.endScope("sprites.draw");
		Profiler.beginScope("sprites.hud");
		drawHud(fps);
		Profiler.endScope("sprites.hud");
		Profiler.beginScope("batch.flush");
		batch.flush(Gfx.ALPHA);
		Profiler.endScope("batch.flush");
		Profiler.beginScope("gfx.end_pass");
		Gfx.endPass();
		Profiler.endScope("gfx.end_pass");
		maybePrintScore(fps);
	}
}
