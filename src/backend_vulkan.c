// Vulkan backend (Windows / Linux)。Linux では既定、Windows では
// LUB_BACKEND=vulkan / config({backend="vulkan"}) で選ぶ。
//
// Implements the RenderBackend vtable directly on Vulkan 1.3 — the
// counterpart of backend_d3d12.cpp, same design:
//
//   * Single graphics queue, kFramesInFlight = 2. One command buffer is open
//     from begin_frame to end_frame; passes, copies and compute all record
//     into it. In-order execution on the one queue reproduces the SDL_GPU
//     "cycle" semantics for mid-frame buffer updates: draws recorded before
//     an update read the old contents.
//   * Uniforms are suballocated from a per-frame host-visible upload arena
//     and bound through per-draw descriptor sets from a per-frame pool ring.
//   * Descriptor set layouts are built per shader from ShaderReflection.
//     The SPIR-V comes from SHADER_TARGET_SDLGPU, so the set/binding
//     convention is SDL_GPU's: vs resources=set 0 / vs UBs=set 1 /
//     fs resources=set 2 / fs UBs=set 3; compute read=0 / write=1 / UBs=2.
//   * Image layouts are tracked per image and transitioned lazily. Barriers
//     and copies requested inside a pass suspend dynamic rendering and
//     resume it with LOAD ops (D3D12 allows these mid-pass, Vulkan doesn't).
//   * D3D-style clip conventions via negative viewport height; front face
//     stays clockwise to match the d3d12 backend.
#include "app.h"
#include "backend.h"
#include "gpu_stats.h"
#include "stb_image_write.h"

#include <SDL3/SDL.h>
#include <SDL3/SDL_vulkan.h>
#include <vulkan/vulkan.h>

#include <limits.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define KFRAMES_IN_FLIGHT 2
#define KMAX_SWAPCHAIN_IMAGES 8
#define KARENA_CHUNK_CAP 32
#define KARENA_BASE_CHUNK (4 * 1024 * 1024)
#define KDESC_POOL_CAP 8
#define KDESC_POOL_SETS 4096
#define KSET_MAX_BINDINGS 16

// --- upload arena -----------------------------------------------------------
// Per-frame transient upload memory (uniforms, buffer/texture updates).
// Chunked linear allocator over host-visible buffers; reset when the frame
// slot's timeline value has passed. All but the first chunk are released on
// reset so a one-off spike doesn't pin memory forever.
typedef struct ArenaChunk {
  VkBuffer buf;
  VkDeviceMemory mem;
  uint8_t *map;
  size_t cap, off;
} ArenaChunk;

typedef struct Arena {
  ArenaChunk chunks[KARENA_CHUNK_CAP];
  int count;
} Arena;

typedef struct UploadAlloc {
  VkBuffer buf;
  size_t offset;
  uint8_t *cpu;
} UploadAlloc;

// --- per-frame context -------------------------------------------------------
typedef struct FrameCtx {
  VkCommandPool pool;
  VkCommandBuffer cmd;
  uint64_t fence_value; // timeline value that retires this slot
  VkSemaphore acquire_sem;
  Arena arena;
  VkDescriptorPool desc_pools[KDESC_POOL_CAP];
  int desc_pool_count;
  int desc_pool_cur;
} FrameCtx;

// --- resource wrappers -------------------------------------------------------
typedef struct VkbBuffer {
  VkBuffer buf;
  VkDeviceMemory mem;
  size_t bytes;
  SglBufferType type;
} VkbBuffer;

typedef struct VkbImage {
  VkImage img;
  VkDeviceMemory mem;
  VkImageView view;        // sampled/storage view (depth: DEPTH aspect only)
  VkImageView attach_view; // attachment view (full aspect); color: == view
  VkSampler sampler;
  int w, h;
  SglPixelFormat fmt;
  VkFormat vkfmt;
  SglFilter filter;
  SglWrap wrap;
  bool render_target;
  bool storage;
  bool is_depth;
  VkImageLayout layout;
} VkbImage;

// Descriptor set layout contents mirrored for write-time iteration.
typedef struct SetBindingInfo {
  int binding;
  VkDescriptorType type;
} SetBindingInfo;

typedef struct SetInfo {
  int count;
  SetBindingInfo b[KSET_MAX_BINDINGS];
} SetInfo;

typedef struct VkbShader {
  ShaderReflection refl;
  VkShaderModule vs, fs, cs;
  VkDescriptorSetLayout dsl[4];
  SetInfo sets[4];
  int n_sets; // 4 graphics, 3 compute
  VkPipelineLayout layout;
  VkPipeline compute_pipe; // compute collapses shader+pipeline
} VkbShader;

typedef struct VkbPipeline {
  VkPipeline pipe;
  VkPipeline compute_pipe;      // weak, owned by VkbShader
  VkPipelineLayout layout;      // weak, owned by VkbShader
  VkDescriptorSetLayout dsl[4]; // weak, owned by VkbShader
  SetInfo sets[4];
  int n_sets;
  ShaderReflection refl;
  bool is_compute;
} VkbPipeline;

// --- global state ------------------------------------------------------------
typedef struct VkbState {
  App *app;
  VkInstance instance;
  VkDebugUtilsMessengerEXT messenger;
  VkSurfaceKHR surface;
  VkPhysicalDevice phys;
  VkDevice device;
  VkQueue queue;
  uint32_t qfam;
  VkDeviceSize ub_align;

  VkSemaphore timeline;
  uint64_t fence_next;
  uint64_t last_submit_value;

  FrameCtx frames[KFRAMES_IN_FLIGHT];
  int slot;

  VkSwapchainKHR swapchain;
  VkFormat sc_format;
  SglPixelFormat sc_fmt_sgl;
  VkImage sc_images[KMAX_SWAPCHAIN_IMAGES];
  VkImageView sc_views[KMAX_SWAPCHAIN_IMAGES];
  VkImageLayout sc_layouts[KMAX_SWAPCHAIN_IMAGES];
  VkSemaphore render_done_sems[KMAX_SWAPCHAIN_IMAGES];
  uint32_t sc_count;
  uint32_t bb_index;
  int sw_w, sw_h;
  bool need_recreate;

  // Default depth buffer for swapchain passes. pass.c reports
  // SGL_PF_DEPTH24_STENCIL8 as the swapchain pass depth format (pipeline
  // cache key); depth24_fmt is what that maps to on this device.
  VkFormat depth24_fmt;
  VkImage depth_img;
  VkDeviceMemory depth_mem;
  VkImageView depth_view;
  VkImageLayout depth_layout;
  int depth_w, depth_h;

  VkCommandPool oneshot_pool;

  bool recording;
  bool have_acquired;
  bool acquire_waited;
  bool submitted_before_present;

  // Current pass state (for suspend/resume around mid-pass barriers/copies).
  bool in_pass;
  bool pass_suspended;
  int pass_w, pass_h;
  int pass_n_colors;
  VkRenderingAttachmentInfo pass_colors[SGL_MAX_COLOR_TARGETS];
  VkRenderingAttachmentInfo pass_depth;
  bool pass_has_depth;

  // Dummy resources for unbound descriptor slots (parity with the d3d12
  // backend's null SRV/UAV writes; Vulkan descriptors can't be null).
  VkbImage *dummy_tex;     // 1x1 white, SHADER_READ_ONLY
  VkbImage *dummy_storage; // 1x1, GENERAL
  VkbBuffer *dummy_ubuf;
  VkbBuffer *dummy_sbuf;
} VkbState;

static VkbState g;

// Draw-state globals.
static VkbPipeline *g_current_pip = NULL;
static bool g_last_indexed = false;
// Last-applied uniforms per stage (0=vertex 1=fragment) per slot; persist
// across draws like root CBVs on d3d12, re-bound through a fresh descriptor
// set when dirty.
typedef struct UniformSlot {
  VkBuffer buf;
  VkDeviceSize off;
  size_t bytes;
  bool set;
} UniformSlot;
static UniformSlot g_uniforms[2][SGL_MAX_UNIFORM_BLOCKS];
static bool g_uniforms_dirty[2] = {true, true};

static void vkb_drain_zombies(void);
static void vkb_pass_resume(void);

// --- small helpers
// ------------------------------------------------------------

static bool sgl_is_depth_fmt(SglPixelFormat fmt) {
  return fmt == SGL_PF_DEPTH16 || fmt == SGL_PF_DEPTH24_STENCIL8 ||
         fmt == SGL_PF_DEPTH32F;
}

static VkFormat vkb_format(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_R8:
    return VK_FORMAT_R8_UNORM;
  case SGL_PF_RG8:
    return VK_FORMAT_R8G8_UNORM;
  case SGL_PF_R16F:
    return VK_FORMAT_R16_SFLOAT;
  case SGL_PF_RG16F:
    return VK_FORMAT_R16G16_SFLOAT;
  case SGL_PF_R32F:
    return VK_FORMAT_R32_SFLOAT;
  case SGL_PF_RGBA16F:
    return VK_FORMAT_R16G16B16A16_SFLOAT;
  case SGL_PF_RGBA32F:
    return VK_FORMAT_R32G32B32A32_SFLOAT;
  case SGL_PF_DEPTH16:
    return VK_FORMAT_D16_UNORM;
  case SGL_PF_DEPTH32F:
    return VK_FORMAT_D32_SFLOAT;
  case SGL_PF_DEPTH24_STENCIL8:
    return g.depth24_fmt; // D24S8 or D32S8, whichever the device supports
  case SGL_PF_BGRA8:
    return VK_FORMAT_B8G8R8A8_UNORM;
  case SGL_PF_RGBA8:
  default:
    return VK_FORMAT_R8G8B8A8_UNORM;
  }
}

static int vkb_bytes_per_pixel(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_R8:
    return 1;
  case SGL_PF_RG8:
  case SGL_PF_R16F:
  case SGL_PF_DEPTH16:
    return 2;
  case SGL_PF_RGBA16F:
    return 8;
  case SGL_PF_RGBA32F:
    return 16;
  default:
    return 4;
  }
}

static VkImageAspectFlags vkb_aspect(VkFormat fmt, bool sampled_view) {
  switch (fmt) {
  case VK_FORMAT_D16_UNORM:
  case VK_FORMAT_D32_SFLOAT:
    return VK_IMAGE_ASPECT_DEPTH_BIT;
  case VK_FORMAT_D24_UNORM_S8_UINT:
  case VK_FORMAT_D32_SFLOAT_S8_UINT:
    return sampled_view
               ? VK_IMAGE_ASPECT_DEPTH_BIT
               : (VK_IMAGE_ASPECT_DEPTH_BIT | VK_IMAGE_ASPECT_STENCIL_BIT);
  default:
    return VK_IMAGE_ASPECT_COLOR_BIT;
  }
}

static bool vkb_find_mem_type(uint32_t type_bits, VkMemoryPropertyFlags props,
                              uint32_t *out) {
  VkPhysicalDeviceMemoryProperties mp;
  vkGetPhysicalDeviceMemoryProperties(g.phys, &mp);
  for (uint32_t i = 0; i < mp.memoryTypeCount; ++i) {
    if ((type_bits & (1u << i)) &&
        (mp.memoryTypes[i].propertyFlags & props) == props) {
      *out = i;
      return true;
    }
  }
  return false;
}

static bool vkb_alloc_buffer(VkDeviceSize bytes, VkBufferUsageFlags usage,
                             VkMemoryPropertyFlags props, VkBuffer *out_buf,
                             VkDeviceMemory *out_mem) {
  VkBufferCreateInfo bi = {
      .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
      .size = bytes,
      .usage = usage,
      .sharingMode = VK_SHARING_MODE_EXCLUSIVE,
  };
  if (vkCreateBuffer(g.device, &bi, NULL, out_buf) != VK_SUCCESS)
    return false;
  VkMemoryRequirements mr;
  vkGetBufferMemoryRequirements(g.device, *out_buf, &mr);
  uint32_t idx;
  if (!vkb_find_mem_type(mr.memoryTypeBits, props, &idx)) {
    vkDestroyBuffer(g.device, *out_buf, NULL);
    *out_buf = VK_NULL_HANDLE;
    return false;
  }
  VkMemoryAllocateInfo ai = {
      .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
      .allocationSize = mr.size,
      .memoryTypeIndex = idx,
  };
  if (vkAllocateMemory(g.device, &ai, NULL, out_mem) != VK_SUCCESS ||
      vkBindBufferMemory(g.device, *out_buf, *out_mem, 0) != VK_SUCCESS) {
    vkDestroyBuffer(g.device, *out_buf, NULL);
    *out_buf = VK_NULL_HANDLE;
    if (*out_mem) {
      vkFreeMemory(g.device, *out_mem, NULL);
      *out_mem = VK_NULL_HANDLE;
    }
    return false;
  }
  return true;
}

// --- timeline fence
// -----------------------------------------------------------

static void vkb_wait_for_fence(uint64_t value) {
  if (value == 0)
    return;
  uint64_t done = 0;
  vkGetSemaphoreCounterValue(g.device, g.timeline, &done);
  if (done >= value)
    return;
  VkSemaphoreWaitInfo wi = {
      .sType = VK_STRUCTURE_TYPE_SEMAPHORE_WAIT_INFO,
      .semaphoreCount = 1,
      .pSemaphores = &g.timeline,
      .pValues = &value,
  };
  vkWaitSemaphores(g.device, &wi, UINT64_MAX);
}

static void vkb_wait_idle(void) {
  if (g.queue)
    vkQueueWaitIdle(g.queue);
}

// Submit `cmd` signaling the timeline (and optionally the present semaphore
// for the current backbuffer). The first submission of a frame waits on the
// acquire semaphore so swapchain writes are ordered after the acquire.
static uint64_t vkb_submit_cmd(VkCommandBuffer cmd, bool signal_present) {
  VkSemaphoreSubmitInfo waits[1];
  uint32_t n_waits = 0;
  if (g.have_acquired && !g.acquire_waited) {
    waits[0] = (VkSemaphoreSubmitInfo){
        .sType = VK_STRUCTURE_TYPE_SEMAPHORE_SUBMIT_INFO,
        .semaphore = g.frames[g.slot].acquire_sem,
        .stageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
    };
    n_waits = 1;
    g.acquire_waited = true;
  }
  uint64_t value = g.fence_next++;
  VkSemaphoreSubmitInfo signals[2] = {
      {
          .sType = VK_STRUCTURE_TYPE_SEMAPHORE_SUBMIT_INFO,
          .semaphore = g.timeline,
          .value = value,
          .stageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
      },
  };
  uint32_t n_signals = 1;
  if (signal_present) {
    signals[1] = (VkSemaphoreSubmitInfo){
        .sType = VK_STRUCTURE_TYPE_SEMAPHORE_SUBMIT_INFO,
        .semaphore = g.render_done_sems[g.bb_index],
        .stageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
    };
    n_signals = 2;
  }
  VkCommandBufferSubmitInfo ci = {
      .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_SUBMIT_INFO,
      .commandBuffer = cmd,
  };
  VkSubmitInfo2 si = {
      .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO_2,
      .waitSemaphoreInfoCount = n_waits,
      .pWaitSemaphoreInfos = waits,
      .commandBufferInfoCount = 1,
      .pCommandBufferInfos = &ci,
      .signalSemaphoreInfoCount = n_signals,
      .pSignalSemaphoreInfos = signals,
  };
  if (vkQueueSubmit2(g.queue, 1, &si, VK_NULL_HANDLE) != VK_SUCCESS)
    SDL_Log("vk: vkQueueSubmit2 failed");
  g.last_submit_value = value;
  return value;
}

