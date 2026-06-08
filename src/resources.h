#pragma once
#include "enums.h"
#include "shader.h"
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef enum { RES_NONE = 0, RES_BUFFER, RES_TEXTURE, RES_SHADER } ResKind;

#define RES_BUCKETS 256

typedef struct ResEntry {
  char *key; // strdup'd
  ResKind kind;
  int64_t version;
  int64_t last_seen_frame;
  union {
    struct {
      uintptr_t h;
      SglBufferType type;
      size_t size_bytes;
    } buf;
    struct {
      uintptr_t h;
      int w, h_;
      SglPixelFormat fmt;
      SglFilter filter;
      SglWrap wrap;
      bool is_target;
      bool storage;
    } tex;
    struct {
      uintptr_t h;
      ShaderReflection refl;
    } sh;
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

// Callback invoked just before a RES_SHADER entry is destroyed, so the caller
// (typically app.c) can invalidate pipelines that hold the now-stale handle.
typedef void (*ResShaderInvalidateFn)(void *ctx, uintptr_t old_shader);

// Release entries whose last_seen_frame is older than (current_frame -
// max_unused_frames). Entries with last_seen_frame < 0 (never touched) are
// left alone — config callbacks may create resources before the first frame.
void res_table_sweep(ResTable *t, int64_t current_frame,
                     int64_t max_unused_frames,
                     ResShaderInvalidateFn on_shader_release, void *ctx);
