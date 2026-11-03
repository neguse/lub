#include <stdio.h>
#include <stdlib.h>

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 200;
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
  printf("inside: %d of %d\n", inside, n * n);
  return 0;
}
