#include <math.h>
#include <stdio.h>
#include <stdlib.h>

static float A(int i, int j) {
  int ij = i + j;
  return 1.0f / (float)(ij * (ij + 1) / 2 + i + 1);
}

static void multiplyAv(int n, const float *v, float *av) {
  for (int i = 0; i < n; i++) {
    float sum = 0.0f;
    for (int j = 0; j < n; j++)
      sum = sum + A(i, j) * v[j];
    av[i] = sum;
  }
}

static void multiplyAtv(int n, const float *v, float *atv) {
  for (int i = 0; i < n; i++) {
    float sum = 0.0f;
    for (int j = 0; j < n; j++)
      sum = sum + A(j, i) * v[j];
    atv[i] = sum;
  }
}

static void multiplyAtAv(int n, const float *v, float *tmp, float *atav) {
  multiplyAv(n, v, tmp);
  multiplyAtv(n, tmp, atav);
}

static int run(int n, int print) {
  float *u = malloc(n * sizeof(float));
  float *v = malloc(n * sizeof(float));
  float *tmp = malloc(n * sizeof(float));
  for (int i = 0; i < n; i++)
    u[i] = 1.0f;
  for (int k = 0; k < 10; k++) {
    multiplyAtAv(n, u, tmp, v);
    multiplyAtAv(n, v, tmp, u);
  }
  float vBv = 0.0f, vv = 0.0f;
  for (int i = 0; i < n; i++) {
    vBv = vBv + u[i] * v[i];
    vv = vv + v[i] * v[i];
  }
  int result = (int)(sqrtf(vBv / vv) * 1000000.0f);
  free(u);
  free(v);
  free(tmp);
  if (print)
    printf("spectral norm x 1e6: %d\n", result);
  return result;
}

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 100;
  // BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
  const char *rs = getenv("BENCH_REPS");
  int reps = rs ? atoi(rs) : 1;
  int prev = 0;
  for (int r = 0; r < reps; r++)
    prev = run(prev == -1 ? n + 1 : n, r == reps - 1);
  return 0;
}
