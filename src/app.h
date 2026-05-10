#pragma once
#include <SDL3/SDL.h>
#include <SDL3/SDL_vulkan.h>
#include <SDL3/SDL_gpu.h>
#include <vulkan/vulkan.h>
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

    // Per-frame semaphores (PoC: 1 ペアのみ)
    VkSemaphore      vk_acquire_sem;
    VkSemaphore      vk_present_sem;
    uint32_t         vk_current_image;

    // Frame snapshot for capture: the swapchain image presented this frame.
    // Set in begin_frame, used by sokol backend's capture path.
    VkImage          vk_last_presented_image;

    LuaCtx        lua;
    PassState     pass;
    ResTable      res;
    PipelineCache pip_cache;
    uint64_t      frame_index;

    // Offscreen capture
    CaptureState  capture;
    bool          capture_then_exit; // set by app_frame_end after a successful capture
    int           last_w, last_h;    // last extents seen by app_frame_begin

    // Backend selection (Task 2). app_init sets phase = PRE_BACKEND and
    // backend_name = "sokol". Lua's config() may overwrite backend_name during
    // on_init (PRE_BACKEND only). app_backend_init flips phase to POST_BACKEND
    // after the backend's init() succeeds.
    AppPhase      phase;
    char          backend_name[16];

    // SDL3 GPU backend state (Task 3). Owned/used by backend_sdlgpu.c only.
    SDL_GPUDevice       *gpu_device;
    SDL_GPUTexture      *gpu_swapchain_tex;  // current frame の swapchain
    SDL_GPUCommandBuffer *gpu_cmd;           // current frame
    // Snapshot for capture (Task 8): set in sg_end_frame just before
    // gpu_swapchain_tex is cleared. capture_state_drain runs AFTER
    // end_frame, so sg_capture reads from this field instead.
    SDL_GPUTexture      *gpu_last_swapchain_tex;
} App;

bool app_init(App *app);
bool app_backend_init(App *app);  // call after lua on_init has run; returns false on backend init failure
void app_frame_begin(App *app, int *out_w, int *out_h);
void app_frame_end(App *app);
void app_shutdown(App *app);