// --- deferred destruction
// ------------------------------------------------------ The GPU may still be
// reading a resource for up to kFramesInFlight frames. Park handles with the
// timeline value that retires them; begin_frame drains.
typedef struct Zombie {
  uint64_t fence_value;
  VkBuffer buf;
  VkDeviceMemory mem;
  VkImage img;
  VkImageView view;
  VkImageView view2;
  VkSampler sampler;
  VkPipeline pipe;
  VkPipelineLayout layout;
  VkDescriptorSetLayout dsl[4];
  VkShaderModule mods[3];
} Zombie;

static Zombie *g_zombies = NULL;
static size_t g_zombie_count = 0, g_zombie_cap = 0;

static void vkb_zombie_push(const Zombie *z) {
  if (g_zombie_count == g_zombie_cap) {
    g_zombie_cap = g_zombie_cap ? g_zombie_cap * 2 : 64;
    g_zombies = (Zombie *)realloc(g_zombies, g_zombie_cap * sizeof(Zombie));
  }
  g_zombies[g_zombie_count] = *z;
  g_zombies[g_zombie_count].fence_value = g.fence_next; // next signal retires
  g_zombie_count++;
}

static void vkb_zombie_free(const Zombie *z) {
  if (z->pipe)
    vkDestroyPipeline(g.device, z->pipe, NULL);
  if (z->layout)
    vkDestroyPipelineLayout(g.device, z->layout, NULL);
  for (int i = 0; i < 4; ++i)
    if (z->dsl[i])
      vkDestroyDescriptorSetLayout(g.device, z->dsl[i], NULL);
  for (int i = 0; i < 3; ++i)
    if (z->mods[i])
      vkDestroyShaderModule(g.device, z->mods[i], NULL);
  if (z->view)
    vkDestroyImageView(g.device, z->view, NULL);
  if (z->view2)
    vkDestroyImageView(g.device, z->view2, NULL);
  if (z->sampler)
    vkDestroySampler(g.device, z->sampler, NULL);
  if (z->img)
    vkDestroyImage(g.device, z->img, NULL);
  if (z->buf)
    vkDestroyBuffer(g.device, z->buf, NULL);
  if (z->mem)
    vkFreeMemory(g.device, z->mem, NULL);
}

static void vkb_drain_zombies(void) {
  uint64_t done = 0;
  vkGetSemaphoreCounterValue(g.device, g.timeline, &done);
  size_t w = 0;
  for (size_t i = 0; i < g_zombie_count; ++i) {
    if (g_zombies[i].fence_value > done)
      g_zombies[w++] = g_zombies[i];
    else
      vkb_zombie_free(&g_zombies[i]);
  }
  g_zombie_count = w;
}

static void vkb_free_zombies_now(void) {
  for (size_t i = 0; i < g_zombie_count; ++i)
    vkb_zombie_free(&g_zombies[i]);
  g_zombie_count = 0;
}

// --- upload arena impl
// ----------------------------------------------------------

static void vkb_arena_reset(Arena *a) {
  for (int i = 1; i < a->count; ++i) {
    vkDestroyBuffer(g.device, a->chunks[i].buf, NULL);
    vkFreeMemory(g.device, a->chunks[i].mem, NULL);
  }
  if (a->count > 1)
    a->count = 1;
  if (a->count > 0)
    a->chunks[0].off = 0;
}

static void vkb_arena_release_all(Arena *a) {
  for (int i = 0; i < a->count; ++i) {
    vkDestroyBuffer(g.device, a->chunks[i].buf, NULL);
    vkFreeMemory(g.device, a->chunks[i].mem, NULL);
  }
  a->count = 0;
}

// Allocate transient upload memory valid until this frame slot's fence.
static bool vkb_upload_alloc(size_t bytes, size_t align, UploadAlloc *out) {
  Arena *a = &g.frames[g.slot].arena;
  for (int i = 0; i < a->count; ++i) {
    ArenaChunk *c = &a->chunks[i];
    size_t off = (c->off + align - 1) & ~(align - 1);
    if (off + bytes <= c->cap) {
      c->off = off + bytes;
      out->buf = c->buf;
      out->offset = off;
      out->cpu = c->map + off;
      return true;
    }
  }
  if (a->count == KARENA_CHUNK_CAP) {
    SDL_Log("vk: upload arena chunk cap exceeded");
    return false;
  }
  size_t cap = bytes > KARENA_BASE_CHUNK ? bytes : KARENA_BASE_CHUNK;
  cap = (cap + 65535) & ~(size_t)65535;
  ArenaChunk c = {0};
  if (!vkb_alloc_buffer(cap,
                        VK_BUFFER_USAGE_TRANSFER_SRC_BIT |
                            VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,
                        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                            VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,
                        &c.buf, &c.mem)) {
    SDL_Log("vk: upload chunk alloc failed (%zu bytes)", cap);
    return false;
  }
  if (vkMapMemory(g.device, c.mem, 0, VK_WHOLE_SIZE, 0, (void **)&c.map) !=
      VK_SUCCESS) {
    SDL_Log("vk: upload chunk map failed");
    vkDestroyBuffer(g.device, c.buf, NULL);
    vkFreeMemory(g.device, c.mem, NULL);
    return false;
  }
  c.cap = cap;
  c.off = bytes;
  a->chunks[a->count++] = c;
  ArenaChunk *back = &a->chunks[a->count - 1];
  out->buf = back->buf;
  out->offset = 0;
  out->cpu = back->map;
  return true;
}

// --- pass suspend/resume
// -------------------------------------------------------- D3D12 lets copies
// and barriers record mid-"pass"; Vulkan forbids them inside a dynamic
// rendering scope. Any such request suspends rendering and the calling vtable
// op resumes it (LOAD ops, state carries over).

static void vkb_pass_suspend(void) {
  if (!g.in_pass || g.pass_suspended)
    return;
  vkCmdEndRendering(g.frames[g.slot].cmd);
  g.pass_suspended = true;
}

static void vkb_begin_rendering(bool resume) {
  VkRenderingAttachmentInfo colors[SGL_MAX_COLOR_TARGETS];
  VkRenderingAttachmentInfo depth;
  for (int i = 0; i < g.pass_n_colors; ++i) {
    colors[i] = g.pass_colors[i];
    if (resume)
      colors[i].loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
  }
  depth = g.pass_depth;
  if (resume)
    depth.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
  VkRenderingInfo ri = {
      .sType = VK_STRUCTURE_TYPE_RENDERING_INFO,
      .renderArea = {{0, 0}, {(uint32_t)g.pass_w, (uint32_t)g.pass_h}},
      .layerCount = 1,
      .colorAttachmentCount = (uint32_t)g.pass_n_colors,
      .pColorAttachments = g.pass_n_colors > 0 ? colors : NULL,
      .pDepthAttachment = g.pass_has_depth ? &depth : NULL,
      .pStencilAttachment = NULL,
  };
  vkCmdBeginRendering(g.frames[g.slot].cmd, &ri);
}

static void vkb_pass_resume(void) {
  if (!g.in_pass || !g.pass_suspended)
    return;
  vkb_begin_rendering(/*resume=*/true);
  g.pass_suspended = false;
}

// --- one-shot submit (resource work outside a frame)
// -----------------------------

typedef struct OneShot {
  VkCommandBuffer cmd;
  bool ok;
} OneShot;

static OneShot vkb_oneshot_begin(void) {
  OneShot one = {0};
  VkCommandBufferAllocateInfo ai = {
      .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
      .commandPool = g.oneshot_pool,
      .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
      .commandBufferCount = 1,
  };
  if (vkAllocateCommandBuffers(g.device, &ai, &one.cmd) != VK_SUCCESS)
    return one;
  VkCommandBufferBeginInfo bi = {
      .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
      .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
  };
  if (vkBeginCommandBuffer(one.cmd, &bi) != VK_SUCCESS) {
    vkFreeCommandBuffers(g.device, g.oneshot_pool, 1, &one.cmd);
    one.cmd = VK_NULL_HANDLE;
    return one;
  }
  one.ok = true;
  return one;
}

static void vkb_oneshot_submit_and_wait(OneShot *one) {
  vkEndCommandBuffer(one->cmd);
  vkb_submit_cmd(one->cmd, /*signal_present=*/false);
  vkb_wait_idle();
  vkFreeCommandBuffers(g.device, g.oneshot_pool, 1, &one->cmd);
  one->cmd = VK_NULL_HANDLE;
}

// Recording context for copies/barriers: the open frame command buffer
// (suspending the pass if one is active) or a one-shot buffer outside frames.
// Callers pair vkb_record_begin/vkb_record_end around the copy.
typedef struct RecordCtx {
  VkCommandBuffer cmd;
  OneShot one;
  bool used_oneshot;
  bool ok;
} RecordCtx;

static RecordCtx vkb_record_begin(void) {
  RecordCtx ctx = {0};
  if (g.recording) {
    vkb_pass_suspend();
    ctx.cmd = g.frames[g.slot].cmd;
    ctx.ok = true;
    return ctx;
  }
  ctx.one = vkb_oneshot_begin();
  ctx.used_oneshot = true;
  ctx.cmd = ctx.one.cmd;
  ctx.ok = ctx.one.ok;
  return ctx;
}

static void vkb_record_end(RecordCtx *ctx) {
  if (!ctx->ok)
    return;
  if (ctx->used_oneshot)
    vkb_oneshot_submit_and_wait(&ctx->one);
  else
    vkb_pass_resume();
}

// --- barriers
// ---------------------------------------------------------------------

// Coarse global memory barrier: orders every prior write against every
// subsequent access. Per-resource precision isn't worth the bookkeeping at
// lub's draw-call scale.
static void vkb_memory_barrier(VkCommandBuffer cmd) {
  VkMemoryBarrier2 mb = {
      .sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER_2,
      .srcStageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
      .srcAccessMask = VK_ACCESS_2_MEMORY_WRITE_BIT,
      .dstStageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
      .dstAccessMask =
          VK_ACCESS_2_MEMORY_READ_BIT | VK_ACCESS_2_MEMORY_WRITE_BIT,
  };
  VkDependencyInfo di = {
      .sType = VK_STRUCTURE_TYPE_DEPENDENCY_INFO,
      .memoryBarrierCount = 1,
      .pMemoryBarriers = &mb,
  };
  vkCmdPipelineBarrier2(cmd, &di);
}

static void vkb_image_barrier(VkCommandBuffer cmd, VkImage img,
                              VkImageAspectFlags aspect, VkImageLayout from,
                              VkImageLayout to) {
  VkImageMemoryBarrier2 ib = {
      .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER_2,
      .srcStageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
      .srcAccessMask = VK_ACCESS_2_MEMORY_WRITE_BIT,
      .dstStageMask = VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT,
      .dstAccessMask =
          VK_ACCESS_2_MEMORY_READ_BIT | VK_ACCESS_2_MEMORY_WRITE_BIT,
      .oldLayout = from,
      .newLayout = to,
      .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
      .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
      .image = img,
      .subresourceRange = {aspect, 0, 1, 0, 1},
  };
  VkDependencyInfo di = {
      .sType = VK_STRUCTURE_TYPE_DEPENDENCY_INFO,
      .imageMemoryBarrierCount = 1,
      .pImageMemoryBarriers = &ib,
  };
  vkCmdPipelineBarrier2(cmd, &di);
}

// Transition a tracked image to `to` on the current recording context.
// Suspends an active pass; the calling vtable op resumes it (or use the
// RecordCtx variant when already inside one).
static void vkb_transition(VkbImage *im, VkImageLayout to) {
  if (!im || !im->img || im->layout == to)
    return;
  RecordCtx ctx = vkb_record_begin();
  if (!ctx.ok)
    return;
  vkb_image_barrier(ctx.cmd, im->img, vkb_aspect(im->vkfmt, false), im->layout,
                    to);
  im->layout = to;
  if (ctx.used_oneshot)
    vkb_record_end(&ctx);
  // frame-recording case: leave the pass suspended, caller resumes once.
}

// --- swapchain / default depth
// ---------------------------------------------------

static void vkb_destroy_default_depth(void) {
  if (g.depth_view)
    vkDestroyImageView(g.device, g.depth_view, NULL);
  if (g.depth_img)
    vkDestroyImage(g.device, g.depth_img, NULL);
  if (g.depth_mem)
    vkFreeMemory(g.device, g.depth_mem, NULL);
  g.depth_view = VK_NULL_HANDLE;
  g.depth_img = VK_NULL_HANDLE;
  g.depth_mem = VK_NULL_HANDLE;
  g.depth_w = g.depth_h = 0;
}

static bool vkb_ensure_default_depth(int w, int h) {
  if (g.depth_img && g.depth_w == w && g.depth_h == h)
    return true;
  vkb_destroy_default_depth();

  VkImageCreateInfo ii = {
      .sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
      .imageType = VK_IMAGE_TYPE_2D,
      .format = g.depth24_fmt,
      .extent = {(uint32_t)w, (uint32_t)h, 1},
      .mipLevels = 1,
      .arrayLayers = 1,
      .samples = VK_SAMPLE_COUNT_1_BIT,
      .tiling = VK_IMAGE_TILING_OPTIMAL,
      .usage = VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT,
      .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
  };
  if (vkCreateImage(g.device, &ii, NULL, &g.depth_img) != VK_SUCCESS) {
    SDL_Log("vk: default depth create failed (%dx%d)", w, h);
    return false;
  }
  VkMemoryRequirements mr;
  vkGetImageMemoryRequirements(g.device, g.depth_img, &mr);
  uint32_t idx;
  if (!vkb_find_mem_type(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT,
                         &idx)) {
    SDL_Log("vk: default depth: no memory type");
    return false;
  }
  VkMemoryAllocateInfo ai = {
      .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
      .allocationSize = mr.size,
      .memoryTypeIndex = idx,
  };
  if (vkAllocateMemory(g.device, &ai, NULL, &g.depth_mem) != VK_SUCCESS ||
      vkBindImageMemory(g.device, g.depth_img, g.depth_mem, 0) != VK_SUCCESS) {
    SDL_Log("vk: default depth: alloc/bind failed");
    return false;
  }
  VkImageViewCreateInfo vi = {
      .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
      .image = g.depth_img,
      .viewType = VK_IMAGE_VIEW_TYPE_2D,
      .format = g.depth24_fmt,
      .subresourceRange = {vkb_aspect(g.depth24_fmt, false), 0, 1, 0, 1},
  };
  if (vkCreateImageView(g.device, &vi, NULL, &g.depth_view) != VK_SUCCESS) {
    SDL_Log("vk: default depth: view create failed");
    return false;
  }
  g.depth_layout = VK_IMAGE_LAYOUT_UNDEFINED;
  g.depth_w = w;
  g.depth_h = h;
  return true;
}

