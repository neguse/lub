/* tcs VM の runtime の object: heap、string、List、Dictionary、List.Sort。 */
#include "tcsvm_internal.h"

#include "../../runtime/sort/tcs_sort.h"

#include <stdlib.h>
#include <string.h>

/* ---- string: 不変の octet 列 (il-spec §11) ---- */

TvmString *tvm_str_new(TvmVm *vm, const char *data, size_t len) {
  if (len > 0x7FFFFFFF)
    return NULL;
  TvmString *s = (TvmString *)tvm_alloc(vm, sizeof(TvmString) + len + 1);
  if (!s)
    return NULL;
  TVM_TYPE(s) = &vm->types[vm->string_type];
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
  TVM_TYPE(s) = &vm->types[vm->string_type];
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
    TVM_TYPE(l) = type;
  return l;
}

bool tvm_list_grow(TvmVm *vm, TvmList *list) {
  int32_t cap = list->cap ? list->cap * 2 : 4;
  if (cap <= list->cap ||
      (uint64_t)cap * TVM_TYPE(list)->elem_size > ((uint64_t)1 << 31))
    return false;
  char *data =
      (char *)tvm_alloc_blob(vm, (size_t)cap * TVM_TYPE(list)->elem_size);
  if (!data)
    return false;
  if (list->count)
    memcpy(data, list->data, (size_t)list->count * TVM_TYPE(list)->elem_size);
  list->data = data;
  list->cap = cap;
  return true;
}

/* ---- Dictionary: 足した順の entry の列と、番号を引く open addressing の表 ----
   列挙は足した順 (.NET の Dictionary も消さなければ足した順)。il-spec §11 は列挙の順を未規定とする */

/* key の hash と等価 (EqualityComparer<T>.Default と同じ): int / bool は値、float は Equals (NaN どうし、-0 と 0 が等しい)、
   string は内容、ほかの ref は identity */
static bool key_is_string(const TvmVm *vm, const TvmDict *d) {
  return TVM_TYPE(d)->key_kind == TVM_K_REF &&
         TVM_TYPE(d)->key_type == vm->string_type;
}

static uint32_t key_hash(const TvmVm *vm, const TvmDict *d, TvmSlot key) {
  uint32_t h;
  switch (TVM_TYPE(d)->key_kind) {
  case TVM_K_I32:
  case TVM_K_BOOL:
    h = (uint32_t)key.i * 2654435761u;
    break;
  case TVM_K_F32: {
    float f = key.f;
    if (f == 0.0f)
      f = 0.0f;
    uint32_t bits;
    if (f != f)
      bits = 0x7FC00000u;
    else
      memcpy(&bits, &f, 4);
    h = bits * 2654435761u;
    break;
  }
  default:
    if (key_is_string(vm, d)) {
      const TvmString *s = (const TvmString *)key.p;
      h = 2166136261u;
      if (s)
        for (int32_t i = 0; i < s->len; i++)
          h = (h ^ (uint8_t)STR_DATA(s)[i]) * 16777619u;
    } else {
      uintptr_t p = (uintptr_t)key.p;
      h = (uint32_t)(p ^ (p >> 32)) * 2654435761u;
    }
    break;
  }
  return h ? h : 1;
}

static bool key_eq(const TvmVm *vm, const TvmDict *d, TvmSlot a, TvmSlot b) {
  switch (TVM_TYPE(d)->key_kind) {
  case TVM_K_I32:
  case TVM_K_BOOL:
    return a.i == b.i;
  case TVM_K_F32:
    return a.f == b.f || (a.f != a.f && b.f != b.f);
  default:
    if (key_is_string(vm, d))
      return tvm_str_eq((const TvmString *)a.p, (const TvmString *)b.p);
    return a.p == b.p;
  }
}

TvmDict *tvm_dict_new(TvmVm *vm, const TvmType *type) {
  TvmDict *d = (TvmDict *)tvm_alloc(vm, sizeof(TvmDict));
  if (d)
    TVM_TYPE(d) = type;
  return d;
}

/* key の bucket の位置 (見つかった位置か、入れる位置) */
static int32_t probe(const TvmVm *vm, const TvmDict *d, TvmSlot key, uint32_t h,
                     bool *found) {
  uint32_t mask = (uint32_t)d->nbuckets - 1;
  int32_t tomb = -1;
  for (uint32_t i = h & mask;; i = (i + 1) & mask) {
    int32_t b = d->buckets[i];
    if (b == 0) {
      *found = false;
      return tomb >= 0 ? tomb : (int32_t)i;
    }
    if (b < 0) {
      if (tomb < 0)
        tomb = (int32_t)i;
      continue;
    }
    TvmDictEntry *e = DICT_ENTRY(d, b - 1);
    if (e->hash == h && key_eq(vm, d, e->key, key)) {
      *found = true;
      return (int32_t)i;
    }
  }
}

