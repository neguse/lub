// phys3d の Lua 面。table を LubPhys3d* の desc に詰め、C API を呼び、結果を
// table に写す。参照は sentinel table { __lub_kind, key, world, body, handle }
// で、解決は key で行う。
#include "lua_phys.h"
#include "lua_phys_table.h"

#include <SDL3/SDL.h>
#include <box3d/math_functions.h>
#include <float.h>
#include <lauxlib.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

static LubContext *g_ctx = NULL;

static int raise_last(lua_State *L) {
  return luaL_error(L, "%s", lub_last_error(g_ctx));
}

// ------------------------------------------------------------ vec3 / quat

static LubVec3 read_vec3_at(lua_State *L, int t, LubVec3 out, bool *has_x,
                            bool *has_y, bool *has_z) {
  static const char *names[3] = {"x", "y", "z"};
  float *comps[3] = {&out.x, &out.y, &out.z};
  bool *flags[3] = {has_x, has_y, has_z};
  for (int i = 0; i < 3; ++i) {
    lua_getfield(L, t, names[i]);
    if (lua_isnumber(L, -1)) {
      *comps[i] = (float)lua_tonumber(L, -1);
      if (flags[i])
        *flags[i] = true;
    }
    lua_pop(L, 1);
  }
  for (int i = 0; i < 3; ++i) {
    lua_rawgeti(L, t, i + 1);
    if (lua_isnumber(L, -1)) {
      *comps[i] = (float)lua_tonumber(L, -1);
      if (flags[i])
        *flags[i] = true;
    }
    lua_pop(L, 1);
  }
  return out;
}

static LubVec3 value_vec3(lua_State *L, int idx, LubVec3 def) {
  if (!lua_istable(L, idx))
    return def;
  return read_vec3_at(L, abs_index(L, idx), def, NULL, NULL, NULL);
}

static LubVec3 value_vec3_optional(lua_State *L, int idx, LubVec3 def,
                                   bool *has_x, bool *has_y, bool *has_z) {
  if (!lua_istable(L, idx))
    return def;
  return read_vec3_at(L, abs_index(L, idx), def, has_x, has_y, has_z);
}

static LubVec3 table_vec3(lua_State *L, int idx, const char *a, const char *b,
                          LubVec3 def) {
  LubVec3 out = def;
  if (!table_get_any(L, idx, a, b))
    return out;
  if (lua_istable(L, -1))
    out = value_vec3(L, lua_gettop(L), def);
  lua_pop(L, 1);
  return out;
}

// 四元数の正規化と euler の合成は Box3D の inline 関数をそのまま使う
// (core の側で同じ値になるように、丸めの規約を揃える)。
static LubQuat quat_from_b3(b3Quat q) {
  return (LubQuat){q.v.x, q.v.y, q.v.z, q.s};
}

static LubQuat value_quat(lua_State *L, int idx, LubQuat def) {
  LubQuat out = def;
  if (!lua_istable(L, idx))
    return out;
  idx = abs_index(L, idx);
  static const char *names[4] = {"x", "y", "z", "w"};
  float *comps[4] = {&out.x, &out.y, &out.z, &out.w};
  for (int i = 0; i < 4; ++i) {
    lua_getfield(L, idx, names[i]);
    if (lua_isnumber(L, -1))
      *comps[i] = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
  }
  for (int i = 0; i < 4; ++i) {
    lua_rawgeti(L, idx, i + 1);
    if (lua_isnumber(L, -1))
      *comps[i] = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
  }
  b3Quat q = {{out.x, out.y, out.z}, out.w};
  return quat_from_b3(b3NormalizeQuat(q));
}

// Box3D に euler の helper は無いので、軸ごとの四元数を XYZ の順で合成する。
static LubQuat quat_from_euler_xyz(LubVec3 e) {
  b3Quat qx = b3MakeQuatFromAxisAngle(b3Vec3_axisX, e.x);
  b3Quat qy = b3MakeQuatFromAxisAngle(b3Vec3_axisY, e.y);
  b3Quat qz = b3MakeQuatFromAxisAngle(b3Vec3_axisZ, e.z);
  return quat_from_b3(b3NormalizeQuat(b3MulQuat(b3MulQuat(qx, qy), qz)));
}

// { quat = {..} } か { euler = {..} }。
static bool table_rotation(lua_State *L, int idx, LubQuat *out) {
  idx = abs_index(L, idx);
  if (table_get_any(L, idx, "quat", NULL)) {
    bool ok = lua_istable(L, -1);
    if (ok)
      *out = value_quat(L, lua_gettop(L), (LubQuat){0, 0, 0, 1});
    lua_pop(L, 1);
    if (ok)
      return true;
  }
  if (table_get_any(L, idx, "euler", NULL)) {
    bool ok = lua_istable(L, -1);
    if (ok)
      *out =
          quat_from_euler_xyz(value_vec3(L, lua_gettop(L), (LubVec3){0, 0, 0}));
    lua_pop(L, 1);
    if (ok)
      return true;
  }
  return false;
}

static void push_vec3(lua_State *L, LubVec3 v) {
  lua_newtable(L);
  set_number(L, "x", v.x);
  set_number(L, "y", v.y);
  set_number(L, "z", v.z);
}

// ------------------------------------------------------------------ refs

static int l_phys3d_begin(lua_State *L);
static int l_phys3d_step(lua_State *L);
static int l_phys3d_world_info(lua_State *L);
static int l_phys3d_body(lua_State *L);
static int l_phys3d_sphere(lua_State *L);
static int l_phys3d_box(lua_State *L);
static int l_phys3d_capsule(lua_State *L);
static int l_phys3d_cylinder(lua_State *L);
static int l_phys3d_cone(lua_State *L);
static int l_phys3d_hull(lua_State *L);
static int l_phys3d_mesh(lua_State *L);
static int l_phys3d_height_field(lua_State *L);
static int l_phys3d_compound(lua_State *L);
static int l_phys3d_joint(lua_State *L);
static int l_phys3d_joint_info(lua_State *L);
static int l_phys3d_joint_force(lua_State *L);
static int l_phys3d_joint_torque(lua_State *L);
static int l_phys3d_joint_angle(lua_State *L);
static int l_phys3d_joint_translation(lua_State *L);
static int l_phys3d_joint_speed(lua_State *L);
static int l_phys3d_joint_length(lua_State *L);
static int l_phys3d_joint_motor_force(lua_State *L);
static int l_phys3d_joint_motor_torque(lua_State *L);
static int l_phys3d_joint_set_motor(lua_State *L);
static int l_phys3d_joint_set_limit(lua_State *L);
static int l_phys3d_joint_set_spring(lua_State *L);
static int l_phys3d_joint_set_target(lua_State *L);
static int l_phys3d_pose(lua_State *L);
static int l_phys3d_velocity(lua_State *L);
static int l_phys3d_mass(lua_State *L);
static int l_phys3d_center(lua_State *L);
static int l_phys3d_world_point(lua_State *L);
static int l_phys3d_local_point(lua_State *L);
static int l_phys3d_velocity_at(lua_State *L);
static int l_phys3d_body_shapes(lua_State *L);
static int l_phys3d_body_joints(lua_State *L);
static int l_phys3d_body_contacts(lua_State *L);
static int l_phys3d_shape_raycast(lua_State *L);
static int l_phys3d_shape_closest_point(lua_State *L);
static int l_phys3d_shape_aabb(lua_State *L);
static int l_phys3d_shape_info(lua_State *L);
static int l_phys3d_shape_set_material(lua_State *L);
static int l_phys3d_shape_set_filter(lua_State *L);
static int l_phys3d_shape_set_events(lua_State *L);
static int l_phys3d_contacts(lua_State *L);
static int l_phys3d_body_events(lua_State *L);
static int l_phys3d_sensors(lua_State *L);
static int l_phys3d_joint_events(lua_State *L);
static int l_phys3d_raycast(lua_State *L);
static int l_phys3d_overlap_aabb(lua_State *L);
static int l_phys3d_overlap_shape(lua_State *L);
static int l_phys3d_shape_cast(lua_State *L);
static int l_phys3d_cast_mover(lua_State *L);
static int l_phys3d_collide_mover(lua_State *L);
static int l_phys3d_profile(lua_State *L);
static int l_phys3d_counters(lua_State *L);
static int l_phys3d_add_force(lua_State *L);
static int l_phys3d_add_force_center(lua_State *L);
static int l_phys3d_add_impulse(lua_State *L);
static int l_phys3d_add_impulse_center(lua_State *L);
static int l_phys3d_add_torque(lua_State *L);
static int l_phys3d_add_angular_impulse(lua_State *L);
static int l_phys3d_set_velocity(lua_State *L);
static int l_phys3d_teleport(lua_State *L);
static int l_phys3d_set_target(lua_State *L);

static void push_world_ref(lua_State *L, LubStr key, LubHandle h) {
  lua_newtable(L);
  lua_pushstring(L, "phys3d_world");
  lua_setfield(L, -2, "__lub_kind");
  set_str_field(L, "key", key);
  set_handle_field(L, h);
  set_cfunc_field(L, "begin", l_phys3d_begin);
  set_cfunc_field(L, "step", l_phys3d_step);
  set_cfunc_field(L, "info", l_phys3d_world_info);
  set_cfunc_field(L, "body", l_phys3d_body);
  set_cfunc_field(L, "joint", l_phys3d_joint);
  set_cfunc_field(L, "pose", l_phys3d_pose);
  set_cfunc_field(L, "contacts", l_phys3d_contacts);
  set_cfunc_field(L, "body_events", l_phys3d_body_events);
  set_cfunc_field(L, "sensors", l_phys3d_sensors);
  set_cfunc_field(L, "joint_events", l_phys3d_joint_events);
  set_cfunc_field(L, "raycast", l_phys3d_raycast);
  set_cfunc_field(L, "overlap_aabb", l_phys3d_overlap_aabb);
  set_cfunc_field(L, "overlap_shape", l_phys3d_overlap_shape);
  set_cfunc_field(L, "shape_cast", l_phys3d_shape_cast);
  set_cfunc_field(L, "cast_mover", l_phys3d_cast_mover);
  set_cfunc_field(L, "collide_mover", l_phys3d_collide_mover);
  set_cfunc_field(L, "profile", l_phys3d_profile);
  set_cfunc_field(L, "counters", l_phys3d_counters);
}

static void push_body_ref(lua_State *L, LubStr world_key, LubStr body_key,
                          LubHandle h) {
  lua_newtable(L);
  lua_pushstring(L, "phys3d_body");
  lua_setfield(L, -2, "__lub_kind");
  set_str_field(L, "world", world_key);
  set_str_field(L, "key", body_key);
  set_handle_field(L, h);
  set_cfunc_field(L, "sphere", l_phys3d_sphere);
  set_cfunc_field(L, "box", l_phys3d_box);
  set_cfunc_field(L, "capsule", l_phys3d_capsule);
  set_cfunc_field(L, "cylinder", l_phys3d_cylinder);
  set_cfunc_field(L, "cone", l_phys3d_cone);
  set_cfunc_field(L, "hull", l_phys3d_hull);
  set_cfunc_field(L, "mesh", l_phys3d_mesh);
  set_cfunc_field(L, "height_field", l_phys3d_height_field);
  set_cfunc_field(L, "compound", l_phys3d_compound);
  set_cfunc_field(L, "pose", l_phys3d_pose);
  set_cfunc_field(L, "velocity", l_phys3d_velocity);
  set_cfunc_field(L, "mass", l_phys3d_mass);
  set_cfunc_field(L, "center", l_phys3d_center);
  set_cfunc_field(L, "world_point", l_phys3d_world_point);
  set_cfunc_field(L, "local_point", l_phys3d_local_point);
  set_cfunc_field(L, "velocity_at", l_phys3d_velocity_at);
  set_cfunc_field(L, "shapes", l_phys3d_body_shapes);
  set_cfunc_field(L, "joints", l_phys3d_body_joints);
  set_cfunc_field(L, "contacts", l_phys3d_body_contacts);
  set_cfunc_field(L, "add_force", l_phys3d_add_force);
  set_cfunc_field(L, "add_force_center", l_phys3d_add_force_center);
  set_cfunc_field(L, "add_impulse", l_phys3d_add_impulse);
  set_cfunc_field(L, "add_impulse_center", l_phys3d_add_impulse_center);
  set_cfunc_field(L, "add_torque", l_phys3d_add_torque);
  set_cfunc_field(L, "add_angular_impulse", l_phys3d_add_angular_impulse);
  set_cfunc_field(L, "set_velocity", l_phys3d_set_velocity);
  set_cfunc_field(L, "teleport", l_phys3d_teleport);
  set_cfunc_field(L, "set_target", l_phys3d_set_target);
}

static void push_shape_ref(lua_State *L, LubStr world_key, LubStr body_key,
                           LubStr shape_key, LubHandle h) {
  lua_newtable(L);
  lua_pushstring(L, "phys3d_shape");
  lua_setfield(L, -2, "__lub_kind");
  set_str_field(L, "world", world_key);
  set_str_field(L, "body", body_key);
  set_str_field(L, "key", shape_key);
  set_handle_field(L, h);
  set_cfunc_field(L, "raycast", l_phys3d_shape_raycast);
  set_cfunc_field(L, "closest_point", l_phys3d_shape_closest_point);
  set_cfunc_field(L, "aabb", l_phys3d_shape_aabb);
  set_cfunc_field(L, "info", l_phys3d_shape_info);
  set_cfunc_field(L, "set_material", l_phys3d_shape_set_material);
  set_cfunc_field(L, "set_filter", l_phys3d_shape_set_filter);
  set_cfunc_field(L, "set_events", l_phys3d_shape_set_events);
}

