// SDL3 GPU backend (Tasks 3-4).
//
// Task 3 implemented init/shutdown + frame/pass clear-only flow.
// Task 4 fills in make_buffer/make_shader/make_pipeline + apply_pipeline /
// apply_bindings / draw so sample 01_triangle (no uniforms, no textures)
// renders end-to-end.
//
// Sequence per frame:
//   begin_frame: AcquireGPUCommandBuffer -> AcquireGPUSwapchainTexture
//   begin_pass : BeginGPURenderPass with LOADOP_CLEAR
//   end_pass   : EndGPURenderPass
//   end_frame  : SubmitGPUCommandBuffer
#include "backend.h"
#include "app.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_gpu.h>
#include <stdlib.h>
#include <string.h>

// Render pass handle is per begin/end-pass pair. Stored in a file-static
// because the vtable's begin_pass/end_pass don't take a backend cookie.
static SDL_GPURenderPass *g_render_pass = NULL;

// Cached App* for use by resource-creation vtable functions that don't
// receive App* as an argument (make_buffer/make_shader/make_pipeline).
// PoC concession: there is exactly one App per process. Set in sg_init
// (and re-confirmed each begin_frame as a paranoia measure).
static App *g_app = NULL;

// Most-recently bound pipeline. Tracked here because SDL_GPU's pipeline
// binding is per-render-pass and we need to (re)issue it after begin_pass
// if the user calls apply_pipeline before begin_pass — but for sample 01
// the order is begin_pass -> apply_pipeline -> apply_bindings -> draw, so
// this just records the current pipeline for future use.
static struct SgPipeline *g_current_pip = NULL;

// --- per-resource backend objects ----------------------------------------

typedef struct SgBuffer {
    SDL_GPUBuffer *gpu;
    Uint32 bytes;
    SglBufferType type;
} SgBuffer;

typedef struct SgShader {
    SDL_GPUShader *vs;
    SDL_GPUShader *fs;
    ShaderReflection refl;
} SgShader;

typedef struct SgPipeline {
    SDL_GPUGraphicsPipeline *gpu;
    ShaderReflection refl;
} SgPipeline;

// --- backend lifecycle ----------------------------------------------------

static bool sg_init(App *app) {
    app->gpu_device = SDL_CreateGPUDevice(SDL_GPU_SHADERFORMAT_SPIRV, true, NULL);
    if (!app->gpu_device) {
        SDL_Log("SDL_CreateGPUDevice failed: %s", SDL_GetError());
        return false;
    }
    if (!SDL_ClaimWindowForGPUDevice(app->gpu_device, app->window)) {
        SDL_Log("SDL_ClaimWindowForGPUDevice failed: %s", SDL_GetError());
        SDL_DestroyGPUDevice(app->gpu_device);
        app->gpu_device = NULL;
        return false;
    }
    g_app = app;
    return true;
}

static void sg_shutdown(App *app) {
    if (app->gpu_device) {
        SDL_ReleaseWindowFromGPUDevice(app->gpu_device, app->window);
        SDL_DestroyGPUDevice(app->gpu_device);
        app->gpu_device = NULL;
    }
    g_app = NULL;
}

static void sg_begin_frame(App *app, int *out_w, int *out_h) {
    g_app = app;
    app->gpu_cmd = SDL_AcquireGPUCommandBuffer(app->gpu_device);
    if (!app->gpu_cmd) {
        SDL_Log("SDL_AcquireGPUCommandBuffer failed: %s", SDL_GetError());
        if (out_w) *out_w = 0;
        if (out_h) *out_h = 0;
        return;
    }
    Uint32 sw = 0, sh = 0;
    app->gpu_swapchain_tex = NULL;
    if (!SDL_AcquireGPUSwapchainTexture(app->gpu_cmd, app->window,
                                          &app->gpu_swapchain_tex, &sw, &sh)) {
        SDL_Log("SDL_AcquireGPUSwapchainTexture failed: %s", SDL_GetError());
    }
    if (out_w) *out_w = (int)sw;
    if (out_h) *out_h = (int)sh;
}

static void sg_end_frame(App *app) {
    if (app->gpu_cmd && !SDL_SubmitGPUCommandBuffer(app->gpu_cmd)) {
        SDL_Log("SDL_SubmitGPUCommandBuffer failed: %s", SDL_GetError());
    }
    app->gpu_cmd = NULL;
    app->gpu_swapchain_tex = NULL;
}

