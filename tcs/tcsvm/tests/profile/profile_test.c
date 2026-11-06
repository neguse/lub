/* 行ごとの負荷 (tvm_profile_*) のテスト。時間は、Game の Probe.Clock.Advance が tvm_profile_tick で知らせる分だけ経つ。
   - 時間は知らせたときの行に付き、--trace の印のある関数の中の時間はその関数の行に、印の無い関数 (Lib.cs) の中の時間は呼んだ行に付く
   - tvm_call の外で知らせた時間はどの行にも付かない
   - 確保はした行に付き、GC の止まりは別に数える (test の時計は読むたびに 1000 ns 進み、そのたびに 500 ns を知らせる。
     GC の長さは時計で測った 1000 ns で、GC の間に知らされた時間はどの行にも付かない)
   - 持ち越し: 前の frame の境目の後に --trace の行で作り、entry と --trace の class の static から辿れる物を、作った行と持つ field ごとに数える
   - 別の thread から知らせた時間も欠けずに付く
   - 止めると数えない */
#include "../../vm/tcsvm.h"

#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint64_t now;
static TvmVm *the_vm;

static uint64_t fake_clock(void) {
  now += 1000;
  tvm_profile_tick(the_vm, 500);
  return now;
}

static TvmStatus advance(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)r;
  (void)user;
  tvm_profile_tick(vm, (uint64_t)a[0].i);
  return TVM_OK;
}

/* 別の thread: Spin の中に入ったら THREAD_TICKS 回 1 ns ずつ知らせ、終えたら done にする */
#define THREAD_TICKS 200000
static atomic_int started, done;

static TvmStatus done_fn(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)vm;
  (void)a;
  (void)user;
  atomic_store(&started, 1);
  r[0].i = atomic_load(&done);
  return TVM_OK;
}

static void *ticker(void *arg) {
  TvmVm *vm = (TvmVm *)arg;
  while (!atomic_load(&started)) {
  }
  for (int i = 0; i < THREAD_TICKS; i++)
    tvm_profile_tick(vm, 1);
  atomic_store(&done, 1);
  return NULL;
}

static uint64_t thread_ns;

static void sum_ns(void *user, const char *file, uint32_t line, uint64_t ns, uint64_t bytes, uint32_t allocs) {
  (void)user;
  (void)file;
  (void)line;
  (void)bytes;
  (void)allocs;
  thread_ns += ns;
}

typedef struct Kept {
  uint32_t line;
  char holder[64];
  uint32_t count;
  uint64_t bytes;
} Kept;

static Kept kepts[16];
static int nkepts;

static void take_kept(void *user, const char *file, uint32_t line, const char *holder, uint32_t count, uint64_t bytes) {
  (void)user;
  const char *slash = strrchr(file, '/');
  if (strcmp(slash ? slash + 1 : file, "Game.cs") != 0 || nkepts == 16)
    return;
  kepts[nkepts].line = line;
  snprintf(kepts[nkepts].holder, sizeof kepts[nkepts].holder, "%s", holder ? holder : "(host)");
  kepts[nkepts].count = count;
  kepts[nkepts].bytes = bytes;
  nkepts++;
}

static int cmp_kept(const void *a, const void *b) {
  const Kept *x = (const Kept *)a, *y = (const Kept *)b;
  return x->line != y->line ? (int)x->line - (int)y->line : strcmp(x->holder, y->holder);
}

static void print_kept(const char *what) {
  qsort(kepts, (size_t)nkepts, sizeof(Kept), cmp_kept);
  printf("%s:\n", what);
  for (int i = 0; i < nkepts; i++)
    printf("  Game.cs:%u %s count=%u bytes%s\n", kepts[i].line, kepts[i].holder, kepts[i].count,
           kepts[i].bytes >= 32u * kepts[i].count && kepts[i].count > 0 ? "=ok" : "=bad");
  nkepts = 0;
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

static int gcs, gcs_timed;
static size_t gc_live;

static void take_gc(void *user, uint64_t ns, size_t live) {
  (void)user;
  gcs++;
  gcs_timed += ns == 1000;
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
  the_vm = vm;
  if (!tvm_host(vm, "probe.clock.advance", "i", "", advance, NULL) ||
      !tvm_host(vm, "probe.clock.done", "", "b", done_fn, NULL))
    return 1;
  if (tvm_load(vm, bytes, (size_t)size) != TVM_OK) {
    fprintf(stderr, "%s\n", tvm_error(vm));
    return 1;
  }
  /* 数える前に一度呼び、static を作っておく */
  if (!call(vm, "Game.Keep"))
    return 1;
  tvm_profile_enable(vm, fake_clock);
  if (!call(vm, "Game.Step") || !call(vm, "Game.Step"))
    return 1;
  /* 呼び出しの外で知らせた時間は付かない */
  tvm_profile_tick(vm, 1000);
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
  printf("gc: %s\n", gcs > 0 && gc_live > 0 ? (gcs_timed == gcs ? "stopped 1000 ns each" : "stopped, wrong length") : "none");
  /* 持ち越し: frame の境目の後に作った物だけを数え、一度数えた物は数えない */
  tvm_frame_end(vm);
  if (!call(vm, "Game.Keep"))
    return 1;
  tvm_profile_take_kept(vm, "Game", take_kept, NULL);
  print_kept("kept");
  tvm_profile_take_kept(vm, "Game", take_kept, NULL);
  print_kept("kept taken again");
  tvm_frame_end(vm);
  if (!call(vm, "Game.Keep"))
    return 1;
  tvm_frame_end(vm);
  tvm_profile_take_kept(vm, "Game", take_kept, NULL);
  print_kept("kept after frame end");
  /* 別の thread から知らせた時間 */
  tvm_profile_take_lines(vm, sum_ns, NULL);
  thread_ns = 0;
  pthread_t th;
  if (pthread_create(&th, NULL, ticker, vm) != 0)
    return 1;
  if (!call(vm, "Game.Spin"))
    return 1;
  pthread_join(th, NULL);
  tvm_profile_take_lines(vm, sum_ns, NULL);
  printf("thread: %s\n", thread_ns == THREAD_TICKS ? "all ticks" : "lost ticks");
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
