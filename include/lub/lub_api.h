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
// 段階 3 の形: 全 subsystem がこの API を通る。段階 4 で この header は
// cs-lib/lub_stub.cs からの生成物になる。
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
  // 問い合わせの対象 (handle / key) が無い。想定内の結果なので last_error は
  // 書かない。
  LUB_NOT_FOUND = 2,
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

typedef struct LubVec2 {
  float x, y;
} LubVec2;

typedef struct LubVec3 {
  float x, y, z;
} LubVec3;

typedef struct LubQuat {
  float x, y, z, w;
} LubQuat;

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

// snd を key で宣言する。samples は interleaved f32 PCM で count 個 (channels
// の倍数)。version の意味論は use_buffer と同じ (NULL = 内容が変わった宣言、
// 同じ version なら samples は読まない)。宣言が resource_sweep_after_frames
// の間途切れた snd は sweep され、鳴っている voice が終わってから PCM を
// 回収する。同じ内容の PCM は同じ snd に dedupe されるので、hot reload で
// 作り直しても鳴っている voice は途切れない。
LubStatus lub_audio_snd(LubContext *ctx, LubStr key, const float *samples,
                        int32_t count, int32_t channels, int32_t rate,
                        const int32_t *version, int32_t *out_snd);
// file format bytes → f32 PCM の view (frame の終わりまで有効)。
LubStatus lub_audio_decode(LubContext *ctx, const uint8_t *data, int32_t len,
                           LubView *pcm, int32_t *channels, int32_t *rate);
bool lub_audio_play(LubContext *ctx, int32_t snd, const LubAudioPlayDesc *desc);
bool lub_audio_voice(LubContext *ctx, LubStr key, int32_t snd,
                     const LubAudioPlayDesc *desc);
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
  LubView pixels; // READY のとき。frame の終わりまで
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
// token はゲームが決める int32 の user token。queue は key で宣言する
// resource で、resource_sweep_after_frames の間 poll されなければ sweep。
LubStatus lub_gfx_readback(LubContext *ctx, LubStr key, bool has_request,
                           LubHandle tex, int32_t token,
                           LubGfxReadbackResult *out);

// ---------------------------------------------------------------- phys2d
// Box2D の即時モード API。world / body / shape / chain / joint は key で毎
// フレーム宣言し、begin から step までに宣言されなかったものは step で prune
// される (begin の prune=false で抑止)。handle は entry の寿命に結ぶ:
// prune されるまで同じ値で、prune 後は stale (find で引き直す)。
// filter の bit は Box2D の 64 bit mask をそのまま持つ (Lua 面は hex 文字列、
// C# は ulong)。
//
// callback (world desc の callbacks、query の visitor) は呼び出し元と同じ
// thread で呼ばれ、渡す struct は呼び出しの間だけ有効。callback の中から
// 物理を変える API を呼ぶと LUB_ERROR。

typedef enum LubPhys2dBodyType {
  LUB_PHYS2D_BODY_TYPE_STATIC = 0,
  LUB_PHYS2D_BODY_TYPE_KINEMATIC = 1,
  LUB_PHYS2D_BODY_TYPE_DYNAMIC = 2,
} LubPhys2dBodyType;

typedef enum LubPhys2dShapeKind {
  LUB_PHYS2D_SHAPE_KIND_BOX = 1,
  LUB_PHYS2D_SHAPE_KIND_CIRCLE = 2,
  LUB_PHYS2D_SHAPE_KIND_CAPSULE = 3,
  LUB_PHYS2D_SHAPE_KIND_SEGMENT = 4,
  LUB_PHYS2D_SHAPE_KIND_POLYGON = 5,
  LUB_PHYS2D_SHAPE_KIND_CHAIN_SEGMENT = 6,
} LubPhys2dShapeKind;

typedef enum LubPhys2dJointType {
  LUB_PHYS2D_JOINT_TYPE_DISTANCE = 1,
  LUB_PHYS2D_JOINT_TYPE_FILTER = 2,
  LUB_PHYS2D_JOINT_TYPE_MOTOR = 3,
  LUB_PHYS2D_JOINT_TYPE_MOUSE = 4,
  LUB_PHYS2D_JOINT_TYPE_PRISMATIC = 5,
  LUB_PHYS2D_JOINT_TYPE_REVOLUTE = 6,
  LUB_PHYS2D_JOINT_TYPE_WELD = 7,
  LUB_PHYS2D_JOINT_TYPE_WHEEL = 8,
} LubPhys2dJointType;

typedef enum LubPhys2dEventKind {
  LUB_PHYS2D_EVENT_KIND_BEGIN = 0,
  LUB_PHYS2D_EVENT_KIND_END = 1,
  LUB_PHYS2D_EVENT_KIND_HIT = 2,
} LubPhys2dEventKind;

typedef enum LubPhys2dProxyKind {
  LUB_PHYS2D_PROXY_KIND_CIRCLE = 1,
  LUB_PHYS2D_PROXY_KIND_CAPSULE = 2,
  LUB_PHYS2D_PROXY_KIND_SEGMENT = 3,
  LUB_PHYS2D_PROXY_KIND_BOX = 4,
  LUB_PHYS2D_PROXY_KIND_POLYGON = 5,
} LubPhys2dProxyKind;

typedef struct LubPhys2dFilter {
  uint64_t category_bits; // 既定 1
  uint64_t mask_bits;     // 既定 全 bit
  int32_t group_index;
} LubPhys2dFilter;

// shape の識別。event / query / callback が返す。文字列は runtime が持ち、
// その shape (か tombstone) が生きている間有効。
typedef struct LubPhys2dShapePart {
  LubHandle shape; // shape か chain の handle。0 = 無し
  LubHandle body;
  LubStr body_key;
  LubStr shape_key;
  LubStr chain_key; // len 0 = chain segment ではない
  LubStr tag;
  LubStr material_name;
  int32_t material_id;
  int32_t kind; // LubPhys2dShapeKind。0 = 不明
  bool valid;   // shape が live
  bool has_material;
  bool has_filter;
  LubPhys2dFilter filter;
} LubPhys2dShapePart;

typedef struct LubPhys2dManifoldPoint {
  float x, y;
  float anchor_a_x, anchor_a_y;
  float anchor_b_x, anchor_b_y;
  float separation;
  float normal_impulse, tangent_impulse, total_normal_impulse;
  float normal_velocity;
  int32_t id;
  bool persisted;
} LubPhys2dManifoldPoint;

typedef struct LubPhys2dPreSolve {
  LubPhys2dShapePart a, b;
  float nx, ny;
  float rolling_impulse;
  int32_t point_count; // 0..2
  LubPhys2dManifoldPoint points[2];
} LubPhys2dPreSolve;

// 全部 NULL = callback 無し。生存期間は次の world 宣言か step まで。
typedef struct LubPhys2dCallbacks {
  void *user;
  bool (*filter)(void *user, const LubPhys2dShapePart *a,
                 const LubPhys2dShapePart *b);
  bool (*pre_solve)(void *user, const LubPhys2dPreSolve *contact);
  float (*friction)(void *user, float friction_a, int32_t material_a,
                    float friction_b, int32_t material_b);
  float (*restitution)(void *user, float restitution_a, int32_t material_a,
                       float restitution_b, int32_t material_b);
} LubPhys2dCallbacks;

typedef struct LubPhys2dWorldDesc {
  bool has_version;
  int32_t version;
  float gravity_x, gravity_y; // 既定 (0, -9.8)
  float fixed_dt;             // 既定 1/60
  int32_t substeps;           // 既定 4
  int32_t max_steps;          // 既定 4
  bool sleep;                 // 既定 true
  bool continuous;            // 既定 true
  bool has_hit_event_threshold;
  float hit_event_threshold;
  LubPhys2dCallbacks callbacks;
} LubPhys2dWorldDesc;

