// phys2d の Lua 面。table を LubPhys2d* の desc に詰め、C API を呼び、結果を
// table に写す。参照は sentinel table { __lub_kind, key, world, body, handle }
// で、解決は key で行う (handle は情報)。
#include "lua_phys.h"

#include <SDL3/SDL.h>
#include <float.h>
#include <lauxlib.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

static LubContext *g_ctx = NULL;

// ------------------------------------------------------------ table read

static int abs_index(lua_State *L, int idx) {
  if (idx < 0)
    return lua_gettop(L) + idx + 1;
  return idx;
}

static bool table_get_any(lua_State *L, int idx, const char *a, const char *b) {
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

static float table_number(lua_State *L, int idx, const char *a, const char *b,
                          float def) {
  float out = def;
  if (table_get_any(L, idx, a, b)) {
    if (lua_isnumber(L, -1))
      out = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
  }
  return out;
}

static bool table_number_optional(lua_State *L, int idx, const char *a,
                                  const char *b, float *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_isnumber(L, -1);
  if (ok)
    *out = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return ok;
}

static int table_int(lua_State *L, int idx, const char *a, const char *b,
                     int def) {
  int out = def;
  if (table_get_any(L, idx, a, b)) {
    if (lua_isinteger(L, -1) || lua_isnumber(L, -1))
      out = (int)lua_tointeger(L, -1);
    lua_pop(L, 1);
  }
  return out;
}

static bool table_int_optional(lua_State *L, int idx, const char *a,
                               const char *b, int *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_isinteger(L, -1) || lua_isnumber(L, -1);
  if (ok)
    *out = (int)lua_tointeger(L, -1);
  lua_pop(L, 1);
  return ok;
}

static bool table_bool(lua_State *L, int idx, const char *a, const char *b,
                       bool def) {
  bool out = def;
  if (table_get_any(L, idx, a, b)) {
    if (lua_isboolean(L, -1))
      out = lua_toboolean(L, -1) != 0;
    lua_pop(L, 1);
  }
  return out;
}

static bool table_bool_optional(lua_State *L, int idx, const char *a,
                                const char *b, bool *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_isboolean(L, -1);
  if (ok)
    *out = lua_toboolean(L, -1) != 0;
  lua_pop(L, 1);
  return ok;
}

static bool table_has_int(lua_State *L, int idx, const char *a, const char *b,
                          int32_t *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_isinteger(L, -1) || lua_isnumber(L, -1);
  if (ok)
    *out = (int32_t)lua_tointeger(L, -1);
  lua_pop(L, 1);
  return ok;
}

// 文字列 field への view。table が stack にある間だけ有効。
static LubStr table_str(lua_State *L, int idx, const char *a) {
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

static LubVec2 read_vec2_at(lua_State *L, int t, LubVec2 out, bool *has_x,
                            bool *has_y) {
  lua_getfield(L, t, "x");
  if (lua_isnumber(L, -1)) {
    out.x = (float)lua_tonumber(L, -1);
    if (has_x)
      *has_x = true;
  }
  lua_pop(L, 1);
  lua_getfield(L, t, "y");
  if (lua_isnumber(L, -1)) {
    out.y = (float)lua_tonumber(L, -1);
    if (has_y)
      *has_y = true;
  }
  lua_pop(L, 1);
  lua_rawgeti(L, t, 1);
  if (lua_isnumber(L, -1)) {
    out.x = (float)lua_tonumber(L, -1);
    if (has_x)
      *has_x = true;
  }
  lua_pop(L, 1);
  lua_rawgeti(L, t, 2);
  if (lua_isnumber(L, -1)) {
    out.y = (float)lua_tonumber(L, -1);
    if (has_y)
      *has_y = true;
  }
  lua_pop(L, 1);
  return out;
}

static LubVec2 table_vec2(lua_State *L, int idx, const char *a, const char *b,
                          LubVec2 def) {
  LubVec2 out = def;
  if (!table_get_any(L, idx, a, b))
    return out;
  if (lua_istable(L, -1))
    out = read_vec2_at(L, lua_gettop(L), out, NULL, NULL);
  lua_pop(L, 1);
  return out;
}

static LubVec2 value_vec2(lua_State *L, int idx, LubVec2 def) {
  if (!lua_istable(L, idx))
    return def;
  return read_vec2_at(L, abs_index(L, idx), def, NULL, NULL);
}

static LubVec2 value_vec2_optional(lua_State *L, int idx, LubVec2 def,
                                   bool *has_x, bool *has_y) {
  if (!lua_istable(L, idx))
    return def;
  return read_vec2_at(L, abs_index(L, idx), def, has_x, has_y);
}

static bool opt_wake(lua_State *L, int idx, bool def) {
  if (!lua_istable(L, idx))
    return def;
  return table_bool(L, idx, "wake", NULL, def);
}

static LubStr lstr_arg(lua_State *L, int idx) {
  size_t n = 0;
  const char *s = luaL_checklstring(L, idx, &n);
  LubStr r = {s, (int32_t)n};
  return r;
}

static void push_lstr(lua_State *L, LubStr s) {
  lua_pushlstring(L, s.ptr ? s.ptr : "", s.ptr ? (size_t)s.len : 0);
}

static bool lstr_empty(LubStr s) { return !s.ptr || s.len <= 0; }

static int raise_last(lua_State *L) {
  return luaL_error(L, "%s", lub_last_error(g_ctx));
}

static int push_not_found(lua_State *L) {
  lua_pushnil(L);
  lua_pushstring(L, "not found");
  return 2;
}

// ------------------------------------------------------------------ refs

static bool is_ref(lua_State *L, int idx, const char *kind) {
  if (!lua_istable(L, idx))
    return false;
  idx = abs_index(L, idx);
  lua_getfield(L, idx, "__lub_kind");
  bool ok = lua_isstring(L, -1) && strcmp(lua_tostring(L, -1), kind) == 0;
  lua_pop(L, 1);
  return ok;
}

static LubStr ref_field(lua_State *L, int idx, const char *field) {
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

static void set_cfunc_field(lua_State *L, const char *name, lua_CFunction fn) {
  lua_pushcfunction(L, fn);
  lua_setfield(L, -2, name);
}

static void set_str_field(lua_State *L, const char *name, LubStr s) {
  push_lstr(L, s);
  lua_setfield(L, -2, name);
}

static void set_handle_field(lua_State *L, LubHandle h) {
  lua_pushinteger(L, (lua_Integer)h);
  lua_setfield(L, -2, "handle");
}

static int l_phys2d_begin(lua_State *L);
static int l_phys2d_step(lua_State *L);
static int l_phys2d_world_info(lua_State *L);
static int l_phys2d_body(lua_State *L);
static int l_phys2d_box(lua_State *L);
static int l_phys2d_circle(lua_State *L);
static int l_phys2d_capsule(lua_State *L);
static int l_phys2d_segment(lua_State *L);
static int l_phys2d_polygon(lua_State *L);
static int l_phys2d_chain(lua_State *L);
static int l_phys2d_chain_segments(lua_State *L);
static int l_phys2d_joint(lua_State *L);
static int l_phys2d_joint_info(lua_State *L);
static int l_phys2d_joint_force(lua_State *L);
static int l_phys2d_joint_torque(lua_State *L);
static int l_phys2d_joint_angle(lua_State *L);
static int l_phys2d_joint_translation(lua_State *L);
static int l_phys2d_joint_speed(lua_State *L);
static int l_phys2d_joint_length(lua_State *L);
static int l_phys2d_joint_motor_force(lua_State *L);
static int l_phys2d_joint_motor_torque(lua_State *L);
static int l_phys2d_joint_set_motor(lua_State *L);
static int l_phys2d_joint_set_limit(lua_State *L);
static int l_phys2d_joint_set_spring(lua_State *L);
static int l_phys2d_joint_set_target(lua_State *L);
static int l_phys2d_pose(lua_State *L);
static int l_phys2d_velocity(lua_State *L);
static int l_phys2d_mass(lua_State *L);
static int l_phys2d_center(lua_State *L);
static int l_phys2d_world_point(lua_State *L);
static int l_phys2d_local_point(lua_State *L);
static int l_phys2d_velocity_at(lua_State *L);
static int l_phys2d_body_shapes(lua_State *L);
static int l_phys2d_body_joints(lua_State *L);
static int l_phys2d_body_contacts(lua_State *L);
static int l_phys2d_shape_test_point(lua_State *L);
static int l_phys2d_shape_raycast(lua_State *L);
static int l_phys2d_shape_closest_point(lua_State *L);
static int l_phys2d_shape_aabb(lua_State *L);
static int l_phys2d_shape_info(lua_State *L);
static int l_phys2d_shape_set_material(lua_State *L);
static int l_phys2d_shape_set_filter(lua_State *L);
static int l_phys2d_shape_set_events(lua_State *L);
static int l_phys2d_contacts(lua_State *L);
static int l_phys2d_body_events(lua_State *L);
static int l_phys2d_sensors(lua_State *L);
static int l_phys2d_raycast(lua_State *L);
static int l_phys2d_overlap_aabb(lua_State *L);
static int l_phys2d_shape_cast(lua_State *L);
static int l_phys2d_cast_mover(lua_State *L);
static int l_phys2d_collide_mover(lua_State *L);
static int l_phys2d_explode(lua_State *L);
static int l_phys2d_debug(lua_State *L);
static int l_phys2d_profile(lua_State *L);
static int l_phys2d_counters(lua_State *L);
static int l_phys2d_add_force(lua_State *L);
static int l_phys2d_add_force_center(lua_State *L);
static int l_phys2d_add_impulse(lua_State *L);
static int l_phys2d_add_impulse_center(lua_State *L);
static int l_phys2d_add_torque(lua_State *L);
static int l_phys2d_add_angular_impulse(lua_State *L);
static int l_phys2d_set_velocity(lua_State *L);
static int l_phys2d_teleport(lua_State *L);
static int l_phys2d_set_target(lua_State *L);
static int l_phys2d_set_mass_data(lua_State *L);

static void push_world_ref(lua_State *L, LubStr key, LubHandle h) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_world");
  lua_setfield(L, -2, "__lub_kind");
  set_str_field(L, "key", key);
  set_handle_field(L, h);
  set_cfunc_field(L, "begin", l_phys2d_begin);
  set_cfunc_field(L, "step", l_phys2d_step);
  set_cfunc_field(L, "info", l_phys2d_world_info);
  set_cfunc_field(L, "body", l_phys2d_body);
  set_cfunc_field(L, "joint", l_phys2d_joint);
  set_cfunc_field(L, "pose", l_phys2d_pose);
  set_cfunc_field(L, "contacts", l_phys2d_contacts);
  set_cfunc_field(L, "body_events", l_phys2d_body_events);
  set_cfunc_field(L, "sensors", l_phys2d_sensors);
  set_cfunc_field(L, "raycast", l_phys2d_raycast);
  set_cfunc_field(L, "overlap_aabb", l_phys2d_overlap_aabb);
  set_cfunc_field(L, "shape_cast", l_phys2d_shape_cast);
  set_cfunc_field(L, "cast_mover", l_phys2d_cast_mover);
  set_cfunc_field(L, "collide_mover", l_phys2d_collide_mover);
  set_cfunc_field(L, "explode", l_phys2d_explode);
  set_cfunc_field(L, "debug_draw", l_phys2d_debug);
  set_cfunc_field(L, "profile", l_phys2d_profile);
  set_cfunc_field(L, "counters", l_phys2d_counters);
}

static void push_body_ref(lua_State *L, LubStr world_key, LubStr body_key,
                          LubHandle h) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_body");
  lua_setfield(L, -2, "__lub_kind");
  set_str_field(L, "world", world_key);
  set_str_field(L, "key", body_key);
  set_handle_field(L, h);
  set_cfunc_field(L, "box", l_phys2d_box);
  set_cfunc_field(L, "circle", l_phys2d_circle);
  set_cfunc_field(L, "capsule", l_phys2d_capsule);
  set_cfunc_field(L, "segment", l_phys2d_segment);
  set_cfunc_field(L, "polygon", l_phys2d_polygon);
  set_cfunc_field(L, "chain", l_phys2d_chain);
  set_cfunc_field(L, "pose", l_phys2d_pose);
  set_cfunc_field(L, "velocity", l_phys2d_velocity);
  set_cfunc_field(L, "mass", l_phys2d_mass);
  set_cfunc_field(L, "center", l_phys2d_center);
  set_cfunc_field(L, "world_point", l_phys2d_world_point);
  set_cfunc_field(L, "local_point", l_phys2d_local_point);
  set_cfunc_field(L, "velocity_at", l_phys2d_velocity_at);
  set_cfunc_field(L, "shapes", l_phys2d_body_shapes);
  set_cfunc_field(L, "joints", l_phys2d_body_joints);
  set_cfunc_field(L, "contacts", l_phys2d_body_contacts);
  set_cfunc_field(L, "add_force", l_phys2d_add_force);
  set_cfunc_field(L, "add_force_center", l_phys2d_add_force_center);
  set_cfunc_field(L, "add_impulse", l_phys2d_add_impulse);
  set_cfunc_field(L, "add_impulse_center", l_phys2d_add_impulse_center);
  set_cfunc_field(L, "add_torque", l_phys2d_add_torque);
  set_cfunc_field(L, "add_angular_impulse", l_phys2d_add_angular_impulse);
  set_cfunc_field(L, "set_velocity", l_phys2d_set_velocity);
  set_cfunc_field(L, "teleport", l_phys2d_teleport);
  set_cfunc_field(L, "set_target", l_phys2d_set_target);
  set_cfunc_field(L, "set_mass_data", l_phys2d_set_mass_data);
}

static void push_chain_ref(lua_State *L, LubStr world_key, LubStr body_key,
                           LubStr chain_key, LubHandle h) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_chain");
  lua_setfield(L, -2, "__lub_kind");
  set_str_field(L, "world", world_key);
  set_str_field(L, "body", body_key);
  set_str_field(L, "key", chain_key);
  set_handle_field(L, h);
  set_cfunc_field(L, "segments", l_phys2d_chain_segments);
}

static void push_shape_ref(lua_State *L, LubStr world_key, LubStr body_key,
                           LubStr shape_key, LubHandle h) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_shape");
  lua_setfield(L, -2, "__lub_kind");
  set_str_field(L, "world", world_key);
  set_str_field(L, "body", body_key);
  set_str_field(L, "key", shape_key);
  set_handle_field(L, h);
  set_cfunc_field(L, "test_point", l_phys2d_shape_test_point);
  set_cfunc_field(L, "raycast", l_phys2d_shape_raycast);
  set_cfunc_field(L, "closest_point", l_phys2d_shape_closest_point);
  set_cfunc_field(L, "aabb", l_phys2d_shape_aabb);
  set_cfunc_field(L, "info", l_phys2d_shape_info);
  set_cfunc_field(L, "set_material", l_phys2d_shape_set_material);
  set_cfunc_field(L, "set_filter", l_phys2d_shape_set_filter);
  set_cfunc_field(L, "set_events", l_phys2d_shape_set_events);
}

