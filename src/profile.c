#include "profile.h"
#include <SDL3/SDL.h>
#include <inttypes.h>
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
}

void profile_frame_begin(ProfileState *p, uint64_t frame_index) {
  if (!p->enabled)
    return;
  p->recording = frame_index >= p->start_frame;
  if (p->recording)
    p->frame_start_ns = SDL_GetTicksNS();
}

void profile_frame_end(ProfileState *p, uint64_t frame_index) {
  if (!p->enabled || !p->recording)
    return;
  uint64_t now = SDL_GetTicksNS();
  uint64_t dt = now - p->frame_start_ns;
  p->frame_total_ns += dt;
  if (dt > p->frame_max_ns)
    p->frame_max_ns = dt;
  p->frames++;
  p->stack_count = 0;

  if (p->report_frame > 0 && !p->report_frame_done &&
      frame_index + 1 >= p->report_frame) {
    profile_report(p, p->report_label[0] ? p->report_label : "frame");
    p->report_frame_done = true;
  }
  if (p->report_every > 0 && p->frames >= p->report_every) {
    profile_report(p, p->report_label[0] ? p->report_label : "every");
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
}

void profile_report(ProfileState *p, const char *label) {
  if (!p->enabled)
    return;
  const char *tag = (label && label[0]) ? label : "manual";
  double avg_frame = p->frames > 0 ? ns_to_ms(p->frame_total_ns) / p->frames : 0.0;
  double max_frame = ns_to_ms(p->frame_max_ns);
  SDL_Log("LUB_PROFILE label=%s frames=%" PRIu64 " avg_frame_ms=%.3f "
          "max_frame_ms=%.3f",
          tag, p->frames, avg_frame, max_frame);
  for (int i = 0; i < p->scope_count; ++i) {
    ProfileScopeStats *scope = &p->scopes[i];
    double total_ms = ns_to_ms(scope->total_ns);
    double avg_ms = scope->calls > 0 ? total_ms / scope->calls : 0.0;
    double pct = p->frame_total_ns > 0
                     ? (double)scope->total_ns * 100.0 / (double)p->frame_total_ns
                     : 0.0;
    SDL_Log("LUB_PROFILE_SCOPE label=%s name=%s calls=%" PRIu64
            " total_ms=%.3f avg_ms=%.3f max_ms=%.3f pct=%.1f",
            tag, scope->name, scope->calls, total_ms, avg_ms,
            ns_to_ms(scope->max_ns), pct);
  }
}
