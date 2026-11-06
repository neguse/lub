/* --trace の記録と、動いている VM の状態を外から読む口 (tcsvm.h の「検査」)。
   - 行と値: tcs vm --trace が TRACE (文の頭) と TVAL (代入の後) を置き、module の後ろに行の表と値の場所の表を書く。
     interpreter は通った行の印と、値の場所ごとの最後の値を持ち、host が take で受け取って空にする。
   - 値の木 (inspect): --trace のファイルの class の static から辿れる値を、C# の書き方の path で 1 段ずつ返す。
   - 状態の控え (snapshot): 同じ static から辿れる object の中身を写して持ち、同じ object に書き戻す。
     控えた object は回収しない (GC の root)。 */
#include "tcsvm_internal.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- module の後ろの表 (tcsvm_load.c が parse の後に呼ぶ) ---- */

void tvm_debug_free(TvmDebug *d) {
  if (!d)
    return;
  free(d->func_paths);
  free(d->line_files);
  free(d->line_nums);
  free(d->hit);
  free(d->hits);
  free(d->pair_line);
  free(d->pair_self);
  free(d->pair_type);
  free(d->pair_slot);
  free(d->line_flags);
  if (d->vals)
    for (uint32_t i = 0; i < d->nvals; i++)
      free(d->vals[i].buf);
  free(d->vals);
  free(d->dirty);
  free(d->classes);
  free(d->prof_ns);
  free(d->prof_bytes);
  free(d->prof_allocs);
  free(d->prof_hit);
  free(d->prof_hits);
  free(d->prof_new);
  free(d->prof_new_line);
  free(d);
}

static const char *dstr(TvmVm *vm, uint32_t i) { return i < vm->nstrings ? vm->strings[i] : ""; }

bool tvm_debug_read(TvmVm *vm, TcsReader *r) {
  TvmDebug *d = (TvmDebug *)calloc(1, sizeof(TvmDebug));
  if (!d)
    return false;
  vm->debug = d;
  if (r->p >= r->end) /* 表の無い module (古い tcs) */
    return true;
  uint32_t n = tcs_rd_u32(r);
  if (n != vm->nfuncs || !tcs_rd_count_ok(r, n, 4))
    return false;
  d->func_paths = (const char **)calloc(n ? n : 1, sizeof(char *));
  for (uint32_t i = 0; i < n; i++)
    d->func_paths[i] = dstr(vm, tcs_rd_u32(r));
  n = tcs_rd_u32(r);
  if (!tcs_rd_count_ok(r, n, 8))
    return false;
  d->nlines = n;
  d->line_files = (const char **)calloc(n ? n : 1, sizeof(char *));
  d->line_nums = (uint32_t *)calloc(n ? n : 1, sizeof(uint32_t));
  d->hit = (uint8_t *)calloc(n ? n : 1, 1);
  d->hits = (uint32_t *)calloc(n ? n : 1, sizeof(uint32_t));
  d->prof_ns = (uint64_t *)calloc(n ? n : 1, sizeof(uint64_t));
  d->prof_bytes = (uint64_t *)calloc(n ? n : 1, sizeof(uint64_t));
  d->prof_allocs = (uint32_t *)calloc(n ? n : 1, sizeof(uint32_t));
  d->prof_hit = (uint8_t *)calloc(n ? n : 1, 1);
  d->prof_hits = (uint32_t *)calloc(n ? n : 1, sizeof(uint32_t));
  if (!d->line_files || !d->line_nums || !d->hit || !d->hits || !d->prof_ns || !d->prof_bytes || !d->prof_allocs ||
      !d->prof_hit || !d->prof_hits)
    return false;
  for (uint32_t i = 0; i < n; i++) {
    d->line_files[i] = dstr(vm, tcs_rd_u32(r));
    d->line_nums[i] = tcs_rd_u32(r);
  }
  n = tcs_rd_u32(r);
  if (!tcs_rd_count_ok(r, n, 21))
    return false;
  d->nvals = n;
  d->vals = (TvmTraceVal *)calloc(n ? n : 1, sizeof(TvmTraceVal));
  d->dirty = (uint32_t *)calloc(n ? n : 1, sizeof(uint32_t));
  for (uint32_t i = 0; i < n; i++) {
    TvmTraceVal *v = &d->vals[i];
    v->file = dstr(vm, tcs_rd_u32(r));
    v->line = tcs_rd_u32(r);
    v->col = tcs_rd_u32(r);
    v->label = dstr(vm, tcs_rd_u32(r));
    v->kind = tcs_rd_u8(r);
    v->type = tcs_rd_u16(r);
    v->nleaves = tcs_rd_u16(r);
    if (v->nleaves == 0 || v->nleaves > 64)
      return false;
    v->buf = (TvmSlot *)calloc(v->nleaves, sizeof(TvmSlot));
  }
  n = tcs_rd_u32(r);
  if (!tcs_rd_count_ok(r, n, 4))
    return false;
  d->nclasses = n;
  d->classes = (const char **)calloc(n ? n : 1, sizeof(char *));
  for (uint32_t i = 0; i < n; i++)
    d->classes[i] = dstr(vm, tcs_rd_u32(r));
  return !r->bad;
}

/* 値の場所の葉 k の種類 (struct なら型の葉) */
uint8_t tvm_debug_val_leaf_kind(TvmVm *vm, const TvmTraceVal *v, uint32_t k) {
  if (v->kind == TVM_K_STRUCT)
    return v->type < vm->ntypes && k < vm->types[v->type].nleaves ? vm->types[v->type].leaves[k].kind : 0xFF;
  return k == 0 ? v->kind : 0xFF;
}

void tvm_debug_mark(TvmVm *vm, void (*mark)(TvmVm *vm, void *p)) {
  TvmDebug *d = vm->debug;
  if (d)
    for (uint32_t i = 0; i < d->ndirty; i++) {
      TvmTraceVal *v = &d->vals[d->dirty[i]];
      for (uint32_t k = 0; k < v->nleaves; k++)
        if (tvm_debug_val_leaf_kind(vm, v, k) == TVM_K_REF)
          mark(vm, v->buf[k].p);
    }
  for (TvmSnapshot *s = vm->snapshots; s; s = s->next) {
    for (uint32_t i = 0; i < s->nobjs; i++)
      mark(vm, s->objs[i].p);
    for (uint32_t i = 0; i < s->nstatics; i++)
      if (vm->static_info[s->statics[i].slot].kind == TVM_K_REF)
        mark(vm, s->statics[i].value.p);
  }
}

/* ---- 負荷 ---- */

/* 知らされた時間 (prof_pending) は tvm_profile_tick が別の thread からも足すので、どの読み書きも不可分にする */
#if defined(_MSC_VER) && !defined(__clang__)
#include <intrin.h>
/* 文ごとに覗くので、ロックの付く命令にしない (MSVC の volatile の 64 bit の読みは x64 と ARM64 で裂けない) */
static uint64_t pending_peek(TvmVm *vm) { return vm->prof_pending; }
static uint64_t pending_take(TvmVm *vm) { return (uint64_t)_InterlockedExchange64((volatile long long *)&vm->prof_pending, 0); }
void tvm_profile_tick(TvmVm *vm, uint64_t ns) { _InterlockedExchangeAdd64((volatile long long *)&vm->prof_pending, (long long)ns); }
#else
static uint64_t pending_peek(TvmVm *vm) { return __atomic_load_n(&vm->prof_pending, __ATOMIC_RELAXED); }
static uint64_t pending_take(TvmVm *vm) { return __atomic_exchange_n(&vm->prof_pending, 0, __ATOMIC_RELAXED); }
void tvm_profile_tick(TvmVm *vm, uint64_t ns) { __atomic_fetch_add(&vm->prof_pending, ns, __ATOMIC_RELAXED); }
#endif

