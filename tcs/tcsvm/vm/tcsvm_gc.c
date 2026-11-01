/* tcs VM の GC (doc/vm-design.md §10): 動かさない mark-sweep。
   root は static、VM の frame (register の種類表で ref の register だけを見る)、runtime の pin。
   object を動かさないので、native の C が持つ pointer は回収の前後で変わらない。
   回収は interpreter の確保の地点 (frame を保存してから) とフレーム境界 (tvm_frame_end) でだけ起きる。 */
#include "tcsvm_internal.h"

#include <stdlib.h>
#include <string.h>

#define GC_MIN_LIMIT ((size_t)1 << 20)

static void *alloc_with_header(TvmVm *vm, size_t size, uint8_t blob) {
  TvmGcHeader *h = (TvmGcHeader *)calloc(1, sizeof(TvmGcHeader) + size);
  if (!h)
    return NULL;
  h->size = (uint32_t)size;
  h->blob = blob;
  h->next = vm->objects;
  vm->objects = h;
  vm->gc_debt += size + sizeof(TvmGcHeader);
  return h + 1;
}

void *tvm_alloc(TvmVm *vm, size_t size) {
  if (size > 0x7FFFFFFF)
    return NULL;
  return alloc_with_header(vm, size, 0);
}

void *tvm_alloc_blob(TvmVm *vm, size_t size) {
  if (size > 0x7FFFFFFF)
    return NULL;
  return alloc_with_header(vm, size, 1);
}

void *tvm_alloc_fixed(size_t size) {
  TvmGcHeader *h = (TvmGcHeader *)calloc(1, sizeof(TvmGcHeader) + size);
  if (!h)
    return NULL;
  h->size = (uint32_t)size;
  h->fixed = 1;
  return h + 1;
}

void tvm_free_fixed(void *p) { free(GC_HEADER(p)); }

void tvm_heap_free(TvmVm *vm) {
  while (vm->objects) {
    TvmGcHeader *n = vm->objects->next;
    free(vm->objects);
    vm->objects = n;
  }
  free(vm->mark_stack);
  vm->mark_stack = NULL;
  vm->nmark = vm->capmark = 0;
}

/* ---- mark ---- */

static void push(TvmVm *vm, void *p) {
  if (vm->nmark == vm->capmark) {
    size_t cap = vm->capmark ? vm->capmark * 2 : 256;
    void **stack = (void **)realloc(vm->mark_stack, cap * sizeof(void *));
    if (!stack)
      abort(); /* 回収の途中で止まる手段が無い */
    vm->mark_stack = stack;
    vm->capmark = cap;
  }
  vm->mark_stack[vm->nmark++] = p;
}

static void mark(TvmVm *vm, void *p) {
  if (!p)
    return;
  TvmGcHeader *h = GC_HEADER(p);
  if (h->mark || h->fixed)
    return;
  h->mark = 1;
  if (!h->blob)
    push(vm, p);
}

static void mark_leaves(TvmVm *vm, const char *base, const TvmType *t) {
  for (uint32_t k = 0; k < t->nleaves; k++)
    if (t->leaves[k].kind == TVM_K_REF)
      mark(vm, *(void *const *)(base + t->leaves[k].offset));
}

static void mark_elems(TvmVm *vm, const char *data, int32_t n, const TvmType *t) {
  if (t->elem_kind == TVM_K_REF)
    for (int32_t i = 0; i < n; i++)
      mark(vm, ((void *const *)data)[i]);
}

static void scan(TvmVm *vm, void *p) {
  const TvmType *t = ((TvmObject *)p)->type;
  switch (t->tkind) {
  case TVM_T_CLASS:
    mark_leaves(vm, (const char *)p, t);
    break;
  case TVM_T_ARRAY: {
    TvmArray *a = (TvmArray *)p;
    if (t->elem_kind == TVM_K_REF) {
      mark_elems(vm, ARR_DATA(a), a->len, t);
    } else if (t->elem_kind == TVM_K_STRUCT) {
      const TvmType *s = &vm->types[t->elem_type];
      for (int32_t i = 0; i < a->len; i++)
        mark_leaves(vm, ARR_DATA(a) + (size_t)i * t->elem_size, s);
    }
    break;
  }
  case TVM_T_LIST: {
    TvmList *l = (TvmList *)p;
    mark(vm, l->data);
    if (l->data)
      mark_elems(vm, l->data, l->count, t);
    break;
  }
  case TVM_T_DICT: {
    TvmDict *d = (TvmDict *)p;
    mark(vm, d->entries);
    for (int32_t i = 0; i < d->cap; i++) {
      TvmDictEntry *e = &d->entries[i];
      if (!e->hash)
        continue;
      if (t->key_kind == TVM_K_REF)
        mark(vm, e->key.p);
      if (t->elem_kind == TVM_K_REF)
        mark(vm, e->value.p);
    }
    break;
  }
  case TVM_T_FUNC: {
    TvmClosure *c = (TvmClosure *)p;
    for (uint32_t k = 0; k < c->ncaps; k++)
      if (c->proto->caps[k].kind == TVM_K_REF)
        mark(vm, CLO_CAPS(c)[k].p);
    break;
  }
  default: /* string */
    break;
  }
}

static void mark_roots(TvmVm *vm) {
  for (uint32_t i = 0; i < vm->nstatics; i++)
    if (vm->static_info[i].kind == TVM_K_REF)
      mark(vm, vm->statics[i].p);
  /* 保存された frame: interpreter は回収の前と入れ子の呼び出しの前に、今の frame を ftop の下に置く */
  for (TvmFrame *fr = vm->frames; fr < vm->ftop; fr++) {
    const TvmProto *proto = fr->proto;
    if (!proto)
      continue;
    for (uint32_t k = 0; k < proto->nregs; k++)
      if (proto->regs[k].kind == TVM_K_REF)
        mark(vm, fr->R[k].p);
    mark(vm, (void *)fr->clo);
  }
  for (uint32_t i = 0; i < vm->npins; i++)
    mark(vm, *vm->pins[i]);
}

void tvm_gc_collect(TvmVm *vm) {
  mark_roots(vm);
  while (vm->nmark)
    scan(vm, vm->mark_stack[--vm->nmark]);
  /* sweep */
  size_t live = 0;
  TvmGcHeader **link = &vm->objects;
  while (*link) {
    TvmGcHeader *h = *link;
    if (h->mark) {
      h->mark = 0;
      live += h->size + sizeof(TvmGcHeader);
      link = &h->next;
    } else {
      *link = h->next;
      free(h);
    }
  }
  vm->gc_live = live;
  vm->gc_debt = 0;
  vm->gc_limit = vm->gc_stress ? 0 : (live > GC_MIN_LIMIT ? live : GC_MIN_LIMIT);
  vm->gc_count++;
}

void tvm_frame_end(TvmVm *vm) {
  if (vm->gc_debt >= vm->gc_limit)
    tvm_gc_collect(vm);
}

size_t tvm_gc_live(TvmVm *vm) { return vm->gc_live; }
