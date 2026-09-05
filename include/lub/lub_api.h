// lub の C API。runtime の immediate mode 層の上に引いた ABI で、Lua binding
// (src/lua_api.c)、.NET 実行の P/Invoke facade、tcs→C の生成コードはどれも
// この API への薄い詰め替えになる。設計は
// docs/log/2026-09-04-language-architecture-design.md の「C API 層」。
//
// 規則:
//   - context を第 1 引数に取り、typed な desc 構造体を受け、status を返す。
//     検証はこの層で行い、失敗は LUB_ERROR と lub_last_error() の文字列。
//   - 型は int32 / float / bool / UTF-8 の byte 列だけ。文字列は LubStr
//     (pointer + length、NUL 終端を要求しない)。
//   - ゲームの memory は呼び出しの間だけ借用する。runtime の memory は
//     LubView (frame の終わりまで有効) で返す。frame を跨いで生きるものは
//     runtime 所有の keyed resource で、ゲームは key と int32 の handle
//     だけ持つ。
//   - main thread 限定。
//
// 段階 3 の途中の形: gfx / input / sys / profiler / host / audio / io / png /
// font / ui / mesh がこの API を通る。残り (phys2d / phys3d) は順に移す。段階 4
// で この header は cs-lib/lub_stub.cs からの生成物になる。
#pragma once
#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct LubContext LubContext;

typedef enum LubStatus {
  LUB_OK = 0,
  LUB_ERROR = 1,
} LubStatus;

// UTF-8 の byte 列。ptr は len byte だけ有効で NUL 終端は要らない。
typedef struct LubStr {
  const char *ptr;
  int32_t len;
} LubStr;

// runtime の memory への view。frame 番号 `frame` の間だけ有効。
typedef struct LubView {
  const uint8_t *ptr;
  int32_t len;
  int32_t frame;
} LubView;

// runtime 所有の resource への handle。0 = 無し。resource が sweep されるまで
// 同じ値で、hot reload を跨いでも有効。
typedef int32_t LubHandle;

// 直近の LUB_ERROR の message。次の API 呼び出しまで有効。
const char *lub_last_error(LubContext *ctx);

// 現在の frame 番号 (LubView.frame と比較する)。
int32_t lub_frame_index(LubContext *ctx);

// ------------------------------------------------------------------ core

// config は on_init の中でだけ呼べる。省略は「変えない」: backend は len 0、
// width / height は 0、resource_sweep_after_frames / readback_depth は -1。
typedef struct LubConfigDesc {
  LubStr backend; // "native" / "sdlgpu" (web は無視して webgpu)
  int32_t width, height;
  int32_t resource_sweep_after_frames; // 0 = sweep しない
  int32_t readback_depth;              // 1..32
} LubConfigDesc;

LubStatus lub_config(LubContext *ctx, const LubConfigDesc *desc);
void lub_quit(LubContext *ctx);

// ----------------------------------------------------------------- input

// key は "space" / "a".."z" / "left" 等の名前。未知の名前は常に false。
bool lub_input_key_down(LubContext *ctx, LubStr key);
bool lub_input_key_pressed(LubContext *ctx, LubStr key);
bool lub_input_key_released(LubContext *ctx, LubStr key);
// button は SDL 準拠の 1 始まり (1 = 左、2 = 中、3 = 右)。
bool lub_input_mouse_down(LubContext *ctx, int32_t button);
bool lub_input_mouse_pressed(LubContext *ctx, int32_t button);
bool lub_input_mouse_released(LubContext *ctx, int32_t button);
void lub_input_mouse_pos(LubContext *ctx, float *x, float *y);
void lub_input_mouse_delta(LubContext *ctx, float *dx, float *dy);

// ------------------------------------------------------------------- sys

float lub_sys_actual_fps(LubContext *ctx);
bool lub_sys_is_web(LubContext *ctx);

// -------------------------------------------------------------- profiler

bool lub_profiler_enabled(LubContext *ctx);
void lub_profiler_begin_scope(LubContext *ctx, LubStr name);
// name は len 0 で「直近の scope」。
void lub_profiler_end_scope(LubContext *ctx, LubStr name);
void lub_profiler_reset(LubContext *ctx);
void lub_profiler_report(LubContext *ctx, LubStr label);