typedef struct LubPhys2dBodyDesc {
  bool has_version;
  int32_t version;
  int32_t type; // LubPhys2dBodyType
  bool fixed_rotation;
  bool bullet;
  bool has_enabled, enabled;
  bool has_awake, awake;
  bool has_sleep, sleep;
  float gravity_scale; // 既定 1
  float linear_damping, angular_damping;
  bool has_sleep_threshold;
  float sleep_threshold;
  // initial (再生成のときだけ使う)
  float x, y, angle;
  float vx, vy, w;
  bool initial_awake; // 既定 true
} LubPhys2dBodyDesc;

typedef struct LubPhys2dShapeDesc {
  bool has_version;
  int32_t version;
  bool has_density;
  float density;  // 既定 dynamic 1 / それ以外 0
  float friction; // 既定 0.6
  float restitution;
  int32_t material_id;
  LubStr material_name; // len 0 = 無し
  LubStr tag;           // len 0 = 無し
  bool sensor, contact, hit, sensor_events, pre_solve;
  LubPhys2dFilter filter;
} LubPhys2dShapeDesc;

typedef struct LubPhys2dBoxDesc {
  LubPhys2dShapeDesc shape;
  float hx, hy, cx, cy, angle;
} LubPhys2dBoxDesc;

typedef struct LubPhys2dCircleDesc {
  LubPhys2dShapeDesc shape;
  float r, cx, cy;
} LubPhys2dCircleDesc;

typedef struct LubPhys2dCapsuleDesc {
  LubPhys2dShapeDesc shape;
  float ax, ay, bx, by, r;
} LubPhys2dCapsuleDesc;

typedef struct LubPhys2dSegmentDesc {
  LubPhys2dShapeDesc shape;
  float ax, ay, bx, by;
} LubPhys2dSegmentDesc;

typedef struct LubPhys2dPolygonDesc {
  LubPhys2dShapeDesc shape;
  const float *points; // x, y の組。3..8 点
  int32_t point_count;
  float radius, cx, cy, angle;
} LubPhys2dPolygonDesc;

typedef struct LubPhys2dSurfaceMaterial {
  float friction, restitution;
  int32_t material_id;
} LubPhys2dSurfaceMaterial;

typedef struct LubPhys2dChainDesc {
  int32_t version;     // 必須 (chain は version 無しの宣言を受けない)
  const float *points; // x, y の組。4 点以上
  int32_t point_count;
  bool loop;
  float friction; // 既定 0.6
  float restitution;
  int32_t material_id;
  LubStr material_name;
  LubStr tag;
  bool sensor_events;
  LubPhys2dFilter filter;
  const LubPhys2dSurfaceMaterial *materials; // NULL = 無し
  int32_t material_count;                    // 1 か point_count
} LubPhys2dChainDesc;

typedef struct LubPhys2dJointDesc {
  bool has_version;
  int32_t version;
  int32_t type; // LubPhys2dJointType
  LubHandle body_a, body_b;
  LubVec2 local_anchor_a, local_anchor_b;
  LubVec2 local_axis_a; // 既定 (1, 0)
  LubVec2 linear_offset;
  LubVec2 target;
  float reference_angle;
  float length;     // 既定 1
  float min_length; // 既定 0
  float max_length; // 既定 1
  float lower;      // 既定 0
  float upper;      // 既定 1
  float target_angle, target_translation, angular_offset;
  float hertz, damping_ratio;
  float linear_hertz, angular_hertz;
  float linear_damping_ratio, angular_damping_ratio;
  float max_force;  // 既定 1
  float max_torque; // 既定 1
  float motor_speed;
  float correction_factor; // 既定 0.3
  float draw_size;         // 既定 0.25
  bool collide_connected, enable_spring, enable_limit, enable_motor;
} LubPhys2dJointDesc;

// desc に既定値を入れる (callbacks / 文字列は空)。
void lub_phys2d_world_desc_init(LubPhys2dWorldDesc *desc);
void lub_phys2d_body_desc_init(LubPhys2dBodyDesc *desc);
void lub_phys2d_shape_desc_init(LubPhys2dShapeDesc *desc);
void lub_phys2d_chain_desc_init(LubPhys2dChainDesc *desc);
void lub_phys2d_joint_desc_init(LubPhys2dJointDesc *desc);

typedef struct LubPhys2dWorldInfo {
  LubStr key;
  bool valid;
  int32_t version;
  int32_t generation;
  bool begun, prune;
  float fixed_dt;
  int32_t substeps, max_steps;
  float accumulator;
  int32_t pending_commands;
  bool callback_filter, callback_pre_solve, callback_friction,
      callback_restitution;
  // 以下は valid のとき
  float gravity_x, gravity_y;
  bool sleep, continuous, warm_starting;
  float restitution_threshold, hit_event_threshold, maximum_linear_speed;
  int32_t awake_body_count;
} LubPhys2dWorldInfo;

typedef struct LubPhys2dStepInfo {
  int32_t steps;
  int32_t commands;
  float alpha;
  bool dropped;
  int32_t contact_begins, contact_ends, contact_hits;
  int32_t sensor_begins, sensor_ends;
  int32_t body_moves;
} LubPhys2dStepInfo;

typedef struct LubPhys2dPose {
  float x, y, angle;
  float vx, vy, w;
  bool awake, enabled, sleep;
  float sleep_threshold;
} LubPhys2dPose;

typedef struct LubPhys2dVelocity {
  float x, y, w;
} LubPhys2dVelocity;

typedef struct LubPhys2dMassData {
  float mass, inertia;
  float center_x, center_y; // world
  float local_center_x, local_center_y;
} LubPhys2dMassData;

typedef struct LubPhys2dAabb {
  float min_x, min_y, max_x, max_y;
} LubPhys2dAabb;

typedef struct LubPhys2dShapeInfo {
  LubPhys2dShapePart part;
  float density, friction, restitution;
  bool sensor, sensor_events, contact, pre_solve, hit;
  LubPhys2dAabb aabb;
} LubPhys2dShapeInfo;

typedef struct LubPhys2dJointView {
  LubHandle joint;
  LubStr key;
  int32_t type; // LubPhys2dJointType。0 = 不明
  LubStr a, b;  // body の key
  bool valid;
} LubPhys2dJointView;

typedef struct LubPhys2dJointInfo {
  LubPhys2dJointView view;
  bool collide_connected;
  float force_x, force_y, torque;
  float linear_separation, angular_separation;
  bool has_local_anchors;
  LubVec2 local_anchor_a, local_anchor_b;
  bool has_local_axis;
  LubVec2 local_axis_a;
  bool has_reference_angle;
  float reference_angle;
} LubPhys2dJointInfo;

// step が集めた contact / sensor event。a / b の filter は無い。
typedef struct LubPhys2dContact {
  LubPhys2dShapePart a, b; // sensor event では a = sensor、b = visitor
  float nx, ny;
  int32_t point_count;
  float x, y;
  float approach_speed; // hit のとき
} LubPhys2dContact;

typedef struct LubPhys2dBodyEvent {
  LubStr body;
  bool valid;
  float x, y, angle;
  bool fell_asleep;
} LubPhys2dBodyEvent;

// body に今触れている contact (live)。
typedef struct LubPhys2dContactData {
  LubPhys2dShapePart a, b;
  float nx, ny;
  int32_t point_count;
  float x, y, separation;
} LubPhys2dContactData;

typedef struct LubPhys2dRay {
  float x, y;         // origin
  float dx, dy;       // translation
  float max_fraction; // 既定 1
} LubPhys2dRay;

typedef struct LubPhys2dRayHit {
  LubPhys2dShapePart shape; // world query のとき
  float x, y, nx, ny, fraction;
  int32_t iterations;               // shape raycast のとき
  int32_t node_visits, leaf_visits; // raycast_closest のとき
} LubPhys2dRayHit;

typedef struct LubPhys2dTreeStats {
  int32_t node_visits, leaf_visits;
} LubPhys2dTreeStats;

typedef struct LubPhys2dQueryFilter {
  uint64_t category_bits; // 既定 1
  uint64_t mask_bits;     // 既定 全 bit
} LubPhys2dQueryFilter;

