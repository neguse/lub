import lub.Gfx;
import lub.Input;
import lub.Io;
import lub.Lub;

class Sponza14 {
	static inline var DT:Float = 1.0 / 60.0;
	static inline var MODEL_SCALE:Float = 0.002;
	static inline var ASSET_FULL:String = "samples/14_sponza/data/Sponza/Sponza.gltf";
	static inline var ASSET_WEB:String = "samples/14_sponza/data/Sponza/SponzaLite.gltf";

	static var rtW:Int = 1280;
	static var rtH:Int = 720;
	static var tAccum:Float = 0.0;

	static var meshVersion:Int = -1;
	static var primVbs:Array<Dynamic> = [];
	static var primIbs:Array<Dynamic> = [];
	static var primCounts:Array<Int> = [];
	static var primMats:Array<Dynamic> = [];

	static var camEye:Array<Float> = [0.0, 1.15, 0.0];
	static var camYaw:Float = 1.5708;
	static var camPitch:Float = -0.02;

	static var quadVerts:Array<Float> = [
		-1, -1, 0, 0,
		 1, -1, 1, 0,
		 1,  1, 1, 1,
		-1, -1, 0, 0,
		 1,  1, 1, 1,
		-1,  1, 0, 1,
	];

	static var whitePx:Array<Int> = [255, 255, 255, 255];
	static var normalPx:Array<Int> = [128, 128, 255, 255];

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	public static function onFrame() {
		tAccum += DT;
		var sz = Gfx.size();
		rtW = sz.w;
		rtH = sz.h;

		var gShader = shader2("sponza_gbuffer", "14_sponza_gbuffer.vs.slang", "14_sponza_gbuffer.fs.slang");
		var lightShader = shader2("sponza_lighting", "14_sponza_light.vs.slang", "14_sponza_light.fs.slang");
		if (gShader == null || lightShader == null)
			return;

		var meshR = Io.loadGltf(lub.Sys.isWeb() ? ASSET_WEB : ASSET_FULL);
		var mesh:Dynamic = meshR.mesh;
		if (mesh == null)
			return;
		ensureMesh(mesh, meshR.version);

		var gAlbedo = target("sponza_g_albedo", rtW, rtH, Gfx.RGBA8, Gfx.NEAREST);
		var gNormal = target("sponza_g_normal", rtW, rtH, Gfx.RGBA16F, Gfx.NEAREST);
		var gPosition = target("sponza_g_position", rtW, rtH, Gfx.RGBA16F, Gfx.NEAREST);
		var gDepth = target("sponza_g_depth", rtW, rtH, Gfx.DEPTH32F, Gfx.NEAREST);
		var quad = Gfx.useBuffer("sponza_quad", Gfx.VERTEX, lua.Table.fromArray(quadVerts), 1);

		var view = updateCamera();
		var proj = perspectiveLh(55.0, rtW / rtH, 0.05, 80.0);
		proj[5] = -proj[5];
		var model = scaleTransMat(MODEL_SCALE, 0.0, 0.0, 0.0);

		geometryPass(gShader, gAlbedo, gNormal, gPosition, gDepth, proj, view, model);
		lightingPass(lightShader, quad, gAlbedo, gNormal, gPosition, view);
	}

	static function ensureMesh(mesh:Dynamic, version:Int) {
		if (meshVersion == version)
			return;
		meshVersion = version;
		primVbs = [];
		primIbs = [];
		primCounts = [];
		primMats = [];

		var prims:lua.Table<Int, Dynamic> = mesh.primitives;
		var n:Int = mesh.primitive_count;
		for (i in 0...n) {
			var prim:Dynamic = prims[i + 1];
			var verts = Io.interleavePnu(prim);
			primVbs.push(Gfx.useBuffer("sponza_vb_" + i, Gfx.VERTEX, verts, version));
			if (prim.indices != null && prim.index_count > 0) {
				primIbs.push(Gfx.useBuffer("sponza_ib_" + i, Gfx.INDEX, prim.indices, version));
				primCounts.push(prim.index_count);
			} else {
				primIbs.push(null);
				primCounts.push(prim.vert_count);
			}
			primMats.push(prim.material);
		}
	}

