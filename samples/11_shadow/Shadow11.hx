import lub.Lub;
import lub.Gfx;
import lub.Io;
import lub.Math;

// Shadow mapping: render light-space depth into an offscreen target with
// a depth attachment, then use it as a comparison sampler in the scene pass.
class Shadow11 {
	static inline var STRIDE:Int = 10; // pos.xyz + normal.xyz + color.rgba
	static inline var SHADOW_SIZE:Int = 1024;
	static inline var DT:Float = 1.0 / 60.0;

	static var tAccum:Float = 0;
	static var meshVersion:Int = 0;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	static inline function pushV(out:Array<Float>, x:Float, y:Float, z:Float, nx:Float, ny:Float, nz:Float, c:Array<Float>) {
		out.push(x);
		out.push(y);
		out.push(z);
		out.push(nx);
		out.push(ny);
		out.push(nz);
		out.push(c[0]);
		out.push(c[1]);
		out.push(c[2]);
		out.push(c[3]);
	}

	static function tri(out:Array<Float>, a:Array<Float>, b:Array<Float>, c:Array<Float>, n:Array<Float>, col:Array<Float>) {
		pushV(out, a[0], a[1], a[2], n[0], n[1], n[2], col);
		pushV(out, b[0], b[1], b[2], n[0], n[1], n[2], col);
		pushV(out, c[0], c[1], c[2], n[0], n[1], n[2], col);
	}

	static function quad(out:Array<Float>, a:Array<Float>, b:Array<Float>, c:Array<Float>, d:Array<Float>, n:Array<Float>, col:Array<Float>) {
		tri(out, a, b, c, n, col);
		tri(out, a, c, d, n, col);
	}

	static function addFloor(out:Array<Float>) {
		var n:Array<Float> = [0, 1, 0];
		quad(out, [-2.3, 0, -1.55], [2.3, 0, -1.55], [2.3, 0, 1.75], [-2.3, 0, 1.75], n, [0.50, 0.55, 0.50, 1.0]);

		var line:Array<Float> = [0.38, 0.42, 0.39, 1.0];
		for (i in -4...5) {
			var x = i * 0.48;
			quad(out, [x - 0.005, 0.003, -1.55], [x + 0.005, 0.003, -1.55], [x + 0.005, 0.003, 1.75], [x - 0.005, 0.003, 1.75], n, line);
		}
		for (i in -3...4) {
			var z = i * 0.48;
			quad(out, [-2.3, 0.003, z - 0.005], [2.3, 0.003, z - 0.005], [2.3, 0.003, z + 0.005], [-2.3, 0.003, z + 0.005], n, line);
		}
	}

	static function addBox(out:Array<Float>, cx:Float, cy:Float, cz:Float, sx:Float, sy:Float, sz:Float, col:Array<Float>) {
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

		quad(out, p000, p010, p110, p100, [0, 0, -1], col);
		quad(out, p001, p101, p111, p011, [0, 0, 1], col);
		quad(out, p000, p001, p011, p010, [-1, 0, 0], col);
		quad(out, p100, p110, p111, p101, [1, 0, 0], col);
		quad(out, p010, p011, p111, p110, [0, 1, 0], col);
		quad(out, p000, p100, p101, p001, [0, -1, 0], col);
	}

	static function spherePoint(cx:Float, cy:Float, cz:Float, r:Float, u:Float, vv:Float):Array<Float> {
		var cv = Math.cos(vv);
		var nx = Math.cos(u) * cv;
		var ny = Math.sin(vv);
		var nz = Math.sin(u) * cv;
		return [cx + nx * r, cy + ny * r, cz + nz * r, nx, ny, nz];
	}

