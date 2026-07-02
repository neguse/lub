import lub.Lub;
import lub.Gfx;
import lub.Io;

// Compute writes 3 vertices (vec4 = position.xy + color.rg) into a storage
// buffer. The render pass then rebinds the same buffer as a VERTEX buffer
// and draws a triangle whose vertex positions/colors came from the GPU.
class Compute07 {
	// 3 vertices * 4 floats (vec2 pos + vec2 col) = 12 floats.
	static inline var VERT_FLOATS = 12;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	public static function onFrame() {
		var csR = Io.loadText("samples/07_compute/data/07_gen_verts.cs.slang");
		var vsR = Io.loadText("samples/07_compute/data/07_render.vs.slang");
		var fsR = Io.loadText("samples/07_compute/data/07_render.fs.slang");
		var cs:String = csR.text;
		var csv:Int = csR.version;
		var vs:String = vsR.text;
		var vsv:Int = vsR.version;
		var fs:String = fsR.text;
		var fsv:Int = fsR.version;
		if (cs == null || vs == null || fs == null)
			return;

		var vbuf = Gfx.useBuffer("compute_verts", Gfx.STORAGE, VERT_FLOATS, 1);
		var shC = Gfx.useShaderCompute("gen", cs, csv);
		var shR = Gfx.useShader("render", vs, fs, vsv * 31 + fsv);

		Gfx.dispatch(1, 1, 1, {out_verts: vbuf}, {shader: shC});

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.05, 0.05, 0.1, 1.0])
		});
		Gfx.draw(3, {verts: vbuf}, {shader: shR, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}
}
