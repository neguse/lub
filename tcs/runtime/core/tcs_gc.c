/* ---- GC: frame-synchronized generational ---- */
#ifndef TCS_GC_MIN_THRESHOLD
#define TCS_GC_MIN_THRESHOLD ((size_t)1 << 20)
#endif
#ifndef TCS_GC_NURSERY_CHUNK
#ifdef TCS_GC_STRESS
#define TCS_GC_NURSERY_CHUNK ((size_t)256)
#else
#define TCS_GC_NURSERY_CHUNK ((size_t)1 << 18)
#endif
#endif
#define TCS_GC_ALIGN (alignof(max_align_t))

static const uint32_t tcs_layout_ptr_offsets[1] = {0};
static const TcsLayout tcs_layout_ptr = {sizeof(void *), 1,
                                         tcs_layout_ptr_offsets};

typedef struct TcsChunk {
  struct TcsChunk *next;
  size_t capacity;
  size_t used;
  _Alignas(max_align_t) unsigned char data[];
} TcsChunk;

static TcsChunk *tcs_gc_nursery;     /* first chunk (one always remains) */
static TcsChunk *tcs_gc_nursery_cur; /* chunk currently being allocated from */
static size_t tcs_gc_nursery_bytes;  /* bytes allocated this frame */
static size_t tcs_gc_nursery_chunks;

static TcsGcHeader *tcs_gc_objects; /* linked list of the old generation */
static size_t tcs_gc_object_count;
static size_t tcs_gc_live_bytes; /* live bytes after the latest mark-sweep */
static size_t
    tcs_gc_allocated_since; /* bytes promoted since the latest mark-sweep */
static size_t tcs_gc_threshold = TCS_GC_MIN_THRESHOLD;
static size_t tcs_gc_collections;    /* number of old-generation mark-sweeps */
static size_t tcs_gc_frames;         /* number of boundaries */
static size_t tcs_gc_promoted_bytes; /* total promoted bytes */
static size_t
    tcs_gc_old_bytes; /* current old-generation bytes (including headers) */
static size_t
    tcs_gc_nursery_objects;        /* number of objects allocated this frame */
static unsigned tcs_gc_call_depth; /* entry nesting depth (0 = boundary) */
static int tcs_gc_collecting;

static TcsGcHeader *
    *tcs_gc_mark_stack; /* used by both mark and the promotion scan */
static size_t tcs_gc_mark_count;
static size_t tcs_gc_mark_capacity;
static TcsGcHeader **tcs_gc_dirty; /* remembered set (old objects) */
static size_t tcs_gc_dirty_count;
static size_t tcs_gc_dirty_capacity;
static void ***tcs_gc_holds; /* slots the host holds across frames */
static size_t tcs_gc_hold_count;
static size_t tcs_gc_hold_capacity;

static void tcs_gc_push(TcsGcHeader *hdr) {
  if (hdr->flags & (TCS_GC_MARK | TCS_GC_STATIC))
    return;
  hdr->flags |= TCS_GC_MARK;
  if (tcs_gc_mark_count == tcs_gc_mark_capacity) {
    size_t capacity =
        tcs_gc_mark_capacity == 0 ? 1024 : tcs_gc_mark_capacity * 2;
    TcsGcHeader **grown = realloc(tcs_gc_mark_stack, capacity * sizeof(*grown));
    if (grown == NULL)
      tcs_fault("allocation");
    tcs_gc_mark_stack = grown;
    tcs_gc_mark_capacity = capacity;
  }
  tcs_gc_mark_stack[tcs_gc_mark_count++] = hdr;
}

/* ---- Write barrier: a store into an old object registers the owner as dirty
 * ---- */
static void tcs_wb(const void *owner) {
  TcsGcHeader *hdr;
  if (owner == NULL)
    return;
  hdr = TCS_GC_HEADER(owner);
  if ((hdr->flags & (TCS_GC_OLD | TCS_GC_DIRTY)) != TCS_GC_OLD)
    return;
  hdr->flags |= TCS_GC_DIRTY;
  if (tcs_gc_dirty_count == tcs_gc_dirty_capacity) {
    size_t capacity =
        tcs_gc_dirty_capacity == 0 ? 256 : tcs_gc_dirty_capacity * 2;
    TcsGcHeader **grown = realloc(tcs_gc_dirty, capacity * sizeof(*grown));
    if (grown == NULL)
      tcs_fault("allocation");
    tcs_gc_dirty = grown;
    tcs_gc_dirty_capacity = capacity;
  }
  tcs_gc_dirty[tcs_gc_dirty_count++] = hdr;
}

