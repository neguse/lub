#include <stdio.h>
#include <stdlib.h>

typedef struct {
  float px, py, vx, vy;
} Particle;

static int state = 12345;

static int lcg(void) {
  state = (int)(((unsigned)state * 1103515245u + 12345u) & 0x3FFFFFFFu);
  return state;
}

static float frand(float lo, float hi) { return lo + (float)(lcg() % 1000) * (1.0f / 1000.0f) * (hi - lo); }

int main(void) {
  const char *s = getenv("BENCH_N");
  int frames = s ? atoi(s) : 100;
  int n = 4096;
  float dt = 0.02f;
  Particle *ps = calloc(n, sizeof(Particle));
  for (int i = 0; i < n; i++) {
    ps[i].px = frand(100.0f, 300.0f);
    ps[i].py = frand(50.0f, 200.0f);
    ps[i].vx = frand(-40.0f, 40.0f);
    ps[i].vy = frand(-40.0f, 40.0f);
  }
  for (int f = 0; f < frames; f++) {
    for (int i = 0; i < n; i++) {
      Particle *p = &ps[i];
      p->vy = p->vy + 98.0f * dt;
      p->px = p->px + p->vx * dt;
      p->py = p->py + p->vy * dt;
      if (p->px < 0.0f) {
        p->px = 0.0f;
        p->vx = -p->vx * 0.9f;
      }
      if (p->px > 400.0f) {
        p->px = 400.0f;
        p->vx = -p->vx * 0.9f;
      }
      if (p->py < 0.0f) {
        p->py = 0.0f;
        p->vy = -p->vy * 0.9f;
      }
      if (p->py > 240.0f) {
        p->py = 240.0f;
        p->vy = -p->vy * 0.9f;
      }
      float dx = p->px - 200.0f;
      float dy = p->py - 120.0f;
      float d2 = dx * dx + dy * dy;
      if (d2 < 400.0f) {
        p->px = p->px + dx * 0.1f;
        p->py = p->py + dy * 0.1f;
      }
    }
  }
  float sx = 0.0f, sy = 0.0f;
  for (int i = 0; i < n; i++) {
    sx = sx + ps[i].px;
    sy = sy + ps[i].py;
  }
  printf("sum x: %d, sum y: %d\n", (int)sx, (int)sy);
  return 0;
}