static void vkb_destroy_swapchain_views(void) {
  for (uint32_t i = 0; i < g.sc_count; ++i) {
    if (g.sc_views[i])
      vkDestroyImageView(g.device, g.sc_views[i], NULL);
    g.sc_views[i] = VK_NULL_HANDLE;
    if (g.render_done_sems[i])
      vkDestroySemaphore(g.device, g.render_done_sems[i], NULL);
    g.render_done_sems[i] = VK_NULL_HANDLE;
  }
  g.sc_count = 0;
}

static bool vkb_create_swapchain(void) {
  VkSurfaceCapabilitiesKHR caps;
  if (vkGetPhysicalDeviceSurfaceCapabilitiesKHR(g.phys, g.surface, &caps) !=
      VK_SUCCESS) {
    SDL_Log("vk: surface caps query failed");
    return false;
  }
  VkExtent2D extent = caps.currentExtent;
  if (extent.width == 0xFFFFFFFFu) {
    int w = 0, h = 0;
    SDL_GetWindowSizeInPixels(g.app->window, &w, &h);
    extent.width = (uint32_t)w;
    extent.height = (uint32_t)h;
  }
  if (extent.width == 0 || extent.height == 0)
    return false; // minimized; retry next frame

  // Pick a format: prefer RGBA8 (matches the other backends' goldens),
  // else BGRA8, else whatever the surface offers first. Only SRGB_NONLINEAR
  // colorspace entries qualify — anything else needs
  // VK_EXT_swapchain_colorspace, which we don't enable.
  uint32_t nfmt = 0;
  vkGetPhysicalDeviceSurfaceFormatsKHR(g.phys, g.surface, &nfmt, NULL);
  VkSurfaceFormatKHR fmts[64];
  if (nfmt > 64)
    nfmt = 64;
  vkGetPhysicalDeviceSurfaceFormatsKHR(g.phys, g.surface, &nfmt, fmts);
  VkSurfaceFormatKHR chosen = fmts[0];
  bool have_chosen = false;
  for (uint32_t i = 0; i < nfmt; ++i) {
    if (fmts[i].colorSpace != VK_COLOR_SPACE_SRGB_NONLINEAR_KHR)
      continue;
    if (!have_chosen) {
      chosen = fmts[i];
      have_chosen = true;
    }
    if (fmts[i].format == VK_FORMAT_B8G8R8A8_UNORM &&
        chosen.format != VK_FORMAT_R8G8B8A8_UNORM)
      chosen = fmts[i];
    if (fmts[i].format == VK_FORMAT_R8G8B8A8_UNORM)
      chosen = fmts[i];
  }
  g.sc_format = chosen.format;
  g.sc_fmt_sgl =
      (chosen.format == VK_FORMAT_B8G8R8A8_UNORM) ? SGL_PF_BGRA8 : SGL_PF_RGBA8;

  uint32_t count = caps.minImageCount + 1;
  if (caps.maxImageCount > 0 && count > caps.maxImageCount)
    count = caps.maxImageCount;

  VkImageUsageFlags usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
  if (caps.supportedUsageFlags & VK_IMAGE_USAGE_TRANSFER_SRC_BIT)
    usage |= VK_IMAGE_USAGE_TRANSFER_SRC_BIT; // --capture reads it back

  VkSwapchainKHR old = g.swapchain;
  VkSwapchainCreateInfoKHR sci = {
      .sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR,
      .surface = g.surface,
      .minImageCount = count,
      .imageFormat = g.sc_format,
      .imageColorSpace = chosen.colorSpace,
      .imageExtent = extent,
      .imageArrayLayers = 1,
      .imageUsage = usage,
      .imageSharingMode = VK_SHARING_MODE_EXCLUSIVE,
      .preTransform = caps.currentTransform,
      .compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
      .presentMode = VK_PRESENT_MODE_FIFO_KHR,
      .clipped = VK_TRUE,
      .oldSwapchain = old,
  };
  VkSwapchainKHR sc;
  if (vkCreateSwapchainKHR(g.device, &sci, NULL, &sc) != VK_SUCCESS) {
    SDL_Log("vk: vkCreateSwapchainKHR failed");
    return false;
  }
  vkb_destroy_swapchain_views();
  if (old)
    vkDestroySwapchainKHR(g.device, old, NULL);
  g.swapchain = sc;

  uint32_t n = 0;
  vkGetSwapchainImagesKHR(g.device, g.swapchain, &n, NULL);
  if (n > KMAX_SWAPCHAIN_IMAGES) {
    SDL_Log("vk: too many swapchain images (%u)", n);
    return false;
  }
  vkGetSwapchainImagesKHR(g.device, g.swapchain, &n, g.sc_images);
  g.sc_count = n;
  for (uint32_t i = 0; i < n; ++i) {
    VkImageViewCreateInfo vi = {
        .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
        .image = g.sc_images[i],
        .viewType = VK_IMAGE_VIEW_TYPE_2D,
        .format = g.sc_format,
        .subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1},
    };
    if (vkCreateImageView(g.device, &vi, NULL, &g.sc_views[i]) != VK_SUCCESS) {
      SDL_Log("vk: swapchain view create failed");
      return false;
    }
    g.sc_layouts[i] = VK_IMAGE_LAYOUT_UNDEFINED;
    VkSemaphoreCreateInfo si = {.sType =
                                    VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
    if (vkCreateSemaphore(g.device, &si, NULL, &g.render_done_sems[i]) !=
        VK_SUCCESS) {
      SDL_Log("vk: render-done semaphore create failed");
      return false;
    }
  }
  g.sw_w = (int)extent.width;
  g.sw_h = (int)extent.height;
  return vkb_ensure_default_depth(g.sw_w, g.sw_h);
}

static bool vkb_recreate_swapchain(void) {
  vkb_wait_idle();
  return vkb_create_swapchain();
}

// --- vtable: lifecycle
// -------------------------------------------------------------

static VKAPI_ATTR VkBool32 VKAPI_CALL
vkb_debug_cb(VkDebugUtilsMessageSeverityFlagBitsEXT severity,
             VkDebugUtilsMessageTypeFlagsEXT types,
             const VkDebugUtilsMessengerCallbackDataEXT *data, void *user) {
  (void)severity;
  (void)types;
  (void)user;
  SDL_Log("vk[validation]: %s", data->pMessage);
  return VK_FALSE;
}

static bool vkb_pick_device(void) {
  uint32_t n = 0;
  vkEnumeratePhysicalDevices(g.instance, &n, NULL);
  if (n == 0) {
    SDL_Log("vk: no physical devices");
    return false;
  }
  VkPhysicalDevice devs[16];
  if (n > 16)
    n = 16;
  vkEnumeratePhysicalDevices(g.instance, &n, devs);

  int best_score = -1;
  for (uint32_t i = 0; i < n; ++i) {
    VkPhysicalDeviceProperties props;
    vkGetPhysicalDeviceProperties(devs[i], &props);
    if (props.apiVersion < VK_API_VERSION_1_3)
      continue;
    VkPhysicalDeviceVulkan13Features f13 = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
    VkPhysicalDeviceVulkan12Features f12 = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES,
        .pNext = &f13};
    VkPhysicalDeviceFeatures2 f2 = {
        .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2, .pNext = &f12};
    vkGetPhysicalDeviceFeatures2(devs[i], &f2);
    if (!f12.timelineSemaphore || !f13.dynamicRendering ||
        !f13.synchronization2)
      continue;

    // Need one family with graphics + present.
    uint32_t nq = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(devs[i], &nq, NULL);
    VkQueueFamilyProperties qs[32];
    if (nq > 32)
      nq = 32;
    vkGetPhysicalDeviceQueueFamilyProperties(devs[i], &nq, qs);
    int fam = -1;
    for (uint32_t q = 0; q < nq; ++q) {
      if (!(qs[q].queueFlags & VK_QUEUE_GRAPHICS_BIT))
        continue;
      VkBool32 present = VK_FALSE;
      vkGetPhysicalDeviceSurfaceSupportKHR(devs[i], q, g.surface, &present);
      if (present) {
        fam = (int)q;
        break;
      }
    }
    if (fam < 0)
      continue;

    int score = 0;
    switch (props.deviceType) {
    case VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU:
      score = 3;
      break;
    case VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU:
      score = 2;
      break;
    default:
      score = 1;
      break;
    }
    if (score > best_score) {
      best_score = score;
      g.phys = devs[i];
      g.qfam = (uint32_t)fam;
    }
  }
  if (best_score < 0) {
    SDL_Log("vk: no device with Vulkan 1.3 + dynamicRendering + "
            "synchronization2 + timelineSemaphore + present support");
    return false;
  }
  VkPhysicalDeviceProperties props;
  vkGetPhysicalDeviceProperties(g.phys, &props);
  SDL_Log("vk: adapter: %s", props.deviceName);
  g.ub_align = props.limits.minUniformBufferOffsetAlignment;
  if (g.ub_align < 16)
    g.ub_align = 16;
  return true;
}

static bool vkb_make_dummies(void);

static bool vkb_init(App *app) {
  g.app = app;

  bool want_debug = getenv("LUB_VK_DEBUG") != NULL;
#if !defined(NDEBUG)
  want_debug = true;
#endif
  // Validation layer only when it's actually installed.
  bool have_validation = false;
  if (want_debug) {
    uint32_t nl = 0;
    vkEnumerateInstanceLayerProperties(&nl, NULL);
    VkLayerProperties layers[64];
    if (nl > 64)
      nl = 64;
    vkEnumerateInstanceLayerProperties(&nl, layers);
    for (uint32_t i = 0; i < nl; ++i) {
      if (strcmp(layers[i].layerName, "VK_LAYER_KHRONOS_validation") == 0)
        have_validation = true;
    }
  }

  Uint32 n_sdl_ext = 0;
  const char *const *sdl_exts = SDL_Vulkan_GetInstanceExtensions(&n_sdl_ext);
  if (!sdl_exts) {
    SDL_Log("vk: SDL_Vulkan_GetInstanceExtensions failed: %s", SDL_GetError());
    return false;
  }
  const char *exts[16];
  uint32_t n_exts = 0;
  for (Uint32 i = 0; i < n_sdl_ext && n_exts < 15; ++i)
    exts[n_exts++] = sdl_exts[i];
  if (have_validation)
    exts[n_exts++] = VK_EXT_DEBUG_UTILS_EXTENSION_NAME;

  const char *layers[1] = {"VK_LAYER_KHRONOS_validation"};
  VkApplicationInfo appi = {
      .sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
      .pApplicationName = "lub",
      .apiVersion = VK_API_VERSION_1_3,
  };
  VkInstanceCreateInfo ici = {
      .sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
      .pApplicationInfo = &appi,
      .enabledLayerCount = have_validation ? 1u : 0u,
      .ppEnabledLayerNames = layers,
      .enabledExtensionCount = n_exts,
      .ppEnabledExtensionNames = exts,
  };
  if (vkCreateInstance(&ici, NULL, &g.instance) != VK_SUCCESS) {
    SDL_Log("vk: vkCreateInstance failed");
    return false;
  }
  if (have_validation) {
    PFN_vkCreateDebugUtilsMessengerEXT create =
        (PFN_vkCreateDebugUtilsMessengerEXT)vkGetInstanceProcAddr(
            g.instance, "vkCreateDebugUtilsMessengerEXT");
    if (create) {
      VkDebugUtilsMessengerCreateInfoEXT mi = {
          .sType = VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT,
          .messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT |
                             VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT,
          .messageType = VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT |
                         VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT,
          .pfnUserCallback = vkb_debug_cb,
      };
      create(g.instance, &mi, NULL, &g.messenger);
      SDL_Log("vk: validation layer enabled");
    }
  }

  if (!SDL_Vulkan_CreateSurface(app->window, g.instance, NULL, &g.surface)) {
    SDL_Log("vk: SDL_Vulkan_CreateSurface failed: %s", SDL_GetError());
    return false;
  }

  if (!vkb_pick_device())
    return false;

  float prio = 1.0f;
  VkDeviceQueueCreateInfo qci = {
      .sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
      .queueFamilyIndex = g.qfam,
      .queueCount = 1,
      .pQueuePriorities = &prio,
  };
  VkPhysicalDeviceVulkan13Features f13 = {
      .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES,
      .dynamicRendering = VK_TRUE,
      .synchronization2 = VK_TRUE,
  };
  VkPhysicalDeviceVulkan12Features f12 = {
      .sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES,
      .pNext = &f13,
      .timelineSemaphore = VK_TRUE,
  };
  const char *dev_exts[1] = {VK_KHR_SWAPCHAIN_EXTENSION_NAME};
  VkDeviceCreateInfo dci = {
      .sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
      .pNext = &f12,
      .queueCreateInfoCount = 1,
      .pQueueCreateInfos = &qci,
      .enabledExtensionCount = 1,
      .ppEnabledExtensionNames = dev_exts,
  };
  if (vkCreateDevice(g.phys, &dci, NULL, &g.device) != VK_SUCCESS) {
    SDL_Log("vk: vkCreateDevice failed");
    return false;
  }
  vkGetDeviceQueue(g.device, g.qfam, 0, &g.queue);

  // D24S8 where supported (matches the d3d12 default), else D32S8.
  {
    VkFormatProperties fp;
    vkGetPhysicalDeviceFormatProperties(g.phys, VK_FORMAT_D24_UNORM_S8_UINT,
                                        &fp);
    g.depth24_fmt = (fp.optimalTilingFeatures &
                     VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT)
                        ? VK_FORMAT_D24_UNORM_S8_UINT
                        : VK_FORMAT_D32_SFLOAT_S8_UINT;
  }

  VkSemaphoreTypeCreateInfo tci = {
      .sType = VK_STRUCTURE_TYPE_SEMAPHORE_TYPE_CREATE_INFO,
      .semaphoreType = VK_SEMAPHORE_TYPE_TIMELINE,
      .initialValue = 0,
  };
  VkSemaphoreCreateInfo sci = {.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO,
                               .pNext = &tci};
  if (vkCreateSemaphore(g.device, &sci, NULL, &g.timeline) != VK_SUCCESS) {
    SDL_Log("vk: timeline semaphore create failed");
    return false;
  }
  g.fence_next = 1;

  for (int i = 0; i < KFRAMES_IN_FLIGHT; ++i) {
    FrameCtx *f = &g.frames[i];
    VkCommandPoolCreateInfo pci = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
        .flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
        .queueFamilyIndex = g.qfam,
    };
    if (vkCreateCommandPool(g.device, &pci, NULL, &f->pool) != VK_SUCCESS) {
      SDL_Log("vk: command pool create failed");
      return false;
    }
    VkCommandBufferAllocateInfo cai = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
        .commandPool = f->pool,
        .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY,
        .commandBufferCount = 1,
    };
    if (vkAllocateCommandBuffers(g.device, &cai, &f->cmd) != VK_SUCCESS) {
      SDL_Log("vk: command buffer alloc failed");
      return false;
    }
    VkSemaphoreCreateInfo asi = {.sType =
                                     VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
    if (vkCreateSemaphore(g.device, &asi, NULL, &f->acquire_sem) !=
        VK_SUCCESS) {
      SDL_Log("vk: acquire semaphore create failed");
      return false;
    }
  }
  {
    VkCommandPoolCreateInfo pci = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
        .flags = VK_COMMAND_POOL_CREATE_TRANSIENT_BIT,
        .queueFamilyIndex = g.qfam,
    };
    if (vkCreateCommandPool(g.device, &pci, NULL, &g.oneshot_pool) !=
        VK_SUCCESS) {
      SDL_Log("vk: one-shot command pool create failed");
      return false;
    }
  }

  if (!vkb_create_swapchain())
    return false;
  if (!vkb_make_dummies())
    return false;

  SDL_Log("vk: initialized (%dx%d, %d frames in flight, %s)", g.sw_w, g.sw_h,
          KFRAMES_IN_FLIGHT, g.sc_fmt_sgl == SGL_PF_BGRA8 ? "bgra8" : "rgba8");
  return true;
}