static void prof_add(TvmDebug *d, uint32_t k, uint64_t ns, uint64_t bytes, uint32_t allocs) {
  if (k >= d->nlines)
    return;
  d->prof_ns[k] += ns;
  d->prof_bytes[k] += bytes;
  d->prof_allocs[k] += allocs;
  if (!d->prof_hit[k]) {
    d->prof_hit[k] = 1;
    d->prof_hits[d->nprof_hits++] = k;
  }
}

/* 知らされた時間を今の行に足す (行の外なら捨てる) */
static void prof_flush(TvmVm *vm) {
  uint64_t ns = pending_take(vm);
  TvmDebug *d = vm->debug;
  if (ns && d && d->prof_cur)
    prof_add(d, d->prof_cur - 1, ns, 0, 0);
}

void tvm_profile_enable(TvmVm *vm, TvmClockFn clock) {
  vm->prof_clock = clock;
  vm->prof_depth = 0;
  if (vm->debug)
    vm->debug->prof_cur = 0;
  pending_take(vm);
  tvm_profile_frame_end(vm);
}

/* 行が変わる前に、それまでに知らされた時間を前の行に足す (知らせが無ければ不可分の読み書きをしない) */
void tvm_profile_line(TvmVm *vm, uint32_t k) {
  if (pending_peek(vm))
    prof_flush(vm);
  vm->debug->prof_cur = k + 1;
}

/* host から一番外の呼び出しに入るときは、それまでに知らされた時間を捨てて行の外から始め、
   出るときに今の行に足して行の外に戻る (呼び出しの外の時間をどの行にも付けない) */
void tvm_profile_enter(TvmVm *vm) {
  if (vm->prof_depth++ > 0)
    return;
  pending_take(vm);
  if (vm->debug)
    vm->debug->prof_cur = 0;
}

void tvm_profile_leave(TvmVm *vm) {
  if (vm->prof_depth == 0 || --vm->prof_depth > 0)
    return;
  prof_flush(vm);
  if (vm->debug)
    vm->debug->prof_cur = 0;
}

static uint32_t new_hash(const void *p, uint32_t cap) { return (uint32_t)(((uintptr_t)p >> 4) * 2654435761u) & (cap - 1); }

static bool new_grow(TvmDebug *d) {
  uint32_t cap = d->capprof_new ? d->capprof_new * 2 : 1024;
  void **keys = (void **)calloc(cap, sizeof(void *));
  uint32_t *lines = (uint32_t *)malloc(cap * sizeof(uint32_t));
  if (!keys || !lines) {
    free(keys);
    free(lines);
    return false;
  }
  for (uint32_t i = 0; i < d->capprof_new; i++) {
    if (!d->prof_new[i])
      continue;
    uint32_t h = new_hash(d->prof_new[i], cap);
    while (keys[h])
      h = (h + 1) & (cap - 1);
    keys[h] = d->prof_new[i];
    lines[h] = d->prof_new_line[i];
  }
  free(d->prof_new);
  free(d->prof_new_line);
  d->prof_new = keys;
  d->prof_new_line = lines;
  d->capprof_new = cap;
  return true;
}

/* p を作った行 (frame の境目の後に作った物でなければ false) */
static bool new_line(const TvmDebug *d, const void *p, uint32_t *line) {
  if (!d->nprof_new)
    return false;
  uint32_t h = new_hash(p, d->capprof_new);
  while (d->prof_new[h]) {
    if (d->prof_new[h] == p) {
      *line = d->prof_new_line[h];
      return true;
    }
    h = (h + 1) & (d->capprof_new - 1);
  }
  return false;
}

/* 確保を今の行に足し、p (shared heap では NULL) を作った行を覚える。回収された物の番地は、同じ番地に作り直すと上書きされる */
void tvm_profile_alloc(TvmVm *vm, void *p, size_t bytes) {
  TvmDebug *d = vm->debug;
  if (!d || !d->prof_cur)
    return;
  uint32_t k = d->prof_cur - 1;
  prof_add(d, k, 0, bytes, 1);
  if (!p || ((d->nprof_new + 1) * 2 > d->capprof_new && !new_grow(d)))
    return;
  uint32_t h = new_hash(p, d->capprof_new);
  while (d->prof_new[h] && d->prof_new[h] != p)
    h = (h + 1) & (d->capprof_new - 1);
  if (!d->prof_new[h]) {
    d->prof_new[h] = p;
    d->nprof_new++;
  }
  d->prof_new_line[h] = k;
  GC_HEADER(p)->flags |= TVM_GC_NEW;
}

/* frame の境目: 作った物を忘れる (TVM_GC_NEW の印は、表に無ければ数えない) */
void tvm_profile_frame_end(TvmVm *vm) {
  TvmDebug *d = vm->debug;
  if (!d || !d->nprof_new)
    return;
  memset(d->prof_new, 0, d->capprof_new * sizeof(void *));
  d->nprof_new = 0;
}

/* GC の前までの時間は今の行に足し、GC の間に知らされた時間は捨てる (GC の長さは時計で測って別に持つ) */
void tvm_profile_gc_begin(TvmVm *vm) { prof_flush(vm); }

void tvm_profile_gc(TvmVm *vm, uint64_t ns, size_t live) {
  pending_take(vm);
  if (vm->nprof_gc == TVM_PROFILE_GCS) {
    memmove(vm->prof_gc, vm->prof_gc + 1, sizeof(vm->prof_gc[0]) * (TVM_PROFILE_GCS - 1));
    vm->nprof_gc--;
  }
  vm->prof_gc[vm->nprof_gc].ns = ns;
  vm->prof_gc[vm->nprof_gc].live = live;
  vm->nprof_gc++;
}

void tvm_profile_take_lines(TvmVm *vm, TvmProfileLineFn fn, void *user) {
  TvmDebug *d = vm->debug;
  if (!d)
    return;
  for (uint32_t i = 0; i < d->nprof_hits; i++) {
    uint32_t k = d->prof_hits[i];
    fn(user, d->line_files[k], d->line_nums[k], d->prof_ns[k], d->prof_bytes[k], d->prof_allocs[k]);
    d->prof_ns[k] = 0;
    d->prof_bytes[k] = 0;
    d->prof_allocs[k] = 0;
    d->prof_hit[k] = 0;
  }
  d->nprof_hits = 0;
}

void tvm_profile_take_gc(TvmVm *vm, TvmProfileGcFn fn, void *user) {
  for (uint32_t i = 0; i < vm->nprof_gc; i++)
    fn(user, vm->prof_gc[i].ns, vm->prof_gc[i].live);
  vm->nprof_gc = 0;
}

/* ---- 行と値の取り出し ---- */

static uint32_t pair_hash(uint32_t k, const void *self) {
  uintptr_t h = (uintptr_t)self >> 3;
  return (uint32_t)(h ^ (h >> 17)) * 2654435761u ^ k * 40503u;
}

