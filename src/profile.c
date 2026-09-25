#include "profile.h"
#include "lub_lua_user.h"
#include <SDL3/SDL.h>
#include <inttypes.h>
#include <lua.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static bool env_flag(const char *name) {
  const char *s = SDL_getenv(name);
  return s && s[0] && SDL_strcmp(s, "0") != 0 &&
         SDL_strcasecmp(s, "false") != 0;
}

static uint64_t env_u64(const char *name, uint64_t fallback) {
  const char *s = SDL_getenv(name);
  if (!s || !s[0])
    return fallback;
  char *end = NULL;
  unsigned long long v = strtoull(s, &end, 10);
  return end != s ? (uint64_t)v : fallback;
}

static double ns_to_ms(uint64_t ns) { return (double)ns / 1000000.0; }

static double bytes_to_kb(uint64_t b) { return (double)b / 1024.0; }

static double avg_per(double v, uint64_t n) {
  return n > 0 ? v / (double)n : 0.0;
}

// 累計値の差。host が渡す値が戻っても (GC API の都合など) 負にしない。
static uint64_t delta_u64(uint64_t now, uint64_t before) {
  return now > before ? now - before : 0;
}

static int find_scope(ProfileState *p, const char *name) {
  for (int i = 0; i < p->scope_count; ++i) {
    if (SDL_strcmp(p->scopes[i].name, name) == 0)
      return i;
  }
  if (p->scope_count >= LUB_PROFILE_MAX_SCOPES)
    return -1;
  int i = p->scope_count++;
  SDL_strlcpy(p->scopes[i].name, name, sizeof(p->scopes[i].name));
  p->scopes[i].total_ns = 0;
  p->scopes[i].max_ns = 0;
  p->scopes[i].calls = 0;
  p->scopes[i].alloc_bytes = 0;
  p->scopes[i].gc_ns = 0;
  return i;
}

void profile_state_init(ProfileState *p) {
  memset(p, 0, sizeof(*p));
  p->enabled = env_flag("LUB_PROFILE");
  p->start_frame = env_u64("LUB_PROFILE_START_FRAME", 0);
  p->report_frame = env_u64("LUB_PROFILE_FRAME", 0);
  p->report_every = env_u64("LUB_PROFILE_EVERY", 0);
  const char *label = SDL_getenv("LUB_PROFILE_LABEL");
  if (label && label[0])
    SDL_strlcpy(p->report_label, label, sizeof(p->report_label));
}

void profile_reset(ProfileState *p) {
  p->frames = 0;
  p->frame_start_ns = p->recording ? SDL_GetTicksNS() : 0;
  p->frame_total_ns = 0;
  p->frame_max_ns = 0;
  p->scope_count = 0;
  p->stack_count = 0;
  // 記録中の frame は reset の時点から数える (時間と同じ)
  p->frame_heap_start = p->heap;
  p->gap_alloc_start = p->heap.alloc_bytes;
  memset(&p->win_heap, 0, sizeof(p->win_heap));
  p->win_alloc_max = 0;
  p->win_gc_ns_max = 0;
  p->win_gc_step_max_ns = 0;
  p->win_outside_alloc = 0;
  p->managed_frames = 0;
  p->managed_alloc_total = 0;
  p->managed_alloc_max = 0;
  memset(p->managed_collections, 0, sizeof(p->managed_collections));
  p->managed_pause_ns = 0;
  p->managed_pause_max_ns = 0;
}

void profile_frame_begin(ProfileState *p, uint64_t frame_index) {
  if (!p->enabled)
    return;
  p->recording = frame_index >= p->start_frame;
  if (p->recording) {
    p->frame_start_ns = SDL_GetTicksNS();
    // 直前の frame_end からここまで (on_event など) は frame の外
    p->win_outside_alloc += p->heap.alloc_bytes - p->gap_alloc_start;
    p->frame_heap_start = p->heap;
    p->in_frame = true;
  }
}

