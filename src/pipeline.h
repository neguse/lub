#pragma once
#include "sokol_gfx.h"
#include "shader.h"
#include "enums.h"
#include <stdint.h>
#include <stdbool.h>

typedef struct PipelineKey {
    uint32_t shader_id;
    uint8_t blend, depth_test, depth_write, cull, primitive;
    uint8_t color_fmt;
    uint8_t depth_fmt;
} PipelineKey;

typedef struct PipelineEntry {
    PipelineKey key;
    sg_pipeline pip;
    struct PipelineEntry *next;
} PipelineEntry;

#define PIPELINE_BUCKETS 64

typedef struct PipelineCache {
    PipelineEntry *buckets[PIPELINE_BUCKETS];
} PipelineCache;

void pipeline_cache_init(PipelineCache *c);
void pipeline_cache_shutdown(PipelineCache *c);

sg_pipeline pipeline_cache_get(
    PipelineCache *c,
    sg_shader sh, const ShaderReflection *refl,
    SglBlend blend, bool depth_test, bool depth_write,
    SglCull cull, SglPrimitive prim,
    sg_pixel_format color_fmt, sg_pixel_format depth_fmt);
