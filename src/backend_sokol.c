// backend_sokol.c — RenderBackend impl using sokol_gfx (Vulkan backend) plus
// direct Vulkan calls for instance/device/swapchain ownership and capture.
//
// All sg_*/vk* calls used by the runtime live in this file. Other source files
// (pass.c, pipeline.c, resources.c, capture.c, lua_api.c) talk to the GPU
// only through g_backend->xxx().
#include "backend.h"
#include "app.h"
#include "shader.h"

#include "sokol_gfx.h"

#include <SDL3/SDL.h>
#include <SDL3/SDL_vulkan.h>
#include <vulkan/vulkan.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "stb_image_write.h"

// --- per-image / per-shader / per-pipeline backend objects ---------------

typedef struct SkImage {
    sg_image  img;
    sg_sampler smp;
    sg_view   view;
} SkImage;

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

static bool create_swapchain(App *app) {
    if (!SDL_Vulkan_CreateSurface(app->window, app->vk_instance, NULL, &app->vk_surface)) {
        SDL_Log("SDL_Vulkan_CreateSurface failed: %s", SDL_GetError());
        return false;
    }

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

    // Depth attachment
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
    if (!create_swapchain(app)) return false;

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
            app->vk_swapchain_views = NULL;
        }
        free(app->vk_swapchain_images);
        app->vk_swapchain_images = NULL;
        if (app->vk_swapchain) vkDestroySwapchainKHR(app->vk_device, app->vk_swapchain, NULL);
    }
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
    vkAcquireNextImageKHR(app->vk_device, app->vk_swapchain, UINT64_MAX,
                          app->vk_acquire_sem, VK_NULL_HANDLE, &app->vk_current_image);
    app->vk_last_presented_image = app->vk_swapchain_images[app->vk_current_image];
}

static void sk_end_frame(App *app) {
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
}

static BackendBuffer sk_make_buffer(SglBufferType type, const float *data, size_t bytes) {
    sg_buffer h = sg_make_buffer(&(sg_buffer_desc){
        .size = bytes,
        .usage = {
            .vertex_buffer  = (type == SGL_BUFFER_VERTEX),
            .index_buffer   = (type == SGL_BUFFER_INDEX),
            .dynamic_update = true,
        },
        // dynamic_update: initial data はここで渡せないので make 後に update
    });
    if (h.id == SG_INVALID_ID) return 0;
    if (data && bytes > 0) {
        sg_update_buffer(h, &(sg_range){ .ptr = data, .size = bytes });
    }
    return (uintptr_t)h.id;
}

static void sk_destroy_buffer(BackendBuffer h) {
    if (!h) return;
    sg_destroy_buffer((sg_buffer){ .id = (uint32_t)h });
}

