// Lua (lua_static) の LUA_USER_H。lua.h が途中で include する。submodule を
// 書き換えずに、lgc.c の GC step の前後にある luai_tracegc を
// lub_lua_gc_trace へつなぐ (LUB_PROFILE の GC 時間と step 数)。
// CMake が lua_static にだけ LUA_USER_H を定義する。MSVC の /D は関数形式の
// macro を受け付けないので、macro はこの header に置く。
#pragma once

struct lua_State;

// GC step の始め (begin = 1) と終わり (begin = 0) に呼ばれる。cycle_done は
// その step で GC の 1 周が終わったとき 1。NULL なら何もしない (profile.c が
// LUB_PROFILE の有効時だけ入れる)。全量 GC (collectgarbage("collect") と
// 確保失敗時の緊急 GC) はここを通らない。定義は lub_lua_user.c (lua_static
// の中に置き、lub_objs を link しない smoke test でも解決できるようにする)。
extern void (*lub_lua_gc_trace)(struct lua_State *L, int begin, int cycle_done);

// 展開先は lgc.c の luaC_step で、G(L) と GCSpause はそこで見える。
#define luai_tracegc(L, f)                                                     \
  do {                                                                         \
    if (lub_lua_gc_trace)                                                      \
      lub_lua_gc_trace((L), (f), !(f) && G(L)->gcstate == GCSpause);           \
  } while (0)