/* 組の表を広げ直す (cap は 2 の冪) */
static bool pairs_grow(TvmDebug *d) {
  uint32_t cap = d->cappairs ? d->cappairs * 2 : 256;
  uint32_t *line = (uint32_t *)realloc(d->pair_line, cap * sizeof(uint32_t));
  if (line)
    d->pair_line = line;
  void **self = (void **)realloc(d->pair_self, cap * sizeof(void *));
  if (self)
    d->pair_self = self;
  const TvmType **type = (const TvmType **)realloc((void *)d->pair_type, cap * sizeof(TvmType *));
  if (type)
    d->pair_type = type;
  uint32_t *slot = (uint32_t *)calloc(cap * 2, sizeof(uint32_t));
  if (!line || !self || !type || !slot) {
    free(slot);
    return false;
  }
  free(d->pair_slot);
  d->pair_slot = slot;
  d->cappairs = cap;
  for (uint32_t i = 0; i < d->npairs; i++) {
    uint32_t mask = cap * 2 - 1, at = pair_hash(d->pair_line[i], d->pair_self[i]) & mask;
    while (slot[at])
      at = (at + 1) & mask;
    slot[at] = i + 1;
  }
  return true;
}

void tvm_debug_pair(TvmVm *vm, uint32_t k, void *self) {
  TvmDebug *d = vm->debug;
  if (!self)
    return;
  if (d->cappairs) {
    uint32_t mask = d->cappairs * 2 - 1, at = pair_hash(k, self) & mask;
    for (uint32_t i; (i = d->pair_slot[at]) != 0; at = (at + 1) & mask)
      if (d->pair_line[i - 1] == k && d->pair_self[i - 1] == self)
        return;
  }
  if (d->npairs == d->cappairs && !pairs_grow(d))
    return;
  uint32_t i = d->npairs++, mask = d->cappairs * 2 - 1, at = pair_hash(k, self) & mask;
  d->pair_line[i] = k;
  d->pair_self[i] = self;
  d->pair_type[i] = TVM_TYPE(self);
  while (d->pair_slot[at])
    at = (at + 1) & mask;
  d->pair_slot[at] = i + 1;
}

void tvm_trace_take_lines(TvmVm *vm, TvmTraceLineFn fn, void *user) { tvm_trace_take_lines_for(vm, NULL, fn, user); }

void tvm_trace_take_lines_for(TvmVm *vm, const char *key, TvmTraceLineFn fn, void *user) {
  TvmDebug *d = vm->debug;
  if (!d)
    return;
  void *target = NULL;
  for (uint32_t i = 0; key && key[0] && i < vm->npinned; i++)
    if (strcmp(vm->pinned[i].key, key) == 0)
      target = vm->pinned[i].p;
  /* 行ごとに、1 = target を self にして通った、2 = target と同じ型の別の self で通った、4 = 別の型の self で通った */
  if (target && !d->line_flags)
    d->line_flags = (uint8_t *)calloc(d->nlines ? d->nlines : 1, 1);
  if (target && d->line_flags)
    for (uint32_t i = 0; i < d->npairs; i++)
      d->line_flags[d->pair_line[i]] |= d->pair_self[i] == target ? 1 : d->pair_type[i] == TVM_TYPE(target) ? 2 : 4;
  for (uint32_t i = 0; i < d->nhits; i++) {
    uint32_t k = d->hits[i];
    d->hit[k] = 0;
    uint8_t f = target && d->line_flags ? d->line_flags[k] : 0;
    if (f == 2)
      continue;
    fn(user, d->line_files[k], d->line_nums[k]);
  }
  if (target && d->line_flags)
    for (uint32_t i = 0; i < d->npairs; i++)
      d->line_flags[d->pair_line[i]] = 0;
  d->nhits = 0;
  if (d->npairs)
    memset(d->pair_slot, 0, d->cappairs * 2 * sizeof(uint32_t));
  d->npairs = 0;
}

typedef struct Buf {
  char *p;
  size_t cap, len;
} Buf;

static void put(Buf *b, const char *s, size_t n) {
  if (b->len + 1 >= b->cap)
    return;
  if (n > b->cap - 1 - b->len)
    n = b->cap - 1 - b->len;
  memcpy(b->p + b->len, s, n);
  b->len += n;
  b->p[b->len] = '\0';
}

static void puts_(Buf *b, const char *s) { put(b, s, strlen(s)); }

static void put_float(Buf *b, float f) {
  char tmp[48];
  snprintf(tmp, sizeof tmp, "%.7g", (double)f);
  puts_(b, tmp);
}

static void put_int(Buf *b, int32_t i) {
  char tmp[24];
  snprintf(tmp, sizeof tmp, "%d", (int)i);
  puts_(b, tmp);
}

/* 値は 2 つの置き方のどちらか: packed (object の中の C の layout)、slots (static と register の 1 葉 1 slot) */
typedef struct Loc {
  uint8_t kind;
  uint16_t type;
  const char *addr;
  bool slots;
} Loc;

static int32_t leaf_i32(const Loc *l, uint32_t leaf, uint32_t offset) {
  if (l->slots)
    return ((const TvmSlot *)l->addr)[leaf].i;
  int32_t v;
  memcpy(&v, l->addr + offset, 4);
  return v;
}

static float leaf_f32(const Loc *l, uint32_t leaf, uint32_t offset) {
  if (l->slots)
    return ((const TvmSlot *)l->addr)[leaf].f;
  float v;
  memcpy(&v, l->addr + offset, 4);
  return v;
}

static bool leaf_bool(const Loc *l, uint32_t leaf, uint32_t offset) {
  if (l->slots)
    return ((const TvmSlot *)l->addr)[leaf].i != 0;
  return l->addr[offset] != 0;
}

static void *leaf_ref(const Loc *l, uint32_t leaf, uint32_t offset) {
  if (l->slots)
    return ((const TvmSlot *)l->addr)[leaf].p;
  void *v;
  memcpy(&v, l->addr + offset, sizeof(void *));
  return v;
}

static uint32_t kind_leaves(TvmVm *vm, uint8_t kind, uint16_t type) {
  return kind == TVM_K_STRUCT && type < vm->ntypes ? vm->types[type].nleaves : 1;
}

/* struct の field i の場所 (packed は offset、slots は先の field の葉の数) */
static Loc field_loc(TvmVm *vm, const Loc *s, uint32_t i) {
  const TvmType *st = &vm->types[s->type];
  const TcsField *f = &st->fields[i];
  Loc l = {f->kind, f->type, s->addr, s->slots};
  if (s->slots) {
    uint32_t leaf = 0;
    for (uint32_t k = 0; k < i; k++)
      leaf += kind_leaves(vm, st->fields[k].kind, st->fields[k].type);
    l.addr = s->addr + (size_t)leaf * sizeof(TvmSlot);
  } else
    l.addr = s->addr + f->offset;
  return l;
}

static bool is_simple(uint8_t kind) { return kind == TVM_K_I32 || kind == TVM_K_F32 || kind == TVM_K_BOOL; }

static void put_value(TvmVm *vm, Buf *b, const Loc *l, int depth);

static void put_string(Buf *b, const TvmString *s) {
  size_t n = (size_t)s->len;
  puts_(b, "\"");
  if (n > 40) {
    /* UTF-8 の文字の途中で切らない */
    n = 40;
    while (n > 0 && ((unsigned char)STR_DATA(s)[n] & 0xC0) == 0x80)
      n--;
    put(b, STR_DATA(s), n);
    puts_(b, "\xE2\x80\xA6\"");
  } else {
    put(b, STR_DATA(s), n);
    puts_(b, "\"");
  }
}

/* struct と class の中身の短い書き方: field が 4 つ以下で数と bool だけなら {X=1 Y=2} */
static void put_fields(TvmVm *vm, Buf *b, const TvmType *t, const Loc *base) {
  if (t->nfields == 0 || t->nfields > 4)
    return;
  for (uint32_t i = 0; i < t->nfields; i++)
    if (!is_simple(t->fields[i].kind))
      return;
  puts_(b, "{");
  for (uint32_t i = 0; i < t->nfields; i++) {
    if (i)
      puts_(b, " ");
    puts_(b, t->fields[i].name);
    puts_(b, "=");
    Loc f = field_loc(vm, base, i);
    put_value(vm, b, &f, 1);
  }
  puts_(b, "}");
}

