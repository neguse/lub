// backend_sokol.c — RenderBackend impl using sokol_gfx.
//
// Two GPU API paths share most of this file:
//   - native: SOKOL_VULKAN, with direct Vulkan calls for instance / device /
//     swapchain ownership and the capture (vkCmdCopyImageToBuffer) path.
//   - wasm  : SOKOL_WGPU via emdawnwebgpu, with direct webgpu.h calls for
//     surface ownership / per-frame texture acquisition.
//
// Data-path callbacks (make/destroy/update/apply/draw/dispatch/end_pass and
// the offscreen branch of begin_pass) are pure sokol_gfx and therefore
// shared verbatim — they sit outside the platform guards. Only the bring-up,
// swapchain, capture, and "what's the swapchain color format" entry points
// differ per backend.
//
// All sg_*/vk*/wgpu* calls used by the runtime live in this file. Other
// source files (pass.c, pipeline.c, resources.c, capture.c, lua_api.c) talk
// to the GPU only through g_backend->xxx().
#include "backend.h"
#include "app.h"
#include "shader.h"

#include "sokol_gfx.h"

#include <SDL3/SDL.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifndef __EMSCRIPTEN__
#include <SDL3/SDL_vulkan.h>
#include <vulkan/vulkan.h>

#include "stb_image_write.h"
#else
#include <webgpu/webgpu.h>
#include <emscripten/emscripten.h>
#endif

// --- per-image / per-shader / per-pipeline backend objects ---------------
// Cross-platform: sokol-level handles only.

typedef struct SkImage {
    sg_image  img;
    sg_sampler smp;
    sg_view   view;         // texture-sample view
    sg_view   color_att;    // color-attachment view (valid when render_target)
    bool      render_target;
} SkImage;

typedef struct SkBuffer {
    sg_buffer buf;
    sg_view   storage_view; // valid only for storage buffers (id != 0)
    SglBufferType type;
} SkBuffer;

typedef struct SkShader {
    sg_shader sh;
    ShaderReflection refl;  // copy for binding resolution
} SkShader;

// --- sokol logger --------------------------------------------------------

static void sglua_sokol_logger(
    const char* tag, uint32_t level, uint32_t item_id,
    const char* msg, uint32_t line, const char* file, void* user)
{
    (void)tag; (void)item_id; (void)file; (void)user;
    const char *lvl = (level == 0) ? "PANIC" : (level == 1) ? "ERROR"
                    : (level == 2) ? "WARN"  : "INFO";
    SDL_Log("[sg %s:%u] %s", lvl, line, msg ? msg : "(no msg)");
}

#ifndef __EMSCRIPTEN__
// --- Vulkan boot (instance/device/swapchain) ----------------------------

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
        .apiVersion = VK_API_VERSION_1_3,
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

static bool create_surface(App *app) {
    if (!SDL_Vulkan_CreateSurface(app->window, app->vk_instance, NULL, &app->vk_surface)) {
        SDL_Log("SDL_Vulkan_CreateSurface failed: %s", SDL_GetError());
        return false;
    }
    return true;
}

// Destroys swapchain-derived resources (views, depth, semaphores, swapchain)
// but leaves surface / device / instance intact. Safe to call multiple times.
static void destroy_swapchain_resources(App *app) {
    if (!app->vk_device) return;
    if (app->vk_present_sems) {
        for (uint32_t i = 0; i < app->vk_swapchain_image_count; ++i) {
            if (app->vk_present_sems[i]) vkDestroySemaphore(app->vk_device, app->vk_present_sems[i], NULL);
        }
        free(app->vk_present_sems);
        app->vk_present_sems = NULL;
    }
    if (app->vk_acquire_sems) {
        for (uint32_t i = 0; i < app->vk_swapchain_image_count; ++i) {
            if (app->vk_acquire_sems[i]) vkDestroySemaphore(app->vk_device, app->vk_acquire_sems[i], NULL);
        }
        free(app->vk_acquire_sems);
        app->vk_acquire_sems = NULL;
    }
    if (app->vk_depth_view)  { vkDestroyImageView(app->vk_device, app->vk_depth_view, NULL);  app->vk_depth_view  = VK_NULL_HANDLE; }
    if (app->vk_depth_image) { vkDestroyImage(app->vk_device, app->vk_depth_image, NULL);     app->vk_depth_image = VK_NULL_HANDLE; }
    if (app->vk_depth_mem)   { vkFreeMemory(app->vk_device, app->vk_depth_mem, NULL);         app->vk_depth_mem   = VK_NULL_HANDLE; }
    if (app->vk_swapchain_views) {
        for (uint32_t i = 0; i < app->vk_swapchain_image_count; ++i) {
            if (app->vk_swapchain_views[i]) {
                vkDestroyImageView(app->vk_device, app->vk_swapchain_views[i], NULL);
            }
        }
        free(app->vk_swapchain_views);
        app->vk_swapchain_views = NULL;
    }
    free(app->vk_swapchain_images);
    app->vk_swapchain_images = NULL;
    if (app->vk_swapchain) { vkDestroySwapchainKHR(app->vk_device, app->vk_swapchain, NULL); app->vk_swapchain = VK_NULL_HANDLE; }
    app->vk_swapchain_image_count = 0;
}

