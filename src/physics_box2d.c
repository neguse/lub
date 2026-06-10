#include "physics_box2d.h"

#include <SDL3/SDL.h>
#include <box2d/box2d.h>
#include <box2d/math_functions.h>
#include <float.h>
#include <lauxlib.h>
#include <math.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#define PHYS2D_BODY_BUCKETS 256
#define PHYS2D_SHAPE_BUCKETS 64
#define PHYS2D_CHAIN_BUCKETS 32
#define PHYS2D_JOINT_BUCKETS 128
#define PHYS2D_TOMBSTONE_BUCKETS 256

typedef enum PhysShapeKind {
  PHYS_SHAPE_BOX = 1,
  PHYS_SHAPE_CIRCLE = 2,
  PHYS_SHAPE_CAPSULE = 3,
  PHYS_SHAPE_SEGMENT = 4,
  PHYS_SHAPE_POLYGON = 5,
} PhysShapeKind;

typedef struct PhysShape {
  char *key;
  char *tag;
  char *material_name;
  struct PhysBody *body;
  b2ShapeId id;
  uint64_t seen_generation;
  uint64_t desc_hash;
  uint64_t constructor_hash;
  bool constructor_warned;
  int material_id;
  PhysShapeKind kind;
  struct PhysShape *next;
} PhysShape;

typedef struct PhysChain {
  char *key;
  char *tag;
  char *material_name;
  struct PhysBody *body;
  b2ChainId id;
  uint64_t seen_generation;
  int64_t version;
  uint64_t constructor_hash;
  bool constructor_warned;
  int material_id;
  struct PhysChain *next;
} PhysChain;

typedef struct PhysBody {
  char *key;
  struct PhysWorld *world;
  b2BodyId id;
  uint64_t seen_generation;
  int64_t version;
  uint64_t constructor_hash;
  bool constructor_warned;
  PhysShape *shapes[PHYS2D_SHAPE_BUCKETS];
  PhysChain *chains[PHYS2D_CHAIN_BUCKETS];
  struct PhysBody *next;
} PhysBody;

typedef enum PhysJointKind {
  PHYS_JOINT_DISTANCE = 1,
  PHYS_JOINT_FILTER = 2,
  PHYS_JOINT_MOTOR = 3,
  PHYS_JOINT_MOUSE = 4,
  PHYS_JOINT_PRISMATIC = 5,
  PHYS_JOINT_REVOLUTE = 6,
  PHYS_JOINT_WELD = 7,
  PHYS_JOINT_WHEEL = 8,
} PhysJointKind;

typedef struct PhysJoint {
  char *key;
  struct PhysWorld *world;
  PhysBody *body_a;
  PhysBody *body_b;
  b2JointId id;
  uint64_t seen_generation;
  int64_t version;
  uint64_t constructor_hash;
  bool constructor_warned;
  PhysJointKind kind;
  struct PhysJoint *next;
} PhysJoint;

typedef struct PhysContactSnapshot {
  char *a_body;
  char *a_shape;
  char *a_tag;
  char *a_material;
  char *b_body;
  char *b_shape;
  char *b_tag;
  char *b_material;
  bool a_valid;
  bool b_valid;
  int a_material_id;
  int b_material_id;
  float nx;
  float ny;
  int point_count;
  float x;
  float y;
  float approach_speed;
} PhysContactSnapshot;

typedef struct PhysBodyEventSnapshot {
  char *body;
  bool valid;
  float x;
  float y;
  float angle;
  bool fell_asleep;
} PhysBodyEventSnapshot;

typedef struct PhysEventBuffer {
  PhysContactSnapshot *begins;
  int begin_count;
  int begin_cap;
  PhysContactSnapshot *ends;
  int end_count;
  int end_cap;
  PhysContactSnapshot *hits;
  int hit_count;
  int hit_cap;
  PhysContactSnapshot *sensor_begins;
  int sensor_begin_count;
  int sensor_begin_cap;
  PhysContactSnapshot *sensor_ends;
  int sensor_end_count;
  int sensor_end_cap;
  PhysBodyEventSnapshot *moves;
  int move_count;
  int move_cap;
} PhysEventBuffer;

typedef struct PhysDebugArray {
  float *items;
  int count;
  int cap;
} PhysDebugArray;

typedef struct PhysDebugBuffer {
  PhysDebugArray segments;
  PhysDebugArray circles;
  PhysDebugArray capsules;
  PhysDebugArray polygons;
  PhysDebugArray points;
  bool failed;
} PhysDebugBuffer;

typedef struct PhysCallbacks {
  lua_State *L;
  int filter_ref;
  int pre_solve_ref;
  int friction_ref;
  int restitution_ref;
  bool filter_error_logged;
  bool pre_solve_error_logged;
  bool friction_error_logged;
  bool restitution_error_logged;
} PhysCallbacks;

typedef enum PhysCommandKind {
  PHYS_COMMAND_ADD_FORCE = 1,
  PHYS_COMMAND_ADD_FORCE_CENTER,
  PHYS_COMMAND_ADD_IMPULSE,
  PHYS_COMMAND_ADD_IMPULSE_CENTER,
  PHYS_COMMAND_ADD_TORQUE,
  PHYS_COMMAND_ADD_ANGULAR_IMPULSE,
  PHYS_COMMAND_SET_VELOCITY,
  PHYS_COMMAND_TELEPORT,
  PHYS_COMMAND_SET_TARGET,
  PHYS_COMMAND_SET_MASS_DATA,
} PhysCommandKind;

typedef struct PhysCommand {
  char *body_key;
  uint64_t body_id_key;
  PhysCommandKind kind;
  b2Vec2 vector;
  b2Vec2 point;
  b2Transform transform;
  b2MassData mass_data;
  float scalar;
  float time_step;
  bool wake;
  bool has_point;
  bool has_x;
  bool has_y;
  bool has_angle;
  bool has_w;
} PhysCommand;

typedef struct PhysCommandQueue {
  PhysCommand *items;
  int count;
  int cap;
} PhysCommandQueue;

typedef struct PhysShapeTombstone {
  uint64_t id_key;
  char *body;
  char *shape;
  char *tag;
  char *material;
  int material_id;
  struct PhysShapeTombstone *next;
} PhysShapeTombstone;

struct PhysWorld {
  char *key;
  b2WorldId id;
  double accumulator;
  float fixed_dt;
  int substeps;
  int max_steps;
  uint64_t generation;
  bool begun;
  bool prune;
  bool step_without_begin_logged;
  int64_t version;
  PhysBody *bodies[PHYS2D_BODY_BUCKETS];
  PhysJoint *joints[PHYS2D_JOINT_BUCKETS];
  PhysShapeTombstone *shape_tombstones[PHYS2D_TOMBSTONE_BUCKETS];
  PhysEventBuffer events;
  PhysCommandQueue commands;
  PhysCallbacks callbacks;
  bool callbacks_pending;
  uint64_t callbacks_generation;
  struct PhysWorld *next;
};

typedef struct PhysWorldOpts {
  int64_t version;
  bool has_version;
  b2Vec2 gravity;
  float fixed_dt;
  int substeps;
  int max_steps;
  bool sleep;
  bool continuous;
  bool has_hit_event_threshold;
  float hit_event_threshold;
} PhysWorldOpts;

typedef struct PhysBodyDesc {
  int64_t version;
  bool has_version;
  b2BodyType type;
  bool fixed_rotation;
  bool bullet;
  bool enabled;
  bool has_enabled;
  bool awake;
  bool has_awake;
  bool sleep;
  bool has_sleep;
  float gravity_scale;
  float linear_damping;
  float angular_damping;
  float sleep_threshold;
  bool has_sleep_threshold;
  b2Vec2 initial_pos;
  float initial_angle;
  b2Vec2 initial_vel;
  float initial_w;
  bool initial_awake;
} PhysBodyDesc;

typedef struct PhysShapeDesc {
  int64_t version;
  bool has_version;
  float density;
  bool has_density;
  float friction;
  float restitution;
  int material_id;
  bool sensor;
  bool contact;
  bool hit;
  bool sensor_events;
  bool pre_solve;
  uint64_t category_bits;
  uint64_t mask_bits;
  int group_index;
} PhysShapeDesc;

typedef struct PhysJointDesc {
  int64_t version;
  bool has_version;
  PhysJointKind kind;
  PhysBody *body_a;
  PhysBody *body_b;
  b2Vec2 local_anchor_a;
  b2Vec2 local_anchor_b;
  b2Vec2 local_axis_a;
  b2Vec2 linear_offset;
  b2Vec2 target;
  float reference_angle;
  float length;
  float min_length;
  float max_length;
  float lower;
  float upper;
  float target_angle;
  float target_translation;
  float angular_offset;
  float hertz;
  float damping_ratio;
  float linear_hertz;
  float angular_hertz;
  float linear_damping_ratio;
  float angular_damping_ratio;
  float max_force;
  float max_torque;
  float motor_speed;
  float correction_factor;
  float draw_size;
  bool collide_connected;
  bool enable_spring;
  bool enable_limit;
  bool enable_motor;
} PhysJointDesc;

static PhysState *g_phys_state = NULL;
static PhysWorld *g_mixer_world = NULL;
static int g_phys_callback_depth = 0;

static bool body_is_live(PhysBody *b);
static bool shape_is_live(PhysShape *s);
static bool chain_is_live(PhysChain *c);
static bool joint_is_live(PhysJoint *j);
static bool phys2d_custom_filter_callback(b2ShapeId shape_id_a,
                                          b2ShapeId shape_id_b, void *context);
static bool phys2d_pre_solve_callback(b2ShapeId shape_id_a,
                                      b2ShapeId shape_id_b,
                                      b2Manifold *manifold, void *context);
static float phys2d_friction_callback(float friction_a, int material_a,
                                      float friction_b, int material_b);
static float phys2d_restitution_callback(float restitution_a, int material_a,
                                         float restitution_b, int material_b);
static void push_shape_id_view(lua_State *L, b2ShapeId shape_id);

static uint32_t hash_str32(const char *s) {
  uint32_t h = 2166136261u;
  while (*s) {
    h ^= (uint8_t)*s++;
    h *= 16777619u;
  }
  return h;
}

static uint64_t hash_init(void) { return 1469598103934665603ull; }

static uint64_t hash_u64(uint64_t h, uint64_t v) {
  for (int i = 0; i < 8; ++i) {
    h ^= (uint8_t)(v & 0xffu);
    h *= 1099511628211ull;
    v >>= 8;
  }
  return h;
}

static uint64_t hash_i64(uint64_t h, int64_t v) {
  return hash_u64(h, (uint64_t)v);
}

static uint64_t hash_f32(uint64_t h, float f) {
  uint32_t bits = 0;
  memcpy(&bits, &f, sizeof(bits));
  return hash_u64(h, bits);
}

static uint64_t hash_bool(uint64_t h, bool b) { return hash_u64(h, b ? 1 : 0); }

static uint64_t hash_cstr(uint64_t h, const char *s) {
  if (!s)
    s = "";
  while (*s) {
    h ^= (uint8_t)*s++;
    h *= 1099511628211ull;
  }
  return h;
}

static char *phys_strdup(const char *s) {
  if (!s)
    s = "";
  return SDL_strdup(s);
}

static void owned_string_clear(char **dst) {
  SDL_free(*dst);
  *dst = NULL;
}

static void owned_string_set_lua(lua_State *L, char **dst, const char *value,
                                 const char *fn_name) {
  if (value && value[0] == '\0')
    value = NULL;
  if (!*dst && !value)
    return;
  if (*dst && value && strcmp(*dst, value) == 0)
    return;
  char *copy = value ? phys_strdup(value) : NULL;
  if (value && !copy)
    luaL_error(L, "%s: out of memory", fn_name);
  SDL_free(*dst);
  *dst = copy;
}

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
                          int64_t *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_isinteger(L, -1) || lua_isnumber(L, -1);
  if (ok)
    *out = (int64_t)lua_tointeger(L, -1);
  lua_pop(L, 1);
  return ok;
}

static b2Vec2 table_vec2(lua_State *L, int idx, const char *a, const char *b,
                         b2Vec2 def) {
  b2Vec2 out = def;
  if (!table_get_any(L, idx, a, b))
    return out;
  if (lua_istable(L, -1)) {
    int t = lua_gettop(L);
    lua_getfield(L, t, "x");
    if (lua_isnumber(L, -1))
      out.x = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, t, "y");
    if (lua_isnumber(L, -1))
      out.y = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
    lua_rawgeti(L, t, 1);
    if (lua_isnumber(L, -1))
      out.x = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
    lua_rawgeti(L, t, 2);
    if (lua_isnumber(L, -1))
      out.y = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  return out;
}

static b2Vec2 value_vec2(lua_State *L, int idx, b2Vec2 def) {
  b2Vec2 out = def;
  if (!lua_istable(L, idx))
    return out;
  idx = abs_index(L, idx);
  lua_getfield(L, idx, "x");
  if (lua_isnumber(L, -1))
    out.x = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, idx, "y");
  if (lua_isnumber(L, -1))
    out.y = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 1);
  if (lua_isnumber(L, -1))
    out.x = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 2);
  if (lua_isnumber(L, -1))
    out.y = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return out;
}

static b2Vec2 value_vec2_optional(lua_State *L, int idx, b2Vec2 def,
                                  bool *has_x, bool *has_y) {
  b2Vec2 out = def;
  if (!lua_istable(L, idx))
    return out;
  idx = abs_index(L, idx);
  lua_getfield(L, idx, "x");
  if (lua_isnumber(L, -1)) {
    out.x = (float)lua_tonumber(L, -1);
    *has_x = true;
  }
  lua_pop(L, 1);
  lua_getfield(L, idx, "y");
  if (lua_isnumber(L, -1)) {
    out.y = (float)lua_tonumber(L, -1);
    *has_y = true;
  }
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 1);
  if (lua_isnumber(L, -1)) {
    out.x = (float)lua_tonumber(L, -1);
    *has_x = true;
  }
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 2);
  if (lua_isnumber(L, -1)) {
    out.y = (float)lua_tonumber(L, -1);
    *has_y = true;
  }
  lua_pop(L, 1);
  return out;
}

static bool opt_wake(lua_State *L, int idx, bool def) {
  if (!lua_istable(L, idx))
    return def;
  return table_bool(L, idx, "wake", NULL, def);
}

static bool is_ref(lua_State *L, int idx, const char *kind) {
  if (!lua_istable(L, idx))
    return false;
  idx = abs_index(L, idx);
  lua_getfield(L, idx, "__lub_kind");
  bool ok = lua_isstring(L, -1) && strcmp(lua_tostring(L, -1), kind) == 0;
  lua_pop(L, 1);
  return ok;
}

static const char *ref_string(lua_State *L, int idx, const char *field) {
  idx = abs_index(L, idx);
  lua_getfield(L, idx, field);
  const char *s = lua_tostring(L, -1);
  lua_pop(L, 1);
  return s;
}

static void set_cfunc_field(lua_State *L, const char *name, lua_CFunction fn) {
  lua_pushcfunction(L, fn);
  lua_setfield(L, -2, name);
}

static bool phys_in_callback(lua_State *L, const char *fn) {
  if (g_phys_callback_depth <= 0)
    return false;
  luaL_error(L, "%s: physics mutation is not allowed inside phys2d callback",
             fn);
  return true;
}

static bool callback_ref_is_set(int ref) {
  return ref != LUA_NOREF && ref != LUA_REFNIL;
}

static void callbacks_init(PhysCallbacks *callbacks) {
  callbacks->L = NULL;
  callbacks->filter_ref = LUA_NOREF;
  callbacks->pre_solve_ref = LUA_NOREF;
  callbacks->friction_ref = LUA_NOREF;
  callbacks->restitution_ref = LUA_NOREF;
  callbacks->filter_error_logged = false;
  callbacks->pre_solve_error_logged = false;
  callbacks->friction_error_logged = false;
  callbacks->restitution_error_logged = false;
}

static void callback_unref(lua_State *L, int *ref) {
  if (L && callback_ref_is_set(*ref))
    luaL_unref(L, LUA_REGISTRYINDEX, *ref);
  *ref = LUA_NOREF;
}

static bool callbacks_any(const PhysCallbacks *callbacks) {
  return callback_ref_is_set(callbacks->filter_ref) ||
         callback_ref_is_set(callbacks->pre_solve_ref) ||
         callback_ref_is_set(callbacks->friction_ref) ||
         callback_ref_is_set(callbacks->restitution_ref);
}

static void callbacks_install(PhysWorld *w) {
  if (!w || B2_IS_NULL(w->id) || !b2World_IsValid(w->id))
    return;
  b2World_SetCustomFilterCallback(
      w->id,
      callback_ref_is_set(w->callbacks.filter_ref)
          ? phys2d_custom_filter_callback
          : NULL,
      callback_ref_is_set(w->callbacks.filter_ref) ? w : NULL);
  b2World_SetPreSolveCallback(
      w->id,
      callback_ref_is_set(w->callbacks.pre_solve_ref)
          ? phys2d_pre_solve_callback
          : NULL,
      callback_ref_is_set(w->callbacks.pre_solve_ref) ? w : NULL);
  b2World_SetFrictionCallback(w->id,
                              callback_ref_is_set(w->callbacks.friction_ref)
                                  ? phys2d_friction_callback
                                  : NULL);
  b2World_SetRestitutionCallback(
      w->id, callback_ref_is_set(w->callbacks.restitution_ref)
                 ? phys2d_restitution_callback
                 : NULL);
}

static void callbacks_clear(lua_State *L, PhysWorld *w) {
  if (!w)
    return;
  callback_unref(L, &w->callbacks.filter_ref);
  callback_unref(L, &w->callbacks.pre_solve_ref);
  callback_unref(L, &w->callbacks.friction_ref);
  callback_unref(L, &w->callbacks.restitution_ref);
  w->callbacks.L = NULL;
  w->callbacks.filter_error_logged = false;
  w->callbacks.pre_solve_error_logged = false;
  w->callbacks.friction_error_logged = false;
  w->callbacks.restitution_error_logged = false;
  w->callbacks_pending = false;
  w->callbacks_generation = 0;
  callbacks_install(w);
}

static void callback_store_field(lua_State *L, int idx, const char *a,
                                 const char *b, int *ref) {
  if (!table_get_any(L, idx, a, b))
    return;
  if (lua_isfunction(L, -1)) {
    lua_pushvalue(L, -1);
    *ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  lua_pop(L, 1);
}

static void callbacks_replace_from_opts(lua_State *L, PhysWorld *w,
                                        int opts_idx) {
  callbacks_clear(L, w);
  if (!lua_istable(L, opts_idx))
    return;
  if (!table_get_any(L, opts_idx, "callbacks", NULL))
    return;
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return;
  }

  int cidx = lua_gettop(L);
  callback_store_field(L, cidx, "filter", NULL, &w->callbacks.filter_ref);
  callback_store_field(L, cidx, "pre_solve", "preSolve",
                       &w->callbacks.pre_solve_ref);
  callback_store_field(L, cidx, "friction", NULL, &w->callbacks.friction_ref);
  callback_store_field(L, cidx, "restitution", NULL,
                       &w->callbacks.restitution_ref);
  lua_pop(L, 1);

  if (!callbacks_any(&w->callbacks))
    return;
  w->callbacks.L = L;
  w->callbacks_pending = !w->begun;
  w->callbacks_generation = w->begun ? w->generation : 0;
  callbacks_install(w);
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

static void push_world_ref(lua_State *L, const char *key) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_world");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, key);
  lua_setfield(L, -2, "key");
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

static void push_body_ref(lua_State *L, const char *world_key,
                          const char *body_key) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_body");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, world_key);
  lua_setfield(L, -2, "world");
  lua_pushstring(L, body_key);
  lua_setfield(L, -2, "key");
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

static void push_chain_ref(lua_State *L, const char *world_key,
                           const char *body_key, const char *chain_key) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_chain");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, world_key);
  lua_setfield(L, -2, "world");
  lua_pushstring(L, body_key);
  lua_setfield(L, -2, "body");
  lua_pushstring(L, chain_key);
  lua_setfield(L, -2, "key");
  set_cfunc_field(L, "segments", l_phys2d_chain_segments);
}

static void push_shape_ref(lua_State *L, const char *world_key,
                           const char *body_key, const char *shape_key) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_shape");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, world_key);
  lua_setfield(L, -2, "world");
  lua_pushstring(L, body_key);
  lua_setfield(L, -2, "body");
  lua_pushstring(L, shape_key);
  lua_setfield(L, -2, "key");
  set_cfunc_field(L, "test_point", l_phys2d_shape_test_point);
  set_cfunc_field(L, "raycast", l_phys2d_shape_raycast);
  set_cfunc_field(L, "closest_point", l_phys2d_shape_closest_point);
  set_cfunc_field(L, "aabb", l_phys2d_shape_aabb);
  set_cfunc_field(L, "info", l_phys2d_shape_info);
  set_cfunc_field(L, "set_material", l_phys2d_shape_set_material);
  set_cfunc_field(L, "set_filter", l_phys2d_shape_set_filter);
  set_cfunc_field(L, "set_events", l_phys2d_shape_set_events);
}

static void push_joint_ref(lua_State *L, const char *world_key,
                           const char *joint_key) {
  lua_newtable(L);
  lua_pushstring(L, "phys2d_joint");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, world_key);
  lua_setfield(L, -2, "world");
  lua_pushstring(L, joint_key);
  lua_setfield(L, -2, "key");
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

static PhysWorld *world_get(PhysState *state, const char *key) {
  if (!state || !key)
    return NULL;
  uint32_t i = hash_str32(key) & (PHYS2D_WORLD_BUCKETS - 1);
  for (PhysWorld *w = state->worlds[i]; w; w = w->next) {
    if (strcmp(w->key, key) == 0)
      return w;
  }
  return NULL;
}

static PhysWorld *world_get_or_create(PhysState *state, const char *key) {
  PhysWorld *w = world_get(state, key);
  if (w)
    return w;
  uint32_t i = hash_str32(key) & (PHYS2D_WORLD_BUCKETS - 1);
  w = (PhysWorld *)SDL_calloc(1, sizeof(PhysWorld));
  if (!w)
    return NULL;
  w->key = phys_strdup(key);
  if (!w->key) {
    SDL_free(w);
    return NULL;
  }
  w->fixed_dt = 1.0f / 60.0f;
  w->substeps = 4;
  w->max_steps = 4;
  w->prune = true;
  w->version = INT64_MIN;
  w->id = b2_nullWorldId;
  callbacks_init(&w->callbacks);
  w->next = state->worlds[i];
  state->worlds[i] = w;
  return w;
}

static PhysBody *body_get(PhysWorld *w, const char *key) {
  if (!w || !key)
    return NULL;
  uint32_t i = hash_str32(key) & (PHYS2D_BODY_BUCKETS - 1);
  for (PhysBody *b = w->bodies[i]; b; b = b->next) {
    if (strcmp(b->key, key) == 0)
      return b;
  }
  return NULL;
}

static PhysBody *body_get_or_create(PhysWorld *w, const char *key) {
  PhysBody *b = body_get(w, key);
  if (b)
    return b;
  uint32_t i = hash_str32(key) & (PHYS2D_BODY_BUCKETS - 1);
  b = (PhysBody *)SDL_calloc(1, sizeof(PhysBody));
  if (!b)
    return NULL;
  b->key = phys_strdup(key);
  if (!b->key) {
    SDL_free(b);
    return NULL;
  }
  b->world = w;
  b->id = b2_nullBodyId;
  b->version = INT64_MIN;
  b->next = w->bodies[i];
  w->bodies[i] = b;
  return b;
}

static PhysShape *shape_get(PhysBody *b, const char *key) {
  if (!b || !key)
    return NULL;
  uint32_t i = hash_str32(key) & (PHYS2D_SHAPE_BUCKETS - 1);
  for (PhysShape *s = b->shapes[i]; s; s = s->next) {
    if (strcmp(s->key, key) == 0)
      return s;
  }
  return NULL;
}

static PhysShape *shape_get_or_create(PhysBody *b, const char *key) {
  PhysShape *s = shape_get(b, key);
  if (s)
    return s;
  uint32_t i = hash_str32(key) & (PHYS2D_SHAPE_BUCKETS - 1);
  s = (PhysShape *)SDL_calloc(1, sizeof(PhysShape));
  if (!s)
    return NULL;
  s->key = phys_strdup(key);
  if (!s->key) {
    SDL_free(s);
    return NULL;
  }
  s->body = b;
  s->id = b2_nullShapeId;
  s->next = b->shapes[i];
  b->shapes[i] = s;
  return s;
}

static PhysChain *chain_get(PhysBody *b, const char *key) {
  if (!b || !key)
    return NULL;
  uint32_t i = hash_str32(key) & (PHYS2D_CHAIN_BUCKETS - 1);
  for (PhysChain *c = b->chains[i]; c; c = c->next) {
    if (strcmp(c->key, key) == 0)
      return c;
  }
  return NULL;
}

static PhysChain *chain_get_or_create(PhysBody *b, const char *key) {
  PhysChain *c = chain_get(b, key);
  if (c)
    return c;
  uint32_t i = hash_str32(key) & (PHYS2D_CHAIN_BUCKETS - 1);
  c = (PhysChain *)SDL_calloc(1, sizeof(PhysChain));
  if (!c)
    return NULL;
  c->key = phys_strdup(key);
  if (!c->key) {
    SDL_free(c);
    return NULL;
  }
  c->body = b;
  c->id = b2_nullChainId;
  c->version = INT64_MIN;
  c->next = b->chains[i];
  b->chains[i] = c;
  return c;
}

static PhysJoint *joint_get(PhysWorld *w, const char *key) {
  if (!w || !key)
    return NULL;
  uint32_t i = hash_str32(key) & (PHYS2D_JOINT_BUCKETS - 1);
  for (PhysJoint *j = w->joints[i]; j; j = j->next) {
    if (strcmp(j->key, key) == 0)
      return j;
  }
  return NULL;
}

static PhysJoint *joint_get_or_create(PhysWorld *w, const char *key) {
  PhysJoint *j = joint_get(w, key);
  if (j)
    return j;
  uint32_t i = hash_str32(key) & (PHYS2D_JOINT_BUCKETS - 1);
  j = (PhysJoint *)SDL_calloc(1, sizeof(PhysJoint));
  if (!j)
    return NULL;
  j->key = phys_strdup(key);
  if (!j->key) {
    SDL_free(j);
    return NULL;
  }
  j->world = w;
  j->id = b2_nullJointId;
  j->version = INT64_MIN;
  j->next = w->joints[i];
  w->joints[i] = j;
  return j;
}

static uint32_t shape_tombstone_bucket(uint64_t id_key) {
  return (uint32_t)(id_key ^ (id_key >> 32)) & (PHYS2D_TOMBSTONE_BUCKETS - 1);
}

static PhysShapeTombstone *shape_tombstone_get(PhysWorld *w,
                                               b2ShapeId shape_id) {
  if (!w || B2_IS_NULL(shape_id))
    return NULL;
  uint64_t id_key = b2StoreShapeId(shape_id);
  uint32_t bucket = shape_tombstone_bucket(id_key);
  for (PhysShapeTombstone *t = w->shape_tombstones[bucket]; t; t = t->next) {
    if (t->id_key == id_key)
      return t;
  }
  return NULL;
}