/* ---- Old-generation mark (precise pointers only) ---- */
static void tcs_gc_mark_ptr(const void *pointer) {
  if (pointer != NULL)
    tcs_gc_push(TCS_GC_HEADER(pointer));
}

/* Per-module mixing (docs/tcs/object-model.md §10.2): a function that traces
   the reference locations of tcs VM objects (TCS_KIND_FOREIGN), and a function
   that traces the VM roots (statics and handles). The tcs2c --vm-host glue
   installs the VM functions (unused if missing) */
typedef void (*TcsSlotFn)(void **slot);
static void (*tcs_gc_foreign_refs)(void *payload, TcsSlotFn fn);
static void (*tcs_gc_root_hook)(TcsSlotFn fn);

static void tcs_gc_mark_slot(void **slot) { tcs_gc_mark_ptr(*slot); }

static void tcs_gc_mark_value(const void *value, const TcsLayout *layout) {
  uint32_t i;
  if (layout == NULL)
    return;
  for (i = 0; i < layout->count; i++) {
    void *pointer;
    memcpy(&pointer, (const unsigned char *)value + layout->offsets[i],
           sizeof(pointer));
    tcs_gc_mark_ptr(pointer);
  }
}

static void tcs_gc_trace(TcsGcHeader *hdr) {
  void *payload = TCS_GC_PAYLOAD(hdr);
  size_t i;
  switch (hdr->kind) {
  case TCS_KIND_OBJECT:
    tcs_gc_mark_value(payload, hdr->layout);
    break;
  case TCS_KIND_ARRAY: {
    TcsArray *array = payload;
    if (hdr->layout == NULL)
      break;
    for (i = 0; i < array->length; i++)
      tcs_gc_mark_value(array->data + i * array->element_size, hdr->layout);
    break;
  }
  case TCS_KIND_LIST: {
    TcsList *list = payload;
    tcs_gc_mark_ptr(list->data);
    if (hdr->layout == NULL || list->data == NULL)
      break;
    for (i = 0; i < list->length; i++)
      tcs_gc_mark_value((unsigned char *)list->data + i * list->element_size,
                        hdr->layout);
    break;
  }
  case TCS_KIND_DICT: {
    TcsDict *dict = payload;
    tcs_gc_mark_ptr(dict->buckets);
    for (i = 0; i < dict->bucket_count; i++)
      tcs_gc_mark_ptr(dict->buckets[i]);
    tcs_gc_mark_ptr(dict->order);
    for (i = 0; i < dict->order_len; i++)
      tcs_gc_mark_ptr(dict->order[i]);
    break;
  }
  case TCS_KIND_DICT_NODE: {
    TcsDictNode *node = payload;
    tcs_gc_mark_ptr(node->next);
    tcs_gc_mark_ptr(node->key_s);
    tcs_gc_mark_value(node->value, hdr->layout);
    break;
  }
  case TCS_KIND_CLOSURE: {
    TcsClosure *closure = payload;
    size_t cells = (hdr->size - sizeof(TcsClosure)) / sizeof(void *);
    for (i = 0; i < cells; i++)
      tcs_gc_mark_ptr(closure->cells[i]);
    break;
  }
  case TCS_KIND_FOREIGN:
    if (tcs_gc_foreign_refs)
      tcs_gc_foreign_refs(payload, tcs_gc_mark_slot);
    break;
  default:
    break;
  }
}

/* Old-generation mark-sweep. Roots are statics and host holds (at a boundary
   there are no young objects and no references on the C stack) */