// ------------------------------------------------------------------ host

bool lub_host_available(LubContext *ctx);
void lub_host_send(LubContext *ctx, LubStr topic, LubStr payload);
// queue から 1 件取り出す。無ければ false。view は次の poll まで有効。
bool lub_host_poll(LubContext *ctx, LubView *topic, LubView *payload);

// ----------------------------------------------------------------- audio

typedef struct LubAudioPlayDesc {
  float volume; // 既定 1
  float pitch;  // 既定 1。0 = 停止、負 = 逆再生
  float pan;    // 既定 0
  bool loop;    // voice のみ
} LubAudioPlayDesc;

typedef struct LubAudioInfo {
  bool device;
  int32_t rate;
  int32_t voices;
  int32_t snds;
} LubAudioInfo;

// interleaved f32 PCM から snd を作る。samples は count 個 (channels の倍数)。
LubStatus lub_audio_pcm(LubContext *ctx, const float *samples, int32_t count,
                        int32_t channels, int32_t rate, int32_t *out_snd);
// file format bytes → f32 PCM の view (次の decode まで有効)。
LubStatus lub_audio_decode(LubContext *ctx, const uint8_t *data, int32_t len,
                           LubView *pcm, int32_t *channels, int32_t *rate);
bool lub_audio_play(LubContext *ctx, int32_t snd, const LubAudioPlayDesc *desc);
bool lub_audio_voice(LubContext *ctx, LubStr key, int32_t snd,
                     const LubAudioPlayDesc *desc);
bool lub_audio_free(LubContext *ctx, int32_t snd);
void lub_audio_master_volume(LubContext *ctx, float volume);
void lub_audio_info(LubContext *ctx, LubAudioInfo *out);

// -------------------------------------------------------------------- io

// file の load は毎フレーム呼べる即時モード API。runtime が path を key に
// cache し (mtime の fast path + 内容 hash)、結果は frame 有効の view。
typedef enum LubIoStatus {
  LUB_IO_STATUS_PENDING = 0, // web で取得中
  LUB_IO_STATUS_READY = 1,
  LUB_IO_STATUS_ERROR = 2,
} LubIoStatus;

typedef struct LubIoResult {
  int32_t status;  // LubIoStatus
  int32_t version; // 内容の hash。use_* の version にそのまま渡せる
  LubStr error;    // ERROR のとき
} LubIoResult;

// 平らな配列のメッシュ。vert_count 頂点、positions は vec3、normals vec3、
// uvs vec2、tangents vec4、colors vec3、metal_rough vec2、joints / weights は
// 頂点あたり 2 つ。NULL の配列は「無し」。indices は index_count 個。
typedef struct LubMeshData {
  const float *positions;
  const float *normals;
  const float *uvs;
  const float *tangents;
  const float *colors;
  const float *metal_rough;
  const float *joints;
  const float *weights;
  const uint32_t *indices;
  int32_t vert_count;
  int32_t index_count;
} LubMeshData;

typedef struct LubGltfMaterial {
  float base_color_factor[4];
  float metallic_factor;
  float roughness_factor;
  int32_t alpha_mode; // 0 = opaque, 1 = mask, 2 = blend
  float alpha_cutoff;
  bool double_sided;
  float normal_scale;
  LubStr base_color_path; // len 0 = 無し
  LubStr metallic_roughness_path;
  LubStr normal_path;
  LubStr name;
} LubGltfMaterial;

typedef struct LubGltfPrimitive {
  LubMeshData mesh;
  int32_t material_index; // -1 = 無し
} LubGltfPrimitive;

typedef struct LubGltfView {
  const LubGltfPrimitive *primitives;
  int32_t primitive_count;
  const LubGltfMaterial *materials;
  int32_t material_count;
} LubGltfView;

// interleave の頂点レイアウト。P = position、N = normal、U = uv、T = tangent、
// C = color、M = metal/rough、W = skin (j0, w0, j1, w1)。
typedef enum LubMeshLayout {
  LUB_MESH_LAYOUT_PN = 1,    // stride 6
  LUB_MESH_LAYOUT_PNU = 2,   // stride 8
  LUB_MESH_LAYOUT_PNUT = 3,  // stride 12
  LUB_MESH_LAYOUT_PNCM = 4,  // stride 11
  LUB_MESH_LAYOUT_PNCMW = 5, // stride 15
} LubMeshLayout;

