import lub.Gfx;
import lub.Input;
import lub.Io;
import lub.Math;
import lubx.Boot;
import lubx.Png;

class Sponza14 {
	static inline var MODEL_SCALE:Float = 0.002;
	static inline var SHADOW_SIZE:Int = 2048;
	static inline var ASSET_FULL:String = "samples/14_sponza/data/Sponza/Sponza.gltf";

	static var rtW:Int = 1280;
	static var rtH:Int = 720;
	static var tAccum:Float = 0.0;

	static var meshVersion:Int = -1;
	static var primVbs:Array<Dynamic> = [];
	static var primIbs:Array<Dynamic> = [];
	static var primCounts:Array<Int> = [];
	static var primMats:Array<Dynamic> = [];

	static var camEye:Vec3 = new Vec3(-1.5, 0.25, 0.0);
	static var camYaw:Float = 1.5708;
	static var camPitch:Float = 0.0;
	static var prevViewProj:Mat4 = null;
	static var pcEye:Vec3 = new Vec3(-1.5, 0.25, 0.0);
	static var pcYaw:Float = 1.5708;
	static var pcPitch:Float = 0.0;

	static var quadVerts:Array<Float> = [
		-1, -1, 0, 0,
		 1, -1, 1, 0,
		 1,  1, 1, 1,
		-1, -1, 0, 0,
		 1,  1, 1, 1,
		-1,  1, 0, 1,
	];

	static var quadVertsFlip:Array<Float> = [
		-1, -1, 0, 1,
		 1, -1, 1, 1,
		 1,  1, 1, 0,
		-1, -1, 0, 1,
		 1,  1, 1, 0,
		-1,  1, 0, 0,
	];

	static var whitePx:Array<Int> = [255, 255, 255, 255];
	static var normalPx:Array<Int> = [128, 128, 255, 255];

	public static function main() {}

	public static function onInit() {
		Boot.config({});
	}

