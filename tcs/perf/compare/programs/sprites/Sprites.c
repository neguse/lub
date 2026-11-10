#include <stdio.h>
#include <stdlib.h>

typedef struct {
  float x, y, vx, vy;
  int frame;
} Sprite;

static int state = 12345;

static int lcg(void) {
  state = (int)(((unsigned)state * 1103515245u + 12345u) & 0x3FFFFFFFu);
  return state;
}

static float frand(float lo, float hi) {
  return lo + (float)(lcg() % 1000) * (1.0f / 1000.0f) * (hi - lo);
}

static void update(Sprite *s, float dt) {
  s->x = s->x + s->vx * dt;
  s->y = s->y + s->vy * dt;
  if (s->x < 0.0f) {
    s->x = 0.0f;
    s->vx = -s->vx;
  }
  if (s->x > 400.0f) {
    s->x = 400.0f;
    s->vx = -s->vx;
  }
  if (s->y < 0.0f) {
    s->y = 0.0f;
    s->vy = -s->vy;
  }
  if (s->y > 240.0f) {
    s->y = 240.0f;
    s->vy = -s->vy;
  }
  s->frame = (s->frame + 1) % 8;
}

static int run(int frames, int print) {
  state = 12345;
  int count = 1024;
  float dt = 0.02f;
  // C# の List<Sprite> (参照の列) に合わせ、sprite を 1 つずつ確保してポインタの配列に持つ
  Sprite **sprites = malloc(count * sizeof(Sprite *));
  for (int i = 0; i < count; i++) {
    Sprite *sp = malloc(sizeof(Sprite));
    sp->x = frand(0.0f, 400.0f);
    sp->y = frand(0.0f, 240.0f);
    sp->vx = frand(-60.0f, 60.0f);
    sp->vy = frand(-60.0f, 60.0f);
    sp->frame = lcg() % 8;
    sprites[i] = sp;
  }
  for (int f = 0; f < frames; f++)
    for (int i = 0; i < count; i++)
      update(sprites[i], dt);
  float sx = 0.0f, sy = 0.0f;
  int sf = 0;
  for (int i = 0; i < count; i++) {
    sx = sx + sprites[i]->x;
    sy = sy + sprites[i]->y;
    sf = sf + sprites[i]->frame;
  }
  for (int i = 0; i < count; i++)
    free(sprites[i]);
  free(sprites);
  if (print)
    printf("sum x: %d, sum y: %d, sum frame: %d\n", (int)sx, (int)sy, sf);
  return sf + (int)sx + (int)sy;
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