// 結果は r->status が READY のときだけ有効 (view は frame の終わりまで)。
LubStatus lub_io_load_text(LubContext *ctx, LubStr path, LubView *text,
                           LubIoResult *r);
// `return { ... }` 形式の Lua ファイルを float 列として読む。
LubStatus lub_io_load_floats(LubContext *ctx, LubStr path, const float **data,
                             int32_t *count, LubIoResult *r);
LubStatus lub_io_load_gltf(LubContext *ctx, LubStr path, LubGltfView *mesh,
                           LubIoResult *r);
// mesh を layout で interleave して out に書く。戻り値は必要な float 数で、
// out == NULL か cap が足りなければ書かずに必要数だけ返す。
int32_t lub_mesh_interleave(LubContext *ctx, const LubMeshData *mesh,
                            int32_t layout, float *out, int32_t cap);

// ------------------------------------------------------------------- png

// RGBA8 に decode した画像。format は常に LUB_GFX_PIXEL_FORMAT_RGBA8。
LubStatus lub_png_load(LubContext *ctx, LubStr path, LubView *pixels,
                       int32_t *w, int32_t *h, int32_t *format, int32_t *stride,
                       LubIoResult *r);
LubStatus lub_png_write(LubContext *ctx, LubStr path, const uint8_t *pixels,
                        int32_t len, int32_t w, int32_t h, int32_t stride);

// ------------------------------------------------------------------ mesh

// SDF 木のノード (docs/log/2026-07-04-sdf-tree-design.md)。木は node の配列と
// index で平らに渡す。params の意味は op ごと:
//   SPHERE r / BOX hx hy hz / CAPSULE ax ay az bx by bz r /
//   TORUS rmajor rminor / MOVE x y z / ROTATE qx qy qz qw / SCALE s /
//   PAINT cr cg cb metallic roughness / BONE px py pz (name 必須) /
//   SMIN k / SSUB k。a は xform / mirror / paint / bone の子、combine は a と
//   b。
typedef enum LubSdfOp {
  LUB_SDF_OP_SPHERE = 1,
  LUB_SDF_OP_BOX = 2,
  LUB_SDF_OP_CAPSULE = 3,
  LUB_SDF_OP_TORUS = 4,
  LUB_SDF_OP_MOVE = 5,
  LUB_SDF_OP_ROTATE = 6,
  LUB_SDF_OP_SCALE = 7,
  LUB_SDF_OP_MIRROR_X = 8,
  LUB_SDF_OP_PAINT = 9,
  LUB_SDF_OP_BONE = 10,
  LUB_SDF_OP_UNION = 11,
  LUB_SDF_OP_SMIN = 12,
  LUB_SDF_OP_SUBTRACT = 13,
  LUB_SDF_OP_SSUB = 14,
  LUB_SDF_OP_INTERSECT = 15,
} LubSdfOp;

typedef struct LubSdfNode {
  int32_t op; // LubSdfOp
  float params[8];
  int32_t a, b; // 子の index。-1 = 無し
  LubStr name;  // BONE の名前
} LubSdfNode;

typedef struct LubSdfBone {
  LubStr name;
  float pivot[3];
} LubSdfBone;

// sdf のメッシュ。mesh は positions / normals / indices に加えて colors
// (vec3) / metal_rough (vec2)、bone があれば joints / weights も持つ。
typedef struct LubSdfMesh {
  LubMeshData mesh;
  const LubSdfBone *bones;
  int32_t bone_count;
  float bounds_min[3];
  float bounds_max[3];
  float cell;
} LubSdfMesh;

// grid は nx*ny*nz の signed distance (x が最速)。結果の view は次の mesh_*
// 呼び出しまで有効。
LubStatus lub_mesh_surface_nets(LubContext *ctx, const float *grid, int32_t nx,
                                int32_t ny, int32_t nz, float cell, float ox,
                                float oy, float oz, LubMeshData *out);
// n は最長軸の cell 数 (4..512)。skin_k は bone の重みの blend 幅 (0 = 0.1)。
LubStatus lub_mesh_sdf(LubContext *ctx, const LubSdfNode *nodes, int32_t count,
                       int32_t root, int32_t n, float skin_k, LubSdfMesh *out);