typedef struct LubPhys2dShapeProxy {
  int32_t kind;         // LubPhys2dProxyKind
  float x, y, angle;    // 配置
  float r;              // circle / capsule / polygon / box の丸め
  float cx, cy;         // circle / box の中心
  float ax, ay, bx, by; // capsule / segment
  float hx, hy;         // box
  const float *points;  // polygon (x, y の組)
  int32_t point_count;
} LubPhys2dShapeProxy;

typedef struct LubPhys2dMover {
  float ax, ay, bx, by, r;
} LubPhys2dMover;

typedef struct LubPhys2dMoverPlane {
  LubPhys2dShapePart shape;
  bool hit;
  float x, y, nx, ny, offset;
} LubPhys2dMoverPlane;

typedef struct LubPhys2dExplosionDesc {
  float x, y;
  float radius, falloff, impulse_per_length;
  uint64_t mask_bits; // 既定 全 bit
} LubPhys2dExplosionDesc;

typedef struct LubPhys2dDebugDesc {
  bool shapes; // 既定 true
  bool joints, joint_extras, bounds, mass, body_names, contacts, graph_colors,
      contact_normals, contact_impulses, contact_features, friction_impulses,
      islands;
  bool has_drawing_bounds;
  LubPhys2dAabb drawing_bounds;
} LubPhys2dDebugDesc;

// 平らな float 配列。次の lub_phys2d_debug まで有効。色は r g b a。
typedef struct LubPhys2dDebugData {
  const float *segments; // x1 y1 x2 y2 + 色
  int32_t segment_count; // float の個数
  const float *circles;  // cx cy r + 色
  int32_t circle_count;
  const float *capsules; // x1 y1 x2 y2 r + 色
  int32_t capsule_count;
  const float *polygons; // n solid + 色 + x0 y0 ... (n 点)
  int32_t polygon_count;
  const float *points; // x y size + 色
  int32_t point_count;
} LubPhys2dDebugData;

typedef struct LubPhys2dProfile {
  float step, pairs, collide, solve, merge_islands, prepare_stages,
      solve_constraints, prepare_constraints, integrate_velocities, warm_start,
      solve_impulses, integrate_positions, relax_impulses, apply_restitution,
      store_impulses, split_islands, transforms, hit_events, refit, bullets,
      sleep_islands, sensors;
} LubPhys2dProfile;

typedef struct LubPhys2dCounters {
  int32_t body_count, shape_count, contact_count, joint_count, island_count,
      stack_used, static_tree_height, tree_height, byte_count, task_count;
  int32_t color_counts[12];
} LubPhys2dCounters;

typedef struct LubPhys2dSetVelocity {
  bool has_vx, has_vy, has_w;
  float vx, vy, w;
  bool wake;
} LubPhys2dSetVelocity;

typedef struct LubPhys2dTeleport {
  bool has_x, has_y, has_angle;
  float x, y, angle;
  bool wake;
} LubPhys2dTeleport;

typedef struct LubPhys2dSetTarget {
  bool has_x, has_y, has_angle;
  float x, y, angle;
  float time_step; // <= 0 で world の fixed_dt
  bool wake;
} LubPhys2dSetTarget;

typedef struct LubPhys2dMassDataDesc {
  float mass, inertia;
  float center_x, center_y; // local
} LubPhys2dMassDataDesc;

typedef struct LubPhys2dJointMotor {
  bool enabled;
  float speed, max_force, max_torque;
  bool has_correction_factor; // motor joint
  float correction_factor;
} LubPhys2dJointMotor;

typedef struct LubPhys2dJointLimit {
  bool enabled;
  float lower, upper;           // prismatic / revolute / wheel
  float min_length, max_length; // distance
} LubPhys2dJointLimit;

typedef struct LubPhys2dJointSpring {
  bool enabled;
  float hertz, damping_ratio;
  float linear_hertz, linear_damping_ratio; // weld
  float angular_hertz, angular_damping_ratio;
} LubPhys2dJointSpring;

typedef struct LubPhys2dJointTarget {
  bool has_x, has_y; // mouse。無い成分は今の値
  float x, y;
  bool has_translation; // prismatic
  float translation;
  bool has_angle; // revolute
  float angle;
  bool has_linear_offset; // motor
  float linear_offset_x, linear_offset_y;
  bool has_angular_offset;
  float angular_offset;
} LubPhys2dJointTarget;

typedef struct LubPhys2dMaterialDesc {
  bool has_density, has_friction, has_restitution, has_material_id,
      has_material_name;
  float density, friction, restitution;
  int32_t material_id;
  LubStr material_name; // has_material_name で len 0 = 名前を消す
} LubPhys2dMaterialDesc;

typedef struct LubPhys2dEventFlags {
  bool has_sensor_events, sensor_events;
  bool has_contact, contact;
  bool has_pre_solve, pre_solve;
  bool has_hit, hit;
} LubPhys2dEventFlags;

// query の visitor。false / 0 で打ち切り。raycast は Box2D の規約:
// -1 = この hit を無視、0 = 打ち切り、fraction = ここまでに詰める、1 = 続行。
typedef bool (*LubPhys2dOverlapFn)(void *user, const LubPhys2dShapePart *shape);
typedef float (*LubPhys2dRayFn)(void *user, const LubPhys2dRayHit *hit);
typedef bool (*LubPhys2dPlaneFn)(void *user, const LubPhys2dMoverPlane *plane);

LubStatus lub_phys2d_world(LubContext *ctx, LubStr key,
                           const LubPhys2dWorldDesc *desc, LubHandle *out);
LubHandle lub_phys2d_world_find(LubContext *ctx, LubStr key);
LubStatus lub_phys2d_begin(LubContext *ctx, LubHandle world, bool prune);
LubStatus lub_phys2d_world_info(LubContext *ctx, LubHandle world,
                                LubPhys2dWorldInfo *out);
LubStatus lub_phys2d_step(LubContext *ctx, LubHandle world, float dt,
                          LubPhys2dStepInfo *out);

LubStatus lub_phys2d_body(LubContext *ctx, LubHandle world, LubStr key,
                          const LubPhys2dBodyDesc *desc, LubHandle *out);
LubHandle lub_phys2d_body_find(LubContext *ctx, LubHandle world, LubStr key);
LubStatus lub_phys2d_box(LubContext *ctx, LubHandle body, LubStr key,
                         const LubPhys2dBoxDesc *desc, LubHandle *out);
LubStatus lub_phys2d_circle(LubContext *ctx, LubHandle body, LubStr key,
                            const LubPhys2dCircleDesc *desc, LubHandle *out);
LubStatus lub_phys2d_capsule(LubContext *ctx, LubHandle body, LubStr key,
                             const LubPhys2dCapsuleDesc *desc, LubHandle *out);
LubStatus lub_phys2d_segment(LubContext *ctx, LubHandle body, LubStr key,
                             const LubPhys2dSegmentDesc *desc, LubHandle *out);
LubStatus lub_phys2d_polygon(LubContext *ctx, LubHandle body, LubStr key,
                             const LubPhys2dPolygonDesc *desc, LubHandle *out);
LubHandle lub_phys2d_shape_find(LubContext *ctx, LubHandle body, LubStr key);
LubStatus lub_phys2d_chain(LubContext *ctx, LubHandle body, LubStr key,
                           const LubPhys2dChainDesc *desc, LubHandle *out);
LubHandle lub_phys2d_chain_find(LubContext *ctx, LubHandle body, LubStr key);
// 配列の view は同じ subsystem の次の呼び出しまで有効。
LubStatus lub_phys2d_chain_segments(LubContext *ctx, LubHandle chain,
                                    const LubPhys2dShapePart **items,
                                    int32_t *count);

LubStatus lub_phys2d_joint(LubContext *ctx, LubHandle world, LubStr key,
                           const LubPhys2dJointDesc *desc, LubHandle *out);
LubHandle lub_phys2d_joint_find(LubContext *ctx, LubHandle world, LubStr key);
LubStatus lub_phys2d_joint_info(LubContext *ctx, LubHandle joint,
                                LubPhys2dJointInfo *out);
