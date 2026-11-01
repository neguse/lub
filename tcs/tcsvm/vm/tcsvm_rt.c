/* tcs VM の runtime の object: heap、string、List、Dictionary、List.Sort。 */
#include "tcsvm_internal.h"

#include "../../runtime/sort/tcs_sort.h"

#include <stdlib.h>
#include <string.h>

/* ---- heap: GC はまだ無く bump 確保だけ (doc/vm-design.md §10、V2 の残り) ---- */

void *tvm_alloc(TvmVm *vm, size_t size) {
  size = (size + 7u) & ~(size_t)7u;
  TvmChunk *c = vm->heap;
  if (!c || c->used + size > c->cap) {
    size_t cap = size > ((size_t)1 << 20) ? size : ((size_t)1 << 20);
    c = (TvmChunk *)malloc(sizeof(TvmChunk) + cap);
    if (!c)
      return NULL;
    c->next = vm->heap;
    c->used = 0;
    c->cap = cap;
    vm->heap = c;
  }
  void *p = (char *)(c + 1) + c->used;
  c->used += size;
  vm->heap_bytes += size;
  memset(p, 0, size);
  return p;
}

void tvm_heap_free(TvmVm *vm) {
  while (vm->heap) {
    TvmChunk *n = vm->heap->next;
    free(vm->heap);
    vm->heap = n;
  }
}

/* ---- string: 不変の octet 列 (il-spec §11) ---- */

TvmString *tvm_str_new(TvmVm *vm, const char *data, size_t len) {
  if (len > 0x7FFFFFFF)
    return NULL;
  TvmString *s = (TvmString *)tvm_alloc(vm, sizeof(TvmString) + len + 1);
  if (!s)
    return NULL;
  s->type = &vm->types[vm->string_type];
  s->len = (int32_t)len;
  if (len)
    memcpy(STR_DATA(s), data, len);
  return s;
}

/* null は空文字列 (C# の string.Concat と同じ) */
TvmString *tvm_str_concat(TvmVm *vm, const TvmString *a, const TvmString *b) {
  size_t la = a ? (size_t)a->len : 0, lb = b ? (size_t)b->len : 0;
  if (la + lb > 0x7FFFFFFF)
    return NULL;
  TvmString *s = (TvmString *)tvm_alloc(vm, sizeof(TvmString) + la + lb + 1);
  if (!s)
    return NULL;
  s->type = &vm->types[vm->string_type];
  s->len = (int32_t)(la + lb);
  if (la)
    memcpy(STR_DATA(s), STR_DATA(a), la);
  if (lb)
    memcpy(STR_DATA(s) + la, STR_DATA(b), lb);
  return s;
}

bool tvm_str_eq(const TvmString *a, const TvmString *b) {
  if (a == b)
    return true;
  if (!a || !b || a->len != b->len)
    return false;
  return memcmp(STR_DATA(a), STR_DATA(b), (size_t)a->len) == 0;
}

/* ---- List ---- */

TvmList *tvm_list_new(TvmVm *vm, const TvmType *type) {
  TvmList *l = (TvmList *)tvm_alloc(vm, sizeof(TvmList));
  if (l)
    l->type = type;
  return l;
}

bool tvm_list_grow(TvmVm *vm, TvmList *list) {
  int32_t cap = list->cap ? list->cap * 2 : 4;
  if (cap <= list->cap || (uint64_t)cap * list->type->elem_size > ((uint64_t)1 << 31))
    return false;
  char *data = (char *)tvm_alloc(vm, (size_t)cap * list->type->elem_size);
  if (!data)
    return false;
  if (list->count)
    memcpy(data, list->data, (size_t)list->count * list->type->elem_size);
  list->data = data;
  list->cap = cap;
  return true;
}

/* ---- Dictionary: open addressing。列挙の順は未規定 (il-spec §11) なのでまだ列挙を持たない ---- */

static uint32_t key_hash(const TvmDict *d, TvmSlot key) {
  uint32_t h;
  if (d->type->key_kind == TVM_K_I32) {
    h = (uint32_t)key.i * 2654435761u;
  } else {
    const TvmString *s = (const TvmString *)key.p;
    h = 2166136261u;
    if (s)
      for (int32_t i = 0; i < s->len; i++)
        h = (h ^ (uint8_t)STR_DATA(s)[i]) * 16777619u;
  }
  return h ? h : 1;
}

