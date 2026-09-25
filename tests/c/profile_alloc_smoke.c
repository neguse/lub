// LUB_PROFILE の Lua heap の C smoke: 数える allocator (新しい block / realloc
// / free の数え方、Lua 自身の数との一致) と、GC step の hook (lua_static の
// LUA_USER_H がつながっていること)。window も GPU も要らない。
#include "lub_lua_user.h"
#include "profile.h"

#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>
#include <stdio.h>
#include <string.h>

static int g_failures;

static void check(bool cond, const char *msg) {
  if (!cond) {
    fprintf(stderr, "FAIL: %s\n", msg);
    g_failures++;
  }
}

static int64_t lua_count(lua_State *L) {
  return (int64_t)lua_gc(L, LUA_GCCOUNT) * 1024 + lua_gc(L, LUA_GCCOUNTB);
}

static void run(lua_State *L, const char *chunk) {
  if (luaL_dostring(L, chunk) != LUA_OK) {
    fprintf(stderr, "FAIL: chunk error: %s\n", lua_tostring(L, -1));
    g_failures++;
  }
  lua_settop(L, 0);
}

static void check_live(ProfileState *p, lua_State *L, const char *msg) {
  if (p->lua_live != lua_count(L)) {
    fprintf(stderr, "FAIL: %s: live=%lld lua=%lld\n", msg,
            (long long)p->lua_live, (long long)lua_count(L));
    g_failures++;
  }
}

// profile_state_init は LUB_PROFILE_* を読む。手元で export されていても同じ
// 条件で走るように、env から来る設定は消す
static void init_profile(ProfileState *p, bool enabled) {
  profile_state_init(p);
  p->enabled = enabled;
  p->start_frame = 0;
  p->report_frame = 0;
  p->report_every = 0;
  p->report_label[0] = '\0';
}