int32_t tvm_dict_find(const TvmVm *vm, const TvmDict *d, TvmSlot key) {
  if (!d->nbuckets)
    return -1;
  bool found;
  int32_t i = probe(vm, d, key, key_hash(vm, d, key), &found);
  return found ? d->buckets[i] - 1 : -1;
}

/* entry の列を詰め直し (消した entry を落とす)、表を作り直す */
static bool dict_rebuild(TvmVm *vm, TvmDict *d, int32_t cap) {
  uint32_t es = TVM_TYPE(d)->dict_entry;
  char *entries = (char *)tvm_alloc_blob(vm, (size_t)cap * es);
  int32_t nb = 8;
  while (nb < cap * 2)
    nb *= 2;
  int32_t *buckets =
      (int32_t *)tvm_alloc_blob(vm, (size_t)nb * sizeof(int32_t));
  if (!entries || !buckets)
    return false;
  int32_t n = 0;
  for (int32_t i = 0; i < d->used; i++) {
    TvmDictEntry *e = DICT_ENTRY(d, i);
    if (!e->live)
      continue;
    memcpy(entries + (size_t)n * es, e, es);
    n++;
  }
  d->entries = entries;
  d->buckets = buckets;
  d->cap = cap;
  d->nbuckets = nb;
  d->used = n;
  uint32_t mask = (uint32_t)nb - 1;
  for (int32_t k = 0; k < n; k++) {
    uint32_t i = DICT_ENTRY(d, k)->hash & mask;
    while (buckets[i])
      i = (i + 1) & mask;
    buckets[i] = k + 1;
  }
  return true;
}

int32_t tvm_dict_insert(TvmVm *vm, TvmDict *d, TvmSlot key) {
  int32_t at = tvm_dict_find(vm, d, key);
  if (at >= 0)
    return at;
  if (d->used == d->cap) {
    /* 末尾が埋まったら、消した entry の穴を詰めて (並びは保つ) 広げる */
    int32_t cap = d->count * 2 > 4 ? d->count * 2 : 4;
    if (cap <= d->count || !dict_rebuild(vm, d, cap))
      return -1;
  }
  uint32_t h = key_hash(vm, d, key);
  bool found;
  int32_t slot = probe(vm, d, key, h, &found);
  at = d->used++; /* 挿入順: 消した entry の場所は使わず末尾に積む */
  d->version++;
  TvmDictEntry *e = DICT_ENTRY(d, at);
  memset(e, 0, TVM_TYPE(d)->dict_entry);
  e->hash = h;
  e->live = 1;
  e->key = key;
  d->buckets[slot] = at + 1;
  d->count++;
  return at;
}

bool tvm_dict_remove(TvmVm *vm, TvmDict *d, TvmSlot key) {
  if (!d->nbuckets)
    return false;
  bool found;
  int32_t slot = probe(vm, d, key, key_hash(vm, d, key), &found);
  if (!found)
    return false;
  int32_t at = d->buckets[slot] - 1;
  TvmDictEntry *e = DICT_ENTRY(d, at);
  memset(
      e, 0,
      TVM_TYPE(d)->dict_entry); /* 穴 (live = 0)。参照を GC が辿らないように */
  d->buckets[slot] = -1;
  d->count--;
  return true;
}

/* ---- List の挿入と削除 ---- */

bool tvm_list_insert0(TvmVm *vm, TvmList *list, int32_t at) {
  if (list->count == list->cap && !tvm_list_grow(vm, list))
    return false;
  size_t es = TVM_TYPE(list)->elem_size;
  memmove(list->data + (size_t)(at + 1) * es, list->data + (size_t)at * es,
          (size_t)(list->count - at) * es);
  memset(list->data + (size_t)at * es, 0, es);
  list->count++;
  return true;
}

void tvm_list_remove_at(TvmList *list, int32_t at) {
  size_t es = TVM_TYPE(list)->elem_size;
  memmove(list->data + (size_t)at * es, list->data + (size_t)(at + 1) * es,
          (size_t)(list->count - at - 1) * es);
  list->count--;
  memset(list->data + (size_t)list->count * es, 0, es);
}

/* ---- 値の等価 (il-spec §10: struct と record は field ごと。EqualityComparer<T>.Default と同じ) ---- */

