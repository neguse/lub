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

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 100;
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
  printf("spectral norm x 1e6: %d\n", (int)(sqrtf(vBv / vv) * 1000000.0f));
  return 0;
}
