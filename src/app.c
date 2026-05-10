#include "app.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_vulkan.h>
#include <vulkan/vulkan.h>
#include "sokol_gfx.h"
#include <stdlib.h>
#include <string.h>

static void sglua_sokol_logger(
    const char* tag, uint32_t level, uint32_t item_id,
    const char* msg, uint32_t line, const char* file, void* user)
{
    (void)tag; (void)item_id; (void)file; (void)user;
    const char *lvl = (level == 0) ? "PANIC" : (level == 1) ? "ERROR"
                    : (level == 2) ? "WARN"  : "INFO";
    SDL_Log("[sg %s:%u] %s", lvl, line, msg ? msg : "(no msg)");
}

static bool create_vk_instance(VkInstance *out_inst) {
    Uint32 sdl_ext_count = 0;
    const char * const *sdl_exts = SDL_Vulkan_GetInstanceExtensions(&sdl_ext_count);
    if (!sdl_exts) {
        SDL_Log("SDL_Vulkan_GetInstanceExtensions failed: %s", SDL_GetError());
        return false;
    }
    // Sokol's Vulkan backend (under SOKOL_DEBUG, i.e. non-NDEBUG builds) looks
    // up vkSetDebugUtilsObjectNameEXT at sg_setup time and panics if missing,
    // so request VK_EXT_debug_utils alongside SDL's required extensions.
    const uint32_t extra_count = 1;
    const char *extra_exts[] = { VK_EXT_DEBUG_UTILS_EXTENSION_NAME };
    uint32_t total = sdl_ext_count + extra_count;
    const char **all = (const char**)malloc(sizeof(const char*) * total);
    for (uint32_t i = 0; i < sdl_ext_count; ++i) all[i] = sdl_exts[i];
    for (uint32_t i = 0; i < extra_count; ++i) all[sdl_ext_count + i] = extra_exts[i];

    VkApplicationInfo ai = {
        .sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
        .pApplicationName = "sglua",
        .applicationVersion = VK_MAKE_VERSION(0, 1, 0),
        .pEngineName = "sglua",
        .engineVersion = VK_MAKE_VERSION(0, 1, 0),
        .apiVersion = VK_API_VERSION_1_2,
    };
    VkInstanceCreateInfo ci = {
        .sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
        .pApplicationInfo = &ai,
        .enabledExtensionCount = total,
        .ppEnabledExtensionNames = all,
    };
    VkResult res = vkCreateInstance(&ci, NULL, out_inst);
    free(all);
    if (res != VK_SUCCESS) {
        SDL_Log("vkCreateInstance failed (VkResult=%d)", res);
        return false;
    }
    return true;
}

static bool pick_physical_device(VkInstance inst, VkPhysicalDevice *out_phys, uint32_t *out_qf) {
    uint32_t n = 0;
    vkEnumeratePhysicalDevices(inst, &n, NULL);
    if (n == 0) { SDL_Log("no Vulkan physical device"); return false; }
    VkPhysicalDevice *phys = (VkPhysicalDevice*)malloc(sizeof(VkPhysicalDevice) * n);
    vkEnumeratePhysicalDevices(inst, &n, phys);

    // Pick first device that has a graphics queue family.
    for (uint32_t i = 0; i < n; ++i) {
        uint32_t qfn = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(phys[i], &qfn, NULL);
        VkQueueFamilyProperties *qfp = (VkQueueFamilyProperties*)malloc(sizeof(VkQueueFamilyProperties) * qfn);
        vkGetPhysicalDeviceQueueFamilyProperties(phys[i], &qfn, qfp);
        for (uint32_t q = 0; q < qfn; ++q) {
            if (qfp[q].queueFlags & VK_QUEUE_GRAPHICS_BIT) {
                *out_phys = phys[i];
                *out_qf = q;
                free(qfp); free(phys);
                return true;
            }
        }
        free(qfp);
    }
    free(phys);
    SDL_Log("no graphics queue family found");
    return false;
}