	static function addSphere(out:Array<Float>, cx:Float, cy:Float, cz:Float, r:Float, col:Array<Float>) {
		var rings = 12;
		var segs = 24;
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
				pushV(out, a[0], a[1], a[2], a[3], a[4], a[5], col);
				pushV(out, b[0], b[1], b[2], b[3], b[4], b[5], col);
				pushV(out, c[0], c[1], c[2], c[3], c[4], c[5], col);
				pushV(out, a[0], a[1], a[2], a[3], a[4], a[5], col);
				pushV(out, c[0], c[1], c[2], c[3], c[4], c[5], col);
				pushV(out, d[0], d[1], d[2], d[3], d[4], d[5], col);
			}
		}
	}

	static function addCasters(out:Array<Float>, t:Float) {
		addBox(out, -0.05, 0.12, 0.48, 0.88, 0.24, 0.34, [0.95, 0.76, 0.38, 1.0]);
		addBox(out, -0.58, 0.52 + Math.sin(t * 1.4) * 0.07, -0.12, 0.42, 0.42, 0.42, [0.18, 0.72, 0.78, 1.0]);
		addSphere(out, 0.62 + Math.cos(t * 1.1) * 0.20, 0.58 + Math.sin(t * 1.7) * 0.08, -0.18 + Math.sin(t * 0.8) * 0.22, 0.22, [0.95, 0.28, 0.34, 1.0]);
		addBox(out, 0.92, 0.34, 0.36, 0.18, 0.68, 0.18, [0.48, 0.39, 0.86, 1.0]);
	}

	// Mirrors Lua build_meshes: returns { casters, scene } where scene is floor + casters.
	static function buildMeshes(t:Float):{casters:Array<Float>, scene:Array<Float>} {
		var casters:Array<Float> = [];
		var scene:Array<Float> = [];
		addFloor(scene);
		addCasters(casters, t);
		for (f in casters)
			scene.push(f);
		return {casters: casters, scene: scene};
	}

	static function cameraMvp(t:Float):Mat4 {
		var eye = new Vec3(2.0 + Math.sin(t * 0.25) * 0.12, 1.35, -2.85);
		var view = Mat4.lookAtLh(eye, new Vec3(0.05, 0.34, 0.12), new Vec3(0, 1, 0));
		return Mat4.perspectiveLh(52, 16.0 / 9.0, 0.1, 40.0).mul(view);
	}

	static function lightMvp():Mat4 {
		var lightPos = new Vec3(-2.0, 3.3, -1.5);
		var view = Mat4.lookAtLh(lightPos, new Vec3(0.08, 0.24, 0.08), new Vec3(0, 1, 0));
		return Mat4.orthoLh(3.4, 3.4, 0.1, 7.0).mul(view);
	}

	public static function onFrame() {
		tAccum = tAccum + DT;

		var dvsR = Io.loadText("samples/11_shadow/data/11_shadow_depth.vs.slang");
		var dfsR = Io.loadText("samples/11_shadow/data/11_shadow_depth.fs.slang");
		var svsR = Io.loadText("samples/11_shadow/data/11_shadow_scene.vs.slang");
		var sfsR = Io.loadText("samples/11_shadow/data/11_shadow_scene.fs.slang");
		var dvs:String = dvsR.text;
		var dvsv:Int = dvsR.version;
		var dfs:String = dfsR.text;
		var dfsv:Int = dfsR.version;
		var svs:String = svsR.text;
		var svsv:Int = svsR.version;
		var sfs:String = sfsR.text;
		var sfsv:Int = sfsR.version;
		if (dvs == null || dfs == null || svs == null || sfs == null)
			return;

		var depthShader = Gfx.useShader("shadow_depth_shader", dvs, dfs, dvsv ^ dfsv);
		var sceneShader = Gfx.useShader("shadow_scene_shader", svs, sfs, svsv ^ sfsv);

		var shadowMap = Gfx.useTexture("shadow_map", SHADOW_SIZE, SHADOW_SIZE, Gfx.RGBA8, null, 1, {target: true, filter: Gfx.NEAREST, wrap: Gfx.CLAMP});
		var shadowDepth = Gfx.useTexture("shadow_depth", SHADOW_SIZE, SHADOW_SIZE, Gfx.DEPTH16, null, 1, {target: true, filter: Gfx.NEAREST, wrap: Gfx.CLAMP});

		var meshes = buildMeshes(tAccum);
		var casters = meshes.casters;
		var scene = meshes.scene;
		meshVersion = meshVersion + 1;
		var casterBuf = Gfx.useBuffer("shadow_casters", Gfx.VERTEX, lua.Table.fromArray(casters), meshVersion);
		var sceneBuf = Gfx.useBuffer("shadow_scene", Gfx.VERTEX, lua.Table.fromArray(scene), meshVersion);

		var lmvp = lua.Table.fromArray(lightMvp().m);

		Gfx.beginPass({
			target: shadowMap,
			depth_target: shadowDepth,
			clear_color: lua.Table.fromArray([1.0, 1.0, 1.0, 1.0]),
			clear_depth: 1,
		});
		Gfx.draw(Std.int(casters.length / STRIDE), {verts: casterBuf, uniforms: {light_mvp: lmvp}}, {
			shader: depthShader,
			depth: true,
			depth_write: true,
			cull: Gfx.NONE
		});
		Gfx.endPass();

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.09, 0.12, 0.15, 1.0])
		});
		Gfx.draw(Std.int(scene.length / STRIDE), {
			verts: sceneBuf,
			shadow_map: shadowMap,
			uniforms: {
				mvp: lua.Table.fromArray(cameraMvp(tAccum).m),
				light_mvp: lmvp,
			},
		}, {
			shader: sceneShader,
			depth: true,
			depth_write: true,
			cull: Gfx.NONE
		});
		Gfx.endPass();
	}
}