static bool create_swapchain_resources(App *app) {
    uint32_t fmt_count = 0;
    vkGetPhysicalDeviceSurfaceFormatsKHR(app->vk_phys, app->vk_surface, &fmt_count, NULL);
    if (fmt_count == 0) { SDL_Log("no surface formats"); return false; }
    VkSurfaceFormatKHR *fmts = (VkSurfaceFormatKHR*)malloc(sizeof(VkSurfaceFormatKHR) * fmt_count);
    vkGetPhysicalDeviceSurfaceFormatsKHR(app->vk_phys, app->vk_surface, &fmt_count, fmts);
    VkSurfaceFormatKHR chosen = fmts[0];
    for (uint32_t i = 0; i < fmt_count; ++i) {
        if (fmts[i].format == VK_FORMAT_B8G8R8A8_UNORM) { chosen = fmts[i]; break; }
    }
    free(fmts);
    app->vk_swapchain_format = chosen.format;

    VkSurfaceCapabilitiesKHR caps;
    vkGetPhysicalDeviceSurfaceCapabilitiesKHR(app->vk_phys, app->vk_surface, &caps);

    int w, h;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    VkExtent2D extent = caps.currentExtent.width != 0xffffffff ? caps.currentExtent
                                                               : (VkExtent2D){ (uint32_t)w, (uint32_t)h };
    // Minimized window or surface lost: bail without leaving partial state.
    // Caller retries next frame; destroy_swapchain_resources has already zero'd
    // the relevant fields.
    if (extent.width == 0 || extent.height == 0) {
        return false;
    }

    uint32_t min_image_count = caps.minImageCount + 1;
    if (caps.maxImageCount > 0 && min_image_count > caps.maxImageCount) {
        min_image_count = caps.maxImageCount;
    }

    VkSwapchainCreateInfoKHR sci = {
        .sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR,
        .surface = app->vk_surface,
        .minImageCount = min_image_count,
        .imageFormat = chosen.format,
        .imageColorSpace = chosen.colorSpace,
        .imageExtent = extent,
        .imageArrayLayers = 1,
        .imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT,
        .imageSharingMode = VK_SHARING_MODE_EXCLUSIVE,
        .preTransform = caps.currentTransform,
        .compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
        .presentMode = VK_PRESENT_MODE_FIFO_KHR,
        .clipped = VK_TRUE,
    };
    if (vkCreateSwapchainKHR(app->vk_device, &sci, NULL, &app->vk_swapchain) != VK_SUCCESS) {
        SDL_Log("vkCreateSwapchainKHR failed");
        return false;
    }

    vkGetSwapchainImagesKHR(app->vk_device, app->vk_swapchain, &app->vk_swapchain_image_count, NULL);
    app->vk_swapchain_images = (VkImage*)malloc(sizeof(VkImage) * app->vk_swapchain_image_count);
    vkGetSwapchainImagesKHR(app->vk_device, app->vk_swapchain, &app->vk_swapchain_image_count, app->vk_swapchain_images);
    // calloc so a partial failure mid-loop leaves the rest as NULL and the
    // destroy path can skip them safely.
    app->vk_swapchain_views = (VkImageView*)calloc(app->vk_swapchain_image_count, sizeof(VkImageView));
    for (uint32_t i = 0; i < app->vk_swapchain_image_count; ++i) {
        VkImageViewCreateInfo ivci = {
            .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            .image = app->vk_swapchain_images[i],
            .viewType = VK_IMAGE_VIEW_TYPE_2D,
            .format = chosen.format,
            .subresourceRange = {
                .aspectMask = VK_IMAGE_ASPECT_COLOR_BIT,
                .levelCount = 1, .layerCount = 1,
            },
        };
        if (vkCreateImageView(app->vk_device, &ivci, NULL, &app->vk_swapchain_views[i]) != VK_SUCCESS) {
            SDL_Log("vkCreateImageView (swapchain) failed");
            return false;
        }
    }

    // Depth attachment
    // sokol_gfx の Vulkan backend が SG_PIXELFORMAT_DEPTH_STENCIL に対して
    // D32_SFLOAT_S8_UINT を採用するためそれに合わせる。
    VkFormat depth_fmt = VK_FORMAT_D32_SFLOAT_S8_UINT;
    VkFormatProperties fp;
    vkGetPhysicalDeviceFormatProperties(app->vk_phys, depth_fmt, &fp);
    if (!(fp.optimalTilingFeatures & VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT)) {
        depth_fmt = VK_FORMAT_D24_UNORM_S8_UINT;
    }

    VkImageCreateInfo dci = {
        .sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
        .imageType = VK_IMAGE_TYPE_2D,
        .format = depth_fmt,
        .extent = { extent.width, extent.height, 1 },
        .mipLevels = 1, .arrayLayers = 1,
        .samples = VK_SAMPLE_COUNT_1_BIT,
        .tiling = VK_IMAGE_TILING_OPTIMAL,
        .usage = VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT,
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
        .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
    };
    if (vkCreateImage(app->vk_device, &dci, NULL, &app->vk_depth_image) != VK_SUCCESS) {
        SDL_Log("vkCreateImage (depth) failed");
        return false;
    }

    VkMemoryRequirements mr;
    vkGetImageMemoryRequirements(app->vk_device, app->vk_depth_image, &mr);
    VkPhysicalDeviceMemoryProperties pmp;
    vkGetPhysicalDeviceMemoryProperties(app->vk_phys, &pmp);
    uint32_t mem_type = 0;
    for (uint32_t i = 0; i < pmp.memoryTypeCount; ++i) {
        if ((mr.memoryTypeBits & (1u << i)) &&
            (pmp.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)) {
            mem_type = i; break;
        }
    }
    VkMemoryAllocateInfo mai = {
        .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
        .allocationSize = mr.size,
        .memoryTypeIndex = mem_type,
    };
    if (vkAllocateMemory(app->vk_device, &mai, NULL, &app->vk_depth_mem) != VK_SUCCESS) {
        SDL_Log("vkAllocateMemory (depth) failed");
        return false;
    }
    vkBindImageMemory(app->vk_device, app->vk_depth_image, app->vk_depth_mem, 0);

    VkImageViewCreateInfo dvci = {
        .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
        .image = app->vk_depth_image,
        .viewType = VK_IMAGE_VIEW_TYPE_2D,
        .format = depth_fmt,
        .subresourceRange = {
            .aspectMask = VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT,
            .levelCount = 1, .layerCount = 1,
        },
    };
    if (vkCreateImageView(app->vk_device, &dvci, NULL, &app->vk_depth_view) != VK_SUCCESS) {
        SDL_Log("vkCreateImageView (depth) failed");
        return false;
    }

    VkSemaphoreCreateInfo sci2 = { .sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    app->vk_acquire_sems = (VkSemaphore*)calloc(app->vk_swapchain_image_count, sizeof(VkSemaphore));
    app->vk_present_sems = (VkSemaphore*)calloc(app->vk_swapchain_image_count, sizeof(VkSemaphore));
    for (uint32_t i = 0; i < app->vk_swapchain_image_count; ++i) {
        if (vkCreateSemaphore(app->vk_device, &sci2, NULL, &app->vk_acquire_sems[i]) != VK_SUCCESS) {
            SDL_Log("vkCreateSemaphore (acquire #%u) failed", i);
            return false;
        }
        if (vkCreateSemaphore(app->vk_device, &sci2, NULL, &app->vk_present_sems[i]) != VK_SUCCESS) {
            SDL_Log("vkCreateSemaphore (present #%u) failed", i);
            return false;
        }
    }

    return true;
}

// Wait for GPU idle, tear down the swapchain-derived resources, then build a
// fresh swapchain matching the current window size. Returns false if the
// window is currently 0x0 (minimized) or creation otherwise failed; in either
// case app->vk_swapchain stays VK_NULL_HANDLE so begin_frame can skip the
// frame and retry next iteration.
static bool recreate_swapchain_resources(App *app) {
    if (!app->vk_device) return false;
    vkDeviceWaitIdle(app->vk_device);
    destroy_swapchain_resources(app);
    if (!create_swapchain_resources(app)) {
        destroy_swapchain_resources(app);  // ensure clean slate after partial failure
        return false;
    }
    return true;
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
    const char *dev_exts[] = {
        VK_KHR_SWAPCHAIN_EXTENSION_NAME,
        VK_EXT_DESCRIPTOR_BUFFER_EXTENSION_NAME,
    };
    VkPhysicalDeviceDescriptorBufferFeaturesEXT desc_buf_feat = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_BUFFER_FEATURES_EXT,
        .descriptorBuffer = VK_TRUE,
    };
    VkPhysicalDeviceVulkan13Features vk13 = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES,
        .pNext = &desc_buf_feat,
        .synchronization2 = VK_TRUE,
        .dynamicRendering = VK_TRUE,
    };
    VkPhysicalDeviceVulkan12Features vk12 = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES,
        .pNext = &vk13,
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

static sg_pixel_format vk_to_sg_fmt(VkFormat f) {
    switch (f) {
        case VK_FORMAT_B8G8R8A8_UNORM:
        case VK_FORMAT_B8G8R8A8_SRGB:  return SG_PIXELFORMAT_BGRA8;
        case VK_FORMAT_R8G8B8A8_UNORM:
        case VK_FORMAT_R8G8B8A8_SRGB:  return SG_PIXELFORMAT_RGBA8;
        default:                        return SG_PIXELFORMAT_BGRA8;
    }
}

// --- RenderBackend implementation ----------------------------------------

