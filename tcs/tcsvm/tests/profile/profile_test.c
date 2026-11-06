/* 行ごとの負荷 (tvm_profile_*) のテスト。時計は test が持ち、Game の Probe.Clock.Advance でだけ進む。
   - 時間はその行に付き、--trace の印のある関数の中の時間はその関数の行に、印の無い関数 (Lib.cs) の中の時間は呼んだ行に付く
   - tvm_call の外で進んだ時間はどの行にも付かない
   - 確保はした行に付き、GC の止まりは別に数える
   - 止めると数えない */
#include "../../vm/tcsvm.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint64_t now;

static uint64_t fake_clock(void) { return now; }

static TvmStatus advance(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)r;
  (void)user;
  now += (uint64_t)a[0].i;
  return TVM_OK;
}

typedef struct Row {
  uint32_t line;
  uint64_t ns;
  uint64_t bytes;
  uint32_t allocs;
} Row;

static Row rows[64];
static int nrows;

static void take(void *user, const char *file, uint32_t line, uint64_t ns, uint64_t bytes, uint32_t allocs) {
  (void)user;
  const char *slash = strrchr(file, '/');
  if (strcmp(slash ? slash + 1 : file, "Game.cs") != 0 || nrows == 64)
    return;
  rows[nrows].line = line;
  rows[nrows].ns = ns;
  rows[nrows].bytes = bytes;
  rows[nrows].allocs = allocs;
  nrows++;
}

static int cmp(const void *a, const void *b) { return (int)((const Row *)a)->line - (int)((const Row *)b)->line; }

static void print(const char *what) {
  qsort(rows, (size_t)nrows, sizeof(Row), cmp);
  printf("%s:\n", what);
  for (int i = 0; i < nrows; i++)
    printf("  Game.cs:%u ns=%llu allocs=%u bytes%s\n", rows[i].line, (unsigned long long)rows[i].ns, rows[i].allocs,
           rows[i].bytes >= 400u * rows[i].allocs && (rows[i].allocs > 0) == (rows[i].bytes > 0) ? "=ok" : "=bad");
  nrows = 0;
}

static int gcs;
static size_t gc_live;

static void take_gc(void *user, uint64_t ns, size_t live) {
  (void)user;
  (void)ns;
  gcs++;
  gc_live = live;
}

static int call(TvmVm *vm, const char *name) {
  if (tvm_call(vm, tvm_find(vm, name), NULL, 0, NULL, 0) != TVM_OK) {
    fprintf(stderr, "%s\n", tvm_error(vm));
    return 0;
  }
  return 1;
}

int main(int argc, char **argv) {
  if (argc < 2)
    return 2;
  FILE *f = fopen(argv[1], "rb");
  if (!f)
    return 2;
  fseek(f, 0, SEEK_END);
  long size = ftell(f);
  fseek(f, 0, SEEK_SET);
  uint8_t *bytes = (uint8_t *)malloc((size_t)size);
  if (!bytes || fread(bytes, 1, (size_t)size, f) != (size_t)size)
    return 2;
  fclose(f);
  TvmConfig config = {0};
  TvmVm *vm = tvm_new(&config);
  if (!tvm_host(vm, "probe.clock.advance", "i", "", advance, NULL))
    return 1;
  if (tvm_load(vm, bytes, (size_t)size) != TVM_OK) {
    fprintf(stderr, "%s\n", tvm_error(vm));
    return 1;
  }
  tvm_profile_enable(vm, fake_clock);
  if (!call(vm, "Game.Step") || !call(vm, "Game.Step"))
    return 1;
  /* 呼び出しの外で進んだ時間は付かない */
  now += 1000;
  tvm_profile_take_lines(vm, take, NULL);
  print("two steps");
  tvm_profile_take_lines(vm, take, NULL);
  print("taken again");
  /* 1 MB を超えて確保すると、呼び出しの中で GC が止める */
  if (!call(vm, "Game.Churn"))
    return 1;
  tvm_profile_take_lines(vm, take, NULL);
  print("churn");
  tvm_profile_take_gc(vm, take_gc, NULL);
  printf("gc: %s\n", gcs > 0 && gc_live > 0 ? "stopped" : "none");
  /* 止めると数えない */
  tvm_profile_enable(vm, NULL);
  if (!call(vm, "Game.Step"))
    return 1;
  tvm_profile_take_lines(vm, take, NULL);
  print("disabled");
  tvm_free(vm);
  free(bytes);
  return 0;
}