static void heap_frame_end(ProfileState *p) {
  const ProfileHeapCounters *a = &p->frame_heap_start;
  const ProfileHeapCounters *b = &p->heap;
  uint64_t alloc = b->alloc_bytes - a->alloc_bytes;
  uint64_t gc_ns = b->gc_ns - a->gc_ns;
  p->win_heap.alloc_bytes += alloc;
  p->win_heap.allocs += b->allocs - a->allocs;
  p->win_heap.free_bytes += b->free_bytes - a->free_bytes;
  p->win_heap.gc_ns += gc_ns;
  p->win_heap.gc_steps += b->gc_steps - a->gc_steps;
  p->win_heap.gc_cycles += b->gc_cycles - a->gc_cycles;
  if (alloc > p->win_alloc_max)
    p->win_alloc_max = alloc;
  if (gc_ns > p->win_gc_ns_max)
    p->win_gc_ns_max = gc_ns;
}

void profile_frame_end(ProfileState *p, uint64_t frame_index) {
  if (!p->enabled)
    return;
  p->gap_alloc_start = p->heap.alloc_bytes;
  if (!p->recording)
    return;
  uint64_t now = SDL_GetTicksNS();
  uint64_t dt = now - p->frame_start_ns;
  p->frame_total_ns += dt;
  if (dt > p->frame_max_ns)
    p->frame_max_ns = dt;
  p->frames++;
  p->stack_count = 0;
  p->in_frame = false;
  heap_frame_end(p);

  if (p->report_frame > 0 && !p->report_frame_done &&
      frame_index + 1 >= p->report_frame) {
    profile_report(p, p->report_label[0] ? p->report_label : "frame");
    p->report_frame_done = true;
    p->auto_reported = true;
  }
  if (p->report_every > 0 && p->frames >= p->report_every) {
    profile_report(p, p->report_label[0] ? p->report_label : "every");
    p->auto_reported = true;
    profile_reset(p);
  }
}

void profile_begin_scope(ProfileState *p, const char *name) {
  if (!p->enabled || !p->recording || !name || !name[0])
    return;
  if (p->stack_count >= LUB_PROFILE_STACK_MAX)
    return;
  int scope = find_scope(p, name);
  if (scope < 0)
    return;
  ProfileStackEntry *entry = &p->stack[p->stack_count++];
  entry->scope_index = scope;
  entry->alloc_start = p->heap.alloc_bytes;
  entry->gc_ns_start = p->heap.gc_ns;
  entry->start_ns = SDL_GetTicksNS();
}

void profile_end_scope(ProfileState *p, const char *name) {
  if (!p->enabled || !p->recording || p->stack_count <= 0)
    return;
  int stack_index = name && name[0] ? -1 : p->stack_count - 1;
  if (name && name[0]) {
    for (int i = p->stack_count - 1; i >= 0; --i) {
      ProfileStackEntry *entry = &p->stack[i];
      if (SDL_strcmp(p->scopes[entry->scope_index].name, name) == 0) {
        stack_index = i;
        break;
      }
    }
  }
  if (stack_index < 0)
    return;
  ProfileStackEntry entry = p->stack[stack_index];
  p->stack_count = stack_index;
  uint64_t dt = SDL_GetTicksNS() - entry.start_ns;
  ProfileScopeStats *scope = &p->scopes[entry.scope_index];
  scope->total_ns += dt;
  if (dt > scope->max_ns)
    scope->max_ns = dt;
  scope->calls++;
  scope->alloc_bytes += p->heap.alloc_bytes - entry.alloc_start;
  scope->gc_ns += p->heap.gc_ns - entry.gc_ns_start;
}