// ------------------------------------------------------------------ font

// TTF glyph の純関数 utility。ttf は font ファイルの byte 列で毎回渡す。
typedef struct LubFontMetrics {
  float ascent, descent, line_gap; // em 単位
} LubFontMetrics;

typedef struct LubFontGlyph {
  bool found;   // false = glyph が無い (呼び出し側が他の font に fallback)
  int32_t w, h; // bitmap の大きさ (空 glyph は 0)
  int32_t xoff, yoff; // baseline 原点からの左上 offset (px、y-down)
  float advance;      // px
  LubView bytes;      // w*h の R8 coverage (行優先、上から)。空 glyph は len 0
} LubFontGlyph;

typedef struct LubFontGlyphMesh {
  bool found;
  LubMeshData mesh; // em 単位、y-up、baseline 原点、z = 0、normal +z
  float advance;    // em
} LubFontGlyphMesh;

LubStatus lub_font_metrics(LubContext *ctx, LubStr ttf, LubFontMetrics *out);
// px は 1 em あたりの pixel 数。view は次の font_* 呼び出しまで有効。
LubStatus lub_font_glyph(LubContext *ctx, LubStr ttf, int32_t codepoint,
                         float px, LubFontGlyph *out);
// tolerance は曲線の平坦化誤差 (em)。0 = 既定 0.002。
LubStatus lub_font_glyph_mesh(LubContext *ctx, LubStr ttf, int32_t codepoint,
                              float tolerance, LubFontGlyphMesh *out);
LubStatus lub_font_kern(LubContext *ctx, LubStr ttf, int32_t cp1, int32_t cp2,
                        float *out);

// -------------------------------------------------------------------- ui

// Dear ImGui の debug UI (immediate mode)。frame が開いていないときの widget
// 呼び出しは既定値を返して何もしない。render は begin_pass の中で 1 回。
LubStatus lub_ui_render(LubContext *ctx);
bool lub_ui_begin_window(LubContext *ctx, LubStr title);
void lub_ui_end_window(LubContext *ctx);
void lub_ui_text(LubContext *ctx, LubStr text);
bool lub_ui_button(LubContext *ctx, LubStr label);
bool lub_ui_checkbox(LubContext *ctx, LubStr label, bool value);
float lub_ui_slider_float(LubContext *ctx, LubStr label, float value, float min,
                          float max);
int32_t lub_ui_slider_int(LubContext *ctx, LubStr label, int32_t value,
                          int32_t min, int32_t max);
// speed / min / max は 0 で既定 (speed 1、範囲無し)。
float lub_ui_drag_float(LubContext *ctx, LubStr label, float value, float speed,
                        float min, float max);
// rgb は in/out。
void lub_ui_color_edit3(LubContext *ctx, LubStr label, float rgb[3]);
void lub_ui_separator(LubContext *ctx);
void lub_ui_same_line(LubContext *ctx);
bool lub_ui_tree_node(LubContext *ctx, LubStr label, bool default_open);
void lub_ui_tree_pop(LubContext *ctx);
// 初回配置だけ指定する (ユーザのドラッグは活かす)。
void lub_ui_set_next_window(LubContext *ctx, float x, float y, float w,
                            float h);
bool lub_ui_want_capture_mouse(LubContext *ctx);

// ------------------------------------------------------------------- gfx

typedef enum LubGfxBufferType {
  LUB_GFX_BUFFER_TYPE_VERTEX = 1,
  LUB_GFX_BUFFER_TYPE_INDEX = 2,
  LUB_GFX_BUFFER_TYPE_UNIFORM = 3,
  LUB_GFX_BUFFER_TYPE_STORAGE = 4,
} LubGfxBufferType;

