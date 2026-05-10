#include "resources.h"
#include "backend.h"
#include <string.h>
#include <stdlib.h>

_Static_assert((RES_BUCKETS & (RES_BUCKETS - 1)) == 0, "RES_BUCKETS must be a power of 2");

static uint32_t hash_str(const char *s) {
    uint32_t h = 2166136261u;
    while (*s) { h ^= (uint8_t)*s++; h *= 16777619u; }
    return h;
}

void res_table_init(ResTable *t) {
    memset(t, 0, sizeof(*t));
}

static void res_entry_release(ResEntry *e) {
    switch (e->kind) {
        case RES_BUFFER:
            if (e->u.buf.h) g_backend->destroy_buffer(e->u.buf.h);
            break;
        case RES_TEXTURE:
            if (e->u.tex.h) g_backend->destroy_image(e->u.tex.h);
            break;
        case RES_SHADER:
            if (e->u.sh.h) g_backend->destroy_shader(e->u.sh.h);
            break;
        default: break;
    }
    free(e->key);
    free(e);
}

void res_table_shutdown(ResTable *t) {
    for (int i = 0; i < RES_BUCKETS; ++i) {
        ResEntry *e = t->buckets[i];
        while (e) {
            ResEntry *n = e->next;
            res_entry_release(e);
            e = n;
        }
        t->buckets[i] = NULL;
    }
}

ResEntry *res_table_get(ResTable *t, const char *key) {
    uint32_t i = hash_str(key) & (RES_BUCKETS - 1);
    for (ResEntry *e = t->buckets[i]; e; e = e->next) {
        if (strcmp(e->key, key) == 0) return e;
    }
    return NULL;
}

ResEntry *res_table_get_or_create(ResTable *t, const char *key, ResKind kind) {
    ResEntry *e = res_table_get(t, key);
    if (e) {
        if (e->kind != RES_NONE && e->kind != kind) return NULL; // 種別衝突
        return e;
    }
    uint32_t i = hash_str(key) & (RES_BUCKETS - 1);
    e = (ResEntry*)calloc(1, sizeof(ResEntry));
    if (!e) return NULL;
    e->key = strdup(key);
    if (!e->key) { free(e); return NULL; }
    e->kind = kind;
    e->version = -1;
    e->last_seen_frame = -1;
    e->next = t->buckets[i];
    t->buckets[i] = e;
    return e;
}

void res_table_touch(ResEntry *e, int64_t frame_index) {
    e->last_seen_frame = frame_index;
}