static void push_joint_ref(lua_State *L, LubStr world_key, LubStr joint_key,
                           LubHandle h) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_joint");
  lua_setfield(L, -2, "__lub_kind");
  set_str_field(L, "world", world_key);
  set_str_field(L, "key", joint_key);
  set_handle_field(L, h);
  set_cfunc_field(L, "info", l_phys2d_joint_info);
  set_cfunc_field(L, "force", l_phys2d_joint_force);
  set_cfunc_field(L, "torque", l_phys2d_joint_torque);
  set_cfunc_field(L, "angle", l_phys2d_joint_angle);
  set_cfunc_field(L, "translation", l_phys2d_joint_translation);
  set_cfunc_field(L, "speed", l_phys2d_joint_speed);
  set_cfunc_field(L, "length", l_phys2d_joint_length);
  set_cfunc_field(L, "motor_force", l_phys2d_joint_motor_force);
  set_cfunc_field(L, "motor_torque", l_phys2d_joint_motor_torque);
  set_cfunc_field(L, "set_motor", l_phys2d_joint_set_motor);
  set_cfunc_field(L, "set_limit", l_phys2d_joint_set_limit);
  set_cfunc_field(L, "set_spring", l_phys2d_joint_set_spring);
  set_cfunc_field(L, "set_target", l_phys2d_joint_set_target);
}

// sentinel を key で解決する。無ければ 0。kind 違いは error。
static LubHandle ref_world(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_world"))
    luaL_error(L, "expected Phys2d WorldRef");
  return lub_phys2d_world_find(g_ctx, ref_field(L, idx, "key"));
}

static LubHandle check_world(lua_State *L, int idx) {
  LubHandle h = ref_world(L, idx);
  if (!h)
    luaL_error(L, "phys2d world not found: %s",
               lua_tostring(L, idx) ? "" : "?");
  return h;
}

static LubHandle ref_body(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_body"))
    luaL_error(L, "expected Phys2d BodyRef");
  LubHandle w = lub_phys2d_world_find(g_ctx, ref_field(L, idx, "world"));
  return w ? lub_phys2d_body_find(g_ctx, w, ref_field(L, idx, "key")) : 0;
}

static LubHandle check_body(lua_State *L, int idx) {
  LubHandle h = ref_body(L, idx);
  if (!h) {
    LubStr w = ref_field(L, idx, "world");
    LubStr k = ref_field(L, idx, "key");
    luaL_error(L, "phys2d body not found: %s/%s", w.ptr ? w.ptr : "?",
               k.ptr ? k.ptr : "?");
  }
  return h;
}

static LubHandle ref_shape(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_shape"))
    luaL_error(L, "expected Phys2d ShapeRef");
  LubHandle w = lub_phys2d_world_find(g_ctx, ref_field(L, idx, "world"));
  LubHandle b =
      w ? lub_phys2d_body_find(g_ctx, w, ref_field(L, idx, "body")) : 0;
  return b ? lub_phys2d_shape_find(g_ctx, b, ref_field(L, idx, "key")) : 0;
}

static LubHandle check_shape(lua_State *L, int idx) {
  LubHandle h = ref_shape(L, idx);
  if (!h) {
    LubStr w = ref_field(L, idx, "world");
    LubStr b = ref_field(L, idx, "body");
    LubStr k = ref_field(L, idx, "key");
    luaL_error(L, "phys2d shape not found: %s/%s/%s", w.ptr ? w.ptr : "?",
               b.ptr ? b.ptr : "?", k.ptr ? k.ptr : "?");
  }
  return h;
}

static LubHandle ref_chain(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_chain"))
    luaL_error(L, "expected Phys2d ChainRef");
  LubHandle w = lub_phys2d_world_find(g_ctx, ref_field(L, idx, "world"));
  LubHandle b =
      w ? lub_phys2d_body_find(g_ctx, w, ref_field(L, idx, "body")) : 0;
  return b ? lub_phys2d_chain_find(g_ctx, b, ref_field(L, idx, "key")) : 0;
}

static LubHandle ref_joint(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_joint"))
    luaL_error(L, "expected Phys2d JointRef");
  LubHandle w = lub_phys2d_world_find(g_ctx, ref_field(L, idx, "world"));
  return w ? lub_phys2d_joint_find(g_ctx, w, ref_field(L, idx, "key")) : 0;
}

static LubHandle check_joint(lua_State *L, int idx) {
  LubHandle h = ref_joint(L, idx);
  if (!h) {
    LubStr w = ref_field(L, idx, "world");
    LubStr k = ref_field(L, idx, "key");
    luaL_error(L, "phys2d joint not found: %s/%s", w.ptr ? w.ptr : "?",
               k.ptr ? k.ptr : "?");
  }
  return h;
}

// ------------------------------------------------------------- callbacks
// world ごとの Lua closure。生存期間は次の world 宣言まで (runtime 側は step
// の終わりで callback を外すので、それ以降は呼ばれない)。

typedef struct LuaCallbacks {
  char *world_key;
  lua_State *L;
  int filter_ref;
  int pre_solve_ref;
  int friction_ref;
  int restitution_ref;
  bool filter_logged;
  bool pre_solve_logged;
  bool friction_logged;
  bool restitution_logged;
  struct LuaCallbacks *next;
} LuaCallbacks;

static LuaCallbacks *g_callbacks = NULL;

static void callbacks_unref(LuaCallbacks *cb, lua_State *L) {
  if (cb->L == L) {
    if (cb->filter_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, cb->filter_ref);
    if (cb->pre_solve_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, cb->pre_solve_ref);
    if (cb->friction_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, cb->friction_ref);
    if (cb->restitution_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, cb->restitution_ref);
  }
  cb->filter_ref = cb->pre_solve_ref = cb->friction_ref = cb->restitution_ref =
      LUA_NOREF;
  cb->filter_logged = cb->pre_solve_logged = cb->friction_logged =
      cb->restitution_logged = false;
  cb->L = L;
}

static LuaCallbacks *callbacks_for(lua_State *L, LubStr key) {
  for (LuaCallbacks *cb = g_callbacks; cb; cb = cb->next) {
    if (strlen(cb->world_key) == (size_t)key.len &&
        memcmp(cb->world_key, key.ptr, (size_t)key.len) == 0) {
      callbacks_unref(cb, L);
      return cb;
    }
  }
  LuaCallbacks *cb = (LuaCallbacks *)calloc(1, sizeof(*cb));
  if (!cb)
    return NULL;
  cb->world_key = (char *)malloc((size_t)key.len + 1);
  if (!cb->world_key) {
    free(cb);
    return NULL;
  }
  memcpy(cb->world_key, key.ptr, (size_t)key.len);
  cb->world_key[key.len] = '\0';
  cb->filter_ref = cb->pre_solve_ref = cb->friction_ref = cb->restitution_ref =
      LUA_NOREF;
  cb->L = L;
  cb->next = g_callbacks;
  g_callbacks = cb;
  return cb;
}

static int store_callback(lua_State *L, int idx, const char *a, const char *b) {
  int ref = LUA_NOREF;
  if (!table_get_any(L, idx, a, b))
    return ref;
  if (lua_isfunction(L, -1)) {
    lua_pushvalue(L, -1);
    ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  lua_pop(L, 1);
  return ref;
}

static void callback_log_error_once(bool *logged, const char *name,
                                    lua_State *L) {
  if (*logged)
    return;
  const char *message = lua_tostring(L, -1);
  SDL_Log("phys2d %s callback error: %s", name,
          message ? message : "unknown error");
  *logged = true;
}

static void push_shape_part(lua_State *L, const LubPhys2dShapePart *p,
                            bool with_kind);

static bool cb_filter(void *user, const LubPhys2dShapePart *a,
                      const LubPhys2dShapePart *b) {
  LuaCallbacks *cb = (LuaCallbacks *)user;
  lua_State *L = cb->L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, cb->filter_ref);
  push_shape_part(L, a, false);
  push_shape_part(L, b, false);
  if (lua_pcall(L, 2, 1, 0) != LUA_OK) {
    callback_log_error_once(&cb->filter_logged, "filter", L);
    lua_settop(L, top);
    return true;
  }
  bool collide = !lua_isboolean(L, -1) || lua_toboolean(L, -1) != 0;
  lua_settop(L, top);
  return collide;
}

static void push_manifold_point(lua_State *L, const LubPhys2dManifoldPoint *p) {
  lua_newtable(L);
  lua_pushnumber(L, p->x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, p->y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, p->anchor_a_x);
  lua_setfield(L, -2, "anchor_a_x");
  lua_pushnumber(L, p->anchor_a_y);
  lua_setfield(L, -2, "anchor_a_y");
  lua_pushnumber(L, p->anchor_b_x);
  lua_setfield(L, -2, "anchor_b_x");
  lua_pushnumber(L, p->anchor_b_y);
  lua_setfield(L, -2, "anchor_b_y");
  lua_pushnumber(L, p->separation);
  lua_setfield(L, -2, "separation");
  lua_pushnumber(L, p->normal_impulse);
  lua_setfield(L, -2, "normal_impulse");
  lua_pushnumber(L, p->tangent_impulse);
  lua_setfield(L, -2, "tangent_impulse");
  lua_pushnumber(L, p->total_normal_impulse);
  lua_setfield(L, -2, "total_normal_impulse");
  lua_pushnumber(L, p->normal_velocity);
  lua_setfield(L, -2, "normal_velocity");
  lua_pushinteger(L, p->id);
  lua_setfield(L, -2, "id");
  lua_pushboolean(L, p->persisted);
  lua_setfield(L, -2, "persisted");
}

static bool cb_pre_solve(void *user, const LubPhys2dPreSolve *c) {
  LuaCallbacks *cb = (LuaCallbacks *)user;
  lua_State *L = cb->L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, cb->pre_solve_ref);
  lua_newtable(L);
  push_shape_part(L, &c->a, false);
  lua_setfield(L, -2, "a");
  push_shape_part(L, &c->b, false);
  lua_setfield(L, -2, "b");
  lua_pushnumber(L, c->nx);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, c->ny);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, c->rolling_impulse);
  lua_setfield(L, -2, "rolling_impulse");
  lua_pushinteger(L, c->point_count);
  lua_setfield(L, -2, "point_count");
  lua_newtable(L);
  for (int i = 0; i < c->point_count; ++i) {
    push_manifold_point(L, &c->points[i]);
    lua_rawseti(L, -2, i + 1);
  }
  lua_setfield(L, -2, "points");
  if (c->point_count > 0) {
    lua_pushnumber(L, c->points[0].x);
    lua_setfield(L, -2, "x");
    lua_pushnumber(L, c->points[0].y);
    lua_setfield(L, -2, "y");
    lua_pushnumber(L, c->points[0].separation);
    lua_setfield(L, -2, "separation");
    lua_pushnumber(L, c->points[0].normal_velocity);
    lua_setfield(L, -2, "normal_velocity");
  }
  if (lua_pcall(L, 1, 1, 0) != LUA_OK) {
    callback_log_error_once(&cb->pre_solve_logged, "pre_solve", L);
    lua_settop(L, top);
    return true;
  }
  bool solve = !lua_isboolean(L, -1) || lua_toboolean(L, -1) != 0;
  lua_settop(L, top);
  return solve;
}

static void push_material_view(lua_State *L, const char *field, float value,
                               int material) {
  lua_newtable(L);
  lua_pushnumber(L, value);
  lua_setfield(L, -2, field);
  lua_pushinteger(L, material);
  lua_setfield(L, -2, "material");
}

static float call_mixer(LuaCallbacks *cb, int ref, bool *logged,
                        const char *name, const char *field, float a,
                        int32_t ma, float b, int32_t mb, float fallback) {
  lua_State *L = cb->L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, ref);
  push_material_view(L, field, a, ma);
  push_material_view(L, field, b, mb);
  if (lua_pcall(L, 2, 1, 0) != LUA_OK) {
    callback_log_error_once(logged, name, L);
    lua_settop(L, top);
    return fallback;
  }
  float out = lua_isnumber(L, -1) ? (float)lua_tonumber(L, -1) : fallback;
  lua_settop(L, top);
  return out;
}

static float cb_friction(void *user, float fa, int32_t ma, float fb,
                         int32_t mb) {
  LuaCallbacks *cb = (LuaCallbacks *)user;
  float fallback = sqrtf((fa < 0 ? 0 : fa) * (fb < 0 ? 0 : fb));
  return call_mixer(cb, cb->friction_ref, &cb->friction_logged, "friction",
                    "friction", fa, ma, fb, mb, fallback);
}

static float cb_restitution(void *user, float ra, int32_t ma, float rb,
                            int32_t mb) {
  LuaCallbacks *cb = (LuaCallbacks *)user;
  float fallback = ra > rb ? ra : rb;
  return call_mixer(cb, cb->restitution_ref, &cb->restitution_logged,
                    "restitution", "restitution", ra, ma, rb, mb, fallback);
}

// opts.callbacks を読んで desc に入れる。無ければ desc の callbacks は空。
static void parse_callbacks(lua_State *L, int opts_idx, LubStr key,
                            LubPhys2dCallbacks *out) {
  memset(out, 0, sizeof(*out));
  LuaCallbacks *cb = callbacks_for(L, key);
  if (!cb)
    luaL_error(L, "phys2d_world: out of memory");
  if (!lua_istable(L, opts_idx))
    return;
  if (!table_get_any(L, opts_idx, "callbacks", NULL))
    return;
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return;
  }
  int cidx = lua_gettop(L);
  cb->filter_ref = store_callback(L, cidx, "filter", NULL);
  cb->pre_solve_ref = store_callback(L, cidx, "pre_solve", "preSolve");
  cb->friction_ref = store_callback(L, cidx, "friction", NULL);
  cb->restitution_ref = store_callback(L, cidx, "restitution", NULL);
  lua_pop(L, 1);
  out->user = cb;
  if (cb->filter_ref != LUA_NOREF)
    out->filter = cb_filter;
  if (cb->pre_solve_ref != LUA_NOREF)
    out->pre_solve = cb_pre_solve;
  if (cb->friction_ref != LUA_NOREF)
    out->friction = cb_friction;
  if (cb->restitution_ref != LUA_NOREF)
    out->restitution = cb_restitution;
}

// ----------------------------------------------------------------- world

static void parse_world_desc(lua_State *L, int idx, LubPhys2dWorldDesc *d) {
  lub_phys2d_world_desc_init(d);
  if (!lua_istable(L, idx))
    return;
  int32_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    d->version = v;
    d->has_version = true;
  }
  LubVec2 g = table_vec2(L, idx, "gravity", NULL,
                         (LubVec2){d->gravity_x, d->gravity_y});
  d->gravity_x = g.x;
  d->gravity_y = g.y;
  d->fixed_dt = table_number(L, idx, "fixed_dt", "fixedDt", d->fixed_dt);
  d->substeps = table_int(L, idx, "substeps", NULL, d->substeps);
  d->max_steps = table_int(L, idx, "max_steps", "maxSteps", d->max_steps);
  d->sleep = table_bool(L, idx, "sleep", NULL, d->sleep);
  d->continuous = table_bool(L, idx, "continuous", NULL, d->continuous);
  float t = 0;
  if (table_number_optional(L, idx, "hit_event_threshold", "hitEventThreshold",
                            &t)) {
    d->has_hit_event_threshold = true;
    d->hit_event_threshold = t;
  }
  if (d->fixed_dt <= 0.0f)
    d->fixed_dt = 1.0f / 60.0f;
  if (d->substeps <= 0)
    d->substeps = 4;
  if (d->max_steps <= 0)
    d->max_steps = 4;
}

