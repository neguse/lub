#include "pipeline.h"
#include <string.h>
#include <stdlib.h>

_Static_assert((PIPELINE_BUCKETS & (PIPELINE_BUCKETS - 1)) == 0,
               "PIPELINE_BUCKETS must be power of 2");

void pipeline_cache_init(PipelineCache *c) {
    memset(c, 0, sizeof(*c));
}

void pipeline_cache_shutdown(PipelineCache *c) {
    for (int i = 0; i < PIPELINE_BUCKETS; ++i) {
        PipelineEntry *e = c->buckets[i];
        while (e) {
            PipelineEntry *n = e->next;
            if (e->pip.id != 0) sg_destroy_pipeline(e->pip);
            free(e);
            e = n;
        }
        c->buckets[i] = NULL;
    }
}

static uint32_t hash_key(const PipelineKey *k) {
    const uint8_t *p = (const uint8_t*)k;
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < sizeof(*k); ++i) {
        h ^= p[i];
        h *= 16777619u;
    }
    return h;
}

static sg_blend_state to_sokol_blend(SglBlend b) {
    sg_blend_state bs = {0};
    switch (b) {
        case SGL_BLEND_ALPHA:
            bs.enabled = true;
            bs.src_factor_rgb = SG_BLENDFACTOR_SRC_ALPHA;
            bs.dst_factor_rgb = SG_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
            bs.src_factor_alpha = SG_BLENDFACTOR_ONE;
            bs.dst_factor_alpha = SG_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
            break;
        case SGL_BLEND_ADDITIVE:
            bs.enabled = true;
            bs.src_factor_rgb = SG_BLENDFACTOR_ONE;
            bs.dst_factor_rgb = SG_BLENDFACTOR_ONE;
            break;
        case SGL_BLEND_MULTIPLY:
            bs.enabled = true;
            bs.src_factor_rgb = SG_BLENDFACTOR_DST_COLOR;
            bs.dst_factor_rgb = SG_BLENDFACTOR_ZERO;
            break;
        default: break;  // SGL_BLEND_NONE → disabled
    }
    return bs;
}

sg_pipeline pipeline_cache_get(
    PipelineCache *c, sg_shader sh, const ShaderReflection *refl,
    SglBlend blend, bool dt, bool dw, SglCull cull, SglPrimitive prim,
    sg_pixel_format cfmt, sg_pixel_format dfmt)
{
    // memset before designated init: designated initialization does not strictly
    // guarantee struct padding bytes are zeroed (compiler-defined). Since the
    // cache compares keys with memcmp, indeterminate padding could cause false
    // misses on lookups.
    PipelineKey k;
    memset(&k, 0, sizeof(k));
    k.shader_id = sh.id;
    k.blend = (uint8_t)blend;
    k.depth_test = dt ? 1 : 0;
    k.depth_write = dw ? 1 : 0;
    k.cull = (uint8_t)cull;
    k.primitive = (uint8_t)prim;
    k.color_fmt = (uint8_t)cfmt;
    k.depth_fmt = (uint8_t)dfmt;
    uint32_t bi = hash_key(&k) & (PIPELINE_BUCKETS - 1);
    for (PipelineEntry *e = c->buckets[bi]; e; e = e->next) {
        if (memcmp(&e->key, &k, sizeof(k)) == 0) return e->pip;
    }

    sg_pipeline_desc desc = {
        .shader = sh,
        .colors[0] = { .pixel_format = cfmt, .blend = to_sokol_blend(blend) },
        .depth = {
            .pixel_format = dfmt,
            .compare = dt ? SG_COMPAREFUNC_LESS_EQUAL : SG_COMPAREFUNC_ALWAYS,
            .write_enabled = dw,
        },
        .cull_mode =
            (cull == SGL_CULL_BACK)  ? SG_CULLMODE_BACK :
            (cull == SGL_CULL_FRONT) ? SG_CULLMODE_FRONT :
                                       SG_CULLMODE_NONE,
        .primitive_type =
            (prim == SGL_PRIM_LINES)          ? SG_PRIMITIVETYPE_LINES :
            (prim == SGL_PRIM_LINE_STRIP)     ? SG_PRIMITIVETYPE_LINE_STRIP :
            (prim == SGL_PRIM_POINTS)         ? SG_PRIMITIVETYPE_POINTS :
            (prim == SGL_PRIM_TRIANGLE_STRIP) ? SG_PRIMITIVETYPE_TRIANGLE_STRIP :
                                                SG_PRIMITIVETYPE_TRIANGLES,
    };

    // Vertex layout: derive from the reflection's attrs[]
    for (int i = 0; i < refl->attr_count; ++i) {
        sg_vertex_format fmt;
        switch (refl->attrs[i].comp_count) {
            case 1: fmt = SG_VERTEXFORMAT_FLOAT;  break;
            case 2: fmt = SG_VERTEXFORMAT_FLOAT2; break;
            case 3: fmt = SG_VERTEXFORMAT_FLOAT3; break;
            case 4: fmt = SG_VERTEXFORMAT_FLOAT4; break;
            default: fmt = SG_VERTEXFORMAT_FLOAT3;
        }
        desc.layout.attrs[refl->attrs[i].slot] = (sg_vertex_attr_state){
            .buffer_index = 0,
            .offset = refl->attrs[i].offset_floats * (int)sizeof(float),
            .format = fmt,
        };
    }
    desc.layout.buffers[0].stride = refl->vertex_stride_floats * (int)sizeof(float);

    sg_pipeline pip = sg_make_pipeline(&desc);

    PipelineEntry *e = (PipelineEntry*)calloc(1, sizeof(PipelineEntry));
    if (!e) {
        // OOM on cache entry: pipeline is live but un-cached. Caller still gets
        // a working sg_pipeline; resource will be reclaimed at sg_shutdown.
        // Returning here under sustained OOM would leak a new pipeline per call,
        // but the alternative (destroy + return {0}) would force every call site
        // to handle a sentinel. PoC-tier: accept the trade-off.
        return pip;
    }
    e->key = k;
    e->pip = pip;
    e->next = c->buckets[bi];
    c->buckets[bi] = e;
    return pip;
}
