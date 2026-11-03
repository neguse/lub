/* host 連携のテスト: stub.cs (namespace Eng) の面を host の関数として登録し、Game の OnInit / OnEvent / OnFrame を
   host から呼ぶ。host が VM の object の field を名前で引いて読み書きすることも確かめる。 */
#include "../../vm/tcsvm.h"

#include <math.h>
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

static TvmStatus load(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)user;
  size_t n;
  const char *path = tvm_string_data(a[0].p, &n);
  char buf[64];
  int len = snprintf(buf, sizeof buf, "<%.*s>", (int)n, path ? path : "");
  r[0].p = tvm_string_new(vm, buf, (size_t)len);
  r[1].i = 9;
  return r[0].p ? TVM_OK : tvm_host_fault(vm, "out of memory");
}

static TvmStatus try_(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)user;
  r[0].i = a[0].i > 5;
  r[1].f = (float)a[0].i * 0.5f;
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

int main(int argc, char **argv) {
  if (argc < 2)
    return 2;
  TvmConfig config = {0, 0, argc > 2 && strcmp(argv[2], "--gc-stress") == 0};
  TvmVm *vm = tvm_new(&config);
  static int bias = 0;
  if (!tvm_host(vm, "eng.gfx.begin", "o", "", begin, NULL) || !tvm_host(vm, "eng.gfx.add", "ii", "i", add, &bias) ||
      !tvm_host(vm, "eng.gfx.len", "ff", "f", len, NULL) || !tvm_host(vm, "eng.gfx.name", "", "s", name, NULL) ||
      !tvm_host(vm, "eng.gfx.main_tex", "", "i", main_tex, NULL) ||
      !tvm_host(vm, "eng.gfx.load", "s", "si", load, NULL) || !tvm_host(vm, "eng.gfx.try", "i", "bf", try_, NULL) ||
      !tvm_host(vm, "eng.gfx.make", "i", "o", make, NULL) || !tvm_host(vm, "eng.gfx.handle", "o", "i", handle, NULL) ||
      !tvm_host(vm, "eng.gfx.range", "i", "o", range, NULL) || !tvm_host(vm, "eng.gfx.sum", "o", "i", sum, NULL) ||
      !tvm_host(vm, "eng.gfx.many", "i", "o", many, NULL) || !tvm_host(vm, "eng.gfx.squares", "i", "o", squares, NULL) ||
      !tvm_host(vm, "eng.gfx.ring", "f", "o", ring, NULL) || !tvm_host(vm, "eng.circle.area", "o", "f", area, NULL))
    return 1;
  size_t size;
  uint8_t *bytes = read_file(argv[1], &size);
  int ok = bytes && check(vm, tvm_load(vm, bytes, size));
  ok = ok && check(vm, tvm_call(vm, tvm_find(vm, "Game.OnInit"), NULL, 0, NULL, 0));
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
  return ok ? 0 : 1;
}
