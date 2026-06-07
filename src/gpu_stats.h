#pragma once
#include "enums.h"
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum GpuStatKind {
  GPU_STAT_BUFFER,
  GPU_STAT_TEXTURE,
  GPU_STAT_SAMPLER,
  GPU_STAT_VIEW,
  GPU_STAT_SHADER,
  GPU_STAT_PIPELINE,
  GPU_STAT_TRANSFER_BUFFER,
  GPU_STAT_FENCE,
  GPU_STAT_SURFACE_TEXTURE,
  GPU_STAT_SURFACE_VIEW,
  GPU_STAT_KIND_COUNT,
} GpuStatKind;

void gpu_stats_init_from_env(void);
void gpu_stats_create(GpuStatKind kind, uint64_t bytes);
void gpu_stats_destroy(GpuStatKind kind, uint64_t bytes);
void gpu_stats_frame(uint64_t frame, const char *backend_name);
void gpu_stats_shutdown(const char *backend_name);
uint64_t gpu_stats_image_bytes(SglPixelFormat fmt, int w, int h);

#ifdef __cplusplus
}
#endif
