import lub.Lub;
import lub.Gfx;
import lub.Io;
import lub.Input;
import lub.Math;

// 3D Breakout: paddle, ball, bricks rendered as boxes/sphere with an MVP.
// MVP and camera oscillation derive from frame-deterministic camera_t.
class Breakout3d10 {
	static inline var DT:Float = 1.0 / 60.0;
	static inline var STRIDE:Int = 7; // pos.xyz + color.rgba

	static inline var COLS:Int = 9;
	static inline var ROWS:Int = 5;
	static inline var BRICK_GAP_X:Float = 0.035;
	static inline var BRICK_GAP_Y:Float = 0.03;
	static inline var BRICK_LEFT:Float = -0.83;
	static inline var BRICK_RIGHT:Float = 0.83;
	static inline var BRICK_TOP:Float = 0.70;
	static inline var BRICK_H:Float = 0.075;
	static inline var BRICK_D:Float = 0.16;
	static var BRICK_W:Float = (BRICK_RIGHT - BRICK_LEFT - BRICK_GAP_X * (COLS - 1)) / COLS;

	static inline var PADDLE_Y:Float = -0.76;
	static inline var PADDLE_W:Float = 0.38;
	static inline var PADDLE_H:Float = 0.055;
	static inline var PADDLE_D:Float = 0.24;
	static inline var PADDLE_SPEED:Float = 1.55;

	static inline var BALL_R:Float = 0.035;
	static inline var BALL_SPEED_X:Float = 0.58;
	static inline var BALL_SPEED_Y:Float = 0.85;

	static var rowColors:Array<Array<Float>> = [
		[0.95, 0.24, 0.28, 1.0],
		[0.98, 0.55, 0.15, 1.0],
		[0.98, 0.86, 0.22, 1.0],
		[0.22, 0.70, 0.40, 1.0],
		[0.16, 0.58, 0.88, 1.0],
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
	static var resetWasDown:Bool = false;
	static var meshVersion:Int = 0;
	static var cameraT:Float = 0;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
		resetGame();
	}

