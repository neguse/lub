#include "resources.h"
#include "backend.h"
#include "gfx_bind.h"
#include <stdbool.h>
#include <stdint.h>
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
    shader_names_free(e->u.sh.names);
    break;
  case RES_DRAW_STATE:
    draw_state_free(e->u.ds.state);
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
  t->handle_count = 0;
  t->next_handle = 0;
  t->handle_wrapped = false;
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

static uint32_t handle_slot(int32_t handle, int32_t cap) {
  return ((uint32_t)handle * 2654435761u) & (uint32_t)(cap - 1);
}

ResEntry *res_table_get_by_handle(ResTable *t, int32_t handle) {
  if (handle <= 0 || t->handle_cap == 0)
    return NULL;
  uint32_t mask = (uint32_t)t->handle_cap - 1;
  for (uint32_t i = handle_slot(handle, t->handle_cap);; i = (i + 1) & mask) {
    ResEntry *e = t->by_handle[i];
    if (!e)
      return NULL;
    if (e->handle == handle)
      return e;
  }
}

bool res_table_handle_issued(const ResTable *t, int32_t handle) {
  return handle > 0 && (t->handle_wrapped || handle <= t->next_handle);
}

static void handle_insert(ResEntry **slots, int32_t cap, ResEntry *e) {
  uint32_t mask = (uint32_t)cap - 1;
  uint32_t i = handle_slot(e->handle, cap);
  while (slots[i])
    i = (i + 1) & mask;
  slots[i] = e;
}

// 表の大きさを cap にして入れ直す。
static bool handle_rehash(ResTable *t, int32_t cap) {
  ResEntry **slots = (ResEntry **)calloc((size_t)cap, sizeof(ResEntry *));
  if (!slots)
    return false;
  for (int32_t i = 0; i < t->handle_cap; ++i)
    if (t->by_handle[i])
      handle_insert(slots, cap, t->by_handle[i]);
  free(t->by_handle);
  t->by_handle = slots;
  t->handle_cap = cap;
  return true;
}

// handle を表から抜く。後ろに続く entry を詰め直す (墓標を残さない)。
static void handle_remove(ResTable *t, ResEntry *e) {
  if (t->handle_cap == 0)
    return;
  uint32_t mask = (uint32_t)t->handle_cap - 1;
  uint32_t i = handle_slot(e->handle, t->handle_cap);
  while (t->by_handle[i] && t->by_handle[i] != e)
    i = (i + 1) & mask;
  if (!t->by_handle[i])
    return;
  t->by_handle[i] = NULL;
  t->handle_count--;
  for (uint32_t j = (i + 1) & mask; t->by_handle[j]; j = (j + 1) & mask) {
    uint32_t home = handle_slot(t->by_handle[j]->handle, t->handle_cap);
    // j の entry が空いた i より前 (巡回順で i..j の外) を home に持つなら
    // i に移せる
    bool movable = i <= j ? (home <= i || home > j) : (home <= i && home > j);
    if (movable) {
      t->by_handle[i] = t->by_handle[j];
      t->by_handle[j] = NULL;
      i = j;
    }
  }
}

static bool res_table_assign_handle(ResTable *t, ResEntry *e) {
  if ((t->handle_count + 1) * 2 > t->handle_cap &&
      !handle_rehash(t, t->handle_cap ? t->handle_cap * 2 : 256))
    return false;
  // 一周した後は、まだ生きている handle を飛ばす
  do {
    if (t->next_handle == INT32_MAX) {
      t->next_handle = 0;
      t->handle_wrapped = true;
    }
    ++t->next_handle;
  } while (t->handle_wrapped && res_table_get_by_handle(t, t->next_handle));
  e->handle = t->next_handle;
  handle_insert(t->by_handle, t->handle_cap, e);
  t->handle_count++;
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
      int evict = current_frame - e->last_seen_frame > max_unused_frames;
      if (evict) {
        if (e->kind == RES_SHADER && e->u.sh.h && on_shader_release) {
          on_shader_release(ctx, e->u.sh.h);
        }
        *prev = next;
        handle_remove(t, e);
        res_entry_release(e);
      } else {
        prev = &e->next;
      }
      e = next;
    }
  }
  // 生きている entry が大きく減ったら表も縮める
  if (t->handle_cap > 256 && t->handle_count * 8 < t->handle_cap)
    handle_rehash(t, t->handle_cap / 2);
}
