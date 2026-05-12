#pragma once
#include <SDL3/SDL.h>
#ifndef __EMSCRIPTEN__
// Vulkan + SDL_GPU headers only exist on the native build; the wasm path
// uses sokol's WGPU backend and doesn't touch these APIs at all.
#include <SDL3/SDL_vulkan.h>
#include <SDL3/SDL_gpu.h>
#include <vulkan/vulkan.h>
#else
// emdawnwebgpu port: the same webgpu/webgpu.h header that sokol_gfx's WGPU
// backend pulls in. Defining types here keeps the App struct WGPU fields
// real (opaque pointer typedefs) instead of void* placeholders.
#include <webgpu/webgpu.h>
#endif
#include <stdint.h>
#include <stdbool.h>
#include "lua_api.h"
#include "pass.h"
#include "resources.h"
#include "pipeline.h"
#include "capture.h"

typedef enum {
    APP_PHASE_PRE_BACKEND,
    APP_PHASE_POST_BACKEND
} AppPhase;

typedef struct App {
    SDL_Window *window;

#ifndef __EMSCRIPTEN__
    // Vulkan core (still owned by App in Task 1; sokol backend reads/writes
    // these directly. Task 3 will introduce a parallel sdlgpu state set.)
    VkInstance       vk_instance;
    VkPhysicalDevice vk_phys;
    VkDevice         vk_device;
    VkQueue          vk_queue;
    uint32_t         vk_queue_family;

    // Surface & swapchain
    VkSurfaceKHR     vk_surface;
    VkSwapchainKHR   vk_swapchain;
    VkFormat         vk_swapchain_format;
    uint32_t         vk_swapchain_image_count;
    VkImage         *vk_swapchain_images;
    VkImageView     *vk_swapchain_views;

    // Depth attachment (swapchain 全体で 1 枚共有)
    VkImage          vk_depth_image;
    VkDeviceMemory   vk_depth_mem;
    VkImageView      vk_depth_view;

    // Per-frame semaphores: 1 ペア / swapchain image. frame_index % N で回す。
    // 単一ペアだと前フレームの present が in-flight な間に acquire / submit を
    // 同じ semaphore で再利用してしまい VUID-vkAcquireNextImageKHR-semaphore-01779
    // および vkQueueSubmit-pSignalSemaphores-00067 に抵触する。
    VkSemaphore     *vk_acquire_sems;
    VkSemaphore     *vk_present_sems;
    uint32_t         vk_current_image;

    // Frame snapshot for capture: the swapchain image presented this frame.
    // Set in begin_frame, used by sokol backend's capture path.
    VkImage          vk_last_presented_image;
#else  // __EMSCRIPTEN__
    // WGPU state. Mirrors the Vulkan owners above: the wasm sokol backend
    // creates these in sk_init and tears them down in sk_shutdown. The
    // surface lives across resizes; depth_stencil + the current swapchain
    // view get rebuilt when canvas extents change.
    WGPUInstance     wgpu_instance;
    WGPUDevice       wgpu_device;
    WGPUSurface      wgpu_surface;
    WGPUTextureFormat wgpu_surface_format;   // WGPUTextureFormat_BGRA8Unorm
    WGPUTexture      wgpu_depth_tex;
    WGPUTextureView  wgpu_depth_view;
    // Per-frame: acquired from wgpuSurfaceGetCurrentTexture in begin_frame,
    // released in end_frame. begin_pass reads it to populate the swapchain
    // attachment.
    WGPUTexture      wgpu_swapchain_tex;
    WGPUTextureView  wgpu_swapchain_view;
#endif  // __EMSCRIPTEN__

    LuaCtx        lua;
    PassState     pass;
    ResTable      res;
    PipelineCache pip_cache;
    uint64_t      frame_index;

    // Offscreen capture
    CaptureState  capture;
    bool          capture_then_exit; // set by app_frame_end after a successful capture
    int           last_w, last_h;    // last extents seen by app_frame_begin

    // Set by SDL_AppEvent on SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED. The backend's
    // begin_frame checks this and rebuilds its swapchain before acquire. The
    // VkResult path (OUT_OF_DATE / SUBOPTIMAL) is also a trigger — both write
    // through this flag so the swapchain is only ever rebuilt at frame start.
    bool          pending_resize;

    // Backend selection (Task 2). app_init sets phase = PRE_BACKEND and
    // backend_name = "sokol". Lua's config() may overwrite backend_name during
    // on_init (PRE_BACKEND only). app_backend_init flips phase to POST_BACKEND
    // after the backend's init() succeeds.
    AppPhase      phase;
    char          backend_name[16];

    // Frame-based GC threshold. 0 disables sweeping. When > 0, app_frame_end
    // releases resource / pipeline entries whose last_seen_frame is older than
    // (frame_index - resource_sweep_after_frames). Configurable via Lua
    // config({ resource_sweep_after_frames = N }) during on_init.
    int           resource_sweep_after_frames;

#ifndef __EMSCRIPTEN__
    // SDL3 GPU backend state (Task 3). Owned/used by backend_sdlgpu.c only.
    SDL_GPUDevice       *gpu_device;
    SDL_GPUTexture      *gpu_swapchain_tex;  // current frame の swapchain
    SDL_GPUCommandBuffer *gpu_cmd;           // current frame
    // Snapshot for capture (Task 8): set in sg_end_frame just before
    // gpu_swapchain_tex is cleared. capture_state_drain runs AFTER
    // end_frame, so sg_capture reads from this field instead.
    SDL_GPUTexture      *gpu_last_swapchain_tex;
#endif  // __EMSCRIPTEN__

    // Entry .lua hot-reload state. main.c populates entry_path /
    // entry_module_name after app_init; app_frame_begin polls mtime each
    // frame and calls lume.hotswap when it changes.
    char    entry_path[256];        // e.g. "samples/01_triangle.lua"
    char    entry_module_name[128]; // e.g. "01_triangle"
    int64_t entry_mtime_cache;      // last observed mtime in ns; 0 means "unknown / first poll"
} App;

bool app_init(App *app);
bool app_backend_init(App *app);  // call after lua on_init has run; returns false on backend init failure
void app_frame_begin(App *app, int *out_w, int *out_h);
void app_frame_end(App *app);
void app_shutdown(App *app);

// Returns mtime of `path` in nanoseconds since epoch (sub-second precision on
// POSIX, seconds * 1e9 on Windows). Returns 0 if the file does not exist or
// stat fails. Used by both the C-side entry-Lua mtime poll in app_frame_begin
// and the `file_mtime` Lua binding consumed by samples/sg_io.lua.
int64_t app_file_mtime_ns(const char *path);
