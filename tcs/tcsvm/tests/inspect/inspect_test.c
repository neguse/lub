/* 大きな配列 (Game.Boxes、2^20 要素) の一部を tvm_inspect で見るテスト。
   子の数、終わり近くの範囲、要素の中の field が正しいことと、頼んだ範囲の外の要素に手間をかけないこと (繰り返しても速いこと) を確かめる。
   tvm_inspect_instances が static から幅優先で辿った class の個体を、見つけた順に 1 度ずつ、tvm_inspect と同じ path と id で渡すこと、
   輪を辿り直さず、struct の中も辿り、深さと子の数の上限で止まることも確かめる。 */
#include "../../vm/tcsvm.h"

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

static void instance(void *user, const char *type, const char *path, const char *id) {
  (void)user;
  printf("  %s %s\n", type, path);
  if (strcmp(path, "Game.Solo") == 0)
    snprintf(solo_id, sizeof solo_id, "%s", id);
}

static void print(const TvmInspectNode *n) {
  printf("%s %s %s count=%d\n", n->name, n->type ? n->type : "-", n->text, (int)n->count);
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
    /* 子の数は要素の数 */
    if (tvm_inspect(vm, "Game", "Game", 0, 1, &boxes) != 1)
      return 1;
    /* 終わり近くの範囲は、残っている分だけ */
    if (tvm_inspect(vm, "Game", "Game.Boxes", N - 2, 4, tail) != 2)
      return 1;
    /* 要素の中 */
    if (tvm_inspect(vm, "Game", last, 0, 4, hp) != 1)
      return 1;
  }
  double ms = now_ms() - t0;
  print(&boxes);
  print(&tail[0]);
  print(&tail[1]);
  print(&hp[0]);
  /* 範囲の外の要素まで作ると 1 回で数十 ms かかる。頼んだ分だけなら全部で 1 ms もかからない */
  if (ms < 500)
    printf("fast\n");
  else
    printf("slow: %.0f ms\n", ms);
  /* 深さ 3 (static field が 1)、子は先頭の 1000 個まで (Boxes の終わりの Box は辿らない) */
  printf("instances:\n");
  tvm_inspect_instances(vm, "Game", 3, 1000, instance, NULL);
  TvmInspectNode game[8];
  int32_t n = tvm_inspect(vm, "Game", "Game", 0, 8, game);
  for (int32_t i = 0; i < n; i++)
    if (strcmp(game[i].path, "Game.Solo") == 0)
      printf("same id: %s\n", strcmp(game[i].id, solo_id) == 0 && solo_id[0] ? "yes" : "no");
  tvm_free(vm);
  free(bytes);
  return 0;
}
