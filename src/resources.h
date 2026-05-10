#pragma once
#include "enums.h"
#include "shader.h"
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>

typedef enum { RES_NONE = 0, RES_BUFFER, RES_TEXTURE, RES_SHADER } ResKind;

#define RES_BUCKETS 256

typedef struct ResEntry {
    char *key;            // strdup'd
    ResKind kind;
    int64_t version;
    int64_t last_seen_frame;
    union {
        struct { uintptr_t h; SglBufferType type; size_t size_bytes; } buf;
        struct { uintptr_t h; int w, h_; SglPixelFormat fmt; } tex;
        struct { uintptr_t h; ShaderReflection refl; } sh;
    } u;
    struct ResEntry *next;
} ResEntry;

typedef struct ResTable {
    ResEntry *buckets[RES_BUCKETS];
} ResTable;

void res_table_init(ResTable *t);
void res_table_shutdown(ResTable *t);

ResEntry *res_table_get(ResTable *t, const char *key);
ResEntry *res_table_get_or_create(ResTable *t, const char *key, ResKind kind);
void res_table_touch(ResEntry *e, int64_t frame_index);
