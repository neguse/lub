#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define LUB_PROFILE_MAX_SCOPES 128
#define LUB_PROFILE_NAME_LEN 64
#define LUB_PROFILE_STACK_MAX 64

struct lua_State;

typedef struct ProfileScopeStats {
  char name[LUB_PROFILE_NAME_LEN];
  uint64_t total_ns;
  uint64_t max_ns;
  uint64_t calls;
  uint64_t alloc_bytes; // scope の中で Lua heap が確保した byte 数 (内側込み)
  uint64_t gc_ns; // scope の中の GC step の時間
} ProfileScopeStats;

typedef struct ProfileStackEntry {
  int scope_index;
  uint64_t start_ns;
  uint64_t alloc_start;
  uint64_t gc_ns_start;
} ProfileStackEntry;

// Lua heap の累計 (単調増加)。LUB_PROFILE の有効時だけ、profile_attach_lua
// が入れる allocator と GC step の hook が数える。frame と scope はこの値の
// 差を取る。
typedef struct ProfileHeapCounters {
  uint64_t alloc_bytes; // 確保した byte 数 (新しい block と realloc の増分)
  uint64_t allocs;     // 新しい block の数
  uint64_t free_bytes; // 返した byte 数 (free と realloc の縮小分)
  uint64_t gc_ns;      // GC step の時間
  uint64_t gc_steps;
  uint64_t gc_cycles; // 終わった GC の周回
} ProfileHeapCounters;

// .NET 実行の managed heap の累計値 (host が lub_host_profile_managed で渡す)。
typedef struct ProfileManagedSample {
  uint64_t alloc_bytes;
  uint64_t collections[3]; // 世代 0 / 1 / 2 の GC 回数
  uint64_t pause_ns;
} ProfileManagedSample;

typedef struct ProfileState {
  bool enabled;
  bool recording;
  bool in_frame; // 記録する frame の begin と end の間
  bool report_frame_done;
  bool auto_reported; // FRAME / EVERY の report を出した
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

  // Lua heap。allocator は元の allocator (luaL_newstate のもの) を包む。
  // 値は App と一緒に lua_close の後まで残る。
  void *(*lua_base_alloc)(void *ud, void *ptr, size_t osize, size_t nsize);
  void *lua_base_ud;
  bool lua_attached;
  int64_t lua_live; // allocator から見た今の byte 数
  ProfileHeapCounters heap;
  bool gc_step_open;
  uint64_t gc_step_start_ns;
  // window (frames と同じ区間) の集計。frame の外 (on_event / on_init /
  // frame の間) の確保は win_outside_alloc に分ける。
  ProfileHeapCounters frame_heap_start; // 今の frame の始まりの heap
  uint64_t gap_alloc_start; // 直前の frame_end の heap.alloc_bytes
  ProfileHeapCounters win_heap;
  uint64_t win_alloc_max;
  uint64_t win_gc_ns_max;      // frame ごとの GC 時間の最大
  uint64_t win_gc_step_max_ns; // GC step 1 回の最大
  uint64_t win_outside_alloc;

  // managed heap (.NET 実行)。
  bool managed_seen;
  ProfileManagedSample managed_last;
  uint64_t managed_frames;
  uint64_t managed_alloc_total;
  uint64_t managed_alloc_max;
  uint64_t managed_collections[3];
  uint64_t managed_pause_ns;
  uint64_t managed_pause_max_ns;
} ProfileState;

void profile_state_init(ProfileState *p);
void profile_reset(ProfileState *p);
void profile_frame_begin(ProfileState *p, uint64_t frame_index);
void profile_frame_end(ProfileState *p, uint64_t frame_index);
void profile_begin_scope(ProfileState *p, const char *name);
void profile_end_scope(ProfileState *p, const char *name);
void profile_report(ProfileState *p, const char *label);
// 終了時 (lub_host_destroy)。FRAME / EVERY の report がまだ出ていなければ
// label=exit で 1 回出す (短い実行でも数字が残るように)。
void profile_report_at_exit(ProfileState *p);

// 有効時だけ、L に数える allocator と GC step の hook を付ける。L を作った
// 直後 (luaL_newstate の後) に呼ぶ。
void profile_attach_lua(ProfileState *p, struct lua_State *L);
// GC step の hook を外す。Lua state を閉じた後、ProfileState を捨てる前に呼ぶ。
void profile_detach_lua(ProfileState *p);
// .NET 実行の host が渡す managed heap の累計値。直前の値との差を、記録中の
// frame の値として集計する。
void profile_managed_sample(ProfileState *p, const ProfileManagedSample *s);
