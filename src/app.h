#pragma once
#include <SDL3/SDL.h>
#include <SDL3/SDL_vulkan.h>
#include <vulkan/vulkan.h>
#include <stdint.h>
#include <stdbool.h>
#include "lua_api.h"
#include "pass.h"
#include "resources.h"
#include "pipeline.h"

typedef struct App {
    SDL_Window *window;

    // Vulkan core
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

    LuaCtx        lua;
    PassState     pass;
    ResTable      res;
    PipelineCache pip_cache;
    uint64_t      frame_index;
} App;

bool app_init(App *app);
void app_frame_begin(App *app, int *out_w, int *out_h);
void app_frame_end(App *app);
void app_shutdown(App *app);

// Returns the sg_pixel_format matching the active Vulkan swapchain image format.
// Inline so multiple TUs can call it without linker fuss. Used by both pipeline
// creation (lua_api.c) and the swapchain pass description (pass.c) so the two
// stay in lock-step — sokol validation rejects pipelines whose color_format
// doesn't match the active pass.
static inline sg_pixel_format app_swapchain_color_format(const App *app) {
    switch (app->vk_swapchain_format) {
        case VK_FORMAT_B8G8R8A8_UNORM: return SG_PIXELFORMAT_BGRA8;
        case VK_FORMAT_R8G8B8A8_UNORM: return SG_PIXELFORMAT_RGBA8;
        case VK_FORMAT_B8G8R8A8_SRGB:  return SG_PIXELFORMAT_BGRA8;  // close enough for PoC
        case VK_FORMAT_R8G8B8A8_SRGB:  return SG_PIXELFORMAT_RGBA8;
        default:                       return SG_PIXELFORMAT_BGRA8;
    }
}
