/* GC of the tcs VM (doc/vm-design.md §10): non-moving mark-sweep.
   Roots are statics, VM frames (only the ref registers, per the register kind
   table), and runtime pins. Objects do not move, so pointers held by native C
   do not change across a collection. Collection happens only at interpreter
   allocation points (after saving the frame) and at frame boundaries
   (tvm_frame_end). */
#include "../../runtime/model/tcs_walk.h"
#include "tcsvm_internal.h"

#include <stdlib.h>
#include <string.h>

#define GC_MIN_LIMIT ((size_t)1 << 20)

static void *alloc_with_header(TvmVm *vm, size_t size, uint8_t blob) {
  if (vm->heap) { /* shared heap: allocates in the nursery of the tcs2c runtime
                     (the header has the same shape; kept objects are not
                     counted) */
    if (vm->prof_clock)
      tvm_profile_alloc(vm, NULL, size + sizeof(TvmGcHeader));
    return vm->heap->alloc(blob ? TVM_KIND_BLOB : TVM_KIND_OBJECT, size);
  }
  TvmGcHeader *h = (TvmGcHeader *)calloc(1, sizeof(TvmGcHeader) + size);
  if (!h)
    return NULL;
  h->size = size;
  h->kind = blob ? TVM_KIND_BLOB : TVM_KIND_OBJECT;
  h->next = vm->objects;
  vm->objects = h;
  vm->gc_debt += size + sizeof(TvmGcHeader);
  if (vm->prof_clock)
    tvm_profile_alloc(vm, h + 1, size + sizeof(TvmGcHeader));
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
  h->size = size;
  h->kind = TVM_KIND_OBJECT;
  h->flags = TVM_GC_STATIC;
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
      abort(); /* There is no way to stop in the middle of a collection */
    vm->mark_stack = stack;
    vm->capmark = cap;
  }
  vm->mark_stack[vm->nmark++] = p;
}

static void mark(TvmVm *vm, void *p) {
  if (!p)
    return;
  TvmGcHeader *h = GC_HEADER(p);
  if (h->flags & (TVM_GC_MARK | TVM_GC_STATIC))
    return;
  h->flags |= TVM_GC_MARK;
  if (!GC_BLOB(h))
    push(vm, p);
}

/* References inside values are walked by the shared walker
   (runtime/model/tcs_walk, doc/object-model.md §7), replacing only the sink
   (mark for the GC, repointing to the forwarding target for reload) */
void tvm_object_refs(TvmVm *vm, void *p, void (*fn)(void *ctx, void **slot),
                     void *ctx) {
  const TvmType *t = TVM_TYPE(((TvmObject *)p));
  switch (t->tkind) {
  case TVM_T_CLASS:
    tcs_walk_value(t, (char *)p, fn, ctx);
    break;
  case TVM_T_ARRAY: {
    TvmArray *a = (TvmArray *)p;
    tcs_walk_elems(vm->types, t->elem_kind, t->elem_type, t->elem_size,
                   ARR_DATA(a), (uint32_t)a->len, fn, ctx);
    break;
  }
  case TVM_T_LIST: {
    TvmList *l = (TvmList *)p;
    fn(ctx, (void **)&l->data);
    if (l->data)
      tcs_walk_elems(vm->types, t->elem_kind, t->elem_type, t->elem_size,
                     l->data, (uint32_t)l->count, fn, ctx);
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
      tcs_walk_elems(vm->types, t->elem_kind, t->elem_type, t->elem_size,
                     DICT_VALUE(e), 1, fn, ctx);
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
  default: /* string, box (the contents are values) */
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
  /* Saved frames: before a collection and before a nested call, the interpreter
   * puts the current frame below ftop */
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
      mark_leaves(
          vm, vm->struct_pins[i].base + (size_t)k * vm->struct_pins[i].st->size,
          vm->struct_pins[i].st);
  /* Values recorded by --trace and objects held by state snapshots */
  tvm_debug_mark(vm, mark);
}

void tvm_gc_collect(TvmVm *vm) {
  if (vm->heap)
    return; /* shared heap: the tcs2c runtime collects at frame boundaries */
  uint64_t t0 = 0;
  if (vm->prof_clock) {
    tvm_profile_gc_begin(vm);
    t0 = vm->prof_clock();
  }
  mark_roots(vm);
  while (vm->nmark)
    scan(vm, vm->mark_stack[--vm->nmark]);
  /* sweep */
  size_t live = 0;
  TvmGcHeader **link = &vm->objects;
  while (*link) {
    TvmGcHeader *h = *link;
    if (h->flags & TVM_GC_MARK) {
      h->flags &= (uint16_t)~TVM_GC_MARK;
      live += h->size + sizeof(TvmGcHeader);
      link = &h->next;
    } else {
      *link = h->next;
      if (vm->npinned || vm->nids)
        tvm_debug_forget(vm, h + 1);
      free(h);
    }
  }
  vm->gc_live = live;
  vm->gc_debt = 0;
  vm->gc_limit =
      vm->gc_stress ? 0 : (live > GC_MIN_LIMIT ? live : GC_MIN_LIMIT);
  vm->gc_count++;
  if (vm->prof_clock)
    tvm_profile_gc(vm, vm->prof_clock() - t0, live);
}

void tvm_frame_end(TvmVm *vm) {
  if (vm->heap) {
    if (vm->ftop == vm->frames && !vm->depth)
      vm->heap->frame();
    return;
  }
  tvm_profile_frame_end(vm);
  if (vm->gc_debt >= vm->gc_limit)
    tvm_gc_collect(vm);
}

size_t tvm_gc_live(TvmVm *vm) { return vm->gc_live; }

/* ---- shared heap (doc/object-model.md §10.2) ---- */

static TvmVm *shared_vm; /* The VM using the shared heap (one per process) */

void tvm_use_heap(TvmVm *vm, const TvmHeap *heap) {
  vm->heap = heap;
  vm->gc_limit = (size_t)-1; /* Does not collect at allocation points */
  shared_vm = vm;
}

/* Roots for the runtime/core GC: VM statics, host handles, and objects in the
 * middle of a reload. This is a frame boundary, so there are no VM frames */
void tvm_heap_roots(TvmSlotFn fn) {
  TvmVm *vm = shared_vm;
  if (!vm)
    return;
  for (uint32_t i = 0; i < vm->nstatics; i++)
    if (vm->static_info[i].kind == TVM_K_REF && vm->statics[i].p)
      fn(&vm->statics[i].p);
  for (uint32_t i = 0; i < vm->nhandles; i++)
    if (vm->handles[i])
      fn(&vm->handles[i]);
  for (size_t i = 0; i < vm->nreload_roots; i++)
    fn(&vm->reload_roots[i]);
}

static void slot_fn(void *ctx, void **slot) {
  if (*slot)
    ((TvmSlotFn)ctx)(slot);
}

/* The runtime/core GC walks inside VM objects (kind FOREIGN) */
void tvm_heap_refs(void *payload, TvmSlotFn fn) {
  if (!shared_vm || !TVM_TYPE(payload))
    return;
  tvm_object_refs(shared_vm, payload, slot_fn, (void *)fn);
}