static bool sk_init(App *app) {
    if (!SDL_Vulkan_LoadLibrary(NULL)) {
        SDL_Log("SDL_Vulkan_LoadLibrary failed: %s", SDL_GetError());
        return false;
    }
    if (!create_vk_instance(&app->vk_instance)) return false;
    if (!pick_physical_device(app->vk_instance, &app->vk_phys, &app->vk_queue_family)) return false;
    if (!create_vk_device(app->vk_phys, app->vk_queue_family, &app->vk_device, &app->vk_queue)) return false;
    if (!create_surface(app)) return false;
    if (!create_swapchain_resources(app)) return false;

    sg_pixel_format color_pf = vk_to_sg_fmt(app->vk_swapchain_format);

    sg_setup(&(sg_desc){
        .environment = {
            .defaults = {
                .color_format = color_pf,
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
    return true;
}

static void sk_shutdown(App *app) {
    if (app->vk_device) vkDeviceWaitIdle(app->vk_device);
    sg_shutdown();

    destroy_swapchain_resources(app);
    if (app->vk_surface)  vkDestroySurfaceKHR(app->vk_instance, app->vk_surface, NULL);
    if (app->vk_device)   vkDestroyDevice(app->vk_device, NULL);
    if (app->vk_instance) vkDestroyInstance(app->vk_instance, NULL);
    SDL_Vulkan_UnloadLibrary();
}

static void sk_begin_frame(App *app, int *out_w, int *out_h) {
    int w, h;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    if (out_w) *out_w = w;
    if (out_h) *out_h = h;

    // The SDL event path sets pending_resize before this fires; rebuild the
    // swapchain at frame start so the acquire below sees fresh extents.
    if (app->pending_resize) {
        app->pending_resize = false;
        recreate_swapchain_resources(app);
    }
    if (!app->vk_swapchain) return;  // minimize / failed recreate; main loop skips frame

    uint32_t slot = (uint32_t)(app->frame_index % app->vk_swapchain_image_count);
    VkResult r = vkAcquireNextImageKHR(app->vk_device, app->vk_swapchain, UINT64_MAX,
                                       app->vk_acquire_sems[slot], VK_NULL_HANDLE,
                                       &app->vk_current_image);
    if (r == VK_ERROR_OUT_OF_DATE_KHR) {
        // Driver noticed the resize before SDL did. Recreate now and retry
        // once with a fresh acquire semaphore from the rebuilt array.
        if (recreate_swapchain_resources(app)) {
            slot = (uint32_t)(app->frame_index % app->vk_swapchain_image_count);
            r = vkAcquireNextImageKHR(app->vk_device, app->vk_swapchain, UINT64_MAX,
                                      app->vk_acquire_sems[slot], VK_NULL_HANDLE,
                                      &app->vk_current_image);
        }
    }
    if (r != VK_SUCCESS && r != VK_SUBOPTIMAL_KHR) {
        // Couldn't acquire even after rebuild; defer to next frame.
        app->pending_resize = true;
        return;
    }
    app->vk_last_presented_image = app->vk_swapchain_images[app->vk_current_image];
}

static void sk_end_frame(App *app) {
    sg_commit();
    if (!app->vk_swapchain) return;
    uint32_t slot = (uint32_t)(app->frame_index % app->vk_swapchain_image_count);
    VkPresentInfoKHR pi = {
        .sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
        .waitSemaphoreCount = 1,
        .pWaitSemaphores = &app->vk_present_sems[slot],
        .swapchainCount = 1,
        .pSwapchains = &app->vk_swapchain,
        .pImageIndices = &app->vk_current_image,
    };
    VkResult r = vkQueuePresentKHR(app->vk_queue, &pi);
    if (r == VK_ERROR_OUT_OF_DATE_KHR || r == VK_SUBOPTIMAL_KHR) {
        // Defer to next begin_frame so we don't recreate while the present is
        // still being consumed by the compositor.
        app->pending_resize = true;
    }
}

#endif // !__EMSCRIPTEN__

// --- shared data path -----------------------------------------------------
// Pure sokol_gfx — no API-direct calls. Used by both native and wasm.

static BackendBuffer sk_make_buffer(SglBufferType type, const float *data, size_t bytes) {
    SkBuffer *sb = (SkBuffer*)calloc(1, sizeof(SkBuffer));
    if (!sb) return 0;
    sb->type = type;
    if (type == SGL_BUFFER_STORAGE) {
        // Storage buffer: also marked vertex_buffer so the same backing buffer
        // can be re-bound as a VBO after compute writes. .immutable + size=0
        // is sokol's "let compute populate it" mode. If initial data is given
        // we use dynamic_update instead so sg_update_buffer can seed it.
        if (data && bytes > 0) {
            sb->buf = sg_make_buffer(&(sg_buffer_desc){
                .size = bytes,
                .usage = {
                    .vertex_buffer  = true,
                    .storage_buffer = true,
                    .dynamic_update = true,
                },
            });
            if (sb->buf.id == SG_INVALID_ID) { free(sb); return 0; }
            sg_update_buffer(sb->buf, &(sg_range){ .ptr = data, .size = bytes });
        } else {
            sb->buf = sg_make_buffer(&(sg_buffer_desc){
                .size = bytes,
                .usage = {
                    .vertex_buffer  = true,
                    .storage_buffer = true,
                },
            });
            if (sb->buf.id == SG_INVALID_ID) { free(sb); return 0; }
        }
        sb->storage_view = sg_make_view(&(sg_view_desc){
            .storage_buffer = { .buffer = sb->buf },
        });
        if (sb->storage_view.id == SG_INVALID_ID) {
            sg_destroy_buffer(sb->buf);
            free(sb);
            return 0;
        }
        return (uintptr_t)sb;
    }

    sb->buf = sg_make_buffer(&(sg_buffer_desc){
        .size = bytes,
        .usage = {
            .vertex_buffer  = (type == SGL_BUFFER_VERTEX),
            .index_buffer   = (type == SGL_BUFFER_INDEX),
            .dynamic_update = true,
        },
        // dynamic_update: initial data はここで渡せないので make 後に update
    });
    if (sb->buf.id == SG_INVALID_ID) { free(sb); return 0; }
    if (data && bytes > 0) {
        sg_update_buffer(sb->buf, &(sg_range){ .ptr = data, .size = bytes });
    }
    return (uintptr_t)sb;
}

static void sk_destroy_buffer(BackendBuffer h) {
    SkBuffer *sb = (SkBuffer*)h;
    if (!sb) return;
    if (sb->storage_view.id) sg_destroy_view(sb->storage_view);
    if (sb->buf.id) sg_destroy_buffer(sb->buf);
    free(sb);
}

static BackendImage sk_make_image(const ImageDesc *d) {
    SkImage *si = (SkImage*)calloc(1, sizeof(SkImage));
    if (!si) return 0;
    si->render_target = d->render_target;
    sg_pixel_format pf = (d->fmt == SGL_PF_R8) ? SG_PIXELFORMAT_R8 : SG_PIXELFORMAT_RGBA8;
    sg_image_desc img_desc = {
        .width = d->w,
        .height = d->h,
        .pixel_format = pf,
    };
    if (d->render_target) {
        img_desc.usage.color_attachment = true;
    } else {
        img_desc.usage.dynamic_update = true;
    }
    si->img = sg_make_image(&img_desc);
    if (si->img.id == SG_INVALID_ID) { free(si); return 0; }
    if (!d->render_target && d->data && d->data_bytes > 0) {
        sg_update_image(si->img, &(sg_image_data){
            .mip_levels[0] = { .ptr = d->data, .size = d->data_bytes },
        });
    }
    sg_filter sf = (d->filter == SGL_FILTER_NEAREST) ? SG_FILTER_NEAREST : SG_FILTER_LINEAR;
    sg_wrap   sw = (d->wrap   == SGL_WRAP_CLAMP)     ? SG_WRAP_CLAMP_TO_EDGE : SG_WRAP_REPEAT;
    si->smp = sg_make_sampler(&(sg_sampler_desc){
        .min_filter = sf,
        .mag_filter = sf,
        .wrap_u = sw,
        .wrap_v = sw,
    });
    si->view = sg_make_view(&(sg_view_desc){
        .texture = { .image = si->img },
    });
    if (d->render_target) {
        si->color_att = sg_make_view(&(sg_view_desc){
            .color_attachment = { .image = si->img },
        });
    }
    return (uintptr_t)si;
}

static void sk_destroy_image(BackendImage h) {
    SkImage *si = (SkImage*)h;
    if (!si) return;
    if (si->color_att.id) sg_destroy_view(si->color_att);
    if (si->view.id) sg_destroy_view(si->view);
    if (si->img.id)  sg_destroy_image(si->img);
    if (si->smp.id)  sg_destroy_sampler(si->smp);
    free(si);
}

static BackendShader sk_make_shader(const ShaderDesc *d) {
    SkShader *ss = (SkShader*)calloc(1, sizeof(SkShader));
    if (!ss) return 0;
    if (d->refl) ss->refl = *d->refl;

    sg_shader_desc desc = {0};
    bool is_compute = (d->cs_spirv != NULL);

    // Backend dispatch:
    //   * Native Vulkan: Slang emits SPIR-V binaries; sokol-gfx reads
    //     desc.*.bytecode and the spirv_set*_binding_n fields point at the
    //     descriptor-set/binding pair patch_spirv_descriptor_sets() wrote.
    //   * WASM / WebGPU: slang-bridge.ts emits WGSL source text into the
    //     same ShaderBlob.spirv buffer (null-terminated). sokol-gfx's WGPU
    //     backend reads desc.*.source and the wgsl_groupN_binding_n
    //     fields. Slang puts everything in @group(0); slang-bridge.ts's
    //     WGSL post-processor remaps non-UB resources to @group(1) so the
    //     sokol layout matches.
    //
    // Entry name: Slang's SPIR-V emitter renames the entry point to "main",
    // but its WGSL emitter preserves the user-supplied name (e.g. "vs_main",
    // "fs_main", "cs_main"). We pull that from the reflection rather than
    // hardcoding.
#ifdef __EMSCRIPTEN__
    const char *vs_entry = "vs_main";
    const char *fs_entry = "fs_main";
    const char *cs_entry = "cs_main";
#else
    const char *vs_entry = "main";
    const char *fs_entry = "main";
    const char *cs_entry = "main";
#endif

    if (is_compute) {
#ifdef __EMSCRIPTEN__
        desc.compute_func.entry  = cs_entry;
        desc.compute_func.source = (const char*)d->cs_spirv;  // WGSL string
#else
        desc.compute_func.entry         = cs_entry;
        desc.compute_func.bytecode.ptr  = d->cs_spirv;
        desc.compute_func.bytecode.size = d->cs_bytes;
#endif
        // Storage buffers: PoC restriction — only RW storage at set=1, binding=slot.
        for (int i = 0; i < ss->refl.storage_buf_count && i < SG_MAX_VIEW_BINDSLOTS; ++i) {
            ShaderStorageBuf *sbuf = &ss->refl.storage_bufs[i];
            int slot = sbuf->slot;
            if (slot < 0 || slot >= SG_MAX_VIEW_BINDSLOTS) continue;
            sg_shader_view *view = &desc.views[slot];
            view->storage_buffer.stage = SG_SHADERSTAGE_COMPUTE;
            view->storage_buffer.readonly = sbuf->readonly;
#ifdef __EMSCRIPTEN__
            view->storage_buffer.wgsl_group1_binding_n = (uint8_t)slot;
#else
            view->storage_buffer.spirv_set1_binding_n = (uint8_t)slot;
            view->storage_buffer.glsl_binding_n = (uint8_t)slot;
#endif
        }
        for (int b = 0; b < ss->refl.ub_count && b < SGL_MAX_UNIFORM_BLOCKS; ++b) {
            ShaderUniformBlock *u = &ss->refl.ubs[b];
            int slot = u->slot;
            if (slot < 0 || slot >= SG_MAX_UNIFORMBLOCK_BINDSLOTS) continue;
            sg_shader_uniform_block *dst = &desc.uniform_blocks[slot];
            dst->stage = SG_SHADERSTAGE_COMPUTE;
            dst->size = (uint32_t)(u->size_floats * 4);
            dst->layout = SG_UNIFORMLAYOUT_STD140;
#ifdef __EMSCRIPTEN__
            dst->wgsl_group0_binding_n = (uint8_t)slot;
#else
            dst->spirv_set0_binding_n = (uint8_t)slot;
#endif
        }
        ss->sh = sg_make_shader(&desc);
        if (ss->sh.id == 0) { free(ss); return 0; }
        return (uintptr_t)ss;
    }

#ifdef __EMSCRIPTEN__
    desc.vertex_func.entry            = vs_entry;
    desc.vertex_func.source           = (const char*)d->vs_spirv;  // WGSL string
    desc.fragment_func.entry          = fs_entry;
    desc.fragment_func.source         = (const char*)d->fs_spirv;  // WGSL string
#else
    desc.vertex_func.entry            = vs_entry;
    desc.vertex_func.bytecode.ptr     = d->vs_spirv;
    desc.vertex_func.bytecode.size    = d->vs_bytes;
    desc.fragment_func.entry          = fs_entry;
    desc.fragment_func.bytecode.ptr   = d->fs_spirv;
    desc.fragment_func.bytecode.size  = d->fs_bytes;
#endif

    // Vertex attributes — SPIR-V identifies inputs by location number, which
    // sokol's Vulkan backend reads from the SPIR-V module directly; the desc
    // only needs base_type set for validation. WGPU's WGSL backend reads the
    // module similarly.
    for (int i = 0; i < ss->refl.attr_count && i < SG_MAX_VERTEX_ATTRIBUTES; ++i) {
        desc.attrs[i].base_type = SG_SHADERATTRBASETYPE_FLOAT;
    }

    // Uniform blocks: map sokol bind slot to (set=0, binding=N) for Vulkan
    // or (group=0, binding=N) for WGPU. Same numerical slot — the bridge
    // ensures Slang's @binding number equals the reflection slot index.
    for (int b = 0; b < ss->refl.ub_count && b < SGL_MAX_UNIFORM_BLOCKS; ++b) {
        ShaderUniformBlock *u = &ss->refl.ubs[b];
        int slot = u->slot;
        if (slot < 0 || slot >= SG_MAX_UNIFORMBLOCK_BINDSLOTS) continue;
        sg_shader_uniform_block *dst = &desc.uniform_blocks[slot];
        // PoC assumption: vertex shader is the only stage that uses uniform blocks.
        dst->stage = SG_SHADERSTAGE_VERTEX;
        dst->size = (uint32_t)(u->size_floats * 4);
        dst->layout = SG_UNIFORMLAYOUT_STD140;
#ifdef __EMSCRIPTEN__
        dst->wgsl_group0_binding_n = (uint8_t)slot;
#else
        dst->spirv_set0_binding_n = (uint8_t)slot;
#endif
    }

    // Textures + samplers + texture-sampler pairs (PoC: stage=FRAGMENT).
    for (int i = 0; i < ss->refl.tex_count && i < SGL_MAX_TEXTURES; ++i) {
        ShaderTexture *tx = &ss->refl.texs[i];
        int img_slot = tx->img_slot;
        int smp_slot = tx->smp_slot;
        if (img_slot < 0 || img_slot >= SG_MAX_VIEW_BINDSLOTS) continue;

        sg_shader_view *view = &desc.views[img_slot];
        view->texture.stage = SG_SHADERSTAGE_FRAGMENT;
        view->texture.image_type = SG_IMAGETYPE_2D;
        view->texture.sample_type = SG_IMAGESAMPLETYPE_FLOAT;
#ifdef __EMSCRIPTEN__
        view->texture.wgsl_group1_binding_n = (uint8_t)img_slot;
#else
        view->texture.spirv_set1_binding_n = (uint8_t)img_slot;
#endif

        if (smp_slot >= 0 && smp_slot < SG_MAX_SAMPLER_BINDSLOTS) {
            sg_shader_sampler *smp = &desc.samplers[smp_slot];
            smp->stage = SG_SHADERSTAGE_FRAGMENT;
            smp->sampler_type = SG_SAMPLERTYPE_FILTERING;
#ifdef __EMSCRIPTEN__
            smp->wgsl_group1_binding_n = (uint8_t)smp_slot;
#else
            smp->spirv_set1_binding_n = (uint8_t)smp_slot;
#endif
        }
        if (i < SG_MAX_TEXTURE_SAMPLER_PAIRS) {
            sg_shader_texture_sampler_pair *pair = &desc.texture_sampler_pairs[i];
            pair->stage = SG_SHADERSTAGE_FRAGMENT;
            pair->view_slot = (uint8_t)img_slot;
            pair->sampler_slot = (uint8_t)(smp_slot >= 0 ? smp_slot : 0);
        }
    }

    ss->sh = sg_make_shader(&desc);
    if (ss->sh.id == 0) {
        free(ss);
        return 0;
    }
    return (uintptr_t)ss;
}

static void sk_destroy_shader(BackendShader h) {
    SkShader *ss = (SkShader*)h;
    if (!ss) return;
    if (ss->sh.id) sg_destroy_shader(ss->sh);
    free(ss);
}

static sg_blend_state to_sokol_blend(SglBlend b) {
    sg_blend_state bs = {0};
    switch (b) {
        case SGL_BLEND_ALPHA:
            bs.enabled = true;
            bs.src_factor_rgb = SG_BLENDFACTOR_SRC_ALPHA;
            bs.dst_factor_rgb = SG_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
            bs.src_factor_alpha = SG_BLENDFACTOR_ONE;
            bs.dst_factor_alpha = SG_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
            break;
        case SGL_BLEND_ADDITIVE:
            bs.enabled = true;
            bs.src_factor_rgb = SG_BLENDFACTOR_ONE;
            bs.dst_factor_rgb = SG_BLENDFACTOR_ONE;
            break;
        case SGL_BLEND_MULTIPLY:
            bs.enabled = true;
            bs.src_factor_rgb = SG_BLENDFACTOR_DST_COLOR;
            bs.dst_factor_rgb = SG_BLENDFACTOR_ZERO;
            break;
        default: break;  // SGL_BLEND_NONE → disabled
    }
    return bs;
}

static sg_pixel_format sgl_to_sg_fmt(SglPixelFormat fmt) {
    switch (fmt) {
        case SGL_PF_RGBA8: return SG_PIXELFORMAT_RGBA8;
        case SGL_PF_R8:    return SG_PIXELFORMAT_R8;
        case SGL_PF_BGRA8: return SG_PIXELFORMAT_BGRA8;
        default:           return SG_PIXELFORMAT_RGBA8;
    }
}

static BackendPipeline sk_make_pipeline(const PipelineDesc *d) {
    SkShader *ss = (SkShader*)d->shader;
    if (!ss) return 0;

    if (d->is_compute) {
        sg_pipeline pip = sg_make_pipeline(&(sg_pipeline_desc){
            .compute = true,
            .shader = ss->sh,
        });
        return (uintptr_t)pip.id;
    }

    sg_pipeline_desc desc = {0};
    desc.shader = ss->sh;
    int nct = d->n_color_targets > 0 ? d->n_color_targets : 1;
    if (nct > SGL_MAX_COLOR_TARGETS) nct = SGL_MAX_COLOR_TARGETS;
    desc.color_count = nct;
    sg_blend_state bs = to_sokol_blend(d->blend);
    for (int i = 0; i < nct; ++i) {
        desc.colors[i].pixel_format = sgl_to_sg_fmt(d->color_fmts[i]);
        // PoC: replicate blend state across all color attachments.
        desc.colors[i].blend = bs;
    }
    if (d->has_depth) {
        desc.depth.pixel_format = SG_PIXELFORMAT_DEPTH_STENCIL;
        desc.depth.compare = d->depth_test ? SG_COMPAREFUNC_LESS_EQUAL : SG_COMPAREFUNC_ALWAYS;
        desc.depth.write_enabled = d->depth_write;
    } else {
        // Offscreen color-only pass: no depth attachment, so the pipeline must
        // match (SG_PIXELFORMAT_NONE) and depth state stays off regardless of
        // what the caller asked for.
        desc.depth.pixel_format = SG_PIXELFORMAT_NONE;
        desc.depth.compare = SG_COMPAREFUNC_ALWAYS;
        desc.depth.write_enabled = false;
    }
    desc.cull_mode =
        (d->cull == SGL_CULL_BACK)  ? SG_CULLMODE_BACK :
        (d->cull == SGL_CULL_FRONT) ? SG_CULLMODE_FRONT :
                                       SG_CULLMODE_NONE;
    desc.primitive_type =
        (d->primitive == SGL_PRIM_LINES)          ? SG_PRIMITIVETYPE_LINES :
        (d->primitive == SGL_PRIM_LINE_STRIP)     ? SG_PRIMITIVETYPE_LINE_STRIP :
        (d->primitive == SGL_PRIM_POINTS)         ? SG_PRIMITIVETYPE_POINTS :
        (d->primitive == SGL_PRIM_TRIANGLE_STRIP) ? SG_PRIMITIVETYPE_TRIANGLE_STRIP :
                                                    SG_PRIMITIVETYPE_TRIANGLES;

    if (d->refl) {
        for (int i = 0; i < d->refl->attr_count; ++i) {
            sg_vertex_format fmt;
            switch (d->refl->attrs[i].comp_count) {
                case 1: fmt = SG_VERTEXFORMAT_FLOAT;  break;
                case 2: fmt = SG_VERTEXFORMAT_FLOAT2; break;
                case 3: fmt = SG_VERTEXFORMAT_FLOAT3; break;
                case 4: fmt = SG_VERTEXFORMAT_FLOAT4; break;
                default: fmt = SG_VERTEXFORMAT_FLOAT3;
            }
            desc.layout.attrs[d->refl->attrs[i].slot] = (sg_vertex_attr_state){
                .buffer_index = 0,
                .offset = d->refl->attrs[i].offset_floats * (int)sizeof(float),
                .format = fmt,
            };
        }
        desc.layout.buffers[0].stride = d->refl->vertex_stride_floats * (int)sizeof(float);
    }

    sg_pipeline pip = sg_make_pipeline(&desc);
    return (uintptr_t)pip.id;
}

static void sk_destroy_pipeline(BackendPipeline h) {
    if (!h) return;
    sg_destroy_pipeline((sg_pipeline){ .id = (uint32_t)h });
}

static void sk_update_buffer(BackendBuffer h, const void *data, size_t bytes) {
    SkBuffer *sb = (SkBuffer*)h;
    if (!sb || !data || bytes == 0) return;
    sg_update_buffer(sb->buf, &(sg_range){ .ptr = data, .size = bytes });
}

static void sk_update_image(BackendImage h, const void *data, size_t bytes) {
    if (!h || !data || bytes == 0) return;
    SkImage *si = (SkImage*)h;
    sg_update_image(si->img, &(sg_image_data){
        .mip_levels[0] = { .ptr = data, .size = bytes },
    });
}

// Shared offscreen-MRT path; the swapchain branch is platform-specific and
// lives in sk_begin_pass below. Returns true if the desc described an
// offscreen pass and the pass was started here.
static bool sk_try_begin_offscreen_pass(const PassBeginDesc *d) {
    if (!d->targets[0]) return false;
    int nct = d->n_color_targets > 0 ? d->n_color_targets : 1;
    if (nct > SGL_MAX_COLOR_TARGETS) nct = SGL_MAX_COLOR_TARGETS;
    sg_pass pass = {0};
    for (int i = 0; i < nct; ++i) {
        SkImage *si = (SkImage*)d->targets[i];
        pass.action.colors[i].load_action = SG_LOADACTION_CLEAR;
        pass.action.colors[i].clear_value.r = d->clear[i][0];
        pass.action.colors[i].clear_value.g = d->clear[i][1];
        pass.action.colors[i].clear_value.b = d->clear[i][2];
        pass.action.colors[i].clear_value.a = d->clear[i][3];
        pass.attachments.colors[i] = si->color_att;
    }
    sg_begin_pass(&pass);
    return true;
}

#ifndef __EMSCRIPTEN__
static void sk_begin_pass(App *app, const PassBeginDesc *d) {
    if (sk_try_begin_offscreen_pass(d)) return;
    uint32_t slot = (uint32_t)(app->frame_index % app->vk_swapchain_image_count);
    sg_pass pass = {
        .action.colors[0] = {
            .load_action = SG_LOADACTION_CLEAR,
            .clear_value = { d->clear[0][0], d->clear[0][1], d->clear[0][2], d->clear[0][3] },
        },
        .swapchain = {
            .width = app->last_w,
            .height = app->last_h,
            .color_format = vk_to_sg_fmt(app->vk_swapchain_format),
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .sample_count = 1,
            .vulkan = {
                .render_image = (const void*)app->vk_swapchain_images[app->vk_current_image],
                .render_view  = (const void*)app->vk_swapchain_views[app->vk_current_image],
                .depth_stencil_image = (const void*)app->vk_depth_image,
                .depth_stencil_view  = (const void*)app->vk_depth_view,
                .render_finished_semaphore = (const void*)app->vk_present_sems[slot],
                .present_complete_semaphore = (const void*)app->vk_acquire_sems[slot],
            },
        },
    };
    sg_begin_pass(&pass);
}
#endif // !__EMSCRIPTEN__

static void sk_end_pass(App *app) {
    (void)app;
    sg_end_pass();
}

static void sk_apply_pipeline(BackendPipeline p) {
    sg_apply_pipeline((sg_pipeline){ .id = (uint32_t)p });
}

static void sk_apply_bindings(const BindingsDesc *b) {
    sg_bindings sb = {0};
    if (b->vbuf) {
        SkBuffer *vb = (SkBuffer*)b->vbuf;
        sb.vertex_buffers[0] = vb->buf;
    }

    // Resolve textures via reflection name → slot.
    if (b->refl) {
        for (int i = 0; i < b->texture_count; ++i) {
            const char *name = b->textures[i].name;
            SkImage *si = (SkImage*)b->textures[i].image;
            if (!name || !si) continue;
            for (int k = 0; k < b->refl->tex_count; ++k) {
                if (strcmp(b->refl->texs[k].name, name) == 0) {
                    int img_slot = b->refl->texs[k].img_slot;
                    int smp_slot = b->refl->texs[k].smp_slot;
                    if (img_slot >= 0 && img_slot < SG_MAX_VIEW_BINDSLOTS)
                        sb.views[img_slot] = si->view;
                    if (smp_slot >= 0 && smp_slot < SG_MAX_SAMPLER_BINDSLOTS)
                        sb.samplers[smp_slot] = si->smp;
                    break;
                }
            }
        }
    }
    sg_apply_bindings(&sb);
}

static void sk_apply_uniforms(int ub_slot, const void *data, size_t bytes) {
    sg_apply_uniforms(ub_slot, &(sg_range){ .ptr = data, .size = bytes });
}

static void sk_draw(int base, int count) {
    sg_draw(base, count, 1);
}

static void sk_dispatch(App *app, const ComputeDispatchDesc *d) {
    (void)app;
    if (!d || !d->pipeline || !d->refl) return;
    sg_begin_pass(&(sg_pass){ .compute = true });
    sg_apply_pipeline((sg_pipeline){ .id = (uint32_t)d->pipeline });
    sg_bindings sb = {0};
    for (int i = 0; i < d->n_storage_bufs; ++i) {
        SkBuffer *buf = (SkBuffer*)d->storage_bufs[i].buf;
        if (!buf || !buf->storage_view.id || !d->storage_bufs[i].name) continue;
        // Resolve name -> slot via reflection.
        for (int k = 0; k < d->refl->storage_buf_count; ++k) {
            if (strcmp(d->refl->storage_bufs[k].name, d->storage_bufs[i].name) == 0) {
                int slot = d->refl->storage_bufs[k].slot;
                if (slot >= 0 && slot < SG_MAX_VIEW_BINDSLOTS) {
                    sb.views[slot] = buf->storage_view;
                }
                break;
            }
        }
    }
    sg_apply_bindings(&sb);
    if (d->uniform_slot >= 0 && d->uniform_data && d->uniform_bytes > 0) {
        sg_apply_uniforms(d->uniform_slot,
                          &(sg_range){ .ptr = d->uniform_data, .size = d->uniform_bytes });
    }
    sg_dispatch(d->groups_x, d->groups_y, d->groups_z);
    sg_end_pass();
}

#ifndef __EMSCRIPTEN__
// --- capture: vkCmdCopyImageToBuffer + stb_image_write -------------------

static uint32_t pick_host_visible_memory_type(
    VkPhysicalDevice phys, uint32_t type_bits, bool *out_coherent)
{
    VkPhysicalDeviceMemoryProperties pmp;
    vkGetPhysicalDeviceMemoryProperties(phys, &pmp);
    for (uint32_t i = 0; i < pmp.memoryTypeCount; ++i) {
        if ((type_bits & (1u << i)) == 0) continue;
        VkMemoryPropertyFlags flags = pmp.memoryTypes[i].propertyFlags;
        if ((flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) &&
            (flags & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) {
            if (out_coherent) *out_coherent = true;
            return i;
        }
    }
    for (uint32_t i = 0; i < pmp.memoryTypeCount; ++i) {
        if ((type_bits & (1u << i)) == 0) continue;
        VkMemoryPropertyFlags flags = pmp.memoryTypes[i].propertyFlags;
        if (flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) {
            if (out_coherent) *out_coherent = false;
            return i;
        }
    }
    return UINT32_MAX;
}

static bool format_is_bgra8(VkFormat f) {
    return f == VK_FORMAT_B8G8R8A8_UNORM || f == VK_FORMAT_B8G8R8A8_SRGB;
}
static bool format_is_rgba8(VkFormat f) {
    return f == VK_FORMAT_R8G8B8A8_UNORM || f == VK_FORMAT_R8G8B8A8_SRGB;
}

static bool sk_capture(App *app, const char *path) {
    if (!app || !path) return false;
    VkDevice         dev   = app->vk_device;
    VkPhysicalDevice phys  = app->vk_phys;
    VkQueue          queue = app->vk_queue;
    uint32_t         queue_family = app->vk_queue_family;
    VkImage          swapchain_image = app->vk_last_presented_image;
    uint32_t         width  = (uint32_t)app->last_w;
    uint32_t         height = (uint32_t)app->last_h;
    VkFormat         format = app->vk_swapchain_format;

    if (width == 0 || height == 0) {
        SDL_Log("capture error: zero extent");
        return false;
    }
    if (!format_is_bgra8(format) && !format_is_rgba8(format)) {
        SDL_Log("capture error: unsupported swapchain format (need BGRA8/RGBA8)");
        return false;
    }

    const VkDeviceSize buf_size = (VkDeviceSize)width * (VkDeviceSize)height * 4;

    VkBuffer buf = VK_NULL_HANDLE;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    VkCommandPool pool = VK_NULL_HANDLE;
    VkCommandBuffer cmd = VK_NULL_HANDLE;
    VkFence fence = VK_NULL_HANDLE;
    void *mapped = NULL;
    uint8_t *rgba = NULL;
    bool coherent = false;
    bool ok = false;
    const char *err = NULL;

    VkBufferCreateInfo bci = {
        .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
        .size = buf_size,
        .usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT,
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
    };
    if (vkCreateBuffer(dev, &bci, NULL, &buf) != VK_SUCCESS) {
        err = "vkCreateBuffer failed"; goto done;
    }

    VkMemoryRequirements mr;
    vkGetBufferMemoryRequirements(dev, buf, &mr);
    uint32_t mt = pick_host_visible_memory_type(phys, mr.memoryTypeBits, &coherent);
    if (mt == UINT32_MAX) { err = "no host-visible memory type"; goto done; }
    VkMemoryAllocateInfo mai = {
        .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
        .allocationSize = mr.size,
        .memoryTypeIndex = mt,
    };
    if (vkAllocateMemory(dev, &mai, NULL, &mem) != VK_SUCCESS) {
        err = "vkAllocateMemory failed"; goto done;
    }
    if (vkBindBufferMemory(dev, buf, mem, 0) != VK_SUCCESS) {
        err = "vkBindBufferMemory failed"; goto done;
    }

    VkCommandPoolCreateInfo cpci = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
        .flags = VK_COMMAND_POOL_CREATE_TRANSIENT_BIT,
        .queueFamilyIndex = queue_family,
    };
    if (vkCreateCommandPool(dev, &cpci, NULL, &pool) != VK_SUCCESS) {
        err = "vkCreateCommandPool failed"; goto done;
    }
    VkCommandBufferAllocateInfo cbai = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
        .commandPool = pool,
        .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
        .commandBufferCount = 1,
    };
    if (vkAllocateCommandBuffers(dev, &cbai, &cmd) != VK_SUCCESS) {
        err = "vkAllocateCommandBuffers failed"; goto done;
    }

    VkCommandBufferBeginInfo cbbi = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
        .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
    };
    if (vkBeginCommandBuffer(cmd, &cbbi) != VK_SUCCESS) {
        err = "vkBeginCommandBuffer failed"; goto done;
    }

    VkImageMemoryBarrier b1 = {
        .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
        .srcAccessMask = VK_ACCESS_MEMORY_READ_BIT,
        .dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT,
        .oldLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
        .newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .image = swapchain_image,
        .subresourceRange = {
            .aspectMask = VK_IMAGE_ASPECT_COLOR_BIT,
            .levelCount = 1, .layerCount = 1,
        },
    };
    vkCmdPipelineBarrier(cmd,
        VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
        VK_PIPELINE_STAGE_TRANSFER_BIT,
        0, 0, NULL, 0, NULL, 1, &b1);

    VkBufferImageCopy region = {
        .bufferOffset = 0,
        .bufferRowLength = 0,
        .bufferImageHeight = 0,
        .imageSubresource = {
            .aspectMask = VK_IMAGE_ASPECT_COLOR_BIT,
            .layerCount = 1,
        },
        .imageOffset = {0, 0, 0},
        .imageExtent = { width, height, 1 },
    };
    vkCmdCopyImageToBuffer(cmd, swapchain_image,
        VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, buf, 1, &region);

    VkImageMemoryBarrier b2 = {
        .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
        .srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT,
        .dstAccessMask = VK_ACCESS_MEMORY_READ_BIT,
        .oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        .newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR,
        .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .image = swapchain_image,
        .subresourceRange = {
            .aspectMask = VK_IMAGE_ASPECT_COLOR_BIT,
            .levelCount = 1, .layerCount = 1,
        },
    };
    vkCmdPipelineBarrier(cmd,
        VK_PIPELINE_STAGE_TRANSFER_BIT,
        VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
        0, 0, NULL, 0, NULL, 1, &b2);

    if (vkEndCommandBuffer(cmd) != VK_SUCCESS) {
        err = "vkEndCommandBuffer failed"; goto done;
    }

    VkFenceCreateInfo fci = { .sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
    if (vkCreateFence(dev, &fci, NULL, &fence) != VK_SUCCESS) {
        err = "vkCreateFence failed"; goto done;
    }
    VkSubmitInfo si = {
        .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO,
        .commandBufferCount = 1,
        .pCommandBuffers = &cmd,
    };
    if (vkQueueSubmit(queue, 1, &si, fence) != VK_SUCCESS) {
        err = "vkQueueSubmit failed"; goto done;
    }
    if (vkWaitForFences(dev, 1, &fence, VK_TRUE, UINT64_MAX) != VK_SUCCESS) {
        err = "vkWaitForFences failed"; goto done;
    }

    if (vkMapMemory(dev, mem, 0, buf_size, 0, &mapped) != VK_SUCCESS) {
        err = "vkMapMemory failed"; goto done;
    }
    if (!coherent) {
        VkMappedMemoryRange range = {
            .sType = VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE,
            .memory = mem,
            .offset = 0,
            .size = VK_WHOLE_SIZE,
        };
        vkInvalidateMappedMemoryRanges(dev, 1, &range);
    }

    rgba = (uint8_t*)malloc((size_t)buf_size);
    if (!rgba) { err = "out of memory (rgba buffer)"; goto done; }
    const uint8_t *src = (const uint8_t*)mapped;
    if (format_is_bgra8(format)) {
        const size_t pixels = (size_t)width * (size_t)height;
        for (size_t i = 0; i < pixels; ++i) {
            uint8_t bv = src[i*4 + 0];
            uint8_t gv = src[i*4 + 1];
            uint8_t rv = src[i*4 + 2];
            uint8_t av = src[i*4 + 3];
            rgba[i*4 + 0] = rv;
            rgba[i*4 + 1] = gv;
            rgba[i*4 + 2] = bv;
            rgba[i*4 + 3] = av;
        }
    } else {
        memcpy(rgba, src, (size_t)buf_size);
    }

    if (!stbi_write_png(path, (int)width, (int)height, 4, rgba, (int)width * 4)) {
        err = "stbi_write_png failed"; goto done;
    }
    ok = true;

done:
    if (mapped) { vkUnmapMemory(dev, mem); mapped = NULL; }
    if (rgba) free(rgba);
    if (fence) vkDestroyFence(dev, fence, NULL);
    if (cmd && pool) vkFreeCommandBuffers(dev, pool, 1, &cmd);
    if (pool) vkDestroyCommandPool(dev, pool, NULL);
    if (buf)  vkDestroyBuffer(dev, buf, NULL);
    if (mem)  vkFreeMemory(dev, mem, NULL);

    if (!ok && err) {
        SDL_Log("capture error: %s", err);
    }
    return ok;
}

static SglPixelFormat sk_swapchain_color_format(App *app) {
    switch (app->vk_swapchain_format) {
        case VK_FORMAT_B8G8R8A8_UNORM:
        case VK_FORMAT_B8G8R8A8_SRGB:  return SGL_PF_BGRA8;
        case VK_FORMAT_R8G8B8A8_UNORM:
        case VK_FORMAT_R8G8B8A8_SRGB:
        default:                        return SGL_PF_RGBA8;
    }
}

#endif // !__EMSCRIPTEN__

#ifdef __EMSCRIPTEN__
// -------------------------------------------------------------------------
// Emscripten / WebGPU bring-up.
//
// Architecture mirror of the Vulkan path above:
//   - sk_init   : grab the JS-side preinitialized WGPUDevice, build an
//                 instance, create a surface from the #canvas selector,
//                 configure it, allocate a depth-stencil, hand the device
//                 to sg_setup.
//   - sk_begin_frame : on resize, reconfigure surface + rebuild depth.
//                 Otherwise, wgpuSurfaceGetCurrentTexture → cache view.
//   - sk_begin_pass  : when targets[0]==0 (swapchain), populate
//                 sg_pass.swapchain.wgpu with the cached view + depth view.
//   - sk_end_frame   : sg_commit; release the per-frame view. On Emscripten
//                 the browser presents automatically via rAF — there's no
//                 wgpuSurfacePresent call.
//
// Canvas dimensions are queried from the JS side (player.html sets
// window._canvasWidth / _canvasHeight before loading the WASM, then keeps
// them in sync when the iframe is resized). Phase 5 will land that html.

EM_JS(int, sglua_canvas_width,  (void), { return (window._canvasWidth  || 480) | 0; })
EM_JS(int, sglua_canvas_height, (void), { return (window._canvasHeight || 360) | 0; })

static WGPUStringView sglua_wgpu_string(const char *s) {
    WGPUStringView v = { s, s ? strlen(s) : 0 };
    return v;
}

// Create/recreate the depth-stencil texture sized to the current canvas.
// Releases the old pair if present. Called from sk_init and on resize.
static bool sglua_wgpu_recreate_depth(App *app, uint32_t w, uint32_t h) {
    if (app->wgpu_depth_view) {
        wgpuTextureViewRelease(app->wgpu_depth_view);
        app->wgpu_depth_view = NULL;
    }
    if (app->wgpu_depth_tex) {
        wgpuTextureRelease(app->wgpu_depth_tex);
        app->wgpu_depth_tex = NULL;
    }
    WGPUTextureDescriptor ds_desc = {
        .usage = WGPUTextureUsage_RenderAttachment,
        .dimension = WGPUTextureDimension_2D,
        .size = { .width = w, .height = h, .depthOrArrayLayers = 1 },
        .format = WGPUTextureFormat_Depth32FloatStencil8,
        .mipLevelCount = 1,
        .sampleCount = 1,
    };
    app->wgpu_depth_tex = wgpuDeviceCreateTexture(app->wgpu_device, &ds_desc);
    if (!app->wgpu_depth_tex) {
        SDL_Log("[wgpu] wgpuDeviceCreateTexture (depth) failed");
        return false;
    }
    app->wgpu_depth_view = wgpuTextureCreateView(app->wgpu_depth_tex, NULL);
    if (!app->wgpu_depth_view) {
        SDL_Log("[wgpu] wgpuTextureCreateView (depth) failed");
        return false;
    }
    return true;
}

static void sglua_wgpu_configure_surface(App *app, uint32_t w, uint32_t h) {
    WGPUSurfaceConfiguration cfg = {
        .device = app->wgpu_device,
        .format = app->wgpu_surface_format,
        .usage  = WGPUTextureUsage_RenderAttachment,
        .width  = w,
        .height = h,
        .alphaMode   = WGPUCompositeAlphaMode_Opaque,
        .presentMode = WGPUPresentMode_Fifo,
    };
    wgpuSurfaceConfigure(app->wgpu_surface, &cfg);
}

static bool sk_init(App *app) {
    // emdawnwebgpu exposes the JS-side preinitializedWebGPUDevice through
    // emscripten_webgpu_get_device(). player.html (Phase 5) is expected to
    // have already done `await navigator.gpu.requestAdapter().requestDevice()`
    // and assigned `Module.preinitializedWebGPUDevice = device` before
    // loading sglua.js. If this returns NULL the page hasn't done that and
    // there's nothing the C side can recover to.
    app->wgpu_device = emscripten_webgpu_get_device();
    if (!app->wgpu_device) {
        SDL_Log("[wgpu] emscripten_webgpu_get_device returned NULL — "
                "did player.html set Module.preinitializedWebGPUDevice?");
        return false;
    }

    app->wgpu_instance = wgpuCreateInstance(NULL);
    if (!app->wgpu_instance) {
        SDL_Log("[wgpu] wgpuCreateInstance failed");
        return false;
    }

    // Surface from the player.html canvas. The CSS selector matches the
    // <canvas id="canvas"> in player.html documented in the spec.
    WGPUEmscriptenSurfaceSourceCanvasHTMLSelector canvas_src = {
        .chain = { .sType = WGPUSType_EmscriptenSurfaceSourceCanvasHTMLSelector },
        .selector = sglua_wgpu_string("#canvas"),
    };
    WGPUSurfaceDescriptor surf_desc = {
        .nextInChain = &canvas_src.chain,
    };
    app->wgpu_surface = wgpuInstanceCreateSurface(app->wgpu_instance, &surf_desc);
    if (!app->wgpu_surface) {
        SDL_Log("[wgpu] wgpuInstanceCreateSurface failed");
        return false;
    }

    // BGRA8Unorm is what every browser-side WebGPU surface supports today.
    // The pixel-format match with sg_setup defaults below is load-bearing —
    // sokol_gfx asserts on mismatch in begin_pass.
    app->wgpu_surface_format = WGPUTextureFormat_BGRA8Unorm;

    int cw = sglua_canvas_width();
    int ch = sglua_canvas_height();
    if (cw <= 0) cw = 480;
    if (ch <= 0) ch = 360;
    sglua_wgpu_configure_surface(app, (uint32_t)cw, (uint32_t)ch);
    if (!sglua_wgpu_recreate_depth(app, (uint32_t)cw, (uint32_t)ch)) return false;

    sg_setup(&(sg_desc){
        .environment = {
            .defaults = {
                .color_format = SG_PIXELFORMAT_BGRA8,
                .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
                .sample_count = 1,
            },
            .wgpu = {
                .device = (const void*)app->wgpu_device,
            },
        },
        .logger.func = sglua_sokol_logger,
    });
    SDL_Log("[wgpu] sokol backend init OK: %dx%d", cw, ch);
    return true;
}

static void sk_shutdown(App *app) {
    sg_shutdown();
    if (app->wgpu_swapchain_view) {
        wgpuTextureViewRelease(app->wgpu_swapchain_view);
        app->wgpu_swapchain_view = NULL;
    }
    if (app->wgpu_swapchain_tex) {
        wgpuTextureRelease(app->wgpu_swapchain_tex);
        app->wgpu_swapchain_tex = NULL;
    }
    if (app->wgpu_depth_view) {
        wgpuTextureViewRelease(app->wgpu_depth_view);
        app->wgpu_depth_view = NULL;
    }
    if (app->wgpu_depth_tex) {
        wgpuTextureRelease(app->wgpu_depth_tex);
        app->wgpu_depth_tex = NULL;
    }
    if (app->wgpu_surface) {
        wgpuSurfaceRelease(app->wgpu_surface);
        app->wgpu_surface = NULL;
    }
    if (app->wgpu_instance) {
        wgpuInstanceRelease(app->wgpu_instance);
        app->wgpu_instance = NULL;
    }
    // wgpu_device is owned by the JS side (preinitializedWebGPUDevice).
    // We do NOT release it; the page will discard it on iframe reload.
    app->wgpu_device = NULL;
}

static void sk_begin_frame(App *app, int *out_w, int *out_h) {
    int cw = sglua_canvas_width();
    int ch = sglua_canvas_height();
    if (cw <= 0) cw = 480;
    if (ch <= 0) ch = 360;

    // Resize: either the SDL event handler flagged pending_resize or the
    // canvas extents diverged from what's currently configured.
    bool needs_resize = app->pending_resize
                       || (app->last_w != 0 && cw != app->last_w)
                       || (app->last_h != 0 && ch != app->last_h);
    if (needs_resize) {
        app->pending_resize = false;
        // Drop any in-flight per-frame view before reconfiguring.
        if (app->wgpu_swapchain_view) {
            wgpuTextureViewRelease(app->wgpu_swapchain_view);
            app->wgpu_swapchain_view = NULL;
        }
        if (app->wgpu_swapchain_tex) {
            wgpuTextureRelease(app->wgpu_swapchain_tex);
            app->wgpu_swapchain_tex = NULL;
        }
        sglua_wgpu_configure_surface(app, (uint32_t)cw, (uint32_t)ch);
        if (!sglua_wgpu_recreate_depth(app, (uint32_t)cw, (uint32_t)ch)) {
            SDL_Log("sk_begin_frame: depth recreate failed during resize; skipping frame");
            if (out_w) *out_w = cw;
            if (out_h) *out_h = ch;
            return;
        }
    }

    // Acquire this frame's swapchain texture and create a view.
    WGPUSurfaceTexture surf_tex = {0};
    wgpuSurfaceGetCurrentTexture(app->wgpu_surface, &surf_tex);
    bool ok = (surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal
            || surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal);
    if (!ok) {
        // Outdated / Lost / Timeout: reconfigure and retry once. Anything
        // else, give up on this frame — the next iteration will try again.
        if (surf_tex.texture) {
            wgpuTextureRelease(surf_tex.texture);
            surf_tex.texture = NULL;
        }
        if (surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_Outdated
         || surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_Lost
         || surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_Timeout) {
            sglua_wgpu_configure_surface(app, (uint32_t)cw, (uint32_t)ch);
            if (!sglua_wgpu_recreate_depth(app, (uint32_t)cw, (uint32_t)ch)) {
                SDL_Log("sk_begin_frame: depth recreate failed during retry; skipping frame");
                if (out_w) *out_w = cw;
                if (out_h) *out_h = ch;
                return;
            }
            WGPUSurfaceTexture retry = {0};
            wgpuSurfaceGetCurrentTexture(app->wgpu_surface, &retry);
            ok = (retry.status == WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal
               || retry.status == WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal);
            surf_tex = retry;
        }
    }
    if (ok && surf_tex.texture) {
        app->wgpu_swapchain_tex  = surf_tex.texture;
        app->wgpu_swapchain_view = wgpuTextureCreateView(surf_tex.texture, NULL);
    } else {
        // sk_begin_pass will see NULL view and skip the swapchain attachment;
        // sokol_gfx will then panic. We log and let the next frame retry —
        // the most common cause is a 0x0 canvas while the iframe loads.
        if (surf_tex.texture) wgpuTextureRelease(surf_tex.texture);
        SDL_Log("[wgpu] surface acquire failed (status=%d)", (int)surf_tex.status);
    }

    if (out_w) *out_w = cw;
    if (out_h) *out_h = ch;
}

static void sk_end_frame(App *app) {
    sg_commit();
    // Per-frame view+texture are 1-use: release them after sg_commit has
    // queued the command buffer. Texture itself is owned by the surface so
    // we release the handle, not destroy the underlying GPU resource.
    if (app->wgpu_swapchain_view) {
        wgpuTextureViewRelease(app->wgpu_swapchain_view);
        app->wgpu_swapchain_view = NULL;
    }
    if (app->wgpu_swapchain_tex) {
        wgpuTextureRelease(app->wgpu_swapchain_tex);
        app->wgpu_swapchain_tex = NULL;
    }
    // No wgpuSurfacePresent on Emscripten — the browser flushes via rAF
    // and presents automatically after the JS event loop runs.
}

static void sk_begin_pass(App *app, const PassBeginDesc *d) {
    if (sk_try_begin_offscreen_pass(d)) return;
    sg_pass pass = {
        .action.colors[0] = {
            .load_action = SG_LOADACTION_CLEAR,
            .clear_value = { d->clear[0][0], d->clear[0][1], d->clear[0][2], d->clear[0][3] },
        },
        .swapchain = {
            .width = app->last_w,
            .height = app->last_h,
            .color_format = SG_PIXELFORMAT_BGRA8,
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .sample_count = 1,
            .wgpu = {
                .render_view = (const void*)app->wgpu_swapchain_view,
                .depth_stencil_view = (const void*)app->wgpu_depth_view,
            },
        },
    };
    sg_begin_pass(&pass);
}

static bool sk_capture(App *app, const char *path) {
    (void)app; (void)path;
    // WebGPU readback (mapAsync) would work, but the path is async and the
    // capture API is currently synchronous. Phase 6+ may revisit; for now
    // capture is intentionally not supported under wasm.
    SDL_Log("[wasm] capture not supported on WebGPU backend");
    return false;
}

static SglPixelFormat sk_swapchain_color_format(App *app) {
    (void)app;
    // Surface format is pinned to BGRA8Unorm at sk_init (see comment there).
    return SGL_PF_BGRA8;
}

#endif // __EMSCRIPTEN__

const RenderBackend g_backend_sokol = {
    .name = "sokol",
    .init = sk_init,
    .shutdown = sk_shutdown,
    .begin_frame = sk_begin_frame,
    .end_frame = sk_end_frame,
    .make_buffer = sk_make_buffer,
    .make_image = sk_make_image,
    .make_shader = sk_make_shader,
    .make_pipeline = sk_make_pipeline,
    .destroy_buffer = sk_destroy_buffer,
    .destroy_image = sk_destroy_image,
    .destroy_shader = sk_destroy_shader,
    .destroy_pipeline = sk_destroy_pipeline,
    .update_buffer = sk_update_buffer,
    .update_image  = sk_update_image,
    .begin_pass = sk_begin_pass,
    .end_pass = sk_end_pass,
    .apply_pipeline = sk_apply_pipeline,
    .apply_bindings = sk_apply_bindings,
    .apply_uniforms = sk_apply_uniforms,
    .draw = sk_draw,
    .dispatch = sk_dispatch,
    .capture = sk_capture,
    .swapchain_color_format = sk_swapchain_color_format,
};

const RenderBackend *g_backend = &g_backend_sokol;