static BackendImage sk_make_image(const ImageDesc *d) {
    SkImage *si = (SkImage*)calloc(1, sizeof(SkImage));
    if (!si) return 0;
    sg_pixel_format pf = (d->fmt == SGL_PF_R8) ? SG_PIXELFORMAT_R8 : SG_PIXELFORMAT_RGBA8;
    sg_image_desc img_desc = {
        .width = d->w,
        .height = d->h,
        .pixel_format = pf,
        .usage = { .dynamic_update = true },
    };
    si->img = sg_make_image(&img_desc);
    if (si->img.id == SG_INVALID_ID) { free(si); return 0; }
    if (d->data && d->data_bytes > 0) {
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
    return (uintptr_t)si;
}

static void sk_destroy_image(BackendImage h) {
    SkImage *si = (SkImage*)h;
    if (!si) return;
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
    // Slang's SPIR-V emitter renames the entry-point function to "main".
    desc.vertex_func.entry            = "main";
    desc.vertex_func.bytecode.ptr     = d->vs_spirv;
    desc.vertex_func.bytecode.size    = d->vs_bytes;
    desc.fragment_func.entry          = "main";
    desc.fragment_func.bytecode.ptr   = d->fs_spirv;
    desc.fragment_func.bytecode.size  = d->fs_bytes;

    // Vertex attributes — SPIR-V identifies inputs by location number, which
    // sokol's Vulkan backend reads from the SPIR-V module directly; the desc
    // only needs base_type set for validation.
    for (int i = 0; i < ss->refl.attr_count && i < SG_MAX_VERTEX_ATTRIBUTES; ++i) {
        desc.attrs[i].base_type = SG_SHADERATTRBASETYPE_FLOAT;
    }

    // Uniform blocks: map sokol bind slot to (set=0, binding=N).
    for (int b = 0; b < ss->refl.ub_count && b < SGL_MAX_UNIFORM_BLOCKS; ++b) {
        ShaderUniformBlock *u = &ss->refl.ubs[b];
        int slot = u->slot;
        if (slot < 0 || slot >= SG_MAX_UNIFORMBLOCK_BINDSLOTS) continue;
        sg_shader_uniform_block *dst = &desc.uniform_blocks[slot];
        // PoC assumption: vertex shader is the only stage that uses uniform blocks.
        dst->stage = SG_SHADERSTAGE_VERTEX;
        dst->size = (uint32_t)(u->size_floats * 4);
        dst->layout = SG_UNIFORMLAYOUT_STD140;
        dst->spirv_set0_binding_n = (uint8_t)slot;
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
        view->texture.spirv_set1_binding_n = (uint8_t)img_slot;

        if (smp_slot >= 0 && smp_slot < SG_MAX_SAMPLER_BINDSLOTS) {
            sg_shader_sampler *smp = &desc.samplers[smp_slot];
            smp->stage = SG_SHADERSTAGE_FRAGMENT;
            smp->sampler_type = SG_SAMPLERTYPE_FILTERING;
            smp->spirv_set1_binding_n = (uint8_t)smp_slot;
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

    sg_pipeline_desc desc = {0};
    desc.shader = ss->sh;
    desc.colors[0].pixel_format = sgl_to_sg_fmt(d->color_fmt);
    desc.colors[0].blend = to_sokol_blend(d->blend);
    desc.depth.pixel_format = SG_PIXELFORMAT_DEPTH_STENCIL;
    desc.depth.compare = d->depth_test ? SG_COMPAREFUNC_LESS_EQUAL : SG_COMPAREFUNC_ALWAYS;
    desc.depth.write_enabled = d->depth_write;
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
    if (!h || !data || bytes == 0) return;
    sg_update_buffer((sg_buffer){ .id = (uint32_t)h },
                     &(sg_range){ .ptr = data, .size = bytes });
}

static void sk_update_image(BackendImage h, const void *data, size_t bytes) {
    if (!h || !data || bytes == 0) return;
    SkImage *si = (SkImage*)h;
    sg_update_image(si->img, &(sg_image_data){
        .mip_levels[0] = { .ptr = data, .size = bytes },
    });
}

static void sk_begin_pass(App *app, const PassBeginDesc *d) {
    sg_pass pass = {
        .action.colors[0] = {
            .load_action = SG_LOADACTION_CLEAR,
            .clear_value = { d->clear[0], d->clear[1], d->clear[2], d->clear[3] },
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
                .render_finished_semaphore = (const void*)app->vk_present_sem,
                .present_complete_semaphore = (const void*)app->vk_acquire_sem,
            },
        },
    };
    sg_begin_pass(&pass);
}

static void sk_end_pass(App *app) {
    (void)app;
    sg_end_pass();
}

static void sk_apply_pipeline(BackendPipeline p) {
    sg_apply_pipeline((sg_pipeline){ .id = (uint32_t)p });
}

static void sk_apply_bindings(const BindingsDesc *b) {
    sg_bindings sb = {0};
    if (b->vbuf) sb.vertex_buffers[0] = (sg_buffer){ .id = (uint32_t)b->vbuf };

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
    .capture = sk_capture,
    .swapchain_color_format = sk_swapchain_color_format,
};

const RenderBackend *g_backend = &g_backend_sokol;
