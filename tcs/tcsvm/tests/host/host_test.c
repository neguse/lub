/* host 連携のテスト: stub.cs (namespace Eng) の面を host の関数として登録し、Game の OnInit / Structs / OnEvent / OnFrame を host から呼ぶ。
   host が VM の object の field を名前で引いて読み書きすることも確かめる。
   struct と out は C の layout の塊と pointer で受ける (C の API を包む host と同じ形)。
   2 つ目の module (Reject.cs) では、登録が module の struct と合わないときに読み込みが拒否されることを確かめる。 */
#include "../../vm/tcsvm.h"

#include <math.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static TvmStatus begin(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)r;
  (void)user;
  void *opts = a[0].p;
  if (!opts || strcmp(tvm_object_type(opts), "Eng_Opts") != 0)
    return tvm_host_fault(vm, "begin: Opts expected");
  int32_t w = tvm_field_offset(vm, "Eng_Opts", "w"), scale = tvm_field_offset(vm, "Eng_Opts", "scale"),
          title = tvm_field_offset(vm, "Eng_Opts", "title");
  size_t n;
  const char *t = tvm_string_data(*(void **)((char *)opts + title), &n);
  printf("begin w=%d scale=%g title=%.*s\n", *(int32_t *)((char *)opts + w), *(float *)((char *)opts + scale), (int)n,
         t ? t : "");
  return TVM_OK;
}

static TvmStatus make(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)user;
  void *t = tvm_object_new_of(vm, tvm_type(vm, "Eng_Tex"));
  int32_t version = tvm_type_field_offset(tvm_type(vm, "Eng_Tex"), "version");
  if (!t || version < 16) /* host_value (8 byte) の後 */
    return tvm_host_fault(vm, "make: Tex layout");
  TVM_HOST_VALUE(t) = (uint64_t)a[0].i;
  *(int32_t *)((char *)t + version) = 7;
  r[0].p = t;
  return TVM_OK;
}

static TvmStatus handle(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  r[0].i = a[0].p ? (int32_t)TVM_HOST_VALUE(a[0].p) : -1;
  return TVM_OK;
}

static TvmStatus range(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)user;
  void *l = tvm_list_new_of(vm, tvm_type(vm, "List<int>"), a[0].i);
  if (!l)
    return tvm_host_fault(vm, "range: no List<int>");
  int32_t *d = (int32_t *)tvm_list_data(l);
  for (int32_t i = 0; i < a[0].i; i++)
    d[i] = i;
  r[0].p = l;
  return TVM_OK;
}

static TvmStatus sum(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  int32_t s = 0, n = tvm_list_count(a[0].p);
  const int32_t *d = (const int32_t *)tvm_list_data(a[0].p);
  for (int32_t i = 0; i < n; i++)
    s += d[i];
  r[0].i = s;
  return TVM_OK;
}

static TvmStatus many(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)user;
  const TvmTypeInfo *opts = tvm_type(vm, "Eng_Opts");
  int32_t w = tvm_type_field_offset(opts, "w"), title = tvm_type_field_offset(opts, "title");
  void *l = tvm_list_new_of(vm, tvm_type(vm, "List<Eng_Opts>"), 0);
  if (!l || !tvm_list_resize(vm, l, a[0].i))
    return tvm_host_fault(vm, "many: no List<Eng_Opts>");
  for (int32_t i = 0; i < a[0].i; i++) {
    void *o = tvm_object_new_of(vm, opts);
    *(int32_t *)((char *)o + w) = i * 10;
    *(void **)((char *)o + title) = tvm_string_new(vm, "x", 1);
    ((void **)tvm_list_data(l))[i] = o;
  }
  r[0].p = l;
  return TVM_OK;
}

static TvmStatus squares(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)user;
  void *arr = tvm_array_new_of(vm, tvm_type(vm, "int[]"), a[0].i);
  if (!arr || tvm_array_len(arr) != a[0].i)
    return tvm_host_fault(vm, "squares: no int[]");
  for (int32_t i = 0; i < a[0].i; i++)
    ((int32_t *)tvm_array_data(arr))[i] = i * i;
  r[0].p = arr;
  return TVM_OK;
}

static TvmStatus ring(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)user;
  const TvmTypeInfo *t = tvm_type(vm, "Eng_Circle");
  int32_t kind = tvm_type_field_offset(t, "kind"), radius = tvm_type_field_offset(t, "r");
  void *c = tvm_object_new_of(vm, t);
  if (!c || kind != 16 || radius != 20) /* host_value の後に field */
    return tvm_host_fault(vm, "ring: Circle layout");
  *(int32_t *)((char *)c + kind) = 3;
  *(float *)((char *)c + radius) = a[0].f;
  r[0].p = c;
  return TVM_OK;
}