static void sg_begin_pass(App *app, const PassBeginDesc *d) {
    if (!app->gpu_swapchain_tex) {
        g_render_pass = NULL;
        return;
    }
    SDL_GPUColorTargetInfo target = {
        .texture = app->gpu_swapchain_tex,
        .clear_color = { d->clear[0], d->clear[1], d->clear[2], d->clear[3] },
        .load_op = SDL_GPU_LOADOP_CLEAR,
        .store_op = SDL_GPU_STOREOP_STORE,
    };
    g_render_pass = SDL_BeginGPURenderPass(app->gpu_cmd, &target, 1, NULL);
}

static void sg_end_pass(App *app) {
    (void)app;
    if (g_render_pass) {
        SDL_EndGPURenderPass(g_render_pass);
        g_render_pass = NULL;
    }
    g_current_pip = NULL;
}

// --- resources ------------------------------------------------------------

static BackendBuffer sg_make_buffer(SglBufferType type, const float *data, size_t bytes) {
    if (!g_app || !g_app->gpu_device) {
        SDL_Log("sg_make_buffer: no GPU device");
        return 0;
    }
    SgBuffer *b = (SgBuffer*)calloc(1, sizeof(SgBuffer));
    if (!b) return 0;
    b->bytes = (Uint32)bytes;
    b->type = type;
    b->gpu = SDL_CreateGPUBuffer(g_app->gpu_device, &(SDL_GPUBufferCreateInfo){
        .usage = (type == SGL_BUFFER_VERTEX) ? SDL_GPU_BUFFERUSAGE_VERTEX
                                              : SDL_GPU_BUFFERUSAGE_INDEX,
        .size  = (Uint32)bytes,
    });
    if (!b->gpu) {
        SDL_Log("SDL_CreateGPUBuffer failed: %s", SDL_GetError());
        free(b);
        return 0;
    }
    SDL_GPUTransferBuffer *tb = SDL_CreateGPUTransferBuffer(g_app->gpu_device,
        &(SDL_GPUTransferBufferCreateInfo){
            .usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
            .size = (Uint32)bytes,
        });
    if (!tb) {
        SDL_Log("SDL_CreateGPUTransferBuffer failed: %s", SDL_GetError());
        SDL_ReleaseGPUBuffer(g_app->gpu_device, b->gpu);
        free(b);
        return 0;
    }
    void *dst = SDL_MapGPUTransferBuffer(g_app->gpu_device, tb, false);
    if (!dst) {
        SDL_Log("sg_make_buffer: SDL_MapGPUTransferBuffer failed: %s", SDL_GetError());
        SDL_ReleaseGPUTransferBuffer(g_app->gpu_device, tb);
        SDL_ReleaseGPUBuffer(g_app->gpu_device, b->gpu);
        free(b);
        return 0;
    }
    memcpy(dst, data, bytes);
    SDL_UnmapGPUTransferBuffer(g_app->gpu_device, tb);

    SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(g_app->gpu_device);
    if (!cmd) {
        SDL_Log("sg_make_buffer: SDL_AcquireGPUCommandBuffer failed: %s", SDL_GetError());
        SDL_ReleaseGPUTransferBuffer(g_app->gpu_device, tb);
        SDL_ReleaseGPUBuffer(g_app->gpu_device, b->gpu);
        free(b);
        return 0;
    }
    SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
    SDL_UploadToGPUBuffer(cp,
        &(SDL_GPUTransferBufferLocation){ .transfer_buffer = tb, .offset = 0 },
        &(SDL_GPUBufferRegion){ .buffer = b->gpu, .offset = 0, .size = (Uint32)bytes },
        false);
    SDL_EndGPUCopyPass(cp);
    SDL_SubmitGPUCommandBuffer(cmd);
    SDL_ReleaseGPUTransferBuffer(g_app->gpu_device, tb);
    return (uintptr_t)b;
}

static void sg_destroy_buffer(BackendBuffer h) {
    SgBuffer *b = (SgBuffer*)h;
    if (!b) return;
    if (g_app && g_app->gpu_device && b->gpu) {
        SDL_ReleaseGPUBuffer(g_app->gpu_device, b->gpu);
    }
    free(b);
}

static BackendImage sg_make_image(const ImageDesc *d) {
    (void)d;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: make_image not yet implemented (Task 6)"); warned = true; }
    return 0;
}

