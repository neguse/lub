import lub.Lub;
import lub.Gfx;
import lub.Io;

class Triangle01 {
  public static function main() {}

  public static function onInit() {
    var backend: String = lua.Os.getenv("LUB_BACKEND");
    if (backend == null) backend = "sokol";
    Lub.config({ backend: backend });
  }

  public static function onFrame() {
    var vsResult = Io.loadText("samples/data/01_triangle.vs.slang");
    var fsResult = Io.loadText("samples/data/01_triangle.fs.slang");
    var vertsResult = Io.loadFloats("samples/data/01_triangle.verts.lua");
    var vs: String = vsResult.text;
    var vsv: Int = vsResult.version;
    var fs: String = fsResult.text;
    var fsv: Int = fsResult.version;
    var verts: Dynamic = vertsResult.data;
    var vv: Int = vertsResult.version;
    if (vs == null || fs == null || verts == null) return;

    var s = Gfx.useShader("tri_shader", vs, fs, vsv ^ fsv);
    var b = Gfx.useBuffer("tri_verts", Gfx.VERTEX, verts, vv);
    Gfx.beginPass({
      target: Gfx.mainTex,
      clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0])
    });
    Gfx.draw(3, { verts: b }, { shader: s, depth: false, cull: Gfx.NONE });
    Gfx.endPass();
  }
}
