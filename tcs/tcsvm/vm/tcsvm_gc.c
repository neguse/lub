/* tcs VM の GC (doc/vm-design.md §10): 動かさない mark-sweep。
   root は static、VM の frame (register の種類表で ref の register だけを見る)、runtime の pin。
   object を動かさないので、native の C が持つ pointer は回収の前後で変わらない。
   回収は interpreter の確保の地点 (frame を保存してから) とフレーム境界 (tvm_frame_end) でだけ起きる。 */
#include "tcsvm_internal.h"
#include "../../runtime/model/tcs_walk.h"

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


/* 値の中の参照は共有の walker (runtime/model/tcs_walk、doc/object-model.md §7) で辿り、sink だけを替える
   (GC は mark、reload は転送先への張り替え) */
void tvm_object_refs(TvmVm *vm, void *p, void (*fn)(void *ctx, void **slot), void *ctx) {
  const TvmType *t = TVM_TYPE(((TvmObject *)p));
  switch (t->tkind) {
  case TVM_T_CLASS:
    tcs_walk_value(t, (char *)p, fn, ctx);
    break;
  case TVM_T_ARRAY: {
    TvmArray *a = (TvmArray *)p;
    tcs_walk_elems(vm->types, t->elem_kind, t->elem_type, t->elem_size, ARR_DATA(a), (uint32_t)a->len, fn, ctx);
    break;
  }
  case TVM_T_LIST: {
    TvmList *l = (TvmList *)p;
    fn(ctx, (void **)&l->data);
    if (l->data)
      tcs_walk_elems(vm->types, t->elem_kind, t->elem_type, t->elem_size, l->data, (uint32_t)l->count, fn, ctx);
    break;
  }
  case TVM_T_DICT: {
    TvmDict *d = (TvmDict *)p;
    fn(ctx, (void **)&d->entries);
    fn(ctx, (void **)&d->buckets);
    for (int32_t i = 0; i < d->used; i++) {
      TvmDictEntry *e = DICT_ENTRY(d, i);
      if (!e->live)
        continue;
      if (t->key_kind == TVM_K_REF)
        fn(ctx, &e->key.p);
      tcs_walk_elems(vm->types, t->elem_kind, t->elem_type, t->elem_size, DICT_VALUE(e), 1, fn, ctx);
    }
    break;
  }
  case TVM_T_FUNC: {
    TvmClosure *c = (TvmClosure *)p;
    for (uint32_t k = 0; k < c->ncaps; k++)
      if (c->proto->caps[k].kind == TVM_K_REF)
        fn(ctx, &CLO_CAPS(c)[k].p);
    break;
  }
  default: /* string、box (中は値) */
    break;
  }
}

static void mark_slot(void *ctx, void **slot) { mark((TvmVm *)ctx, *slot); }

static void mark_leaves(TvmVm *vm, const char *base, const TvmType *t) {
  tcs_walk_value(t, (char *)base, mark_slot, vm);
}

static void scan(TvmVm *vm, void *p) { tvm_object_refs(vm, p, mark_slot, vm); }

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
  for (size_t i = 0; i < vm->nreload_roots; i++)
    mark(vm, vm->reload_roots[i]);
  for (uint32_t i = 0; i < vm->nhandles; i++)
    mark(vm, vm->handles[i]);
  for (uint32_t i = 0; i < vm->nstruct_pins; i++)
    for (uint32_t k = 0; k < vm->struct_pins[i].count; k++)
      mark_leaves(vm, vm->struct_pins[i].base + (size_t)k * vm->struct_pins[i].st->size, vm->struct_pins[i].st);
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