static TvmStatus area(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)user;
  float radius = *(float *)((char *)a[0].p + tvm_field_offset(vm, "Eng_Circle", "r"));
  r[0].f = 3.0f * radius * radius;
  return TVM_OK;
}

static TvmStatus add(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  r[0].i = a[0].i + a[1].i + *(int *)user;
  return TVM_OK;
}

static TvmStatus len(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  r[0].f = sqrtf(a[0].f * a[0].f + a[1].f * a[1].f);
  return TVM_OK;
}

static TvmStatus name(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)a;
  (void)user;
  r[0].p = tvm_string_new(vm, "eng", 3);
  return r[0].p ? TVM_OK : tvm_host_fault(vm, "out of memory");
}

/* out は args の pointer の先 (string は参照の slot) に書く */
static TvmStatus load(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)r;
  (void)user;
  size_t n;
  const char *path = tvm_string_data(a[0].p, &n);
  char buf[64];
  int len = snprintf(buf, sizeof buf, "<%.*s>", (int)n, path ? path : "");
  if (*(void **)a[1].p || *(int32_t *)a[2].p)
    return tvm_host_fault(vm, "load: out is not zeroed");
  *(void **)a[1].p = tvm_string_new(vm, buf, (size_t)len);
  *(int32_t *)a[2].p = 9;
  return *(void **)a[1].p ? TVM_OK : tvm_host_fault(vm, "out of memory");
}

static TvmStatus try_(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  r[0].i = a[0].i > 5;
  *(float *)a[1].p = (float)a[0].i * 0.5f;
  return TVM_OK;
}

/* ---- struct: host は C の struct (doc/object-model.md §5 の layout) で読み書きする ---- */

typedef struct Vec2 {
  float x, y;
} Vec2;
typedef struct Box2 {
  Vec2 min, max;
  bool solid;
  int32_t layer; /* enum は int */
} Box2;
typedef struct Hit {
  int32_t id;
  float t;
} Hit;
typedef struct OptHit { /* Hit? */
  bool has;
  Hit v;
} OptHit;

/* host が包む C の API (struct を値で受け、out は pointer) */
static float phys_dot(Vec2 a, Vec2 b) { return a.x * b.x + a.y * b.y; }
static Vec2 phys_center(Box2 b) { return (Vec2){(b.min.x + b.max.x) / 2, (b.min.y + b.max.y) / 2}; }
static void phys_size(Box2 b, Vec2 *size, bool *solid) {
  float sign = b.layer == 5 ? 1.0f : -1.0f;
  *size = (Vec2){(b.max.x - b.min.x) * sign, (b.max.y - b.min.y) * sign};
  *solid = b.solid;
}
static bool phys_pick(int32_t id, Hit *hit) {
  if (id <= 0)
    return false;
  *hit = (Hit){id * 2, 0.25f};
  return true;
}
static OptHit phys_cast(Vec2 from, float limit) {
  if (sqrtf(from.x * from.x + from.y * from.y) > limit)
    return (OptHit){0};
  return (OptHit){true, {(int32_t)from.x, from.y + 0.5f}};
}
static int32_t phys_score(OptHit h, int32_t bias) { return h.has ? h.v.id * 10 + bias : -bias; }

static TvmStatus dot(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  r[0].f = phys_dot(*(const Vec2 *)a[0].p, *(const Vec2 *)a[1].p);
  return TVM_OK;
}

static TvmStatus center(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  *(Vec2 *)r[0].p = phys_center(*(const Box2 *)a[0].p);
  return TVM_OK;
}

static TvmStatus size_(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)r;
  (void)user;
  const Vec2 *s = (const Vec2 *)a[1].p;
  if (s->x != 0 || s->y != 0 || *(const bool *)a[2].p)
    return tvm_host_fault(vm, "size: out is not zeroed");
  phys_size(*(const Box2 *)a[0].p, (Vec2 *)a[1].p, (bool *)a[2].p);
  return TVM_OK;
}

static TvmStatus pick(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  r[0].i = phys_pick(a[0].i, (Hit *)a[1].p);
  return TVM_OK;
}

static TvmStatus cast(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  *(OptHit *)r[0].p = phys_cast(*(const Vec2 *)a[0].p, a[1].f);
  return TVM_OK;
}

static TvmStatus score(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  r[0].i = phys_score(*(const OptHit *)a[0].p, a[1].i);
  return TVM_OK;
}

/* 入れ子: delegate を呼ぶと、その中で別の struct の host の関数 (center) が呼ばれる。自分の引数の塊はその後も読める */
static TvmStatus apply(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)user;
  const Vec2 *v = (const Vec2 *)a[1].p;
  TvmSlot args[2] = {{.f = v->x}, {.f = v->y}}, rets[2];
  TvmStatus st = tvm_invoke(vm, a[0].p, args, 2, rets, 2);
  if (st != TVM_OK)
    return st;
  *(Vec2 *)r[0].p = (Vec2){rets[0].f + v->x, rets[1].f + v->y};
  return TVM_OK;
}

