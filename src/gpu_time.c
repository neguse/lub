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

static bool g_on;
// 数え始める frame (frame の途中で数え始めたら次の frame。backend は frame の頭で時刻の用意をする)
static uint64_t g_from;
static GpuFrame g_frames[GPU_TIME_FRAMES];
static GpuPassRec *g_cur; // 控えている pass (無ければ NULL)
static uint32_t g_cur_index;
// 届いた pass と、渡した pass (渡した文字列は次に取り出すまで有効)
static GpuDoneList g_pending, g_taken;
static LubGpuPass *g_out;
static int32_t g_out_cap;

void gpu_time_enable(App *app, bool on) {
  if (on && !g_on)
    g_from = app->proc->frame_index + (app->proc->in_frame ? 1 : 0);
  g_on = on;
  g_cur = NULL;
}

bool gpu_time_enabled(void) { return g_on; }

static void copy_str(char *dst, size_t cap, const char *src) {
  SDL_strlcpy(dst, src ? src : "", cap);
}

// 描き先の名前の無い pass は lub の中の pass (Gfx.Pick の描き直し)
static bool wants(App *app, const char *target) {
  return g_on && app->proc->in_frame && !app->in_overlay && target &&
         app->proc->frame_index >= g_from;
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

void gpu_time_take(const LubGpuPass **out, int32_t *count) {
  GpuDoneList t = g_taken;
  g_taken = g_pending;
  g_pending = t;
  g_pending.n = 0;
  if (g_taken.n > g_out_cap) {
    LubGpuPass *p = (LubGpuPass *)SDL_realloc(g_out, (size_t)g_taken.n *
                                                         sizeof(LubGpuPass));
    if (!p) {
      *out = NULL;
      *count = 0;
      return;
    }
    g_out = p;
    g_out_cap = g_taken.n;
  }
  for (int32_t i = 0; i < g_taken.n; i++) {
    const GpuDone *d = &g_taken.p[i];
    g_out[i].site = (LubStr){d->rec.site, (int32_t)SDL_strlen(d->rec.site)};
    g_out[i].target =
        (LubStr){d->rec.target, (int32_t)SDL_strlen(d->rec.target)};
    g_out[i].ms = d->ms;
    g_out[i].draws = d->rec.draws;
  }
  *out = g_out;
  *count = g_taken.n;
}