static void push_joint_ref(lua_State *L, LubStr world_key, LubStr joint_key,
                           LubHandle h) {
  lua_newtable(L);
  lua_pushstring(L, "phys3d_joint");
  lua_setfield(L, -2, "__lub_kind");
  set_str_field(L, "world", world_key);
  set_str_field(L, "key", joint_key);
  set_handle_field(L, h);
  set_cfunc_field(L, "info", l_phys3d_joint_info);
  set_cfunc_field(L, "force", l_phys3d_joint_force);
  set_cfunc_field(L, "torque", l_phys3d_joint_torque);
  set_cfunc_field(L, "angle", l_phys3d_joint_angle);
  set_cfunc_field(L, "translation", l_phys3d_joint_translation);
  set_cfunc_field(L, "speed", l_phys3d_joint_speed);
  set_cfunc_field(L, "length", l_phys3d_joint_length);
  set_cfunc_field(L, "motor_force", l_phys3d_joint_motor_force);
  set_cfunc_field(L, "motor_torque", l_phys3d_joint_motor_torque);
  set_cfunc_field(L, "set_motor", l_phys3d_joint_set_motor);
  set_cfunc_field(L, "set_limit", l_phys3d_joint_set_limit);
  set_cfunc_field(L, "set_spring", l_phys3d_joint_set_spring);
  set_cfunc_field(L, "set_target", l_phys3d_joint_set_target);
}

static LubHandle ref_world(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_world"))
    luaL_error(L, "expected Phys3d WorldRef");
  return lub_phys3d_world_find(g_ctx, ref_field(L, idx, "key"));
}

static LubHandle check_world(lua_State *L, int idx) {
  LubHandle h = ref_world(L, idx);
  if (!h) {
    LubStr k = ref_field(L, idx, "key");
    luaL_error(L, "phys3d world not found: %s", k.ptr ? k.ptr : "?");
  }
  return h;
}

static LubHandle ref_body(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_body"))
    luaL_error(L, "expected Phys3d BodyRef");
  LubHandle w = lub_phys3d_world_find(g_ctx, ref_field(L, idx, "world"));
  return w ? lub_phys3d_body_find(g_ctx, w, ref_field(L, idx, "key")) : 0;
}

static LubHandle check_body(lua_State *L, int idx) {
  LubHandle h = ref_body(L, idx);
  if (!h) {
    LubStr w = ref_field(L, idx, "world");
    LubStr k = ref_field(L, idx, "key");
    luaL_error(L, "phys3d body not found: %s/%s", w.ptr ? w.ptr : "?",
               k.ptr ? k.ptr : "?");
  }
  return h;
}

static LubHandle ref_shape(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_shape"))
    luaL_error(L, "expected Phys3d ShapeRef");
  LubHandle w = lub_phys3d_world_find(g_ctx, ref_field(L, idx, "world"));
  LubHandle b =
      w ? lub_phys3d_body_find(g_ctx, w, ref_field(L, idx, "body")) : 0;
  return b ? lub_phys3d_shape_find(g_ctx, b, ref_field(L, idx, "key")) : 0;
}

static LubHandle check_shape(lua_State *L, int idx) {
  LubHandle h = ref_shape(L, idx);
  if (!h) {
    LubStr w = ref_field(L, idx, "world");
    LubStr b = ref_field(L, idx, "body");
    LubStr k = ref_field(L, idx, "key");
    luaL_error(L, "phys3d shape not found: %s/%s/%s", w.ptr ? w.ptr : "?",
               b.ptr ? b.ptr : "?", k.ptr ? k.ptr : "?");
  }
  return h;
}

static LubHandle ref_joint(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_joint"))
    luaL_error(L, "expected Phys3d JointRef");
  LubHandle w = lub_phys3d_world_find(g_ctx, ref_field(L, idx, "world"));
  return w ? lub_phys3d_joint_find(g_ctx, w, ref_field(L, idx, "key")) : 0;
}

static LubHandle check_joint(lua_State *L, int idx) {
  LubHandle h = ref_joint(L, idx);
  if (!h) {
    LubStr w = ref_field(L, idx, "world");
    LubStr k = ref_field(L, idx, "key");
    luaL_error(L, "phys3d joint not found: %s/%s", w.ptr ? w.ptr : "?",
               k.ptr ? k.ptr : "?");
  }
  return h;
}

// ------------------------------------------------------------- callbacks

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
  SDL_Log("phys3d %s callback error: %s", name,
          message ? message : "unknown error");
  *logged = true;
}

static void push_shape_part(lua_State *L, const LubPhys3dShapePart *p,
                            bool with_kind);

