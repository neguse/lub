#include "gpu_time.h"
#include "app.h"
#include "backend.h"
#include <SDL3/SDL.h>
#include <string.h>

// 時刻を待つ frame の数 (backend の frames in flight と非同期の読み出しの遅れより多く)。届かないまま回ってきた frame の pass は時間 -1 で出す
#define GPU_TIME_FRAMES 8
// 取り出されるまで持つ pass の数 (超えた分は捨てる)
#define GPU_TIME_KEEP 4096

typedef struct GpuPassRec {
  // pass を始めた app (取り出すときに分ける。比べるだけで読まない)
  const App *owner;
  char site[160];
  char target[64];
  int32_t draws;
} GpuPassRec;

typedef struct GpuFrame {
  uint64_t frame;
  bool used;
  bool waiting; // backend が時刻を書いた (gpu_time_resolved を待つ)
  uint32_t n;
  GpuPassRec passes[GPU_TIME_PASSES];
} GpuFrame;

typedef struct GpuDone {
  GpuPassRec rec;
  float ms;
} GpuDone;

typedef struct GpuDoneList {
  GpuDone *p;
  int32_t n, cap;
} GpuDoneList;

// 数えている app の数 (App.gpu_time_on)。1 つでもあれば backend が時刻の用意をする
static int g_on;
static GpuFrame g_frames[GPU_TIME_FRAMES];
static GpuPassRec *g_cur; // 控えている pass (無ければ NULL)
static uint32_t g_cur_index;
// 届いた pass と、渡した pass (渡した文字列は次に取り出すまで有効)
static GpuDoneList g_pending, g_taken;
static LubGpuPass *g_out;
static int32_t g_out_cap;

void gpu_time_enable(App *app, bool on) {
  if (on && !app->gpu_time_on)
    app->gpu_time_from = app->proc->frame_index + (app->proc->in_frame ? 1 : 0);
  if (on != app->gpu_time_on)
    g_on += on ? 1 : -1;
  app->gpu_time_on = on;
  g_cur = NULL;
}

bool gpu_time_enabled(void) { return g_on > 0; }

static void copy_str(char *dst, size_t cap, const char *src) {
  SDL_strlcpy(dst, src ? src : "", cap);
}

// 描き先の名前の無い pass は lub の中の pass (Gfx.Pick の描き直し)
static bool wants(App *app, const char *target) {
  return app->gpu_time_on && app->proc->in_frame && target &&
         app->proc->frame_index >= app->gpu_time_from;
}

bool gpu_time_wants_pass(App *app) { return wants(app, app->pass.label); }

static void flush(GpuFrame *f, const uint64_t *ns, uint32_t count);

void gpu_time_pass_begin(App *app, const char *target) {
  g_cur = NULL;
  if (!wants(app, target))
    return;
  char site[sizeof(g_cur->site)] = "";
  if (app->debug_hooks && app->debug_hooks->profile_site)
    app->debug_hooks->profile_site(app, site, (int32_t)sizeof site);
  GpuFrame *f = &g_frames[app->proc->frame_index % GPU_TIME_FRAMES];
  if (f->used && f->frame != app->proc->frame_index)
    flush(f, NULL, 0);
  if (!f->used) {
    f->used = true;
    f->waiting = false;
    f->frame = app->proc->frame_index;
    f->n = 0;
  }
  if (f->n >= GPU_TIME_PASSES)
    return;
  g_cur_index = f->n++;
  g_cur = &f->passes[g_cur_index];
  g_cur->owner = app;
  copy_str(g_cur->site, sizeof g_cur->site, site);
  copy_str(g_cur->target, sizeof g_cur->target, target);
  g_cur->draws = 0;
  if (g_backend->timestamp && g_backend->timestamp(app->proc, 2 * g_cur_index))
    f->waiting = true;
}

void gpu_time_pass_end(App *app) {
  if (g_cur && g_backend->timestamp)
    g_backend->timestamp(app->proc, 2 * g_cur_index + 1);
  g_cur = NULL;
}

void gpu_time_draw(App *app) {
  (void)app;
  if (g_cur)
    g_cur->draws++;
}