	public static function onFrame(dt:Float) {
		tAccum += dt;
		var sz = Gfx.size();
		rtW = sz.w;
		rtH = sz.h;

		var gShader = shader2("sponza_gbuffer", "14_sponza_gbuffer.vs.slang", "14_sponza_gbuffer.fs.slang");
		var shadowShader = shader2("sponza_shadow", "14_sponza_shadow.vs.slang", "14_sponza_shadow.fs.slang");
		var ssaoShader = shader2("sponza_ssao", "14_sponza_ssao.vs.slang", "14_sponza_ssao.fs.slang");
		var lightShader = shader2("sponza_lighting", "14_sponza_light.vs.slang", "14_sponza_light.fs.slang");
		var copyShader = fsShader("sponza_copy", "14_sponza_copy.fs.slang");
		var pShader = fsShader("sponza_present", "14_sponza_present.fs.slang");
		var fogShader = fsShader("sponza_fog", "14_sponza_fog.fs.slang");
		var brightShader = fsShader("sponza_bright", "14_sponza_bright.fs.slang");
		var blurHShader = fsShader("sponza_blurh", "14_sponza_blur_h.fs.slang");
		var blurVShader = fsShader("sponza_blurv", "14_sponza_blur_v.fs.slang");
		var combineShader = fsShader("sponza_combine", "14_sponza_combine.fs.slang");
		var outlineShader = fsShader("sponza_outline", "14_sponza_outline.fs.slang");
		var dofShader = fsShader("sponza_dof", "14_sponza_dof.fs.slang");
		var motionShader = shader2("sponza_motion", "14_sponza_motion.vs.slang", "14_sponza_motion.fs.slang");
		var screenShader = shader2("sponza_screen", "14_sponza_screen.vs.slang", "14_sponza_screen.fs.slang");
		if (gShader == null || shadowShader == null || ssaoShader == null || lightShader == null || copyShader == null || pShader == null
			|| fogShader == null || brightShader == null || blurHShader == null || blurVShader == null || combineShader == null || outlineShader == null
			|| dofShader == null || motionShader == null || screenShader == null)
			return;

		var meshR = Io.loadGltf(ASSET_FULL);
		var mesh:Dynamic = meshR.mesh;
		if (mesh == null)
			return;
		ensureMesh(mesh, meshR.version);

		var gAlbedo = target("sponza_g_albedo", rtW, rtH, Gfx.RGBA8, Gfx.NEAREST);
		var gNormal = target("sponza_g_normal", rtW, rtH, Gfx.RGBA16F, Gfx.NEAREST);
		var gPosition = target("sponza_g_position", rtW, rtH, Gfx.RGBA16F, Gfx.NEAREST);
		var gDepth = target("sponza_g_depth", rtW, rtH, Gfx.DEPTH32F, Gfx.NEAREST);
		var aoTex = target("sponza_ao", rtW, rtH, Gfx.RGBA8, Gfx.LINEAR);
		var shadowMap = target("sponza_shadow_map", SHADOW_SIZE, SHADOW_SIZE, Gfx.RGBA8, Gfx.NEAREST);
		var shadowDepth = target("sponza_shadow_depth", SHADOW_SIZE, SHADOW_SIZE, Gfx.DEPTH32F, Gfx.NEAREST);
		var texA = target("sponza_texA", rtW, rtH, Gfx.RGBA16F, Gfx.LINEAR);
		var texB = target("sponza_texB", rtW, rtH, Gfx.RGBA16F, Gfx.LINEAR);
		var bloomA = target("sponza_bloomA", rtW, rtH, Gfx.RGBA16F, Gfx.LINEAR);
		var bloomB = target("sponza_bloomB", rtW, rtH, Gfx.RGBA16F, Gfx.LINEAR);
		var quad = Gfx.useBuffer("sponza_quad", Gfx.VERTEX, lua.Table.fromArray(quadVerts), 1);
		var quadF = Gfx.useBuffer("sponza_quadF", Gfx.VERTEX, lua.Table.fromArray(quadVertsFlip), 1);

		var view = updateCamera(dt);
		var proj = Mat4.perspectiveLh(55.0, rtW / rtH, 0.05, 80.0);
		proj.m[5] = -proj.m[5];
		var model = Mat4.scaleTrans(MODEL_SCALE, new Vec3(0.0, 0.0, 0.0));

		var worldLight = new Vec3(-0.42, 0.92, -0.32).normalize();
		var lightTarget = new Vec3(0.0, 1.1, 0.0);
		var lightEye = new Vec3(lightTarget.x + worldLight.x * 7.0, lightTarget.y + worldLight.y * 7.0, lightTarget.z + worldLight.z * 7.0);
		var lightView = Mat4.lookAtLh(lightEye, lightTarget, new Vec3(0, 1, 0));
		var lightMvp = Mat4.orthoLh(8.0, 8.0, 0.1, 15.0).mul(lightView);
		var invView = view.rigidInverse(camEye);
		var viewToLight = lightMvp.mul(invView);

		var viewProj = proj.mul(view);
		var reproj = ((prevViewProj == null) ? viewProj : prevViewProj).mul(invView);
		prevViewProj = viewProj;
		var camMoved = cameraMoved();

		shadowPass(shadowShader, shadowMap, shadowDepth, model, lightMvp);
		geometryPass(gShader, gAlbedo, gNormal, gPosition, gDepth, proj, view, model, lightMvp);
		ssaoPass(aoTex, ssaoShader, quadF, gNormal, gPosition, proj.m[0], proj.m[5]);
		lightingPass(texA, lightShader, quadF, gAlbedo, gNormal, gPosition, shadowMap, aoTex, view, viewToLight);

		blitFog(texB, fogShader, quadF, texA, gPosition);
		blit(bloomA, brightShader, quadF, texB);
		blit(bloomB, blurHShader, quadF, bloomA);
		blit(bloomA, blurVShader, quadF, bloomB);
		blitCombine(texA, combineShader, quadF, texB, bloomA);

		if (lua.Os.getenv("LUB_SPONZA_NO_OUTLINE") == null)
			blitOutline(texB, outlineShader, quadF, texA, gNormal, gPosition);
		else
			blit(texB, copyShader, quadF, texA);

		if (lua.Os.getenv("LUB_SPONZA_NO_DOF") == null) {
			blit(bloomB, blurHShader, quadF, texB);
			blit(bloomA, blurVShader, quadF, bloomB);
			blitDof(texA, dofShader, quadF, texB, bloomA, gPosition);
		} else {
			blit(texA, copyShader, quadF, texB);
		}

		var beauty:Dynamic = texA;
		var outTex:Dynamic = texB;
		if (camMoved && lua.Os.getenv("LUB_SPONZA_NO_MOTION") == null) {
			motionPass(texB, motionShader, quadF, texA, gPosition, reproj);
			beauty = texB;
			outTex = texA;
		}

		var mode = sponzaMode();
		var screenSrc:Dynamic = beauty;
		if (mode == 1 || mode == 4)
			screenSrc = gAlbedo;
		else if (mode == 2 || mode == 5)
			screenSrc = gNormal;
		else if (mode == 3)
			screenSrc = gPosition;
		else if (mode == 6)
			screenSrc = aoTex;
		else if (mode == 7)
			screenSrc = shadowMap;

		screenPass(outTex, screenShader, quadF, screenSrc, mode);
		present(pShader, quad, outTex);
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
			var verts = Io.interleavePnut(prim);
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

	static function shadowPass(shader:Dynamic, shadowMap:Dynamic, shadowDepth:Dynamic, model:Mat4, lightMvp:Mat4) {
		Gfx.beginPass({
			target: shadowMap,
			depth_target: shadowDepth,
			clear_color: lua.Table.fromArray([1.0, 1.0, 1.0, 1.0]),
			clear_depth: 1.0
		});
		var lmvp = lua.Table.fromArray(lightMvp.m);
		var mv = lua.Table.fromArray(model.m);
		for (i in 0...primVbs.length) {
			var mat:Dynamic = primMats[i];
			var bindings:Dynamic = {
				verts: primVbs[i],
				base_color: materialTexture(mat == null ? null : mat.base_color_path, "bc", whitePx),
				uniforms: {
					light_mvp: lmvp,
					model: mv,
					base_color_factor: baseColorFactor(mat),
					material: materialParams(mat),
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

	static function geometryPass(shader:Dynamic, gAlbedo:Dynamic, gNormal:Dynamic, gPosition:Dynamic, gDepth:Dynamic, proj:Mat4, view:Mat4, model:Mat4,
			lightMvp:Mat4) {
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

		var pv = lua.Table.fromArray(proj.m);
		var vv = lua.Table.fromArray(view.m);
		var mv = lua.Table.fromArray(model.m);
		var lmvp = lua.Table.fromArray(lightMvp.m);
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
					light_mvp: lmvp,
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

	static function lightingPass(targ:Dynamic, shader:Dynamic, quad:Dynamic, gAlbedo:Dynamic, gNormal:Dynamic, gPosition:Dynamic, shadowMap:Dynamic,
			aoTex:Dynamic, view:Mat4, viewToLight:Mat4) {
		var l0 = view.mat3MulVec3(new Vec3(-0.42, 0.92, -0.32).normalize()).normalize();
		var l1 = view.mat3MulVec3(new Vec3(0.58, 0.35, 0.22).normalize()).normalize();
		Gfx.beginPass({target: targ, clear_color: lua.Table.fromArray([0.0, 0.0, 0.0, 1.0])});
		Gfx.draw(6, {
			verts: quad,
			g_albedo: gAlbedo,
			g_normal: gNormal,
			g_position: gPosition,
			shadow_map: shadowMap,
			ao_map: aoTex,
			uniforms: {
				light0: lua.Table.fromArray([l0.x, l0.y, l0.z, 5.6]),
				light1: lua.Table.fromArray([l1.x, l1.y, l1.z, 0.7]),
				params: lua.Table.fromArray([1.0, 0.050, 0.82, 0.85]),
				vl0: lua.Table.fromArray([viewToLight.m[0], viewToLight.m[1], viewToLight.m[2], viewToLight.m[3]]),
				vl1: lua.Table.fromArray([viewToLight.m[4], viewToLight.m[5], viewToLight.m[6], viewToLight.m[7]]),
				vl2: lua.Table.fromArray([viewToLight.m[8], viewToLight.m[9], viewToLight.m[10], viewToLight.m[11]]),
				vl3: lua.Table.fromArray([viewToLight.m[12], viewToLight.m[13], viewToLight.m[14], viewToLight.m[15]]),
			}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function blit(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {verts: quad, scene: tex}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function blitFog(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic, gPosition:Dynamic) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {verts: quad, scene: tex, gpos: gPosition}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function blitCombine(targ:Dynamic, shader:Dynamic, quad:Dynamic, base:Dynamic, bloom:Dynamic) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {verts: quad, scene: base, bloom: bloom}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function blitOutline(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic, gNormal:Dynamic, gPosition:Dynamic) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {
			verts: quad,
			scene: tex,
			gnormal: gNormal,
			gpos: gPosition
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function blitDof(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic, blurred:Dynamic, gPosition:Dynamic) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {
			verts: quad,
			scene: tex,
			blurred: blurred,
			gpos: gPosition
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function ssaoPass(targ:Dynamic, shader:Dynamic, quad:Dynamic, gNormal:Dynamic, gPosition:Dynamic, p00:Float, p11:Float) {
		Gfx.beginPass({target: targ, clear_color: lua.Table.fromArray([1.0, 1.0, 1.0, 1.0])});
		Gfx.draw(6, {
			verts: quad,
			gnormal: gNormal,
			gpos: gPosition,
			uniforms: {params: lua.Table.fromArray([p00, p11, 0.0, 0.0])}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function motionPass(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic, gPosition:Dynamic, m:Mat4) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {
			verts: quad,
			scene: tex,
			gpos: gPosition,
			uniforms: {
				r0: lua.Table.fromArray([m.m[0], m.m[1], m.m[2], m.m[3]]),
				r1: lua.Table.fromArray([m.m[4], m.m[5], m.m[6], m.m[7]]),
				r2: lua.Table.fromArray([m.m[8], m.m[9], m.m[10], m.m[11]]),
				r3: lua.Table.fromArray([m.m[12], m.m[13], m.m[14], m.m[15]]),
			}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function screenPass(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic, mode:Int) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {
			verts: quad,
			scene: tex,
			uniforms: {params: lua.Table.fromArray([mode, 0.0, 0.0, 0.0])}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function present(shader:Dynamic, quad:Dynamic, tex:Dynamic) {
		Gfx.beginPass({target: Gfx.mainTex, clear_color: black()});
		Gfx.draw(6, {verts: quad, scene: tex}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function materialTexture(path:Dynamic, suffix:String, fallback:Array<Int>):Dynamic {
		if (path != null) {
			var p = Std.string(path);
			var r = Png.load(p);
			if (r.bytes != null) {
				return Gfx.useTexture("sponza_tex_" + suffix + "_" + p, r.width, r.height, r.format, r.bytes, r.version,
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
		var ver = w * 100000 + h * 100 + fmt;
		return Gfx.useTexture(key, w, h, fmt, null, ver, {target: true, filter: filter, wrap: Gfx.CLAMP});
	}

	static function fsShader(key:String, fsPath:String):Dynamic {
		return shader2(key, "14_sponza_quad.vs.slang", fsPath);
	}

	static function shader2(key:String, vsPath:String, fsPath:String):Dynamic {
		var v = Io.loadText("samples/14_sponza/data/" + vsPath);
		var f = Io.loadText("samples/14_sponza/data/" + fsPath);
		if (v.text == null || f.text == null)
			return null;
		return Gfx.useShader(key, v.text, f.text, v.version * 31 + f.version);
	}

	static inline function black():lua.Table<Int, Float> {
		return lua.Table.fromArray([0.0, 0.0, 0.0, 1.0]);
	}

	static function sponzaMode():Int {
		var s = lua.Os.getenv("LUB_SPONZA_MODE");
		if (s == null)
			return 0;
		switch (s.toLowerCase()) {
			case "albedo":
				return 1;
			case "normal":
				return 2;
			case "depth":
				return 3;
			case "roughness":
				return 4;
			case "metallic":
				return 5;
			case "ao":
				return 6;
			case "shadow":
				return 7;
			case "beauty":
				return 8;
		}
		var n = Std.parseInt(s);
		return n == null ? 0 : n;
	}

	static function cameraMoved():Bool {
		var moved = Math.abs(camEye.x - pcEye.x) + Math.abs(camEye.y - pcEye.y) + Math.abs(camEye.z - pcEye.z) + Math.abs(camYaw - pcYaw)
			+ Math.abs(camPitch - pcPitch) > 1e-6;
		pcEye.x = camEye.x;
		pcEye.y = camEye.y;
		pcEye.z = camEye.z;
		pcYaw = camYaw;
		pcPitch = camPitch;
		return moved;
	}

	static function updateCamera(dt:Float):Mat4 {
		var camStr = lua.Os.getenv("LUB_SPONZA_CAM");
		if (camStr != null) {
			var p = camStr.split(",");
			if (p.length >= 5) {
				camYaw = Std.parseFloat(p[0]);
				camPitch = Std.parseFloat(p[1]);
				camEye.x = Std.parseFloat(p[2]);
				camEye.y = Std.parseFloat(p[3]);
				camEye.z = Std.parseFloat(p[4]);
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

		var up = new Vec3(0, 1, 0);
		var fwd = forwardDir();
		var right = up.cross(fwd).normalize();
		var spd = 2.6 * dt;
		if (Input.keyDown("w")) {
			camEye.x += fwd.x * spd;
			camEye.y += fwd.y * spd;
			camEye.z += fwd.z * spd;
		}
		if (Input.keyDown("s")) {
			camEye.x -= fwd.x * spd;
			camEye.y -= fwd.y * spd;
			camEye.z -= fwd.z * spd;
		}
		if (Input.keyDown("d")) {
			camEye.x += right.x * spd;
			camEye.y += right.y * spd;
			camEye.z += right.z * spd;
		}
		if (Input.keyDown("a")) {
			camEye.x -= right.x * spd;
			camEye.y -= right.y * spd;
			camEye.z -= right.z * spd;
		}
		if (Input.keyDown("e"))
			camEye.y += spd;
		if (Input.keyDown("q"))
			camEye.y -= spd;

		var target = new Vec3(camEye.x + fwd.x, camEye.y + fwd.y, camEye.z + fwd.z);
		return Mat4.lookAtLh(camEye, target, up);
	}

	static function forwardDir():Vec3 {
		var cp = Math.cos(camPitch);
		return new Vec3(Math.sin(camYaw) * cp, Math.sin(camPitch), Math.cos(camYaw) * cp);
	}
}