static void shape_tombstone_put(PhysWorld *w, b2ShapeId shape_id,
                                const char *body, const char *shape,
                                const char *tag, const char *material,
                                int material_id) {
  if (!w || B2_IS_NULL(shape_id))
    return;

  char *body_copy = phys_strdup(body ? body : "");
  char *shape_copy = phys_strdup(shape ? shape : "");
  char *tag_copy = phys_strdup(tag ? tag : "");
  char *material_copy = phys_strdup(material ? material : "");
  if (!body_copy || !shape_copy || !tag_copy || !material_copy) {
    SDL_free(body_copy);
    SDL_free(shape_copy);
    SDL_free(tag_copy);
    SDL_free(material_copy);
    return;
  }

  uint64_t id_key = b2StoreShapeId(shape_id);
  uint32_t bucket = shape_tombstone_bucket(id_key);
  PhysShapeTombstone *t = shape_tombstone_get(w, shape_id);
  if (!t) {
    t = (PhysShapeTombstone *)SDL_calloc(1, sizeof(*t));
    if (!t) {
      SDL_free(body_copy);
      SDL_free(shape_copy);
      SDL_free(tag_copy);
      SDL_free(material_copy);
      return;
    }
    t->id_key = id_key;
    t->next = w->shape_tombstones[bucket];
    w->shape_tombstones[bucket] = t;
  }

  SDL_free(t->body);
  SDL_free(t->shape);
  SDL_free(t->tag);
  SDL_free(t->material);
  t->body = body_copy;
  t->shape = shape_copy;
  t->tag = tag_copy;
  t->material = material_copy;
  t->material_id = material_id;
}

static void shape_tombstone_update_shape(PhysShape *shape) {
  if (!shape || !shape->body || !shape->body->world || B2_IS_NULL(shape->id) ||
      !b2Shape_IsValid(shape->id))
    return;
  shape_tombstone_put(shape->body->world, shape->id, shape->body->key,
                      shape->key, shape->tag, shape->material_name,
                      b2Shape_GetMaterial(shape->id));
}

static void shape_tombstone_clear(PhysShapeTombstone *t) {
  if (!t)
    return;
  SDL_free(t->body);
  SDL_free(t->shape);
  SDL_free(t->tag);
  SDL_free(t->material);
  SDL_free(t);
}

static void shape_tombstone_clear_all(PhysWorld *w) {
  if (!w)
    return;
  for (int i = 0; i < PHYS2D_TOMBSTONE_BUCKETS; ++i) {
    PhysShapeTombstone *t = w->shape_tombstones[i];
    while (t) {
      PhysShapeTombstone *next = t->next;
      shape_tombstone_clear(t);
      t = next;
    }
    w->shape_tombstones[i] = NULL;
  }
}

static void snapshot_clear_one(PhysContactSnapshot *e) {
  SDL_free(e->a_body);
  SDL_free(e->a_shape);
  SDL_free(e->a_tag);
  SDL_free(e->a_material);
  SDL_free(e->b_body);
  SDL_free(e->b_shape);
  SDL_free(e->b_tag);
  SDL_free(e->b_material);
  memset(e, 0, sizeof(*e));
}

static void body_snapshot_clear_one(PhysBodyEventSnapshot *e) {
  SDL_free(e->body);
  memset(e, 0, sizeof(*e));
}

static void event_buffer_clear(PhysEventBuffer *events) {
  for (int i = 0; i < events->begin_count; ++i)
    snapshot_clear_one(&events->begins[i]);
  for (int i = 0; i < events->end_count; ++i)
    snapshot_clear_one(&events->ends[i]);
  for (int i = 0; i < events->hit_count; ++i)
    snapshot_clear_one(&events->hits[i]);
  for (int i = 0; i < events->sensor_begin_count; ++i)
    snapshot_clear_one(&events->sensor_begins[i]);
  for (int i = 0; i < events->sensor_end_count; ++i)
    snapshot_clear_one(&events->sensor_ends[i]);
  for (int i = 0; i < events->move_count; ++i)
    body_snapshot_clear_one(&events->moves[i]);
  events->begin_count = 0;
  events->end_count = 0;
  events->hit_count = 0;
  events->sensor_begin_count = 0;
  events->sensor_end_count = 0;
  events->move_count = 0;
}

static void event_buffer_free(PhysEventBuffer *events) {
  event_buffer_clear(events);
  SDL_free(events->begins);
  SDL_free(events->ends);
  SDL_free(events->hits);
  SDL_free(events->sensor_begins);
  SDL_free(events->sensor_ends);
  SDL_free(events->moves);
  memset(events, 0, sizeof(*events));
}

static PhysContactSnapshot *event_push(PhysContactSnapshot **items, int *count,
                                       int *cap) {
  if (*count >= *cap) {
    int new_cap = *cap ? *cap * 2 : 8;
    PhysContactSnapshot *new_items =
        (PhysContactSnapshot *)SDL_realloc(*items, sizeof(**items) * new_cap);
    if (!new_items)
      return NULL;
    memset(new_items + *cap, 0, sizeof(**items) * (new_cap - *cap));
    *items = new_items;
    *cap = new_cap;
  }
  PhysContactSnapshot *out = &(*items)[(*count)++];
  memset(out, 0, sizeof(*out));
  return out;
}

static PhysBodyEventSnapshot *body_event_push(PhysBodyEventSnapshot **items,
                                              int *count, int *cap) {
  if (*count >= *cap) {
    int new_cap = *cap ? *cap * 2 : 16;
    PhysBodyEventSnapshot *new_items =
        (PhysBodyEventSnapshot *)SDL_realloc(*items, sizeof(**items) * new_cap);
    if (!new_items)
      return NULL;
    memset(new_items + *cap, 0, sizeof(**items) * (new_cap - *cap));
    *items = new_items;
    *cap = new_cap;
  }
  PhysBodyEventSnapshot *out = &(*items)[(*count)++];
  memset(out, 0, sizeof(*out));
  return out;
}

static void command_clear(PhysCommand *cmd) {
  SDL_free(cmd->body_key);
  memset(cmd, 0, sizeof(*cmd));
}

static void command_queue_clear(PhysCommandQueue *queue) {
  if (!queue)
    return;
  for (int i = 0; i < queue->count; ++i)
    command_clear(&queue->items[i]);
  queue->count = 0;
}

static void command_queue_free(PhysCommandQueue *queue) {
  if (!queue)
    return;
  command_queue_clear(queue);
  SDL_free(queue->items);
  memset(queue, 0, sizeof(*queue));
}

static PhysCommand *command_queue_push(lua_State *L, PhysWorld *w, PhysBody *b,
                                       PhysCommandKind kind, const char *fn) {
  if (!w || !b || !b->key)
    luaL_error(L, "%s: missing body", fn);
  PhysCommandQueue *queue = &w->commands;
  if (queue->count >= queue->cap) {
    int new_cap = queue->cap ? queue->cap * 2 : 32;
    PhysCommand *new_items = (PhysCommand *)SDL_realloc(
        queue->items, sizeof(*queue->items) * new_cap);
    if (!new_items)
      luaL_error(L, "%s: out of memory", fn);
    memset(new_items + queue->cap, 0,
           sizeof(*queue->items) * (new_cap - queue->cap));
    queue->items = new_items;
    queue->cap = new_cap;
  }
  PhysCommand *cmd = &queue->items[queue->count++];
  memset(cmd, 0, sizeof(*cmd));
  cmd->body_key = phys_strdup(b->key);
  if (!cmd->body_key) {
    queue->count--;
    luaL_error(L, "%s: out of memory", fn);
  }
  cmd->kind = kind;
  if (B2_IS_NON_NULL(b->id) && b2Body_IsValid(b->id))
    cmd->body_id_key = b2StoreBodyId(b->id);
  cmd->wake = true;
  return cmd;
}

static void shape_free(PhysShape *s, bool destroy_id) {
  if (!s)
    return;
  if (destroy_id && B2_IS_NON_NULL(s->id) && b2Shape_IsValid(s->id)) {
    b2DestroyShape(s->id, true);
  }
  owned_string_clear(&s->tag);
  owned_string_clear(&s->material_name);
  SDL_free(s->key);
  SDL_free(s);
}

static void chain_free(PhysChain *c, bool destroy_id) {
  if (!c)
    return;
  if (destroy_id && B2_IS_NON_NULL(c->id) && b2Chain_IsValid(c->id)) {
    b2DestroyChain(c->id);
  }
  owned_string_clear(&c->tag);
  owned_string_clear(&c->material_name);
  SDL_free(c->key);
  SDL_free(c);
}

static void joint_free(PhysJoint *j, bool destroy_id) {
  if (!j)
    return;
  if (destroy_id && B2_IS_NON_NULL(j->id) && b2Joint_IsValid(j->id)) {
    b2DestroyJoint(j->id);
  }
  SDL_free(j->key);
  SDL_free(j);
}

static void world_remove_joints_for_body(PhysWorld *w, PhysBody *body,
                                         bool destroy_ids) {
  if (!w || !body)
    return;
  for (int i = 0; i < PHYS2D_JOINT_BUCKETS; ++i) {
    PhysJoint **prev = &w->joints[i];
    PhysJoint *j = w->joints[i];
    while (j) {
      PhysJoint *next = j->next;
      if (j->body_a == body || j->body_b == body) {
        *prev = next;
        joint_free(j, destroy_ids);
      } else {
        prev = &j->next;
      }
      j = next;
    }
  }
}

static void body_free_shapes(PhysBody *b, bool destroy_ids) {
  for (int i = 0; i < PHYS2D_SHAPE_BUCKETS; ++i) {
    PhysShape *s = b->shapes[i];
    while (s) {
      PhysShape *next = s->next;
      shape_free(s, destroy_ids);
      s = next;
    }
    b->shapes[i] = NULL;
  }
}

static void body_free_chains(PhysBody *b, bool destroy_ids) {
  for (int i = 0; i < PHYS2D_CHAIN_BUCKETS; ++i) {
    PhysChain *c = b->chains[i];
    while (c) {
      PhysChain *next = c->next;
      chain_free(c, destroy_ids);
      c = next;
    }
    b->chains[i] = NULL;
  }
}

static void body_free(PhysBody *b, bool destroy_id) {
  if (!b)
    return;
  if (destroy_id && B2_IS_NON_NULL(b->id) && b2Body_IsValid(b->id)) {
    world_remove_joints_for_body(b->world, b, true);
    b2DestroyBody(b->id);
    body_free_shapes(b, false);
    body_free_chains(b, false);
  } else {
    body_free_shapes(b, destroy_id);
    body_free_chains(b, destroy_id);
  }
  SDL_free(b->key);
  SDL_free(b);
}

static void world_free_joints(PhysWorld *w, bool destroy_ids) {
  for (int i = 0; i < PHYS2D_JOINT_BUCKETS; ++i) {
    PhysJoint *j = w->joints[i];
    while (j) {
      PhysJoint *next = j->next;
      joint_free(j, destroy_ids);
      j = next;
    }
    w->joints[i] = NULL;
  }
}

static void world_free_bodies(PhysWorld *w, bool destroy_ids) {
  for (int i = 0; i < PHYS2D_BODY_BUCKETS; ++i) {
    PhysBody *b = w->bodies[i];
    while (b) {
      PhysBody *next = b->next;
      body_free(b, destroy_ids);
      b = next;
    }
    w->bodies[i] = NULL;
  }
}

static void world_destroy_box2d_and_contents(PhysWorld *w) {
  if (B2_IS_NON_NULL(w->id) && b2World_IsValid(w->id)) {
    b2DestroyWorld(w->id);
    w->id = b2_nullWorldId;
    world_free_joints(w, false);
    world_free_bodies(w, false);
  } else {
    world_free_joints(w, true);
    world_free_bodies(w, true);
  }
  event_buffer_clear(&w->events);
  command_queue_clear(&w->commands);
  shape_tombstone_clear_all(w);
  w->accumulator = 0.0;
  w->begun = false;
  w->step_without_begin_logged = false;
}

static void world_free(PhysWorld *w) {
  if (!w)
    return;
  callbacks_clear(w->callbacks.L, w);
  world_destroy_box2d_and_contents(w);
  event_buffer_free(&w->events);
  command_queue_free(&w->commands);
  SDL_free(w->key);
  SDL_free(w);
}

void phys2d_state_init(PhysState *state) { memset(state, 0, sizeof(*state)); }

void phys2d_state_shutdown(PhysState *state) {
  if (!state)
    return;
  for (int i = 0; i < PHYS2D_WORLD_BUCKETS; ++i) {
    PhysWorld *w = state->worlds[i];
    while (w) {
      PhysWorld *next = w->next;
      world_free(w);
      w = next;
    }
    state->worlds[i] = NULL;
  }
}

void phys2d_lua_set_state(PhysState *state) { g_phys_state = state; }

static PhysWorld *check_world(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_world"))
    luaL_error(L, "expected Phys2d WorldRef");
  const char *key = ref_string(L, idx, "key");
  PhysWorld *w = world_get(g_phys_state, key);
  if (!w)
    luaL_error(L, "phys2d world not found: %s", key ? key : "?");
  return w;
}

static PhysWorld *query_world_ref(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_world"))
    luaL_error(L, "expected Phys2d WorldRef");
  const char *key = ref_string(L, idx, "key");
  PhysWorld *w = world_get(g_phys_state, key);
  if (!w || B2_IS_NULL(w->id) || !b2World_IsValid(w->id))
    return NULL;
  return w;
}