// forward decls used by shutdown/dummies
static void vkb_destroy_buffer(BackendBuffer h);
static void vkb_destroy_image(BackendImage h);
static BackendBuffer vkb_make_buffer(SglBufferType type, const void *data,
                                     size_t bytes);
static BackendImage vkb_make_image(const ImageDesc *desc);

static bool vkb_make_dummies(void) {
  static const uint8_t white[4] = {255, 255, 255, 255};
  ImageDesc id = {
      .fmt = SGL_PF_RGBA8,
      .w = 1,
      .h = 1,
      .data = white,
      .data_bytes = 4,
  };
  g.dummy_tex = (VkbImage *)vkb_make_image(&id);
  ImageDesc sd = {
      .fmt = SGL_PF_RGBA8,
      .w = 1,
      .h = 1,
      .storage = true,
  };
  g.dummy_storage = (VkbImage *)vkb_make_image(&sd);
  if (g.dummy_storage)
    vkb_transition(g.dummy_storage, VK_IMAGE_LAYOUT_GENERAL);
  static const uint8_t zeros[256] = {0};
  g.dummy_ubuf =
      (VkbBuffer *)vkb_make_buffer(SGL_BUFFER_UNIFORM, zeros, sizeof(zeros));
  g.dummy_sbuf =
      (VkbBuffer *)vkb_make_buffer(SGL_BUFFER_STORAGE, zeros, sizeof(zeros));
  if (!g.dummy_tex || !g.dummy_storage || !g.dummy_ubuf || !g.dummy_sbuf) {
    SDL_Log("vk: dummy resource creation failed");
    return false;
  }
  return true;
}

static void vkb_shutdown(App *app) {
  (void)app;
  vkb_wait_idle();
  if (g.dummy_tex)
    vkb_destroy_image((BackendImage)g.dummy_tex);
  if (g.dummy_storage)
    vkb_destroy_image((BackendImage)g.dummy_storage);
  if (g.dummy_ubuf)
    vkb_destroy_buffer((BackendBuffer)g.dummy_ubuf);
  if (g.dummy_sbuf)
    vkb_destroy_buffer((BackendBuffer)g.dummy_sbuf);
  vkb_free_zombies_now();
  free(g_zombies);
  g_zombies = NULL;
  g_zombie_count = g_zombie_cap = 0;
  for (int i = 0; i < KFRAMES_IN_FLIGHT; ++i) {
    FrameCtx *f = &g.frames[i];
    vkb_arena_release_all(&f->arena);
    for (int p = 0; p < f->desc_pool_count; ++p)
      vkDestroyDescriptorPool(g.device, f->desc_pools[p], NULL);
    if (f->acquire_sem)
      vkDestroySemaphore(g.device, f->acquire_sem, NULL);
    if (f->pool)
      vkDestroyCommandPool(g.device, f->pool, NULL);
  }
  if (g.oneshot_pool)
    vkDestroyCommandPool(g.device, g.oneshot_pool, NULL);
  vkb_destroy_default_depth();
  vkb_destroy_swapchain_views();
  if (g.swapchain)
    vkDestroySwapchainKHR(g.device, g.swapchain, NULL);
  if (g.timeline)
    vkDestroySemaphore(g.device, g.timeline, NULL);
  if (g.device)
    vkDestroyDevice(g.device, NULL);
  if (g.surface)
    vkDestroySurfaceKHR(g.instance, g.surface, NULL);
  if (g.messenger) {
    PFN_vkDestroyDebugUtilsMessengerEXT destroy =
        (PFN_vkDestroyDebugUtilsMessengerEXT)vkGetInstanceProcAddr(
            g.instance, "vkDestroyDebugUtilsMessengerEXT");
    if (destroy)
      destroy(g.instance, g.messenger, NULL);
  }
  if (g.instance)
    vkDestroyInstance(g.instance, NULL);
  memset(&g, 0, sizeof(g));
  g_current_pip = NULL;
  g_last_indexed = false;
  memset(g_uniforms, 0, sizeof(g_uniforms));
}

// --- vtable: frame
// -----------------------------------------------------------------

static void vkb_begin_frame(App *app, int *out_w, int *out_h) {
  if (app->pending_resize || g.need_recreate) {
    app->pending_resize = false;
    g.need_recreate = false;
    if (!vkb_recreate_swapchain())
      g.need_recreate = true; // minimized etc.; retry next frame
  }

  FrameCtx *f = &g.frames[g.slot];
  vkb_wait_for_fence(f->fence_value);
  vkb_drain_zombies();
  vkb_arena_reset(&f->arena);
  for (int p = 0; p < f->desc_pool_count; ++p)
    vkResetDescriptorPool(g.device, f->desc_pools[p], 0);
  f->desc_pool_cur = 0;

  vkResetCommandBuffer(f->cmd, 0);
  VkCommandBufferBeginInfo bi = {
      .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
      .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
  };
  vkBeginCommandBuffer(f->cmd, &bi);
  g.recording = true;
  g.acquire_waited = false;
  g.have_acquired = false;
  g.submitted_before_present = false;
  g_current_pip = NULL;
  g_uniforms_dirty[0] = g_uniforms_dirty[1] = true;
  // Cached uniform allocations point into the previous frame's arena;
  // callers re-apply uniforms every draw, so just invalidate.
  memset(g_uniforms, 0, sizeof(g_uniforms));

  if (g.swapchain && !g.need_recreate) {
    VkResult r =
        vkAcquireNextImageKHR(g.device, g.swapchain, UINT64_MAX, f->acquire_sem,
                              VK_NULL_HANDLE, &g.bb_index);
    if (r == VK_ERROR_OUT_OF_DATE_KHR) {
      if (vkb_recreate_swapchain())
        r = vkAcquireNextImageKHR(g.device, g.swapchain, UINT64_MAX,
                                  f->acquire_sem, VK_NULL_HANDLE, &g.bb_index);
    }
    if (r == VK_SUCCESS || r == VK_SUBOPTIMAL_KHR) {
      g.have_acquired = true;
      // Present leaves the contents undefined; every swapchain pass clears.
      g.sc_layouts[g.bb_index] = VK_IMAGE_LAYOUT_UNDEFINED;
      if (r == VK_SUBOPTIMAL_KHR)
        g.need_recreate = true;
    } else {
      SDL_Log("vk: vkAcquireNextImageKHR failed (%d)", (int)r);
    }
  }

  if (out_w)
    *out_w = g.sw_w;
  if (out_h)
    *out_h = g.sw_h;
}

static void vkb_end_frame(App *app) {
  (void)app;
  if (g.recording) {
    if (g.have_acquired) {
      vkb_image_barrier(g.frames[g.slot].cmd, g.sc_images[g.bb_index],
                        VK_IMAGE_ASPECT_COLOR_BIT, g.sc_layouts[g.bb_index],
                        VK_IMAGE_LAYOUT_PRESENT_SRC_KHR);
      g.sc_layouts[g.bb_index] = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    }
    vkEndCommandBuffer(g.frames[g.slot].cmd);
    vkb_submit_cmd(g.frames[g.slot].cmd, /*signal_present=*/g.have_acquired);
    g.recording = false;
  } else if (!g.submitted_before_present) {
    return;
  }
  g.submitted_before_present = false;

  if (g.have_acquired) {
    VkPresentInfoKHR pi = {
        .sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
        .waitSemaphoreCount = 1,
        .pWaitSemaphores = &g.render_done_sems[g.bb_index],
        .swapchainCount = 1,
        .pSwapchains = &g.swapchain,
        .pImageIndices = &g.bb_index,
    };
    VkResult r = vkQueuePresentKHR(g.queue, &pi);
    if (r == VK_ERROR_OUT_OF_DATE_KHR || r == VK_SUBOPTIMAL_KHR)
      g.need_recreate = true;
    else if (r != VK_SUCCESS)
      SDL_Log("vk: vkQueuePresentKHR failed (%d)", (int)r);
  }

  g.frames[g.slot].fence_value = g.last_submit_value;
  g.slot = (g.slot + 1) % KFRAMES_IN_FLIGHT;
}

// --- vtable: passes
// ------------------------------------------------------------------

// D3D-style clip space: flip the viewport (negative height) so the front
// face stays clockwise like the d3d12 backend. Scissor stays top-left.
static void vkb_set_viewport_scissor(VkCommandBuffer cmd, int w, int h) {
  VkViewport vp = {0.0f, (float)h, (float)w, -(float)h, 0.0f, 1.0f};
  VkRect2D sc = {{0, 0}, {(uint32_t)w, (uint32_t)h}};
  vkCmdSetViewport(cmd, 0, 1, &vp);
  vkCmdSetScissor(cmd, 0, 1, &sc);
}

