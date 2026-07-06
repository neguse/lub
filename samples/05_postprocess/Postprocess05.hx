import lubx.Boot;
import lub.Gfx;
import lub.Io;

class Postprocess05 {
	static inline var RT_W = 256;
	static inline var RT_H = 256;

	public static function main() {}

	public static function onInit() {
		Boot.config({});
	}

	public static function onFrame() {
		var ovsR = Io.loadText("samples/05_postprocess/data/05_offscreen.vs.slang");
		var ofsR = Io.loadText("samples/05_postprocess/data/05_offscreen.fs.slang");
		var overtsR = Io.loadFloats("samples/05_postprocess/data/05_offscreen.verts.lua");
		var pvsR = Io.loadText("samples/05_postprocess/data/05_post.vs.slang");
		var pfsR = Io.loadText("samples/05_postprocess/data/05_post.fs.slang");
		var pvertsR = Io.loadFloats("samples/05_postprocess/data/05_post.verts.lua");
		var ovs:String = ovsR.text;
		var ovsv:Int = ovsR.version;
		var ofs:String = ofsR.text;
		var ofsv:Int = ofsR.version;
		var overts:Dynamic = overtsR.data;
		var ovv:Int = overtsR.version;
		var pvs:String = pvsR.text;
		var pvsv:Int = pvsR.version;
		var pfs:String = pfsR.text;
		var pfsv:Int = pfsR.version;
		var pverts:Dynamic = pvertsR.data;
		var pvv:Int = pvertsR.version;
		if (ovs == null || ofs == null || overts == null || pvs == null || pfs == null || pverts == null)
			return;

		var rt = Gfx.useTexture("rt_scene", RT_W, RT_H, Gfx.RGBA8, null, 1, {filter: Gfx.LINEAR, wrap: Gfx.CLAMP, target: true});

		var shOff = Gfx.useShader("off_shader", ovs, ofs, ovsv * 31 + ofsv);
		var bOff = Gfx.useBuffer("off_verts", Gfx.VERTEX, overts, ovv);
		Gfx.beginPass({
			target: rt,
			clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0])
		});
		Gfx.draw(3, {verts: bOff}, {shader: shOff, depth: false, cull: Gfx.NONE});
		Gfx.endPass();

		var shPost = Gfx.useShader("post_shader", pvs, pfs, pvsv * 31 + pfsv);
		var bPost = Gfx.useBuffer("post_verts", Gfx.VERTEX, pverts, pvv);
		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.0, 0.0, 0.0, 1.0])
		});
		Gfx.draw(6, {verts: bPost, scene: rt}, {shader: shPost, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}
}
