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
