import lub.Lub;
import lub.Gfx;
import lub.Io;

class Mvp04 {
	static var t:Float = 0;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	// column-major 4x4 (Z-axis rotation)
	static function rotZ(theta:Float):lua.Table<Int, Float> {
		var c = Math.cos(theta);
		var s = Math.sin(theta);
		return lua.Table.fromArray([
			  c,   s, 0.0, 0.0,
			 -s,   c, 0.0, 0.0,
			0.0, 0.0, 1.0, 0.0,
			0.0, 0.0, 0.0, 1.0
		]);
	}

	public static function onFrame() {
		t = t + 1.0 / 60.0;
		var vsResult = Io.loadText("samples/04_mvp/data/04_mvp.vs.slang");
		var fsResult = Io.loadText("samples/04_mvp/data/04_mvp.fs.slang");
		var vertsResult = Io.loadFloats("samples/04_mvp/data/04_mvp.verts.lua");
		var vs:String = vsResult.text;
		var vsv:Int = vsResult.version;
		var fs:String = fsResult.text;
		var fsv:Int = fsResult.version;
		var verts:Dynamic = vertsResult.data;
		var vv:Int = vertsResult.version;
		if (vs == null || fs == null || verts == null)
			return;

		var s = Gfx.useShader("mvp_shader", vs, fs, vsv ^ fsv);
		var b = Gfx.useBuffer("mvp_verts", Gfx.VERTEX, verts, vv);
		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0])
		});
		Gfx.draw(3, {verts: b, uniforms: {mvp: rotZ(t)}}, {shader: s, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}
}
