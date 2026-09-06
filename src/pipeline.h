#pragma once
#include "backend.h"
#include "enums.h"
#include "shader.h"
#include <stdbool.h>
#include <stdint.h>

#define PIPELINE_BUCKETS 64

typedef struct PipelineKey {
  uintptr_t shader_handle;
  uint8_t blend, depth_test, depth_write, cull, primitive, has_depth;
  uint8_t n_color_targets;
  uint8_t color_fmts[SGL_MAX_COLOR_TARGETS];
  uint8_t depth_fmt;
  uint8_t is_compute; // 1 = compute pipeline (all graphics fields are zero)
  // Bit i = reflection texture i is bound to a depth-format texture this
  // draw. webgpu needs a different bind group layout (unfilterable-float)
  // for depth sampling, so it is part of the pipeline identity.
  uint8_t depth_tex_mask;
  uint8_t _pad[2]; // memset-zeroed; memcmp would otherwise hit indeterminate
                   // padding
} PipelineKey;

typedef struct PipelineEntry {
  PipelineKey key;
  BackendPipeline pip;
  int64_t last_seen_frame;
  struct PipelineEntry *next;
} PipelineEntry;

typedef struct PipelineCache {
  PipelineEntry *buckets[PIPELINE_BUCKETS];
} PipelineCache;

void pipeline_cache_init(PipelineCache *c);
void pipeline_cache_shutdown(PipelineCache *c);

BackendPipeline pipeline_cache_get(
    PipelineCache *c, BackendShader sh, const ShaderReflection *refl,
    SglBlend blend, bool depth_test, bool depth_write, SglCull cull,
    SglPrimitive prim, int n_color_targets, const SglPixelFormat *color_fmts,
    bool has_depth, SglPixelFormat depth_fmt, uint8_t depth_tex_mask,
    int64_t current_frame);

// Compute pipeline lookup. The same cache holds both render and compute
// pipelines, keyed by `is_compute` so they never collide on shader_handle.
BackendPipeline pipeline_cache_get_compute(PipelineCache *c, BackendShader sh,
                                           const ShaderReflection *refl,
                                           int64_t current_frame);

// 指定 shader handle を参照する全 pipeline entry を破棄しキャッシュから外す。
// shader recompile で旧 handle が無効になる際に呼ぶ。
void pipeline_cache_invalidate_shader(PipelineCache *c, uintptr_t old_shader);

// last_seen_frame が (current_frame - max_unused_frames)
// より古いエントリを破棄。 last_seen_frame < 0 (一度も touch されていない)
// のエントリはスキップ。
void pipeline_cache_sweep(PipelineCache *c, int64_t current_frame,
                          int64_t max_unused_frames);
