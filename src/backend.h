#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include "enums.h"
#include "shader.h"

#ifdef __cplusplus
extern "C" {
#endif

struct App;

// Opaque handles. Each backend casts integer IDs or pointers into uintptr_t.
typedef uintptr_t BackendBuffer;
typedef uintptr_t BackendImage;
typedef uintptr_t BackendShader;
typedef uintptr_t BackendPipeline;

// Max color attachments for MRT (G-buffer style). Sokol's hard cap is
// SG_MAX_COLOR_ATTACHMENTS = 8; SDL_GPU also supports up to 4 on every
// platform. 4 is more than enough for the PoC samples.
#define SGL_MAX_COLOR_TARGETS 4

typedef struct ImageDesc {
    SglPixelFormat fmt;
    int w, h;
    const uint8_t *data;
    size_t data_bytes;
    SglFilter filter;   // 0 = default (LINEAR)
    SglWrap   wrap;     // 0 = default (REPEAT)
    bool render_target; // true = usable as color attachment (no initial data)
} ImageDesc;

typedef struct ShaderDesc {
    const uint32_t *vs_spirv; size_t vs_bytes;
    const uint32_t *fs_spirv; size_t fs_bytes;
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
    int n_color_targets;       // 1..SGL_MAX_COLOR_TARGETS
    SglPixelFormat color_fmts[SGL_MAX_COLOR_TARGETS];
    bool has_depth;            // false = offscreen color-only pass
} PipelineDesc;

typedef struct PassBeginDesc {
    int n_color_targets;          // 1..SGL_MAX_COLOR_TARGETS
    BackendImage targets[SGL_MAX_COLOR_TARGETS]; // targets[0] == 0 (with n_color_targets == 1) => swapchain
    SglPixelFormat color_fmts[SGL_MAX_COLOR_TARGETS]; // per-target color format
    int target_w, target_h;       // offscreen target size (ignored for swapchain)
    float clear[SGL_MAX_COLOR_TARGETS][4];
} PassBeginDesc;

typedef struct BindingsDesc {
    const ShaderReflection *refl; // for resolving texture name -> slot. NULL = skip texture binding.
    BackendBuffer vbuf;           // 0 = none
    int texture_count;
    struct {
        const char *name;         // matches reflection name
        BackendImage image;
    } textures[8];
} BindingsDesc;

typedef struct RenderBackend {
    const char *name;

    bool (*init)(struct App *app);
    void (*shutdown)(struct App *app);

    void (*begin_frame)(struct App *app, int *out_w, int *out_h);
    void (*end_frame)(struct App *app);

    BackendBuffer   (*make_buffer)(SglBufferType type, const float *data, size_t bytes);
    BackendImage    (*make_image)(const ImageDesc *desc);
    BackendShader   (*make_shader)(const ShaderDesc *desc);
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
    void (*apply_uniforms)(int ub_slot, const void *data, size_t bytes);
    void (*draw)(int base, int count);

    bool (*capture)(struct App *app, const char *path);

    // Pipeline cache uses this as part of its key — both backends must
    // return the swapchain's color format for the current frame.
    SglPixelFormat (*swapchain_color_format)(struct App *app);
} RenderBackend;

extern const RenderBackend *g_backend;
extern const RenderBackend g_backend_sokol;
extern const RenderBackend g_backend_sdlgpu;

#ifdef __cplusplus
}
#endif
