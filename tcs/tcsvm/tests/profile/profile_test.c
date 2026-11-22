/* Test of per-line cost (tvm_profile_*). Time advances only by the amount
   Game's Probe.Clock.Advance reports with tvm_profile_tick.
   - Time is attributed to the line where it is reported; time inside a function
   marked --trace goes to that function's line, and time inside an unmarked
   function (Lib.cs) goes to the calling line
   - Time reported outside tvm_call is attributed to no line
   - Allocations are attributed to the line that made them, and GC pauses are
   counted separately (the test clock advances 1000 ns on each read and reports
   500 ns each time. The GC duration is the 1000 ns measured by the clock, and
   time reported during GC is attributed to no line)
   - Kept: objects created on a --trace line after the previous frame boundary
   and reachable from the entry and the statics of --trace classes are counted
   per creating line and holding field
   - Time reported from another thread is also attributed without loss
   - Time and allocations after returning from a called --trace function (calls
   inside the VM and nested calls from the host) are attributed to the calling
   line
   - Looking up the current line inside a host function gives the line that
   called it (none when not counting)
   - Nothing is counted when stopped */
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

/* Another thread: once inside Spin, reports 1 ns THREAD_TICKS times, then sets
 * done */
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

static TvmStatus around_fn(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  TvmSlot ret[1];
  TvmStatus st = tvm_call(vm, tvm_find(vm, "Game.Mark"), NULL, 0, ret, 1);
  tvm_profile_tick(vm, 5);
  return st;
}

static char where[256];

static TvmStatus where_fn(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  const char *file;
  uint32_t line;
  if (!tvm_profile_site(vm, &file, &line)) {
    snprintf(where, sizeof where, "none");
    return TVM_OK;
  }
  const char *slash = strrchr(file, '/');
  snprintf(where, sizeof where, "%s:%u", slash ? slash + 1 : file, line);
  return TVM_OK;
}

static uint64_t thread_ns;

static void sum_ns(void *user, const char *file, uint32_t line, uint64_t ns,
                   uint64_t bytes, uint32_t allocs) {
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

static void take_kept(void *user, const char *file, uint32_t line,
                      const char *holder, uint32_t count, uint64_t bytes) {
  (void)user;
  const char *slash = strrchr(file, '/');
  if (strcmp(slash ? slash + 1 : file, "Game.cs") != 0 || nkepts == 16)
    return;
  kepts[nkepts].line = line;
  snprintf(kepts[nkepts].holder, sizeof kepts[nkepts].holder, "%s",
           holder ? holder : "(host)");
  kepts[nkepts].count = count;
  kepts[nkepts].bytes = bytes;
  nkepts++;
}

static int cmp_kept(const void *a, const void *b) {
  const Kept *x = (const Kept *)a, *y = (const Kept *)b;
  return x->line != y->line ? (int)x->line - (int)y->line
                            : strcmp(x->holder, y->holder);
}

static void print_kept(const char *what) {
  qsort(kepts, (size_t)nkepts, sizeof(Kept), cmp_kept);
  printf("%s:\n", what);
  for (int i = 0; i < nkepts; i++)
    printf("  Game.cs:%u %s count=%u bytes%s\n", kepts[i].line, kepts[i].holder,
           kepts[i].count,
           kepts[i].bytes >= 32u * kepts[i].count && kepts[i].count > 0
               ? "=ok"
               : "=bad");
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

static void take(void *user, const char *file, uint32_t line, uint64_t ns,
                 uint64_t bytes, uint32_t allocs) {
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

static int cmp(const void *a, const void *b) {
  return (int)((const Row *)a)->line - (int)((const Row *)b)->line;
}

static void print(const char *what) {
  qsort(rows, (size_t)nrows, sizeof(Row), cmp);
  printf("%s:\n", what);
  for (int i = 0; i < nrows; i++)
    printf("  Game.cs:%u ns=%llu allocs=%u bytes%s\n", rows[i].line,
           (unsigned long long)rows[i].ns, rows[i].allocs,
           rows[i].bytes >= 400u * rows[i].allocs &&
                   (rows[i].allocs > 0) == (rows[i].bytes > 0)
               ? "=ok"
               : "=bad");
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
      !tvm_host(vm, "probe.clock.done", "", "b", done_fn, NULL) ||
      !tvm_host(vm, "probe.clock.around", "", "", around_fn, NULL) ||
      !tvm_host(vm, "probe.clock.where", "", "", where_fn, NULL))
    return 1;
  if (tvm_load(vm, bytes, (size_t)size) != TVM_OK) {
    fprintf(stderr, "%s\n", tvm_error(vm));
    return 1;
  }
  /* Calls once before counting to create the statics */
  if (!call(vm, "Game.Keep"))
    return 1;
  tvm_profile_enable(vm, fake_clock);
  if (!call(vm, "Game.Step") || !call(vm, "Game.Step"))
    return 1;
  /* Time reported outside a call is not attributed */
  tvm_profile_tick(vm, 1000);
  tvm_profile_take_lines(vm, take, NULL);
  print("two steps");
  tvm_profile_take_lines(vm, take, NULL);
  print("taken again");
  /* Allocating more than 1 MB makes GC pause inside the call */
  if (!call(vm, "Game.Churn"))
    return 1;
  tvm_profile_take_lines(vm, take, NULL);
  print("churn");
  tvm_profile_take_gc(vm, take_gc, NULL);
  printf("gc: %s\n", gcs > 0 && gc_live > 0
                         ? (gcs_timed == gcs ? "stopped 1000 ns each"
                                             : "stopped, wrong length")
                         : "none");
  /* Kept: counts only objects created after the frame boundary, and never
   * counts an object twice */
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
  /* Time reported from another thread */
  tvm_profile_take_lines(vm, sum_ns, NULL);
  thread_ns = 0;
  pthread_t th;
  if (pthread_create(&th, NULL, ticker, vm) != 0)
    return 1;
  if (!call(vm, "Game.Spin"))
    return 1;
  pthread_join(th, NULL);
  tvm_profile_take_lines(vm, sum_ns, NULL);
  printf("thread: %s\n",
         thread_ns == THREAD_TICKS ? "all ticks" : "lost ticks");
  /* After returning from the callee */
  if (!call(vm, "Game.Nest"))
    return 1;
  tvm_profile_take_lines(vm, take, NULL);
  print("nested");
  if (!call(vm, "Game.Around"))
    return 1;
  tvm_profile_take_lines(vm, take, NULL);
  print("around");
  if (!call(vm, "Game.Where"))
    return 1;
  printf("where: %s\n", where);
  /* Nothing is counted when stopped */
  tvm_profile_enable(vm, NULL);
  if (!call(vm, "Game.Step"))
    return 1;
  tvm_profile_take_lines(vm, take, NULL);
  print("disabled");
  if (!call(vm, "Game.Where"))
    return 1;
  printf("where disabled: %s\n", where);
  tvm_free(vm);
  free(bytes);
  return 0;
}
