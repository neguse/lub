#include <stdio.h>
#include <stdlib.h>

static int run(int n, int print) {
  int inside = 0;
  for (int y = 0; y < n; y++) {
    float ci = 2.0f * (float)y / (float)n - 1.0f;
    for (int x = 0; x < n; x++) {
      float cr = 2.0f * (float)x / (float)n - 1.5f;
      float zr = 0.0f, zi = 0.0f, tr = 0.0f, ti = 0.0f;
      int i = 0;
      while (i < 50 && tr + ti <= 4.0f) {
        zi = 2.0f * zr * zi + ci;
        zr = tr - ti + cr;
        tr = zr * zr;
        ti = zi * zi;
        i++;
      }
      if (tr + ti <= 4.0f)
        inside++;
    }
  }
  if (print)
    printf("inside: %d of %d\n", inside, n * n);
  return inside;
}

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 200;
  // BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
  const char *rs = getenv("BENCH_REPS");
  int reps = rs ? atoi(rs) : 1;
  int prev = 0;
  for (int r = 0; r < reps; r++)
    prev = run(prev == -1 ? n + 1 : n, r == reps - 1);
  return 0;
}