static int l_phys2d_world(lua_State *L) {
  LubStr key = lstr_arg(L, 1);
  LubPhys2dWorldDesc d;
  parse_world_desc(L, 2, &d);
  parse_callbacks(L, 2, key, &d.callbacks);
  LubHandle h = 0;
  if (lub_phys2d_world(g_ctx, key, &d, &h) != LUB_OK)
    return raise_last(L);
  push_world_ref(L, key, h);
  return 1;
}

static int l_phys2d_begin(lua_State *L) {
  LubHandle w = check_world(L, 1);
  bool prune = lua_istable(L, 2) ? table_bool(L, 2, "prune", NULL, true) : true;
  if (lub_phys2d_begin(g_ctx, w, prune) != LUB_OK)
    return raise_last(L);
  return 0;
}

static void push_vec2(lua_State *L, float x, float y) {
  lua_newtable(L);
  lua_pushnumber(L, x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, y);
  lua_setfield(L, -2, "y");
}

static int l_phys2d_world_info(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  LubPhys2dWorldInfo info;
  LubStatus st = lub_phys2d_world_info(g_ctx, w, &info);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  set_str_field(L, "key", info.key);
  lua_pushboolean(L, info.valid);
  lua_setfield(L, -2, "valid");
  lua_pushinteger(L, info.version);
  lua_setfield(L, -2, "version");
  lua_pushinteger(L, info.generation);
  lua_setfield(L, -2, "generation");
  lua_pushboolean(L, info.begun);
  lua_setfield(L, -2, "begun");
  lua_pushboolean(L, info.prune);
  lua_setfield(L, -2, "prune");
  lua_pushnumber(L, info.fixed_dt);
  lua_setfield(L, -2, "fixed_dt");
  lua_pushinteger(L, info.substeps);
  lua_setfield(L, -2, "substeps");
  lua_pushinteger(L, info.max_steps);
  lua_setfield(L, -2, "max_steps");
  lua_pushnumber(L, info.accumulator);
  lua_setfield(L, -2, "accumulator");
  lua_pushinteger(L, info.pending_commands);
  lua_setfield(L, -2, "pending_commands");
  lua_newtable(L);
  lua_pushboolean(L, info.callback_filter);
  lua_setfield(L, -2, "filter");
  lua_pushboolean(L, info.callback_pre_solve);
  lua_setfield(L, -2, "pre_solve");
  lua_pushboolean(L, info.callback_friction);
  lua_setfield(L, -2, "friction");
  lua_pushboolean(L, info.callback_restitution);
  lua_setfield(L, -2, "restitution");
  lua_setfield(L, -2, "callbacks");
  if (!info.valid)
    return 1;
  push_vec2(L, info.gravity_x, info.gravity_y);
  lua_setfield(L, -2, "gravity");
  lua_pushnumber(L, info.gravity_x);
  lua_setfield(L, -2, "gx");
  lua_pushnumber(L, info.gravity_y);
  lua_setfield(L, -2, "gy");
  lua_pushboolean(L, info.sleep);
  lua_setfield(L, -2, "sleep");
  lua_pushboolean(L, info.continuous);
  lua_setfield(L, -2, "continuous");
  lua_pushboolean(L, info.warm_starting);
  lua_setfield(L, -2, "warm_starting");
  lua_pushnumber(L, info.restitution_threshold);
  lua_setfield(L, -2, "restitution_threshold");
  lua_pushnumber(L, info.hit_event_threshold);
  lua_setfield(L, -2, "hit_event_threshold");
  lua_pushnumber(L, info.maximum_linear_speed);
  lua_setfield(L, -2, "maximum_linear_speed");
  lua_pushinteger(L, info.awake_body_count);
  lua_setfield(L, -2, "awake_body_count");
  return 1;
}

// ------------------------------------------------------------------ body

static void parse_body_desc(lua_State *L, int idx, LubPhys2dBodyDesc *d) {
  lub_phys2d_body_desc_init(d);
  luaL_checktype(L, idx, LUA_TTABLE);
  int32_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    d->version = v;
    d->has_version = true;
  }
  if (table_get_any(L, idx, "type", NULL)) {
    d->type = (int32_t)luaL_checkinteger(L, -1);
    lua_pop(L, 1);
  }
  d->fixed_rotation =
      table_bool(L, idx, "fixed_rotation", "fixedRotation", false);
  d->bullet = table_bool(L, idx, "bullet", NULL, false);
  bool flag = false;
  if (table_bool_optional(L, idx, "enabled", NULL, &flag)) {
    d->enabled = flag;
    d->has_enabled = true;
  }
  if (table_bool_optional(L, idx, "awake", NULL, &flag)) {
    d->awake = flag;
    d->has_awake = true;
  }
  if (table_bool_optional(L, idx, "sleep", "enableSleep", &flag)) {
    d->sleep = flag;
    d->has_sleep = true;
  }
  d->gravity_scale =
      table_number(L, idx, "gravity_scale", "gravityScale", 1.0f);
  d->linear_damping =
      table_number(L, idx, "linear_damping", "linearDamping", 0.0f);
  d->angular_damping =
      table_number(L, idx, "angular_damping", "angularDamping", 0.0f);
  float t = 0;
  if (table_number_optional(L, idx, "sleep_threshold", "sleepThreshold", &t)) {
    d->sleep_threshold = t;
    d->has_sleep_threshold = true;
  }
  if (table_get_any(L, idx, "initial", NULL)) {
    if (lua_istable(L, -1)) {
      int t2 = lua_gettop(L);
      d->x = table_number(L, t2, "x", NULL, d->x);
      d->y = table_number(L, t2, "y", NULL, d->y);
      d->angle = table_number(L, t2, "angle", NULL, d->angle);
      d->vx = table_number(L, t2, "vx", NULL, d->vx);
      d->vy = table_number(L, t2, "vy", NULL, d->vy);
      d->w = table_number(L, t2, "w", NULL, d->w);
      d->initial_awake = table_bool(L, t2, "awake", NULL, d->initial_awake);
    }
    lua_pop(L, 1);
  }
}

static int l_phys2d_body(lua_State *L) {
  LubHandle w = check_world(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys2dBodyDesc d;
  parse_body_desc(L, 3, &d);
  LubHandle h = 0;
  if (lub_phys2d_body(g_ctx, w, key, &d, &h) != LUB_OK)
    return raise_last(L);
  push_body_ref(L, ref_field(L, 1, "key"), key, h);
  return 1;
}

// ---------------------------------------------------------------- filter

static uint64_t parse_hex_u64(lua_State *L, const char *s,
                              const char *field_name) {
  uint64_t value = 0;
  const char *p = s;
  if (p[0] == '0' && (p[1] == 'x' || p[1] == 'X'))
    p += 2;
  if (*p == '\0')
    luaL_error(L, "phys2d filter %s: empty hex string", field_name);
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
      luaL_error(L, "phys2d filter %s: invalid hex digit", field_name);
    value = (value << 4) | (uint64_t)d;
  }
  return value;
}

static uint64_t parse_bit_list(lua_State *L, int idx, const char *field_name) {
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
      luaL_error(L, "phys2d filter %s bit index out of range: %d", field_name,
                 bit);
    bits |= (uint64_t)1 << bit;
  }
  return bits;
}

static void parse_filter_table(lua_State *L, int f, uint64_t *category_bits,
                               uint64_t *mask_bits, int32_t *group_index) {
  f = abs_index(L, f);
  if (table_get_any(L, f, "category", NULL)) {
    int bit = (int)luaL_checkinteger(L, -1);
    lua_pop(L, 1);
    if (bit < 0 || bit > 63)
      luaL_error(L, "phys2d filter category bit index out of range: %d", bit);
    *category_bits = (uint64_t)1 << bit;
  }
  if (table_get_any(L, f, "category_bits", "categoryBits")) {
    if (lua_isstring(L, -1))
      *category_bits = parse_hex_u64(L, lua_tostring(L, -1), "category_bits");
    lua_pop(L, 1);
  }
  if (table_get_any(L, f, "mask", NULL)) {
    if (lua_isstring(L, -1)) {
      const char *s = lua_tostring(L, -1);
      if (strcmp(s, "all") == 0) {
        *mask_bits = UINT64_MAX;
      } else {
        *mask_bits = parse_hex_u64(L, s, "mask");
      }
    } else if (lua_istable(L, -1)) {
      *mask_bits = parse_bit_list(L, lua_gettop(L), "mask");
    }
    lua_pop(L, 1);
  }
  if (table_get_any(L, f, "mask_bits", "maskBits")) {
    if (lua_isstring(L, -1))
      *mask_bits = parse_hex_u64(L, lua_tostring(L, -1), "mask_bits");
    lua_pop(L, 1);
  }
  if (group_index)
    *group_index = table_int(L, f, "group", NULL, *group_index);
}

// desc.filter = { ... } があれば上書きする。
static void parse_filter_field(lua_State *L, int idx, LubPhys2dFilter *f) {
  if (!table_get_any(L, idx, "filter", NULL))
    return;
  if (lua_istable(L, -1))
    parse_filter_table(L, lua_gettop(L), &f->category_bits, &f->mask_bits,
                       &f->group_index);
  lua_pop(L, 1);
}

static LubPhys2dQueryFilter parse_query_filter(lua_State *L, int idx) {
  LubPhys2dQueryFilter f = {1u, UINT64_MAX};
  if (!lua_istable(L, idx))
    return f;
  if (table_get_any(L, idx, "filter", NULL)) {
    if (lua_istable(L, -1))
      parse_filter_table(L, lua_gettop(L), &f.category_bits, &f.mask_bits,
                         NULL);
    lua_pop(L, 1);
  } else {
    parse_filter_table(L, idx, &f.category_bits, &f.mask_bits, NULL);
  }
  return f;
}

// ----------------------------------------------------------------- shape

static void parse_shape_desc(lua_State *L, int idx, LubPhys2dShapeDesc *d) {
  lub_phys2d_shape_desc_init(d);
  luaL_checktype(L, idx, LUA_TTABLE);
  int32_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    d->version = v;
    d->has_version = true;
  }
  d->has_density = table_number_optional(L, idx, "density", NULL, &d->density);
  d->friction = table_number(L, idx, "friction", NULL, d->friction);
  d->restitution = table_number(L, idx, "restitution", NULL, d->restitution);
  d->material_id = table_int(L, idx, "material", "materialId", d->material_id);
  d->material_id = table_int(L, idx, "material_id", NULL, d->material_id);
  d->material_id =
      table_int(L, idx, "user_material_id", "userMaterialId", d->material_id);
  d->sensor = table_bool(L, idx, "sensor", NULL, d->sensor);
  d->contact = table_bool(L, idx, "contact", NULL, d->contact);
  d->hit = table_bool(L, idx, "hit", NULL, d->hit);
  d->sensor_events =
      table_bool(L, idx, "sensor_events", "sensorEvents", d->sensor_events);
  d->pre_solve = table_bool(L, idx, "pre_solve", "preSolve", d->pre_solve);
  parse_filter_field(L, idx, &d->filter);
  d->tag = table_str(L, idx, "tag");
  d->material_name = table_str(L, idx, "material");
}

static int push_shape_result(lua_State *L, LubStatus st, LubStr key,
                             LubHandle h) {
  if (st != LUB_OK)
    return raise_last(L);
  push_shape_ref(L, ref_field(L, 1, "world"), ref_field(L, 1, "key"), key, h);
  return 1;
}

static int l_phys2d_box(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys2dBoxDesc d;
  parse_shape_desc(L, 3, &d.shape);
  d.hx = table_number(L, 3, "hx", NULL, 0.0f);
  d.hy = table_number(L, 3, "hy", NULL, 0.0f);
  d.cx = table_number(L, 3, "cx", NULL, 0.0f);
  d.cy = table_number(L, 3, "cy", NULL, 0.0f);
  d.angle = table_number(L, 3, "angle", NULL, 0.0f);
  LubHandle h = 0;
  return push_shape_result(L, lub_phys2d_box(g_ctx, b, key, &d, &h), key, h);
}

static int l_phys2d_circle(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys2dCircleDesc d;
  parse_shape_desc(L, 3, &d.shape);
  d.r = table_number(L, 3, "r", NULL, 0.0f);
  d.cx = table_number(L, 3, "cx", NULL, 0.0f);
  d.cy = table_number(L, 3, "cy", NULL, 0.0f);
  LubHandle h = 0;
  return push_shape_result(L, lub_phys2d_circle(g_ctx, b, key, &d, &h), key, h);
}

static int l_phys2d_capsule(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys2dCapsuleDesc d;
  parse_shape_desc(L, 3, &d.shape);
  d.ax = table_number(L, 3, "ax", "x1", 0.0f);
  d.ay = table_number(L, 3, "ay", "y1", 0.0f);
  d.bx = table_number(L, 3, "bx", "x2", 0.0f);
  d.by = table_number(L, 3, "by", "y2", 0.0f);
  d.r = table_number(L, 3, "r", NULL, 0.0f);
  LubHandle h = 0;
  return push_shape_result(L, lub_phys2d_capsule(g_ctx, b, key, &d, &h), key,
                           h);
}

static int l_phys2d_segment(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys2dSegmentDesc d;
  parse_shape_desc(L, 3, &d.shape);
  d.ax = table_number(L, 3, "ax", "x1", 0.0f);
  d.ay = table_number(L, 3, "ay", "y1", 0.0f);
  d.bx = table_number(L, 3, "bx", "x2", 0.0f);
  d.by = table_number(L, 3, "by", "y2", 0.0f);
  LubHandle h = 0;
  return push_shape_result(L, lub_phys2d_segment(g_ctx, b, key, &d, &h), key,
                           h);
}