	static function shade(c:Array<Float>, k:Float):Array<Float> {
		return [
			MathUtil.clamp(c[0] * k, 0, 1),
			MathUtil.clamp(c[1] * k, 0, 1),
			MathUtil.clamp(c[2] * k, 0, 1),
			c.length > 3 ? c[3] : 1.0,
		];
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
		ballY = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.015;
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

	static function updateGame() {
		var resetDown = Input.keyDown("r");
		if (resetDown && !resetWasDown)
			resetGame();
		resetWasDown = resetDown;

		var move = 0;
		if (Input.keyDown("left") || Input.keyDown("a"))
			move = move - 1;
		if (Input.keyDown("right") || Input.keyDown("d"))
			move = move + 1;

		paddlePrevX = paddleX;
		paddleX = MathUtil.clamp(paddleX + move * PADDLE_SPEED * DT, -1 + PADDLE_W * 0.5 + 0.05, 1 - PADDLE_W * 0.5 - 0.05);

		if (ballStuck) {
			ballX = paddleX;
			ballY = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.015;
			launchTimer = launchTimer + DT;
			if (Input.keyDown("space") || launchTimer > 1.0)
				launchBall();
			return;
		}

		ballX = ballX + ballVx * DT;
		ballY = ballY + ballVy * DT;

		if (ballX - BALL_R < -0.95) {
			ballX = -0.95 + BALL_R;
			ballVx = Math.abs(ballVx);
		} else if (ballX + BALL_R > 0.95) {
			ballX = 0.95 - BALL_R;
			ballVx = -Math.abs(ballVx);
		}
		if (ballY + BALL_R > 0.88) {
			ballY = 0.88 - BALL_R;
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
			ballVx = MathUtil.clamp(hit * 0.9 + (paddleX - paddlePrevX) * 2.5, -0.98, 0.98);
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
			if (lives <= 0)
				resetGame()
			else
				resetBall();
		} else if (aliveBricks() == 0) {
			resetGame();
		}
	}

	static inline function pushV(out:Array<Float>, x:Float, y:Float, z:Float, c:Array<Float>) {
		out.push(x);
		out.push(y);
		out.push(z);
		out.push(c[0]);
		out.push(c[1]);
		out.push(c[2]);
		out.push(c[3]);
	}

	static function quad(out:Array<Float>, a:Array<Float>, b:Array<Float>, c:Array<Float>, d:Array<Float>, col:Array<Float>) {
		pushV(out, a[0], a[1], a[2], col);
		pushV(out, b[0], b[1], b[2], col);
		pushV(out, c[0], c[1], c[2], col);
		pushV(out, a[0], a[1], a[2], col);
		pushV(out, c[0], c[1], c[2], col);
		pushV(out, d[0], d[1], d[2], col);
	}

	static function addBox(out:Array<Float>, cx:Float, cy:Float, cz:Float, sx:Float, sy:Float, sz:Float, base:Array<Float>) {
		var x0 = cx - sx * 0.5;
		var x1 = cx + sx * 0.5;
		var y0 = cy - sy * 0.5;
		var y1 = cy + sy * 0.5;
		var z0 = cz - sz * 0.5;
		var z1 = cz + sz * 0.5;

		var p000:Array<Float> = [x0, y0, z0];
		var p100:Array<Float> = [x1, y0, z0];
		var p010:Array<Float> = [x0, y1, z0];
		var p110:Array<Float> = [x1, y1, z0];
		var p001:Array<Float> = [x0, y0, z1];
		var p101:Array<Float> = [x1, y0, z1];
		var p011:Array<Float> = [x0, y1, z1];
		var p111:Array<Float> = [x1, y1, z1];

		quad(out, p000, p100, p110, p010, shade(base, 1.05));
		quad(out, p101, p001, p011, p111, shade(base, 0.58));
		quad(out, p001, p000, p010, p011, shade(base, 0.72));
		quad(out, p100, p101, p111, p110, shade(base, 0.82));
		quad(out, p010, p110, p111, p011, shade(base, 1.22));
		quad(out, p001, p101, p100, p000, shade(base, 0.48));
	}

	static function spherePoint(cx:Float, cy:Float, cz:Float, r:Float, u:Float, vv:Float):Array<Float> {
		var cv = Math.cos(vv);
		return [
			cx + Math.cos(u) * cv * r,
			cy + Math.sin(vv) * r,
			cz + Math.sin(u) * cv * r,
			Math.cos(u) * cv,
			Math.sin(vv),
			Math.sin(u) * cv,
		];
	}

	static function sphereCol(base:Array<Float>, pt:Array<Float>):Array<Float> {
		var ny = pt[4] > 0 ? pt[4] : 0;
		var nzNeg = -pt[5] > 0 ? -pt[5] : 0;
		return shade(base, 0.70 + ny * 0.25 + nzNeg * 0.18);
	}

	static function addSphere(out:Array<Float>, cx:Float, cy:Float, cz:Float, r:Float, base:Array<Float>) {
		var rings = 8;
		var segs = 16;
		for (ring in 0...rings) {
			var v0 = -Math.PI * 0.5 + ring / rings * Math.PI;
			var v1 = -Math.PI * 0.5 + (ring + 1) / rings * Math.PI;
			for (seg in 0...segs) {
				var u0 = seg / segs * Math.PI * 2;
				var u1 = (seg + 1) / segs * Math.PI * 2;

				var a = spherePoint(cx, cy, cz, r, u0, v0);
				var b = spherePoint(cx, cy, cz, r, u1, v0);
				var c = spherePoint(cx, cy, cz, r, u1, v1);
				var d = spherePoint(cx, cy, cz, r, u0, v1);
				pushV(out, a[0], a[1], a[2], sphereCol(base, a));
				pushV(out, b[0], b[1], b[2], sphereCol(base, b));
				pushV(out, c[0], c[1], c[2], sphereCol(base, c));
				pushV(out, a[0], a[1], a[2], sphereCol(base, a));
				pushV(out, c[0], c[1], c[2], sphereCol(base, c));
				pushV(out, d[0], d[1], d[2], sphereCol(base, d));
			}
		}
	}

	static function buildVertices():Array<Float> {
		var out:Array<Float> = [];
		addBox(out, 0, -0.04, 0.13, 2.05, 1.95, 0.04, [0.05, 0.07, 0.11, 1.0]);
		addBox(out, -1.02, -0.02, -0.02, 0.05, 1.92, 0.28, [0.22, 0.27, 0.36, 1.0]);
		addBox(out, 1.02, -0.02, -0.02, 0.05, 1.92, 0.28, [0.22, 0.27, 0.36, 1.0]);
		addBox(out, 0, 0.93, -0.02, 2.09, 0.05, 0.28, [0.22, 0.27, 0.36, 1.0]);

		for (b in bricks) {
			if (b.alive) {
				var rowIdx:Int = b.row;
				addBox(out, (b.x0 + b.x1) * 0.5, (b.y0 + b.y1) * 0.5, -0.03, b.x1 - b.x0, b.y1 - b.y0, BRICK_D, rowColors[rowIdx - 1]);
			}
		}

		addBox(out, paddleX, PADDLE_Y, -0.10, PADDLE_W, PADDLE_H, PADDLE_D, [0.94, 0.96, 0.86, 1.0]);
		addSphere(out, ballX, ballY, -0.20, BALL_R, [1.0, 0.95, 0.65, 1.0]);

		for (i in 1...lives + 1) {
			addSphere(out, -0.88 + (i - 1) * 0.08, -0.94, -0.15, 0.025, [0.95, 0.32, 0.36, 1.0]);
		}
		var scoreShow = score < 12 ? score : 12;
		for (i in 1...scoreShow + 1) {
			addBox(out, 0.48 + (i - 1) * 0.04, -0.94, -0.12, 0.022, 0.055, 0.04, [0.26, 0.82, 0.62, 1.0]);
		}

		return out;
	}

	static function makeMvp(t:Float):lua.Table<Int, Float> {
		var yaw = -0.22 + Math.sin(t * 0.35) * 0.025;
		var pitch = -0.18;
		var ry = Mat4.rotateY(-yaw);
		var rx = Mat4.rotateX(-pitch);
		var view = Mat4.translate(new Vec3(0, -0.02, 3.15));
		// proj: perspective with focal length f=2.05 directly, aspect=16/9, near=0.1, far=40
		var f = 2.05;
		var aspect = 16.0 / 9.0;
		var nz = 0.1;
		var fz = 40.0;
		var proj = Mat4.zero();
		proj.m[0] = f / aspect;
		proj.m[5] = f;
		proj.m[10] = fz / (fz - nz);
		proj.m[11] = -fz * nz / (fz - nz);
		proj.m[14] = 1.0;
		return lua.Table.fromArray(proj.mul(view.mul(rx.mul(ry))).m);
	}

	public static function onFrame() {
		cameraT = cameraT + DT;
		updateGame();

		var vsR = Io.loadText("samples/10_breakout3d/data/10_breakout3d.vs.slang");
		var fsR = Io.loadText("samples/10_breakout3d/data/10_breakout3d.fs.slang");
		var vs:String = vsR.text;
		var vsv:Int = vsR.version;
		var fs:String = fsR.text;
		var fsv:Int = fsR.version;
		if (vs == null || fs == null)
			return;

		var verts = buildVertices();
		meshVersion = meshVersion + 1;
		var shader = Gfx.useShader("breakout3d_shader", vs, fs, vsv ^ fsv);
		var vbuf = Gfx.useBuffer("breakout3d_verts", Gfx.VERTEX, lua.Table.fromArray(verts), meshVersion);

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.025, 0.032, 0.048, 1.0])
		});
		Gfx.draw(Std.int(verts.length / STRIDE), {verts: vbuf, uniforms: {mvp: makeMvp(cameraT)}}, {
			shader: shader,
			depth: true,
			depth_write: true,
			cull: Gfx.NONE
		});
		Gfx.endPass();
	}
}