static bool key_eq(const TvmDict *d, TvmSlot a, TvmSlot b) {
  if (d->type->key_kind == TVM_K_I32)
    return a.i == b.i;
  return tvm_str_eq((const TvmString *)a.p, (const TvmString *)b.p);
}

TvmDict *tvm_dict_new(TvmVm *vm, const TvmType *type) {
  TvmDict *d = (TvmDict *)tvm_alloc(vm, sizeof(TvmDict));
  if (d)
    d->type = type;
  return d;
}

TvmDictEntry *tvm_dict_find(const TvmDict *d, TvmSlot key) {
  if (!d->cap)
    return NULL;
  uint32_t h = key_hash(d, key);
  uint32_t mask = (uint32_t)d->cap - 1;
  for (uint32_t i = h & mask;; i = (i + 1) & mask) {
    TvmDictEntry *e = &d->entries[i];
    if (!e->hash)
      return NULL;
    if (e->hash == h && key_eq(d, e->key, key))
      return e;
  }
}

static bool dict_resize(TvmVm *vm, TvmDict *d) {
  int32_t cap = d->cap ? d->cap * 2 : 8;
  if (cap <= d->cap)
    return false;
  TvmDictEntry *entries = (TvmDictEntry *)tvm_alloc(vm, (size_t)cap * sizeof(TvmDictEntry));
  if (!entries)
    return false;
  uint32_t mask = (uint32_t)cap - 1;
  for (int32_t k = 0; k < d->cap; k++) {
    TvmDictEntry *e = &d->entries[k];
    if (!e->hash)
      continue;
    uint32_t i = e->hash & mask;
    while (entries[i].hash)
      i = (i + 1) & mask;
    entries[i] = *e;
  }
  d->entries = entries;
  d->cap = cap;
  return true;
}

/* key の entry を返す (無ければ足す。value は 0)。確保に失敗したら NULL */
TvmDictEntry *tvm_dict_insert(TvmVm *vm, TvmDict *d, TvmSlot key) {
  TvmDictEntry *e = tvm_dict_find(d, key);
  if (e)
    return e;
  if ((d->count + 1) * 4 > d->cap * 3 && !dict_resize(vm, d))
    return NULL;
  uint32_t h = key_hash(d, key);
  uint32_t mask = (uint32_t)d->cap - 1;
  uint32_t i = h & mask;
  while (d->entries[i].hash)
    i = (i + 1) & mask;
  e = &d->entries[i];
  e->hash = h;
  e->key = key;
  e->value.u = 0;
  d->count++;
  return e;
}

/* ---- List.Sort: runtime/sort の introsort。比較は VM の closure を入れ子で呼ぶ ---- */

typedef struct SortCtx {
  TvmVm *vm;
  const TvmClosure *compare;
  uint32_t es;
  uint8_t kind;
  TvmStatus status;
} SortCtx;

static TvmSlot load_elem(const void *p, uint8_t kind) {
  TvmSlot s;
  s.u = 0;
  switch (kind) {
  case TVM_K_I32:
    s.i = *(const int32_t *)p;
    break;
  case TVM_K_F32:
    s.f = *(const float *)p;
    break;
  case TVM_K_BOOL:
    s.i = *(const uint8_t *)p;
    break;
  default:
    s.p = *(void *const *)p;
    break;
  }
  return s;
}

static int sort_compare(void *ctx, const void *left, const void *right, int *stop) {
  SortCtx *c = (SortCtx *)ctx;
  TvmSlot args[2] = {load_elem(left, c->kind), load_elem(right, c->kind)};
  TvmSlot ret[1];
  TvmStatus st = tvm_call_closure(c->vm, c->compare, args, ret);
  if (st != TVM_OK) {
    c->status = st;
    *stop = 1;
    return 0;
  }
  return ret[0].i;
}

TvmStatus tvm_list_sort(TvmVm *vm, TvmList *list, const TvmClosure *compare) {
  SortCtx ctx = {vm, compare, list->type->elem_size, list->type->elem_kind, TVM_OK};
  /* 作業領域: 要素 2 個分 */
  uint64_t scratch[4];
  TcsSortStatus st = tcs_sort(list->data, (size_t)list->count, ctx.es, scratch, sort_compare, &ctx);
  if (st == TCS_SORT_STOPPED)
    return ctx.status;
  if (st == TCS_SORT_INCONSISTENT) {
    tvm_set_error(vm, "fault: Unable to sort because the IComparer.Compare() method returns "
                      "inconsistent results");
    vm->faulting = true;
    return TVM_FAULT;
  }
  return TVM_OK;
}
