/* module 単位の混在 (doc/object-model.md §10) の試験: editor (Editor.cs) は tcs2c --lib --vm-host の生成 C を
   取り込み (EDITOR_C)、content (Content.cs) は tcs VM で動かして reload する。
   1. tcs_vm_register で editor の公開関数を VM の host 関数にする
   2. content の Setup が返した object を host が handle で持つ
   3. frame を回し (content が editor を呼び、editor が content の object の field を書く)、content を v2 に reload する
   4. reload で作り直された object を handle から読む (C の module が持つ参照も張り替わる)
   usage: modules_test CONTENT_V1.tcsb V2.batch [--gc-stress] */
#include EDITOR_C

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

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

static int call(TvmVm *vm, const char *name, TvmSlot *ret) {
  TvmSlot dummy;
  int32_t f = tvm_find(vm, name);
  if (f < 0 || tvm_call(vm, f, NULL, 0, ret ? ret : &dummy, ret ? 1 : 0) != TVM_OK) {
    fprintf(stderr, "modules_test: %s: %s\n", name, tvm_error(vm));
    return 0;
  }
  return 1;
}

static void show(TvmVm *vm, TvmHandle h, const char *when) {
  char *e = (char *)tvm_handle_get(vm, h);
  int32_t hp = tvm_field_offset(vm, "Enemy", "Hp"), mana = tvm_field_offset(vm, "Enemy", "Mana");
  if (mana < 0)
    printf("host %s: hp=%d (no Mana)\n", when, *(int32_t *)(e + hp));
  else
    printf("host %s: hp=%d mana=%d\n", when, *(int32_t *)(e + hp), *(int32_t *)(e + mana));
}

int main(int argc, char **argv) {
  if (argc < 3)
    return 2;
  TvmConfig config = {0, 0, argc > 3 && strcmp(argv[3], "--gc-stress") == 0};
  tcs_lib_init();
  TvmVm *vm = tvm_new(&config);
  printf("registered %d editor functions\n", tcs_vm_register(vm));
  size_t size, bsize;
  uint8_t *bytes = read_file(argv[1], &size), *batch = read_file(argv[2], &bsize);
  if (!bytes || !batch || tvm_load(vm, bytes, size) != TVM_OK) {
    fprintf(stderr, "modules_test: %s\n", tvm_error(vm));
    return 1;
  }
  TvmSlot enemy;
  if (!call(vm, "Game.Setup", &enemy))
    return 1;
  TvmHandle h = tvm_hold(vm, enemy.p);
  for (int k = 0; k < 2; k++) {
    if (!call(vm, "Game.Frame", NULL))
      return 1;
    tvm_frame_end(vm);
    tcs_lib_gc();
  }
  show(vm, h, "before reload");
  if (tvm_apply_batch(vm, batch, bsize) != TVM_OK) {
    fprintf(stderr, "modules_test: reload: %s\n", tvm_error(vm));
    return 1;
  }
  show(vm, h, "after reload");
  for (int k = 0; k < 2; k++) {
    if (!call(vm, "Game.Frame", NULL))
      return 1;
    tvm_frame_end(vm);
    tcs_lib_gc();
  }
  show(vm, h, "end");
  if (!call(vm, "Game.Report", NULL))
    return 1;
  tvm_release(vm, h);
  tvm_free(vm);
  free(bytes);
  free(batch);
  return 0;
}