LubStatus lub_phys2d_joint_force(LubContext *ctx, LubHandle joint, float *x,
                                 float *y);
LubStatus lub_phys2d_joint_torque(LubContext *ctx, LubHandle joint, float *out);
// joint の種類に無い量は has = false。
LubStatus lub_phys2d_joint_angle(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has);
LubStatus lub_phys2d_joint_translation(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has);
LubStatus lub_phys2d_joint_speed(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has);
LubStatus lub_phys2d_joint_length(LubContext *ctx, LubHandle joint, float *out,
                                  bool *has);
LubStatus lub_phys2d_joint_motor_force(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has);
LubStatus lub_phys2d_joint_motor_torque(LubContext *ctx, LubHandle joint,
                                        float *out, bool *has);
LubStatus lub_phys2d_joint_set_motor(LubContext *ctx, LubHandle joint,
                                     const LubPhys2dJointMotor *desc);
LubStatus lub_phys2d_joint_set_limit(LubContext *ctx, LubHandle joint,
                                     const LubPhys2dJointLimit *desc);
LubStatus lub_phys2d_joint_set_spring(LubContext *ctx, LubHandle joint,
                                      const LubPhys2dJointSpring *desc);
LubStatus lub_phys2d_joint_set_target(LubContext *ctx, LubHandle joint,
                                      const LubPhys2dJointTarget *desc);

LubStatus lub_phys2d_pose(LubContext *ctx, LubHandle body, LubPhys2dPose *out);
LubStatus lub_phys2d_velocity(LubContext *ctx, LubHandle body,
                              LubPhys2dVelocity *out);
LubStatus lub_phys2d_mass(LubContext *ctx, LubHandle body,
                          LubPhys2dMassData *out);
LubStatus lub_phys2d_center(LubContext *ctx, LubHandle body, float *x,
                            float *y);
LubStatus lub_phys2d_world_point(LubContext *ctx, LubHandle body, float lx,
                                 float ly, float *x, float *y);
LubStatus lub_phys2d_local_point(LubContext *ctx, LubHandle body, float wx,
                                 float wy, float *x, float *y);
LubStatus lub_phys2d_velocity_at(LubContext *ctx, LubHandle body, float wx,
                                 float wy, float *x, float *y);
LubStatus lub_phys2d_body_shapes(LubContext *ctx, LubHandle body,
                                 const LubPhys2dShapePart **items,
                                 int32_t *count);
LubStatus lub_phys2d_body_joints(LubContext *ctx, LubHandle body,
                                 const LubPhys2dJointView **items,
                                 int32_t *count);
LubStatus lub_phys2d_body_contacts(LubContext *ctx, LubHandle body,
                                   const LubPhys2dContactData **items,
                                   int32_t *count);

LubStatus lub_phys2d_shape_test_point(LubContext *ctx, LubHandle shape, float x,
                                      float y, bool *out);
LubStatus lub_phys2d_shape_raycast(LubContext *ctx, LubHandle shape,
                                   const LubPhys2dRay *ray,
                                   LubPhys2dRayHit *out, bool *hit);
LubStatus lub_phys2d_shape_closest_point(LubContext *ctx, LubHandle shape,
                                         float x, float y, float *ox,
                                         float *oy);
LubStatus lub_phys2d_shape_aabb(LubContext *ctx, LubHandle shape,
                                LubPhys2dAabb *out);
LubStatus lub_phys2d_shape_info(LubContext *ctx, LubHandle shape,
                                LubPhys2dShapeInfo *out);
LubStatus lub_phys2d_shape_set_material(LubContext *ctx, LubHandle shape,
                                        const LubPhys2dMaterialDesc *desc);
LubStatus lub_phys2d_shape_set_filter(LubContext *ctx, LubHandle shape,
                                      const LubPhys2dFilter *filter);
LubStatus lub_phys2d_shape_set_events(LubContext *ctx, LubHandle shape,
                                      const LubPhys2dEventFlags *flags);

LubStatus lub_phys2d_contacts(LubContext *ctx, LubHandle world, int32_t kind,
                              const LubPhys2dContact **items, int32_t *count);
LubStatus lub_phys2d_body_events(LubContext *ctx, LubHandle world,
                                 const LubPhys2dBodyEvent **items,
                                 int32_t *count);
// kind は BEGIN / END。
LubStatus lub_phys2d_sensors(LubContext *ctx, LubHandle world, int32_t kind,
                             const LubPhys2dContact **items, int32_t *count);

LubStatus lub_phys2d_raycast_closest(LubContext *ctx, LubHandle world,
                                     const LubPhys2dRay *ray,
                                     const LubPhys2dQueryFilter *filter,
                                     LubPhys2dRayHit *out, bool *hit);
LubStatus lub_phys2d_raycast(LubContext *ctx, LubHandle world,
                             const LubPhys2dRay *ray,
                             const LubPhys2dQueryFilter *filter,
                             LubPhys2dRayFn fn, void *user,
                             LubPhys2dTreeStats *stats);
LubStatus lub_phys2d_overlap_aabb(LubContext *ctx, LubHandle world,
                                  const LubPhys2dAabb *aabb,
                                  const LubPhys2dQueryFilter *filter,
                                  LubPhys2dOverlapFn fn, void *user,
                                  LubPhys2dTreeStats *stats);
LubStatus lub_phys2d_shape_cast(LubContext *ctx, LubHandle world,
                                const LubPhys2dShapeProxy *proxy, float dx,
                                float dy, const LubPhys2dQueryFilter *filter,
                                LubPhys2dRayFn fn, void *user,
                                LubPhys2dTreeStats *stats);
LubStatus lub_phys2d_cast_mover(LubContext *ctx, LubHandle world,
                                const LubPhys2dMover *mover, float dx, float dy,
                                const LubPhys2dQueryFilter *filter,
                                float *fraction);
LubStatus lub_phys2d_collide_mover(LubContext *ctx, LubHandle world,
                                   const LubPhys2dMover *mover,
                                   const LubPhys2dQueryFilter *filter,
                                   LubPhys2dPlaneFn fn, void *user);
LubStatus lub_phys2d_explode(LubContext *ctx, LubHandle world,
                             const LubPhys2dExplosionDesc *desc);
LubStatus lub_phys2d_debug(LubContext *ctx, LubHandle world,
                           const LubPhys2dDebugDesc *desc,
                           LubPhys2dDebugData *out);
LubStatus lub_phys2d_profile(LubContext *ctx, LubHandle world,
                             LubPhys2dProfile *out);
LubStatus lub_phys2d_counters(LubContext *ctx, LubHandle world,
                              LubPhys2dCounters *out);

// body への command。次の step の冒頭でまとめて適用する。point == NULL は
// 重心。
LubStatus lub_phys2d_add_force(LubContext *ctx, LubHandle body, float fx,
                               float fy, const LubVec2 *point, bool wake);
LubStatus lub_phys2d_add_force_center(LubContext *ctx, LubHandle body, float fx,
                                      float fy, bool wake);
LubStatus lub_phys2d_add_impulse(LubContext *ctx, LubHandle body, float ix,
                                 float iy, const LubVec2 *point, bool wake);
LubStatus lub_phys2d_add_impulse_center(LubContext *ctx, LubHandle body,
                                        float ix, float iy, bool wake);
LubStatus lub_phys2d_add_torque(LubContext *ctx, LubHandle body, float torque,
                                bool wake);
LubStatus lub_phys2d_add_angular_impulse(LubContext *ctx, LubHandle body,
                                         float impulse, bool wake);
LubStatus lub_phys2d_set_velocity(LubContext *ctx, LubHandle body,
                                  const LubPhys2dSetVelocity *desc);
LubStatus lub_phys2d_teleport(LubContext *ctx, LubHandle body,
                              const LubPhys2dTeleport *desc);
LubStatus lub_phys2d_set_target(LubContext *ctx, LubHandle body,
                                const LubPhys2dSetTarget *desc);
