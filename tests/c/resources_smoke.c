// resource table (src/resources.c) の handle の表: 生きている entry だけを持ち、
// sweep で縮む。handle は entry の寿命の間は変わらず、sweep 後は stale になり、
// int32 の上限を越えたら生きている値を飛ばして 1 に戻る。
#include "backend.h"
#include "resources.h"
#include <stdint.h>
#include <stdio.h>

const RenderBackend *g_backend = NULL; // resources.c が参照する。entry は GPU 資源を持たない

static int failures;

#define CHECK(cond)                                                            \
  do {                                                                         \
    if (!(cond)) {                                                             \
      fprintf(stderr, "RESOURCES: %s:%d: %s\n", __FILE__, __LINE__, #cond);    \
      failures++;                                                              \
    }                                                                          \
  } while (0)

#define KEEP 3
#define ROUNDS 200
#define PER_ROUND 1000
#define MAX_CAP 256 // 生きている数が少ないとき、表がここを越えて残らない

static ResEntry *declare(ResTable *t, const char *fmt, int a, int b,
                         int64_t frame) {
  char key[64];
  snprintf(key, sizeof key, fmt, a, b);
  ResEntry *e = res_table_get_or_create(t, key, RES_BUFFER);
  CHECK(e != NULL);
  if (e)
    res_table_touch(e, frame);
  return e;
}

// 毎 round 新しい key を大量に作って捨てても、表は伸び続けず、生きている
// key の handle は変わらない。
static void test_churn(void) {
  ResTable t;
  res_table_init(&t);
  ResEntry *keep[KEEP];
  int32_t keep_handle[KEEP];
  int32_t first_round[PER_ROUND];
  for (int round = 0; round < ROUNDS; ++round) {
    int64_t frame = 2 * round;
    for (int k = 0; k < KEEP; ++k) {
      keep[k] = declare(&t, "keep_%d%d", k, 0, frame);
      if (round == 0)
        keep_handle[k] = keep[k]->handle;
    }
    for (int i = 0; i < PER_ROUND; ++i) {
      ResEntry *e = declare(&t, "churn_%d_%d", round, i, frame);
      if (round == 0)
        first_round[i] = e->handle;
    }
    for (int k = 0; k < KEEP; ++k)
      res_table_touch(keep[k], frame + 2);
    res_table_sweep(&t, frame + 2, 1, NULL, NULL);

    CHECK(t.handle_count == KEEP);
    CHECK(t.handle_cap <= MAX_CAP);
    for (int k = 0; k < KEEP; ++k) {
      CHECK(res_table_get(&t, keep[k]->key) == keep[k]);
      CHECK(keep[k]->handle == keep_handle[k]);
      CHECK(res_table_get_by_handle(&t, keep_handle[k]) == keep[k]);
    }
  }
  // 最初の round の handle は stale のまま (別の entry に使い回されない)
  for (int i = 0; i < PER_ROUND; ++i)
    CHECK(res_table_get_by_handle(&t, first_round[i]) == NULL);
  CHECK(res_table_get_by_handle(&t, 0) == NULL);
  CHECK(res_table_get_by_handle(&t, -1) == NULL);
  res_table_shutdown(&t);
}

// 一部だけが生き残る sweep の後も、生きている handle は全て引け、消えた
// handle は全て引けない。
static void test_partial_sweep(void) {
  enum { N = 5000 };
  ResTable t;
  res_table_init(&t);
  ResEntry *entries[N];
  int32_t handles[N];
  for (int i = 0; i < N; ++i) {
    entries[i] = declare(&t, "partial_%d%d", i, 0, 0);
    handles[i] = entries[i]->handle;
  }
  for (int i = 0; i < N; i += 3)
    res_table_touch(entries[i], 10);
  res_table_sweep(&t, 10, 5, NULL, NULL);
  int live = 0;
  for (int i = 0; i < N; ++i) {
    if (i % 3 == 0) {
      ++live;
      CHECK(res_table_get_by_handle(&t, handles[i]) == entries[i]);
    } else {
      CHECK(res_table_get_by_handle(&t, handles[i]) == NULL);
    }
  }
  CHECK(t.handle_count == live);
  CHECK(t.handle_cap >= live * 2);
  // 残りを全部消すと表は元の大きさまで縮む
  res_table_sweep(&t, 100, 5, NULL, NULL);
  CHECK(t.handle_count == 0);
  CHECK(t.handle_cap <= MAX_CAP);
  // 空になった後も宣言できる
  ResEntry *e = declare(&t, "after_%d%d", 0, 0, 100);
  CHECK(res_table_get_by_handle(&t, e->handle) == e);
  res_table_shutdown(&t);
}

// handle が int32 の上限に達したら 1 に戻り、生きている値を飛ばす。
static void test_wrap(void) {
  ResTable t;
  res_table_init(&t);
  ResEntry *a = declare(&t, "wrap_%d%d", 0, 0, 0);
  ResEntry *b = declare(&t, "wrap_%d%d", 1, 0, 0);
  CHECK(a->handle == 1 && b->handle == 2);
  t.next_handle = INT32_MAX - 1;
  ResEntry *c = declare(&t, "wrap_%d%d", 2, 0, 0);
  ResEntry *d = declare(&t, "wrap_%d%d", 3, 0, 0);
  ResEntry *e = declare(&t, "wrap_%d%d", 4, 0, 0);
  CHECK(c->handle == INT32_MAX);
  CHECK(d->handle == 3); // 1 と 2 は生きているので飛ばす
  CHECK(e->handle == 4);
  ResEntry *all[] = {a, b, c, d, e};
  for (int i = 0; i < 5; ++i)
    CHECK(res_table_get_by_handle(&t, all[i]->handle) == all[i]);
  res_table_shutdown(&t);
}

int main(void) {
  test_churn();
  test_partial_sweep();
  test_wrap();
  if (failures) {
    fprintf(stderr, "RESOURCES: %d failure(s)\n", failures);
    return 1;
  }
  puts("resources smoke OK");
  return 0;
}