static bool create_vk_device(VkPhysicalDevice phys, uint32_t qf,
                             VkDevice *out_dev, VkQueue *out_q) {
    float prio = 1.0f;
    VkDeviceQueueCreateInfo qci = {
        .sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
        .queueFamilyIndex = qf,
        .queueCount = 1,
        .pQueuePriorities = &prio,
    };
    // Sokol's Vulkan backend uses VK_EXT_descriptor_buffer for resource binding.
    // The extension transitively requires VK_KHR_buffer_device_address (core in
    // Vulkan 1.2) and VK_EXT_descriptor_indexing (core in 1.2); features must
    // still be enabled explicitly via the features chain.
    const char *dev_exts[] = {
        VK_KHR_SWAPCHAIN_EXTENSION_NAME,
        VK_EXT_DESCRIPTOR_BUFFER_EXTENSION_NAME,
    };
    VkPhysicalDeviceDescriptorBufferFeaturesEXT desc_buf_feat = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_BUFFER_FEATURES_EXT,
        .descriptorBuffer = VK_TRUE,
    };
    VkPhysicalDeviceVulkan12Features vk12 = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES,
        .pNext = &desc_buf_feat,
        .bufferDeviceAddress = VK_TRUE,
        .descriptorIndexing  = VK_TRUE,
    };
    VkPhysicalDeviceFeatures2 feat2 = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2,
        .pNext = &vk12,
    };
    VkDeviceCreateInfo ci = {
        .sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
        .pNext = &feat2,
        .queueCreateInfoCount = 1,
        .pQueueCreateInfos = &qci,
        .enabledExtensionCount = (uint32_t)(sizeof(dev_exts) / sizeof(dev_exts[0])),
        .ppEnabledExtensionNames = dev_exts,
    };
    VkResult res = vkCreateDevice(phys, &ci, NULL, out_dev);
    if (res != VK_SUCCESS) {
        SDL_Log("vkCreateDevice failed (VkResult=%d)", res);
        return false;
    }
    vkGetDeviceQueue(*out_dev, qf, 0, out_q);
    return true;
}

bool app_init(App *app) {
    memset(app, 0, sizeof(*app));
    app->window = SDL_CreateWindow("sglua", 1280, 720,
        SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE);
    if (!app->window) {
        SDL_Log("SDL_CreateWindow failed: %s", SDL_GetError());
        return false;
    }

    if (!SDL_Vulkan_LoadLibrary(NULL)) {
        SDL_Log("SDL_Vulkan_LoadLibrary failed: %s", SDL_GetError());
        return false;
    }
    if (!create_vk_instance(&app->vk_instance)) return false;
    if (!pick_physical_device(app->vk_instance, &app->vk_phys, &app->vk_queue_family)) return false;
    if (!create_vk_device(app->vk_phys, app->vk_queue_family, &app->vk_device, &app->vk_queue)) return false;

    sg_setup(&(sg_desc){
        .environment = {
            .defaults = {
                .color_format = SG_PIXELFORMAT_BGRA8,    // Vulkan swapchain は BGRA がデフォルト
                .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
                .sample_count = 1,
            },
            .vulkan = {
                .instance        = (const void*)app->vk_instance,
                .physical_device = (const void*)app->vk_phys,
                .device          = (const void*)app->vk_device,
                .queue           = (const void*)app->vk_queue,
                .queue_family_index = app->vk_queue_family,
            },
        },
        .logger.func = sglua_sokol_logger,
    });

    pass_state_init(&app->pass);
    res_table_init(&app->res);
    pipeline_cache_init(&app->pip_cache);
    app->frame_index = 0;
    return true;
}

void app_frame_begin(App *app, int *out_w, int *out_h) {
    int w, h;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    if (out_w) *out_w = w;
    if (out_h) *out_h = h;
    pass_state_set_swapchain_size(&app->pass, w, h);
    // (Task 15: vkAcquireNextImage 等)
}

void app_frame_end(App *app) {
    sg_commit();
    // (Task 15: vkQueuePresentKHR 等)
    app->frame_index++;
}

void app_shutdown(App *app) {
    // Pipelines reference shaders, so destroy pipelines before resources.
    pipeline_cache_shutdown(&app->pip_cache);
    res_table_shutdown(&app->res);
    sg_shutdown();
    if (app->vk_device) {
        vkDeviceWaitIdle(app->vk_device);
        vkDestroyDevice(app->vk_device, NULL);
    }
    if (app->vk_instance) vkDestroyInstance(app->vk_instance, NULL);
    SDL_Vulkan_UnloadLibrary();
    if (app->window) SDL_DestroyWindow(app->window);
}