static void report_lua_heap(ProfileState *p, const char *tag) {
  const ProfileHeapCounters *w = &p->win_heap;
  // report の時点で frame の外にいれば、直前の frame_end からの分も足す
  // (終了時の report なら on_quit など)
  uint64_t outside = p->win_outside_alloc;
  if (!p->in_frame)
    outside += p->heap.alloc_bytes - p->gap_alloc_start;
  double gc_pct = p->frame_total_ns > 0
                      ? (double)w->gc_ns * 100.0 / (double)p->frame_total_ns
                      : 0.0;
  SDL_Log("LUB_PROFILE_HEAP label=%s heap=lua frames=%" PRIu64
          " alloc_kb_avg=%.3f alloc_kb_max=%.3f allocs_avg=%.1f "
          "free_kb_avg=%.3f live_kb=%.1f gc_steps=%" PRIu64
          " gc_cycles=%" PRIu64 " gc_ms_total=%.3f gc_ms_avg=%.3f "
          "gc_ms_max_frame=%.3f gc_ms_max_step=%.3f gc_pct=%.1f "
          "outside_kb=%.3f",
          tag, p->frames, avg_per(bytes_to_kb(w->alloc_bytes), p->frames),
          bytes_to_kb(p->win_alloc_max), avg_per((double)w->allocs, p->frames),
          avg_per(bytes_to_kb(w->free_bytes), p->frames),
          (double)p->lua_live / 1024.0, w->gc_steps, w->gc_cycles,
          ns_to_ms(w->gc_ns), avg_per(ns_to_ms(w->gc_ns), p->frames),
          ns_to_ms(p->win_gc_ns_max), ns_to_ms(p->win_gc_step_max_ns), gc_pct,
          bytes_to_kb(outside));
}

static void report_managed_heap(ProfileState *p, const char *tag) {
  uint64_t n = p->managed_frames;
  double gc_pct = p->frame_total_ns > 0 ? (double)p->managed_pause_ns * 100.0 /
                                              (double)p->frame_total_ns
                                        : 0.0;
  SDL_Log("LUB_PROFILE_HEAP label=%s heap=dotnet frames=%" PRIu64
          " alloc_kb_avg=%.3f alloc_kb_max=%.3f gc_gen0=%" PRIu64
          " gc_gen1=%" PRIu64 " gc_gen2=%" PRIu64
          " gc_pause_ms_total=%.3f gc_pause_ms_max_frame=%.3f gc_pct=%.1f",
          tag, n, avg_per(bytes_to_kb(p->managed_alloc_total), n),
          bytes_to_kb(p->managed_alloc_max), p->managed_collections[0],
          p->managed_collections[1], p->managed_collections[2],
          ns_to_ms(p->managed_pause_ns), ns_to_ms(p->managed_pause_max_ns),
          gc_pct);
}

void profile_report(ProfileState *p, const char *label) {
  if (!p->enabled)
    return;
  const char *tag = (label && label[0]) ? label : "manual";
  double avg_frame =
      p->frames > 0 ? ns_to_ms(p->frame_total_ns) / p->frames : 0.0;
  double max_frame = ns_to_ms(p->frame_max_ns);
  const ProfileHeapCounters *w = &p->win_heap;
  SDL_Log("LUB_PROFILE label=%s frames=%" PRIu64 " avg_frame_ms=%.3f "
          "max_frame_ms=%.3f alloc_kb_avg=%.3f alloc_kb_max=%.3f "
          "gc_ms_avg=%.3f gc_steps_avg=%.1f",
          tag, p->frames, avg_frame, max_frame,
          avg_per(bytes_to_kb(w->alloc_bytes), p->frames),
          bytes_to_kb(p->win_alloc_max), avg_per(ns_to_ms(w->gc_ns), p->frames),
          avg_per((double)w->gc_steps, p->frames));
  for (int i = 0; i < p->scope_count; ++i) {
    ProfileScopeStats *scope = &p->scopes[i];
    double total_ms = ns_to_ms(scope->total_ns);
    double avg_ms = scope->calls > 0 ? total_ms / scope->calls : 0.0;
    double pct = p->frame_total_ns > 0 ? (double)scope->total_ns * 100.0 /
                                             (double)p->frame_total_ns
                                       : 0.0;
    SDL_Log("LUB_PROFILE_SCOPE label=%s name=%s calls=%" PRIu64
            " total_ms=%.3f avg_ms=%.3f max_ms=%.3f pct=%.1f alloc_kb=%.3f "
            "alloc_kb_avg=%.3f gc_ms=%.3f",
            tag, scope->name, scope->calls, total_ms, avg_ms,
            ns_to_ms(scope->max_ns), pct, bytes_to_kb(scope->alloc_bytes),
            avg_per(bytes_to_kb(scope->alloc_bytes), scope->calls),
            ns_to_ms(scope->gc_ns));
  }
  if (p->lua_attached)
    report_lua_heap(p, tag);
  if (p->managed_seen)
    report_managed_heap(p, tag);
}

