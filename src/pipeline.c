#include "pipeline.h"
#include "backend.h"
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
            if (e->pip) g_backend->destroy_pipeline(e->pip);
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

BackendPipeline pipeline_cache_get(
    PipelineCache *c, BackendShader sh, const ShaderReflection *refl,
    SglBlend blend, bool dt, bool dw, SglCull cull, SglPrimitive prim,
    SglPixelFormat cfmt, bool has_depth)
{
    // memset before designated init: designated initialization does not strictly
    // guarantee struct padding bytes are zeroed. Since the cache compares keys
    // with memcmp, indeterminate padding could cause false misses on lookups.
    PipelineKey k;
    memset(&k, 0, sizeof(k));
    k.shader_handle = sh;
    k.blend = (uint8_t)blend;
    k.depth_test = dt ? 1 : 0;
    k.depth_write = dw ? 1 : 0;
    k.cull = (uint8_t)cull;
    k.primitive = (uint8_t)prim;
    k.color_fmt = (uint8_t)cfmt;
    k.has_depth = has_depth ? 1 : 0;
    uint32_t bi = hash_key(&k) & (PIPELINE_BUCKETS - 1);
    for (PipelineEntry *e = c->buckets[bi]; e; e = e->next) {
        if (memcmp(&e->key, &k, sizeof(k)) == 0) return e->pip;
    }

    PipelineDesc desc = {
        .shader = sh,
        .refl = refl,
        .blend = blend,
        .depth_test = dt,
        .depth_write = dw,
        .cull = cull,
        .primitive = prim,
        .color_fmt = cfmt,
        .has_depth = has_depth,
    };
    BackendPipeline pip = g_backend->make_pipeline(&desc);

    PipelineEntry *e = (PipelineEntry*)calloc(1, sizeof(PipelineEntry));
    if (!e) {
        // OOM on cache entry: pipeline is live but un-cached. Caller still gets
        // a working pipeline; resource will be reclaimed at backend shutdown.
        return pip;
    }
    e->key = k;
    e->pip = pip;
    e->next = c->buckets[bi];
    c->buckets[bi] = e;
    return pip;
}

void pipeline_cache_invalidate_shader(PipelineCache *c, uintptr_t old_shader) {
    for (int i = 0; i < PIPELINE_BUCKETS; ++i) {
        PipelineEntry **prev = &c->buckets[i];
        PipelineEntry *e = c->buckets[i];
        while (e) {
            PipelineEntry *next = e->next;
            if (e->key.shader_handle == old_shader) {
                if (e->pip) g_backend->destroy_pipeline(e->pip);
                *prev = next;
                free(e);
            } else {
                prev = &e->next;
            }
            e = next;
        }
    }
}
