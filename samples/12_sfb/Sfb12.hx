import lub.Lub;
import lub.Gfx;
import lub.Io;
import lub.Input;

// 3D-game-shaders-for-beginners style showcase.
//
// A deferred-ish pipeline: one geometry pass fills a G-buffer
//   gColor    (RGBA8)   Blinn-Phong lit color
//   gNormal   (RGBA16F) view-space normal
//   gPosition (RGBA16F) view-space position + linear depth in .w
// then a chain of full-screen post passes reads the G-buffer to add fog,
// outline, SSAO, bloom, DoF, and screen-space toy effects. Each post pass is a
// fullscreen quad draw sampling the previous result (ping-pong between two
// RGBA8 work textures).
class Sfb12 {
	static inline var STRIDE:Int = 10; // pos.xyz + normal.xyz + color.rgba
	// render-target size; set from Gfx.size() each frame so the whole post chain
	// runs at the real drawable resolution (smaller = faster on weak devices).
	static var RT_W:Int = 1280;
	static var RT_H:Int = 720;
	static inline var DT:Float = 1.0 / 60.0;
	static inline var WATER_Y:Float = 0.12; // world height of the water plane

	static var tAccum:Float = 0;
	static var meshVersion:Int = 0;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	// ---- geometry (world-space, model baked on CPU) ----

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
		var line:Array<Float> = [0.36, 0.40, 0.37, 1.0];
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
		var rings = 14;
		var segs = 28;
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

	// ---- textured hero object (material demo) ----
	static inline var TEX_N:Int = 64;
	static var albedoPx:Array<Int> = null;
	static var normalPx:Array<Int> = null;

	// Procedural albedo (checker) + normal map (grid of bumps), generated once.
	static function genTextures() {
		if (albedoPx != null)
			return;
		albedoPx = [];
		normalPx = [];
		for (y in 0...TEX_N) {
			for (x in 0...TEX_N) {
				var c = (((x >> 3) + (y >> 3)) & 1) == 0;
				if (c) {
					albedoPx.push(210);
					albedoPx.push(175);
					albedoPx.push(95);
					albedoPx.push(255);
				} else {
					albedoPx.push(70);
					albedoPx.push(120);
					albedoPx.push(160);
					albedoPx.push(255);
				}
				var nx = Math.sin(x / TEX_N * Math.PI * 8) * 0.6;
				var ny = Math.sin(y / TEX_N * Math.PI * 8) * 0.6;
				var len = Math.sqrt(nx * nx + ny * ny + 1.0);
				normalPx.push(Std.int((nx / len * 0.5 + 0.5) * 255));
				normalPx.push(Std.int((ny / len * 0.5 + 0.5) * 255));
				normalPx.push(Std.int((1.0 / len * 0.5 + 0.5) * 255));
				normalPx.push(255);
			}
		}
	}

	static var flowPx:Array<Int> = null;
	static var waterNrmPx:Array<Int> = null;
	static var lutPx:Array<Int> = null;
	static inline var LUT_N:Int = 16;

	// Flow map (RG = flow direction) + ripple normal map for the water surface.
	static function genWaterTextures() {
		if (flowPx != null)
			return;
		flowPx = [];
		waterNrmPx = [];
		for (y in 0...TEX_N) {
			for (x in 0...TEX_N) {
				var fx = 0.7;
				var fy = 0.35 * Math.sin(y / TEX_N * Math.PI * 2);
				var fl = Math.sqrt(fx * fx + fy * fy);
				flowPx.push(Std.int((fx / fl * 0.5 + 0.5) * 255));
				flowPx.push(Std.int((fy / fl * 0.5 + 0.5) * 255));
				flowPx.push(128);
				flowPx.push(255);
				var nx = Math.sin(x / TEX_N * Math.PI * 12) * 0.4;
				var ny = Math.sin(y / TEX_N * Math.PI * 12 + 1.7) * 0.4;
				var len = Math.sqrt(nx * nx + ny * ny + 1.0);
				waterNrmPx.push(Std.int((nx / len * 0.5 + 0.5) * 255));
				waterNrmPx.push(Std.int((ny / len * 0.5 + 0.5) * 255));
				waterNrmPx.push(Std.int((1.0 / len * 0.5 + 0.5) * 255));
				waterNrmPx.push(255);
			}
		}
	}