static void __attribute__((noinline)) tcs_gc_collect(void) {
  TcsGcHeader *hdr;
  TcsGcHeader **link;
  size_t i;
  size_t live = 0;
  size_t survivors = 0;
  if (tcs_gc_collecting)
    return;
  tcs_gc_collecting = 1;
  tcs_gc_mark_count = 0;
  tcs_gc_mark_statics();
  if (tcs_gc_root_hook)
    tcs_gc_root_hook(tcs_gc_mark_slot);
  for (i = 0; i < tcs_gc_hold_count; i++)
    tcs_gc_mark_ptr(*tcs_gc_holds[i]);
  while (tcs_gc_mark_count > 0)
    tcs_gc_trace(tcs_gc_mark_stack[--tcs_gc_mark_count]);

  link = &tcs_gc_objects;
  while (*link != NULL) {
    hdr = *link;
    if (hdr->flags & TCS_GC_MARK) {
      hdr->flags &= ~(uint32_t)TCS_GC_MARK;
      live += sizeof(*hdr) + hdr->size;
      survivors++;
      link = &hdr->next;
    } else {
      *link = hdr->next;
      free(hdr);
    }
  }
  tcs_gc_object_count = survivors;
  tcs_gc_live_bytes = live;
  tcs_gc_old_bytes = live;
  tcs_gc_allocated_since = 0;
  tcs_gc_threshold = live > TCS_GC_MIN_THRESHOLD ? live : TCS_GC_MIN_THRESHOLD;
  tcs_gc_collections++;
  tcs_gc_collecting = 0;
}

/* ---- nursery (bump allocation) ---- */
static TcsChunk *tcs_gc_chunk_new(size_t capacity) {
  TcsChunk *chunk;
  if (capacity < TCS_GC_NURSERY_CHUNK)
    capacity = TCS_GC_NURSERY_CHUNK;
  chunk = calloc(1, sizeof(*chunk) + capacity);
  if (chunk == NULL)
    tcs_fault("allocation");
  chunk->capacity = capacity;
  tcs_gc_nursery_chunks++;
  return chunk;
}

static void *tcs_gc_alloc(uint32_t kind, const TcsLayout *layout, size_t size) {
  TcsGcHeader *hdr;
  size_t need;
  if (size > SIZE_MAX - sizeof(*hdr) - TCS_GC_ALIGN)
    tcs_fault("allocation-overflow");
  need = (sizeof(*hdr) + size + (TCS_GC_ALIGN - 1)) & ~(TCS_GC_ALIGN - 1);
  if (tcs_gc_nursery_cur == NULL) {
    tcs_gc_nursery = tcs_gc_nursery_cur = tcs_gc_chunk_new(need);
  } else if (tcs_gc_nursery_cur->used + need > tcs_gc_nursery_cur->capacity) {
    TcsChunk *chunk = tcs_gc_chunk_new(need);
    tcs_gc_nursery_cur->next = chunk;
    tcs_gc_nursery_cur = chunk;
  }
  hdr = (TcsGcHeader *)(tcs_gc_nursery_cur->data + tcs_gc_nursery_cur->used);
#ifdef TCS_GC_STRESS
  memset(hdr, 0, need);
#endif
  tcs_gc_nursery_cur->used += need;
  tcs_gc_nursery_bytes += need;
  tcs_gc_nursery_objects++;
  hdr->next = NULL;
  hdr->layout = layout;
  hdr->size = size;
  hdr->kind = (uint16_t)kind;
  hdr->flags = 0;
  hdr->type_id = 0;
  return TCS_GC_PAYLOAD(hdr);
}

/* Empty the nursery at a boundary: keep only the first chunk and reset it to
   zero. Under stress, fill it with a poison value instead of zero (reset to
   zero on allocation), so that reads of stale pointers across a boundary are
   not passed off as 0 but show up as output differences */