static bool f32_equals(float a, float b) {
  return a == b || (a != a && b != b);
}

bool tvm_value_eq(TvmVm *vm, uint8_t kind, uint16_t type, const void *a,
                  const void *b) {
  switch (kind) {
  case TVM_K_I32:
    return *(const int32_t *)a == *(const int32_t *)b;
  case TVM_K_F32:
    return f32_equals(*(const float *)a, *(const float *)b);
  case TVM_K_BOOL:
    return *(const uint8_t *)a == *(const uint8_t *)b;
  case TVM_K_STRUCT: {
    const TvmType *st = &vm->types[type];
    for (uint32_t k = 0; k < st->nleaves; k++)
      if (!tvm_value_eq(vm, st->leaves[k].kind, st->leaves[k].type,
                        (const char *)a + st->leaves[k].offset,
                        (const char *)b + st->leaves[k].offset))
        return false;
    return true;
  }
  default:
    return tvm_object_eq(vm, *(const TvmObject *const *)a,
                         *(const TvmObject *const *)b);
  }
}

bool tvm_object_eq(TvmVm *vm, const TvmObject *x, const TvmObject *y) {
  if (x == y)
    return true;
  if (!x || !y)
    return false;
  const TvmType *tx = tvm_type_of(vm, x);
  if (!tx || tx != tvm_type_of(vm, y))
    return false;
  if (tx->tkind == TVM_T_STRING)
    return tvm_str_eq((const TvmString *)x, (const TvmString *)y);
  if (tx->tkind == TVM_T_BOX)
    return tvm_value_eq(vm, tx->elem_kind, TVM_NO_TYPE,
                        &((const TvmBox *)x)->value,
                        &((const TvmBox *)y)->value);
  if (tx->tkind == TVM_T_CLASS && (tx->flags & TVM_CLASS_RECORD))
    return tvm_record_eq(vm, x, y);
  return false;
}

bool tvm_record_eq(TvmVm *vm, const TvmObject *a, const TvmObject *b) {
  if (a == b)
    return true;
  if (!a || !b || TVM_TYPE(a) != TVM_TYPE(b))
    return false;
  const TvmType *t = TVM_TYPE(a);
  for (uint32_t k = 0; k < t->nleaves; k++)
    if (!tvm_value_eq(vm, t->leaves[k].kind, t->leaves[k].type,
                      (const char *)a + t->leaves[k].offset,
                      (const char *)b + t->leaves[k].offset))
      return false;
  return true;
}

/* 実行時の型 test (il-spec §9): 継承が無いので class は型の一致、interface は実装の一覧、object は全部 */
const TvmType *tvm_type_of(TvmVm *vm, const void *o) {
  const TvmGcHeader *h = GC_HEADER(o);
  if (!vm->heap || h->kind == TVM_KIND_OBJECT)
    return h->type;
  if (h->kind == TVM_KIND_BLOB && vm->string_type != TVM_NO_TYPE)
    return &vm->types[vm->string_type];
  /* C の module の object: tvm_host_type で番号を合わせた class なら、その VM の型 */
  for (uint32_t i = 0; i < vm->ntypes; i++)
    if (vm->type_ids[i] == h->type_id && vm->types[i].tkind == TVM_T_CLASS)
      return &vm->types[i];
  return NULL;
}

bool tvm_is_type(TvmVm *vm, const TvmObject *o, const TvmType *t) {
  const TvmType *ot = tvm_type_of(vm, o);
  TvmIfaceCheck check = vm->iface_checks[t - vm->types];
  if (check) { /* --ref の interface: VM の class は実装の列、C の object は C の module の判定 */
    if (GC_HEADER(o)->kind != TVM_KIND_OBJECT)
      return check((void *)o);
    if (!ot || ot->tkind != TVM_T_CLASS)
      return false;
    for (uint32_t k = 0; k < ot->nifaces; k++)
      if (ot->ifaces[k] == (uint16_t)(t - vm->types))
        return true;
    return false;
  }
  if (ot == t || t->tkind == TVM_T_OBJECT)
    return true;
  if (ot && t->tkind == TVM_T_IFACE && ot->tkind == TVM_T_CLASS) {
    uint16_t want = (uint16_t)(t - vm->types);
    for (uint32_t k = 0; k < ot->nifaces; k++)
      if (ot->ifaces[k] == want)
        return true;
  }
  return false;
}

TvmStatus tvm_fault_msg(TvmVm *vm, const char *what) {
  tvm_set_error(vm, "fault: %s", what);
  vm->faulting = true;
  return TVM_FAULT;
}

