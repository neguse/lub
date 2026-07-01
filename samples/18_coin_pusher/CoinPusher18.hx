import lub.Gfx;
import lub.Input;
import lub.Io;
import lub.Lub;
import lub.Math.Mat4;
import lub.Math.Quat;
import lub.Math.Vec3;
import lub.Phys3d;
import lua.Table;

typedef Coin = {
	var active:Bool;
	var gen:Int;
	var flash:Int;
	var spawnX:Float;
}

class CoinPusher18 {
	static inline var DT:Float = 1.0 / 60.0;
	static inline var MAX_COINS:Int = 48;
	static inline var COIN_R:Float = 0.17;
	static inline var COIN_H:Float = 0.07;
	static inline var SPAWN_INTERVAL:Int = 55;

	static var frame:Int = 0;
	static var coins:Array<Coin> = [];
	static var score:Int = 0;
	static var spawnX:Float = 0.0;
	static var cubeVerts:Table<Int, Float> = null;
	static var cubeIndices:Table<Int, Int> = null;
	static var cylVerts:Table<Int, Float> = null;
	static var cylIndices:Table<Int, Int> = null;
	static var cylIndexCount:Int = 0;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend, width: 640, height: 360});
		for (_ in 0...MAX_COINS)
			coins.push({
				active: false,
				gen: 0,
				flash: 0,
				spawnX: 0.0
			});
	}

	// --- procedural unit meshes (pos + normal interleaved) ------------------

	static function buildCube() {
		var verts:Array<Float> = [];
		var indices:Array<Int> = [];
		// One face per axis direction; unit half-extents, so model scale maps
		// hx/hy/hz directly.
		var faces = [
			{n: [1.0, 0.0, 0.0], u: [0.0, 1.0, 0.0], v: [0.0, 0.0, 1.0]},
			{n: [-1.0, 0.0, 0.0], u: [0.0, 0.0, 1.0], v: [0.0, 1.0, 0.0]},
			{n: [0.0, 1.0, 0.0], u: [0.0, 0.0, 1.0], v: [1.0, 0.0, 0.0]},
			{n: [0.0, -1.0, 0.0], u: [1.0, 0.0, 0.0], v: [0.0, 0.0, 1.0]},
			{n: [0.0, 0.0, 1.0], u: [1.0, 0.0, 0.0], v: [0.0, 1.0, 0.0]},
			{n: [0.0, 0.0, -1.0], u: [0.0, 1.0, 0.0], v: [1.0, 0.0, 0.0]},
		];
		for (f in faces) {
			var base = Std.int(verts.length / 6);
			for (i in 0...4) {
				var su = (i == 1 || i == 2) ? 1.0 : -1.0;
				var sv = (i >= 2) ? 1.0 : -1.0;
				for (k in 0...3)
					verts.push(f.n[k] + f.u[k] * su + f.v[k] * sv);
				for (k in 0...3)
					verts.push(f.n[k]);
			}
			for (idx in [0, 1, 2, 0, 2, 3])
				indices.push(base + idx);
		}
		cubeVerts = Table.fromArray(verts);
		cubeIndices = Table.fromArray(indices);
	}

	static function buildCylinder(sides:Int) {
		var verts:Array<Float> = [];
		var indices:Array<Int> = [];
		// Side quads: two rings at y=±0.5 with radial normals.
		for (i in 0...sides) {
			var a = i / sides * Math.PI * 2.0;
			var nx = Math.cos(a);
			var nz = Math.sin(a);
			verts = verts.concat([nx, -0.5, nz, nx, 0.0, nz]);
			verts = verts.concat([nx, 0.5, nz, nx, 0.0, nz]);
		}
		for (i in 0...sides) {
			var b0 = i * 2;
			var b1 = ((i + 1) % sides) * 2;
			for (idx in [b0, b0 + 1, b1 + 1, b0, b1 + 1, b1])
				indices.push(idx);
		}
		// Caps: center + ring, flat y normals.
		for (side in 0...2) {
			var ny = side == 0 ? 1.0 : -1.0;
			var y = ny * 0.5;
			var center = Std.int(verts.length / 6);
			verts = verts.concat([0.0, y, 0.0, 0.0, ny, 0.0]);
			for (i in 0...sides) {
				var a = i / sides * Math.PI * 2.0;
				verts = verts.concat([Math.cos(a), y, Math.sin(a), 0.0, ny, 0.0]);
			}
			for (i in 0...sides) {
				var r0 = center + 1 + i;
				var r1 = center + 1 + ((i + 1) % sides);
				if (ny > 0) {
					for (idx in [center, r0, r1])
						indices.push(idx);
				} else {
					for (idx in [center, r1, r0])
						indices.push(idx);
				}
			}
		}
		cylVerts = Table.fromArray(verts);
		cylIndices = Table.fromArray(indices);
		cylIndexCount = indices.length;
	}

	// --- physics declaration -------------------------------------------------

	static function declareStatics(world:Dynamic) {
		// Upper shelf the pusher slides on; coins get shoved off its front
		// edge (z = -0.8) down onto the lower tray.
		var shelf = Phys3d.body(world, "shelf", {type: Phys3d.STATIC, initial: {x: 0.0, y: 0.3, z: -1.7}});
		Phys3d.box(shelf, "solid", {
			hx: 1.5,
			hy: 0.3,
			hz: 0.9,
			friction: 0.55,
			contact: true
		});

		// Lower tray; its front edge (z = 1.0) is the payout drop.
		var tray = Phys3d.body(world, "tray", {type: Phys3d.STATIC, initial: {x: 0.0, y: 0.05, z: 0.1}});
		Phys3d.box(tray, "solid", {
			hx: 1.5,
			hy: 0.05,
			hz: 0.9,
			friction: 0.5,
			contact: true
		});

		var wallL = Phys3d.body(world, "wall:l", {type: Phys3d.STATIC, initial: {x: -1.58, y: 0.5, z: -0.8}});
		Phys3d.box(wallL, "solid", {
			hx: 0.08,
			hy: 0.5,
			hz: 1.8,
			friction: 0.2
		});
		var wallR = Phys3d.body(world, "wall:r", {type: Phys3d.STATIC, initial: {x: 1.58, y: 0.5, z: -0.8}});
		Phys3d.box(wallR, "solid", {
			hx: 0.08,
			hy: 0.5,
			hz: 1.8,
			friction: 0.2
		});
		var wallB = Phys3d.body(world, "wall:b", {type: Phys3d.STATIC, initial: {x: 0.0, y: 0.8, z: -2.68}});
		Phys3d.box(wallB, "solid", {
			hx: 1.5,
			hy: 0.8,
			hz: 0.08,
			friction: 0.2
		});
	}

	static function pusherZ(t:Float):Float {
		return -1.7 + 0.38 * Math.sin(t * 1.35);
	}

	static function declarePusher(world:Dynamic):Dynamic {
		var pusher = Phys3d.body(world, "pusher", {
			type: Phys3d.KINEMATIC,
			initial: {x: 0.0, y: 0.82, z: pusherZ(0.0)},
		});
		Phys3d.box(pusher, "solid", {
			hx: 1.45,
			hy: 0.22,
			hz: 0.55,
			friction: 0.7,
			contact: true
		});
		Phys3d.setTarget(pusher, {
			x: 0.0,
			y: 0.82,
			z: pusherZ(frame * DT),
			dt: DT
		});
		return pusher;
	}

	static function spawnCoin(x:Float) {
		for (c in coins) {
			if (!c.active) {
				c.active = true;
				c.gen++;
				c.flash = 0;
				// Store the spawn pose via the body's versioned constructor:
				// bumping gen recreates the body at the new initial pose.
				c.spawnX = x;
				return;
			}
		}
	}

	static function declareCoins(world:Dynamic):Array<{coin:Coin, body:Dynamic, index:Int}> {
		var live = [];
		for (i in 0...MAX_COINS) {
			var c = coins[i];
			if (!c.active)
				continue;
			var body = Phys3d.body(world, "coin:" + i, {
				type: Phys3d.DYNAMIC,
				version: c.gen,
				initial: {
					x: c.spawnX,
					y: 1.35,
					z: -1.0,
					euler: {x: 0.0, y: (i * 0.61803) % 6.283, z: 0.0},
				},
			});
			Phys3d.cylinder(body, "solid", {
				version: c.gen,
				height: COIN_H,
				radius: COIN_R,
				sides: 20,
				density: 1.0,
				friction: 0.35,
				contact: true,
			});
			live.push({coin: c, body: body, index: i});
		}
		return live;
	}

	// --- rendering -----------------------------------------------------------

	static function modelMat(pose:Dynamic, sx:Float, sy:Float, sz:Float):Mat4 {
		var rot = new Quat(pose.qx, pose.qy, pose.qz, pose.qw).toMat4();
		return Mat4.translate(new Vec3(pose.x, pose.y, pose.z)).mul(rot).mul(Mat4.scale(new Vec3(sx, sy, sz)));
	}

	static function staticModel(x:Float, y:Float, z:Float, sx:Float, sy:Float, sz:Float):Mat4 {
		return Mat4.translate(new Vec3(x, y, z)).mul(Mat4.scale(new Vec3(sx, sy, sz)));
	}

	static function drawMesh(indexCount:Int, vb:Dynamic, ib:Dynamic, shader:Dynamic, vp:Mat4, model:Mat4, color:Array<Float>) {
		var mvp = vp.mul(model);
		Gfx.draw(indexCount, {
			verts: vb,
			indices: ib,
			uniforms: {
				mvp: Table.fromArray(mvp.m),
				model: Table.fromArray(model.m),
				color: Table.fromArray(color),
			},
		}, {
			shader: shader,
			depth: true,
			depth_write: true,
			cull: Gfx.NONE
		});
	}

	public static function onFrame() {
		if (cubeVerts == null) {
			buildCube();
			buildCylinder(24);
		}

		var world = Phys3d.world("coin_pusher", {
			gravity: {x: 0.0, y: -10.0, z: 0.0},
			fixedDt: DT,
			substeps: 4,
			maxSteps: 1,
		});
		Phys3d.begin(world);

		declareStatics(world);
		declarePusher(world);

		// Move the spawn cursor with A/D (or arrow keys) plus mouse drag.
		var d = Input.mouseDelta();
		spawnX += d.dx * 0.004;
		if (Input.keyDown("a") || Input.keyDown("left"))
			spawnX -= 0.03;
		if (Input.keyDown("d") || Input.keyDown("right"))
			spawnX += 0.03;
		if (spawnX < -1.25)
			spawnX = -1.25;
		if (spawnX > 1.25)
			spawnX = 1.25;

		// Manual drop plus a deterministic auto drop so the demo (and the
		// golden capture) feeds itself.
		var wantDrop = Input.keyDown("space") || Input.mouseDown(1);
		var autoDrop = frame % SPAWN_INTERVAL == 10;
		if (autoDrop)
			spawnCoin(-1.0 + ((frame * 7919) % 2000) / 1000.0);
		else if (wantDrop && frame % 8 == 0)
			spawnCoin(spawnX);

		var live = declareCoins(world);

		Phys3d.step(world, DT);

		// Contact begin events light coins up for a few frames.
		var contacts:Dynamic = Phys3d.contacts(world, "begin");
		var ci = 1;
		while (contacts[ci] != null) {
			var contact = contacts[ci];
			for (entry in live) {
				var key = "coin:" + entry.index;
				if (contact.a.body == key || contact.b.body == key)
					entry.coin.flash = 10;
			}
			ci++;
		}

		// Payout: anything that fell below the table is collected.
		for (entry in live) {
			var pose = Phys3d.pose(entry.body);
			if (pose == null)
				continue;
			if (pose.y < -1.5) {
				entry.coin.active = false;
				score++;
			}
		}

		// --- draw ---
		var vs = Io.loadText("samples/18_coin_pusher/data/18_lit.vs.slang");
		var fs = Io.loadText("samples/18_coin_pusher/data/18_lit.fs.slang");
		if (vs.text == null || fs.text == null)
			return;
		var shader = Gfx.useShader("coin_pusher_lit", vs.text, fs.text, vs.version ^ fs.version);
		var cubeVb = Gfx.useBuffer("cp_cube_vb", Gfx.VERTEX, cubeVerts, 1);
		var cubeIb = Gfx.useBuffer("cp_cube_ib", Gfx.INDEX, cubeIndices, 1);
		var cylVb = Gfx.useBuffer("cp_cyl_vb", Gfx.VERTEX, cylVerts, 1);
		var cylIb = Gfx.useBuffer("cp_cyl_ib", Gfx.INDEX, cylIndices, 1);

		var view = Mat4.lookAtLh(new Vec3(0.0, 2.7, 3.3), new Vec3(0.0, 0.15, -0.7), new Vec3(0, 1, 0));
		var proj = Mat4.perspectiveLh(40.0, 640.0 / 360.0, 0.1, 50.0);
		var vp = proj.mul(view);

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: Table.fromArray([0.035, 0.045, 0.06, 1.0])
		});

		var gray = [0.42, 0.45, 0.5, 1.0];
		var dark = [0.22, 0.24, 0.28, 1.0];
		drawMesh(36, cubeVb, cubeIb, shader, vp, staticModel(0.0, 0.3, -1.7, 1.5, 0.3, 0.9), gray);
		drawMesh(36, cubeVb, cubeIb, shader, vp, staticModel(0.0, 0.05, 0.1, 1.5, 0.05, 0.9), gray);
		drawMesh(36, cubeVb, cubeIb, shader, vp, staticModel(-1.58, 0.5, -0.8, 0.08, 0.5, 1.8), dark);
		drawMesh(36, cubeVb, cubeIb, shader, vp, staticModel(1.58, 0.5, -0.8, 0.08, 0.5, 1.8), dark);
		drawMesh(36, cubeVb, cubeIb, shader, vp, staticModel(0.0, 0.8, -2.68, 1.5, 0.8, 0.08), dark);

		var pusherPose = Phys3d.pose(world, "pusher");
		if (pusherPose != null)
			drawMesh(36, cubeVb, cubeIb, shader, vp, modelMat(pusherPose, 1.45, 0.22, 0.55), [0.85, 0.45, 0.15, 1.0]);

		for (entry in live) {
			var pose = Phys3d.pose(entry.body);
			if (pose == null)
				continue;
			if (entry.coin.flash > 0)
				entry.coin.flash--;
			var hot = entry.coin.flash > 0 ? 0.18 : 0.0;
			var color = [0.85 + hot, 0.68 + hot, 0.2 + hot, 1.0];
			drawMesh(cylIndexCount, cylVb, cylIb, shader, vp, modelMat(pose, COIN_R, COIN_H, COIN_R), color);
		}

		// Spawn cursor marker: a small floating coin ghost at the drop line.
		drawMesh(cylIndexCount, cylVb, cylIb, shader, vp, staticModel(spawnX, 1.35, -1.0, COIN_R, COIN_H, COIN_R), [0.5, 0.75, 0.9, 1.0]);

		Gfx.endPass();
		frame++;
	}
}
