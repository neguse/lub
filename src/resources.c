#include "resources.h"
#include "backend.h"
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

_Static_assert((RES_BUCKETS & (RES_BUCKETS - 1)) == 0,
               "RES_BUCKETS must be a power of 2");

static uint32_t hash_str(const char *s) {
  uint32_t h = 2166136261u;
  while (*s) {
    h ^= (uint8_t)*s++;
    h *= 16777619u;
  }
  return h;
}

void res_table_init(ResTable *t) { memset(t, 0, sizeof(*t)); }

int64_t res_table_next_revision(ResTable *t) { return ++t->revision; }

static void res_entry_release(ResEntry *e) {
  switch (e->kind) {
  case RES_BUFFER:
    if (e->u.buf.h)
      g_backend->destroy_buffer(e->u.buf.h);
    break;
  case RES_TEXTURE:
    if (e->u.tex.h)
      g_backend->destroy_image(e->u.tex.h);
    break;
  case RES_SHADER:
    if (e->u.sh.h)
      g_backend->destroy_shader(e->u.sh.h);
    break;
  default:
    break;
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
  free(t->by_handle);
  t->by_handle = NULL;
  t->handle_cap = 0;
  t->next_handle = 0;
}

ResEntry *res_table_get_n(ResTable *t, const char *key, size_t len) {
  uint32_t h = 2166136261u;
  for (size_t k = 0; k < len; ++k) {
    h ^= (uint8_t)key[k];
    h *= 16777619u;
  }
  for (ResEntry *e = t->buckets[h & (RES_BUCKETS - 1)]; e; e = e->next) {
    if (strlen(e->key) == len && memcmp(e->key, key, len) == 0)
      return e;
  }
  return NULL;
}

ResEntry *res_table_get_by_handle(ResTable *t, int32_t handle) {
  if (handle <= 0 || handle >= t->handle_cap)
    return NULL;
  return t->by_handle[handle];
}

static bool res_table_assign_handle(ResTable *t, ResEntry *e) {
  int32_t h = ++t->next_handle;
  if (h >= t->handle_cap) {
    int32_t cap = t->handle_cap ? t->handle_cap * 2 : 256;
    while (cap <= h)
      cap *= 2;
    ResEntry **grown =
        (ResEntry **)realloc(t->by_handle, (size_t)cap * sizeof(ResEntry *));
    if (!grown)
      return false;
    memset(grown + t->handle_cap, 0,
           (size_t)(cap - t->handle_cap) * sizeof(ResEntry *));
    t->by_handle = grown;
    t->handle_cap = cap;
  }
  t->by_handle[h] = e;
  e->handle = h;
  return true;
}

ResEntry *res_table_get(ResTable *t, const char *key) {
  uint32_t i = hash_str(key) & (RES_BUCKETS - 1);
  for (ResEntry *e = t->buckets[i]; e; e = e->next) {
    if (strcmp(e->key, key) == 0)
      return e;
  }
  return NULL;
}

ResEntry *res_table_get_or_create(ResTable *t, const char *key, ResKind kind) {
  ResEntry *e = res_table_get(t, key);
  if (e) {
    if (e->kind != RES_NONE && e->kind != kind)
      return NULL; // 種別衝突
    return e;
  }
  uint32_t i = hash_str(key) & (RES_BUCKETS - 1);
  e = (ResEntry *)calloc(1, sizeof(ResEntry));
  if (!e)
    return NULL;
  e->key = strdup(key);
  if (!e->key) {
    free(e);
    return NULL;
  }
  e->kind = kind;
  e->version = -1;
  e->last_seen_frame = -1;
  if (!res_table_assign_handle(t, e)) {
    free(e->key);
    free(e);
    return NULL;
  }
  e->next = t->buckets[i];
  t->buckets[i] = e;
  return e;
}

void res_table_touch(ResEntry *e, int64_t frame_index) {
  e->last_seen_frame = frame_index;
}

void res_table_sweep(ResTable *t, int64_t current_frame,
                     int64_t max_unused_frames,
                     ResShaderInvalidateFn on_shader_release, void *ctx) {
  if (max_unused_frames < 0)
    return;
  for (int i = 0; i < RES_BUCKETS; ++i) {
    ResEntry **prev = &t->buckets[i];
    ResEntry *e = t->buckets[i];
    while (e) {
      ResEntry *next = e->next;
      int evict = (e->last_seen_frame >= 0) &&
                  (current_frame - e->last_seen_frame > max_unused_frames);
      if (evict) {
        if (e->kind == RES_SHADER && e->u.sh.h && on_shader_release) {
          on_shader_release(ctx, e->u.sh.h);
        }
        *prev = next;
        if (e->handle > 0 && e->handle < t->handle_cap)
          t->by_handle[e->handle] = NULL;
        res_entry_release(e);
      } else {
        prev = &e->next;
      }
      e = next;
    }
  }
}