static BackendShader sg_make_shader(const ShaderDesc *d) {
    if (!g_app || !g_app->gpu_device) {
        SDL_Log("sg_make_shader: no GPU device");
        return 0;
    }
    SgShader *s = (SgShader*)calloc(1, sizeof(SgShader));
    if (!s) return 0;
    if (d->refl) s->refl = *d->refl;
    // Slang's SPIR-V emitter renames the entry-point function to "main"
    // (same convention used by the sokol backend). Both vs and fs blobs
    // each have a single "main" entry point.
    s->vs = SDL_CreateGPUShader(g_app->gpu_device, &(SDL_GPUShaderCreateInfo){
        .code = (const Uint8*)d->vs_spirv,
        .code_size = d->vs_bytes,
        .entrypoint = "main",
        .format = SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = SDL_GPU_SHADERSTAGE_VERTEX,
        .num_uniform_buffers = (Uint32)(d->refl ? d->refl->ub_count : 0),
        .num_storage_buffers = 0,
        .num_storage_textures = 0,
        .num_samplers = 0,
    });
    s->fs = SDL_CreateGPUShader(g_app->gpu_device, &(SDL_GPUShaderCreateInfo){
        .code = (const Uint8*)d->fs_spirv,
        .code_size = d->fs_bytes,
        .entrypoint = "main",
        .format = SDL_GPU_SHADERFORMAT_SPIRV,
        .stage = SDL_GPU_SHADERSTAGE_FRAGMENT,
        .num_uniform_buffers = 0,
        .num_storage_buffers = 0,
        .num_storage_textures = 0,
        .num_samplers = (Uint32)(d->refl ? d->refl->tex_count : 0),
    });
    if (!s->vs || !s->fs) {
        SDL_Log("sg_make_shader: shader create failed (vs=%p fs=%p): %s",
                (void*)s->vs, (void*)s->fs, SDL_GetError());
        if (s->vs) SDL_ReleaseGPUShader(g_app->gpu_device, s->vs);
        if (s->fs) SDL_ReleaseGPUShader(g_app->gpu_device, s->fs);
        free(s);
        return 0;
    }
    return (uintptr_t)s;
}

static void sg_destroy_shader(BackendShader h) {
    SgShader *s = (SgShader*)h;
    if (!s) return;
    if (g_app && g_app->gpu_device) {
        if (s->vs) SDL_ReleaseGPUShader(g_app->gpu_device, s->vs);
        if (s->fs) SDL_ReleaseGPUShader(g_app->gpu_device, s->fs);
    }
    free(s);
}

static BackendPipeline sg_make_pipeline(const PipelineDesc *d) {
    if (!g_app || !g_app->gpu_device) {
        SDL_Log("sg_make_pipeline: no GPU device");
        return 0;
    }
    SgShader *sh = (SgShader*)d->shader;
    if (!sh || !sh->vs || !sh->fs) {
        SDL_Log("sg_make_pipeline: invalid shader");
        return 0;
    }
    SgPipeline *p = (SgPipeline*)calloc(1, sizeof(SgPipeline));
    if (!p) return 0;
    if (d->refl) p->refl = *d->refl;

    SDL_GPUVertexAttribute attrs[SGL_MAX_ATTRS];
    int attr_count = d->refl ? d->refl->attr_count : 0;
    for (int i = 0; i < attr_count; ++i) {
        SDL_GPUVertexElementFormat fmt;
        switch (d->refl->attrs[i].comp_count) {
            case 1: fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT;  break;
            case 2: fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2; break;
            case 3: fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3; break;
            default: fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4;
        }
        attrs[i] = (SDL_GPUVertexAttribute){
            .location = (Uint32)d->refl->attrs[i].slot,
            .buffer_slot = 0,
            .format = fmt,
            .offset = (Uint32)(d->refl->attrs[i].offset_floats * sizeof(float)),
        };
    }
    SDL_GPUVertexBufferDescription vbd = {
        .slot = 0,
        .pitch = (Uint32)((d->refl ? d->refl->vertex_stride_floats : 0) * sizeof(float)),
        .input_rate = SDL_GPU_VERTEXINPUTRATE_VERTEX,
        .instance_step_rate = 0,
    };
    SDL_GPUColorTargetDescription ctd = {
        .format = SDL_GetGPUSwapchainTextureFormat(g_app->gpu_device, g_app->window),
        // PoC: blend off (sample 01 uses SGL_BLEND_NONE).
    };

    SDL_GPUPrimitiveType prim;
    switch (d->primitive) {
        case SGL_PRIM_LINES:          prim = SDL_GPU_PRIMITIVETYPE_LINELIST; break;
        case SGL_PRIM_LINE_STRIP:     prim = SDL_GPU_PRIMITIVETYPE_LINESTRIP; break;
        case SGL_PRIM_POINTS:         prim = SDL_GPU_PRIMITIVETYPE_POINTLIST; break;
        case SGL_PRIM_TRIANGLE_STRIP: prim = SDL_GPU_PRIMITIVETYPE_TRIANGLESTRIP; break;
        case SGL_PRIM_TRIANGLES:
        default:                       prim = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST; break;
    }

    SDL_GPUCullMode cull =
        (d->cull == SGL_CULL_BACK)  ? SDL_GPU_CULLMODE_BACK :
        (d->cull == SGL_CULL_FRONT) ? SDL_GPU_CULLMODE_FRONT :
                                       SDL_GPU_CULLMODE_NONE;

    p->gpu = SDL_CreateGPUGraphicsPipeline(g_app->gpu_device,
        &(SDL_GPUGraphicsPipelineCreateInfo){
            .vertex_shader = sh->vs,
            .fragment_shader = sh->fs,
            .vertex_input_state = {
                .vertex_buffer_descriptions = &vbd,
                .num_vertex_buffers = 1,
                .vertex_attributes = attrs,
                .num_vertex_attributes = (Uint32)attr_count,
            },
            .primitive_type = prim,
            .rasterizer_state = {
                .fill_mode = SDL_GPU_FILLMODE_FILL,
                .cull_mode = cull,
                .front_face = SDL_GPU_FRONTFACE_COUNTER_CLOCKWISE,
            },
            .multisample_state = {
                .sample_count = SDL_GPU_SAMPLECOUNT_1,
            },
            .depth_stencil_state = {
                // PoC: depth disabled (no depth attachment in our render pass).
                .enable_depth_test = false,
                .enable_depth_write = false,
            },
            .target_info = {
                .color_target_descriptions = &ctd,
                .num_color_targets = 1,
                .has_depth_stencil_target = false,
            },
        });
    if (!p->gpu) {
        SDL_Log("sg_make_pipeline: SDL_CreateGPUGraphicsPipeline failed: %s", SDL_GetError());
        free(p);
        return 0;
    }
    return (uintptr_t)p;
}