typedef enum LubGfxPixelFormat {
  LUB_GFX_PIXEL_FORMAT_RGBA8 = 1,
  LUB_GFX_PIXEL_FORMAT_R8 = 2,
  LUB_GFX_PIXEL_FORMAT_RG8 = 3,
  LUB_GFX_PIXEL_FORMAT_R16F = 4,
  LUB_GFX_PIXEL_FORMAT_RG16F = 5,
  LUB_GFX_PIXEL_FORMAT_R32F = 6,
  LUB_GFX_PIXEL_FORMAT_RGBA16F = 7,
  LUB_GFX_PIXEL_FORMAT_RGBA32F = 8,
  LUB_GFX_PIXEL_FORMAT_DEPTH16 = 9,
  LUB_GFX_PIXEL_FORMAT_DEPTH24_STENCIL8 = 10,
  LUB_GFX_PIXEL_FORMAT_DEPTH32F = 11,
} LubGfxPixelFormat;

typedef enum LubGfxLoadAction {
  LUB_GFX_LOAD_ACTION_CLEAR = 1,
  LUB_GFX_LOAD_ACTION_LOAD = 2,
  LUB_GFX_LOAD_ACTION_DONT_CARE = 3,
} LubGfxLoadAction;

typedef enum LubGfxBlend {
  LUB_GFX_BLEND_NONE = 1,
  LUB_GFX_BLEND_ALPHA = 2,
  LUB_GFX_BLEND_ADDITIVE = 3,
  LUB_GFX_BLEND_MULTIPLY = 4,
} LubGfxBlend;

typedef enum LubGfxCull {
  LUB_GFX_CULL_NONE = 1,
  LUB_GFX_CULL_BACK = 2,
  LUB_GFX_CULL_FRONT = 3,
} LubGfxCull;

typedef enum LubGfxPrimitive {
  LUB_GFX_PRIMITIVE_TRIANGLES = 1,
  LUB_GFX_PRIMITIVE_TRIANGLE_STRIP = 2,
  LUB_GFX_PRIMITIVE_LINES = 3,
  LUB_GFX_PRIMITIVE_LINE_STRIP = 4,
  LUB_GFX_PRIMITIVE_POINTS = 5,
} LubGfxPrimitive;

typedef enum LubGfxFilter {
  LUB_GFX_FILTER_LINEAR = 1,
  LUB_GFX_FILTER_NEAREST = 2,
} LubGfxFilter;

typedef enum LubGfxWrap {
  LUB_GFX_WRAP_REPEAT = 1,
  LUB_GFX_WRAP_CLAMP = 2,
} LubGfxWrap;

#define LUB_GFX_MAX_COLOR_TARGETS 4

// swapchain を指す特別な handle (lub_gfx_main_tex() が返す)。
#define LUB_GFX_MAIN_TEX ((LubHandle) - 1)

// pass の宣言。n_targets == 1 && targets[0] == LUB_GFX_MAIN_TEX で swapchain、
// n_targets == 0 で depth-only (depth_target 必須)。
typedef struct LubGfxPassDesc {
  int32_t n_targets;
  LubHandle targets[LUB_GFX_MAX_COLOR_TARGETS];
  float clear_color[LUB_GFX_MAX_COLOR_TARGETS][4];
  LubHandle depth_target; // 0 = 無し (swapchain pass は既定の depth を使う)
  float clear_depth;      // 既定 1.0
  int32_t load;           // LubGfxLoadAction。0 = CLEAR
} LubGfxPassDesc;

typedef struct LubGfxTextureDesc {
  int32_t w, h;
  int32_t format;        // LubGfxPixelFormat
  const uint8_t *pixels; // NULL = data 無し。呼び出しの間だけ借用
  int32_t pixels_len;
  int32_t filter; // LubGfxFilter。0 = LINEAR (depth は NEAREST 固定)
  int32_t wrap;   // LubGfxWrap。0 = REPEAT
  bool target;    // render target として使う
  bool storage;   // compute の storage texture として使う
} LubGfxTextureDesc;

// 名前つきの resource 束縛 (shader の reflection 名で結ぶ)。
typedef struct LubGfxBinding {
  LubStr name;
  LubHandle handle;
} LubGfxBinding;

// uniform block のメンバ値。name は reflection のメンバ名。
typedef struct LubGfxUniform {
  LubStr name;
  const float *values;
  int32_t count;
} LubGfxUniform;

