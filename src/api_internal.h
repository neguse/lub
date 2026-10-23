// C API (include/lub/lub_api.h) の実装側で共有する helper。
// LubContext は App そのもの。
#pragma once
#include "app.h"
#include "lub/lub_api.h"
#include "shader.h"
#include <stdbool.h>
#include <stddef.h>

static inline App *lub_api_app(LubContext *ctx) { return (App *)ctx; }
static inline LubContext *lub_api_ctx(App *app) { return (LubContext *)app; }

// last_error に message を書いて LUB_ERROR を返す。
LubStatus lub_api_fail(App *app, const char *fmt, ...);

// XR の片眼の描画先を指す特別な handle (lub_xr_view が XrView.target に返す)。
// main_tex (-1) と同じく resource table には無く、begin_pass が解決する。
#define LUB_GFX_XR_EYE_TEX(eye) ((LubHandle)(-2 - (eye)))
static inline int lub_gfx_xr_eye_of(LubHandle h) {
  return h == -2 || h == -3 ? (int)(-2 - h) : -1;
}

static inline LubStr lub_str_c(const char *s) {
  LubStr r = {s, s ? (int32_t)strlen(s) : 0};
  return r;
}

static inline bool lub_str_eq(LubStr a, const char *b) {
  size_t n = strlen(b);
  return (size_t)a.len == n && memcmp(a.ptr, b, n) == 0;
}

// NUL 終端の copy。
// cap に収まらなければ false。
static inline bool lub_str_copy(LubStr s, char *buf, size_t cap) {
  if (s.len < 0 || (size_t)s.len >= cap)
    return false;
  if (s.len > 0)
    memcpy(buf, s.ptr, (size_t)s.len);
  buf[s.len] = '\0';
  return true;
}

// 今の backend が使う target で shader を compile する。
// export では、export 先の OS の player が使う他の target の分も生成キャッシュに入れる。
bool shader_compile_for_backend(const char *vs, const char *fs,
                                ShaderBlob *out_vs, ShaderBlob *out_fs,
                                ShaderReflection *out_refl, char *err,
                                size_t err_size);
bool shader_compile_compute_for_backend(const char *cs, ShaderBlob *out_cs,
                                        ShaderReflection *out_refl, char *err,
                                        size_t err_size);

// frame の終わりに呼ぶ (app_frame_end)。
// key で宣言する queue / snd の sweep。
void api_gfx_frame_end(App *app);
void api_audio_frame_end(App *app);
// 保証するモードで、この frame に渡す readback が GPU から届いているか (app_frame_waits_for_gpu が見る)。
bool api_gfx_readbacks_due_ready(App *app);
// frame 有効の view の実体を預ける。
// app_frame_end が free する。
// 同じ API を同じ frame で呼び直しても前の view を壊さないよう、view を返す API は結果を使い回さずここに預ける。
void app_frame_garbage_push(App *app, void *ptr);
// free でない解放関数を要る実体 (stb の bitmap 等) を預ける。
void app_frame_garbage_push_fn(App *app, void *ptr, void (*free_fn)(void *));
// 預けた実体を全部解放する (app_frame_end と app_shutdown が呼ぶ)。
void app_frame_garbage_drain(App *app);

// api_*.c 所有の状態 (readback queue、decode / poll の view) を解放する。
// app_shutdown が呼ぶ。
void api_gfx_shutdown(App *app);
void api_audio_shutdown(App *app);
void api_io_shutdown(App *app);

uint64_t lub_io_fnv1a64(const void *data, size_t len);
