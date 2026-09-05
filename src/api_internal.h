// C API (include/lub/lub_api.h) の実装側で共有する helper。LubContext は
// App そのもの。
#pragma once
#include "app.h"
#include "lub/lub_api.h"
#include <stdbool.h>
#include <stddef.h>

static inline App *lub_api_app(LubContext *ctx) { return (App *)ctx; }
static inline LubContext *lub_api_ctx(App *app) { return (LubContext *)app; }

// last_error に message を書いて LUB_ERROR を返す。
LubStatus lub_api_fail(App *app, const char *fmt, ...);

static inline LubStr lub_str_c(const char *s) {
  LubStr r = {s, s ? (int32_t)strlen(s) : 0};
  return r;
}

static inline bool lub_str_eq(LubStr a, const char *b) {
  size_t n = strlen(b);
  return (size_t)a.len == n && memcmp(a.ptr, b, n) == 0;
}

// NUL 終端の copy。cap に収まらなければ false。
static inline bool lub_str_copy(LubStr s, char *buf, size_t cap) {
  if (s.len < 0 || (size_t)s.len >= cap)
    return false;
  if (s.len > 0)
    memcpy(buf, s.ptr, (size_t)s.len);
  buf[s.len] = '\0';
  return true;
}

// api_*.c 所有の状態 (readback queue、decode / poll の view) を解放する。
// app_shutdown が呼ぶ。
void api_gfx_shutdown(App *app);
void api_audio_shutdown(App *app);
void api_io_shutdown(App *app);
void api_font_shutdown(App *app);
void api_mesh_shutdown(App *app);

// file の取得状態: 0 = pending (web で fetch 中)、1 = ready、2 = missing。
int lub_io_request_file(const char *path);
uint64_t lub_io_fnv1a64(const void *data, size_t len);