/* object の型。host が型の印の無い blob を field に置くこと (lub の native の値の block) があるので、
   blob と型の無い object は NULL */
static const TvmType *type_of(void *p) {
  const TvmGcHeader *h = GC_HEADER(p);
  return GC_BLOB(h) ? NULL : h->type;
}

static bool is_func(void *p) {
  const TvmType *t = p ? type_of(p) : NULL;
  return t && t->tkind == TVM_T_FUNC;
}

static void put_object(TvmVm *vm, Buf *b, void *p, int depth) {
  const TvmType *t = type_of(p);
  if (!t) {
    puts_(b, "<native>");
    return;
  }
  switch (t->tkind) {
  case TVM_T_STRING:
    put_string(b, (const TvmString *)p);
    return;
  case TVM_T_LIST:
    puts_(b, "[");
    put_int(b, ((TvmList *)p)->count);
    puts_(b, "]");
    return;
  case TVM_T_ARRAY:
    puts_(b, "[");
    put_int(b, ((TvmArray *)p)->len);
    puts_(b, "]");
    return;
  case TVM_T_DICT:
    puts_(b, "{\xE2\x80\xA6}");
    return;
  case TVM_T_BOX: {
    Loc l = {t->elem_kind, t->elem_type, (const char *)p, false};
    put_value(vm, b, &l, depth);
    return;
  }
  case TVM_T_FUNC:
    puts_(b, "function");
    return;
  case TVM_T_CLASS: {
    puts_(b, t->name);
    if (depth == 0) {
      Loc base = {TVM_K_STRUCT, (uint16_t)(t - vm->types), (const char *)p, false};
      put_fields(vm, b, t, &base);
    }
    return;
  }
  default:
    puts_(b, t->name ? t->name : "?");
  }
}

static void put_value(TvmVm *vm, Buf *b, const Loc *l, int depth) {
  switch (l->kind) {
  case TVM_K_I32:
    put_int(b, leaf_i32(l, 0, 0));
    return;
  case TVM_K_F32:
    put_float(b, leaf_f32(l, 0, 0));
    return;
  case TVM_K_BOOL:
    puts_(b, leaf_bool(l, 0, 0) ? "true" : "false");
    return;
  case TVM_K_REF: {
    void *p = leaf_ref(l, 0, 0);
    if (!p)
      puts_(b, "null");
    else
      put_object(vm, b, p, depth);
    return;
  }
  case TVM_K_STRUCT: {
    if (l->type >= vm->ntypes)
      return;
    const TvmType *t = &vm->types[l->type];
    puts_(b, t->name);
    if (depth == 0)
      put_fields(vm, b, t, l);
    return;
  }
  }
}

typedef struct ValueCtx {
  TvmTraceValueFn fn;
  void *user;
} ValueCtx;

void tvm_trace_take_values(TvmVm *vm, TvmTraceValueFn fn, void *user) {
  TvmDebug *d = vm->debug;
  if (!d)
    return;
  for (uint32_t i = 0; i < d->ndirty; i++) {
    TvmTraceVal *v = &d->vals[d->dirty[i]];
    char text[160];
    Buf b = {text, sizeof text, 0};
    text[0] = '\0';
    Loc l = {v->kind, v->type, (const char *)v->buf, true};
    put_value(vm, &b, &l, 0);
    v->set = false;
    fn(user, v->file, v->line, v->col, v->label, text);
  }
  d->ndirty = 0;
}

/* ---- 呼び出しの並び ---- */

static uint32_t line_at(const TvmProto *p, const uint64_t *pc) {
  if (!p->nlines || !pc)
    return 0;
  uint32_t at = (uint32_t)(pc - p->code);
  if (at > 0)
    at--; /* pc は次の命令を指す */
  uint32_t line = p->lines[0].line;
  for (uint32_t k = 0; k < p->nlines && p->lines[k].pc <= at; k++)
    line = p->lines[k].line;
  return line;
}

const char *tvm_func_path(TvmVm *vm, const TvmProto *p) {
  TvmDebug *d = vm->debug;
  if (d && d->func_paths && p >= vm->funcs && p < vm->funcs + vm->nfuncs) {
    const char *path = d->func_paths[p - vm->funcs];
    if (path && path[0])
      return path;
  }
  return p->file ? p->file : "";
}

void tvm_stack(TvmVm *vm, TvmFrameFn fn, void *user) {
  /* 今の frame は ftop の 1 つ下 (host の関数の中から呼ぶ) */
  for (TvmFrame *fr = vm->ftop; fr > vm->frames;) {
    fr--;
    if (fr->proto)
      fn(user, tvm_func_path(vm, fr->proto), line_at(fr->proto, fr->pc));
  }
}

void tvm_stack_self(TvmVm *vm, TvmFrameSelfFn fn, void *user) {
  for (TvmFrame *fr = vm->ftop; fr > vm->frames;) {
    fr--;
    if (!fr->proto)
      continue;
    char id[32] = "";
    void *self = fr->proto->trace_self ? fr->R[fr->proto->trace_self - 1].p : NULL;
    if (self)
      snprintf(id, sizeof id, "%p", self);
    fn(user, tvm_func_path(vm, fr->proto), line_at(fr->proto, fr->pc), id);
  }
}

uint32_t tvm_traced_class_count(TvmVm *vm) { return vm->debug ? vm->debug->nclasses : 0; }

const char *tvm_traced_class(TvmVm *vm, uint32_t i) {
  return vm->debug && i < vm->debug->nclasses ? vm->debug->classes[i] : "";
}

/* ---- 値の木 ---- */

/* 根の class: entry が先頭、ほかは --trace の class を名前の順 */
static uint32_t root_classes(TvmVm *vm, const char *entry, const char **out, uint32_t max) {
  uint32_t n = 0;
  if (entry && entry[0] && n < max)
    out[n++] = entry;
  uint32_t first = n;
  for (uint32_t i = 0; i < tvm_traced_class_count(vm) && n < max; i++) {
    const char *c = tvm_traced_class(vm, i);
    if (entry && strcmp(c, entry) == 0)
      continue;
    uint32_t at = n;
    while (at > first && strcmp(out[at - 1], c) > 0) {
      out[at] = out[at - 1];
      at--;
    }
    out[at] = c;
    n++;
  }
  return n;
}

/* class の static field (名前は "型.field"。初期化の flag の "<init>" は除く) */
static bool static_of(const TvmStaticField *f, const char *cls) {
  size_t n = strlen(cls);
  return strncmp(f->name, cls, n) == 0 && f->name[n] == '.' && !strchr(f->name + n + 1, '<');
}

static Loc static_loc(TvmVm *vm, const TvmStaticField *f) {
  Loc l = {f->kind, f->type, (const char *)&vm->statics[f->slot], true};
  return l;
}

static bool loc_null(const Loc *l) { return l->kind == TVM_K_REF && !leaf_ref(l, 0, 0); }

/* 子の 1 つ (name は親から見た名前、path に足す形) */
typedef struct Child {
  char name[256];
  Loc loc;
  int32_t sort_i; /* Dictionary の int の key */
  bool sort_num;
} Child;

typedef struct Children {
  Child *v;
  uint32_t n, cap;
  uint32_t first; /* v[0] が子の何番目か (List と配列は頼まれた範囲だけ並べる) */
} Children;

