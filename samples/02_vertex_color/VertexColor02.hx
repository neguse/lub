import lubx.Boot;
import lub.Gfx;
import lub.Io;

class VertexColor02 {
	public static function main() {}

	public static function onInit() {
		Boot.config({});
	}

	public static function onFrame() {
		var vsResult = Io.loadText("samples/02_vertex_color/data/02_vcol.vs.slang");
		var fsResult = Io.loadText("samples/02_vertex_color/data/02_vcol.fs.slang");
		var vertsResult = Io.loadFloats("samples/02_vertex_color/data/02_vcol.verts.lua");
		var vs:String = vsResult.text;
		var vsv:Int = vsResult.version;
		var fs:String = fsResult.text;
		var fsv:Int = fsResult.version;
		var verts:Dynamic = vertsResult.data;
		var vv:Int = vertsResult.version;
		if (vs == null || fs == null || verts == null)
			return;

		var s = Gfx.useShader("vc_shader", vs, fs, vsv * 31 + fsv);
		var b = Gfx.useBuffer("vc_verts", Gfx.VERTEX, verts, vv);
		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0])
		});
		Gfx.draw(3, {verts: b}, {shader: s, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}
}
