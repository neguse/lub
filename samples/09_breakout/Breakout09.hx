import lubx.Boot;
import lubx.FixedStep;
import lub.Gfx;
import lub.Io;
import lub.Input;

// 2D Breakout: paddle, ball, bricks. Stateful gameplay; key_down driven.
// Module-level Lua locals are mirrored as static vars. During capture
// (offscreen / no input), state is deterministic from reset_game() init.
class Breakout09 {
	static inline var DT:Float = 1.0 / 60.0;
	static inline var STRIDE:Int = 6; // pos.xy + color.rgba

	static inline var COLS:Int = 11;
	static inline var ROWS:Int = 6;
	static inline var BRICK_GAP_X:Float = 0.018;
	static inline var BRICK_GAP_Y:Float = 0.018;
	static inline var BRICK_LEFT:Float = -0.88;
	static inline var BRICK_RIGHT:Float = 0.88;
	static inline var BRICK_TOP:Float = 0.76;
	static inline var BRICK_H:Float = 0.06;
	// BRICK_W computed at runtime to avoid Haxe inline-eval differences.
	static var BRICK_W:Float = (BRICK_RIGHT - BRICK_LEFT - BRICK_GAP_X * (COLS - 1)) / COLS;

	static inline var PADDLE_Y:Float = -0.78;
	static inline var PADDLE_W:Float = 0.34;
	static inline var PADDLE_H:Float = 0.045;
	static inline var PADDLE_SPEED:Float = 1.55;

	static inline var BALL_R:Float = 0.026;
	static inline var BALL_SPEED_X:Float = 0.55;
	static inline var BALL_SPEED_Y:Float = 0.83;

	static var rowColors:Array<Array<Float>> = [
		[0.93, 0.23, 0.25, 1.0],
		[0.96, 0.62, 0.16, 1.0],
		[0.98, 0.88, 0.24, 1.0],
		[0.22, 0.72, 0.43, 1.0],
		[0.14, 0.63, 0.86, 1.0],
		[0.55, 0.42, 0.86, 1.0],
	];

	static var bricks:Array<Dynamic> = [];
	static var paddleX:Float = 0;
	static var paddlePrevX:Float = 0;
	static var ballX:Float = 0;
	static var ballY:Float = 0;
	static var ballVx:Float = BALL_SPEED_X;
	static var ballVy:Float = BALL_SPEED_Y;
	static var ballStuck:Bool = true;
	static var lives:Int = 3;
	static var score:Int = 0;
	static var launchTimer:Float = 0;
	static var step = new FixedStep();
	static var initialized:Bool = false;

	public static function main() {}

	public static function onInit() {
		Boot.config({});
		resetGame();
	}

	static function clamp(v:Float, lo:Float, hi:Float):Float {
		if (v < lo)
			return lo;
		if (v > hi)
			return hi;
		return v;
	}

	static function resetBricks() {
		bricks = [];
		for (row in 1...ROWS + 1) {
			var y1 = BRICK_TOP - (row - 1) * (BRICK_H + BRICK_GAP_Y);
			var y0 = y1 - BRICK_H;
			for (col in 1...COLS + 1) {
				var x0 = BRICK_LEFT + (col - 1) * (BRICK_W + BRICK_GAP_X);
				bricks.push({
					x0: x0,
					y0: y0,
					x1: x0 + BRICK_W,
					y1: y1,
					row: row,
					alive: true,
				});
			}
		}
	}

	static function resetBall() {
		ballX = paddleX;
		ballY = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.01;
		ballVx = BALL_SPEED_X;
		ballVy = BALL_SPEED_Y;
		ballStuck = true;
		launchTimer = 0;
	}

	static function resetGame() {
		paddleX = 0;
		paddlePrevX = 0;
		lives = 3;
		score = 0;
		resetBricks();
		resetBall();
	}

	static function launchBall() {
		if (!ballStuck)
			return;
		ballStuck = false;
		ballVx = (paddleX >= 0) ? -BALL_SPEED_X : BALL_SPEED_X;
		ballVy = BALL_SPEED_Y;
	}

