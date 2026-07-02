import lub.Lub;
import lub.Gfx;
import lub.Io;
import lubx.Png;

class Texture03 {
	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	public static function onFrame() {
		var vsResult = Io.loadText("samples/03_texture/data/03_tex.vs.slang");
		var fsResult = Io.loadText("samples/03_texture/data/03_tex.fs.slang");
		var vertsResult = Io.loadFloats("samples/03_texture/data/03_tex.verts.lua");
		var pngResult = Png.load("samples/03_texture/data/03_tex.png");
		var vs:String = vsResult.text;
		var vsv:Int = vsResult.version;
		var fs:String = fsResult.text;
		var fsv:Int = fsResult.version;
		var verts:Dynamic = vertsResult.data;
		var vv:Int = vertsResult.version;
		var px:Dynamic = pngResult.bytes;
		var w:Int = pngResult.width;
		var h:Int = pngResult.height;
		var fmt:Int = pngResult.format;
		var pv:Int = pngResult.version;
		if (vs == null || fs == null || verts == null || px == null)
			return;

		var s = Gfx.useShader("tex_shader", vs, fs, vsv * 31 + fsv);
		var b = Gfx.useBuffer("tex_verts", Gfx.VERTEX, verts, vv);
		var t = Gfx.useTexture("tex_chk", w, h, fmt, px, pv);
		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0])
		});
		Gfx.draw(3, {verts: b, diffuse: t}, {shader: s, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}
}