void profile_report_at_exit(ProfileState *p) {
  if (!p->enabled || p->auto_reported)
    return;
  // LUB_PROFILE_LABEL は付けない。label=exit なら FRAME / EVERY の report が
  // 出ずに終わったことが分かる
  profile_report(p, "exit");
}

// ---------------------------------------------------------------- Lua heap

// GC step の hook は Lua 側では関数 pointer 1 つなので、数え先もここに 1 つ。
static ProfileState *g_gc_profile = NULL;

static void profile_gc_trace(struct lua_State *L, int begin, int cycle_done) {
  (void)L;
  ProfileState *p = g_gc_profile;
  if (!p)
    return;
  uint64_t now = SDL_GetTicksNS();
  if (begin) {
    // end が来ないまま (longjmp で抜けた step) 次が始まったら古い始まりは捨てる
    p->gc_step_open = true;
    p->gc_step_start_ns = now;
    return;
  }
  if (!p->gc_step_open)
    return;
  p->gc_step_open = false;
  uint64_t dt = now - p->gc_step_start_ns;
  p->heap.gc_ns += dt;
  p->heap.gc_steps++;
  if (cycle_done)
    p->heap.gc_cycles++;
  if (p->in_frame && dt > p->win_gc_step_max_ns)
    p->win_gc_step_max_ns = dt;
}

// 元の allocator を包んで数える。Lua の API はここから呼ばない。
static void *profile_lua_alloc(void *ud, void *ptr, size_t osize,
                               size_t nsize) {
  ProfileState *p = (ProfileState *)ud;
  void *block = p->lua_base_alloc(p->lua_base_ud, ptr, osize, nsize);
  if (nsize > 0 && !block)
    return NULL; // 失敗は数えない (Lua は緊急 GC の後にもう一度呼ぶ)
  // ptr が NULL (新しい block) のとき osize は大きさではなく型の tag
  size_t old = ptr ? osize : 0;
  if (nsize > old)
    p->heap.alloc_bytes += nsize - old;
  else
    p->heap.free_bytes += old - nsize;
  if (!ptr && nsize > 0)
    p->heap.allocs++;
  p->lua_live += (int64_t)nsize - (int64_t)old;
  return block;
}

void profile_attach_lua(ProfileState *p, struct lua_State *L) {
  if (!p->enabled || !L)
    return;
  p->lua_base_alloc = lua_getallocf(L, &p->lua_base_ud);
  // 付ける前 (luaL_newstate の中) の確保は Lua 自身の数えた値で引き継ぐ。
  // 以後 lua_live は lua_gc(COUNT) * 1024 + COUNTB と一致する (lauxlib の
  // buffer と、それを引き取った文字列の分だけは Lua の数に入らない)。
  p->lua_live =
      (int64_t)lua_gc(L, LUA_GCCOUNT) * 1024 + (int64_t)lua_gc(L, LUA_GCCOUNTB);
  lua_setallocf(L, profile_lua_alloc, p);
  p->lua_attached = true;
  g_gc_profile = p;
  lub_lua_gc_trace = profile_gc_trace;
}

void profile_detach_lua(ProfileState *p) {
  if (g_gc_profile != p)
    return;
  lub_lua_gc_trace = NULL;
  g_gc_profile = NULL;
}

// ------------------------------------------------------------ managed heap

void profile_managed_sample(ProfileState *p, const ProfileManagedSample *s) {
  if (!p->enabled)
    return;
  if (p->managed_seen && p->in_frame) {
    const ProfileManagedSample *last = &p->managed_last;
    uint64_t alloc = delta_u64(s->alloc_bytes, last->alloc_bytes);
    uint64_t pause = delta_u64(s->pause_ns, last->pause_ns);
    p->managed_frames++;
    p->managed_alloc_total += alloc;
    if (alloc > p->managed_alloc_max)
      p->managed_alloc_max = alloc;
    for (int g = 0; g < 3; ++g)
      p->managed_collections[g] +=
          delta_u64(s->collections[g], last->collections[g]);
    p->managed_pause_ns += pause;
    if (pause > p->managed_pause_max_ns)
      p->managed_pause_max_ns = pause;
  }
  p->managed_last = *s;
  p->managed_seen = true;
}
