#include <stdio.h>
#include <stdlib.h>

static int calc(int n) {
  if (n < 2)
    return n;
  return calc(n - 1) + calc(n - 2);
}

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 30;
  printf("fib(%d) = %d\n", n, calc(n));
  return 0;
}