static Child *child_add(Children *c) {
  if (c->n == c->cap) {
    uint32_t cap = c->cap ? c->cap * 2 : 16;
    Child *g = (Child *)realloc(c->v, cap * sizeof(Child));
    if (!g)
      return NULL;
    c->v = g;
    c->cap = cap;
  }
  Child *ch = &c->v[c->n++];
  memset(ch, 0, sizeof *ch);
  return ch;
}

static int cmp_child(const void *a, const void *b) {
  const Child *x = (const Child *)a, *y = (const Child *)b;
  if (x->sort_num && y->sort_num)
    return x->sort_i < y->sort_i ? -1 : x->sort_i > y->sort_i;
  return strcmp(x->name, y->name);
}

/* 値 l の子を out に並べる (null の field と関数は除く)。
   List と配列は from 番目から count 個だけ並べる (要素の数に比例する手間をかけない)。ほかは全部並べる */
static void children_of(TvmVm *vm, const Loc *l, uint32_t from, uint32_t count, Children *out) {
  out->first = 0;
  if (l->kind == TVM_K_STRUCT) {
    if (l->type >= vm->ntypes)
      return;
    const TvmType *t = &vm->types[l->type];
    for (uint32_t i = 0; i < t->nfields; i++) {
      Loc f = field_loc(vm, l, i);
      if (loc_null(&f))
        continue;
      Child *c = child_add(out);
      if (!c)
        return;
      snprintf(c->name, sizeof c->name, ".%s", t->fields[i].name);
      c->loc = f;
    }
    qsort(out->v, out->n, sizeof(Child), cmp_child);
    return;
  }
  if (l->kind != TVM_K_REF)
    return;
  void *p = leaf_ref(l, 0, 0);
  if (!p)
    return;
  const TvmType *t = type_of(p);
  if (!t)
    return;
  switch (t->tkind) {
  case TVM_T_CLASS:
    for (uint32_t i = 0; i < t->nfields; i++) {
      Loc f = {t->fields[i].kind, t->fields[i].type, (const char *)p + t->fields[i].offset, false};
      if (loc_null(&f) || (f.kind == TVM_K_REF && is_func(leaf_ref(&f, 0, 0))))
        continue;
      Child *c = child_add(out);
      if (!c)
        return;
      snprintf(c->name, sizeof c->name, ".%s", t->fields[i].name);
      c->loc = f;
    }
    qsort(out->v, out->n, sizeof(Child), cmp_child);
    return;
  case TVM_T_LIST:
  case TVM_T_ARRAY: {
    uint32_t n = (uint32_t)(t->tkind == TVM_T_LIST ? ((TvmList *)p)->count : ((TvmArray *)p)->len);
    const char *data = t->tkind == TVM_T_LIST ? ((TvmList *)p)->data : ARR_DATA(p);
    uint32_t lo = from < n ? from : n;
    uint32_t hi = count < n - lo ? lo + count : n;
    out->first = lo;
    for (uint32_t i = lo; i < hi; i++) {
      Child *c = child_add(out);
      if (!c)
        return;
      snprintf(c->name, sizeof c->name, "[%u]", (unsigned)i);
      Loc e = {t->elem_kind, t->elem_type, data + (size_t)i * t->elem_size, false};
      c->loc = e;
    }
    return;
  }
  case TVM_T_DICT: {
    TvmDict *d = (TvmDict *)p;
    for (int32_t i = 0; i < d->used; i++) {
      TvmDictEntry *e = DICT_ENTRY(d, i);
      if (!e->live)
        continue;
      Child *c = child_add(out);
      if (!c)
        return;
      const TvmType *kt = t->key_kind == TVM_K_REF && e->key.p ? type_of(e->key.p) : NULL;
      if (kt && kt->tkind == TVM_T_STRING) {
        const TvmString *s = (const TvmString *)e->key.p;
        snprintf(c->name, sizeof c->name, "[\"%.*s\"]", (int)(s->len > 200 ? 200 : s->len), STR_DATA(s));
      } else if (t->key_kind == TVM_K_I32) {
        snprintf(c->name, sizeof c->name, "[%d]", (int)e->key.i);
        c->sort_i = e->key.i;
        c->sort_num = true;
      } else if (t->key_kind == TVM_K_BOOL)
        snprintf(c->name, sizeof c->name, "[%s]", e->key.i ? "true" : "false");
      else
        snprintf(c->name, sizeof c->name, "[#%d]", (int)i);
      Loc v = {t->elem_kind, t->elem_type, DICT_VALUE(e), false};
      c->loc = v;
    }
    qsort(out->v, out->n, sizeof(Child), cmp_child);
    return;
  }
  default:
    return;
  }
}

static void class_children(TvmVm *vm, const char *cls, Children *out) {
  for (uint32_t i = 0; i < vm->nstatic_fields; i++) {
    const TvmStaticField *f = &vm->static_fields[i];
    if (!static_of(f, cls))
      continue;
    Loc l = static_loc(vm, f);
    if (loc_null(&l) || (l.kind == TVM_K_REF && is_func(leaf_ref(&l, 0, 0))))
      continue;
    Child *c = child_add(out);
    if (!c)
      return;
    snprintf(c->name, sizeof c->name, ".%s", f->name + strlen(cls) + 1);
    c->loc = l;
  }
}

static void *loc_object(const Loc *l) { return l->kind == TVM_K_REF ? leaf_ref(l, 0, 0) : NULL; }

static uint32_t count_children(TvmVm *vm, const Loc *l) {
  /* List と配列は要素を並べずに数える */
  void *p = loc_object(l);
  const TvmType *t = p ? type_of(p) : NULL;
  if (t && t->tkind == TVM_T_LIST)
    return (uint32_t)((TvmList *)p)->count;
  if (t && t->tkind == TVM_T_ARRAY)
    return (uint32_t)((TvmArray *)p)->len;
  Children c = {0};
  children_of(vm, l, 0, UINT32_MAX, &c);
  free(c.v);
  return c.n;
}

/* path を辿る。根の class なら *cls、値なら *loc に置き、祖先の object を anc に並べる。辿れなければ false */
typedef struct Walk {
  const char *cls; /* 根の class の節 (値でない) */
  Loc loc;
  void *anc[64];
  uint32_t nanc;
  /* 最後の段の親 (InspectHistory が控えから読む): 親が object なら parent、static なら parent_static */
  void *parent;
  const TvmStaticField *parent_static;
  uint32_t parent_offset; /* parent の payload の中の位置 */
} Walk;

/* path の段 s (n 文字) が [数] なら、その数を *out に置く */
static bool path_index(const char *s, size_t n, uint32_t *out) {
  if (n < 3 || s[0] != '[' || s[n - 1] != ']')
    return false;
  uint64_t v = 0;
  for (size_t i = 1; i + 1 < n; i++) {
    if (s[i] < '0' || s[i] > '9' || v > UINT32_MAX)
      return false;
    v = v * 10 + (uint64_t)(s[i] - '0');
  }
  if (v >= UINT32_MAX)
    return false;
  *out = (uint32_t)v;
  return true;
}