LubStatus lub_phys2d_set_mass_data(LubContext *ctx, LubHandle body,
                                   const LubPhys2dMassDataDesc *desc,
                                   bool wake);

// ---------------------------------------------------------------- phys3d
// Box3D の即時モード API。宣言 / prune / handle / callback の規則は phys2d と
// 同じ。位置は float (Box3D の倍精度位置は面に出さない)。回転は
// 正規化した四元数。

typedef enum LubPhys3dBodyType {
  LUB_PHYS3D_BODY_TYPE_STATIC = 0,
  LUB_PHYS3D_BODY_TYPE_KINEMATIC = 1,
  LUB_PHYS3D_BODY_TYPE_DYNAMIC = 2,
} LubPhys3dBodyType;

typedef enum LubPhys3dShapeKind {
  LUB_PHYS3D_SHAPE_KIND_SPHERE = 1,
  LUB_PHYS3D_SHAPE_KIND_BOX = 2,
  LUB_PHYS3D_SHAPE_KIND_CAPSULE = 3,
  LUB_PHYS3D_SHAPE_KIND_CYLINDER = 4,
  LUB_PHYS3D_SHAPE_KIND_CONE = 5,
  LUB_PHYS3D_SHAPE_KIND_HULL = 6,
  LUB_PHYS3D_SHAPE_KIND_MESH = 7,
  LUB_PHYS3D_SHAPE_KIND_HEIGHT_FIELD = 8,
  LUB_PHYS3D_SHAPE_KIND_COMPOUND = 9,
} LubPhys3dShapeKind;

typedef enum LubPhys3dJointType {
  LUB_PHYS3D_JOINT_TYPE_DISTANCE = 1,
  LUB_PHYS3D_JOINT_TYPE_FILTER = 2,
  LUB_PHYS3D_JOINT_TYPE_MOTOR = 3,
  LUB_PHYS3D_JOINT_TYPE_PARALLEL = 4,
  LUB_PHYS3D_JOINT_TYPE_PRISMATIC = 5,
  LUB_PHYS3D_JOINT_TYPE_REVOLUTE = 6,
  LUB_PHYS3D_JOINT_TYPE_SPHERICAL = 7,
  LUB_PHYS3D_JOINT_TYPE_WELD = 8,
  LUB_PHYS3D_JOINT_TYPE_WHEEL = 9,
} LubPhys3dJointType;

typedef enum LubPhys3dEventKind {
  LUB_PHYS3D_EVENT_KIND_BEGIN = 0,
  LUB_PHYS3D_EVENT_KIND_END = 1,
  LUB_PHYS3D_EVENT_KIND_HIT = 2,
} LubPhys3dEventKind;

typedef enum LubPhys3dProxyKind {
  LUB_PHYS3D_PROXY_KIND_SPHERE = 1,
  LUB_PHYS3D_PROXY_KIND_BOX = 2,
  LUB_PHYS3D_PROXY_KIND_CAPSULE = 3,
} LubPhys3dProxyKind;

typedef enum LubPhys3dCompoundChildKind {
  LUB_PHYS3D_COMPOUND_CHILD_KIND_SPHERE = 1,
  LUB_PHYS3D_COMPOUND_CHILD_KIND_CAPSULE = 2,
  LUB_PHYS3D_COMPOUND_CHILD_KIND_BOX = 3,
} LubPhys3dCompoundChildKind;

typedef struct LubPhys3dFilter {
  uint64_t category_bits; // 既定 1
  uint64_t mask_bits;     // 既定 全 bit
  int32_t group_index;
} LubPhys3dFilter;

typedef struct LubPhys3dShapePart {
  LubHandle shape; // 0 = 無し
  LubHandle body;
  LubStr body_key;
  LubStr shape_key;
  LubStr tag;
  LubStr material_name;
  int32_t material_id;
  int32_t kind; // LubPhys3dShapeKind。0 = 不明
  bool valid;
  bool has_material;
  bool has_filter;
  LubPhys3dFilter filter;
} LubPhys3dShapePart;

// Box3D の pre-solve は manifold を持たず、点と法線が 1 つ。
typedef struct LubPhys3dPreSolve {
  LubPhys3dShapePart a, b;
  float x, y, z;
  float nx, ny, nz;
} LubPhys3dPreSolve;

typedef struct LubPhys3dCallbacks {
  void *user;
  bool (*filter)(void *user, const LubPhys3dShapePart *a,
                 const LubPhys3dShapePart *b);
  bool (*pre_solve)(void *user, const LubPhys3dPreSolve *contact);
  float (*friction)(void *user, float friction_a, int32_t material_a,
                    float friction_b, int32_t material_b);
  float (*restitution)(void *user, float restitution_a, int32_t material_a,
                       float restitution_b, int32_t material_b);
} LubPhys3dCallbacks;

typedef struct LubPhys3dWorldDesc {
  bool has_version;
  int32_t version;
  LubVec3 gravity; // 既定 (0, -9.8, 0)
  float fixed_dt;  // 既定 1/60
  int32_t substeps;
  int32_t max_steps;
  bool sleep;
  bool continuous;
  bool has_hit_event_threshold;
  float hit_event_threshold;
  LubPhys3dCallbacks callbacks;
} LubPhys3dWorldDesc;

typedef struct LubPhys3dBodyDesc {
  bool has_version;
  int32_t version;
  int32_t type; // LubPhys3dBodyType
  bool lock_linear_x, lock_linear_y, lock_linear_z;
  bool lock_angular_x, lock_angular_y, lock_angular_z;
  bool bullet;
  bool has_enabled, enabled;
  bool has_awake, awake;
  bool has_sleep, sleep;
  float gravity_scale; // 既定 1
  float linear_damping, angular_damping;
  bool has_sleep_threshold;
  float sleep_threshold;
  // initial (再生成のときだけ使う)
  LubVec3 position;
  LubQuat rotation; // 既定 identity
  LubVec3 linear_velocity;
  LubVec3 angular_velocity;
  bool initial_awake; // 既定 true
} LubPhys3dBodyDesc;

typedef struct LubPhys3dShapeDesc {
  bool has_version;
  int32_t version;
  bool has_density;
  float density;
  float friction; // 既定 0.6
  float restitution;
  int32_t material_id;
  LubStr material_name;
  LubStr tag;
  bool sensor, contact, hit, sensor_events, pre_solve;
  LubPhys3dFilter filter;
} LubPhys3dShapeDesc;

typedef struct LubPhys3dSphereDesc {
  LubPhys3dShapeDesc shape;
  float r;
  LubVec3 offset;
} LubPhys3dSphereDesc;

typedef struct LubPhys3dBoxDesc {
  LubPhys3dShapeDesc shape;
  float hx, hy, hz;
  LubVec3 offset;
  bool has_rotation;
  LubQuat rotation;
} LubPhys3dBoxDesc;

typedef struct LubPhys3dCapsuleDesc {
  LubPhys3dShapeDesc shape;
  LubVec3 a, b;
  float r;
} LubPhys3dCapsuleDesc;

typedef struct LubPhys3dCylinderDesc {
  LubPhys3dShapeDesc shape;
  float height, radius;
  int32_t sides;  // 3..32、既定 16
  float y_offset; // 既定 -height / 2 (胴を原点中心に)
} LubPhys3dCylinderDesc;

typedef struct LubPhys3dConeDesc {
  LubPhys3dShapeDesc shape;
  float height, radius1, radius2;
  int32_t slices; // 4..32、既定 16
} LubPhys3dConeDesc;

typedef struct LubPhys3dHullDesc {
  LubPhys3dShapeDesc shape; // version 必須
  const float *points;      // x, y, z の組。4 点以上
  int32_t point_count;
  int32_t max_vertices; // 既定 255
} LubPhys3dHullDesc;

typedef struct LubPhys3dSurfaceMaterial {
  float friction, restitution;
  int32_t material_id;
} LubPhys3dSurfaceMaterial;

