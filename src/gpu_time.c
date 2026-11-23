#include "gpu_time.h"
#include "app.h"
#include "backend.h"
#include <SDL3/SDL.h>
#include <string.h>

// Number of frames to wait for timestamps (more than the backend's frames in
// flight plus the asynchronous readback delay). Passes of a frame that comes
// around again without its timestamps arriving are output with time -1
#define GPU_TIME_FRAMES 8
// Number of passes kept until they are taken (any beyond this are dropped)
#define GPU_TIME_KEEP 4096

typedef struct GpuPassRec {
  // The app that began the pass (used to separate them when taking. Only
  // compared, never dereferenced)
  const App *owner;
  char site[160];
  char target[64];
  int32_t draws;
} GpuPassRec;

typedef struct GpuFrame {
  uint64_t frame;
  bool used;
  bool waiting; // The backend wrote timestamps (waiting for gpu_time_resolved)
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

// Number of apps that are counting (App.gpu_time_on). If there is at least one,
// the backend prepares timestamps
static int g_on;
static GpuFrame g_frames[GPU_TIME_FRAMES];
static GpuPassRec *g_cur; // The pass being recorded (NULL if none)
static uint32_t g_cur_index;
// Passes that arrived, and passes handed out (handed-out strings are valid
// until the next take)
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

// A pass without a render target name is a pass internal to lub (the Gfx.Pick
// redraw)
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

// Output the frame's passes and free the frame. If ns is NULL, the time is -1
static void flush(GpuFrame *f, const uint64_t *ns, uint32_t count) {
  for (uint32_t i = 0; i < f->n; i++) {
    uint64_t b = ns && 2 * i < count ? ns[2 * i] : GPU_TIME_NONE;
    uint64_t e = ns && 2 * i + 1 < count ? ns[2 * i + 1] : GPU_TIME_NONE;
    float ms = b != GPU_TIME_NONE && e != GPU_TIME_NONE && e >= b
                   ? (float)((double)(e - b) / 1e6)
                   : -1.0f;
    // Drop passes whose app no longer exists
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

// Move the app's passes from pending to taken (other apps' passes stay in
// pending)
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

// The app is ending: stop counting and drop the passes not taken yet
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
