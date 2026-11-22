// Helpers shared by the implementation of the C API (include/lub/lub_api.h).
// LubContext is the App itself.
#pragma once
#include "app.h"
#include "lub/lub_api.h"
#include "shader.h"
#include <stdbool.h>
#include <stddef.h>

static inline App *lub_api_app(LubContext *ctx) { return (App *)ctx; }
static inline LubContext *lub_api_ctx(App *app) { return (LubContext *)app; }

// Write the message to last_error and return LUB_ERROR.
LubStatus lub_api_fail(App *app, const char *fmt, ...);

// Special handle that refers to the render target of one XR eye (lub_xr_view
// returns it in XrView.target). Like main_tex (-1), it is not in the resource
// table, and begin_pass resolves it.
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

// NUL-terminated copy.
// false if it does not fit in cap.
static inline bool lub_str_copy(LubStr s, char *buf, size_t cap) {
  if (s.len < 0 || (size_t)s.len >= cap)
    return false;
  if (s.len > 0)
    memcpy(buf, s.ptr, (size_t)s.len);
  buf[s.len] = '\0';
  return true;
}

// Compile the shader for the target the current backend uses.
// On export, also put the output for the other targets used by the player of
// the export's target OS into the generated cache.
bool shader_compile_for_backend(const char *vs, const char *fs,
                                ShaderBlob *out_vs, ShaderBlob *out_fs,
                                ShaderReflection *out_refl, char *err,
                                size_t err_size);
bool shader_compile_compute_for_backend(const char *cs, ShaderBlob *out_cs,
                                        ShaderReflection *out_refl, char *err,
                                        size_t err_size);

// Maximum key length (including the terminating NUL).
#define LUB_KEY_MAX 256

// Called at the end of the frame (app_frame_end).
// Sweep of queues / snds declared by key.
void api_gfx_frame_end(App *app);
void api_audio_frame_end(App *app);
// In the guaranteed mode, whether the readbacks to deliver this frame have
// arrived from the GPU (checked by app_frame_waits_for_gpu).
bool api_gfx_readbacks_due_ready(App *app);
// Hand over the backing data of a frame-scoped view.
// Freed by app_frame_end.
// So that calling the same API again in the same frame does not break the
// previous view, APIs that return views do not reuse results and hand them over
// here instead.
void app_frame_garbage_push(App *app, void *ptr);
// Hand over data that needs a release function other than free (stb bitmaps,
// etc.).
void app_frame_garbage_push_fn(App *app, void *ptr, void (*free_fn)(void *));
// Release all handed-over data (called by app_frame_end and app_shutdown).
void app_frame_garbage_drain(App *app);

// Release the state owned by api_*.c (readback queues, decode / poll views).
// Called by app_shutdown.
void api_gfx_shutdown(App *app);
void api_audio_shutdown(App *app);
// Sweep of apps opened with App.Use, and the cleanup that closes all of them
// (api_app.c). The app the inspection APIs look at: the app itself if handle is
// 0, otherwise the app opened with App.Use (NULL if closed)
App *api_app_observed(App *app, LubHandle handle);
void api_app_frame_end(App *app);
void api_app_shutdown(App *app);
void api_io_shutdown(App *app);

uint64_t lub_io_fnv1a64(const void *data, size_t len);
