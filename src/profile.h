#pragma once
#include <stdbool.h>
#include <stddef.h>
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
  bool reported; // FRAME / EVERY の report を一度でも出したか
  uint64_t start_frame;
  uint64_t report_frame;
  uint64_t report_every;
  char report_label[LUB_PROFILE_NAME_LEN];
  uint64_t frames;
  uint64_t frame_start_ns;
  uint64_t frame_total_ns;
  uint64_t frame_max_ns;
  // Lua の heap。
  // Lua VM の無い .NET 実行では lua_tracked が false のまま
  bool lua_tracked;
  uint64_t frame_alloc_bytes;
  uint64_t alloc_total_bytes;
  uint64_t alloc_max_bytes;
  uint64_t free_total_bytes;
  uint64_t gc_cycles;
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
// Lua の allocator が大きさを old_size から new_size に変えたときに呼ぶ。
void profile_note_lua_alloc(ProfileState *p, size_t old_size, size_t new_size);
void profile_note_gc_cycle(ProfileState *p);
void profile_report(ProfileState *p, const char *label);
// 終了時に呼ぶ。
// FRAME / EVERY の report が一度も出ていなければ label `exit` (LUB_PROFILE_LABEL があればそれ) で出す。
void profile_report_at_exit(ProfileState *p);