// points = { x, y, x, y, ... } か { {x, y}, ... }。stack の table は残す。
// 返り値は点の数。points は SDL_malloc した x, y の列 (caller が free)。
static float *read_point_list(lua_State *L, int idx, int min_points,
                              int max_points, const char *fn_name,
                              int *out_count) {
  if (!table_get_any(L, idx, "points", NULL))
    luaL_error(L, "%s: points table is required", fn_name);
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "%s: points must be a table", fn_name);
  }
  int pidx = lua_gettop(L);
  int raw_len = (int)lua_rawlen(L, pidx);
  bool flat_numbers = false;
  if (raw_len > 0) {
    lua_rawgeti(L, pidx, 1);
    flat_numbers = lua_isnumber(L, -1);
    lua_pop(L, 1);
  }
  if (flat_numbers && (raw_len & 1) != 0)
    luaL_error(L, "%s: flat points must have x/y pairs", fn_name);
  int count = flat_numbers ? raw_len / 2 : raw_len;
  if (count < min_points)
    luaL_error(L, "%s: at least %d points are required", fn_name, min_points);
  if (max_points > 0 && count > max_points)
    luaL_error(L, "%s: at most %d points are supported", fn_name, max_points);
  float *points = (float *)SDL_malloc(sizeof(float) * 2 * (size_t)count);
  if (!points)
    luaL_error(L, "%s: out of memory", fn_name);
  if (flat_numbers) {
    for (int i = 0; i < count * 2; ++i) {
      lua_rawgeti(L, pidx, i + 1);
      points[i] = (float)luaL_checknumber(L, -1);
      lua_pop(L, 1);
    }
  } else {
    for (int i = 0; i < count; ++i) {
      lua_rawgeti(L, pidx, i + 1);
      luaL_checktype(L, -1, LUA_TTABLE);
      LubVec2 p = value_vec2(L, lua_gettop(L), (LubVec2){0, 0});
      points[i * 2] = p.x;
      points[i * 2 + 1] = p.y;
      lua_pop(L, 1);
    }
  }
  lua_pop(L, 1);
  *out_count = count;
  return points;
}

static int l_phys2d_polygon(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys2dPolygonDesc d;
  parse_shape_desc(L, 3, &d.shape);
  int count = 0;
  float *points = read_point_list(L, 3, 3, 8, "phys2d_polygon", &count);
  d.points = points;
  d.point_count = count;
  d.radius = table_number(L, 3, "radius", "r", 0.0f);
  d.cx = table_number(L, 3, "cx", NULL, 0.0f);
  d.cy = table_number(L, 3, "cy", NULL, 0.0f);
  d.angle = table_number(L, 3, "angle", NULL, 0.0f);
  LubHandle h = 0;
  LubStatus st = lub_phys2d_polygon(g_ctx, b, key, &d, &h);
  SDL_free(points);
  return push_shape_result(L, st, key, h);
}

static int l_phys2d_chain(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  LubPhys2dChainDesc d;
  lub_phys2d_chain_desc_init(&d);
  int32_t version = 0;
  if (!table_has_int(L, 3, "version", NULL, &version))
    return luaL_error(L, "phys2d_chain: explicit version is required");
  d.version = version;
  int count = 0;
  float *points = read_point_list(L, 3, 4, 0, "phys2d_chain", &count);
  d.points = points;
  d.point_count = count;
  d.loop = table_bool(L, 3, "loop", NULL, false);
  d.friction = table_number(L, 3, "friction", NULL, 0.6f);
  d.restitution = table_number(L, 3, "restitution", NULL, 0.0f);
  d.material_id = table_int(L, 3, "material", "materialId", 0);
  d.material_id =
      table_int(L, 3, "user_material_id", "userMaterialId", d.material_id);
  d.sensor_events = table_bool(L, 3, "sensor_events", "sensorEvents", false);
  parse_filter_field(L, 3, &d.filter);
  d.tag = table_str(L, 3, "tag");
  d.material_name = table_str(L, 3, "material");
  LubPhys2dSurfaceMaterial *materials = NULL;
  if (table_get_any(L, 3, "materials", NULL)) {
    if (!lua_istable(L, -1)) {
      SDL_free(points);
      return luaL_error(L, "phys2d_chain: materials must be a table");
    }
    int midx = lua_gettop(L);
    int mcount = (int)lua_rawlen(L, midx);
    if (mcount != 1 && mcount != count) {
      SDL_free(points);
      return luaL_error(
          L, "phys2d_chain: materials length must be 1 or point count");
    }
    materials = (LubPhys2dSurfaceMaterial *)SDL_malloc(sizeof(*materials) *
                                                       (size_t)mcount);
    if (!materials) {
      SDL_free(points);
      return luaL_error(L, "phys2d_chain: out of memory");
    }
    for (int i = 0; i < mcount; ++i) {
      materials[i].friction = d.friction;
      materials[i].restitution = d.restitution;
      materials[i].material_id = d.material_id;
      lua_rawgeti(L, midx, i + 1);
      if (lua_istable(L, -1)) {
        int m = lua_gettop(L);
        materials[i].friction =
            table_number(L, m, "friction", NULL, materials[i].friction);
        materials[i].restitution =
            table_number(L, m, "restitution", NULL, materials[i].restitution);
        materials[i].material_id =
            table_int(L, m, "material", "materialId", materials[i].material_id);
        materials[i].material_id =
            table_int(L, m, "user_material_id", "userMaterialId",
                      materials[i].material_id);
      } else if (lua_isinteger(L, -1) || lua_isnumber(L, -1)) {
        materials[i].material_id = (int)lua_tointeger(L, -1);
      }
      lua_pop(L, 1);
    }
    lua_pop(L, 1);
    d.materials = materials;
    d.material_count = mcount;
  }
  LubHandle h = 0;
  LubStatus st = lub_phys2d_chain(g_ctx, b, key, &d, &h);
  SDL_free(points);
  SDL_free(materials);
  if (st != LUB_OK)
    return raise_last(L);
  push_chain_ref(L, ref_field(L, 1, "world"), ref_field(L, 1, "key"), key, h);
  return 1;
}

// ----------------------------------------------------------------- joint

static int32_t parse_joint_kind(lua_State *L, int idx) {
  const char *type = "revolute";
  if (table_get_any(L, idx, "type", "kind")) {
    if (lua_isstring(L, -1))
      type = lua_tostring(L, -1);
    lua_pop(L, 1);
  }
  if (strcmp(type, "distance") == 0)
    return LUB_PHYS2D_JOINT_TYPE_DISTANCE;
  if (strcmp(type, "filter") == 0)
    return LUB_PHYS2D_JOINT_TYPE_FILTER;
  if (strcmp(type, "motor") == 0)
    return LUB_PHYS2D_JOINT_TYPE_MOTOR;
  if (strcmp(type, "mouse") == 0)
    return LUB_PHYS2D_JOINT_TYPE_MOUSE;
  if (strcmp(type, "prismatic") == 0)
    return LUB_PHYS2D_JOINT_TYPE_PRISMATIC;
  if (strcmp(type, "revolute") == 0 || strcmp(type, "hinge") == 0)
    return LUB_PHYS2D_JOINT_TYPE_REVOLUTE;
  if (strcmp(type, "weld") == 0)
    return LUB_PHYS2D_JOINT_TYPE_WELD;
  if (strcmp(type, "wheel") == 0)
    return LUB_PHYS2D_JOINT_TYPE_WHEEL;
  luaL_error(L, "phys2d_joint: unknown joint type '%s'", type);
  return LUB_PHYS2D_JOINT_TYPE_REVOLUTE;
}

static const char *joint_kind_name(int32_t kind) {
  switch (kind) {
  case LUB_PHYS2D_JOINT_TYPE_DISTANCE:
    return "distance";
  case LUB_PHYS2D_JOINT_TYPE_FILTER:
    return "filter";
  case LUB_PHYS2D_JOINT_TYPE_MOTOR:
    return "motor";
  case LUB_PHYS2D_JOINT_TYPE_MOUSE:
    return "mouse";
  case LUB_PHYS2D_JOINT_TYPE_PRISMATIC:
    return "prismatic";
  case LUB_PHYS2D_JOINT_TYPE_REVOLUTE:
    return "revolute";
  case LUB_PHYS2D_JOINT_TYPE_WELD:
    return "weld";
  case LUB_PHYS2D_JOINT_TYPE_WHEEL:
    return "wheel";
  default:
    return "unknown";
  }
}

static float nested_number(lua_State *L, int idx, const char *table_name,
                           const char *a, const char *b, float def) {
  float out = def;
  if (table_get_any(L, idx, table_name, NULL)) {
    if (lua_istable(L, -1))
      out = table_number(L, lua_gettop(L), a, b, out);
    lua_pop(L, 1);
  }
  return out;
}

static bool nested_bool(lua_State *L, int idx, const char *table_name,
                        const char *a, const char *b, bool def) {
  bool out = def;
  if (table_get_any(L, idx, table_name, NULL)) {
    if (lua_istable(L, -1))
      out = table_bool(L, lua_gettop(L), a, b, out);
    lua_pop(L, 1);
  }
  return out;
}

static LubVec2 nested_vec2(lua_State *L, int idx, const char *table_name,
                           const char *a, const char *b, LubVec2 def) {
  LubVec2 out = def;
  if (table_get_any(L, idx, table_name, NULL)) {
    if (lua_istable(L, -1))
      out = table_vec2(L, lua_gettop(L), a, b, out);
    lua_pop(L, 1);
  }
  return out;
}

// BodyRef か key 文字列。無ければ 0 (C API が "missing body field" にする)。
static LubHandle joint_body_from_value(lua_State *L, LubHandle w, int idx,
                                       const char *field_name) {
  if (is_ref(L, idx, "phys2d_body"))
    return check_body(L, idx);
  if (lua_isstring(L, idx)) {
    size_t n = 0;
    const char *s = lua_tolstring(L, idx, &n);
    LubStr key = {s, (int32_t)n};
    return lub_phys2d_body_find(g_ctx, w, key);
  }
  return luaL_error(L, "phys2d_joint: missing body field '%s'", field_name), 0;
}

static LubHandle joint_body_field(lua_State *L, LubHandle w, int idx,
                                  const char *a, const char *b, const char *c) {
  if (!table_get_any(L, idx, a, b)) {
    if (!c || !table_get_any(L, idx, c, NULL))
      luaL_error(L, "phys2d_joint: missing body field '%s'", a);
  }
  LubHandle body = joint_body_from_value(L, w, lua_gettop(L), a);
  lua_pop(L, 1);
  return body;
}

static LubVec2 joint_vec2(lua_State *L, int idx, const char *a, const char *b,
                          const char *c, const char *d, LubVec2 def) {
  LubVec2 out = table_vec2(L, idx, a, b, def);
  if (c)
    out = table_vec2(L, idx, c, d, out);
  return out;
}

static void parse_joint_desc(lua_State *L, LubHandle w, int idx,
                             LubPhys2dJointDesc *d) {
  luaL_checktype(L, idx, LUA_TTABLE);
  lub_phys2d_joint_desc_init(d);
  d->type = parse_joint_kind(L, idx);
  int32_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    d->version = v;
    d->has_version = true;
  }
  d->body_a = joint_body_field(L, w, idx, "a", "body_a", "bodyA");
  d->body_b = joint_body_field(L, w, idx, "b", "body_b", "bodyB");
  d->local_anchor_a =
      joint_vec2(L, idx, "anchor_a", "anchorA", "local_anchor_a",
                 "localAnchorA", d->local_anchor_a);
  d->local_anchor_b =
      joint_vec2(L, idx, "anchor_b", "anchorB", "local_anchor_b",
                 "localAnchorB", d->local_anchor_b);
  d->local_axis_a = joint_vec2(L, idx, "axis", NULL, "local_axis_a",
                               "localAxisA", d->local_axis_a);
  d->linear_offset = joint_vec2(L, idx, "linear_offset", "linearOffset", NULL,
                                NULL, d->linear_offset);
  d->target = joint_vec2(L, idx, "target", NULL, NULL, NULL, d->target);
  d->reference_angle = table_number(L, idx, "reference_angle", "referenceAngle",
                                    d->reference_angle);
  d->length = table_number(L, idx, "length", NULL, d->length);
  d->min_length =
      table_number(L, idx, "min_length", "minLength", d->min_length);
  d->max_length =
      table_number(L, idx, "max_length", "maxLength", d->max_length);
  d->lower = table_number(L, idx, "lower", NULL, d->lower);
  d->upper = table_number(L, idx, "upper", NULL, d->upper);
  d->target_angle =
      table_number(L, idx, "target_angle", "targetAngle", d->target_angle);
  d->target_translation = table_number(
      L, idx, "target_translation", "targetTranslation", d->target_translation);
  d->angular_offset = table_number(L, idx, "angular_offset", "angularOffset",
                                   d->angular_offset);
  d->hertz = table_number(L, idx, "hertz", NULL, d->hertz);
  d->damping_ratio =
      table_number(L, idx, "damping_ratio", "dampingRatio", d->damping_ratio);
  d->linear_hertz =
      table_number(L, idx, "linear_hertz", "linearHertz", d->linear_hertz);
  d->angular_hertz =
      table_number(L, idx, "angular_hertz", "angularHertz", d->angular_hertz);
  d->linear_damping_ratio =
      table_number(L, idx, "linear_damping_ratio", "linearDampingRatio",
                   d->linear_damping_ratio);
  d->angular_damping_ratio =
      table_number(L, idx, "angular_damping_ratio", "angularDampingRatio",
                   d->angular_damping_ratio);
  d->max_force = table_number(L, idx, "max_force", "maxForce", d->max_force);
  d->max_torque =
      table_number(L, idx, "max_torque", "maxTorque", d->max_torque);
  d->motor_speed =
      table_number(L, idx, "motor_speed", "motorSpeed", d->motor_speed);
  d->correction_factor = table_number(L, idx, "correction_factor",
                                      "correctionFactor", d->correction_factor);
  d->draw_size = table_number(L, idx, "draw_size", "drawSize", d->draw_size);
  d->collide_connected = table_bool(L, idx, "collide_connected",
                                    "collideConnected", d->collide_connected);
  d->enable_spring =
      table_bool(L, idx, "enable_spring", "enableSpring", d->enable_spring);
  d->enable_limit =
      table_bool(L, idx, "enable_limit", "enableLimit", d->enable_limit);
  d->enable_motor =
      table_bool(L, idx, "enable_motor", "enableMotor", d->enable_motor);

  d->enable_spring =
      nested_bool(L, idx, "spring", "enabled", NULL, d->enable_spring);
  d->hertz = nested_number(L, idx, "spring", "hertz", NULL, d->hertz);
  d->damping_ratio = nested_number(L, idx, "spring", "damping_ratio",
                                   "dampingRatio", d->damping_ratio);
  d->target_angle = nested_number(L, idx, "spring", "target_angle",
                                  "targetAngle", d->target_angle);
  d->target_translation =
      nested_number(L, idx, "spring", "target_translation", "targetTranslation",
                    d->target_translation);

  d->enable_limit =
      nested_bool(L, idx, "limit", "enabled", NULL, d->enable_limit);
  d->lower = nested_number(L, idx, "limit", "lower", NULL, d->lower);
  d->upper = nested_number(L, idx, "limit", "upper", NULL, d->upper);
  d->min_length =
      nested_number(L, idx, "limit", "min", "min_length", d->min_length);
  d->max_length =
      nested_number(L, idx, "limit", "max", "max_length", d->max_length);

  d->enable_motor =
      nested_bool(L, idx, "motor", "enabled", NULL, d->enable_motor);
  d->motor_speed =
      nested_number(L, idx, "motor", "speed", NULL, d->motor_speed);
  d->max_force =
      nested_number(L, idx, "motor", "max_force", "maxForce", d->max_force);
  d->max_torque =
      nested_number(L, idx, "motor", "max_torque", "maxTorque", d->max_torque);
  d->linear_offset = nested_vec2(L, idx, "motor", "linear_offset",
                                 "linearOffset", d->linear_offset);
  d->angular_offset = nested_number(L, idx, "motor", "angular_offset",
                                    "angularOffset", d->angular_offset);
}

