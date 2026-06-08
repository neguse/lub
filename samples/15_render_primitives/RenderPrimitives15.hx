import lub.Gfx;
import lub.Io;
import lub.Lub;
import lua.Table;

class RenderPrimitives15 {
	static inline var W:Int = 640;
	static inline var H:Int = 360;
	static inline var RTW:Int = 160;
	static inline var RTH:Int = 90;

	static var quad:Dynamic = null;
	static var tri:Dynamic = null;
	static var version:Int = 0;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend, width: W, height: H});
	}

	static function target(key:String, fmt:Int, filter:Int = -1, storage:Bool = false):Dynamic {
		if (filter < 0)
			filter = Gfx.NEAREST;
		return Gfx.useTexture(key, RTW, RTH, fmt, null, 1, {
			target: true,
			filter: filter,
			wrap: Gfx.CLAMP,
			storage: storage
		});
	}

	static function ensureGeometry() {
		if (quad == null) {
			quad = Gfx.useBuffer("rp15_quad", Gfx.VERTEX, Table.fromArray([
				-1.0, -1.0, 0.0, 1.0,
				1.0, -1.0, 1.0, 1.0,
				1.0, 1.0, 1.0, 0.0,
				-1.0, -1.0, 0.0, 1.0,
				1.0, 1.0, 1.0, 0.0,
				-1.0, 1.0, 0.0, 0.0
			]), 1);
		}
		if (tri == null) {
			tri = Gfx.useBuffer("rp15_tri", Gfx.VERTEX, Table.fromArray([
				-0.75, -0.70, 0.25,
				0.85, -0.65, 0.75,
				-0.10, 0.82, 0.55
			]), 1);
		}
	}

	static function shader2(key:String, vsPath:String, fsPath:String):Dynamic {
		var vs = Io.loadText("samples/15_render_primitives/data/" + vsPath);
		var fs = Io.loadText("samples/15_render_primitives/data/" + fsPath);
		if (vs.text == null || fs.text == null)
			return null;
		return Gfx.useShader(key, vs.text, fs.text, vs.version ^ fs.version);
	}

	static function shaderC(key:String, csPath:String):Dynamic {
		var cs = Io.loadText("samples/15_render_primitives/data/" + csPath);
		if (cs.text == null)
			return null;
		return Gfx.useShaderCompute(key, cs.text, cs.version);
	}

	static function drawPanel(shader:Dynamic, tex:Dynamic, x:Float, y:Float, sx:Float, sy:Float, tint:Array<Float>, mode:Float) {
		Gfx.draw(6, {
			verts: quad,
			scene: tex,
			uniforms: {
				transform: Table.fromArray([sx, sy, x, y]),
				tint: Table.fromArray(tint),
				mode: Table.fromArray([mode, 0.0, 0.0, 0.0])
			}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
	}

	public static function onFrame() {
		ensureGeometry();
		version++;

		var fill = shader2("rp15_fill", "15_quad.vs.slang", "15_fill.fs.slang");
		var depth = shader2("rp15_depth_scene", "15_depth_scene.vs.slang", "15_depth_scene.fs.slang");
		var present = shader2("rp15_present", "15_present.vs.slang", "15_present.fs.slang");
		var compute = shaderC("rp15_compute_tex", "15_storage.cs.slang");
		if (fill == null || depth == null || present == null || compute == null)
			return;

		var r16 = target("rp15_r16f", Gfx.R16F);
		var rg16 = target("rp15_rg16f", Gfx.RG16F);
		var r32 = target("rp15_r32f", Gfx.R32F);
		var depthColor = target("rp15_depth_color", Gfx.RGBA8);
		var depthTex = target("rp15_depth", Gfx.DEPTH32F);
		var storageTex = target("rp15_storage", Gfx.RGBA16F, Gfx.LINEAR, true);

		Gfx.beginPass({target: r16, clear_color: Table.fromArray([0.0, 0.0, 0.0, 1.0])});
		Gfx.draw(6, {verts: quad, uniforms: {fill: Table.fromArray([0.25, 0.0, 0.0, 1.0])}}, {shader: fill, depth: false, cull: Gfx.NONE});
		Gfx.endPass();

		Gfx.beginPass({target: rg16, clear_color: Table.fromArray([0.0, 0.0, 0.0, 1.0])});
		Gfx.draw(6, {verts: quad, uniforms: {fill: Table.fromArray([0.1, 0.85, 0.0, 1.0])}}, {shader: fill, depth: false, cull: Gfx.NONE});
		Gfx.endPass();

		Gfx.beginPass({target: r32, clear_color: Table.fromArray([0.0, 0.0, 0.0, 1.0])});
		Gfx.draw(6, {verts: quad, uniforms: {fill: Table.fromArray([0.85, 0.0, 0.0, 1.0])}}, {shader: fill, depth: false, cull: Gfx.NONE});
		Gfx.endPass();

		Gfx.beginPass({
			target: depthColor,
			depth_target: depthTex,
			clear_color: Table.fromArray([0.02, 0.02, 0.04, 1.0]),
			clear_depth: 1.0
		});
		Gfx.draw(3, {verts: tri}, {shader: depth, depth: true, depth_write: true, cull: Gfx.NONE});
		Gfx.endPass();

		Gfx.dispatch(Std.int(Math.ceil(RTW / 8)), Std.int(Math.ceil(RTH / 8)), 1, {
			dst: storageTex,
			uniforms: {params: Table.fromArray([RTW, RTH, version * 0.01, 0.0])}
		}, {shader: compute});

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: Table.fromArray([0.025, 0.03, 0.04, 1.0])
		});
		drawPanel(present, r16, -0.66, 0.47, 0.29, 0.42, [1.0, 0.35, 0.25, 1.0], 0.0);
		drawPanel(present, rg16, 0.0, 0.47, 0.29, 0.42, [0.35, 1.0, 0.55, 1.0], 0.0);
		drawPanel(present, r32, 0.66, 0.47, 0.29, 0.42, [0.55, 0.72, 1.0, 1.0], 0.0);
		drawPanel(present, depthTex, -0.34, -0.48, 0.29, 0.42, [0.8, 0.9, 1.0, 1.0], 1.0);
		drawPanel(present, storageTex, 0.34, -0.48, 0.29, 0.42, [1.0, 1.0, 1.0, 1.0], 0.0);
		Gfx.endPass();
	}
}
