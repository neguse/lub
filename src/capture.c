#include "capture.h"

#include <SDL3/SDL.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "stb_image_write.h"

void capture_state_init(CaptureState *c) {
    if (!c) return;
    c->pending = false;
    c->path = NULL;
    c->target_frame = 0;
}

void capture_state_shutdown(CaptureState *c) {
    if (!c) return;
    if (c->path) {
        free(c->path);
        c->path = NULL;
    }
    c->pending = false;
    c->target_frame = 0;
}

void capture_schedule(CaptureState *c, const char *path, uint64_t at_frame) {
    if (!c || !path) return;
    if (c->path) {
        free(c->path);
        c->path = NULL;
    }
    size_t n = strlen(path);
    c->path = (char*)malloc(n + 1);
    if (c->path) {
        memcpy(c->path, path, n + 1);
    }
    c->target_frame = at_frame;
    c->pending = true;
}

static uint32_t pick_host_visible_memory_type(
    VkPhysicalDevice phys, uint32_t type_bits, bool *out_coherent)
{
    VkPhysicalDeviceMemoryProperties pmp;
    vkGetPhysicalDeviceMemoryProperties(phys, &pmp);
    // Prefer host-visible + host-coherent.
    for (uint32_t i = 0; i < pmp.memoryTypeCount; ++i) {
        if ((type_bits & (1u << i)) == 0) continue;
        VkMemoryPropertyFlags flags = pmp.memoryTypes[i].propertyFlags;
        if ((flags & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) &&
            (flags & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) {
            if (out_coherent) *out_coherent = true;
            return i;
        }
    }
    // Fall back to host-visible (non-coherent).
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

bool capture_run_if_pending(
    CaptureState    *c,
    uint64_t         current_frame,
    VkInstance       inst,
    VkPhysicalDevice phys,
    VkDevice         dev,
    VkQueue          queue,
    uint32_t         queue_family,
    VkImage          swapchain_image,
    uint32_t         width,
    uint32_t         height,
    VkFormat         format,
    const char     **out_err)
{
    (void)inst;
    if (!c || !c->pending || !c->path) return false;
    if (current_frame < c->target_frame) return false;
    if (width == 0 || height == 0) return false;
    if (!format_is_bgra8(format) && !format_is_rgba8(format)) {
        if (out_err) *out_err = "unsupported swapchain format (need BGRA8/RGBA8)";
        // Consume the request to avoid retrying every frame.
        c->pending = false;
        return true;
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
    bool wrote = false;
    const char *err = NULL;

    // 1. Create host-visible buffer.
    VkBufferCreateInfo bci = {
        .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
        .size = buf_size,
        .usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT,
        .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
    };
    if (vkCreateBuffer(dev, &bci, NULL, &buf) != VK_SUCCESS) {
        err = "vkCreateBuffer failed";
        goto done;
    }

    VkMemoryRequirements mr;
    vkGetBufferMemoryRequirements(dev, buf, &mr);
    uint32_t mt = pick_host_visible_memory_type(phys, mr.memoryTypeBits, &coherent);
    if (mt == UINT32_MAX) {
        err = "no host-visible memory type";
        goto done;
    }
    VkMemoryAllocateInfo mai = {
        .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
        .allocationSize = mr.size,
        .memoryTypeIndex = mt,
    };
    if (vkAllocateMemory(dev, &mai, NULL, &mem) != VK_SUCCESS) {
        err = "vkAllocateMemory failed";
        goto done;
    }
    if (vkBindBufferMemory(dev, buf, mem, 0) != VK_SUCCESS) {
        err = "vkBindBufferMemory failed";
        goto done;
    }

    // 2. Transient command pool + command buffer.
    VkCommandPoolCreateInfo cpci = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
        .flags = VK_COMMAND_POOL_CREATE_TRANSIENT_BIT,
        .queueFamilyIndex = queue_family,
    };
    if (vkCreateCommandPool(dev, &cpci, NULL, &pool) != VK_SUCCESS) {
        err = "vkCreateCommandPool failed";
        goto done;
    }
    VkCommandBufferAllocateInfo cbai = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
        .commandPool = pool,
        .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
        .commandBufferCount = 1,
    };
    if (vkAllocateCommandBuffers(dev, &cbai, &cmd) != VK_SUCCESS) {
        err = "vkAllocateCommandBuffers failed";
        goto done;
    }

    // 3. Record commands.
    VkCommandBufferBeginInfo cbbi = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
        .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
    };
    if (vkBeginCommandBuffer(cmd, &cbbi) != VK_SUCCESS) {
        err = "vkBeginCommandBuffer failed";
        goto done;
    }

    // PRESENT_SRC_KHR -> TRANSFER_SRC_OPTIMAL
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

    // TRANSFER_SRC_OPTIMAL -> PRESENT_SRC_KHR (so subsequent presents don't choke)
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
        err = "vkEndCommandBuffer failed";
        goto done;
    }

    // 4. Submit and wait.
    VkFenceCreateInfo fci = { .sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
    if (vkCreateFence(dev, &fci, NULL, &fence) != VK_SUCCESS) {
        err = "vkCreateFence failed";
        goto done;
    }
    VkSubmitInfo si = {
        .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO,
        .commandBufferCount = 1,
        .pCommandBuffers = &cmd,
    };
    if (vkQueueSubmit(queue, 1, &si, fence) != VK_SUCCESS) {
        err = "vkQueueSubmit failed";
        goto done;
    }
    if (vkWaitForFences(dev, 1, &fence, VK_TRUE, UINT64_MAX) != VK_SUCCESS) {
        err = "vkWaitForFences failed";
        goto done;
    }

    // 5. Map memory; if non-coherent, invalidate first.
    if (vkMapMemory(dev, mem, 0, buf_size, 0, &mapped) != VK_SUCCESS) {
        err = "vkMapMemory failed";
        goto done;
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

    // 6. Convert/copy bytes into a contiguous RGBA buffer for stb.
    rgba = (uint8_t*)malloc((size_t)buf_size);
    if (!rgba) {
        err = "out of memory (rgba buffer)";
        goto done;
    }
    const uint8_t *src = (const uint8_t*)mapped;
    if (format_is_bgra8(format)) {
        const size_t pixels = (size_t)width * (size_t)height;
        for (size_t i = 0; i < pixels; ++i) {
            uint8_t b = src[i*4 + 0];
            uint8_t g = src[i*4 + 1];
            uint8_t r = src[i*4 + 2];
            uint8_t a = src[i*4 + 3];
            rgba[i*4 + 0] = r;
            rgba[i*4 + 1] = g;
            rgba[i*4 + 2] = b;
            rgba[i*4 + 3] = a;
        }
    } else {
        memcpy(rgba, src, (size_t)buf_size);
    }

    // 7. Write PNG.
    int ok = stbi_write_png(c->path, (int)width, (int)height, 4,
                            rgba, (int)width * 4);
    if (!ok) {
        err = "stbi_write_png failed";
        goto done;
    }
    wrote = true;

done:
    if (mapped) {
        vkUnmapMemory(dev, mem);
        mapped = NULL;
    }
    if (rgba) free(rgba);
    if (fence) vkDestroyFence(dev, fence, NULL);
    if (cmd && pool) vkFreeCommandBuffers(dev, pool, 1, &cmd);
    if (pool) vkDestroyCommandPool(dev, pool, NULL);
    if (buf)  vkDestroyBuffer(dev, buf, NULL);
    if (mem)  vkFreeMemory(dev, mem, NULL);

    if (out_err) *out_err = err;
    // Always consume the pending request — success or failure, we don't loop.
    c->pending = false;
    (void)wrote;
    return true;
}