static void vkb_begin_pass(App *app, const PassBeginDesc *d) {
  (void)app;
  if (!g.recording)
    return;
  VkCommandBuffer cmd = g.frames[g.slot].cmd;
  VkAttachmentLoadOp load_op = (d->load == SGL_LOAD_LOAD)
                                   ? VK_ATTACHMENT_LOAD_OP_LOAD
                                   : VK_ATTACHMENT_LOAD_OP_CLEAR;

  int nct = d->n_color_targets;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;
  int w = 0, h = 0;
  g.pass_n_colors = 0;
  g.pass_has_depth = false;

  if (nct == 1 && d->targets[0] == 0 && !d->depth_target) {
    // Swapchain pass.
    if (!g.have_acquired)
      return;
    vkb_image_barrier(cmd, g.sc_images[g.bb_index], VK_IMAGE_ASPECT_COLOR_BIT,
                      g.sc_layouts[g.bb_index],
                      VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
    g.sc_layouts[g.bb_index] = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
    if (g.depth_layout != VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL) {
      vkb_image_barrier(cmd, g.depth_img, vkb_aspect(g.depth24_fmt, false),
                        g.depth_layout,
                        VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL);
      g.depth_layout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
    }
    g.pass_colors[0] = (VkRenderingAttachmentInfo){
        .sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO,
        .imageView = g.sc_views[g.bb_index],
        .imageLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
        .loadOp = load_op,
        .storeOp = VK_ATTACHMENT_STORE_OP_STORE,
    };
    memcpy(g.pass_colors[0].clearValue.color.float32, d->clear[0],
           4 * sizeof(float));
    g.pass_n_colors = 1;
    g.pass_depth = (VkRenderingAttachmentInfo){
        .sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO,
        .imageView = g.depth_view,
        .imageLayout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL,
        .loadOp = load_op,
        .storeOp = VK_ATTACHMENT_STORE_OP_STORE,
    };
    g.pass_depth.clearValue.depthStencil.depth = 1.0f;
    g.pass_has_depth = true;
    w = g.sw_w;
    h = g.sw_h;
  } else {
    // Offscreen pass (color targets and/or depth-only).
    for (int i = 0; i < nct; ++i) {
      VkbImage *im = (VkbImage *)d->targets[i];
      if (!im || !im->img)
        return;
      if (im->layout != VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL) {
        vkb_image_barrier(cmd, im->img, VK_IMAGE_ASPECT_COLOR_BIT, im->layout,
                          VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
        im->layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
      }
      g.pass_colors[i] = (VkRenderingAttachmentInfo){
          .sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO,
          .imageView = im->attach_view,
          .imageLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
          .loadOp = load_op,
          .storeOp = VK_ATTACHMENT_STORE_OP_STORE,
      };
      memcpy(g.pass_colors[i].clearValue.color.float32, d->clear[i],
             4 * sizeof(float));
      w = im->w;
      h = im->h;
    }
    g.pass_n_colors = nct;
    if (d->depth_target) {
      VkbImage *di = (VkbImage *)d->depth_target;
      if (!di || !di->img)
        return;
      if (di->layout != VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL) {
        vkb_image_barrier(cmd, di->img, vkb_aspect(di->vkfmt, false),
                          di->layout,
                          VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL);
        di->layout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL;
      }
      g.pass_depth = (VkRenderingAttachmentInfo){
          .sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO,
          .imageView = di->attach_view,
          .imageLayout = VK_IMAGE_LAYOUT_DEPTH_STENCIL_ATTACHMENT_OPTIMAL,
          .loadOp = load_op,
          .storeOp = VK_ATTACHMENT_STORE_OP_STORE,
      };
      g.pass_depth.clearValue.depthStencil.depth = d->clear_depth;
      g.pass_has_depth = true;
      if (nct == 0) {
        w = di->w;
        h = di->h;
      }
    }
  }

  g.pass_w = w;
  g.pass_h = h;
  g.pass_suspended = false;
  vkb_begin_rendering(/*resume=*/false);
  vkb_set_viewport_scissor(cmd, w, h);
  g.in_pass = true;
}

static void vkb_end_pass(App *app) {
  (void)app;
  if (g.in_pass && g.recording && !g.pass_suspended)
    vkCmdEndRendering(g.frames[g.slot].cmd);
  g.in_pass = false;
  g.pass_suspended = false;
  g_current_pip = NULL;
}

static void vkb_set_scissor(int x, int y, int w, int h) {
  if (!g.recording || !g.in_pass)
    return;
  if (x < 0) {
    w += x;
    x = 0;
  }
  if (y < 0) {
    h += y;
    y = 0;
  }
  if (w < 0)
    w = 0;
  if (h < 0)
    h = 0;
  VkRect2D sc = {{x, y}, {(uint32_t)w, (uint32_t)h}};
  vkCmdSetScissor(g.frames[g.slot].cmd, 0, 1, &sc);
}

// --- buffers
// ---------------------------------------------------------------------

static VkBufferUsageFlags vkb_buffer_usage(SglBufferType type) {
  // Compute-written storage buffers get rebound as vertex/index buffers
  // (generated geometry), so keep those usages on everything cheap to allow.
  VkBufferUsageFlags u =
      VK_BUFFER_USAGE_TRANSFER_DST_BIT | VK_BUFFER_USAGE_TRANSFER_SRC_BIT |
      VK_BUFFER_USAGE_VERTEX_BUFFER_BIT | VK_BUFFER_USAGE_INDEX_BUFFER_BIT;
  if (type == SGL_BUFFER_STORAGE)
    u |= VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;
  if (type == SGL_BUFFER_UNIFORM)
    u |= VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT;
  return u;
}

// Copy `data` into `buf` through the per-frame upload arena, in command
// order (draws recorded before the update read the old contents).
static bool vkb_upload_buffer_bytes(VkbBuffer *buf, const void *data,
                                    size_t bytes) {
  UploadAlloc ua;
  if (!vkb_upload_alloc(bytes, 4, &ua))
    return false;
  memcpy(ua.cpu, data, bytes);

  RecordCtx ctx = vkb_record_begin();
  if (!ctx.ok)
    return false;
  vkb_memory_barrier(ctx.cmd);
  VkBufferCopy region = {.srcOffset = ua.offset, .size = bytes};
  vkCmdCopyBuffer(ctx.cmd, ua.buf, buf->buf, 1, &region);
  vkb_memory_barrier(ctx.cmd);
  vkb_record_end(&ctx);
  return true;
}

static BackendBuffer vkb_make_buffer(SglBufferType type, const void *data,
                                     size_t bytes) {
  if (bytes == 0)
    return 0;
  VkbBuffer *buf = (VkbBuffer *)calloc(1, sizeof(VkbBuffer));
  buf->bytes = bytes;
  buf->type = type;
  if (!vkb_alloc_buffer(bytes, vkb_buffer_usage(type),
                        VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, &buf->buf,
                        &buf->mem)) {
    SDL_Log("vk: make_buffer: create failed (%zu bytes)", bytes);
    free(buf);
    return 0;
  }
  if (data && !vkb_upload_buffer_bytes(buf, data, bytes)) {
    vkDestroyBuffer(g.device, buf->buf, NULL);
    vkFreeMemory(g.device, buf->mem, NULL);
    free(buf);
    return 0;
  }
  gpu_stats_create(GPU_STAT_BUFFER, bytes);
  return (uintptr_t)buf;
}

static void vkb_update_buffer(BackendBuffer h, const void *data, size_t bytes) {
  VkbBuffer *buf = (VkbBuffer *)h;
  if (!buf || !data || bytes == 0)
    return;
  if (bytes > buf->bytes)
    bytes = buf->bytes;
  vkb_upload_buffer_bytes(buf, data, bytes);
}

static void vkb_destroy_buffer(BackendBuffer h) {
  VkbBuffer *buf = (VkbBuffer *)h;
  if (!buf)
    return;
  gpu_stats_destroy(GPU_STAT_BUFFER, buf->bytes);
  Zombie z = {0};
  z.buf = buf->buf;
  z.mem = buf->mem;
  vkb_zombie_push(&z);
  free(buf);
}

// --- images
// ----------------------------------------------------------------------

// Copy packed pixel data into `im`. Vulkan buffer-image copies accept tight
// rows (bufferRowLength = 0), so no pitch alignment pass is needed.
static bool vkb_upload_image_bytes(VkbImage *im, const void *data,
                                   size_t bytes) {
  int bpp = vkb_bytes_per_pixel(im->fmt);
  size_t need = (size_t)im->w * bpp * (size_t)im->h;
  if (bytes < need) {
    SDL_Log("vk: image upload: %zu bytes < expected %zu", bytes, need);
    return false;
  }
  UploadAlloc ua;
  if (!vkb_upload_alloc(need, 16, &ua))
    return false;
  memcpy(ua.cpu, data, need);

  RecordCtx ctx = vkb_record_begin();
  if (!ctx.ok)
    return false;
  vkb_image_barrier(ctx.cmd, im->img, VK_IMAGE_ASPECT_COLOR_BIT, im->layout,
                    VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
  VkBufferImageCopy region = {
      .bufferOffset = ua.offset,
      .imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1},
      .imageExtent = {(uint32_t)im->w, (uint32_t)im->h, 1},
  };
  vkCmdCopyBufferToImage(ctx.cmd, ua.buf, im->img,
                         VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
  vkb_image_barrier(ctx.cmd, im->img, VK_IMAGE_ASPECT_COLOR_BIT,
                    VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                    VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
  im->layout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
  vkb_record_end(&ctx);
  return true;
}

static BackendImage vkb_make_image(const ImageDesc *d) {
  if (!d || d->w <= 0 || d->h <= 0)
    return 0;
  VkbImage *im = (VkbImage *)calloc(1, sizeof(VkbImage));
  im->w = d->w;
  im->h = d->h;
  im->fmt = d->fmt;
  im->vkfmt = vkb_format(d->fmt);
  im->filter = d->filter ? d->filter : SGL_FILTER_LINEAR;
  im->wrap = d->wrap ? d->wrap : SGL_WRAP_REPEAT;
  im->render_target = d->render_target;
  im->storage = d->storage;
  im->is_depth = sgl_is_depth_fmt(d->fmt);
  im->layout = VK_IMAGE_LAYOUT_UNDEFINED;

  VkImageUsageFlags usage =
      VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
  if (im->is_depth) {
    usage |= VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT;
  } else {
    usage |= VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    if (d->render_target)
      usage |= VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    if (d->storage)
      usage |= VK_IMAGE_USAGE_STORAGE_BIT;
  }

  VkImageCreateInfo ii = {
      .sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
      .imageType = VK_IMAGE_TYPE_2D,
      .format = im->vkfmt,
      .extent = {(uint32_t)d->w, (uint32_t)d->h, 1},
      .mipLevels = 1,
      .arrayLayers = 1,
      .samples = VK_SAMPLE_COUNT_1_BIT,
      .tiling = VK_IMAGE_TILING_OPTIMAL,
      .usage = usage,
      .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED,
  };
  if (vkCreateImage(g.device, &ii, NULL, &im->img) != VK_SUCCESS) {
    SDL_Log("vk: make_image: create failed (%dx%d fmt=%d)", d->w, d->h,
            (int)d->fmt);
    free(im);
    return 0;
  }
  VkMemoryRequirements mr;
  vkGetImageMemoryRequirements(g.device, im->img, &mr);
  uint32_t idx;
  if (!vkb_find_mem_type(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT,
                         &idx)) {
    vkDestroyImage(g.device, im->img, NULL);
    free(im);
    return 0;
  }
  VkMemoryAllocateInfo mai = {
      .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
      .allocationSize = mr.size,
      .memoryTypeIndex = idx,
  };
  if (vkAllocateMemory(g.device, &mai, NULL, &im->mem) != VK_SUCCESS ||
      vkBindImageMemory(g.device, im->img, im->mem, 0) != VK_SUCCESS) {
    SDL_Log("vk: make_image: memory alloc/bind failed");
    vkDestroyImage(g.device, im->img, NULL);
    if (im->mem)
      vkFreeMemory(g.device, im->mem, NULL);
    free(im);
    return 0;
  }

  // Sampled/storage view. Depth formats sample through a DEPTH-only aspect
  // view; the attachment view carries the full aspect.
  VkImageViewCreateInfo vi = {
      .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
      .image = im->img,
      .viewType = VK_IMAGE_VIEW_TYPE_2D,
      .format = im->vkfmt,
      .subresourceRange = {vkb_aspect(im->vkfmt, true), 0, 1, 0, 1},
  };
  if (vkCreateImageView(g.device, &vi, NULL, &im->view) != VK_SUCCESS) {
    SDL_Log("vk: make_image: view create failed");
    vkDestroyImage(g.device, im->img, NULL);
    vkFreeMemory(g.device, im->mem, NULL);
    free(im);
    return 0;
  }
  if (im->is_depth) {
    vi.subresourceRange.aspectMask = vkb_aspect(im->vkfmt, false);
    if (vkCreateImageView(g.device, &vi, NULL, &im->attach_view) !=
        VK_SUCCESS) {
      SDL_Log("vk: make_image: attach view create failed");
      vkDestroyImageView(g.device, im->view, NULL);
      vkDestroyImage(g.device, im->img, NULL);
      vkFreeMemory(g.device, im->mem, NULL);
      free(im);
      return 0;
    }
  } else {
    im->attach_view = im->view;
  }

  VkSamplerCreateInfo si = {
      .sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
      .magFilter = im->filter == SGL_FILTER_NEAREST ? VK_FILTER_NEAREST
                                                    : VK_FILTER_LINEAR,
      .minFilter = im->filter == SGL_FILTER_NEAREST ? VK_FILTER_NEAREST
                                                    : VK_FILTER_LINEAR,
      .mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST,
      .addressModeU = im->wrap == SGL_WRAP_CLAMP
                          ? VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE
                          : VK_SAMPLER_ADDRESS_MODE_REPEAT,
      .addressModeV = im->wrap == SGL_WRAP_CLAMP
                          ? VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE
                          : VK_SAMPLER_ADDRESS_MODE_REPEAT,
      .addressModeW = im->wrap == SGL_WRAP_CLAMP
                          ? VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE
                          : VK_SAMPLER_ADDRESS_MODE_REPEAT,
  };
  if (vkCreateSampler(g.device, &si, NULL, &im->sampler) != VK_SUCCESS) {
    SDL_Log("vk: make_image: sampler create failed");
    if (im->attach_view != im->view)
      vkDestroyImageView(g.device, im->attach_view, NULL);
    vkDestroyImageView(g.device, im->view, NULL);
    vkDestroyImage(g.device, im->img, NULL);
    vkFreeMemory(g.device, im->mem, NULL);
    free(im);
    return 0;
  }

  if (!d->render_target && d->data && d->data_bytes > 0) {
    if (!vkb_upload_image_bytes(im, d->data, d->data_bytes)) {
      vkDestroySampler(g.device, im->sampler, NULL);
      if (im->attach_view != im->view)
        vkDestroyImageView(g.device, im->attach_view, NULL);
      vkDestroyImageView(g.device, im->view, NULL);
      vkDestroyImage(g.device, im->img, NULL);
      vkFreeMemory(g.device, im->mem, NULL);
      free(im);
      return 0;
    }
  }
  gpu_stats_create(GPU_STAT_TEXTURE, gpu_stats_image_bytes(d->fmt, d->w, d->h));
  gpu_stats_create(GPU_STAT_SAMPLER, 0);
  return (uintptr_t)im;
}

static void vkb_update_image(BackendImage h, const void *data, size_t bytes) {
  VkbImage *im = (VkbImage *)h;
  if (!im || !data || bytes == 0)
    return;
  vkb_upload_image_bytes(im, data, bytes);
}

static void vkb_destroy_image(BackendImage h) {
  VkbImage *im = (VkbImage *)h;
  if (!im)
    return;
  gpu_stats_destroy(GPU_STAT_TEXTURE,
                    gpu_stats_image_bytes(im->fmt, im->w, im->h));
  gpu_stats_destroy(GPU_STAT_SAMPLER, 0);
  Zombie z = {0};
  z.img = im->img;
  z.mem = im->mem;
  z.view = im->view;
  z.view2 = im->attach_view != im->view ? im->attach_view : VK_NULL_HANDLE;
  z.sampler = im->sampler;
  vkb_zombie_push(&z);
  free(im);
}

// --- shaders / descriptor set layouts
// ---------------------------------------------- The SPIR-V is
// SHADER_TARGET_SDLGPU output, so set/binding numbers follow SDL_GPU's
// convention (shader.cpp patch_spirv_bindings_from_reflection):
//   graphics: set 0 = vs resources, set 1 = vs UBs,
//             set 2 = fs resources, set 3 = fs UBs
//   compute:  set 0 = sampled textures + RO storage, set 1 = RW storage,
//             set 2 = UBs
// Within a resource set: combined image samplers at texs[].smp_slot, then
// storage textures at sampler_count + slot, then storage buffers at
// sampler_count + ro_storage_tex_count + slot. Textures and samplers are
// combined descriptors (Sampler2D in the sdlgpu prelude).

static int vkb_sampler_count(const ShaderReflection *r, SglShaderStage st) {
  int n = 0;
  for (int i = 0; i < r->tex_count; ++i)
    if (r->texs[i].stage == st)
      n++;
  return n;
}

static int vkb_storage_tex_count(const ShaderReflection *r, SglShaderStage st,
                                 bool readonly) {
  int n = 0;
  for (int i = 0; i < r->storage_tex_count; ++i)
    if (r->storage_texs[i].stage == st &&
        r->storage_texs[i].readonly == readonly)
      n++;
  return n;
}

static int vkb_ro_storage_buf_binding(const ShaderReflection *r,
                                      SglShaderStage st,
                                      const ShaderStorageBuf *b) {
  return vkb_sampler_count(r, st) + vkb_storage_tex_count(r, st, true) +
         b->slot;
}

static int vkb_ro_storage_tex_binding(const ShaderReflection *r,
                                      SglShaderStage st,
                                      const ShaderStorageTexture *t) {
  return vkb_sampler_count(r, st) + t->slot;
}

static int vkb_rw_storage_buf_binding(const ShaderReflection *r,
                                      SglShaderStage st,
                                      const ShaderStorageBuf *b) {
  return vkb_storage_tex_count(r, st, false) + b->slot;
}

static void vkb_set_info_add(SetInfo *s, int binding, VkDescriptorType type) {
  if (s->count >= KSET_MAX_BINDINGS) {
    SDL_Log("vk: set binding cap exceeded");
    return;
  }
  for (int i = 0; i < s->count; ++i)
    if (s->b[i].binding == binding)
      return; // same register attributed twice
  s->b[s->count].binding = binding;
  s->b[s->count].type = type;
  s->count++;
}

// Resource set (graphics set 0/2, compute set 0 = readonly half).
static void vkb_collect_resource_set(const ShaderReflection *r,
                                     SglShaderStage st, SetInfo *out) {
  for (int i = 0; i < r->tex_count; ++i)
    if (r->texs[i].stage == st)
      vkb_set_info_add(out, r->texs[i].smp_slot,
                       VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER);
  for (int i = 0; i < r->storage_tex_count; ++i)
    if (r->storage_texs[i].stage == st && r->storage_texs[i].readonly)
      vkb_set_info_add(out,
                       vkb_ro_storage_tex_binding(r, st, &r->storage_texs[i]),
                       VK_DESCRIPTOR_TYPE_STORAGE_IMAGE);
  for (int i = 0; i < r->storage_buf_count; ++i)
    if (r->storage_bufs[i].stage == st && r->storage_bufs[i].readonly)
      vkb_set_info_add(out,
                       vkb_ro_storage_buf_binding(r, st, &r->storage_bufs[i]),
                       VK_DESCRIPTOR_TYPE_STORAGE_BUFFER);
}

static void vkb_collect_write_set(const ShaderReflection *r, SglShaderStage st,
                                  SetInfo *out) {
  for (int i = 0; i < r->storage_tex_count; ++i)
    if (r->storage_texs[i].stage == st && !r->storage_texs[i].readonly)
      vkb_set_info_add(out, r->storage_texs[i].slot,
                       VK_DESCRIPTOR_TYPE_STORAGE_IMAGE);
  for (int i = 0; i < r->storage_buf_count; ++i)
    if (r->storage_bufs[i].stage == st && !r->storage_bufs[i].readonly)
      vkb_set_info_add(out,
                       vkb_rw_storage_buf_binding(r, st, &r->storage_bufs[i]),
                       VK_DESCRIPTOR_TYPE_STORAGE_BUFFER);
}

static void vkb_collect_uniform_set(const ShaderReflection *r,
                                    SglShaderStage st, SetInfo *out) {
  for (int i = 0; i < r->ub_count; ++i)
    if (r->ubs[i].stage == st)
      vkb_set_info_add(out, r->ubs[i].slot, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER);
}

static bool vkb_create_set_layout(const SetInfo *info,
                                  VkShaderStageFlags stages,
                                  VkDescriptorSetLayout *out) {
  VkDescriptorSetLayoutBinding binds[KSET_MAX_BINDINGS];
  for (int i = 0; i < info->count; ++i) {
    binds[i] = (VkDescriptorSetLayoutBinding){
        .binding = (uint32_t)info->b[i].binding,
        .descriptorType = info->b[i].type,
        .descriptorCount = 1,
        .stageFlags = stages,
    };
  }
  VkDescriptorSetLayoutCreateInfo ci = {
      .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
      .bindingCount = (uint32_t)info->count,
      .pBindings = binds,
  };
  return vkCreateDescriptorSetLayout(g.device, &ci, NULL, out) == VK_SUCCESS;
}

static void vkb_free_shader_objects(VkbShader *sh) {
  Zombie z = {0};
  for (int i = 0; i < 4; ++i)
    z.dsl[i] = sh->dsl[i];
  z.layout = sh->layout;
  z.mods[0] = sh->vs;
  z.mods[1] = sh->fs;
  z.mods[2] = sh->cs;
  z.pipe = sh->compute_pipe;
  vkb_zombie_push(&z);
}

static VkShaderModule vkb_make_module(const uint32_t *code, size_t bytes) {
  VkShaderModuleCreateInfo ci = {
      .sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO,
      .codeSize = bytes,
      .pCode = code,
  };
  VkShaderModule mod = VK_NULL_HANDLE;
  if (vkCreateShaderModule(g.device, &ci, NULL, &mod) != VK_SUCCESS)
    SDL_Log("vk: vkCreateShaderModule failed (%zu bytes)", bytes);
  return mod;
}

static BackendShader vkb_make_shader(const ShaderDesc *d) {
  if (!d)
    return 0;
  VkbShader *sh = (VkbShader *)calloc(1, sizeof(VkbShader));
  if (d->refl)
    sh->refl = *d->refl;

  bool ok = true;
  if (d->cs_spirv) {
    sh->n_sets = 3;
    vkb_collect_resource_set(&sh->refl, SGL_STAGE_COMPUTE, &sh->sets[0]);
    vkb_collect_write_set(&sh->refl, SGL_STAGE_COMPUTE, &sh->sets[1]);
    vkb_collect_uniform_set(&sh->refl, SGL_STAGE_COMPUTE, &sh->sets[2]);
    for (int i = 0; i < 3 && ok; ++i)
      ok = vkb_create_set_layout(&sh->sets[i], VK_SHADER_STAGE_COMPUTE_BIT,
                                 &sh->dsl[i]);
  } else {
    if (!d->vs_spirv || !d->fs_spirv) {
      SDL_Log("vk: make_shader: missing vs/fs blob");
      free(sh);
      return 0;
    }
    sh->n_sets = 4;
    vkb_collect_resource_set(&sh->refl, SGL_STAGE_VERTEX, &sh->sets[0]);
    vkb_collect_uniform_set(&sh->refl, SGL_STAGE_VERTEX, &sh->sets[1]);
    vkb_collect_resource_set(&sh->refl, SGL_STAGE_FRAGMENT, &sh->sets[2]);
    vkb_collect_uniform_set(&sh->refl, SGL_STAGE_FRAGMENT, &sh->sets[3]);
    ok = vkb_create_set_layout(&sh->sets[0], VK_SHADER_STAGE_VERTEX_BIT,
                               &sh->dsl[0]) &&
         vkb_create_set_layout(&sh->sets[1], VK_SHADER_STAGE_VERTEX_BIT,
                               &sh->dsl[1]) &&
         vkb_create_set_layout(&sh->sets[2], VK_SHADER_STAGE_FRAGMENT_BIT,
                               &sh->dsl[2]) &&
         vkb_create_set_layout(&sh->sets[3], VK_SHADER_STAGE_FRAGMENT_BIT,
                               &sh->dsl[3]);
  }
  if (!ok) {
    SDL_Log("vk: descriptor set layout create failed");
    vkb_free_shader_objects(sh);
    free(sh);
    return 0;
  }

  VkPipelineLayoutCreateInfo pli = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO,
      .setLayoutCount = (uint32_t)sh->n_sets,
      .pSetLayouts = sh->dsl,
  };
  if (vkCreatePipelineLayout(g.device, &pli, NULL, &sh->layout) != VK_SUCCESS) {
    SDL_Log("vk: vkCreatePipelineLayout failed");
    vkb_free_shader_objects(sh);
    free(sh);
    return 0;
  }

  if (d->cs_spirv) {
    sh->cs = vkb_make_module(d->cs_spirv, d->cs_bytes);
    if (!sh->cs) {
      vkb_free_shader_objects(sh);
      free(sh);
      return 0;
    }
    VkComputePipelineCreateInfo ci = {
        .sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO,
        .stage = {.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
                  .stage = VK_SHADER_STAGE_COMPUTE_BIT,
                  .module = sh->cs,
                  .pName = "main"},
        .layout = sh->layout,
    };
    if (vkCreateComputePipelines(g.device, VK_NULL_HANDLE, 1, &ci, NULL,
                                 &sh->compute_pipe) != VK_SUCCESS) {
      SDL_Log("vk: vkCreateComputePipelines failed");
      vkb_free_shader_objects(sh);
      free(sh);
      return 0;
    }
    gpu_stats_create(GPU_STAT_PIPELINE, 0);
    return (uintptr_t)sh;
  }

  sh->vs = vkb_make_module(d->vs_spirv, d->vs_bytes);
  sh->fs = vkb_make_module(d->fs_spirv, d->fs_bytes);
  if (!sh->vs || !sh->fs) {
    vkb_free_shader_objects(sh);
    free(sh);
    return 0;
  }
  gpu_stats_create(GPU_STAT_SHADER, 0);
  gpu_stats_create(GPU_STAT_SHADER, 0);
  return (uintptr_t)sh;
}

static void vkb_destroy_shader(BackendShader h) {
  VkbShader *sh = (VkbShader *)h;
  if (!sh)
    return;
  if (sh->compute_pipe)
    gpu_stats_destroy(GPU_STAT_PIPELINE, 0);
  else {
    gpu_stats_destroy(GPU_STAT_SHADER, 0);
    gpu_stats_destroy(GPU_STAT_SHADER, 0);
  }
  vkb_free_shader_objects(sh);
  free(sh);
}

// --- pipelines
// --------------------------------------------------------------------

static void vkb_blend_state(SglBlend b,
                            VkPipelineColorBlendAttachmentState *out) {
  *out = (VkPipelineColorBlendAttachmentState){
      .srcColorBlendFactor = VK_BLEND_FACTOR_ONE,
      .dstColorBlendFactor = VK_BLEND_FACTOR_ZERO,
      .colorBlendOp = VK_BLEND_OP_ADD,
      .srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE,
      .dstAlphaBlendFactor = VK_BLEND_FACTOR_ZERO,
      .alphaBlendOp = VK_BLEND_OP_ADD,
      .colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT |
                        VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT,
  };
  switch (b) {
  case SGL_BLEND_ALPHA:
    out->blendEnable = VK_TRUE;
    out->srcColorBlendFactor = VK_BLEND_FACTOR_SRC_ALPHA;
    out->dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    out->srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    out->dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    break;
  case SGL_BLEND_ADDITIVE:
    out->blendEnable = VK_TRUE;
    out->srcColorBlendFactor = VK_BLEND_FACTOR_ONE;
    out->dstColorBlendFactor = VK_BLEND_FACTOR_ONE;
    out->srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    out->dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
    break;
  case SGL_BLEND_MULTIPLY:
    out->blendEnable = VK_TRUE;
    out->srcColorBlendFactor = VK_BLEND_FACTOR_DST_COLOR;
    out->dstColorBlendFactor = VK_BLEND_FACTOR_ZERO;
    break;
  default:
    break;
  }
}

static BackendPipeline vkb_make_pipeline(const PipelineDesc *d) {
  VkbShader *sh = (VkbShader *)d->shader;
  if (!sh) {
    SDL_Log("vk: make_pipeline: null shader");
    return 0;
  }
  VkbPipeline *p = (VkbPipeline *)calloc(1, sizeof(VkbPipeline));
  if (d->refl)
    p->refl = *d->refl;
  p->layout = sh->layout;
  p->n_sets = sh->n_sets;
  memcpy(p->sets, sh->sets, sizeof(p->sets));
  memcpy(p->dsl, sh->dsl, sizeof(p->dsl));

  if (d->is_compute) {
    if (!sh->compute_pipe) {
      SDL_Log("vk: make_pipeline: shader is not compute");
      free(p);
      return 0;
    }
    p->compute_pipe = sh->compute_pipe;
    p->is_compute = true;
    return (uintptr_t)p;
  }

  VkVertexInputAttributeDescription attrs[SGL_MAX_ATTRS];
  VkVertexInputBindingDescription binds[SGL_MAX_VERTEX_BUFFERS];
  uint32_t n_binds = 0;
  bool buffer_used[SGL_MAX_VERTEX_BUFFERS] = {false};
  int attr_count = p->refl.attr_count;
  static const VkFormat comp_fmt[5] = {
      VK_FORMAT_UNDEFINED, VK_FORMAT_R32_SFLOAT, VK_FORMAT_R32G32_SFLOAT,
      VK_FORMAT_R32G32B32_SFLOAT, VK_FORMAT_R32G32B32A32_SFLOAT};
  for (int i = 0; i < attr_count; ++i) {
    const ShaderAttr *a = &p->refl.attrs[i];
    int cc = a->comp_count >= 1 && a->comp_count <= 4 ? a->comp_count : 4;
    int bi = (a->buffer_index >= 0 && a->buffer_index < SGL_MAX_VERTEX_BUFFERS)
                 ? a->buffer_index
                 : 0;
    attrs[i] = (VkVertexInputAttributeDescription){
        .location = (uint32_t)a->slot,
        .binding = (uint32_t)bi,
        .format = comp_fmt[cc],
        .offset = (uint32_t)(a->offset_floats * sizeof(float)),
    };
    buffer_used[bi] = true;
  }
  for (int bi = 0; bi < SGL_MAX_VERTEX_BUFFERS; ++bi) {
    if (!buffer_used[bi])
      continue;
    binds[n_binds++] = (VkVertexInputBindingDescription){
        .binding = (uint32_t)bi,
        .stride = (uint32_t)(p->refl.buffer_stride_floats[bi] * sizeof(float)),
        .inputRate = bi == 0 ? VK_VERTEX_INPUT_RATE_VERTEX
                             : VK_VERTEX_INPUT_RATE_INSTANCE,
    };
  }
  VkPipelineVertexInputStateCreateInfo vin = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO,
      .vertexBindingDescriptionCount = n_binds,
      .pVertexBindingDescriptions = binds,
      .vertexAttributeDescriptionCount = (uint32_t)attr_count,
      .pVertexAttributeDescriptions = attrs,
  };

  VkPrimitiveTopology topo;
  switch (d->primitive) {
  case SGL_PRIM_LINES:
    topo = VK_PRIMITIVE_TOPOLOGY_LINE_LIST;
    break;
  case SGL_PRIM_LINE_STRIP:
    topo = VK_PRIMITIVE_TOPOLOGY_LINE_STRIP;
    break;
  case SGL_PRIM_POINTS:
    topo = VK_PRIMITIVE_TOPOLOGY_POINT_LIST;
    break;
  case SGL_PRIM_TRIANGLE_STRIP:
    topo = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP;
    break;
  case SGL_PRIM_TRIANGLES:
  default:
    topo = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    break;
  }
  VkPipelineInputAssemblyStateCreateInfo ia = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO,
      .topology = topo,
  };
  VkPipelineViewportStateCreateInfo vps = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO,
      .viewportCount = 1,
      .scissorCount = 1,
  };
  VkPipelineRasterizationStateCreateInfo rs = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO,
      .polygonMode = VK_POLYGON_MODE_FILL,
      .cullMode = d->cull == SGL_CULL_BACK    ? VK_CULL_MODE_BACK_BIT
                  : d->cull == SGL_CULL_FRONT ? VK_CULL_MODE_FRONT_BIT
                                              : VK_CULL_MODE_NONE,
      // D3D-style: clockwise front, paired with the flipped viewport.
      .frontFace = VK_FRONT_FACE_CLOCKWISE,
      .lineWidth = 1.0f,
  };
  VkPipelineMultisampleStateCreateInfo ms = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO,
      .rasterizationSamples = VK_SAMPLE_COUNT_1_BIT,
  };
  VkPipelineDepthStencilStateCreateInfo ds = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO,
  };
  if (d->has_depth) {
    ds.depthTestEnable = (d->depth_test || d->depth_write) ? VK_TRUE : VK_FALSE;
    ds.depthWriteEnable = d->depth_write ? VK_TRUE : VK_FALSE;
    ds.depthCompareOp =
        d->depth_test ? VK_COMPARE_OP_LESS_OR_EQUAL : VK_COMPARE_OP_ALWAYS;
  }

  int nct = d->n_color_targets > 0 ? d->n_color_targets : 0;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;
  VkPipelineColorBlendAttachmentState blends[SGL_MAX_COLOR_TARGETS];
  VkFormat color_fmts[SGL_MAX_COLOR_TARGETS];
  vkb_blend_state(d->blend, &blends[0]);
  for (int i = 0; i < nct; ++i) {
    blends[i] = blends[0];
    color_fmts[i] = vkb_format(d->color_fmts[i]);
  }
  VkPipelineColorBlendStateCreateInfo cb = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO,
      .attachmentCount = (uint32_t)nct,
      .pAttachments = blends,
  };
  VkDynamicState dyn[2] = {VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR};
  VkPipelineDynamicStateCreateInfo dyns = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO,
      .dynamicStateCount = 2,
      .pDynamicStates = dyn,
  };

  VkFormat depth_fmt =
      d->has_depth ? vkb_format(d->depth_fmt) : VK_FORMAT_UNDEFINED;
  // Passes never attach stencil (pStencilAttachment stays NULL), so the
  // pipeline's stencil format is UNDEFINED even for D24S8 depth targets.
  VkPipelineRenderingCreateInfo pri = {
      .sType = VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO,
      .colorAttachmentCount = (uint32_t)nct,
      .pColorAttachmentFormats = color_fmts,
      .depthAttachmentFormat = depth_fmt,
      .stencilAttachmentFormat = VK_FORMAT_UNDEFINED,
  };

  VkPipelineShaderStageCreateInfo stages[2] = {
      {
          .sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
          .stage = VK_SHADER_STAGE_VERTEX_BIT,
          .module = sh->vs,
          .pName = "main",
      },
      {
          .sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
          .stage = VK_SHADER_STAGE_FRAGMENT_BIT,
          .module = sh->fs,
          .pName = "main",
      },
  };
  VkGraphicsPipelineCreateInfo ci = {
      .sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO,
      .pNext = &pri,
      .stageCount = 2,
      .pStages = stages,
      .pVertexInputState = &vin,
      .pInputAssemblyState = &ia,
      .pViewportState = &vps,
      .pRasterizationState = &rs,
      .pMultisampleState = &ms,
      .pDepthStencilState = d->has_depth ? &ds : NULL,
      .pColorBlendState = &cb,
      .pDynamicState = &dyns,
      .layout = sh->layout,
  };
  if (vkCreateGraphicsPipelines(g.device, VK_NULL_HANDLE, 1, &ci, NULL,
                                &p->pipe) != VK_SUCCESS) {
    SDL_Log("vk: vkCreateGraphicsPipelines failed");
    free(p);
    return 0;
  }
  gpu_stats_create(GPU_STAT_PIPELINE, 0);
  return (uintptr_t)p;
}