static void sg_destroy_pipeline(BackendPipeline h) {
    SgPipeline *p = (SgPipeline*)h;
    if (!p) return;
    if (g_app && g_app->gpu_device && p->gpu) {
        SDL_ReleaseGPUGraphicsPipeline(g_app->gpu_device, p->gpu);
    }
    free(p);
}

static void sg_destroy_image(BackendImage h) { (void)h; }

// --- draw -----------------------------------------------------------------

static void sg_apply_pipeline(BackendPipeline h) {
    g_current_pip = (SgPipeline*)h;
    if (g_current_pip && g_current_pip->gpu && g_render_pass) {
        SDL_BindGPUGraphicsPipeline(g_render_pass, g_current_pip->gpu);
    }
}

static void sg_apply_bindings(const BindingsDesc *b) {
    if (!g_render_pass) return;
    if (b->vbuf) {
        SgBuffer *vb = (SgBuffer*)b->vbuf;
        if (vb && vb->gpu) {
            SDL_BindGPUVertexBuffers(g_render_pass, 0,
                &(SDL_GPUBufferBinding){ .buffer = vb->gpu, .offset = 0 }, 1);
        }
    }
    // Texture binding: deferred to Task 6.
}

static void sg_apply_uniforms(int slot, const void *d, size_t b) {
    (void)slot; (void)d; (void)b;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: apply_uniforms not yet implemented (Task 7)"); warned = true; }
}

static void sg_draw(int base, int count) {
    if (!g_render_pass) return;
    SDL_DrawGPUPrimitives(g_render_pass, (Uint32)count, 1, (Uint32)base, 0);
}

static bool sg_capture(App *app, const char *path) {
    (void)app; (void)path;
    SDL_Log("sdlgpu capture not yet implemented (Task 8)");
    return false;
}

static SglPixelFormat sg_swapchain_color_format(App *app) {
    SDL_GPUTextureFormat fmt = SDL_GetGPUSwapchainTextureFormat(app->gpu_device, app->window);
    switch (fmt) {
        case SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM:
        case SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB:
            return SGL_PF_RGBA8;
        case SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM:
        case SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM_SRGB:
            return SGL_PF_BGRA8;
        default:
            return SGL_PF_BGRA8;
    }
}

const RenderBackend g_backend_sdlgpu = {
    .name = "sdlgpu",
    .init = sg_init,
    .shutdown = sg_shutdown,
    .begin_frame = sg_begin_frame,
    .end_frame = sg_end_frame,
    .make_buffer = sg_make_buffer,
    .make_image = sg_make_image,
    .make_shader = sg_make_shader,
    .make_pipeline = sg_make_pipeline,
    .destroy_buffer = sg_destroy_buffer,
    .destroy_image = sg_destroy_image,
    .destroy_shader = sg_destroy_shader,
    .destroy_pipeline = sg_destroy_pipeline,
    .begin_pass = sg_begin_pass,
    .end_pass = sg_end_pass,
    .apply_pipeline = sg_apply_pipeline,
    .apply_bindings = sg_apply_bindings,
    .apply_uniforms = sg_apply_uniforms,
    .draw = sg_draw,
    .capture = sg_capture,
    .swapchain_color_format = sg_swapchain_color_format,
};
