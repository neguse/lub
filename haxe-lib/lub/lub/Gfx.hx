package lub;

@:multiReturn extern class GfxSize {
  var w: Int;
  var h: Int;
}

extern class Gfx {
  // pass
  @:native("begin_pass")         public static function beginPass(opts: Dynamic): Void;
  @:native("end_pass")           public static function endPass(): Void;
  // resources
  @:native("use_shader")         public static function useShader(key: String, vs: String, fs: String, version: Int): Dynamic;
  @:native("use_shader_compute") public static function useShaderCompute(key: String, src: String, version: Int): Dynamic;
  // `data` is `lua.Table<Int, Float>` for VERTEX/INDEX/STORAGE-with-data,
  // or an `Int` float-count for STORAGE-allocate-empty (compute output buffers).
  @:native("use_buffer")         public static function useBuffer(key: String, type: Int, data: Dynamic, version: Int): Dynamic;
  @:native("use_texture")        public static function useTexture(key: String, w: Int, h: Int, fmt: Int, px: Dynamic, version: Int, ?opts: Dynamic): Dynamic;
  // commands
  @:native("draw")               public static function draw(count: Int, bindings: Dynamic, opts: Dynamic): Void;
  @:native("dispatch")           public static function dispatch(x: Int, y: Int, z: Int, bindings: Dynamic, opts: Dynamic): Void;
  // capture
  @:native("capture")            public static function capture(path: String): Void;
  // current drawable size in pixels (swapchain / canvas) -> w, h
  @:native("size")               public static function size(): GfxSize;

  // globals
  @:native("main_tex")           public static var mainTex(default, null): Dynamic;

  // buffer type
  @:native("VERTEX")             public static var VERTEX(default, null): Int;
  @:native("INDEX")              public static var INDEX(default, null): Int;
  @:native("UNIFORM")            public static var UNIFORM(default, null): Int;
  @:native("STORAGE")            public static var STORAGE(default, null): Int;
  // pixel format
  @:native("RGBA8")              public static var RGBA8(default, null): Int;
  @:native("R8")                 public static var R8(default, null): Int;
  @:native("RG8")                public static var RG8(default, null): Int;
  @:native("RGBA16F")            public static var RGBA16F(default, null): Int;
  @:native("RGBA32F")            public static var RGBA32F(default, null): Int;
  @:native("DEPTH16")            public static var DEPTH16(default, null): Int;
  @:native("DEPTH24_STENCIL8")   public static var DEPTH24_STENCIL8(default, null): Int;
  @:native("DEPTH32F")           public static var DEPTH32F(default, null): Int;
  // load / store
  @:native("CLEAR")              public static var CLEAR(default, null): Int;
  @:native("LOAD")               public static var LOAD(default, null): Int;
  @:native("DONTCARE")           public static var DONTCARE(default, null): Int;
  @:native("STORE")              public static var STORE(default, null): Int;
  // blend / cull
  @:native("NONE")               public static var NONE(default, null): Int;
  @:native("ALPHA")              public static var ALPHA(default, null): Int;
  @:native("ADDITIVE")           public static var ADDITIVE(default, null): Int;
  @:native("MULTIPLY")           public static var MULTIPLY(default, null): Int;
  @:native("BACK")               public static var BACK(default, null): Int;
  @:native("FRONT")              public static var FRONT(default, null): Int;
  // primitive
  @:native("TRIANGLES")          public static var TRIANGLES(default, null): Int;
  @:native("TRIANGLE_STRIP")     public static var TRIANGLE_STRIP(default, null): Int;
  @:native("LINES")              public static var LINES(default, null): Int;
  @:native("LINE_STRIP")         public static var LINE_STRIP(default, null): Int;
  @:native("POINTS")             public static var POINTS(default, null): Int;
  // sampler
  @:native("LINEAR")             public static var LINEAR(default, null): Int;
  @:native("NEAREST")            public static var NEAREST(default, null): Int;
  @:native("REPEAT")             public static var REPEAT(default, null): Int;
  @:native("CLAMP")              public static var CLAMP(default, null): Int;
}
