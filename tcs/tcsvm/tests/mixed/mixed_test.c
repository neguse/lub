/* mixed-mode の最小の試験: tcs2c --lib の生成 C (MIXED_C) を取り込み、VM の object に生成 C の関数を呼ぶ。 */
#include MIXED_C
#include "../../vm/tcsvm.h"

#include <stdio.h>
#include <stdlib.h>

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

int main(int argc, char **argv) {
  if (argc < 2)
    return 2;
  TvmVm *vm = tvm_new(NULL);
  size_t size;
  uint8_t *bytes = read_file(argv[1], &size);
  if (!bytes || tvm_load(vm, bytes, size) != TVM_OK || tvm_call(vm, tvm_find(vm, "Game.Setup"), NULL, 0, NULL, 0)) {
    fprintf(stderr, "mixed_test: %s\n", tvm_error(vm));
    return 1;
  }
  TvmSlot obj;
  if (tvm_call(vm, tvm_find(vm, "Game.Get"), NULL, 0, &obj, 1) != TVM_OK)
    return 1;
  /* VM の object を tcs2c の struct として読む */
  Tcs_Pt *p = (Tcs_Pt *)obj.p;
  printf("tcs2c reads: X=%d Y=%g On=%d Z=%d next=%s sum=%d\n", p->f_X, p->f_Y, p->f_On, p->f_Z,
         p->f_Next == p ? "self" : "other", tcs_m_Game_Sum(p));
  /* tcs2c の関数が書き、VM の関数が読む */
  tcs_m_Game_Bump(p);
  TvmSlot sum, y;
  if (tvm_call(vm, tvm_find(vm, "Game.Sum"), &obj, 1, &sum, 1) != TVM_OK ||
      tvm_call(vm, tvm_find(vm, "Game.Y"), &obj, 1, &y, 1) != TVM_OK)
    return 1;
  printf("vm reads after tcs2c Bump: sum=%d Y=%g\n", sum.i, y.f);
  tvm_free(vm);
  free(bytes);
  return 0;
}