typedef struct LubPhys3dMeshDesc {
  LubPhys3dShapeDesc shape; // version 必須
  const float *positions;   // x, y, z の組
  int32_t vertex_count;
  const int32_t *indices; // 0 始まり、3 の倍数
  int32_t index_count;
  LubVec3 scale; // 既定 (1, 1, 1)
  bool weld_vertices;
  float weld_tolerance;
  bool use_median_split;
  bool identify_edges;                       // 既定 true
  const LubPhys3dSurfaceMaterial *materials; // NULL = 無し。1..255
  int32_t material_count;
  const int32_t *material_indices; // NULL = 無し。三角形ごと
  int32_t material_index_count;
} LubPhys3dMeshDesc;

typedef struct LubPhys3dHeightFieldDesc {
  LubPhys3dShapeDesc shape; // version 必須
  int32_t x_count, z_count; // >= 2
  const float *heights;     // x_count * z_count
  LubVec3 scale;            // 既定 (cell_width, 1, cell_width)
  bool has_min_height, has_max_height;
  float min_height, max_height; // 無ければ heights から
  bool clockwise_winding;
} LubPhys3dHeightFieldDesc;

typedef struct LubPhys3dCompoundChild {
  int32_t kind; // LubPhys3dCompoundChildKind
  LubVec3 position;
  LubQuat rotation; // 既定 identity
  LubPhys3dSurfaceMaterial material;
  float r;          // sphere / capsule
  LubVec3 center;   // sphere
  LubVec3 a, b;     // capsule
  float hx, hy, hz; // box
} LubPhys3dCompoundChild;

typedef struct LubPhys3dCompoundDesc {
  LubPhys3dShapeDesc shape; // version 必須。static body 限定、sensor 不可
  const LubPhys3dCompoundChild *children;
  int32_t child_count;
} LubPhys3dCompoundDesc;

typedef struct LubPhys3dJointDesc {
  bool has_version;
  int32_t version;
  int32_t type; // LubPhys3dJointType
  LubHandle body_a, body_b;
  // 世界座標の axis / anchor から local frame を作る。frame_a / frame_b を
  // 明示すればそちらが勝つ。
  bool has_axis;
  LubVec3 axis;
  bool has_anchor_a, has_anchor_b;
  LubVec3 anchor_a, anchor_b;
  bool has_frame_a, has_frame_b;
  LubVec3 frame_a_position, frame_b_position;
  LubQuat frame_a_rotation, frame_b_rotation;
  float force_threshold, torque_threshold; // 既定 FLT_MAX
  bool has_constraint_tuning;
  float constraint_hertz, constraint_damping_ratio;
  bool collide_connected;
  float length;     // 既定 1
  float min_length; // 既定 0
  float max_length; // 既定 FLT_MAX
  float lower, upper;
  float hertz, damping_ratio;
  float linear_hertz, angular_hertz;
  float linear_damping_ratio, angular_damping_ratio;
  float max_force, max_torque, motor_speed;
  float target_angle, target_translation;
  bool enable_spring, enable_limit, enable_motor;
  float lower_spring_force, upper_spring_force; // 既定 -FLT_MAX / FLT_MAX
  LubVec3 linear_velocity, angular_velocity;    // motor
  float max_velocity_force, max_velocity_torque;
  float max_spring_force, max_spring_torque;
  LubQuat target_rotation; // spherical。既定 identity
  bool enable_cone_limit;
  float cone_angle;
  bool enable_twist_limit;
  float lower_twist_angle, upper_twist_angle;
  LubVec3 motor_velocity;
  bool enable_steering; // wheel
  float steering_hertz, steering_damping_ratio;
  float target_steering_angle, max_steering_torque;
  bool enable_steering_limit;
  float lower_steering_limit, upper_steering_limit;
} LubPhys3dJointDesc;

void lub_phys3d_world_desc_init(LubPhys3dWorldDesc *desc);
void lub_phys3d_body_desc_init(LubPhys3dBodyDesc *desc);
void lub_phys3d_shape_desc_init(LubPhys3dShapeDesc *desc);
// type ごとの既定値 (parallel / wheel は spring が既定で有効) を入れる。
void lub_phys3d_joint_desc_init(LubPhys3dJointDesc *desc, int32_t type);

typedef struct LubPhys3dWorldInfo {
  LubStr key;
  bool valid;
  int32_t version;
  int32_t generation;
  bool begun, prune;
  float fixed_dt;
  int32_t substeps, max_steps;
  float accumulator;
  int32_t pending_commands;
  bool callback_filter, callback_pre_solve, callback_friction,
      callback_restitution;
  LubVec3 gravity;
  bool sleep, continuous, warm_starting;
  float restitution_threshold, hit_event_threshold, maximum_linear_speed;
  int32_t awake_body_count;
} LubPhys3dWorldInfo;

typedef struct LubPhys3dStepInfo {
  int32_t steps;
  int32_t commands;
  float alpha;
  bool dropped;
  int32_t contact_begins, contact_ends, contact_hits;
  int32_t sensor_begins, sensor_ends;
  int32_t body_moves;
  int32_t joint_events;
} LubPhys3dStepInfo;

typedef struct LubPhys3dPose {
  LubVec3 position;
  LubQuat rotation;
  LubVec3 linear_velocity, angular_velocity;
  bool awake, enabled, sleep;
  float sleep_threshold;
} LubPhys3dPose;

typedef struct LubPhys3dVelocity {
  LubVec3 linear, angular;
} LubPhys3dVelocity;

typedef struct LubPhys3dMassData {
  float mass;
  LubVec3 center; // world
  LubVec3 local_center;
  // 慣性テンソル (local center まわり、対称) の成分
  float xx, yy, zz, xy, xz, yz;
} LubPhys3dMassData;

typedef struct LubPhys3dAabb {
  LubVec3 min, max;
} LubPhys3dAabb;

typedef struct LubPhys3dShapeInfo {
  LubPhys3dShapePart part;
  float density, friction, restitution;
  bool sensor, sensor_events, contact, pre_solve, hit;
  LubPhys3dAabb aabb;
} LubPhys3dShapeInfo;

typedef struct LubPhys3dJointView {
  LubHandle joint;
  LubStr key;
  int32_t type; // LubPhys3dJointType。0 = 不明
  LubStr a, b;
  bool valid;
} LubPhys3dJointView;

typedef struct LubPhys3dFrame {
  LubVec3 position;
  LubQuat rotation;
} LubPhys3dFrame;

typedef struct LubPhys3dJointInfo {
  LubPhys3dJointView view;
  bool collide_connected;
  LubVec3 force, torque;
  float linear_separation, angular_separation;
  LubPhys3dFrame local_frame_a, local_frame_b;
} LubPhys3dJointInfo;

typedef struct LubPhys3dContact {
  LubPhys3dShapePart a, b;
  LubVec3 normal;
  int32_t point_count;
  LubVec3 point;
  float approach_speed;
} LubPhys3dContact;

typedef struct LubPhys3dBodyEvent {
  LubStr body;
  bool valid;
  LubVec3 position;
  LubQuat rotation;
  bool fell_asleep;
} LubPhys3dBodyEvent;

typedef struct LubPhys3dJointEvent {
  LubStr joint;
  int32_t type; // LubPhys3dJointType。0 = 不明
  LubStr a, b;
  bool valid;
} LubPhys3dJointEvent;

typedef struct LubPhys3dContactData {
  LubPhys3dShapePart a, b;
  LubVec3 normal;
  int32_t manifold_count;
  int32_t point_count;
  bool has_point;
  LubVec3 point;
  float separation;
} LubPhys3dContactData;

typedef struct LubPhys3dRay {
  LubVec3 origin;
  LubVec3 translation; // max_fraction を掛けた後の値
} LubPhys3dRay;

typedef struct LubPhys3dRayHit {
  LubPhys3dShapePart shape; // world query のとき
  LubVec3 point, normal;
  float fraction;
  int32_t iterations; // shape raycast のとき
  int32_t hit_material_id;
  int32_t triangle_index, child_index;
  int32_t node_visits, leaf_visits; // raycast_closest のとき
} LubPhys3dRayHit;

