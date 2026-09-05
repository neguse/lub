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
// 段階 3 の途中の形: gfx / input / sys / profiler / host / audio がこの API を
// 通る。残り (io / png / mesh / font / ui / physics) は順に移す。段階 4 で
// この header は cs-lib/lub_stub.cs からの生成物になる。
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
