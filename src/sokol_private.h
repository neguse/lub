#pragma once

#include "sokol_gfx.h"

#ifndef __EMSCRIPTEN__
#include <stdbool.h>
#include <vulkan/vulkan.h>

bool lub_sokol_vk_image_readback_info(sg_image img, VkImage *out_image,
                                      VkImageLayout *out_layout);
#else
#include <stdbool.h>
#include <webgpu/webgpu.h>

bool lub_sokol_wgpu_image_texture(sg_image img, WGPUTexture *out_texture);
#endif
