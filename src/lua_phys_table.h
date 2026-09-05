#pragma once
// phys2d / phys3d の Lua 面が共有する table の読み書き。
#include "lub/lub_api.h"
#include <SDL3/SDL.h>
#include <lauxlib.h>
#include <lua.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

static inline int abs_index(lua_State *L, int idx) {
  if (idx < 0)
    return lua_gettop(L) + idx + 1;
  return idx;
}

// field a (無ければ b) を push して true。無ければ何も push せず false。
static inline bool table_get_any(lua_State *L, int idx, const char *a,
                                 const char *b) {
  idx = abs_index(L, idx);
  lua_getfield(L, idx, a);
  if (!lua_isnil(L, -1))
    return true;
  lua_pop(L, 1);
  if (b) {
    lua_getfield(L, idx, b);
    if (!lua_isnil(L, -1))
      return true;
    lua_pop(L, 1);
  }
  return false;
}

static inline float table_number(lua_State *L, int idx, const char *a,
                                 const char *b, float def) {
  float out = def;
  if (table_get_any(L, idx, a, b)) {
    if (lua_isnumber(L, -1))
      out = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
  }
  return out;
}

static inline bool table_number_optional(lua_State *L, int idx, const char *a,
                                         const char *b, float *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_isnumber(L, -1);
  if (ok)
    *out = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return ok;
}

static inline int table_int(lua_State *L, int idx, const char *a, const char *b,
                            int def) {
  int out = def;
  if (table_get_any(L, idx, a, b)) {
    if (lua_isinteger(L, -1) || lua_isnumber(L, -1))
      out = (int)lua_tointeger(L, -1);
    lua_pop(L, 1);
  }
  return out;
}

static inline bool table_int_optional(lua_State *L, int idx, const char *a,
                                      const char *b, int *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_isinteger(L, -1) || lua_isnumber(L, -1);
  if (ok)
    *out = (int)lua_tointeger(L, -1);
  lua_pop(L, 1);
  return ok;
}

static inline bool table_bool(lua_State *L, int idx, const char *a,
                              const char *b, bool def) {
  bool out = def;
  if (table_get_any(L, idx, a, b)) {
    if (lua_isboolean(L, -1))
      out = lua_toboolean(L, -1) != 0;
    lua_pop(L, 1);
  }
  return out;
}

static inline bool table_bool_optional(lua_State *L, int idx, const char *a,
                                       const char *b, bool *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_isboolean(L, -1);
  if (ok)
    *out = lua_toboolean(L, -1) != 0;
  lua_pop(L, 1);
  return ok;
}

static inline bool table_has_int(lua_State *L, int idx, const char *a,
                                 const char *b, int32_t *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_isinteger(L, -1) || lua_isnumber(L, -1);
  if (ok)
    *out = (int32_t)lua_tointeger(L, -1);
  lua_pop(L, 1);
  return ok;
}

// 文字列 field への view。table が stack にある間だけ有効。
static inline LubStr table_str(lua_State *L, int idx, const char *a) {
  LubStr out = {NULL, 0};
  idx = abs_index(L, idx);
  lua_getfield(L, idx, a);
  if (lua_type(L, -1) == LUA_TSTRING) {
    size_t n = 0;
    out.ptr = lua_tolstring(L, -1, &n);
    out.len = (int32_t)n;
  }
  lua_pop(L, 1);
  return out;
}

static inline bool opt_wake(lua_State *L, int idx, bool def) {
  if (!lua_istable(L, idx))
    return def;
  return table_bool(L, idx, "wake", NULL, def);
}

static inline LubStr lstr_arg(lua_State *L, int idx) {
  size_t n = 0;
  const char *s = luaL_checklstring(L, idx, &n);
  LubStr r = {s, (int32_t)n};
  return r;
}

static inline void push_lstr(lua_State *L, LubStr s) {
  lua_pushlstring(L, s.ptr ? s.ptr : "", s.ptr ? (size_t)s.len : 0);
}

