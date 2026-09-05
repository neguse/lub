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
  // C API の handle (lub_api.h の LubHandle)。entry の寿命の間は同じ値で、
  // sweep で消えたあとは stale。1 始まりの通し番号。
  int32_t handle;
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
  // Effective-version counter for "content changed" declarations (use_* with
  // a nil version).  Lives in the table so it can never desync from the
  // entries it versions: entry hot reloads keep both, a fresh player resets
  // both together.
  int64_t revision;
  ResEntry *buckets[RES_BUCKETS];
  // handle → entry。index = handle。NULL は sweep 済み (stale)。
  ResEntry **by_handle;
  int32_t handle_cap;
  int32_t next_handle;
} ResTable;

void res_table_init(ResTable *t);
void res_table_shutdown(ResTable *t);

// Issues a fresh effective version for a "content changed" declaration.
// Monotonic within the table's lifetime, so it never repeats a value a live
// entry may still store.
int64_t res_table_next_revision(ResTable *t);

ResEntry *res_table_get(ResTable *t, const char *key);
// key の長さ指定版 (NUL 終端を要求しない LubStr 向け)。
ResEntry *res_table_get_n(ResTable *t, const char *key, size_t len);
ResEntry *res_table_get_by_handle(ResTable *t, int32_t handle);
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