	static function aliveBricks():Int {
		var n = 0;
		for (b in bricks) {
			if (b.alive)
				n = n + 1;
		}
		return n;
	}

	static function circleHitsRect(cx:Float, cy:Float, r:Float, rect:Dynamic):Bool {
		return cx + r > rect.x0 && cx - r < rect.x1 && cy + r > rect.y0 && cy - r < rect.y1;
	}

	static function bounceFromRect(rect:Dynamic) {
		var left = ballX + BALL_R - rect.x0;
		var right = rect.x1 - (ballX - BALL_R);
		var bottom = ballY + BALL_R - rect.y0;
		var top = rect.y1 - (ballY - BALL_R);
		var m = Math.min(Math.min(left, right), Math.min(bottom, top));

		if (m == left) {
			ballX = rect.x0 - BALL_R;
			ballVx = -Math.abs(ballVx);
		} else if (m == right) {
			ballX = rect.x1 + BALL_R;
			ballVx = Math.abs(ballVx);
		} else if (m == bottom) {
			ballY = rect.y0 - BALL_R;
			ballVy = -Math.abs(ballVy);
		} else {
			ballY = rect.y1 + BALL_R;
			ballVy = Math.abs(ballVy);
		}
	}

	static function updateGame(resetPressed:Bool) {
		if (resetPressed) {
			resetGame();
		}

		var move = 0;
		if (Input.keyDown("left") || Input.keyDown("a"))
			move = move - 1;
		if (Input.keyDown("right") || Input.keyDown("d"))
			move = move + 1;

		paddlePrevX = paddleX;
		paddleX = clamp(paddleX + move * PADDLE_SPEED * DT, -1 + PADDLE_W * 0.5 + 0.03, 1 - PADDLE_W * 0.5 - 0.03);

		if (ballStuck) {
			ballX = paddleX;
			ballY = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.01;
			launchTimer = launchTimer + DT;
			if (Input.keyDown("space") || launchTimer > 1.0) {
				launchBall();
			}
			return;
		}

		ballX = ballX + ballVx * DT;
		ballY = ballY + ballVy * DT;

		if (ballX - BALL_R < -0.96) {
			ballX = -0.96 + BALL_R;
			ballVx = Math.abs(ballVx);
		} else if (ballX + BALL_R > 0.96) {
			ballX = 0.96 - BALL_R;
			ballVx = -Math.abs(ballVx);
		}
		if (ballY + BALL_R > 0.90) {
			ballY = 0.90 - BALL_R;
			ballVy = -Math.abs(ballVy);
		}

		var paddleRect = {
			x0: paddleX - PADDLE_W * 0.5,
			y0: PADDLE_Y - PADDLE_H * 0.5,
			x1: paddleX + PADDLE_W * 0.5,
			y1: PADDLE_Y + PADDLE_H * 0.5,
		};
		if (ballVy < 0 && circleHitsRect(ballX, ballY, BALL_R, paddleRect)) {
			var hit = (ballX - paddleX) / (PADDLE_W * 0.5);
			ballY = paddleRect.y1 + BALL_R;
			ballVx = clamp(hit * 0.85 + (paddleX - paddlePrevX) * 2.5, -0.95, 0.95);
			ballVy = Math.abs(ballVy);
		}

		for (b in bricks) {
			if (b.alive && circleHitsRect(ballX, ballY, BALL_R, b)) {
				b.alive = false;
				score = score + 1;
				bounceFromRect(b);
				break;
			}
		}

		if (ballY + BALL_R < -1.0) {
			lives = lives - 1;
			if (lives <= 0) {
				resetGame();
			} else {
				resetBall();
			}
		} else if (aliveBricks() == 0) {
			resetGame();
		}
	}

	static function pushVertex(out:Array<Float>, x:Float, y:Float, c:Array<Float>) {
		out.push(x);
		out.push(y);
		out.push(c[0]);
		out.push(c[1]);
		out.push(c[2]);
		out.push(c[3]);
	}

