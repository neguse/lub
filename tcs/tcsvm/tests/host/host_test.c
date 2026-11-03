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
      !tvm_host(vm, "eng.gfx.main_tex", "", "i", main_tex, NULL))
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
