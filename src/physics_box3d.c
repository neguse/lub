#include "physics_box3d.h"

#include <SDL3/SDL.h>
#include <box3d/box3d.h>
#include <box3d/collision.h>
#include <box3d/math_functions.h>
#include <float.h>
#include <lauxlib.h>
#include <math.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#define PHYS3D_BODY_BUCKETS 256
#define PHYS3D_SHAPE_BUCKETS 64
#define PHYS3D_JOINT_BUCKETS 128
#define PHYS3D_TOMBSTONE_BUCKETS 256

typedef enum Phys3dShapeKind {
  PHYS3D_SHAPE_SPHERE = 1,
  PHYS3D_SHAPE_BOX = 2,
  PHYS3D_SHAPE_CAPSULE = 3,
  PHYS3D_SHAPE_CYLINDER = 4,
  PHYS3D_SHAPE_CONE = 5,
  PHYS3D_SHAPE_HULL = 6,
  PHYS3D_SHAPE_MESH = 7,
  PHYS3D_SHAPE_HEIGHT_FIELD = 8,
  PHYS3D_SHAPE_COMPOUND = 9,
} Phys3dShapeKind;

typedef struct Phys3dShape {
  char *key;
  char *tag;
  char *material_name;
  struct Phys3dBody *body;
  b3ShapeId id;
  uint64_t seen_generation;
  uint64_t desc_hash;
  uint64_t constructor_hash;
  bool constructor_warned;
  int material_id;
  Phys3dShapeKind kind;
  // Heavy geometry data referenced (not copied) by the box3d shape; owned by
  // this struct and freed after the box3d shape is destroyed.
  b3MeshData *mesh_data;
  b3HeightFieldData *height_field_data;
  b3CompoundData *compound_data;
  struct Phys3dShape *next;
} Phys3dShape;

typedef struct Phys3dBody {
  char *key;
  struct Phys3dWorld *world;
  b3BodyId id;
  uint64_t seen_generation;
  int64_t version;
  uint64_t constructor_hash;
  bool constructor_warned;
  Phys3dShape *shapes[PHYS3D_SHAPE_BUCKETS];
  struct Phys3dBody *next;
} Phys3dBody;

typedef enum Phys3dJointKind {
  PHYS3D_JOINT_DISTANCE = 1,
  PHYS3D_JOINT_FILTER = 2,
  PHYS3D_JOINT_MOTOR = 3,
  PHYS3D_JOINT_PARALLEL = 4,
  PHYS3D_JOINT_PRISMATIC = 5,
  PHYS3D_JOINT_REVOLUTE = 6,
  PHYS3D_JOINT_SPHERICAL = 7,
  PHYS3D_JOINT_WELD = 8,
  PHYS3D_JOINT_WHEEL = 9,
} Phys3dJointKind;

typedef struct Phys3dJoint {
  char *key;
  struct Phys3dWorld *world;
  Phys3dBody *body_a;
  Phys3dBody *body_b;
  b3JointId id;
  uint64_t seen_generation;
  int64_t version;
  uint64_t constructor_hash;
  bool constructor_warned;
  Phys3dJointKind kind;
  struct Phys3dJoint *next;
} Phys3dJoint;

typedef enum Phys3dCommandKind {
  PHYS3D_COMMAND_ADD_FORCE = 1,
  PHYS3D_COMMAND_ADD_FORCE_CENTER,
  PHYS3D_COMMAND_ADD_IMPULSE,
  PHYS3D_COMMAND_ADD_IMPULSE_CENTER,
  PHYS3D_COMMAND_ADD_TORQUE,
  PHYS3D_COMMAND_ADD_ANGULAR_IMPULSE,
  PHYS3D_COMMAND_SET_VELOCITY,
  PHYS3D_COMMAND_TELEPORT,
  PHYS3D_COMMAND_SET_TARGET,
} Phys3dCommandKind;

typedef struct Phys3dCommand {
  char *body_key;
  uint64_t body_id_key;
  Phys3dCommandKind kind;
  b3Vec3 vector;
  b3Vec3 point;
  b3Vec3 angular;
  b3Transform transform;
  float time_step;
  bool wake;
  bool has_point;
  bool has_x;
  bool has_y;
  bool has_z;
  bool has_wx;
  bool has_wy;
  bool has_wz;
  bool has_rotation;
} Phys3dCommand;

typedef struct Phys3dCommandQueue {
  Phys3dCommand *items;
  int count;
  int cap;
} Phys3dCommandQueue;

typedef struct Phys3dContactSnapshot {
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
  float nz;
  int point_count;
  float x;
  float y;
  float z;
  float approach_speed;
} Phys3dContactSnapshot;

typedef struct Phys3dBodyEventSnapshot {
  char *body;
  bool valid;
  float x;
  float y;
  float z;
  float qx;
  float qy;
  float qz;
  float qw;
  bool fell_asleep;
} Phys3dBodyEventSnapshot;

typedef struct Phys3dJointEventSnapshot {
  char *joint;
  char *body_a;
  char *body_b;
  const char *type;
  bool valid;
} Phys3dJointEventSnapshot;

typedef struct Phys3dEventBuffer {
  Phys3dContactSnapshot *begins;
  int begin_count;
  int begin_cap;
  Phys3dContactSnapshot *ends;
  int end_count;
  int end_cap;
  Phys3dContactSnapshot *hits;
  int hit_count;
  int hit_cap;
  Phys3dContactSnapshot *sensor_begins;
  int sensor_begin_count;
  int sensor_begin_cap;
  Phys3dContactSnapshot *sensor_ends;
  int sensor_end_count;
  int sensor_end_cap;
  Phys3dBodyEventSnapshot *moves;
  int move_count;
  int move_cap;
  Phys3dJointEventSnapshot *joints;
  int joint_count;
  int joint_cap;
} Phys3dEventBuffer;

typedef struct Phys3dShapeTombstone {
  uint64_t id_key;
  char *body;
  char *shape;
  char *tag;
  char *material;
  int material_id;
  struct Phys3dShapeTombstone *next;
} Phys3dShapeTombstone;

typedef struct Phys3dCallbacks {
  lua_State *L;
  int filter_ref;
  int pre_solve_ref;
  int friction_ref;
  int restitution_ref;
  bool filter_error_logged;
  bool pre_solve_error_logged;
  bool friction_error_logged;
  bool restitution_error_logged;
} Phys3dCallbacks;

struct Phys3dWorld {
  char *key;
  b3WorldId id;
  double accumulator;
  float fixed_dt;
  int substeps;
  int max_steps;
  uint64_t generation;
  bool begun;
  bool prune;
  bool step_without_begin_logged;
  int64_t version;
  Phys3dBody *bodies[PHYS3D_BODY_BUCKETS];
  Phys3dJoint *joints[PHYS3D_JOINT_BUCKETS];
  Phys3dShapeTombstone *shape_tombstones[PHYS3D_TOMBSTONE_BUCKETS];
  Phys3dEventBuffer events;
  Phys3dCommandQueue commands;
  Phys3dCallbacks callbacks;
  bool callbacks_pending;
  uint64_t callbacks_generation;
  struct Phys3dWorld *next;
};

typedef struct Phys3dWorldOpts {
  int64_t version;
  bool has_version;
  b3Vec3 gravity;
  float fixed_dt;
  int substeps;
  int max_steps;
  bool sleep;
  bool continuous;
  bool has_hit_event_threshold;
  float hit_event_threshold;
} Phys3dWorldOpts;

typedef struct Phys3dBodyDesc {
  int64_t version;
  bool has_version;
  b3BodyType type;
  b3MotionLocks motion_locks;
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
  b3Vec3 initial_pos;
  b3Quat initial_rot;
  b3Vec3 initial_vel;
  b3Vec3 initial_w;
  bool initial_awake;
} Phys3dBodyDesc;

typedef struct Phys3dShapeDesc {
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
} Phys3dShapeDesc;

typedef struct Phys3dJointDesc {
  int64_t version;
  bool has_version;
  Phys3dJointKind kind;
  Phys3dBody *body_a;
  Phys3dBody *body_b;
  b3Transform local_frame_a;
  b3Transform local_frame_b;
  // Raw anchor/axis/frame inputs; the constructor hash covers these instead
  // of the derived local frames so world-space anchors on moving bodies do
  // not recreate the joint every declare.
  bool has_axis;
  b3Vec3 axis;
  bool has_anchor_a;
  bool has_anchor_b;
  b3Vec3 anchor_a;
  b3Vec3 anchor_b;
  bool has_frame_a;
  bool has_frame_b;
  float force_threshold;
  float torque_threshold;
  bool has_constraint_tuning;
  float constraint_hertz;
  float constraint_damping_ratio;
  bool collide_connected;
  float length;
  float min_length;
  float max_length;
  float lower;
  float upper;
  float hertz;
  float damping_ratio;
  float linear_hertz;
  float angular_hertz;
  float linear_damping_ratio;
  float angular_damping_ratio;
  float max_force;
  float max_torque;
  float motor_speed;
  float target_angle;
  float target_translation;
  bool enable_spring;
  bool enable_limit;
  bool enable_motor;
  float lower_spring_force;
  float upper_spring_force;
  b3Vec3 linear_velocity;
  b3Vec3 angular_velocity;
  float max_velocity_force;
  float max_velocity_torque;
  float max_spring_force;
  float max_spring_torque;
  b3Quat target_rotation;
  bool enable_cone_limit;
  float cone_angle;
  bool enable_twist_limit;
  float lower_twist_angle;
  float upper_twist_angle;
  b3Vec3 motor_velocity;
  bool enable_steering;
  float steering_hertz;
  float steering_damping_ratio;
  float target_steering_angle;
  float max_steering_torque;
  bool enable_steering_limit;
  float lower_steering_limit;
  float upper_steering_limit;
} Phys3dJointDesc;

static Phys3dState *g_phys3d_state = NULL;
static Phys3dWorld *g_phys3d_mixer_world = NULL;
static int g_phys3d_callback_depth = 0;

static bool body_is_live(Phys3dBody *b);
static bool shape_is_live(Phys3dShape *s);
static bool joint_is_live(Phys3dJoint *j);
static bool phys3d_custom_filter_callback(b3ShapeId shape_id_a,
                                          b3ShapeId shape_id_b, void *context);
static bool phys3d_pre_solve_callback(b3ShapeId shape_id_a,
                                      b3ShapeId shape_id_b, b3Pos point,
                                      b3Vec3 normal, void *context);
static float phys3d_friction_callback(float friction_a, uint64_t material_a,
                                      float friction_b, uint64_t material_b);
static float phys3d_restitution_callback(float restitution_a,
                                         uint64_t material_a,
                                         float restitution_b,
                                         uint64_t material_b);
static void push_shape_id_view(lua_State *L, b3ShapeId shape_id);

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

static b3Vec3 value_vec3(lua_State *L, int idx, b3Vec3 def) {
  b3Vec3 out = def;
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
  lua_getfield(L, idx, "z");
  if (lua_isnumber(L, -1))
    out.z = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 1);
  if (lua_isnumber(L, -1))
    out.x = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 2);
  if (lua_isnumber(L, -1))
    out.y = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 3);
  if (lua_isnumber(L, -1))
    out.z = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return out;
}

static b3Vec3 value_vec3_optional(lua_State *L, int idx, b3Vec3 def,
                                  bool *has_x, bool *has_y, bool *has_z) {
  b3Vec3 out = def;
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
  lua_getfield(L, idx, "z");
  if (lua_isnumber(L, -1)) {
    out.z = (float)lua_tonumber(L, -1);
    *has_z = true;
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
  lua_rawgeti(L, idx, 3);
  if (lua_isnumber(L, -1)) {
    out.z = (float)lua_tonumber(L, -1);
    *has_z = true;
  }
  lua_pop(L, 1);
  return out;
}

static b3Vec3 table_vec3(lua_State *L, int idx, const char *a, const char *b,
                         b3Vec3 def) {
  b3Vec3 out = def;
  if (!table_get_any(L, idx, a, b))
    return out;
  if (lua_istable(L, -1))
    out = value_vec3(L, lua_gettop(L), def);
  lua_pop(L, 1);
  return out;
}

static b3Quat value_quat(lua_State *L, int idx, b3Quat def) {
  b3Quat out = def;
  if (!lua_istable(L, idx))
    return out;
  idx = abs_index(L, idx);
  lua_getfield(L, idx, "x");
  if (lua_isnumber(L, -1))
    out.v.x = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, idx, "y");
  if (lua_isnumber(L, -1))
    out.v.y = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, idx, "z");
  if (lua_isnumber(L, -1))
    out.v.z = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, idx, "w");
  if (lua_isnumber(L, -1))
    out.s = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 1);
  if (lua_isnumber(L, -1))
    out.v.x = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 2);
  if (lua_isnumber(L, -1))
    out.v.y = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 3);
  if (lua_isnumber(L, -1))
    out.v.z = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_rawgeti(L, idx, 4);
  if (lua_isnumber(L, -1))
    out.s = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return b3NormalizeQuat(out);
}

// Box3D has no euler helper; compose per-axis quats in XYZ intrinsic order.
static b3Quat quat_from_euler_xyz(b3Vec3 euler) {
  b3Quat qx = b3MakeQuatFromAxisAngle(b3Vec3_axisX, euler.x);
  b3Quat qy = b3MakeQuatFromAxisAngle(b3Vec3_axisY, euler.y);
  b3Quat qz = b3MakeQuatFromAxisAngle(b3Vec3_axisZ, euler.z);
  return b3NormalizeQuat(b3MulQuat(b3MulQuat(qx, qy), qz));
}

static bool table_rotation(lua_State *L, int idx, b3Quat *out) {
  idx = abs_index(L, idx);
  if (table_get_any(L, idx, "quat", NULL)) {
    bool ok = lua_istable(L, -1);
    if (ok)
      *out = value_quat(L, lua_gettop(L), b3Quat_identity);
    lua_pop(L, 1);
    if (ok)
      return true;
  }
  if (table_get_any(L, idx, "euler", NULL)) {
    bool ok = lua_istable(L, -1);
    if (ok)
      *out = quat_from_euler_xyz(value_vec3(L, lua_gettop(L), b3Vec3_zero));
    lua_pop(L, 1);
    if (ok)
      return true;
  }
  return false;
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
  if (g_phys3d_callback_depth <= 0)
    return false;
  luaL_error(L, "%s: physics mutation is not allowed inside phys3d callback",
             fn);
  return true;
}

static bool callback_ref_is_set(int ref) {
  return ref != LUA_NOREF && ref != LUA_REFNIL;
}

