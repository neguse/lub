#include "gpu_stats.h"
#include <SDL3/SDL.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

typedef struct GpuStatCounter {
  uint64_t live;
  uint64_t created;
  uint64_t destroyed;
  uint64_t live_bytes;
  uint64_t created_bytes;
  uint64_t destroyed_bytes;
} GpuStatCounter;

typedef struct GpuStatsState {
  bool enabled;
  uint64_t every;
  GpuStatCounter counters[GPU_STAT_KIND_COUNT];
} GpuStatsState;

static GpuStatsState g_gpu_stats;

static bool env_flag(const char *name) {
  const char *s = SDL_getenv(name);
  return s && s[0] && SDL_strcmp(s, "0") != 0 &&
         SDL_strcasecmp(s, "false") != 0;
}

static uint64_t env_u64(const char *name, uint64_t fallback) {
  const char *s = SDL_getenv(name);
  if (!s || !s[0])
    return fallback;
  char *end = NULL;
  unsigned long long v = strtoull(s, &end, 10);
  return end != s ? (uint64_t)v : fallback;
}

void gpu_stats_init_from_env(void) {
  memset(&g_gpu_stats, 0, sizeof(g_gpu_stats));
  g_gpu_stats.every = env_u64("LUB_GPU_STATS_EVERY", 0);
  g_gpu_stats.enabled = env_flag("LUB_GPU_STATS") || g_gpu_stats.every > 0;
  if (g_gpu_stats.enabled && g_gpu_stats.every == 0)
    g_gpu_stats.every = 300;
}

void gpu_stats_create(GpuStatKind kind, uint64_t bytes) {
  if (kind < 0 || kind >= GPU_STAT_KIND_COUNT)
    return;
  GpuStatCounter *c = &g_gpu_stats.counters[kind];
  c->live++;
  c->created++;
  c->live_bytes += bytes;
  c->created_bytes += bytes;
}

void gpu_stats_destroy(GpuStatKind kind, uint64_t bytes) {
  if (kind < 0 || kind >= GPU_STAT_KIND_COUNT)
    return;
  GpuStatCounter *c = &g_gpu_stats.counters[kind];
  if (c->live > 0)
    c->live--;
  c->destroyed++;
  c->destroyed_bytes += bytes;
  c->live_bytes = c->live_bytes >= bytes ? c->live_bytes - bytes : 0;
}

uint64_t gpu_stats_image_bytes(SglPixelFormat fmt, int w, int h) {
  if (w <= 0 || h <= 0)
    return 0;
  uint64_t bpp = 4;
  switch (fmt) {
  case SGL_PF_R8:
    bpp = 1;
    break;
  case SGL_PF_RG8:
  case SGL_PF_R16F:
  case SGL_PF_DEPTH16:
    bpp = 2;
    break;
  case SGL_PF_RG16F:
  case SGL_PF_R32F:
    bpp = 4;
    break;
  case SGL_PF_RGBA16F:
    bpp = 8;
    break;
  case SGL_PF_RGBA32F:
    bpp = 16;
    break;
  case SGL_PF_DEPTH32F:
  case SGL_PF_DEPTH24_STENCIL8:
  case SGL_PF_RGBA8:
  case SGL_PF_BGRA8:
  default:
    bpp = 4;
    break;
  }
  return (uint64_t)w * (uint64_t)h * bpp;
}

