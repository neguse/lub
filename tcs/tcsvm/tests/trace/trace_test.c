/* Test that takes out the --trace line records as (line, self) pairs.
   Pins Game.A (tvm_inspect_pin) and checks that lines passed only with another
   object of the same type as A (Game.B) as self are excluded by
   tvm_trace_take_lines_for. Lines with a self of another type and lines of
   static methods remain. Also checks, from inside a host function with
   tvm_stack_self, that the self of each frame is Game.B ("" for static frames).
 */
#include "../../vm/tcsvm.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int lines[256];
static int nlines;

static void take(void *user, const char *file, uint32_t line) {
  (void)user;
  (void)file;
  if (nlines < 256)
    lines[nlines++] = (int)line;
}

static int cmp(const void *a, const void *b) {
  return *(const int *)a - *(const int *)b;
}

static void print(const char *what) {
  qsort(lines, (size_t)nlines, sizeof(int), cmp);
  printf("%s:", what);
  for (int i = 0; i < nlines; i++)
    printf(" %d", lines[i]);
  printf("\n");
  nlines = 0;
}

static char b_id[32];

/* Prints the call sequence as B if self is Game.B, otherwise as - */
static void frame(void *user, const char *file, uint32_t line, const char *id) {
  (void)user;
  (void)file;
  printf(" %u:%s", (unsigned)line,
         id[0] == 0              ? "-"
         : strcmp(id, b_id) == 0 ? "B"
                                 : "?");
}

static TvmStatus here(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  printf("stack:");
  tvm_stack_self(vm, frame, NULL);
  printf("\n");
  return TVM_OK;
}

static int step(TvmVm *vm, int32_t k) {
  TvmSlot arg;
  arg.i = k;
  if (tvm_call(vm, tvm_find(vm, "Game.Step"), &arg, 1, NULL, 0) != TVM_OK) {
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
  if (!tvm_host(vm, "probe.stack.here", "", "", here, NULL))
    return 1;
  if (tvm_load(vm, bytes, (size_t)size) != TVM_OK) {
    fprintf(stderr, "%s\n", tvm_error(vm));
    return 1;
  }
  tvm_trace_take_lines(vm, take, NULL);
  nlines = 0;
  tvm_inspect_pin(vm, "a", "Game", "Game.A");
  /* A.Hit and B.Heal: Heal is excluded because only B passed through it */
  if (!step(vm, 0))
    return 1;
  tvm_trace_take_lines_for(vm, "a", take, NULL);
  print("pinned A, A.Hit B.Heal");
  /* B.Hit: Hit is excluded because only B passed through it */
  if (!step(vm, 1))
    return 1;
  tvm_trace_take_lines_for(vm, "a", take, NULL);
  print("pinned A, B.Hit");
  /* If nothing is pinned, all lines */
  if (!step(vm, 1))
    return 1;
  tvm_trace_take_lines_for(vm, "none", take, NULL);
  print("no pin, B.Hit");
  TvmInspectNode b;
  if (tvm_inspect(vm, "Game", "Game", 1, 1, &b) != 1 ||
      strcmp(b.name, "B") != 0)
    return 1;
  snprintf(b_id, sizeof b_id, "%s", b.id);
  if (tvm_call(vm, tvm_find(vm, "Game.Look"), NULL, 0, NULL, 0) != TVM_OK)
    return 1;
  tvm_free(vm);
  free(bytes);
  return 0;
}