static PhysBody *check_body(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_body"))
    luaL_error(L, "expected Phys2d BodyRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *body_key = ref_string(L, idx, "key");
  PhysWorld *w = world_get(g_phys_state, world_key);
  PhysBody *b = w ? body_get(w, body_key) : NULL;
  if (!b)
    luaL_error(L, "phys2d body not found: %s/%s", world_key ? world_key : "?",
               body_key ? body_key : "?");
  return b;
}

static PhysBody *query_body_ref(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_body"))
    luaL_error(L, "expected Phys2d BodyRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *body_key = ref_string(L, idx, "key");
  PhysWorld *w = world_get(g_phys_state, world_key);
  PhysBody *b = w ? body_get(w, body_key) : NULL;
  return body_is_live(b) ? b : NULL;
}

static int push_not_found(lua_State *L) {
  lua_pushnil(L);
  lua_pushstring(L, "not found");
  return 2;
}

static PhysShape *check_shape(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_shape"))
    luaL_error(L, "expected Phys2d ShapeRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *body_key = ref_string(L, idx, "body");
  const char *shape_key = ref_string(L, idx, "key");
  PhysWorld *w = world_get(g_phys_state, world_key);
  PhysBody *b = w ? body_get(w, body_key) : NULL;
  PhysShape *s = b ? shape_get(b, shape_key) : NULL;
  if (!s)
    luaL_error(L, "phys2d shape not found: %s/%s/%s",
               world_key ? world_key : "?", body_key ? body_key : "?",
               shape_key ? shape_key : "?");
  return s;
}

static PhysShape *query_shape_ref(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_shape"))
    luaL_error(L, "expected Phys2d ShapeRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *body_key = ref_string(L, idx, "body");
  const char *shape_key = ref_string(L, idx, "key");
  PhysWorld *w = world_get(g_phys_state, world_key);
  PhysBody *b = w ? body_get(w, body_key) : NULL;
  PhysShape *s = b ? shape_get(b, shape_key) : NULL;
  return shape_is_live(s) ? s : NULL;
}

static PhysChain *query_chain_ref(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_chain"))
    luaL_error(L, "expected Phys2d ChainRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *body_key = ref_string(L, idx, "body");
  const char *chain_key = ref_string(L, idx, "key");
  PhysWorld *w = world_get(g_phys_state, world_key);
  PhysBody *b = w ? body_get(w, body_key) : NULL;
  PhysChain *c = b ? chain_get(b, chain_key) : NULL;
  return chain_is_live(c) ? c : NULL;
}

static PhysJoint *check_joint(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_joint"))
    luaL_error(L, "expected Phys2d JointRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *joint_key = ref_string(L, idx, "key");
  PhysWorld *w = world_get(g_phys_state, world_key);
  PhysJoint *j = w ? joint_get(w, joint_key) : NULL;
  if (!j)
    luaL_error(L, "phys2d joint not found: %s/%s", world_key ? world_key : "?",
               joint_key ? joint_key : "?");
  return j;
}

static PhysJoint *query_joint_ref(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys2d_joint"))
    luaL_error(L, "expected Phys2d JointRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *joint_key = ref_string(L, idx, "key");
  PhysWorld *w = world_get(g_phys_state, world_key);
  PhysJoint *j = w ? joint_get(w, joint_key) : NULL;
  return joint_is_live(j) ? j : NULL;
}

static PhysChain *chain_find_by_id(b2ChainId id) {
  if (B2_IS_NULL(id))
    return NULL;
  for (int wi = 0; wi < PHYS2D_WORLD_BUCKETS; ++wi) {
    for (PhysWorld *w = g_phys_state ? g_phys_state->worlds[wi] : NULL; w;
         w = w->next) {
      for (int bi = 0; bi < PHYS2D_BODY_BUCKETS; ++bi) {
        for (PhysBody *b = w->bodies[bi]; b; b = b->next) {
          for (int ci = 0; ci < PHYS2D_CHAIN_BUCKETS; ++ci) {
            for (PhysChain *c = b->chains[ci]; c; c = c->next) {
              if (B2_IS_NON_NULL(c->id) && B2_ID_EQUALS(c->id, id))
                return c;
            }
          }
        }
      }
    }
  }
  return NULL;
}

static void parse_world_opts(lua_State *L, int idx, PhysWorldOpts *opts) {
  opts->version = 0;
  opts->has_version = false;
  opts->gravity = (b2Vec2){0.0f, -9.8f};
  opts->fixed_dt = 1.0f / 60.0f;
  opts->substeps = 4;
  opts->max_steps = 4;
  opts->sleep = true;
  opts->continuous = true;
  opts->has_hit_event_threshold = false;
  opts->hit_event_threshold = 0.0f;

  if (!lua_istable(L, idx))
    return;
  int64_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    opts->version = v;
    opts->has_version = true;
  }
  opts->gravity = table_vec2(L, idx, "gravity", NULL, opts->gravity);
  opts->fixed_dt = table_number(L, idx, "fixed_dt", "fixedDt", opts->fixed_dt);
  opts->substeps = table_int(L, idx, "substeps", NULL, opts->substeps);
  opts->max_steps = table_int(L, idx, "max_steps", "maxSteps", opts->max_steps);
  opts->sleep = table_bool(L, idx, "sleep", NULL, opts->sleep);
  opts->continuous = table_bool(L, idx, "continuous", NULL, opts->continuous);
  if (table_get_any(L, idx, "hit_event_threshold", "hitEventThreshold")) {
    if (lua_isnumber(L, -1)) {
      opts->has_hit_event_threshold = true;
      opts->hit_event_threshold = (float)lua_tonumber(L, -1);
    }
    lua_pop(L, 1);
  }
  if (opts->fixed_dt <= 0.0f)
    opts->fixed_dt = 1.0f / 60.0f;
  if (opts->substeps <= 0)
    opts->substeps = 4;
  if (opts->max_steps <= 0)
    opts->max_steps = 4;
}

static bool world_create_or_recreate(lua_State *L, PhysWorld *w,
                                     const PhysWorldOpts *opts) {
  int64_t version = opts->has_version ? opts->version : 0;
  bool needs_create =
      B2_IS_NULL(w->id) || !b2World_IsValid(w->id) || w->version != version;
  if (needs_create) {
    callbacks_clear(L, w);
    world_destroy_box2d_and_contents(w);
    b2WorldDef def = b2DefaultWorldDef();
    def.gravity = opts->gravity;
    def.enableSleep = opts->sleep;
    w->id = b2CreateWorld(&def);
    if (B2_IS_NULL(w->id))
      return luaL_error(L, "phys2d_world: b2CreateWorld failed"), false;
    w->version = version;
  } else {
    b2World_SetGravity(w->id, opts->gravity);
    b2World_EnableSleeping(w->id, opts->sleep);
  }
  b2World_EnableContinuous(w->id, opts->continuous);
  if (opts->has_hit_event_threshold)
    b2World_SetHitEventThreshold(w->id, opts->hit_event_threshold);
  w->fixed_dt = opts->fixed_dt;
  w->substeps = opts->substeps;
  w->max_steps = opts->max_steps;
  return true;
}

static int l_phys2d_world(lua_State *L) {
  if (phys_in_callback(L, "phys2d_world"))
    return 0;
  const char *key = luaL_checkstring(L, 1);
  PhysWorldOpts opts;
  parse_world_opts(L, 2, &opts);
  PhysWorld *w = world_get_or_create(g_phys_state, key);
  if (!w)
    return luaL_error(L, "phys2d_world: out of memory");
  if (!world_create_or_recreate(L, w, &opts))
    return 0;
  callbacks_replace_from_opts(L, w, 2);
  push_world_ref(L, key);
  return 1;
}

static int l_phys2d_begin(lua_State *L) {
  if (phys_in_callback(L, "phys2d_begin"))
    return 0;
  PhysWorld *w = check_world(L, 1);
  w->generation++;
  if (w->generation == 0)
    w->generation = 1;
  if (w->callbacks_pending) {
    w->callbacks_generation = w->generation;
    w->callbacks_pending = false;
  } else if (callbacks_any(&w->callbacks) &&
             w->callbacks_generation != w->generation) {
    callbacks_clear(L, w);
  }
  w->prune = lua_istable(L, 2) ? table_bool(L, 2, "prune", NULL, true) : true;
  w->begun = true;
  return 0;
}

static b2BodyType parse_body_type(lua_State *L, int idx) {
  int type = (int)luaL_checkinteger(L, idx);
  switch (type) {
  case PHYS2D_STATIC:
    return b2_staticBody;
  case PHYS2D_KINEMATIC:
    return b2_kinematicBody;
  case PHYS2D_DYNAMIC:
    return b2_dynamicBody;
  default:
    luaL_error(L, "phys2d_body: unknown body type %d", type);
    return b2_staticBody;
  }
}

static void parse_initial(lua_State *L, int idx, PhysBodyDesc *desc) {
  if (!table_get_any(L, idx, "initial", NULL))
    return;
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return;
  }
  int t = lua_gettop(L);
  desc->initial_pos.x = table_number(L, t, "x", NULL, desc->initial_pos.x);
  desc->initial_pos.y = table_number(L, t, "y", NULL, desc->initial_pos.y);
  desc->initial_angle = table_number(L, t, "angle", NULL, desc->initial_angle);
  desc->initial_vel.x = table_number(L, t, "vx", NULL, desc->initial_vel.x);
  desc->initial_vel.y = table_number(L, t, "vy", NULL, desc->initial_vel.y);
  desc->initial_w = table_number(L, t, "w", NULL, desc->initial_w);
  desc->initial_awake = table_bool(L, t, "awake", NULL, desc->initial_awake);
  lua_pop(L, 1);
}

static void parse_body_desc(lua_State *L, int idx, PhysBodyDesc *desc) {
  desc->version = 0;
  desc->has_version = false;
  desc->type = b2_staticBody;
  desc->fixed_rotation = false;
  desc->bullet = false;
  desc->enabled = true;
  desc->has_enabled = false;
  desc->awake = true;
  desc->has_awake = false;
  desc->sleep = true;
  desc->has_sleep = false;
  desc->gravity_scale = 1.0f;
  desc->linear_damping = 0.0f;
  desc->angular_damping = 0.0f;
  desc->sleep_threshold = 0.0f;
  desc->has_sleep_threshold = false;
  desc->initial_pos = b2Vec2_zero;
  desc->initial_angle = 0.0f;
  desc->initial_vel = b2Vec2_zero;
  desc->initial_w = 0.0f;
  desc->initial_awake = true;

  luaL_checktype(L, idx, LUA_TTABLE);
  int64_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    desc->version = v;
    desc->has_version = true;
  }
  if (table_get_any(L, idx, "type", NULL)) {
    desc->type = parse_body_type(L, lua_gettop(L));
    lua_pop(L, 1);
  }
  desc->fixed_rotation =
      table_bool(L, idx, "fixed_rotation", "fixedRotation", false);
  desc->bullet = table_bool(L, idx, "bullet", NULL, false);
  if (table_get_any(L, idx, "enabled", NULL)) {
    if (lua_isboolean(L, -1)) {
      desc->enabled = lua_toboolean(L, -1);
      desc->has_enabled = true;
    }
    lua_pop(L, 1);
  }
  if (table_get_any(L, idx, "awake", NULL)) {
    if (lua_isboolean(L, -1)) {
      desc->awake = lua_toboolean(L, -1);
      desc->has_awake = true;
    }
    lua_pop(L, 1);
  }
  if (table_get_any(L, idx, "sleep", "enableSleep")) {
    if (lua_isboolean(L, -1)) {
      desc->sleep = lua_toboolean(L, -1);
      desc->has_sleep = true;
    }
    lua_pop(L, 1);
  }
  desc->gravity_scale =
      table_number(L, idx, "gravity_scale", "gravityScale", 1.0f);
  desc->linear_damping =
      table_number(L, idx, "linear_damping", "linearDamping", 0.0f);
  desc->angular_damping =
      table_number(L, idx, "angular_damping", "angularDamping", 0.0f);
  if (table_get_any(L, idx, "sleep_threshold", "sleepThreshold")) {
    if (lua_isnumber(L, -1)) {
      desc->sleep_threshold = (float)lua_tonumber(L, -1);
      desc->has_sleep_threshold = true;
    }
    lua_pop(L, 1);
  }
  if (desc->sleep_threshold < 0.0f)
    luaL_error(L, "phys2d_body: sleep_threshold must be >= 0");
  parse_initial(L, idx, desc);
}

static uint64_t body_constructor_hash(const PhysBodyDesc *desc) {
  uint64_t h = hash_init();
  h = hash_f32(h, desc->initial_pos.x);
  h = hash_f32(h, desc->initial_pos.y);
  h = hash_f32(h, desc->initial_angle);
  h = hash_f32(h, desc->initial_vel.x);
  h = hash_f32(h, desc->initial_vel.y);
  h = hash_f32(h, desc->initial_w);
  h = hash_bool(h, desc->initial_awake);
  return h;
}

static int64_t body_effective_version(const PhysBodyDesc *desc,
                                      uint64_t fallback_hash) {
  return desc->has_version ? desc->version : (int64_t)fallback_hash;
}

static void log_body_constructor_drift(PhysBody *b, uint64_t hash) {
  if (b->constructor_hash == hash || b->constructor_warned)
    return;
  SDL_Log("phys2d_body('%s'): constructor fields changed without version bump",
          b->key);
  b->constructor_warned = true;
}

static void body_create(lua_State *L, PhysBody *b, const PhysBodyDesc *desc,
                        uint64_t constructor_hash, int64_t version) {
  b2BodyDef def = b2DefaultBodyDef();
  def.type = desc->type;
  def.position = desc->initial_pos;
  def.rotation = b2MakeRot(desc->initial_angle);
  def.linearVelocity = desc->initial_vel;
  def.angularVelocity = desc->initial_w;
  def.fixedRotation = desc->fixed_rotation;
  def.isBullet = desc->bullet;
  def.gravityScale = desc->gravity_scale;
  def.linearDamping = desc->linear_damping;
  def.angularDamping = desc->angular_damping;
  def.isAwake = desc->initial_awake;
  if (desc->has_enabled)
    def.isEnabled = desc->enabled;
  if (desc->has_sleep)
    def.enableSleep = desc->sleep;
  if (desc->has_sleep_threshold)
    def.sleepThreshold = desc->sleep_threshold;
  def.userData = b;
  b->id = b2CreateBody(b->world->id, &def);
  if (B2_IS_NULL(b->id))
    luaL_error(L, "phys2d_body: b2CreateBody failed");
  if (desc->has_awake)
    b2Body_SetAwake(b->id, desc->awake);
  b->version = version;
  b->constructor_hash = constructor_hash;
  b->constructor_warned = false;
}

static void body_apply_runtime(PhysBody *b, const PhysBodyDesc *desc) {
  b2Body_SetType(b->id, desc->type);
  b2Body_SetFixedRotation(b->id, desc->fixed_rotation);
  b2Body_SetBullet(b->id, desc->bullet);
  b2Body_SetGravityScale(b->id, desc->gravity_scale);
  b2Body_SetLinearDamping(b->id, desc->linear_damping);
  b2Body_SetAngularDamping(b->id, desc->angular_damping);
  if (desc->has_awake)
    b2Body_SetAwake(b->id, desc->awake);
  if (desc->has_sleep)
    b2Body_EnableSleep(b->id, desc->sleep);
  if (desc->has_sleep_threshold)
    b2Body_SetSleepThreshold(b->id, desc->sleep_threshold);
  if (desc->has_enabled && b2Body_IsEnabled(b->id) != desc->enabled) {
    if (desc->enabled) {
      b2Body_Enable(b->id);
    } else {
      b2Body_Disable(b->id);
    }
  }
}

static int l_phys2d_body(lua_State *L) {
  if (phys_in_callback(L, "phys2d_body"))
    return 0;
  PhysWorld *w = check_world(L, 1);
  const char *key = luaL_checkstring(L, 2);
  PhysBodyDesc desc;
  parse_body_desc(L, 3, &desc);
  if (!w->begun)
    return luaL_error(L, "phys2d_body: call phys2d_begin(world) first");
  PhysBody *b = body_get_or_create(w, key);
  if (!b)
    return luaL_error(L, "phys2d_body: out of memory");
  uint64_t constructor_hash = body_constructor_hash(&desc);
  int64_t version = body_effective_version(&desc, constructor_hash);
  if (B2_IS_NULL(b->id) || !b2Body_IsValid(b->id) || b->version != version) {
    if (B2_IS_NON_NULL(b->id) && b2Body_IsValid(b->id)) {
      world_remove_joints_for_body(w, b, true);
      b2DestroyBody(b->id);
      body_free_shapes(b, false);
      body_free_chains(b, false);
      b->id = b2_nullBodyId;
    }
    body_create(L, b, &desc, constructor_hash, version);
  } else {
    if (desc.has_version)
      log_body_constructor_drift(b, constructor_hash);
    b->constructor_hash = constructor_hash;
    body_apply_runtime(b, &desc);
  }
  b->seen_generation = w->generation;
  push_body_ref(L, w->key, b->key);
  return 1;
}

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
                               uint64_t *mask_bits, int *group_index) {
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

static void parse_filter(lua_State *L, int idx, PhysShapeDesc *desc) {
  if (!table_get_any(L, idx, "filter", NULL))
    return;
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return;
  }
  parse_filter_table(L, lua_gettop(L), &desc->category_bits, &desc->mask_bits,
                     &desc->group_index);
  lua_pop(L, 1);
}

static void parse_shape_desc(lua_State *L, int idx, PhysShapeDesc *desc) {
  desc->version = 0;
  desc->has_version = false;
  desc->density = 1.0f;
  desc->has_density = false;
  desc->friction = 0.6f;
  desc->restitution = 0.0f;
  desc->material_id = 0;
  desc->sensor = false;
  desc->contact = false;
  desc->hit = false;
  desc->sensor_events = false;
  desc->pre_solve = false;
  desc->category_bits = 1u;
  desc->mask_bits = UINT64_MAX;
  desc->group_index = 0;
  int64_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    desc->version = v;
    desc->has_version = true;
  }
  desc->has_density =
      table_number_optional(L, idx, "density", NULL, &desc->density);
  desc->friction = table_number(L, idx, "friction", NULL, desc->friction);
  desc->restitution =
      table_number(L, idx, "restitution", NULL, desc->restitution);
  desc->material_id =
      table_int(L, idx, "material", "materialId", desc->material_id);
  desc->material_id = table_int(L, idx, "user_material_id", "userMaterialId",
                                desc->material_id);
  desc->sensor = table_bool(L, idx, "sensor", NULL, desc->sensor);
  desc->contact = table_bool(L, idx, "contact", NULL, desc->contact);
  desc->hit = table_bool(L, idx, "hit", NULL, desc->hit);
  desc->sensor_events =
      table_bool(L, idx, "sensor_events", "sensorEvents", desc->sensor_events);
  desc->pre_solve =
      table_bool(L, idx, "pre_solve", "preSolve", desc->pre_solve);
  parse_filter(L, idx, desc);
}

static void shape_apply_density_default(PhysBody *body, PhysShapeDesc *desc) {
  if (!body || !desc || desc->has_density || !body_is_live(body))
    return;
  desc->density = b2Body_GetType(body->id) == b2_dynamicBody ? 1.0f : 0.0f;
}

static void shape_update_metadata(lua_State *L, PhysShape *shape, int idx,
                                  int material_id) {
  const char *tag = NULL;
  if (table_get_any(L, idx, "tag", NULL)) {
    if (lua_type(L, -1) == LUA_TSTRING)
      tag = lua_tostring(L, -1);
    owned_string_set_lua(L, &shape->tag, tag, "phys2d shape metadata");
    lua_pop(L, 1);
  } else {
    owned_string_set_lua(L, &shape->tag, NULL, "phys2d shape metadata");
  }

  const char *material_name = NULL;
  lua_getfield(L, abs_index(L, idx), "material");
  if (lua_type(L, -1) == LUA_TSTRING)
    material_name = lua_tostring(L, -1);
  owned_string_set_lua(L, &shape->material_name, material_name,
                       "phys2d shape metadata");
  lua_pop(L, 1);
  shape->material_id = material_id;
}

static void chain_update_metadata(lua_State *L, PhysChain *chain, int idx,
                                  int material_id) {
  const char *tag = NULL;
  if (table_get_any(L, idx, "tag", NULL)) {
    if (lua_type(L, -1) == LUA_TSTRING)
      tag = lua_tostring(L, -1);
    owned_string_set_lua(L, &chain->tag, tag, "phys2d_chain metadata");
    lua_pop(L, 1);
  } else {
    owned_string_set_lua(L, &chain->tag, NULL, "phys2d_chain metadata");
  }

  const char *material_name = NULL;
  lua_getfield(L, abs_index(L, idx), "material");
  if (lua_type(L, -1) == LUA_TSTRING)
    material_name = lua_tostring(L, -1);
  owned_string_set_lua(L, &chain->material_name, material_name,
                       "phys2d_chain metadata");
  lua_pop(L, 1);
  chain->material_id = material_id;
}

static void chain_update_tombstones(PhysChain *chain) {
  if (!chain || !chain->body || !chain->body->world || B2_IS_NULL(chain->id) ||
      !b2Chain_IsValid(chain->id))
    return;
  int capacity = b2Chain_GetSegmentCount(chain->id);
  if (capacity <= 0)
    return;
  b2ShapeId *ids = (b2ShapeId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return;
  int count = b2Chain_GetSegments(chain->id, ids, capacity);
  for (int i = 0; i < count; ++i) {
    if (B2_IS_NON_NULL(ids[i]) && b2Shape_IsValid(ids[i])) {
      shape_tombstone_put(chain->body->world, ids[i], chain->body->key,
                          chain->key, chain->tag, chain->material_name,
                          b2Shape_GetMaterial(ids[i]));
    }
  }
  SDL_free(ids);
}

static b2ShapeDef make_shape_def(const PhysShapeDesc *desc, PhysShape *shape) {
  b2ShapeDef def = b2DefaultShapeDef();
  def.userData = shape;
  def.density = desc->density;
  def.material.friction = desc->friction;
  def.material.restitution = desc->restitution;
  def.material.userMaterialId = desc->material_id;
  def.isSensor = desc->sensor;
  def.enableContactEvents = desc->contact;
  def.enableHitEvents = desc->hit;
  def.enableSensorEvents = desc->sensor_events;
  def.enablePreSolveEvents = desc->pre_solve;
  def.filter.categoryBits = desc->category_bits;
  def.filter.maskBits = desc->mask_bits;
  def.filter.groupIndex = desc->group_index;
  return def;
}

static uint64_t shape_base_hash(const PhysShapeDesc *desc, PhysShapeKind kind) {
  uint64_t h = hash_init();
  h = hash_u64(h, (uint64_t)kind);
  h = hash_bool(h, desc->sensor);
  h = hash_u64(h, desc->category_bits);
  h = hash_u64(h, desc->mask_bits);
  h = hash_i64(h, desc->group_index);
  return h;
}

static void shape_apply_runtime_desc(PhysShape *shape,
                                     const PhysShapeDesc *desc) {
  b2Shape_SetDensity(shape->id, desc->density, true);
  b2Shape_SetFriction(shape->id, desc->friction);
  b2Shape_SetRestitution(shape->id, desc->restitution);
  b2Shape_SetMaterial(shape->id, desc->material_id);
  b2Shape_EnableSensorEvents(shape->id, desc->sensor_events);
  b2Shape_EnableContactEvents(shape->id, desc->contact);
  b2Shape_EnablePreSolveEvents(shape->id, desc->pre_solve);
  b2Shape_EnableHitEvents(shape->id, desc->hit);
}

static int64_t shape_effective_version(const PhysShapeDesc *desc,
                                       uint64_t fallback_hash) {
  return desc->has_version ? desc->version : (int64_t)fallback_hash;
}

static void log_shape_constructor_drift(const char *fn, PhysShape *shape,
                                        uint64_t hash) {
  if (shape->constructor_hash == hash || shape->constructor_warned)
    return;
  SDL_Log("%s('%s/%s'): constructor fields changed without version bump", fn,
          shape->body ? shape->body->key : "?", shape->key);
  shape->constructor_warned = true;
}

static void shape_mark_declared(PhysShape *shape, PhysShapeKind kind,
                                uint64_t fallback_hash,
                                const PhysShapeDesc *desc, bool recreated) {
  if (recreated)
    shape->kind = kind;
  shape->desc_hash = (uint64_t)shape_effective_version(desc, fallback_hash);
  shape->constructor_hash = fallback_hash;
  if (recreated)
    shape->constructor_warned = false;
  shape->seen_generation = shape->body->world->generation;
  shape_tombstone_update_shape(shape);
}

static int l_phys2d_box(lua_State *L) {
  if (phys_in_callback(L, "phys2d_box"))
    return 0;
  PhysBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  PhysShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  float hx = table_number(L, 3, "hx", NULL, 0.0f);
  float hy = table_number(L, 3, "hy", NULL, 0.0f);
  if (hx <= 0.0f || hy <= 0.0f)
    return luaL_error(L, "phys2d_box: hx and hy must be > 0");
  b2Vec2 center = {table_number(L, 3, "cx", NULL, 0.0f),
                   table_number(L, 3, "cy", NULL, 0.0f)};
  float angle = table_number(L, 3, "angle", NULL, 0.0f);
  uint64_t h = shape_base_hash(&desc, PHYS_SHAPE_BOX);
  h = hash_f32(h, hx);
  h = hash_f32(h, hy);
  h = hash_f32(h, center.x);
  h = hash_f32(h, center.y);
  h = hash_f32(h, angle);
  int64_t version = shape_effective_version(&desc, h);
  PhysShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys2d_box: out of memory");
  bool recreated = B2_IS_NULL(shape->id) || !b2Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS_SHAPE_BOX);
  if (recreated) {
    if (B2_IS_NON_NULL(shape->id) && b2Shape_IsValid(shape->id))
      b2DestroyShape(shape->id, true);
    b2ShapeDef def = make_shape_def(&desc, shape);
    b2Polygon polygon = (center.x != 0.0f || center.y != 0.0f || angle != 0.0f)
                            ? b2MakeOffsetBox(hx, hy, center, b2MakeRot(angle))
                            : b2MakeBox(hx, hy);
    shape->id = b2CreatePolygonShape(b->id, &def, &polygon);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys2d_box", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS_SHAPE_BOX, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static int l_phys2d_circle(lua_State *L) {
  if (phys_in_callback(L, "phys2d_circle"))
    return 0;
  PhysBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  PhysShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  float r = table_number(L, 3, "r", NULL, 0.0f);
  if (r <= 0.0f)
    return luaL_error(L, "phys2d_circle: r must be > 0");
  b2Vec2 center = {table_number(L, 3, "cx", NULL, 0.0f),
                   table_number(L, 3, "cy", NULL, 0.0f)};
  uint64_t h = shape_base_hash(&desc, PHYS_SHAPE_CIRCLE);
  h = hash_f32(h, r);
  h = hash_f32(h, center.x);
  h = hash_f32(h, center.y);
  int64_t version = shape_effective_version(&desc, h);
  PhysShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys2d_circle: out of memory");
  bool recreated = B2_IS_NULL(shape->id) || !b2Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS_SHAPE_CIRCLE);
  if (recreated) {
    if (B2_IS_NON_NULL(shape->id) && b2Shape_IsValid(shape->id))
      b2DestroyShape(shape->id, true);
    b2ShapeDef def = make_shape_def(&desc, shape);
    b2Circle circle = {.center = center, .radius = r};
    shape->id = b2CreateCircleShape(b->id, &def, &circle);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys2d_circle", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS_SHAPE_CIRCLE, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static int l_phys2d_capsule(lua_State *L) {
  if (phys_in_callback(L, "phys2d_capsule"))
    return 0;
  PhysBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  PhysShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  b2Vec2 a = {table_number(L, 3, "ax", "x1", 0.0f),
              table_number(L, 3, "ay", "y1", 0.0f)};
  b2Vec2 c = {table_number(L, 3, "bx", "x2", 0.0f),
              table_number(L, 3, "by", "y2", 0.0f)};
  float r = table_number(L, 3, "r", NULL, 0.0f);
  float dx = c.x - a.x;
  float dy = c.y - a.y;
  if (r <= 0.0f)
    return luaL_error(L, "phys2d_capsule: r must be > 0");
  if (dx * dx + dy * dy <= 1e-12f)
    return luaL_error(L, "phys2d_capsule: endpoints must be distinct");
  uint64_t h = shape_base_hash(&desc, PHYS_SHAPE_CAPSULE);
  h = hash_f32(h, a.x);
  h = hash_f32(h, a.y);
  h = hash_f32(h, c.x);
  h = hash_f32(h, c.y);
  h = hash_f32(h, r);
  int64_t version = shape_effective_version(&desc, h);
  PhysShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys2d_capsule: out of memory");
  bool recreated = B2_IS_NULL(shape->id) || !b2Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS_SHAPE_CAPSULE);
  if (recreated) {
    if (B2_IS_NON_NULL(shape->id) && b2Shape_IsValid(shape->id))
      b2DestroyShape(shape->id, true);
    b2ShapeDef def = make_shape_def(&desc, shape);
    b2Capsule capsule = {.center1 = a, .center2 = c, .radius = r};
    shape->id = b2CreateCapsuleShape(b->id, &def, &capsule);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys2d_capsule", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS_SHAPE_CAPSULE, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static int l_phys2d_segment(lua_State *L) {
  if (phys_in_callback(L, "phys2d_segment"))
    return 0;
  PhysBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  PhysShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  b2Vec2 a = {table_number(L, 3, "ax", "x1", 0.0f),
              table_number(L, 3, "ay", "y1", 0.0f)};
  b2Vec2 c = {table_number(L, 3, "bx", "x2", 0.0f),
              table_number(L, 3, "by", "y2", 0.0f)};
  float dx = c.x - a.x;
  float dy = c.y - a.y;
  if (dx * dx + dy * dy <= 1e-12f)
    return luaL_error(L, "phys2d_segment: endpoints must be distinct");
  uint64_t h = shape_base_hash(&desc, PHYS_SHAPE_SEGMENT);
  h = hash_f32(h, a.x);
  h = hash_f32(h, a.y);
  h = hash_f32(h, c.x);
  h = hash_f32(h, c.y);
  int64_t version = shape_effective_version(&desc, h);
  PhysShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys2d_segment: out of memory");
  bool recreated = B2_IS_NULL(shape->id) || !b2Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS_SHAPE_SEGMENT);
  if (recreated) {
    if (B2_IS_NON_NULL(shape->id) && b2Shape_IsValid(shape->id))
      b2DestroyShape(shape->id, true);
    b2ShapeDef def = make_shape_def(&desc, shape);
    b2Segment segment = {.point1 = a, .point2 = c};
    shape->id = b2CreateSegmentShape(b->id, &def, &segment);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys2d_segment", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS_SHAPE_SEGMENT, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static int read_point_array(lua_State *L, int idx, b2Vec2 *points,
                            int max_points, const char *fn_name) {
  if (!table_get_any(L, idx, "points", NULL))
    luaL_error(L, "%s: points table is required", fn_name);
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "%s: points must be a table", fn_name);
  }
  int pidx = lua_gettop(L);
  int count = 0;
  int raw_len = (int)lua_rawlen(L, pidx);
  if (raw_len > 0) {
    lua_rawgeti(L, pidx, 1);
    bool flat_numbers = lua_isnumber(L, -1);
    lua_pop(L, 1);
    if (flat_numbers) {
      if ((raw_len & 1) != 0)
        luaL_error(L, "%s: flat points must have x/y pairs", fn_name);
      count = raw_len / 2;
      if (count > max_points)
        luaL_error(L, "%s: at most %d points are supported", fn_name,
                   max_points);
      for (int i = 0; i < count; ++i) {
        lua_rawgeti(L, pidx, i * 2 + 1);
        points[i].x = (float)luaL_checknumber(L, -1);
        lua_pop(L, 1);
        lua_rawgeti(L, pidx, i * 2 + 2);
        points[i].y = (float)luaL_checknumber(L, -1);
        lua_pop(L, 1);
      }
    } else {
      count = raw_len;
      if (count > max_points)
        luaL_error(L, "%s: at most %d points are supported", fn_name,
                   max_points);
      for (int i = 0; i < count; ++i) {
        lua_rawgeti(L, pidx, i + 1);
        luaL_checktype(L, -1, LUA_TTABLE);
        points[i] = value_vec2(L, lua_gettop(L), b2Vec2_zero);
        lua_pop(L, 1);
      }
    }
  }
  lua_pop(L, 1);
  if (count < 3)
    luaL_error(L, "%s: at least 3 points are required", fn_name);
  return count;
}

static int l_phys2d_polygon(lua_State *L) {
  if (phys_in_callback(L, "phys2d_polygon"))
    return 0;
  PhysBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  PhysShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  b2Vec2 points[B2_MAX_POLYGON_VERTICES];
  int point_count =
      read_point_array(L, 3, points, B2_MAX_POLYGON_VERTICES, "phys2d_polygon");
  float radius = table_number(L, 3, "radius", "r", 0.0f);
  if (radius < 0.0f)
    return luaL_error(L, "phys2d_polygon: radius must be >= 0");
  b2Vec2 center = {table_number(L, 3, "cx", NULL, 0.0f),
                   table_number(L, 3, "cy", NULL, 0.0f)};
  float angle = table_number(L, 3, "angle", NULL, 0.0f);
  b2Hull hull = b2ComputeHull(points, point_count);
  if (hull.count < 3)
    return luaL_error(L, "phys2d_polygon: points must form a convex hull");
  uint64_t h = shape_base_hash(&desc, PHYS_SHAPE_POLYGON);
  h = hash_u64(h, (uint64_t)point_count);
  for (int i = 0; i < point_count; ++i) {
    h = hash_f32(h, points[i].x);
    h = hash_f32(h, points[i].y);
  }
  h = hash_f32(h, radius);
  h = hash_f32(h, center.x);
  h = hash_f32(h, center.y);
  h = hash_f32(h, angle);
  int64_t version = shape_effective_version(&desc, h);
  PhysShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys2d_polygon: out of memory");
  bool recreated = B2_IS_NULL(shape->id) || !b2Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS_SHAPE_POLYGON);
  if (recreated) {
    if (B2_IS_NON_NULL(shape->id) && b2Shape_IsValid(shape->id))
      b2DestroyShape(shape->id, true);
    b2ShapeDef def = make_shape_def(&desc, shape);
    b2Polygon polygon =
        (center.x != 0.0f || center.y != 0.0f || angle != 0.0f)
            ? (radius > 0.0f
                   ? b2MakeOffsetRoundedPolygon(&hull, center, b2MakeRot(angle),
                                                radius)
                   : b2MakeOffsetPolygon(&hull, center, b2MakeRot(angle)))
            : b2MakePolygon(&hull, radius);
    shape->id = b2CreatePolygonShape(b->id, &def, &polygon);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys2d_polygon", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS_SHAPE_POLYGON, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static b2Vec2 *read_chain_points(lua_State *L, int idx, int *out_count) {
  if (!table_get_any(L, idx, "points", NULL))
    luaL_error(L, "phys2d_chain: points table is required");
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "phys2d_chain: points must be a table");
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
    luaL_error(L, "phys2d_chain: flat points must have x/y pairs");
  int count = flat_numbers ? raw_len / 2 : raw_len;
  if (count < 4)
    luaL_error(L, "phys2d_chain: at least 4 points are required");
  b2Vec2 *points = (b2Vec2 *)SDL_malloc(sizeof(*points) * count);
  if (!points)
    luaL_error(L, "phys2d_chain: out of memory");
  if (flat_numbers) {
    for (int i = 0; i < count; ++i) {
      lua_rawgeti(L, pidx, i * 2 + 1);
      points[i].x = (float)luaL_checknumber(L, -1);
      lua_pop(L, 1);
      lua_rawgeti(L, pidx, i * 2 + 2);
      points[i].y = (float)luaL_checknumber(L, -1);
      lua_pop(L, 1);
    }
  } else {
    for (int i = 0; i < count; ++i) {
      lua_rawgeti(L, pidx, i + 1);
      luaL_checktype(L, -1, LUA_TTABLE);
      points[i] = value_vec2(L, lua_gettop(L), b2Vec2_zero);
      lua_pop(L, 1);
    }
  }
  lua_pop(L, 1);
  *out_count = count;
  return points;
}

static b2SurfaceMaterial *
read_chain_materials(lua_State *L, int idx, int point_count, float friction,
                     float restitution, int material_id, int *out_count) {
  int count = 1;
  int materials_idx = 0;
  if (table_get_any(L, idx, "materials", NULL)) {
    if (!lua_istable(L, -1)) {
      lua_pop(L, 1);
      luaL_error(L, "phys2d_chain: materials must be a table");
    }
    materials_idx = lua_gettop(L);
    count = (int)lua_rawlen(L, materials_idx);
    if (count != 1 && count != point_count) {
      lua_pop(L, 1);
      luaL_error(L, "phys2d_chain: materials length must be 1 or point count");
    }
  }

  b2SurfaceMaterial *materials =
      (b2SurfaceMaterial *)SDL_malloc(sizeof(*materials) * count);
  if (!materials) {
    if (materials_idx)
      lua_pop(L, 1);
    luaL_error(L, "phys2d_chain: out of memory");
  }

  for (int i = 0; i < count; ++i) {
    materials[i] = b2DefaultSurfaceMaterial();
    materials[i].friction = friction;
    materials[i].restitution = restitution;
    materials[i].userMaterialId = material_id;
    if (!materials_idx)
      continue;
    lua_rawgeti(L, materials_idx, i + 1);
    if (lua_istable(L, -1)) {
      int m = lua_gettop(L);
      materials[i].friction =
          table_number(L, m, "friction", NULL, materials[i].friction);
      materials[i].restitution =
          table_number(L, m, "restitution", NULL, materials[i].restitution);
      materials[i].userMaterialId = table_int(L, m, "material", "materialId",
                                              materials[i].userMaterialId);
      materials[i].userMaterialId =
          table_int(L, m, "user_material_id", "userMaterialId",
                    materials[i].userMaterialId);
    } else if (lua_isinteger(L, -1) || lua_isnumber(L, -1)) {
      materials[i].userMaterialId = (int)lua_tointeger(L, -1);
    }
    lua_pop(L, 1);
  }

  if (materials_idx)
    lua_pop(L, 1);
  *out_count = count;
  return materials;
}

static uint64_t chain_constructor_hash(const b2Vec2 *points, int point_count,
                                       bool loop, bool sensor_events,
                                       uint64_t category_bits,
                                       uint64_t mask_bits, int group_index,
                                       const b2SurfaceMaterial *materials,
                                       int material_count, bool has_materials) {
  uint64_t h = hash_init();
  h = hash_u64(h, (uint64_t)point_count);
  for (int i = 0; i < point_count; ++i) {
    h = hash_f32(h, points[i].x);
    h = hash_f32(h, points[i].y);
  }
  h = hash_bool(h, loop);
  h = hash_bool(h, sensor_events);
  h = hash_u64(h, category_bits);
  h = hash_u64(h, mask_bits);
  h = hash_i64(h, group_index);
  h = hash_bool(h, has_materials);
  if (has_materials) {
    h = hash_u64(h, (uint64_t)material_count);
    for (int i = 0; i < material_count; ++i) {
      h = hash_f32(h, materials[i].friction);
      h = hash_f32(h, materials[i].restitution);
      h = hash_i64(h, materials[i].userMaterialId);
    }
  }
  return h;
}

static void log_chain_constructor_drift(PhysChain *chain, uint64_t hash) {
  if (chain->constructor_hash == hash || chain->constructor_warned)
    return;
  SDL_Log("phys2d_chain('%s/%s'): constructor fields changed without version "
          "bump",
          chain->body ? chain->body->key : "?", chain->key);
  chain->constructor_warned = true;
}

