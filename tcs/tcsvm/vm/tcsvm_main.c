/* tcsvm: .tcsb を読み込み、実行する CLI。
     tcsvm [--gc-stress] program.tcsb               entry (Main) を走らせる
     tcsvm [--gc-stress] --entry C.M program.tcsb   引数の無い static method を走らせる
     tcsvm [--gc-stress] --lib C N program.tcsb     C.Setup → (C.Frame → フレーム境界) × N → C.Report
     --reload K NEW.tcsb PLAN                       (--lib と) K 回目のフレームの後に新版を当てる (繰り返せる)
   --lib は host の形 (tcs2c の lib 形と同じ順)。フレーム境界で GC が回る。
   reload が restart を求めたら終了コード 3 (理由は stderr)。 */
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

static TvmStatus call_named(TvmVm *vm, const char *cls, const char *method, int required) {
  char name[512];
  snprintf(name, sizeof name, "%s.%s", cls, method);
  int32_t f = tvm_find(vm, name);
  if (f < 0) {
    if (!required)
      return TVM_OK;
    fprintf(stderr, "tcsvm: no function %s\n", name);
    return TVM_LOAD_ERROR;
  }
  return tvm_call(vm, f, NULL, 0, NULL, 0);
}

#define MAX_RELOADS 64

int main(int argc, char **argv) {
  const char *path = NULL, *entry = NULL, *lib = NULL;
  long frames = 0;
  struct {
    long at;
    const char *module, *plan;
  } reloads[MAX_RELOADS];
  int nreloads = 0;
  TvmConfig config = {0, 0, 0};
  for (int i = 1; i < argc; i++) {
    if (strcmp(argv[i], "--entry") == 0 && i + 1 < argc)
      entry = argv[++i];
    else if (strcmp(argv[i], "--lib") == 0 && i + 2 < argc) {
      lib = argv[++i];
      frames = strtol(argv[++i], NULL, 10);
    } else if (strcmp(argv[i], "--reload") == 0 && i + 3 < argc && nreloads < MAX_RELOADS) {
      reloads[nreloads].at = strtol(argv[++i], NULL, 10);
      reloads[nreloads].module = argv[++i];
      reloads[nreloads].plan = argv[++i];
      nreloads++;
    } else if (strcmp(argv[i], "--gc-stress") == 0)
      config.gc_stress = 1;
    else
      path = argv[i];
  }
  if (!path) {
    fprintf(stderr, "usage: tcsvm [--gc-stress] [--entry C.M | --lib C FRAMES [--reload K NEW.tcsb PLAN]...] "
                    "program.tcsb\n");
    return 2;
  }
  size_t size;
  uint8_t *bytes = read_file(path, &size);
  if (!bytes) {
    fprintf(stderr, "tcsvm: cannot read %s\n", path);
    return 2;
  }
  TvmVm *vm = tvm_new(&config);
  if (!vm) {
    fprintf(stderr, "tcsvm: out of memory\n");
    return 2;
  }
  int code = 0;
  TvmStatus st = tvm_load(vm, bytes, size);
  if (st == TVM_OK && lib) {
    st = call_named(vm, lib, "Setup", 0);
    for (long k = 0; k < frames && st == TVM_OK; k++) {
      st = call_named(vm, lib, "Frame", 1);
      tvm_frame_end(vm);
      for (int r = 0; r < nreloads && st == TVM_OK; r++) {
        if (reloads[r].at != k + 1)
          continue;
        size_t msize, psize;
        uint8_t *m = read_file(reloads[r].module, &msize), *p = read_file(reloads[r].plan, &psize);
        if (!m || !p) {
          fprintf(stderr, "tcsvm: cannot read %s or %s\n", reloads[r].module, reloads[r].plan);
          st = TVM_LOAD_ERROR;
        } else {
          st = tvm_apply(vm, m, msize, p, psize);
          if (st == TVM_OK && tvm_error(vm)[0])
            fprintf(stderr, "tcsvm: reload warning: %s\n", tvm_error(vm));
        }
        free(m);
        free(p);
      }
    }
    if (st == TVM_OK)
      st = call_named(vm, lib, "Report", 0);
  } else if (st == TVM_OK) {
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
    code = st == TVM_RESTART ? 3 : 1;
  }
  tvm_free(vm);
  free(bytes);
  return code;
}
