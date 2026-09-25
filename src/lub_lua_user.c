// lub_lua_user.h の定義。lua_static に入る。
#include "lua.h"

void (*lub_lua_gc_trace)(struct lua_State *L, int begin, int cycle_done) = NULL;