static inline bool lstr_empty(LubStr s) { return !s.ptr || s.len <= 0; }

static inline int push_not_found(lua_State *L) {
  lua_pushnil(L);
  lua_pushstring(L, "not found");
  return 2;
}

static inline bool is_ref(lua_State *L, int idx, const char *kind) {
  if (!lua_istable(L, idx))
    return false;
  idx = abs_index(L, idx);
  lua_getfield(L, idx, "__lub_kind");
  bool ok = lua_isstring(L, -1) && strcmp(lua_tostring(L, -1), kind) == 0;
  lua_pop(L, 1);
  return ok;
}

static inline LubStr ref_field(lua_State *L, int idx, const char *field) {
  idx = abs_index(L, idx);
  lua_getfield(L, idx, field);
  LubStr out = {NULL, 0};
  if (lua_type(L, -1) == LUA_TSTRING) {
    size_t n = 0;
    out.ptr = lua_tolstring(L, -1, &n);
    out.len = (int32_t)n;
  }
  lua_pop(L, 1);
  return out;
}

static inline void set_cfunc_field(lua_State *L, const char *name,
                                   lua_CFunction fn) {
  lua_pushcfunction(L, fn);
  lua_setfield(L, -2, name);
}

static inline void set_str_field(lua_State *L, const char *name, LubStr s) {
  push_lstr(L, s);
  lua_setfield(L, -2, name);
}

static inline void set_handle_field(lua_State *L, LubHandle h) {
  lua_pushinteger(L, (lua_Integer)h);
  lua_setfield(L, -2, "handle");
}

static inline void set_number(lua_State *L, const char *key, float value) {
  lua_pushnumber(L, value);
  lua_setfield(L, -2, key);
}

static inline void set_integer(lua_State *L, const char *key, int value) {
  lua_pushinteger(L, value);
  lua_setfield(L, -2, key);
}

static inline void set_boolean(lua_State *L, const char *key, bool value) {
  lua_pushboolean(L, value);
  lua_setfield(L, -2, key);
}

