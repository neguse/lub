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
        // sokol_gfx Vulkan backend uses vkCmdPipelineBarrier2 (core in 1.3).
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

static bool create_swapchain(App *app) {
    if (!SDL_Vulkan_CreateSurface(app->window, app->vk_instance, NULL, &app->vk_surface)) {
        SDL_Log("SDL_Vulkan_CreateSurface failed: %s", SDL_GetError());
        return false;
    }

    // Surface format: prefer B8G8R8A8_UNORM, otherwise the first available.
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

    // Swapchain images & views
    vkGetSwapchainImagesKHR(app->vk_device, app->vk_swapchain, &app->vk_swapchain_image_count, NULL);
    app->vk_swapchain_images = (VkImage*)malloc(sizeof(VkImage) * app->vk_swapchain_image_count);
    vkGetSwapchainImagesKHR(app->vk_device, app->vk_swapchain, &app->vk_swapchain_image_count, app->vk_swapchain_images);
    app->vk_swapchain_views = (VkImageView*)malloc(sizeof(VkImageView) * app->vk_swapchain_image_count);
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

    // Depth attachment (D24S8 with D32FS8 fallback)
    VkFormat depth_fmt = VK_FORMAT_D24_UNORM_S8_UINT;
    VkFormatProperties fp;
    vkGetPhysicalDeviceFormatProperties(app->vk_phys, depth_fmt, &fp);
    if (!(fp.optimalTilingFeatures & VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT)) {
        depth_fmt = VK_FORMAT_D32_SFLOAT_S8_UINT;
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

    // Semaphores (single pair — PoC, no in-flight frames)
    VkSemaphoreCreateInfo sci2 = { .sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    vkCreateSemaphore(app->vk_device, &sci2, NULL, &app->vk_acquire_sem);
    vkCreateSemaphore(app->vk_device, &sci2, NULL, &app->vk_present_sem);

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
    // sokol_gfx Vulkan backend uses vkCmdPipelineBarrier2 / vkCmdBeginRendering
    // (both core in Vulkan 1.3). Enable the corresponding feature bits.
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
    if (!create_swapchain(app)) return false;

    // Match sg_environment.defaults.color_format to actual swapchain surface format.
    sg_pixel_format color_pf =
        (app->vk_swapchain_format == VK_FORMAT_B8G8R8A8_UNORM) ? SG_PIXELFORMAT_BGRA8 :
        (app->vk_swapchain_format == VK_FORMAT_R8G8B8A8_UNORM) ? SG_PIXELFORMAT_RGBA8 :
        SG_PIXELFORMAT_BGRA8;

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

    pass_state_init(&app->pass);
    pass_state_set_app(&app->pass, app);
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

    vkAcquireNextImageKHR(app->vk_device, app->vk_swapchain, UINT64_MAX,
                          app->vk_acquire_sem, VK_NULL_HANDLE, &app->vk_current_image);
}

void app_frame_end(App *app) {
    sg_commit();
    VkPresentInfoKHR pi = {
        .sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
        .waitSemaphoreCount = 1,
        .pWaitSemaphores = &app->vk_present_sem,
        .swapchainCount = 1,
        .pSwapchains = &app->vk_swapchain,
        .pImageIndices = &app->vk_current_image,
    };
    vkQueuePresentKHR(app->vk_queue, &pi);
    app->frame_index++;
}

void app_shutdown(App *app) {
    // Make sure GPU is idle before tearing anything down.
    if (app->vk_device) vkDeviceWaitIdle(app->vk_device);

    // Pipelines reference shaders, so destroy pipelines before resources.
    pipeline_cache_shutdown(&app->pip_cache);
    res_table_shutdown(&app->res);
    sg_shutdown();

    if (app->vk_device) {
        if (app->vk_present_sem) vkDestroySemaphore(app->vk_device, app->vk_present_sem, NULL);
        if (app->vk_acquire_sem) vkDestroySemaphore(app->vk_device, app->vk_acquire_sem, NULL);
        if (app->vk_depth_view)  vkDestroyImageView(app->vk_device, app->vk_depth_view, NULL);
        if (app->vk_depth_image) vkDestroyImage(app->vk_device, app->vk_depth_image, NULL);
        if (app->vk_depth_mem)   vkFreeMemory(app->vk_device, app->vk_depth_mem, NULL);
        if (app->vk_swapchain_views) {
            for (uint32_t i = 0; i < app->vk_swapchain_image_count; ++i) {
                if (app->vk_swapchain_views[i]) {
                    vkDestroyImageView(app->vk_device, app->vk_swapchain_views[i], NULL);
                }
            }
            free(app->vk_swapchain_views);
        }
        free(app->vk_swapchain_images);
        if (app->vk_swapchain) vkDestroySwapchainKHR(app->vk_device, app->vk_swapchain, NULL);
    }
    if (app->vk_surface)  vkDestroySurfaceKHR(app->vk_instance, app->vk_surface, NULL);
    if (app->vk_device)   vkDestroyDevice(app->vk_device, NULL);
    if (app->vk_instance) vkDestroyInstance(app->vk_instance, NULL);
    SDL_Vulkan_UnloadLibrary();
    if (app->window) SDL_DestroyWindow(app->window);
}