	static function addRect(out:Array<Float>, x0:Float, y0:Float, x1:Float, y1:Float, c:Array<Float>) {
		pushVertex(out, x0, y0, c);
		pushVertex(out, x1, y0, c);
		pushVertex(out, x1, y1, c);
		pushVertex(out, x0, y0, c);
		pushVertex(out, x1, y1, c);
		pushVertex(out, x0, y1, c);
	}

	static function addCircle(out:Array<Float>, cx:Float, cy:Float, r:Float, c:Array<Float>) {
		var segments = 20;
		for (i in 0...segments) {
			var a0 = i / segments * Math.PI * 2;
			var a1 = (i + 1) / segments * Math.PI * 2;
			pushVertex(out, cx, cy, c);
			pushVertex(out, cx + Math.cos(a0) * r, cy + Math.sin(a0) * r, c);
			pushVertex(out, cx + Math.cos(a1) * r, cy + Math.sin(a1) * r, c);
		}
	}

	static function buildVertices():Array<Float> {
		var out:Array<Float> = [];
		var rail:Array<Float> = [0.18, 0.22, 0.30, 1.0];
		var paddleColor:Array<Float> = [0.95, 0.96, 0.88, 1.0];
		var ballColor:Array<Float> = [1.0, 0.98, 0.78, 1.0];
		var liveColor:Array<Float> = [0.92, 0.34, 0.36, 1.0];
		var scoreColor:Array<Float> = [0.30, 0.82, 0.65, 1.0];
		var highlight:Array<Float> = [1.0, 1.0, 1.0, 0.20];

		addRect(out, -0.99, -0.98, -0.96, 0.93, rail);
		addRect(out, 0.96, -0.98, 0.99, 0.93, rail);
		addRect(out, -0.99, 0.90, 0.99, 0.93, rail);

		for (b in bricks) {
			if (b.alive) {
				var rowIdx:Int = b.row;
				var c:Array<Float> = rowColors[rowIdx - 1];
				addRect(out, b.x0, b.y0, b.x1, b.y1, c);
				addRect(out, b.x0 + 0.006, b.y1 - 0.012, b.x1 - 0.006, b.y1 - 0.006, highlight);
			}
		}

		addRect(out, paddleX - PADDLE_W * 0.5, PADDLE_Y - PADDLE_H * 0.5, paddleX + PADDLE_W * 0.5, PADDLE_Y + PADDLE_H * 0.5, paddleColor);
		addCircle(out, ballX, ballY, BALL_R, ballColor);

		for (i in 1...lives + 1) {
			addCircle(out, -0.86 + (i - 1) * 0.07, -0.92, 0.018, liveColor);
		}
		var scoreShow = score < 12 ? score : 12;
		for (i in 1...scoreShow + 1) {
			var x = 0.48 + (i - 1) * 0.035;
			addRect(out, x, -0.94, x + 0.018, -0.90, scoreColor);
		}

		return out;
	}

	public static function onFrame(dt:Float) {
		step.frame(dt, _ -> updateGame(step.keyPressed("r")));

		var vsR = Io.loadText("samples/09_breakout/data/09_breakout.vs.slang");
		var fsR = Io.loadText("samples/09_breakout/data/09_breakout.fs.slang");
		var vs:String = vsR.text;
		var vsv:Int = vsR.version;
		var fs:String = fsR.text;
		var fsv:Int = fsR.version;
		if (vs == null || fs == null)
			return;

		var verts = buildVertices();
		var shader = Gfx.useShader("breakout_shader", vs, fs, vsv * 31 + fsv);
		var vbuf = Gfx.useBuffer("breakout_verts", Gfx.VERTEX, lua.Table.fromArray(verts));

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.035, 0.045, 0.065, 1.0])
		});
		Gfx.draw(Std.int(verts.length / STRIDE), {verts: vbuf}, {
			shader: shader,
			depth: false,
			cull: Gfx.NONE,
			blend: Gfx.ALPHA
		});
		Gfx.endPass();
	}
}