static void tcs_gc_nursery_reset(void) {
  TcsChunk *chunk;
  if (tcs_gc_nursery == NULL)
    return;
  chunk = tcs_gc_nursery->next;
  while (chunk != NULL) {
    TcsChunk *next = chunk->next;
#ifdef TCS_GC_STRESS
    memset(chunk->data, 0xA5, chunk->used);
#endif
    free(chunk);
    chunk = next;
  }
  tcs_gc_nursery->next = NULL;
#ifdef TCS_GC_STRESS
  memset(tcs_gc_nursery->data, 0xA5, tcs_gc_nursery->used);
#else
  memset(tcs_gc_nursery->data, 0, tcs_gc_nursery->used);
#endif
  tcs_gc_nursery->used = 0;
  tcs_gc_nursery_cur = tcs_gc_nursery;
  tcs_gc_nursery_chunks = 1;
  tcs_gc_nursery_bytes = 0;
  tcs_gc_nursery_objects = 0;
}

/* ---- Promotion (copy young objects to the old generation, Cheney) ---- */
static void *tcs_gc_promote(void *pointer) {
  TcsGcHeader *hdr;
  TcsGcHeader *copy;
  if (pointer == NULL)
    return NULL;
  hdr = TCS_GC_HEADER(pointer);
  if (hdr->flags & (TCS_GC_OLD | TCS_GC_STATIC))
    return pointer;
  if (hdr->flags & TCS_GC_FORWARD)
    return TCS_GC_PAYLOAD(hdr->next);
  copy = malloc(sizeof(*copy) + hdr->size);
  if (copy == NULL)
    tcs_fault("allocation");
  memcpy(copy, hdr, sizeof(*copy) + hdr->size);
  copy->flags = TCS_GC_OLD;
  copy->next = tcs_gc_objects;
  tcs_gc_objects = copy;
  tcs_gc_object_count++;
  tcs_gc_allocated_since += sizeof(*copy) + hdr->size;
  tcs_gc_promoted_bytes += sizeof(*copy) + hdr->size;
  tcs_gc_old_bytes += sizeof(*copy) + hdr->size;
  hdr->next = copy;
  hdr->flags |= TCS_GC_FORWARD;
  /* The inner slots are handled later by tcs_gc_forward_object (scan queue) */
  if (tcs_gc_mark_count == tcs_gc_mark_capacity) {
    size_t capacity =
        tcs_gc_mark_capacity == 0 ? 1024 : tcs_gc_mark_capacity * 2;
    TcsGcHeader **grown = realloc(tcs_gc_mark_stack, capacity * sizeof(*grown));
    if (grown == NULL)
      tcs_fault("allocation");
    tcs_gc_mark_stack = grown;
    tcs_gc_mark_capacity = capacity;
  }
  tcs_gc_mark_stack[tcs_gc_mark_count++] = copy;
  return TCS_GC_PAYLOAD(copy);
}

static void tcs_gc_forward_slot(void *slot) {
  void *pointer;
  memcpy(&pointer, slot, sizeof(pointer));
  pointer = tcs_gc_promote(pointer);
  memcpy(slot, &pointer, sizeof(pointer));
}

static void tcs_gc_forward_slot_p(void **slot) { tcs_gc_forward_slot(slot); }

static void tcs_gc_forward_value(void *value, const TcsLayout *layout) {
  uint32_t i;
  if (layout == NULL)
    return;
  for (i = 0; i < layout->count; i++)
    tcs_gc_forward_slot((unsigned char *)value + layout->offsets[i]);
}

/* Rewrite all pointer slots of an old object (just promoted / dirty) to point
 * to the promotion targets */