static void callbacks_init(Phys3dCallbacks *callbacks) {
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

static bool callbacks_any(const Phys3dCallbacks *callbacks) {
  return callback_ref_is_set(callbacks->filter_ref) ||
         callback_ref_is_set(callbacks->pre_solve_ref) ||
         callback_ref_is_set(callbacks->friction_ref) ||
         callback_ref_is_set(callbacks->restitution_ref);
}

static void callbacks_install(Phys3dWorld *w) {
  if (!w || B3_IS_NULL(w->id) || !b3World_IsValid(w->id))
    return;
  b3World_SetCustomFilterCallback(
      w->id,
      callback_ref_is_set(w->callbacks.filter_ref)
          ? phys3d_custom_filter_callback
          : NULL,
      callback_ref_is_set(w->callbacks.filter_ref) ? w : NULL);
  b3World_SetPreSolveCallback(
      w->id,
      callback_ref_is_set(w->callbacks.pre_solve_ref)
          ? phys3d_pre_solve_callback
          : NULL,
      callback_ref_is_set(w->callbacks.pre_solve_ref) ? w : NULL);
  b3World_SetFrictionCallback(w->id,
                              callback_ref_is_set(w->callbacks.friction_ref)
                                  ? phys3d_friction_callback
                                  : NULL);
  b3World_SetRestitutionCallback(
      w->id, callback_ref_is_set(w->callbacks.restitution_ref)
                 ? phys3d_restitution_callback
                 : NULL);
}

static void callbacks_clear(lua_State *L, Phys3dWorld *w) {
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

static void callbacks_replace_from_opts(lua_State *L, Phys3dWorld *w,
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
static int l_phys3d_body_joints(lua_State *L);
static int l_phys3d_cast_mover(lua_State *L);
static int l_phys3d_collide_mover(lua_State *L);
static int l_phys3d_pose(lua_State *L);
static int l_phys3d_velocity(lua_State *L);
static int l_phys3d_mass(lua_State *L);
static int l_phys3d_center(lua_State *L);
static int l_phys3d_world_point(lua_State *L);
static int l_phys3d_local_point(lua_State *L);
static int l_phys3d_velocity_at(lua_State *L);
static int l_phys3d_add_force(lua_State *L);
static int l_phys3d_add_force_center(lua_State *L);
static int l_phys3d_add_impulse(lua_State *L);
static int l_phys3d_add_impulse_center(lua_State *L);
static int l_phys3d_add_torque(lua_State *L);
static int l_phys3d_add_angular_impulse(lua_State *L);
static int l_phys3d_set_velocity(lua_State *L);
static int l_phys3d_teleport(lua_State *L);
static int l_phys3d_set_target(lua_State *L);
static int l_phys3d_body_shapes(lua_State *L);
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
static int l_phys3d_profile(lua_State *L);
static int l_phys3d_counters(lua_State *L);

static void push_world_ref(lua_State *L, const char *key) {
  lua_newtable(L);
  lua_pushstring(L, "phys3d_world");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, key);
  lua_setfield(L, -2, "key");
  set_cfunc_field(L, "begin", l_phys3d_begin);
  set_cfunc_field(L, "step", l_phys3d_step);
  set_cfunc_field(L, "info", l_phys3d_world_info);
  set_cfunc_field(L, "body", l_phys3d_body);
  set_cfunc_field(L, "joint", l_phys3d_joint);
  set_cfunc_field(L, "pose", l_phys3d_pose);
  set_cfunc_field(L, "cast_mover", l_phys3d_cast_mover);
  set_cfunc_field(L, "collide_mover", l_phys3d_collide_mover);
  set_cfunc_field(L, "contacts", l_phys3d_contacts);
  set_cfunc_field(L, "body_events", l_phys3d_body_events);
  set_cfunc_field(L, "sensors", l_phys3d_sensors);
  set_cfunc_field(L, "joint_events", l_phys3d_joint_events);
  set_cfunc_field(L, "raycast", l_phys3d_raycast);
  set_cfunc_field(L, "overlap_aabb", l_phys3d_overlap_aabb);
  set_cfunc_field(L, "overlap_shape", l_phys3d_overlap_shape);
  set_cfunc_field(L, "shape_cast", l_phys3d_shape_cast);
  set_cfunc_field(L, "profile", l_phys3d_profile);
  set_cfunc_field(L, "counters", l_phys3d_counters);
}

static void push_body_ref(lua_State *L, const char *world_key,
                          const char *body_key) {
  lua_newtable(L);
  lua_pushstring(L, "phys3d_body");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, world_key);
  lua_setfield(L, -2, "world");
  lua_pushstring(L, body_key);
  lua_setfield(L, -2, "key");
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

static void push_shape_ref(lua_State *L, const char *world_key,
                           const char *body_key, const char *shape_key) {
  lua_newtable(L);
  lua_pushstring(L, "phys3d_shape");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, world_key);
  lua_setfield(L, -2, "world");
  lua_pushstring(L, body_key);
  lua_setfield(L, -2, "body");
  lua_pushstring(L, shape_key);
  lua_setfield(L, -2, "key");
  set_cfunc_field(L, "raycast", l_phys3d_shape_raycast);
  set_cfunc_field(L, "closest_point", l_phys3d_shape_closest_point);
  set_cfunc_field(L, "aabb", l_phys3d_shape_aabb);
  set_cfunc_field(L, "info", l_phys3d_shape_info);
  set_cfunc_field(L, "set_material", l_phys3d_shape_set_material);
  set_cfunc_field(L, "set_filter", l_phys3d_shape_set_filter);
  set_cfunc_field(L, "set_events", l_phys3d_shape_set_events);
}

static void push_joint_ref(lua_State *L, const char *world_key,
                           const char *joint_key) {
  lua_newtable(L);
  lua_pushstring(L, "phys3d_joint");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, world_key);
  lua_setfield(L, -2, "world");
  lua_pushstring(L, joint_key);
  lua_setfield(L, -2, "key");
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

static Phys3dWorld *world_get(Phys3dState *state, const char *key) {
  if (!state || !key)
    return NULL;
  uint32_t i = hash_str32(key) & (PHYS3D_WORLD_BUCKETS - 1);
  for (Phys3dWorld *w = state->worlds[i]; w; w = w->next) {
    if (strcmp(w->key, key) == 0)
      return w;
  }
  return NULL;
}

static Phys3dWorld *world_get_or_create(Phys3dState *state, const char *key) {
  Phys3dWorld *w = world_get(state, key);
  if (w)
    return w;
  uint32_t i = hash_str32(key) & (PHYS3D_WORLD_BUCKETS - 1);
  w = (Phys3dWorld *)SDL_calloc(1, sizeof(Phys3dWorld));
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
  w->id = b3_nullWorldId;
  callbacks_init(&w->callbacks);
  w->next = state->worlds[i];
  state->worlds[i] = w;
  return w;
}

static Phys3dBody *body_get(Phys3dWorld *w, const char *key) {
  if (!w || !key)
    return NULL;
  uint32_t i = hash_str32(key) & (PHYS3D_BODY_BUCKETS - 1);
  for (Phys3dBody *b = w->bodies[i]; b; b = b->next) {
    if (strcmp(b->key, key) == 0)
      return b;
  }
  return NULL;
}

static Phys3dBody *body_get_or_create(Phys3dWorld *w, const char *key) {
  Phys3dBody *b = body_get(w, key);
  if (b)
    return b;
  uint32_t i = hash_str32(key) & (PHYS3D_BODY_BUCKETS - 1);
  b = (Phys3dBody *)SDL_calloc(1, sizeof(Phys3dBody));
  if (!b)
    return NULL;
  b->key = phys_strdup(key);
  if (!b->key) {
    SDL_free(b);
    return NULL;
  }
  b->world = w;
  b->id = b3_nullBodyId;
  b->version = INT64_MIN;
  b->next = w->bodies[i];
  w->bodies[i] = b;
  return b;
}

static Phys3dShape *shape_get(Phys3dBody *b, const char *key) {
  if (!b || !key)
    return NULL;
  uint32_t i = hash_str32(key) & (PHYS3D_SHAPE_BUCKETS - 1);
  for (Phys3dShape *s = b->shapes[i]; s; s = s->next) {
    if (strcmp(s->key, key) == 0)
      return s;
  }
  return NULL;
}

static Phys3dShape *shape_get_or_create(Phys3dBody *b, const char *key) {
  Phys3dShape *s = shape_get(b, key);
  if (s)
    return s;
  uint32_t i = hash_str32(key) & (PHYS3D_SHAPE_BUCKETS - 1);
  s = (Phys3dShape *)SDL_calloc(1, sizeof(Phys3dShape));
  if (!s)
    return NULL;
  s->key = phys_strdup(key);
  if (!s->key) {
    SDL_free(s);
    return NULL;
  }
  s->body = b;
  s->id = b3_nullShapeId;
  s->next = b->shapes[i];
  b->shapes[i] = s;
  return s;
}

static Phys3dJoint *joint_get(Phys3dWorld *w, const char *key) {
  if (!w || !key)
    return NULL;
  uint32_t i = hash_str32(key) & (PHYS3D_JOINT_BUCKETS - 1);
  for (Phys3dJoint *j = w->joints[i]; j; j = j->next) {
    if (strcmp(j->key, key) == 0)
      return j;
  }
  return NULL;
}

static Phys3dJoint *joint_get_or_create(Phys3dWorld *w, const char *key) {
  Phys3dJoint *j = joint_get(w, key);
  if (j)
    return j;
  uint32_t i = hash_str32(key) & (PHYS3D_JOINT_BUCKETS - 1);
  j = (Phys3dJoint *)SDL_calloc(1, sizeof(Phys3dJoint));
  if (!j)
    return NULL;
  j->key = phys_strdup(key);
  if (!j->key) {
    SDL_free(j);
    return NULL;
  }
  j->world = w;
  j->id = b3_nullJointId;
  j->version = INT64_MIN;
  j->next = w->joints[i];
  w->joints[i] = j;
  return j;
}

static uint32_t shape_tombstone_bucket(uint64_t id_key) {
  return (uint32_t)(id_key ^ (id_key >> 32)) & (PHYS3D_TOMBSTONE_BUCKETS - 1);
}

static Phys3dShapeTombstone *shape_tombstone_get(Phys3dWorld *w,
                                                 b3ShapeId shape_id) {
  if (!w || B3_IS_NULL(shape_id))
    return NULL;
  uint64_t id_key = b3StoreShapeId(shape_id);
  uint32_t bucket = shape_tombstone_bucket(id_key);
  for (Phys3dShapeTombstone *t = w->shape_tombstones[bucket]; t; t = t->next) {
    if (t->id_key == id_key)
      return t;
  }
  return NULL;
}

static void shape_tombstone_put(Phys3dWorld *w, b3ShapeId shape_id,
                                const char *body, const char *shape,
                                const char *tag, const char *material,
                                int material_id) {
  if (!w || B3_IS_NULL(shape_id))
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

  uint64_t id_key = b3StoreShapeId(shape_id);
  uint32_t bucket = shape_tombstone_bucket(id_key);
  Phys3dShapeTombstone *t = shape_tombstone_get(w, shape_id);
  if (!t) {
    t = (Phys3dShapeTombstone *)SDL_calloc(1, sizeof(*t));
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

static void shape_tombstone_update_shape(Phys3dShape *shape) {
  if (!shape || !shape->body || !shape->body->world || B3_IS_NULL(shape->id) ||
      !b3Shape_IsValid(shape->id))
    return;
  shape_tombstone_put(
      shape->body->world, shape->id, shape->body->key, shape->key, shape->tag,
      shape->material_name,
      (int)b3Shape_GetSurfaceMaterial(shape->id).userMaterialId);
}

static void shape_tombstone_clear(Phys3dShapeTombstone *t) {
  if (!t)
    return;
  SDL_free(t->body);
  SDL_free(t->shape);
  SDL_free(t->tag);
  SDL_free(t->material);
  SDL_free(t);
}

static void shape_tombstone_clear_all(Phys3dWorld *w) {
  if (!w)
    return;
  for (int i = 0; i < PHYS3D_TOMBSTONE_BUCKETS; ++i) {
    Phys3dShapeTombstone *t = w->shape_tombstones[i];
    while (t) {
      Phys3dShapeTombstone *next = t->next;
      shape_tombstone_clear(t);
      t = next;
    }
    w->shape_tombstones[i] = NULL;
  }
}

static void snapshot_clear_one(Phys3dContactSnapshot *e) {
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

static void body_snapshot_clear_one(Phys3dBodyEventSnapshot *e) {
  SDL_free(e->body);
  memset(e, 0, sizeof(*e));
}

static void joint_snapshot_clear_one(Phys3dJointEventSnapshot *e) {
  SDL_free(e->joint);
  SDL_free(e->body_a);
  SDL_free(e->body_b);
  memset(e, 0, sizeof(*e));
}

static void event_buffer_clear(Phys3dEventBuffer *events) {
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
  for (int i = 0; i < events->joint_count; ++i)
    joint_snapshot_clear_one(&events->joints[i]);
  events->begin_count = 0;
  events->end_count = 0;
  events->hit_count = 0;
  events->sensor_begin_count = 0;
  events->sensor_end_count = 0;
  events->move_count = 0;
  events->joint_count = 0;
}

static void event_buffer_free(Phys3dEventBuffer *events) {
  event_buffer_clear(events);
  SDL_free(events->begins);
  SDL_free(events->ends);
  SDL_free(events->hits);
  SDL_free(events->sensor_begins);
  SDL_free(events->sensor_ends);
  SDL_free(events->moves);
  SDL_free(events->joints);
  memset(events, 0, sizeof(*events));
}

static Phys3dContactSnapshot *event_push(Phys3dContactSnapshot **items,
                                         int *count, int *cap) {
  if (*count >= *cap) {
    int new_cap = *cap ? *cap * 2 : 8;
    Phys3dContactSnapshot *new_items =
        (Phys3dContactSnapshot *)SDL_realloc(*items, sizeof(**items) * new_cap);
    if (!new_items)
      return NULL;
    memset(new_items + *cap, 0, sizeof(**items) * (new_cap - *cap));
    *items = new_items;
    *cap = new_cap;
  }
  Phys3dContactSnapshot *out = &(*items)[(*count)++];
  memset(out, 0, sizeof(*out));
  return out;
}

static Phys3dBodyEventSnapshot *body_event_push(Phys3dBodyEventSnapshot **items,
                                                int *count, int *cap) {
  if (*count >= *cap) {
    int new_cap = *cap ? *cap * 2 : 16;
    Phys3dBodyEventSnapshot *new_items = (Phys3dBodyEventSnapshot *)SDL_realloc(
        *items, sizeof(**items) * new_cap);
    if (!new_items)
      return NULL;
    memset(new_items + *cap, 0, sizeof(**items) * (new_cap - *cap));
    *items = new_items;
    *cap = new_cap;
  }
  Phys3dBodyEventSnapshot *out = &(*items)[(*count)++];
  memset(out, 0, sizeof(*out));
  return out;
}

static Phys3dJointEventSnapshot *
joint_event_push(Phys3dJointEventSnapshot **items, int *count, int *cap) {
  if (*count >= *cap) {
    int new_cap = *cap ? *cap * 2 : 8;
    Phys3dJointEventSnapshot *new_items =
        (Phys3dJointEventSnapshot *)SDL_realloc(*items,
                                                sizeof(**items) * new_cap);
    if (!new_items)
      return NULL;
    memset(new_items + *cap, 0, sizeof(**items) * (new_cap - *cap));
    *items = new_items;
    *cap = new_cap;
  }
  Phys3dJointEventSnapshot *out = &(*items)[(*count)++];
  memset(out, 0, sizeof(*out));
  return out;
}

static void command_clear(Phys3dCommand *cmd) {
  SDL_free(cmd->body_key);
  memset(cmd, 0, sizeof(*cmd));
}

static void command_queue_clear(Phys3dCommandQueue *queue) {
  if (!queue)
    return;
  for (int i = 0; i < queue->count; ++i)
    command_clear(&queue->items[i]);
  queue->count = 0;
}

static void command_queue_free(Phys3dCommandQueue *queue) {
  if (!queue)
    return;
  command_queue_clear(queue);
  SDL_free(queue->items);
  memset(queue, 0, sizeof(*queue));
}

static Phys3dCommand *command_queue_push(lua_State *L, Phys3dWorld *w,
                                         Phys3dBody *b, Phys3dCommandKind kind,
                                         const char *fn) {
  if (!w || !b || !b->key)
    luaL_error(L, "%s: missing body", fn);
  Phys3dCommandQueue *queue = &w->commands;
  if (queue->count >= queue->cap) {
    int new_cap = queue->cap ? queue->cap * 2 : 32;
    Phys3dCommand *new_items = (Phys3dCommand *)SDL_realloc(
        queue->items, sizeof(*queue->items) * new_cap);
    if (!new_items)
      luaL_error(L, "%s: out of memory", fn);
    memset(new_items + queue->cap, 0,
           sizeof(*queue->items) * (new_cap - queue->cap));
    queue->items = new_items;
    queue->cap = new_cap;
  }
  Phys3dCommand *cmd = &queue->items[queue->count++];
  memset(cmd, 0, sizeof(*cmd));
  cmd->body_key = phys_strdup(b->key);
  if (!cmd->body_key) {
    queue->count--;
    luaL_error(L, "%s: out of memory", fn);
  }
  cmd->kind = kind;
  if (B3_IS_NON_NULL(b->id) && b3Body_IsValid(b->id))
    cmd->body_id_key = b3StoreBodyId(b->id);
  cmd->wake = true;
  return cmd;
}

// Box3D references (does not copy) mesh/height-field/compound data, so the
// owned data may only be released after the box3d shape is gone.
static void shape_free_heavy_data(Phys3dShape *s) {
  if (s->mesh_data) {
    b3DestroyMesh(s->mesh_data);
    s->mesh_data = NULL;
  }
  if (s->height_field_data) {
    b3DestroyHeightField(s->height_field_data);
    s->height_field_data = NULL;
  }
  if (s->compound_data) {
    b3DestroyCompound(s->compound_data);
    s->compound_data = NULL;
  }
}

static void shape_free(Phys3dShape *s, bool destroy_id) {
  if (!s)
    return;
  if (destroy_id && B3_IS_NON_NULL(s->id) && b3Shape_IsValid(s->id)) {
    b3DestroyShape(s->id, true);
  }
  shape_free_heavy_data(s);
  owned_string_clear(&s->tag);
  owned_string_clear(&s->material_name);
  SDL_free(s->key);
  SDL_free(s);
}

static void joint_free(Phys3dJoint *j, bool destroy_id) {
  if (!j)
    return;
  if (destroy_id && B3_IS_NON_NULL(j->id) && b3Joint_IsValid(j->id)) {
    b3DestroyJoint(j->id, true);
  }
  SDL_free(j->key);
  SDL_free(j);
}

static void world_remove_joints_for_body(Phys3dWorld *w, Phys3dBody *body,
                                         bool destroy_ids) {
  if (!w || !body)
    return;
  for (int i = 0; i < PHYS3D_JOINT_BUCKETS; ++i) {
    Phys3dJoint **prev = &w->joints[i];
    Phys3dJoint *j = w->joints[i];
    while (j) {
      Phys3dJoint *next = j->next;
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

static void body_free_shapes(Phys3dBody *b, bool destroy_ids) {
  for (int i = 0; i < PHYS3D_SHAPE_BUCKETS; ++i) {
    Phys3dShape *s = b->shapes[i];
    while (s) {
      Phys3dShape *next = s->next;
      shape_free(s, destroy_ids);
      s = next;
    }
    b->shapes[i] = NULL;
  }
}

static void body_free(Phys3dBody *b, bool destroy_id) {
  if (!b)
    return;
  if (destroy_id && B3_IS_NON_NULL(b->id) && b3Body_IsValid(b->id)) {
    world_remove_joints_for_body(b->world, b, true);
    b3DestroyBody(b->id);
    body_free_shapes(b, false);
  } else {
    body_free_shapes(b, destroy_id);
  }
  SDL_free(b->key);
  SDL_free(b);
}

static void world_free_joints(Phys3dWorld *w, bool destroy_ids) {
  for (int i = 0; i < PHYS3D_JOINT_BUCKETS; ++i) {
    Phys3dJoint *j = w->joints[i];
    while (j) {
      Phys3dJoint *next = j->next;
      joint_free(j, destroy_ids);
      j = next;
    }
    w->joints[i] = NULL;
  }
}

static void world_free_bodies(Phys3dWorld *w, bool destroy_ids) {
  for (int i = 0; i < PHYS3D_BODY_BUCKETS; ++i) {
    Phys3dBody *b = w->bodies[i];
    while (b) {
      Phys3dBody *next = b->next;
      body_free(b, destroy_ids);
      b = next;
    }
    w->bodies[i] = NULL;
  }
}

static void world_destroy_box3d_and_contents(Phys3dWorld *w) {
  if (B3_IS_NON_NULL(w->id) && b3World_IsValid(w->id)) {
    b3DestroyWorld(w->id);
    w->id = b3_nullWorldId;
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

static void world_free(Phys3dWorld *w) {
  if (!w)
    return;
  callbacks_clear(w->callbacks.L, w);
  world_destroy_box3d_and_contents(w);
  event_buffer_free(&w->events);
  command_queue_free(&w->commands);
  SDL_free(w->key);
  SDL_free(w);
}

void phys3d_state_init(Phys3dState *state) { memset(state, 0, sizeof(*state)); }

void phys3d_state_shutdown(Phys3dState *state) {
  if (!state)
    return;
  for (int i = 0; i < PHYS3D_WORLD_BUCKETS; ++i) {
    Phys3dWorld *w = state->worlds[i];
    while (w) {
      Phys3dWorld *next = w->next;
      world_free(w);
      w = next;
    }
    state->worlds[i] = NULL;
  }
}

void phys3d_lua_set_state(Phys3dState *state) { g_phys3d_state = state; }

static Phys3dWorld *check_world(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_world"))
    luaL_error(L, "expected Phys3d WorldRef");
  const char *key = ref_string(L, idx, "key");
  Phys3dWorld *w = world_get(g_phys3d_state, key);
  if (!w)
    luaL_error(L, "phys3d world not found: %s", key ? key : "?");
  return w;
}

static Phys3dWorld *query_world_ref(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_world"))
    luaL_error(L, "expected Phys3d WorldRef");
  const char *key = ref_string(L, idx, "key");
  Phys3dWorld *w = world_get(g_phys3d_state, key);
  if (!w || B3_IS_NULL(w->id) || !b3World_IsValid(w->id))
    return NULL;
  return w;
}

static Phys3dBody *check_body(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_body"))
    luaL_error(L, "expected Phys3d BodyRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *body_key = ref_string(L, idx, "key");
  Phys3dWorld *w = world_get(g_phys3d_state, world_key);
  Phys3dBody *b = w ? body_get(w, body_key) : NULL;
  if (!b)
    luaL_error(L, "phys3d body not found: %s/%s", world_key ? world_key : "?",
               body_key ? body_key : "?");
  return b;
}

static Phys3dBody *query_body_ref(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_body"))
    luaL_error(L, "expected Phys3d BodyRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *body_key = ref_string(L, idx, "key");
  Phys3dWorld *w = world_get(g_phys3d_state, world_key);
  Phys3dBody *b = w ? body_get(w, body_key) : NULL;
  return body_is_live(b) ? b : NULL;
}

static int push_not_found(lua_State *L) {
  lua_pushnil(L);
  lua_pushstring(L, "not found");
  return 2;
}

static Phys3dShape *check_shape(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_shape"))
    luaL_error(L, "expected Phys3d ShapeRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *body_key = ref_string(L, idx, "body");
  const char *shape_key = ref_string(L, idx, "key");
  Phys3dWorld *w = world_get(g_phys3d_state, world_key);
  Phys3dBody *b = w ? body_get(w, body_key) : NULL;
  Phys3dShape *s = b ? shape_get(b, shape_key) : NULL;
  if (!s)
    luaL_error(L, "phys3d shape not found: %s/%s/%s",
               world_key ? world_key : "?", body_key ? body_key : "?",
               shape_key ? shape_key : "?");
  return s;
}

static Phys3dShape *query_shape_ref(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_shape"))
    luaL_error(L, "expected Phys3d ShapeRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *body_key = ref_string(L, idx, "body");
  const char *shape_key = ref_string(L, idx, "key");
  Phys3dWorld *w = world_get(g_phys3d_state, world_key);
  Phys3dBody *b = w ? body_get(w, body_key) : NULL;
  Phys3dShape *s = b ? shape_get(b, shape_key) : NULL;
  return shape_is_live(s) ? s : NULL;
}

static Phys3dJoint *check_joint(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_joint"))
    luaL_error(L, "expected Phys3d JointRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *joint_key = ref_string(L, idx, "key");
  Phys3dWorld *w = world_get(g_phys3d_state, world_key);
  Phys3dJoint *j = w ? joint_get(w, joint_key) : NULL;
  if (!j)
    luaL_error(L, "phys3d joint not found: %s/%s", world_key ? world_key : "?",
               joint_key ? joint_key : "?");
  return j;
}

static Phys3dJoint *query_joint_ref(lua_State *L, int idx) {
  luaL_checktype(L, idx, LUA_TTABLE);
  if (!is_ref(L, idx, "phys3d_joint"))
    luaL_error(L, "expected Phys3d JointRef");
  const char *world_key = ref_string(L, idx, "world");
  const char *joint_key = ref_string(L, idx, "key");
  Phys3dWorld *w = world_get(g_phys3d_state, world_key);
  Phys3dJoint *j = w ? joint_get(w, joint_key) : NULL;
  return joint_is_live(j) ? j : NULL;
}

static bool body_is_live(Phys3dBody *b) {
  return b && B3_IS_NON_NULL(b->id) && b3Body_IsValid(b->id);
}

static bool shape_is_live(Phys3dShape *s) {
  return s && B3_IS_NON_NULL(s->id) && b3Shape_IsValid(s->id);
}

static void check_live_body(lua_State *L, Phys3dBody *b, const char *fn) {
  if (!body_is_live(b))
    luaL_error(L, "%s: body is not live", fn);
}

static void check_live_shape(lua_State *L, Phys3dShape *s, const char *fn) {
  if (!shape_is_live(s))
    luaL_error(L, "%s: shape is not live", fn);
}

static bool joint_is_live(Phys3dJoint *j) {
  return j && B3_IS_NON_NULL(j->id) && b3Joint_IsValid(j->id);
}

static void check_live_joint(lua_State *L, Phys3dJoint *j, const char *fn) {
  if (!joint_is_live(j))
    luaL_error(L, "%s: joint is not live", fn);
}

static void parse_world_opts(lua_State *L, int idx, Phys3dWorldOpts *opts) {
  opts->version = 0;
  opts->has_version = false;
  opts->gravity = (b3Vec3){0.0f, -9.8f, 0.0f};
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
  opts->gravity = table_vec3(L, idx, "gravity", NULL, opts->gravity);
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

static bool world_create_or_recreate(lua_State *L, Phys3dWorld *w,
                                     const Phys3dWorldOpts *opts) {
  int64_t version = opts->has_version ? opts->version : 0;
  bool needs_create =
      B3_IS_NULL(w->id) || !b3World_IsValid(w->id) || w->version != version;
  if (needs_create) {
    callbacks_clear(L, w);
    world_destroy_box3d_and_contents(w);
    b3WorldDef def = b3DefaultWorldDef();
    def.gravity = opts->gravity;
    def.enableSleep = opts->sleep;
    w->id = b3CreateWorld(&def);
    if (B3_IS_NULL(w->id))
      return luaL_error(L, "phys3d_world: b3CreateWorld failed"), false;
    w->version = version;
  } else {
    b3World_SetGravity(w->id, opts->gravity);
    b3World_EnableSleeping(w->id, opts->sleep);
  }
  b3World_EnableContinuous(w->id, opts->continuous);
  if (opts->has_hit_event_threshold)
    b3World_SetHitEventThreshold(w->id, opts->hit_event_threshold);
  w->fixed_dt = opts->fixed_dt;
  w->substeps = opts->substeps;
  w->max_steps = opts->max_steps;
  return true;
}

static int l_phys3d_world(lua_State *L) {
  if (phys_in_callback(L, "phys3d_world"))
    return 0;
  const char *key = luaL_checkstring(L, 1);
  Phys3dWorldOpts opts;
  parse_world_opts(L, 2, &opts);
  Phys3dWorld *w = world_get_or_create(g_phys3d_state, key);
  if (!w)
    return luaL_error(L, "phys3d_world: out of memory");
  if (!world_create_or_recreate(L, w, &opts))
    return 0;
  callbacks_replace_from_opts(L, w, 2);
  push_world_ref(L, key);
  return 1;
}

static int l_phys3d_begin(lua_State *L) {
  if (phys_in_callback(L, "phys3d_begin"))
    return 0;
  Phys3dWorld *w = check_world(L, 1);
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

static b3BodyType parse_body_type(lua_State *L, int idx) {
  int type = (int)luaL_checkinteger(L, idx);
  switch (type) {
  case PHYS3D_STATIC:
    return b3_staticBody;
  case PHYS3D_KINEMATIC:
    return b3_kinematicBody;
  case PHYS3D_DYNAMIC:
    return b3_dynamicBody;
  default:
    luaL_error(L, "phys3d_body: unknown body type %d", type);
    return b3_staticBody;
  }
}

static void parse_initial(lua_State *L, int idx, Phys3dBodyDesc *desc) {
  if (!table_get_any(L, idx, "initial", NULL))
    return;
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return;
  }
  int t = lua_gettop(L);
  desc->initial_pos.x = table_number(L, t, "x", NULL, desc->initial_pos.x);
  desc->initial_pos.y = table_number(L, t, "y", NULL, desc->initial_pos.y);
  desc->initial_pos.z = table_number(L, t, "z", NULL, desc->initial_pos.z);
  table_rotation(L, t, &desc->initial_rot);
  desc->initial_vel.x = table_number(L, t, "vx", NULL, desc->initial_vel.x);
  desc->initial_vel.y = table_number(L, t, "vy", NULL, desc->initial_vel.y);
  desc->initial_vel.z = table_number(L, t, "vz", NULL, desc->initial_vel.z);
  desc->initial_w.x = table_number(L, t, "wx", NULL, desc->initial_w.x);
  desc->initial_w.y = table_number(L, t, "wy", NULL, desc->initial_w.y);
  desc->initial_w.z = table_number(L, t, "wz", NULL, desc->initial_w.z);
  desc->initial_awake = table_bool(L, t, "awake", NULL, desc->initial_awake);
  lua_pop(L, 1);
}

static void parse_motion_locks(lua_State *L, int idx, Phys3dBodyDesc *desc) {
  if (!table_get_any(L, idx, "motion_locks", "motionLocks"))
    return;
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return;
  }
  int t = lua_gettop(L);
  desc->motion_locks.linearX = table_bool(L, t, "linear_x", NULL, false);
  desc->motion_locks.linearY = table_bool(L, t, "linear_y", NULL, false);
  desc->motion_locks.linearZ = table_bool(L, t, "linear_z", NULL, false);
  desc->motion_locks.angularX = table_bool(L, t, "angular_x", NULL, false);
  desc->motion_locks.angularY = table_bool(L, t, "angular_y", NULL, false);
  desc->motion_locks.angularZ = table_bool(L, t, "angular_z", NULL, false);
  lua_pop(L, 1);
}

static void parse_body_desc(lua_State *L, int idx, Phys3dBodyDesc *desc) {
  desc->version = 0;
  desc->has_version = false;
  desc->type = b3_staticBody;
  desc->motion_locks =
      (b3MotionLocks){false, false, false, false, false, false};
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
  desc->initial_pos = b3Vec3_zero;
  desc->initial_rot = b3Quat_identity;
  desc->initial_vel = b3Vec3_zero;
  desc->initial_w = b3Vec3_zero;
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
  parse_motion_locks(L, idx, desc);
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
    luaL_error(L, "phys3d_body: sleep_threshold must be >= 0");
  parse_initial(L, idx, desc);
}

static uint64_t body_constructor_hash(const Phys3dBodyDesc *desc) {
  uint64_t h = hash_init();
  h = hash_f32(h, desc->initial_pos.x);
  h = hash_f32(h, desc->initial_pos.y);
  h = hash_f32(h, desc->initial_pos.z);
  h = hash_f32(h, desc->initial_rot.v.x);
  h = hash_f32(h, desc->initial_rot.v.y);
  h = hash_f32(h, desc->initial_rot.v.z);
  h = hash_f32(h, desc->initial_rot.s);
  h = hash_f32(h, desc->initial_vel.x);
  h = hash_f32(h, desc->initial_vel.y);
  h = hash_f32(h, desc->initial_vel.z);
  h = hash_f32(h, desc->initial_w.x);
  h = hash_f32(h, desc->initial_w.y);
  h = hash_f32(h, desc->initial_w.z);
  h = hash_bool(h, desc->initial_awake);
  return h;
}

static int64_t body_effective_version(const Phys3dBodyDesc *desc,
                                      uint64_t fallback_hash) {
  return desc->has_version ? desc->version : (int64_t)fallback_hash;
}

static void log_body_constructor_drift(Phys3dBody *b, uint64_t hash) {
  if (b->constructor_hash == hash || b->constructor_warned)
    return;
  SDL_Log("phys3d_body('%s'): constructor fields changed without version bump",
          b->key);
  b->constructor_warned = true;
}

static void body_create(lua_State *L, Phys3dBody *b, const Phys3dBodyDesc *desc,
                        uint64_t constructor_hash, int64_t version) {
  b3BodyDef def = b3DefaultBodyDef();
  def.type = desc->type;
  def.position = b3ToPos(desc->initial_pos);
  def.rotation = desc->initial_rot;
  def.linearVelocity = desc->initial_vel;
  def.angularVelocity = desc->initial_w;
  def.motionLocks = desc->motion_locks;
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
  b->id = b3CreateBody(b->world->id, &def);
  if (B3_IS_NULL(b->id))
    luaL_error(L, "phys3d_body: b3CreateBody failed");
  if (desc->has_awake)
    b3Body_SetAwake(b->id, desc->awake);
  b->version = version;
  b->constructor_hash = constructor_hash;
  b->constructor_warned = false;
}

static void body_apply_runtime(Phys3dBody *b, const Phys3dBodyDesc *desc) {
  b3Body_SetType(b->id, desc->type);
  b3Body_SetMotionLocks(b->id, desc->motion_locks);
  b3Body_SetBullet(b->id, desc->bullet);
  b3Body_SetGravityScale(b->id, desc->gravity_scale);
  b3Body_SetLinearDamping(b->id, desc->linear_damping);
  b3Body_SetAngularDamping(b->id, desc->angular_damping);
  if (desc->has_awake)
    b3Body_SetAwake(b->id, desc->awake);
  if (desc->has_sleep)
    b3Body_EnableSleep(b->id, desc->sleep);
  if (desc->has_sleep_threshold)
    b3Body_SetSleepThreshold(b->id, desc->sleep_threshold);
  if (desc->has_enabled && b3Body_IsEnabled(b->id) != desc->enabled) {
    if (desc->enabled) {
      b3Body_Enable(b->id);
    } else {
      b3Body_Disable(b->id);
    }
  }
}

static int l_phys3d_body(lua_State *L) {
  if (phys_in_callback(L, "phys3d_body"))
    return 0;
  Phys3dWorld *w = check_world(L, 1);
  const char *key = luaL_checkstring(L, 2);
  Phys3dBodyDesc desc;
  parse_body_desc(L, 3, &desc);
  if (!w->begun)
    return luaL_error(L, "phys3d_body: call phys3d_begin(world) first");
  Phys3dBody *b = body_get_or_create(w, key);
  if (!b)
    return luaL_error(L, "phys3d_body: out of memory");
  uint64_t constructor_hash = body_constructor_hash(&desc);
  int64_t version = body_effective_version(&desc, constructor_hash);
  if (B3_IS_NULL(b->id) || !b3Body_IsValid(b->id) || b->version != version) {
    if (B3_IS_NON_NULL(b->id) && b3Body_IsValid(b->id)) {
      b3DestroyBody(b->id);
      body_free_shapes(b, false);
      b->id = b3_nullBodyId;
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
    luaL_error(L, "phys3d filter %s: empty hex string", field_name);
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
      luaL_error(L, "phys3d filter %s: invalid hex digit", field_name);
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
      luaL_error(L, "phys3d filter %s bit index out of range: %d", field_name,
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
      luaL_error(L, "phys3d filter category bit index out of range: %d", bit);
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

static void parse_filter(lua_State *L, int idx, Phys3dShapeDesc *desc) {
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

static void parse_shape_desc(lua_State *L, int idx, Phys3dShapeDesc *desc) {
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
  desc->material_id = table_int(L, idx, "material_id", NULL, desc->material_id);
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

static void shape_apply_density_default(Phys3dBody *body,
                                        Phys3dShapeDesc *desc) {
  if (!body || !desc || desc->has_density || !body_is_live(body))
    return;
  desc->density = b3Body_GetType(body->id) == b3_dynamicBody ? 1.0f : 0.0f;
}

static void shape_update_metadata(lua_State *L, Phys3dShape *shape, int idx,
                                  int material_id) {
  const char *tag = NULL;
  if (table_get_any(L, idx, "tag", NULL)) {
    if (lua_type(L, -1) == LUA_TSTRING)
      tag = lua_tostring(L, -1);
    owned_string_set_lua(L, &shape->tag, tag, "phys3d shape metadata");
    lua_pop(L, 1);
  } else {
    owned_string_set_lua(L, &shape->tag, NULL, "phys3d shape metadata");
  }

  const char *material_name = NULL;
  lua_getfield(L, abs_index(L, idx), "material");
  if (lua_type(L, -1) == LUA_TSTRING)
    material_name = lua_tostring(L, -1);
  owned_string_set_lua(L, &shape->material_name, material_name,
                       "phys3d shape metadata");
  lua_pop(L, 1);
  shape->material_id = material_id;
}

static b3ShapeDef make_shape_def(const Phys3dShapeDesc *desc,
                                 Phys3dShape *shape) {
  b3ShapeDef def = b3DefaultShapeDef();
  def.userData = shape;
  def.density = desc->density;
  def.baseMaterial.friction = desc->friction;
  def.baseMaterial.restitution = desc->restitution;
  def.baseMaterial.userMaterialId = (uint64_t)desc->material_id;
  def.isSensor = desc->sensor;
  // Box3D only invokes the custom filter callback when a shape opts in;
  // phys2d applies the world filter callback to every shape, so opt in
  // unconditionally (box3d checks the callback pointer first, so this is
  // free when no callback is installed).
  def.enableCustomFiltering = true;
  def.enableContactEvents = desc->contact;
  def.enableHitEvents = desc->hit;
  def.enableSensorEvents = desc->sensor_events;
  def.enablePreSolveEvents = desc->pre_solve;
  def.filter.categoryBits = desc->category_bits;
  def.filter.maskBits = desc->mask_bits;
  def.filter.groupIndex = desc->group_index;
  return def;
}

static uint64_t shape_base_hash(const Phys3dShapeDesc *desc,
                                Phys3dShapeKind kind) {
  uint64_t h = hash_init();
  h = hash_u64(h, (uint64_t)kind);
  h = hash_bool(h, desc->sensor);
  h = hash_u64(h, desc->category_bits);
  h = hash_u64(h, desc->mask_bits);
  h = hash_i64(h, desc->group_index);
  return h;
}

static void shape_apply_runtime_desc(Phys3dShape *shape,
                                     const Phys3dShapeDesc *desc) {
  b3Shape_SetDensity(shape->id, desc->density, true);
  // Box3D has no per-field userMaterialId setter, so round-trip the surface
  // material for friction/restitution/material id in one go.
  b3SurfaceMaterial material = b3Shape_GetSurfaceMaterial(shape->id);
  material.friction = desc->friction;
  material.restitution = desc->restitution;
  material.userMaterialId = (uint64_t)desc->material_id;
  b3Shape_SetSurfaceMaterial(shape->id, material);
  b3Shape_EnableSensorEvents(shape->id, desc->sensor_events);
  b3Shape_EnableContactEvents(shape->id, desc->contact);
  b3Shape_EnablePreSolveEvents(shape->id, desc->pre_solve);
  b3Shape_EnableHitEvents(shape->id, desc->hit);
}

static int64_t shape_effective_version(const Phys3dShapeDesc *desc,
                                       uint64_t fallback_hash) {
  return desc->has_version ? desc->version : (int64_t)fallback_hash;
}

static void log_shape_constructor_drift(const char *fn, Phys3dShape *shape,
                                        uint64_t hash) {
  if (shape->constructor_hash == hash || shape->constructor_warned)
    return;
  SDL_Log("%s('%s/%s'): constructor fields changed without version bump", fn,
          shape->body ? shape->body->key : "?", shape->key);
  shape->constructor_warned = true;
}

static void shape_mark_declared(Phys3dShape *shape, Phys3dShapeKind kind,
                                uint64_t fallback_hash,
                                const Phys3dShapeDesc *desc, bool recreated) {
  if (recreated)
    shape->kind = kind;
  shape->desc_hash = (uint64_t)shape_effective_version(desc, fallback_hash);
  shape->constructor_hash = fallback_hash;
  if (recreated)
    shape->constructor_warned = false;
  shape->seen_generation = shape->body->world->generation;
  shape_tombstone_update_shape(shape);
}

static int l_phys3d_sphere(lua_State *L) {
  if (phys_in_callback(L, "phys3d_sphere"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  Phys3dShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  float r = table_number(L, 3, "r", "radius", 0.0f);
  if (r <= 0.0f)
    return luaL_error(L, "phys3d_sphere: r must be > 0");
  b3Vec3 offset = table_vec3(L, 3, "offset", NULL, b3Vec3_zero);
  uint64_t h = shape_base_hash(&desc, PHYS3D_SHAPE_SPHERE);
  h = hash_f32(h, r);
  h = hash_f32(h, offset.x);
  h = hash_f32(h, offset.y);
  h = hash_f32(h, offset.z);
  int64_t version = shape_effective_version(&desc, h);
  Phys3dShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys3d_sphere: out of memory");
  bool recreated = B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS3D_SHAPE_SPHERE);
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    b3ShapeDef def = make_shape_def(&desc, shape);
    b3Sphere sphere = {.center = offset, .radius = r};
    shape->id = b3CreateSphereShape(b->id, &def, &sphere);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys3d_sphere", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS3D_SHAPE_SPHERE, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static int l_phys3d_box(lua_State *L) {
  if (phys_in_callback(L, "phys3d_box"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  Phys3dShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  float hx = table_number(L, 3, "hx", NULL, 0.0f);
  float hy = table_number(L, 3, "hy", NULL, 0.0f);
  float hz = table_number(L, 3, "hz", NULL, 0.0f);
  if (hx <= 0.0f || hy <= 0.0f || hz <= 0.0f)
    return luaL_error(L, "phys3d_box: hx, hy and hz must be > 0");
  b3Vec3 offset = table_vec3(L, 3, "offset", NULL, b3Vec3_zero);
  b3Quat rotation = b3Quat_identity;
  bool has_rotation = false;
  if (table_get_any(L, 3, "quat", NULL)) {
    if (lua_istable(L, -1)) {
      rotation = value_quat(L, lua_gettop(L), b3Quat_identity);
      has_rotation = true;
    }
    lua_pop(L, 1);
  }
  uint64_t h = shape_base_hash(&desc, PHYS3D_SHAPE_BOX);
  h = hash_f32(h, hx);
  h = hash_f32(h, hy);
  h = hash_f32(h, hz);
  h = hash_f32(h, offset.x);
  h = hash_f32(h, offset.y);
  h = hash_f32(h, offset.z);
  h = hash_f32(h, rotation.v.x);
  h = hash_f32(h, rotation.v.y);
  h = hash_f32(h, rotation.v.z);
  h = hash_f32(h, rotation.s);
  int64_t version = shape_effective_version(&desc, h);
  Phys3dShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys3d_box: out of memory");
  bool recreated = B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS3D_SHAPE_BOX);
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    b3ShapeDef def = make_shape_def(&desc, shape);
    b3BoxHull hull;
    if (has_rotation) {
      b3Transform transform = {offset, rotation};
      hull = b3MakeTransformedBoxHull(hx, hy, hz, transform);
    } else if (offset.x != 0.0f || offset.y != 0.0f || offset.z != 0.0f) {
      hull = b3MakeOffsetBoxHull(hx, hy, hz, offset);
    } else {
      hull = b3MakeBoxHull(hx, hy, hz);
    }
    shape->id = b3CreateHullShape(b->id, &def, &hull.base);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys3d_box", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS3D_SHAPE_BOX, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static int l_phys3d_capsule(lua_State *L) {
  if (phys_in_callback(L, "phys3d_capsule"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  Phys3dShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  b3Vec3 a = table_vec3(L, 3, "a", NULL, b3Vec3_zero);
  b3Vec3 c = table_vec3(L, 3, "b", NULL, b3Vec3_zero);
  float r = table_number(L, 3, "r", "radius", 0.0f);
  if (r <= 0.0f)
    return luaL_error(L, "phys3d_capsule: r must be > 0");
  if (b3DistanceSquared(a, c) <= 1e-12f)
    return luaL_error(L, "phys3d_capsule: endpoints must be distinct");
  uint64_t h = shape_base_hash(&desc, PHYS3D_SHAPE_CAPSULE);
  h = hash_f32(h, a.x);
  h = hash_f32(h, a.y);
  h = hash_f32(h, a.z);
  h = hash_f32(h, c.x);
  h = hash_f32(h, c.y);
  h = hash_f32(h, c.z);
  h = hash_f32(h, r);
  int64_t version = shape_effective_version(&desc, h);
  Phys3dShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys3d_capsule: out of memory");
  bool recreated = B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS3D_SHAPE_CAPSULE);
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    b3ShapeDef def = make_shape_def(&desc, shape);
    b3Capsule capsule = {.center1 = a, .center2 = c, .radius = r};
    shape->id = b3CreateCapsuleShape(b->id, &def, &capsule);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys3d_capsule", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS3D_SHAPE_CAPSULE, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static int l_phys3d_cylinder(lua_State *L) {
  if (phys_in_callback(L, "phys3d_cylinder"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  Phys3dShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  float height = table_number(L, 3, "height", NULL, 0.0f);
  float radius = table_number(L, 3, "radius", "r", 0.0f);
  if (height <= 0.0f || radius <= 0.0f)
    return luaL_error(L, "phys3d_cylinder: height and radius must be > 0");
  int sides = table_int(L, 3, "sides", NULL, 16);
  if (sides < 3 || sides > 32)
    return luaL_error(L, "phys3d_cylinder: sides must be between 3 and 32");
  // b3CreateCylinder spans y in [yOffset, yOffset + height] (see
  // third_party/box3d/src/hull.c); default y_offset = -height/2 keeps the
  // hull centered on the body origin like sphere/box.
  float y_offset = table_number(L, 3, "y_offset", "yOffset", -0.5f * height);
  uint64_t h = shape_base_hash(&desc, PHYS3D_SHAPE_CYLINDER);
  h = hash_f32(h, height);
  h = hash_f32(h, radius);
  h = hash_f32(h, y_offset);
  h = hash_i64(h, sides);
  int64_t version = shape_effective_version(&desc, h);
  Phys3dShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys3d_cylinder: out of memory");
  bool recreated = B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS3D_SHAPE_CYLINDER);
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    b3ShapeDef def = make_shape_def(&desc, shape);
    b3HullData *hull = b3CreateCylinder(height, radius, y_offset, sides);
    if (!hull)
      return luaL_error(L, "phys3d_cylinder: b3CreateCylinder failed");
    shape->id = b3CreateHullShape(b->id, &def, hull);
    // b3CreateHullShape copies the hull into a world-owned hull database
    // (b3AddHullToDatabase -> b3CloneHull), so the temporary can be freed.
    b3DestroyHull(hull);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys3d_cylinder", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS3D_SHAPE_CYLINDER, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static int l_phys3d_cone(lua_State *L) {
  if (phys_in_callback(L, "phys3d_cone"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  Phys3dShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  float height = table_number(L, 3, "height", NULL, 0.0f);
  float radius1 = table_number(L, 3, "radius1", NULL, 0.0f);
  float radius2 = table_number(L, 3, "radius2", NULL, 0.0f);
  if (height <= 0.0f || radius1 <= 0.0f)
    return luaL_error(L, "phys3d_cone: height and radius1 must be > 0");
  if (radius2 < 0.0f)
    return luaL_error(L, "phys3d_cone: radius2 must be >= 0");
  // b3CreateCone asserts radius2 > 0 (see third_party/box3d/src/hull.c), so a
  // zero top radius is clamped to a tiny cap that still hulls cleanly.
  if (radius2 <= 0.0f)
    radius2 = radius1 * 1e-3f;
  int slices = table_int(L, 3, "slices", NULL, 16);
  if (slices < 4 || slices > 32)
    return luaL_error(L, "phys3d_cone: slices must be between 4 and 32");
  uint64_t h = shape_base_hash(&desc, PHYS3D_SHAPE_CONE);
  h = hash_f32(h, height);
  h = hash_f32(h, radius1);
  h = hash_f32(h, radius2);
  h = hash_i64(h, slices);
  int64_t version = shape_effective_version(&desc, h);
  Phys3dShape *shape = shape_get_or_create(b, key);
  if (!shape)
    return luaL_error(L, "phys3d_cone: out of memory");
  bool recreated = B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != PHYS3D_SHAPE_CONE);
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    b3ShapeDef def = make_shape_def(&desc, shape);
    b3HullData *hull = b3CreateCone(height, radius1, radius2, slices);
    if (!hull)
      return luaL_error(L, "phys3d_cone: b3CreateCone failed");
    shape->id = b3CreateHullShape(b->id, &def, hull);
    // b3CreateHullShape copies the hull (see phys3d_cylinder note).
    b3DestroyHull(hull);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift("phys3d_cone", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS3D_SHAPE_CONE, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

// Reads { points = {{x,y,z},...} } or flat { points = {x1,y1,z1,...} }.
static b3Vec3 *read_hull_points(lua_State *L, int idx, int *out_count) {
  if (!table_get_any(L, idx, "points", NULL))
    luaL_error(L, "phys3d_hull: points table is required");
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "phys3d_hull: points must be a table");
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
    luaL_error(L, "phys3d_hull: flat points must have x/y/z triples");
  int count = flat_numbers ? raw_len / 3 : raw_len;
  if (count < 4)
    luaL_error(L, "phys3d_hull: at least 4 points are required");
  b3Vec3 *points = (b3Vec3 *)SDL_malloc(sizeof(*points) * count);
  if (!points)
    luaL_error(L, "phys3d_hull: out of memory");
  if (flat_numbers) {
    for (int i = 0; i < count; ++i) {
      lua_rawgeti(L, pidx, i * 3 + 1);
      points[i].x = (float)luaL_checknumber(L, -1);
      lua_pop(L, 1);
      lua_rawgeti(L, pidx, i * 3 + 2);
      points[i].y = (float)luaL_checknumber(L, -1);
      lua_pop(L, 1);
      lua_rawgeti(L, pidx, i * 3 + 3);
      points[i].z = (float)luaL_checknumber(L, -1);
      lua_pop(L, 1);
    }
  } else {
    for (int i = 0; i < count; ++i) {
      lua_rawgeti(L, pidx, i + 1);
      luaL_checktype(L, -1, LUA_TTABLE);
      points[i] = value_vec3(L, lua_gettop(L), b3Vec3_zero);
      lua_pop(L, 1);
    }
  }
  lua_pop(L, 1);
  *out_count = count;
  return points;
}

static int l_phys3d_hull(lua_State *L) {
  if (phys_in_callback(L, "phys3d_hull"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  Phys3dShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  if (!desc.has_version)
    return luaL_error(L, "phys3d_hull: explicit version is required");
  int64_t version = desc.version;
  int point_count = 0;
  b3Vec3 *points = read_hull_points(L, 3, &point_count);
  // b3CreateHull clamps maxVertexCount to [4, 255] internally.
  int max_vertices = table_int(L, 3, "max_vertices", "maxVertices", 255);
  uint64_t h = shape_base_hash(&desc, PHYS3D_SHAPE_HULL);
  for (int i = 0; i < point_count; ++i) {
    h = hash_f32(h, points[i].x);
    h = hash_f32(h, points[i].y);
    h = hash_f32(h, points[i].z);
  }
  h = hash_i64(h, max_vertices);
  Phys3dShape *shape = shape_get_or_create(b, key);
  if (!shape) {
    SDL_free(points);
    return luaL_error(L, "phys3d_hull: out of memory");
  }
  bool recreated = B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version;
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    shape_free_heavy_data(shape);
    b3ShapeDef def = make_shape_def(&desc, shape);
    b3HullData *hull = b3CreateHull(points, point_count, max_vertices);
    if (!hull) {
      SDL_free(points);
      return luaL_error(L, "phys3d_hull: b3CreateHull failed (degenerate or "
                           "coplanar points?)");
    }
    shape->id = b3CreateHullShape(b->id, &def, hull);
    // b3CreateHullShape copies the hull into a world-owned hull database
    // (b3AddHullToDatabase -> b3CloneHull), so the temporary can be freed.
    b3DestroyHull(hull);
  } else {
    log_shape_constructor_drift("phys3d_hull", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  SDL_free(points);
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS3D_SHAPE_HULL, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static float *read_flat_numbers(lua_State *L, int idx, const char *fn,
                                const char *field_a, const char *field_b,
                                int *out_count) {
  if (!table_get_any(L, idx, field_a, field_b)) {
    luaL_error(L, "%s: %s array is required", fn, field_a);
    return NULL;
  }
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "%s: %s must be a table", fn, field_a);
    return NULL;
  }
  int t = lua_gettop(L);
  int count = (int)lua_rawlen(L, t);
  if (count <= 0) {
    lua_pop(L, 1);
    luaL_error(L, "%s: %s must not be empty", fn, field_a);
    return NULL;
  }
  float *values = (float *)SDL_malloc(sizeof(*values) * count);
  if (!values) {
    lua_pop(L, 1);
    luaL_error(L, "%s: out of memory", fn);
    return NULL;
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
    return NULL;
  }
  int t = lua_gettop(L);
  int count = (int)lua_rawlen(L, t);
  if (count <= 0) {
    lua_pop(L, 1);
    if (required)
      luaL_error(L, "%s: %s must not be empty", fn, field_a);
    return NULL;
  }
  int32_t *values = (int32_t *)SDL_malloc(sizeof(*values) * count);
  if (!values) {
    lua_pop(L, 1);
    luaL_error(L, "%s: out of memory", fn);
    return NULL;
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

static b3SurfaceMaterial *read_mesh_materials(lua_State *L, int idx,
                                              const Phys3dShapeDesc *desc,
                                              int *out_count) {
  *out_count = 0;
  if (!table_get_any(L, idx, "materials", NULL))
    return NULL;
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "phys3d_mesh: materials must be a table");
    return NULL;
  }
  int materials_idx = lua_gettop(L);
  int count = (int)lua_rawlen(L, materials_idx);
  if (count < 1 || count > 255) {
    lua_pop(L, 1);
    luaL_error(L, "phys3d_mesh: materials length must be in [1, 255]");
    return NULL;
  }
  b3SurfaceMaterial *materials =
      (b3SurfaceMaterial *)SDL_malloc(sizeof(*materials) * count);
  if (!materials) {
    lua_pop(L, 1);
    luaL_error(L, "phys3d_mesh: out of memory");
    return NULL;
  }
  for (int i = 0; i < count; ++i) {
    materials[i] = b3DefaultSurfaceMaterial();
    materials[i].friction = desc->friction;
    materials[i].restitution = desc->restitution;
    materials[i].userMaterialId = (uint64_t)desc->material_id;
    lua_rawgeti(L, materials_idx, i + 1);
    if (lua_istable(L, -1)) {
      int m = lua_gettop(L);
      materials[i].friction =
          table_number(L, m, "friction", NULL, materials[i].friction);
      materials[i].restitution =
          table_number(L, m, "restitution", NULL, materials[i].restitution);
      materials[i].userMaterialId = (uint64_t)table_int(
          L, m, "material", "materialId", (int)materials[i].userMaterialId);
      materials[i].userMaterialId =
          (uint64_t)table_int(L, m, "user_material_id", "userMaterialId",
                              (int)materials[i].userMaterialId);
    } else if (lua_isinteger(L, -1) || lua_isnumber(L, -1)) {
      materials[i].userMaterialId = (uint64_t)lua_tointeger(L, -1);
    }
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  *out_count = count;
  return materials;
}

static int l_phys3d_mesh(lua_State *L) {
  if (phys_in_callback(L, "phys3d_mesh"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  Phys3dShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  if (!desc.has_version)
    return luaL_error(L, "phys3d_mesh: explicit version is required");
  int64_t version = desc.version;
  int position_count = 0;
  float *positions = read_flat_numbers(L, 3, "phys3d_mesh", "positions", NULL,
                                       &position_count);
  if ((position_count % 3) != 0 || position_count < 9) {
    SDL_free(positions);
    return luaL_error(
        L, "phys3d_mesh: positions must hold at least 3 x/y/z triples");
  }
  int vertex_count = position_count / 3;
  int index_count = 0;
  // Indices are 0-based, matching the raw glTF accessor values that
  // load_gltf (src/gltf.c push_index_table) hands to Lua.
  int32_t *indices =
      read_flat_ints(L, 3, "phys3d_mesh", "indices", NULL, true, &index_count);
  if ((index_count % 3) != 0) {
    SDL_free(positions);
    SDL_free(indices);
    return luaL_error(L, "phys3d_mesh: indices must hold index triples");
  }
  int triangle_count = index_count / 3;
  for (int i = 0; i < index_count; ++i) {
    if (indices[i] < 0 || indices[i] >= vertex_count) {
      SDL_free(positions);
      SDL_free(indices);
      return luaL_error(L, "phys3d_mesh: index out of range (0-based)");
    }
  }
  b3Vec3 scale = table_vec3(L, 3, "scale", NULL, b3Vec3_one);
  bool weld_vertices = table_bool(L, 3, "weld_vertices", "weldVertices", false);
  float weld_tolerance =
      table_number(L, 3, "weld_tolerance", "weldTolerance", 0.0f);
  bool use_median_split =
      table_bool(L, 3, "use_median_split", "useMedianSplit", false);
  bool identify_edges =
      table_bool(L, 3, "identify_edges", "identifyEdges", true);
  int material_count = 0;
  b3SurfaceMaterial *materials =
      read_mesh_materials(L, 3, &desc, &material_count);
  int material_index_count = 0;
  int32_t *material_index_values =
      read_flat_ints(L, 3, "phys3d_mesh", "material_indices", "materialIndices",
                     false, &material_index_count);
  uint8_t *material_indices = NULL;
  if (material_index_values) {
    if (material_index_count != triangle_count) {
      SDL_free(positions);
      SDL_free(indices);
      SDL_free(materials);
      SDL_free(material_index_values);
      return luaL_error(
          L, "phys3d_mesh: material_indices length must match triangle count");
    }
    material_indices =
        (uint8_t *)SDL_malloc(sizeof(*material_indices) * triangle_count);
    if (!material_indices) {
      SDL_free(positions);
      SDL_free(indices);
      SDL_free(materials);
      SDL_free(material_index_values);
      return luaL_error(L, "phys3d_mesh: out of memory");
    }
    int limit = material_count > 0 ? material_count : 1;
    for (int i = 0; i < triangle_count; ++i) {
      int32_t m = material_index_values[i];
      if (m < 0 || m >= limit) {
        SDL_free(positions);
        SDL_free(indices);
        SDL_free(materials);
        SDL_free(material_index_values);
        SDL_free(material_indices);
        return luaL_error(L, "phys3d_mesh: material index out of range");
      }
      material_indices[i] = (uint8_t)m;
    }
  }
  SDL_free(material_index_values);

  uint64_t h = shape_base_hash(&desc, PHYS3D_SHAPE_MESH);
  for (int i = 0; i < position_count; ++i)
    h = hash_f32(h, positions[i]);
  for (int i = 0; i < index_count; ++i)
    h = hash_i64(h, indices[i]);
  h = hash_f32(h, scale.x);
  h = hash_f32(h, scale.y);
  h = hash_f32(h, scale.z);
  h = hash_bool(h, weld_vertices);
  h = hash_f32(h, weld_tolerance);
  h = hash_bool(h, identify_edges);

  Phys3dShape *shape = shape_get_or_create(b, key);
  if (!shape) {
    SDL_free(positions);
    SDL_free(indices);
    SDL_free(materials);
    SDL_free(material_indices);
    return luaL_error(L, "phys3d_mesh: out of memory");
  }
  bool recreated = B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version;
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    shape_free_heavy_data(shape);
    b3MeshDef mesh_def = {0};
    mesh_def.vertices = (b3Vec3 *)positions;
    mesh_def.vertexCount = vertex_count;
    mesh_def.indices = indices;
    mesh_def.triangleCount = triangle_count;
    mesh_def.materialIndices = material_indices;
    mesh_def.weldVertices = weld_vertices;
    mesh_def.weldTolerance = weld_tolerance;
    mesh_def.useMedianSplit = use_median_split;
    mesh_def.identifyEdges = identify_edges;
    int degenerate[16];
    for (int i = 0; i < 16; ++i)
      degenerate[i] = -1;
    // b3CreateMesh clones the input arrays into its own blob.
    b3MeshData *mesh = b3CreateMesh(&mesh_def, degenerate, 16);
    for (int i = 0; i < 16 && degenerate[i] >= 0; ++i)
      SDL_Log("phys3d_mesh('%s/%s'): degenerate triangle %d skipped", b->key,
              key, degenerate[i]);
    if (!mesh) {
      SDL_free(positions);
      SDL_free(indices);
      SDL_free(materials);
      SDL_free(material_indices);
      return luaL_error(L, "phys3d_mesh: b3CreateMesh failed");
    }
    b3ShapeDef def = make_shape_def(&desc, shape);
    if (materials) {
      // b3CreateShapeInternal copies the material array into the shape.
      def.materials = materials;
      def.materialCount = material_count;
    }
    shape->id = b3CreateMeshShape(b->id, &def, mesh, scale);
    // b3CreateMeshShape stores the mesh pointer without copying, so this
    // shape owns the data until the box3d shape is destroyed.
    shape->mesh_data = mesh;
  } else {
    log_shape_constructor_drift("phys3d_mesh", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  SDL_free(positions);
  SDL_free(indices);
  SDL_free(materials);
  SDL_free(material_indices);
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS3D_SHAPE_MESH, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static int l_phys3d_height_field(lua_State *L) {
  if (phys_in_callback(L, "phys3d_height_field"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  Phys3dShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  if (!desc.has_version)
    return luaL_error(L, "phys3d_height_field: explicit version is required");
  int64_t version = desc.version;
  int x_count = table_int(L, 3, "x_count", "xCount", 0);
  int z_count = table_int(L, 3, "z_count", "zCount", 0);
  if (x_count < 2 || z_count < 2)
    return luaL_error(L, "phys3d_height_field: x_count and z_count must be "
                         ">= 2");
  int height_count = 0;
  float *heights = read_flat_numbers(L, 3, "phys3d_height_field", "heights",
                                     NULL, &height_count);
  if (height_count != x_count * z_count) {
    SDL_free(heights);
    return luaL_error(
        L, "phys3d_height_field: heights length must be x_count * z_count");
  }
  float cell_width = table_number(L, 3, "cell_width", "cellWidth", 1.0f);
  b3Vec3 scale = {cell_width, 1.0f, cell_width};
  scale = table_vec3(L, 3, "scale", NULL, scale);
  if (scale.x <= 0.0f || scale.y <= 0.0f || scale.z <= 0.0f) {
    SDL_free(heights);
    return luaL_error(L, "phys3d_height_field: scale must be positive");
  }
  float min_height = heights[0];
  float max_height = heights[0];
  for (int i = 1; i < height_count; ++i) {
    if (heights[i] < min_height)
      min_height = heights[i];
    if (heights[i] > max_height)
      max_height = heights[i];
  }
  min_height = table_number(L, 3, "min_height", "minHeight", min_height);
  max_height = table_number(L, 3, "max_height", "maxHeight", max_height);
  if (max_height - min_height < 1e-6f)
    max_height = min_height + 1.0f;
  bool clockwise =
      table_bool(L, 3, "clockwise_winding", "clockwiseWinding", false);

  uint64_t h = shape_base_hash(&desc, PHYS3D_SHAPE_HEIGHT_FIELD);
  for (int i = 0; i < height_count; ++i)
    h = hash_f32(h, heights[i]);
  h = hash_i64(h, x_count);
  h = hash_i64(h, z_count);
  h = hash_f32(h, scale.x);
  h = hash_f32(h, scale.y);
  h = hash_f32(h, scale.z);
  h = hash_f32(h, min_height);
  h = hash_f32(h, max_height);
  h = hash_bool(h, clockwise);

  Phys3dShape *shape = shape_get_or_create(b, key);
  if (!shape) {
    SDL_free(heights);
    return luaL_error(L, "phys3d_height_field: out of memory");
  }
  bool recreated = B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version;
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    shape_free_heavy_data(shape);
    b3HeightFieldDef hf_def = {0};
    hf_def.heights = heights;
    hf_def.scale = scale;
    hf_def.countX = x_count;
    hf_def.countZ = z_count;
    hf_def.globalMinimumHeight = min_height;
    hf_def.globalMaximumHeight = max_height;
    hf_def.clockwiseWinding = clockwise;
    // b3CreateHeightField quantizes the heights into its own blob, so the
    // temporary array can be freed after creation.
    b3HeightFieldData *hf = b3CreateHeightField(&hf_def);
    if (!hf) {
      SDL_free(heights);
      return luaL_error(L, "phys3d_height_field: b3CreateHeightField failed");
    }
    b3ShapeDef def = make_shape_def(&desc, shape);
    shape->id = b3CreateHeightFieldShape(b->id, &def, hf);
    // b3CreateHeightFieldShape stores the data pointer without copying.
    shape->height_field_data = hf;
  } else {
    log_shape_constructor_drift("phys3d_height_field", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  SDL_free(heights);
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS3D_SHAPE_HEIGHT_FIELD, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

typedef struct Phys3dCompoundChildren {
  b3CompoundSphereDef *spheres;
  int sphere_count;
  b3CompoundCapsuleDef *capsules;
  int capsule_count;
  b3CompoundHullDef *hulls;
  b3BoxHull *box_hulls;
  int hull_count;
} Phys3dCompoundChildren;

static void compound_children_free(Phys3dCompoundChildren *children) {
  SDL_free(children->spheres);
  SDL_free(children->capsules);
  SDL_free(children->hulls);
  SDL_free(children->box_hulls);
  memset(children, 0, sizeof(*children));
}

static b3Transform parse_child_pose(lua_State *L, int idx) {
  b3Transform pose = {b3Vec3_zero, b3Quat_identity};
  if (!table_get_any(L, idx, "pose", NULL))
    return pose;
  if (lua_istable(L, -1)) {
    int t = lua_gettop(L);
    pose.p.x = table_number(L, t, "x", NULL, 0.0f);
    pose.p.y = table_number(L, t, "y", NULL, 0.0f);
    pose.p.z = table_number(L, t, "z", NULL, 0.0f);
    table_rotation(L, t, &pose.q);
  }
  lua_pop(L, 1);
  return pose;
}

static b3SurfaceMaterial parse_child_material(lua_State *L, int idx,
                                              const Phys3dShapeDesc *desc) {
  b3SurfaceMaterial material = b3DefaultSurfaceMaterial();
  material.friction = table_number(L, idx, "friction", NULL, desc->friction);
  material.restitution =
      table_number(L, idx, "restitution", NULL, desc->restitution);
  int material_id =
      table_int(L, idx, "material", "materialId", desc->material_id);
  material_id =
      table_int(L, idx, "user_material_id", "userMaterialId", material_id);
  material.userMaterialId = (uint64_t)material_id;
  return material;
}

static uint64_t hash_surface_material(uint64_t h,
                                      const b3SurfaceMaterial *material) {
  h = hash_f32(h, material->friction);
  h = hash_f32(h, material->restitution);
  h = hash_u64(h, material->userMaterialId);
  return h;
}

static uint64_t hash_transform(uint64_t h, b3Transform t) {
  h = hash_f32(h, t.p.x);
  h = hash_f32(h, t.p.y);
  h = hash_f32(h, t.p.z);
  h = hash_f32(h, t.q.v.x);
  h = hash_f32(h, t.q.v.y);
  h = hash_f32(h, t.q.v.z);
  h = hash_f32(h, t.q.s);
  return h;
}

// Parses desc.children into compound child defs. Supported child kinds are
// sphere / box / capsule; each child takes an optional pose and material
// overrides. Returns the accumulated constructor hash.
static uint64_t read_compound_children(lua_State *L, int idx,
                                       const Phys3dShapeDesc *desc, uint64_t h,
                                       Phys3dCompoundChildren *children) {
  memset(children, 0, sizeof(*children));
  if (!table_get_any(L, idx, "children", NULL))
    luaL_error(L, "phys3d_compound: children table is required");
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "phys3d_compound: children must be a table");
  }
  int cidx = lua_gettop(L);
  int count = (int)lua_rawlen(L, cidx);
  if (count <= 0) {
    lua_pop(L, 1);
    luaL_error(L, "phys3d_compound: children must not be empty");
  }
  children->spheres =
      (b3CompoundSphereDef *)SDL_calloc(count, sizeof(*children->spheres));
  children->capsules =
      (b3CompoundCapsuleDef *)SDL_calloc(count, sizeof(*children->capsules));
  children->hulls =
      (b3CompoundHullDef *)SDL_calloc(count, sizeof(*children->hulls));
  children->box_hulls =
      (b3BoxHull *)SDL_calloc(count, sizeof(*children->box_hulls));
  if (!children->spheres || !children->capsules || !children->hulls ||
      !children->box_hulls) {
    compound_children_free(children);
    lua_pop(L, 1);
    luaL_error(L, "phys3d_compound: out of memory");
  }
  h = hash_u64(h, (uint64_t)count);
  for (int i = 0; i < count; ++i) {
    lua_rawgeti(L, cidx, i + 1);
    if (!lua_istable(L, -1)) {
      compound_children_free(children);
      luaL_error(L, "phys3d_compound: child %d must be a table", i + 1);
    }
    int child = lua_gettop(L);
    b3Transform pose = parse_child_pose(L, child);
    b3SurfaceMaterial material = parse_child_material(L, child, desc);
    if (table_get_any(L, child, "sphere", NULL)) {
      if (!lua_istable(L, -1)) {
        compound_children_free(children);
        luaL_error(L, "phys3d_compound: sphere child must be a table");
      }
      int t = lua_gettop(L);
      float r = table_number(L, t, "r", "radius", 0.0f);
      if (r <= 0.0f) {
        compound_children_free(children);
        luaL_error(L, "phys3d_compound: sphere r must be > 0");
      }
      b3Vec3 center = table_vec3(L, t, "center", NULL, b3Vec3_zero);
      lua_pop(L, 1);
      b3CompoundSphereDef *out = &children->spheres[children->sphere_count++];
      out->sphere.center = b3TransformPoint(pose, center);
      out->sphere.radius = r;
      out->material = material;
      h = hash_u64(h, 1);
      h = hash_f32(h, out->sphere.center.x);
      h = hash_f32(h, out->sphere.center.y);
      h = hash_f32(h, out->sphere.center.z);
      h = hash_f32(h, r);
    } else if (table_get_any(L, child, "capsule", NULL)) {
      if (!lua_istable(L, -1)) {
        compound_children_free(children);
        luaL_error(L, "phys3d_compound: capsule child must be a table");
      }
      int t = lua_gettop(L);
      b3Vec3 a = table_vec3(L, t, "a", NULL, b3Vec3_zero);
      b3Vec3 c = table_vec3(L, t, "b", NULL, b3Vec3_zero);
      float r = table_number(L, t, "r", "radius", 0.0f);
      lua_pop(L, 1);
      if (r <= 0.0f) {
        compound_children_free(children);
        luaL_error(L, "phys3d_compound: capsule r must be > 0");
      }
      if (b3DistanceSquared(a, c) <= 1e-12f) {
        compound_children_free(children);
        luaL_error(L, "phys3d_compound: capsule endpoints must be distinct");
      }
      b3CompoundCapsuleDef *out =
          &children->capsules[children->capsule_count++];
      out->capsule.center1 = b3TransformPoint(pose, a);
      out->capsule.center2 = b3TransformPoint(pose, c);
      out->capsule.radius = r;
      out->material = material;
      h = hash_u64(h, 2);
      h = hash_f32(h, out->capsule.center1.x);
      h = hash_f32(h, out->capsule.center1.y);
      h = hash_f32(h, out->capsule.center1.z);
      h = hash_f32(h, out->capsule.center2.x);
      h = hash_f32(h, out->capsule.center2.y);
      h = hash_f32(h, out->capsule.center2.z);
      h = hash_f32(h, r);
    } else if (table_get_any(L, child, "box", NULL)) {
      if (!lua_istable(L, -1)) {
        compound_children_free(children);
        luaL_error(L, "phys3d_compound: box child must be a table");
      }
      int t = lua_gettop(L);
      float hx = table_number(L, t, "hx", NULL, 0.0f);
      float hy = table_number(L, t, "hy", NULL, 0.0f);
      float hz = table_number(L, t, "hz", NULL, 0.0f);
      lua_pop(L, 1);
      if (hx <= 0.0f || hy <= 0.0f || hz <= 0.0f) {
        compound_children_free(children);
        luaL_error(L, "phys3d_compound: box hx, hy and hz must be > 0");
      }
      int hull_index = children->hull_count++;
      children->box_hulls[hull_index] = b3MakeBoxHull(hx, hy, hz);
      b3CompoundHullDef *out = &children->hulls[hull_index];
      out->hull = &children->box_hulls[hull_index].base;
      out->transform = pose;
      out->material = material;
      h = hash_u64(h, 3);
      h = hash_f32(h, hx);
      h = hash_f32(h, hy);
      h = hash_f32(h, hz);
      h = hash_transform(h, pose);
    } else {
      compound_children_free(children);
      luaL_error(L,
                 "phys3d_compound: child %d must have sphere, box, or "
                 "capsule",
                 i + 1);
    }
    h = hash_surface_material(h, &material);
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  return h;
}

static int l_phys3d_compound(lua_State *L) {
  if (phys_in_callback(L, "phys3d_compound"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  const char *key = luaL_checkstring(L, 2);
  luaL_checktype(L, 3, LUA_TTABLE);
  Phys3dShapeDesc desc;
  parse_shape_desc(L, 3, &desc);
  shape_apply_density_default(b, &desc);
  if (!desc.has_version)
    return luaL_error(L, "phys3d_compound: explicit version is required");
  int64_t version = desc.version;
  // Box3D asserts that compounds live on static, non-sensor bodies.
  if (!body_is_live(b) || b3Body_GetType(b->id) != b3_staticBody)
    return luaL_error(L, "phys3d_compound: body must be static");
  if (desc.sensor)
    return luaL_error(L, "phys3d_compound: compound cannot be a sensor");

  uint64_t h = shape_base_hash(&desc, PHYS3D_SHAPE_COMPOUND);
  Phys3dCompoundChildren children;
  h = read_compound_children(L, 3, &desc, h, &children);

  Phys3dShape *shape = shape_get_or_create(b, key);
  if (!shape) {
    compound_children_free(&children);
    return luaL_error(L, "phys3d_compound: out of memory");
  }
  bool recreated = B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version;
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    shape_free_heavy_data(shape);
    b3CompoundDef compound_def = {0};
    compound_def.spheres = children.spheres;
    compound_def.sphereCount = children.sphere_count;
    compound_def.capsules = children.capsules;
    compound_def.capsuleCount = children.capsule_count;
    compound_def.hulls = children.hulls;
    compound_def.hullCount = children.hull_count;
    // b3CreateCompound clones all input data, so the child defs (and the
    // temporary box hulls they point at) can be freed after this call.
    b3CompoundData *compound = b3CreateCompound(&compound_def);
    if (!compound) {
      compound_children_free(&children);
      return luaL_error(L, "phys3d_compound: b3CreateCompound failed");
    }
    b3ShapeDef def = make_shape_def(&desc, shape);
    shape->id = b3CreateCompoundShape(b->id, &def, compound);
    // b3CreateCompoundShape stores the data pointer without copying.
    shape->compound_data = compound;
  } else {
    log_shape_constructor_drift("phys3d_compound", shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  compound_children_free(&children);
  shape_update_metadata(L, shape, 3, desc.material_id);
  shape_mark_declared(shape, PHYS3D_SHAPE_COMPOUND, h, &desc, recreated);
  push_shape_ref(L, b->world->key, b->key, shape->key);
  return 1;
}

static const char *joint_kind_name(Phys3dJointKind kind) {
  switch (kind) {
  case PHYS3D_JOINT_DISTANCE:
    return "distance";
  case PHYS3D_JOINT_FILTER:
    return "filter";
  case PHYS3D_JOINT_MOTOR:
    return "motor";
  case PHYS3D_JOINT_PARALLEL:
    return "parallel";
  case PHYS3D_JOINT_PRISMATIC:
    return "prismatic";
  case PHYS3D_JOINT_REVOLUTE:
    return "revolute";
  case PHYS3D_JOINT_SPHERICAL:
    return "spherical";
  case PHYS3D_JOINT_WELD:
    return "weld";
  case PHYS3D_JOINT_WHEEL:
    return "wheel";
  default:
    return "unknown";
  }
}

static Phys3dJointKind parse_joint_kind(lua_State *L, int idx) {
  const char *type = "revolute";
  if (table_get_any(L, idx, "type", "kind")) {
    if (lua_isstring(L, -1))
      type = lua_tostring(L, -1);
    lua_pop(L, 1);
  }
  if (strcmp(type, "distance") == 0)
    return PHYS3D_JOINT_DISTANCE;
  if (strcmp(type, "filter") == 0)
    return PHYS3D_JOINT_FILTER;
  if (strcmp(type, "motor") == 0)
    return PHYS3D_JOINT_MOTOR;
  if (strcmp(type, "parallel") == 0)
    return PHYS3D_JOINT_PARALLEL;
  if (strcmp(type, "prismatic") == 0)
    return PHYS3D_JOINT_PRISMATIC;
  if (strcmp(type, "revolute") == 0 || strcmp(type, "hinge") == 0)
    return PHYS3D_JOINT_REVOLUTE;
  if (strcmp(type, "spherical") == 0)
    return PHYS3D_JOINT_SPHERICAL;
  if (strcmp(type, "weld") == 0)
    return PHYS3D_JOINT_WELD;
  if (strcmp(type, "wheel") == 0)
    return PHYS3D_JOINT_WHEEL;
  luaL_error(L, "phys3d_joint: unknown joint type '%s'", type);
  return PHYS3D_JOINT_REVOLUTE;
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

static b3Vec3 nested_vec3(lua_State *L, int idx, const char *table_name,
                          const char *a, const char *b, b3Vec3 def) {
  b3Vec3 out = def;
  if (table_get_any(L, idx, table_name, NULL)) {
    if (lua_istable(L, -1))
      out = table_vec3(L, lua_gettop(L), a, b, out);
    lua_pop(L, 1);
  }
  return out;
}

static Phys3dBody *joint_body_from_value(lua_State *L, Phys3dWorld *w, int idx,
                                         const char *field_name) {
  Phys3dBody *b = NULL;
  if (is_ref(L, idx, "phys3d_body")) {
    b = check_body(L, idx);
  } else if (lua_isstring(L, idx)) {
    b = body_get(w, lua_tostring(L, idx));
  }
  if (!b)
    luaL_error(L, "phys3d_joint: missing body field '%s'", field_name);
  if (b->world != w)
    luaL_error(L, "phys3d_joint: body '%s' belongs to another world", b->key);
  if (!body_is_live(b) || b->seen_generation != w->generation)
    luaL_error(L, "phys3d_joint: declare live body '%s' before joint", b->key);
  return b;
}

static Phys3dBody *joint_body_field(lua_State *L, Phys3dWorld *w, int idx,
                                    const char *a, const char *b,
                                    const char *c) {
  if (!table_get_any(L, idx, a, b)) {
    if (!c || !table_get_any(L, idx, c, NULL))
      luaL_error(L, "phys3d_joint: missing body field '%s'", a);
  }
  Phys3dBody *body = joint_body_from_value(L, w, lua_gettop(L), a);
  lua_pop(L, 1);
  return body;
}

// The joint axis is expressed by rotating the joint's canonical local axis
// (frame A local x for slide axes, local z for hinge/cone axes) onto the
// given world axis.
static b3Vec3 joint_canonical_axis(Phys3dJointKind kind) {
  switch (kind) {
  case PHYS3D_JOINT_PRISMATIC:
  case PHYS3D_JOINT_WHEEL:
    return b3Vec3_axisX;
  default:
    return b3Vec3_axisZ;
  }
}

// Reads a local joint frame { x, y, z, quat = {..} | euler = {..} }.
static bool table_local_frame(lua_State *L, int idx, const char *a,
                              const char *b, b3Transform *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_istable(L, -1);
  if (ok) {
    int t = lua_gettop(L);
    b3Transform frame = {b3Vec3_zero, b3Quat_identity};
    frame.p.x = table_number(L, t, "x", NULL, 0.0f);
    frame.p.y = table_number(L, t, "y", NULL, 0.0f);
    frame.p.z = table_number(L, t, "z", NULL, 0.0f);
    table_rotation(L, t, &frame.q);
    *out = frame;
  }
  lua_pop(L, 1);
  return ok;
}

static bool table_world_anchor(lua_State *L, int idx, const char *a,
                               const char *b, b3Vec3 *out) {
  if (!table_get_any(L, idx, a, b))
    return false;
  bool ok = lua_istable(L, -1);
  if (ok)
    *out = value_vec3(L, lua_gettop(L), b3Vec3_zero);
  lua_pop(L, 1);
  return ok;
}

// Builds the local joint frame for one body: world anchor + world axis are
// localized against the body transform; an explicit local frame_a/frame_b
// wins over both. The raw inputs are recorded for the constructor hash.
static void joint_frame_for_body(lua_State *L, int idx, Phys3dBody *body,
                                 const char *anchor_a, const char *anchor_b,
                                 const char *frame_a, const char *frame_b,
                                 bool has_axis, b3Quat world_rot,
                                 b3Transform *out, bool *has_anchor,
                                 b3Vec3 *anchor, bool *has_frame) {
  b3WorldTransform body_transform = b3Body_GetTransform(body->id);
  out->p = b3Vec3_zero;
  out->q = has_axis ? b3NormalizeQuat(b3InvMulQuat(body_transform.q, world_rot))
                    : b3Quat_identity;
  *anchor = b3Vec3_zero;
  *has_anchor = table_world_anchor(L, idx, anchor_a, anchor_b, anchor);
  if (*has_anchor)
    out->p = b3Body_GetLocalPoint(body->id, b3ToPos(*anchor));
  *has_frame = table_local_frame(L, idx, frame_a, frame_b, out);
}

static bool table_quat_field(lua_State *L, int idx, const char *a,
                             const char *b, b3Quat *out) {
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

static void parse_joint_desc(lua_State *L, Phys3dWorld *w, int idx,
                             Phys3dJointDesc *desc) {
  luaL_checktype(L, idx, LUA_TTABLE);
  memset(desc, 0, sizeof(*desc));
  desc->kind = parse_joint_kind(L, idx);
  desc->local_frame_a = (b3Transform){b3Vec3_zero, b3Quat_identity};
  desc->local_frame_b = (b3Transform){b3Vec3_zero, b3Quat_identity};
  desc->force_threshold = FLT_MAX;
  desc->torque_threshold = FLT_MAX;
  desc->length = 1.0f;
  desc->max_length = FLT_MAX;
  desc->lower_spring_force = -FLT_MAX;
  desc->upper_spring_force = FLT_MAX;
  desc->target_rotation = b3Quat_identity;
  switch (desc->kind) {
  case PHYS3D_JOINT_PARALLEL:
    desc->hertz = 1.0f;
    desc->damping_ratio = 1.0f;
    desc->max_torque = FLT_MAX;
    break;
  case PHYS3D_JOINT_WHEEL:
    desc->enable_spring = true;
    desc->hertz = 1.0f;
    desc->damping_ratio = 0.7f;
    desc->steering_hertz = 1.0f;
    desc->steering_damping_ratio = 0.7f;
    break;
  default:
    break;
  }

  int64_t v = 0;
  if (table_has_int(L, idx, "version", NULL, &v)) {
    desc->version = v;
    desc->has_version = true;
  }
  desc->body_a = joint_body_field(L, w, idx, "a", "body_a", "bodyA");
  desc->body_b = joint_body_field(L, w, idx, "b", "body_b", "bodyB");

  bool has_axis = false;
  b3Vec3 axis = b3Vec3_axisZ;
  if (table_get_any(L, idx, "axis", NULL)) {
    if (lua_istable(L, -1)) {
      axis = value_vec3(L, lua_gettop(L), b3Vec3_zero);
      has_axis = true;
    }
    lua_pop(L, 1);
  }
  b3Quat world_rot = b3Quat_identity;
  if (has_axis) {
    if (b3LengthSquared(axis) <= 1e-12f)
      luaL_error(L, "phys3d_joint: axis must be non-zero");
    axis = b3Normalize(axis);
    world_rot =
        b3ComputeQuatBetweenUnitVectors(joint_canonical_axis(desc->kind), axis);
  }
  desc->has_axis = has_axis;
  desc->axis = axis;
  joint_frame_for_body(L, idx, desc->body_a, "anchor_a", "anchorA", "frame_a",
                       "frameA", has_axis, world_rot, &desc->local_frame_a,
                       &desc->has_anchor_a, &desc->anchor_a,
                       &desc->has_frame_a);
  joint_frame_for_body(L, idx, desc->body_b, "anchor_b", "anchorB", "frame_b",
                       "frameB", has_axis, world_rot, &desc->local_frame_b,
                       &desc->has_anchor_b, &desc->anchor_b,
                       &desc->has_frame_b);

  desc->collide_connected =
      table_bool(L, idx, "collide_connected", "collideConnected", false);
  desc->force_threshold = table_number(L, idx, "force_threshold",
                                       "forceThreshold", desc->force_threshold);
  desc->torque_threshold = table_number(
      L, idx, "torque_threshold", "torqueThreshold", desc->torque_threshold);
  float tuning = 0.0f;
  if (table_number_optional(L, idx, "constraint_hertz", "constraintHertz",
                            &tuning)) {
    desc->has_constraint_tuning = true;
    desc->constraint_hertz = tuning;
    desc->constraint_damping_ratio = 2.0f;
  }
  if (table_number_optional(L, idx, "constraint_damping_ratio",
                            "constraintDampingRatio", &tuning)) {
    if (!desc->has_constraint_tuning) {
      desc->has_constraint_tuning = true;
      desc->constraint_hertz = 60.0f;
    }
    desc->constraint_damping_ratio = tuning;
  }

  desc->length = table_number(L, idx, "length", NULL, desc->length);
  desc->min_length =
      table_number(L, idx, "min_length", "minLength", desc->min_length);
  desc->max_length =
      table_number(L, idx, "max_length", "maxLength", desc->max_length);
  desc->lower = table_number(L, idx, "lower", NULL, desc->lower);
  desc->upper = table_number(L, idx, "upper", NULL, desc->upper);
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
  desc->target_angle =
      table_number(L, idx, "target_angle", "targetAngle", desc->target_angle);
  desc->target_translation =
      table_number(L, idx, "target_translation", "targetTranslation",
                   desc->target_translation);
  desc->enable_spring =
      table_bool(L, idx, "enable_spring", "enableSpring", desc->enable_spring);
  desc->enable_limit =
      table_bool(L, idx, "enable_limit", "enableLimit", desc->enable_limit);
  desc->enable_motor =
      table_bool(L, idx, "enable_motor", "enableMotor", desc->enable_motor);
  desc->lower_spring_force =
      table_number(L, idx, "lower_spring_force", "lowerSpringForce",
                   desc->lower_spring_force);
  desc->upper_spring_force =
      table_number(L, idx, "upper_spring_force", "upperSpringForce",
                   desc->upper_spring_force);
  desc->linear_velocity = table_vec3(L, idx, "linear_velocity",
                                     "linearVelocity", desc->linear_velocity);
  desc->angular_velocity = table_vec3(
      L, idx, "angular_velocity", "angularVelocity", desc->angular_velocity);
  desc->max_velocity_force =
      table_number(L, idx, "max_velocity_force", "maxVelocityForce",
                   desc->max_velocity_force);
  desc->max_velocity_torque =
      table_number(L, idx, "max_velocity_torque", "maxVelocityTorque",
                   desc->max_velocity_torque);
  desc->max_spring_force = table_number(
      L, idx, "max_spring_force", "maxSpringForce", desc->max_spring_force);
  desc->max_spring_torque = table_number(
      L, idx, "max_spring_torque", "maxSpringTorque", desc->max_spring_torque);
  table_quat_field(L, idx, "target_rotation", "targetRotation",
                   &desc->target_rotation);
  desc->enable_cone_limit = table_bool(
      L, idx, "enable_cone_limit", "enableConeLimit", desc->enable_cone_limit);
  desc->cone_angle =
      table_number(L, idx, "cone_angle", "coneAngle", desc->cone_angle);
  desc->enable_twist_limit =
      table_bool(L, idx, "enable_twist_limit", "enableTwistLimit",
                 desc->enable_twist_limit);
  desc->lower_twist_angle = table_number(
      L, idx, "lower_twist_angle", "lowerTwistAngle", desc->lower_twist_angle);
  desc->upper_twist_angle = table_number(
      L, idx, "upper_twist_angle", "upperTwistAngle", desc->upper_twist_angle);
  desc->motor_velocity = table_vec3(L, idx, "motor_velocity", "motorVelocity",
                                    desc->motor_velocity);

  // Wheel joints alias the generic spring/limit/motor names onto the
  // suspension and spin motor; the box3d-specific names below win.
  desc->enable_spring =
      table_bool(L, idx, "enable_suspension_spring", "enableSuspensionSpring",
                 desc->enable_spring);
  desc->hertz =
      table_number(L, idx, "suspension_hertz", "suspensionHertz", desc->hertz);
  desc->damping_ratio =
      table_number(L, idx, "suspension_damping_ratio", "suspensionDampingRatio",
                   desc->damping_ratio);
  desc->enable_limit = table_bool(L, idx, "enable_suspension_limit",
                                  "enableSuspensionLimit", desc->enable_limit);
  desc->lower = table_number(L, idx, "lower_suspension_limit",
                             "lowerSuspensionLimit", desc->lower);
  desc->upper = table_number(L, idx, "upper_suspension_limit",
                             "upperSuspensionLimit", desc->upper);
  desc->enable_motor = table_bool(L, idx, "enable_spin_motor",
                                  "enableSpinMotor", desc->enable_motor);
  desc->max_torque = table_number(L, idx, "max_spin_torque", "maxSpinTorque",
                                  desc->max_torque);
  desc->motor_speed =
      table_number(L, idx, "spin_speed", "spinSpeed", desc->motor_speed);
  desc->enable_steering = table_bool(L, idx, "enable_steering",
                                     "enableSteering", desc->enable_steering);
  desc->steering_hertz = table_number(L, idx, "steering_hertz", "steeringHertz",
                                      desc->steering_hertz);
  desc->steering_damping_ratio =
      table_number(L, idx, "steering_damping_ratio", "steeringDampingRatio",
                   desc->steering_damping_ratio);
  desc->target_steering_angle =
      table_number(L, idx, "target_steering_angle", "targetSteeringAngle",
                   desc->target_steering_angle);
  desc->max_steering_torque =
      table_number(L, idx, "max_steering_torque", "maxSteeringTorque",
                   desc->max_steering_torque);
  desc->enable_steering_limit =
      table_bool(L, idx, "enable_steering_limit", "enableSteeringLimit",
                 desc->enable_steering_limit);
  desc->lower_steering_limit =
      table_number(L, idx, "lower_steering_limit", "lowerSteeringLimit",
                   desc->lower_steering_limit);
  desc->upper_steering_limit =
      table_number(L, idx, "upper_steering_limit", "upperSteeringLimit",
                   desc->upper_steering_limit);

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
  desc->cone_angle = nested_number(L, idx, "limit", "cone_angle", "coneAngle",
                                   desc->cone_angle);

  desc->enable_motor =
      nested_bool(L, idx, "motor", "enabled", NULL, desc->enable_motor);
  desc->motor_speed =
      nested_number(L, idx, "motor", "speed", NULL, desc->motor_speed);
  desc->max_force =
      nested_number(L, idx, "motor", "max_force", "maxForce", desc->max_force);
  desc->max_torque = nested_number(L, idx, "motor", "max_torque", "maxTorque",
                                   desc->max_torque);
  desc->motor_velocity = nested_vec3(L, idx, "motor", "velocity",
                                     "motor_velocity", desc->motor_velocity);
}

static uint64_t joint_constructor_hash(const Phys3dJointDesc *desc) {
  uint64_t h = hash_init();
  h = hash_u64(h, (uint64_t)desc->kind);
  h = hash_cstr(h, desc->body_a ? desc->body_a->key : "");
  h = hash_cstr(h, desc->body_b ? desc->body_b->key : "");
  h = hash_bool(h, desc->collide_connected);
  h = hash_bool(h, desc->has_axis);
  if (desc->has_axis) {
    h = hash_f32(h, desc->axis.x);
    h = hash_f32(h, desc->axis.y);
    h = hash_f32(h, desc->axis.z);
  }
  h = hash_bool(h, desc->has_anchor_a);
  if (desc->has_anchor_a) {
    h = hash_f32(h, desc->anchor_a.x);
    h = hash_f32(h, desc->anchor_a.y);
    h = hash_f32(h, desc->anchor_a.z);
  }
  h = hash_bool(h, desc->has_anchor_b);
  if (desc->has_anchor_b) {
    h = hash_f32(h, desc->anchor_b.x);
    h = hash_f32(h, desc->anchor_b.y);
    h = hash_f32(h, desc->anchor_b.z);
  }
  h = hash_bool(h, desc->has_frame_a);
  if (desc->has_frame_a)
    h = hash_transform(h, desc->local_frame_a);
  h = hash_bool(h, desc->has_frame_b);
  if (desc->has_frame_b)
    h = hash_transform(h, desc->local_frame_b);
  return h;
}

static int64_t joint_effective_version(const Phys3dJointDesc *desc,
                                       uint64_t fallback_hash) {
  if (desc->has_version)
    return desc->version;
  return (int64_t)fallback_hash;
}

static void log_joint_constructor_drift(Phys3dJoint *j, uint64_t hash) {
  if (j->constructor_hash == hash || j->constructor_warned)
    return;
  SDL_Log("phys3d_joint('%s'): constructor fields changed without version "
          "bump",
          j->key);
  j->constructor_warned = true;
}

static void joint_mark_declared(Phys3dJoint *j, const Phys3dJointDesc *desc,
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

static void joint_apply_runtime(Phys3dJoint *j, const Phys3dJointDesc *desc) {
  switch (desc->kind) {
  case PHYS3D_JOINT_DISTANCE:
    b3DistanceJoint_SetLength(j->id, desc->length);
    b3DistanceJoint_EnableSpring(j->id, desc->enable_spring);
    b3DistanceJoint_SetSpringHertz(j->id, desc->hertz);
    b3DistanceJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b3DistanceJoint_SetSpringForceRange(j->id, desc->lower_spring_force,
                                        desc->upper_spring_force);
    b3DistanceJoint_EnableLimit(j->id, desc->enable_limit);
    b3DistanceJoint_SetLengthRange(j->id, desc->min_length, desc->max_length);
    b3DistanceJoint_EnableMotor(j->id, desc->enable_motor);
    b3DistanceJoint_SetMotorSpeed(j->id, desc->motor_speed);
    b3DistanceJoint_SetMaxMotorForce(j->id, desc->max_force);
    break;
  case PHYS3D_JOINT_MOTOR:
    b3MotorJoint_SetLinearVelocity(j->id, desc->linear_velocity);
    b3MotorJoint_SetAngularVelocity(j->id, desc->angular_velocity);
    b3MotorJoint_SetMaxVelocityForce(j->id, desc->max_velocity_force);
    b3MotorJoint_SetMaxVelocityTorque(j->id, desc->max_velocity_torque);
    b3MotorJoint_SetLinearHertz(j->id, desc->linear_hertz);
    b3MotorJoint_SetLinearDampingRatio(j->id, desc->linear_damping_ratio);
    b3MotorJoint_SetAngularHertz(j->id, desc->angular_hertz);
    b3MotorJoint_SetAngularDampingRatio(j->id, desc->angular_damping_ratio);
    b3MotorJoint_SetMaxSpringForce(j->id, desc->max_spring_force);
    b3MotorJoint_SetMaxSpringTorque(j->id, desc->max_spring_torque);
    break;
  case PHYS3D_JOINT_PARALLEL:
    b3ParallelJoint_SetSpringHertz(j->id, desc->hertz);
    b3ParallelJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b3ParallelJoint_SetMaxTorque(j->id, desc->max_torque);
    break;
  case PHYS3D_JOINT_PRISMATIC:
    b3PrismaticJoint_EnableSpring(j->id, desc->enable_spring);
    b3PrismaticJoint_SetSpringHertz(j->id, desc->hertz);
    b3PrismaticJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b3PrismaticJoint_SetTargetTranslation(j->id, desc->target_translation);
    b3PrismaticJoint_EnableLimit(j->id, desc->enable_limit);
    b3PrismaticJoint_SetLimits(j->id, desc->lower, desc->upper);
    b3PrismaticJoint_EnableMotor(j->id, desc->enable_motor);
    b3PrismaticJoint_SetMotorSpeed(j->id, desc->motor_speed);
    b3PrismaticJoint_SetMaxMotorForce(j->id, desc->max_force);
    break;
  case PHYS3D_JOINT_REVOLUTE:
    b3RevoluteJoint_EnableSpring(j->id, desc->enable_spring);
    b3RevoluteJoint_SetSpringHertz(j->id, desc->hertz);
    b3RevoluteJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b3RevoluteJoint_SetTargetAngle(j->id, desc->target_angle);
    b3RevoluteJoint_EnableLimit(j->id, desc->enable_limit);
    b3RevoluteJoint_SetLimits(j->id, desc->lower, desc->upper);
    b3RevoluteJoint_EnableMotor(j->id, desc->enable_motor);
    b3RevoluteJoint_SetMotorSpeed(j->id, desc->motor_speed);
    b3RevoluteJoint_SetMaxMotorTorque(j->id, desc->max_torque);
    break;
  case PHYS3D_JOINT_SPHERICAL:
    b3SphericalJoint_EnableSpring(j->id, desc->enable_spring);
    b3SphericalJoint_SetSpringHertz(j->id, desc->hertz);
    b3SphericalJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b3SphericalJoint_SetTargetRotation(j->id, desc->target_rotation);
    b3SphericalJoint_EnableConeLimit(j->id, desc->enable_cone_limit);
    b3SphericalJoint_SetConeLimit(j->id, desc->cone_angle);
    b3SphericalJoint_EnableTwistLimit(j->id, desc->enable_twist_limit);
    b3SphericalJoint_SetTwistLimits(j->id, desc->lower_twist_angle,
                                    desc->upper_twist_angle);
    b3SphericalJoint_EnableMotor(j->id, desc->enable_motor);
    b3SphericalJoint_SetMotorVelocity(j->id, desc->motor_velocity);
    b3SphericalJoint_SetMaxMotorTorque(j->id, desc->max_torque);
    break;
  case PHYS3D_JOINT_WELD:
    b3WeldJoint_SetLinearHertz(j->id, desc->linear_hertz);
    b3WeldJoint_SetLinearDampingRatio(j->id, desc->linear_damping_ratio);
    b3WeldJoint_SetAngularHertz(j->id, desc->angular_hertz);
    b3WeldJoint_SetAngularDampingRatio(j->id, desc->angular_damping_ratio);
    break;
  case PHYS3D_JOINT_WHEEL:
    b3WheelJoint_EnableSuspension(j->id, desc->enable_spring);
    b3WheelJoint_SetSuspensionHertz(j->id, desc->hertz);
    b3WheelJoint_SetSuspensionDampingRatio(j->id, desc->damping_ratio);
    b3WheelJoint_EnableSuspensionLimit(j->id, desc->enable_limit);
    b3WheelJoint_SetSuspensionLimits(j->id, desc->lower, desc->upper);
    b3WheelJoint_EnableSpinMotor(j->id, desc->enable_motor);
    b3WheelJoint_SetSpinMotorSpeed(j->id, desc->motor_speed);
    b3WheelJoint_SetMaxSpinTorque(j->id, desc->max_torque);
    b3WheelJoint_EnableSteering(j->id, desc->enable_steering);
    b3WheelJoint_SetSteeringHertz(j->id, desc->steering_hertz);
    b3WheelJoint_SetSteeringDampingRatio(j->id, desc->steering_damping_ratio);
    b3WheelJoint_SetTargetSteeringAngle(j->id, desc->target_steering_angle);
    b3WheelJoint_SetMaxSteeringTorque(j->id, desc->max_steering_torque);
    b3WheelJoint_EnableSteeringLimit(j->id, desc->enable_steering_limit);
    b3WheelJoint_SetSteeringLimits(j->id, desc->lower_steering_limit,
                                   desc->upper_steering_limit);
    break;
  case PHYS3D_JOINT_FILTER:
  default:
    break;
  }
  b3Joint_SetForceThreshold(j->id, desc->force_threshold);
  b3Joint_SetTorqueThreshold(j->id, desc->torque_threshold);
  if (desc->has_constraint_tuning)
    b3Joint_SetConstraintTuning(j->id, desc->constraint_hertz,
                                desc->constraint_damping_ratio);
}

static void joint_def_apply_base(b3JointDef *base, Phys3dJoint *j,
                                 const Phys3dJointDesc *desc) {
  base->userData = j;
  base->bodyIdA = desc->body_a->id;
  base->bodyIdB = desc->body_b->id;
  base->localFrameA = desc->local_frame_a;
  base->localFrameB = desc->local_frame_b;
  base->forceThreshold = desc->force_threshold;
  base->torqueThreshold = desc->torque_threshold;
  base->collideConnected = desc->collide_connected;
  if (desc->has_constraint_tuning) {
    base->constraintHertz = desc->constraint_hertz;
    base->constraintDampingRatio = desc->constraint_damping_ratio;
  }
}

static void joint_create(lua_State *L, Phys3dWorld *w, Phys3dJoint *j,
                         const Phys3dJointDesc *desc, uint64_t constructor_hash,
                         int64_t version) {
  switch (desc->kind) {
  case PHYS3D_JOINT_DISTANCE: {
    b3DistanceJointDef def = b3DefaultDistanceJointDef();
    joint_def_apply_base(&def.base, j, desc);
    def.length = desc->length;
    j->id = b3CreateDistanceJoint(w->id, &def);
    break;
  }
  case PHYS3D_JOINT_FILTER: {
    b3FilterJointDef def = b3DefaultFilterJointDef();
    joint_def_apply_base(&def.base, j, desc);
    j->id = b3CreateFilterJoint(w->id, &def);
    break;
  }
  case PHYS3D_JOINT_MOTOR: {
    b3MotorJointDef def = b3DefaultMotorJointDef();
    joint_def_apply_base(&def.base, j, desc);
    j->id = b3CreateMotorJoint(w->id, &def);
    break;
  }
  case PHYS3D_JOINT_PARALLEL: {
    b3ParallelJointDef def = b3DefaultParallelJointDef();
    joint_def_apply_base(&def.base, j, desc);
    j->id = b3CreateParallelJoint(w->id, &def);
    break;
  }
  case PHYS3D_JOINT_PRISMATIC: {
    b3PrismaticJointDef def = b3DefaultPrismaticJointDef();
    joint_def_apply_base(&def.base, j, desc);
    j->id = b3CreatePrismaticJoint(w->id, &def);
    break;
  }
  case PHYS3D_JOINT_REVOLUTE: {
    b3RevoluteJointDef def = b3DefaultRevoluteJointDef();
    joint_def_apply_base(&def.base, j, desc);
    j->id = b3CreateRevoluteJoint(w->id, &def);
    break;
  }
  case PHYS3D_JOINT_SPHERICAL: {
    b3SphericalJointDef def = b3DefaultSphericalJointDef();
    joint_def_apply_base(&def.base, j, desc);
    j->id = b3CreateSphericalJoint(w->id, &def);
    break;
  }
  case PHYS3D_JOINT_WELD: {
    b3WeldJointDef def = b3DefaultWeldJointDef();
    joint_def_apply_base(&def.base, j, desc);
    j->id = b3CreateWeldJoint(w->id, &def);
    break;
  }
  case PHYS3D_JOINT_WHEEL: {
    b3WheelJointDef def = b3DefaultWheelJointDef();
    joint_def_apply_base(&def.base, j, desc);
    j->id = b3CreateWheelJoint(w->id, &def);
    break;
  }
  default:
    break;
  }
  if (B3_IS_NULL(j->id) || !b3Joint_IsValid(j->id))
    luaL_error(L, "phys3d_joint: b3Create%sJoint failed",
               joint_kind_name(desc->kind));
  b3Joint_SetUserData(j->id, j);
  joint_mark_declared(j, desc, constructor_hash, version, true);
  joint_apply_runtime(j, desc);
}

static int l_phys3d_joint(lua_State *L) {
  if (phys_in_callback(L, "phys3d_joint"))
    return 0;
  Phys3dWorld *w = check_world(L, 1);
  const char *key = luaL_checkstring(L, 2);
  Phys3dJointDesc desc;
  parse_joint_desc(L, w, 3, &desc);
  if (!w->begun)
    return luaL_error(L, "phys3d_joint: call phys3d_begin(world) first");
  Phys3dJoint *j = joint_get_or_create(w, key);
  if (!j)
    return luaL_error(L, "phys3d_joint: out of memory");
  uint64_t constructor_hash = joint_constructor_hash(&desc);
  int64_t version = joint_effective_version(&desc, constructor_hash);
  bool endpoints_changed = j->body_a != desc.body_a || j->body_b != desc.body_b;
  bool kind_changed = j->kind != desc.kind;
  bool recreated = !joint_is_live(j) || j->version != version ||
                   (!desc.has_version && (kind_changed || endpoints_changed));
  if (recreated) {
    if (joint_is_live(j))
      b3DestroyJoint(j->id, true);
    j->id = b3_nullJointId;
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

static void command_read_point(lua_State *L, int idx, Phys3dBody *b,
                               Phys3dCommand *cmd) {
  if (!lua_istable(L, idx))
    return;
  b3Vec3 point = b3ToVec3(b3Body_GetWorldCenterOfMass(b->id));
  bool has_point = false;
  if (table_get_any(L, idx, "point", NULL)) {
    if (lua_istable(L, -1)) {
      point = value_vec3(L, lua_gettop(L), point);
      has_point = true;
    }
    lua_pop(L, 1);
  }
  float out = 0.0f;
  if (table_number_optional(L, idx, "px", NULL, &out)) {
    point.x = out;
    has_point = true;
  }
  if (table_number_optional(L, idx, "py", NULL, &out)) {
    point.y = out;
    has_point = true;
  }
  if (table_number_optional(L, idx, "pz", NULL, &out)) {
    point.z = out;
    has_point = true;
  }
  cmd->point = point;
  cmd->has_point = has_point;
}

static int l_phys3d_add_force(lua_State *L) {
  if (phys_in_callback(L, "phys3d_add_force"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  check_live_body(L, b, "phys3d_add_force");
  luaL_checktype(L, 2, LUA_TTABLE);
  Phys3dCommand *cmd = command_queue_push(
      L, b->world, b, PHYS3D_COMMAND_ADD_FORCE, "phys3d_add_force");
  cmd->vector = value_vec3(L, 2, b3Vec3_zero);
  command_read_point(L, 3, b, cmd);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys3d_add_force_center(lua_State *L) {
  if (phys_in_callback(L, "phys3d_add_force_center"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  check_live_body(L, b, "phys3d_add_force_center");
  luaL_checktype(L, 2, LUA_TTABLE);
  Phys3dCommand *cmd =
      command_queue_push(L, b->world, b, PHYS3D_COMMAND_ADD_FORCE_CENTER,
                         "phys3d_add_force_center");
  cmd->vector = value_vec3(L, 2, b3Vec3_zero);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys3d_add_impulse(lua_State *L) {
  if (phys_in_callback(L, "phys3d_add_impulse"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  check_live_body(L, b, "phys3d_add_impulse");
  luaL_checktype(L, 2, LUA_TTABLE);
  Phys3dCommand *cmd = command_queue_push(
      L, b->world, b, PHYS3D_COMMAND_ADD_IMPULSE, "phys3d_add_impulse");
  cmd->vector = value_vec3(L, 2, b3Vec3_zero);
  command_read_point(L, 3, b, cmd);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys3d_add_impulse_center(lua_State *L) {
  if (phys_in_callback(L, "phys3d_add_impulse_center"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  check_live_body(L, b, "phys3d_add_impulse_center");
  luaL_checktype(L, 2, LUA_TTABLE);
  Phys3dCommand *cmd =
      command_queue_push(L, b->world, b, PHYS3D_COMMAND_ADD_IMPULSE_CENTER,
                         "phys3d_add_impulse_center");
  cmd->vector = value_vec3(L, 2, b3Vec3_zero);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys3d_add_torque(lua_State *L) {
  if (phys_in_callback(L, "phys3d_add_torque"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  check_live_body(L, b, "phys3d_add_torque");
  luaL_checktype(L, 2, LUA_TTABLE);
  Phys3dCommand *cmd = command_queue_push(
      L, b->world, b, PHYS3D_COMMAND_ADD_TORQUE, "phys3d_add_torque");
  cmd->vector = value_vec3(L, 2, b3Vec3_zero);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys3d_add_angular_impulse(lua_State *L) {
  if (phys_in_callback(L, "phys3d_add_angular_impulse"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  check_live_body(L, b, "phys3d_add_angular_impulse");
  luaL_checktype(L, 2, LUA_TTABLE);
  Phys3dCommand *cmd =
      command_queue_push(L, b->world, b, PHYS3D_COMMAND_ADD_ANGULAR_IMPULSE,
                         "phys3d_add_angular_impulse");
  cmd->vector = value_vec3(L, 2, b3Vec3_zero);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys3d_set_velocity(lua_State *L) {
  if (phys_in_callback(L, "phys3d_set_velocity"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  check_live_body(L, b, "phys3d_set_velocity");
  luaL_checktype(L, 2, LUA_TTABLE);
  Phys3dCommand *cmd = command_queue_push(
      L, b->world, b, PHYS3D_COMMAND_SET_VELOCITY, "phys3d_set_velocity");
  cmd->vector = value_vec3_optional(L, 2, b3Vec3_zero, &cmd->has_x, &cmd->has_y,
                                    &cmd->has_z);
  float out = 0.0f;
  if (table_number_optional(L, 2, "vx", NULL, &out)) {
    cmd->vector.x = out;
    cmd->has_x = true;
  }
  if (table_number_optional(L, 2, "vy", NULL, &out)) {
    cmd->vector.y = out;
    cmd->has_y = true;
  }
  if (table_number_optional(L, 2, "vz", NULL, &out)) {
    cmd->vector.z = out;
    cmd->has_z = true;
  }
  if (table_number_optional(L, 2, "wx", NULL, &out)) {
    cmd->angular.x = out;
    cmd->has_wx = true;
  }
  if (table_number_optional(L, 2, "wy", NULL, &out)) {
    cmd->angular.y = out;
    cmd->has_wy = true;
  }
  if (table_number_optional(L, 2, "wz", NULL, &out)) {
    cmd->angular.z = out;
    cmd->has_wz = true;
  }
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys3d_teleport(lua_State *L) {
  if (phys_in_callback(L, "phys3d_teleport"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  check_live_body(L, b, "phys3d_teleport");
  luaL_checktype(L, 2, LUA_TTABLE);
  Phys3dCommand *cmd = command_queue_push(
      L, b->world, b, PHYS3D_COMMAND_TELEPORT, "phys3d_teleport");
  float out = 0.0f;
  if (table_number_optional(L, 2, "x", NULL, &out)) {
    cmd->transform.p.x = out;
    cmd->has_x = true;
  }
  if (table_number_optional(L, 2, "y", NULL, &out)) {
    cmd->transform.p.y = out;
    cmd->has_y = true;
  }
  if (table_number_optional(L, 2, "z", NULL, &out)) {
    cmd->transform.p.z = out;
    cmd->has_z = true;
  }
  cmd->has_rotation = table_rotation(L, 2, &cmd->transform.q);
  cmd->wake = opt_wake(L, 3, true);
  return 0;
}

static int l_phys3d_set_target(lua_State *L) {
  if (phys_in_callback(L, "phys3d_set_target"))
    return 0;
  Phys3dBody *b = check_body(L, 1);
  check_live_body(L, b, "phys3d_set_target");
  luaL_checktype(L, 2, LUA_TTABLE);
  Phys3dCommand *cmd = command_queue_push(
      L, b->world, b, PHYS3D_COMMAND_SET_TARGET, "phys3d_set_target");
  float out = 0.0f;
  if (table_number_optional(L, 2, "x", NULL, &out)) {
    cmd->transform.p.x = out;
    cmd->has_x = true;
  }
  if (table_number_optional(L, 2, "y", NULL, &out)) {
    cmd->transform.p.y = out;
    cmd->has_y = true;
  }
  if (table_number_optional(L, 2, "z", NULL, &out)) {
    cmd->transform.p.z = out;
    cmd->has_z = true;
  }
  cmd->has_rotation = table_rotation(L, 2, &cmd->transform.q);
  cmd->time_step = b->world ? b->world->fixed_dt : 1.0f / 60.0f;
  cmd->time_step = table_number(L, 2, "dt", NULL, cmd->time_step);
  cmd->time_step = table_number(L, 2, "time_step", "timeStep", cmd->time_step);
  if (cmd->time_step <= 0.0f)
    cmd->time_step = b->world ? b->world->fixed_dt : 1.0f / 60.0f;
  cmd->wake = table_bool(L, 2, "wake", NULL, true);
  return 0;
}

static bool apply_body_command(Phys3dWorld *w, const Phys3dCommand *cmd) {
  Phys3dBody *b = body_get(w, cmd->body_key);
  if (!body_is_live(b))
    return false;
  if (cmd->body_id_key != 0 && b3StoreBodyId(b->id) != cmd->body_id_key) {
    SDL_Log("phys3d_step: dropped stale command for recreated body '%s'",
            cmd->body_key ? cmd->body_key : "?");
    return false;
  }

  switch (cmd->kind) {
  case PHYS3D_COMMAND_ADD_FORCE: {
    b3Pos point = cmd->has_point ? b3ToPos(cmd->point)
                                 : b3Body_GetWorldCenterOfMass(b->id);
    b3Body_ApplyForce(b->id, cmd->vector, point, cmd->wake);
    return true;
  }
  case PHYS3D_COMMAND_ADD_FORCE_CENTER:
    b3Body_ApplyForceToCenter(b->id, cmd->vector, cmd->wake);
    return true;
  case PHYS3D_COMMAND_ADD_IMPULSE: {
    b3Pos point = cmd->has_point ? b3ToPos(cmd->point)
                                 : b3Body_GetWorldCenterOfMass(b->id);
    b3Body_ApplyLinearImpulse(b->id, cmd->vector, point, cmd->wake);
    return true;
  }
  case PHYS3D_COMMAND_ADD_IMPULSE_CENTER:
    b3Body_ApplyLinearImpulseToCenter(b->id, cmd->vector, cmd->wake);
    return true;
  case PHYS3D_COMMAND_ADD_TORQUE:
    b3Body_ApplyTorque(b->id, cmd->vector, cmd->wake);
    return true;
  case PHYS3D_COMMAND_ADD_ANGULAR_IMPULSE:
    b3Body_ApplyAngularImpulse(b->id, cmd->vector, cmd->wake);
    return true;
  case PHYS3D_COMMAND_SET_VELOCITY: {
    b3Vec3 linear = b3Body_GetLinearVelocity(b->id);
    if (cmd->has_x)
      linear.x = cmd->vector.x;
    if (cmd->has_y)
      linear.y = cmd->vector.y;
    if (cmd->has_z)
      linear.z = cmd->vector.z;
    b3Body_SetLinearVelocity(b->id, linear);
    if (cmd->has_wx || cmd->has_wy || cmd->has_wz) {
      b3Vec3 angular = b3Body_GetAngularVelocity(b->id);
      if (cmd->has_wx)
        angular.x = cmd->angular.x;
      if (cmd->has_wy)
        angular.y = cmd->angular.y;
      if (cmd->has_wz)
        angular.z = cmd->angular.z;
      b3Body_SetAngularVelocity(b->id, angular);
    }
    if (cmd->wake)
      b3Body_SetAwake(b->id, true);
    return true;
  }
  case PHYS3D_COMMAND_TELEPORT: {
    b3WorldTransform xf = b3Body_GetTransform(b->id);
    if (cmd->has_x)
      xf.p.x = cmd->transform.p.x;
    if (cmd->has_y)
      xf.p.y = cmd->transform.p.y;
    if (cmd->has_z)
      xf.p.z = cmd->transform.p.z;
    if (cmd->has_rotation)
      xf.q = cmd->transform.q;
    b3Body_SetTransform(b->id, xf.p, xf.q);
    if (cmd->wake)
      b3Body_SetAwake(b->id, true);
    return true;
  }
  case PHYS3D_COMMAND_SET_TARGET: {
    b3WorldTransform target = b3Body_GetTransform(b->id);
    if (cmd->has_x)
      target.p.x = cmd->transform.p.x;
    if (cmd->has_y)
      target.p.y = cmd->transform.p.y;
    if (cmd->has_z)
      target.p.z = cmd->transform.p.z;
    if (cmd->has_rotation)
      target.q = cmd->transform.q;
    b3Body_SetTargetTransform(b->id, target, cmd->time_step, cmd->wake);
    return true;
  }
  default:
    return false;
  }
  return false;
}

static int apply_body_commands(Phys3dWorld *w) {
  int count = 0;
  for (int i = 0; i < w->commands.count; ++i)
    count += apply_body_command(w, &w->commands.items[i]) ? 1 : 0;
  command_queue_clear(&w->commands);
  return count;
}

static void prune_shapes(Phys3dBody *b) {
  uint64_t gen = b->world->generation;
  for (int i = 0; i < PHYS3D_SHAPE_BUCKETS; ++i) {
    Phys3dShape **prev = &b->shapes[i];
    Phys3dShape *s = b->shapes[i];
    while (s) {
      Phys3dShape *next = s->next;
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

static void prune_joints(Phys3dWorld *w) {
  uint64_t gen = w->generation;
  for (int i = 0; i < PHYS3D_JOINT_BUCKETS; ++i) {
    Phys3dJoint **prev = &w->joints[i];
    Phys3dJoint *j = w->joints[i];
    while (j) {
      Phys3dJoint *next = j->next;
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

static void prune_world(Phys3dWorld *w) {
  uint64_t gen = w->generation;
  prune_joints(w);
  for (int i = 0; i < PHYS3D_BODY_BUCKETS; ++i) {
    Phys3dBody **prev = &w->bodies[i];
    Phys3dBody *b = w->bodies[i];
    while (b) {
      Phys3dBody *next = b->next;
      if (b->seen_generation != gen) {
        *prev = next;
        body_free(b, true);
      } else {
        prune_shapes(b);
        prev = &b->next;
      }
      b = next;
    }
  }
}

static void fill_shape_snapshot(Phys3dWorld *w, Phys3dContactSnapshot *out,
                                bool is_a, b3ShapeId shape_id) {
  bool valid = B3_IS_NON_NULL(shape_id) && b3Shape_IsValid(shape_id);
  Phys3dShape *shape =
      valid ? (Phys3dShape *)b3Shape_GetUserData(shape_id) : NULL;
  const char *body_key = "";
  const char *shape_key = "";
  const char *tag = NULL;
  const char *material_name = NULL;
  int material_id =
      valid ? (int)b3Shape_GetSurfaceMaterial(shape_id).userMaterialId : 0;
  if (shape && shape->body) {
    body_key = shape->body->key;
    shape_key = shape->key;
    tag = shape->tag;
    material_name = shape->material_name;
  }
  bool view_valid = valid && body_key && body_key[0] != '\0' && shape_key &&
                    shape_key[0] != '\0';
  if (!view_valid) {
    Phys3dShapeTombstone *t = shape_tombstone_get(w, shape_id);
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

static void fill_sensor_snapshot(Phys3dWorld *w, Phys3dContactSnapshot *out,
                                 b3ShapeId sensor_shape_id,
                                 b3ShapeId visitor_shape_id) {
  fill_shape_snapshot(w, out, true, sensor_shape_id);
  fill_shape_snapshot(w, out, false, visitor_shape_id);
}

static void snapshot_fill_manifold(Phys3dContactSnapshot *dst,
                                   b3ShapeId shape_id_a,
                                   const b3Manifold *manifold) {
  if (!manifold)
    return;
  dst->nx = manifold->normal.x;
  dst->ny = manifold->normal.y;
  dst->nz = manifold->normal.z;
  dst->point_count = manifold->pointCount;
  if (manifold->pointCount > 0 && B3_IS_NON_NULL(shape_id_a) &&
      b3Shape_IsValid(shape_id_a)) {
    // Manifold anchors are relative to the body center of mass in world
    // space, so rebuild the world point from body A.
    b3Pos com = b3Body_GetWorldCenterOfMass(b3Shape_GetBody(shape_id_a));
    b3Vec3 anchor = manifold->points[0].anchorA;
    dst->x = (float)(com.x + anchor.x);
    dst->y = (float)(com.y + anchor.y);
    dst->z = (float)(com.z + anchor.z);
  }
}

static void capture_contact_events(Phys3dWorld *w) {
  b3ContactEvents ev = b3World_GetContactEvents(w->id);
  for (int i = 0; i < ev.beginCount; ++i) {
    b3ContactBeginTouchEvent *src = &ev.beginEvents[i];
    Phys3dContactSnapshot *dst = event_push(
        &w->events.begins, &w->events.begin_count, &w->events.begin_cap);
    if (!dst)
      continue;
    fill_shape_snapshot(w, dst, true, src->shapeIdA);
    fill_shape_snapshot(w, dst, false, src->shapeIdB);
    // Box3D begin events carry no manifold; pull it from the contact id.
    if (b3Contact_IsValid(src->contactId)) {
      b3ContactData data = b3Contact_GetData(src->contactId);
      if (data.manifoldCount > 0 && data.manifolds)
        snapshot_fill_manifold(dst, src->shapeIdA, &data.manifolds[0]);
    }
  }
  for (int i = 0; i < ev.endCount; ++i) {
    b3ContactEndTouchEvent *src = &ev.endEvents[i];
    Phys3dContactSnapshot *dst =
        event_push(&w->events.ends, &w->events.end_count, &w->events.end_cap);
    if (!dst)
      continue;
    fill_shape_snapshot(w, dst, true, src->shapeIdA);
    fill_shape_snapshot(w, dst, false, src->shapeIdB);
  }
  for (int i = 0; i < ev.hitCount; ++i) {
    b3ContactHitEvent *src = &ev.hitEvents[i];
    Phys3dContactSnapshot *dst =
        event_push(&w->events.hits, &w->events.hit_count, &w->events.hit_cap);
    if (!dst)
      continue;
    fill_shape_snapshot(w, dst, true, src->shapeIdA);
    fill_shape_snapshot(w, dst, false, src->shapeIdB);
    dst->nx = src->normal.x;
    dst->ny = src->normal.y;
    dst->nz = src->normal.z;
    b3Vec3 point = b3ToVec3(src->point);
    dst->x = point.x;
    dst->y = point.y;
    dst->z = point.z;
    dst->approach_speed = src->approachSpeed;
  }
}

static void capture_sensor_events(Phys3dWorld *w) {
  b3SensorEvents ev = b3World_GetSensorEvents(w->id);
  for (int i = 0; i < ev.beginCount; ++i) {
    b3SensorBeginTouchEvent *src = &ev.beginEvents[i];
    Phys3dContactSnapshot *dst =
        event_push(&w->events.sensor_begins, &w->events.sensor_begin_count,
                   &w->events.sensor_begin_cap);
    if (!dst)
      continue;
    fill_sensor_snapshot(w, dst, src->sensorShapeId, src->visitorShapeId);
  }
  for (int i = 0; i < ev.endCount; ++i) {
    b3SensorEndTouchEvent *src = &ev.endEvents[i];
    Phys3dContactSnapshot *dst =
        event_push(&w->events.sensor_ends, &w->events.sensor_end_count,
                   &w->events.sensor_end_cap);
    if (!dst)
      continue;
    fill_sensor_snapshot(w, dst, src->sensorShapeId, src->visitorShapeId);
  }
}

static void capture_body_events(Phys3dWorld *w) {
  b3BodyEvents ev = b3World_GetBodyEvents(w->id);
  for (int i = 0; i < ev.moveCount; ++i) {
    b3BodyMoveEvent *src = &ev.moveEvents[i];
    Phys3dBody *body = (Phys3dBody *)src->userData;
    Phys3dBodyEventSnapshot *dst = body_event_push(
        &w->events.moves, &w->events.move_count, &w->events.move_cap);
    if (!dst)
      continue;
    bool valid = B3_IS_NON_NULL(src->bodyId) && b3Body_IsValid(src->bodyId) &&
                 body && body->key;
    dst->valid = valid;
    dst->body = phys_strdup(valid ? body->key : "");
    b3Vec3 p = b3ToVec3(src->transform.p);
    dst->x = p.x;
    dst->y = p.y;
    dst->z = p.z;
    dst->qx = src->transform.q.v.x;
    dst->qy = src->transform.q.v.y;
    dst->qz = src->transform.q.v.z;
    dst->qw = src->transform.q.s;
    dst->fell_asleep = src->fellAsleep;
  }
}

static const char *joint_type_name(b3JointType type) {
  switch (type) {
  case b3_parallelJoint:
    return "parallel";
  case b3_distanceJoint:
    return "distance";
  case b3_filterJoint:
    return "filter";
  case b3_motorJoint:
    return "motor";
  case b3_prismaticJoint:
    return "prismatic";
  case b3_revoluteJoint:
    return "revolute";
  case b3_sphericalJoint:
    return "spherical";
  case b3_weldJoint:
    return "weld";
  case b3_wheelJoint:
    return "wheel";
  default:
    return "unknown";
  }
}

static void capture_joint_events(Phys3dWorld *w) {
  b3JointEvents ev = b3World_GetJointEvents(w->id);
  for (int i = 0; i < ev.count; ++i) {
    b3JointEvent *src = &ev.jointEvents[i];
    Phys3dJointEventSnapshot *dst = joint_event_push(
        &w->events.joints, &w->events.joint_count, &w->events.joint_cap);
    if (!dst)
      continue;
    bool valid = B3_IS_NON_NULL(src->jointId) && b3Joint_IsValid(src->jointId);
    const char *joint_key = "";
    const char *a_key = "";
    const char *b_key = "";
    dst->type = "unknown";
    if (valid) {
      dst->type = joint_type_name(b3Joint_GetType(src->jointId));
      Phys3dJoint *joint = (Phys3dJoint *)b3Joint_GetUserData(src->jointId);
      if (joint && joint->key)
        joint_key = joint->key;
      b3BodyId body_a_id = b3Joint_GetBodyA(src->jointId);
      b3BodyId body_b_id = b3Joint_GetBodyB(src->jointId);
      Phys3dBody *body_a =
          B3_IS_NON_NULL(body_a_id) && b3Body_IsValid(body_a_id)
              ? (Phys3dBody *)b3Body_GetUserData(body_a_id)
              : NULL;
      Phys3dBody *body_b =
          B3_IS_NON_NULL(body_b_id) && b3Body_IsValid(body_b_id)
              ? (Phys3dBody *)b3Body_GetUserData(body_b_id)
              : NULL;
      if (body_a && body_a->key)
        a_key = body_a->key;
      if (body_b && body_b->key)
        b_key = body_b->key;
    }
    dst->valid = valid;
    dst->joint = phys_strdup(joint_key);
    dst->body_a = phys_strdup(a_key);
    dst->body_b = phys_strdup(b_key);
  }
}

// box3d は b3World_Step の冒頭でイベント配列をクリアする。
// 1フレームに複数固定ステップ回すときは各ステップ直後に回収しないと
// 最後のステップ以外のイベントが失われる。
static void capture_step_events(Phys3dWorld *w) {
  capture_contact_events(w);
  capture_sensor_events(w);
  capture_joint_events(w);
}

static int l_phys3d_step(lua_State *L) {
  if (phys_in_callback(L, "phys3d_step"))
    return 0;
  Phys3dWorld *w = check_world(L, 1);
  float dt = (float)luaL_checknumber(L, 2);
  if (dt < 0.0f)
    dt = 0.0f;
  if (!w->begun && !w->step_without_begin_logged) {
    SDL_Log("phys3d_step: world '%s' stepped without phys3d_begin; using "
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
    Phys3dWorld *prev_mixer_world = g_phys3d_mixer_world;
    g_phys3d_mixer_world = callbacks_any(&w->callbacks) ? w : NULL;
    b3World_Step(w->id, w->fixed_dt, w->substeps);
    g_phys3d_mixer_world = prev_mixer_world;
    capture_step_events(w);
    w->accumulator -= (double)w->fixed_dt;
    steps++;
  }
  bool dropped = false;
  if (w->accumulator + 1e-9 >= (double)w->fixed_dt) {
    w->accumulator = 0.0;
    dropped = true;
  }
  // move は最終姿勢だけあればよいので最後のステップぶんのみ。
  // steps == 0 のフレームで前ステップの残骸を再回収しない。
  if (steps > 0)
    capture_body_events(w);
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
  lua_pushinteger(L, w->events.joint_count);
  lua_setfield(L, -2, "joint_events");
  return 1;
}

static void push_pose(lua_State *L, Phys3dBody *b) {
  b3Pos p = b3Body_GetPosition(b->id);
  b3Quat q = b3Body_GetRotation(b->id);
  b3Vec3 v = b3Body_GetLinearVelocity(b->id);
  b3Vec3 w = b3Body_GetAngularVelocity(b->id);
  lua_newtable(L);
  lua_pushnumber(L, p.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, p.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, p.z);
  lua_setfield(L, -2, "z");
  lua_pushnumber(L, q.v.x);
  lua_setfield(L, -2, "qx");
  lua_pushnumber(L, q.v.y);
  lua_setfield(L, -2, "qy");
  lua_pushnumber(L, q.v.z);
  lua_setfield(L, -2, "qz");
  lua_pushnumber(L, q.s);
  lua_setfield(L, -2, "qw");
  lua_pushnumber(L, v.x);
  lua_setfield(L, -2, "vx");
  lua_pushnumber(L, v.y);
  lua_setfield(L, -2, "vy");
  lua_pushnumber(L, v.z);
  lua_setfield(L, -2, "vz");
  lua_pushnumber(L, w.x);
  lua_setfield(L, -2, "wx");
  lua_pushnumber(L, w.y);
  lua_setfield(L, -2, "wy");
  lua_pushnumber(L, w.z);
  lua_setfield(L, -2, "wz");
  lua_pushboolean(L, b3Body_IsAwake(b->id));
  lua_setfield(L, -2, "awake");
  lua_pushboolean(L, b3Body_IsEnabled(b->id));
  lua_setfield(L, -2, "enabled");
  lua_pushboolean(L, b3Body_IsSleepEnabled(b->id));
  lua_setfield(L, -2, "sleep");
  lua_pushnumber(L, b3Body_GetSleepThreshold(b->id));
  lua_setfield(L, -2, "sleep_threshold");
}

static int l_phys3d_pose(lua_State *L) {
  Phys3dBody *b = NULL;
  if (is_ref(L, 1, "phys3d_body")) {
    const char *world_key = ref_string(L, 1, "world");
    const char *body_key = ref_string(L, 1, "key");
    Phys3dWorld *w = world_get(g_phys3d_state, world_key);
    b = w ? body_get(w, body_key) : NULL;
  } else if (is_ref(L, 1, "phys3d_world")) {
    Phys3dWorld *w = query_world_ref(L, 1);
    if (!w)
      return push_not_found(L);
    const char *body_key = luaL_checkstring(L, 2);
    b = body_get(w, body_key);
  } else {
    return luaL_error(L, "phys3d_pose: expected BodyRef or WorldRef, key");
  }
  if (!b || B3_IS_NULL(b->id) || !b3Body_IsValid(b->id)) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_pose(L, b);
  return 1;
}

static void push_vec3(lua_State *L, b3Vec3 v) {
  lua_newtable(L);
  lua_pushnumber(L, v.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, v.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, v.z);
  lua_setfield(L, -2, "z");
}

static int l_phys3d_world_info(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  bool valid = B3_IS_NON_NULL(w->id) && b3World_IsValid(w->id);

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

  if (!valid)
    return 1;

  b3Vec3 gravity = b3World_GetGravity(w->id);
  push_vec3(L, gravity);
  lua_setfield(L, -2, "gravity");
  lua_pushnumber(L, gravity.x);
  lua_setfield(L, -2, "gx");
  lua_pushnumber(L, gravity.y);
  lua_setfield(L, -2, "gy");
  lua_pushnumber(L, gravity.z);
  lua_setfield(L, -2, "gz");
  lua_pushboolean(L, b3World_IsSleepingEnabled(w->id));
  lua_setfield(L, -2, "sleep");
  lua_pushboolean(L, b3World_IsContinuousEnabled(w->id));
  lua_setfield(L, -2, "continuous");
  lua_pushboolean(L, b3World_IsWarmStartingEnabled(w->id));
  lua_setfield(L, -2, "warm_starting");
  lua_pushnumber(L, b3World_GetRestitutionThreshold(w->id));
  lua_setfield(L, -2, "restitution_threshold");
  lua_pushnumber(L, b3World_GetHitEventThreshold(w->id));
  lua_setfield(L, -2, "hit_event_threshold");
  lua_pushnumber(L, b3World_GetMaximumLinearSpeed(w->id));
  lua_setfield(L, -2, "maximum_linear_speed");
  lua_pushinteger(L, b3World_GetAwakeBodyCount(w->id));
  lua_setfield(L, -2, "awake_body_count");
  return 1;
}

static int l_phys3d_velocity(lua_State *L) {
  Phys3dBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  b3Vec3 v = b3Body_GetLinearVelocity(b->id);
  b3Vec3 w = b3Body_GetAngularVelocity(b->id);
  lua_newtable(L);
  lua_pushnumber(L, v.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, v.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, v.z);
  lua_setfield(L, -2, "z");
  lua_pushnumber(L, w.x);
  lua_setfield(L, -2, "wx");
  lua_pushnumber(L, w.y);
  lua_setfield(L, -2, "wy");
  lua_pushnumber(L, w.z);
  lua_setfield(L, -2, "wz");
  return 1;
}

static int l_phys3d_mass(lua_State *L) {
  Phys3dBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  b3MassData mass_data = b3Body_GetMassData(b->id);
  b3Pos center = b3Body_GetWorldCenterOfMass(b->id);
  lua_newtable(L);
  lua_pushnumber(L, mass_data.mass);
  lua_setfield(L, -2, "mass");
  push_vec3(L, b3ToVec3(center));
  lua_setfield(L, -2, "center");
  push_vec3(L, mass_data.center);
  lua_setfield(L, -2, "local_center");
  // Unique components of the symmetric inertia tensor about the local center.
  lua_newtable(L);
  lua_pushnumber(L, mass_data.inertia.cx.x);
  lua_setfield(L, -2, "xx");
  lua_pushnumber(L, mass_data.inertia.cy.y);
  lua_setfield(L, -2, "yy");
  lua_pushnumber(L, mass_data.inertia.cz.z);
  lua_setfield(L, -2, "zz");
  lua_pushnumber(L, mass_data.inertia.cy.x);
  lua_setfield(L, -2, "xy");
  lua_pushnumber(L, mass_data.inertia.cz.x);
  lua_setfield(L, -2, "xz");
  lua_pushnumber(L, mass_data.inertia.cz.y);
  lua_setfield(L, -2, "yz");
  lua_setfield(L, -2, "inertia");
  return 1;
}

static int l_phys3d_center(lua_State *L) {
  Phys3dBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  b3Pos center = b3Body_GetWorldCenterOfMass(b->id);
  push_vec3(L, b3ToVec3(center));
  return 1;
}

static int l_phys3d_world_point(lua_State *L) {
  Phys3dBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Vec3 local = value_vec3(L, 2, b3Vec3_zero);
  push_vec3(L, b3ToVec3(b3Body_GetWorldPoint(b->id, local)));
  return 1;
}

static int l_phys3d_local_point(lua_State *L) {
  Phys3dBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Vec3 world = value_vec3(L, 2, b3Vec3_zero);
  push_vec3(L, b3Body_GetLocalPoint(b->id, b3ToPos(world)));
  return 1;
}

static int l_phys3d_velocity_at(lua_State *L) {
  Phys3dBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Vec3 world = value_vec3(L, 2, b3Vec3_zero);
  push_vec3(L, b3Body_GetWorldPointVelocity(b->id, b3ToPos(world)));
  return 1;
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

static void push_filter_fields(lua_State *L, b3Filter filter) {
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

static void push_filter_info(lua_State *L, b3Filter filter) {
  lua_newtable(L);
  push_filter_fields(L, filter);
}

static void push_aabb(lua_State *L, b3AABB aabb) {
  lua_newtable(L);
  lua_pushnumber(L, aabb.lowerBound.x);
  lua_setfield(L, -2, "min_x");
  lua_pushnumber(L, aabb.lowerBound.y);
  lua_setfield(L, -2, "min_y");
  lua_pushnumber(L, aabb.lowerBound.z);
  lua_setfield(L, -2, "min_z");
  lua_pushnumber(L, aabb.upperBound.x);
  lua_setfield(L, -2, "max_x");
  lua_pushnumber(L, aabb.upperBound.y);
  lua_setfield(L, -2, "max_y");
  lua_pushnumber(L, aabb.upperBound.z);
  lua_setfield(L, -2, "max_z");
}

static const char *shape_kind_name(Phys3dShapeKind kind) {
  switch (kind) {
  case PHYS3D_SHAPE_SPHERE:
    return "sphere";
  case PHYS3D_SHAPE_BOX:
    return "box";
  case PHYS3D_SHAPE_CAPSULE:
    return "capsule";
  case PHYS3D_SHAPE_CYLINDER:
    return "cylinder";
  case PHYS3D_SHAPE_CONE:
    return "cone";
  case PHYS3D_SHAPE_HULL:
    return "hull";
  case PHYS3D_SHAPE_MESH:
    return "mesh";
  case PHYS3D_SHAPE_HEIGHT_FIELD:
    return "height_field";
  case PHYS3D_SHAPE_COMPOUND:
    return "compound";
  default:
    return "unknown";
  }
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

static void push_shape_id_view(lua_State *L, b3ShapeId shape_id) {
  bool valid = B3_IS_NON_NULL(shape_id) && b3Shape_IsValid(shape_id);
  Phys3dShape *shape =
      valid ? (Phys3dShape *)b3Shape_GetUserData(shape_id) : NULL;
  const char *body_key = "";
  const char *shape_key = "";
  const char *tag = NULL;
  const char *material_name = NULL;
  if (shape && shape->body) {
    body_key = shape->body->key;
    shape_key = shape->key;
    tag = shape->tag;
    material_name = shape->material_name;
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
  if (valid) {
    int material_id = (int)b3Shape_GetSurfaceMaterial(shape_id).userMaterialId;
    if (material_name && material_name[0] != '\0') {
      lua_pushstring(L, material_name);
    } else {
      lua_pushinteger(L, material_id);
    }
    lua_setfield(L, -2, "material");
    lua_pushinteger(L, material_id);
    lua_setfield(L, -2, "user_material_id");
    push_filter_fields(L, b3Shape_GetFilter(shape_id));
  }
  lua_pushboolean(L, valid && shape && shape->body && shape->key);
  lua_setfield(L, -2, "valid");
}

static void callback_log_error_once(Phys3dWorld *w, bool *logged,
                                    const char *name, lua_State *L) {
  if (!w || !logged || *logged)
    return;
  const char *message = lua_tostring(L, -1);
  SDL_Log("phys3d %s callback error: %s", name,
          message ? message : "unknown error");
  *logged = true;
}

// Box3D pre-solve callbacks carry a single point and normal (no manifold)
// because they are also used during CCD.
static void push_pre_solve_contact(lua_State *L, b3ShapeId shape_id_a,
                                   b3ShapeId shape_id_b, b3Pos point,
                                   b3Vec3 normal) {
  lua_newtable(L);
  push_shape_id_view(L, shape_id_a);
  lua_setfield(L, -2, "a");
  push_shape_id_view(L, shape_id_b);
  lua_setfield(L, -2, "b");
  lua_pushnumber(L, point.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, point.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, point.z);
  lua_setfield(L, -2, "z");
  lua_pushnumber(L, normal.x);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, normal.y);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, normal.z);
  lua_setfield(L, -2, "nz");
}

static bool phys3d_custom_filter_callback(b3ShapeId shape_id_a,
                                          b3ShapeId shape_id_b, void *context) {
  Phys3dWorld *w = (Phys3dWorld *)context;
  if (!w || !w->callbacks.L || !callback_ref_is_set(w->callbacks.filter_ref))
    return true;
  lua_State *L = w->callbacks.L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, w->callbacks.filter_ref);
  push_shape_id_view(L, shape_id_a);
  push_shape_id_view(L, shape_id_b);
  g_phys3d_callback_depth++;
  int status = lua_pcall(L, 2, 1, 0);
  g_phys3d_callback_depth--;
  if (status != LUA_OK) {
    callback_log_error_once(w, &w->callbacks.filter_error_logged, "filter", L);
    lua_settop(L, top);
    return true;
  }
  bool collide = !lua_isboolean(L, -1) || lua_toboolean(L, -1) != 0;
  lua_settop(L, top);
  return collide;
}

static bool phys3d_pre_solve_callback(b3ShapeId shape_id_a,
                                      b3ShapeId shape_id_b, b3Pos point,
                                      b3Vec3 normal, void *context) {
  Phys3dWorld *w = (Phys3dWorld *)context;
  if (!w || !w->callbacks.L || !callback_ref_is_set(w->callbacks.pre_solve_ref))
    return true;
  lua_State *L = w->callbacks.L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, w->callbacks.pre_solve_ref);
  push_pre_solve_contact(L, shape_id_a, shape_id_b, point, normal);
  g_phys3d_callback_depth++;
  int status = lua_pcall(L, 1, 1, 0);
  g_phys3d_callback_depth--;
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
                               uint64_t material) {
  lua_newtable(L);
  lua_pushnumber(L, value);
  lua_setfield(L, -2, field);
  lua_pushinteger(L, (lua_Integer)material);
  lua_setfield(L, -2, "material");
}

static float phys3d_friction_callback(float friction_a, uint64_t material_a,
                                      float friction_b, uint64_t material_b) {
  float fallback = default_friction(friction_a, friction_b);
  Phys3dWorld *w = g_phys3d_mixer_world;
  if (!w || !w->callbacks.L || !callback_ref_is_set(w->callbacks.friction_ref))
    return fallback;
  lua_State *L = w->callbacks.L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, w->callbacks.friction_ref);
  push_material_view(L, "friction", friction_a, material_a);
  push_material_view(L, "friction", friction_b, material_b);
  g_phys3d_callback_depth++;
  int status = lua_pcall(L, 2, 1, 0);
  g_phys3d_callback_depth--;
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

static float phys3d_restitution_callback(float restitution_a,
                                         uint64_t material_a,
                                         float restitution_b,
                                         uint64_t material_b) {
  float fallback = default_restitution(restitution_a, restitution_b);
  Phys3dWorld *w = g_phys3d_mixer_world;
  if (!w || !w->callbacks.L ||
      !callback_ref_is_set(w->callbacks.restitution_ref))
    return fallback;
  lua_State *L = w->callbacks.L;
  int top = lua_gettop(L);
  lua_rawgeti(L, LUA_REGISTRYINDEX, w->callbacks.restitution_ref);
  push_material_view(L, "restitution", restitution_a, material_a);
  push_material_view(L, "restitution", restitution_b, material_b);
  g_phys3d_callback_depth++;
  int status = lua_pcall(L, 2, 1, 0);
  g_phys3d_callback_depth--;
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

static void parse_ray_query(lua_State *L, int idx, const char *fn_name,
                            b3Vec3 *origin, b3Vec3 *translation) {
  b3Vec3 o = {table_number(L, idx, "x", NULL, 0.0f),
              table_number(L, idx, "y", NULL, 0.0f),
              table_number(L, idx, "z", NULL, 0.0f)};
  o = table_vec3(L, idx, "origin", "from", o);
  b3Vec3 d = {table_number(L, idx, "dx", NULL, 0.0f),
              table_number(L, idx, "dy", NULL, 0.0f),
              table_number(L, idx, "dz", NULL, 0.0f)};
  d = table_vec3(L, idx, "translation", "delta", d);
  if (table_get_any(L, idx, "to", NULL)) {
    if (lua_istable(L, -1)) {
      b3Vec3 to = value_vec3(L, lua_gettop(L), o);
      d = b3Sub(to, o);
    }
    lua_pop(L, 1);
  }
  float max_fraction =
      table_number(L, idx, "max_fraction", "maxFraction", 1.0f);
  if (max_fraction < 0.0f)
    max_fraction = 0.0f;
  d = b3MulSV(max_fraction, d);
  if (d.x * d.x + d.y * d.y + d.z * d.z <= 1e-12f)
    luaL_error(L, "%s: ray translation must be non-zero", fn_name);
  *origin = o;
  *translation = d;
}

static int l_phys3d_shape_raycast(lua_State *L) {
  Phys3dShape *s = query_shape_ref(L, 1);
  if (!s)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Vec3 origin;
  b3Vec3 translation;
  parse_ray_query(L, 2, "phys3d_shape_raycast", &origin, &translation);
  b3WorldCastOutput hit = b3Shape_RayCast(s->id, b3ToPos(origin), translation);
  if (!hit.hit) {
    lua_pushnil(L);
    return 1;
  }
  b3Vec3 point = b3ToVec3(hit.point);
  lua_newtable(L);
  lua_pushnumber(L, point.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, point.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, point.z);
  lua_setfield(L, -2, "z");
  lua_pushnumber(L, hit.normal.x);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, hit.normal.y);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, hit.normal.z);
  lua_setfield(L, -2, "nz");
  lua_pushnumber(L, hit.fraction);
  lua_setfield(L, -2, "fraction");
  lua_pushinteger(L, hit.iterations);
  lua_setfield(L, -2, "iterations");
  lua_pushinteger(L, hit.triangleIndex);
  lua_setfield(L, -2, "triangle_index");
  lua_pushinteger(L, hit.childIndex);
  lua_setfield(L, -2, "child_index");
  return 1;
}

static int l_phys3d_shape_closest_point(lua_State *L) {
  Phys3dShape *s = query_shape_ref(L, 1);
  if (!s)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Vec3 target = value_vec3(L, 2, b3Vec3_zero);
  push_vec3(L, b3Shape_GetClosestPoint(s->id, target));
  return 1;
}

static int l_phys3d_shape_aabb(lua_State *L) {
  Phys3dShape *s = query_shape_ref(L, 1);
  if (!s)
    return push_not_found(L);
  push_aabb(L, b3Shape_GetAABB(s->id));
  return 1;
}

static int l_phys3d_shape_info(lua_State *L) {
  Phys3dShape *s = query_shape_ref(L, 1);
  if (!s)
    return push_not_found(L);

  push_shape_id_view(L, s->id);
  lua_pushstring(L, shape_kind_name(s->kind));
  lua_setfield(L, -2, "kind");
  lua_pushnumber(L, b3Shape_GetDensity(s->id));
  lua_setfield(L, -2, "density");
  b3SurfaceMaterial material = b3Shape_GetSurfaceMaterial(s->id);
  lua_pushnumber(L, material.friction);
  lua_setfield(L, -2, "friction");
  lua_pushnumber(L, material.restitution);
  lua_setfield(L, -2, "restitution");
  lua_pushboolean(L, b3Shape_IsSensor(s->id));
  lua_setfield(L, -2, "sensor");
  lua_pushboolean(L, b3Shape_AreSensorEventsEnabled(s->id));
  lua_setfield(L, -2, "sensor_events");
  lua_pushboolean(L, b3Shape_AreContactEventsEnabled(s->id));
  lua_setfield(L, -2, "contact");
  lua_pushboolean(L, b3Shape_ArePreSolveEventsEnabled(s->id));
  lua_setfield(L, -2, "pre_solve");
  lua_pushboolean(L, b3Shape_AreHitEventsEnabled(s->id));
  lua_setfield(L, -2, "hit");
  push_filter_info(L, b3Shape_GetFilter(s->id));
  lua_setfield(L, -2, "filter");
  push_aabb(L, b3Shape_GetAABB(s->id));
  lua_setfield(L, -2, "aabb");
  return 1;
}

static int l_phys3d_shape_set_material(lua_State *L) {
  if (phys_in_callback(L, "phys3d_shape_set_material"))
    return 0;
  Phys3dShape *s = check_shape(L, 1);
  check_live_shape(L, s, "phys3d_shape_set_material");

  // Box3D has no per-field material setters, so round-trip the surface
  // material for friction/restitution/material id in one go.
  b3SurfaceMaterial material = b3Shape_GetSurfaceMaterial(s->id);
  int material_id = (int)material.userMaterialId;
  bool set_material_id = false;
  bool set_material_name = false;
  bool material_dirty = false;
  const char *material_name = NULL;

  if (lua_istable(L, 2)) {
    float density = 0.0f;
    if (table_number_optional(L, 2, "density", NULL, &density))
      b3Shape_SetDensity(s->id, density, true);
    float friction = 0.0f;
    if (table_number_optional(L, 2, "friction", NULL, &friction)) {
      material.friction = friction;
      material_dirty = true;
    }
    float restitution = 0.0f;
    if (table_number_optional(L, 2, "restitution", NULL, &restitution)) {
      material.restitution = restitution;
      material_dirty = true;
    }

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
        L, "phys3d_shape_set_material: expected table, number, or string");
  }

  if (set_material_id) {
    material.userMaterialId = (uint64_t)material_id;
    material_dirty = true;
    s->material_id = material_id;
  }
  if (material_dirty)
    b3Shape_SetSurfaceMaterial(s->id, material);
  if (set_material_name)
    owned_string_set_lua(L, &s->material_name, material_name,
                         "phys3d_shape_set_material");
  shape_tombstone_update_shape(s);
  return 0;
}

static int l_phys3d_shape_set_filter(lua_State *L) {
  if (phys_in_callback(L, "phys3d_shape_set_filter"))
    return 0;
  Phys3dShape *s = check_shape(L, 1);
  check_live_shape(L, s, "phys3d_shape_set_filter");
  luaL_checktype(L, 2, LUA_TTABLE);

  b3Filter filter = b3Shape_GetFilter(s->id);
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
  b3Shape_SetFilter(s->id, filter, true);
  shape_tombstone_update_shape(s);
  return 0;
}

static int l_phys3d_shape_set_events(lua_State *L) {
  if (phys_in_callback(L, "phys3d_shape_set_events"))
    return 0;
  Phys3dShape *s = check_shape(L, 1);
  check_live_shape(L, s, "phys3d_shape_set_events");
  luaL_checktype(L, 2, LUA_TTABLE);

  bool flag = false;
  if (table_bool_optional(L, 2, "sensor", NULL, &flag))
    return luaL_error(
        L, "phys3d_shape_set_events: sensor cannot change at runtime");
  if (table_bool_optional(L, 2, "sensor_events", "sensorEvents", &flag))
    b3Shape_EnableSensorEvents(s->id, flag);
  if (table_bool_optional(L, 2, "contact", "contactEvents", &flag) ||
      table_bool_optional(L, 2, "contact_events", "contactEvents", &flag))
    b3Shape_EnableContactEvents(s->id, flag);
  if (table_bool_optional(L, 2, "pre_solve", "preSolve", &flag))
    b3Shape_EnablePreSolveEvents(s->id, flag);
  if (table_bool_optional(L, 2, "hit", "hitEvents", &flag) ||
      table_bool_optional(L, 2, "hit_events", "hitEvents", &flag))
    b3Shape_EnableHitEvents(s->id, flag);
  shape_tombstone_update_shape(s);
  return 0;
}

static int l_phys3d_body_shapes(lua_State *L) {
  Phys3dBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  int capacity = b3Body_GetShapeCount(b->id);
  lua_newtable(L);
  if (capacity <= 0)
    return 1;
  b3ShapeId *ids = (b3ShapeId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return luaL_error(L, "phys3d_body_shapes: out of memory");
  int count = b3Body_GetShapes(b->id, ids, capacity);
  for (int i = 0; i < count; ++i) {
    push_shape_id_view(L, ids[i]);
    if (B3_IS_NON_NULL(ids[i]) && b3Shape_IsValid(ids[i])) {
      Phys3dShape *shape = (Phys3dShape *)b3Shape_GetUserData(ids[i]);
      if (shape) {
        lua_pushstring(L, shape_kind_name(shape->kind));
        lua_setfield(L, -2, "kind");
      }
    }
    lua_rawseti(L, -2, i + 1);
  }
  SDL_free(ids);
  return 1;
}

static void push_contact_data(lua_State *L, const b3ContactData *contact) {
  lua_newtable(L);
  push_shape_id_view(L, contact->shapeIdA);
  lua_setfield(L, -2, "a");
  push_shape_id_view(L, contact->shapeIdB);
  lua_setfield(L, -2, "b");
  const b3Manifold *manifold = contact->manifoldCount > 0 && contact->manifolds
                                   ? &contact->manifolds[0]
                                   : NULL;
  lua_pushnumber(L, manifold ? manifold->normal.x : 0.0f);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, manifold ? manifold->normal.y : 0.0f);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, manifold ? manifold->normal.z : 0.0f);
  lua_setfield(L, -2, "nz");
  lua_pushinteger(L, contact->manifoldCount);
  lua_setfield(L, -2, "manifold_count");
  lua_pushinteger(L, manifold ? manifold->pointCount : 0);
  lua_setfield(L, -2, "point_count");
  if (manifold && manifold->pointCount > 0 &&
      B3_IS_NON_NULL(contact->shapeIdA) && b3Shape_IsValid(contact->shapeIdA)) {
    // Manifold anchors are relative to the body center of mass in world
    // space, so rebuild the world point from body A.
    b3Pos com = b3Body_GetWorldCenterOfMass(b3Shape_GetBody(contact->shapeIdA));
    b3Vec3 anchor = manifold->points[0].anchorA;
    lua_pushnumber(L, com.x + anchor.x);
    lua_setfield(L, -2, "x");
    lua_pushnumber(L, com.y + anchor.y);
    lua_setfield(L, -2, "y");
    lua_pushnumber(L, com.z + anchor.z);
    lua_setfield(L, -2, "z");
    lua_pushnumber(L, manifold->points[0].separation);
    lua_setfield(L, -2, "separation");
  }
}

static int l_phys3d_body_contacts(lua_State *L) {
  Phys3dBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  int capacity = b3Body_GetContactCapacity(b->id);
  lua_newtable(L);
  if (capacity <= 0)
    return 1;
  b3ContactData *items = (b3ContactData *)SDL_malloc(sizeof(*items) * capacity);
  if (!items)
    return luaL_error(L, "phys3d_body_contacts: out of memory");
  int count = b3Body_GetContactData(b->id, items, capacity);
  for (int i = 0; i < count; ++i) {
    push_contact_data(L, &items[i]);
    lua_rawseti(L, -2, i + 1);
  }
  SDL_free(items);
  return 1;
}

static void push_joint_view(lua_State *L, Phys3dJoint *j) {
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

static void push_joint_id_view(lua_State *L, b3JointId joint_id) {
  Phys3dJoint *j = B3_IS_NON_NULL(joint_id) && b3Joint_IsValid(joint_id)
                       ? (Phys3dJoint *)b3Joint_GetUserData(joint_id)
                       : NULL;
  push_joint_view(L, j);
}

static int l_phys3d_body_joints(lua_State *L) {
  Phys3dBody *b = query_body_ref(L, 1);
  if (!b)
    return push_not_found(L);
  int capacity = b3Body_GetJointCount(b->id);
  lua_newtable(L);
  if (capacity <= 0)
    return 1;
  b3JointId *ids = (b3JointId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return luaL_error(L, "phys3d_body_joints: out of memory");
  int count = b3Body_GetJoints(b->id, ids, capacity);
  for (int i = 0; i < count; ++i) {
    push_joint_id_view(L, ids[i]);
    lua_rawseti(L, -2, i + 1);
  }
  SDL_free(ids);
  return 1;
}

static void push_local_frame(lua_State *L, b3Transform frame) {
  lua_newtable(L);
  lua_pushnumber(L, frame.p.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, frame.p.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, frame.p.z);
  lua_setfield(L, -2, "z");
  lua_pushnumber(L, frame.q.v.x);
  lua_setfield(L, -2, "qx");
  lua_pushnumber(L, frame.q.v.y);
  lua_setfield(L, -2, "qy");
  lua_pushnumber(L, frame.q.v.z);
  lua_setfield(L, -2, "qz");
  lua_pushnumber(L, frame.q.s);
  lua_setfield(L, -2, "qw");
}

static int l_phys3d_joint_info(lua_State *L) {
  Phys3dJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  push_joint_view(L, j);
  lua_pushboolean(L, b3Joint_GetCollideConnected(j->id));
  lua_setfield(L, -2, "collide_connected");
  push_vec3(L, b3Joint_GetConstraintForce(j->id));
  lua_setfield(L, -2, "force");
  push_vec3(L, b3Joint_GetConstraintTorque(j->id));
  lua_setfield(L, -2, "torque");
  lua_pushnumber(L, b3Joint_GetLinearSeparation(j->id));
  lua_setfield(L, -2, "linear_separation");
  lua_pushnumber(L, b3Joint_GetAngularSeparation(j->id));
  lua_setfield(L, -2, "angular_separation");
  push_local_frame(L, b3Joint_GetLocalFrameA(j->id));
  lua_setfield(L, -2, "local_frame_a");
  push_local_frame(L, b3Joint_GetLocalFrameB(j->id));
  lua_setfield(L, -2, "local_frame_b");
  return 1;
}

static int l_phys3d_joint_force(lua_State *L) {
  Phys3dJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  push_vec3(L, b3Joint_GetConstraintForce(j->id));
  return 1;
}

static int l_phys3d_joint_torque(lua_State *L) {
  Phys3dJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  push_vec3(L, b3Joint_GetConstraintTorque(j->id));
  return 1;
}

static int l_phys3d_joint_angle(lua_State *L) {
  Phys3dJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind != PHYS3D_JOINT_REVOLUTE) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, b3RevoluteJoint_GetAngle(j->id));
  return 1;
}

static int l_phys3d_joint_translation(lua_State *L) {
  Phys3dJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind != PHYS3D_JOINT_PRISMATIC) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, b3PrismaticJoint_GetTranslation(j->id));
  return 1;
}

static int l_phys3d_joint_speed(lua_State *L) {
  Phys3dJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind == PHYS3D_JOINT_PRISMATIC) {
    lua_pushnumber(L, b3PrismaticJoint_GetSpeed(j->id));
    return 1;
  }
  if (j->kind == PHYS3D_JOINT_WHEEL) {
    lua_pushnumber(L, b3WheelJoint_GetSpinSpeed(j->id));
    return 1;
  }
  lua_pushnil(L);
  return 1;
}

static int l_phys3d_joint_length(lua_State *L) {
  Phys3dJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind != PHYS3D_JOINT_DISTANCE) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushnumber(L, b3DistanceJoint_GetCurrentLength(j->id));
  return 1;
}

static int l_phys3d_joint_motor_force(lua_State *L) {
  Phys3dJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind == PHYS3D_JOINT_DISTANCE) {
    lua_pushnumber(L, b3DistanceJoint_GetMotorForce(j->id));
    return 1;
  }
  if (j->kind == PHYS3D_JOINT_PRISMATIC) {
    lua_pushnumber(L, b3PrismaticJoint_GetMotorForce(j->id));
    return 1;
  }
  lua_pushnil(L);
  return 1;
}

static int l_phys3d_joint_motor_torque(lua_State *L) {
  Phys3dJoint *j = query_joint_ref(L, 1);
  if (!j)
    return push_not_found(L);
  if (j->kind == PHYS3D_JOINT_REVOLUTE) {
    lua_pushnumber(L, b3RevoluteJoint_GetMotorTorque(j->id));
    return 1;
  }
  if (j->kind == PHYS3D_JOINT_WHEEL) {
    lua_pushnumber(L, b3WheelJoint_GetSpinTorque(j->id));
    return 1;
  }
  if (j->kind == PHYS3D_JOINT_SPHERICAL) {
    // The spherical motor torque is a vector in box3d.
    push_vec3(L, b3SphericalJoint_GetMotorTorque(j->id));
    return 1;
  }
  lua_pushnil(L);
  return 1;
}

static int l_phys3d_joint_set_motor(lua_State *L) {
  if (phys_in_callback(L, "phys3d_joint_set_motor"))
    return 0;
  Phys3dJoint *j = check_joint(L, 1);
  check_live_joint(L, j, "phys3d_joint_set_motor");
  luaL_checktype(L, 2, LUA_TTABLE);
  bool enabled = table_bool(L, 2, "enabled", NULL, true);
  float speed = table_number(L, 2, "speed", "motor_speed", 0.0f);
  float max_force = table_number(L, 2, "max_force", "maxForce", 0.0f);
  float max_torque = table_number(L, 2, "max_torque", "maxTorque", 0.0f);
  switch (j->kind) {
  case PHYS3D_JOINT_DISTANCE:
    b3DistanceJoint_EnableMotor(j->id, enabled);
    b3DistanceJoint_SetMotorSpeed(j->id, speed);
    b3DistanceJoint_SetMaxMotorForce(j->id, max_force);
    break;
  case PHYS3D_JOINT_PRISMATIC:
    b3PrismaticJoint_EnableMotor(j->id, enabled);
    b3PrismaticJoint_SetMotorSpeed(j->id, speed);
    b3PrismaticJoint_SetMaxMotorForce(j->id, max_force);
    break;
  case PHYS3D_JOINT_REVOLUTE:
    b3RevoluteJoint_EnableMotor(j->id, enabled);
    b3RevoluteJoint_SetMotorSpeed(j->id, speed);
    b3RevoluteJoint_SetMaxMotorTorque(j->id, max_torque);
    break;
  case PHYS3D_JOINT_SPHERICAL:
    b3SphericalJoint_EnableMotor(j->id, enabled);
    b3SphericalJoint_SetMotorVelocity(
        j->id, table_vec3(L, 2, "velocity", "motor_velocity",
                          b3SphericalJoint_GetMotorVelocity(j->id)));
    b3SphericalJoint_SetMaxMotorTorque(j->id, max_torque);
    break;
  case PHYS3D_JOINT_WHEEL:
    b3WheelJoint_EnableSpinMotor(j->id, enabled);
    b3WheelJoint_SetSpinMotorSpeed(j->id, speed);
    b3WheelJoint_SetMaxSpinTorque(j->id, max_torque);
    break;
  case PHYS3D_JOINT_MOTOR:
    b3MotorJoint_SetLinearVelocity(
        j->id, table_vec3(L, 2, "linear_velocity", "linearVelocity",
                          b3MotorJoint_GetLinearVelocity(j->id)));
    b3MotorJoint_SetAngularVelocity(
        j->id, table_vec3(L, 2, "angular_velocity", "angularVelocity",
                          b3MotorJoint_GetAngularVelocity(j->id)));
    b3MotorJoint_SetMaxVelocityForce(
        j->id, table_number(L, 2, "max_velocity_force", "maxVelocityForce",
                            b3MotorJoint_GetMaxVelocityForce(j->id)));
    b3MotorJoint_SetMaxVelocityTorque(
        j->id, table_number(L, 2, "max_velocity_torque", "maxVelocityTorque",
                            b3MotorJoint_GetMaxVelocityTorque(j->id)));
    break;
  default:
    return luaL_error(L, "phys3d_joint_set_motor: unsupported joint type");
  }
  b3Joint_WakeBodies(j->id);
  return 0;
}

static int l_phys3d_joint_set_limit(lua_State *L) {
  if (phys_in_callback(L, "phys3d_joint_set_limit"))
    return 0;
  Phys3dJoint *j = check_joint(L, 1);
  check_live_joint(L, j, "phys3d_joint_set_limit");
  luaL_checktype(L, 2, LUA_TTABLE);
  bool enabled = table_bool(L, 2, "enabled", NULL, true);
  float lower = table_number(L, 2, "lower", NULL, 0.0f);
  float upper = table_number(L, 2, "upper", NULL, 0.0f);
  switch (j->kind) {
  case PHYS3D_JOINT_DISTANCE: {
    float min_length = table_number(L, 2, "min", "min_length", 0.0f);
    float max_length = table_number(L, 2, "max", "max_length", FLT_MAX);
    b3DistanceJoint_EnableLimit(j->id, enabled);
    b3DistanceJoint_SetLengthRange(j->id, min_length, max_length);
    break;
  }
  case PHYS3D_JOINT_PRISMATIC:
    b3PrismaticJoint_EnableLimit(j->id, enabled);
    b3PrismaticJoint_SetLimits(j->id, lower, upper);
    break;
  case PHYS3D_JOINT_REVOLUTE:
    b3RevoluteJoint_EnableLimit(j->id, enabled);
    b3RevoluteJoint_SetLimits(j->id, lower, upper);
    break;
  case PHYS3D_JOINT_SPHERICAL: {
    float cone_angle = 0.0f;
    if (table_number_optional(L, 2, "cone_angle", "coneAngle", &cone_angle)) {
      b3SphericalJoint_EnableConeLimit(j->id, enabled);
      b3SphericalJoint_SetConeLimit(j->id, cone_angle);
    }
    bool has_lower = table_get_any(L, 2, "lower", "lower_twist_angle");
    if (has_lower)
      lua_pop(L, 1);
    bool has_upper = table_get_any(L, 2, "upper", "upper_twist_angle");
    if (has_upper)
      lua_pop(L, 1);
    if (has_lower || has_upper) {
      lower = table_number(L, 2, "lower_twist_angle", "lowerTwistAngle", lower);
      upper = table_number(L, 2, "upper_twist_angle", "upperTwistAngle", upper);
      b3SphericalJoint_EnableTwistLimit(j->id, enabled);
      b3SphericalJoint_SetTwistLimits(j->id, lower, upper);
    }
    break;
  }
  case PHYS3D_JOINT_WHEEL:
    b3WheelJoint_EnableSuspensionLimit(j->id, enabled);
    b3WheelJoint_SetSuspensionLimits(j->id, lower, upper);
    break;
  default:
    return luaL_error(L, "phys3d_joint_set_limit: unsupported joint type");
  }
  b3Joint_WakeBodies(j->id);
  return 0;
}

static int l_phys3d_joint_set_spring(lua_State *L) {
  if (phys_in_callback(L, "phys3d_joint_set_spring"))
    return 0;
  Phys3dJoint *j = check_joint(L, 1);
  check_live_joint(L, j, "phys3d_joint_set_spring");
  luaL_checktype(L, 2, LUA_TTABLE);
  bool enabled = table_bool(L, 2, "enabled", NULL, true);
  float hertz = table_number(L, 2, "hertz", NULL, 0.0f);
  float damping = table_number(L, 2, "damping_ratio", "dampingRatio", 0.0f);
  switch (j->kind) {
  case PHYS3D_JOINT_DISTANCE:
    b3DistanceJoint_EnableSpring(j->id, enabled);
    b3DistanceJoint_SetSpringHertz(j->id, hertz);
    b3DistanceJoint_SetSpringDampingRatio(j->id, damping);
    break;
  case PHYS3D_JOINT_PRISMATIC:
    b3PrismaticJoint_EnableSpring(j->id, enabled);
    b3PrismaticJoint_SetSpringHertz(j->id, hertz);
    b3PrismaticJoint_SetSpringDampingRatio(j->id, damping);
    break;
  case PHYS3D_JOINT_REVOLUTE:
    b3RevoluteJoint_EnableSpring(j->id, enabled);
    b3RevoluteJoint_SetSpringHertz(j->id, hertz);
    b3RevoluteJoint_SetSpringDampingRatio(j->id, damping);
    break;
  case PHYS3D_JOINT_SPHERICAL:
    b3SphericalJoint_EnableSpring(j->id, enabled);
    b3SphericalJoint_SetSpringHertz(j->id, hertz);
    b3SphericalJoint_SetSpringDampingRatio(j->id, damping);
    break;
  case PHYS3D_JOINT_PARALLEL:
    b3ParallelJoint_SetSpringHertz(j->id, hertz);
    b3ParallelJoint_SetSpringDampingRatio(j->id, damping);
    b3ParallelJoint_SetMaxTorque(
        j->id, table_number(L, 2, "max_torque", "maxTorque",
                            b3ParallelJoint_GetMaxTorque(j->id)));
    break;
  case PHYS3D_JOINT_WHEEL:
    b3WheelJoint_EnableSuspension(j->id, enabled);
    b3WheelJoint_SetSuspensionHertz(j->id, hertz);
    b3WheelJoint_SetSuspensionDampingRatio(j->id, damping);
    break;
  case PHYS3D_JOINT_WELD:
    b3WeldJoint_SetLinearHertz(
        j->id, table_number(L, 2, "linear_hertz", "linearHertz", hertz));
    b3WeldJoint_SetLinearDampingRatio(
        j->id, table_number(L, 2, "linear_damping_ratio", "linearDampingRatio",
                            damping));
    b3WeldJoint_SetAngularHertz(
        j->id, table_number(L, 2, "angular_hertz", "angularHertz", hertz));
    b3WeldJoint_SetAngularDampingRatio(
        j->id, table_number(L, 2, "angular_damping_ratio",
                            "angularDampingRatio", damping));
    break;
  case PHYS3D_JOINT_MOTOR:
    b3MotorJoint_SetLinearHertz(
        j->id, table_number(L, 2, "linear_hertz", "linearHertz", hertz));
    b3MotorJoint_SetLinearDampingRatio(
        j->id, table_number(L, 2, "linear_damping_ratio", "linearDampingRatio",
                            damping));
    b3MotorJoint_SetAngularHertz(
        j->id, table_number(L, 2, "angular_hertz", "angularHertz", hertz));
    b3MotorJoint_SetAngularDampingRatio(
        j->id, table_number(L, 2, "angular_damping_ratio",
                            "angularDampingRatio", damping));
    break;
  default:
    return luaL_error(L, "phys3d_joint_set_spring: unsupported joint type");
  }
  b3Joint_WakeBodies(j->id);
  return 0;
}

static int l_phys3d_joint_set_target(lua_State *L) {
  if (phys_in_callback(L, "phys3d_joint_set_target"))
    return 0;
  Phys3dJoint *j = check_joint(L, 1);
  check_live_joint(L, j, "phys3d_joint_set_target");
  luaL_checktype(L, 2, LUA_TTABLE);
  switch (j->kind) {
  case PHYS3D_JOINT_PRISMATIC:
    b3PrismaticJoint_SetTargetTranslation(
        j->id, table_number(L, 2, "translation", "target_translation",
                            b3PrismaticJoint_GetTargetTranslation(j->id)));
    break;
  case PHYS3D_JOINT_REVOLUTE:
    b3RevoluteJoint_SetTargetAngle(
        j->id, table_number(L, 2, "angle", "target_angle",
                            b3RevoluteJoint_GetTargetAngle(j->id)));
    break;
  case PHYS3D_JOINT_SPHERICAL: {
    b3Quat target = b3SphericalJoint_GetTargetRotation(j->id);
    if (!table_quat_field(L, 2, "rotation", "target_rotation", &target))
      table_rotation(L, 2, &target);
    b3SphericalJoint_SetTargetRotation(j->id, b3NormalizeQuat(target));
    break;
  }
  case PHYS3D_JOINT_WHEEL:
    b3WheelJoint_SetTargetSteeringAngle(
        j->id, table_number(L, 2, "angle", "steering_angle",
                            b3WheelJoint_GetTargetSteeringAngle(j->id)));
    break;
  case PHYS3D_JOINT_MOTOR:
    b3MotorJoint_SetLinearVelocity(
        j->id, table_vec3(L, 2, "linear_velocity", "linearVelocity",
                          b3MotorJoint_GetLinearVelocity(j->id)));
    b3MotorJoint_SetAngularVelocity(
        j->id, table_vec3(L, 2, "angular_velocity", "angularVelocity",
                          b3MotorJoint_GetAngularVelocity(j->id)));
    break;
  default:
    return luaL_error(L, "phys3d_joint_set_target: unsupported joint type");
  }
  b3Joint_WakeBodies(j->id);
  return 0;
}

static void push_contact_list(lua_State *L, Phys3dContactSnapshot *items,
                              int count) {
  lua_newtable(L);
  for (int i = 0; i < count; ++i) {
    Phys3dContactSnapshot *e = &items[i];
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
    lua_pushnumber(L, e->nz);
    lua_setfield(L, -2, "nz");
    lua_pushinteger(L, e->point_count);
    lua_setfield(L, -2, "point_count");
    lua_pushnumber(L, e->x);
    lua_setfield(L, -2, "x");
    lua_pushnumber(L, e->y);
    lua_setfield(L, -2, "y");
    lua_pushnumber(L, e->z);
    lua_setfield(L, -2, "z");
    if (e->approach_speed != 0.0f) {
      lua_pushnumber(L, e->approach_speed);
      lua_setfield(L, -2, "approach_speed");
    }
    lua_rawseti(L, -2, i + 1);
  }
}

static void push_sensor_list(lua_State *L, Phys3dContactSnapshot *items,
                             int count) {
  lua_newtable(L);
  for (int i = 0; i < count; ++i) {
    Phys3dContactSnapshot *e = &items[i];
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

static void push_body_event_list(lua_State *L, Phys3dBodyEventSnapshot *items,
                                 int count) {
  lua_newtable(L);
  for (int i = 0; i < count; ++i) {
    Phys3dBodyEventSnapshot *e = &items[i];
    lua_newtable(L);
    lua_pushstring(L, e->body ? e->body : "");
    lua_setfield(L, -2, "body");
    lua_pushboolean(L, e->valid);
    lua_setfield(L, -2, "valid");
    lua_pushnumber(L, e->x);
    lua_setfield(L, -2, "x");
    lua_pushnumber(L, e->y);
    lua_setfield(L, -2, "y");
    lua_pushnumber(L, e->z);
    lua_setfield(L, -2, "z");
    lua_pushnumber(L, e->qx);
    lua_setfield(L, -2, "qx");
    lua_pushnumber(L, e->qy);
    lua_setfield(L, -2, "qy");
    lua_pushnumber(L, e->qz);
    lua_setfield(L, -2, "qz");
    lua_pushnumber(L, e->qw);
    lua_setfield(L, -2, "qw");
    lua_pushboolean(L, e->fell_asleep);
    lua_setfield(L, -2, "fell_asleep");
    lua_rawseti(L, -2, i + 1);
  }
}

static void push_joint_event_list(lua_State *L, Phys3dJointEventSnapshot *items,
                                  int count) {
  lua_newtable(L);
  for (int i = 0; i < count; ++i) {
    Phys3dJointEventSnapshot *e = &items[i];
    lua_newtable(L);
    lua_pushstring(L, e->joint ? e->joint : "");
    lua_setfield(L, -2, "joint");
    lua_pushstring(L, e->type ? e->type : "unknown");
    lua_setfield(L, -2, "type");
    lua_pushstring(L, e->body_a ? e->body_a : "");
    lua_setfield(L, -2, "a");
    lua_pushstring(L, e->body_b ? e->body_b : "");
    lua_setfield(L, -2, "b");
    lua_pushboolean(L, e->valid);
    lua_setfield(L, -2, "valid");
    lua_rawseti(L, -2, i + 1);
  }
}

static int l_phys3d_contacts(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
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
    return luaL_error(L, "phys3d_contacts: kind must be begin, end, or hit");
  }
  return 1;
}

static int l_phys3d_body_events(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  push_body_event_list(L, w->events.moves, w->events.move_count);
  return 1;
}

static int l_phys3d_sensors(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  const char *kind = luaL_optstring(L, 2, "begin");
  if (strcmp(kind, "begin") == 0) {
    push_sensor_list(L, w->events.sensor_begins, w->events.sensor_begin_count);
  } else if (strcmp(kind, "end") == 0) {
    push_sensor_list(L, w->events.sensor_ends, w->events.sensor_end_count);
  } else {
    return luaL_error(L, "phys3d_sensors: kind must be begin or end");
  }
  return 1;
}

static int l_phys3d_joint_events(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  push_joint_event_list(L, w->events.joints, w->events.joint_count);
  return 1;
}

static b3QueryFilter parse_query_filter(lua_State *L, int idx) {
  b3QueryFilter filter = b3DefaultQueryFilter();
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

static void push_raycast_hit(lua_State *L, b3ShapeId shape_id, b3Pos point,
                             b3Vec3 normal, float fraction,
                             uint64_t user_material_id, int triangle_index,
                             int child_index) {
  push_shape_id_view(L, shape_id);
  b3Vec3 p = b3ToVec3(point);
  lua_pushnumber(L, p.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, p.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, p.z);
  lua_setfield(L, -2, "z");
  lua_pushnumber(L, normal.x);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, normal.y);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, normal.z);
  lua_setfield(L, -2, "nz");
  lua_pushnumber(L, fraction);
  lua_setfield(L, -2, "fraction");
  lua_pushinteger(L, (lua_Integer)user_material_id);
  lua_setfield(L, -2, "hit_material_id");
  lua_pushinteger(L, triangle_index);
  lua_setfield(L, -2, "triangle_index");
  lua_pushinteger(L, child_index);
  lua_setfield(L, -2, "child_index");
}

typedef struct Phys3dQueryContext {
  lua_State *L;
  int results_ref;
  int visitor_ref;
  int count;
  bool continue_all;
  char *error;
} Phys3dQueryContext;

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

static bool overlap_result_callback(b3ShapeId shape_id, void *context) {
  Phys3dQueryContext *ctx = (Phys3dQueryContext *)context;
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
    g_phys3d_callback_depth++;
    int status = lua_pcall(L, 1, 1, 0);
    g_phys3d_callback_depth--;
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

static float raycast_result_callback(b3ShapeId shape_id, b3Pos point,
                                     b3Vec3 normal, float fraction,
                                     uint64_t user_material_id,
                                     int triangle_index, int child_index,
                                     void *context) {
  Phys3dQueryContext *ctx = (Phys3dQueryContext *)context;
  lua_State *L = ctx->L;
  if (ctx->error)
    return 0.0f;

  lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->results_ref);
  push_raycast_hit(L, shape_id, point, normal, fraction, user_material_id,
                   triangle_index, child_index);
  bool include = true;
  float result = ctx->continue_all ? 1.0f : fraction;

  if (ctx->visitor_ref != LUA_NOREF) {
    lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->visitor_ref);
    lua_pushvalue(L, -2);
    g_phys3d_callback_depth++;
    int status = lua_pcall(L, 1, 1, 0);
    g_phys3d_callback_depth--;
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

static void set_tree_stats(lua_State *L, b3TreeStats stats) {
  lua_pushinteger(L, stats.nodeVisits);
  lua_setfield(L, -2, "node_visits");
  lua_pushinteger(L, stats.leafVisits);
  lua_setfield(L, -2, "leaf_visits");
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

static int l_phys3d_raycast(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Vec3 origin;
  b3Vec3 translation;
  parse_ray_query(L, 2, "phys3d_raycast", &origin, &translation);
  b3QueryFilter filter = parse_query_filter(L, 2);
  bool collect_all = parse_raycast_mode_all(L, 2, "phys3d_raycast");

  if (!lua_isfunction(L, 3) && !collect_all) {
    b3RayResult hit =
        b3World_CastRayClosest(w->id, b3ToPos(origin), translation, filter);
    if (!hit.hit) {
      lua_pushnil(L);
      return 1;
    }
    push_raycast_hit(L, hit.shapeId, hit.point, hit.normal, hit.fraction,
                     hit.userMaterialId, hit.triangleIndex, hit.childIndex);
    lua_pushinteger(L, hit.nodeVisits);
    lua_setfield(L, -2, "node_visits");
    lua_pushinteger(L, hit.leafVisits);
    lua_setfield(L, -2, "leaf_visits");
    return 1;
  }

  lua_newtable(L);
  int results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  int visitor_ref = LUA_NOREF;
  if (lua_isfunction(L, 3)) {
    lua_pushvalue(L, 3);
    visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  Phys3dQueryContext ctx = {.L = L,
                            .results_ref = results_ref,
                            .visitor_ref = visitor_ref,
                            .count = 0,
                            .continue_all =
                                collect_all && visitor_ref == LUA_NOREF,
                            .error = NULL};
  b3TreeStats stats = b3World_CastRay(w->id, b3ToPos(origin), translation,
                                      filter, raycast_result_callback, &ctx);
  if (ctx.error) {
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    if (visitor_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
    return push_visitor_error(L, "phys3d_raycast", ctx.error);
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, results_ref);
  set_tree_stats(L, stats);
  luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
  if (visitor_ref != LUA_NOREF)
    luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
  return 1;
}

static int l_phys3d_overlap_aabb(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Vec3 lower = {table_number(L, 2, "min_x", "minX", 0.0f),
                  table_number(L, 2, "min_y", "minY", 0.0f),
                  table_number(L, 2, "min_z", "minZ", 0.0f)};
  lower = table_vec3(L, 2, "min", NULL, lower);
  b3Vec3 upper = {table_number(L, 2, "max_x", "maxX", 0.0f),
                  table_number(L, 2, "max_y", "maxY", 0.0f),
                  table_number(L, 2, "max_z", "maxZ", 0.0f)};
  upper = table_vec3(L, 2, "max", NULL, upper);
  if (lower.x > upper.x || lower.y > upper.y || lower.z > upper.z)
    return luaL_error(L, "phys3d_overlap_aabb: min must be <= max");
  b3AABB aabb = {lower, upper};
  b3QueryFilter filter = parse_query_filter(L, 2);

  lua_newtable(L);
  int results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  int visitor_ref = LUA_NOREF;
  if (lua_isfunction(L, 3)) {
    lua_pushvalue(L, 3);
    visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  Phys3dQueryContext ctx = {.L = L,
                            .results_ref = results_ref,
                            .visitor_ref = visitor_ref,
                            .count = 0,
                            .continue_all = false,
                            .error = NULL};
  b3TreeStats stats =
      b3World_OverlapAABB(w->id, aabb, filter, overlap_result_callback, &ctx);
  if (ctx.error) {
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    if (visitor_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
    return push_visitor_error(L, "phys3d_overlap_aabb", ctx.error);
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, results_ref);
  set_tree_stats(L, stats);
  luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
  if (visitor_ref != LUA_NOREF)
    luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
  return 1;
}

#define PHYS3D_PROXY_MAX_POINTS 8

// Parses { sphere = { r, center } } | { box = { hx, hy, hz, center, quat } } |
// { capsule = { a, b, r } } into a shape proxy. The proxy points are relative
// to the returned origin so the query stays precise far from the world origin.
static b3Pos parse_shape_proxy(lua_State *L, int idx, const char *fn_name,
                               b3Vec3 *points, b3ShapeProxy *proxy) {
  idx = abs_index(L, idx);
  if (table_get_any(L, idx, "sphere", NULL)) {
    if (!lua_istable(L, -1))
      luaL_error(L, "%s: sphere must be a table", fn_name);
    int t = lua_gettop(L);
    float r = table_number(L, t, "r", "radius", 0.0f);
    if (r <= 0.0f)
      luaL_error(L, "%s: sphere r must be > 0", fn_name);
    b3Vec3 center = table_vec3(L, t, "center", NULL, b3Vec3_zero);
    lua_pop(L, 1);
    points[0] = b3Vec3_zero;
    proxy->points = points;
    proxy->count = 1;
    proxy->radius = r;
    return b3ToPos(center);
  }
  if (table_get_any(L, idx, "box", NULL)) {
    if (!lua_istable(L, -1))
      luaL_error(L, "%s: box must be a table", fn_name);
    int t = lua_gettop(L);
    float hx = table_number(L, t, "hx", NULL, 0.0f);
    float hy = table_number(L, t, "hy", NULL, 0.0f);
    float hz = table_number(L, t, "hz", NULL, 0.0f);
    if (hx <= 0.0f || hy <= 0.0f || hz <= 0.0f)
      luaL_error(L, "%s: box hx, hy and hz must be > 0", fn_name);
    float radius = table_number(L, t, "radius", "r", 0.0f);
    if (radius < 0.0f)
      luaL_error(L, "%s: box radius must be >= 0", fn_name);
    b3Vec3 center = table_vec3(L, t, "center", NULL, b3Vec3_zero);
    b3Quat rotation = b3Quat_identity;
    bool has_rotation = false;
    if (table_get_any(L, t, "quat", NULL)) {
      if (lua_istable(L, -1)) {
        rotation = value_quat(L, lua_gettop(L), b3Quat_identity);
        has_rotation = true;
      }
      lua_pop(L, 1);
    }
    lua_pop(L, 1);
    int count = 0;
    for (int ix = -1; ix <= 1; ix += 2) {
      for (int iy = -1; iy <= 1; iy += 2) {
        for (int iz = -1; iz <= 1; iz += 2) {
          b3Vec3 corner = {hx * (float)ix, hy * (float)iy, hz * (float)iz};
          points[count++] =
              has_rotation ? b3RotateVector(rotation, corner) : corner;
        }
      }
    }
    proxy->points = points;
    proxy->count = count;
    proxy->radius = radius;
    return b3ToPos(center);
  }
  if (table_get_any(L, idx, "capsule", NULL)) {
    if (!lua_istable(L, -1))
      luaL_error(L, "%s: capsule must be a table", fn_name);
    int t = lua_gettop(L);
    b3Vec3 a = table_vec3(L, t, "a", NULL, b3Vec3_zero);
    b3Vec3 c = table_vec3(L, t, "b", NULL, b3Vec3_zero);
    float r = table_number(L, t, "r", "radius", 0.0f);
    if (r <= 0.0f)
      luaL_error(L, "%s: capsule r must be > 0", fn_name);
    if (b3DistanceSquared(a, c) <= 1e-12f)
      luaL_error(L, "%s: capsule endpoints must be distinct", fn_name);
    lua_pop(L, 1);
    b3Vec3 mid = b3MulSV(0.5f, b3Add(a, c));
    points[0] = b3Sub(a, mid);
    points[1] = b3Sub(c, mid);
    proxy->points = points;
    proxy->count = 2;
    proxy->radius = r;
    return b3ToPos(mid);
  }
  luaL_error(L, "%s: expected sphere, box, or capsule", fn_name);
  return b3ToPos(b3Vec3_zero);
}

static b3Vec3 parse_translation(lua_State *L, int idx, const char *fn_name) {
  b3Vec3 translation = {table_number(L, idx, "dx", NULL, 0.0f),
                        table_number(L, idx, "dy", NULL, 0.0f),
                        table_number(L, idx, "dz", NULL, 0.0f)};
  translation = table_vec3(L, idx, "translation", "delta", translation);
  float max_fraction =
      table_number(L, idx, "max_fraction", "maxFraction", 1.0f);
  if (max_fraction < 0.0f)
    max_fraction = 0.0f;
  translation = b3MulSV(max_fraction, translation);
  if (translation.x * translation.x + translation.y * translation.y +
          translation.z * translation.z <=
      1e-12f)
    luaL_error(L, "%s: translation must be non-zero", fn_name);
  return translation;
}

static int l_phys3d_overlap_shape(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Vec3 points[PHYS3D_PROXY_MAX_POINTS];
  b3ShapeProxy proxy;
  b3Pos origin =
      parse_shape_proxy(L, 2, "phys3d_overlap_shape", points, &proxy);
  b3QueryFilter filter = parse_query_filter(L, 2);

  lua_newtable(L);
  int results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  int visitor_ref = LUA_NOREF;
  if (lua_isfunction(L, 3)) {
    lua_pushvalue(L, 3);
    visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  Phys3dQueryContext ctx = {.L = L,
                            .results_ref = results_ref,
                            .visitor_ref = visitor_ref,
                            .count = 0,
                            .continue_all = false,
                            .error = NULL};
  b3TreeStats stats = b3World_OverlapShape(w->id, origin, &proxy, filter,
                                           overlap_result_callback, &ctx);
  if (ctx.error) {
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    if (visitor_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
    return push_visitor_error(L, "phys3d_overlap_shape", ctx.error);
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, results_ref);
  set_tree_stats(L, stats);
  luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
  if (visitor_ref != LUA_NOREF)
    luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
  return 1;
}

static int l_phys3d_shape_cast(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Vec3 points[PHYS3D_PROXY_MAX_POINTS];
  b3ShapeProxy proxy;
  b3Pos origin = parse_shape_proxy(L, 2, "phys3d_shape_cast", points, &proxy);
  b3Vec3 translation = parse_translation(L, 2, "phys3d_shape_cast");
  b3QueryFilter filter = parse_query_filter(L, 2);

  lua_newtable(L);
  int results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  int visitor_ref = LUA_NOREF;
  if (lua_isfunction(L, 3)) {
    lua_pushvalue(L, 3);
    visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  Phys3dQueryContext ctx = {.L = L,
                            .results_ref = results_ref,
                            .visitor_ref = visitor_ref,
                            .count = 0,
                            .continue_all = false,
                            .error = NULL};
  b3TreeStats stats = b3World_CastShape(w->id, origin, &proxy, translation,
                                        filter, raycast_result_callback, &ctx);
  if (ctx.error) {
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    if (visitor_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
    return push_visitor_error(L, "phys3d_shape_cast", ctx.error);
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

// Parses the mover capsule { a = {x,y,z}, b = {x,y,z}, r }. The capsule is
// returned relative to the midpoint origin so the query stays precise far
// from the world origin.
static b3Pos parse_mover_capsule(lua_State *L, int idx, const char *fn_name,
                                 b3Capsule *mover) {
  b3Vec3 a = table_vec3(L, idx, "a", NULL, b3Vec3_zero);
  b3Vec3 c = table_vec3(L, idx, "b", NULL, b3Vec3_zero);
  mover->radius = table_number(L, idx, "r", "radius", 0.0f);
  if (b3DistanceSquared(a, c) <= 1e-12f)
    luaL_error(L, "%s: mover endpoints must be distinct", fn_name);
  if (mover->radius <= 0.011f)
    luaL_error(L, "%s: mover radius must be > 0.011", fn_name);
  b3Vec3 mid = b3MulSV(0.5f, b3Add(a, c));
  mover->center1 = b3Sub(a, mid);
  mover->center2 = b3Sub(c, mid);
  return b3ToPos(mid);
}

static int l_phys3d_cast_mover(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Capsule mover;
  b3Pos origin = parse_mover_capsule(L, 2, "phys3d_cast_mover", &mover);
  b3Vec3 translation = parse_translation(L, 2, "phys3d_cast_mover");
  b3QueryFilter filter = parse_query_filter(L, 2);
  float fraction =
      b3World_CastMover(w->id, origin, &mover, translation, filter, NULL, NULL);

  lua_newtable(L);
  lua_pushnumber(L, fraction);
  lua_setfield(L, -2, "fraction");
  lua_pushnumber(L, translation.x * fraction);
  lua_setfield(L, -2, "dx");
  lua_pushnumber(L, translation.y * fraction);
  lua_setfield(L, -2, "dy");
  lua_pushnumber(L, translation.z * fraction);
  lua_setfield(L, -2, "dz");
  return 1;
}

typedef struct Phys3dMoverContext {
  lua_State *L;
  b3Pos origin;
  int results_ref;
  int visitor_ref;
  int count;
  char *error;
} Phys3dMoverContext;

static void push_mover_plane(lua_State *L, b3Pos origin, b3ShapeId shape_id,
                             const b3PlaneResult *plane, int plane_count) {
  push_shape_id_view(L, shape_id);
  // The plane result is relative to the query origin; convert back to world.
  lua_pushnumber(L, origin.x + plane->point.x);
  lua_setfield(L, -2, "x");
  lua_pushnumber(L, origin.y + plane->point.y);
  lua_setfield(L, -2, "y");
  lua_pushnumber(L, origin.z + plane->point.z);
  lua_setfield(L, -2, "z");
  lua_pushnumber(L, plane->plane.normal.x);
  lua_setfield(L, -2, "nx");
  lua_pushnumber(L, plane->plane.normal.y);
  lua_setfield(L, -2, "ny");
  lua_pushnumber(L, plane->plane.normal.z);
  lua_setfield(L, -2, "nz");
  lua_pushnumber(L, plane->plane.offset + plane->plane.normal.x * origin.x +
                        plane->plane.normal.y * origin.y +
                        plane->plane.normal.z * origin.z);
  lua_setfield(L, -2, "offset");
  lua_pushinteger(L, plane_count);
  lua_setfield(L, -2, "plane_count");
}

static bool mover_plane_callback(b3ShapeId shape_id, const b3PlaneResult *plane,
                                 int plane_count, void *context) {
  Phys3dMoverContext *ctx = (Phys3dMoverContext *)context;
  lua_State *L = ctx->L;
  if (ctx->error)
    return false;

  lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->results_ref);
  push_mover_plane(L, ctx->origin, shape_id, plane, plane_count);
  bool include = true;
  bool keep_going = true;

  if (ctx->visitor_ref != LUA_NOREF) {
    lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->visitor_ref);
    lua_pushvalue(L, -2);
    g_phys3d_callback_depth++;
    int status = lua_pcall(L, 1, 1, 0);
    g_phys3d_callback_depth--;
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

static int l_phys3d_collide_mover(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  luaL_checktype(L, 2, LUA_TTABLE);
  b3Capsule mover;
  b3Pos origin = parse_mover_capsule(L, 2, "phys3d_collide_mover", &mover);
  b3QueryFilter filter = parse_query_filter(L, 2);

  lua_newtable(L);
  int results_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  int visitor_ref = LUA_NOREF;
  if (lua_isfunction(L, 3)) {
    lua_pushvalue(L, 3);
    visitor_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  Phys3dMoverContext ctx = {.L = L,
                            .origin = origin,
                            .results_ref = results_ref,
                            .visitor_ref = visitor_ref,
                            .count = 0,
                            .error = NULL};
  b3World_CollideMover(w->id, origin, &mover, filter, mover_plane_callback,
                       &ctx);
  if (ctx.error) {
    luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
    if (visitor_ref != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
    return push_visitor_error(L, "phys3d_collide_mover", ctx.error);
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, results_ref);
  luaL_unref(L, LUA_REGISTRYINDEX, results_ref);
  if (visitor_ref != LUA_NOREF)
    luaL_unref(L, LUA_REGISTRYINDEX, visitor_ref);
  return 1;
}

static void set_profile_number(lua_State *L, const char *key, float value) {
  lua_pushnumber(L, value);
  lua_setfield(L, -2, key);
}

static int l_phys3d_profile(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  b3Profile p = b3World_GetProfile(w->id);
  lua_newtable(L);
  set_profile_number(L, "step", p.step);
  set_profile_number(L, "pairs", p.pairs);
  set_profile_number(L, "collide", p.collide);
  set_profile_number(L, "solve", p.solve);
  set_profile_number(L, "solver_setup", p.solverSetup);
  set_profile_number(L, "constraints", p.constraints);
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
  set_profile_number(L, "sensor_hits", p.sensorHits);
  set_profile_number(L, "joint_events", p.jointEvents);
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

static int l_phys3d_counters(lua_State *L) {
  Phys3dWorld *w = query_world_ref(L, 1);
  if (!w)
    return push_not_found(L);
  b3Counters c = b3World_GetCounters(w->id);
  lua_newtable(L);
  set_counter_integer(L, "body_count", c.bodyCount);
  set_counter_integer(L, "shape_count", c.shapeCount);
  set_counter_integer(L, "contact_count", c.contactCount);
  set_counter_integer(L, "joint_count", c.jointCount);
  set_counter_integer(L, "island_count", c.islandCount);
  set_counter_integer(L, "stack_used", c.stackUsed);
  set_counter_integer(L, "arena_capacity", c.arenaCapacity);
  set_counter_integer(L, "static_tree_height", c.staticTreeHeight);
  set_counter_integer(L, "tree_height", c.treeHeight);
  set_counter_integer(L, "sat_call_count", c.satCallCount);
  set_counter_integer(L, "sat_cache_hit_count", c.satCacheHitCount);
  set_counter_integer(L, "byte_count", c.byteCount);
  set_counter_integer(L, "task_count", c.taskCount);
  set_counter_integer(L, "awake_contact_count", c.awakeContactCount);
  set_counter_integer(L, "recycled_contact_count", c.recycledContactCount);
  set_counter_integer(L, "distance_iterations", c.distanceIterations);
  set_counter_integer(L, "push_back_iterations", c.pushBackIterations);
  set_counter_integer(L, "root_iterations", c.rootIterations);
  lua_newtable(L);
  for (int i = 0; i < 24; ++i) {
    lua_pushinteger(L, c.colorCounts[i]);
    lua_rawseti(L, -2, i + 1);
  }
  lua_setfield(L, -2, "color_counts");
  lua_newtable(L);
  for (int i = 0; i < B3_CONTACT_MANIFOLD_COUNT_BUCKETS; ++i) {
    lua_pushinteger(L, c.manifoldCounts[i]);
    lua_rawseti(L, -2, i + 1);
  }
  lua_setfield(L, -2, "manifold_counts");
  return 1;
}

void phys3d_lua_register(lua_State *L) {
  lua_pushinteger(L, PHYS3D_STATIC);
  lua_setglobal(L, "STATIC");
  lua_pushinteger(L, PHYS3D_KINEMATIC);
  lua_setglobal(L, "KINEMATIC");
  lua_pushinteger(L, PHYS3D_DYNAMIC);
  lua_setglobal(L, "DYNAMIC");

  lua_pushcfunction(L, l_phys3d_world);
  lua_setglobal(L, "phys3d_world");
  lua_pushcfunction(L, l_phys3d_begin);
  lua_setglobal(L, "phys3d_begin");
  lua_pushcfunction(L, l_phys3d_world_info);
  lua_setglobal(L, "phys3d_world_info");
  lua_pushcfunction(L, l_phys3d_body);
  lua_setglobal(L, "phys3d_body");
  lua_pushcfunction(L, l_phys3d_sphere);
  lua_setglobal(L, "phys3d_sphere");
  lua_pushcfunction(L, l_phys3d_box);
  lua_setglobal(L, "phys3d_box");
  lua_pushcfunction(L, l_phys3d_capsule);
  lua_setglobal(L, "phys3d_capsule");
  lua_pushcfunction(L, l_phys3d_cylinder);
  lua_setglobal(L, "phys3d_cylinder");
  lua_pushcfunction(L, l_phys3d_cone);
  lua_setglobal(L, "phys3d_cone");
  lua_pushcfunction(L, l_phys3d_hull);
  lua_setglobal(L, "phys3d_hull");
  lua_pushcfunction(L, l_phys3d_mesh);
  lua_setglobal(L, "phys3d_mesh");
  lua_pushcfunction(L, l_phys3d_height_field);
  lua_setglobal(L, "phys3d_height_field");
  lua_pushcfunction(L, l_phys3d_compound);
  lua_setglobal(L, "phys3d_compound");
  lua_pushcfunction(L, l_phys3d_joint);
  lua_setglobal(L, "phys3d_joint");
  lua_pushcfunction(L, l_phys3d_joint_info);
  lua_setglobal(L, "phys3d_joint_info");
  lua_pushcfunction(L, l_phys3d_joint_force);
  lua_setglobal(L, "phys3d_joint_force");
  lua_pushcfunction(L, l_phys3d_joint_torque);
  lua_setglobal(L, "phys3d_joint_torque");
  lua_pushcfunction(L, l_phys3d_joint_angle);
  lua_setglobal(L, "phys3d_joint_angle");
  lua_pushcfunction(L, l_phys3d_joint_translation);
  lua_setglobal(L, "phys3d_joint_translation");
  lua_pushcfunction(L, l_phys3d_joint_speed);
  lua_setglobal(L, "phys3d_joint_speed");
  lua_pushcfunction(L, l_phys3d_joint_length);
  lua_setglobal(L, "phys3d_joint_length");
  lua_pushcfunction(L, l_phys3d_joint_motor_force);
  lua_setglobal(L, "phys3d_joint_motor_force");
  lua_pushcfunction(L, l_phys3d_joint_motor_torque);
  lua_setglobal(L, "phys3d_joint_motor_torque");
  lua_pushcfunction(L, l_phys3d_joint_set_motor);
  lua_setglobal(L, "phys3d_joint_set_motor");
  lua_pushcfunction(L, l_phys3d_joint_set_limit);
  lua_setglobal(L, "phys3d_joint_set_limit");
  lua_pushcfunction(L, l_phys3d_joint_set_spring);
  lua_setglobal(L, "phys3d_joint_set_spring");
  lua_pushcfunction(L, l_phys3d_joint_set_target);
  lua_setglobal(L, "phys3d_joint_set_target");
  lua_pushcfunction(L, l_phys3d_body_joints);
  lua_setglobal(L, "phys3d_body_joints");
  lua_pushcfunction(L, l_phys3d_cast_mover);
  lua_setglobal(L, "phys3d_cast_mover");
  lua_pushcfunction(L, l_phys3d_collide_mover);
  lua_setglobal(L, "phys3d_collide_mover");
  lua_pushcfunction(L, l_phys3d_step);
  lua_setglobal(L, "phys3d_step");
  lua_pushcfunction(L, l_phys3d_pose);
  lua_setglobal(L, "phys3d_pose");
  lua_pushcfunction(L, l_phys3d_velocity);
  lua_setglobal(L, "phys3d_velocity");
  lua_pushcfunction(L, l_phys3d_mass);
  lua_setglobal(L, "phys3d_mass");
  lua_pushcfunction(L, l_phys3d_center);
  lua_setglobal(L, "phys3d_center");
  lua_pushcfunction(L, l_phys3d_world_point);
  lua_setglobal(L, "phys3d_world_point");
  lua_pushcfunction(L, l_phys3d_local_point);
  lua_setglobal(L, "phys3d_local_point");
  lua_pushcfunction(L, l_phys3d_velocity_at);
  lua_setglobal(L, "phys3d_velocity_at");
  lua_pushcfunction(L, l_phys3d_body_shapes);
  lua_setglobal(L, "phys3d_body_shapes");
  lua_pushcfunction(L, l_phys3d_body_contacts);
  lua_setglobal(L, "phys3d_body_contacts");
  lua_pushcfunction(L, l_phys3d_shape_raycast);
  lua_setglobal(L, "phys3d_shape_raycast");
  lua_pushcfunction(L, l_phys3d_shape_closest_point);
  lua_setglobal(L, "phys3d_shape_closest_point");
  lua_pushcfunction(L, l_phys3d_shape_aabb);
  lua_setglobal(L, "phys3d_shape_aabb");
  lua_pushcfunction(L, l_phys3d_shape_info);
  lua_setglobal(L, "phys3d_shape_info");
  lua_pushcfunction(L, l_phys3d_shape_set_material);
  lua_setglobal(L, "phys3d_shape_set_material");
  lua_pushcfunction(L, l_phys3d_shape_set_filter);
  lua_setglobal(L, "phys3d_shape_set_filter");
  lua_pushcfunction(L, l_phys3d_shape_set_events);
  lua_setglobal(L, "phys3d_shape_set_events");
  lua_pushcfunction(L, l_phys3d_contacts);
  lua_setglobal(L, "phys3d_contacts");
  lua_pushcfunction(L, l_phys3d_body_events);
  lua_setglobal(L, "phys3d_body_events");
  lua_pushcfunction(L, l_phys3d_sensors);
  lua_setglobal(L, "phys3d_sensors");
  lua_pushcfunction(L, l_phys3d_joint_events);
  lua_setglobal(L, "phys3d_joint_events");
  lua_pushcfunction(L, l_phys3d_raycast);
  lua_setglobal(L, "phys3d_raycast");
  lua_pushcfunction(L, l_phys3d_overlap_aabb);
  lua_setglobal(L, "phys3d_overlap_aabb");
  lua_pushcfunction(L, l_phys3d_overlap_shape);
  lua_setglobal(L, "phys3d_overlap_shape");
  lua_pushcfunction(L, l_phys3d_shape_cast);
  lua_setglobal(L, "phys3d_shape_cast");
  lua_pushcfunction(L, l_phys3d_profile);
  lua_setglobal(L, "phys3d_profile");
  lua_pushcfunction(L, l_phys3d_counters);
  lua_setglobal(L, "phys3d_counters");
  lua_pushcfunction(L, l_phys3d_add_force);
  lua_setglobal(L, "phys3d_add_force");
  lua_pushcfunction(L, l_phys3d_add_force_center);
  lua_setglobal(L, "phys3d_add_force_center");
  lua_pushcfunction(L, l_phys3d_add_impulse);
  lua_setglobal(L, "phys3d_add_impulse");
  lua_pushcfunction(L, l_phys3d_add_impulse_center);
  lua_setglobal(L, "phys3d_add_impulse_center");
  lua_pushcfunction(L, l_phys3d_add_torque);
  lua_setglobal(L, "phys3d_add_torque");
  lua_pushcfunction(L, l_phys3d_add_angular_impulse);
  lua_setglobal(L, "phys3d_add_angular_impulse");
  lua_pushcfunction(L, l_phys3d_set_velocity);
  lua_setglobal(L, "phys3d_set_velocity");
  lua_pushcfunction(L, l_phys3d_teleport);
  lua_setglobal(L, "phys3d_teleport");
  lua_pushcfunction(L, l_phys3d_set_target);
  lua_setglobal(L, "phys3d_set_target");
}
