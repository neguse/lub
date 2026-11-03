#include <stdio.h>
#include <stdlib.h>

static int run(int n, int print) {
  int *perm = calloc(n, sizeof(int));
  int *perm1 = calloc(n, sizeof(int));
  int *count = calloc(n, sizeof(int));
  for (int i = 0; i < n; i++)
    perm1[i] = i;

  int maxFlips = 0, checksum = 0, permCount = 0, r = n;
  for (;;) {
    while (r != 1) {
      count[r - 1] = r;
      r--;
    }
    for (int i = 0; i < n; i++)
      perm[i] = perm1[i];
    int flips = 0;
    int k = perm[0];
    while (k != 0) {
      int lo = 0, hi = k;
      while (lo < hi) {
        int t = perm[lo];
        perm[lo] = perm[hi];
        perm[hi] = t;
        lo++;
        hi--;
      }
      flips++;
      k = perm[0];
    }
    if (flips > maxFlips)
      maxFlips = flips;
    if (permCount % 2 == 0)
      checksum += flips;
    else
      checksum -= flips;

    for (;;) {
      if (r == n) {
        if (print)
          printf("%d\nPfannkuchen(%d) = %d\n", checksum, n, maxFlips);
        free(perm);
        free(perm1);
        free(count);
        return checksum;
      }
      int perm0 = perm1[0];
      for (int i = 0; i < r; i++)
        perm1[i] = perm1[i + 1];
      perm1[r] = perm0;
      count[r]--;
      if (count[r] > 0)
        break;
      r++;
    }
    permCount++;
  }
}

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 7;
  // BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
  const char *rs = getenv("BENCH_REPS");
  int reps = rs ? atoi(rs) : 1;
  int prev = 0;
  for (int r = 0; r < reps; r++)
    prev = run(prev == -1 ? n + 1 : n, r == reps - 1);
  return 0;
}
