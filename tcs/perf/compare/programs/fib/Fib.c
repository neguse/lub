#include <stdio.h>
#include <stdlib.h>

static int calc(int n) {
  if (n < 2)
    return n;
  return calc(n - 1) + calc(n - 2);
}

static int run(int n, int print) {
  int f = calc(n);
  if (print)
    printf("fib(%d) = %d\n", n, f);
  return f;
}

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 30;
  // BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
  const char *rs = getenv("BENCH_REPS");
  int reps = rs ? atoi(rs) : 1;
  int prev = 0;
  for (int r = 0; r < reps; r++)
    prev = run(prev == -1 ? n + 1 : n, r == reps - 1);
  return 0;
}