static bool walk_path(TvmVm *vm, const char *entry, const char *path, Walk *w) {
  memset(w, 0, sizeof *w);
  const char *roots[256];
  uint32_t nroots = root_classes(vm, entry, roots, 256);
  const char *rest = NULL;
  size_t best = 0;
  for (uint32_t i = 0; i < nroots; i++) {
    size_t n = strlen(roots[i]);
    if (n > best && strncmp(path, roots[i], n) == 0 && (path[n] == '\0' || path[n] == '.' || path[n] == '[')) {
      best = n;
      w->cls = roots[i];
      rest = path + n;
    }
  }
  if (!w->cls)
    return false;
  if (!*rest)
    return true;
  /* 最初の段は class の static */
  Children c = {0};
  bool have = false;
  while (*rest) {
    const char *end = rest + 1;
    if (*rest == '[') {
      /* ["..."] の中の ']' は文字列の中にありうる */
      if (end[0] == '"') {
        end++;
        while (*end && !(end[0] == '"' && end[1] == ']'))
          end++;
        end = *end ? end + 2 : end;
      } else {
        while (*end && *end != ']')
          end++;
        end = *end ? end + 1 : end;
      }
    } else
      while (*end && *end != '.' && *end != '[')
        end++;
    size_t n = (size_t)(end - rest);
    c.n = 0;
    if (!have)
      class_children(vm, w->cls, &c);
    else {
      void *o = loc_object(&w->loc);
      if (o && w->nanc < 64)
        w->anc[w->nanc++] = o;
      /* List と配列の [i] は i 番目だけ並べる */
      uint32_t at;
      if (path_index(rest, n, &at))
        children_of(vm, &w->loc, at, 1, &c);
      else
        children_of(vm, &w->loc, 0, UINT32_MAX, &c);
    }
    bool found = false;
    for (uint32_t i = 0; i < c.n && !found; i++)
      if (strlen(c.v[i].name) == n && strncmp(c.v[i].name, rest, n) == 0) {
        /* 親を覚える (static か object の payload) */
        w->parent = NULL;
        w->parent_static = NULL;
        if (!have) {
          for (uint32_t k = 0; k < vm->nstatic_fields; k++)
            if ((const char *)&vm->statics[vm->static_fields[k].slot] == c.v[i].loc.addr) {
              w->parent_static = &vm->static_fields[k];
              break;
            }
        } else {
          void *o = loc_object(&w->loc);
          if (o && !c.v[i].loc.slots) {
            w->parent = o;
            w->parent_offset = (uint32_t)(c.v[i].loc.addr - (const char *)o);
          }
        }
        w->loc = c.v[i].loc;
        found = true;
      }
    if (!found) {
      free(c.v);
      return false;
    }
    have = true;
    w->cls = NULL;
    rest = end;
  }
  free(c.v);
  return true;
}

static void fill_node(TvmVm *vm, TvmInspectNode *node, const char *parent_path, const char *name, const Loc *l,
                      const Walk *w) {
  memset(node, 0, sizeof *node);
  snprintf(node->path, sizeof node->path, "%s%s", parent_path, name);
  snprintf(node->name, sizeof node->name, "%s", name[0] == '.' ? name + 1 : name);
  Buf b = {node->text, sizeof node->text, 0};
  put_value(vm, &b, l, 0);
  void *o = loc_object(l);
  if (o) {
    const TvmType *t = type_of(o);
    if (t && t->tkind == TVM_T_CLASS)
      node->type = t->name;
    if (!t || t->tkind != TVM_T_STRING)
      snprintf(node->id, sizeof node->id, "%p", o);
    for (uint32_t i = 0; i < w->nanc; i++)
      if (w->anc[i] == o)
        node->cycle = true;
  }
  node->count = node->cycle ? 0 : (int32_t)count_children(vm, l);
}

int32_t tvm_inspect(TvmVm *vm, const char *entry, const char *path, int32_t offset, int32_t count,
                    TvmInspectNode *out) {
  if (offset < 0)
    offset = 0;
  int32_t written = 0;
  if (!path || !path[0]) {
    const char *roots[256];
    uint32_t n = root_classes(vm, entry, roots, 256);
    for (uint32_t i = (uint32_t)offset; i < n && written < count; i++) {
      TvmInspectNode *node = &out[written++];
      memset(node, 0, sizeof *node);
      snprintf(node->path, sizeof node->path, "%s", roots[i]);
      snprintf(node->name, sizeof node->name, "%s", roots[i]);
      Children c = {0};
      class_children(vm, roots[i], &c);
      node->count = (int32_t)c.n;
      free(c.v);
    }
    return written;
  }
  Walk w;
  if (!walk_path(vm, entry, path, &w))
    return 0;
  Children c = {0};
  if (w.cls)
    class_children(vm, w.cls, &c);
  else {
    void *o = loc_object(&w.loc);
    if (o && w.nanc < 64)
      w.anc[w.nanc++] = o;
    children_of(vm, &w.loc, (uint32_t)offset, count > 0 ? (uint32_t)count : 0, &c);
  }
  for (uint32_t i = (uint32_t)offset > c.first ? (uint32_t)offset - c.first : 0; i < c.n && written < count; i++)
    fill_node(vm, &out[written++], path, c.v[i].name, &c.v[i].loc, &w);
  free(c.v);
  return written;
}

/* ---- 弱く覚える object ---- */

void tvm_inspect_pin(TvmVm *vm, const char *key, const char *entry, const char *path) {
  void *o = NULL;
  Walk w;
  if (path && path[0] && walk_path(vm, entry, path, &w) && !w.cls)
    o = loc_object(&w.loc);
  for (uint32_t i = 0; i < vm->npinned; i++)
    if (strcmp(vm->pinned[i].key, key) == 0) {
      vm->pinned[i].p = o;
      return;
    }
  if (!o)
    return;
  if (vm->npinned == vm->cappinned) {
    uint32_t cap = vm->cappinned ? vm->cappinned * 2 : 8;
    TvmPinned *g = (TvmPinned *)realloc(vm->pinned, cap * sizeof(TvmPinned));
    if (!g)
      return;
    vm->pinned = g;
    vm->cappinned = cap;
  }
  TvmPinned *pin = &vm->pinned[vm->npinned++];
  snprintf(pin->key, sizeof pin->key, "%s", key);
  pin->p = o;
}

bool tvm_inspect_pinned(TvmVm *vm, const char *key, const char *id) {
  for (uint32_t i = 0; i < vm->npinned; i++)
    if (strcmp(vm->pinned[i].key, key) == 0) {
      if (!vm->pinned[i].p)
        return false;
      char now[32];
      snprintf(now, sizeof now, "%p", vm->pinned[i].p);
      return strcmp(now, id) == 0;
    }
  return false;
}

/* 回収した object を覚えていたら忘れる (tcsvm_gc.c の sweep が呼ぶ) */
void tvm_debug_forget(TvmVm *vm, void *p) {
  for (uint32_t i = 0; i < vm->npinned; i++)
    if (vm->pinned[i].p == p)
      vm->pinned[i].p = NULL;
}

/* ---- 状態の控え ---- */

typedef struct Seen {
  void **keys;
  uint32_t *vals;
  uint32_t cap, n;
} Seen;

static uint32_t hash_ptr(const void *p, uint32_t cap) {
  uintptr_t x = (uintptr_t)p;
  x ^= x >> 17;
  x *= 0xed5ad4bbu;
  x ^= x >> 11;
  return (uint32_t)x & (cap - 1);
}

static bool seen_grow(Seen *s) {
  uint32_t cap = s->cap ? s->cap * 2 : 256;
  void **keys = (void **)calloc(cap, sizeof(void *));
  uint32_t *vals = (uint32_t *)calloc(cap, sizeof(uint32_t));
  if (!keys || !vals) {
    free(keys);
    free(vals);
    return false;
  }
  for (uint32_t i = 0; i < s->cap; i++)
    if (s->keys[i]) {
      uint32_t h = hash_ptr(s->keys[i], cap);
      while (keys[h])
        h = (h + 1) & (cap - 1);
      keys[h] = s->keys[i];
      vals[h] = s->vals[i];
    }
  free(s->keys);
  free(s->vals);
  s->keys = keys;
  s->vals = vals;
  s->cap = cap;
  return true;
}