static void vkb_destroy_pipeline(BackendPipeline h) {
  VkbPipeline *p = (VkbPipeline *)h;
  if (!p)
    return;
  if (p->pipe) {
    gpu_stats_destroy(GPU_STAT_PIPELINE, 0);
    Zombie z = {0};
    z.pipe = p->pipe;
    vkb_zombie_push(&z);
  }
  free(p);
}

// --- descriptor sets
// ----------------------------------------------------------------

static VkDescriptorPool vkb_new_desc_pool(void) {
  VkDescriptorPoolSize sizes[4] = {
      {VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, KDESC_POOL_SETS * 2},
      {VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, KDESC_POOL_SETS * 2},
      {VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, KDESC_POOL_SETS / 2},
      {VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, KDESC_POOL_SETS / 2},
  };
  VkDescriptorPoolCreateInfo ci = {
      .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO,
      .maxSets = KDESC_POOL_SETS,
      .poolSizeCount = 4,
      .pPoolSizes = sizes,
  };
  VkDescriptorPool pool = VK_NULL_HANDLE;
  if (vkCreateDescriptorPool(g.device, &ci, NULL, &pool) != VK_SUCCESS)
    SDL_Log("vk: descriptor pool create failed");
  return pool;
}

// Allocate a transient descriptor set from this frame's pool ring.
static VkDescriptorSet vkb_alloc_set(VkDescriptorSetLayout layout) {
  FrameCtx *f = &g.frames[g.slot];
  for (;;) {
    if (f->desc_pool_cur == f->desc_pool_count) {
      if (f->desc_pool_count == KDESC_POOL_CAP) {
        static bool warned = false;
        if (!warned) {
          SDL_Log("vk: descriptor pool ring overflow");
          warned = true;
        }
        return VK_NULL_HANDLE;
      }
      VkDescriptorPool pool = vkb_new_desc_pool();
      if (!pool)
        return VK_NULL_HANDLE;
      f->desc_pools[f->desc_pool_count++] = pool;
    }
    VkDescriptorSetAllocateInfo ai = {
        .sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO,
        .descriptorPool = f->desc_pools[f->desc_pool_cur],
        .descriptorSetCount = 1,
        .pSetLayouts = &layout,
    };
    VkDescriptorSet set = VK_NULL_HANDLE;
    VkResult r = vkAllocateDescriptorSets(g.device, &ai, &set);
    if (r == VK_SUCCESS)
      return set;
    if (r == VK_ERROR_OUT_OF_POOL_MEMORY || r == VK_ERROR_FRAGMENTED_POOL) {
      f->desc_pool_cur++;
      continue;
    }
    SDL_Log("vk: vkAllocateDescriptorSets failed (%d)", (int)r);
    return VK_NULL_HANDLE;
  }
}