static TvmStatus weight(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)a;
  (void)user;
  r[0].f = 0;
  return TVM_OK;
}

static TvmStatus main_tex(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)a;
  (void)user;
  r[0].i = 42;
  return TVM_OK;
}

static uint8_t *read_file(const char *path, size_t *size) {
  FILE *fp = fopen(path, "rb");
  if (!fp)
    return NULL;
  fseek(fp, 0, SEEK_END);
  long n = ftell(fp);
  fseek(fp, 0, SEEK_SET);
  uint8_t *buf = (uint8_t *)malloc(n > 0 ? (size_t)n : 1);
  if (buf && n > 0 && fread(buf, 1, (size_t)n, fp) != (size_t)n) {
    free(buf);
    buf = NULL;
  }
  fclose(fp);
  *size = (size_t)(n > 0 ? n : 0);
  return buf;
}

static int check(TvmVm *vm, TvmStatus st) {
  if (st != TVM_OK) {
    fprintf(stderr, "host_test: %s\n", tvm_error(vm));
    return 0;
  }
  return 1;
}

/* 読み込みの拒否: Reject.cs の module を hosts で登録した VM に読ませ、エラーに needle があるかを出す */
typedef struct RejectHost {
  const char *name, *params, *rets;
} RejectHost;

static void reject(const char *label, const uint8_t *bytes, size_t size, const RejectHost *hosts, size_t nhosts,
                   const char *needle) {
  TvmVm *vm = tvm_new(NULL);
  for (size_t k = 0; k < nhosts; k++)
    if (!tvm_host(vm, hosts[k].name, hosts[k].params, hosts[k].rets, weight, NULL)) {
      printf("reject %s: registration refused\n", label);
      tvm_free(vm);
      return;
    }
  TvmStatus st = tvm_load(vm, bytes, size);
  const char *err = tvm_error(vm);
  printf("reject %s: %s\n", label, st != TVM_LOAD_ERROR ? "loaded" : strstr(err, needle) ? needle : err);
  tvm_free(vm);
}

static void rejects(const uint8_t *bytes, size_t size) {
  /* 登録の書き方の誤りは tvm_host が断る */
  TvmVm *vm = tvm_new(NULL);
  static const char *const bad[][2] = {
      {"S{Eng_Vec2", "f"}, /* 閉じない */
      {"S{}", "f"},        /* 名前が無い */
      {"&&i", ""},         /* & を重ねる */
      {"i", "&f"},         /* 戻り値に & */
      {"i", "if"},         /* 戻り値は 1 つ (out は & の引数) */
      {"x", ""},           /* 知らない種類 */
  };
  for (size_t k = 0; k < sizeof bad / sizeof bad[0]; k++)
    printf("register %s / %s: %s\n", bad[k][0], bad[k][1],
           tvm_host(vm, "probe.bad", bad[k][0], bad[k][1], weight, NULL) ? "accepted" : "refused");
  tvm_free(vm);
  /* 他の場合の登録 (Named は塊にできないので、weight は葉の種類の列で受ける) */
  static const RejectHost dot = {"eng.phys.dot", "S{Eng_Vec2}S{Eng_Vec2}", "f"};
  static const RejectHost weight_leaves = {"eng.phys.weight", "sf", "f"};
  static const RejectHost pick = {"eng.phys.pick", "i&S{Eng_Hit}", "b"};
  /* module に無い struct の名前 */
  reject("unknown struct", bytes, size,
         (RejectHost[]){{"eng.phys.dot", "S{Eng_Vec3}S{Eng_Vec2}", "f"}, weight_leaves, pick}, 3,
         "eng.phys.dot: no struct type Eng_Vec3 in the module");
  /* struct でない型の名前 */
  reject("not a struct", bytes, size,
         (RejectHost[]){{"eng.phys.dot", "S{string}S{Eng_Vec2}", "f"}, weight_leaves, pick}, 3,
         "eng.phys.dot: string is not a struct");
  /* 参照の field を持つ struct は塊にできない */
  reject("reference field", bytes, size, (RejectHost[]){dot, {"eng.phys.weight", "S{Eng_Named}", "f"}, pick}, 3,
         "eng.phys.weight: struct Eng_Named has a reference field");
  /* 呼び出しの葉と struct の葉が合わない (Hit は int と float、Vec2 は float 2 つ) */
  reject("shape", bytes, size,
         (RejectHost[]){{"eng.phys.dot", "S{Eng_Hit}S{Eng_Vec2}", "f"}, weight_leaves, pick}, 3,
         "argument 0 of eng.phys.dot: the call does not pass the leaves of struct Eng_Hit");
  reject("out shape", bytes, size, (RejectHost[]){dot, weight_leaves, {"eng.phys.pick", "i&S{Eng_Vec2}", "b"}}, 3,
         "argument 1 of eng.phys.pick: the call does not pass the leaves of struct Eng_Vec2");
  /* 合う登録は読める */
  reject("matching", bytes, size, (RejectHost[]){dot, weight_leaves, pick}, 3, "");
}

