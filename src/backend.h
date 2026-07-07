#pragma once
#include "enums.h"
#include "shader.h"
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

struct App;

// Opaque handles. Each backend casts integer IDs or pointers into uintptr_t.
typedef uintptr_t BackendBuffer;
typedef uintptr_t BackendImage;
typedef uintptr_t BackendShader;
typedef uintptr_t BackendPipeline;
typedef uintptr_t BackendReadback;

// Max color attachments for MRT (G-buffer style). SDL_GPU supports up to 4
// on every platform. lub exposes 4 until a real use case needs more.
#define SGL_MAX_COLOR_TARGETS 4

typedef struct ImageDesc {
  SglPixelFormat fmt;
  int w, h;
  const uint8_t *data;
  size_t data_bytes;
  SglFilter filter;   // 0 = default (LINEAR)
  SglWrap wrap;       // 0 = default (REPEAT)
  bool render_target; // true = usable as color attachment (no initial data)
  bool storage;       // true = usable as compute storage texture output
} ImageDesc;

typedef struct ShaderDesc {
  const uint32_t *vs_spirv;
  size_t vs_bytes;
  const uint32_t *fs_spirv;
  size_t fs_bytes;
  // Compute shader. Either (vs_spirv & fs_spirv) or cs_spirv is non-NULL,
  // never both; the backend branches on cs_spirv != NULL.
  const uint32_t *cs_spirv;
  size_t cs_bytes;
  const ShaderReflection *refl;
} ShaderDesc;

typedef struct PipelineDesc {
  BackendShader shader;
  const ShaderReflection *refl;
  SglBlend blend;
  bool depth_test;
  bool depth_write;
  SglCull cull;
  SglPrimitive primitive;
  int n_color_targets; // 1..SGL_MAX_COLOR_TARGETS
  SglPixelFormat color_fmts[SGL_MAX_COLOR_TARGETS];
  bool has_depth;           // false = offscreen color-only pass
  SglPixelFormat depth_fmt; // valid when has_depth
  bool is_indexed; // true = pipeline used for indexed draw (u32 indices)
  bool is_compute; // true: make_pipeline ignores graphics state and builds a
                   // compute pipeline
} PipelineDesc;

typedef struct PassBeginDesc {
  int n_color_targets; // 0..SGL_MAX_COLOR_TARGETS; 0 = depth-only
  BackendImage
      targets[SGL_MAX_COLOR_TARGETS]; // targets[0] == 0 (with n_color_targets
                                      // == 1) => swapchain
  SglPixelFormat color_fmts[SGL_MAX_COLOR_TARGETS]; // per-target color format
  int target_w, target_h; // offscreen target size (ignored for swapchain)
  float clear[SGL_MAX_COLOR_TARGETS][4];
  BackendImage
      depth_target; // 0 = swapchain/default depth or no offscreen depth
  SglPixelFormat depth_fmt;
  float clear_depth;
  bool has_depth;
} PassBeginDesc;

typedef struct BindingsDesc {
  const ShaderReflection
      *refl; // for resolving texture name -> slot. NULL = skip texture binding.
  BackendBuffer vbuf;          // 0 = none
  BackendBuffer instance_vbuf; // 0 = none; slot 1, per-instance attributes
  BackendBuffer ibuf; // 0 = none (non-indexed); non-0 = u32 index buffer
  int texture_count;
  struct {
    const char *name; // matches reflection name
    BackendImage image;
  } textures[8];
} BindingsDesc;

typedef struct ReadbackResult {
  int w, h;
  int stride;
  SglPixelFormat fmt; // result format, currently RGBA8
  uint8_t *data;      // malloc'd; caller takes ownership
  size_t data_bytes;
} ReadbackResult;

typedef enum ReadbackPollStatus {
  READBACK_POLL_PENDING = 0,
  READBACK_POLL_READY = 1,
  READBACK_POLL_ERROR = 2,
} ReadbackPollStatus;

// Compute dispatch: bundles pipeline + storage-buffer bindings + uniform data
// into a single backend call. The backend wraps everything in its own
// compute pass (begin/dispatch/end). Issued outside begin_pass/end_pass.
typedef struct ComputeDispatchDesc {
  BackendPipeline pipeline;
  const ShaderReflection *refl;
  int groups_x, groups_y, groups_z;
  int n_storage_bufs;
  struct {
    const char *name; // matches ShaderStorageBuf.name
    BackendBuffer buf;
  } storage_bufs[SGL_MAX_STORAGE_BUFS];
  int texture_count;
  struct {
    const char *name; // matches ShaderTexture.name
    BackendImage image;
  } textures[SGL_MAX_TEXTURES];
  int n_storage_textures;
  struct {
    const char *name; // matches ShaderStorageTexture.name
    BackendImage image;
  } storage_textures[SGL_MAX_STORAGE_TEXTURES];
  int uniform_count;
  struct {
    SglShaderStage stage;
    int slot;
    const void *data;
    size_t bytes;
  } uniforms[SGL_MAX_UNIFORM_BLOCKS];
} ComputeDispatchDesc;

typedef struct RenderBackend {
  const char *name;

  bool (*init)(struct App *app);
  void (*shutdown)(struct App *app);

  void (*begin_frame)(struct App *app, int *out_w, int *out_h);
  void (*end_frame)(struct App *app);

  BackendBuffer (*make_buffer)(SglBufferType type, const void *data,
                               size_t bytes);
  BackendImage (*make_image)(const ImageDesc *desc);
  BackendShader (*make_shader)(const ShaderDesc *desc);
  BackendPipeline (*make_pipeline)(const PipelineDesc *desc);

  void (*destroy_buffer)(BackendBuffer);
  void (*destroy_image)(BackendImage);
  void (*destroy_shader)(BackendShader);
  void (*destroy_pipeline)(BackendPipeline);

  void (*update_buffer)(BackendBuffer h, const void *data, size_t bytes);
  void (*update_image)(BackendImage h, const void *data, size_t bytes);

  void (*begin_pass)(struct App *app, const PassBeginDesc *);
  void (*end_pass)(struct App *app);

  void (*apply_pipeline)(BackendPipeline);
  void (*apply_bindings)(const BindingsDesc *);
  void (*apply_uniforms)(SglShaderStage stage, int ub_slot, const void *data,
                         size_t bytes);
  void (*draw)(int base, int count, int instance_count);
  // Scissor rect in framebuffer pixels, top-left origin. Only valid inside a
  // render pass; begin_pass resets it to the full target.
  void (*set_scissor)(int x, int y, int w, int h);

  // Compute dispatch (outside any render pass). The backend opens its own
  // compute pass internally; the call must not be made between begin_pass
  // and end_pass.
  void (*dispatch)(struct App *app, const ComputeDispatchDesc *);

  bool (*request_readback_image)(struct App *app, BackendImage image, int w,
                                 int h, SglPixelFormat src_fmt,
                                 BackendReadback *out);
  ReadbackPollStatus (*poll_readback)(BackendReadback req, ReadbackResult *out);
  void (*destroy_readback)(BackendReadback req);

  bool (*capture)(struct App *app, const char *path);
  bool capture_before_end_frame;

  // Pipeline cache uses this as part of its key — both backends must
  // return the swapchain's color format for the current frame.
  SglPixelFormat (*swapchain_color_format)(struct App *app);
} RenderBackend;

extern const RenderBackend *g_backend;
extern const RenderBackend g_backend_sdlgpu;
extern const RenderBackend g_backend_webgpu;
#ifdef _WIN32
extern const RenderBackend g_backend_dx12; // backend_dx12.cpp, Windows-only
#endif

#ifdef __cplusplus
}
#endif
