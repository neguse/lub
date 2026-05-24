import lub.Lub;
import lub.Gfx;
import lub.Io;

class Deferred06 {
  static inline var RT_W = 256;
  static inline var RT_H = 256;

  public static function main() {}

  public static function onInit() {
    var backend: String = lua.Os.getenv("LUB_BACKEND");
    if (backend == null) backend = "sokol";
    Lub.config({ backend: backend });
  }

  public static function onFrame() {
    var gvsR = Io.loadText("samples/data/06_gbuffer.vs.slang");
    var gfsR = Io.loadText("samples/data/06_gbuffer.fs.slang");
    var gvertsR = Io.loadFloats("samples/data/06_gbuffer.verts.lua");
    var vvsR = Io.loadText("samples/data/06_view.vs.slang");
    var vfsR = Io.loadText("samples/data/06_view.fs.slang");
    var vvertsR = Io.loadFloats("samples/data/06_view.verts.lua");
    var gvs: String = gvsR.text;
    var gvsv: Int = gvsR.version;
    var gfs: String = gfsR.text;
    var gfsv: Int = gfsR.version;
    var gverts: Dynamic = gvertsR.data;
    var gvv: Int = gvertsR.version;
    var vvs: String = vvsR.text;
    var vvsv: Int = vvsR.version;
    var vfs: String = vfsR.text;
    var vfsv: Int = vfsR.version;
    var vverts: Dynamic = vvertsR.data;
    var vvv: Int = vvertsR.version;
    if (gvs == null || gfs == null || gverts == null
        || vvs == null || vfs == null || vverts == null) return;

    // G-buffer attachments. Two render-target textures of the same size.
    var gbuf0 = Gfx.useTexture("gbuf0", RT_W, RT_H, Gfx.RGBA8, null, 1,
                               { filter: Gfx.LINEAR, wrap: Gfx.CLAMP, target: true });
    var gbuf1 = Gfx.useTexture("gbuf1", RT_W, RT_H, Gfx.RGBA8, null, 1,
                               { filter: Gfx.LINEAR, wrap: Gfx.CLAMP, target: true });

    // G-buffer pass: MRT write. SV_Target0 -> gbuf0, SV_Target1 -> gbuf1.
    var shG = Gfx.useShader("gbuf_shader", gvs, gfs, gvsv ^ gfsv);
    var bG  = Gfx.useBuffer("gbuf_verts", Gfx.VERTEX, gverts, gvv);
    Gfx.beginPass({
      targets: lua.Table.fromArray([gbuf0, gbuf1]),
      clear_colors: lua.Table.fromArray([
        lua.Table.fromArray([0.1, 0.1, 0.15, 1.0]),
        lua.Table.fromArray([0.15, 0.1, 0.1, 1.0])
      ])
    });
    Gfx.draw(3, { verts: bG },
                { shader: shG, depth: false, cull: Gfx.NONE });
    Gfx.endPass();

    // View pass: split-screen visualization. Left half samples gbuf0,
    // right half samples gbuf1. Same shader; only the texture binding and
    // the (scale, offset) transform uniform differ.
    var shV = Gfx.useShader("view_shader", vvs, vfs, vvsv ^ vfsv);
    var bV  = Gfx.useBuffer("view_verts", Gfx.VERTEX, vverts, vvv);
    Gfx.beginPass({
      target: Gfx.mainTex,
      clear_color: lua.Table.fromArray([0.0, 0.0, 0.0, 1.0])
    });
    Gfx.draw(6, { verts: bV, gbuf: gbuf0,
                  uniforms: { transform: lua.Table.fromArray([0.5, 1.0, -0.5, 0.0]) } },
                { shader: shV, depth: false, cull: Gfx.NONE });
    Gfx.draw(6, { verts: bV, gbuf: gbuf1,
                  uniforms: { transform: lua.Table.fromArray([0.5, 1.0,  0.5, 0.0]) } },
                { shader: shV, depth: false, cull: Gfx.NONE });
    Gfx.endPass();
  }
}
