/* tcs_sort.h の実装。手順は tcs2c の生成 C (tcs2c/CRuntime.Lib.cs の tcs_sort_*) と同じ。 */
#include "tcs_sort.h"

#include <string.h>

typedef struct Sort {
  TcsSortCompare compare;
  void *ctx;
  size_t es;
  unsigned char *held; /* pivot、挿入中の要素、DownHeap の要素 */
  unsigned char *tmp;  /* swap 用 */
  int stop;
  int inconsistent;
} Sort;

/* 止まった後は比較を呼ばず 0 を返す。0 だけを返す比較ではどの loop も有限回で終わる */
static int cmp(Sort *s, const void *a, const void *b) {
  if (s->stop || s->inconsistent)
    return 0;
  return s->compare(s->ctx, a, b, &s->stop);
}

static void swap(Sort *s, unsigned char *keys, ptrdiff_t i, ptrdiff_t j) {
  size_t es = s->es;
  memcpy(s->tmp, keys + i * es, es);
  memcpy(keys + i * es, keys + j * es, es);
  memcpy(keys + j * es, s->tmp, es);
}

static void swap_if_greater(Sort *s, unsigned char *keys, ptrdiff_t i,
                            ptrdiff_t j) {
  if (cmp(s, keys + i * s->es, keys + j * s->es) > 0)
    swap(s, keys, i, j);
}

static void insertion(Sort *s, unsigned char *keys, ptrdiff_t n) {
  size_t es = s->es;
  for (ptrdiff_t i = 0; i < n - 1; i++) {
    ptrdiff_t j = i;
    memcpy(s->held, keys + (i + 1) * es, es);
    while (j >= 0 && cmp(s, s->held, keys + j * es) < 0) {
      memcpy(keys + (j + 1) * es, keys + j * es, es);
      j--;
    }
    memcpy(keys + (j + 1) * es, s->held, es);
  }
}

static void down_heap(Sort *s, unsigned char *keys, ptrdiff_t i, ptrdiff_t n) {
  size_t es = s->es;
  memcpy(s->held, keys + (i - 1) * es, es);
  while (i <= n >> 1) {
    ptrdiff_t child = 2 * i;
    if (child < n && cmp(s, keys + (child - 1) * es, keys + child * es) < 0)
      child++;
    if (!(cmp(s, s->held, keys + (child - 1) * es) < 0))
      break;
    memcpy(keys + (i - 1) * es, keys + (child - 1) * es, es);
    i = child;
  }
  memcpy(keys + (i - 1) * es, s->held, es);
}

static void heap(Sort *s, unsigned char *keys, ptrdiff_t n) {
  for (ptrdiff_t i = n >> 1; i >= 1; i--)
    down_heap(s, keys, i, n);
  for (ptrdiff_t i = n; i > 1; i--) {
    swap(s, keys, 0, i - 1);
    down_heap(s, keys, 1, i - 1);
  }
}

static ptrdiff_t partition(Sort *s, unsigned char *keys, ptrdiff_t n) {
  size_t es = s->es;
  ptrdiff_t hi = n - 1;
  ptrdiff_t middle = hi >> 1;
  ptrdiff_t left = 0;
  ptrdiff_t right = hi - 1;
  swap_if_greater(s, keys, 0, middle);
  swap_if_greater(s, keys, 0, hi);
  swap_if_greater(s, keys, middle, hi);
  memcpy(s->held, keys + middle * es, es);
  swap(s, keys, middle, hi - 1);
  while (left < right) {
    do {
      if (++left > hi) {
        s->inconsistent = 1;
        return 0;
      }
    } while (cmp(s, keys + left * es, s->held) < 0);
    do {
      if (--right < 0) {
        s->inconsistent = 1;
        return 0;
      }
    } while (cmp(s, s->held, keys + right * es) < 0);
    if (left >= right)
      break;
    swap(s, keys, left, right);
  }
  if (left != hi - 1)
    swap(s, keys, left, hi - 1);
  return left;
}

static void intro(Sort *s, unsigned char *keys, ptrdiff_t n, int depth) {
  while (n > 1 && !s->stop && !s->inconsistent) {
    if (n <= 16) {
      if (n == 2) {
        swap_if_greater(s, keys, 0, 1);
      } else if (n == 3) {
        swap_if_greater(s, keys, 0, 1);
        swap_if_greater(s, keys, 0, 2);
        swap_if_greater(s, keys, 1, 2);
      } else {
        insertion(s, keys, n);
      }
      return;
    }
    if (depth == 0) {
      heap(s, keys, n);
      return;
    }
    depth--;
    ptrdiff_t p = partition(s, keys, n);
    if (s->inconsistent)
      return;
    intro(s, keys + (p + 1) * s->es, n - p - 1, depth);
    n = p;
  }
}

TcsSortStatus tcs_sort(void *keys, size_t n, size_t es, void *scratch,
                       TcsSortCompare compare, void *ctx) {
  Sort s;
  int log2 = 0;
  if (n < 2)
    return TCS_SORT_OK;
  s.compare = compare;
  s.ctx = ctx;
  s.es = es;
  s.held = (unsigned char *)scratch;
  s.tmp = (unsigned char *)scratch + es;
  s.stop = 0;
  s.inconsistent = 0;
  for (size_t m = n; m > 1; m >>= 1)
    log2++;
  intro(&s, (unsigned char *)keys, (ptrdiff_t)n, 2 * (log2 + 1));
  if (s.stop)
    return TCS_SORT_STOPPED;
  return s.inconsistent ? TCS_SORT_INCONSISTENT : TCS_SORT_OK;
}