	// 16^3 color lookup table flattened into a 256x16 texture. This is a real
	// LUT sample in the grade pass, with a deliberately subtle teal/warm grade.
	static function genLut() {
		if (lutPx != null)
			return;
		lutPx = [];
		for (g in 0...LUT_N) {
			for (b in 0...LUT_N) {
				for (r in 0...LUT_N) {
					var rr = r / (LUT_N - 1);
					var gg = g / (LUT_N - 1);
					var bb = b / (LUT_N - 1);
					var lum = rr * 0.2126 + gg * 0.7152 + bb * 0.0722;
					var shadow = 1.0 - lum;
					var high = lum;
					var nr = rr * 1.03 + high * 0.035 - shadow * 0.025;
					var ng = gg * 1.01 + shadow * 0.025 + high * 0.010;
					var nb = bb * 0.98 + shadow * 0.060 - high * 0.020;
					lutPx.push(Std.int(saturate(nr) * 255));
					lutPx.push(Std.int(saturate(ng) * 255));
					lutPx.push(Std.int(saturate(nb) * 255));
					lutPx.push(255);
				}
			}
		}
	}

	static inline function saturate(v:Float):Float {
		return v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);
	}

	static inline function pushHero(out:Array<Float>, cx:Float, cy:Float, cz:Float, r:Float, seg:Int, ring:Int, segs:Int, rings:Int) {
		var u0 = seg / segs * Math.PI * 2;
		var v0 = -Math.PI * 0.5 + ring / rings * Math.PI;
		var cv = Math.cos(v0);
		var nx = Math.cos(u0) * cv;
		var ny = Math.sin(v0);
		var nz = Math.sin(u0) * cv;
		out.push(cx + nx * r);
		out.push(cy + ny * r);
		out.push(cz + nz * r);
		out.push(nx);
		out.push(ny);
		out.push(nz);
		out.push(seg / segs * 3.0);
		out.push(ring / rings * 3.0);
	}

	// UV-sphere (pos.xyz, normal.xyz, uv.xy) for the material shader.
	static function buildHero():Array<Float> {
		var out:Array<Float> = [];
		var cx = 0.1, cy = 0.5, cz = -0.95, r = 0.45;
		var rings = 24, segs = 48;
		for (ring in 0...rings) {
			for (seg in 0...segs) {
				pushHero(out, cx, cy, cz, r, seg, ring, segs, rings);
				pushHero(out, cx, cy, cz, r, seg + 1, ring, segs, rings);
				pushHero(out, cx, cy, cz, r, seg + 1, ring + 1, segs, rings);
				pushHero(out, cx, cy, cz, r, seg, ring, segs, rings);
				pushHero(out, cx, cy, cz, r, seg + 1, ring + 1, segs, rings);
				pushHero(out, cx, cy, cz, r, seg, ring + 1, segs, rings);
			}
		}
		return out;
	}

	static function buildScene(t:Float):Array<Float> {
		var out:Array<Float> = [];
		addFloor(out);
		addBox(out, -0.05, 0.12, 0.48, 0.88, 0.24, 0.34, [0.95, 0.76, 0.38, 1.0]);
		addBox(out, -0.58, 0.52 + Math.sin(t * 1.4) * 0.07, -0.12, 0.42, 0.42, 0.42, [0.18, 0.72, 0.78, 1.0]);
		addSphere(out, 0.62 + Math.cos(t * 1.1) * 0.20, 0.58 + Math.sin(t * 1.7) * 0.08, -0.18 + Math.sin(t * 0.8) * 0.22, 0.22, [0.95, 0.28, 0.34, 1.0]);
		addBox(out, 0.92, 0.34, 0.36, 0.18, 0.68, 0.18, [0.48, 0.39, 0.86, 1.0]);
		return out;
	}

	// ---- matrix math (row-major, matches Slang ROW_MAJOR) ----

	static function mul4(a:Array<Float>, b:Array<Float>):Array<Float> {
		var r:Array<Float> = [for (_ in 0...16) 0.0];
		for (row in 0...4)
			for (col in 0...4) {
				var s = 0.0;
				for (k in 0...4)
					s = s + a[row * 4 + k] * b[k * 4 + col];
				r[row * 4 + col] = s;
			}
		return r;
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

	static function identityMat():Array<Float> {
		return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
	}

	// uniform scale + translation (row-major).
	static function scaleTransMat(s:Float, tx:Float, ty:Float, tz:Float):Array<Float> {
		return [s, 0, 0, tx, 0, s, 0, ty, 0, 0, s, tz, 0, 0, 0, 1];
	}

	// inverse of a rigid view matrix [R | -R*eye] -> [R^T | eye] (row-major).
	static function rigidInverse(view:Array<Float>, eye:Array<Float>):Array<Float> {
		return [
			view[0], view[4],  view[8], eye[0],
			view[1], view[5],  view[9], eye[1],
			view[2], view[6], view[10], eye[2],
			      0,       0,        0,      1,
		];
	}

	// upper-left 3x3 of a row-major 4x4 applied to a vector
	static function mat3mul(m:Array<Float>, v:Array<Float>):Array<Float> {
		return [
			m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
			m[4] * v[0] + m[5] * v[1] + m[6] * v[2],
			m[8] * v[0] + m[9] * v[1] + m[10] * v[2],
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

	static function orthoLh(w:Float, h:Float, nz:Float, fz:Float):Array<Float> {
		return [
			2 / w,     0,             0,               0,
			    0, 2 / h,             0,               0,
			    0,     0, 1 / (fz - nz), -nz / (fz - nz),
			    0,     0,             0,               1,
		];
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

	// ---- free-fly camera (WASD move, Q/E down/up, left-drag to look) ----
	// Persists across frames. With no input (headless golden) it stays at the
	// initial pose, so captures remain deterministic.
	static var camEye:Array<Float> = [2.0, 1.35, -2.85];
	static var camYaw:Float = -0.581; // looks toward the scene centre
	static var camPitch:Float = -0.277;
	static var prevViewProj:Array<Float> = null; // last frame's proj*view (motion blur)
	// last frame's camera pose, to detect whether the camera actually moved
	// (motion blur only runs when it did, so a still camera stays a clean no-op).
	static var pcEye:Array<Float> = [2.0, 1.35, -2.85];
	static var pcYaw:Float = -0.581;
	static var pcPitch:Float = -0.277;

	static function forwardDir():Array<Float> {
		var cp = Math.cos(camPitch);
		return [Math.sin(camYaw) * cp, Math.sin(camPitch), Math.cos(camYaw) * cp];
	}

	static function updateCamera():Array<Float> {
		var up:Array<Float> = [0, 1, 0];

		// LUB_SFB_CAM="yaw,pitch,ex,ey,ez" pins the camera to a fixed pose (testing).
		var camStr = lua.Os.getenv("LUB_SFB_CAM");
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

		// LUB_SFB_SPIN auto-orbits the camera (deterministic) so motion blur is
		// visible in a headless capture; default (unset) keeps the golden still.
		if (lua.Os.getenv("LUB_SFB_SPIN") != null)
			camYaw = camYaw + 0.02;

		// Mouse look: consume the delta every frame (so it never jumps), apply
		// only while the left button is held.
		var md = Input.mouseDelta();
		if (Input.mouseDown(1)) {
			camYaw = camYaw + md.dx * 0.003;
			camPitch = camPitch - md.dy * 0.003;
			if (camPitch > 1.5)
				camPitch = 1.5;
			if (camPitch < -1.5)
				camPitch = -1.5;
		}

		var fwd = forwardDir();
		var right = norm3(cross3(up, fwd));
		var spd = 2.0 * DT;
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

	// ---- fullscreen quad (pos.xy, uv) ----
	// Quad used for the final present (offscreen -> swapchain). sokol y-flips
	// the swapchain, so this maps clip y = -1 to uv.y = 0.
	static var quadVerts:Array<Float> = [
		-1, -1, 0, 0,
		 1, -1, 1, 0,
		 1,  1, 1, 1,
		-1, -1, 0, 0,
		 1,  1, 1, 1,
		-1,  1, 0, 1,
	];

	// Quad used for offscreen -> offscreen post passes. Offscreen targets are
	// not y-flipped, so we flip uv.y here to keep every intermediate blit an
	// identity (no orientation drift no matter how many passes we chain).
	static var quadVertsFlip:Array<Float> = [
		-1, -1, 0, 1,
		 1, -1, 1, 1,
		 1,  1, 1, 0,
		-1, -1, 0, 1,
		 1,  1, 1, 0,
		-1,  1, 0, 0,
	];

	public static function onFrame() {
		tAccum = tAccum + DT;

		// size the offscreen chain to the real drawable (canvas/swapchain).
		var sz = Gfx.size();
		RT_W = sz.w;
		RT_H = sz.h;

		var gShader = shader2("sfb_gbuf", "12_gbuffer.vs.slang", "12_gbuffer.fs.slang");
		var matShader = shader2("sfb_mat", "12_mat.vs.slang", "12_mat.fs.slang");
		var shFlatShader = shader2("sfb_shflat", "12_shadow_flat.vs.slang", "12_shadow.fs.slang");
		var shHeroShader = shader2("sfb_shhero", "12_shadow_hero.vs.slang", "12_shadow.fs.slang");
		var pShader = fsShader("sfb_present", "12_present.fs.slang");
		var ssaoShader = shader2("sfb_ssao", "12_ssao.vs.slang", "12_ssao.fs.slang");
		var fogShader = fsShader("sfb_fog", "12_fog.fs.slang");
		var brightShader = fsShader("sfb_bright", "12_bright.fs.slang");
		var blurHShader = fsShader("sfb_blurh", "12_blur_h.fs.slang");
		var blurVShader = fsShader("sfb_blurv", "12_blur_v.fs.slang");
		var combineShader = fsShader("sfb_combine", "12_combine.fs.slang");
		var outlineShader = fsShader("sfb_outline", "12_outline.fs.slang");
		var dofShader = fsShader("sfb_dof", "12_dof.fs.slang");
		var motionShader = shader2("sfb_motion", "12_motion.vs.slang", "12_motion.fs.slang");
		var waterShader = shader2("sfb_water", "12_water.vs.slang", "12_water.fs.slang");
		var screenShader = shader2("sfb_screen", "12_screen.vs.slang", "12_screen.fs.slang");
		var gradeShader = shader2("sfb_grade", "12_grade.vs.slang", "12_grade.fs.slang");
		if (gShader == null || matShader == null || shFlatShader == null || shHeroShader == null || pShader == null || ssaoShader == null
			|| fogShader == null || brightShader == null || blurHShader == null || blurVShader == null || combineShader == null || outlineShader == null
			|| dofShader == null || motionShader == null || waterShader == null || screenShader == null || gradeShader == null)
			return;

		// effect isolation mode (LUB_SFB_MODE): 0=composite, 1=posterize,
		// 2=pixelize, 3=chromatic, 4=sharpen, 5=dilation, 6=normal, 7=depth,
		// 8=shadow map. (LUB_SFB_NOWATER=1 skips the water plane.)
		var modeStr:String = lua.Os.getenv("LUB_SFB_MODE");
		var mode:Int = (modeStr == null) ? 0 : Std.parseInt(modeStr);
		if (mode == null)
			mode = 0;

		// G-buffer + work targets.
		var gColor = target("sfb_gColor", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);
		var gNormal = target("sfb_gNormal", RT_W, RT_H, Gfx.RGBA16F, Gfx.NEAREST);
		var gPosition = target("sfb_gPosition", RT_W, RT_H, Gfx.RGBA16F, Gfx.NEAREST);
		var gDepth = target("sfb_gDepth", RT_W, RT_H, Gfx.DEPTH32F, Gfx.NEAREST);
		var shadowMap = target("sfb_shadow", 1024, 1024, Gfx.RGBA8, Gfx.NEAREST);
		var shadowDepth = target("sfb_shadowD", 1024, 1024, Gfx.DEPTH32F, Gfx.NEAREST);
		var texA = target("sfb_texA", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);
		var texB = target("sfb_texB", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);
		var bloomA = target("sfb_bloomA", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);
		var bloomB = target("sfb_bloomB", RT_W, RT_H, Gfx.RGBA8, Gfx.LINEAR);

		// Scene + camera.
		var scene = buildScene(tAccum);
		meshVersion = meshVersion + 1;
		var sceneBuf = Gfx.useBuffer("sfb_scene", Gfx.VERTEX, lua.Table.fromArray(scene), meshVersion);
		var quadBuf = Gfx.useBuffer("sfb_quad", Gfx.VERTEX, lua.Table.fromArray(quadVerts), 1);
		var quadBufF = Gfx.useBuffer("sfb_quadF", Gfx.VERTEX, lua.Table.fromArray(quadVertsFlip), 1);

		// Textured hero (material demo): generated albedo + normal map. The hero is
		// a procedural UV sphere by default, or the vendored CC0 Avocado glTF when
		// LUB_SFB_GLTF is set (exercises the loader's UV/index path + interleavePnu).
		genTextures();
		var heroBuf:Dynamic = null;
		var heroIdx:Dynamic = null;
		var heroCount:Int = 0;
		var heroModel:Array<Float> = identityMat();
		if (lua.Os.getenv("LUB_SFB_GLTF") != null) {
			var gr = Io.loadGltf("samples/12_sfb/data/12_avocado.gltf");
			var mesh:Dynamic = gr.mesh;
			if (mesh != null) {
				heroBuf = Gfx.useBuffer("sfb_hero", Gfx.VERTEX, Io.interleavePnu(mesh), gr.version);
				heroIdx = Gfx.useBuffer("sfb_heroIdx", Gfx.INDEX, mesh.indices, gr.version);
				heroCount = mesh.index_count;
				heroModel = scaleTransMat(8.5, 0.1, 0.0, -0.7);
			}
		}
		if (heroBuf == null) {
			var heroMesh = buildHero();
			heroBuf = Gfx.useBuffer("sfb_hero", Gfx.VERTEX, lua.Table.fromArray(heroMesh), 1);
			heroCount = Std.int(heroMesh.length / 8);
		}
		var albedoTex = Gfx.useTexture("sfb_albedo", TEX_N, TEX_N, Gfx.RGBA8, lua.Table.fromArray(albedoPx), 1, {filter: Gfx.LINEAR, wrap: Gfx.REPEAT});
		var normalTex = Gfx.useTexture("sfb_normalmap", TEX_N, TEX_N, Gfx.RGBA8, lua.Table.fromArray(normalPx), 1, {filter: Gfx.LINEAR, wrap: Gfx.REPEAT});

		genWaterTextures();
		var flowTex = Gfx.useTexture("sfb_flow", TEX_N, TEX_N, Gfx.RGBA8, lua.Table.fromArray(flowPx), 1, {filter: Gfx.LINEAR, wrap: Gfx.REPEAT});
		var waterNrmTex = Gfx.useTexture("sfb_waternrm", TEX_N, TEX_N, Gfx.RGBA8, lua.Table.fromArray(waterNrmPx), 1, {filter: Gfx.LINEAR, wrap: Gfx.REPEAT});
		genLut();
		var lutTex = Gfx.useTexture("sfb_lut", LUT_N * LUT_N, LUT_N, Gfx.RGBA8, lua.Table.fromArray(lutPx), 1, {filter: Gfx.LINEAR, wrap: Gfx.CLAMP});

		var view = updateCamera();
		var proj = perspectiveLh(52, RT_W / RT_H, 0.1, 40.0);
		// Offscreen targets are stored y-down vs the swapchain (sokol only y-flips
		// the default framebuffer). Pre-flip clip-space Y so the G-buffer is
		// screen-oriented; cull is NONE so the winding change is harmless.
		proj[5] = -proj[5];
		var worldLight = norm3([-0.48, 1.0, -0.32]); // direction toward the light
		var lightView = norm3(mat3mul(view, worldLight));
		// directional shadow: orthographic light camera looking at the scene centre.
		var lightLook = lookAtLh([0.1 + worldLight[0] * 6.0, 0.3 + worldLight[1] * 6.0, worldLight[2] * 6.0], [0.1, 0.3, 0.0], [0, 1, 0]);
		var lightMvp = mul4(orthoLh(5.5, 5.5, 0.1, 12.0), lightLook);

		// Reprojection for motion blur: maps a current view-space point to last
		// frame's clip space = prevViewProj * inverse(currentView). Still camera =>
		// reproj == proj => zero velocity.
		var viewProj = mul4(proj, view);
		var invView = rigidInverse(view, camEye);
		var reproj = mul4((prevViewProj == null) ? viewProj : prevViewProj, invView);
		prevViewProj = viewProj;

		var camMoved = Math.abs(camEye[0] - pcEye[0]) + Math.abs(camEye[1] - pcEye[1]) + Math.abs(camEye[2] - pcEye[2]) + Math.abs(camYaw - pcYaw)
			+ Math.abs(camPitch - pcPitch) > 1e-6;
		pcEye[0] = camEye[0];
		pcEye[1] = camEye[1];
		pcEye[2] = camEye[2];
		pcYaw = camYaw;
		pcPitch = camPitch;

		// shadow depth pass (light POV) -> shadowMap, then the G-buffer samples it.
		shadowPass(shadowMap, shadowDepth, shFlatShader, sceneBuf, Std.int(scene.length / STRIDE), shHeroShader, heroBuf, heroCount, heroIdx, heroModel,
			lightMvp);

		geometryPass(gShader, sceneBuf, Std.int(scene.length / STRIDE), matShader, heroBuf, heroCount, heroIdx, heroModel, albedoTex, normalTex, shadowMap,
			lightMvp, gColor, gNormal, gPosition, gDepth, proj, view, lightView);

		// SSAO folds AO into the lit color; fog -> bloom -> outline build the look.
		// (proj[0], proj[5]) are the two projection scalars SSAO needs to project a
		// view-space sample point back to uv.
		ssaoPass(texA, ssaoShader, quadBufF, gColor, gNormal, gPosition, proj[0], proj[5]);
		blitFog(texB, fogShader, quadBufF, texA, gPosition);
		blit(bloomA, brightShader, quadBufF, texB);
		blit(bloomB, blurHShader, quadBufF, bloomA);
		blit(bloomA, blurVShader, quadBufF, bloomB);
		blitCombine(texA, combineShader, quadBufF, texB, bloomA);
		blitOutline(texB, outlineShader, quadBufF, texA, gNormal, gPosition); // texB = beauty

		// Water: composite a flow-mapped, refracting, foaming plane at y = WATER_Y
		// over the scene (reflection/refraction/foam/flow). texB -> texA.
		if (lua.Os.getenv("LUB_SFB_NOWATER") != null) {
			blit(texA, pShader, quadBufF, texB);
		} else {
			waterPass(texA, waterShader, quadBufF, texB, gPosition, flowTex, waterNrmTex, invView, tAccum, WATER_Y, proj[0], proj[5]);
		}

		// Depth of field: blur a copy of the beauty (texA) through the bloom
		// buffers, then lerp by circle-of-confusion -> texB.
		blit(bloomB, blurHShader, quadBufF, texA);
		blit(bloomA, blurVShader, quadBufF, bloomB);
		blitDof(texB, dofShader, quadBufF, texA, bloomA, gPosition); // texB = beauty

		// Camera motion blur, only when the camera actually moved (keeps a still
		// camera a clean, deterministic no-op).
		var beauty = texB;
		if (camMoved) {
			motionPass(texA, motionShader, quadBufF, texB, gPosition, reproj);
			beauty = texA;
		}
		var outBuf = (beauty == texB) ? texA : texB;

		// Parameterised screen effect runs offscreen (the swapchain pass stays free
		// of a fragment uniform block); debug modes sample the raw G-buffer.
		var screenSrc = (mode == 6) ? gNormal : (mode == 7) ? gPosition : (mode == 8) ? shadowMap : beauty;
		screenPass(outBuf, screenShader, quadBufF, screenSrc, mode);
		var gradeOut = (outBuf == texA) ? texB : texA;
		gradePass(gradeOut, gradeShader, quadBufF, outBuf, lutTex, tAccum);
		present(pShader, quadBuf, gradeOut);
	}

	// ---- resource + pass helpers (kept out of onFrame to stay under Lua's
	// 200-locals-per-function limit; uniforms live in vertex stages only —
	// a fragment-stage uniform block next to sampled textures gets an
	// inconsistent descriptor binding in the backend) ----

	static function target(key:String, w:Int, h:Int, fmt:Int, filter:Int):Dynamic {
		return Gfx.useTexture(key, w, h, fmt, null, 1, {target: true, filter: filter, wrap: Gfx.CLAMP});
	}

	static function fsShader(key:String, fsPath:String):Dynamic {
		return shader2(key, "12_quad.vs.slang", fsPath);
	}

	static function shader2(key:String, vsPath:String, fsPath:String):Dynamic {
		var v = Io.loadText("samples/12_sfb/data/" + vsPath);
		var f = Io.loadText("samples/12_sfb/data/" + fsPath);
		if (v.text == null || f.text == null)
			return null;
		return Gfx.useShader(key, v.text, f.text, v.version ^ f.version);
	}

	static inline function black():lua.Table<Int, Float> {
		return lua.Table.fromArray([0.0, 0.0, 0.0, 1.0]);
	}

	// shadow depth pass: render flat + hero from the light's POV into shadowMap.
	static function shadowPass(shadowMap:Dynamic, shadowDepth:Dynamic, flatShader:Dynamic, sceneBuf:Dynamic, count:Int, heroShader:Dynamic, heroBuf:Dynamic,
			heroCount:Int, heroIdx:Dynamic, heroModel:Array<Float>, lightMvp:Array<Float>) {
		var lmvp = lua.Table.fromArray(lightMvp);
		var mv = lua.Table.fromArray(heroModel);
		Gfx.beginPass({
			target: shadowMap,
			depth_target: shadowDepth,
			clear_color: lua.Table.fromArray([1.0, 1.0, 1.0, 1.0]),
			clear_depth: 1
		});
		Gfx.draw(count, {verts: sceneBuf, uniforms: {light_mvp: lmvp}}, {
			shader: flatShader,
			depth: true,
			depth_write: true,
			cull: Gfx.NONE
		});
		var opts = {
			shader: heroShader,
			depth: true,
			depth_write: true,
			cull: Gfx.NONE
		};
		if (heroIdx != null) {
			Gfx.draw(heroCount, {verts: heroBuf, indices: heroIdx, uniforms: {light_mvp: lmvp, model: mv}}, opts);
		} else {
			Gfx.draw(heroCount, {verts: heroBuf, uniforms: {light_mvp: lmvp, model: mv}}, opts);
		}
		Gfx.endPass();
	}

	static function geometryPass(shader:Dynamic, sceneBuf:Dynamic, count:Int, matShader:Dynamic, heroBuf:Dynamic, heroCount:Int, heroIdx:Dynamic,
			heroModel:Array<Float>, albedoTex:Dynamic, normalTex:Dynamic, shadowMap:Dynamic, lightMvp:Array<Float>, gColor:Dynamic, gNormal:Dynamic,
			gPosition:Dynamic, gDepth:Dynamic, proj:Array<Float>, view:Array<Float>, lightView:Array<Float>) {
		var lt = lua.Table.fromArray([lightView[0], lightView[1], lightView[2], 0.0]);
		var pv = lua.Table.fromArray(proj);
		var vv = lua.Table.fromArray(view);
		var mv = lua.Table.fromArray(heroModel);
		var lmvp = lua.Table.fromArray(lightMvp);
		Gfx.beginPass({
			targets: lua.Table.fromArray([gColor, gNormal, gPosition]),
			depth_target: gDepth,
			clear_colors: lua.Table.fromArray([
				lua.Table.fromArray([0.09, 0.12, 0.15, 1.0]),
				lua.Table.fromArray([0.5, 0.5, 1.0, 0.0]),
				lua.Table.fromArray([0.0, 0.0, 0.0, 0.0]),
			]),
			clear_depth: 1,
		});
		// flat-shaded scene objects
		Gfx.draw(count, {
			verts: sceneBuf,
			shadow_map: shadowMap,
			uniforms: {
				proj: pv,
				view: vv,
				light_mvp: lmvp,
				light_dir_view: lt
			}
		}, {
			shader: shader,
			depth: true,
			depth_write: true,
			cull: Gfx.NONE
		});
		// textured + normal-mapped hero (same G-buffer); indexed for glTF meshes.
		var opts = {
			shader: matShader,
			depth: true,
			depth_write: true,
			cull: Gfx.NONE
		};
		if (heroIdx != null) {
			Gfx.draw(heroCount, {
				verts: heroBuf,
				indices: heroIdx,
				albedo: albedoTex,
				normalmap: normalTex,
				shadow_map: shadowMap,
				uniforms: {
					proj: pv,
					view: vv,
					model: mv,
					light_mvp: lmvp,
					light_dir_view: lt
				}
			}, opts);
		} else {
			Gfx.draw(heroCount, {
				verts: heroBuf,
				albedo: albedoTex,
				normalmap: normalTex,
				shadow_map: shadowMap,
				uniforms: {
					proj: pv,
					view: vv,
					model: mv,
					light_mvp: lmvp,
					light_dir_view: lt
				}
			}, opts);
		}
		Gfx.endPass();
	}

	// single-texture blit, no uniform block (bright / blur / passthrough).
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

	// SSAO + screen carry their (vertex-stage) uniform via the draw's `uniforms`.
	static function ssaoPass(targ:Dynamic, shader:Dynamic, quad:Dynamic, gColor:Dynamic, gNormal:Dynamic, gPosition:Dynamic, p00:Float, p11:Float) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {
			verts: quad,
			scene: gColor,
			gnormal: gNormal,
			gpos: gPosition,
			uniforms: {params: lua.Table.fromArray([p00, p11, 0.0, 0.0])}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function screenPass(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic, mode:Int) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {
			verts: quad,
			scene: tex,
			uniforms: {params: lua.Table.fromArray([mode, 0.004, 0.0, 0.0])}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function gradePass(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic, lut:Dynamic, time:Float) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {
			verts: quad,
			scene: tex,
			lut: lut,
			uniforms: {params: lua.Table.fromArray([time, 0.025, 0.65, 2.2])}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function waterPass(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic, gPosition:Dynamic, flowTex:Dynamic, wnTex:Dynamic, iv:Array<Float>,
			time:Float, waterY:Float, p00:Float, p11:Float) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {
			verts: quad,
			scene: tex,
			gpos: gPosition,
			flowmap: flowTex,
			waternormal: wnTex,
			uniforms: {
				ir0: lua.Table.fromArray([iv[0], iv[1], iv[2], iv[3]]),
				ir1: lua.Table.fromArray([iv[4], iv[5], iv[6], iv[7]]),
				ir2: lua.Table.fromArray([iv[8], iv[9], iv[10], iv[11]]),
				params: lua.Table.fromArray([time, waterY, p00, p11]),
			}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function motionPass(targ:Dynamic, shader:Dynamic, quad:Dynamic, tex:Dynamic, gPosition:Dynamic, m:Array<Float>) {
		Gfx.beginPass({target: targ, clear_color: black()});
		Gfx.draw(6, {
			verts: quad,
			scene: tex,
			gpos: gPosition,
			uniforms: {
				r0: lua.Table.fromArray([m[0], m[1], m[2], m[3]]),
				r1: lua.Table.fromArray([m[4], m[5], m[6], m[7]]),
				r2: lua.Table.fromArray([m[8], m[9], m[10], m[11]]),
				r3: lua.Table.fromArray([m[12], m[13], m[14], m[15]]),
			}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}

	static function present(shader:Dynamic, quad:Dynamic, tex:Dynamic) {
		Gfx.beginPass({target: Gfx.mainTex, clear_color: black()});
		Gfx.draw(6, {verts: quad, scene: tex}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}
}