/* ---- List.Sort: runtime/sort の introsort。比較は VM の closure を入れ子で呼ぶ ---- */

typedef struct SortCtx {
  TvmVm *vm;
  const TvmClosure *compare;
  uint32_t es;
  uint8_t kind;
  const TvmType *st; /* struct の要素なら、その型 (葉に展開して渡す) */
  TvmStatus status;
  char sig
      [5]; /* C の module の比較の delegate の sig (struct の要素は '?' で、invoke は無い) */
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

static int sort_compare(void *ctx, const void *left, const void *right,
                        int *stop) {
  SortCtx *c = (SortCtx *)ctx;
  TvmSlot args[512];
  TvmSlot ret[1];
  if (c->st) {
    uint32_t n = c->st->nleaves;
    for (uint32_t k = 0; k < n; k++) {
      args[k] = load_elem((const char *)left + c->st->leaves[k].offset,
                          c->st->leaves[k].kind);
      args[n + k] = load_elem((const char *)right + c->st->leaves[k].offset,
                              c->st->leaves[k].kind);
    }
  } else {
    args[0] = load_elem(left, c->kind);
    args[1] = load_elem(right, c->kind);
  }
  TvmStatus st = tvm_call_delegate(c->vm, c->compare, c->sig, args, ret);
  if (st != TVM_OK) {
    c->status = st;
    *stop = 1;
    return 0;
  }
  return ret[0].i;
}

TvmStatus tvm_list_sort(TvmVm *vm, TvmList *list, const TvmClosure *compare) {
  const TvmType *st = TVM_TYPE(list)->elem_kind == TVM_K_STRUCT
                          ? &vm->types[TVM_TYPE(list)->elem_type]
                          : NULL;
  if (st && st->nleaves > 256)
    return tvm_fault_msg(vm, "struct too large to sort");
  char e =
      tvm_sig_letter(vm, TVM_TYPE(list)->elem_kind, TVM_TYPE(list)->elem_type);
  SortCtx ctx = {
      vm, compare, TVM_TYPE(list)->elem_size, TVM_TYPE(list)->elem_kind,
      st, TVM_OK,  {e, e, ':', 'i', 0}};
  TcsSortStatus sorted;
  /* 作業領域: 要素 2 個分。比較の closure が確保して GC が走っても、作業領域の参照と並べている
     buffer (比較が List に足して buffer が替わっても、並べるのは元の buffer) は生きていなければならない。
     要素は並べている間 buffer か作業領域のどちらかにあるので、作業領域も root にする (struct は葉の型で辿る) */
  void *scratch_small[4] = {NULL, NULL, NULL, NULL};
  void *scratch = scratch_small;
  if (ctx.es * 2 > sizeof scratch_small) {
    scratch = tvm_alloc_blob(vm, ctx.es * 2);
    if (!scratch)
      return tvm_fault_msg(vm, "out of memory");
  }
  void *data = list->data;
  if (vm->npins + 3 > TVM_MAX_PINS) {
    tvm_set_error(vm, "fault: stack overflow (nested sorts)");
    vm->faulting = true;
    return TVM_FAULT;
  }
  uint32_t pins0 = vm->npins;
  vm->pins[vm->npins++] = &data;
  if (ctx.kind == TVM_K_REF) {
    vm->pins[vm->npins++] = &scratch_small[0];
    vm->pins[vm->npins++] = &scratch_small[1];
  } else if (st) {
    if (scratch != scratch_small)
      vm->pins[vm->npins++] = &scratch;
    if (vm->nstruct_pins >= TVM_MAX_DEPTH) {
      vm->npins = pins0;
      return tvm_fault_msg(vm, "stack overflow (nested sorts)");
    }
    vm->struct_pins[vm->nstruct_pins].base = (const char *)scratch;
    vm->struct_pins[vm->nstruct_pins].st = st;
    vm->struct_pins[vm->nstruct_pins].count = 2;
    vm->nstruct_pins++;
  }
  sorted =
      tcs_sort(data, (size_t)list->count, ctx.es, scratch, sort_compare, &ctx);
  vm->npins = pins0;
  if (st)
    vm->nstruct_pins--;
  if (sorted == TCS_SORT_STOPPED)
    return ctx.status;
  if (sorted == TCS_SORT_INCONSISTENT) {
    tvm_set_error(
        vm,
        "fault: Unable to sort because the IComparer.Compare() method returns "
        "inconsistent results");
    vm->faulting = true;
    return TVM_FAULT;
  }
  return TVM_OK;
}