/* 見つかれば番号、無ければ足して UINT32_MAX を返す (足せなければ UINT32_MAX - 1) */
static uint32_t seen_put(Seen *s, void *p, uint32_t val) {
  if ((s->n + 1) * 2 > s->cap && !seen_grow(s))
    return UINT32_MAX - 1;
  uint32_t h = hash_ptr(p, s->cap);
  while (s->keys[h]) {
    if (s->keys[h] == p)
      return s->vals[h];
    h = (h + 1) & (s->cap - 1);
  }
  s->keys[h] = p;
  s->vals[h] = val;
  s->n++;
  return UINT32_MAX;
}

static uint32_t seen_get(const Seen *s, const void *p) {
  if (!s->cap)
    return UINT32_MAX;
  uint32_t h = hash_ptr(p, s->cap);
  while (s->keys[h]) {
    if (s->keys[h] == p)
      return s->vals[h];
    h = (h + 1) & (s->cap - 1);
  }
  return UINT32_MAX;
}

typedef struct TakeCtx {
  TvmVm *vm;
  TvmSnapshot *s;
  Seen seen;
  void **stack;
  uint32_t nstack, capstack;
  bool failed;
} TakeCtx;

static void snap_add(TakeCtx *t, void *p) {
  if (!p || t->failed)
    return;
  TvmGcHeader *h = GC_HEADER(p);
  if (h->flags & TVM_GC_STATIC)
    return; /* string の定数 (回収されず、変わらない) */
  TvmSnapshot *s = t->s;
  uint32_t r = seen_put(&t->seen, p, s->nobjs);
  if (r == UINT32_MAX - 1) {
    t->failed = true;
    return;
  }
  if (r != UINT32_MAX)
    return;
  if (s->nobjs == s->capobjs) {
    uint32_t cap = s->capobjs ? s->capobjs * 2 : 64;
    TvmSnapObj *g = (TvmSnapObj *)realloc(s->objs, cap * sizeof(TvmSnapObj));
    if (!g) {
      t->failed = true;
      return;
    }
    s->objs = g;
    s->capobjs = cap;
  }
  TvmSnapObj *o = &s->objs[s->nobjs++];
  o->p = p;
  /* string は中身が変わらないので写さない (生かしておくだけ) */
  bool copy = GC_BLOB(h) || !h->type || h->type->tkind != TVM_T_STRING;
  o->size = copy ? h->size : 0;
  o->bytes = o->size ? malloc(o->size) : NULL;
  if (o->size && !o->bytes) {
    t->failed = true;
    return;
  }
  if (o->size)
    memcpy(o->bytes, p, o->size);
  /* blob (List の要素、Dictionary の entry) は持ち主が中を辿る */
  if (GC_BLOB(h) || !h->type)
    return;
  if (t->nstack == t->capstack) {
    uint32_t cap = t->capstack ? t->capstack * 2 : 64;
    void **g = (void **)realloc(t->stack, cap * sizeof(void *));
    if (!g) {
      t->failed = true;
      return;
    }
    t->stack = g;
    t->capstack = cap;
  }
  t->stack[t->nstack++] = p;
}

static void snap_slot(void *ctx, void **slot) { snap_add((TakeCtx *)ctx, *slot); }

/* 根の class (entry と --trace の class) の static の slot を控える */
static void snap_statics(TakeCtx *t, const char *entry) {
  TvmVm *vm = t->vm;
  const char *roots[256];
  uint32_t nroots = root_classes(vm, entry, roots, 256);
  TvmSnapshot *s = t->s;
  for (uint32_t i = 0; i < vm->nstatic_fields; i++) {
    const TvmStaticField *f = &vm->static_fields[i];
    bool root = false;
    for (uint32_t k = 0; k < nroots && !root; k++) {
      size_t n = strlen(roots[k]);
      root = strncmp(f->name, roots[k], n) == 0 && f->name[n] == '.';
    }
    if (!root)
      continue;
    uint32_t leaves = kind_leaves(vm, f->kind, f->type);
    for (uint32_t k = 0; k < leaves; k++) {
      uint32_t slot = f->slot + k;
      if (s->nstatics == s->capstatics) {
        uint32_t cap = s->capstatics ? s->capstatics * 2 : 64;
        TvmSnapStatic *g = (TvmSnapStatic *)realloc(s->statics, cap * sizeof(TvmSnapStatic));
        if (!g) {
          t->failed = true;
          return;
        }
        s->statics = g;
        s->capstatics = cap;
      }
      s->statics[s->nstatics].slot = slot;
      s->statics[s->nstatics].value = vm->statics[slot];
      s->nstatics++;
      if (vm->static_info[slot].kind == TVM_K_REF)
        snap_add(t, vm->statics[slot].p);
    }
  }
}

TvmSnapshot *tvm_snapshot_take(TvmVm *vm, const char *entry) {
  TvmSnapshot *s = (TvmSnapshot *)calloc(1, sizeof(TvmSnapshot));
  if (!s)
    return NULL;
  s->layout_version = vm->layout_version;
  TakeCtx t = {vm, s, {0}, NULL, 0, 0, false};
  snap_statics(&t, entry);
  while (t.nstack && !t.failed)
    tvm_object_refs(vm, t.stack[--t.nstack], snap_slot, &t);
  free(t.stack);
  s->seen_keys = t.seen.keys;
  s->seen_vals = t.seen.vals;
  s->seen_cap = t.seen.cap;
  if (t.failed) {
    tvm_snapshot_free(vm, s);
    return NULL;
  }
  s->next = vm->snapshots;
  vm->snapshots = s;
  return s;
}

void tvm_snapshots_invalidate(TvmVm *vm) {
  for (TvmSnapshot *s = vm->snapshots; s; s = s->next) {
    for (uint32_t i = 0; i < s->nobjs; i++)
      free(s->objs[i].bytes);
    s->nobjs = 0;
    s->nstatics = 0;
    if (s->seen_keys)
      memset(s->seen_keys, 0, s->seen_cap * sizeof(void *));
  }
}

void tvm_snapshot_restore(TvmVm *vm, const TvmSnapshot *s) {
  if (!s || s->layout_version != vm->layout_version)
    return;
  for (uint32_t i = 0; i < s->nobjs; i++) {
    if (!s->objs[i].size)
      continue;
    TvmGcHeader *h = GC_HEADER(s->objs[i].p);
    /* class の型の番号は reload で変わりうるので、今の番号のまま残す */
    bool cls = !GC_BLOB(h) && h->type && h->type->tkind == TVM_T_CLASS;
    uint32_t type_id = cls ? ((TvmObject *)s->objs[i].p)->type_id : 0;
    memcpy(s->objs[i].p, s->objs[i].bytes, s->objs[i].size);
    if (cls)
      ((TvmObject *)s->objs[i].p)->type_id = type_id;
  }
  for (uint32_t i = 0; i < s->nstatics; i++)
    if (s->statics[i].slot < vm->nstatics)
      vm->statics[s->statics[i].slot] = s->statics[i].value;
}