static int l_phys2d_chain(lua_State *L) {
  if (phys_in_callback(L, "phys2d_chain"))
    return 0;
  PhysBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  int64_t version = 0;
  if (!table_has_int(L, 3, "version", NULL, &version))
    return luaL_error(L, "phys2d_chain: explicit version is required");
  int point_count = 0;
  b2Vec2 *points = read_chain_points(L, 3, &point_count);
  bool loop = table_bool(L, 3, "loop", NULL, false);
  float friction = table_number(L, 3, "friction", NULL, 0.6f);
  float restitution = table_number(L, 3, "restitution", NULL, 0.0f);
  int material_id = table_int(L, 3, "material", "materialId", 0);
  material_id =
      table_int(L, 3, "user_material_id", "userMaterialId", material_id);
  bool sensor_events = table_bool(L, 3, "sensor_events", "sensorEvents", false);
  uint64_t category_bits = 1u;
  uint64_t mask_bits = UINT64_MAX;
  int group_index = 0;
  if (table_get_any(L, 3, "filter", NULL)) {
    if (lua_istable(L, -1))
      parse_filter_table(L, lua_gettop(L), &category_bits, &mask_bits,
                         &group_index);
    lua_pop(L, 1);
  }
  bool has_materials = false;
  if (table_get_any(L, 3, "materials", NULL)) {
    has_materials = lua_istable(L, -1);
    lua_pop(L, 1);
  }
  int material_count = 0;
  b2SurfaceMaterial *materials = read_chain_materials(
      L, 3, point_count, friction, restitution, material_id, &material_count);
  uint64_t constructor_hash = chain_constructor_hash(
      points, point_count, loop, sensor_events, category_bits, mask_bits,
      group_index, materials, material_count, has_materials);

  PhysChain *chain = chain_get_or_create(b, key);
  if (!chain) {
    SDL_free(points);
    SDL_free(materials);
    return luaL_error(L, "phys2d_chain: out of memory");
  }

  bool recreated = B2_IS_NULL(chain->id) || !b2Chain_IsValid(chain->id) ||
                   chain->version != version;
  if (recreated) {
    if (B2_IS_NON_NULL(chain->id) && b2Chain_IsValid(chain->id))
      b2DestroyChain(chain->id);
    b2ChainDef def = b2DefaultChainDef();
    def.points = points;
    def.count = point_count;
    def.materials = materials;
    def.materialCount = material_count;
    def.isLoop = loop;
    def.enableSensorEvents = sensor_events;
    def.filter.categoryBits = category_bits;
    def.filter.maskBits = mask_bits;
    def.filter.groupIndex = group_index;
    chain->id = b2CreateChain(b->id, &def);
    if (B2_IS_NULL(chain->id)) {
      SDL_free(points);
      SDL_free(materials);
      return luaL_error(L, "phys2d_chain: b2CreateChain failed");
    }
    chain->version = version;
    chain->constructor_hash = constructor_hash;
    chain->constructor_warned = false;
  } else {
    log_chain_constructor_drift(chain, constructor_hash);
    chain->constructor_hash = constructor_hash;
    if (!has_materials) {
      b2Chain_SetFriction(chain->id, friction);
      b2Chain_SetRestitution(chain->id, restitution);
      b2Chain_SetMaterial(chain->id, material_id);
    }
  }
  SDL_free(points);
  SDL_free(materials);
  chain_update_metadata(L, chain, 3, material_id);
  chain_update_tombstones(chain);
  chain->seen_generation = b->world->generation;
  push_chain_ref(L, b->world->key, b->key, chain->key);
  return 1;
}

static bool body_is_live(PhysBody *b) {
  return b && B2_IS_NON_NULL(b->id) && b2Body_IsValid(b->id);
}

static bool shape_is_live(PhysShape *s) {
  return s && B2_IS_NON_NULL(s->id) && b2Shape_IsValid(s->id);
}

static bool chain_is_live(PhysChain *c) {
  return c && B2_IS_NON_NULL(c->id) && b2Chain_IsValid(c->id);
}

static void check_live_body(lua_State *L, PhysBody *b, const char *fn) {
  if (!body_is_live(b))
    luaL_error(L, "%s: body is not live", fn);
}

static void check_live_shape(lua_State *L, PhysShape *s, const char *fn) {
  if (!shape_is_live(s))
    luaL_error(L, "%s: shape is not live", fn);
}

static bool joint_is_live(PhysJoint *j) {
  return j && B2_IS_NON_NULL(j->id) && b2Joint_IsValid(j->id);
}

static void check_live_joint(lua_State *L, PhysJoint *j, const char *fn) {
  if (!joint_is_live(j))
    luaL_error(L, "%s: joint is not live", fn);
}

static const char *joint_kind_name(PhysJointKind kind) {
  switch (kind) {
  case PHYS_JOINT_DISTANCE:
    return "distance";
  case PHYS_JOINT_FILTER:
    return "filter";
  case PHYS_JOINT_MOTOR:
    return "motor";
  case PHYS_JOINT_MOUSE:
    return "mouse";
  case PHYS_JOINT_PRISMATIC:
    return "prismatic";
  case PHYS_JOINT_REVOLUTE:
    return "revolute";
  case PHYS_JOINT_WELD:
    return "weld";
  case PHYS_JOINT_WHEEL:
    return "wheel";
  default:
    return "unknown";
  }
}

static PhysJointKind parse_joint_kind(lua_State *L, int idx) {
  const char *type = "revolute";
  if (table_get_any(L, idx, "type", "kind")) {
    if (lua_isstring(L, -1))
      type = lua_tostring(L, -1);
    lua_pop(L, 1);
  }
  if (strcmp(type, "distance") == 0)
    return PHYS_JOINT_DISTANCE;
  if (strcmp(type, "filter") == 0)
    return PHYS_JOINT_FILTER;
  if (strcmp(type, "motor") == 0)
    return PHYS_JOINT_MOTOR;
  if (strcmp(type, "mouse") == 0)
    return PHYS_JOINT_MOUSE;
  if (strcmp(type, "prismatic") == 0)
    return PHYS_JOINT_PRISMATIC;
  if (strcmp(type, "revolute") == 0 || strcmp(type, "hinge") == 0)
    return PHYS_JOINT_REVOLUTE;
  if (strcmp(type, "weld") == 0)
    return PHYS_JOINT_WELD;
  if (strcmp(type, "wheel") == 0)
    return PHYS_JOINT_WHEEL;
  luaL_error(L, "phys2d_joint: unknown joint type '%s'", type);
  return PHYS_JOINT_REVOLUTE;
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

static b2Vec2 nested_vec2(lua_State *L, int idx, const char *table_name,
                          const char *a, const char *b, b2Vec2 def) {
  b2Vec2 out = def;
  if (table_get_any(L, idx, table_name, NULL)) {
    if (lua_istable(L, -1))
      out = table_vec2(L, lua_gettop(L), a, b, out);
    lua_pop(L, 1);
  }
  return out;
}

static PhysBody *joint_body_from_value(lua_State *L, PhysWorld *w, int idx,
                                       const char *field_name) {
  PhysBody *b = NULL;
  if (is_ref(L, idx, "phys2d_body")) {
    b = check_body(L, idx);
  } else if (lua_isstring(L, idx)) {
    b = body_get(w, lua_tostring(L, idx));
  }
  if (!b)
    luaL_error(L, "phys2d_joint: missing body field '%s'", field_name);
  if (b->world != w)
    luaL_error(L, "phys2d_joint: body '%s' belongs to another world", b->key);
  if (!body_is_live(b) || b->seen_generation != w->generation)
    luaL_error(L, "phys2d_joint: declare live body '%s' before joint", b->key);
  return b;
}

static PhysBody *joint_body_field(lua_State *L, PhysWorld *w, int idx,
                                  const char *a, const char *b, const char *c) {
  if (!table_get_any(L, idx, a, b)) {
    if (!c || !table_get_any(L, idx, c, NULL))
      luaL_error(L, "phys2d_joint: missing body field '%s'", a);
  }
  PhysBody *body = joint_body_from_value(L, w, lua_gettop(L), a);
  lua_pop(L, 1);
  return body;
}

static b2Vec2 joint_vec2(lua_State *L, int idx, const char *a, const char *b,
                         const char *c, const char *d, b2Vec2 def) {
  b2Vec2 out = table_vec2(L, idx, a, b, def);
  if (c)
    out = table_vec2(L, idx, c, d, out);
  return out;
}

static void parse_joint_desc(lua_State *L, PhysWorld *w, int idx,
                             PhysJointDesc *desc) {
  luaL_checktype(L, idx, LUA_TTABLE);
  memset(desc, 0, sizeof(*desc));
  desc->version = 0;
  desc->has_version = false;
  desc->kind = parse_joint_kind(L, idx);
  desc->local_axis_a = (b2Vec2){1.0f, 0.0f};
  desc->length = 1.0f;
  desc->max_length = 1.0f;
  desc->upper = 1.0f;
  desc->hertz = 0.0f;
  desc->damping_ratio = 0.0f;
  desc->linear_hertz = 0.0f;
  desc->angular_hertz = 0.0f;
  desc->linear_damping_ratio = 0.0f;
  desc->angular_damping_ratio = 0.0f;
  desc->max_force = 1.0f;
  desc->max_torque = 1.0f;
  desc->correction_factor = 0.3f;
  desc->draw_size = 0.25f;

  int64_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    desc->version = v;
    desc->has_version = true;
  }
  desc->body_a = joint_body_field(L, w, idx, "a", "body_a", "bodyA");
  desc->body_b = joint_body_field(L, w, idx, "b", "body_b", "bodyB");
  desc->local_anchor_a =
      joint_vec2(L, idx, "anchor_a", "anchorA", "local_anchor_a",
                 "localAnchorA", desc->local_anchor_a);
  desc->local_anchor_b =
      joint_vec2(L, idx, "anchor_b", "anchorB", "local_anchor_b",
                 "localAnchorB", desc->local_anchor_b);
  desc->local_axis_a = joint_vec2(L, idx, "axis", NULL, "local_axis_a",
                                  "localAxisA", desc->local_axis_a);
  float axis_len2 = desc->local_axis_a.x * desc->local_axis_a.x +
                    desc->local_axis_a.y * desc->local_axis_a.y;
  if (axis_len2 <= 1e-12f)
    luaL_error(L, "phys2d_joint: local axis must be non-zero");
  desc->local_axis_a = b2Normalize(desc->local_axis_a);
  desc->linear_offset = joint_vec2(L, idx, "linear_offset", "linearOffset",
                                   NULL, NULL, desc->linear_offset);
  desc->target = joint_vec2(L, idx, "target", NULL, NULL, NULL, desc->target);

  desc->reference_angle = table_number(L, idx, "reference_angle",
                                       "referenceAngle", desc->reference_angle);
  desc->length = table_number(L, idx, "length", NULL, desc->length);
  desc->min_length =
      table_number(L, idx, "min_length", "minLength", desc->min_length);
  desc->max_length =
      table_number(L, idx, "max_length", "maxLength", desc->max_length);
  desc->lower = table_number(L, idx, "lower", NULL, desc->lower);
  desc->upper = table_number(L, idx, "upper", NULL, desc->upper);
  desc->target_angle =
      table_number(L, idx, "target_angle", "targetAngle", desc->target_angle);
  desc->target_translation =
      table_number(L, idx, "target_translation", "targetTranslation",
                   desc->target_translation);
  desc->angular_offset = table_number(L, idx, "angular_offset", "angularOffset",
                                      desc->angular_offset);
  desc->hertz = table_number(L, idx, "hertz", NULL, desc->hertz);
  desc->damping_ratio = table_number(L, idx, "damping_ratio", "dampingRatio",
                                     desc->damping_ratio);
  desc->linear_hertz =
      table_number(L, idx, "linear_hertz", "linearHertz", desc->linear_hertz);
  desc->angular_hertz = table_number(L, idx, "angular_hertz", "angularHertz",
                                     desc->angular_hertz);
  desc->linear_damping_ratio =
      table_number(L, idx, "linear_damping_ratio", "linearDampingRatio",
                   desc->linear_damping_ratio);
  desc->angular_damping_ratio =
      table_number(L, idx, "angular_damping_ratio", "angularDampingRatio",
                   desc->angular_damping_ratio);
  desc->max_force =
      table_number(L, idx, "max_force", "maxForce", desc->max_force);
  desc->max_torque =
      table_number(L, idx, "max_torque", "maxTorque", desc->max_torque);
  desc->motor_speed =
      table_number(L, idx, "motor_speed", "motorSpeed", desc->motor_speed);
  desc->correction_factor = table_number(
      L, idx, "correction_factor", "correctionFactor", desc->correction_factor);
  desc->draw_size =
      table_number(L, idx, "draw_size", "drawSize", desc->draw_size);
  desc->collide_connected = table_bool(
      L, idx, "collide_connected", "collideConnected", desc->collide_connected);
  desc->enable_spring =
      table_bool(L, idx, "enable_spring", "enableSpring", desc->enable_spring);
  desc->enable_limit =
      table_bool(L, idx, "enable_limit", "enableLimit", desc->enable_limit);
  desc->enable_motor =
      table_bool(L, idx, "enable_motor", "enableMotor", desc->enable_motor);

  desc->enable_spring =
      nested_bool(L, idx, "spring", "enabled", NULL, desc->enable_spring);
  desc->hertz = nested_number(L, idx, "spring", "hertz", NULL, desc->hertz);
  desc->damping_ratio = nested_number(L, idx, "spring", "damping_ratio",
                                      "dampingRatio", desc->damping_ratio);
  desc->target_angle = nested_number(L, idx, "spring", "target_angle",
                                     "targetAngle", desc->target_angle);
  desc->target_translation =
      nested_number(L, idx, "spring", "target_translation", "targetTranslation",
                    desc->target_translation);

  desc->enable_limit =
      nested_bool(L, idx, "limit", "enabled", NULL, desc->enable_limit);
  desc->lower = nested_number(L, idx, "limit", "lower", NULL, desc->lower);
  desc->upper = nested_number(L, idx, "limit", "upper", NULL, desc->upper);
  desc->min_length =
      nested_number(L, idx, "limit", "min", "min_length", desc->min_length);
  desc->max_length =
      nested_number(L, idx, "limit", "max", "max_length", desc->max_length);

  desc->enable_motor =
      nested_bool(L, idx, "motor", "enabled", NULL, desc->enable_motor);
  desc->motor_speed =
      nested_number(L, idx, "motor", "speed", NULL, desc->motor_speed);
  desc->max_force =
      nested_number(L, idx, "motor", "max_force", "maxForce", desc->max_force);
  desc->max_torque = nested_number(L, idx, "motor", "max_torque", "maxTorque",
                                   desc->max_torque);
  desc->linear_offset = nested_vec2(L, idx, "motor", "linear_offset",
                                    "linearOffset", desc->linear_offset);
  desc->angular_offset = nested_number(L, idx, "motor", "angular_offset",
                                       "angularOffset", desc->angular_offset);
}

static uint64_t joint_constructor_hash(const PhysJointDesc *desc) {
  uint64_t h = hash_init();
  h = hash_u64(h, (uint64_t)desc->kind);
  h = hash_cstr(h, desc->body_a ? desc->body_a->key : "");
  h = hash_cstr(h, desc->body_b ? desc->body_b->key : "");
  h = hash_f32(h, desc->local_anchor_a.x);
  h = hash_f32(h, desc->local_anchor_a.y);
  h = hash_f32(h, desc->local_anchor_b.x);
  h = hash_f32(h, desc->local_anchor_b.y);
  h = hash_f32(h, desc->local_axis_a.x);
  h = hash_f32(h, desc->local_axis_a.y);
  h = hash_f32(h, desc->reference_angle);
  h = hash_bool(h, desc->collide_connected);
  return h;
}

static int64_t joint_effective_version(const PhysJointDesc *desc,
                                       uint64_t fallback_hash) {
  if (desc->has_version)
    return desc->version;
  return (int64_t)fallback_hash;
}

static void log_joint_constructor_drift(PhysJoint *j, uint64_t hash) {
  if (j->constructor_hash == hash || j->constructor_warned)
    return;
  SDL_Log("phys2d_joint('%s'): constructor fields changed without version bump",
          j->key);
  j->constructor_warned = true;
}

static void joint_mark_declared(PhysJoint *j, const PhysJointDesc *desc,
                                uint64_t constructor_hash, int64_t version,
                                bool recreated) {
  if (recreated) {
    j->kind = desc->kind;
    j->body_a = desc->body_a;
    j->body_b = desc->body_b;
    j->constructor_warned = false;
  }
  j->version = version;
  j->constructor_hash = constructor_hash;
}

static void joint_apply_runtime(PhysJoint *j, const PhysJointDesc *desc) {
  switch (desc->kind) {
  case PHYS_JOINT_DISTANCE:
    b2DistanceJoint_SetLength(j->id, desc->length);
    b2DistanceJoint_EnableSpring(j->id, desc->enable_spring);
    b2DistanceJoint_SetSpringHertz(j->id, desc->hertz);
    b2DistanceJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b2DistanceJoint_EnableLimit(j->id, desc->enable_limit);
    b2DistanceJoint_SetLengthRange(j->id, desc->min_length, desc->max_length);
    b2DistanceJoint_EnableMotor(j->id, desc->enable_motor);
    b2DistanceJoint_SetMotorSpeed(j->id, desc->motor_speed);
    b2DistanceJoint_SetMaxMotorForce(j->id, desc->max_force);
    break;
  case PHYS_JOINT_MOTOR:
    b2MotorJoint_SetLinearOffset(j->id, desc->linear_offset);
    b2MotorJoint_SetAngularOffset(j->id, desc->angular_offset);
    b2MotorJoint_SetMaxForce(j->id, desc->max_force);
    b2MotorJoint_SetMaxTorque(j->id, desc->max_torque);
    b2MotorJoint_SetCorrectionFactor(j->id, desc->correction_factor);
    break;
  case PHYS_JOINT_MOUSE:
    b2MouseJoint_SetTarget(j->id, desc->target);
    b2MouseJoint_SetSpringHertz(j->id, desc->hertz);
    b2MouseJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b2MouseJoint_SetMaxForce(j->id, desc->max_force);
    break;
  case PHYS_JOINT_PRISMATIC:
    b2PrismaticJoint_EnableSpring(j->id, desc->enable_spring);
    b2PrismaticJoint_SetSpringHertz(j->id, desc->hertz);
    b2PrismaticJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b2PrismaticJoint_SetTargetTranslation(j->id, desc->target_translation);
    b2PrismaticJoint_EnableLimit(j->id, desc->enable_limit);
    b2PrismaticJoint_SetLimits(j->id, desc->lower, desc->upper);
    b2PrismaticJoint_EnableMotor(j->id, desc->enable_motor);
    b2PrismaticJoint_SetMotorSpeed(j->id, desc->motor_speed);
    b2PrismaticJoint_SetMaxMotorForce(j->id, desc->max_force);
    break;
  case PHYS_JOINT_REVOLUTE:
    b2RevoluteJoint_EnableSpring(j->id, desc->enable_spring);
    b2RevoluteJoint_SetSpringHertz(j->id, desc->hertz);
    b2RevoluteJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b2RevoluteJoint_SetTargetAngle(j->id, desc->target_angle);
    b2RevoluteJoint_EnableLimit(j->id, desc->enable_limit);
    b2RevoluteJoint_SetLimits(j->id, desc->lower, desc->upper);
    b2RevoluteJoint_EnableMotor(j->id, desc->enable_motor);
    b2RevoluteJoint_SetMotorSpeed(j->id, desc->motor_speed);
    b2RevoluteJoint_SetMaxMotorTorque(j->id, desc->max_torque);
    break;
  case PHYS_JOINT_WELD:
    b2WeldJoint_SetLinearHertz(j->id, desc->linear_hertz);
    b2WeldJoint_SetLinearDampingRatio(j->id, desc->linear_damping_ratio);
    b2WeldJoint_SetAngularHertz(j->id, desc->angular_hertz);
    b2WeldJoint_SetAngularDampingRatio(j->id, desc->angular_damping_ratio);
    break;
  case PHYS_JOINT_WHEEL:
    b2WheelJoint_EnableSpring(j->id, desc->enable_spring);
    b2WheelJoint_SetSpringHertz(j->id, desc->hertz);
    b2WheelJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b2WheelJoint_EnableLimit(j->id, desc->enable_limit);
    b2WheelJoint_SetLimits(j->id, desc->lower, desc->upper);
    b2WheelJoint_EnableMotor(j->id, desc->enable_motor);
    b2WheelJoint_SetMotorSpeed(j->id, desc->motor_speed);
    b2WheelJoint_SetMaxMotorTorque(j->id, desc->max_torque);
    break;
  case PHYS_JOINT_FILTER:
  default:
    break;
  }
}

static void joint_create(lua_State *L, PhysWorld *w, PhysJoint *j,
                         const PhysJointDesc *desc, uint64_t constructor_hash,
                         int64_t version) {
  switch (desc->kind) {
  case PHYS_JOINT_DISTANCE: {
    b2DistanceJointDef def = b2DefaultDistanceJointDef();
    def.bodyIdA = desc->body_a->id;
    def.bodyIdB = desc->body_b->id;
    def.localAnchorA = desc->local_anchor_a;
    def.localAnchorB = desc->local_anchor_b;
    def.length = desc->length;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateDistanceJoint(w->id, &def);
    break;
  }
  case PHYS_JOINT_FILTER: {
    b2FilterJointDef def = b2DefaultFilterJointDef();
    def.bodyIdA = desc->body_a->id;
    def.bodyIdB = desc->body_b->id;
    def.userData = j;
    j->id = b2CreateFilterJoint(w->id, &def);
    break;
  }
  case PHYS_JOINT_MOTOR: {
    b2MotorJointDef def = b2DefaultMotorJointDef();
    def.bodyIdA = desc->body_a->id;
    def.bodyIdB = desc->body_b->id;
    def.linearOffset = desc->linear_offset;
    def.angularOffset = desc->angular_offset;
    def.maxForce = desc->max_force;
    def.maxTorque = desc->max_torque;
    def.correctionFactor = desc->correction_factor;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateMotorJoint(w->id, &def);
    break;
  }
  case PHYS_JOINT_MOUSE: {
    b2MouseJointDef def = b2DefaultMouseJointDef();
    def.bodyIdA = desc->body_a->id;
    def.bodyIdB = desc->body_b->id;
    def.target = desc->target;
    def.hertz = desc->hertz;
    def.dampingRatio = desc->damping_ratio;
    def.maxForce = desc->max_force;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateMouseJoint(w->id, &def);
    break;
  }
  case PHYS_JOINT_PRISMATIC: {
    b2PrismaticJointDef def = b2DefaultPrismaticJointDef();
    def.bodyIdA = desc->body_a->id;
    def.bodyIdB = desc->body_b->id;
    def.localAnchorA = desc->local_anchor_a;
    def.localAnchorB = desc->local_anchor_b;
    def.localAxisA = desc->local_axis_a;
    def.referenceAngle = desc->reference_angle;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreatePrismaticJoint(w->id, &def);
    break;
  }
  case PHYS_JOINT_REVOLUTE: {
    b2RevoluteJointDef def = b2DefaultRevoluteJointDef();
    def.bodyIdA = desc->body_a->id;
    def.bodyIdB = desc->body_b->id;
    def.localAnchorA = desc->local_anchor_a;
    def.localAnchorB = desc->local_anchor_b;
    def.referenceAngle = desc->reference_angle;
    def.drawSize = desc->draw_size;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateRevoluteJoint(w->id, &def);
    break;
  }
  case PHYS_JOINT_WELD: {
    b2WeldJointDef def = b2DefaultWeldJointDef();
    def.bodyIdA = desc->body_a->id;
    def.bodyIdB = desc->body_b->id;
    def.localAnchorA = desc->local_anchor_a;
    def.localAnchorB = desc->local_anchor_b;
    def.referenceAngle = desc->reference_angle;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateWeldJoint(w->id, &def);
    break;
  }
  case PHYS_JOINT_WHEEL: {
    b2WheelJointDef def = b2DefaultWheelJointDef();
    def.bodyIdA = desc->body_a->id;
    def.bodyIdB = desc->body_b->id;
    def.localAnchorA = desc->local_anchor_a;
    def.localAnchorB = desc->local_anchor_b;
    def.localAxisA = desc->local_axis_a;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateWheelJoint(w->id, &def);
    break;
  }
  default:
    break;
  }
  if (B2_IS_NULL(j->id) || !b2Joint_IsValid(j->id))
    luaL_error(L, "phys2d_joint: b2Create%sJoint failed",
               joint_kind_name(desc->kind));
  b2Joint_SetUserData(j->id, j);
  joint_mark_declared(j, desc, constructor_hash, version, true);
  joint_apply_runtime(j, desc);
}

static int l_phys2d_joint(lua_State *L) {
  if (phys_in_callback(L, "phys2d_joint"))
    return 0;
  PhysWorld *w = check_world(L, 1);
  const char *key = luaL_checkstring(L, 2);
  PhysJointDesc desc;
  parse_joint_desc(L, w, 3, &desc);
  if (!w->begun)
    return luaL_error(L, "phys2d_joint: call phys2d_begin(world) first");
  PhysJoint *j = joint_get_or_create(w, key);
  if (!j)
    return luaL_error(L, "phys2d_joint: out of memory");
  uint64_t constructor_hash = joint_constructor_hash(&desc);
  int64_t version = joint_effective_version(&desc, constructor_hash);
  bool endpoints_changed = j->body_a != desc.body_a || j->body_b != desc.body_b;
  bool kind_changed = j->kind != desc.kind;
  bool recreated = !joint_is_live(j) || j->version != version ||
                   (!desc.has_version && (kind_changed || endpoints_changed));
  if (recreated) {
    if (joint_is_live(j))
      b2DestroyJoint(j->id);
    j->id = b2_nullJointId;
    joint_create(L, w, j, &desc, constructor_hash, version);
  } else {
    if (desc.has_version)
      log_joint_constructor_drift(j, constructor_hash);
    joint_mark_declared(j, &desc, constructor_hash, version, false);
    if (!kind_changed)
      joint_apply_runtime(j, &desc);
  }
  j->seen_generation = w->generation;
  push_joint_ref(L, w->key, j->key);
  return 1;
}