typedef struct LubPhys3dTreeStats {
  int32_t node_visits, leaf_visits;
} LubPhys3dTreeStats;

typedef struct LubPhys3dQueryFilter {
  uint64_t category_bits;
  uint64_t mask_bits;
} LubPhys3dQueryFilter;

typedef struct LubPhys3dShapeProxy {
  int32_t kind;   // LubPhys3dProxyKind
  float r;        // sphere / capsule / box の丸め
  LubVec3 center; // sphere / box
  float hx, hy, hz;
  bool has_rotation;
  LubQuat rotation; // box
  LubVec3 a, b;     // capsule
} LubPhys3dShapeProxy;

typedef struct LubPhys3dMover {
  LubVec3 a, b;
  float r;
} LubPhys3dMover;

typedef struct LubPhys3dMoverPlane {
  LubPhys3dShapePart shape;
  LubVec3 point, normal; // world
  float offset;
  int32_t plane_count;
} LubPhys3dMoverPlane;

typedef struct LubPhys3dProfile {
  float step, pairs, collide, solve, solver_setup, constraints,
      prepare_constraints, integrate_velocities, warm_start, solve_impulses,
      integrate_positions, relax_impulses, apply_restitution, store_impulses,
      split_islands, transforms, sensor_hits, joint_events, hit_events, refit,
      bullets, sleep_islands, sensors;
} LubPhys3dProfile;

#define LUB_PHYS3D_MANIFOLD_COUNT_BUCKETS 8

typedef struct LubPhys3dCounters {
  int32_t body_count, shape_count, contact_count, joint_count, island_count,
      stack_used, arena_capacity, static_tree_height, tree_height,
      sat_call_count, sat_cache_hit_count, byte_count, task_count,
      awake_contact_count, recycled_contact_count, distance_iterations,
      push_back_iterations, root_iterations;
  int32_t color_counts[24];
  int32_t manifold_counts[LUB_PHYS3D_MANIFOLD_COUNT_BUCKETS];
} LubPhys3dCounters;

typedef struct LubPhys3dSetVelocity {
  bool has_vx, has_vy, has_vz;
  bool has_wx, has_wy, has_wz;
  LubVec3 linear, angular;
  bool wake;
} LubPhys3dSetVelocity;

typedef struct LubPhys3dTeleport {
  bool has_x, has_y, has_z;
  LubVec3 position;
  bool has_rotation;
  LubQuat rotation;
  bool wake;
} LubPhys3dTeleport;

typedef struct LubPhys3dSetTarget {
  bool has_x, has_y, has_z;
  LubVec3 position;
  bool has_rotation;
  LubQuat rotation;
  float time_step; // <= 0 で world の fixed_dt
  bool wake;
} LubPhys3dSetTarget;

typedef struct LubPhys3dJointMotor {
  bool enabled;
  float speed, max_force, max_torque;
  bool has_velocity; // spherical
  LubVec3 velocity;
  bool has_linear_velocity, has_angular_velocity; // motor
  LubVec3 linear_velocity, angular_velocity;
  bool has_max_velocity_force, has_max_velocity_torque;
  float max_velocity_force, max_velocity_torque;
} LubPhys3dJointMotor;

typedef struct LubPhys3dJointLimit {
  bool enabled;
  float lower, upper;
  float min_length, max_length; // distance。max の既定 FLT_MAX
  bool has_cone_angle;          // spherical
  float cone_angle;
  bool has_twist; // spherical: lower / upper を twist に使う
} LubPhys3dJointLimit;

typedef struct LubPhys3dJointSpring {
  bool enabled;
  float hertz, damping_ratio;
  float linear_hertz, linear_damping_ratio; // weld / motor
  float angular_hertz, angular_damping_ratio;
  bool has_max_torque; // parallel
  float max_torque;
} LubPhys3dJointSpring;

typedef struct LubPhys3dJointTarget {
  bool has_translation; // prismatic
  float translation;
  bool has_angle; // revolute / wheel (steering)
  float angle;
  bool has_rotation; // spherical
  LubQuat rotation;
  bool has_linear_velocity, has_angular_velocity; // motor
  LubVec3 linear_velocity, angular_velocity;
} LubPhys3dJointTarget;

typedef struct LubPhys3dMaterialDesc {
  bool has_density, has_friction, has_restitution, has_material_id,
      has_material_name;
  float density, friction, restitution;
  int32_t material_id;
  LubStr material_name;
} LubPhys3dMaterialDesc;

typedef struct LubPhys3dEventFlags {
  bool has_sensor_events, sensor_events;
  bool has_contact, contact;
  bool has_pre_solve, pre_solve;
  bool has_hit, hit;
} LubPhys3dEventFlags;

typedef bool (*LubPhys3dOverlapFn)(void *user, const LubPhys3dShapePart *shape);
typedef float (*LubPhys3dRayFn)(void *user, const LubPhys3dRayHit *hit);
typedef bool (*LubPhys3dPlaneFn)(void *user, const LubPhys3dMoverPlane *plane);

LubStatus lub_phys3d_world(LubContext *ctx, LubStr key,
                           const LubPhys3dWorldDesc *desc, LubHandle *out);
LubHandle lub_phys3d_world_find(LubContext *ctx, LubStr key);
LubStatus lub_phys3d_begin(LubContext *ctx, LubHandle world, bool prune);
LubStatus lub_phys3d_world_info(LubContext *ctx, LubHandle world,
                                LubPhys3dWorldInfo *out);
LubStatus lub_phys3d_step(LubContext *ctx, LubHandle world, float dt,
                          LubPhys3dStepInfo *out);

LubStatus lub_phys3d_body(LubContext *ctx, LubHandle world, LubStr key,
                          const LubPhys3dBodyDesc *desc, LubHandle *out);
LubHandle lub_phys3d_body_find(LubContext *ctx, LubHandle world, LubStr key);
LubStatus lub_phys3d_sphere(LubContext *ctx, LubHandle body, LubStr key,
                            const LubPhys3dSphereDesc *desc, LubHandle *out);
LubStatus lub_phys3d_box(LubContext *ctx, LubHandle body, LubStr key,
                         const LubPhys3dBoxDesc *desc, LubHandle *out);
LubStatus lub_phys3d_capsule(LubContext *ctx, LubHandle body, LubStr key,
                             const LubPhys3dCapsuleDesc *desc, LubHandle *out);
LubStatus lub_phys3d_cylinder(LubContext *ctx, LubHandle body, LubStr key,
                              const LubPhys3dCylinderDesc *desc,
                              LubHandle *out);
LubStatus lub_phys3d_cone(LubContext *ctx, LubHandle body, LubStr key,
                          const LubPhys3dConeDesc *desc, LubHandle *out);
LubStatus lub_phys3d_hull(LubContext *ctx, LubHandle body, LubStr key,
                          const LubPhys3dHullDesc *desc, LubHandle *out);
LubStatus lub_phys3d_mesh(LubContext *ctx, LubHandle body, LubStr key,
                          const LubPhys3dMeshDesc *desc, LubHandle *out);
LubStatus lub_phys3d_height_field(LubContext *ctx, LubHandle body, LubStr key,
                                  const LubPhys3dHeightFieldDesc *desc,
                                  LubHandle *out);
LubStatus lub_phys3d_compound(LubContext *ctx, LubHandle body, LubStr key,
                              const LubPhys3dCompoundDesc *desc,
                              LubHandle *out);
LubHandle lub_phys3d_shape_find(LubContext *ctx, LubHandle body, LubStr key);

LubStatus lub_phys3d_joint(LubContext *ctx, LubHandle world, LubStr key,
                           const LubPhys3dJointDesc *desc, LubHandle *out);
LubHandle lub_phys3d_joint_find(LubContext *ctx, LubHandle world, LubStr key);
LubStatus lub_phys3d_joint_info(LubContext *ctx, LubHandle joint,
                                LubPhys3dJointInfo *out);