typedef struct LubGfxDrawDesc {
  LubHandle shader;
  int32_t vertex_count;
  int32_t instance_count; // 0 = 1
  int32_t blend;          // LubGfxBlend。0 = NONE
  int32_t cull;           // LubGfxCull。0 = BACK
  int32_t primitive;      // LubGfxPrimitive。0 = TRIANGLES
  bool depth_test;        // 既定値 (true) の補完は binding 側の仕事
  bool depth_write;
  // buffers: name "indices" = index buffer、"instances" = per-instance の
  // vertex buffer、それ以外 = vertex buffer。
  const LubGfxBinding *buffers;
  int32_t n_buffers;
  const LubGfxBinding *textures;
  int32_t n_textures;
  const LubGfxUniform *uniforms;
  int32_t n_uniforms;
} LubGfxDrawDesc;

typedef struct LubGfxDispatchDesc {
  LubHandle shader;
  int32_t groups_x, groups_y, groups_z;
  const LubGfxBinding *buffers; // storage buffer
  int32_t n_buffers;
  const LubGfxBinding
      *textures; // sampled / storage texture (reflection で判定)
  int32_t n_textures;
  const LubGfxUniform *uniforms;
  int32_t n_uniforms;
} LubGfxDispatchDesc;

typedef enum LubGfxReadbackStatus {
  LUB_GFX_READBACK_STATUS_PROCESSING = 0,
  LUB_GFX_READBACK_STATUS_READY = 1,
  LUB_GFX_READBACK_STATUS_ERROR = 2,
  LUB_GFX_READBACK_STATUS_DROPPED = 3,
} LubGfxReadbackStatus;

typedef struct LubGfxReadbackResult {
  int32_t status; // LubGfxReadbackStatus
  LubView pixels; // READY のとき。次の lub_gfx_readback か frame の終わりまで
  int32_t w, h;
  int32_t format; // LubGfxPixelFormat
  int32_t stride;
  int32_t token; // READY / ERROR / DROPPED のとき、要求時に渡した token
  LubStr error;  // ERROR のとき
} LubGfxReadbackResult;

LubStatus lub_gfx_begin_pass(LubContext *ctx, const LubGfxPassDesc *desc);
LubStatus lub_gfx_end_pass(LubContext *ctx);
LubHandle lub_gfx_main_tex(LubContext *ctx);
void lub_gfx_size(LubContext *ctx, int32_t *w, int32_t *h);

// use_*: key で宣言し handle を返す。version は NULL で「内容が変わった」
// 宣言 (runtime が新しい実効 version を発行)、非 NULL で「その version と
// 同じなら upload を省略してよい」主張。
LubStatus lub_gfx_use_shader(LubContext *ctx, LubStr key, LubStr vs, LubStr fs,
                             const int32_t *version, LubHandle *out);
LubStatus lub_gfx_use_shader_compute(LubContext *ctx, LubStr key, LubStr cs,
                                     const int32_t *version, LubHandle *out);
// data: VERTEX / STORAGE は float 列、INDEX は uint32 列 (bytes で渡す)。
// data == NULL && bytes > 0 は STORAGE の空確保。
LubStatus lub_gfx_use_buffer(LubContext *ctx, LubStr key, int32_t type,
                             const void *data, int32_t bytes,
                             const int32_t *version, LubHandle *out);
LubStatus lub_gfx_use_texture(LubContext *ctx, LubStr key,
                              const LubGfxTextureDesc *desc,
                              const int32_t *version, LubHandle *out);

// handle の key と実効 version。handle が stale (sweep 済み) なら LUB_ERROR。
LubStatus lub_gfx_resource_info(LubContext *ctx, LubHandle handle, LubStr *key,
                                int32_t *version);
// key から handle を引く。無ければ 0。
LubHandle lub_gfx_lookup(LubContext *ctx, LubStr key);

LubStatus lub_gfx_draw(LubContext *ctx, const LubGfxDrawDesc *desc);
LubStatus lub_gfx_dispatch(LubContext *ctx, const LubGfxDispatchDesc *desc);

// readback queue `key` を poll し、has_request なら (tex, token) を積む。
// 完了した先頭があればそれを返し (READY / ERROR)、無ければ PROCESSING。
// queue が満杯で積めなかったときは DROPPED (token は積めなかった要求のもの)。
LubStatus lub_gfx_readback(LubContext *ctx, LubStr key, bool has_request,
                           LubHandle tex, int32_t token,
                           LubGfxReadbackResult *out);

#ifdef __cplusplus
}
#endif
