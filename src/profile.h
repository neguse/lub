#pragma once
#include <stdbool.h>
#include <stdint.h>

#define LUB_PROFILE_MAX_SCOPES 128
#define LUB_PROFILE_NAME_LEN 64
#define LUB_PROFILE_STACK_MAX 64

typedef struct ProfileScopeStats {
  char name[LUB_PROFILE_NAME_LEN];
  uint64_t total_ns;
  uint64_t max_ns;
  uint64_t calls;
} ProfileScopeStats;

typedef struct ProfileStackEntry {
  int scope_index;
  uint64_t start_ns;
} ProfileStackEntry;

typedef struct ProfileState {
  bool enabled;
  bool recording;
  bool report_frame_done;
  uint64_t start_frame;
  uint64_t report_frame;
  uint64_t report_every;
  char report_label[LUB_PROFILE_NAME_LEN];
  uint64_t frames;
  uint64_t frame_start_ns;
  uint64_t frame_total_ns;
  uint64_t frame_max_ns;
  int scope_count;
  int stack_count;
  ProfileScopeStats scopes[LUB_PROFILE_MAX_SCOPES];
  ProfileStackEntry stack[LUB_PROFILE_STACK_MAX];
} ProfileState;

void profile_state_init(ProfileState *p);
void profile_reset(ProfileState *p);
void profile_frame_begin(ProfileState *p, uint64_t frame_index);
void profile_frame_end(ProfileState *p, uint64_t frame_index);
void profile_begin_scope(ProfileState *p, const char *name);
void profile_end_scope(ProfileState *p, const char *name);
void profile_report(ProfileState *p, const char *label);
