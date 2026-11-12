/* tcsvm: .tcsb を読み込み、実行する CLI。
     tcsvm [--gc-stress] program.tcsb               entry (Main) を走らせる
     tcsvm [--gc-stress] --entry C.M program.tcsb   引数の無い static method を走らせる
     tcsvm [--gc-stress] --lib C N program.tcsb     C.Setup → (C.Frame → フレーム境界) × N → C.Report
     --reload K NEW.tcsb PLAN                       (--lib と) K 回目のフレームの後に新版を当てる (繰り返せる)
     --reload-batch K BATCH                         同じく、tcs2vm --watch の vm_batch を戻した byte 列を当てる
     --snapshot K / --restore K                     (--lib と) K 回目のフレームの後 (reload の後) に状態を控える / 控えを書き戻す
     --save-state K FILE                            (--lib と) K 回目のフレームの後に状態を FILE に書く
     --load-state K FILE                            (--lib と) K 回目のフレームの後に FILE の状態を読み込む。0 なら Setup の代わりに読み込む
   --lib は host の形 (tcs2c の lib 形と同じ順)。フレーム境界で GC が回る。
   reload が restart を求めたら終了コード 3 (理由は stderr)。 */
#include "tcsvm.h"

#include <stdbool.h>
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

static TvmStatus save_state(TvmVm *vm, const char *path) {
  uint8_t *state;
  size_t size;
  TvmStatus st = tvm_state_save(vm, &state, &size);
  if (st != TVM_OK)
    return st;
  FILE *fp = fopen(path, "wb");
  bool ok = fp && fwrite(state, 1, size, fp) == size;
  if (fp)
    ok = fclose(fp) == 0 && ok;
  tvm_state_free(state);
  if (!ok) {
    fprintf(stderr, "tcsvm: cannot write %s\n", path);
    return TVM_LOAD_ERROR;
  }
  return TVM_OK;
}

static TvmStatus load_state(TvmVm *vm, const char *path) {
  size_t size;
  uint8_t *state = read_file(path, &size);
  if (!state) {
    fprintf(stderr, "tcsvm: cannot read %s\n", path);
    return TVM_LOAD_ERROR;
  }
  TvmStatus st = tvm_state_load(vm, state, size);
  free(state);
  return st;
}

static TvmStatus call_named(TvmVm *vm, const char *cls, const char *method,
                            int required) {
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
    const char *module, *plan; /* plan が NULL なら module は batch */
  } reloads[MAX_RELOADS];
  int nreloads = 0;
  long snapshot_at = 0, restore_at = 0;
  long save_at = -1, load_at = -1;
  const char *save_path = NULL, *load_path = NULL;
  TvmConfig config = {0, 0, 0};
  for (int i = 1; i < argc; i++) {
    if (strcmp(argv[i], "--entry") == 0 && i + 1 < argc)
      entry = argv[++i];
    else if (strcmp(argv[i], "--lib") == 0 && i + 2 < argc) {
      lib = argv[++i];
      frames = strtol(argv[++i], NULL, 10);
    } else if (strcmp(argv[i], "--reload") == 0 && i + 3 < argc &&
               nreloads < MAX_RELOADS) {
      reloads[nreloads].at = strtol(argv[++i], NULL, 10);
      reloads[nreloads].module = argv[++i];
      reloads[nreloads].plan = argv[++i];
      nreloads++;
    } else if (strcmp(argv[i], "--reload-batch") == 0 && i + 2 < argc &&
               nreloads < MAX_RELOADS) {
      reloads[nreloads].at = strtol(argv[++i], NULL, 10);
      reloads[nreloads].module = argv[++i];
      reloads[nreloads].plan = NULL;
      nreloads++;
    } else if (strcmp(argv[i], "--snapshot") == 0 && i + 1 < argc)
      snapshot_at = strtol(argv[++i], NULL, 10);
    else if (strcmp(argv[i], "--restore") == 0 && i + 1 < argc)
      restore_at = strtol(argv[++i], NULL, 10);
    else if (strcmp(argv[i], "--save-state") == 0 && i + 2 < argc) {
      save_at = strtol(argv[++i], NULL, 10);
      save_path = argv[++i];
    } else if (strcmp(argv[i], "--load-state") == 0 && i + 2 < argc) {
      load_at = strtol(argv[++i], NULL, 10);
      load_path = argv[++i];
    } else if (strcmp(argv[i], "--gc-stress") == 0)
      config.gc_stress = 1;
    else
      path = argv[i];
  }
  if (!path) {
    fprintf(stderr, "usage: tcsvm [--gc-stress] [--entry C.M | --lib C FRAMES "
                    "[--reload K NEW.tcsb PLAN]...] "
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
  TvmSnapshot *snap = NULL;
  TvmStatus st = tvm_load(vm, bytes, size);
  if (st == TVM_OK && lib) {
    st = load_at == 0 ? load_state(vm, load_path)
                      : call_named(vm, lib, "Setup", 0);
    if (st == TVM_OK && save_at == 0)
      st = save_state(vm, save_path);
    for (long k = 0; k < frames && st == TVM_OK; k++) {
      st = call_named(vm, lib, "Frame", 1);
      tvm_frame_end(vm);
      for (int r = 0; r < nreloads && st == TVM_OK; r++) {
        if (reloads[r].at != k + 1)
          continue;
        size_t msize, psize = 0;
        uint8_t *m = read_file(reloads[r].module, &msize);
        uint8_t *p =
            reloads[r].plan ? read_file(reloads[r].plan, &psize) : NULL;
        if (!m || (reloads[r].plan && !p)) {
          fprintf(stderr, "tcsvm: cannot read %s\n", reloads[r].module);
          st = TVM_LOAD_ERROR;
        } else {
          st = p ? tvm_apply(vm, m, msize, p, psize)
                 : tvm_apply_batch(vm, m, msize);
          if (st == TVM_OK && tvm_error(vm)[0])
            fprintf(stderr, "tcsvm: reload warning: %s\n", tvm_error(vm));
        }
        free(m);
        free(p);
      }
      if (st == TVM_OK && load_at == k + 1)
        st = load_state(vm, load_path);
      if (st == TVM_OK && save_at == k + 1)
        st = save_state(vm, save_path);
      if (st == TVM_OK && restore_at == k + 1)
        tvm_snapshot_restore(vm, snap);
      if (st == TVM_OK && snapshot_at == k + 1) {
        tvm_snapshot_free(vm, snap);
        snap = tvm_snapshot_take(vm, lib);
      }
    }
    if (st == TVM_OK)
      st = call_named(vm, lib, "Report", 0);
  } else if (st == TVM_OK) {
    int32_t f = entry ? tvm_find(vm, entry) : tvm_entry(vm);
    if (f < 0) {
      fprintf(stderr, "tcsvm: no entry %s\n",
              entry ? entry : "(module has none)");
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
  tvm_snapshot_free(vm, snap);
  tvm_free(vm);
  free(bytes);
  return code;
}