static void tcs_gc_forward_object(TcsGcHeader *hdr) {
  void *payload = TCS_GC_PAYLOAD(hdr);
  size_t i;
  switch (hdr->kind) {
  case TCS_KIND_OBJECT:
    tcs_gc_forward_value(payload, hdr->layout);
    break;
  case TCS_KIND_ARRAY: {
    TcsArray *array = payload;
    if (hdr->layout == NULL)
      break;
    for (i = 0; i < array->length; i++)
      tcs_gc_forward_value(array->data + i * array->element_size, hdr->layout);
    break;
  }
  case TCS_KIND_LIST: {
    TcsList *list = payload;
    list->data = tcs_gc_promote(list->data);
    if (hdr->layout == NULL || list->data == NULL)
      break;
    for (i = 0; i < list->length; i++)
      tcs_gc_forward_value((unsigned char *)list->data + i * list->element_size,
                           hdr->layout);
    break;
  }
  case TCS_KIND_DICT: {
    TcsDict *dict = payload;
    dict->buckets = tcs_gc_promote(dict->buckets);
    for (i = 0; i < dict->bucket_count; i++)
      tcs_gc_forward_slot(&dict->buckets[i]);
    dict->order = tcs_gc_promote(dict->order);
    for (i = 0; i < dict->order_len; i++)
      tcs_gc_forward_slot(&dict->order[i]);
    break;
  }
  case TCS_KIND_DICT_NODE: {
    TcsDictNode *node = payload;
    tcs_gc_forward_slot(&node->next);
    tcs_gc_forward_slot(&node->key_s);
    tcs_gc_forward_value(node->value, hdr->layout);
    break;
  }
  case TCS_KIND_CLOSURE: {
    TcsClosure *closure = payload;
    size_t cells = (hdr->size - sizeof(TcsClosure)) / sizeof(void *);
    for (i = 0; i < cells; i++)
      tcs_gc_forward_slot(&closure->cells[i]);
    break;
  }
  case TCS_KIND_FOREIGN:
    if (tcs_gc_foreign_refs)
      tcs_gc_foreign_refs(payload, tcs_gc_forward_slot_p);
    break;
  default:
    break;
  }
}

/* Frame boundary: the host calls this when the C# stack is empty */
static void __attribute__((noinline)) tcs_gc_frame(void) {
  size_t i;
  tcs_gc_mark_count = 0;
  tcs_gc_forward_statics();
  if (tcs_gc_root_hook)
    tcs_gc_root_hook(tcs_gc_forward_slot_p);
  for (i = 0; i < tcs_gc_hold_count; i++)
    tcs_gc_forward_slot(tcs_gc_holds[i]);
  for (i = 0; i < tcs_gc_dirty_count; i++) {
    tcs_gc_dirty[i]->flags &= ~(uint32_t)TCS_GC_DIRTY;
    tcs_gc_forward_object(tcs_gc_dirty[i]);
  }
  tcs_gc_dirty_count = 0;
  while (tcs_gc_mark_count > 0)
    tcs_gc_forward_object(tcs_gc_mark_stack[--tcs_gc_mark_count]);
  tcs_gc_nursery_reset();
  tcs_gc_frames++;
#ifdef TCS_GC_STRESS
  tcs_gc_collect();
#else
  if (tcs_gc_allocated_since >= tcs_gc_threshold)
    tcs_gc_collect();
#endif
}

/* Pointer slots the host holds across frames (rewritten to the promotion target
 * at a boundary) */
static void tcs_gc_hold(void **slot) {
  if (tcs_gc_hold_count == tcs_gc_hold_capacity) {
    size_t capacity = tcs_gc_hold_capacity == 0 ? 16 : tcs_gc_hold_capacity * 2;
    void ***grown = realloc(tcs_gc_holds, capacity * sizeof(*grown));
    if (grown == NULL)
      tcs_fault("allocation");
    tcs_gc_holds = grown;
    tcs_gc_hold_capacity = capacity;
  }
  tcs_gc_holds[tcs_gc_hold_count++] = slot;
}

static void tcs_gc_release(void **slot) {
  size_t i;
  for (i = 0; i < tcs_gc_hold_count; i++) {
    if (tcs_gc_holds[i] != slot)
      continue;
    tcs_gc_holds[i] = tcs_gc_holds[--tcs_gc_hold_count];
    return;
  }
}

static void *tcs_alloc(size_t size) {
  return tcs_gc_alloc(TCS_KIND_RAW, NULL, size);
}

static void *tcs_new_object(const TcsLayout *layout) {
  return tcs_gc_alloc(TCS_KIND_OBJECT, layout, layout->size);
}

static void *tcs_new_cell(size_t size, const TcsLayout *layout) {
  return tcs_gc_alloc(TCS_KIND_OBJECT, layout, size);
}

static TcsClosure *tcs_new_closure(void *fn, size_t cells) {
  TcsClosure *closure = tcs_gc_alloc(TCS_KIND_CLOSURE, NULL,
                                     sizeof(*closure) + cells * sizeof(void *));
  closure->fn = fn;
  return closure;
}