static int l_phys2d_joint(lua_State *L) {
  LubHandle w = check_world(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys2dJointDesc d;
  parse_joint_desc(L, w, 3, &d);
  LubHandle h = 0;
  if (lub_phys2d_joint(g_ctx, w, key, &d, &h) != LUB_OK)
    return raise_last(L);
  push_joint_ref(L, ref_field(L, 1, "key"), key, h);
  return 1;
}

static void push_joint_view(lua_State *L, const LubPhys2dJointView *v) {
  lua_newtable(L);
  set_str_field(L, "joint", v->key);
  lua_pushstring(L, joint_kind_name(v->type));
  lua_setfield(L, -2, "type");
  set_str_field(L, "a", v->a);
  set_str_field(L, "b", v->b);
  lua_pushboolean(L, v->valid);
  lua_setfield(L, -2, "valid");
}

static int l_phys2d_joint_info(lua_State *L) {
  LubHandle j = ref_joint(L, 1);
  LubPhys2dJointInfo info;
  LubStatus st = lub_phys2d_joint_info(g_ctx, j, &info);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  push_joint_view(L, &info.view);
  lua_pushboolean(L, info.collide_connected);
  lua_setfield(L, -2, "collide_connected");
  push_vec2(L, info.force_x, info.force_y);
  lua_setfield(L, -2, "force");
  lua_pushnumber(L, info.torque);
  lua_setfield(L, -2, "torque");
  lua_pushnumber(L, info.linear_separation);
  lua_setfield(L, -2, "linear_separation");
  lua_pushnumber(L, info.angular_separation);
  lua_setfield(L, -2, "angular_separation");
  if (info.has_local_anchors) {
    push_vec2(L, info.local_anchor_a.x, info.local_anchor_a.y);
    lua_setfield(L, -2, "local_anchor_a");
    push_vec2(L, info.local_anchor_b.x, info.local_anchor_b.y);
    lua_setfield(L, -2, "local_anchor_b");
  }
  if (info.has_local_axis) {
    push_vec2(L, info.local_axis_a.x, info.local_axis_a.y);
    lua_setfield(L, -2, "local_axis_a");
  }
  if (info.has_reference_angle) {
    lua_pushnumber(L, info.reference_angle);
    lua_setfield(L, -2, "reference_angle");
  }
  return 1;
}

static int l_phys2d_joint_force(lua_State *L) {
  LubHandle j = ref_joint(L, 1);
  float x = 0, y = 0;
  LubStatus st = lub_phys2d_joint_force(g_ctx, j, &x, &y);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  push_vec2(L, x, y);
  return 1;
}

static int l_phys2d_joint_torque(lua_State *L) {
  LubHandle j = ref_joint(L, 1);
  float t = 0;
  LubStatus st = lub_phys2d_joint_torque(g_ctx, j, &t);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  lua_pushnumber(L, t);
  return 1;
}

typedef LubStatus (*JointMeasureFn)(LubContext *, LubHandle, float *, bool *);

static int joint_measure(lua_State *L, JointMeasureFn fn) {
  LubHandle j = ref_joint(L, 1);
  float v = 0;
  bool has = false;
  LubStatus st = fn(g_ctx, j, &v, &has);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (!has) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, v);
  return 1;
}

static int l_phys2d_joint_angle(lua_State *L) {
  return joint_measure(L, lub_phys2d_joint_angle);
}

static int l_phys2d_joint_translation(lua_State *L) {
  return joint_measure(L, lub_phys2d_joint_translation);
}

static int l_phys2d_joint_speed(lua_State *L) {
  return joint_measure(L, lub_phys2d_joint_speed);
}

static int l_phys2d_joint_length(lua_State *L) {
  return joint_measure(L, lub_phys2d_joint_length);
}

static int l_phys2d_joint_motor_force(lua_State *L) {
  return joint_measure(L, lub_phys2d_joint_motor_force);
}

static int l_phys2d_joint_motor_torque(lua_State *L) {
  return joint_measure(L, lub_phys2d_joint_motor_torque);
}

