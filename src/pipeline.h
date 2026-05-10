#pragma once
#include "backend.h"
#include "shader.h"
#include "enums.h"
#include <stdint.h>
#include <stdbool.h>

#define PIPELINE_BUCKETS 64

typedef struct PipelineKey {
    uintptr_t shader_handle;
    uint8_t blend, depth_test, depth_write, cull, primitive, color_fmt;
    uint8_t _pad[2];
} PipelineKey;

typedef struct PipelineEntry {
    PipelineKey key;
    BackendPipeline pip;
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
    SglPixelFormat color_fmt);