int main(void) {
  // 無効なら allocator も hook も付かない
  {
    ProfileState p;
    init_profile(&p, false);
    lua_State *L = luaL_newstate();
    lua_Alloc before = lua_getallocf(L, NULL);
    profile_attach_lua(&p, L);
    check(lua_getallocf(L, NULL) == before, "disabled: allocator unchanged");
    check(lub_lua_gc_trace == NULL, "disabled: no gc hook");
    lua_close(L);
  }

  ProfileState p;
  init_profile(&p, true);
  lua_State *L = luaL_newstate();
  profile_attach_lua(&p, L);
  check(p.lua_attached, "attached");
  check(lub_lua_gc_trace != NULL, "gc hook installed");
  check_live(&p, L, "live after attach");

  // allocator を直に呼ぶ。新しい block の osize は型の tag (大きさではない)
  {
    void *ud = NULL;
    lua_Alloc f = lua_getallocf(L, &ud);
    ProfileHeapCounters h0 = p.heap;
    int64_t live0 = p.lua_live;
    void *b = f(ud, NULL, LUA_TTABLE, 100);
    check(b != NULL, "alloc 100");
    check(p.heap.alloc_bytes - h0.alloc_bytes == 100, "new block counts nsize");
    check(p.heap.allocs - h0.allocs == 1, "new block counts one alloc");
    check(p.lua_live - live0 == 100, "live after new block");
    b = f(ud, b, 100, 250);
    check(p.heap.alloc_bytes - h0.alloc_bytes == 250, "grow counts the growth");
    check(p.heap.allocs - h0.allocs == 1, "realloc is not a new block");
    b = f(ud, b, 250, 50);
    check(p.heap.free_bytes - h0.free_bytes == 200, "shrink counts as free");
    check(p.heap.alloc_bytes - h0.alloc_bytes == 250, "shrink allocates 0");
    f(ud, b, 50, 0);
    check(p.heap.free_bytes - h0.free_bytes == 250, "free counts osize");
    check(p.lua_live == live0, "live back after free");
  }
  check_live(&p, L, "live after direct calls");

  luaL_openlibs(L);
  check_live(&p, L, "live after openlibs");

  // chunk の確保は Lua 自身の数と一致する (API の境目で)
  uint64_t a0 = p.heap.alloc_bytes;
  run(L, "keep = {} for i = 1, 1000 do keep[i] = { i } end");
  check(p.heap.alloc_bytes - a0 > 1000 * 16, "tables allocate");
  check_live(&p, L, "live after tables");

  // lauxlib の buffer から作る長い文字列は allocator を直に使うので、Lua の
  // 数には入らない。解放されれば一致に戻る
  run(L, "s = string.rep('x', 100000)");
  check(p.lua_live - lua_count(L) >= 100001, "buffer string is outside count");
  run(L, "s = nil collectgarbage()");
  check_live(&p, L, "live after buffer string is collected");

  // frame と scope の集計
  profile_frame_begin(&p, 0);
  profile_begin_scope(&p, "smoke.tables");
  run(L, "local t = {} for i = 1, 500 do t[i] = { i } end");
  profile_end_scope(&p, "smoke.tables");
  profile_begin_scope(&p, "smoke.idle");
  profile_end_scope(&p, "smoke.idle");
  profile_frame_end(&p, 0);
  check(p.frames == 1, "one frame");
  check(p.win_heap.alloc_bytes > 500 * 16, "frame alloc");
  check(p.win_alloc_max == p.win_heap.alloc_bytes, "frame alloc max");
  check(p.scope_count == 2, "two scopes");
  check(p.scopes[0].alloc_bytes > 500 * 16, "scope alloc");
  check(p.scopes[0].alloc_bytes <= p.win_heap.alloc_bytes,
        "scope alloc is inside the frame");
  check(p.scopes[1].alloc_bytes == 0, "idle scope allocates nothing");
  // frame の外の確保は outside に入り、frame の値には入らない
  uint64_t outside0 = p.win_outside_alloc;
  uint64_t frame_alloc0 = p.win_heap.alloc_bytes;
  run(L, "outside = {} for i = 1, 100 do outside[i] = { i } end");
  profile_frame_begin(&p, 1);
  profile_frame_end(&p, 1);
  check(p.win_outside_alloc - outside0 > 100 * 16, "outside alloc");
  check(p.win_heap.alloc_bytes == frame_alloc0, "empty frame allocates 0");

  // 20 MB ほどのごみで GC step が回る (LUA_USER_H の hook がつながっている)
  ProfileHeapCounters h1 = p.heap;
  run(L, "for i = 1, 200000 do local t = { i, i, i, i } end");
  check(p.heap.alloc_bytes - h1.alloc_bytes > 10 * 1024 * 1024,
        "garbage allocates");
  check(p.heap.gc_steps > h1.gc_steps, "gc steps counted");
  check(p.heap.gc_cycles > h1.gc_cycles, "gc cycles counted");
  check(p.heap.gc_ns > h1.gc_ns, "gc time counted");
  check(p.heap.free_bytes > h1.free_bytes, "gc frees");
  check(!p.gc_step_open, "no open gc step");
  check_live(&p, L, "live after gc");
  printf("profile alloc: alloc=%llu allocs=%llu free=%llu live=%lld "
         "gc_steps=%llu gc_cycles=%llu gc_ms=%.3f\n",
         (unsigned long long)p.heap.alloc_bytes,
         (unsigned long long)p.heap.allocs,
         (unsigned long long)p.heap.free_bytes, (long long)p.lua_live,
         (unsigned long long)p.heap.gc_steps,
         (unsigned long long)p.heap.gc_cycles, (double)p.heap.gc_ns / 1e6);

  // lua_close は全部を元の allocator 経由で返す。付ける前の確保も含めて 0 へ
  lua_close(L);
  check(p.lua_live == 0, "live is 0 after lua_close");
  profile_detach_lua(&p);
  check(lub_lua_gc_trace == NULL, "gc hook removed");

  if (g_failures) {
    fprintf(stderr, "profile alloc smoke: %d failure(s)\n", g_failures);
    return 1;
  }
  printf("profile alloc smoke OK\n");
  return 0;
}