	static function geometryPass(shader:Dynamic, gAlbedo:Dynamic, gNormal:Dynamic, gPosition:Dynamic, gDepth:Dynamic, proj:Array<Float>, view:Array<Float>,
			model:Array<Float>) {
		Gfx.beginPass({
			targets: lua.Table.fromArray([gAlbedo, gNormal, gPosition]),
			depth_target: gDepth,
			clear_colors: lua.Table.fromArray([
				lua.Table.fromArray([0.0, 0.0, 0.0, 1.0]),
				lua.Table.fromArray([0.5, 0.5, 1.0, 0.0]),
				lua.Table.fromArray([0.0, 0.0, 0.0, 0.0]),
			]),
			clear_depth: 1.0,
		});

		var pv = lua.Table.fromArray(proj);
		var vv = lua.Table.fromArray(view);
		var mv = lua.Table.fromArray(model);
		for (i in 0...primVbs.length) {
			var mat:Dynamic = primMats[i];
			var bindings:Dynamic = {
				verts: primVbs[i],
				base_color: materialTexture(mat == null ? null : mat.base_color_path, "bc", whitePx),
				metallic_roughness: materialTexture(mat == null ? null : mat.metallic_roughness_path, "mr", whitePx),
				normal_map: materialTexture(mat == null ? null : mat.normal_path, "n", normalPx),
				uniforms: {
					proj: pv,
					view: vv,
					model: mv,
					base_color_factor: baseColorFactor(mat),
					material: materialParams(mat),
					normal_params: normalParams(mat),
				}
			};
			if (primIbs[i] != null)
				bindings.indices = primIbs[i];
			Gfx.draw(primCounts[i], bindings, {
				shader: shader,
				depth: true,
				depth_write: true,
				cull: Gfx.NONE
			});
		}
		Gfx.endPass();
	}