static bool cb_filter(void *user, const LubPhys3dShapePart *a,
                      const LubPhys3dShapePart *b) {
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

static bool cb_pre_solve(void *user, const LubPhys3dPreSolve *c) {
  LuaCallbacks *cb = (LuaCallbacks *)user;
  lua_State *L = cb->L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, cb->pre_solve_ref);
  lua_newtable(L);
  push_shape_part(L, &c->a, false);
  lua_setfield(L, -2, "a");
  push_shape_part(L, &c->b, false);
  lua_setfield(L, -2, "b");
  set_number(L, "x", c->x);
  set_number(L, "y", c->y);
  set_number(L, "z", c->z);
  set_number(L, "nx", c->nx);
  set_number(L, "ny", c->ny);
  set_number(L, "nz", c->nz);
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
                               int32_t material) {
  lua_newtable(L);
  set_number(L, field, value);
  set_integer(L, "material", material);
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

static void parse_callbacks(lua_State *L, int opts_idx, LubStr key,
                            LubPhys3dCallbacks *out) {
  memset(out, 0, sizeof(*out));
  LuaCallbacks *cb = callbacks_for(L, key);
  if (!cb)
    luaL_error(L, "phys3d_world: out of memory");
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

static int l_phys3d_world(lua_State *L) {
  LubStr key = lstr_arg(L, 1);
  LubPhys3dWorldDesc d;
  lub_phys3d_world_desc_init(&d);
  if (lua_istable(L, 2)) {
    int32_t v = 0;
    if (table_has_int(L, 2, "version", NULL, &v)) {
      d.version = v;
      d.has_version = true;
    }
    d.gravity = table_vec3(L, 2, "gravity", NULL, d.gravity);
    d.fixed_dt = table_number(L, 2, "fixed_dt", "fixedDt", d.fixed_dt);
    d.substeps = table_int(L, 2, "substeps", NULL, d.substeps);
    d.max_steps = table_int(L, 2, "max_steps", "maxSteps", d.max_steps);
    d.sleep = table_bool(L, 2, "sleep", NULL, d.sleep);
    d.continuous = table_bool(L, 2, "continuous", NULL, d.continuous);
    float t = 0;
    if (table_number_optional(L, 2, "hit_event_threshold", "hitEventThreshold",
                              &t)) {
      d.has_hit_event_threshold = true;
      d.hit_event_threshold = t;
    }
    if (d.fixed_dt <= 0.0f)
      d.fixed_dt = 1.0f / 60.0f;
    if (d.substeps <= 0)
      d.substeps = 4;
    if (d.max_steps <= 0)
      d.max_steps = 4;
  }
  parse_callbacks(L, 2, key, &d.callbacks);
  LubHandle h = 0;
  if (lub_phys3d_world(g_ctx, key, &d, &h) != LUB_OK)
    return raise_last(L);
  push_world_ref(L, key, h);
  return 1;
}

static int l_phys3d_begin(lua_State *L) {
  LubHandle w = check_world(L, 1);
  bool prune = lua_istable(L, 2) ? table_bool(L, 2, "prune", NULL, true) : true;
  if (lub_phys3d_begin(g_ctx, w, prune) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys3d_world_info(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  LubPhys3dWorldInfo info;
  LubStatus st = lub_phys3d_world_info(g_ctx, w, &info);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  set_str_field(L, "key", info.key);
  set_boolean(L, "valid", info.valid);
  set_integer(L, "version", info.version);
  set_integer(L, "generation", info.generation);
  set_boolean(L, "begun", info.begun);
  set_boolean(L, "prune", info.prune);
  set_number(L, "fixed_dt", info.fixed_dt);
  set_integer(L, "substeps", info.substeps);
  set_integer(L, "max_steps", info.max_steps);
  set_number(L, "accumulator", info.accumulator);
  set_integer(L, "pending_commands", info.pending_commands);
  if (!info.valid)
    return 1;
  push_vec3(L, info.gravity);
  lua_setfield(L, -2, "gravity");
  set_number(L, "gx", info.gravity.x);
  set_number(L, "gy", info.gravity.y);
  set_number(L, "gz", info.gravity.z);
  set_boolean(L, "sleep", info.sleep);
  set_boolean(L, "continuous", info.continuous);
  set_boolean(L, "warm_starting", info.warm_starting);
  set_number(L, "restitution_threshold", info.restitution_threshold);
  set_number(L, "hit_event_threshold", info.hit_event_threshold);
  set_number(L, "maximum_linear_speed", info.maximum_linear_speed);
  set_integer(L, "awake_body_count", info.awake_body_count);
  return 1;
}

// ------------------------------------------------------------------ body

static int l_phys3d_body(lua_State *L) {
  LubHandle w = check_world(L, 1);
  LubStr key = lstr_arg(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  LubPhys3dBodyDesc d;
  lub_phys3d_body_desc_init(&d);
  int32_t v = 0;
  if (table_has_int(L, 3, "version", NULL, &v)) {
    d.version = v;
    d.has_version = true;
  }
  if (table_get_any(L, 3, "type", NULL)) {
    d.type = (int32_t)luaL_checkinteger(L, -1);
    lua_pop(L, 1);
  }
  if (table_get_any(L, 3, "motion_locks", "motionLocks")) {
    if (lua_istable(L, -1)) {
      int t = lua_gettop(L);
      d.lock_linear_x = table_bool(L, t, "linear_x", NULL, false);
      d.lock_linear_y = table_bool(L, t, "linear_y", NULL, false);
      d.lock_linear_z = table_bool(L, t, "linear_z", NULL, false);
      d.lock_angular_x = table_bool(L, t, "angular_x", NULL, false);
      d.lock_angular_y = table_bool(L, t, "angular_y", NULL, false);
      d.lock_angular_z = table_bool(L, t, "angular_z", NULL, false);
    }
    lua_pop(L, 1);
  }
  d.bullet = table_bool(L, 3, "bullet", NULL, false);
  bool flag = false;
  if (table_bool_optional(L, 3, "enabled", NULL, &flag)) {
    d.enabled = flag;
    d.has_enabled = true;
  }
  if (table_bool_optional(L, 3, "awake", NULL, &flag)) {
    d.awake = flag;
    d.has_awake = true;
  }
  if (table_bool_optional(L, 3, "sleep", "enableSleep", &flag)) {
    d.sleep = flag;
    d.has_sleep = true;
  }
  d.gravity_scale = table_number(L, 3, "gravity_scale", "gravityScale", 1.0f);
  d.linear_damping =
      table_number(L, 3, "linear_damping", "linearDamping", 0.0f);
  d.angular_damping =
      table_number(L, 3, "angular_damping", "angularDamping", 0.0f);
  float t = 0;
  if (table_number_optional(L, 3, "sleep_threshold", "sleepThreshold", &t)) {
    d.sleep_threshold = t;
    d.has_sleep_threshold = true;
  }
  if (table_get_any(L, 3, "initial", NULL)) {
    if (lua_istable(L, -1)) {
      int t2 = lua_gettop(L);
      d.position.x = table_number(L, t2, "x", NULL, d.position.x);
      d.position.y = table_number(L, t2, "y", NULL, d.position.y);
      d.position.z = table_number(L, t2, "z", NULL, d.position.z);
      table_rotation(L, t2, &d.rotation);
      d.linear_velocity.x =
          table_number(L, t2, "vx", NULL, d.linear_velocity.x);
      d.linear_velocity.y =
          table_number(L, t2, "vy", NULL, d.linear_velocity.y);
      d.linear_velocity.z =
          table_number(L, t2, "vz", NULL, d.linear_velocity.z);
      d.angular_velocity.x =
          table_number(L, t2, "wx", NULL, d.angular_velocity.x);
      d.angular_velocity.y =
          table_number(L, t2, "wy", NULL, d.angular_velocity.y);
      d.angular_velocity.z =
          table_number(L, t2, "wz", NULL, d.angular_velocity.z);
      d.initial_awake = table_bool(L, t2, "awake", NULL, d.initial_awake);
    }
    lua_pop(L, 1);
  }
  LubHandle h = 0;
  if (lub_phys3d_body(g_ctx, w, key, &d, &h) != LUB_OK)
    return raise_last(L);
  push_body_ref(L, ref_field(L, 1, "key"), key, h);
  return 1;
}

// ----------------------------------------------------------------- shape

static void parse_filter_field(lua_State *L, int idx, LubPhys3dFilter *f) {
  if (!table_get_any(L, idx, "filter", NULL))
    return;
  if (lua_istable(L, -1))
    parse_filter_table(L, "phys3d", lua_gettop(L), &f->category_bits,
                       &f->mask_bits, &f->group_index);
  lua_pop(L, 1);
}

static LubPhys3dQueryFilter parse_query_filter(lua_State *L, int idx) {
  LubPhys3dQueryFilter f = {1u, UINT64_MAX};
  if (!lua_istable(L, idx))
    return f;
  if (table_get_any(L, idx, "filter", NULL)) {
    if (lua_istable(L, -1))
      parse_filter_table(L, "phys3d", lua_gettop(L), &f.category_bits,
                         &f.mask_bits, NULL);
    lua_pop(L, 1);
  } else {
    parse_filter_table(L, "phys3d", idx, &f.category_bits, &f.mask_bits, NULL);
  }
  return f;
}

static void parse_shape_desc(lua_State *L, int idx, LubPhys3dShapeDesc *d) {
  lub_phys3d_shape_desc_init(d);
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

static int l_phys3d_sphere(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dSphereDesc d;
  parse_shape_desc(L, 3, &d.shape);
  d.r = table_number(L, 3, "r", "radius", 0.0f);
  d.offset = table_vec3(L, 3, "offset", NULL, (LubVec3){0, 0, 0});
  LubHandle h = 0;
  return push_shape_result(L, lub_phys3d_sphere(g_ctx, b, key, &d, &h), key, h);
}

static int l_phys3d_box(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dBoxDesc d;
  parse_shape_desc(L, 3, &d.shape);
  d.hx = table_number(L, 3, "hx", NULL, 0.0f);
  d.hy = table_number(L, 3, "hy", NULL, 0.0f);
  d.hz = table_number(L, 3, "hz", NULL, 0.0f);
  d.offset = table_vec3(L, 3, "offset", NULL, (LubVec3){0, 0, 0});
  d.has_rotation = false;
  d.rotation = (LubQuat){0, 0, 0, 1};
  if (table_get_any(L, 3, "quat", NULL)) {
    if (lua_istable(L, -1)) {
      d.rotation = value_quat(L, lua_gettop(L), d.rotation);
      d.has_rotation = true;
    }
    lua_pop(L, 1);
  }
  LubHandle h = 0;
  return push_shape_result(L, lub_phys3d_box(g_ctx, b, key, &d, &h), key, h);
}

static int l_phys3d_capsule(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dCapsuleDesc d;
  parse_shape_desc(L, 3, &d.shape);
  d.a = table_vec3(L, 3, "a", NULL, (LubVec3){0, 0, 0});
  d.b = table_vec3(L, 3, "b", NULL, (LubVec3){0, 0, 0});
  d.r = table_number(L, 3, "r", "radius", 0.0f);
  LubHandle h = 0;
  return push_shape_result(L, lub_phys3d_capsule(g_ctx, b, key, &d, &h), key,
                           h);
}

static int l_phys3d_cylinder(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dCylinderDesc d;
  parse_shape_desc(L, 3, &d.shape);
  d.height = table_number(L, 3, "height", NULL, 0.0f);
  d.radius = table_number(L, 3, "radius", "r", 0.0f);
  d.sides = table_int(L, 3, "sides", NULL, 16);
  d.y_offset = table_number(L, 3, "y_offset", "yOffset", -0.5f * d.height);
  LubHandle h = 0;
  return push_shape_result(L, lub_phys3d_cylinder(g_ctx, b, key, &d, &h), key,
                           h);
}

static int l_phys3d_cone(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dConeDesc d;
  parse_shape_desc(L, 3, &d.shape);
  d.height = table_number(L, 3, "height", NULL, 0.0f);
  d.radius1 = table_number(L, 3, "radius1", NULL, 0.0f);
  d.radius2 = table_number(L, 3, "radius2", NULL, 0.0f);
  d.slices = table_int(L, 3, "slices", NULL, 16);
  LubHandle h = 0;
  return push_shape_result(L, lub_phys3d_cone(g_ctx, b, key, &d, &h), key, h);
}

// points = { x, y, z, ... } か { {x, y, z}, ... }。SDL_malloc した xyz の列。
static float *read_point_list3(lua_State *L, int idx, const char *fn,
                               int *out_count) {
  if (!table_get_any(L, idx, "points", NULL))
    luaL_error(L, "%s: points table is required", fn);
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "%s: points must be a table", fn);
  }
  int pidx = lua_gettop(L);
  int raw_len = (int)lua_rawlen(L, pidx);
  bool flat_numbers = false;
  if (raw_len > 0) {
    lua_rawgeti(L, pidx, 1);
    flat_numbers = lua_isnumber(L, -1);
    lua_pop(L, 1);
  }
  if (flat_numbers && (raw_len % 3) != 0)
    luaL_error(L, "%s: flat points must have x/y/z triples", fn);
  int count = flat_numbers ? raw_len / 3 : raw_len;
  if (count < 4)
    luaL_error(L, "%s: at least 4 points are required", fn);
  float *points = (float *)SDL_malloc(sizeof(float) * 3 * (size_t)count);
  if (!points)
    luaL_error(L, "%s: out of memory", fn);
  if (flat_numbers) {
    for (int i = 0; i < count * 3; ++i) {
      lua_rawgeti(L, pidx, i + 1);
      points[i] = (float)luaL_checknumber(L, -1);
      lua_pop(L, 1);
    }
  } else {
    for (int i = 0; i < count; ++i) {
      lua_rawgeti(L, pidx, i + 1);
      luaL_checktype(L, -1, LUA_TTABLE);
      LubVec3 p = value_vec3(L, lua_gettop(L), (LubVec3){0, 0, 0});
      points[i * 3] = p.x;
      points[i * 3 + 1] = p.y;
      points[i * 3 + 2] = p.z;
      lua_pop(L, 1);
    }
  }
  lua_pop(L, 1);
  *out_count = count;
  return points;
}

static int l_phys3d_hull(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dHullDesc d;
  parse_shape_desc(L, 3, &d.shape);
  if (!d.shape.has_version)
    return luaL_error(L, "phys3d_hull: explicit version is required");
  int count = 0;
  float *points = read_point_list3(L, 3, "phys3d_hull", &count);
  d.points = points;
  d.point_count = count;
  d.max_vertices = table_int(L, 3, "max_vertices", "maxVertices", 255);
  LubHandle h = 0;
  LubStatus st = lub_phys3d_hull(g_ctx, b, key, &d, &h);
  SDL_free(points);
  return push_shape_result(L, st, key, h);
}

static float *read_flat_numbers(lua_State *L, int idx, const char *fn,
                                const char *field_a, const char *field_b,
                                int *out_count) {
  if (!table_get_any(L, idx, field_a, field_b))
    luaL_error(L, "%s: %s array is required", fn, field_a);
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "%s: %s must be a table", fn, field_a);
  }
  int t = lua_gettop(L);
  int count = (int)lua_rawlen(L, t);
  if (count <= 0) {
    lua_pop(L, 1);
    luaL_error(L, "%s: %s must not be empty", fn, field_a);
  }
  float *values = (float *)SDL_malloc(sizeof(*values) * (size_t)count);
  if (!values) {
    lua_pop(L, 1);
    luaL_error(L, "%s: out of memory", fn);
  }
  for (int i = 0; i < count; ++i) {
    lua_rawgeti(L, t, i + 1);
    values[i] = (float)luaL_checknumber(L, -1);
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  *out_count = count;
  return values;
}

static int32_t *read_flat_ints(lua_State *L, int idx, const char *fn,
                               const char *field_a, const char *field_b,
                               bool required, int *out_count) {
  *out_count = 0;
  if (!table_get_any(L, idx, field_a, field_b)) {
    if (required)
      luaL_error(L, "%s: %s array is required", fn, field_a);
    return NULL;
  }
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "%s: %s must be a table", fn, field_a);
  }
  int t = lua_gettop(L);
  int count = (int)lua_rawlen(L, t);
  if (count <= 0) {
    lua_pop(L, 1);
    if (required)
      luaL_error(L, "%s: %s must not be empty", fn, field_a);
    return NULL;
  }
  int32_t *values = (int32_t *)SDL_malloc(sizeof(*values) * (size_t)count);
  if (!values) {
    lua_pop(L, 1);
    luaL_error(L, "%s: out of memory", fn);
  }
  for (int i = 0; i < count; ++i) {
    lua_rawgeti(L, t, i + 1);
    values[i] = (int32_t)luaL_checkinteger(L, -1);
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  *out_count = count;
  return values;
}

// materials = { { friction, restitution, material }, ... } か整数の列。
static LubPhys3dSurfaceMaterial *read_materials(lua_State *L, int idx,
                                                const char *fn,
                                                const LubPhys3dShapeDesc *desc,
                                                int *out_count) {
  *out_count = 0;
  if (!table_get_any(L, idx, "materials", NULL))
    return NULL;
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "%s: materials must be a table", fn);
  }
  int midx = lua_gettop(L);
  int count = (int)lua_rawlen(L, midx);
  if (count < 1 || count > 255) {
    lua_pop(L, 1);
    luaL_error(L, "%s: materials length must be in [1, 255]", fn);
  }
  LubPhys3dSurfaceMaterial *materials = (LubPhys3dSurfaceMaterial *)SDL_malloc(
      sizeof(*materials) * (size_t)count);
  if (!materials) {
    lua_pop(L, 1);
    luaL_error(L, "%s: out of memory", fn);
  }
  for (int i = 0; i < count; ++i) {
    materials[i].friction = desc->friction;
    materials[i].restitution = desc->restitution;
    materials[i].material_id = desc->material_id;
    lua_rawgeti(L, midx, i + 1);
    if (lua_istable(L, -1)) {
      int m = lua_gettop(L);
      materials[i].friction =
          table_number(L, m, "friction", NULL, materials[i].friction);
      materials[i].restitution =
          table_number(L, m, "restitution", NULL, materials[i].restitution);
      materials[i].material_id =
          table_int(L, m, "material", "materialId", materials[i].material_id);
      materials[i].material_id = table_int(
          L, m, "user_material_id", "userMaterialId", materials[i].material_id);
    } else if (lua_isinteger(L, -1) || lua_isnumber(L, -1)) {
      materials[i].material_id = (int32_t)lua_tointeger(L, -1);
    }
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  *out_count = count;
  return materials;
}

static int l_phys3d_mesh(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dMeshDesc d;
  memset(&d, 0, sizeof(d));
  parse_shape_desc(L, 3, &d.shape);
  if (!d.shape.has_version)
    return luaL_error(L, "phys3d_mesh: explicit version is required");
  int position_count = 0;
  float *positions = read_flat_numbers(L, 3, "phys3d_mesh", "positions", NULL,
                                       &position_count);
  if ((position_count % 3) != 0 || position_count < 9) {
    SDL_free(positions);
    return luaL_error(
        L, "phys3d_mesh: positions must hold at least 3 x/y/z triples");
  }
  int index_count = 0;
  int32_t *indices =
      read_flat_ints(L, 3, "phys3d_mesh", "indices", NULL, true, &index_count);
  int material_count = 0;
  LubPhys3dSurfaceMaterial *materials =
      read_materials(L, 3, "phys3d_mesh", &d.shape, &material_count);
  int material_index_count = 0;
  int32_t *material_indices =
      read_flat_ints(L, 3, "phys3d_mesh", "material_indices", "materialIndices",
                     false, &material_index_count);
  d.positions = positions;
  d.vertex_count = position_count / 3;
  d.indices = indices;
  d.index_count = index_count;
  d.scale = table_vec3(L, 3, "scale", NULL, (LubVec3){1, 1, 1});
  d.weld_vertices = table_bool(L, 3, "weld_vertices", "weldVertices", false);
  d.weld_tolerance =
      table_number(L, 3, "weld_tolerance", "weldTolerance", 0.0f);
  d.use_median_split =
      table_bool(L, 3, "use_median_split", "useMedianSplit", false);
  d.identify_edges = table_bool(L, 3, "identify_edges", "identifyEdges", true);
  d.materials = materials;
  d.material_count = material_count;
  d.material_indices = material_indices;
  d.material_index_count = material_index_count;
  LubHandle h = 0;
  LubStatus st = lub_phys3d_mesh(g_ctx, b, key, &d, &h);
  SDL_free(positions);
  SDL_free(indices);
  SDL_free(materials);
  SDL_free(material_indices);
  return push_shape_result(L, st, key, h);
}

static int l_phys3d_height_field(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dHeightFieldDesc d;
  memset(&d, 0, sizeof(d));
  parse_shape_desc(L, 3, &d.shape);
  if (!d.shape.has_version)
    return luaL_error(L, "phys3d_height_field: explicit version is required");
  d.x_count = table_int(L, 3, "x_count", "xCount", 0);
  d.z_count = table_int(L, 3, "z_count", "zCount", 0);
  if (d.x_count < 2 || d.z_count < 2)
    return luaL_error(L,
                      "phys3d_height_field: x_count and z_count must be >= 2");
  int height_count = 0;
  float *heights = read_flat_numbers(L, 3, "phys3d_height_field", "heights",
                                     NULL, &height_count);
  if (height_count != d.x_count * d.z_count) {
    SDL_free(heights);
    return luaL_error(
        L, "phys3d_height_field: heights length must be x_count * z_count");
  }
  d.heights = heights;
  float cell_width = table_number(L, 3, "cell_width", "cellWidth", 1.0f);
  d.scale =
      table_vec3(L, 3, "scale", NULL, (LubVec3){cell_width, 1.0f, cell_width});
  d.has_min_height =
      table_number_optional(L, 3, "min_height", "minHeight", &d.min_height);
  d.has_max_height =
      table_number_optional(L, 3, "max_height", "maxHeight", &d.max_height);
  d.clockwise_winding =
      table_bool(L, 3, "clockwise_winding", "clockwiseWinding", false);
  LubHandle h = 0;
  LubStatus st = lub_phys3d_height_field(g_ctx, b, key, &d, &h);
  SDL_free(heights);
  return push_shape_result(L, st, key, h);
}

static int l_phys3d_compound(lua_State *L) {
  LubHandle b = check_body(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dCompoundDesc d;
  memset(&d, 0, sizeof(d));
  parse_shape_desc(L, 3, &d.shape);
  if (!d.shape.has_version)
    return luaL_error(L, "phys3d_compound: explicit version is required");
  if (!table_get_any(L, 3, "children", NULL))
    return luaL_error(L, "phys3d_compound: children table is required");
  if (!lua_istable(L, -1))
    return luaL_error(L, "phys3d_compound: children must be a table");
  int cidx = lua_gettop(L);
  int count = (int)lua_rawlen(L, cidx);
  if (count <= 0)
    return luaL_error(L, "phys3d_compound: children must not be empty");
  LubPhys3dCompoundChild *children = (LubPhys3dCompoundChild *)SDL_calloc(
      (size_t)count, sizeof(LubPhys3dCompoundChild));
  if (!children)
    return luaL_error(L, "phys3d_compound: out of memory");
  for (int i = 0; i < count; ++i) {
    LubPhys3dCompoundChild *c = &children[i];
    c->rotation = (LubQuat){0, 0, 0, 1};
    lua_rawgeti(L, cidx, i + 1);
    if (!lua_istable(L, -1)) {
      SDL_free(children);
      return luaL_error(L, "phys3d_compound: child %d must be a table", i + 1);
    }
    int child = lua_gettop(L);
    if (table_get_any(L, child, "pose", NULL)) {
      if (lua_istable(L, -1)) {
        int t = lua_gettop(L);
        c->position.x = table_number(L, t, "x", NULL, 0.0f);
        c->position.y = table_number(L, t, "y", NULL, 0.0f);
        c->position.z = table_number(L, t, "z", NULL, 0.0f);
        table_rotation(L, t, &c->rotation);
      }
      lua_pop(L, 1);
    }
    c->material.friction =
        table_number(L, child, "friction", NULL, d.shape.friction);
    c->material.restitution =
        table_number(L, child, "restitution", NULL, d.shape.restitution);
    int material_id =
        table_int(L, child, "material", "materialId", d.shape.material_id);
    c->material.material_id =
        table_int(L, child, "user_material_id", "userMaterialId", material_id);
    if (table_get_any(L, child, "sphere", NULL)) {
      if (!lua_istable(L, -1)) {
        SDL_free(children);
        return luaL_error(L, "phys3d_compound: sphere child must be a table");
      }
      int t = lua_gettop(L);
      c->kind = LUB_PHYS3D_COMPOUND_CHILD_KIND_SPHERE;
      c->r = table_number(L, t, "r", "radius", 0.0f);
      c->center = table_vec3(L, t, "center", NULL, (LubVec3){0, 0, 0});
      lua_pop(L, 1);
    } else if (table_get_any(L, child, "capsule", NULL)) {
      if (!lua_istable(L, -1)) {
        SDL_free(children);
        return luaL_error(L, "phys3d_compound: capsule child must be a table");
      }
      int t = lua_gettop(L);
      c->kind = LUB_PHYS3D_COMPOUND_CHILD_KIND_CAPSULE;
      c->a = table_vec3(L, t, "a", NULL, (LubVec3){0, 0, 0});
      c->b = table_vec3(L, t, "b", NULL, (LubVec3){0, 0, 0});
      c->r = table_number(L, t, "r", "radius", 0.0f);
      lua_pop(L, 1);
    } else if (table_get_any(L, child, "box", NULL)) {
      if (!lua_istable(L, -1)) {
        SDL_free(children);
        return luaL_error(L, "phys3d_compound: box child must be a table");
      }
      int t = lua_gettop(L);
      c->kind = LUB_PHYS3D_COMPOUND_CHILD_KIND_BOX;
      c->hx = table_number(L, t, "hx", NULL, 0.0f);
      c->hy = table_number(L, t, "hy", NULL, 0.0f);
      c->hz = table_number(L, t, "hz", NULL, 0.0f);
      lua_pop(L, 1);
    } else {
      SDL_free(children);
      return luaL_error(
          L, "phys3d_compound: child %d must have sphere, box, or capsule",
          i + 1);
    }
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  d.children = children;
  d.child_count = count;
  LubHandle h = 0;
  LubStatus st = lub_phys3d_compound(g_ctx, b, key, &d, &h);
  SDL_free(children);
  return push_shape_result(L, st, key, h);
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
    return LUB_PHYS3D_JOINT_TYPE_DISTANCE;
  if (strcmp(type, "filter") == 0)
    return LUB_PHYS3D_JOINT_TYPE_FILTER;
  if (strcmp(type, "motor") == 0)
    return LUB_PHYS3D_JOINT_TYPE_MOTOR;
  if (strcmp(type, "parallel") == 0)
    return LUB_PHYS3D_JOINT_TYPE_PARALLEL;
  if (strcmp(type, "prismatic") == 0)
    return LUB_PHYS3D_JOINT_TYPE_PRISMATIC;
  if (strcmp(type, "revolute") == 0 || strcmp(type, "hinge") == 0)
    return LUB_PHYS3D_JOINT_TYPE_REVOLUTE;
  if (strcmp(type, "spherical") == 0)
    return LUB_PHYS3D_JOINT_TYPE_SPHERICAL;
  if (strcmp(type, "weld") == 0)
    return LUB_PHYS3D_JOINT_TYPE_WELD;
  if (strcmp(type, "wheel") == 0)
    return LUB_PHYS3D_JOINT_TYPE_WHEEL;
  luaL_error(L, "phys3d_joint: unknown joint type '%s'", type);
  return LUB_PHYS3D_JOINT_TYPE_REVOLUTE;
}

static const char *joint_kind_name(int32_t kind) {
  switch (kind) {
  case LUB_PHYS3D_JOINT_TYPE_DISTANCE:
    return "distance";
  case LUB_PHYS3D_JOINT_TYPE_FILTER:
    return "filter";
  case LUB_PHYS3D_JOINT_TYPE_MOTOR:
    return "motor";
  case LUB_PHYS3D_JOINT_TYPE_PARALLEL:
    return "parallel";
  case LUB_PHYS3D_JOINT_TYPE_PRISMATIC:
    return "prismatic";
  case LUB_PHYS3D_JOINT_TYPE_REVOLUTE:
    return "revolute";
  case LUB_PHYS3D_JOINT_TYPE_SPHERICAL:
    return "spherical";
  case LUB_PHYS3D_JOINT_TYPE_WELD:
    return "weld";
  case LUB_PHYS3D_JOINT_TYPE_WHEEL:
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

static LubVec3 nested_vec3(lua_State *L, int idx, const char *table_name,
                           const char *a, const char *b, LubVec3 def) {
  LubVec3 out = def;
  if (table_get_any(L, idx, table_name, NULL)) {
    if (lua_istable(L, -1))
      out = table_vec3(L, lua_gettop(L), a, b, out);
    lua_pop(L, 1);
  }
  return out;
}

static LubHandle joint_body_from_value(lua_State *L, LubHandle w, int idx,
                                       const char *field_name) {
  if (is_ref(L, idx, "phys3d_body"))
    return check_body(L, idx);
  if (lua_isstring(L, idx)) {
    size_t n = 0;
    const char *s = lua_tolstring(L, idx, &n);
    LubStr key = {s, (int32_t)n};
    return lub_phys3d_body_find(g_ctx, w, key);
  }
  return luaL_error(L, "phys3d_joint: missing body field '%s'", field_name), 0;
}

static LubHandle joint_body_field(lua_State *L, LubHandle w, int idx,
                                  const char *a, const char *b, const char *c) {
  if (!table_get_any(L, idx, a, b)) {
    if (!c || !table_get_any(L, idx, c, NULL))
      luaL_error(L, "phys3d_joint: missing body field '%s'", a);
  }
  LubHandle body = joint_body_from_value(L, w, lua_gettop(L), a);
  lua_pop(L, 1);
  return body;
}

// { x, y, z, quat = {..} | euler = {..} } の local frame。
static bool table_local_frame(lua_State *L, int idx, const char *a,
                              const char *b, LubVec3 *position,
                              LubQuat *rotation) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_istable(L, -1);
  if (ok) {
    int t = lua_gettop(L);
    position->x = table_number(L, t, "x", NULL, 0.0f);
    position->y = table_number(L, t, "y", NULL, 0.0f);
    position->z = table_number(L, t, "z", NULL, 0.0f);
    *rotation = (LubQuat){0, 0, 0, 1};
    table_rotation(L, t, rotation);
  }
  lua_pop(L, 1);
  return ok;
}

static bool table_world_anchor(lua_State *L, int idx, const char *a,
                               const char *b, LubVec3 *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_istable(L, -1);
  if (ok)
    *out = value_vec3(L, lua_gettop(L), (LubVec3){0, 0, 0});
  lua_pop(L, 1);
  return ok;
}

static bool table_quat_field(lua_State *L, int idx, const char *a,
                             const char *b, LubQuat *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_istable(L, -1);
  if (ok) {
    int t = lua_gettop(L);
    if (!table_rotation(L, t, out))
      *out = value_quat(L, t, *out);
  }
  lua_pop(L, 1);
  return ok;
}

static void parse_joint_desc(lua_State *L, LubHandle w, int idx,
                             LubPhys3dJointDesc *d) {
  luaL_checktype(L, idx, LUA_TTABLE);
  lub_phys3d_joint_desc_init(d, parse_joint_kind(L, idx));
  int32_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    d->version = v;
    d->has_version = true;
  }
  d->body_a = joint_body_field(L, w, idx, "a", "body_a", "bodyA");
  d->body_b = joint_body_field(L, w, idx, "b", "body_b", "bodyB");
  if (table_get_any(L, idx, "axis", NULL)) {
    if (lua_istable(L, -1)) {
      d->axis = value_vec3(L, lua_gettop(L), (LubVec3){0, 0, 0});
      d->has_axis = true;
    }
    lua_pop(L, 1);
  }
  d->has_anchor_a =
      table_world_anchor(L, idx, "anchor_a", "anchorA", &d->anchor_a);
  d->has_frame_a = table_local_frame(
      L, idx, "frame_a", "frameA", &d->frame_a_position, &d->frame_a_rotation);
  d->has_anchor_b =
      table_world_anchor(L, idx, "anchor_b", "anchorB", &d->anchor_b);
  d->has_frame_b = table_local_frame(
      L, idx, "frame_b", "frameB", &d->frame_b_position, &d->frame_b_rotation);

  d->collide_connected =
      table_bool(L, idx, "collide_connected", "collideConnected", false);
  d->force_threshold = table_number(L, idx, "force_threshold", "forceThreshold",
                                    d->force_threshold);
  d->torque_threshold = table_number(L, idx, "torque_threshold",
                                     "torqueThreshold", d->torque_threshold);
  float tuning = 0.0f;
  if (table_number_optional(L, idx, "constraint_hertz", "constraintHertz",
                            &tuning)) {
    d->has_constraint_tuning = true;
    d->constraint_hertz = tuning;
    d->constraint_damping_ratio = 2.0f;
  }
  if (table_number_optional(L, idx, "constraint_damping_ratio",
                            "constraintDampingRatio", &tuning)) {
    if (!d->has_constraint_tuning) {
      d->has_constraint_tuning = true;
      d->constraint_hertz = 60.0f;
    }
    d->constraint_damping_ratio = tuning;
  }

  d->length = table_number(L, idx, "length", NULL, d->length);
  d->min_length =
      table_number(L, idx, "min_length", "minLength", d->min_length);
  d->max_length =
      table_number(L, idx, "max_length", "maxLength", d->max_length);
  d->lower = table_number(L, idx, "lower", NULL, d->lower);
  d->upper = table_number(L, idx, "upper", NULL, d->upper);
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
  d->target_angle =
      table_number(L, idx, "target_angle", "targetAngle", d->target_angle);
  d->target_translation = table_number(
      L, idx, "target_translation", "targetTranslation", d->target_translation);
  d->enable_spring =
      table_bool(L, idx, "enable_spring", "enableSpring", d->enable_spring);
  d->enable_limit =
      table_bool(L, idx, "enable_limit", "enableLimit", d->enable_limit);
  d->enable_motor =
      table_bool(L, idx, "enable_motor", "enableMotor", d->enable_motor);
  d->lower_spring_force = table_number(
      L, idx, "lower_spring_force", "lowerSpringForce", d->lower_spring_force);
  d->upper_spring_force = table_number(
      L, idx, "upper_spring_force", "upperSpringForce", d->upper_spring_force);
  d->linear_velocity = table_vec3(L, idx, "linear_velocity", "linearVelocity",
                                  d->linear_velocity);
  d->angular_velocity = table_vec3(L, idx, "angular_velocity",
                                   "angularVelocity", d->angular_velocity);
  d->max_velocity_force = table_number(
      L, idx, "max_velocity_force", "maxVelocityForce", d->max_velocity_force);
  d->max_velocity_torque =
      table_number(L, idx, "max_velocity_torque", "maxVelocityTorque",
                   d->max_velocity_torque);
  d->max_spring_force = table_number(L, idx, "max_spring_force",
                                     "maxSpringForce", d->max_spring_force);
  d->max_spring_torque = table_number(L, idx, "max_spring_torque",
                                      "maxSpringTorque", d->max_spring_torque);
  table_quat_field(L, idx, "target_rotation", "targetRotation",
                   &d->target_rotation);
  d->enable_cone_limit = table_bool(L, idx, "enable_cone_limit",
                                    "enableConeLimit", d->enable_cone_limit);
  d->cone_angle =
      table_number(L, idx, "cone_angle", "coneAngle", d->cone_angle);
  d->enable_twist_limit = table_bool(L, idx, "enable_twist_limit",
                                     "enableTwistLimit", d->enable_twist_limit);
  d->lower_twist_angle = table_number(L, idx, "lower_twist_angle",
                                      "lowerTwistAngle", d->lower_twist_angle);
  d->upper_twist_angle = table_number(L, idx, "upper_twist_angle",
                                      "upperTwistAngle", d->upper_twist_angle);
  d->motor_velocity =
      table_vec3(L, idx, "motor_velocity", "motorVelocity", d->motor_velocity);

  // wheel は汎用の spring / limit / motor 名を suspension / spin に重ね、
  // box3d 固有の名前が勝つ。
  d->enable_spring = table_bool(L, idx, "enable_suspension_spring",
                                "enableSuspensionSpring", d->enable_spring);
  d->hertz =
      table_number(L, idx, "suspension_hertz", "suspensionHertz", d->hertz);
  d->damping_ratio = table_number(L, idx, "suspension_damping_ratio",
                                  "suspensionDampingRatio", d->damping_ratio);
  d->enable_limit = table_bool(L, idx, "enable_suspension_limit",
                               "enableSuspensionLimit", d->enable_limit);
  d->lower = table_number(L, idx, "lower_suspension_limit",
                          "lowerSuspensionLimit", d->lower);
  d->upper = table_number(L, idx, "upper_suspension_limit",
                          "upperSuspensionLimit", d->upper);
  d->enable_motor = table_bool(L, idx, "enable_spin_motor", "enableSpinMotor",
                               d->enable_motor);
  d->max_torque =
      table_number(L, idx, "max_spin_torque", "maxSpinTorque", d->max_torque);
  d->motor_speed =
      table_number(L, idx, "spin_speed", "spinSpeed", d->motor_speed);
  d->enable_steering = table_bool(L, idx, "enable_steering", "enableSteering",
                                  d->enable_steering);
  d->steering_hertz = table_number(L, idx, "steering_hertz", "steeringHertz",
                                   d->steering_hertz);
  d->steering_damping_ratio =
      table_number(L, idx, "steering_damping_ratio", "steeringDampingRatio",
                   d->steering_damping_ratio);
  d->target_steering_angle =
      table_number(L, idx, "target_steering_angle", "targetSteeringAngle",
                   d->target_steering_angle);
  d->max_steering_torque =
      table_number(L, idx, "max_steering_torque", "maxSteeringTorque",
                   d->max_steering_torque);
  d->enable_steering_limit =
      table_bool(L, idx, "enable_steering_limit", "enableSteeringLimit",
                 d->enable_steering_limit);
  d->lower_steering_limit =
      table_number(L, idx, "lower_steering_limit", "lowerSteeringLimit",
                   d->lower_steering_limit);
  d->upper_steering_limit =
      table_number(L, idx, "upper_steering_limit", "upperSteeringLimit",
                   d->upper_steering_limit);

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
  d->cone_angle =
      nested_number(L, idx, "limit", "cone_angle", "coneAngle", d->cone_angle);

  d->enable_motor =
      nested_bool(L, idx, "motor", "enabled", NULL, d->enable_motor);
  d->motor_speed =
      nested_number(L, idx, "motor", "speed", NULL, d->motor_speed);
  d->max_force =
      nested_number(L, idx, "motor", "max_force", "maxForce", d->max_force);
  d->max_torque =
      nested_number(L, idx, "motor", "max_torque", "maxTorque", d->max_torque);
  d->motor_velocity = nested_vec3(L, idx, "motor", "velocity", "motor_velocity",
                                  d->motor_velocity);
}

static int l_phys3d_joint(lua_State *L) {
  LubHandle w = check_world(L, 1);
  LubStr key = lstr_arg(L, 2);
  LubPhys3dJointDesc d;
  parse_joint_desc(L, w, 3, &d);
  LubHandle h = 0;
  if (lub_phys3d_joint(g_ctx, w, key, &d, &h) != LUB_OK)
    return raise_last(L);
  push_joint_ref(L, ref_field(L, 1, "key"), key, h);
  return 1;
}

static void push_joint_view(lua_State *L, const LubPhys3dJointView *v) {
  lua_newtable(L);
  set_str_field(L, "joint", v->key);
  lua_pushstring(L, joint_kind_name(v->type));
  lua_setfield(L, -2, "type");
  set_str_field(L, "a", v->a);
  set_str_field(L, "b", v->b);
  set_boolean(L, "valid", v->valid);
}

static void push_frame(lua_State *L, const LubPhys3dFrame *f) {
  lua_newtable(L);
  set_number(L, "x", f->position.x);
  set_number(L, "y", f->position.y);
  set_number(L, "z", f->position.z);
  set_number(L, "qx", f->rotation.x);
  set_number(L, "qy", f->rotation.y);
  set_number(L, "qz", f->rotation.z);
  set_number(L, "qw", f->rotation.w);
}

static int l_phys3d_joint_info(lua_State *L) {
  LubHandle j = ref_joint(L, 1);
  LubPhys3dJointInfo info;
  LubStatus st = lub_phys3d_joint_info(g_ctx, j, &info);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  push_joint_view(L, &info.view);
  set_boolean(L, "collide_connected", info.collide_connected);
  push_vec3(L, info.force);
  lua_setfield(L, -2, "force");
  push_vec3(L, info.torque);
  lua_setfield(L, -2, "torque");
  set_number(L, "linear_separation", info.linear_separation);
  set_number(L, "angular_separation", info.angular_separation);
  push_frame(L, &info.local_frame_a);
  lua_setfield(L, -2, "local_frame_a");
  push_frame(L, &info.local_frame_b);
  lua_setfield(L, -2, "local_frame_b");
  return 1;
}

static int l_phys3d_joint_force(lua_State *L) {
  LubHandle j = ref_joint(L, 1);
  LubVec3 v;
  if (lub_phys3d_joint_force(g_ctx, j, &v) != LUB_OK)
    return push_not_found(L);
  push_vec3(L, v);
  return 1;
}

static int l_phys3d_joint_torque(lua_State *L) {
  LubHandle j = ref_joint(L, 1);
  LubVec3 v;
  if (lub_phys3d_joint_torque(g_ctx, j, &v) != LUB_OK)
    return push_not_found(L);
  push_vec3(L, v);
  return 1;
}

typedef LubStatus (*JointMeasureFn)(LubContext *, LubHandle, float *, bool *);

static int joint_measure(lua_State *L, JointMeasureFn fn) {
  LubHandle j = ref_joint(L, 1);
  float v = 0;
  bool has = false;
  if (fn(g_ctx, j, &v, &has) != LUB_OK)
    return push_not_found(L);
  if (!has) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, v);
  return 1;
}

static int l_phys3d_joint_angle(lua_State *L) {
  return joint_measure(L, lub_phys3d_joint_angle);
}

static int l_phys3d_joint_translation(lua_State *L) {
  return joint_measure(L, lub_phys3d_joint_translation);
}

static int l_phys3d_joint_speed(lua_State *L) {
  return joint_measure(L, lub_phys3d_joint_speed);
}

static int l_phys3d_joint_length(lua_State *L) {
  return joint_measure(L, lub_phys3d_joint_length);
}

static int l_phys3d_joint_motor_force(lua_State *L) {
  return joint_measure(L, lub_phys3d_joint_motor_force);
}

static int l_phys3d_joint_motor_torque(lua_State *L) {
  LubHandle j = ref_joint(L, 1);
  float v = 0;
  bool has = false;
  LubVec3 vec;
  bool has_vec = false;
  if (lub_phys3d_joint_motor_torque(g_ctx, j, &v, &has, &vec, &has_vec) !=
      LUB_OK)
    return push_not_found(L);
  if (has_vec) {
    push_vec3(L, vec);
    return 1;
  }
  if (!has) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, v);
  return 1;
}

static bool table_vec3_optional(lua_State *L, int idx, const char *a,
                                const char *b, LubVec3 *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_istable(L, -1);
  if (ok)
    *out = value_vec3(L, lua_gettop(L), *out);
  lua_pop(L, 1);
  return ok;
}

static int l_phys3d_joint_set_motor(lua_State *L) {
  LubHandle j = check_joint(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dJointMotor d;
  memset(&d, 0, sizeof(d));
  d.enabled = table_bool(L, 2, "enabled", NULL, true);
  d.speed = table_number(L, 2, "speed", "motor_speed", 0.0f);
  d.max_force = table_number(L, 2, "max_force", "maxForce", 0.0f);
  d.max_torque = table_number(L, 2, "max_torque", "maxTorque", 0.0f);
  d.has_velocity =
      table_vec3_optional(L, 2, "velocity", "motor_velocity", &d.velocity);
  d.has_linear_velocity = table_vec3_optional(
      L, 2, "linear_velocity", "linearVelocity", &d.linear_velocity);
  d.has_angular_velocity = table_vec3_optional(
      L, 2, "angular_velocity", "angularVelocity", &d.angular_velocity);
  d.has_max_velocity_force = table_number_optional(
      L, 2, "max_velocity_force", "maxVelocityForce", &d.max_velocity_force);
  d.has_max_velocity_torque = table_number_optional(
      L, 2, "max_velocity_torque", "maxVelocityTorque", &d.max_velocity_torque);
  if (lub_phys3d_joint_set_motor(g_ctx, j, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys3d_joint_set_limit(lua_State *L) {
  LubHandle j = check_joint(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dJointLimit d;
  memset(&d, 0, sizeof(d));
  d.enabled = table_bool(L, 2, "enabled", NULL, true);
  d.lower = table_number(L, 2, "lower", NULL, 0.0f);
  d.upper = table_number(L, 2, "upper", NULL, 0.0f);
  d.min_length = table_number(L, 2, "min", "min_length", 0.0f);
  d.max_length = table_number(L, 2, "max", "max_length", FLT_MAX);
  d.has_cone_angle =
      table_number_optional(L, 2, "cone_angle", "coneAngle", &d.cone_angle);
  bool has_lower = table_get_any(L, 2, "lower", "lower_twist_angle");
  if (has_lower)
    lua_pop(L, 1);
  bool has_upper = table_get_any(L, 2, "upper", "upper_twist_angle");
  if (has_upper)
    lua_pop(L, 1);
  if (has_lower || has_upper) {
    d.has_twist = true;
    d.lower =
        table_number(L, 2, "lower_twist_angle", "lowerTwistAngle", d.lower);
    d.upper =
        table_number(L, 2, "upper_twist_angle", "upperTwistAngle", d.upper);
  }
  if (lub_phys3d_joint_set_limit(g_ctx, j, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys3d_joint_set_spring(lua_State *L) {
  LubHandle j = check_joint(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dJointSpring d;
  memset(&d, 0, sizeof(d));
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
  d.has_max_torque =
      table_number_optional(L, 2, "max_torque", "maxTorque", &d.max_torque);
  if (lub_phys3d_joint_set_spring(g_ctx, j, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys3d_joint_set_target(lua_State *L) {
  LubHandle j = check_joint(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dJointTarget d;
  memset(&d, 0, sizeof(d));
  d.has_translation = table_number_optional(
      L, 2, "translation", "target_translation", &d.translation);
  d.has_angle = table_number_optional(L, 2, "angle", "target_angle", &d.angle);
  if (!d.has_angle)
    d.has_angle = table_number_optional(L, 2, "steering_angle", NULL, &d.angle);
  d.rotation = (LubQuat){0, 0, 0, 1};
  d.has_rotation =
      table_quat_field(L, 2, "rotation", "target_rotation", &d.rotation) ||
      table_rotation(L, 2, &d.rotation);
  d.has_linear_velocity = table_vec3_optional(
      L, 2, "linear_velocity", "linearVelocity", &d.linear_velocity);
  d.has_angular_velocity = table_vec3_optional(
      L, 2, "angular_velocity", "angularVelocity", &d.angular_velocity);
  if (lub_phys3d_joint_set_target(g_ctx, j, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

// -------------------------------------------------------------- commands

static bool read_point_opt3(lua_State *L, int idx, LubVec3 *point) {
  if (!lua_istable(L, idx))
    return false;
  bool has = false;
  if (table_get_any(L, idx, "point", NULL)) {
    if (lua_istable(L, -1)) {
      *point = value_vec3(L, lua_gettop(L), *point);
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
  if (table_number_optional(L, idx, "pz", NULL, &out)) {
    point->z = out;
    has = true;
  }
  return has;
}

typedef LubStatus (*PointCmdFn)(LubContext *, LubHandle, LubVec3,
                                const LubVec3 *, bool);
typedef LubStatus (*VecCmdFn)(LubContext *, LubHandle, LubVec3, bool);

static int vector_command(lua_State *L, PointCmdFn fn_point, VecCmdFn fn_vec) {
  LubHandle b = check_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubVec3 v = value_vec3(L, 2, (LubVec3){0, 0, 0});
  bool wake = opt_wake(L, 3, true);
  LubStatus st;
  if (fn_point) {
    LubVec3 point = {0, 0, 0};
    lub_phys3d_center(g_ctx, b, &point);
    bool has = read_point_opt3(L, 3, &point);
    st = fn_point(g_ctx, b, v, has ? &point : NULL, wake);
  } else {
    st = fn_vec(g_ctx, b, v, wake);
  }
  if (st != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys3d_add_force(lua_State *L) {
  return vector_command(L, lub_phys3d_add_force, NULL);
}

static int l_phys3d_add_force_center(lua_State *L) {
  return vector_command(L, NULL, lub_phys3d_add_force_center);
}

static int l_phys3d_add_impulse(lua_State *L) {
  return vector_command(L, lub_phys3d_add_impulse, NULL);
}

static int l_phys3d_add_impulse_center(lua_State *L) {
  return vector_command(L, NULL, lub_phys3d_add_impulse_center);
}

static int l_phys3d_add_torque(lua_State *L) {
  return vector_command(L, NULL, lub_phys3d_add_torque);
}

static int l_phys3d_add_angular_impulse(lua_State *L) {
  return vector_command(L, NULL, lub_phys3d_add_angular_impulse);
}

static int l_phys3d_set_velocity(lua_State *L) {
  LubHandle b = check_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dSetVelocity d;
  memset(&d, 0, sizeof(d));
  d.linear = value_vec3_optional(L, 2, (LubVec3){0, 0, 0}, &d.has_vx, &d.has_vy,
                                 &d.has_vz);
  float out = 0.0f;
  if (table_number_optional(L, 2, "vx", NULL, &out)) {
    d.linear.x = out;
    d.has_vx = true;
  }
  if (table_number_optional(L, 2, "vy", NULL, &out)) {
    d.linear.y = out;
    d.has_vy = true;
  }
  if (table_number_optional(L, 2, "vz", NULL, &out)) {
    d.linear.z = out;
    d.has_vz = true;
  }
  if (table_number_optional(L, 2, "wx", NULL, &out)) {
    d.angular.x = out;
    d.has_wx = true;
  }
  if (table_number_optional(L, 2, "wy", NULL, &out)) {
    d.angular.y = out;
    d.has_wy = true;
  }
  if (table_number_optional(L, 2, "wz", NULL, &out)) {
    d.angular.z = out;
    d.has_wz = true;
  }
  d.wake = opt_wake(L, 3, true);
  if (lub_phys3d_set_velocity(g_ctx, b, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys3d_teleport(lua_State *L) {
  LubHandle b = check_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dTeleport d;
  memset(&d, 0, sizeof(d));
  d.has_x = table_number_optional(L, 2, "x", NULL, &d.position.x);
  d.has_y = table_number_optional(L, 2, "y", NULL, &d.position.y);
  d.has_z = table_number_optional(L, 2, "z", NULL, &d.position.z);
  d.rotation = (LubQuat){0, 0, 0, 1};
  d.has_rotation = table_rotation(L, 2, &d.rotation);
  d.wake = opt_wake(L, 3, true);
  if (lub_phys3d_teleport(g_ctx, b, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys3d_set_target(lua_State *L) {
  LubHandle b = check_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dSetTarget d;
  memset(&d, 0, sizeof(d));
  d.has_x = table_number_optional(L, 2, "x", NULL, &d.position.x);
  d.has_y = table_number_optional(L, 2, "y", NULL, &d.position.y);
  d.has_z = table_number_optional(L, 2, "z", NULL, &d.position.z);
  d.rotation = (LubQuat){0, 0, 0, 1};
  d.has_rotation = table_rotation(L, 2, &d.rotation);
  d.time_step = table_number(L, 2, "dt", NULL, 0.0f);
  d.time_step = table_number(L, 2, "time_step", "timeStep", d.time_step);
  d.wake = table_bool(L, 2, "wake", NULL, true);
  if (lub_phys3d_set_target(g_ctx, b, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

// ------------------------------------------------------------------ step

static int l_phys3d_step(lua_State *L) {
  LubHandle w = check_world(L, 1);
  float dt = (float)luaL_checknumber(L, 2);
  LubPhys3dStepInfo info;
  if (lub_phys3d_step(g_ctx, w, dt, &info) != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  set_integer(L, "steps", info.steps);
  set_integer(L, "commands", info.commands);
  set_number(L, "alpha", info.alpha);
  set_boolean(L, "dropped", info.dropped);
  set_integer(L, "contact_begins", info.contact_begins);
  set_integer(L, "contact_ends", info.contact_ends);
  set_integer(L, "contact_hits", info.contact_hits);
  set_integer(L, "sensor_begins", info.sensor_begins);
  set_integer(L, "sensor_ends", info.sensor_ends);
  set_integer(L, "body_moves", info.body_moves);
  set_integer(L, "body_events", info.body_moves);
  set_integer(L, "joint_events", info.joint_events);
  return 1;
}

// ---------------------------------------------------------- body getters

static int l_phys3d_pose(lua_State *L) {
  LubHandle b = 0;
  if (is_ref(L, 1, "phys3d_body")) {
    b = ref_body(L, 1);
  } else if (is_ref(L, 1, "phys3d_world")) {
    LubHandle w = ref_world(L, 1);
    if (!w)
      return push_not_found(L);
    LubStr key = lstr_arg(L, 2);
    b = lub_phys3d_body_find(g_ctx, w, key);
  } else {
    return luaL_error(L, "phys3d_pose: expected BodyRef or WorldRef, key");
  }
  LubPhys3dPose p;
  if (lub_phys3d_pose(g_ctx, b, &p) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  set_number(L, "x", p.position.x);
  set_number(L, "y", p.position.y);
  set_number(L, "z", p.position.z);
  set_number(L, "qx", p.rotation.x);
  set_number(L, "qy", p.rotation.y);
  set_number(L, "qz", p.rotation.z);
  set_number(L, "qw", p.rotation.w);
  set_number(L, "vx", p.linear_velocity.x);
  set_number(L, "vy", p.linear_velocity.y);
  set_number(L, "vz", p.linear_velocity.z);
  set_number(L, "wx", p.angular_velocity.x);
  set_number(L, "wy", p.angular_velocity.y);
  set_number(L, "wz", p.angular_velocity.z);
  set_boolean(L, "awake", p.awake);
  set_boolean(L, "enabled", p.enabled);
  set_boolean(L, "sleep", p.sleep);
  set_number(L, "sleep_threshold", p.sleep_threshold);
  return 1;
}

static int l_phys3d_velocity(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  LubPhys3dVelocity v;
  if (lub_phys3d_velocity(g_ctx, b, &v) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  set_number(L, "x", v.linear.x);
  set_number(L, "y", v.linear.y);
  set_number(L, "z", v.linear.z);
  set_number(L, "wx", v.angular.x);
  set_number(L, "wy", v.angular.y);
  set_number(L, "wz", v.angular.z);
  return 1;
}

static int l_phys3d_mass(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  LubPhys3dMassData m;
  if (lub_phys3d_mass(g_ctx, b, &m) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  set_number(L, "mass", m.mass);
  push_vec3(L, m.center);
  lua_setfield(L, -2, "center");
  push_vec3(L, m.local_center);
  lua_setfield(L, -2, "local_center");
  lua_newtable(L);
  set_number(L, "xx", m.xx);
  set_number(L, "yy", m.yy);
  set_number(L, "zz", m.zz);
  set_number(L, "xy", m.xy);
  set_number(L, "xz", m.xz);
  set_number(L, "yz", m.yz);
  lua_setfield(L, -2, "inertia");
  return 1;
}

static int l_phys3d_center(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  LubVec3 c;
  if (lub_phys3d_center(g_ctx, b, &c) != LUB_OK)
    return push_not_found(L);
  push_vec3(L, c);
  return 1;
}

typedef LubStatus (*PointFn)(LubContext *, LubHandle, LubVec3, LubVec3 *);

static int body_point(lua_State *L, PointFn fn) {
  LubHandle b = ref_body(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubVec3 p = value_vec3(L, 2, (LubVec3){0, 0, 0});
  LubVec3 out;
  if (fn(g_ctx, b, p, &out) != LUB_OK)
    return push_not_found(L);
  push_vec3(L, out);
  return 1;
}

static int l_phys3d_world_point(lua_State *L) {
  return body_point(L, lub_phys3d_world_point);
}

static int l_phys3d_local_point(lua_State *L) {
  return body_point(L, lub_phys3d_local_point);
}

static int l_phys3d_velocity_at(lua_State *L) {
  return body_point(L, lub_phys3d_velocity_at);
}

// ----------------------------------------------------------- shape parts

static const char *shape_kind_name(int32_t kind) {
  switch (kind) {
  case LUB_PHYS3D_SHAPE_KIND_SPHERE:
    return "sphere";
  case LUB_PHYS3D_SHAPE_KIND_BOX:
    return "box";
  case LUB_PHYS3D_SHAPE_KIND_CAPSULE:
    return "capsule";
  case LUB_PHYS3D_SHAPE_KIND_CYLINDER:
    return "cylinder";
  case LUB_PHYS3D_SHAPE_KIND_CONE:
    return "cone";
  case LUB_PHYS3D_SHAPE_KIND_HULL:
    return "hull";
  case LUB_PHYS3D_SHAPE_KIND_MESH:
    return "mesh";
  case LUB_PHYS3D_SHAPE_KIND_HEIGHT_FIELD:
    return "height_field";
  case LUB_PHYS3D_SHAPE_KIND_COMPOUND:
    return "compound";
  default:
    return "unknown";
  }
}

static void push_shape_part(lua_State *L, const LubPhys3dShapePart *p,
                            bool with_kind) {
  lua_newtable(L);
  set_str_field(L, "body", p->body_key);
  set_str_field(L, "shape", p->shape_key);
  if (!lstr_empty(p->tag)) {
    push_lstr(L, p->tag);
    lua_setfield(L, -2, "tag");
  }
  if (p->has_material) {
    if (!lstr_empty(p->material_name)) {
      push_lstr(L, p->material_name);
    } else {
      lua_pushinteger(L, p->material_id);
    }
    lua_setfield(L, -2, "material");
    set_integer(L, "user_material_id", p->material_id);
  }
  if (p->has_filter)
    push_filter_bits(L, p->filter.category_bits, p->filter.mask_bits,
                     p->filter.group_index);
  if (with_kind && p->kind != 0) {
    lua_pushstring(L, shape_kind_name(p->kind));
    lua_setfield(L, -2, "kind");
  }
  set_boolean(L, "valid", p->valid);
}

static void push_aabb(lua_State *L, const LubPhys3dAabb *a) {
  lua_newtable(L);
  set_number(L, "min_x", a->min.x);
  set_number(L, "min_y", a->min.y);
  set_number(L, "min_z", a->min.z);
  set_number(L, "max_x", a->max.x);
  set_number(L, "max_y", a->max.y);
  set_number(L, "max_z", a->max.z);
}

// ---------------------------------------------------------- shape queries

static void parse_ray(lua_State *L, int idx, LubPhys3dRay *ray) {
  LubVec3 o = {table_number(L, idx, "x", NULL, 0.0f),
               table_number(L, idx, "y", NULL, 0.0f),
               table_number(L, idx, "z", NULL, 0.0f)};
  o = table_vec3(L, idx, "origin", "from", o);
  LubVec3 d = {table_number(L, idx, "dx", NULL, 0.0f),
               table_number(L, idx, "dy", NULL, 0.0f),
               table_number(L, idx, "dz", NULL, 0.0f)};
  d = table_vec3(L, idx, "translation", "delta", d);
  if (table_get_any(L, idx, "to", NULL)) {
    if (lua_istable(L, -1)) {
      LubVec3 to = value_vec3(L, lua_gettop(L), o);
      d = (LubVec3){to.x - o.x, to.y - o.y, to.z - o.z};
    }
    lua_pop(L, 1);
  }
  float max_fraction =
      table_number(L, idx, "max_fraction", "maxFraction", 1.0f);
  if (max_fraction < 0.0f)
    max_fraction = 0.0f;
  ray->origin = o;
  ray->translation =
      (LubVec3){d.x * max_fraction, d.y * max_fraction, d.z * max_fraction};
}

static void push_ray_hit_fields(lua_State *L, const LubPhys3dRayHit *hit) {
  set_number(L, "x", hit->point.x);
  set_number(L, "y", hit->point.y);
  set_number(L, "z", hit->point.z);
  set_number(L, "nx", hit->normal.x);
  set_number(L, "ny", hit->normal.y);
  set_number(L, "nz", hit->normal.z);
  set_number(L, "fraction", hit->fraction);
}

static int l_phys3d_shape_raycast(lua_State *L) {
  LubHandle s = ref_shape(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dRay ray;
  parse_ray(L, 2, &ray);
  LubPhys3dRayHit hit;
  bool has = false;
  LubStatus st = lub_phys3d_shape_raycast(g_ctx, s, &ray, &hit, &has);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  if (!has) {
    lua_pushnil(L);
    return 1;
  }
  lua_newtable(L);
  push_ray_hit_fields(L, &hit);
  set_integer(L, "iterations", hit.iterations);
  set_integer(L, "triangle_index", hit.triangle_index);
  set_integer(L, "child_index", hit.child_index);
  return 1;
}

static int l_phys3d_shape_closest_point(lua_State *L) {
  LubHandle s = ref_shape(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubVec3 p = value_vec3(L, 2, (LubVec3){0, 0, 0});
  LubVec3 out;
  if (lub_phys3d_shape_closest_point(g_ctx, s, p, &out) != LUB_OK)
    return push_not_found(L);
  push_vec3(L, out);
  return 1;
}

static int l_phys3d_shape_aabb(lua_State *L) {
  LubHandle s = ref_shape(L, 1);
  LubPhys3dAabb a;
  if (lub_phys3d_shape_aabb(g_ctx, s, &a) != LUB_OK)
    return push_not_found(L);
  push_aabb(L, &a);
  return 1;
}

static int l_phys3d_shape_info(lua_State *L) {
  LubHandle s = ref_shape(L, 1);
  LubPhys3dShapeInfo info;
  if (lub_phys3d_shape_info(g_ctx, s, &info) != LUB_OK)
    return push_not_found(L);
  push_shape_part(L, &info.part, true);
  set_number(L, "density", info.density);
  set_number(L, "friction", info.friction);
  set_number(L, "restitution", info.restitution);
  set_boolean(L, "sensor", info.sensor);
  set_boolean(L, "sensor_events", info.sensor_events);
  set_boolean(L, "contact", info.contact);
  set_boolean(L, "pre_solve", info.pre_solve);
  set_boolean(L, "hit", info.hit);
  lua_newtable(L);
  push_filter_bits(L, info.part.filter.category_bits,
                   info.part.filter.mask_bits, info.part.filter.group_index);
  lua_setfield(L, -2, "filter");
  push_aabb(L, &info.aabb);
  lua_setfield(L, -2, "aabb");
  return 1;
}

static int l_phys3d_shape_set_material(lua_State *L) {
  LubHandle s = check_shape(L, 1);
  LubPhys3dMaterialDesc d;
  memset(&d, 0, sizeof(d));
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
        d.has_material_name = true;
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
        L, "phys3d_shape_set_material: expected table, number, or string");
  }
  if (lub_phys3d_shape_set_material(g_ctx, s, &d) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys3d_shape_set_filter(lua_State *L) {
  LubHandle s = check_shape(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dShapeInfo info;
  if (lub_phys3d_shape_info(g_ctx, s, &info) != LUB_OK)
    return luaL_error(L, "phys3d_shape_set_filter: shape is not live");
  LubPhys3dFilter f = info.part.filter;
  if (table_get_any(L, 2, "filter", NULL)) {
    if (lua_istable(L, -1))
      parse_filter_table(L, "phys3d", lua_gettop(L), &f.category_bits,
                         &f.mask_bits, &f.group_index);
    lua_pop(L, 1);
  } else {
    parse_filter_table(L, "phys3d", 2, &f.category_bits, &f.mask_bits,
                       &f.group_index);
  }
  if (lub_phys3d_shape_set_filter(g_ctx, s, &f) != LUB_OK)
    return raise_last(L);
  return 0;
}

static int l_phys3d_shape_set_events(lua_State *L) {
  LubHandle s = check_shape(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dEventFlags f;
  memset(&f, 0, sizeof(f));
  bool flag = false;
  if (table_bool_optional(L, 2, "sensor", NULL, &flag))
    return luaL_error(
        L, "phys3d_shape_set_events: sensor cannot change at runtime");
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
  if (lub_phys3d_shape_set_events(g_ctx, s, &f) != LUB_OK)
    return raise_last(L);
  return 0;
}

// ------------------------------------------------------------- body lists

static int l_phys3d_body_shapes(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  const LubPhys3dShapePart *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys3d_body_shapes(g_ctx, b, &items, &count);
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

static int l_phys3d_body_joints(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  const LubPhys3dJointView *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys3d_body_joints(g_ctx, b, &items, &count);
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

static int l_phys3d_body_contacts(lua_State *L) {
  LubHandle b = ref_body(L, 1);
  const LubPhys3dContactData *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys3d_body_contacts(g_ctx, b, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    const LubPhys3dContactData *c = &items[i];
    lua_newtable(L);
    push_shape_part(L, &c->a, false);
    lua_setfield(L, -2, "a");
    push_shape_part(L, &c->b, false);
    lua_setfield(L, -2, "b");
    set_number(L, "nx", c->normal.x);
    set_number(L, "ny", c->normal.y);
    set_number(L, "nz", c->normal.z);
    set_integer(L, "manifold_count", c->manifold_count);
    set_integer(L, "point_count", c->point_count);
    if (c->has_point) {
      set_number(L, "x", c->point.x);
      set_number(L, "y", c->point.y);
      set_number(L, "z", c->point.z);
      set_number(L, "separation", c->separation);
    }
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

// ------------------------------------------------------------ step events

static int32_t parse_event_kind(lua_State *L, int idx, bool allow_hit,
                                const char *fn) {
  const char *kind = luaL_optstring(L, idx, "begin");
  if (strcmp(kind, "begin") == 0)
    return LUB_PHYS3D_EVENT_KIND_BEGIN;
  if (strcmp(kind, "end") == 0)
    return LUB_PHYS3D_EVENT_KIND_END;
  if (allow_hit && strcmp(kind, "hit") == 0)
    return LUB_PHYS3D_EVENT_KIND_HIT;
  return luaL_error(L, "%s: kind must be %s", fn,
                    allow_hit ? "begin, end, or hit" : "begin or end"),
         0;
}

static void push_contact_events(lua_State *L, const LubPhys3dContact *items,
                                int32_t count, const char *a_name,
                                const char *b_name, bool with_geometry) {
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    const LubPhys3dContact *e = &items[i];
    lua_newtable(L);
    push_shape_part(L, &e->a, false);
    lua_setfield(L, -2, a_name);
    push_shape_part(L, &e->b, false);
    lua_setfield(L, -2, b_name);
    if (with_geometry) {
      set_number(L, "nx", e->normal.x);
      set_number(L, "ny", e->normal.y);
      set_number(L, "nz", e->normal.z);
      set_integer(L, "point_count", e->point_count);
      set_number(L, "x", e->point.x);
      set_number(L, "y", e->point.y);
      set_number(L, "z", e->point.z);
      if (e->approach_speed != 0.0f)
        set_number(L, "approach_speed", e->approach_speed);
    }
    lua_rawseti(L, -2, i + 1);
  }
}

static int l_phys3d_contacts(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  int32_t kind = parse_event_kind(L, 2, true, "phys3d_contacts");
  const LubPhys3dContact *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys3d_contacts(g_ctx, w, kind, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  push_contact_events(L, items, count, "a", "b", true);
  return 1;
}

static int l_phys3d_sensors(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  int32_t kind = parse_event_kind(L, 2, false, "phys3d_sensors");
  const LubPhys3dContact *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys3d_sensors(g_ctx, w, kind, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  push_contact_events(L, items, count, "sensor", "visitor", false);
  return 1;
}

static int l_phys3d_body_events(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  const LubPhys3dBodyEvent *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys3d_body_events(g_ctx, w, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    const LubPhys3dBodyEvent *e = &items[i];
    lua_newtable(L);
    set_str_field(L, "body", e->body);
    set_boolean(L, "valid", e->valid);
    set_number(L, "x", e->position.x);
    set_number(L, "y", e->position.y);
    set_number(L, "z", e->position.z);
    set_number(L, "qx", e->rotation.x);
    set_number(L, "qy", e->rotation.y);
    set_number(L, "qz", e->rotation.z);
    set_number(L, "qw", e->rotation.w);
    set_boolean(L, "fell_asleep", e->fell_asleep);
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

static int l_phys3d_joint_events(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  const LubPhys3dJointEvent *items = NULL;
  int32_t count = 0;
  LubStatus st = lub_phys3d_joint_events(g_ctx, w, &items, &count);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  for (int32_t i = 0; i < count; ++i) {
    const LubPhys3dJointEvent *e = &items[i];
    lua_newtable(L);
    set_str_field(L, "joint", e->joint);
    lua_pushstring(L, joint_kind_name(e->type));
    lua_setfield(L, -2, "type");
    set_str_field(L, "a", e->a);
    set_str_field(L, "b", e->b);
    set_boolean(L, "valid", e->valid);
    lua_rawseti(L, -2, i + 1);
  }
  return 1;
}

// ---------------------------------------------------------- world queries

typedef struct RayVisit {
  LuaPhysVisit base;
  bool continue_all; // visitor 無しの mode = "all"
} RayVisit;

static void push_ray_hit(lua_State *L, const LubPhys3dRayHit *hit) {
  push_shape_part(L, &hit->shape, false);
  push_ray_hit_fields(L, hit);
  set_integer(L, "hit_material_id", hit->hit_material_id);
  set_integer(L, "triangle_index", hit->triangle_index);
  set_integer(L, "child_index", hit->child_index);
}

static bool overlap_visit(void *user, const LubPhys3dShapePart *shape) {
  LuaPhysVisit *v = (LuaPhysVisit *)user;
  lua_State *L = v->L;
  if (v->error)
    return false;
  lua_rawgeti(L, LUA_REGISTRYINDEX, v->results_ref);
  push_shape_part(L, shape, false);
  bool include = true;
  bool keep_going = true;
  if (visit_call(v)) {
    keep_going = parse_overlap_visitor_result(L, -1, &include);
    lua_pop(L, 1);
  } else if (v->error) {
    return false;
  }
  visit_store(v, include);
  return keep_going;
}

static float ray_visit(void *user, const LubPhys3dRayHit *hit) {
  RayVisit *rv = (RayVisit *)user;
  LuaPhysVisit *v = &rv->base;
  lua_State *L = v->L;
  if (v->error)
    return 0.0f;
  lua_rawgeti(L, LUA_REGISTRYINDEX, v->results_ref);
  push_ray_hit(L, hit);
  bool include = true;
  float result = rv->continue_all ? 1.0f : hit->fraction;
  if (visit_call(v)) {
    result = parse_raycast_visitor_result(L, -1, hit->fraction, &include);
    lua_pop(L, 1);
  } else if (v->error) {
    return 0.0f;
  }
  visit_store(v, include);
  return result;
}

static bool plane_visit(void *user, const LubPhys3dMoverPlane *plane) {
  LuaPhysVisit *v = (LuaPhysVisit *)user;
  lua_State *L = v->L;
  if (v->error)
    return false;
  lua_rawgeti(L, LUA_REGISTRYINDEX, v->results_ref);
  push_shape_part(L, &plane->shape, false);
  set_number(L, "x", plane->point.x);
  set_number(L, "y", plane->point.y);
  set_number(L, "z", plane->point.z);
  set_number(L, "nx", plane->normal.x);
  set_number(L, "ny", plane->normal.y);
  set_number(L, "nz", plane->normal.z);
  set_number(L, "offset", plane->offset);
  set_integer(L, "plane_count", plane->plane_count);
  bool include = true;
  bool keep_going = true;
  if (visit_call(v)) {
    keep_going = parse_overlap_visitor_result(L, -1, &include);
    lua_pop(L, 1);
  } else if (v->error) {
    return false;
  }
  visit_store(v, include);
  return keep_going;
}

static bool parse_raycast_mode_all(lua_State *L, int idx, const char *fn_name) {
  bool all = false;
  if (!table_get_any(L, idx, "mode", NULL))
    return false;
  if (lua_isstring(L, -1)) {
    const char *mode = lua_tostring(L, -1);
    if (strcmp(mode, "all") == 0) {
      all = true;
    } else if (strcmp(mode, "closest") != 0) {
      luaL_error(L, "%s: mode must be closest or all", fn_name);
    }
  }
  lua_pop(L, 1);
  return all;
}

static int query_status(lua_State *L, LuaPhysVisit *v, const char *fn,
                        LubStatus st) {
  if (st == LUB_NOT_FOUND) {
    visit_finish(v, fn, false, false, 0, 0);
    return push_not_found(L);
  }
  visit_finish(v, fn, false, false, 0, 0);
  return raise_last(L);
}

static int l_phys3d_raycast(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dRay ray;
  parse_ray(L, 2, &ray);
  LubPhys3dQueryFilter filter = parse_query_filter(L, 2);
  bool collect_all = parse_raycast_mode_all(L, 2, "phys3d_raycast");
  if (!lua_isfunction(L, 3) && !collect_all) {
    LubPhys3dRayHit hit;
    bool has = false;
    LubStatus st =
        lub_phys3d_raycast_closest(g_ctx, w, &ray, &filter, &hit, &has);
    if (st == LUB_NOT_FOUND)
      return push_not_found(L);
    if (st != LUB_OK)
      return raise_last(L);
    if (!has) {
      lua_pushnil(L);
      return 1;
    }
    push_ray_hit(L, &hit);
    set_integer(L, "node_visits", hit.node_visits);
    set_integer(L, "leaf_visits", hit.leaf_visits);
    return 1;
  }
  RayVisit rv;
  visit_init(&rv.base, L, 3);
  rv.continue_all = collect_all && rv.base.visitor_ref == LUA_NOREF;
  LubPhys3dTreeStats stats;
  LubStatus st =
      lub_phys3d_raycast(g_ctx, w, &ray, &filter, ray_visit, &rv, &stats);
  if (st != LUB_OK)
    return query_status(L, &rv.base, "phys3d_raycast", st);
  return visit_finish(&rv.base, "phys3d_raycast", true, true, stats.node_visits,
                      stats.leaf_visits);
}

static int l_phys3d_overlap_aabb(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dAabb aabb;
  aabb.min = (LubVec3){table_number(L, 2, "min_x", "minX", 0.0f),
                       table_number(L, 2, "min_y", "minY", 0.0f),
                       table_number(L, 2, "min_z", "minZ", 0.0f)};
  aabb.min = table_vec3(L, 2, "min", NULL, aabb.min);
  aabb.max = (LubVec3){table_number(L, 2, "max_x", "maxX", 0.0f),
                       table_number(L, 2, "max_y", "maxY", 0.0f),
                       table_number(L, 2, "max_z", "maxZ", 0.0f)};
  aabb.max = table_vec3(L, 2, "max", NULL, aabb.max);
  LubPhys3dQueryFilter filter = parse_query_filter(L, 2);
  LuaPhysVisit v;
  visit_init(&v, L, 3);
  LubPhys3dTreeStats stats;
  LubStatus st = lub_phys3d_overlap_aabb(g_ctx, w, &aabb, &filter,
                                         overlap_visit, &v, &stats);
  if (st != LUB_OK)
    return query_status(L, &v, "phys3d_overlap_aabb", st);
  return visit_finish(&v, "phys3d_overlap_aabb", true, true, stats.node_visits,
                      stats.leaf_visits);
}

// { sphere = { r, center } } | { box = { hx, hy, hz, center, quat, radius } }
// | { capsule = { a, b, r } }
static void parse_shape_proxy(lua_State *L, int idx, const char *fn_name,
                              LubPhys3dShapeProxy *p) {
  memset(p, 0, sizeof(*p));
  p->rotation = (LubQuat){0, 0, 0, 1};
  idx = abs_index(L, idx);
  if (table_get_any(L, idx, "sphere", NULL)) {
    if (!lua_istable(L, -1))
      luaL_error(L, "%s: sphere must be a table", fn_name);
    int t = lua_gettop(L);
    p->kind = LUB_PHYS3D_PROXY_KIND_SPHERE;
    p->r = table_number(L, t, "r", "radius", 0.0f);
    p->center = table_vec3(L, t, "center", NULL, (LubVec3){0, 0, 0});
    lua_pop(L, 1);
    return;
  }
  if (table_get_any(L, idx, "box", NULL)) {
    if (!lua_istable(L, -1))
      luaL_error(L, "%s: box must be a table", fn_name);
    int t = lua_gettop(L);
    p->kind = LUB_PHYS3D_PROXY_KIND_BOX;
    p->hx = table_number(L, t, "hx", NULL, 0.0f);
    p->hy = table_number(L, t, "hy", NULL, 0.0f);
    p->hz = table_number(L, t, "hz", NULL, 0.0f);
    p->r = table_number(L, t, "radius", "r", 0.0f);
    p->center = table_vec3(L, t, "center", NULL, (LubVec3){0, 0, 0});
    if (table_get_any(L, t, "quat", NULL)) {
      if (lua_istable(L, -1)) {
        p->rotation = value_quat(L, lua_gettop(L), p->rotation);
        p->has_rotation = true;
      }
      lua_pop(L, 1);
    }
    lua_pop(L, 1);
    return;
  }
  if (table_get_any(L, idx, "capsule", NULL)) {
    if (!lua_istable(L, -1))
      luaL_error(L, "%s: capsule must be a table", fn_name);
    int t = lua_gettop(L);
    p->kind = LUB_PHYS3D_PROXY_KIND_CAPSULE;
    p->a = table_vec3(L, t, "a", NULL, (LubVec3){0, 0, 0});
    p->b = table_vec3(L, t, "b", NULL, (LubVec3){0, 0, 0});
    p->r = table_number(L, t, "r", "radius", 0.0f);
    lua_pop(L, 1);
    return;
  }
  luaL_error(L, "%s: expected sphere, box, or capsule", fn_name);
}

static LubVec3 parse_translation(lua_State *L, int idx, const char *fn_name) {
  LubVec3 t = {table_number(L, idx, "dx", NULL, 0.0f),
               table_number(L, idx, "dy", NULL, 0.0f),
               table_number(L, idx, "dz", NULL, 0.0f)};
  t = table_vec3(L, idx, "translation", "delta", t);
  float max_fraction =
      table_number(L, idx, "max_fraction", "maxFraction", 1.0f);
  if (max_fraction < 0.0f)
    max_fraction = 0.0f;
  t = (LubVec3){t.x * max_fraction, t.y * max_fraction, t.z * max_fraction};
  if (t.x * t.x + t.y * t.y + t.z * t.z <= 1e-12f)
    luaL_error(L, "%s: translation must be non-zero", fn_name);
  return t;
}

static int l_phys3d_overlap_shape(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dShapeProxy proxy;
  parse_shape_proxy(L, 2, "phys3d_overlap_shape", &proxy);
  LubPhys3dQueryFilter filter = parse_query_filter(L, 2);
  LuaPhysVisit v;
  visit_init(&v, L, 3);
  LubPhys3dTreeStats stats;
  LubStatus st = lub_phys3d_overlap_shape(g_ctx, w, &proxy, &filter,
                                          overlap_visit, &v, &stats);
  if (st != LUB_OK)
    return query_status(L, &v, "phys3d_overlap_shape", st);
  return visit_finish(&v, "phys3d_overlap_shape", true, true, stats.node_visits,
                      stats.leaf_visits);
}

static int l_phys3d_shape_cast(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dShapeProxy proxy;
  parse_shape_proxy(L, 2, "phys3d_shape_cast", &proxy);
  LubVec3 t = parse_translation(L, 2, "phys3d_shape_cast");
  LubPhys3dQueryFilter filter = parse_query_filter(L, 2);
  RayVisit rv;
  visit_init(&rv.base, L, 3);
  rv.continue_all = false;
  LubPhys3dTreeStats stats;
  LubStatus st = lub_phys3d_shape_cast(g_ctx, w, &proxy, t, &filter, ray_visit,
                                       &rv, &stats);
  if (st != LUB_OK)
    return query_status(L, &rv.base, "phys3d_shape_cast", st);
  if (rv.base.visitor_ref == LUA_NOREF && !rv.base.error) {
    if (rv.base.count == 0) {
      visit_finish(&rv.base, "phys3d_shape_cast", false, false, 0, 0);
      lua_pushnil(L);
      return 1;
    }
    lua_rawgeti(L, LUA_REGISTRYINDEX, rv.base.results_ref);
    lua_rawgeti(L, -1, rv.base.count);
    set_integer(L, "node_visits", stats.node_visits);
    set_integer(L, "leaf_visits", stats.leaf_visits);
    lua_remove(L, -2);
    visit_finish(&rv.base, "phys3d_shape_cast", false, false, 0, 0);
    return 1;
  }
  return visit_finish(&rv.base, "phys3d_shape_cast", true, true,
                      stats.node_visits, stats.leaf_visits);
}

static void parse_mover(lua_State *L, int idx, LubPhys3dMover *m) {
  m->a = table_vec3(L, idx, "a", NULL, (LubVec3){0, 0, 0});
  m->b = table_vec3(L, idx, "b", NULL, (LubVec3){0, 0, 0});
  m->r = table_number(L, idx, "r", "radius", 0.0f);
}

static int l_phys3d_cast_mover(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dMover mover;
  parse_mover(L, 2, &mover);
  LubVec3 t = parse_translation(L, 2, "phys3d_cast_mover");
  LubPhys3dQueryFilter filter = parse_query_filter(L, 2);
  float fraction = 0;
  LubStatus st = lub_phys3d_cast_mover(g_ctx, w, &mover, t, &filter, &fraction);
  if (st == LUB_NOT_FOUND)
    return push_not_found(L);
  if (st != LUB_OK)
    return raise_last(L);
  lua_newtable(L);
  set_number(L, "fraction", fraction);
  set_number(L, "dx", t.x * fraction);
  set_number(L, "dy", t.y * fraction);
  set_number(L, "dz", t.z * fraction);
  return 1;
}

static int l_phys3d_collide_mover(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  LubPhys3dMover mover;
  parse_mover(L, 2, &mover);
  LubPhys3dQueryFilter filter = parse_query_filter(L, 2);
  LuaPhysVisit v;
  visit_init(&v, L, 3);
  LubStatus st =
      lub_phys3d_collide_mover(g_ctx, w, &mover, &filter, plane_visit, &v);
  if (st != LUB_OK)
    return query_status(L, &v, "phys3d_collide_mover", st);
  return visit_finish(&v, "phys3d_collide_mover", true, false, 0, 0);
}

static int l_phys3d_profile(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  LubPhys3dProfile p;
  if (lub_phys3d_profile(g_ctx, w, &p) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  set_number(L, "step", p.step);
  set_number(L, "pairs", p.pairs);
  set_number(L, "collide", p.collide);
  set_number(L, "solve", p.solve);
  set_number(L, "solver_setup", p.solver_setup);
  set_number(L, "constraints", p.constraints);
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
  set_number(L, "sensor_hits", p.sensor_hits);
  set_number(L, "joint_events", p.joint_events);
  set_number(L, "hit_events", p.hit_events);
  set_number(L, "refit", p.refit);
  set_number(L, "bullets", p.bullets);
  set_number(L, "sleep_islands", p.sleep_islands);
  set_number(L, "sensors", p.sensors);
  return 1;
}

static int l_phys3d_counters(lua_State *L) {
  LubHandle w = ref_world(L, 1);
  LubPhys3dCounters c;
  if (lub_phys3d_counters(g_ctx, w, &c) != LUB_OK)
    return push_not_found(L);
  lua_newtable(L);
  set_integer(L, "body_count", c.body_count);
  set_integer(L, "shape_count", c.shape_count);
  set_integer(L, "contact_count", c.contact_count);
  set_integer(L, "joint_count", c.joint_count);
  set_integer(L, "island_count", c.island_count);
  set_integer(L, "stack_used", c.stack_used);
  set_integer(L, "arena_capacity", c.arena_capacity);
  set_integer(L, "static_tree_height", c.static_tree_height);
  set_integer(L, "tree_height", c.tree_height);
  set_integer(L, "sat_call_count", c.sat_call_count);
  set_integer(L, "sat_cache_hit_count", c.sat_cache_hit_count);
  set_integer(L, "byte_count", c.byte_count);
  set_integer(L, "task_count", c.task_count);
  set_integer(L, "awake_contact_count", c.awake_contact_count);
  set_integer(L, "recycled_contact_count", c.recycled_contact_count);
  set_integer(L, "distance_iterations", c.distance_iterations);
  set_integer(L, "push_back_iterations", c.push_back_iterations);
  set_integer(L, "root_iterations", c.root_iterations);
  lua_newtable(L);
  for (int i = 0; i < 24; ++i) {
    lua_pushinteger(L, c.color_counts[i]);
    lua_rawseti(L, -2, i + 1);
  }
  lua_setfield(L, -2, "color_counts");
  lua_newtable(L);
  for (int i = 0; i < LUB_PHYS3D_MANIFOLD_COUNT_BUCKETS; ++i) {
    lua_pushinteger(L, c.manifold_counts[i]);
    lua_rawseti(L, -2, i + 1);
  }
  lua_setfield(L, -2, "manifold_counts");
  return 1;
}

// -------------------------------------------------------------- register

void phys3d_lua_register(lua_State *L, LubContext *ctx) {
  g_ctx = ctx;
  lua_pushinteger(L, LUB_PHYS3D_BODY_TYPE_STATIC);
  lua_setglobal(L, "STATIC");
  lua_pushinteger(L, LUB_PHYS3D_BODY_TYPE_KINEMATIC);
  lua_setglobal(L, "KINEMATIC");
  lua_pushinteger(L, LUB_PHYS3D_BODY_TYPE_DYNAMIC);
  lua_setglobal(L, "DYNAMIC");

  static const luaL_Reg fns[] = {
      {"phys3d_world", l_phys3d_world},
      {"phys3d_begin", l_phys3d_begin},
      {"phys3d_world_info", l_phys3d_world_info},
      {"phys3d_body", l_phys3d_body},
      {"phys3d_sphere", l_phys3d_sphere},
      {"phys3d_box", l_phys3d_box},
      {"phys3d_capsule", l_phys3d_capsule},
      {"phys3d_cylinder", l_phys3d_cylinder},
      {"phys3d_cone", l_phys3d_cone},
      {"phys3d_hull", l_phys3d_hull},
      {"phys3d_mesh", l_phys3d_mesh},
      {"phys3d_height_field", l_phys3d_height_field},
      {"phys3d_compound", l_phys3d_compound},
      {"phys3d_joint", l_phys3d_joint},
      {"phys3d_joint_info", l_phys3d_joint_info},
      {"phys3d_joint_force", l_phys3d_joint_force},
      {"phys3d_joint_torque", l_phys3d_joint_torque},
      {"phys3d_joint_angle", l_phys3d_joint_angle},
      {"phys3d_joint_translation", l_phys3d_joint_translation},
      {"phys3d_joint_speed", l_phys3d_joint_speed},
      {"phys3d_joint_length", l_phys3d_joint_length},
      {"phys3d_joint_motor_force", l_phys3d_joint_motor_force},
      {"phys3d_joint_motor_torque", l_phys3d_joint_motor_torque},
      {"phys3d_joint_set_motor", l_phys3d_joint_set_motor},
      {"phys3d_joint_set_limit", l_phys3d_joint_set_limit},
      {"phys3d_joint_set_spring", l_phys3d_joint_set_spring},
      {"phys3d_joint_set_target", l_phys3d_joint_set_target},
      {"phys3d_body_joints", l_phys3d_body_joints},
      {"phys3d_cast_mover", l_phys3d_cast_mover},
      {"phys3d_collide_mover", l_phys3d_collide_mover},
      {"phys3d_step", l_phys3d_step},
      {"phys3d_pose", l_phys3d_pose},
      {"phys3d_velocity", l_phys3d_velocity},
      {"phys3d_mass", l_phys3d_mass},
      {"phys3d_center", l_phys3d_center},
      {"phys3d_world_point", l_phys3d_world_point},
      {"phys3d_local_point", l_phys3d_local_point},
      {"phys3d_velocity_at", l_phys3d_velocity_at},
      {"phys3d_body_shapes", l_phys3d_body_shapes},
      {"phys3d_body_contacts", l_phys3d_body_contacts},
      {"phys3d_shape_raycast", l_phys3d_shape_raycast},
      {"phys3d_shape_closest_point", l_phys3d_shape_closest_point},
      {"phys3d_shape_aabb", l_phys3d_shape_aabb},
      {"phys3d_shape_info", l_phys3d_shape_info},
      {"phys3d_shape_set_material", l_phys3d_shape_set_material},
      {"phys3d_shape_set_filter", l_phys3d_shape_set_filter},
      {"phys3d_shape_set_events", l_phys3d_shape_set_events},
      {"phys3d_contacts", l_phys3d_contacts},
      {"phys3d_body_events", l_phys3d_body_events},
      {"phys3d_sensors", l_phys3d_sensors},
      {"phys3d_joint_events", l_phys3d_joint_events},
      {"phys3d_raycast", l_phys3d_raycast},
      {"phys3d_overlap_aabb", l_phys3d_overlap_aabb},
      {"phys3d_overlap_shape", l_phys3d_overlap_shape},
      {"phys3d_shape_cast", l_phys3d_shape_cast},
      {"phys3d_profile", l_phys3d_profile},
      {"phys3d_counters", l_phys3d_counters},
      {"phys3d_add_force", l_phys3d_add_force},
      {"phys3d_add_force_center", l_phys3d_add_force_center},
      {"phys3d_add_impulse", l_phys3d_add_impulse},
      {"phys3d_add_impulse_center", l_phys3d_add_impulse_center},
      {"phys3d_add_torque", l_phys3d_add_torque},
      {"phys3d_add_angular_impulse", l_phys3d_add_angular_impulse},
      {"phys3d_set_velocity", l_phys3d_set_velocity},
      {"phys3d_teleport", l_phys3d_teleport},
      {"phys3d_set_target", l_phys3d_set_target},
      {NULL, NULL},
  };
  for (const luaL_Reg *r = fns; r->name; ++r) {
    lua_pushcfunction(L, r->func);
    lua_setglobal(L, r->name);
  }
}