void tvm_snapshot_free(TvmVm *vm, TvmSnapshot *s) {
  if (!s)
    return;
  for (TvmSnapshot **link = &vm->snapshots; *link; link = &(*link)->next)
    if (*link == s) {
      *link = s->next;
      break;
    }
  for (uint32_t i = 0; i < s->nobjs; i++)
    free(s->objs[i].bytes);
  free(s->objs);
  free(s->statics);
  free(s->seen_keys);
  free(s->seen_vals);
  free(s);
}

uint32_t tvm_layout_version(TvmVm *vm) { return vm->layout_version; }

/* 控えの中の object p の写し (無ければ NULL) */
static const char *snap_bytes(const TvmSnapshot *s, const void *p) {
  Seen seen = {s->seen_keys, s->seen_vals, s->seen_cap, 0};
  uint32_t i = seen_get(&seen, p);
  return i < s->nobjs && s->objs[i].size ? (const char *)s->objs[i].bytes : NULL;
}

bool tvm_snapshot_number(TvmVm *vm, const TvmSnapshot *s, const char *entry, const char *path, float *out) {
  if (!s || s->layout_version != vm->layout_version)
    return false;
  Walk w;
  if (!walk_path(vm, entry, path, &w) || w.cls)
    return false;
  Loc l = w.loc;
  if (w.parent) {
    const char *bytes = snap_bytes(s, w.parent);
    if (!bytes)
      return false;
    l.addr = bytes + w.parent_offset;
    l.slots = false;
  } else if (w.parent_static) {
    const TvmSnapStatic *found = NULL;
    for (uint32_t i = 0; i < s->nstatics && !found; i++)
      if (s->statics[i].slot == w.parent_static->slot)
        found = &s->statics[i];
    if (!found)
      return false;
    l.addr = (const char *)&found->value;
    l.slots = true;
  } else
    return false;
  if (l.kind == TVM_K_I32)
    *out = (float)leaf_i32(&l, 0, 0);
  else if (l.kind == TVM_K_F32)
    *out = leaf_f32(&l, 0, 0);
  else
    return false;
  return true;
}

/* ---- 持ち越し (tvm_profile_take_kept) ---- */

/* 持つ field: static は name だけ ("class.field")、object の field は型の名前と field の名前 */
typedef struct KeptAt {
  const char *name, *field;
} KeptAt;

typedef struct KeptRow {
  uint32_t line;
  KeptAt at;
  uint32_t count;
  uint64_t bytes;
} KeptRow;

/* 根から幅優先で辿る。objs は辿る順の列で、辿った印を消すのにも使う */
typedef struct KeptWalk {
  TvmVm *vm;
  void **objs;
  KeptAt *ats;
  uint8_t *fresh;
  uint32_t n, cap, cur;
  KeptRow *rows;
  uint32_t nrows, caprows;
  bool failed;
} KeptWalk;

static void kept_add(KeptWalk *w, uint32_t line, KeptAt at, uint64_t bytes) {
  for (uint32_t i = w->nrows; i-- > 0;) {
    KeptRow *r = &w->rows[i];
    if (r->line == line && r->at.name == at.name && r->at.field == at.field) {
      r->count++;
      r->bytes += bytes;
      return;
    }
  }
  if (w->nrows == w->caprows) {
    uint32_t cap = w->caprows ? w->caprows * 2 : 16;
    KeptRow *g = (KeptRow *)realloc(w->rows, cap * sizeof(KeptRow));
    if (!g) {
      w->failed = true;
      return;
    }
    w->rows = g;
    w->caprows = cap;
  }
  w->rows[w->nrows++] = (KeptRow){line, at, 1, bytes};
}

static void kept_visit(KeptWalk *w, void *p, KeptAt at) {
  if (!p || w->failed)
    return;
  TvmGcHeader *h = GC_HEADER(p);
  if (h->flags & (TVM_GC_STATIC | TVM_GC_SEEN))
    return;
  if (w->n == w->cap) {
    uint32_t cap = w->cap ? w->cap * 2 : 256;
    void **objs = (void **)realloc(w->objs, cap * sizeof(void *));
    if (objs)
      w->objs = objs;
    KeptAt *ats = (KeptAt *)realloc(w->ats, cap * sizeof(KeptAt));
    if (ats)
      w->ats = ats;
    uint8_t *fresh = (uint8_t *)realloc(w->fresh, cap);
    if (fresh)
      w->fresh = fresh;
    if (!objs || !ats || !fresh) {
      w->failed = true;
      return;
    }
    w->cap = cap;
  }
  h->flags |= TVM_GC_SEEN;
  uint32_t line;
  bool fresh = (h->flags & TVM_GC_NEW) && new_line(w->vm->debug, p, &line);
  h->flags &= (uint16_t)~TVM_GC_NEW;
  if (fresh)
    kept_add(w, line, at, h->size + sizeof(TvmGcHeader));
  w->objs[w->n] = p;
  w->ats[w->n] = at;
  w->fresh[w->n] = fresh;
  w->n++;
}

/* 辿っている object の slot の先を、持つ field を決めて辿る */
static void kept_slot(void *ctx, void **slot) {
  KeptWalk *w = (KeptWalk *)ctx;
  void *p = w->objs[w->cur];
  KeptAt at = w->ats[w->cur];
  const TvmType *t = TVM_TYPE(p);
  if (!w->fresh[w->cur] && t->tkind == TVM_T_CLASS) {
    uint32_t off = (uint32_t)((char *)slot - (char *)p);
    const TcsField *best = NULL;
    for (uint32_t i = 0; i < t->nfields; i++)
      if (t->fields[i].offset <= off && (!best || t->fields[i].offset > best->offset))
        best = &t->fields[i];
    if (best)
      at = (KeptAt){t->name, best->name};
  }
  kept_visit(w, *slot, at);
}

void tvm_profile_take_kept(TvmVm *vm, const char *entry, TvmProfileKeptFn fn, void *user) {
  TvmDebug *d = vm->debug;
  if (!d || vm->heap)
    return;
  KeptWalk w = {0};
  w.vm = vm;
  const char *roots[256];
  uint32_t nroots = root_classes(vm, entry, roots, 256);
  for (uint32_t r = 0; r < nroots; r++)
    for (uint32_t i = 0; i < vm->nstatic_fields; i++) {
      const TvmStaticField *f = &vm->static_fields[i];
      if (!static_of(f, roots[r]))
        continue;
      uint32_t leaves = kind_leaves(vm, f->kind, f->type);
      for (uint32_t k = 0; k < leaves; k++)
        if (vm->static_info[f->slot + k].kind == TVM_K_REF)
          kept_visit(&w, vm->statics[f->slot + k].p, (KeptAt){f->name, NULL});
    }
  for (w.cur = 0; w.cur < w.n && !w.failed; w.cur++) {
    TvmGcHeader *h = GC_HEADER(w.objs[w.cur]);
    /* blob (List の要素、Dictionary の entry) は持ち主が中を辿る */
    if (!GC_BLOB(h) && h->type)
      tvm_object_refs(vm, w.objs[w.cur], kept_slot, &w);
  }
  for (uint32_t i = 0; i < w.n; i++)
    GC_HEADER(w.objs[i])->flags &= (uint16_t)~TVM_GC_SEEN;
  for (uint32_t i = 0; i < w.nrows; i++) {
    const KeptRow *r = &w.rows[i];
    char holder[256];
    if (r->at.field)
      snprintf(holder, sizeof holder, "%s.%s", r->at.name, r->at.field);
    else
      snprintf(holder, sizeof holder, "%s", r->at.name);
    fn(user, d->line_files[r->line], d->line_nums[r->line], holder, r->count, r->bytes);
  }
  free(w.objs);
  free(w.ats);
  free(w.fresh);
  free(w.rows);
}
