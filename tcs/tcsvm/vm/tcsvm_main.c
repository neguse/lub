/* tcsvm: .tcsb を読み込み、entry (または --entry の関数) を実行する CLI。 */
#include "tcsvm.h"

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
  *size = n > 0 ? (size_t)n : 0;
  return buf;
}

int main(int argc, char **argv) {
  const char *path = NULL, *entry = NULL;
  for (int i = 1; i < argc; i++) {
    if (strcmp(argv[i], "--entry") == 0 && i + 1 < argc)
      entry = argv[++i];
    else
      path = argv[i];
  }
  if (!path) {
    fprintf(stderr, "usage: tcsvm [--entry Class.Method] program.tcsb\n");
    return 2;
  }
  size_t size;
  uint8_t *bytes = read_file(path, &size);
  if (!bytes) {
    fprintf(stderr, "tcsvm: cannot read %s\n", path);
    return 2;
  }
  TvmVm *vm = tvm_new(NULL);
  if (!vm) {
    fprintf(stderr, "tcsvm: out of memory\n");
    return 2;
  }
  int code = 0;
  TvmStatus st = tvm_load(vm, bytes, size);
  if (st == TVM_OK) {
    int32_t f = entry ? tvm_find(vm, entry) : tvm_entry(vm);
    if (f < 0) {
      fprintf(stderr, "tcsvm: no entry %s\n", entry ? entry : "(module has none)");
      code = 2;
    } else {
      st = tvm_call(vm, f, NULL, 0, NULL, 0);
    }
  }
  fflush(stdout);
  if (st != TVM_OK) {
    fprintf(stderr, "tcsvm: %s\n", tvm_error(vm));
    code = 1;
  }
  tvm_free(vm);
  free(bytes);
  return code;
}