// Scratch for building one set's writes.
typedef struct SetWrites {
  VkWriteDescriptorSet writes[KSET_MAX_BINDINGS];
  VkDescriptorImageInfo imgs[KSET_MAX_BINDINGS];
  VkDescriptorBufferInfo bufs[KSET_MAX_BINDINGS];
  int count;
} SetWrites;

static void vkb_write_default(SetWrites *w, VkDescriptorSet set,
                              const SetBindingInfo *b) {
  int i = w->count++;
  w->writes[i] = (VkWriteDescriptorSet){
      .sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,
      .dstSet = set,
      .dstBinding = (uint32_t)b->binding,
      .descriptorCount = 1,
      .descriptorType = b->type,
  };
  switch (b->type) {
  case VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER:
    w->imgs[i] = (VkDescriptorImageInfo){
        .sampler = g.dummy_tex->sampler,
        .imageView = g.dummy_tex->view,
        .imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
    };
    w->writes[i].pImageInfo = &w->imgs[i];
    break;
  case VK_DESCRIPTOR_TYPE_STORAGE_IMAGE:
    w->imgs[i] = (VkDescriptorImageInfo){
        .imageView = g.dummy_storage->view,
        .imageLayout = VK_IMAGE_LAYOUT_GENERAL,
    };
    w->writes[i].pImageInfo = &w->imgs[i];
    break;
  case VK_DESCRIPTOR_TYPE_STORAGE_BUFFER:
    w->bufs[i] = (VkDescriptorBufferInfo){
        .buffer = g.dummy_sbuf->buf,
        .range = VK_WHOLE_SIZE,
    };
    w->writes[i].pBufferInfo = &w->bufs[i];
    break;
  default: // uniform buffer
    w->bufs[i] = (VkDescriptorBufferInfo){
        .buffer = g.dummy_ubuf->buf,
        .range = VK_WHOLE_SIZE,
    };
    w->writes[i].pBufferInfo = &w->bufs[i];
    break;
  }
}

static int vkb_write_index_for_binding(SetWrites *w, int binding) {
  for (int i = 0; i < w->count; ++i)
    if ((int)w->writes[i].dstBinding == binding)
      return i;
  return -1;
}

// --- draw state
// -----------------------------------------------------------------------

static void vkb_apply_pipeline(BackendPipeline h) {
  g_current_pip = (VkbPipeline *)h;
  if (!g_current_pip || !g.recording || g_current_pip->is_compute)
    return;
  vkCmdBindPipeline(g.frames[g.slot].cmd, VK_PIPELINE_BIND_POINT_GRAPHICS,
                    g_current_pip->pipe);
  g_uniforms_dirty[0] = g_uniforms_dirty[1] = true;
}

static void vkb_apply_bindings(const BindingsDesc *b) {
  if (!g.recording || !g_current_pip)
    return;
  VkCommandBuffer cmd = g.frames[g.slot].cmd;
  const ShaderReflection *refl = &g_current_pip->refl;

  VkDeviceSize zero = 0;
  if (b->vbuf) {
    VkbBuffer *vb = (VkbBuffer *)b->vbuf;
    if (vb && vb->buf)
      vkCmdBindVertexBuffers(cmd, 0, 1, &vb->buf, &zero);
  }
  if (b->instance_vbuf) {
    VkbBuffer *vb = (VkbBuffer *)b->instance_vbuf;
    if (vb && vb->buf)
      vkCmdBindVertexBuffers(cmd, 1, 1, &vb->buf, &zero);
  }
  if (b->ibuf) {
    VkbBuffer *ib = (VkbBuffer *)b->ibuf;
    if (ib && ib->buf) {
      vkCmdBindIndexBuffer(cmd, ib->buf, 0, VK_INDEX_TYPE_UINT32);
      g_last_indexed = true;
    } else {
      g_last_indexed = false;
    }
  } else {
    g_last_indexed = false;
  }

  if (!b->refl)
    return;

  // Resource sets (0 = vertex, 2 = fragment). Transition sampled images
  // first: barriers suspend the pass, descriptor writes/binds don't care.
  for (int i = 0; i < b->texture_count; ++i) {
    VkbImage *im = (VkbImage *)b->textures[i].image;
    if (im && im->img && im->layout != VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL)
      vkb_transition(im, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
  }
  vkb_pass_resume();

  // Unmatched bindings keep dummy descriptors — the moral equivalent of the
  // d3d12 backend's null SRV writes.
  static const struct {
    int set_index;
    SglShaderStage stage;
  } stages[2] = {{0, SGL_STAGE_VERTEX}, {2, SGL_STAGE_FRAGMENT}};
  for (int s = 0; s < 2; ++s) {
    const SetInfo *info = &g_current_pip->sets[stages[s].set_index];
    if (info->count == 0)
      continue;
    VkDescriptorSet set =
        vkb_alloc_set(g_current_pip->dsl[stages[s].set_index]);
    if (!set)
      return;
    SetWrites w = {0};
    for (int i = 0; i < info->count; ++i)
      vkb_write_default(&w, set, &info->b[i]);

    for (int i = 0; i < b->texture_count; ++i) {
      if (!b->textures[i].name)
        continue;
      VkbImage *im = (VkbImage *)b->textures[i].image;
      if (!im || !im->img)
        continue;
      for (int j = 0; j < refl->tex_count; ++j) {
        const ShaderTexture *rt = &refl->texs[j];
        if (rt->stage != stages[s].stage ||
            strcmp(rt->name, b->textures[i].name) != 0)
          continue;
        int wi = vkb_write_index_for_binding(&w, rt->smp_slot);
        if (wi >= 0) {
          w.imgs[wi].sampler = im->sampler;
          w.imgs[wi].imageView = im->view;
          w.imgs[wi].imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        }
        // Keep scanning: the same texture name can appear in both stages.
      }
    }
    vkUpdateDescriptorSets(g.device, (uint32_t)w.count, w.writes, 0, NULL);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS,
                            g_current_pip->layout,
                            (uint32_t)stages[s].set_index, 1, &set, 0, NULL);
  }
}

static void vkb_apply_uniforms(SglShaderStage stage, int slot, const void *data,
                               size_t bytes) {
  if (!g.recording || !g_current_pip || !data || bytes == 0)
    return;
  if (stage == SGL_STAGE_COMPUTE)
    return; // compute uniforms flow through vkb_dispatch
  if (slot < 0 || slot >= SGL_MAX_UNIFORM_BLOCKS)
    return;
  int si = stage == SGL_STAGE_VERTEX ? 0 : 1;
  UploadAlloc ua;
  if (!vkb_upload_alloc(bytes, (size_t)g.ub_align, &ua))
    return;
  memcpy(ua.cpu, data, bytes);
  g_uniforms[si][slot].buf = ua.buf;
  g_uniforms[si][slot].off = ua.offset;
  g_uniforms[si][slot].bytes = bytes;
  g_uniforms[si][slot].set = true;
  g_uniforms_dirty[si] = true;
}

// Bind pending uniform sets (set 1 = vertex UBs, set 3 = fragment UBs).
static void vkb_flush_uniform_sets(void) {
  VkCommandBuffer cmd = g.frames[g.slot].cmd;
  for (int si = 0; si < 2; ++si) {
    if (!g_uniforms_dirty[si])
      continue;
    int set_index = si == 0 ? 1 : 3;
    const SetInfo *info = &g_current_pip->sets[set_index];
    if (info->count == 0) {
      g_uniforms_dirty[si] = false;
      continue;
    }
    VkDescriptorSet set = vkb_alloc_set(g_current_pip->dsl[set_index]);
    if (!set)
      return;
    SetWrites w = {0};
    for (int i = 0; i < info->count; ++i) {
      vkb_write_default(&w, set, &info->b[i]);
      int slot = info->b[i].binding;
      if (slot >= 0 && slot < SGL_MAX_UNIFORM_BLOCKS &&
          g_uniforms[si][slot].set) {
        w.bufs[i] = (VkDescriptorBufferInfo){
            .buffer = g_uniforms[si][slot].buf,
            .offset = g_uniforms[si][slot].off,
            .range = g_uniforms[si][slot].bytes,
        };
      }
    }
    vkUpdateDescriptorSets(g.device, (uint32_t)w.count, w.writes, 0, NULL);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS,
                            g_current_pip->layout, (uint32_t)set_index, 1, &set,
                            0, NULL);
    g_uniforms_dirty[si] = false;
  }
}

static void vkb_draw(int base, int count, int instance_count) {
  if (!g.recording || !g.in_pass || !g_current_pip || g_current_pip->is_compute)
    return;
  vkb_flush_uniform_sets();
  VkCommandBuffer cmd = g.frames[g.slot].cmd;
  uint32_t instances = (uint32_t)(instance_count > 0 ? instance_count : 1);
  if (g_last_indexed)
    vkCmdDrawIndexed(cmd, (uint32_t)count, instances, (uint32_t)base, 0, 0);
  else
    vkCmdDraw(cmd, (uint32_t)count, instances, (uint32_t)base, 0);
}

// --- compute
// -----------------------------------------------------------------------