	static function lightingPass(shader:Dynamic, quad:Dynamic, gAlbedo:Dynamic, gNormal:Dynamic, gPosition:Dynamic, view:Array<Float>) {
		var l0 = norm3(mat3mul(view, norm3([-0.35, 0.82, -0.45])));
		var l1 = norm3(mat3mul(view, norm3([0.55, 0.35, 0.25])));
		Gfx.beginPass({target: Gfx.mainTex, clear_color: lua.Table.fromArray([0.015, 0.018, 0.022, 1.0])});
		Gfx.draw(6, {
			verts: quad,
			g_albedo: gAlbedo,
			g_normal: gNormal,
			g_position: gPosition,
			uniforms: {
				light0: lua.Table.fromArray([l0[0], l0[1], l0[2], 5.4]),
				light1: lua.Table.fromArray([l1[0], l1[1], l1[2], 0.9]),
				params: lua.Table.fromArray([1.2, 0.055, 0.0, 0.0]),
			}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function materialTexture(path:Dynamic, suffix:String, fallback:Array<Int>):Dynamic {
		if (path != null) {
			var p = Std.string(path);
			var r = Io.loadPng(p);
			if (r.pixels != null) {
				return Gfx.useTexture("sponza_tex_" + suffix + "_" + p, r.width, r.height, r.format, r.pixels, r.version,
					{filter: Gfx.LINEAR, wrap: Gfx.REPEAT});
			}
		}
		return Gfx.useTexture("sponza_default_" + suffix, 1, 1, Gfx.RGBA8, lua.Table.fromArray(fallback), 1, {filter: Gfx.LINEAR, wrap: Gfx.REPEAT});
	}

	static function baseColorFactor(mat:Dynamic):lua.Table<Int, Float> {
		var bc:Dynamic = mat == null ? null : mat.base_color_factor;
		return lua.Table.fromArray([
			tableFloat(bc, 1, 1.0),
			tableFloat(bc, 2, 1.0),
			tableFloat(bc, 3, 1.0),
			tableFloat(bc, 4, 1.0)
		]);
	}

	static function materialParams(mat:Dynamic):lua.Table<Int, Float> {
		var metallic = mat == null || mat.metallic_factor == null ? 1.0 : mat.metallic_factor;
		var roughness = mat == null || mat.roughness_factor == null ? 1.0 : mat.roughness_factor;
		var cutoff = mat == null || mat.alpha_cutoff == null ? 0.5 : mat.alpha_cutoff;
		var alphaMode = mat == null || mat.alpha_mode == null ? 0.0 : mat.alpha_mode;
		return lua.Table.fromArray([metallic, roughness, cutoff, alphaMode]);
	}

	static function normalParams(mat:Dynamic):lua.Table<Int, Float> {
		var scale = mat == null || mat.normal_scale == null ? 1.0 : mat.normal_scale;
		return lua.Table.fromArray([scale, 0.0, 0.0, 0.0]);
	}

	static inline function tableFloat(t:Dynamic, i:Int, def:Float):Float {
		if (t == null || untyped t[i] == null)
			return def;
		return untyped t[i];
	}

	static function target(key:String, w:Int, h:Int, fmt:Int, filter:Int):Dynamic {
		var ver = w * 10000 + h * 10 + fmt;
		return Gfx.useTexture(key, w, h, fmt, null, ver, {target: true, filter: filter, wrap: Gfx.CLAMP});
	}

	static function shader2(key:String, vsPath:String, fsPath:String):Dynamic {
		var v = Io.loadText("samples/14_sponza/data/" + vsPath);
		var f = Io.loadText("samples/14_sponza/data/" + fsPath);
		if (v.text == null || f.text == null)
			return null;
		return Gfx.useShader(key, v.text, f.text, v.version ^ f.version);
	}

	static function updateCamera():Array<Float> {
		var camStr = lua.Os.getenv("LUB_SPONZA_CAM");
		if (camStr != null) {
			var p = camStr.split(",");
			if (p.length >= 5) {
				camYaw = Std.parseFloat(p[0]);
				camPitch = Std.parseFloat(p[1]);
				camEye[0] = Std.parseFloat(p[2]);
				camEye[1] = Std.parseFloat(p[3]);
				camEye[2] = Std.parseFloat(p[4]);
			}
		}
		if (lua.Os.getenv("LUB_SPONZA_SPIN") != null)
			camYaw = Math.sin(tAccum * 0.25) * 0.32;

		var md = Input.mouseDelta();
		if (Input.mouseDown(1)) {
			camYaw += md.dx * 0.003;
			camPitch -= md.dy * 0.003;
			if (camPitch > 1.45)
				camPitch = 1.45;
			if (camPitch < -1.45)
				camPitch = -1.45;
		}

		var up:Array<Float> = [0, 1, 0];
		var fwd = forwardDir();
		var right = norm3(cross3(up, fwd));
		var spd = 2.6 * DT;
		if (Input.keyDown("w")) {
			camEye[0] += fwd[0] * spd;
			camEye[1] += fwd[1] * spd;
			camEye[2] += fwd[2] * spd;
		}
		if (Input.keyDown("s")) {
			camEye[0] -= fwd[0] * spd;
			camEye[1] -= fwd[1] * spd;
			camEye[2] -= fwd[2] * spd;
		}
		if (Input.keyDown("d")) {
			camEye[0] += right[0] * spd;
			camEye[1] += right[1] * spd;
			camEye[2] += right[2] * spd;
		}
		if (Input.keyDown("a")) {
			camEye[0] -= right[0] * spd;
			camEye[1] -= right[1] * spd;
			camEye[2] -= right[2] * spd;
		}
		if (Input.keyDown("e"))
			camEye[1] += spd;
		if (Input.keyDown("q"))
			camEye[1] -= spd;

		var target:Array<Float> = [camEye[0] + fwd[0], camEye[1] + fwd[1], camEye[2] + fwd[2]];
		return lookAtLh(camEye, target, up);
	}

	static function forwardDir():Array<Float> {
		var cp = Math.cos(camPitch);
		return [Math.sin(camYaw) * cp, Math.sin(camPitch), Math.cos(camYaw) * cp];
	}

	static function scaleTransMat(s:Float, tx:Float, ty:Float, tz:Float):Array<Float> {
		return [s, 0, 0, tx, 0, s, 0, ty, 0, 0, s, tz, 0, 0, 0, 1];
	}

	static function perspectiveLh(fovDeg:Float, aspect:Float, nz:Float, fz:Float):Array<Float> {
		var f = 1 / Math.tan(fovDeg * Math.PI / 360);
		return [
			f / aspect, 0,              0,                    0,
			         0, f,              0,                    0,
			         0, 0, fz / (fz - nz), -fz * nz / (fz - nz),
			         0, 0,              1,                    0,
		];
	}

	static function lookAtLh(eye:Array<Float>, target:Array<Float>, up:Array<Float>):Array<Float> {
		var z = norm3(sub3(target, eye));
		var x = norm3(cross3(up, z));
		var y = cross3(z, x);
		return [
			x[0], x[1], x[2], -dot3(x, eye),
			y[0], y[1], y[2], -dot3(y, eye),
			z[0], z[1], z[2], -dot3(z, eye),
			   0,    0,    0,             1,
		];
	}

	static function mat3mul(m:Array<Float>, v:Array<Float>):Array<Float> {
		return [
			m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
			m[4] * v[0] + m[5] * v[1] + m[6] * v[2],
			m[8] * v[0] + m[9] * v[1] + m[10] * v[2],
		];
	}

	static inline function dot3(a:Array<Float>, b:Array<Float>):Float {
		return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
	}

	static function cross3(a:Array<Float>, b:Array<Float>):Array<Float> {
		return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
	}

	static function norm3(v:Array<Float>):Array<Float> {
		var len = Math.sqrt(dot3(v, v));
		return [v[0] / len, v[1] / len, v[2] / len];
	}

	static inline function sub3(a:Array<Float>, b:Array<Float>):Array<Float> {
		return [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
	}
}