static inline void push_float_array(lua_State *L, const float *items,
                                    int32_t count) {
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    lua_pushnumber(L, items[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

// ---------------------------------------------------------------- filter

static inline uint64_t parse_hex_u64(lua_State *L, const char *ns,
                                     const char *s, const char *field_name) {
  uint64_t value = 0;
  const char *p = s;
  if (p[0] == '0' && (p[1] == 'x' || p[1] == 'X'))
    p += 2;
  if (*p == '\0')
    luaL_error(L, "%s filter %s: empty hex string", ns, field_name);
  while (*p) {
    char c = *p++;
    int d = 0;
    if (c >= '0' && c <= '9')
      d = c - '0';
    else if (c >= 'a' && c <= 'f')
      d = 10 + c - 'a';
    else if (c >= 'A' && c <= 'F')
      d = 10 + c - 'A';
    else
      luaL_error(L, "%s filter %s: invalid hex digit", ns, field_name);
    value = (value << 4) | (uint64_t)d;
  }
  return value;
}

static inline uint64_t parse_bit_list(lua_State *L, const char *ns, int idx,
                                      const char *field_name) {
  idx = abs_index(L, idx);
  uint64_t bits = 0;
  lua_getfield(L, idx, "length");
  int n = 0;
  bool zero_based = false;
  if (lua_isinteger(L, -1) || lua_isnumber(L, -1)) {
    n = (int)lua_tointeger(L, -1);
    zero_based = true;
  } else {
    n = (int)lua_rawlen(L, idx);
  }
  lua_pop(L, 1);
  for (int i = 0; i < n; ++i) {
    lua_Integer raw_index = zero_based ? i : i + 1;
    lua_rawgeti(L, idx, raw_index);
    int bit = (int)luaL_checkinteger(L, -1);
    lua_pop(L, 1);
    if (bit < 0 || bit > 63)
      luaL_error(L, "%s filter %s bit index out of range: %d", ns, field_name,
                 bit);
    bits |= (uint64_t)1 << bit;
  }
  return bits;
}

// { category, category_bits, mask ("all" / hex / bit list), mask_bits, group }
// を今の値の上に重ねる。group_index は NULL なら読まない。
static inline void parse_filter_table(lua_State *L, const char *ns, int f,
                                      uint64_t *category_bits,
                                      uint64_t *mask_bits,
                                      int32_t *group_index) {
  f = abs_index(L, f);
  if (table_get_any(L, f, "category", NULL)) {
    int bit = (int)luaL_checkinteger(L, -1);
    lua_pop(L, 1);
    if (bit < 0 || bit > 63)
      luaL_error(L, "%s filter category bit index out of range: %d", ns, bit);
    *category_bits = (uint64_t)1 << bit;
  }
  if (table_get_any(L, f, "category_bits", "categoryBits")) {
    if (lua_isstring(L, -1))
      *category_bits =
          parse_hex_u64(L, ns, lua_tostring(L, -1), "category_bits");
    lua_pop(L, 1);
  }
  if (table_get_any(L, f, "mask", NULL)) {
    if (lua_isstring(L, -1)) {
      const char *s = lua_tostring(L, -1);
      if (strcmp(s, "all") == 0) {
        *mask_bits = UINT64_MAX;
      } else {
        *mask_bits = parse_hex_u64(L, ns, s, "mask");
      }
    } else if (lua_istable(L, -1)) {
      *mask_bits = parse_bit_list(L, ns, lua_gettop(L), "mask");
    }
    lua_pop(L, 1);
  }
  if (table_get_any(L, f, "mask_bits", "maskBits")) {
    if (lua_isstring(L, -1))
      *mask_bits = parse_hex_u64(L, ns, lua_tostring(L, -1), "mask_bits");
    lua_pop(L, 1);
  }
  if (group_index)
    *group_index = table_int(L, f, "group", NULL, *group_index);
}

static inline void push_u64_hex_field(lua_State *L, const char *name,
                                      uint64_t value) {
  char buf[17];
  SDL_snprintf(buf, sizeof(buf), "%016llx", (unsigned long long)value);
  lua_pushstring(L, buf);
  lua_setfield(L, -2, name);
}

static inline int single_bit_index(uint64_t bits) {
  if (bits == 0 || (bits & (bits - 1)) != 0)
    return -1;
  for (int i = 0; i < 64; ++i) {
    if ((bits & ((uint64_t)1 << i)) != 0)
      return i;
  }
  return -1;
}

static inline void push_bit_indices(lua_State *L, uint64_t bits) {
  lua_newtable(L);
  int out = 1;
  for (int i = 0; i < 64; ++i) {
    if ((bits & ((uint64_t)1 << i)) != 0) {
      lua_pushinteger(L, i);
      lua_rawseti(L, -2, out++);
    }
  }
}

static inline void push_filter_bits(lua_State *L, uint64_t category_bits,
                                    uint64_t mask_bits, int32_t group_index) {
  push_u64_hex_field(L, "category_bits", category_bits);
  push_u64_hex_field(L, "mask_bits", mask_bits);
  int category = single_bit_index(category_bits);
  if (category >= 0) {
    lua_pushinteger(L, category);
    lua_setfield(L, -2, "category");
  }
  push_bit_indices(L, mask_bits);
  lua_setfield(L, -2, "mask");
  lua_pushinteger(L, group_index);
  lua_setfield(L, -2, "group");
  lua_pushinteger(L, group_index);
  lua_setfield(L, -2, "group_index");
}

// -------------------------------------------------------------- visitors

static inline bool query_result_is_string(lua_State *L, int idx,
                                          const char *s) {
  return lua_isstring(L, idx) && strcmp(lua_tostring(L, idx), s) == 0;
}

static inline bool parse_overlap_visitor_result(lua_State *L, int idx,
                                                bool *include) {
  if (lua_isboolean(L, idx) && !lua_toboolean(L, idx))
    return false;
  if (query_result_is_string(L, idx, "stop"))
    return false;
  if (query_result_is_string(L, idx, "ignore")) {
    *include = false;
    return true;
  }
  return true;
}

static inline float parse_raycast_visitor_result(lua_State *L, int idx,
                                                 float fraction,
                                                 bool *include) {
  if (lua_isnumber(L, idx))
    return (float)lua_tonumber(L, idx);
  if (lua_isboolean(L, idx)) {
    if (!lua_toboolean(L, idx))
      return 0.0f;
    return fraction;
  }
  if (lua_isnil(L, idx))
    return fraction;
  if (query_result_is_string(L, idx, "continue"))
    return 1.0f;
  if (query_result_is_string(L, idx, "ignore")) {
    *include = false;
    return -1.0f;
  }
  if (query_result_is_string(L, idx, "stop"))
    return 0.0f;
  if (query_result_is_string(L, idx, "clip"))
    return fraction;
  return fraction;
}

// visitor 付き query の共通部分: results table に hit を積みつつ Lua の
// 関数を呼ぶ。Lua 側の error は貯めて query を打ち切る。
typedef struct LuaPhysVisit {
  lua_State *L;
  int results_ref;
  int visitor_ref;
  int count;
  char *error;
} LuaPhysVisit;

static inline void visit_init(LuaPhysVisit *v, lua_State *L, int visitor_idx) {
  v->L = L;
  lua_newtable(L);
  v->results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  v->visitor_ref = LUA_NOREF;
  if (lua_isfunction(L, visitor_idx)) {
    lua_pushvalue(L, visitor_idx);
    v->visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  v->count = 0;
  v->error = NULL;
}

// 結果 (results table か nil, error) を push して ref を解放する。
static inline int visit_finish(LuaPhysVisit *v, const char *fn_name,
                               bool push_results, bool has_stats,
                               int32_t node_visits, int32_t leaf_visits) {
  lua_State *L = v->L;
  int nret = 0;
  if (v->error) {
    lua_pushnil(L);
    lua_pushfstring(L, "%s visitor: %s", fn_name, v->error);
    SDL_free(v->error);
    nret = 2;
  } else if (push_results) {
    lua_rawgeti(L, LUA_REGISTRYINDEX, v->results_ref);
    if (has_stats) {
      lua_pushinteger(L, node_visits);
      lua_setfield(L, -2, "node_visits");
      lua_pushinteger(L, leaf_visits);
      lua_setfield(L, -2, "leaf_visits");
    }
    nret = 1;
  }
  luaL_unref(L, LUA_REGISTRYINDEX, v->results_ref);
  if (v->visitor_ref != LUA_NOREF)
    luaL_unref(L, LUA_REGISTRYINDEX, v->visitor_ref);
  return nret;
}

// stack: results, item。visitor を呼ぶ。true なら結果が stack の上に残る
// (caller が pop)。error のときは stack を片付けて false。
static inline bool visit_call(LuaPhysVisit *v) {
  lua_State *L = v->L;
  if (v->visitor_ref == LUA_NOREF)
    return false;
  lua_rawgeti(L, LUA_REGISTRYINDEX, v->visitor_ref);
  lua_pushvalue(L, -2);
  if (lua_pcall(L, 1, 1, 0) != LUA_OK) {
    v->error =
        SDL_strdup(lua_tostring(L, -1) ? lua_tostring(L, -1) : "unknown error");
    lua_pop(L, 3);
    return false;
  }
  return true;
}

// stack: results, item。include なら results に積み、両方 pop する。
static inline void visit_store(LuaPhysVisit *v, bool include) {
  lua_State *L = v->L;
  if (include) {
    lua_rawseti(L, -2, ++v->count);
  } else {
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
}
