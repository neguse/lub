// tcs VM の接続コード (lub-gen vm) の試験の入口 (scripts/verify-vm-binding.sh)。
// tests/c/tcs_binding.c の偽の C API に、tcs vm が作った tests/c/tcs_binding.cs の .tcsb を host の関数で繋ぎ、argv[2] の関数を呼ぶ。
#include <stdio.h>
#include <stdlib.h>
int main(int argc, char **argv) {
  FILE *f = fopen(argv[1], "rb");
  if (!f)
    return 2;
  fseek(f, 0, SEEK_END);
  long n = ftell(f);
  fseek(f, 0, SEEK_SET);
  uint8_t *bytes = malloc((size_t)n);
  if (fread(bytes, 1, (size_t)n, f) != (size_t)n)
    return 2;
  fclose(f);
  TvmVm *vm = tvm_new(NULL);
  vm_lub_vm = vm;
  if (!vm_lub_register(vm) || tvm_load(vm, bytes, (size_t)n) != TVM_OK) {
    fprintf(stderr, "load: %s\n", tvm_error(vm));
    return 1;
  }
  char error[256];
  if (!vm_lub_verify(vm, error, sizeof error)) {
    fprintf(stderr, "layout: %s\n", error);
    return 1;
  }
  int32_t fn = tvm_find(vm, argv[2]);
  if (fn < 0 || tvm_call(vm, fn, NULL, 0, NULL, 0) != TVM_OK) {
    fprintf(stderr, "%s\n", tvm_error(vm));
    return 1;
  }
  return 0;
}
