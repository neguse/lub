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
  // C API handle (LubHandle in lub_api.h).
  // Keeps the same value for the lifetime of the entry, and is stale after the
  // entry is removed by sweep. A 1-based serial number; when it reaches the
  // int32 maximum, it wraps back to 1, skipping live values.
  int32_t handle;
  int64_t version;
  int64_t last_seen_frame;
  // The App.declare_epoch of the last declaration, and the version at that
  // time. Differs from version when compile fails.
  uint64_t declared_epoch;
  int64_t declared_version;
  union {
    struct {
      uintptr_t h;
      SglBufferType type;
      size_t size_bytes;     // Declared size. The range visible to shaders
      size_t capacity_bytes; // Size of the backend object
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
      // Version whose compile failed.
      // Declarations with the same version are not compiled again until it is
      // fixed.
      bool failed;
      int64_t failed_version;
      // Vertex shader source used by Gfx.Pick to replay draws (graphics shaders
      // only). pick_h is the shader that combines it with a fragment shader
      // writing the draw index, created on the first Pick
      char *vs_src;
      uintptr_t pick_h;
      ShaderReflection pick_refl;
    } sh;
  } u;
  struct ResEntry *next;
} ResEntry;

// Record of the handles and keys of swept entries (to show the key in errors
// about stale handles). Keeps only the newest RES_SWEPT_KEEP records and
// forgets older ones.
#define RES_SWEPT_KEEP 64
typedef struct ResSwept {
  int32_t handle; // 0 means empty
  ResKind kind;
  int64_t unused_frames; // Number of frames since the last use, at the time of
                         // the sweep
  char *key;
} ResSwept;

typedef struct ResTable {
  // Effective-version counter for "content changed" declarations (use_* with
  // a NULL version).  Lives in the table so it can never desync from the
  // entries it versions: entry hot reloads keep both, a fresh player resets
  // both together.
  int64_t revision;
  ResEntry *buckets[RES_BUCKETS];
  // handle → entry.
  // Open addressing table holding only live entries (linear probing, capacity
  // is a power of 2, load at most 1/2). Entries are removed on sweep, and the
  // table shrinks when free slots grow.
  ResEntry **by_handle;
  int32_t handle_cap;
  int32_t handle_count;
  // Location of the last issued handle (the counter of res_table_init)
  int32_t *next_handle;
  ResSwept swept[RES_SWEPT_KEEP];
  int32_t swept_next;
} ResTable;

// Handle serial numbers come from counter (counter must outlive the table).
// Tables that share a counter never have overlapping handles, so passing a
// handle of another table never points to a different entry.
void res_table_init(ResTable *t, int32_t *counter);
void res_table_shutdown(ResTable *t);

// Issues a fresh effective version for a "content changed" declaration.
// Monotonic within the table's lifetime, so it never repeats a value a live
// entry may still store.
int64_t res_table_next_revision(ResTable *t);

ResEntry *res_table_get(ResTable *t, const char *key);
// Variant that takes the key length (for LubStr that is not NUL-terminated).
ResEntry *res_table_get_n(ResTable *t, const char *key, size_t len);
ResEntry *res_table_get_by_handle(ResTable *t, int32_t handle);
// Record of a handle removed by sweep. NULL if it is not remembered.
const ResSwept *res_table_find_swept(const ResTable *t, int32_t handle);
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
