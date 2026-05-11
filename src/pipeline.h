#pragma once
#include "backend.h"
#include "shader.h"
#include "enums.h"
#include <stdint.h>
#include <stdbool.h>

#define PIPELINE_BUCKETS 64

typedef struct PipelineKey {
    uintptr_t shader_handle;
    uint8_t blend, depth_test, depth_write, cull, primitive, color_fmt, has_depth;
    uint8_t _pad[1];
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
    PipelineCache *c,
    BackendShader sh, const ShaderReflection *refl,
    SglBlend blend, bool depth_test, bool depth_write,
    SglCull cull, SglPrimitive prim,
    SglPixelFormat color_fmt, bool has_depth,
    int64_t current_frame);

// 指定 shader handle を参照する全 pipeline entry を破棄しキャッシュから外す。
// shader recompile で旧 handle が無効になる際に呼ぶ。
void pipeline_cache_invalidate_shader(PipelineCache *c, uintptr_t old_shader);

// last_seen_frame が (current_frame - max_unused_frames) より古いエントリを破棄。
// last_seen_frame < 0 (一度も touch されていない) のエントリはスキップ。
void pipeline_cache_sweep(PipelineCache *c,
                          int64_t current_frame,
                          int64_t max_unused_frames);
