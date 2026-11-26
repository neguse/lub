/* Test that views part of a large array (Game.Boxes, 2^20 elements) with
   tvm_inspect. Checks that the child count, a range near the end, and fields
   inside elements are correct, and that no effort is spent on elements outside
   the requested range (repeated calls stay fast). Also checks that
   tvm_inspect_instances passes the class instances reached breadth-first from
   statics once each, in the order found, with the same path and id as
   tvm_inspect, does not revisit cycles, also follows into structs, and stops at
   the depth and child-count limits. Also checks that instance ids stay the same
   after saving and loading the state. */
#include "../../../src/tcs-vm/tcsvm.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define N (1 << 20)

static double now_ms(void) {
  struct timespec ts;
  timespec_get(&ts, TIME_UTC);
  return ts.tv_sec * 1e3 + ts.tv_nsec / 1e6;
}

static char solo_id[64];

static void instance(void *user, const char *type, const char *path,
                     const char *id) {
  (void)user;
  printf("  %s %s\n", type, path);
  if (strcmp(path, "Game.Solo") == 0)
    snprintf(solo_id, sizeof solo_id, "%s", id);
}

static void print(const TvmInspectNode *n) {
  printf("%s %s %s count=%d\n", n->name, n->type ? n->type : "-", n->text,
         (int)n->count);
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
  if (tvm_load(vm, bytes, (size_t)size) != TVM_OK) {
    fprintf(stderr, "%s\n", tvm_error(vm));
    return 1;
  }
  char last[32];
  snprintf(last, sizeof last, "Game.Boxes[%d]", N - 1);
  TvmInspectNode boxes, tail[4], hp[4];
  double t0 = now_ms();
  for (int k = 0; k < 16; k++) {
    /* The child count is the number of elements */
    if (tvm_inspect(vm, "Game", "Game", 0, 1, &boxes) != 1)
      return 1;
    /* A range near the end gives only what remains */
    if (tvm_inspect(vm, "Game", "Game.Boxes", N - 2, 4, tail) != 2)
      return 1;
    /* Inside an element */
    if (tvm_inspect(vm, "Game", last, 0, 4, hp) != 1)
      return 1;
  }
  double ms = now_ms() - t0;
  print(&boxes);
  print(&tail[0]);
  print(&tail[1]);
  print(&hp[0]);
  /* Creating elements outside the range too takes tens of ms per call. Creating
   * only what was requested takes less than 1 ms in total */
  if (ms < 500)
    printf("fast\n");
  else
    printf("slow: %.0f ms\n", ms);
  /* Depth 3 (static fields are 1), children up to the first 1000 (the Box at
   * the end of Boxes is not followed) */
  printf("instances:\n");
  tvm_inspect_instances(vm, "Game", 3, 1000, instance, NULL);
  TvmInspectNode game[8];
  int32_t n = tvm_inspect(vm, "Game", "Game", 0, 8, game);
  for (int32_t i = 0; i < n; i++)
    if (strcmp(game[i].path, "Game.Solo") == 0)
      printf("same id: %s\n",
             strcmp(game[i].id, solo_id) == 0 && solo_id[0] ? "yes" : "no");
  /* Saving and loading the state recreates the objects, but the ids of the same
   * instances do not change */
  uint8_t *state = NULL;
  size_t state_size = 0;
  if (tvm_state_save(vm, &state, &state_size) != TVM_OK ||
      tvm_state_load(vm, state, state_size) != TVM_OK) {
    fprintf(stderr, "%s\n", tvm_error(vm));
    return 1;
  }
  tvm_state_free(state);
  n = tvm_inspect(vm, "Game", "Game", 0, 8, game);
  for (int32_t i = 0; i < n; i++)
    if (strcmp(game[i].path, "Game.Solo") == 0)
      printf("same id after load: %s\n",
             strcmp(game[i].id, solo_id) == 0 ? "yes" : "no");
  tvm_free(vm);
  free(bytes);
  return 0;
}