LubStatus lub_phys3d_joint_force(LubContext *ctx, LubHandle joint,
                                 LubVec3 *out);
LubStatus lub_phys3d_joint_torque(LubContext *ctx, LubHandle joint,
                                  LubVec3 *out);
LubStatus lub_phys3d_joint_angle(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has);
LubStatus lub_phys3d_joint_translation(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has);
LubStatus lub_phys3d_joint_speed(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has);
LubStatus lub_phys3d_joint_length(LubContext *ctx, LubHandle joint, float *out,
                                  bool *has);
LubStatus lub_phys3d_joint_motor_force(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has);
// spherical は vector (has_vector)、revolute / wheel は scalar (has)。
LubStatus lub_phys3d_joint_motor_torque(LubContext *ctx, LubHandle joint,
                                        float *out, bool *has, LubVec3 *vector,
                                        bool *has_vector);
LubStatus lub_phys3d_joint_set_motor(LubContext *ctx, LubHandle joint,
                                     const LubPhys3dJointMotor *desc);
LubStatus lub_phys3d_joint_set_limit(LubContext *ctx, LubHandle joint,
                                     const LubPhys3dJointLimit *desc);
LubStatus lub_phys3d_joint_set_spring(LubContext *ctx, LubHandle joint,
                                      const LubPhys3dJointSpring *desc);
LubStatus lub_phys3d_joint_set_target(LubContext *ctx, LubHandle joint,
                                      const LubPhys3dJointTarget *desc);

LubStatus lub_phys3d_pose(LubContext *ctx, LubHandle body, LubPhys3dPose *out);
LubStatus lub_phys3d_velocity(LubContext *ctx, LubHandle body,
                              LubPhys3dVelocity *out);
LubStatus lub_phys3d_mass(LubContext *ctx, LubHandle body,
                          LubPhys3dMassData *out);
LubStatus lub_phys3d_center(LubContext *ctx, LubHandle body, LubVec3 *out);
LubStatus lub_phys3d_world_point(LubContext *ctx, LubHandle body, LubVec3 local,
                                 LubVec3 *out);
LubStatus lub_phys3d_local_point(LubContext *ctx, LubHandle body, LubVec3 world,
                                 LubVec3 *out);
LubStatus lub_phys3d_velocity_at(LubContext *ctx, LubHandle body, LubVec3 world,
                                 LubVec3 *out);
LubStatus lub_phys3d_body_shapes(LubContext *ctx, LubHandle body,
                                 const LubPhys3dShapePart **items,
                                 int32_t *count);
LubStatus lub_phys3d_body_joints(LubContext *ctx, LubHandle body,
                                 const LubPhys3dJointView **items,
                                 int32_t *count);
LubStatus lub_phys3d_body_contacts(LubContext *ctx, LubHandle body,
                                   const LubPhys3dContactData **items,
                                   int32_t *count);

LubStatus lub_phys3d_shape_raycast(LubContext *ctx, LubHandle shape,
                                   const LubPhys3dRay *ray,
                                   LubPhys3dRayHit *out, bool *hit);
LubStatus lub_phys3d_shape_closest_point(LubContext *ctx, LubHandle shape,
                                         LubVec3 point, LubVec3 *out);
LubStatus lub_phys3d_shape_aabb(LubContext *ctx, LubHandle shape,
                                LubPhys3dAabb *out);
LubStatus lub_phys3d_shape_info(LubContext *ctx, LubHandle shape,
                                LubPhys3dShapeInfo *out);
LubStatus lub_phys3d_shape_set_material(LubContext *ctx, LubHandle shape,
                                        const LubPhys3dMaterialDesc *desc);
LubStatus lub_phys3d_shape_set_filter(LubContext *ctx, LubHandle shape,
                                      const LubPhys3dFilter *filter);
LubStatus lub_phys3d_shape_set_events(LubContext *ctx, LubHandle shape,
                                      const LubPhys3dEventFlags *flags);

LubStatus lub_phys3d_contacts(LubContext *ctx, LubHandle world, int32_t kind,
                              const LubPhys3dContact **items, int32_t *count);
LubStatus lub_phys3d_body_events(LubContext *ctx, LubHandle world,
                                 const LubPhys3dBodyEvent **items,
                                 int32_t *count);
LubStatus lub_phys3d_sensors(LubContext *ctx, LubHandle world, int32_t kind,
                             const LubPhys3dContact **items, int32_t *count);
LubStatus lub_phys3d_joint_events(LubContext *ctx, LubHandle world,
                                  const LubPhys3dJointEvent **items,
                                  int32_t *count);

LubStatus lub_phys3d_raycast_closest(LubContext *ctx, LubHandle world,
                                     const LubPhys3dRay *ray,
                                     const LubPhys3dQueryFilter *filter,
                                     LubPhys3dRayHit *out, bool *hit);
LubStatus lub_phys3d_raycast(LubContext *ctx, LubHandle world,
                             const LubPhys3dRay *ray,
                             const LubPhys3dQueryFilter *filter,
                             LubPhys3dRayFn fn, void *user,
                             LubPhys3dTreeStats *stats);
LubStatus lub_phys3d_overlap_aabb(LubContext *ctx, LubHandle world,
                                  const LubPhys3dAabb *aabb,
                                  const LubPhys3dQueryFilter *filter,
                                  LubPhys3dOverlapFn fn, void *user,
                                  LubPhys3dTreeStats *stats);
LubStatus lub_phys3d_overlap_shape(LubContext *ctx, LubHandle world,
                                   const LubPhys3dShapeProxy *proxy,
                                   const LubPhys3dQueryFilter *filter,
                                   LubPhys3dOverlapFn fn, void *user,
                                   LubPhys3dTreeStats *stats);
LubStatus lub_phys3d_shape_cast(LubContext *ctx, LubHandle world,
                                const LubPhys3dShapeProxy *proxy,
                                LubVec3 translation,
                                const LubPhys3dQueryFilter *filter,
                                LubPhys3dRayFn fn, void *user,
                                LubPhys3dTreeStats *stats);
LubStatus lub_phys3d_cast_mover(LubContext *ctx, LubHandle world,
                                const LubPhys3dMover *mover,
                                LubVec3 translation,
                                const LubPhys3dQueryFilter *filter,
                                float *fraction);
LubStatus lub_phys3d_collide_mover(LubContext *ctx, LubHandle world,
                                   const LubPhys3dMover *mover,
                                   const LubPhys3dQueryFilter *filter,
                                   LubPhys3dPlaneFn fn, void *user);
LubStatus lub_phys3d_profile(LubContext *ctx, LubHandle world,
                             LubPhys3dProfile *out);
LubStatus lub_phys3d_counters(LubContext *ctx, LubHandle world,
                              LubPhys3dCounters *out);

LubStatus lub_phys3d_add_force(LubContext *ctx, LubHandle body, LubVec3 force,
                               const LubVec3 *point, bool wake);
LubStatus lub_phys3d_add_force_center(LubContext *ctx, LubHandle body,
                                      LubVec3 force, bool wake);
LubStatus lub_phys3d_add_impulse(LubContext *ctx, LubHandle body,
                                 LubVec3 impulse, const LubVec3 *point,
                                 bool wake);
LubStatus lub_phys3d_add_impulse_center(LubContext *ctx, LubHandle body,
                                        LubVec3 impulse, bool wake);
LubStatus lub_phys3d_add_torque(LubContext *ctx, LubHandle body, LubVec3 torque,
                                bool wake);
LubStatus lub_phys3d_add_angular_impulse(LubContext *ctx, LubHandle body,
                                         LubVec3 impulse, bool wake);
LubStatus lub_phys3d_set_velocity(LubContext *ctx, LubHandle body,
                                  const LubPhys3dSetVelocity *desc);
LubStatus lub_phys3d_teleport(LubContext *ctx, LubHandle body,
                              const LubPhys3dTeleport *desc);
LubStatus lub_phys3d_set_target(LubContext *ctx, LubHandle body,
                                const LubPhys3dSetTarget *desc);

#ifdef __cplusplus
}
#endif