static int l_phys2d_add_force(lua_State *L) {
  if (phys_in_callback(L, "phys2d_add_force"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_add_force");
  luaL_checktype(L, 2, LUA_TTABLE);
  PhysCommand *cmd = command_queue_push(L, b->world, b, PHYS_COMMAND_ADD_FORCE,
                                        "phys2d_add_force");
  cmd->vector = value_vec2(L, 2, b2Vec2_zero);
  if (lua_istable(L, 3)) {
    b2Vec2 point = b2Body_GetWorldCenterOfMass(b->id);
    bool has_point = false;
    if (table_get_any(L, 3, "point", NULL)) {
      if (lua_istable(L, -1)) {
        point = value_vec2(L, lua_gettop(L), point);
        has_point = true;
      }
      lua_pop(L, 1);
    }
    float out = 0.0f;
    if (table_number_optional(L, 3, "px", NULL, &out)) {
      point.x = out;
      has_point = true;
    }
    if (table_number_optional(L, 3, "py", NULL, &out)) {
      point.y = out;
      has_point = true;
    }
    cmd->point = point;
    cmd->has_point = has_point;
  }
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys2d_add_force_center(lua_State *L) {
  if (phys_in_callback(L, "phys2d_add_force_center"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_add_force_center");
  luaL_checktype(L, 2, LUA_TTABLE);
  PhysCommand *cmd = command_queue_push(
      L, b->world, b, PHYS_COMMAND_ADD_FORCE_CENTER, "phys2d_add_force_center");
  cmd->vector = value_vec2(L, 2, b2Vec2_zero);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys2d_add_impulse(lua_State *L) {
  if (phys_in_callback(L, "phys2d_add_impulse"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_add_impulse");
  luaL_checktype(L, 2, LUA_TTABLE);
  PhysCommand *cmd = command_queue_push(
      L, b->world, b, PHYS_COMMAND_ADD_IMPULSE, "phys2d_add_impulse");
  cmd->vector = value_vec2(L, 2, b2Vec2_zero);
  if (lua_istable(L, 3)) {
    b2Vec2 point = b2Body_GetWorldCenterOfMass(b->id);
    bool has_point = false;
    if (table_get_any(L, 3, "point", NULL)) {
      if (lua_istable(L, -1)) {
        point = value_vec2(L, lua_gettop(L), point);
        has_point = true;
      }
      lua_pop(L, 1);
    }
    float out = 0.0f;
    if (table_number_optional(L, 3, "px", NULL, &out)) {
      point.x = out;
      has_point = true;
    }
    if (table_number_optional(L, 3, "py", NULL, &out)) {
      point.y = out;
      has_point = true;
    }
    cmd->point = point;
    cmd->has_point = has_point;
  }
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys2d_add_impulse_center(lua_State *L) {
  if (phys_in_callback(L, "phys2d_add_impulse_center"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_add_impulse_center");
  luaL_checktype(L, 2, LUA_TTABLE);
  PhysCommand *cmd =
      command_queue_push(L, b->world, b, PHYS_COMMAND_ADD_IMPULSE_CENTER,
                         "phys2d_add_impulse_center");
  cmd->vector = value_vec2(L, 2, b2Vec2_zero);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys2d_add_torque(lua_State *L) {
  if (phys_in_callback(L, "phys2d_add_torque"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_add_torque");
  PhysCommand *cmd = command_queue_push(L, b->world, b, PHYS_COMMAND_ADD_TORQUE,
                                        "phys2d_add_torque");
  cmd->scalar = (float)luaL_checknumber(L, 2);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys2d_add_angular_impulse(lua_State *L) {
  if (phys_in_callback(L, "phys2d_add_angular_impulse"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_add_angular_impulse");
  PhysCommand *cmd =
      command_queue_push(L, b->world, b, PHYS_COMMAND_ADD_ANGULAR_IMPULSE,
                         "phys2d_add_angular_impulse");
  cmd->scalar = (float)luaL_checknumber(L, 2);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys2d_set_velocity(lua_State *L) {
  if (phys_in_callback(L, "phys2d_set_velocity"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_set_velocity");
  luaL_checktype(L, 2, LUA_TTABLE);
  PhysCommand *cmd = command_queue_push(
      L, b->world, b, PHYS_COMMAND_SET_VELOCITY, "phys2d_set_velocity");
  cmd->vector =
      value_vec2_optional(L, 2, b2Vec2_zero, &cmd->has_x, &cmd->has_y);
  float out = 0.0f;
  if (table_number_optional(L, 2, "vx", NULL, &out)) {
    cmd->vector.x = out;
    cmd->has_x = true;
  }
  if (table_number_optional(L, 2, "vy", NULL, &out)) {
    cmd->vector.y = out;
    cmd->has_y = true;
  }
  if (table_number_optional(L, 2, "w", NULL, &out)) {
    cmd->scalar = out;
    cmd->has_w = true;
  }
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys2d_teleport(lua_State *L) {
  if (phys_in_callback(L, "phys2d_teleport"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_teleport");
  luaL_checktype(L, 2, LUA_TTABLE);
  PhysCommand *cmd = command_queue_push(L, b->world, b, PHYS_COMMAND_TELEPORT,
                                        "phys2d_teleport");
  float out = 0.0f;
  if (table_number_optional(L, 2, "x", NULL, &out)) {
    cmd->transform.p.x = out;
    cmd->has_x = true;
  }
  if (table_number_optional(L, 2, "y", NULL, &out)) {
    cmd->transform.p.y = out;
    cmd->has_y = true;
  }
  if (table_number_optional(L, 2, "angle", NULL, &out)) {
    cmd->scalar = out;
    cmd->has_angle = true;
  }
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys2d_set_target(lua_State *L) {
  if (phys_in_callback(L, "phys2d_set_target"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_set_target");
  luaL_checktype(L, 2, LUA_TTABLE);
  PhysCommand *cmd = command_queue_push(L, b->world, b, PHYS_COMMAND_SET_TARGET,
                                        "phys2d_set_target");
  float out = 0.0f;
  if (table_number_optional(L, 2, "x", NULL, &out)) {
    cmd->transform.p.x = out;
    cmd->has_x = true;
  }
  if (table_number_optional(L, 2, "y", NULL, &out)) {
    cmd->transform.p.y = out;
    cmd->has_y = true;
  }
  if (table_number_optional(L, 2, "angle", NULL, &out)) {
    cmd->scalar = out;
    cmd->has_angle = true;
  }
  cmd->time_step = b->world ? b->world->fixed_dt : 1.0f / 60.0f;
  if (lua_istable(L, 3)) {
    cmd->time_step = table_number(L, 3, "dt", NULL, cmd->time_step);
    cmd->time_step =
        table_number(L, 3, "time_step", "timeStep", cmd->time_step);
  }
  if (cmd->time_step <= 0.0f)
    cmd->time_step = b->world ? b->world->fixed_dt : 1.0f / 60.0f;
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys2d_set_mass_data(lua_State *L) {
  if (phys_in_callback(L, "phys2d_set_mass_data"))
    return 0;
  PhysBody *b = check_body(L, 1);
  check_live_body(L, b, "phys2d_set_mass_data");
  luaL_checktype(L, 2, LUA_TTABLE);
  b2MassData mass_data = b2Body_GetMassData(b->id);
  mass_data.mass = table_number(L, 2, "mass", NULL, mass_data.mass);
  mass_data.rotationalInertia =
      table_number(L, 2, "rotational_inertia", "rotationalInertia",
                   mass_data.rotationalInertia);
  mass_data.rotationalInertia =
      table_number(L, 2, "inertia", NULL, mass_data.rotationalInertia);
  mass_data.center =
      table_vec2(L, 2, "local_center", "localCenter", mass_data.center);
  mass_data.center = table_vec2(L, 2, "center", NULL, mass_data.center);
  mass_data.center.x = table_number(L, 2, "cx", NULL, mass_data.center.x);
  mass_data.center.y = table_number(L, 2, "cy", NULL, mass_data.center.y);
  if (mass_data.mass < 0.0f)
    return luaL_error(L, "phys2d_set_mass_data: mass must be >= 0");
  if (mass_data.rotationalInertia < 0.0f)
    return luaL_error(L, "phys2d_set_mass_data: inertia must be >= 0");
  PhysCommand *cmd = command_queue_push(
      L, b->world, b, PHYS_COMMAND_SET_MASS_DATA, "phys2d_set_mass_data");
  cmd->mass_data = mass_data;
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static bool apply_body_command(PhysWorld *w, const PhysCommand *cmd) {
  PhysBody *b = body_get(w, cmd->body_key);
  if (!body_is_live(b))
    return false;
  if (cmd->body_id_key != 0 && b2StoreBodyId(b->id) != cmd->body_id_key) {
    SDL_Log("phys2d_step: dropped stale command for recreated body '%s'",
            cmd->body_key ? cmd->body_key : "?");
    return false;
  }

  switch (cmd->kind) {
  case PHYS_COMMAND_ADD_FORCE: {
    b2Vec2 point =
        cmd->has_point ? cmd->point : b2Body_GetWorldCenterOfMass(b->id);
    b2Body_ApplyForce(b->id, cmd->vector, point, cmd->wake);
    return true;
  }
  case PHYS_COMMAND_ADD_FORCE_CENTER:
    b2Body_ApplyForceToCenter(b->id, cmd->vector, cmd->wake);
    return true;
  case PHYS_COMMAND_ADD_IMPULSE: {
    b2Vec2 point =
        cmd->has_point ? cmd->point : b2Body_GetWorldCenterOfMass(b->id);
    b2Body_ApplyLinearImpulse(b->id, cmd->vector, point, cmd->wake);
    return true;
  }
  case PHYS_COMMAND_ADD_IMPULSE_CENTER:
    b2Body_ApplyLinearImpulseToCenter(b->id, cmd->vector, cmd->wake);
    return true;
  case PHYS_COMMAND_ADD_TORQUE:
    b2Body_ApplyTorque(b->id, cmd->scalar, cmd->wake);
    return true;
  case PHYS_COMMAND_ADD_ANGULAR_IMPULSE:
    b2Body_ApplyAngularImpulse(b->id, cmd->scalar, cmd->wake);
    return true;
  case PHYS_COMMAND_SET_VELOCITY: {
    b2Vec2 linear = b2Body_GetLinearVelocity(b->id);
    if (cmd->has_x)
      linear.x = cmd->vector.x;
    if (cmd->has_y)
      linear.y = cmd->vector.y;
    b2Body_SetLinearVelocity(b->id, linear);
    if (cmd->has_w)
      b2Body_SetAngularVelocity(b->id, cmd->scalar);
    if (cmd->wake)
      b2Body_SetAwake(b->id, true);
    return true;
  }
  case PHYS_COMMAND_TELEPORT: {
    b2Transform xf = b2Body_GetTransform(b->id);
    if (cmd->has_x)
      xf.p.x = cmd->transform.p.x;
    if (cmd->has_y)
      xf.p.y = cmd->transform.p.y;
    if (cmd->has_angle)
      xf.q = b2MakeRot(cmd->scalar);
    b2Body_SetTransform(b->id, xf.p, xf.q);
    if (cmd->wake)
      b2Body_SetAwake(b->id, true);
    return true;
  }
  case PHYS_COMMAND_SET_TARGET: {
    b2Transform target = b2Body_GetTransform(b->id);
    if (cmd->has_x)
      target.p.x = cmd->transform.p.x;
    if (cmd->has_y)
      target.p.y = cmd->transform.p.y;
    if (cmd->has_angle)
      target.q = b2MakeRot(cmd->scalar);
    b2Body_SetTargetTransform(b->id, target, cmd->time_step);
    if (cmd->wake)
      b2Body_SetAwake(b->id, true);
    return true;
  }
  case PHYS_COMMAND_SET_MASS_DATA:
    b2Body_SetMassData(b->id, cmd->mass_data);
    if (cmd->wake)
      b2Body_SetAwake(b->id, true);
    return true;
  default:
    return false;
  }
  return false;
}

static int apply_body_commands(PhysWorld *w) {
  int count = 0;
  for (int i = 0; i < w->commands.count; ++i)
    count += apply_body_command(w, &w->commands.items[i]) ? 1 : 0;
  command_queue_clear(&w->commands);
  return count;
}

static void prune_shapes(PhysBody *b) {
  uint64_t gen = b->world->generation;
  for (int i = 0; i < PHYS2D_SHAPE_BUCKETS; ++i) {
    PhysShape **prev = &b->shapes[i];
    PhysShape *s = b->shapes[i];
    while (s) {
      PhysShape *next = s->next;
      if (s->seen_generation != gen) {
        *prev = next;
        shape_free(s, true);
      } else {
        prev = &s->next;
      }
      s = next;
    }
  }
}

static void prune_chains(PhysBody *b) {
  uint64_t gen = b->world->generation;
  for (int i = 0; i < PHYS2D_CHAIN_BUCKETS; ++i) {
    PhysChain **prev = &b->chains[i];
    PhysChain *c = b->chains[i];
    while (c) {
      PhysChain *next = c->next;
      if (c->seen_generation != gen) {
        *prev = next;
        chain_free(c, true);
      } else {
        prev = &c->next;
      }
      c = next;
    }
  }
}

static void prune_joints(PhysWorld *w) {
  uint64_t gen = w->generation;
  for (int i = 0; i < PHYS2D_JOINT_BUCKETS; ++i) {
    PhysJoint **prev = &w->joints[i];
    PhysJoint *j = w->joints[i];
    while (j) {
      PhysJoint *next = j->next;
      if (j->seen_generation != gen) {
        *prev = next;
        joint_free(j, true);
      } else {
        prev = &j->next;
      }
      j = next;
    }
  }
}

static void prune_world(PhysWorld *w) {
  uint64_t gen = w->generation;
  prune_joints(w);
  for (int i = 0; i < PHYS2D_BODY_BUCKETS; ++i) {
    PhysBody **prev = &w->bodies[i];
    PhysBody *b = w->bodies[i];
    while (b) {
      PhysBody *next = b->next;
      if (b->seen_generation != gen) {
        *prev = next;
        body_free(b, true);
      } else {
        prune_shapes(b);
        prune_chains(b);
        prev = &b->next;
      }
      b = next;
    }
  }
}

static void fill_shape_snapshot(PhysWorld *w, PhysContactSnapshot *out,
                                bool is_a, b2ShapeId shape_id) {
  bool valid = B2_IS_NON_NULL(shape_id) && b2Shape_IsValid(shape_id);
  PhysShape *shape = valid ? (PhysShape *)b2Shape_GetUserData(shape_id) : NULL;
  PhysChain *chain = NULL;
  if (valid && !shape)
    chain = chain_find_by_id(b2Shape_GetParentChain(shape_id));
  const char *body_key = "";
  const char *shape_key = "";
  const char *tag = NULL;
  const char *material_name = NULL;
  int material_id = valid ? b2Shape_GetMaterial(shape_id) : 0;
  if (shape && shape->body) {
    body_key = shape->body->key;
    shape_key = shape->key;
    tag = shape->tag;
    material_name = shape->material_name;
  } else if (chain && chain->body) {
    body_key = chain->body->key;
    shape_key = chain->key;
    tag = chain->tag;
    material_name = chain->material_name;
  }
  bool view_valid = valid && body_key && body_key[0] != '\0' && shape_key &&
                    shape_key[0] != '\0';
  if (!view_valid) {
    PhysShapeTombstone *t = shape_tombstone_get(w, shape_id);
    if (t) {
      body_key = t->body;
      shape_key = t->shape;
      tag = t->tag;
      material_name = t->material;
      material_id = t->material_id;
    }
  }
  if (is_a) {
    out->a_valid = view_valid;
    out->a_body = phys_strdup(body_key ? body_key : "");
    out->a_shape = phys_strdup(shape_key ? shape_key : "");
    out->a_tag = phys_strdup(tag ? tag : "");
    out->a_material = phys_strdup(material_name ? material_name : "");
    out->a_material_id = material_id;
  } else {
    out->b_valid = view_valid;
    out->b_body = phys_strdup(body_key ? body_key : "");
    out->b_shape = phys_strdup(shape_key ? shape_key : "");
    out->b_tag = phys_strdup(tag ? tag : "");
    out->b_material = phys_strdup(material_name ? material_name : "");
    out->b_material_id = material_id;
  }
}

static void fill_sensor_snapshot(PhysWorld *w, PhysContactSnapshot *out,
                                 b2ShapeId sensor_shape_id,
                                 b2ShapeId visitor_shape_id) {
  fill_shape_snapshot(w, out, true, sensor_shape_id);
  fill_shape_snapshot(w, out, false, visitor_shape_id);
}

static void capture_contact_events(PhysWorld *w) {
  b2ContactEvents ev = b2World_GetContactEvents(w->id);
  for (int i = 0; i < ev.beginCount; ++i) {
    b2ContactBeginTouchEvent *src = &ev.beginEvents[i];
    PhysContactSnapshot *dst = event_push(
        &w->events.begins, &w->events.begin_count, &w->events.begin_cap);
    if (!dst)
      continue;
    fill_shape_snapshot(w, dst, true, src->shapeIdA);
    fill_shape_snapshot(w, dst, false, src->shapeIdB);
    dst->nx = src->manifold.normal.x;
    dst->ny = src->manifold.normal.y;
    dst->point_count = src->manifold.pointCount;
    if (src->manifold.pointCount > 0) {
      dst->x = src->manifold.points[0].point.x;
      dst->y = src->manifold.points[0].point.y;
    }
  }
  for (int i = 0; i < ev.endCount; ++i) {
    b2ContactEndTouchEvent *src = &ev.endEvents[i];
    PhysContactSnapshot *dst =
        event_push(&w->events.ends, &w->events.end_count, &w->events.end_cap);
    if (!dst)
      continue;
    fill_shape_snapshot(w, dst, true, src->shapeIdA);
    fill_shape_snapshot(w, dst, false, src->shapeIdB);
  }
  for (int i = 0; i < ev.hitCount; ++i) {
    b2ContactHitEvent *src = &ev.hitEvents[i];
    PhysContactSnapshot *dst =
        event_push(&w->events.hits, &w->events.hit_count, &w->events.hit_cap);
    if (!dst)
      continue;
    fill_shape_snapshot(w, dst, true, src->shapeIdA);
    fill_shape_snapshot(w, dst, false, src->shapeIdB);
    dst->nx = src->normal.x;
    dst->ny = src->normal.y;
    dst->x = src->point.x;
    dst->y = src->point.y;
    dst->approach_speed = src->approachSpeed;
  }
}

static void capture_sensor_events(PhysWorld *w) {
  b2SensorEvents ev = b2World_GetSensorEvents(w->id);
  for (int i = 0; i < ev.beginCount; ++i) {
    b2SensorBeginTouchEvent *src = &ev.beginEvents[i];
    PhysContactSnapshot *dst =
        event_push(&w->events.sensor_begins, &w->events.sensor_begin_count,
                   &w->events.sensor_begin_cap);
    if (!dst)
      continue;
    fill_sensor_snapshot(w, dst, src->sensorShapeId, src->visitorShapeId);
  }
  for (int i = 0; i < ev.endCount; ++i) {
    b2SensorEndTouchEvent *src = &ev.endEvents[i];
    PhysContactSnapshot *dst =
        event_push(&w->events.sensor_ends, &w->events.sensor_end_count,
                   &w->events.sensor_end_cap);
    if (!dst)
      continue;
    fill_sensor_snapshot(w, dst, src->sensorShapeId, src->visitorShapeId);
  }
}

static void capture_body_events(PhysWorld *w) {
  b2BodyEvents ev = b2World_GetBodyEvents(w->id);
  for (int i = 0; i < ev.moveCount; ++i) {
    b2BodyMoveEvent *src = &ev.moveEvents[i];
    PhysBody *body = (PhysBody *)src->userData;
    PhysBodyEventSnapshot *dst = body_event_push(
        &w->events.moves, &w->events.move_count, &w->events.move_cap);
    if (!dst)
      continue;
    bool valid = B2_IS_NON_NULL(src->bodyId) && b2Body_IsValid(src->bodyId) &&
                 body && body->key;
    dst->valid = valid;
    dst->body = phys_strdup(valid ? body->key : "");
    dst->x = src->transform.p.x;
    dst->y = src->transform.p.y;
    dst->angle = b2Rot_GetAngle(src->transform.q);
    dst->fell_asleep = src->fellAsleep;
  }
}

static void capture_step_events(PhysWorld *w) {
  capture_contact_events(w);
  capture_sensor_events(w);
  capture_body_events(w);
}

static int l_phys2d_step(lua_State *L) {
  if (phys_in_callback(L, "phys2d_step"))
    return 0;
  PhysWorld *w = check_world(L, 1);
  float dt = (float)luaL_checknumber(L, 2);
  if (dt < 0.0f)
    dt = 0.0f;
  if (!w->begun && !w->step_without_begin_logged) {
    SDL_Log("phys2d_step: world '%s' stepped without phys2d_begin; using "
            "previous declarations",
            w->key ? w->key : "?");
    w->step_without_begin_logged = true;
  }
  if (w->callbacks_pending) {
    w->callbacks_generation = w->generation;
    w->callbacks_pending = false;
  }
  if (callbacks_any(&w->callbacks) &&
      w->callbacks_generation != w->generation) {
    callbacks_clear(L, w);
  }
  if (w->prune)
    prune_world(w);
  int command_count = apply_body_commands(w);
  event_buffer_clear(&w->events);
  w->accumulator += dt;
  int steps = 0;
  while (w->accumulator + 1e-9 >= (double)w->fixed_dt && steps < w->max_steps) {
    PhysWorld *prev_mixer_world = g_mixer_world;
    g_mixer_world = callbacks_any(&w->callbacks) ? w : NULL;
    b2World_Step(w->id, w->fixed_dt, w->substeps);
    g_mixer_world = prev_mixer_world;
    w->accumulator -= (double)w->fixed_dt;
    steps++;
  }
  bool dropped = false;
  if (w->accumulator + 1e-9 >= (double)w->fixed_dt) {
    w->accumulator = 0.0;
    dropped = true;
  }
  capture_step_events(w);
  w->begun = false;
  callbacks_clear(L, w);
  lua_newtable(L);
  lua_pushinteger(L, steps);
  lua_setfield(L, -2, "steps");
  lua_pushinteger(L, command_count);
  lua_setfield(L, -2, "commands");
  lua_pushnumber(L, w->fixed_dt > 0.0f ? w->accumulator / w->fixed_dt : 0.0);
  lua_setfield(L, -2, "alpha");
  lua_pushboolean(L, dropped);
  lua_setfield(L, -2, "dropped");
  lua_pushinteger(L, w->events.begin_count);
  lua_setfield(L, -2, "contact_begins");
  lua_pushinteger(L, w->events.end_count);
  lua_setfield(L, -2, "contact_ends");
  lua_pushinteger(L, w->events.hit_count);
  lua_setfield(L, -2, "contact_hits");
  lua_pushinteger(L, w->events.sensor_begin_count);
  lua_setfield(L, -2, "sensor_begins");
  lua_pushinteger(L, w->events.sensor_end_count);
  lua_setfield(L, -2, "sensor_ends");
  lua_pushinteger(L, w->events.move_count);
  lua_setfield(L, -2, "body_moves");
  lua_pushinteger(L, w->events.move_count);
  lua_setfield(L, -2, "body_events");
  return 1;
}

static void push_pose(lua_State *L, PhysBody *b) {
  b2Vec2 p = b2Body_GetPosition(b->id);
  b2Rot q = b2Body_GetRotation(b->id);
  b2Vec2 v = b2Body_GetLinearVelocity(b->id);
  float w = b2Body_GetAngularVelocity(b->id);
  lua_newtable(L);
  lua_pushnumber(L, p.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, p.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, b2Rot_GetAngle(q));
  lua_setfield(L, -2, "angle");
  lua_pushnumber(L, v.x);
  lua_setfield(L, -2, "vx");
  lua_pushnumber(L, v.y);
  lua_setfield(L, -2, "vy");
  lua_pushnumber(L, w);
  lua_setfield(L, -2, "w");
  lua_pushboolean(L, b2Body_IsAwake(b->id));
  lua_setfield(L, -2, "awake");
  lua_pushboolean(L, b2Body_IsEnabled(b->id));
  lua_setfield(L, -2, "enabled");
  lua_pushboolean(L, b2Body_IsSleepEnabled(b->id));
  lua_setfield(L, -2, "sleep");
  lua_pushnumber(L, b2Body_GetSleepThreshold(b->id));
  lua_setfield(L, -2, "sleep_threshold");
}

static int l_phys2d_pose(lua_State *L) {
  PhysBody *b = NULL;
  if (is_ref(L, 1, "phys2d_body")) {
    const char *world_key = ref_string(L, 1, "world");
    const char *body_key = ref_string(L, 1, "key");
    PhysWorld *w = world_get(g_phys_state, world_key);
    b = w ? body_get(w, body_key) : NULL;
  } else if (is_ref(L, 1, "phys2d_world")) {
    PhysWorld *w = query_world_ref(L, 1);
    if (!w)
      return push_not_found(L);
    const char *body_key = luaL_checkstring(L, 2);
    b = body_get(w, body_key);
  } else {
    return luaL_error(L, "phys2d_pose: expected BodyRef or WorldRef, key");
  }
  if (!b || B2_IS_NULL(b->id) || !b2Body_IsValid(b->id)) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_pose(L, b);
  return 1;
}

static void push_vec2(lua_State *L, b2Vec2 v) {
  lua_newtable(L);
  lua_pushnumber(L, v.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, v.y);
  lua_setfield(L, -2, "y");
}

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

static void push_filter_fields(lua_State *L, b2Filter filter) {
  push_u64_hex_field(L, "category_bits", filter.categoryBits);
  push_u64_hex_field(L, "mask_bits", filter.maskBits);
  int category = single_bit_index(filter.categoryBits);
  if (category >= 0) {
    lua_pushinteger(L, category);
    lua_setfield(L, -2, "category");
  }
  push_bit_indices(L, filter.maskBits);
  lua_setfield(L, -2, "mask");
  lua_pushinteger(L, filter.groupIndex);
  lua_setfield(L, -2, "group");
  lua_pushinteger(L, filter.groupIndex);
  lua_setfield(L, -2, "group_index");
}

static void push_filter_info(lua_State *L, b2Filter filter) {
  lua_newtable(L);
  push_filter_fields(L, filter);
}

static void push_aabb(lua_State *L, b2AABB aabb) {
  lua_newtable(L);
  lua_pushnumber(L, aabb.lowerBound.x);
  lua_setfield(L, -2, "min_x");
  lua_pushnumber(L, aabb.lowerBound.y);
  lua_setfield(L, -2, "min_y");
  lua_pushnumber(L, aabb.upperBound.x);
  lua_setfield(L, -2, "max_x");
  lua_pushnumber(L, aabb.upperBound.y);
  lua_setfield(L, -2, "max_y");
}

static int l_phys2d_world_info(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  bool valid = B2_IS_NON_NULL(w->id) && b2World_IsValid(w->id);

  lua_newtable(L);
  lua_pushstring(L, w->key ? w->key : "");
  lua_setfield(L, -2, "key");
  lua_pushboolean(L, valid);
  lua_setfield(L, -2, "valid");
  lua_pushinteger(L, (lua_Integer)w->version);
  lua_setfield(L, -2, "version");
  lua_pushinteger(L, (lua_Integer)w->generation);
  lua_setfield(L, -2, "generation");
  lua_pushboolean(L, w->begun);
  lua_setfield(L, -2, "begun");
  lua_pushboolean(L, w->prune);
  lua_setfield(L, -2, "prune");
  lua_pushnumber(L, w->fixed_dt);
  lua_setfield(L, -2, "fixed_dt");
  lua_pushinteger(L, w->substeps);
  lua_setfield(L, -2, "substeps");
  lua_pushinteger(L, w->max_steps);
  lua_setfield(L, -2, "max_steps");
  lua_pushnumber(L, w->accumulator);
  lua_setfield(L, -2, "accumulator");
  lua_pushinteger(L, w->commands.count);
  lua_setfield(L, -2, "pending_commands");

  lua_newtable(L);
  lua_pushboolean(L, callback_ref_is_set(w->callbacks.filter_ref));
  lua_setfield(L, -2, "filter");
  lua_pushboolean(L, callback_ref_is_set(w->callbacks.pre_solve_ref));
  lua_setfield(L, -2, "pre_solve");
  lua_pushboolean(L, callback_ref_is_set(w->callbacks.friction_ref));
  lua_setfield(L, -2, "friction");
  lua_pushboolean(L, callback_ref_is_set(w->callbacks.restitution_ref));
  lua_setfield(L, -2, "restitution");
  lua_setfield(L, -2, "callbacks");

  if (!valid)
    return 1;

  b2Vec2 gravity = b2World_GetGravity(w->id);
  push_vec2(L, gravity);
  lua_setfield(L, -2, "gravity");
  lua_pushnumber(L, gravity.x);
  lua_setfield(L, -2, "gx");
  lua_pushnumber(L, gravity.y);
  lua_setfield(L, -2, "gy");
  lua_pushboolean(L, b2World_IsSleepingEnabled(w->id));
  lua_setfield(L, -2, "sleep");
  lua_pushboolean(L, b2World_IsContinuousEnabled(w->id));
  lua_setfield(L, -2, "continuous");
  lua_pushboolean(L, b2World_IsWarmStartingEnabled(w->id));
  lua_setfield(L, -2, "warm_starting");
  lua_pushnumber(L, b2World_GetRestitutionThreshold(w->id));
  lua_setfield(L, -2, "restitution_threshold");
  lua_pushnumber(L, b2World_GetHitEventThreshold(w->id));
  lua_setfield(L, -2, "hit_event_threshold");
  lua_pushnumber(L, b2World_GetMaximumLinearSpeed(w->id));
  lua_setfield(L, -2, "maximum_linear_speed");
  lua_pushinteger(L, b2World_GetAwakeBodyCount(w->id));
  lua_setfield(L, -2, "awake_body_count");
  return 1;
}

static int l_phys2d_velocity(lua_State *L) {
  PhysBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  b2Vec2 v = b2Body_GetLinearVelocity(b->id);
  float w = b2Body_GetAngularVelocity(b->id);
  lua_newtable(L);
  lua_pushnumber(L, v.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, v.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, w);
  lua_setfield(L, -2, "w");
  return 1;
}

static int l_phys2d_mass(lua_State *L) {
  PhysBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  b2MassData mass_data = b2Body_GetMassData(b->id);
  b2Vec2 center = b2Body_GetWorldCenterOfMass(b->id);
  lua_newtable(L);
  lua_pushnumber(L, mass_data.mass);
  lua_setfield(L, -2, "mass");
  lua_pushnumber(L, mass_data.rotationalInertia);
  lua_setfield(L, -2, "inertia");
  lua_pushnumber(L, mass_data.rotationalInertia);
  lua_setfield(L, -2, "rotational_inertia");
  push_vec2(L, center);
  lua_setfield(L, -2, "center");
  push_vec2(L, mass_data.center);
  lua_setfield(L, -2, "local_center");
  return 1;
}

static int l_phys2d_center(lua_State *L) {
  PhysBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  b2Vec2 center = b2Body_GetWorldCenterOfMass(b->id);
  push_vec2(L, center);
  return 1;
}

static int l_phys2d_world_point(lua_State *L) {
  PhysBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2Vec2 local = value_vec2(L, 2, b2Vec2_zero);
  push_vec2(L, b2Body_GetWorldPoint(b->id, local));
  return 1;
}

static int l_phys2d_local_point(lua_State *L) {
  PhysBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2Vec2 world = value_vec2(L, 2, b2Vec2_zero);
  push_vec2(L, b2Body_GetLocalPoint(b->id, world));
  return 1;
}

static int l_phys2d_velocity_at(lua_State *L) {
  PhysBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2Vec2 world = value_vec2(L, 2, b2Vec2_zero);
  push_vec2(L, b2Body_GetWorldPointVelocity(b->id, world));
  return 1;
}

static int l_phys2d_shape_test_point(lua_State *L) {
  PhysShape *s = query_shape_ref(L, 1);
  if (!s)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2Vec2 point = value_vec2(L, 2, b2Vec2_zero);
  lua_pushboolean(L, b2Shape_TestPoint(s->id, point));
  return 1;
}

static void parse_raycast_query(lua_State *L, int idx, b2RayCastInput *input) {
  input->origin = (b2Vec2){table_number(L, idx, "x", NULL, 0.0f),
                           table_number(L, idx, "y", NULL, 0.0f)};
  input->origin = table_vec2(L, idx, "origin", "from", input->origin);
  input->translation = (b2Vec2){table_number(L, idx, "dx", NULL, 0.0f),
                                table_number(L, idx, "dy", NULL, 0.0f)};
  input->translation =
      table_vec2(L, idx, "translation", "delta", input->translation);
  if (table_get_any(L, idx, "to", NULL)) {
    if (lua_istable(L, -1)) {
      b2Vec2 to = value_vec2(L, lua_gettop(L), input->origin);
      input->translation.x = to.x - input->origin.x;
      input->translation.y = to.y - input->origin.y;
    }
    lua_pop(L, 1);
  }
  input->maxFraction =
      table_number(L, idx, "max_fraction", "maxFraction", 1.0f);
  if (input->maxFraction < 0.0f)
    input->maxFraction = 0.0f;
}

static int l_phys2d_shape_raycast(lua_State *L) {
  PhysShape *s = query_shape_ref(L, 1);
  if (!s)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2RayCastInput input;
  parse_raycast_query(L, 2, &input);
  if (input.translation.x * input.translation.x +
          input.translation.y * input.translation.y <=
      1e-12f)
    return luaL_error(L,
                      "phys2d_shape_raycast: ray translation must be non-zero");
  b2CastOutput hit = b2Shape_RayCast(s->id, &input);
  if (!hit.hit) {
    lua_pushnil(L);
    return 1;
  }
  lua_newtable(L);
  lua_pushnumber(L, hit.point.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, hit.point.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, hit.normal.x);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, hit.normal.y);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, hit.fraction);
  lua_setfield(L, -2, "fraction");
  lua_pushinteger(L, hit.iterations);
  lua_setfield(L, -2, "iterations");
  return 1;
}

static int l_phys2d_shape_closest_point(lua_State *L) {
  PhysShape *s = query_shape_ref(L, 1);
  if (!s)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2Vec2 target = value_vec2(L, 2, b2Vec2_zero);
  push_vec2(L, b2Shape_GetClosestPoint(s->id, target));
  return 1;
}

static int l_phys2d_shape_aabb(lua_State *L) {
  PhysShape *s = query_shape_ref(L, 1);
  if (!s)
    return push_not_found(L);
  push_aabb(L, b2Shape_GetAABB(s->id));
  return 1;
}

static void push_shape_event_part(lua_State *L, const char *body,
                                  const char *shape, const char *tag,
                                  const char *material_name, int material_id,
                                  bool valid) {
  lua_newtable(L);
  lua_pushstring(L, body ? body : "");
  lua_setfield(L, -2, "body");
  lua_pushstring(L, shape ? shape : "");
  lua_setfield(L, -2, "shape");
  if (tag && tag[0] != '\0') {
    lua_pushstring(L, tag);
    lua_setfield(L, -2, "tag");
  }
  bool has_identity = (body && body[0] != '\0') || (shape && shape[0] != '\0');
  if (valid || has_identity || (material_name && material_name[0] != '\0') ||
      material_id != 0) {
    if (material_name && material_name[0] != '\0') {
      lua_pushstring(L, material_name);
    } else {
      lua_pushinteger(L, material_id);
    }
    lua_setfield(L, -2, "material");
    lua_pushinteger(L, material_id);
    lua_setfield(L, -2, "user_material_id");
  }
  lua_pushboolean(L, valid);
  lua_setfield(L, -2, "valid");
}

static void push_shape_id_view(lua_State *L, b2ShapeId shape_id) {
  bool valid = B2_IS_NON_NULL(shape_id) && b2Shape_IsValid(shape_id);
  PhysShape *shape = valid ? (PhysShape *)b2Shape_GetUserData(shape_id) : NULL;
  PhysChain *chain = NULL;
  if (valid && !shape) {
    chain = chain_find_by_id(b2Shape_GetParentChain(shape_id));
  }
  const char *body_key = "";
  const char *shape_key = "";
  const char *chain_key = "";
  const char *tag = NULL;
  const char *material_name = NULL;
  if (shape && shape->body) {
    body_key = shape->body->key;
    shape_key = shape->key;
    tag = shape->tag;
    material_name = shape->material_name;
  } else if (chain && chain->body) {
    body_key = chain->body->key;
    shape_key = chain->key;
    chain_key = chain->key;
    tag = chain->tag;
    material_name = chain->material_name;
  }
  lua_newtable(L);
  lua_pushstring(L, body_key ? body_key : "");
  lua_setfield(L, -2, "body");
  lua_pushstring(L, shape_key ? shape_key : "");
  lua_setfield(L, -2, "shape");
  if (tag && tag[0] != '\0') {
    lua_pushstring(L, tag);
    lua_setfield(L, -2, "tag");
  }
  if (chain_key && chain_key[0] != '\0') {
    lua_pushstring(L, chain_key);
    lua_setfield(L, -2, "chain");
    lua_pushboolean(L, true);
    lua_setfield(L, -2, "segment");
  }
  if (valid) {
    int material_id = b2Shape_GetMaterial(shape_id);
    if (material_name && material_name[0] != '\0') {
      lua_pushstring(L, material_name);
    } else {
      lua_pushinteger(L, material_id);
    }
    lua_setfield(L, -2, "material");
    lua_pushinteger(L, material_id);
    lua_setfield(L, -2, "user_material_id");
    push_filter_fields(L, b2Shape_GetFilter(shape_id));
  }
  lua_pushboolean(L, valid && ((shape && shape->body && shape->key) ||
                               (chain && chain->body && chain->key)));
  lua_setfield(L, -2, "valid");
}

static void callback_log_error_once(PhysWorld *w, bool *logged,
                                    const char *name, lua_State *L) {
  if (!w || !logged || *logged)
    return;
  const char *message = lua_tostring(L, -1);
  SDL_Log("phys2d %s callback error: %s", name,
          message ? message : "unknown error");
  *logged = true;
}

static void push_manifold_point(lua_State *L, const b2ManifoldPoint *p) {
  lua_newtable(L);
  lua_pushnumber(L, p->point.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, p->point.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, p->anchorA.x);
  lua_setfield(L, -2, "anchor_a_x");
  lua_pushnumber(L, p->anchorA.y);
  lua_setfield(L, -2, "anchor_a_y");
  lua_pushnumber(L, p->anchorB.x);
  lua_setfield(L, -2, "anchor_b_x");
  lua_pushnumber(L, p->anchorB.y);
  lua_setfield(L, -2, "anchor_b_y");
  lua_pushnumber(L, p->separation);
  lua_setfield(L, -2, "separation");
  lua_pushnumber(L, p->normalImpulse);
  lua_setfield(L, -2, "normal_impulse");
  lua_pushnumber(L, p->tangentImpulse);
  lua_setfield(L, -2, "tangent_impulse");
  lua_pushnumber(L, p->totalNormalImpulse);
  lua_setfield(L, -2, "total_normal_impulse");
  lua_pushnumber(L, p->normalVelocity);
  lua_setfield(L, -2, "normal_velocity");
  lua_pushinteger(L, p->id);
  lua_setfield(L, -2, "id");
  lua_pushboolean(L, p->persisted);
  lua_setfield(L, -2, "persisted");
}

static void push_pre_solve_contact(lua_State *L, b2ShapeId shape_id_a,
                                   b2ShapeId shape_id_b,
                                   const b2Manifold *manifold) {
  int point_count = manifold ? manifold->pointCount : 0;
  if (point_count < 0)
    point_count = 0;
  if (point_count > 2)
    point_count = 2;

  lua_newtable(L);
  push_shape_id_view(L, shape_id_a);
  lua_setfield(L, -2, "a");
  push_shape_id_view(L, shape_id_b);
  lua_setfield(L, -2, "b");
  lua_pushnumber(L, manifold ? manifold->normal.x : 0.0f);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, manifold ? manifold->normal.y : 0.0f);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, manifold ? manifold->rollingImpulse : 0.0f);
  lua_setfield(L, -2, "rolling_impulse");
  lua_pushinteger(L, point_count);
  lua_setfield(L, -2, "point_count");

  lua_newtable(L);
  for (int i = 0; i < point_count; ++i) {
    push_manifold_point(L, &manifold->points[i]);
    lua_rawseti(L, -2, i + 1);
  }
  lua_setfield(L, -2, "points");

  if (point_count > 0) {
    const b2ManifoldPoint *p = &manifold->points[0];
    lua_pushnumber(L, p->point.x);
    lua_setfield(L, -2, "x");
    lua_pushnumber(L, p->point.y);
    lua_setfield(L, -2, "y");
    lua_pushnumber(L, p->separation);
    lua_setfield(L, -2, "separation");
    lua_pushnumber(L, p->normalVelocity);
    lua_setfield(L, -2, "normal_velocity");
  }
}

static bool phys2d_custom_filter_callback(b2ShapeId shape_id_a,
                                          b2ShapeId shape_id_b, void *context) {
  PhysWorld *w = (PhysWorld *)context;
  if (!w || !w->callbacks.L || !callback_ref_is_set(w->callbacks.filter_ref))
    return true;
  lua_State *L = w->callbacks.L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, w->callbacks.filter_ref);
  push_shape_id_view(L, shape_id_a);
  push_shape_id_view(L, shape_id_b);
  g_phys_callback_depth++;
  int status = lua_pcall(L, 2, 1, 0);
  g_phys_callback_depth--;
  if (status != LUA_OK) {
    callback_log_error_once(w, &w->callbacks.filter_error_logged, "filter", L);
    lua_settop(L, top);
    return true;
  }
  bool collide = !lua_isboolean(L, -1) || lua_toboolean(L, -1) != 0;
  lua_settop(L, top);
  return collide;
}

static bool phys2d_pre_solve_callback(b2ShapeId shape_id_a,
                                      b2ShapeId shape_id_b,
                                      b2Manifold *manifold, void *context) {
  PhysWorld *w = (PhysWorld *)context;
  if (!w || !w->callbacks.L || !callback_ref_is_set(w->callbacks.pre_solve_ref))
    return true;
  lua_State *L = w->callbacks.L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, w->callbacks.pre_solve_ref);
  push_pre_solve_contact(L, shape_id_a, shape_id_b, manifold);
  g_phys_callback_depth++;
  int status = lua_pcall(L, 1, 1, 0);
  g_phys_callback_depth--;
  if (status != LUA_OK) {
    callback_log_error_once(w, &w->callbacks.pre_solve_error_logged,
                            "pre_solve", L);
    lua_settop(L, top);
    return true;
  }
  bool solve = !lua_isboolean(L, -1) || lua_toboolean(L, -1) != 0;
  lua_settop(L, top);
  return solve;
}

static float default_friction(float friction_a, float friction_b) {
  if (friction_a < 0.0f)
    friction_a = 0.0f;
  if (friction_b < 0.0f)
    friction_b = 0.0f;
  return sqrtf(friction_a * friction_b);
}

static float default_restitution(float restitution_a, float restitution_b) {
  return restitution_a > restitution_b ? restitution_a : restitution_b;
}

static void push_material_view(lua_State *L, const char *field, float value,
                               int material) {
  lua_newtable(L);
  lua_pushnumber(L, value);
  lua_setfield(L, -2, field);
  lua_pushinteger(L, material);
  lua_setfield(L, -2, "material");
}

static float phys2d_friction_callback(float friction_a, int material_a,
                                      float friction_b, int material_b) {
  float fallback = default_friction(friction_a, friction_b);
  PhysWorld *w = g_mixer_world;
  if (!w || !w->callbacks.L || !callback_ref_is_set(w->callbacks.friction_ref))
    return fallback;
  lua_State *L = w->callbacks.L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, w->callbacks.friction_ref);
  push_material_view(L, "friction", friction_a, material_a);
  push_material_view(L, "friction", friction_b, material_b);
  g_phys_callback_depth++;
  int status = lua_pcall(L, 2, 1, 0);
  g_phys_callback_depth--;
  if (status != LUA_OK) {
    callback_log_error_once(w, &w->callbacks.friction_error_logged, "friction",
                            L);
    lua_settop(L, top);
    return fallback;
  }
  float out = lua_isnumber(L, -1) ? (float)lua_tonumber(L, -1) : fallback;
  lua_settop(L, top);
  return out;
}

static float phys2d_restitution_callback(float restitution_a, int material_a,
                                         float restitution_b, int material_b) {
  float fallback = default_restitution(restitution_a, restitution_b);
  PhysWorld *w = g_mixer_world;
  if (!w || !w->callbacks.L ||
      !callback_ref_is_set(w->callbacks.restitution_ref))
    return fallback;
  lua_State *L = w->callbacks.L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, w->callbacks.restitution_ref);
  push_material_view(L, "restitution", restitution_a, material_a);
  push_material_view(L, "restitution", restitution_b, material_b);
  g_phys_callback_depth++;
  int status = lua_pcall(L, 2, 1, 0);
  g_phys_callback_depth--;
  if (status != LUA_OK) {
    callback_log_error_once(w, &w->callbacks.restitution_error_logged,
                            "restitution", L);
    lua_settop(L, top);
    return fallback;
  }
  float out = lua_isnumber(L, -1) ? (float)lua_tonumber(L, -1) : fallback;
  lua_settop(L, top);
  return out;
}

static const char *shape_kind_name(PhysShapeKind kind) {
  switch (kind) {
  case PHYS_SHAPE_BOX:
    return "box";
  case PHYS_SHAPE_CIRCLE:
    return "circle";
  case PHYS_SHAPE_CAPSULE:
    return "capsule";
  case PHYS_SHAPE_SEGMENT:
    return "segment";
  case PHYS_SHAPE_POLYGON:
    return "polygon";
  default:
    return "unknown";
  }
}

static int l_phys2d_shape_info(lua_State *L) {
  PhysShape *s = query_shape_ref(L, 1);
  if (!s)
    return push_not_found(L);

  push_shape_id_view(L, s->id);
  lua_pushstring(L, shape_kind_name(s->kind));
  lua_setfield(L, -2, "kind");
  lua_pushnumber(L, b2Shape_GetDensity(s->id));
  lua_setfield(L, -2, "density");
  lua_pushnumber(L, b2Shape_GetFriction(s->id));
  lua_setfield(L, -2, "friction");
  lua_pushnumber(L, b2Shape_GetRestitution(s->id));
  lua_setfield(L, -2, "restitution");
  lua_pushboolean(L, b2Shape_IsSensor(s->id));
  lua_setfield(L, -2, "sensor");
  lua_pushboolean(L, b2Shape_AreSensorEventsEnabled(s->id));
  lua_setfield(L, -2, "sensor_events");
  lua_pushboolean(L, b2Shape_AreContactEventsEnabled(s->id));
  lua_setfield(L, -2, "contact");
  lua_pushboolean(L, b2Shape_ArePreSolveEventsEnabled(s->id));
  lua_setfield(L, -2, "pre_solve");
  lua_pushboolean(L, b2Shape_AreHitEventsEnabled(s->id));
  lua_setfield(L, -2, "hit");
  push_filter_info(L, b2Shape_GetFilter(s->id));
  lua_setfield(L, -2, "filter");
  push_aabb(L, b2Shape_GetAABB(s->id));
  lua_setfield(L, -2, "aabb");
  return 1;
}

static int l_phys2d_shape_set_material(lua_State *L) {
  if (phys_in_callback(L, "phys2d_shape_set_material"))
    return 0;
  PhysShape *s = check_shape(L, 1);
  check_live_shape(L, s, "phys2d_shape_set_material");

  int material_id = b2Shape_GetMaterial(s->id);
  bool set_material_id = false;
  bool set_material_name = false;
  const char *material_name = NULL;

  if (lua_istable(L, 2)) {
    float density = 0.0f;
    if (table_number_optional(L, 2, "density", NULL, &density))
      b2Shape_SetDensity(s->id, density, true);
    float friction = 0.0f;
    if (table_number_optional(L, 2, "friction", NULL, &friction))
      b2Shape_SetFriction(s->id, friction);
    float restitution = 0.0f;
    if (table_number_optional(L, 2, "restitution", NULL, &restitution))
      b2Shape_SetRestitution(s->id, restitution);

    if (table_get_any(L, 2, "material", NULL)) {
      if (lua_type(L, -1) == LUA_TSTRING) {
        material_name = lua_tostring(L, -1);
        set_material_name = true;
      } else if (lua_isinteger(L, -1) || lua_isnumber(L, -1)) {
        material_id = (int)lua_tointeger(L, -1);
        set_material_id = true;
        set_material_name = true;
      }
      lua_pop(L, 1);
    }
    if (table_int_optional(L, 2, "material_id", "materialId", &material_id))
      set_material_id = true;
    if (table_int_optional(L, 2, "user_material_id", "userMaterialId",
                           &material_id))
      set_material_id = true;
  } else if (lua_isinteger(L, 2) || lua_isnumber(L, 2)) {
    material_id = (int)lua_tointeger(L, 2);
    set_material_id = true;
    set_material_name = true;
  } else if (lua_type(L, 2) == LUA_TSTRING) {
    material_name = lua_tostring(L, 2);
    set_material_name = true;
  } else {
    return luaL_error(
        L, "phys2d_shape_set_material: expected table, number, or string");
  }

  if (set_material_id) {
    b2Shape_SetMaterial(s->id, material_id);
    s->material_id = material_id;
  }
  if (set_material_name)
    owned_string_set_lua(L, &s->material_name, material_name,
                         "phys2d_shape_set_material");
  shape_tombstone_update_shape(s);
  return 0;
}

static int l_phys2d_shape_set_filter(lua_State *L) {
  if (phys_in_callback(L, "phys2d_shape_set_filter"))
    return 0;
  PhysShape *s = check_shape(L, 1);
  check_live_shape(L, s, "phys2d_shape_set_filter");
  luaL_checktype(L, 2, LUA_TTABLE);

  b2Filter filter = b2Shape_GetFilter(s->id);
  int group = filter.groupIndex;
  if (table_get_any(L, 2, "filter", NULL)) {
    if (lua_istable(L, -1)) {
      parse_filter_table(L, lua_gettop(L), &filter.categoryBits,
                         &filter.maskBits, &group);
      filter.groupIndex = group;
    }
    lua_pop(L, 1);
  } else {
    parse_filter_table(L, 2, &filter.categoryBits, &filter.maskBits, &group);
    filter.groupIndex = group;
  }
  b2Shape_SetFilter(s->id, filter);
  shape_tombstone_update_shape(s);
  return 0;
}

static int l_phys2d_shape_set_events(lua_State *L) {
  if (phys_in_callback(L, "phys2d_shape_set_events"))
    return 0;
  PhysShape *s = check_shape(L, 1);
  check_live_shape(L, s, "phys2d_shape_set_events");
  luaL_checktype(L, 2, LUA_TTABLE);

  bool flag = false;
  if (table_bool_optional(L, 2, "sensor", NULL, &flag))
    return luaL_error(
        L, "phys2d_shape_set_events: sensor cannot change at runtime");
  if (table_bool_optional(L, 2, "sensor_events", "sensorEvents", &flag))
    b2Shape_EnableSensorEvents(s->id, flag);
  if (table_bool_optional(L, 2, "contact", "contactEvents", &flag) ||
      table_bool_optional(L, 2, "contact_events", "contactEvents", &flag))
    b2Shape_EnableContactEvents(s->id, flag);
  if (table_bool_optional(L, 2, "pre_solve", "preSolve", &flag))
    b2Shape_EnablePreSolveEvents(s->id, flag);
  if (table_bool_optional(L, 2, "hit", "hitEvents", &flag) ||
      table_bool_optional(L, 2, "hit_events", "hitEvents", &flag))
    b2Shape_EnableHitEvents(s->id, flag);
  shape_tombstone_update_shape(s);
  return 0;
}

static int l_phys2d_body_shapes(lua_State *L) {
  PhysBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  int capacity = b2Body_GetShapeCount(b->id);
  lua_newtable(L);
  if (capacity <= 0)
    return 1;
  b2ShapeId *ids = (b2ShapeId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return luaL_error(L, "phys2d_body_shapes: out of memory");
  int count = b2Body_GetShapes(b->id, ids, capacity);
  for (int i = 0; i < count; ++i) {
    push_shape_id_view(L, ids[i]);
    if (B2_IS_NON_NULL(ids[i]) && b2Shape_IsValid(ids[i])) {
      PhysShape *shape = (PhysShape *)b2Shape_GetUserData(ids[i]);
      if (shape) {
        lua_pushstring(L, shape_kind_name(shape->kind));
        lua_setfield(L, -2, "kind");
      } else if (chain_find_by_id(b2Shape_GetParentChain(ids[i]))) {
        lua_pushstring(L, "chain_segment");
        lua_setfield(L, -2, "kind");
      }
    }
    lua_rawseti(L, -2, i + 1);
  }
  SDL_free(ids);
  return 1;
}

static void push_joint_view(lua_State *L, PhysJoint *j) {
  bool valid = joint_is_live(j);
  lua_newtable(L);
  lua_pushstring(L, j && j->key ? j->key : "");
  lua_setfield(L, -2, "joint");
  lua_pushstring(L, j ? joint_kind_name(j->kind) : "unknown");
  lua_setfield(L, -2, "type");
  lua_pushstring(L, j && j->body_a && j->body_a->key ? j->body_a->key : "");
  lua_setfield(L, -2, "a");
  lua_pushstring(L, j && j->body_b && j->body_b->key ? j->body_b->key : "");
  lua_setfield(L, -2, "b");
  lua_pushboolean(L, valid);
  lua_setfield(L, -2, "valid");
}

static void push_joint_id_view(lua_State *L, b2JointId joint_id) {
  PhysJoint *j = B2_IS_NON_NULL(joint_id) && b2Joint_IsValid(joint_id)
                     ? (PhysJoint *)b2Joint_GetUserData(joint_id)
                     : NULL;
  push_joint_view(L, j);
}

static int l_phys2d_body_joints(lua_State *L) {
  PhysBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  int capacity = b2Body_GetJointCount(b->id);
  lua_newtable(L);
  if (capacity <= 0)
    return 1;
  b2JointId *ids = (b2JointId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return luaL_error(L, "phys2d_body_joints: out of memory");
  int count = b2Body_GetJoints(b->id, ids, capacity);
  for (int i = 0; i < count; ++i) {
    push_joint_id_view(L, ids[i]);
    lua_rawseti(L, -2, i + 1);
  }
  SDL_free(ids);
  return 1;
}

static int l_phys2d_joint_info(lua_State *L) {
  PhysJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  push_joint_view(L, j);
  lua_pushboolean(L, b2Joint_GetCollideConnected(j->id));
  lua_setfield(L, -2, "collide_connected");
  b2Vec2 force = b2Joint_GetConstraintForce(j->id);
  push_vec2(L, force);
  lua_setfield(L, -2, "force");
  lua_pushnumber(L, b2Joint_GetConstraintTorque(j->id));
  lua_setfield(L, -2, "torque");
  lua_pushnumber(L, b2Joint_GetLinearSeparation(j->id));
  lua_setfield(L, -2, "linear_separation");
  lua_pushnumber(L, b2Joint_GetAngularSeparation(j->id));
  lua_setfield(L, -2, "angular_separation");
  if (j->kind == PHYS_JOINT_DISTANCE || j->kind == PHYS_JOINT_PRISMATIC ||
      j->kind == PHYS_JOINT_REVOLUTE || j->kind == PHYS_JOINT_WELD ||
      j->kind == PHYS_JOINT_WHEEL) {
    push_vec2(L, b2Joint_GetLocalAnchorA(j->id));
    lua_setfield(L, -2, "local_anchor_a");
    push_vec2(L, b2Joint_GetLocalAnchorB(j->id));
    lua_setfield(L, -2, "local_anchor_b");
  }
  if (j->kind == PHYS_JOINT_PRISMATIC || j->kind == PHYS_JOINT_WHEEL) {
    push_vec2(L, b2Joint_GetLocalAxisA(j->id));
    lua_setfield(L, -2, "local_axis_a");
  }
  if (j->kind == PHYS_JOINT_PRISMATIC || j->kind == PHYS_JOINT_REVOLUTE ||
      j->kind == PHYS_JOINT_WELD) {
    lua_pushnumber(L, b2Joint_GetReferenceAngle(j->id));
    lua_setfield(L, -2, "reference_angle");
  }
  return 1;
}

static int l_phys2d_joint_force(lua_State *L) {
  PhysJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  push_vec2(L, b2Joint_GetConstraintForce(j->id));
  return 1;
}

static int l_phys2d_joint_torque(lua_State *L) {
  PhysJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  lua_pushnumber(L, b2Joint_GetConstraintTorque(j->id));
  return 1;
}

static int l_phys2d_joint_angle(lua_State *L) {
  PhysJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind != PHYS_JOINT_REVOLUTE) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, b2RevoluteJoint_GetAngle(j->id));
  return 1;
}

static int l_phys2d_joint_translation(lua_State *L) {
  PhysJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind != PHYS_JOINT_PRISMATIC) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, b2PrismaticJoint_GetTranslation(j->id));
  return 1;
}

static int l_phys2d_joint_speed(lua_State *L) {
  PhysJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind != PHYS_JOINT_PRISMATIC) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, b2PrismaticJoint_GetSpeed(j->id));
  return 1;
}

static int l_phys2d_joint_length(lua_State *L) {
  PhysJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind != PHYS_JOINT_DISTANCE) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, b2DistanceJoint_GetCurrentLength(j->id));
  return 1;
}

static int l_phys2d_joint_motor_force(lua_State *L) {
  PhysJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind == PHYS_JOINT_DISTANCE) {
    lua_pushnumber(L, b2DistanceJoint_GetMotorForce(j->id));
    return 1;
  }
  if (j->kind == PHYS_JOINT_PRISMATIC) {
    lua_pushnumber(L, b2PrismaticJoint_GetMotorForce(j->id));
    return 1;
  }
  lua_pushnil(L);
  return 1;
}

static int l_phys2d_joint_motor_torque(lua_State *L) {
  PhysJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind == PHYS_JOINT_REVOLUTE) {
    lua_pushnumber(L, b2RevoluteJoint_GetMotorTorque(j->id));
    return 1;
  }
  if (j->kind == PHYS_JOINT_WHEEL) {
    lua_pushnumber(L, b2WheelJoint_GetMotorTorque(j->id));
    return 1;
  }
  lua_pushnil(L);
  return 1;
}

static int l_phys2d_joint_set_motor(lua_State *L) {
  if (phys_in_callback(L, "phys2d_joint_set_motor"))
    return 0;
  PhysJoint *j = check_joint(L, 1);
  check_live_joint(L, j, "phys2d_joint_set_motor");
  luaL_checktype(L, 2, LUA_TTABLE);
  bool enabled = table_bool(L, 2, "enabled", NULL, true);
  float speed = table_number(L, 2, "speed", "motor_speed", 0.0f);
  float max_force = table_number(L, 2, "max_force", "maxForce", 1.0f);
  float max_torque = table_number(L, 2, "max_torque", "maxTorque", 1.0f);
  switch (j->kind) {
  case PHYS_JOINT_DISTANCE:
    b2DistanceJoint_EnableMotor(j->id, enabled);
    b2DistanceJoint_SetMotorSpeed(j->id, speed);
    b2DistanceJoint_SetMaxMotorForce(j->id, max_force);
    break;
  case PHYS_JOINT_PRISMATIC:
    b2PrismaticJoint_EnableMotor(j->id, enabled);
    b2PrismaticJoint_SetMotorSpeed(j->id, speed);
    b2PrismaticJoint_SetMaxMotorForce(j->id, max_force);
    break;
  case PHYS_JOINT_REVOLUTE:
    b2RevoluteJoint_EnableMotor(j->id, enabled);
    b2RevoluteJoint_SetMotorSpeed(j->id, speed);
    b2RevoluteJoint_SetMaxMotorTorque(j->id, max_torque);
    break;
  case PHYS_JOINT_WHEEL:
    b2WheelJoint_EnableMotor(j->id, enabled);
    b2WheelJoint_SetMotorSpeed(j->id, speed);
    b2WheelJoint_SetMaxMotorTorque(j->id, max_torque);
    break;
  case PHYS_JOINT_MOTOR:
    b2MotorJoint_SetMaxForce(j->id, max_force);
    b2MotorJoint_SetMaxTorque(j->id, max_torque);
    b2MotorJoint_SetCorrectionFactor(
        j->id, table_number(L, 2, "correction_factor", "correctionFactor",
                            b2MotorJoint_GetCorrectionFactor(j->id)));
    break;
  default:
    return luaL_error(L, "phys2d_joint_set_motor: unsupported joint type");
  }
  b2Joint_WakeBodies(j->id);
  return 0;
}

static int l_phys2d_joint_set_limit(lua_State *L) {
  if (phys_in_callback(L, "phys2d_joint_set_limit"))
    return 0;
  PhysJoint *j = check_joint(L, 1);
  check_live_joint(L, j, "phys2d_joint_set_limit");
  luaL_checktype(L, 2, LUA_TTABLE);
  bool enabled = table_bool(L, 2, "enabled", NULL, true);
  float lower = table_number(L, 2, "lower", NULL, 0.0f);
  float upper = table_number(L, 2, "upper", NULL, 1.0f);
  switch (j->kind) {
  case PHYS_JOINT_DISTANCE: {
    float min_length = table_number(L, 2, "min", "min_length", 0.0f);
    float max_length = table_number(L, 2, "max", "max_length", 1.0f);
    b2DistanceJoint_EnableLimit(j->id, enabled);
    b2DistanceJoint_SetLengthRange(j->id, min_length, max_length);
    break;
  }
  case PHYS_JOINT_PRISMATIC:
    b2PrismaticJoint_EnableLimit(j->id, enabled);
    b2PrismaticJoint_SetLimits(j->id, lower, upper);
    break;
  case PHYS_JOINT_REVOLUTE:
    b2RevoluteJoint_EnableLimit(j->id, enabled);
    b2RevoluteJoint_SetLimits(j->id, lower, upper);
    break;
  case PHYS_JOINT_WHEEL:
    b2WheelJoint_EnableLimit(j->id, enabled);
    b2WheelJoint_SetLimits(j->id, lower, upper);
    break;
  default:
    return luaL_error(L, "phys2d_joint_set_limit: unsupported joint type");
  }
  b2Joint_WakeBodies(j->id);
  return 0;
}

static int l_phys2d_joint_set_spring(lua_State *L) {
  if (phys_in_callback(L, "phys2d_joint_set_spring"))
    return 0;
  PhysJoint *j = check_joint(L, 1);
  check_live_joint(L, j, "phys2d_joint_set_spring");
  luaL_checktype(L, 2, LUA_TTABLE);
  bool enabled = table_bool(L, 2, "enabled", NULL, true);
  float hertz = table_number(L, 2, "hertz", NULL, 0.0f);
  float damping = table_number(L, 2, "damping_ratio", "dampingRatio", 0.0f);
  switch (j->kind) {
  case PHYS_JOINT_DISTANCE:
    b2DistanceJoint_EnableSpring(j->id, enabled);
    b2DistanceJoint_SetSpringHertz(j->id, hertz);
    b2DistanceJoint_SetSpringDampingRatio(j->id, damping);
    break;
  case PHYS_JOINT_PRISMATIC:
    b2PrismaticJoint_EnableSpring(j->id, enabled);
    b2PrismaticJoint_SetSpringHertz(j->id, hertz);
    b2PrismaticJoint_SetSpringDampingRatio(j->id, damping);
    break;
  case PHYS_JOINT_REVOLUTE:
    b2RevoluteJoint_EnableSpring(j->id, enabled);
    b2RevoluteJoint_SetSpringHertz(j->id, hertz);
    b2RevoluteJoint_SetSpringDampingRatio(j->id, damping);
    break;
  case PHYS_JOINT_WHEEL:
    b2WheelJoint_EnableSpring(j->id, enabled);
    b2WheelJoint_SetSpringHertz(j->id, hertz);
    b2WheelJoint_SetSpringDampingRatio(j->id, damping);
    break;
  case PHYS_JOINT_WELD:
    b2WeldJoint_SetLinearHertz(
        j->id, table_number(L, 2, "linear_hertz", "linearHertz", hertz));
    b2WeldJoint_SetLinearDampingRatio(
        j->id, table_number(L, 2, "linear_damping_ratio", "linearDampingRatio",
                            damping));
    b2WeldJoint_SetAngularHertz(
        j->id, table_number(L, 2, "angular_hertz", "angularHertz", hertz));
    b2WeldJoint_SetAngularDampingRatio(
        j->id, table_number(L, 2, "angular_damping_ratio",
                            "angularDampingRatio", damping));
    break;
  default:
    return luaL_error(L, "phys2d_joint_set_spring: unsupported joint type");
  }
  b2Joint_WakeBodies(j->id);
  return 0;
}

static int l_phys2d_joint_set_target(lua_State *L) {
  if (phys_in_callback(L, "phys2d_joint_set_target"))
    return 0;
  PhysJoint *j = check_joint(L, 1);
  check_live_joint(L, j, "phys2d_joint_set_target");
  luaL_checktype(L, 2, LUA_TTABLE);
  switch (j->kind) {
  case PHYS_JOINT_MOUSE: {
    b2Vec2 target =
        table_vec2(L, 2, "target", NULL, b2MouseJoint_GetTarget(j->id));
    target.x = table_number(L, 2, "x", NULL, target.x);
    target.y = table_number(L, 2, "y", NULL, target.y);
    b2MouseJoint_SetTarget(j->id, target);
    break;
  }
  case PHYS_JOINT_PRISMATIC:
    b2PrismaticJoint_SetTargetTranslation(
        j->id, table_number(L, 2, "translation", "target_translation",
                            b2PrismaticJoint_GetTargetTranslation(j->id)));
    break;
  case PHYS_JOINT_REVOLUTE:
    b2RevoluteJoint_SetTargetAngle(
        j->id, table_number(L, 2, "angle", "target_angle",
                            b2RevoluteJoint_GetTargetAngle(j->id)));
    break;
  case PHYS_JOINT_MOTOR: {
    b2Vec2 linear = table_vec2(L, 2, "linear_offset", "linearOffset",
                               b2MotorJoint_GetLinearOffset(j->id));
    b2MotorJoint_SetLinearOffset(j->id, linear);
    b2MotorJoint_SetAngularOffset(
        j->id, table_number(L, 2, "angular_offset", "angularOffset",
                            b2MotorJoint_GetAngularOffset(j->id)));
    break;
  }
  default:
    return luaL_error(L, "phys2d_joint_set_target: unsupported joint type");
  }
  b2Joint_WakeBodies(j->id);
  return 0;
}

static int l_phys2d_chain_segments(lua_State *L) {
  PhysChain *c = query_chain_ref(L, 1);
  if (!c)
    return push_not_found(L);
  int capacity = b2Chain_GetSegmentCount(c->id);
  lua_newtable(L);
  if (capacity <= 0)
    return 1;
  b2ShapeId *ids = (b2ShapeId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return luaL_error(L, "phys2d_chain_segments: out of memory");
  int count = b2Chain_GetSegments(c->id, ids, capacity);
  for (int i = 0; i < count; ++i) {
    push_shape_id_view(L, ids[i]);
    lua_pushinteger(L, i + 1);
    lua_setfield(L, -2, "index");
    lua_pushstring(L, "chain_segment");
    lua_setfield(L, -2, "kind");
    lua_rawseti(L, -2, i + 1);
  }
  SDL_free(ids);
  return 1;
}

static void push_contact_data(lua_State *L, const b2ContactData *contact) {
  lua_newtable(L);
  push_shape_id_view(L, contact->shapeIdA);
  lua_setfield(L, -2, "a");
  push_shape_id_view(L, contact->shapeIdB);
  lua_setfield(L, -2, "b");
  lua_pushnumber(L, contact->manifold.normal.x);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, contact->manifold.normal.y);
  lua_setfield(L, -2, "ny");
  lua_pushinteger(L, contact->manifold.pointCount);
  lua_setfield(L, -2, "point_count");
  if (contact->manifold.pointCount > 0) {
    lua_pushnumber(L, contact->manifold.points[0].point.x);
    lua_setfield(L, -2, "x");
    lua_pushnumber(L, contact->manifold.points[0].point.y);
    lua_setfield(L, -2, "y");
    lua_pushnumber(L, contact->manifold.points[0].separation);
    lua_setfield(L, -2, "separation");
  }
}

static int l_phys2d_body_contacts(lua_State *L) {
  PhysBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  int capacity = b2Body_GetContactCapacity(b->id);
  lua_newtable(L);
  if (capacity <= 0)
    return 1;
  b2ContactData *items = (b2ContactData *)SDL_malloc(sizeof(*items) * capacity);
  if (!items)
    return luaL_error(L, "phys2d_body_contacts: out of memory");
  int count = b2Body_GetContactData(b->id, items, capacity);
  for (int i = 0; i < count; ++i) {
    push_contact_data(L, &items[i]);
    lua_rawseti(L, -2, i + 1);
  }
  SDL_free(items);
  return 1;
}

static void push_contact_list(lua_State *L, PhysContactSnapshot *items,
                              int count) {
  lua_newtable(L);
  for (int i = 0; i < count; ++i) {
    PhysContactSnapshot *e = &items[i];
    lua_newtable(L);
    push_shape_event_part(L, e->a_body, e->a_shape, e->a_tag, e->a_material,
                          e->a_material_id, e->a_valid);
    lua_setfield(L, -2, "a");
    push_shape_event_part(L, e->b_body, e->b_shape, e->b_tag, e->b_material,
                          e->b_material_id, e->b_valid);
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
}

static void push_sensor_list(lua_State *L, PhysContactSnapshot *items,
                             int count) {
  lua_newtable(L);
  for (int i = 0; i < count; ++i) {
    PhysContactSnapshot *e = &items[i];
    lua_newtable(L);
    push_shape_event_part(L, e->a_body, e->a_shape, e->a_tag, e->a_material,
                          e->a_material_id, e->a_valid);
    lua_setfield(L, -2, "sensor");
    push_shape_event_part(L, e->b_body, e->b_shape, e->b_tag, e->b_material,
                          e->b_material_id, e->b_valid);
    lua_setfield(L, -2, "visitor");
    lua_rawseti(L, -2, i + 1);
  }
}

static void push_body_event_list(lua_State *L, PhysBodyEventSnapshot *items,
                                 int count) {
  lua_newtable(L);
  for (int i = 0; i < count; ++i) {
    PhysBodyEventSnapshot *e = &items[i];
    lua_newtable(L);
    lua_pushstring(L, e->body ? e->body : "");
    lua_setfield(L, -2, "body");
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
}

static int l_phys2d_contacts(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  const char *kind = luaL_optstring(L, 2, "begin");
  if (strcmp(kind, "begin") == 0) {
    push_contact_list(L, w->events.begins, w->events.begin_count);
  } else if (strcmp(kind, "end") == 0) {
    push_contact_list(L, w->events.ends, w->events.end_count);
  } else if (strcmp(kind, "hit") == 0) {
    push_contact_list(L, w->events.hits, w->events.hit_count);
  } else {
    return luaL_error(L, "phys2d_contacts: kind must be begin, end, or hit");
  }
  return 1;
}

static int l_phys2d_body_events(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  push_body_event_list(L, w->events.moves, w->events.move_count);
  return 1;
}

static int l_phys2d_sensors(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  const char *kind = luaL_optstring(L, 2, "begin");
  if (strcmp(kind, "begin") == 0) {
    push_sensor_list(L, w->events.sensor_begins, w->events.sensor_begin_count);
  } else if (strcmp(kind, "end") == 0) {
    push_sensor_list(L, w->events.sensor_ends, w->events.sensor_end_count);
  } else {
    return luaL_error(L, "phys2d_sensors: kind must be begin or end");
  }
  return 1;
}

static b2QueryFilter parse_query_filter(lua_State *L, int idx) {
  b2QueryFilter filter = b2DefaultQueryFilter();
  if (!lua_istable(L, idx))
    return filter;
  if (table_get_any(L, idx, "filter", NULL)) {
    if (lua_istable(L, -1))
      parse_filter_table(L, lua_gettop(L), &filter.categoryBits,
                         &filter.maskBits, NULL);
    lua_pop(L, 1);
  } else {
    parse_filter_table(L, idx, &filter.categoryBits, &filter.maskBits, NULL);
  }
  return filter;
}

static void push_raycast_hit(lua_State *L, b2ShapeId shape_id, b2Vec2 point,
                             b2Vec2 normal, float fraction) {
  push_shape_id_view(L, shape_id);
  lua_pushnumber(L, point.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, point.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, normal.x);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, normal.y);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, fraction);
  lua_setfield(L, -2, "fraction");
}

typedef struct PhysQueryContext {
  lua_State *L;
  int results_ref;
  int visitor_ref;
  int count;
  char *error;
} PhysQueryContext;

static int push_visitor_error(lua_State *L, const char *fn_name,
                              char *message) {
  lua_pushnil(L);
  lua_pushfstring(L, "%s visitor: %s", fn_name,
                  message ? message : "unknown error");
  SDL_free(message);
  return 2;
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

static bool overlap_result_callback(b2ShapeId shape_id, void *context) {
  PhysQueryContext *ctx = (PhysQueryContext *)context;
  lua_State *L = ctx->L;
  if (ctx->error)
    return false;

  lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->results_ref);
  push_shape_id_view(L, shape_id);
  bool include = true;
  bool keep_going = true;

  if (ctx->visitor_ref != LUA_NOREF) {
    lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->visitor_ref);
    lua_pushvalue(L, -2);
    g_phys_callback_depth++;
    int status = lua_pcall(L, 1, 1, 0);
    g_phys_callback_depth--;
    if (status != LUA_OK) {
      ctx->error = phys_strdup(lua_tostring(L, -1));
      lua_pop(L, 3);
      return false;
    }
    keep_going = parse_overlap_visitor_result(L, -1, &include);
    lua_pop(L, 1);
  }

  if (include) {
    lua_rawseti(L, -2, ++ctx->count);
  } else {
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  return keep_going;
}

static float raycast_result_callback(b2ShapeId shape_id, b2Vec2 point,
                                     b2Vec2 normal, float fraction,
                                     void *context) {
  PhysQueryContext *ctx = (PhysQueryContext *)context;
  lua_State *L = ctx->L;
  if (ctx->error)
    return 0.0f;

  lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->results_ref);
  push_raycast_hit(L, shape_id, point, normal, fraction);
  bool include = true;
  float result = fraction;

  if (ctx->visitor_ref != LUA_NOREF) {
    lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->visitor_ref);
    lua_pushvalue(L, -2);
    g_phys_callback_depth++;
    int status = lua_pcall(L, 1, 1, 0);
    g_phys_callback_depth--;
    if (status != LUA_OK) {
      ctx->error = phys_strdup(lua_tostring(L, -1));
      lua_pop(L, 3);
      return 0.0f;
    }
    result = parse_raycast_visitor_result(L, -1, fraction, &include);
    lua_pop(L, 1);
  }

  if (include) {
    lua_rawseti(L, -2, ++ctx->count);
  } else {
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  return result;
}

static void set_tree_stats(lua_State *L, b2TreeStats stats) {
  lua_pushinteger(L, stats.nodeVisits);
  lua_setfield(L, -2, "node_visits");
  lua_pushinteger(L, stats.leafVisits);
  lua_setfield(L, -2, "leaf_visits");
}

static int l_phys2d_raycast(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2Vec2 origin = {table_number(L, 2, "x", NULL, 0.0f),
                   table_number(L, 2, "y", NULL, 0.0f)};
  origin = table_vec2(L, 2, "origin", "from", origin);
  b2Vec2 translation = {table_number(L, 2, "dx", NULL, 0.0f),
                        table_number(L, 2, "dy", NULL, 0.0f)};
  translation = table_vec2(L, 2, "translation", "delta", translation);
  if (table_get_any(L, 2, "to", NULL)) {
    if (lua_istable(L, -1)) {
      b2Vec2 to = value_vec2(L, lua_gettop(L), origin);
      translation.x = to.x - origin.x;
      translation.y = to.y - origin.y;
    }
    lua_pop(L, 1);
  }
  float max_fraction = table_number(L, 2, "max_fraction", "maxFraction", 1.0f);
  if (max_fraction < 0.0f)
    max_fraction = 0.0f;
  translation.x *= max_fraction;
  translation.y *= max_fraction;
  if (translation.x * translation.x + translation.y * translation.y <= 1e-12f)
    return luaL_error(L, "phys2d_raycast: ray translation must be non-zero");
  b2QueryFilter filter = parse_query_filter(L, 2);

  if (!lua_isfunction(L, 3)) {
    b2RayResult hit =
        b2World_CastRayClosest(w->id, origin, translation, filter);
    if (!hit.hit) {
      lua_pushnil(L);
      return 1;
    }
    push_raycast_hit(L, hit.shapeId, hit.point, hit.normal, hit.fraction);
    lua_pushinteger(L, hit.nodeVisits);
    lua_setfield(L, -2, "node_visits");
    lua_pushinteger(L, hit.leafVisits);
    lua_setfield(L, -2, "leaf_visits");
    return 1;
  }

  lua_newtable(L);
  int results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  lua_pushvalue(L, 3);
  int visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  PhysQueryContext ctx = {.L = L,
                          .results_ref = results_ref,
                          .visitor_ref = visitor_ref,
                          .count = 0,
                          .error = NULL};
  b2TreeStats stats = b2World_CastRay(w->id, origin, translation, filter,
                                      raycast_result_callback, &ctx);
  if (ctx.error) {
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
    return push_visitor_error(L, "phys2d_raycast", ctx.error);
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, results_ref);
  set_tree_stats(L, stats);
  luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
  luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
  return 1;
}

static int l_phys2d_overlap_aabb(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2AABB aabb = {{table_number(L, 2, "min_x", "minX", 0.0f),
                  table_number(L, 2, "min_y", "minY", 0.0f)},
                 {table_number(L, 2, "max_x", "maxX", 0.0f),
                  table_number(L, 2, "max_y", "maxY", 0.0f)}};
  if (aabb.lowerBound.x > aabb.upperBound.x ||
      aabb.lowerBound.y > aabb.upperBound.y)
    return luaL_error(L, "phys2d_overlap_aabb: min must be <= max");
  b2QueryFilter filter = parse_query_filter(L, 2);

  lua_newtable(L);
  int results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  int visitor_ref = LUA_NOREF;
  if (lua_isfunction(L, 3)) {
    lua_pushvalue(L, 3);
    visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  PhysQueryContext ctx = {.L = L,
                          .results_ref = results_ref,
                          .visitor_ref = visitor_ref,
                          .count = 0,
                          .error = NULL};
  b2TreeStats stats =
      b2World_OverlapAABB(w->id, aabb, filter, overlap_result_callback, &ctx);
  if (ctx.error) {
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    if (visitor_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
    return push_visitor_error(L, "phys2d_overlap_aabb", ctx.error);
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, results_ref);
  set_tree_stats(L, stats);
  luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
  if (visitor_ref != LUA_NOREF)
    luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
  return 1;
}

static b2ShapeProxy parse_shape_cast_proxy(lua_State *L, int idx) {
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

  b2Vec2 position = {table_number(L, idx, "x", NULL, 0.0f),
                     table_number(L, idx, "y", NULL, 0.0f)};
  position = table_vec2(L, idx, "origin", "from", position);
  float angle = table_number(L, idx, "angle", NULL, 0.0f);

  if (strcmp(type, "circle") == 0) {
    float r = table_number(L, idx, "r", "radius", 0.0f);
    if (r <= 0.0f)
      luaL_error(L, "phys2d_shape_cast: circle r must be > 0");
    b2Vec2 center = {table_number(L, idx, "cx", NULL, 0.0f),
                     table_number(L, idx, "cy", NULL, 0.0f)};
    return b2MakeOffsetProxy(&center, 1, r, position, b2MakeRot(angle));
  }

  if (strcmp(type, "capsule") == 0) {
    float r = table_number(L, idx, "r", "radius", 0.0f);
    if (r <= 0.0f)
      luaL_error(L, "phys2d_shape_cast: capsule r must be > 0");
    b2Vec2 points[2] = {{table_number(L, idx, "ax", "x1", 0.0f),
                         table_number(L, idx, "ay", "y1", 0.0f)},
                        {table_number(L, idx, "bx", "x2", 0.0f),
                         table_number(L, idx, "by", "y2", 0.0f)}};
    float dx = points[1].x - points[0].x;
    float dy = points[1].y - points[0].y;
    if (dx * dx + dy * dy <= 1e-12f)
      luaL_error(L, "phys2d_shape_cast: capsule endpoints must be distinct");
    return b2MakeOffsetProxy(points, 2, r, position, b2MakeRot(angle));
  }

  if (strcmp(type, "segment") == 0) {
    b2Vec2 points[2] = {{table_number(L, idx, "ax", "x1", 0.0f),
                         table_number(L, idx, "ay", "y1", 0.0f)},
                        {table_number(L, idx, "bx", "x2", 0.0f),
                         table_number(L, idx, "by", "y2", 0.0f)}};
    float dx = points[1].x - points[0].x;
    float dy = points[1].y - points[0].y;
    if (dx * dx + dy * dy <= 1e-12f)
      luaL_error(L, "phys2d_shape_cast: segment endpoints must be distinct");
    return b2MakeOffsetProxy(points, 2, 0.0f, position, b2MakeRot(angle));
  }

  if (strcmp(type, "box") == 0) {
    float hx = table_number(L, idx, "hx", NULL, 0.0f);
    float hy = table_number(L, idx, "hy", NULL, 0.0f);
    if (hx <= 0.0f || hy <= 0.0f)
      luaL_error(L, "phys2d_shape_cast: box hx and hy must be > 0");
    float radius = table_number(L, idx, "radius", "r", 0.0f);
    if (radius < 0.0f)
      luaL_error(L, "phys2d_shape_cast: box radius must be >= 0");
    b2Vec2 center = {table_number(L, idx, "cx", NULL, 0.0f),
                     table_number(L, idx, "cy", NULL, 0.0f)};
    b2Vec2 points[4] = {{center.x - hx, center.y - hy},
                        {center.x + hx, center.y - hy},
                        {center.x + hx, center.y + hy},
                        {center.x - hx, center.y + hy}};
    return b2MakeOffsetProxy(points, 4, radius, position, b2MakeRot(angle));
  }

  if (strcmp(type, "polygon") == 0) {
    b2Vec2 points[B2_MAX_POLYGON_VERTICES];
    int point_count = read_point_array(L, idx, points, B2_MAX_POLYGON_VERTICES,
                                       "phys2d_shape_cast");
    b2Hull hull = b2ComputeHull(points, point_count);
    if (hull.count < 3)
      luaL_error(L, "phys2d_shape_cast: points must form a convex hull");
    float radius = table_number(L, idx, "radius", "r", 0.0f);
    if (radius < 0.0f)
      luaL_error(L, "phys2d_shape_cast: polygon radius must be >= 0");
    return b2MakeOffsetProxy(hull.points, hull.count, radius, position,
                             b2MakeRot(angle));
  }

  luaL_error(L, "phys2d_shape_cast: unknown shape type '%s'", type);
  return (b2ShapeProxy){0};
}

static b2Vec2 parse_translation(lua_State *L, int idx, const char *fn_name) {
  b2Vec2 translation = {table_number(L, idx, "dx", NULL, 0.0f),
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
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2ShapeProxy proxy = parse_shape_cast_proxy(L, 2);
  b2Vec2 translation = parse_translation(L, 2, "phys2d_shape_cast");
  b2QueryFilter filter = parse_query_filter(L, 2);

  lua_newtable(L);
  int results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  int visitor_ref = LUA_NOREF;
  if (lua_isfunction(L, 3)) {
    lua_pushvalue(L, 3);
    visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  PhysQueryContext ctx = {.L = L,
                          .results_ref = results_ref,
                          .visitor_ref = visitor_ref,
                          .count = 0,
                          .error = NULL};
  b2TreeStats stats = b2World_CastShape(w->id, &proxy, translation, filter,
                                        raycast_result_callback, &ctx);
  if (ctx.error) {
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    if (visitor_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
    return push_visitor_error(L, "phys2d_shape_cast", ctx.error);
  }

  if (visitor_ref == LUA_NOREF) {
    if (ctx.count == 0) {
      luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
      lua_pushnil(L);
      return 1;
    }
    lua_rawgeti(L, LUA_REGISTRYINDEX, results_ref);
    lua_rawgeti(L, -1, ctx.count);
    set_tree_stats(L, stats);
    lua_remove(L, -2);
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    return 1;
  }

  lua_rawgeti(L, LUA_REGISTRYINDEX, results_ref);
  set_tree_stats(L, stats);
  luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
  luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
  return 1;
}

static b2Capsule parse_mover_capsule(lua_State *L, int idx,
                                     const char *fn_name) {
  b2Capsule mover;
  mover.center1 = (b2Vec2){table_number(L, idx, "ax", "x1", 0.0f),
                           table_number(L, idx, "ay", "y1", 0.0f)};
  mover.center2 = (b2Vec2){table_number(L, idx, "bx", "x2", 0.0f),
                           table_number(L, idx, "by", "y2", 0.0f)};
  mover.radius = table_number(L, idx, "r", "radius", 0.0f);
  float dx = mover.center2.x - mover.center1.x;
  float dy = mover.center2.y - mover.center1.y;
  if (dx * dx + dy * dy <= 1e-12f)
    luaL_error(L, "%s: mover endpoints must be distinct", fn_name);
  if (mover.radius <= 0.011f)
    luaL_error(L, "%s: mover radius must be > 0.011", fn_name);
  return mover;
}

static int l_phys2d_cast_mover(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2Capsule mover = parse_mover_capsule(L, 2, "phys2d_cast_mover");
  b2Vec2 translation = parse_translation(L, 2, "phys2d_cast_mover");
  b2QueryFilter filter = parse_query_filter(L, 2);
  float fraction = b2World_CastMover(w->id, &mover, translation, filter);

  lua_newtable(L);
  lua_pushnumber(L, fraction);
  lua_setfield(L, -2, "fraction");
  lua_pushnumber(L, translation.x * fraction);
  lua_setfield(L, -2, "dx");
  lua_pushnumber(L, translation.y * fraction);
  lua_setfield(L, -2, "dy");
  return 1;
}

typedef struct PhysMoverContext {
  lua_State *L;
  int results_ref;
  int visitor_ref;
  int count;
  char *error;
} PhysMoverContext;

static void push_mover_plane(lua_State *L, b2ShapeId shape_id,
                             const b2PlaneResult *plane) {
  push_shape_id_view(L, shape_id);
  lua_pushboolean(L, plane->hit);
  lua_setfield(L, -2, "hit");
  lua_pushnumber(L, plane->point.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, plane->point.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, plane->plane.normal.x);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, plane->plane.normal.y);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, plane->plane.offset);
  lua_setfield(L, -2, "offset");
}

static bool mover_plane_callback(b2ShapeId shape_id, const b2PlaneResult *plane,
                                 void *context) {
  PhysMoverContext *ctx = (PhysMoverContext *)context;
  lua_State *L = ctx->L;
  if (ctx->error)
    return false;

  lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->results_ref);
  push_mover_plane(L, shape_id, plane);
  bool include = true;
  bool keep_going = true;

  if (ctx->visitor_ref != LUA_NOREF) {
    lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->visitor_ref);
    lua_pushvalue(L, -2);
    g_phys_callback_depth++;
    int status = lua_pcall(L, 1, 1, 0);
    g_phys_callback_depth--;
    if (status != LUA_OK) {
      ctx->error = phys_strdup(lua_tostring(L, -1));
      lua_pop(L, 3);
      return false;
    }
    keep_going = parse_overlap_visitor_result(L, -1, &include);
    lua_pop(L, 1);
  }

  if (include) {
    lua_rawseti(L, -2, ++ctx->count);
  } else {
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  return keep_going;
}

static int l_phys2d_collide_mover(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2Capsule mover = parse_mover_capsule(L, 2, "phys2d_collide_mover");
  b2QueryFilter filter = parse_query_filter(L, 2);

  lua_newtable(L);
  int results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  int visitor_ref = LUA_NOREF;
  if (lua_isfunction(L, 3)) {
    lua_pushvalue(L, 3);
    visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  PhysMoverContext ctx = {.L = L,
                          .results_ref = results_ref,
                          .visitor_ref = visitor_ref,
                          .count = 0,
                          .error = NULL};
  b2World_CollideMover(w->id, &mover, filter, mover_plane_callback, &ctx);
  if (ctx.error) {
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    if (visitor_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
    return push_visitor_error(L, "phys2d_collide_mover", ctx.error);
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, results_ref);
  luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
  if (visitor_ref != LUA_NOREF)
    luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
  return 1;
}

static int l_phys2d_explode(lua_State *L) {
  if (phys_in_callback(L, "phys2d_explode"))
    return 0;
  PhysWorld *w = check_world(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE);
  b2ExplosionDef def = b2DefaultExplosionDef();
  def.position = (b2Vec2){table_number(L, 2, "x", NULL, 0.0f),
                          table_number(L, 2, "y", NULL, 0.0f)};
  def.position = table_vec2(L, 2, "position", "center", def.position);
  def.radius = table_number(L, 2, "radius", "r", def.radius);
  def.falloff = table_number(L, 2, "falloff", NULL, def.falloff);
  def.impulsePerLength = table_number(L, 2, "impulse_per_length",
                                      "impulsePerLength", def.impulsePerLength);
  def.impulsePerLength =
      table_number(L, 2, "impulse", NULL, def.impulsePerLength);
  def.maskBits = parse_query_filter(L, 2).maskBits;
  if (def.radius < 0.0f)
    return luaL_error(L, "phys2d_explode: radius must be >= 0");
  if (def.falloff < 0.0f)
    return luaL_error(L, "phys2d_explode: falloff must be >= 0");
  b2World_Explode(w->id, &def);
  return 0;
}

static bool debug_array_reserve(PhysDebugArray *array, int add) {
  if (array->count + add <= array->cap)
    return true;
  int new_cap = array->cap ? array->cap * 2 : 256;
  while (new_cap < array->count + add)
    new_cap *= 2;
  float *new_items =
      (float *)SDL_realloc(array->items, sizeof(*array->items) * new_cap);
  if (!new_items)
    return false;
  array->items = new_items;
  array->cap = new_cap;
  return true;
}

static bool debug_array_push(PhysDebugBuffer *buffer, PhysDebugArray *array,
                             float v) {
  if (!debug_array_reserve(array, 1)) {
    buffer->failed = true;
    return false;
  }
  array->items[array->count++] = v;
  return true;
}

static bool debug_array_push_color(PhysDebugBuffer *buffer,
                                   PhysDebugArray *array, b2HexColor color,
                                   float alpha) {
  uint32_t rgb = (uint32_t)color;
  float r = (float)((rgb >> 16) & 0xffu) / 255.0f;
  float g = (float)((rgb >> 8) & 0xffu) / 255.0f;
  float b = (float)(rgb & 0xffu) / 255.0f;
  return debug_array_push(buffer, array, r) &&
         debug_array_push(buffer, array, g) &&
         debug_array_push(buffer, array, b) &&
         debug_array_push(buffer, array, alpha);
}

static bool debug_push_segment(PhysDebugBuffer *buffer, b2Vec2 a, b2Vec2 b,
                               b2HexColor color) {
  PhysDebugArray *out = &buffer->segments;
  return debug_array_push(buffer, out, a.x) &&
         debug_array_push(buffer, out, a.y) &&
         debug_array_push(buffer, out, b.x) &&
         debug_array_push(buffer, out, b.y) &&
         debug_array_push_color(buffer, out, color, 1.0f);
}

static bool debug_push_circle(PhysDebugBuffer *buffer, b2Vec2 center,
                              float radius, b2HexColor color, float alpha) {
  PhysDebugArray *out = &buffer->circles;
  return debug_array_push(buffer, out, center.x) &&
         debug_array_push(buffer, out, center.y) &&
         debug_array_push(buffer, out, radius) &&
         debug_array_push_color(buffer, out, color, alpha);
}

static bool debug_push_capsule(PhysDebugBuffer *buffer, b2Vec2 a, b2Vec2 b,
                               float radius, b2HexColor color, float alpha) {
  PhysDebugArray *out = &buffer->capsules;
  return debug_array_push(buffer, out, a.x) &&
         debug_array_push(buffer, out, a.y) &&
         debug_array_push(buffer, out, b.x) &&
         debug_array_push(buffer, out, b.y) &&
         debug_array_push(buffer, out, radius) &&
         debug_array_push_color(buffer, out, color, alpha);
}

static bool debug_push_polygon(PhysDebugBuffer *buffer, b2Transform transform,
                               const b2Vec2 *vertices, int vertex_count,
                               bool solid, b2HexColor color) {
  if (vertex_count <= 0)
    return true;
  PhysDebugArray *out = &buffer->polygons;
  if (!debug_array_reserve(out, 6 + vertex_count * 2)) {
    buffer->failed = true;
    return false;
  }
  bool ok = debug_array_push(buffer, out, (float)vertex_count) &&
            debug_array_push(buffer, out, solid ? 1.0f : 0.0f) &&
            debug_array_push_color(buffer, out, color, solid ? 0.55f : 1.0f);
  for (int i = 0; i < vertex_count && ok; ++i) {
    b2Vec2 p = b2TransformPoint(transform, vertices[i]);
    ok = debug_array_push(buffer, out, p.x) &&
         debug_array_push(buffer, out, p.y);
  }
  return ok;
}

static bool debug_push_point(PhysDebugBuffer *buffer, b2Vec2 p, float size,
                             b2HexColor color) {
  PhysDebugArray *out = &buffer->points;
  return debug_array_push(buffer, out, p.x) &&
         debug_array_push(buffer, out, p.y) &&
         debug_array_push(buffer, out, size) &&
         debug_array_push_color(buffer, out, color, 1.0f);
}

static void debug_draw_polygon(const b2Vec2 *vertices, int vertex_count,
                               b2HexColor color, void *context) {
  PhysDebugBuffer *buffer = (PhysDebugBuffer *)context;
  debug_push_polygon(buffer, b2Transform_identity, vertices, vertex_count,
                     false, color);
}

static void debug_draw_solid_polygon(b2Transform transform,
                                     const b2Vec2 *vertices, int vertex_count,
                                     float radius, b2HexColor color,
                                     void *context) {
  (void)radius;
  PhysDebugBuffer *buffer = (PhysDebugBuffer *)context;
  debug_push_polygon(buffer, transform, vertices, vertex_count, true, color);
}

static void debug_draw_circle(b2Vec2 center, float radius, b2HexColor color,
                              void *context) {
  PhysDebugBuffer *buffer = (PhysDebugBuffer *)context;
  debug_push_circle(buffer, center, radius, color, 1.0f);
}

static void debug_draw_solid_circle(b2Transform transform, float radius,
                                    b2HexColor color, void *context) {
  PhysDebugBuffer *buffer = (PhysDebugBuffer *)context;
  debug_push_circle(buffer, transform.p, radius, color, 0.55f);
}

static void debug_draw_solid_capsule(b2Vec2 p1, b2Vec2 p2, float radius,
                                     b2HexColor color, void *context) {
  PhysDebugBuffer *buffer = (PhysDebugBuffer *)context;
  debug_push_capsule(buffer, p1, p2, radius, color, 0.55f);
}

static void debug_draw_segment(b2Vec2 p1, b2Vec2 p2, b2HexColor color,
                               void *context) {
  PhysDebugBuffer *buffer = (PhysDebugBuffer *)context;
  debug_push_segment(buffer, p1, p2, color);
}

static void debug_draw_transform(b2Transform transform, void *context) {
  PhysDebugBuffer *buffer = (PhysDebugBuffer *)context;
  float k = 0.25f;
  b2Vec2 x_axis = {transform.p.x + k * transform.q.c,
                   transform.p.y + k * transform.q.s};
  b2Vec2 y_axis = {transform.p.x - k * transform.q.s,
                   transform.p.y + k * transform.q.c};
  debug_push_segment(buffer, transform.p, x_axis, b2_colorRed);
  debug_push_segment(buffer, transform.p, y_axis, b2_colorGreen);
}

static void debug_draw_point(b2Vec2 p, float size, b2HexColor color,
                             void *context) {
  PhysDebugBuffer *buffer = (PhysDebugBuffer *)context;
  debug_push_point(buffer, p, size, color);
}

static void debug_free(PhysDebugBuffer *buffer) {
  SDL_free(buffer->segments.items);
  SDL_free(buffer->circles.items);
  SDL_free(buffer->capsules.items);
  SDL_free(buffer->polygons.items);
  SDL_free(buffer->points.items);
  memset(buffer, 0, sizeof(*buffer));
}

static void push_debug_array(lua_State *L, const PhysDebugArray *array) {
  lua_newtable(L);
  for (int i = 0; i < array->count; ++i) {
    lua_pushnumber(L, array->items[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static int l_phys2d_debug(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  b2DebugDraw draw = b2DefaultDebugDraw();
  draw.DrawPolygonFcn = debug_draw_polygon;
  draw.DrawSolidPolygonFcn = debug_draw_solid_polygon;
  draw.DrawCircleFcn = debug_draw_circle;
  draw.DrawSolidCircleFcn = debug_draw_solid_circle;
  draw.DrawSolidCapsuleFcn = debug_draw_solid_capsule;
  draw.DrawSegmentFcn = debug_draw_segment;
  draw.DrawTransformFcn = debug_draw_transform;
  draw.DrawPointFcn = debug_draw_point;
  draw.drawShapes = true;

  if (lua_istable(L, 2)) {
    draw.drawShapes = table_bool(L, 2, "shapes", NULL, draw.drawShapes);
    draw.drawJoints = table_bool(L, 2, "joints", NULL, false);
    draw.drawJointExtras =
        table_bool(L, 2, "joint_extras", "jointExtras", false);
    draw.drawBounds = table_bool(L, 2, "bounds", NULL, false);
    draw.drawMass = table_bool(L, 2, "mass", NULL, false);
    draw.drawBodyNames = table_bool(L, 2, "body_names", "bodyNames", false);
    draw.drawContacts = table_bool(L, 2, "contacts", NULL, false);
    draw.drawGraphColors =
        table_bool(L, 2, "graph_colors", "graphColors", false);
    draw.drawContactNormals =
        table_bool(L, 2, "contact_normals", "contactNormals", false);
    draw.drawContactImpulses =
        table_bool(L, 2, "contact_impulses", "contactImpulses", false);
    draw.drawContactFeatures =
        table_bool(L, 2, "contact_features", "contactFeatures", false);
    draw.drawFrictionImpulses =
        table_bool(L, 2, "friction_impulses", "frictionImpulses", false);
    draw.drawIslands = table_bool(L, 2, "islands", NULL, false);
    if (table_get_any(L, 2, "drawing_bounds", "drawingBounds")) {
      if (lua_istable(L, -1)) {
        int b = lua_gettop(L);
        draw.drawingBounds.lowerBound.x =
            table_number(L, b, "min_x", "minX", -FLT_MAX);
        draw.drawingBounds.lowerBound.y =
            table_number(L, b, "min_y", "minY", -FLT_MAX);
        draw.drawingBounds.upperBound.x =
            table_number(L, b, "max_x", "maxX", FLT_MAX);
        draw.drawingBounds.upperBound.y =
            table_number(L, b, "max_y", "maxY", FLT_MAX);
        draw.useDrawingBounds = true;
      }
      lua_pop(L, 1);
    }
  }

  PhysDebugBuffer buffer = {0};
  draw.context = &buffer;
  b2World_Draw(w->id, &draw);
  if (buffer.failed) {
    debug_free(&buffer);
    return luaL_error(L, "phys2d_debug: out of memory");
  }

  lua_newtable(L);
  push_debug_array(L, &buffer.segments);
  lua_setfield(L, -2, "segments");
  push_debug_array(L, &buffer.circles);
  lua_setfield(L, -2, "circles");
  push_debug_array(L, &buffer.capsules);
  lua_setfield(L, -2, "capsules");
  push_debug_array(L, &buffer.polygons);
  lua_setfield(L, -2, "polygons");
  push_debug_array(L, &buffer.points);
  lua_setfield(L, -2, "points");
  debug_free(&buffer);
  return 1;
}

static void set_profile_number(lua_State *L, const char *key, float value) {
  lua_pushnumber(L, value);
  lua_setfield(L, -2, key);
}

static int l_phys2d_profile(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  b2Profile p = b2World_GetProfile(w->id);
  lua_newtable(L);
  set_profile_number(L, "step", p.step);
  set_profile_number(L, "pairs", p.pairs);
  set_profile_number(L, "collide", p.collide);
  set_profile_number(L, "solve", p.solve);
  set_profile_number(L, "merge_islands", p.mergeIslands);
  set_profile_number(L, "prepare_stages", p.prepareStages);
  set_profile_number(L, "solve_constraints", p.solveConstraints);
  set_profile_number(L, "prepare_constraints", p.prepareConstraints);
  set_profile_number(L, "integrate_velocities", p.integrateVelocities);
  set_profile_number(L, "warm_start", p.warmStart);
  set_profile_number(L, "solve_impulses", p.solveImpulses);
  set_profile_number(L, "integrate_positions", p.integratePositions);
  set_profile_number(L, "relax_impulses", p.relaxImpulses);
  set_profile_number(L, "apply_restitution", p.applyRestitution);
  set_profile_number(L, "store_impulses", p.storeImpulses);
  set_profile_number(L, "split_islands", p.splitIslands);
  set_profile_number(L, "transforms", p.transforms);
  set_profile_number(L, "hit_events", p.hitEvents);
  set_profile_number(L, "refit", p.refit);
  set_profile_number(L, "bullets", p.bullets);
  set_profile_number(L, "sleep_islands", p.sleepIslands);
  set_profile_number(L, "sensors", p.sensors);
  return 1;
}

static void set_counter_integer(lua_State *L, const char *key, int value) {
  lua_pushinteger(L, value);
  lua_setfield(L, -2, key);
}

static int l_phys2d_counters(lua_State *L) {
  PhysWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  b2Counters c = b2World_GetCounters(w->id);
  lua_newtable(L);
  set_counter_integer(L, "body_count", c.bodyCount);
  set_counter_integer(L, "shape_count", c.shapeCount);
  set_counter_integer(L, "contact_count", c.contactCount);
  set_counter_integer(L, "joint_count", c.jointCount);
  set_counter_integer(L, "island_count", c.islandCount);
  set_counter_integer(L, "stack_used", c.stackUsed);
  set_counter_integer(L, "static_tree_height", c.staticTreeHeight);
  set_counter_integer(L, "tree_height", c.treeHeight);
  set_counter_integer(L, "byte_count", c.byteCount);
  set_counter_integer(L, "task_count", c.taskCount);
  lua_newtable(L);
  for (int i = 0; i < 12; ++i) {
    lua_pushinteger(L, c.colorCounts[i]);
    lua_rawseti(L, -2, i + 1);
  }
  lua_setfield(L, -2, "color_counts");
  return 1;
}

void phys2d_lua_register(lua_State *L) {
  lua_pushinteger(L, PHYS2D_STATIC);
  lua_setglobal(L, "STATIC");
  lua_pushinteger(L, PHYS2D_KINEMATIC);
  lua_setglobal(L, "KINEMATIC");
  lua_pushinteger(L, PHYS2D_DYNAMIC);
  lua_setglobal(L, "DYNAMIC");

  lua_pushcfunction(L, l_phys2d_world);
  lua_setglobal(L, "phys2d_world");
  lua_pushcfunction(L, l_phys2d_begin);
  lua_setglobal(L, "phys2d_begin");
  lua_pushcfunction(L, l_phys2d_world_info);
  lua_setglobal(L, "phys2d_world_info");
  lua_pushcfunction(L, l_phys2d_body);
  lua_setglobal(L, "phys2d_body");
  lua_pushcfunction(L, l_phys2d_box);
  lua_setglobal(L, "phys2d_box");
  lua_pushcfunction(L, l_phys2d_circle);
  lua_setglobal(L, "phys2d_circle");
  lua_pushcfunction(L, l_phys2d_capsule);
  lua_setglobal(L, "phys2d_capsule");
  lua_pushcfunction(L, l_phys2d_segment);
  lua_setglobal(L, "phys2d_segment");
  lua_pushcfunction(L, l_phys2d_polygon);
  lua_setglobal(L, "phys2d_polygon");
  lua_pushcfunction(L, l_phys2d_chain);
  lua_setglobal(L, "phys2d_chain");
  lua_pushcfunction(L, l_phys2d_chain_segments);
  lua_setglobal(L, "phys2d_chain_segments");
  lua_pushcfunction(L, l_phys2d_joint);
  lua_setglobal(L, "phys2d_joint");
  lua_pushcfunction(L, l_phys2d_joint_info);
  lua_setglobal(L, "phys2d_joint_info");
  lua_pushcfunction(L, l_phys2d_joint_force);
  lua_setglobal(L, "phys2d_joint_force");
  lua_pushcfunction(L, l_phys2d_joint_torque);
  lua_setglobal(L, "phys2d_joint_torque");
  lua_pushcfunction(L, l_phys2d_joint_angle);
  lua_setglobal(L, "phys2d_joint_angle");
  lua_pushcfunction(L, l_phys2d_joint_translation);
  lua_setglobal(L, "phys2d_joint_translation");
  lua_pushcfunction(L, l_phys2d_joint_speed);
  lua_setglobal(L, "phys2d_joint_speed");
  lua_pushcfunction(L, l_phys2d_joint_length);
  lua_setglobal(L, "phys2d_joint_length");
  lua_pushcfunction(L, l_phys2d_joint_motor_force);
  lua_setglobal(L, "phys2d_joint_motor_force");
  lua_pushcfunction(L, l_phys2d_joint_motor_torque);
  lua_setglobal(L, "phys2d_joint_motor_torque");
  lua_pushcfunction(L, l_phys2d_joint_set_motor);
  lua_setglobal(L, "phys2d_joint_set_motor");
  lua_pushcfunction(L, l_phys2d_joint_set_limit);
  lua_setglobal(L, "phys2d_joint_set_limit");
  lua_pushcfunction(L, l_phys2d_joint_set_spring);
  lua_setglobal(L, "phys2d_joint_set_spring");
  lua_pushcfunction(L, l_phys2d_joint_set_target);
  lua_setglobal(L, "phys2d_joint_set_target");
  lua_pushcfunction(L, l_phys2d_step);
  lua_setglobal(L, "phys2d_step");
  lua_pushcfunction(L, l_phys2d_pose);
  lua_setglobal(L, "phys2d_pose");
  lua_pushcfunction(L, l_phys2d_velocity);
  lua_setglobal(L, "phys2d_velocity");
  lua_pushcfunction(L, l_phys2d_mass);
  lua_setglobal(L, "phys2d_mass");
  lua_pushcfunction(L, l_phys2d_center);
  lua_setglobal(L, "phys2d_center");
  lua_pushcfunction(L, l_phys2d_world_point);
  lua_setglobal(L, "phys2d_world_point");
  lua_pushcfunction(L, l_phys2d_local_point);
  lua_setglobal(L, "phys2d_local_point");
  lua_pushcfunction(L, l_phys2d_velocity_at);
  lua_setglobal(L, "phys2d_velocity_at");
  lua_pushcfunction(L, l_phys2d_body_shapes);
  lua_setglobal(L, "phys2d_body_shapes");
  lua_pushcfunction(L, l_phys2d_body_joints);
  lua_setglobal(L, "phys2d_body_joints");
  lua_pushcfunction(L, l_phys2d_body_contacts);
  lua_setglobal(L, "phys2d_body_contacts");
  lua_pushcfunction(L, l_phys2d_shape_test_point);
  lua_setglobal(L, "phys2d_shape_test_point");
  lua_pushcfunction(L, l_phys2d_shape_raycast);
  lua_setglobal(L, "phys2d_shape_raycast");
  lua_pushcfunction(L, l_phys2d_shape_closest_point);
  lua_setglobal(L, "phys2d_shape_closest_point");
  lua_pushcfunction(L, l_phys2d_shape_aabb);
  lua_setglobal(L, "phys2d_shape_aabb");
  lua_pushcfunction(L, l_phys2d_shape_info);
  lua_setglobal(L, "phys2d_shape_info");
  lua_pushcfunction(L, l_phys2d_shape_set_material);
  lua_setglobal(L, "phys2d_shape_set_material");
  lua_pushcfunction(L, l_phys2d_shape_set_filter);
  lua_setglobal(L, "phys2d_shape_set_filter");
  lua_pushcfunction(L, l_phys2d_shape_set_events);
  lua_setglobal(L, "phys2d_shape_set_events");
  lua_pushcfunction(L, l_phys2d_contacts);
  lua_setglobal(L, "phys2d_contacts");
  lua_pushcfunction(L, l_phys2d_body_events);
  lua_setglobal(L, "phys2d_body_events");
  lua_pushcfunction(L, l_phys2d_sensors);
  lua_setglobal(L, "phys2d_sensors");
  lua_pushcfunction(L, l_phys2d_raycast);
  lua_setglobal(L, "phys2d_raycast");
  lua_pushcfunction(L, l_phys2d_overlap_aabb);
  lua_setglobal(L, "phys2d_overlap_aabb");
  lua_pushcfunction(L, l_phys2d_shape_cast);
  lua_setglobal(L, "phys2d_shape_cast");
  lua_pushcfunction(L, l_phys2d_cast_mover);
  lua_setglobal(L, "phys2d_cast_mover");
  lua_pushcfunction(L, l_phys2d_collide_mover);
  lua_setglobal(L, "phys2d_collide_mover");
  lua_pushcfunction(L, l_phys2d_explode);
  lua_setglobal(L, "phys2d_explode");
  lua_pushcfunction(L, l_phys2d_debug);
  lua_setglobal(L, "phys2d_debug");
  lua_pushcfunction(L, l_phys2d_profile);
  lua_setglobal(L, "phys2d_profile");
  lua_pushcfunction(L, l_phys2d_counters);
  lua_setglobal(L, "phys2d_counters");
  lua_pushcfunction(L, l_phys2d_add_force);
  lua_setglobal(L, "phys2d_add_force");
  lua_pushcfunction(L, l_phys2d_add_force_center);
  lua_setglobal(L, "phys2d_add_force_center");
  lua_pushcfunction(L, l_phys2d_add_impulse);
  lua_setglobal(L, "phys2d_add_impulse");
  lua_pushcfunction(L, l_phys2d_add_impulse_center);
  lua_setglobal(L, "phys2d_add_impulse_center");
  lua_pushcfunction(L, l_phys2d_add_torque);
  lua_setglobal(L, "phys2d_add_torque");
  lua_pushcfunction(L, l_phys2d_add_angular_impulse);
  lua_setglobal(L, "phys2d_add_angular_impulse");
  lua_pushcfunction(L, l_phys2d_set_velocity);
  lua_setglobal(L, "phys2d_set_velocity");
  lua_pushcfunction(L, l_phys2d_teleport);
  lua_setglobal(L, "phys2d_teleport");
  lua_pushcfunction(L, l_phys2d_set_target);
  lua_setglobal(L, "phys2d_set_target");
  lua_pushcfunction(L, l_phys2d_set_mass_data);
  lua_setglobal(L, "phys2d_set_mass_data");
}
