#pragma once
// phys2d / phys3d の core が共有する小物: handle table、配列 view の
// scratch、hash、文字列。Lua には触らない。
#include "lub/lub_api.h"
#include <SDL3/SDL.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

typedef enum PhysHandleKind {
  PHYS_HANDLE_NONE = 0,
  PHYS_HANDLE_WORLD,
  PHYS_HANDLE_BODY,
  PHYS_HANDLE_SHAPE,
  PHYS_HANDLE_CHAIN,
  PHYS_HANDLE_JOINT,
} PhysHandleKind;

typedef struct PhysHandleSlot {
  void *ptr;
  int kind; // PhysHandleKind。NONE = stale
} PhysHandleSlot;

// handle → object。index = handle。entry の寿命の間は同じ値で、free された
// あとは stale。1 始まりの通し番号。
typedef struct PhysHandles {
  PhysHandleSlot *slots;
  int32_t slot_cap;
  int32_t next_handle;
} PhysHandles;

static inline void phys_handles_init(PhysHandles *h) {
  memset(h, 0, sizeof(*h));
  h->next_handle = 1;
}

static inline void phys_handles_free(PhysHandles *h) {
  SDL_free(h->slots);
  memset(h, 0, sizeof(*h));
}

static inline int32_t phys_handle_alloc(PhysHandles *h, void *ptr, int kind) {
  if (h->next_handle >= h->slot_cap) {
    int32_t cap = h->slot_cap ? h->slot_cap * 2 : 256;
    PhysHandleSlot *slots = (PhysHandleSlot *)SDL_realloc(
        h->slots, sizeof(PhysHandleSlot) * (size_t)cap);
    if (!slots)
      return 0;
    memset(slots + h->slot_cap, 0,
           sizeof(PhysHandleSlot) * (size_t)(cap - h->slot_cap));
    h->slots = slots;
    h->slot_cap = cap;
  }
  int32_t id = h->next_handle++;
  h->slots[id].ptr = ptr;
  h->slots[id].kind = kind;
  return id;
}

static inline void phys_handle_release(PhysHandles *h, int32_t id) {
  if (id <= 0 || id >= h->slot_cap)
    return;
  h->slots[id].ptr = NULL;
  h->slots[id].kind = PHYS_HANDLE_NONE;
}

static inline void *phys_handle_get(PhysHandles *h, int32_t id, int kind) {
  if (id <= 0 || id >= h->slot_cap || h->slots[id].kind != kind)
    return NULL;
  return h->slots[id].ptr;
}

// 配列を返す API の scratch (次の呼び出しまで有効)。
typedef struct PhysScratch {
  void *ptr;
  size_t cap;
} PhysScratch;

static inline void *phys_scratch_alloc(PhysScratch *s, size_t bytes) {
  if (bytes == 0)
    bytes = 1;
  if (s->cap < bytes) {
    size_t cap = s->cap ? s->cap : 256;
    while (cap < bytes)
      cap *= 2;
    void *p = SDL_realloc(s->ptr, cap);
    if (!p)
      return NULL;
    s->ptr = p;
    s->cap = cap;
  }
  memset(s->ptr, 0, bytes);
  return s->ptr;
}

static inline void phys_scratch_free(PhysScratch *s) {
  SDL_free(s->ptr);
  memset(s, 0, sizeof(*s));
}

static inline uint32_t phys_hash_str32(const char *s) {
  uint32_t h = 2166136261u;
  while (*s) {
    h ^= (uint8_t)*s++;
    h *= 16777619u;
  }
  return h;
}

static inline uint64_t phys_hash_init(void) { return 1469598103934665603ull; }

static inline uint64_t phys_hash_u64(uint64_t h, uint64_t v) {
  for (int i = 0; i < 8; ++i) {
    h ^= (uint8_t)(v & 0xffu);
    h *= 1099511628211ull;
    v >>= 8;
  }
  return h;
}

static inline uint64_t phys_hash_i64(uint64_t h, int64_t v) {
  return phys_hash_u64(h, (uint64_t)v);
}

static inline uint64_t phys_hash_f32(uint64_t h, float f) {
  uint32_t bits = 0;
  memcpy(&bits, &f, sizeof(bits));
  return phys_hash_u64(h, bits);
}

static inline uint64_t phys_hash_bool(uint64_t h, bool b) {
  return phys_hash_u64(h, b ? 1 : 0);
}

static inline uint64_t phys_hash_cstr(uint64_t h, const char *s) {
  if (!s)
    s = "";
  while (*s) {
    h ^= (uint8_t)*s++;
    h *= 1099511628211ull;
  }
  return h;
}

static inline char *phys_strdup(const char *s) {
  return s ? SDL_strdup(s) : NULL;
}

static inline char *phys_strdup_n(LubStr s) {
  size_t n = s.len > 0 ? (size_t)s.len : 0;
  char *out = (char *)SDL_malloc(n + 1);
  if (!out)
    return NULL;
  if (n)
    memcpy(out, s.ptr, n);
  out[n] = '\0';
  return out;
}

static inline void phys_owned_string_clear(char **dst) {
  SDL_free(*dst);
  *dst = NULL;
}

// len 0 は「無し」(NULL)。失敗 (out of memory) は false。
static inline bool phys_owned_string_set(char **dst, LubStr value) {
  phys_owned_string_clear(dst);
  if (!value.ptr || value.len <= 0)
    return true;
  *dst = phys_strdup_n(value);
  return *dst != NULL;
}

static inline LubStr phys_str(const char *s) {
  LubStr r = {s ? s : "", s ? (int32_t)strlen(s) : 0};
  return r;
}

#define PHYS_KEY_MAX 256