static void vkb_dispatch(App *app, const ComputeDispatchDesc *d) {
  (void)app;
  if (!g.recording) {
    SDL_Log("vk: dispatch: no open frame");
    return;
  }
  if (g.in_pass) {
    SDL_Log("vk: dispatch: called inside a render pass");
    return;
  }
  VkbPipeline *p = (VkbPipeline *)d->pipeline;
  if (!p || !p->is_compute || !p->compute_pipe) {
    SDL_Log("vk: dispatch: not a compute pipeline");
    return;
  }
  VkCommandBuffer cmd = g.frames[g.slot].cmd;
  const ShaderReflection *refl = d->refl ? d->refl : &p->refl;

  // Layouts first (they carry their own barriers), then one global barrier
  // ordering every prior write against the dispatch.
  for (int i = 0; i < d->texture_count; ++i) {
    VkbImage *im = (VkbImage *)d->textures[i].image;
    if (im && im->img && im->layout != VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL)
      vkb_transition(im, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
  }
  for (int i = 0; i < d->n_storage_textures; ++i) {
    VkbImage *im = (VkbImage *)d->storage_textures[i].image;
    if (im && im->img && im->layout != VK_IMAGE_LAYOUT_GENERAL)
      vkb_transition(im, VK_IMAGE_LAYOUT_GENERAL);
  }
  vkb_memory_barrier(cmd);

  vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, p->compute_pipe);

  // Set 0 = sampled textures + RO storage, set 1 = RW storage, set 2 = UBs.
  for (int set_index = 0; set_index < 3; ++set_index) {
    const SetInfo *info = &p->sets[set_index];
    if (info->count == 0)
      continue;
    VkDescriptorSet set = vkb_alloc_set(p->dsl[set_index]);
    if (!set)
      return;
    SetWrites w = {0};
    for (int i = 0; i < info->count; ++i)
      vkb_write_default(&w, set, &info->b[i]);

    if (set_index == 2) {
      for (int i = 0; i < d->uniform_count; ++i) {
        int slot = d->uniforms[i].slot;
        if (slot < 0 || slot >= SGL_MAX_UNIFORM_BLOCKS ||
            !d->uniforms[i].data || d->uniforms[i].bytes == 0)
          continue;
        int wi = vkb_write_index_for_binding(&w, slot);
        if (wi < 0)
          continue;
        UploadAlloc ua;
        if (!vkb_upload_alloc(d->uniforms[i].bytes, (size_t)g.ub_align, &ua))
          return;
        memcpy(ua.cpu, d->uniforms[i].data, d->uniforms[i].bytes);
        w.bufs[wi] = (VkDescriptorBufferInfo){
            .buffer = ua.buf,
            .offset = ua.offset,
            .range = d->uniforms[i].bytes,
        };
      }
    } else {
      for (int i = 0; i < d->texture_count; ++i) {
        if (!d->textures[i].name)
          continue;
        VkbImage *im = (VkbImage *)d->textures[i].image;
        if (!im || !im->img)
          continue;
        for (int k = 0; k < refl->tex_count; ++k) {
          const ShaderTexture *rt = &refl->texs[k];
          if (rt->stage != SGL_STAGE_COMPUTE ||
              strcmp(rt->name, d->textures[i].name) != 0)
            continue;
          int wi = vkb_write_index_for_binding(&w, rt->smp_slot);
          if (set_index == 0 && wi >= 0) {
            w.imgs[wi].sampler = im->sampler;
            w.imgs[wi].imageView = im->view;
            w.imgs[wi].imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
          }
          break;
        }
      }
      for (int i = 0; i < d->n_storage_textures; ++i) {
        if (!d->storage_textures[i].name)
          continue;
        VkbImage *im = (VkbImage *)d->storage_textures[i].image;
        if (!im || !im->img)
          continue;
        for (int k = 0; k < refl->storage_tex_count; ++k) {
          const ShaderStorageTexture *st = &refl->storage_texs[k];
          if (strcmp(st->name, d->storage_textures[i].name) != 0)
            continue;
          int binding =
              st->readonly
                  ? vkb_ro_storage_tex_binding(refl, SGL_STAGE_COMPUTE, st)
                  : st->slot;
          bool in_this_set = (st->readonly && set_index == 0) ||
                             (!st->readonly && set_index == 1);
          int wi = vkb_write_index_for_binding(&w, binding);
          if (in_this_set && wi >= 0) {
            w.imgs[wi].sampler = VK_NULL_HANDLE;
            w.imgs[wi].imageView = im->view;
            w.imgs[wi].imageLayout = VK_IMAGE_LAYOUT_GENERAL;
          }
          break;
        }
      }
      for (int i = 0; i < d->n_storage_bufs; ++i) {
        if (!d->storage_bufs[i].name)
          continue;
        VkbBuffer *buf = (VkbBuffer *)d->storage_bufs[i].buf;
        if (!buf || !buf->buf)
          continue;
        for (int k = 0; k < refl->storage_buf_count; ++k) {
          const ShaderStorageBuf *sb = &refl->storage_bufs[k];
          if (strcmp(sb->name, d->storage_bufs[i].name) != 0)
            continue;
          int binding =
              sb->readonly
                  ? vkb_ro_storage_buf_binding(refl, SGL_STAGE_COMPUTE, sb)
                  : vkb_rw_storage_buf_binding(refl, SGL_STAGE_COMPUTE, sb);
          bool in_this_set = (sb->readonly && set_index == 0) ||
                             (!sb->readonly && set_index == 1);
          int wi = vkb_write_index_for_binding(&w, binding);
          if (in_this_set && wi >= 0) {
            w.bufs[wi] = (VkDescriptorBufferInfo){
                .buffer = buf->buf,
                .range = VK_WHOLE_SIZE,
            };
          }
          break;
        }
      }
    }
    vkUpdateDescriptorSets(g.device, (uint32_t)w.count, w.writes, 0, NULL);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_COMPUTE, p->layout,
                            (uint32_t)set_index, 1, &set, 0, NULL);
  }

  vkCmdDispatch(cmd, (uint32_t)(d->groups_x > 0 ? d->groups_x : 1),
                (uint32_t)(d->groups_y > 0 ? d->groups_y : 1),
                (uint32_t)(d->groups_z > 0 ? d->groups_z : 1));

  // Order the storage writes against subsequent reads and park written
  // textures back in their resting sampled state.
  vkb_memory_barrier(cmd);
  for (int i = 0; i < d->n_storage_textures; ++i) {
    VkbImage *im = (VkbImage *)d->storage_textures[i].image;
    if (im && im->img)
      vkb_transition(im, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
  }
}

// --- readback
// -----------------------------------------------------------------------

static int vkb_readback_src_bpp(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_RGBA8:
  case SGL_PF_BGRA8:
    return 4;
  case SGL_PF_R8:
    return 1;
  default:
    return 0;
  }
}

// Flush the open frame command buffer, wait, and reopen it so the caller's
// frame continues (synchronous readback, parity with the other backends).
static void vkb_flush_frame_and_reopen(void) {
  VkCommandBuffer cmd = g.frames[g.slot].cmd;
  vkEndCommandBuffer(cmd);
  vkb_submit_cmd(cmd, /*signal_present=*/false);
  vkb_wait_idle();
  vkResetCommandBuffer(cmd, 0);
  VkCommandBufferBeginInfo bi = {
      .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
      .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
  };
  vkBeginCommandBuffer(cmd, &bi);
  g_current_pip = NULL;
  g_uniforms_dirty[0] = g_uniforms_dirty[1] = true;
}

static bool vkb_readback_image_now(VkbImage *im, int w, int h,
                                   SglPixelFormat src_fmt,
                                   ReadbackResult *out) {
  int bpp = vkb_readback_src_bpp(src_fmt);
  if (bpp == 0) {
    SDL_Log("vk: readback: unsupported format %d", (int)src_fmt);
    return false;
  }
  size_t src_pitch = (size_t)w * bpp;
  VkBuffer rb = VK_NULL_HANDLE;
  VkDeviceMemory rb_mem = VK_NULL_HANDLE;
  if (!vkb_alloc_buffer(src_pitch * h, VK_BUFFER_USAGE_TRANSFER_DST_BIT,
                        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                            VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,
                        &rb, &rb_mem)) {
    SDL_Log("vk: readback: alloc failed");
    return false;
  }

  VkImageLayout prev = im->layout;
  RecordCtx ctx = vkb_record_begin();
  if (!ctx.ok) {
    vkDestroyBuffer(g.device, rb, NULL);
    vkFreeMemory(g.device, rb_mem, NULL);
    return false;
  }
  vkb_image_barrier(ctx.cmd, im->img, VK_IMAGE_ASPECT_COLOR_BIT, im->layout,
                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
  VkBufferImageCopy region = {
      .imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1},
      .imageExtent = {(uint32_t)w, (uint32_t)h, 1},
  };
  vkCmdCopyImageToBuffer(ctx.cmd, im->img, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                         rb, 1, &region);
  vkb_image_barrier(ctx.cmd, im->img, VK_IMAGE_ASPECT_COLOR_BIT,
                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    prev == VK_IMAGE_LAYOUT_UNDEFINED
                        ? VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
                        : prev);
  im->layout = prev == VK_IMAGE_LAYOUT_UNDEFINED
                   ? VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
                   : prev;
  if (ctx.used_oneshot) {
    vkb_record_end(&ctx);
  } else {
    vkb_flush_frame_and_reopen();
  }

  uint8_t *mapped = NULL;
  if (vkMapMemory(g.device, rb_mem, 0, VK_WHOLE_SIZE, 0, (void **)&mapped) !=
      VK_SUCCESS) {
    SDL_Log("vk: readback: map failed");
    vkDestroyBuffer(g.device, rb, NULL);
    vkFreeMemory(g.device, rb_mem, NULL);
    return false;
  }
  size_t dst_stride = (size_t)w * 4;
  uint8_t *rgba = (uint8_t *)malloc(dst_stride * h);
  if (!rgba) {
    vkUnmapMemory(g.device, rb_mem);
    vkDestroyBuffer(g.device, rb, NULL);
    vkFreeMemory(g.device, rb_mem, NULL);
    return false;
  }
  for (int y = 0; y < h; ++y) {
    const uint8_t *srow = mapped + src_pitch * y;
    uint8_t *drow = rgba + dst_stride * y;
    if (src_fmt == SGL_PF_RGBA8) {
      memcpy(drow, srow, src_pitch);
    } else if (src_fmt == SGL_PF_BGRA8) {
      for (int x = 0; x < w; ++x) {
        drow[x * 4 + 0] = srow[x * 4 + 2];
        drow[x * 4 + 1] = srow[x * 4 + 1];
        drow[x * 4 + 2] = srow[x * 4 + 0];
        drow[x * 4 + 3] = srow[x * 4 + 3];
      }
    } else { // R8
      for (int x = 0; x < w; ++x) {
        uint8_t v = srow[x];
        drow[x * 4 + 0] = v;
        drow[x * 4 + 1] = v;
        drow[x * 4 + 2] = v;
        drow[x * 4 + 3] = 255;
      }
    }
  }
  vkUnmapMemory(g.device, rb_mem);
  vkDestroyBuffer(g.device, rb, NULL);
  vkFreeMemory(g.device, rb_mem, NULL);

  out->w = w;
  out->h = h;
  out->stride = (int)dst_stride;
  out->fmt = SGL_PF_RGBA8;
  out->data = rgba;
  out->data_bytes = dst_stride * h;
  return true;
}

typedef struct VkbReadbackRequest {
  ReadbackResult rb;
} VkbReadbackRequest;

static bool vkb_request_readback_image(App *app, BackendImage image, int w,
                                       int h, SglPixelFormat src_fmt,
                                       BackendReadback *out) {
  (void)app;
  if (!out)
    return false;
  *out = 0;
  VkbImage *im = (VkbImage *)image;
  if (!im || !im->img || w <= 0 || h <= 0)
    return false;
  VkbReadbackRequest *req =
      (VkbReadbackRequest *)calloc(1, sizeof(VkbReadbackRequest));
  if (!vkb_readback_image_now(im, w, h, src_fmt, &req->rb)) {
    free(req);
    return false;
  }
  *out = (BackendReadback)req;
  return true;
}

static ReadbackPollStatus vkb_poll_readback(BackendReadback h,
                                            ReadbackResult *out) {
  if (!h || !out)
    return READBACK_POLL_ERROR;
  VkbReadbackRequest *req = (VkbReadbackRequest *)h;
  *out = req->rb;
  memset(&req->rb, 0, sizeof(req->rb));
  return READBACK_POLL_READY;
}

static void vkb_destroy_readback(BackendReadback h) {
  if (!h)
    return;
  VkbReadbackRequest *req = (VkbReadbackRequest *)h;
  if (req->rb.data)
    free(req->rb.data);
  free(req);
}

// --- capture
// -----------------------------------------------------------------------

// Copy the current backbuffer to a host buffer, submit the frame command
// buffer (without presenting) and wait, then write the PNG. end_frame sees
// submitted_before_present and only presents.
static bool vkb_capture(App *app, const char *path) {
  (void)app;
  if (!g.recording || !g.have_acquired) {
    SDL_Log("vk: capture: no open frame");
    return false;
  }
  int w = g.sw_w, h = g.sw_h;
  size_t pitch = (size_t)w * 4;
  VkBuffer rb = VK_NULL_HANDLE;
  VkDeviceMemory rb_mem = VK_NULL_HANDLE;
  if (!vkb_alloc_buffer(pitch * h, VK_BUFFER_USAGE_TRANSFER_DST_BIT,
                        VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                            VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,
                        &rb, &rb_mem)) {
    SDL_Log("vk: capture: readback alloc failed");
    return false;
  }

  VkCommandBuffer cmd = g.frames[g.slot].cmd;
  vkb_image_barrier(cmd, g.sc_images[g.bb_index], VK_IMAGE_ASPECT_COLOR_BIT,
                    g.sc_layouts[g.bb_index],
                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
  VkBufferImageCopy region = {
      .imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1},
      .imageExtent = {(uint32_t)w, (uint32_t)h, 1},
  };
  vkCmdCopyImageToBuffer(cmd, g.sc_images[g.bb_index],
                         VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, rb, 1, &region);
  vkb_image_barrier(cmd, g.sc_images[g.bb_index], VK_IMAGE_ASPECT_COLOR_BIT,
                    VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                    VK_IMAGE_LAYOUT_PRESENT_SRC_KHR);
  g.sc_layouts[g.bb_index] = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;

  vkEndCommandBuffer(cmd);
  vkb_submit_cmd(cmd, /*signal_present=*/true);
  g.recording = false;
  g.submitted_before_present = true;
  vkb_wait_idle();

  uint8_t *mapped = NULL;
  if (vkMapMemory(g.device, rb_mem, 0, VK_WHOLE_SIZE, 0, (void **)&mapped) !=
      VK_SUCCESS) {
    SDL_Log("vk: capture: map failed");
    vkDestroyBuffer(g.device, rb, NULL);
    vkFreeMemory(g.device, rb_mem, NULL);
    return false;
  }
  uint8_t *rgba = (uint8_t *)malloc(pitch * h);
  if (!rgba) {
    vkUnmapMemory(g.device, rb_mem);
    vkDestroyBuffer(g.device, rb, NULL);
    vkFreeMemory(g.device, rb_mem, NULL);
    return false;
  }
  if (g.sc_fmt_sgl == SGL_PF_BGRA8) {
    for (int y = 0; y < h; ++y) {
      const uint8_t *srow = mapped + pitch * y;
      uint8_t *drow = rgba + pitch * y;
      for (int x = 0; x < w; ++x) {
        drow[x * 4 + 0] = srow[x * 4 + 2];
        drow[x * 4 + 1] = srow[x * 4 + 1];
        drow[x * 4 + 2] = srow[x * 4 + 0];
        drow[x * 4 + 3] = srow[x * 4 + 3];
      }
    }
  } else {
    memcpy(rgba, mapped, pitch * h);
  }
  vkUnmapMemory(g.device, rb_mem);
  vkDestroyBuffer(g.device, rb, NULL);
  vkFreeMemory(g.device, rb_mem, NULL);

  int ok = stbi_write_png(path, w, h, 4, rgba, (int)pitch);
  free(rgba);
  if (!ok) {
    SDL_Log("vk: capture: stbi_write_png failed");
    return false;
  }
  return true;
}

static SglPixelFormat vkb_swapchain_color_format(App *app) {
  (void)app;
  return g.sc_fmt_sgl ? g.sc_fmt_sgl : SGL_PF_RGBA8;
}

const RenderBackend g_backend_vulkan = {
    "vulkan",
    vkb_init,
    vkb_shutdown,
    vkb_begin_frame,
    vkb_end_frame,
    vkb_make_buffer,
    vkb_make_image,
    vkb_make_shader,
    vkb_make_pipeline,
    vkb_destroy_buffer,
    vkb_destroy_image,
    vkb_destroy_shader,
    vkb_destroy_pipeline,
    vkb_update_buffer,
    vkb_update_image,
    vkb_begin_pass,
    vkb_end_pass,
    vkb_apply_pipeline,
    vkb_apply_bindings,
    vkb_apply_uniforms,
    vkb_draw,
    vkb_set_scissor,
    vkb_dispatch,
    vkb_request_readback_image,
    vkb_poll_readback,
    vkb_destroy_readback,
    vkb_capture,
    /*capture_before_end_frame=*/true,
    vkb_swapchain_color_format,
};
