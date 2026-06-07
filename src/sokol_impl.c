// sokol_gfx.h includes its implementation in the same header, gated by
// SOKOL_GFX_IMPL. The implementation section is *outside* the include guard,
// so defining SOKOL_GFX_IMPL in a translation unit that also pulls
// sokol_gfx.h transitively from multiple headers causes duplicate symbol
// definitions. Keep the implementation in this single dedicated TU.
#define SOKOL_GFX_IMPL
#include "sokol_gfx.h"

#ifndef __EMSCRIPTEN__
#include <stdbool.h>
#include <vulkan/vulkan.h>

bool lub_sokol_vk_image_readback_info(sg_image img, VkImage *out_image,
                                      VkImageLayout *out_layout) {
#if defined(SOKOL_VULKAN)
  const _sg_image_t *si = _sg_lookup_image(img.id);
  if (!si || !si->vk.img)
    return false;
  if (out_image)
    *out_image = si->vk.img;
  if (out_layout)
    *out_layout = _sg_vk_image_layout(si->vk.cur_access);
  return true;
#else
  (void)img;
  (void)out_image;
  (void)out_layout;
  return false;
#endif
}
#else
#include <stdbool.h>
#include <webgpu/webgpu.h>

bool lub_sokol_wgpu_image_texture(sg_image img, WGPUTexture *out_texture) {
#if defined(SOKOL_WGPU)
  const _sg_image_t *si = _sg_lookup_image(img.id);
  if (!si || !si->wgpu.tex)
    return false;
  if (out_texture)
    *out_texture = si->wgpu.tex;
  return true;
#else
  (void)img;
  (void)out_texture;
  return false;
#endif
}
#endif
