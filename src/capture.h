#pragma once
#include <stdbool.h>
#include <stdint.h>
#include <vulkan/vulkan.h>

// Capture state attached to App.
typedef struct CaptureState {
    bool     pending;      // true = capture next presented frame
    char    *path;         // strdup'd PNG path (free on shutdown / after capture)
    uint64_t target_frame; // frame index at/after which to capture (0 = next)
} CaptureState;

void capture_state_init(CaptureState *c);
void capture_state_shutdown(CaptureState *c);

// Schedule a capture: takes ownership of a copy of `path`.
// `at_frame` 0 means "capture as soon as possible (next frame)".
void capture_schedule(CaptureState *c, const char *path, uint64_t at_frame);

// Called from app_frame_end AFTER vkQueuePresentKHR. Reads back the just-rendered
// swapchain image and writes PNG.
// Returns true if a capture was performed (caller should arrange app exit).
// `swapchain_image`: the VkImage that was just rendered to.
// `width`/`height`: extent.
// `format`: VkFormat of the swapchain (BGRA8/RGBA8 only supported in PoC).
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
    const char     **out_err);
