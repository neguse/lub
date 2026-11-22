// The handle table of the resource table (src/resources.c): holds only live
// entries and shrinks on sweep. A handle stays the same for the lifetime of its
// entry, becomes stale after sweep, and wraps back to 1 past the int32 limit,
// skipping live values.
#include "backend.h"
#include "resources.h"
#include <stdint.h>
#include <stdio.h>

const RenderBackend *g_backend =
    NULL; // Referenced by resources.c. Entries hold no GPU resources

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
// When few entries are live, the table does not stay larger than this
#define MAX_CAP 256

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

// Even if many new keys are created and discarded every round, the table does
// not keep growing, and the handles of live keys do not change.
static void test_churn(void) {
  ResTable t;
  int32_t counter = 0;
  res_table_init(&t, &counter);
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
  // Handles from the first round stay stale (not reused for other entries)
  for (int i = 0; i < PER_ROUND; ++i)
    CHECK(res_table_get_by_handle(&t, first_round[i]) == NULL);
  CHECK(res_table_get_by_handle(&t, 0) == NULL);
  CHECK(res_table_get_by_handle(&t, -1) == NULL);
  res_table_shutdown(&t);
}

// After a sweep in which only some survive, every live handle can be looked up
// and no removed handle can.
static void test_partial_sweep(void) {
  enum { N = 5000 };
  ResTable t;
  int32_t counter = 0;
  res_table_init(&t, &counter);
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
  // Removing all the rest shrinks the table back to its original size
  res_table_sweep(&t, 100, 5, NULL, NULL);
  CHECK(t.handle_count == 0);
  CHECK(t.handle_cap <= MAX_CAP);
  // Declarations still work after it becomes empty
  ResEntry *e = declare(&t, "after_%d%d", 0, 0, 100);
  CHECK(res_table_get_by_handle(&t, e->handle) == e);
  res_table_shutdown(&t);
}

// When a handle reaches the int32 limit it wraps back to 1, skipping live
// values.
static void test_wrap(void) {
  ResTable t;
  int32_t counter = 0;
  res_table_init(&t, &counter);
  ResEntry *a = declare(&t, "wrap_%d%d", 0, 0, 0);
  ResEntry *b = declare(&t, "wrap_%d%d", 1, 0, 0);
  CHECK(a->handle == 1 && b->handle == 2);
  counter = INT32_MAX - 1;
  ResEntry *c = declare(&t, "wrap_%d%d", 2, 0, 0);
  ResEntry *d = declare(&t, "wrap_%d%d", 3, 0, 0);
  ResEntry *e = declare(&t, "wrap_%d%d", 4, 0, 0);
  CHECK(c->handle == INT32_MAX);
  CHECK(d->handle == 3); // 1 and 2 are live, so they are skipped
  CHECK(e->handle == 4);
  ResEntry *all[] = {a, b, c, d, e};
  for (int i = 0; i < 5; ++i)
    CHECK(res_table_get_by_handle(&t, all[i]->handle) == all[i]);
  res_table_shutdown(&t);
}

// Handles of 2 tables using the same counter do not overlap, and a handle of
// one cannot be looked up in the other.
static void test_shared_counter(void) {
  ResTable a, b;
  int32_t counter = 0;
  res_table_init(&a, &counter);
  res_table_init(&b, &counter);
  ResEntry *x = declare(&a, "shared_%d%d", 0, 0, 0);
  ResEntry *y = declare(&b, "shared_%d%d", 0, 0, 0);
  ResEntry *z = declare(&a, "shared_%d%d", 1, 0, 0);
  CHECK(x->handle == 1 && y->handle == 2 && z->handle == 3);
  CHECK(res_table_get_by_handle(&b, x->handle) == NULL);
  CHECK(res_table_get_by_handle(&a, y->handle) == NULL);
  CHECK(res_table_get_by_handle(&a, z->handle) == z);
  res_table_shutdown(&a);
  res_table_shutdown(&b);
}

int main(void) {
  test_churn();
  test_partial_sweep();
  test_wrap();
  test_shared_counter();
  if (failures) {
    fprintf(stderr, "RESOURCES: %d failure(s)\n", failures);
    return 1;
  }
  puts("resources smoke OK");
  return 0;
}