static int l_phys2d_joint_set_motor(lua_State *L) {
  LubHandle j = check_joint(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dJointMotor d = {0};
  d.enabled = table_bool(L, 2, "enabled", NULL, true);
  d.speed = table_number(L, 2, "speed", "motor_speed", 0.0f);
  d.max_force = table_number(L, 2, "max_force", "maxForce", 1.0f);
  d.max_torque = table_number(L, 2, "max_torque", "maxTorque", 1.0f);
  d.has_correction_factor = table_number_optional(
      L, 2, "correction_factor", "correctionFactor", &d.correction_factor);
  if (lub_phys2d_joint_set_motor(g_ctx, j, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_joint_set_limit(lua_State *L) {
  LubHandle j = check_joint(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dJointLimit d = {0};
  d.enabled = table_bool(L, 2, "enabled", NULL, true);
  d.lower = table_number(L, 2, "lower", NULL, 0.0f);
  d.upper = table_number(L, 2, "upper", NULL, 1.0f);
  d.min_length = table_number(L, 2, "min", "min_length", 0.0f);
  d.max_length = table_number(L, 2, "max", "max_length", 1.0f);
  if (lub_phys2d_joint_set_limit(g_ctx, j, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_joint_set_spring(lua_State *L) {
  LubHandle j = check_joint(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dJointSpring d = {0};
  d.enabled = table_bool(L, 2, "enabled", NULL, true);
  d.hertz = table_number(L, 2, "hertz", NULL, 0.0f);
  d.damping_ratio = table_number(L, 2, "damping_ratio", "dampingRatio", 0.0f);
  d.linear_hertz = table_number(L, 2, "linear_hertz", "linearHertz", d.hertz);
  d.linear_damping_ratio = table_number(L, 2, "linear_damping_ratio",
                                        "linearDampingRatio", d.damping_ratio);
  d.angular_hertz =
      table_number(L, 2, "angular_hertz", "angularHertz", d.hertz);
  d.angular_damping_ratio = table_number(
      L, 2, "angular_damping_ratio", "angularDampingRatio", d.damping_ratio);
  if (lub_phys2d_joint_set_spring(g_ctx, j, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_joint_set_target(lua_State *L) {
  LubHandle j = check_joint(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dJointTarget d = {0};
  bool has_x = false, has_y = false;
  if (table_get_any(L, 2, "target", NULL)) {
    if (lua_istable(L, -1)) {
      LubVec2 t =
          read_vec2_at(L, lua_gettop(L), (LubVec2){0, 0}, &has_x, &has_y);
      d.x = t.x;
      d.y = t.y;
    }
    lua_pop(L, 1);
  }
  float v = 0;
  if (table_number_optional(L, 2, "x", NULL, &v)) {
    d.x = v;
    has_x = true;
  }
  if (table_number_optional(L, 2, "y", NULL, &v)) {
    d.y = v;
    has_y = true;
  }
  d.has_x = has_x;
  d.has_y = has_y;
  d.has_translation = table_number_optional(
      L, 2, "translation", "target_translation", &d.translation);
  d.has_angle = table_number_optional(L, 2, "angle", "target_angle", &d.angle);
  if (table_get_any(L, 2, "linear_offset", "linearOffset")) {
    if (lua_istable(L, -1)) {
      LubVec2 o = read_vec2_at(L, lua_gettop(L), (LubVec2){0, 0}, NULL, NULL);
      d.linear_offset_x = o.x;
      d.linear_offset_y = o.y;
      d.has_linear_offset = true;
    }
    lua_pop(L, 1);
  }
  d.has_angular_offset = table_number_optional(
      L, 2, "angular_offset", "angularOffset", &d.angular_offset);
  if (lub_phys2d_joint_set_target(g_ctx, j, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

// -------------------------------------------------------------- commands

static bool read_point_opt(lua_State *L, int idx, LubVec2 *point) {
  if (!lua_istable(L, idx))
    return false;
  bool has = false;
  if (table_get_any(L, idx, "point", NULL)) {
    if (lua_istable(L, -1)) {
      *point = value_vec2(L, lua_gettop(L), *point);
      has = true;
    }
    lua_pop(L, 1);
  }
  float out = 0.0f;
  if (table_number_optional(L, idx, "px", NULL, &out)) {
    point->x = out;
    has = true;
  }
  if (table_number_optional(L, idx, "py", NULL, &out)) {
    point->y = out;
    has = true;
  }
  return has;
}

static int vector_command(lua_State *L, bool with_point,
                          LubStatus (*fn_point)(LubContext *, LubHandle, float,
                                                float, const LubVec2 *, bool),
                          LubStatus (*fn_center)(LubContext *, LubHandle, float,
                                                 float, bool)) {
  LubHandle b = check_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubVec2 v = value_vec2(L, 2, (LubVec2){0, 0});
  bool wake = opt_wake(L, 3, true);
  LubStatus st;
  if (with_point) {
    LubVec2 point = {0, 0};
    // 省略時の点は重心。値が要るので先に取る。
    float cx = 0, cy = 0;
    lub_phys2d_center(g_ctx, b, &cx, &cy);
    point = (LubVec2){cx, cy};
    bool has = read_point_opt(L, 3, &point);
    st = fn_point(g_ctx, b, v.x, v.y, has ? &point : NULL, wake);
  } else {
    st = fn_center(g_ctx, b, v.x, v.y, wake);
  }
  if (st != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_add_force(lua_State *L) {
  return vector_command(L, true, lub_phys2d_add_force, NULL);
}

static int l_phys2d_add_force_center(lua_State *L) {
  return vector_command(L, false, NULL, lub_phys2d_add_force_center);
}

static int l_phys2d_add_impulse(lua_State *L) {
  return vector_command(L, true, lub_phys2d_add_impulse, NULL);
}

static int l_phys2d_add_impulse_center(lua_State *L) {
  return vector_command(L, false, NULL, lub_phys2d_add_impulse_center);
}

static int l_phys2d_add_torque(lua_State *L) {
  LubHandle b = check_body(L, 1);
  float t = (float)luaL_checknumber(L, 2);
  if (lub_phys2d_add_torque(g_ctx, b, t, opt_wake(L, 3, true)) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_add_angular_impulse(lua_State *L) {
  LubHandle b = check_body(L, 1);
  float t = (float)luaL_checknumber(L, 2);
  if (lub_phys2d_add_angular_impulse(g_ctx, b, t, opt_wake(L, 3, true)) !=
      LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_set_velocity(lua_State *L) {
  LubHandle b = check_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dSetVelocity d = {0};
  LubVec2 v = value_vec2_optional(L, 2, (LubVec2){0, 0}, &d.has_vx, &d.has_vy);
  d.vx = v.x;
  d.vy = v.y;
  float out = 0.0f;
  if (table_number_optional(L, 2, "vx", NULL, &out)) {
    d.vx = out;
    d.has_vx = true;
  }
  if (table_number_optional(L, 2, "vy", NULL, &out)) {
    d.vy = out;
    d.has_vy = true;
  }
  if (table_number_optional(L, 2, "w", NULL, &out)) {
    d.w = out;
    d.has_w = true;
  }
  d.wake = opt_wake(L, 3, true);
  if (lub_phys2d_set_velocity(g_ctx, b, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_teleport(lua_State *L) {
  LubHandle b = check_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dTeleport d = {0};
  d.has_x = table_number_optional(L, 2, "x", NULL, &d.x);
  d.has_y = table_number_optional(L, 2, "y", NULL, &d.y);
  d.has_angle = table_number_optional(L, 2, "angle", NULL, &d.angle);
  d.wake = opt_wake(L, 3, true);
  if (lub_phys2d_teleport(g_ctx, b, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_set_target(lua_State *L) {
  LubHandle b = check_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dSetTarget d = {0};
  d.has_x = table_number_optional(L, 2, "x", NULL, &d.x);
  d.has_y = table_number_optional(L, 2, "y", NULL, &d.y);
  d.has_angle = table_number_optional(L, 2, "angle", NULL, &d.angle);
  d.time_step = 0.0f;
  if (lua_istable(L, 3)) {
    d.time_step = table_number(L, 3, "dt", NULL, d.time_step);
    d.time_step = table_number(L, 3, "time_step", "timeStep", d.time_step);
  }
  d.wake = opt_wake(L, 3, true);
  if (lub_phys2d_set_target(g_ctx, b, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_set_mass_data(lua_State *L) {
  LubHandle b = check_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dMassData cur;
  if (lub_phys2d_mass(g_ctx, b, &cur) != LUB_OK)
    return luaL_error(L, "phys2d_set_mass_data: body is not live");
  LubPhys2dMassDataDesc d;
  d.mass = table_number(L, 2, "mass", NULL, cur.mass);
  d.inertia = table_number(L, 2, "rotational_inertia", "rotationalInertia",
                           cur.inertia);
  d.inertia = table_number(L, 2, "inertia", NULL, d.inertia);
  LubVec2 c = {cur.local_center_x, cur.local_center_y};
  c = table_vec2(L, 2, "local_center", "localCenter", c);
  c = table_vec2(L, 2, "center", NULL, c);
  c.x = table_number(L, 2, "cx", NULL, c.x);
  c.y = table_number(L, 2, "cy", NULL, c.y);
  d.center_x = c.x;
  d.center_y = c.y;
  if (lub_phys2d_set_mass_data(g_ctx, b, &d, opt_wake(L, 3, true)) != LUB_OK)
    return raise_last(L);
  return 0;
}

// ------------------------------------------------------------------ step

static int l_phys2d_step(lua_State *L) {
  LubHandle w = check_world(L, 1);
  float dt = (float)luaL_checknumber(L, 2);
  LubPhys2dStepInfo info;
  if (lub_phys2d_step(g_ctx, w, dt, &info) != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  lua_pushinteger(L, info.steps);
  lua_setfield(L, -2, "steps");
  lua_pushinteger(L, info.commands);
  lua_setfield(L, -2, "commands");
  lua_pushnumber(L, info.alpha);
  lua_setfield(L, -2, "alpha");
  lua_pushboolean(L, info.dropped);
  lua_setfield(L, -2, "dropped");
  lua_pushinteger(L, info.contact_begins);
  lua_setfield(L, -2, "contact_begins");
  lua_pushinteger(L, info.contact_ends);
  lua_setfield(L, -2, "contact_ends");
  lua_pushinteger(L, info.contact_hits);
  lua_setfield(L, -2, "contact_hits");
  lua_pushinteger(L, info.sensor_begins);
  lua_setfield(L, -2, "sensor_begins");
  lua_pushinteger(L, info.sensor_ends);
  lua_setfield(L, -2, "sensor_ends");
  lua_pushinteger(L, info.body_moves);
  lua_setfield(L, -2, "body_moves");
  lua_pushinteger(L, info.body_moves);
  lua_setfield(L, -2, "body_events");
  return 1;
}

// ---------------------------------------------------------- body getters

static int l_phys2d_pose(lua_State *L) {
  LubHandle b = 0;
  if (is_ref(L, 1, "phys2d_body")) {
    b = ref_body(L, 1);
  } else if (is_ref(L, 1, "phys2d_world")) {
    LubHandle w = ref_world(L, 1);
    if (!w)
      return push_not_found(L);
    LubStr key = lstr_arg(L, 2);
    b = lub_phys2d_body_find(g_ctx, w, key);
  } else {
    return luaL_error(L, "phys2d_pose: expected BodyRef or WorldRef, key");
  }
  LubPhys2dPose p;
  if (lub_phys2d_pose(g_ctx, b, &p) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  lua_pushnumber(L, p.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, p.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, p.angle);
  lua_setfield(L, -2, "angle");
  lua_pushnumber(L, p.vx);
  lua_setfield(L, -2, "vx");
  lua_pushnumber(L, p.vy);
  lua_setfield(L, -2, "vy");
  lua_pushnumber(L, p.w);
  lua_setfield(L, -2, "w");
  lua_pushboolean(L, p.awake);
  lua_setfield(L, -2, "awake");
  lua_pushboolean(L, p.enabled);
  lua_setfield(L, -2, "enabled");
  lua_pushboolean(L, p.sleep);
  lua_setfield(L, -2, "sleep");
  lua_pushnumber(L, p.sleep_threshold);
  lua_setfield(L, -2, "sleep_threshold");
  return 1;
}

static int l_phys2d_velocity(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  LubPhys2dVelocity v;
  if (lub_phys2d_velocity(g_ctx, b, &v) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  lua_pushnumber(L, v.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, v.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, v.w);
  lua_setfield(L, -2, "w");
  return 1;
}

static int l_phys2d_mass(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  LubPhys2dMassData m;
  if (lub_phys2d_mass(g_ctx, b, &m) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  lua_pushnumber(L, m.mass);
  lua_setfield(L, -2, "mass");
  lua_pushnumber(L, m.inertia);
  lua_setfield(L, -2, "inertia");
  lua_pushnumber(L, m.inertia);
  lua_setfield(L, -2, "rotational_inertia");
  push_vec2(L, m.center_x, m.center_y);
  lua_setfield(L, -2, "center");
  push_vec2(L, m.local_center_x, m.local_center_y);
  lua_setfield(L, -2, "local_center");
  return 1;
}

static int l_phys2d_center(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  float x = 0, y = 0;
  if (lub_phys2d_center(g_ctx, b, &x, &y) != LUB_OK)
    return push_not_found(L);
  push_vec2(L, x, y);
  return 1;
}

typedef LubStatus (*PointFn)(LubContext *, LubHandle, float, float, float *,
                             float *);

static int body_point(lua_State *L, PointFn fn) {
  LubHandle b = ref_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubVec2 p = value_vec2(L, 2, (LubVec2){0, 0});
  float x = 0, y = 0;
  if (fn(g_ctx, b, p.x, p.y, &x, &y) != LUB_OK)
    return push_not_found(L);
  push_vec2(L, x, y);
  return 1;
}

static int l_phys2d_world_point(lua_State *L) {
  return body_point(L, lub_phys2d_world_point);
}

static int l_phys2d_local_point(lua_State *L) {
  return body_point(L, lub_phys2d_local_point);
}

static int l_phys2d_velocity_at(lua_State *L) {
  return body_point(L, lub_phys2d_velocity_at);
}

// ----------------------------------------------------------- shape parts

static void push_u64_hex_field(lua_State *L, const char *name, uint64_t value) {
  char buf[17];
  SDL_snprintf(buf, sizeof(buf), "%016llx", (unsigned long long)value);
  lua_pushstring(L, buf);
  lua_setfield(L, -2, name);
}

static int single_bit_index(uint64_t bits) {
  if (bits == 0 || (bits & (bits - 1)) != 0)
    return -1;
  for (int i = 0; i < 64; ++i) {
    if ((bits & ((uint64_t)1 << i)) != 0)
      return i;
  }
  return -1;
}

static void push_bit_indices(lua_State *L, uint64_t bits) {
  lua_newtable(L);
  int out = 1;
  for (int i = 0; i < 64; ++i) {
    if ((bits & ((uint64_t)1 << i)) != 0) {
      lua_pushinteger(L, i);
      lua_rawseti(L, -2, out++);
    }
  }
}

static void push_filter_fields(lua_State *L, const LubPhys2dFilter *f) {
  push_u64_hex_field(L, "category_bits", f->category_bits);
  push_u64_hex_field(L, "mask_bits", f->mask_bits);
  int category = single_bit_index(f->category_bits);
  if (category >= 0) {
    lua_pushinteger(L, category);
    lua_setfield(L, -2, "category");
  }
  push_bit_indices(L, f->mask_bits);
  lua_setfield(L, -2, "mask");
  lua_pushinteger(L, f->group_index);
  lua_setfield(L, -2, "group");
  lua_pushinteger(L, f->group_index);
  lua_setfield(L, -2, "group_index");
}

static void push_aabb(lua_State *L, const LubPhys2dAabb *a) {
  lua_newtable(L);
  lua_pushnumber(L, a->min_x);
  lua_setfield(L, -2, "min_x");
  lua_pushnumber(L, a->min_y);
  lua_setfield(L, -2, "min_y");
  lua_pushnumber(L, a->max_x);
  lua_setfield(L, -2, "max_x");
  lua_pushnumber(L, a->max_y);
  lua_setfield(L, -2, "max_y");
}

static const char *shape_kind_name(int32_t kind) {
  switch (kind) {
  case LUB_PHYS2D_SHAPE_KIND_BOX:
    return "box";
  case LUB_PHYS2D_SHAPE_KIND_CIRCLE:
    return "circle";
  case LUB_PHYS2D_SHAPE_KIND_CAPSULE:
    return "capsule";
  case LUB_PHYS2D_SHAPE_KIND_SEGMENT:
    return "segment";
  case LUB_PHYS2D_SHAPE_KIND_POLYGON:
    return "polygon";
  case LUB_PHYS2D_SHAPE_KIND_CHAIN_SEGMENT:
    return "chain_segment";
  default:
    return "unknown";
  }
}

static void push_shape_part(lua_State *L, const LubPhys2dShapePart *p,
                            bool with_kind) {
  lua_newtable(L);
  set_str_field(L, "body", p->body_key);
  set_str_field(L, "shape", p->shape_key);
  if (!lstr_empty(p->tag)) {
    push_lstr(L, p->tag);
    lua_setfield(L, -2, "tag");
  }
  if (!lstr_empty(p->chain_key)) {
    push_lstr(L, p->chain_key);
    lua_setfield(L, -2, "chain");
    lua_pushboolean(L, true);
    lua_setfield(L, -2, "segment");
  }
  if (p->has_material) {
    if (!lstr_empty(p->material_name)) {
      push_lstr(L, p->material_name);
    } else {
      lua_pushinteger(L, p->material_id);
    }
    lua_setfield(L, -2, "material");
    lua_pushinteger(L, p->material_id);
    lua_setfield(L, -2, "user_material_id");
  }
  if (p->has_filter)
    push_filter_fields(L, &p->filter);
  if (with_kind && p->kind != 0) {
    lua_pushstring(L, shape_kind_name(p->kind));
    lua_setfield(L, -2, "kind");
  }
  lua_pushboolean(L, p->valid);
  lua_setfield(L, -2, "valid");
}

// ---------------------------------------------------------- shape queries

static void parse_ray(lua_State *L, int idx, LubPhys2dRay *ray) {
  LubVec2 origin = {table_number(L, idx, "x", NULL, 0.0f),
                    table_number(L, idx, "y", NULL, 0.0f)};
  origin = table_vec2(L, idx, "origin", "from", origin);
  LubVec2 translation = {table_number(L, idx, "dx", NULL, 0.0f),
                         table_number(L, idx, "dy", NULL, 0.0f)};
  translation = table_vec2(L, idx, "translation", "delta", translation);
  if (table_get_any(L, idx, "to", NULL)) {
    if (lua_istable(L, -1)) {
      LubVec2 to = value_vec2(L, lua_gettop(L), origin);
      translation.x = to.x - origin.x;
      translation.y = to.y - origin.y;
    }
    lua_pop(L, 1);
  }
  ray->x = origin.x;
  ray->y = origin.y;
  ray->dx = translation.x;
  ray->dy = translation.y;
  ray->max_fraction = table_number(L, idx, "max_fraction", "maxFraction", 1.0f);
  if (ray->max_fraction < 0.0f)
    ray->max_fraction = 0.0f;
}

static int l_phys2d_shape_test_point(lua_State *L) {
  LubHandle s = ref_shape(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubVec2 p = value_vec2(L, 2, (LubVec2){0, 0});
  bool inside = false;
  if (lub_phys2d_shape_test_point(g_ctx, s, p.x, p.y, &inside) != LUB_OK)
    return push_not_found(L);
  lua_pushboolean(L, inside);
  return 1;
}

static int l_phys2d_shape_raycast(lua_State *L) {
  LubHandle s = ref_shape(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dRay ray;
  parse_ray(L, 2, &ray);
  LubPhys2dRayHit hit;
  bool has = false;
  LubStatus st = lub_phys2d_shape_raycast(g_ctx, s, &ray, &hit, &has);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  if (!has) {
    lua_pushnil(L);
    return 1;
  }
  lua_newtable(L);
  lua_pushnumber(L, hit.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, hit.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, hit.nx);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, hit.ny);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, hit.fraction);
  lua_setfield(L, -2, "fraction");
  lua_pushinteger(L, hit.iterations);
  lua_setfield(L, -2, "iterations");
  return 1;
}

static int l_phys2d_shape_closest_point(lua_State *L) {
  LubHandle s = ref_shape(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubVec2 p = value_vec2(L, 2, (LubVec2){0, 0});
  float x = 0, y = 0;
  if (lub_phys2d_shape_closest_point(g_ctx, s, p.x, p.y, &x, &y) != LUB_OK)
    return push_not_found(L);
  push_vec2(L, x, y);
  return 1;
}

static int l_phys2d_shape_aabb(lua_State *L) {
  LubHandle s = ref_shape(L, 1);
  LubPhys2dAabb a;
  if (lub_phys2d_shape_aabb(g_ctx, s, &a) != LUB_OK)
    return push_not_found(L);
  push_aabb(L, &a);
  return 1;
}

static int l_phys2d_shape_info(lua_State *L) {
  LubHandle s = ref_shape(L, 1);
  LubPhys2dShapeInfo info;
  if (lub_phys2d_shape_info(g_ctx, s, &info) != LUB_OK)
    return push_not_found(L);
  push_shape_part(L, &info.part, true);
  lua_pushnumber(L, info.density);
  lua_setfield(L, -2, "density");
  lua_pushnumber(L, info.friction);
  lua_setfield(L, -2, "friction");
  lua_pushnumber(L, info.restitution);
  lua_setfield(L, -2, "restitution");
  lua_pushboolean(L, info.sensor);
  lua_setfield(L, -2, "sensor");
  lua_pushboolean(L, info.sensor_events);
  lua_setfield(L, -2, "sensor_events");
  lua_pushboolean(L, info.contact);
  lua_setfield(L, -2, "contact");
  lua_pushboolean(L, info.pre_solve);
  lua_setfield(L, -2, "pre_solve");
  lua_pushboolean(L, info.hit);
  lua_setfield(L, -2, "hit");
  lua_newtable(L);
  push_filter_fields(L, &info.part.filter);
  lua_setfield(L, -2, "filter");
  push_aabb(L, &info.aabb);
  lua_setfield(L, -2, "aabb");
  return 1;
}

static int l_phys2d_shape_set_material(lua_State *L) {
  LubHandle s = check_shape(L, 1);
  LubPhys2dMaterialDesc d = {0};
  if (lua_istable(L, 2)) {
    d.has_density = table_number_optional(L, 2, "density", NULL, &d.density);
    d.has_friction = table_number_optional(L, 2, "friction", NULL, &d.friction);
    d.has_restitution =
        table_number_optional(L, 2, "restitution", NULL, &d.restitution);
    if (table_get_any(L, 2, "material", NULL)) {
      if (lua_type(L, -1) == LUA_TSTRING) {
        d.material_name = table_str(L, 2, "material");
        d.has_material_name = true;
      } else if (lua_isinteger(L, -1) || lua_isnumber(L, -1)) {
        d.material_id = (int32_t)lua_tointeger(L, -1);
        d.has_material_id = true;
        d.has_material_name = true; // 名前は消す
      }
      lua_pop(L, 1);
    }
    int id = 0;
    if (table_int_optional(L, 2, "material_id", "materialId", &id)) {
      d.material_id = id;
      d.has_material_id = true;
    }
    if (table_int_optional(L, 2, "user_material_id", "userMaterialId", &id)) {
      d.material_id = id;
      d.has_material_id = true;
    }
  } else if (lua_isinteger(L, 2) || lua_isnumber(L, 2)) {
    d.material_id = (int32_t)lua_tointeger(L, 2);
    d.has_material_id = true;
    d.has_material_name = true;
  } else if (lua_type(L, 2) == LUA_TSTRING) {
    d.material_name = lstr_arg(L, 2);
    d.has_material_name = true;
  } else {
    return luaL_error(
        L, "phys2d_shape_set_material: expected table, number, or string");
  }
  if (lub_phys2d_shape_set_material(g_ctx, s, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_shape_set_filter(lua_State *L) {
  LubHandle s = check_shape(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dShapeInfo info;
  if (lub_phys2d_shape_info(g_ctx, s, &info) != LUB_OK)
    return luaL_error(L, "phys2d_shape_set_filter: shape is not live");
  LubPhys2dFilter f = info.part.filter;
  if (table_get_any(L, 2, "filter", NULL)) {
    if (lua_istable(L, -1))
      parse_filter_table(L, lua_gettop(L), &f.category_bits, &f.mask_bits,
                         &f.group_index);
    lua_pop(L, 1);
  } else {
    parse_filter_table(L, 2, &f.category_bits, &f.mask_bits, &f.group_index);
  }
  if (lub_phys2d_shape_set_filter(g_ctx, s, &f) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys2d_shape_set_events(lua_State *L) {
  LubHandle s = check_shape(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dEventFlags f = {0};
  bool flag = false;
  if (table_bool_optional(L, 2, "sensor", NULL, &flag))
    return luaL_error(
        L, "phys2d_shape_set_events: sensor cannot change at runtime");
  if (table_bool_optional(L, 2, "sensor_events", "sensorEvents", &flag)) {
    f.has_sensor_events = true;
    f.sensor_events = flag;
  }
  if (table_bool_optional(L, 2, "contact", "contactEvents", &flag) ||
      table_bool_optional(L, 2, "contact_events", "contactEvents", &flag)) {
    f.has_contact = true;
    f.contact = flag;
  }
  if (table_bool_optional(L, 2, "pre_solve", "preSolve", &flag)) {
    f.has_pre_solve = true;
    f.pre_solve = flag;
  }
  if (table_bool_optional(L, 2, "hit", "hitEvents", &flag) ||
      table_bool_optional(L, 2, "hit_events", "hitEvents", &flag)) {
    f.has_hit = true;
    f.hit = flag;
  }
  if (lub_phys2d_shape_set_events(g_ctx, s, &f) != LUB_OK)
    return raise_last(L);
  return 0;
}

// ------------------------------------------------------------- body lists

static int l_phys2d_body_shapes(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  const LubPhys2dShapePart *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys2d_body_shapes(g_ctx, b, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    push_shape_part(L, &items[i], items[i].valid);
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

static int l_phys2d_body_joints(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  const LubPhys2dJointView *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys2d_body_joints(g_ctx, b, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    push_joint_view(L, &items[i]);
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

static int l_phys2d_body_contacts(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  const LubPhys2dContactData *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys2d_body_contacts(g_ctx, b, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    const LubPhys2dContactData *c = &items[i];
    lua_newtable(L);
    push_shape_part(L, &c->a, false);
    lua_setfield(L, -2, "a");
    push_shape_part(L, &c->b, false);
    lua_setfield(L, -2, "b");
    lua_pushnumber(L, c->nx);
    lua_setfield(L, -2, "nx");
    lua_pushnumber(L, c->ny);
    lua_setfield(L, -2, "ny");
    lua_pushinteger(L, c->point_count);
    lua_setfield(L, -2, "point_count");
    if (c->point_count > 0) {
      lua_pushnumber(L, c->x);
      lua_setfield(L, -2, "x");
      lua_pushnumber(L, c->y);
      lua_setfield(L, -2, "y");
      lua_pushnumber(L, c->separation);
      lua_setfield(L, -2, "separation");
    }
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

static int l_phys2d_chain_segments(lua_State *L) {
  LubHandle c = ref_chain(L, 1);
  const LubPhys2dShapePart *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys2d_chain_segments(g_ctx, c, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    push_shape_part(L, &items[i], false);
    lua_pushinteger(L, i + 1);
    lua_setfield(L, -2, "index");
    lua_pushstring(L, "chain_segment");
    lua_setfield(L, -2, "kind");
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

// ------------------------------------------------------------ step events

static int32_t parse_event_kind(lua_State *L, int idx, bool allow_hit,
                                const char *fn) {
  const char *kind = luaL_optstring(L, idx, "begin");
  if (strcmp(kind, "begin") == 0)
    return LUB_PHYS2D_EVENT_KIND_BEGIN;
  if (strcmp(kind, "end") == 0)
    return LUB_PHYS2D_EVENT_KIND_END;
  if (allow_hit && strcmp(kind, "hit") == 0)
    return LUB_PHYS2D_EVENT_KIND_HIT;
  return luaL_error(L, "%s: kind must be %s", fn,
                    allow_hit ? "begin, end, or hit" : "begin or end"),
         0;
}

static int l_phys2d_contacts(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  int32_t kind = parse_event_kind(L, 2, true, "phys2d_contacts");
  const LubPhys2dContact *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys2d_contacts(g_ctx, w, kind, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    const LubPhys2dContact *e = &items[i];
    lua_newtable(L);
    push_shape_part(L, &e->a, false);
    lua_setfield(L, -2, "a");
    push_shape_part(L, &e->b, false);
    lua_setfield(L, -2, "b");
    lua_pushnumber(L, e->nx);
    lua_setfield(L, -2, "nx");
    lua_pushnumber(L, e->ny);
    lua_setfield(L, -2, "ny");
    lua_pushinteger(L, e->point_count);
    lua_setfield(L, -2, "point_count");
    lua_pushnumber(L, e->x);
    lua_setfield(L, -2, "x");
    lua_pushnumber(L, e->y);
    lua_setfield(L, -2, "y");
    if (e->approach_speed != 0.0f) {
      lua_pushnumber(L, e->approach_speed);
      lua_setfield(L, -2, "approach_speed");
    }
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

static int l_phys2d_sensors(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  int32_t kind = parse_event_kind(L, 2, false, "phys2d_sensors");
  const LubPhys2dContact *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys2d_sensors(g_ctx, w, kind, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    lua_newtable(L);
    push_shape_part(L, &items[i].a, false);
    lua_setfield(L, -2, "sensor");
    push_shape_part(L, &items[i].b, false);
    lua_setfield(L, -2, "visitor");
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

static int l_phys2d_body_events(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  const LubPhys2dBodyEvent *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys2d_body_events(g_ctx, w, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    const LubPhys2dBodyEvent *e = &items[i];
    lua_newtable(L);
    set_str_field(L, "body", e->body);
    lua_pushboolean(L, e->valid);
    lua_setfield(L, -2, "valid");
    lua_pushnumber(L, e->x);
    lua_setfield(L, -2, "x");
    lua_pushnumber(L, e->y);
    lua_setfield(L, -2, "y");
    lua_pushnumber(L, e->angle);
    lua_setfield(L, -2, "angle");
    lua_pushboolean(L, e->fell_asleep);
    lua_setfield(L, -2, "fell_asleep");
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

// ---------------------------------------------------------- world queries
// visitor は results table に hit を積みつつ Lua の関数を呼ぶ。Lua 側の
// error は貯めて query を打ち切り、nil, "fn visitor: msg" にする。

typedef struct Visit {
  lua_State *L;
  int results_ref;
  int visitor_ref;
  int count;
  char *error;
} Visit;

static void visit_init(Visit *v, lua_State *L, int visitor_idx) {
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
static int visit_finish(Visit *v, const char *fn_name, bool push_results,
                        const LubPhys2dTreeStats *stats) {
  lua_State *L = v->L;
  int nret = 0;
  if (v->error) {
    lua_pushnil(L);
    lua_pushfstring(L, "%s visitor: %s", fn_name, v->error);
    SDL_free(v->error);
    nret = 2;
  } else if (push_results) {
    lua_rawgeti(L, LUA_REGISTRYINDEX, v->results_ref);
    if (stats) {
      lua_pushinteger(L, stats->node_visits);
      lua_setfield(L, -2, "node_visits");
      lua_pushinteger(L, stats->leaf_visits);
      lua_setfield(L, -2, "leaf_visits");
    }
    nret = 1;
  }
  luaL_unref(L, LUA_REGISTRYINDEX, v->results_ref);
  if (v->visitor_ref != LUA_NOREF)
    luaL_unref(L, LUA_REGISTRYINDEX, v->visitor_ref);
  return nret;
}

static bool query_result_is_string(lua_State *L, int idx, const char *s) {
  return lua_isstring(L, idx) && strcmp(lua_tostring(L, idx), s) == 0;
}

static bool parse_overlap_visitor_result(lua_State *L, int idx, bool *include) {
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

static float parse_raycast_visitor_result(lua_State *L, int idx, float fraction,
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

// stack: results, item。visitor を呼び、include なら results に積む。
// 戻り値は visitor の生の結果 (stack の上、caller が pop) を残すかどうか。
static bool visit_call(Visit *v, bool *include) {
  lua_State *L = v->L;
  *include = true;
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

static void visit_store(Visit *v, bool include) {
  lua_State *L = v->L;
  if (include) {
    lua_rawseti(L, -2, ++v->count);
  } else {
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
}

static bool overlap_visit(void *user, const LubPhys2dShapePart *shape) {
  Visit *v = (Visit *)user;
  lua_State *L = v->L;
  if (v->error)
    return false;
  lua_rawgeti(L, LUA_REGISTRYINDEX, v->results_ref);
  push_shape_part(L, shape, false);
  bool include = true;
  bool keep_going = true;
  if (visit_call(v, &include)) {
    keep_going = parse_overlap_visitor_result(L, -1, &include);
    lua_pop(L, 1);
  } else if (v->error) {
    return false;
  }
  visit_store(v, include);
  return keep_going;
}

static void push_ray_hit(lua_State *L, const LubPhys2dRayHit *hit) {
  push_shape_part(L, &hit->shape, false);
  lua_pushnumber(L, hit->x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, hit->y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, hit->nx);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, hit->ny);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, hit->fraction);
  lua_setfield(L, -2, "fraction");
}

static float ray_visit(void *user, const LubPhys2dRayHit *hit) {
  Visit *v = (Visit *)user;
  lua_State *L = v->L;
  if (v->error)
    return 0.0f;
  lua_rawgeti(L, LUA_REGISTRYINDEX, v->results_ref);
  push_ray_hit(L, hit);
  bool include = true;
  float result = hit->fraction;
  if (visit_call(v, &include)) {
    result = parse_raycast_visitor_result(L, -1, hit->fraction, &include);
    lua_pop(L, 1);
  } else if (v->error) {
    return 0.0f;
  }
  visit_store(v, include);
  return result;
}

static bool plane_visit(void *user, const LubPhys2dMoverPlane *plane) {
  Visit *v = (Visit *)user;
  lua_State *L = v->L;
  if (v->error)
    return false;
  lua_rawgeti(L, LUA_REGISTRYINDEX, v->results_ref);
  push_shape_part(L, &plane->shape, false);
  lua_pushboolean(L, plane->hit);
  lua_setfield(L, -2, "hit");
  lua_pushnumber(L, plane->x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, plane->y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, plane->nx);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, plane->ny);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, plane->offset);
  lua_setfield(L, -2, "offset");
  bool include = true;
  bool keep_going = true;
  if (visit_call(v, &include)) {
    keep_going = parse_overlap_visitor_result(L, -1, &include);
    lua_pop(L, 1);
  } else if (v->error) {
    return false;
  }
  visit_store(v, include);
  return keep_going;
}

static int l_phys2d_raycast(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dRay ray;
  parse_ray(L, 2, &ray);
  LubPhys2dQueryFilter filter = parse_query_filter(L, 2);
  if (!lua_isfunction(L, 3)) {
    LubPhys2dRayHit hit;
    bool has = false;
    LubStatus st =
        lub_phys2d_raycast_closest(g_ctx, w, &ray, &filter, &hit, &has);
    if (st == LUB_NOT_FOUND)
      return push_not_found(L);
    if (st != LUB_OK)
      return raise_last(L);
    if (!has) {
      lua_pushnil(L);
      return 1;
    }
    push_ray_hit(L, &hit);
    lua_pushinteger(L, hit.node_visits);
    lua_setfield(L, -2, "node_visits");
    lua_pushinteger(L, hit.leaf_visits);
    lua_setfield(L, -2, "leaf_visits");
    return 1;
  }
  Visit v;
  visit_init(&v, L, 3);
  LubPhys2dTreeStats stats;
  LubStatus st =
      lub_phys2d_raycast(g_ctx, w, &ray, &filter, ray_visit, &v, &stats);
  if (st == LUB_NOT_FOUND) {
    visit_finish(&v, "phys2d_raycast", false, NULL);
    return push_not_found(L);
  }
  if (st != LUB_OK) {
    visit_finish(&v, "phys2d_raycast", false, NULL);
    return raise_last(L);
  }
  return visit_finish(&v, "phys2d_raycast", true, &stats);
}

static int l_phys2d_overlap_aabb(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dAabb aabb = {table_number(L, 2, "min_x", "minX", 0.0f),
                        table_number(L, 2, "min_y", "minY", 0.0f),
                        table_number(L, 2, "max_x", "maxX", 0.0f),
                        table_number(L, 2, "max_y", "maxY", 0.0f)};
  LubPhys2dQueryFilter filter = parse_query_filter(L, 2);
  Visit v;
  visit_init(&v, L, 3);
  LubPhys2dTreeStats stats;
  LubStatus st = lub_phys2d_overlap_aabb(g_ctx, w, &aabb, &filter,
                                         overlap_visit, &v, &stats);
  if (st == LUB_NOT_FOUND) {
    visit_finish(&v, "phys2d_overlap_aabb", false, NULL);
    return push_not_found(L);
  }
  if (st != LUB_OK) {
    visit_finish(&v, "phys2d_overlap_aabb", false, NULL);
    return raise_last(L);
  }
  return visit_finish(&v, "phys2d_overlap_aabb", true, &stats);
}

static void parse_shape_proxy(lua_State *L, int idx, LubPhys2dShapeProxy *p,
                              float **owned_points) {
  memset(p, 0, sizeof(*p));
  *owned_points = NULL;
  const char *type = "circle";
  if (table_get_any(L, idx, "type", "shape")) {
    if (lua_isstring(L, -1))
      type = lua_tostring(L, -1);
    lua_pop(L, 1);
  } else if (table_get_any(L, idx, "kind", NULL)) {
    if (lua_isstring(L, -1))
      type = lua_tostring(L, -1);
    lua_pop(L, 1);
  }
  LubVec2 position = {table_number(L, idx, "x", NULL, 0.0f),
                      table_number(L, idx, "y", NULL, 0.0f)};
  position = table_vec2(L, idx, "origin", "from", position);
  p->x = position.x;
  p->y = position.y;
  p->angle = table_number(L, idx, "angle", NULL, 0.0f);
  p->cx = table_number(L, idx, "cx", NULL, 0.0f);
  p->cy = table_number(L, idx, "cy", NULL, 0.0f);
  p->ax = table_number(L, idx, "ax", "x1", 0.0f);
  p->ay = table_number(L, idx, "ay", "y1", 0.0f);
  p->bx = table_number(L, idx, "bx", "x2", 0.0f);
  p->by = table_number(L, idx, "by", "y2", 0.0f);
  p->hx = table_number(L, idx, "hx", NULL, 0.0f);
  p->hy = table_number(L, idx, "hy", NULL, 0.0f);
  if (strcmp(type, "circle") == 0) {
    p->kind = LUB_PHYS2D_PROXY_KIND_CIRCLE;
    p->r = table_number(L, idx, "r", "radius", 0.0f);
  } else if (strcmp(type, "capsule") == 0) {
    p->kind = LUB_PHYS2D_PROXY_KIND_CAPSULE;
    p->r = table_number(L, idx, "r", "radius", 0.0f);
  } else if (strcmp(type, "segment") == 0) {
    p->kind = LUB_PHYS2D_PROXY_KIND_SEGMENT;
  } else if (strcmp(type, "box") == 0) {
    p->kind = LUB_PHYS2D_PROXY_KIND_BOX;
    p->r = table_number(L, idx, "radius", "r", 0.0f);
  } else if (strcmp(type, "polygon") == 0) {
    p->kind = LUB_PHYS2D_PROXY_KIND_POLYGON;
    int count = 0;
    *owned_points = read_point_list(L, idx, 3, 8, "phys2d_shape_cast", &count);
    p->points = *owned_points;
    p->point_count = count;
    p->r = table_number(L, idx, "radius", "r", 0.0f);
  } else {
    luaL_error(L, "phys2d_shape_cast: unknown shape type '%s'", type);
  }
}

static LubVec2 parse_translation(lua_State *L, int idx, const char *fn_name) {
  LubVec2 translation = {table_number(L, idx, "dx", NULL, 0.0f),
                         table_number(L, idx, "dy", NULL, 0.0f)};
  translation = table_vec2(L, idx, "translation", "delta", translation);
  float max_fraction =
      table_number(L, idx, "max_fraction", "maxFraction", 1.0f);
  if (max_fraction < 0.0f)
    max_fraction = 0.0f;
  translation.x *= max_fraction;
  translation.y *= max_fraction;
  if (translation.x * translation.x + translation.y * translation.y <= 1e-12f)
    luaL_error(L, "%s: translation must be non-zero", fn_name);
  return translation;
}

static int l_phys2d_shape_cast(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dShapeProxy proxy;
  float *owned = NULL;
  parse_shape_proxy(L, 2, &proxy, &owned);
  LubVec2 t = parse_translation(L, 2, "phys2d_shape_cast");
  LubPhys2dQueryFilter filter = parse_query_filter(L, 2);
  Visit v;
  visit_init(&v, L, 3);
  LubPhys2dTreeStats stats;
  LubStatus st = lub_phys2d_shape_cast(g_ctx, w, &proxy, t.x, t.y, &filter,
                                       ray_visit, &v, &stats);
  SDL_free(owned);
  if (st == LUB_NOT_FOUND) {
    visit_finish(&v, "phys2d_shape_cast", false, NULL);
    return push_not_found(L);
  }
  if (st != LUB_OK) {
    visit_finish(&v, "phys2d_shape_cast", false, NULL);
    return raise_last(L);
  }
  if (v.visitor_ref == LUA_NOREF && !v.error) {
    // visitor 無しは最後 (最も近い) の hit だけ返す。
    if (v.count == 0) {
      visit_finish(&v, "phys2d_shape_cast", false, NULL);
      lua_pushnil(L);
      return 1;
    }
    lua_rawgeti(L, LUA_REGISTRYINDEX, v.results_ref);
    lua_rawgeti(L, -1, v.count);
    lua_pushinteger(L, stats.node_visits);
    lua_setfield(L, -2, "node_visits");
    lua_pushinteger(L, stats.leaf_visits);
    lua_setfield(L, -2, "leaf_visits");
    lua_remove(L, -2);
    visit_finish(&v, "phys2d_shape_cast", false, NULL);
    return 1;
  }
  return visit_finish(&v, "phys2d_shape_cast", true, &stats);
}

static void parse_mover(lua_State *L, int idx, LubPhys2dMover *m) {
  m->ax = table_number(L, idx, "ax", "x1", 0.0f);
  m->ay = table_number(L, idx, "ay", "y1", 0.0f);
  m->bx = table_number(L, idx, "bx", "x2", 0.0f);
  m->by = table_number(L, idx, "by", "y2", 0.0f);
  m->r = table_number(L, idx, "r", "radius", 0.0f);
}

static int l_phys2d_cast_mover(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dMover mover;
  parse_mover(L, 2, &mover);
  LubVec2 t = parse_translation(L, 2, "phys2d_cast_mover");
  LubPhys2dQueryFilter filter = parse_query_filter(L, 2);
  float fraction = 0;
  LubStatus st =
      lub_phys2d_cast_mover(g_ctx, w, &mover, t.x, t.y, &filter, &fraction);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  lua_pushnumber(L, fraction);
  lua_setfield(L, -2, "fraction");
  lua_pushnumber(L, t.x * fraction);
  lua_setfield(L, -2, "dx");
  lua_pushnumber(L, t.y * fraction);
  lua_setfield(L, -2, "dy");
  return 1;
}

static int l_phys2d_collide_mover(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dMover mover;
  parse_mover(L, 2, &mover);
  LubPhys2dQueryFilter filter = parse_query_filter(L, 2);
  Visit v;
  visit_init(&v, L, 3);
  LubStatus st =
      lub_phys2d_collide_mover(g_ctx, w, &mover, &filter, plane_visit, &v);
  if (st == LUB_NOT_FOUND) {
    visit_finish(&v, "phys2d_collide_mover", false, NULL);
    return push_not_found(L);
  }
  if (st != LUB_OK) {
    visit_finish(&v, "phys2d_collide_mover", false, NULL);
    return raise_last(L);
  }
  return visit_finish(&v, "phys2d_collide_mover", true, NULL);
}

static int l_phys2d_explode(lua_State *L) {
  LubHandle w = check_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys2dExplosionDesc d;
  memset(&d, 0, sizeof(d));
  LubVec2 pos = {table_number(L, 2, "x", NULL, 0.0f),
                 table_number(L, 2, "y", NULL, 0.0f)};
  pos = table_vec2(L, 2, "position", "center", pos);
  d.x = pos.x;
  d.y = pos.y;
  d.radius = table_number(L, 2, "radius", "r", 0.0f);
  d.falloff = table_number(L, 2, "falloff", NULL, 0.0f);
  d.impulse_per_length =
      table_number(L, 2, "impulse_per_length", "impulsePerLength", 0.0f);
  d.impulse_per_length =
      table_number(L, 2, "impulse", NULL, d.impulse_per_length);
  d.mask_bits = parse_query_filter(L, 2).mask_bits;
  if (lub_phys2d_explode(g_ctx, w, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

// ----------------------------------------------------------------- debug

static void push_float_array(lua_State *L, const float *items, int32_t count) {
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    lua_pushnumber(L, items[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static int l_phys2d_debug(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  LubPhys2dDebugDesc d;
  memset(&d, 0, sizeof(d));
  d.shapes = true;
  if (lua_istable(L, 2)) {
    d.shapes = table_bool(L, 2, "shapes", NULL, d.shapes);
    d.joints = table_bool(L, 2, "joints", NULL, false);
    d.joint_extras = table_bool(L, 2, "joint_extras", "jointExtras", false);
    d.bounds = table_bool(L, 2, "bounds", NULL, false);
    d.mass = table_bool(L, 2, "mass", NULL, false);
    d.body_names = table_bool(L, 2, "body_names", "bodyNames", false);
    d.contacts = table_bool(L, 2, "contacts", NULL, false);
    d.graph_colors = table_bool(L, 2, "graph_colors", "graphColors", false);
    d.contact_normals =
        table_bool(L, 2, "contact_normals", "contactNormals", false);
    d.contact_impulses =
        table_bool(L, 2, "contact_impulses", "contactImpulses", false);
    d.contact_features =
        table_bool(L, 2, "contact_features", "contactFeatures", false);
    d.friction_impulses =
        table_bool(L, 2, "friction_impulses", "frictionImpulses", false);
    d.islands = table_bool(L, 2, "islands", NULL, false);
    if (table_get_any(L, 2, "drawing_bounds", "drawingBounds")) {
      if (lua_istable(L, -1)) {
        int b = lua_gettop(L);
        d.drawing_bounds.min_x = table_number(L, b, "min_x", "minX", -FLT_MAX);
        d.drawing_bounds.min_y = table_number(L, b, "min_y", "minY", -FLT_MAX);
        d.drawing_bounds.max_x = table_number(L, b, "max_x", "maxX", FLT_MAX);
        d.drawing_bounds.max_y = table_number(L, b, "max_y", "maxY", FLT_MAX);
        d.has_drawing_bounds = true;
      }
      lua_pop(L, 1);
    }
  }
  LubPhys2dDebugData data;
  LubStatus st = lub_phys2d_debug(g_ctx, w, &d, &data);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  push_float_array(L, data.segments, data.segment_count);
  lua_setfield(L, -2, "segments");
  push_float_array(L, data.circles, data.circle_count);
  lua_setfield(L, -2, "circles");
  push_float_array(L, data.capsules, data.capsule_count);
  lua_setfield(L, -2, "capsules");
  push_float_array(L, data.polygons, data.polygon_count);
  lua_setfield(L, -2, "polygons");
  push_float_array(L, data.points, data.point_count);
  lua_setfield(L, -2, "points");
  return 1;
}

static void set_number(lua_State *L, const char *key, float value) {
  lua_pushnumber(L, value);
  lua_setfield(L, -2, key);
}

static int l_phys2d_profile(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  LubPhys2dProfile p;
  if (lub_phys2d_profile(g_ctx, w, &p) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  set_number(L, "step", p.step);
  set_number(L, "pairs", p.pairs);
  set_number(L, "collide", p.collide);
  set_number(L, "solve", p.solve);
  set_number(L, "merge_islands", p.merge_islands);
  set_number(L, "prepare_stages", p.prepare_stages);
  set_number(L, "solve_constraints", p.solve_constraints);
  set_number(L, "prepare_constraints", p.prepare_constraints);
  set_number(L, "integrate_velocities", p.integrate_velocities);
  set_number(L, "warm_start", p.warm_start);
  set_number(L, "solve_impulses", p.solve_impulses);
  set_number(L, "integrate_positions", p.integrate_positions);
  set_number(L, "relax_impulses", p.relax_impulses);
  set_number(L, "apply_restitution", p.apply_restitution);
  set_number(L, "store_impulses", p.store_impulses);
  set_number(L, "split_islands", p.split_islands);
  set_number(L, "transforms", p.transforms);
  set_number(L, "hit_events", p.hit_events);
  set_number(L, "refit", p.refit);
  set_number(L, "bullets", p.bullets);
  set_number(L, "sleep_islands", p.sleep_islands);
  set_number(L, "sensors", p.sensors);
  return 1;
}

static void set_integer(lua_State *L, const char *key, int value) {
  lua_pushinteger(L, value);
  lua_setfield(L, -2, key);
}

static int l_phys2d_counters(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  LubPhys2dCounters c;
  if (lub_phys2d_counters(g_ctx, w, &c) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  set_integer(L, "body_count", c.body_count);
  set_integer(L, "shape_count", c.shape_count);
  set_integer(L, "contact_count", c.contact_count);
  set_integer(L, "joint_count", c.joint_count);
  set_integer(L, "island_count", c.island_count);
  set_integer(L, "stack_used", c.stack_used);
  set_integer(L, "static_tree_height", c.static_tree_height);
  set_integer(L, "tree_height", c.tree_height);
  set_integer(L, "byte_count", c.byte_count);
  set_integer(L, "task_count", c.task_count);
  lua_newtable(L);
  for (int i = 0; i < 12; ++i) {
    lua_pushinteger(L, c.color_counts[i]);
    lua_rawseti(L, -2, i + 1);
  }
  lua_setfield(L, -2, "color_counts");
  return 1;
}

// -------------------------------------------------------------- register

void phys2d_lua_register(lua_State *L, LubContext *ctx) {
  g_ctx = ctx;
  lua_pushinteger(L, LUB_PHYS2D_BODY_TYPE_STATIC);
  lua_setglobal(L, "STATIC");
  lua_pushinteger(L, LUB_PHYS2D_BODY_TYPE_KINEMATIC);
  lua_setglobal(L, "KINEMATIC");
  lua_pushinteger(L, LUB_PHYS2D_BODY_TYPE_DYNAMIC);
  lua_setglobal(L, "DYNAMIC");

  static const luaL_Reg fns[] = {
      {"phys2d_world", l_phys2d_world},
      {"phys2d_begin", l_phys2d_begin},
      {"phys2d_world_info", l_phys2d_world_info},
      {"phys2d_body", l_phys2d_body},
      {"phys2d_box", l_phys2d_box},
      {"phys2d_circle", l_phys2d_circle},
      {"phys2d_capsule", l_phys2d_capsule},
      {"phys2d_segment", l_phys2d_segment},
      {"phys2d_polygon", l_phys2d_polygon},
      {"phys2d_chain", l_phys2d_chain},
      {"phys2d_chain_segments", l_phys2d_chain_segments},
      {"phys2d_joint", l_phys2d_joint},
      {"phys2d_joint_info", l_phys2d_joint_info},
      {"phys2d_joint_force", l_phys2d_joint_force},
      {"phys2d_joint_torque", l_phys2d_joint_torque},
      {"phys2d_joint_angle", l_phys2d_joint_angle},
      {"phys2d_joint_translation", l_phys2d_joint_translation},
      {"phys2d_joint_speed", l_phys2d_joint_speed},
      {"phys2d_joint_length", l_phys2d_joint_length},
      {"phys2d_joint_motor_force", l_phys2d_joint_motor_force},
      {"phys2d_joint_motor_torque", l_phys2d_joint_motor_torque},
      {"phys2d_joint_set_motor", l_phys2d_joint_set_motor},
      {"phys2d_joint_set_limit", l_phys2d_joint_set_limit},
      {"phys2d_joint_set_spring", l_phys2d_joint_set_spring},
      {"phys2d_joint_set_target", l_phys2d_joint_set_target},
      {"phys2d_step", l_phys2d_step},
      {"phys2d_pose", l_phys2d_pose},
      {"phys2d_velocity", l_phys2d_velocity},
      {"phys2d_mass", l_phys2d_mass},
      {"phys2d_center", l_phys2d_center},
      {"phys2d_world_point", l_phys2d_world_point},
      {"phys2d_local_point", l_phys2d_local_point},
      {"phys2d_velocity_at", l_phys2d_velocity_at},
      {"phys2d_body_shapes", l_phys2d_body_shapes},
      {"phys2d_body_joints", l_phys2d_body_joints},
      {"phys2d_body_contacts", l_phys2d_body_contacts},
      {"phys2d_shape_test_point", l_phys2d_shape_test_point},
      {"phys2d_shape_raycast", l_phys2d_shape_raycast},
      {"phys2d_shape_closest_point", l_phys2d_shape_closest_point},
      {"phys2d_shape_aabb", l_phys2d_shape_aabb},
      {"phys2d_shape_info", l_phys2d_shape_info},
      {"phys2d_shape_set_material", l_phys2d_shape_set_material},
      {"phys2d_shape_set_filter", l_phys2d_shape_set_filter},
      {"phys2d_shape_set_events", l_phys2d_shape_set_events},
      {"phys2d_contacts", l_phys2d_contacts},
      {"phys2d_body_events", l_phys2d_body_events},
      {"phys2d_sensors", l_phys2d_sensors},
      {"phys2d_raycast", l_phys2d_raycast},
      {"phys2d_overlap_aabb", l_phys2d_overlap_aabb},
      {"phys2d_shape_cast", l_phys2d_shape_cast},
      {"phys2d_cast_mover", l_phys2d_cast_mover},
      {"phys2d_collide_mover", l_phys2d_collide_mover},
      {"phys2d_explode", l_phys2d_explode},
      {"phys2d_debug", l_phys2d_debug},
      {"phys2d_profile", l_phys2d_profile},
      {"phys2d_counters", l_phys2d_counters},
      {"phys2d_add_force", l_phys2d_add_force},
      {"phys2d_add_force_center", l_phys2d_add_force_center},
      {"phys2d_add_impulse", l_phys2d_add_impulse},
      {"phys2d_add_impulse_center", l_phys2d_add_impulse_center},
      {"phys2d_add_torque", l_phys2d_add_torque},
      {"phys2d_add_angular_impulse", l_phys2d_add_angular_impulse},
      {"phys2d_set_velocity", l_phys2d_set_velocity},
      {"phys2d_teleport", l_phys2d_teleport},
      {"phys2d_set_target", l_phys2d_set_target},
      {"phys2d_set_mass_data", l_phys2d_set_mass_data},
      {NULL, NULL},
  };
  for (const luaL_Reg *r = fns; r->name; ++r) {
    lua_pushcfunction(L, r->func);
    lua_setglobal(L, r->name);
  }
}