static void push_done(const GpuPassRec *rec, float ms) {
  GpuDoneList *l = &g_pending;
  if (l->n == l->cap) {
    if (l->cap >= GPU_TIME_KEEP)
      return;
    int32_t cap = l->cap ? l->cap * 2 : 64;
    GpuDone *p = (GpuDone *)SDL_realloc(l->p, (size_t)cap * sizeof(GpuDone));
    if (!p)
      return;
    l->p = p;
    l->cap = cap;
  }
  l->p[l->n].rec = *rec;
  l->p[l->n].ms = ms;
  l->n++;
}

// frame の pass を出して、frame を空ける。ns が NULL なら時間は -1
static void flush(GpuFrame *f, const uint64_t *ns, uint32_t count) {
  for (uint32_t i = 0; i < f->n; i++) {
    uint64_t b = ns && 2 * i < count ? ns[2 * i] : GPU_TIME_NONE;
    uint64_t e = ns && 2 * i + 1 < count ? ns[2 * i + 1] : GPU_TIME_NONE;
    float ms = b != GPU_TIME_NONE && e != GPU_TIME_NONE && e >= b
                   ? (float)((double)(e - b) / 1e6)
                   : -1.0f;
    // 始めた app がもう無い pass は捨てる
    if (f->passes[i].owner)
      push_done(&f->passes[i], ms);
  }
  f->used = false;
  f->n = 0;
}

void gpu_time_frame_end(Process *proc) {
  g_cur = NULL;
  GpuFrame *f = &g_frames[proc->frame_index % GPU_TIME_FRAMES];
  if (f->used && f->frame == proc->frame_index && !f->waiting)
    flush(f, NULL, 0);
}

void gpu_time_resolved(uint64_t frame, const uint64_t *ns, uint32_t count) {
  GpuFrame *f = &g_frames[frame % GPU_TIME_FRAMES];
  if (f->used && f->frame == frame && f->waiting)
    flush(f, ns, count);
}

// app の pass を pending から taken に移す (ほかの app の pass は pending に残す)
void gpu_time_take(const App *app, const LubGpuPass **out, int32_t *count) {
  *out = NULL;
  *count = 0;
  GpuDoneList *t = &g_taken;
  t->n = 0;
  if (t->cap < g_pending.n) {
    GpuDone *p =
        (GpuDone *)SDL_realloc(t->p, (size_t)g_pending.n * sizeof(GpuDone));
    if (!p)
      return;
    t->p = p;
    t->cap = g_pending.n;
  }
  int32_t kept = 0;
  for (int32_t i = 0; i < g_pending.n; i++) {
    if (g_pending.p[i].rec.owner == app)
      t->p[t->n++] = g_pending.p[i];
    else
      g_pending.p[kept++] = g_pending.p[i];
  }
  g_pending.n = kept;
  if (t->n > g_out_cap) {
    LubGpuPass *p =
        (LubGpuPass *)SDL_realloc(g_out, (size_t)t->n * sizeof(LubGpuPass));
    if (!p)
      return;
    g_out = p;
    g_out_cap = t->n;
  }
  for (int32_t i = 0; i < t->n; i++) {
    const GpuDone *d = &t->p[i];
    g_out[i].site = (LubStr){d->rec.site, (int32_t)SDL_strlen(d->rec.site)};
    g_out[i].target =
        (LubStr){d->rec.target, (int32_t)SDL_strlen(d->rec.target)};
    g_out[i].ms = d->ms;
    g_out[i].draws = d->rec.draws;
  }
  *out = g_out;
  *count = t->n;
}

// app が終わる: 数えるのをやめ、まだ取り出していない pass を捨てる
void gpu_time_forget(App *app) {
  gpu_time_enable(app, false);
  int32_t kept = 0;
  for (int32_t i = 0; i < g_pending.n; i++)
    if (g_pending.p[i].rec.owner != app)
      g_pending.p[kept++] = g_pending.p[i];
  g_pending.n = kept;
  for (int k = 0; k < GPU_TIME_FRAMES; k++)
    for (uint32_t i = 0; i < g_frames[k].n; i++)
      if (g_frames[k].passes[i].owner == app)
        g_frames[k].passes[i].owner = NULL;
}
