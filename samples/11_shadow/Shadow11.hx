import lub.Gfx;
import lub.Io;
import lub.Math;
import lubx.Shapes;
import lubx.Boot;

// Shadow mapping: render light-space depth into an offscreen target with
// a depth attachment, then use it as a comparison sampler in the scene pass.
class Shadow11 {
	static inline var SHADOW_SIZE:Int = 1024;
	static var tAccum:Float = 0;
	static var meshVersion:Int = 0;

	public static function main() {}

	public static function onInit() {
		Boot.config();
	}

	static function addFloor(out:Array<Float>) {
		var n:Array<Float> = [0, 1, 0];
		Shapes.quad(out, [-2.3, 0, -1.55], [2.3, 0, -1.55], [2.3, 0, 1.75], [-2.3, 0, 1.75], n, [0.50, 0.55, 0.50, 1.0]);

		var line:Array<Float> = [0.38, 0.42, 0.39, 1.0];
		for (i in -4...5) {
			var x = i * 0.48;
			Shapes.quad(out, [x - 0.005, 0.003, -1.55], [x + 0.005, 0.003, -1.55], [x + 0.005, 0.003, 1.75], [x - 0.005, 0.003, 1.75], n, line);
		}
		for (i in -3...4) {
			var z = i * 0.48;
			Shapes.quad(out, [-2.3, 0.003, z - 0.005], [2.3, 0.003, z - 0.005], [2.3, 0.003, z + 0.005], [-2.3, 0.003, z + 0.005], n, line);
		}
	}

	static function addCasters(out:Array<Float>, t:Float) {
		Shapes.box(out, -0.05, 0.12, 0.48, 0.88, 0.24, 0.34, [0.95, 0.76, 0.38, 1.0]);
		Shapes.box(out, -0.58, 0.52 + Math.sin(t * 1.4) * 0.07, -0.12, 0.42, 0.42, 0.42, [0.18, 0.72, 0.78, 1.0]);
		Shapes.sphere(out, 0.62 + Math.cos(t * 1.1) * 0.20, 0.58 + Math.sin(t * 1.7) * 0.08, -0.18 + Math.sin(t * 0.8) * 0.22, 0.22, [0.95, 0.28, 0.34, 1.0]);
		Shapes.box(out, 0.92, 0.34, 0.36, 0.18, 0.68, 0.18, [0.48, 0.39, 0.86, 1.0]);
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

	public static function onFrame(dt:Float) {
		tAccum = tAccum + dt;

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

		var depthShader = Gfx.useShader("shadow_depth_shader", dvs, dfs, dvsv * 31 + dfsv);
		var sceneShader = Gfx.useShader("shadow_scene_shader", svs, sfs, svsv * 31 + sfsv);

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
		Gfx.draw(Std.int(casters.length / Shapes.STRIDE), {verts: casterBuf, uniforms: {light_mvp: lmvp}}, {
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
		Gfx.draw(Std.int(scene.length / Shapes.STRIDE), {
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
