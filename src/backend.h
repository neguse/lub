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
  bool is_compute; // true: make_pipeline ignores graphics state and builds a
                   // compute pipeline
  // Bit i = reflection texture i is bound to a depth-format texture. Only
  // the webgpu backend consumes this (bind group layout sample type).
  uint8_t depth_tex_mask;
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
  // Applies to all attachments (color + depth). 0 (unset) behaves as
  // SGL_LOAD_CLEAR; SGL_LOAD_LOAD preserves the previous contents.
  SglLoadAction load;
} PassBeginDesc;

// A byte range of a buffer: a whole persistent buffer (offset 0, size = its
// logical size) or a per-frame transient allocation.
typedef struct BufferSlice {
  BackendBuffer buf;
  size_t offset;
  size_t size;
} BufferSlice;

// Buffer bindings carry a byte range (offset, size). Persistent buffers pass
// offset 0 and their logical size, which can be smaller than the buffer's
// capacity (make_buffer cap_bytes): the shader must see exactly `size` bytes
// (StructuredBuffer length, WGSL arrayLength and bounds clamping). Exception:
// sdlgpu has no range binding and binds the whole buffer, so there the shader
// sees the capacity (bytes past `size` are undefined). Transient slices
// (transient_buffer) pass the offset the backend returned.
//
// `slot` is the index of the entry in the reflection arrays (refl->texs,
// refl->storage_bufs, refl->storage_texs), resolved by the runtime from the
// name. It is not the binding slot (that is refl->storage_bufs[slot].slot).
// It is the FIRST entry with that name: the per-stage reflections are merged
// without de-duplication, so a resource read by both VS and FS has one entry
// per stage (each with its own binding slot). A backend that binds by `slot`
// must also bind the later entries with the same name (scan from `slot` to
// the end of the array), as the name-matching loops do today.
typedef struct BindingsDesc {
  const ShaderReflection
      *refl; // for resolving texture name -> slot. NULL = skip texture binding.
  BackendBuffer ibuf; // 0 = none (non-indexed); non-0 = u32 index buffer
  size_t ibuf_offset; // byte offset of the first index (multiple of 4)
  size_t ibuf_size;   // bytes readable from ibuf_offset
  int texture_count;
  struct {
    const char *name; // matches reflection name
    int slot;         // index into refl->texs
    BackendImage image;
  } textures[8];
  // Graphics-stage read-only storage buffers (StructuredBuffer<T> in a
  // vertex or fragment shader), resolved by reflection name like textures.
  int storage_buf_count;
  struct {
    const char *name; // matches ShaderStorageBuf.name
    int slot;         // index into refl->storage_bufs
    BackendBuffer buf;
    size_t offset;
    size_t size;
  } storage_bufs[SGL_MAX_STORAGE_BUFS];
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
  // Byte ranges and `slot` as in BindingsDesc. A transient slice is only
  // ever bound to a read-only StructuredBuffer (the runtime rejects RW use).
  int n_storage_bufs;
  struct {
    const char *name; // matches ShaderStorageBuf.name
    int slot;         // index into refl->storage_bufs
    BackendBuffer buf;
    size_t offset;
    size_t size;
  } storage_bufs[SGL_MAX_STORAGE_BUFS];
  int texture_count;
  struct {
    const char *name; // matches ShaderTexture.name
    int slot;         // index into refl->texs
    BackendImage image;
  } textures[SGL_MAX_TEXTURES];
  int n_storage_textures;
  struct {
    const char *name; // matches ShaderStorageTexture.name
    int slot;         // index into refl->storage_texs
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

  // Allocates cap_bytes and uploads the first data_bytes of `data` (data may
  // be NULL: contents undefined). cap_bytes >= data_bytes; the runtime keeps
  // spare capacity so a keyed buffer whose size changes a little is updated
  // in place (update_buffer writes a prefix) instead of recreated.
  BackendBuffer (*make_buffer)(SglBufferType type, const void *data,
                               size_t data_bytes, size_t cap_bytes);
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

  // Within a pass the runtime calls apply_pipeline only when the pipeline
  // differs from the one it last applied in that pass: the first draw of a
  // pass always applies, and so does the first draw after the ImGui renderer
  // (which drives the backend directly) or after a shader was replaced. The
  // state set by apply_pipeline / apply_bindings / apply_uniforms must stay
  // in effect across draws that share a pipeline, since those draws only call
  // apply_bindings, apply_uniforms (when they have uniforms) and draw.
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

  // Per-frame, write-once buffer data (Gfx.TransientBuffer, the ImGui
  // renderer). Copies `bytes` bytes of `data` now and returns a slice that
  // stays readable, with exactly that content, until the GPU work of the
  // current frame completes: as a read-only StructuredBuffer (STORAGE) or as
  // a u32 index buffer (INDEX), by every command of the frame whether it was
  // recorded before or after this call. Only called between begin_frame and
  // end_frame, inside or outside a pass; it must not split the pass or submit
  // work. Offset alignment is the backend's business (the slice is bound
  // through BindingsDesc / ComputeDispatchDesc offset and size). Returning
  // false fails the API call with an error.
  //
  // NULL = the runtime's portable fallback: one make_buffer per call,
  // destroyed after end_frame (every backend defers the GPU destruction of a
  // buffer until the frames using it are done). It is correct but costs one
  // GPU allocation per call, held for the frames in flight: on Vulkan one
  // vkAllocateMemory each (drivers commonly allow only 4096 in total), on
  // D3D12 one committed resource (64 KB minimum) each. A native
  // implementation must sub-allocate so that thousands of calls per frame
  // stay cheap.
  bool (*transient_buffer)(SglBufferType type, const void *data, size_t bytes,
                           BufferSlice *out);
} RenderBackend;

extern const RenderBackend *g_backend;
extern const RenderBackend g_backend_sdlgpu;
extern const RenderBackend g_backend_webgpu;
#ifdef _WIN32
extern const RenderBackend g_backend_d3d12; // backend_d3d12.cpp, Windows-only
#endif
#if defined(LUB_HAS_VULKAN)
extern const RenderBackend g_backend_vulkan; // backend_vulkan.c ("vulkan")
#endif

#ifdef __cplusplus
}
#endif