int main(int argc, char **argv) {
  if (argc < 3)
    return 2;
  TvmConfig config = {0, 0, argc > 3 && strcmp(argv[3], "--gc-stress") == 0};
  TvmVm *vm = tvm_new(&config);
  static int bias = 0;
  if (!tvm_host(vm, "eng.gfx.begin", "o", "", begin, NULL) || !tvm_host(vm, "eng.gfx.add", "ii", "i", add, &bias) ||
      !tvm_host(vm, "eng.gfx.len", "ff", "f", len, NULL) || !tvm_host(vm, "eng.gfx.name", "", "s", name, NULL) ||
      !tvm_host(vm, "eng.gfx.main_tex", "", "i", main_tex, NULL) ||
      !tvm_host(vm, "eng.gfx.load", "s&s&i", "", load, NULL) || !tvm_host(vm, "eng.gfx.try", "i&f", "b", try_, NULL) ||
      !tvm_host(vm, "eng.gfx.make", "i", "o", make, NULL) || !tvm_host(vm, "eng.gfx.handle", "o", "i", handle, NULL) ||
      !tvm_host(vm, "eng.gfx.range", "i", "o", range, NULL) || !tvm_host(vm, "eng.gfx.sum", "o", "i", sum, NULL) ||
      !tvm_host(vm, "eng.gfx.many", "i", "o", many, NULL) || !tvm_host(vm, "eng.gfx.squares", "i", "o", squares, NULL) ||
      !tvm_host(vm, "eng.gfx.ring", "f", "o", ring, NULL) || !tvm_host(vm, "eng.circle.area", "o", "f", area, NULL) ||
      !tvm_host(vm, "eng.phys.dot", "S{Eng_Vec2}S{Eng_Vec2}", "f", dot, NULL) ||
      !tvm_host(vm, "eng.phys.center", "S{Eng_Box2}", "S{Eng_Vec2}", center, NULL) ||
      !tvm_host(vm, "eng.phys.size", "S{Eng_Box2}&S{Eng_Vec2}&b", "", size_, NULL) ||
      !tvm_host(vm, "eng.phys.pick", "i&S{Eng_Hit}", "b", pick, NULL) ||
      !tvm_host(vm, "eng.phys.cast", "S{Eng_Vec2}f", "S{Eng_Hit?}", cast, NULL) ||
      !tvm_host(vm, "eng.phys.score", "S{Eng_Hit?}i", "i", score, NULL) ||
      !tvm_host(vm, "eng.phys.apply", "oS{Eng_Vec2}", "S{Eng_Vec2}", apply, NULL))
    return 1;
  size_t size;
  uint8_t *bytes = read_file(argv[1], &size);
  int ok = bytes && check(vm, tvm_load(vm, bytes, size));
  ok = ok && check(vm, tvm_call(vm, tvm_find(vm, "Game.OnInit"), NULL, 0, NULL, 0));
  ok = ok && check(vm, tvm_call(vm, tvm_find(vm, "Game.Structs"), NULL, 0, NULL, 0));
  for (int k = 0; ok && k < 3; k++) {
    /* host が event の object を作って渡す */
    void *ev = tvm_object_new(vm, "Eng_Ev");
    int32_t kind = tvm_field_offset(vm, "Eng_Ev", "kind"), x = tvm_field_offset(vm, "Eng_Ev", "x");
    if (!ev || kind < 0 || x < 0)
      return 1;
    *(int32_t *)((char *)ev + kind) = k;
    *(float *)((char *)ev + x) = 0.5f * (float)k;
    TvmSlot arg = {.p = ev};
    ok = check(vm, tvm_call(vm, tvm_find(vm, "Game.OnEvent"), &arg, 1, NULL, 0));
    TvmSlot dt = {.f = 2.0f};
    ok = ok && check(vm, tvm_call(vm, tvm_find(vm, "Game.OnFrame"), &dt, 1, NULL, 0));
    tvm_frame_end(vm);
  }
  tvm_free(vm);
  free(bytes);
  size_t rsize;
  uint8_t *rbytes = ok ? read_file(argv[2], &rsize) : NULL;
  if (rbytes)
    rejects(rbytes, rsize);
  free(rbytes);
  return ok && rbytes ? 0 : 1;
}