static void gpu_stats_log(const char *label, uint64_t frame,
                          const char *backend_name) {
  const GpuStatCounter *b = &g_gpu_stats.counters[GPU_STAT_BUFFER];
  const GpuStatCounter *t = &g_gpu_stats.counters[GPU_STAT_TEXTURE];
  const GpuStatCounter *s = &g_gpu_stats.counters[GPU_STAT_SAMPLER];
  const GpuStatCounter *v = &g_gpu_stats.counters[GPU_STAT_VIEW];
  const GpuStatCounter *sh = &g_gpu_stats.counters[GPU_STAT_SHADER];
  const GpuStatCounter *p = &g_gpu_stats.counters[GPU_STAT_PIPELINE];
  const GpuStatCounter *tb = &g_gpu_stats.counters[GPU_STAT_TRANSFER_BUFFER];
  const GpuStatCounter *f = &g_gpu_stats.counters[GPU_STAT_FENCE];
  const GpuStatCounter *st = &g_gpu_stats.counters[GPU_STAT_SURFACE_TEXTURE];
  const GpuStatCounter *sv = &g_gpu_stats.counters[GPU_STAT_SURFACE_VIEW];

  SDL_Log(
      "LUB_GPU_STATS label=%s frame=%llu backend=%s "
      "buffers_live=%llu buffers_created=%llu buffers_destroyed=%llu "
      "buffer_bytes_live=%llu buffer_bytes_created=%llu "
      "buffer_bytes_destroyed=%llu textures_live=%llu "
      "textures_created=%llu textures_destroyed=%llu "
      "texture_bytes_live=%llu texture_bytes_created=%llu "
      "texture_bytes_destroyed=%llu samplers_live=%llu "
      "samplers_created=%llu samplers_destroyed=%llu views_live=%llu "
      "views_created=%llu views_destroyed=%llu shaders_live=%llu "
      "shaders_created=%llu shaders_destroyed=%llu pipelines_live=%llu "
      "pipelines_created=%llu pipelines_destroyed=%llu "
      "transfer_live=%llu transfer_created=%llu transfer_destroyed=%llu "
      "transfer_bytes_live=%llu transfer_bytes_created=%llu "
      "transfer_bytes_destroyed=%llu fences_live=%llu fences_created=%llu "
      "fences_destroyed=%llu surface_textures_live=%llu "
      "surface_textures_created=%llu surface_textures_destroyed=%llu "
      "surface_views_live=%llu surface_views_created=%llu "
      "surface_views_destroyed=%llu",
      label ? label : "frame", (unsigned long long)frame,
      backend_name ? backend_name : "(none)", (unsigned long long)b->live,
      (unsigned long long)b->created, (unsigned long long)b->destroyed,
      (unsigned long long)b->live_bytes, (unsigned long long)b->created_bytes,
      (unsigned long long)b->destroyed_bytes, (unsigned long long)t->live,
      (unsigned long long)t->created, (unsigned long long)t->destroyed,
      (unsigned long long)t->live_bytes, (unsigned long long)t->created_bytes,
      (unsigned long long)t->destroyed_bytes, (unsigned long long)s->live,
      (unsigned long long)s->created, (unsigned long long)s->destroyed,
      (unsigned long long)v->live, (unsigned long long)v->created,
      (unsigned long long)v->destroyed, (unsigned long long)sh->live,
      (unsigned long long)sh->created, (unsigned long long)sh->destroyed,
      (unsigned long long)p->live, (unsigned long long)p->created,
      (unsigned long long)p->destroyed, (unsigned long long)tb->live,
      (unsigned long long)tb->created, (unsigned long long)tb->destroyed,
      (unsigned long long)tb->live_bytes, (unsigned long long)tb->created_bytes,
      (unsigned long long)tb->destroyed_bytes, (unsigned long long)f->live,
      (unsigned long long)f->created, (unsigned long long)f->destroyed,
      (unsigned long long)st->live, (unsigned long long)st->created,
      (unsigned long long)st->destroyed, (unsigned long long)sv->live,
      (unsigned long long)sv->created, (unsigned long long)sv->destroyed);
}

void gpu_stats_frame(uint64_t frame, const char *backend_name) {
  if (!g_gpu_stats.enabled || g_gpu_stats.every == 0)
    return;
  if (frame == 0 || frame % g_gpu_stats.every != 0)
    return;
  gpu_stats_log("frame", frame, backend_name);
}

void gpu_stats_shutdown(const char *backend_name) {
  if (!g_gpu_stats.enabled)
    return;
  gpu_stats_log("shutdown", 0, backend_name);
}
