import lub.Gfx;
import lub.Io;
import lubx.Boot;
import lub.Phys2d;
import lub.Phys2d.Pose;
import lua.Table;

class Box2d16 {
	static inline var DT:Float = 1.0 / 60.0;
	static inline var MAX_STEPS:Int = 8;
	static inline var PPM_X:Float = 4.0;
	static inline var PPM_Y:Float = 2.7;
	static var meshVersion:Int = 0;
	static var contactFlash:Int = 0;
	static var accumulator:Float = 0.0;

	public static function main() {}

	public static function onInit() {
		Boot.config({width: 640, height: 360});
	}

	static function pushVertex(out:Array<Float>, x:Float, y:Float, r:Float, g:Float, b:Float, a:Float) {
		out.push(x / PPM_X);
		out.push(y / PPM_Y);
		out.push(0.0);
		out.push(r);
		out.push(g);
		out.push(b);
		out.push(a);
	}

	static function pushBox(out:Array<Float>, pose:Pose, hx:Float, hy:Float, color:Array<Float>) {
		var c = Math.cos(pose.angle);
		var s = Math.sin(pose.angle);
		var corners = [[-hx, -hy], [hx, -hy], [hx, hy], [-hx, hy],];
		var wx:Array<Float> = [];
		var wy:Array<Float> = [];
		for (p in corners) {
			wx.push(pose.x + c * p[0] - s * p[1]);
			wy.push(pose.y + s * p[0] + c * p[1]);
		}
		var idx = [0, 1, 2, 0, 2, 3];
		for (i in idx) {
			pushVertex(out, wx[i], wy[i], color[0], color[1], color[2], color[3]);
		}
	}

	static function simulate(world:lub.Phys2d.WorldRef) {
		Phys2d.begin(world);

		var ground = Phys2d.body(world, "ground", {
			type: Phys2d.STATIC,
			initial: {x: 0.0, y: -1.55},
		});
		Phys2d.box(ground, "floor", {
			hx: 3.4,
			hy: 0.18,
			density: 0.0,
			friction: 0.85,
			contact: true,
		});

		for (i in 0...4) {
			var b = Phys2d.body(world, "box:" + i, {
				type: Phys2d.DYNAMIC,
				initial: {
					x: (i % 2 == 0) ? -0.18 : 0.18,
					y: -0.95 + i * 0.58,
					angle: (i - 1) * 0.08,
				},
			});
			Phys2d.box(b, "solid", {
				hx: 0.26,
				hy: 0.26,
				density: 1.0,
				friction: 0.65,
				contact: true,
			});
		}

		Phys2d.step(world, DT);

		var contacts:Dynamic = Phys2d.contacts(world, "begin");
		if (contacts[1] != null)
			contactFlash = 12;
		if (contactFlash > 0)
			contactFlash--;
	}

	public static function onFrame(dt:Float) {
		var world = Phys2d.world("box2d16", {
			gravity: {x: 0.0, y: -10.0},
			fixedDt: DT,
			substeps: 4,
			maxSteps: 1,
		});
		accumulator += Math.max(0.0, Math.min(dt, DT * MAX_STEPS));
		var steps = 0;
		while (accumulator + 1e-9 >= DT && steps < MAX_STEPS) {
			simulate(world);
			accumulator -= DT;
			steps++;
		}
		if (accumulator < 0.0)
			accumulator = 0.0;
		if (accumulator >= DT)
			accumulator %= DT;

		var verts:Array<Float> = [];
		var groundPose = Phys2d.pose(world, "ground");
		if (groundPose == null)
			return;
		pushBox(verts, groundPose, 3.4, 0.18, [0.28, 0.33, 0.36, 1.0]);
		for (i in 0...4) {
			var pose = Phys2d.pose(world, "box:" + i);
			if (pose == null)
				continue;
			var hot = contactFlash > 0 ? 0.12 : 0.0;
			pushBox(verts, pose, 0.26, 0.26, [0.20 + hot, 0.62, 0.88, 1.0]);
		}

		meshVersion++;
		var vs = Io.loadText("samples/16_box2d/data/16_color.vs.slang");
		var fs = Io.loadText("samples/16_box2d/data/16_color.fs.slang");
		if (vs.text == null || fs.text == null)
			return;
		var shader = Gfx.useShader("box2d16_color", vs.text, fs.text, vs.version * 31 + fs.version);
		var mesh = Gfx.useBuffer("box2d16_mesh", Gfx.VERTEX, Table.fromArray(verts), meshVersion);

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: Table.fromArray([0.03, 0.04, 0.055, 1.0])
		});
		Gfx.draw(Std.int(verts.length / 7), {verts: mesh}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}
}
