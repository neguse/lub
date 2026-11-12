// Rewind (rewind.h)。
#include "rewind.h"
#include "app.h"
#include <stdlib.h>
#include <string.h>

typedef struct RewindFrame {
  void *snapshot; // player の控え (LubDebugHooks.snapshot_take)
  double dt;
  InputState input;
} RewindFrame;

typedef struct RewindState {
  int capacity;
  RewindFrame *frames; // ring
  int head, count;     // head = 一番古い控え
  int paused;          // 止めている位置 (古い方からの添字、-1 = 止めていない)
  bool replaying;      // 止めた frame のゲームの OnFrame の間
  uint32_t layout;     // 控えたときの player の layout_version
  // 止めている間に控えを捨てた。次の frame の控えで止め直す
  bool repause;
} RewindState;

static RewindState *state(App *app) {
  if (!app->rewind) {
    app->rewind = (RewindState *)calloc(1, sizeof(RewindState));
    if (app->rewind)
      app->rewind->paused = -1;
  }
  return app->rewind;
}

static RewindFrame *frame_at(RewindState *r, int i) {
  return &r->frames[(r->head + i) % r->capacity];
}

static void drop_frame(App *app, RewindFrame *f) {
  if (f->snapshot && app->debug_hooks && app->debug_hooks->snapshot_free)
    app->debug_hooks->snapshot_free(app, f->snapshot);
  f->snapshot = NULL;
}

static void drop_all(App *app, RewindState *r) {
  for (int i = 0; i < r->count; ++i)
    drop_frame(app, frame_at(r, i));
  r->count = 0;
  r->head = 0;
  r->paused = -1;
  r->replaying = false;
  r->repause = false;
}

// player が控えを戻せなくなっていたら (reload が object を作り直した)、控えを捨てる。
// 止めていたなら、止めたままにする (次の frame で今の状態を控え直して止める)
static void check_layout(App *app, RewindState *r) {
  const LubDebugHooks *h = app->debug_hooks;
  uint32_t now = h && h->layout_version ? h->layout_version(app) : 0;
  if (now != r->layout) {
    bool paused = r->paused >= 0;
    drop_all(app, r);
    r->layout = now;
    r->repause = paused;
  }
}

static bool hooks_ready(const App *app) {
  const LubDebugHooks *h = app->debug_hooks;
  return h && h->snapshot_take && h->snapshot_restore && h->snapshot_free;
}

void rewind_record(App *app, int frames) {
  RewindState *r = state(app);
  if (!r || !app->in_overlay || frames < 0 || frames == r->capacity)
    return;
  drop_all(app, r);
  free(r->frames);
  r->frames = NULL;
  r->capacity = 0;
  if (frames == 0)
    return;
  r->frames = (RewindFrame *)calloc((size_t)frames, sizeof(RewindFrame));
  if (r->frames)
    r->capacity = frames;
}

int rewind_count(const App *app) {
  return app->rewind ? app->rewind->count : 0;
}

int rewind_position(const App *app) {
  const RewindState *r = app->rewind;
  if (!r || r->paused < 0)
    return -1;
  return r->count - 1 - r->paused;
}

bool rewind_paused(const App *app) {
  return app->rewind && app->rewind->paused >= 0;
}

void *rewind_snapshot(const App *app, int back) {
  RewindState *r = app->rewind;
  if (!r || back < 0)
    return NULL;
  int at = (r->paused >= 0 ? r->paused : r->count - 1) - back;
  return at < 0 ? NULL : frame_at(r, at)->snapshot;
}

// ---------------------------------------------------------------- input

// その frame にゲームから見える入力を控える (overlay が取っている種類は何も押されていない)
static void capture_input(App *app, InputState *in) {
  *in = app->input;
  if (app->overlay_grab_keyboard) {
    memset(in->keys, 0, sizeof(in->keys));
    memset(in->key_pressed, 0, sizeof(in->key_pressed));
    memset(in->key_released, 0, sizeof(in->key_released));
  }
  if (app->overlay_grab_mouse) {
    in->mouse_buttons = 0;
    in->mouse_pressed_mask = 0;
    in->mouse_released_mask = 0;
    in->mouse_rel_x = 0.0f;
    in->mouse_rel_y = 0.0f;
  }
}

const InputState *rewind_input(const App *app) {
  const RewindState *r = app->rewind;
  if (!r || !r->replaying || app->in_overlay)
    return NULL;
  return &((RewindState *)r)->frames[(r->head + r->paused) % r->capacity].input;
}

// ---------------------------------------------------------------- frame

void rewind_pause(App *app, int back) {
  RewindState *r = state(app);
  if (!r || !app->in_overlay || !hooks_ready(app))
    return;
  check_layout(app, r);
  if (back >= 0) {
    if (back >= r->count)
      return;
    r->paused = r->count - 1 - back;
    return;
  }
  if (r->paused < 0) {
    r->repause = false;
    return;
  }
  // 止めた位置の状態に戻し、それより後の控えを捨てる (その位置の控えも次の frame で取り直す)
  app->debug_hooks->snapshot_restore(app, frame_at(r, r->paused)->snapshot);
  for (int i = r->paused; i < r->count; ++i)
    drop_frame(app, frame_at(r, i));
  r->count = r->paused;
  r->paused = -1;
}

bool rewind_frame_begin(App *app, double *dt) {
  RewindState *r = app->rewind;
  if (!r || r->capacity == 0 || !hooks_ready(app))
    return false;
  check_layout(app, r);
  if (r->paused >= 0) {
    RewindFrame *f = frame_at(r, r->paused);
    app->debug_hooks->snapshot_restore(app, f->snapshot);
    *dt = f->dt;
    r->replaying = true;
    return true;
  }
  if (r->count == r->capacity) {
    drop_frame(app, frame_at(r, 0));
    r->head = (r->head + 1) % r->capacity;
    r->count--;
  }
  void *snapshot = app->debug_hooks->snapshot_take(app);
  if (!snapshot)
    return false;
  RewindFrame *f = frame_at(r, r->count);
  f->snapshot = snapshot;
  f->dt = *dt;
  capture_input(app, &f->input);
  r->count++;
  if (r->repause) {
    r->repause = false;
    r->paused = r->count - 1;
    r->replaying = true;
    return true;
  }
  return false;
}

void rewind_frame_end(App *app) {
  if (app->rewind)
    app->rewind->replaying = false;
}

void rewind_drop(App *app) {
  if (app->rewind)
    drop_all(app, app->rewind);
}

void rewind_shutdown(App *app) {
  RewindState *r = app->rewind;
  if (!r)
    return;
  drop_all(app, r);
  free(r->frames);
  free(r);
  app->rewind = NULL;
}
