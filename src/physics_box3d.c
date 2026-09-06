// Box3D の即時モード層。key で宣言する world / body / shape / joint の table
// と、C API (include/lub/lub_api.h の lub_phys3d_*) の実装。Lua には触らない
// (Lua 面は src/lua_phys3d.c)。
#include "physics_box3d.h"
#include "phys3d_internal.h"

#include "api_internal.h"
#include "phys_common.h"

#include <SDL3/SDL.h>
#include <box3d/box3d.h>
#include <box3d/collision.h>
#include <box3d/math_functions.h>
#include <float.h>
#include <math.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#define PHYS3D_BODY_BUCKETS 256
#define PHYS3D_SHAPE_BUCKETS 64
#define PHYS3D_JOINT_BUCKETS 128
#define PHYS3D_TOMBSTONE_BUCKETS 256

typedef struct Phys3dShape {
  char *key;
  char *tag;
  char *material_name;
  struct Phys3dBody *body;
  b3ShapeId id;
  int32_t handle;
  uint64_t seen_generation;
  uint64_t desc_hash;
  uint64_t constructor_hash;
  bool constructor_warned;
  int material_id;
  int kind; // P3ShapeKind
  // box3d の shape が参照する (copy しない) 重い geometry。この struct が
  // 所有し、box3d の shape を壊したあとに解放する。
  b3MeshData *mesh_data;
  b3HeightFieldData *height_field_data;
  b3CompoundData *compound_data;
  struct Phys3dShape *next;
} Phys3dShape;

typedef struct Phys3dBody {
  char *key;
  struct Phys3dWorld *world;
  b3BodyId id;
  int32_t handle;
  uint64_t seen_generation;
  int64_t version;
  uint64_t constructor_hash;
  bool constructor_warned;
  Phys3dShape *shapes[PHYS3D_SHAPE_BUCKETS];
  struct Phys3dBody *next;
} Phys3dBody;

typedef struct Phys3dJoint {
  char *key;
  struct Phys3dWorld *world;
  Phys3dBody *body_a;
  Phys3dBody *body_b;
  b3JointId id;
  int32_t handle;
  uint64_t seen_generation;
  int64_t version;
  uint64_t constructor_hash;
  bool constructor_warned;
  int kind; // P3JointType
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
  int type; // P3JointType
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

struct Phys3dWorld {
  char *key;
  Phys3dState *state;
  b3WorldId id;
  int32_t handle;
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
  P3Callbacks callbacks;
  bool callbacks_pending;
  uint64_t callbacks_generation;
  struct Phys3dWorld *next;
};

static Phys3dWorld *g_mixer_world = NULL;

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
static void fill_shape_part(P3ShapePart *out, b3ShapeId shape_id);

static LubStr str_or_empty(const char *s) { return phys_str(s); }

static b3Vec3 vec3_of(LubVec3d v) { return (b3Vec3){v.x, v.y, v.z}; }
static LubVec3d lub_vec3(b3Vec3 v) { return (LubVec3d){v.x, v.y, v.z}; }
static LubVec3d lub_pos(b3Pos p) {
  return (LubVec3d){(float)p.x, (float)p.y, (float)p.z};
}
// 面の四元数は正規化済みが契約 (Lua 面は value_quat で正規化する)。ここで
// 正規化し直すと丸めが変わるので、そのまま使う。
static b3Quat quat_of(LubQuat3d q) {
  b3Quat out = {{q.x, q.y, q.z}, q.w};
  return out;
}
static LubQuat3d lub_quat(b3Quat q) {
  return (LubQuat3d){q.v.x, q.v.y, q.v.z, q.s};
}

// ------------------------------------------------------------- callbacks

static bool callbacks_any(const P3Callbacks *cb) {
  return cb->filter || cb->pre_solve || cb->friction || cb->restitution;
}

static void callbacks_install(Phys3dWorld *w) {
  if (!w || B3_IS_NULL(w->id) || !b3World_IsValid(w->id))
    return;
  b3World_SetCustomFilterCallback(
      w->id, w->callbacks.filter ? phys3d_custom_filter_callback : NULL,
      w->callbacks.filter ? w : NULL);
  b3World_SetPreSolveCallback(
      w->id, w->callbacks.pre_solve ? phys3d_pre_solve_callback : NULL,
      w->callbacks.pre_solve ? w : NULL);
  b3World_SetFrictionCallback(
      w->id, w->callbacks.friction ? phys3d_friction_callback : NULL);
  b3World_SetRestitutionCallback(
      w->id, w->callbacks.restitution ? phys3d_restitution_callback : NULL);
}

static void callbacks_clear(Phys3dWorld *w) {
  if (!w)
    return;
  if (w->callbacks.release)
    w->callbacks.release(w->callbacks.user);
  memset(&w->callbacks, 0, sizeof(w->callbacks));
  w->callbacks_pending = false;
  w->callbacks_generation = 0;
  callbacks_install(w);
}

static void callbacks_replace(Phys3dWorld *w, const P3Callbacks *cb) {
  callbacks_clear(w);
  if (!cb || !callbacks_any(cb))
    return;
  w->callbacks = *cb;
  w->callbacks_pending = !w->begun;
  w->callbacks_generation = w->begun ? w->generation : 0;
  callbacks_install(w);
}

// --------------------------------------------------------------- lookups

static Phys3dWorld *world_get(Phys3dState *state, const char *key) {
  if (!state || !key)
    return NULL;
  uint32_t i = phys_hash_str32(key) & (PHYS3D_WORLD_BUCKETS - 1);
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
  uint32_t i = phys_hash_str32(key) & (PHYS3D_WORLD_BUCKETS - 1);
  w = (Phys3dWorld *)SDL_calloc(1, sizeof(Phys3dWorld));
  if (!w)
    return NULL;
  w->key = phys_strdup(key);
  if (!w->key) {
    SDL_free(w);
    return NULL;
  }
  w->state = state;
  w->fixed_dt = 1.0f / 60.0f;
  w->substeps = 4;
  w->max_steps = 4;
  w->prune = true;
  w->version = INT64_MIN;
  w->id = b3_nullWorldId;
  w->handle = phys_handle_alloc(&state->handles, w, PHYS_HANDLE_WORLD);
  w->next = state->worlds[i];
  state->worlds[i] = w;
  return w;
}

static Phys3dBody *body_get(Phys3dWorld *w, const char *key) {
  if (!w || !key)
    return NULL;
  uint32_t i = phys_hash_str32(key) & (PHYS3D_BODY_BUCKETS - 1);
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
  uint32_t i = phys_hash_str32(key) & (PHYS3D_BODY_BUCKETS - 1);
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
  b->handle = phys_handle_alloc(&w->state->handles, b, PHYS_HANDLE_BODY);
  b->next = w->bodies[i];
  w->bodies[i] = b;
  return b;
}

static Phys3dShape *shape_get(Phys3dBody *b, const char *key) {
  if (!b || !key)
    return NULL;
  uint32_t i = phys_hash_str32(key) & (PHYS3D_SHAPE_BUCKETS - 1);
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
  uint32_t i = phys_hash_str32(key) & (PHYS3D_SHAPE_BUCKETS - 1);
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
  s->handle =
      phys_handle_alloc(&b->world->state->handles, s, PHYS_HANDLE_SHAPE);
  s->next = b->shapes[i];
  b->shapes[i] = s;
  return s;
}

static Phys3dJoint *joint_get(Phys3dWorld *w, const char *key) {
  if (!w || !key)
    return NULL;
  uint32_t i = phys_hash_str32(key) & (PHYS3D_JOINT_BUCKETS - 1);
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
  uint32_t i = phys_hash_str32(key) & (PHYS3D_JOINT_BUCKETS - 1);
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
  j->handle = phys_handle_alloc(&w->state->handles, j, PHYS_HANDLE_JOINT);
  j->next = w->joints[i];
  w->joints[i] = j;
  return j;
}

// ------------------------------------------------------------ tombstones

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

static void shape_tombstone_clear_all(Phys3dWorld *w) {
  if (!w)
    return;
  for (int i = 0; i < PHYS3D_TOMBSTONE_BUCKETS; ++i) {
    Phys3dShapeTombstone *t = w->shape_tombstones[i];
    while (t) {
      Phys3dShapeTombstone *next = t->next;
      SDL_free(t->body);
      SDL_free(t->shape);
      SDL_free(t->tag);
      SDL_free(t->material);
      SDL_free(t);
      t = next;
    }
    w->shape_tombstones[i] = NULL;
  }
}

// ---------------------------------------------------------------- events

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
  for (int i = 0; i < events->move_count; ++i) {
    SDL_free(events->moves[i].body);
    memset(&events->moves[i], 0, sizeof(events->moves[i]));
  }
  for (int i = 0; i < events->joint_count; ++i) {
    SDL_free(events->joints[i].joint);
    SDL_free(events->joints[i].body_a);
    SDL_free(events->joints[i].body_b);
    memset(&events->joints[i], 0, sizeof(events->joints[i]));
  }
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

// 配列の末尾に 1 要素ぶん (zero) を確保する。失敗は NULL。
static void *event_push_raw(void **items, int *count, int *cap,
                            size_t item_size, int first_cap) {
  if (*count >= *cap) {
    int new_cap = *cap ? *cap * 2 : first_cap;
    void *new_items = SDL_realloc(*items, item_size * (size_t)new_cap);
    if (!new_items)
      return NULL;
    memset((char *)new_items + item_size * (size_t)*cap, 0,
           item_size * (size_t)(new_cap - *cap));
    *items = new_items;
    *cap = new_cap;
  }
  void *out = (char *)*items + item_size * (size_t)(*count)++;
  memset(out, 0, item_size);
  return out;
}

static Phys3dContactSnapshot *event_push(Phys3dContactSnapshot **items,
                                         int *count, int *cap) {
  return (Phys3dContactSnapshot *)event_push_raw(
      (void **)items, count, cap, sizeof(Phys3dContactSnapshot), 8);
}

// -------------------------------------------------------------- commands

static void command_queue_clear(Phys3dCommandQueue *queue) {
  if (!queue)
    return;
  for (int i = 0; i < queue->count; ++i) {
    SDL_free(queue->items[i].body_key);
    memset(&queue->items[i], 0, sizeof(queue->items[i]));
  }
  queue->count = 0;
}

static void command_queue_free(Phys3dCommandQueue *queue) {
  if (!queue)
    return;
  command_queue_clear(queue);
  SDL_free(queue->items);
  memset(queue, 0, sizeof(*queue));
}

static Phys3dCommand *command_queue_push(Phys3dWorld *w, Phys3dBody *b,
                                         Phys3dCommandKind kind) {
  if (!w || !b || !b->key)
    return NULL;
  Phys3dCommandQueue *queue = &w->commands;
  if (queue->count >= queue->cap) {
    int new_cap = queue->cap ? queue->cap * 2 : 32;
    Phys3dCommand *new_items = (Phys3dCommand *)SDL_realloc(
        queue->items, sizeof(*queue->items) * new_cap);
    if (!new_items)
      return NULL;
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
    return NULL;
  }
  cmd->kind = kind;
  if (B3_IS_NON_NULL(b->id) && b3Body_IsValid(b->id))
    cmd->body_id_key = b3StoreBodyId(b->id);
  cmd->wake = true;
  cmd->transform.q = b3Quat_identity;
  return cmd;
}

// ------------------------------------------------------------------ free

static void shape_free_heavy_data(Phys3dShape *s) {
  if (s->mesh_data)
    b3DestroyMesh(s->mesh_data);
  if (s->height_field_data)
    b3DestroyHeightField(s->height_field_data);
  if (s->compound_data)
    b3DestroyCompound(s->compound_data);
  s->mesh_data = NULL;
  s->height_field_data = NULL;
  s->compound_data = NULL;
}

static void shape_free(Phys3dShape *s, bool destroy_id) {
  if (!s)
    return;
  if (destroy_id && B3_IS_NON_NULL(s->id) && b3Shape_IsValid(s->id))
    b3DestroyShape(s->id, true);
  shape_free_heavy_data(s);
  phys_handle_release(&s->body->world->state->handles, s->handle);
  phys_owned_string_clear(&s->tag);
  phys_owned_string_clear(&s->material_name);
  SDL_free(s->key);
  SDL_free(s);
}

static void joint_free(Phys3dJoint *j, bool destroy_id) {
  if (!j)
    return;
  if (destroy_id && B3_IS_NON_NULL(j->id) && b3Joint_IsValid(j->id))
    b3DestroyJoint(j->id, true);
  phys_handle_release(&j->world->state->handles, j->handle);
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
  phys_handle_release(&b->world->state->handles, b->handle);
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
  callbacks_clear(w);
  world_destroy_box3d_and_contents(w);
  event_buffer_free(&w->events);
  command_queue_free(&w->commands);
  phys_handle_release(&w->state->handles, w->handle);
  SDL_free(w->key);
  SDL_free(w);
}

void phys3d_state_init(Phys3dState *state) {
  memset(state, 0, sizeof(*state));
  phys_handles_init(&state->handles);
}

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
  phys_handles_free(&state->handles);
  phys_scratch_free(&state->scratch);
  memset(state, 0, sizeof(*state));
}

// -------------------------------------------------------- handle resolve

static Phys3dState *phys_state(App *app) { return &app->phys3; }

static Phys3dWorld *world_from_handle(App *app, LubHandle h) {
  return (Phys3dWorld *)phys_handle_get(&phys_state(app)->handles, h,
                                        PHYS_HANDLE_WORLD);
}

static Phys3dBody *body_from_handle(App *app, LubHandle h) {
  return (Phys3dBody *)phys_handle_get(&phys_state(app)->handles, h,
                                       PHYS_HANDLE_BODY);
}

static Phys3dShape *shape_from_handle(App *app, LubHandle h) {
  return (Phys3dShape *)phys_handle_get(&phys_state(app)->handles, h,
                                        PHYS_HANDLE_SHAPE);
}

static Phys3dJoint *joint_from_handle(App *app, LubHandle h) {
  return (Phys3dJoint *)phys_handle_get(&phys_state(app)->handles, h,
                                        PHYS_HANDLE_JOINT);
}

static Phys3dWorld *check_world(App *app, LubHandle h, const char *fn) {
  Phys3dWorld *w = world_from_handle(app, h);
  if (!w)
    lub_api_fail(app, "%s: phys3d world not found", fn);
  return w;
}

static Phys3dBody *check_body(App *app, LubHandle h, const char *fn) {
  Phys3dBody *b = body_from_handle(app, h);
  if (!b)
    lub_api_fail(app, "%s: phys3d body not found", fn);
  return b;
}

static Phys3dShape *check_shape(App *app, LubHandle h, const char *fn) {
  Phys3dShape *s = shape_from_handle(app, h);
  if (!s)
    lub_api_fail(app, "%s: phys3d shape not found", fn);
  return s;
}

static Phys3dJoint *check_joint(App *app, LubHandle h, const char *fn) {
  Phys3dJoint *j = joint_from_handle(app, h);
  if (!j)
    lub_api_fail(app, "%s: phys3d joint not found", fn);
  return j;
}

static Phys3dWorld *query_world(App *app, LubHandle h) {
  Phys3dWorld *w = world_from_handle(app, h);
  if (!w || B3_IS_NULL(w->id) || !b3World_IsValid(w->id))
    return NULL;
  return w;
}

static Phys3dBody *query_body(App *app, LubHandle h) {
  Phys3dBody *b = body_from_handle(app, h);
  return body_is_live(b) ? b : NULL;
}

static Phys3dShape *query_shape(App *app, LubHandle h) {
  Phys3dShape *s = shape_from_handle(app, h);
  return shape_is_live(s) ? s : NULL;
}

static Phys3dJoint *query_joint(App *app, LubHandle h) {
  Phys3dJoint *j = joint_from_handle(app, h);
  return joint_is_live(j) ? j : NULL;
}

static bool in_callback(App *app, const char *fn) {
  if (phys_state(app)->callback_depth <= 0)
    return false;
  lub_api_fail(
      app, "%s: physics mutation is not allowed inside phys3d callback", fn);
  return true;
}

static bool body_is_live(Phys3dBody *b) {
  return b && B3_IS_NON_NULL(b->id) && b3Body_IsValid(b->id);
}

static bool shape_is_live(Phys3dShape *s) {
  return s && B3_IS_NON_NULL(s->id) && b3Shape_IsValid(s->id);
}

static bool joint_is_live(Phys3dJoint *j) {
  return j && B3_IS_NON_NULL(j->id) && b3Joint_IsValid(j->id);
}

static bool key_copy(App *app, LubStr key, char *buf, size_t cap,
                     const char *fn) {
  if (!key.ptr || key.len <= 0) {
    lub_api_fail(app, "%s: key required", fn);
    return false;
  }
  if (!lub_str_copy(key, buf, cap)) {
    lub_api_fail(app, "%s: key too long", fn);
    return false;
  }
  return true;
}

static void *scratch_alloc(App *app, size_t bytes) {
  return phys_scratch_alloc(&phys_state(app)->scratch, bytes);
}

// ------------------------------------------------------------ desc init

static void p3_world_desc_init(P3WorldDesc *desc) {
  memset(desc, 0, sizeof(*desc));
  desc->gravity = (LubVec3d){0.0f, -9.8f, 0.0f};
  desc->fixed_dt = 1.0f / 60.0f;
  desc->substeps = 4;
  desc->max_steps = 4;
  desc->sleep = true;
  desc->continuous = true;
}

static void p3_body_desc_init(P3BodyDesc *desc) {
  memset(desc, 0, sizeof(*desc));
  desc->type = LUB_PHYS3D_BODY_TYPE_STATIC;
  desc->enabled = true;
  desc->awake = true;
  desc->sleep = true;
  desc->gravity_scale = 1.0f;
  desc->rotation = (LubQuat3d){0, 0, 0, 1};
  desc->initial_awake = true;
}

static void p3_shape_desc_init(P3ShapeDesc *desc) {
  memset(desc, 0, sizeof(*desc));
  desc->density = 1.0f;
  desc->friction = 0.6f;
  desc->filter.category_bits = 1u;
  desc->filter.mask_bits = UINT64_MAX;
}

static void p3_joint_desc_init(P3JointDesc *desc, int32_t type) {
  memset(desc, 0, sizeof(*desc));
  desc->type = type;
  desc->frame_a_rotation = (LubQuat3d){0, 0, 0, 1};
  desc->frame_b_rotation = (LubQuat3d){0, 0, 0, 1};
  desc->axis = (LubVec3d){0, 0, 1};
  desc->force_threshold = FLT_MAX;
  desc->torque_threshold = FLT_MAX;
  desc->length = 1.0f;
  desc->max_length = FLT_MAX;
  desc->lower_spring_force = -FLT_MAX;
  desc->upper_spring_force = FLT_MAX;
  desc->target_rotation = (LubQuat3d){0, 0, 0, 1};
  switch (type) {
  case LUB_PHYS3D_JOINT_TYPE_PARALLEL:
    desc->hertz = 1.0f;
    desc->damping_ratio = 1.0f;
    desc->max_torque = FLT_MAX;
    break;
  case LUB_PHYS3D_JOINT_TYPE_WHEEL:
    desc->enable_spring = true;
    desc->hertz = 1.0f;
    desc->damping_ratio = 0.7f;
    desc->steering_hertz = 1.0f;
    desc->steering_damping_ratio = 0.7f;
    break;
  default:
    break;
  }
}

// ----------------------------------------------------------------- world

static bool world_create_or_recreate(App *app, Phys3dWorld *w,
                                     const P3WorldDesc *desc) {
  int64_t version = desc->has_version ? desc->version : 0;
  bool needs_create =
      B3_IS_NULL(w->id) || !b3World_IsValid(w->id) || w->version != version;
  b3Vec3 gravity = vec3_of(desc->gravity);
  if (needs_create) {
    callbacks_clear(w);
    world_destroy_box3d_and_contents(w);
    b3WorldDef def = b3DefaultWorldDef();
    def.gravity = gravity;
    def.enableSleep = desc->sleep;
    w->id = b3CreateWorld(&def);
    if (B3_IS_NULL(w->id)) {
      lub_api_fail(app, "phys3d_world: b3CreateWorld failed");
      return false;
    }
    w->version = version;
  } else {
    b3World_SetGravity(w->id, gravity);
    b3World_EnableSleeping(w->id, desc->sleep);
  }
  b3World_EnableContinuous(w->id, desc->continuous);
  if (desc->has_hit_event_threshold)
    b3World_SetHitEventThreshold(w->id, desc->hit_event_threshold);
  w->fixed_dt = desc->fixed_dt > 0.0f ? desc->fixed_dt : 1.0f / 60.0f;
  w->substeps = desc->substeps > 0 ? desc->substeps : 4;
  w->max_steps = desc->max_steps > 0 ? desc->max_steps : 4;
  return true;
}

static LubStatus p3_world(LubContext *ctx, LubStr key, const P3WorldDesc *desc,
                          LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (in_callback(app, "phys3d_world"))
    return LUB_ERROR;
  char kbuf[PHYS_KEY_MAX];
  if (!key_copy(app, key, kbuf, sizeof(kbuf), "phys3d_world"))
    return LUB_ERROR;
  P3WorldDesc def;
  if (!desc) {
    p3_world_desc_init(&def);
    desc = &def;
  }
  Phys3dWorld *w = world_get_or_create(phys_state(app), kbuf);
  if (!w)
    return lub_api_fail(app, "phys3d_world: out of memory");
  if (!world_create_or_recreate(app, w, desc))
    return LUB_ERROR;
  callbacks_replace(w, &desc->callbacks);
  *out = w->handle;
  return LUB_OK;
}

static LubHandle p3_world_find(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  char kbuf[PHYS_KEY_MAX];
  if (!key.ptr || key.len <= 0 || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return 0;
  Phys3dWorld *w = world_get(phys_state(app), kbuf);
  return w ? w->handle : 0;
}

static LubStatus p3_begin(LubContext *ctx, LubHandle world, bool prune) {
  App *app = lub_api_app(ctx);
  if (in_callback(app, "phys3d_begin"))
    return LUB_ERROR;
  Phys3dWorld *w = check_world(app, world, "phys3d_begin");
  if (!w)
    return LUB_ERROR;
  w->generation++;
  if (w->generation == 0)
    w->generation = 1;
  if (w->callbacks_pending) {
    w->callbacks_generation = w->generation;
    w->callbacks_pending = false;
  } else if (callbacks_any(&w->callbacks) &&
             w->callbacks_generation != w->generation) {
    callbacks_clear(w);
  }
  w->prune = prune;
  w->begun = true;
  return LUB_OK;
}

static LubStatus p3_world_info(LubContext *ctx, LubHandle world,
                               P3WorldInfo *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  bool valid = B3_IS_NON_NULL(w->id) && b3World_IsValid(w->id);
  out->key = str_or_empty(w->key);
  out->valid = valid;
  out->version = (int32_t)w->version;
  out->generation = (int32_t)w->generation;
  out->begun = w->begun;
  out->prune = w->prune;
  out->fixed_dt = w->fixed_dt;
  out->substeps = w->substeps;
  out->max_steps = w->max_steps;
  out->accumulator = (float)w->accumulator;
  out->pending_commands = w->commands.count;
  out->callback_filter = w->callbacks.filter != NULL;
  out->callback_pre_solve = w->callbacks.pre_solve != NULL;
  out->callback_friction = w->callbacks.friction != NULL;
  out->callback_restitution = w->callbacks.restitution != NULL;
  if (!valid)
    return LUB_OK;
  out->gravity = lub_vec3(b3World_GetGravity(w->id));
  out->sleep = b3World_IsSleepingEnabled(w->id);
  out->continuous = b3World_IsContinuousEnabled(w->id);
  out->warm_starting = b3World_IsWarmStartingEnabled(w->id);
  out->restitution_threshold = b3World_GetRestitutionThreshold(w->id);
  out->hit_event_threshold = b3World_GetHitEventThreshold(w->id);
  out->maximum_linear_speed = b3World_GetMaximumLinearSpeed(w->id);
  out->awake_body_count = b3World_GetAwakeBodyCount(w->id);
  return LUB_OK;
}

// ------------------------------------------------------------------ body

static uint64_t body_constructor_hash(const P3BodyDesc *d) {
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, d->position.x);
  h = phys_hash_f32(h, d->position.y);
  h = phys_hash_f32(h, d->position.z);
  b3Quat q = quat_of(d->rotation);
  h = phys_hash_f32(h, q.v.x);
  h = phys_hash_f32(h, q.v.y);
  h = phys_hash_f32(h, q.v.z);
  h = phys_hash_f32(h, q.s);
  h = phys_hash_f32(h, d->linear_velocity.x);
  h = phys_hash_f32(h, d->linear_velocity.y);
  h = phys_hash_f32(h, d->linear_velocity.z);
  h = phys_hash_f32(h, d->angular_velocity.x);
  h = phys_hash_f32(h, d->angular_velocity.y);
  h = phys_hash_f32(h, d->angular_velocity.z);
  h = phys_hash_bool(h, d->initial_awake);
  return h;
}

static void log_body_constructor_drift(Phys3dBody *b, uint64_t hash) {
  if (b->constructor_hash == hash || b->constructor_warned)
    return;
  SDL_Log("phys3d_body('%s'): constructor fields changed without version bump",
          b->key);
  b->constructor_warned = true;
}

static b3BodyType body_type_from(int32_t type) {
  switch (type) {
  case LUB_PHYS3D_BODY_TYPE_KINEMATIC:
    return b3_kinematicBody;
  case LUB_PHYS3D_BODY_TYPE_DYNAMIC:
    return b3_dynamicBody;
  default:
    return b3_staticBody;
  }
}

static b3MotionLocks motion_locks_of(const P3BodyDesc *d) {
  b3MotionLocks locks = {d->lock_linear_x,  d->lock_linear_y,
                         d->lock_linear_z,  d->lock_angular_x,
                         d->lock_angular_y, d->lock_angular_z};
  return locks;
}

static bool body_create(App *app, Phys3dBody *b, const P3BodyDesc *d,
                        uint64_t constructor_hash, int64_t version) {
  b3BodyDef def = b3DefaultBodyDef();
  def.type = body_type_from(d->type);
  def.position = b3ToPos(vec3_of(d->position));
  def.rotation = quat_of(d->rotation);
  def.linearVelocity = vec3_of(d->linear_velocity);
  def.angularVelocity = vec3_of(d->angular_velocity);
  def.motionLocks = motion_locks_of(d);
  def.isBullet = d->bullet;
  def.gravityScale = d->gravity_scale;
  def.linearDamping = d->linear_damping;
  def.angularDamping = d->angular_damping;
  def.isAwake = d->initial_awake;
  if (d->has_enabled)
    def.isEnabled = d->enabled;
  if (d->has_sleep)
    def.enableSleep = d->sleep;
  if (d->has_sleep_threshold)
    def.sleepThreshold = d->sleep_threshold;
  def.userData = b;
  b->id = b3CreateBody(b->world->id, &def);
  if (B3_IS_NULL(b->id)) {
    lub_api_fail(app, "phys3d_body: b3CreateBody failed");
    return false;
  }
  if (d->has_awake)
    b3Body_SetAwake(b->id, d->awake);
  b->version = version;
  b->constructor_hash = constructor_hash;
  b->constructor_warned = false;
  return true;
}

static void body_apply_runtime(Phys3dBody *b, const P3BodyDesc *d) {
  b3Body_SetType(b->id, body_type_from(d->type));
  b3Body_SetMotionLocks(b->id, motion_locks_of(d));
  b3Body_SetBullet(b->id, d->bullet);
  b3Body_SetGravityScale(b->id, d->gravity_scale);
  b3Body_SetLinearDamping(b->id, d->linear_damping);
  b3Body_SetAngularDamping(b->id, d->angular_damping);
  if (d->has_awake)
    b3Body_SetAwake(b->id, d->awake);
  if (d->has_sleep)
    b3Body_EnableSleep(b->id, d->sleep);
  if (d->has_sleep_threshold)
    b3Body_SetSleepThreshold(b->id, d->sleep_threshold);
  if (d->has_enabled && b3Body_IsEnabled(b->id) != d->enabled) {
    if (d->enabled) {
      b3Body_Enable(b->id);
    } else {
      b3Body_Disable(b->id);
    }
  }
}

static LubStatus p3_body(LubContext *ctx, LubHandle world, LubStr key,
                         const P3BodyDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (in_callback(app, "phys3d_body"))
    return LUB_ERROR;
  Phys3dWorld *w = check_world(app, world, "phys3d_body");
  if (!w)
    return LUB_ERROR;
  char kbuf[PHYS_KEY_MAX];
  if (!key_copy(app, key, kbuf, sizeof(kbuf), "phys3d_body"))
    return LUB_ERROR;
  if (!desc)
    return lub_api_fail(app, "phys3d_body: desc required");
  if (desc->type < LUB_PHYS3D_BODY_TYPE_STATIC ||
      desc->type > LUB_PHYS3D_BODY_TYPE_DYNAMIC)
    return lub_api_fail(app, "phys3d_body: unknown body type %d",
                        (int)desc->type);
  if (desc->has_sleep_threshold && desc->sleep_threshold < 0.0f)
    return lub_api_fail(app, "phys3d_body: sleep_threshold must be >= 0");
  if (!w->begun)
    return lub_api_fail(app, "phys3d_body: call phys3d_begin(world) first");
  Phys3dBody *b = body_get_or_create(w, kbuf);
  if (!b)
    return lub_api_fail(app, "phys3d_body: out of memory");
  uint64_t constructor_hash = body_constructor_hash(desc);
  int64_t version =
      desc->has_version ? (int64_t)desc->version : (int64_t)constructor_hash;
  if (B3_IS_NULL(b->id) || !b3Body_IsValid(b->id) || b->version != version) {
    if (B3_IS_NON_NULL(b->id) && b3Body_IsValid(b->id)) {
      b3DestroyBody(b->id);
      body_free_shapes(b, false);
      b->id = b3_nullBodyId;
    }
    if (!body_create(app, b, desc, constructor_hash, version))
      return LUB_ERROR;
  } else {
    if (desc->has_version)
      log_body_constructor_drift(b, constructor_hash);
    b->constructor_hash = constructor_hash;
    body_apply_runtime(b, desc);
  }
  b->seen_generation = w->generation;
  *out = b->handle;
  return LUB_OK;
}

static LubHandle p3_body_find(LubContext *ctx, LubHandle world, LubStr key) {
  App *app = lub_api_app(ctx);
  Phys3dWorld *w = world_from_handle(app, world);
  char kbuf[PHYS_KEY_MAX];
  if (!w || !key.ptr || key.len <= 0 || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return 0;
  Phys3dBody *b = body_get(w, kbuf);
  return b ? b->handle : 0;
}

// ----------------------------------------------------------------- shape

static void shape_apply_density_default(Phys3dBody *body, P3ShapeDesc *desc) {
  if (!body || !desc || desc->has_density || !body_is_live(body))
    return;
  desc->density = b3Body_GetType(body->id) == b3_dynamicBody ? 1.0f : 0.0f;
}

static b3ShapeDef make_shape_def(const P3ShapeDesc *desc, Phys3dShape *shape) {
  b3ShapeDef def = b3DefaultShapeDef();
  def.userData = shape;
  def.density = desc->density;
  def.baseMaterial.friction = desc->friction;
  def.baseMaterial.restitution = desc->restitution;
  def.baseMaterial.userMaterialId = (uint64_t)desc->material_id;
  def.isSensor = desc->sensor;
  // Box3D は shape が opt in したときだけ custom filter を呼ぶ。phys2d と
  // 同じく world の filter callback を全 shape に効かせるので常に opt in
  // (callback が無ければ box3d 側で素通りする)。
  def.enableCustomFiltering = true;
  def.enableContactEvents = desc->contact;
  def.enableHitEvents = desc->hit;
  def.enableSensorEvents = desc->sensor_events;
  def.enablePreSolveEvents = desc->pre_solve;
  def.filter.categoryBits = desc->filter.category_bits;
  def.filter.maskBits = desc->filter.mask_bits;
  def.filter.groupIndex = desc->filter.group_index;
  return def;
}

static uint64_t shape_base_hash(const P3ShapeDesc *desc, int kind) {
  uint64_t h = phys_hash_init();
  h = phys_hash_u64(h, (uint64_t)kind);
  h = phys_hash_bool(h, desc->sensor);
  h = phys_hash_u64(h, desc->filter.category_bits);
  h = phys_hash_u64(h, desc->filter.mask_bits);
  h = phys_hash_i64(h, desc->filter.group_index);
  return h;
}

static void shape_apply_runtime_desc(Phys3dShape *shape,
                                     const P3ShapeDesc *desc) {
  b3Shape_SetDensity(shape->id, desc->density, true);
  // Box3D には field ごとの setter が無いので surface material をまとめて
  // 書き戻す。
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

static void log_shape_constructor_drift(const char *fn, Phys3dShape *shape,
                                        uint64_t hash) {
  if (shape->constructor_hash == hash || shape->constructor_warned)
    return;
  SDL_Log("%s('%s/%s'): constructor fields changed without version bump", fn,
          shape->body ? shape->body->key : "?", shape->key);
  shape->constructor_warned = true;
}

static bool shape_update_metadata(App *app, Phys3dShape *shape,
                                  const P3ShapeDesc *desc) {
  if (!phys_owned_string_set(&shape->tag, desc->tag) ||
      !phys_owned_string_set(&shape->material_name, desc->material_name)) {
    lub_api_fail(app, "phys3d shape metadata: out of memory");
    return false;
  }
  shape->material_id = desc->material_id;
  return true;
}

static void shape_mark_declared(Phys3dShape *shape, int kind,
                                uint64_t fallback_hash, const P3ShapeDesc *desc,
                                bool recreated) {
  if (recreated)
    shape->kind = kind;
  shape->desc_hash =
      desc->has_version ? (uint64_t)(int64_t)desc->version : fallback_hash;
  shape->constructor_hash = fallback_hash;
  if (recreated)
    shape->constructor_warned = false;
  shape->seen_generation = shape->body->world->generation;
  shape_tombstone_update_shape(shape);
}

// 共通の宣言手順。explicit_version は hull / mesh / height_field / compound
// (version 必須、geometry の hash では再生成しない)。create は recreated の
// ときだけ呼ばれ、shape->id (と heavy data) を作る。
typedef struct ShapeDeclare {
  const char *fn;
  int kind;
  LubHandle body;
  LubStr key;
  const P3ShapeDesc *desc;
  bool explicit_version;
  uint64_t geometry_hash;
  const void *geom;
  bool (*create)(App *app, Phys3dBody *b, Phys3dShape *shape,
                 const b3ShapeDef *def, const void *geom);
} ShapeDeclare;

static LubStatus shape_declare(App *app, const ShapeDeclare *d,
                               LubHandle *out) {
  *out = 0;
  if (in_callback(app, d->fn))
    return LUB_ERROR;
  Phys3dBody *b = check_body(app, d->body, d->fn);
  if (!b)
    return LUB_ERROR;
  char kbuf[PHYS_KEY_MAX];
  if (!key_copy(app, d->key, kbuf, sizeof(kbuf), d->fn))
    return LUB_ERROR;
  P3ShapeDesc desc = *d->desc;
  if (d->explicit_version && !desc.has_version)
    return lub_api_fail(app, "%s: explicit version is required", d->fn);
  shape_apply_density_default(b, &desc);
  uint64_t h = shape_base_hash(&desc, d->kind);
  h = phys_hash_u64(h, d->geometry_hash);
  uint64_t version = desc.has_version ? (uint64_t)(int64_t)desc.version : h;
  Phys3dShape *shape = shape_get_or_create(b, kbuf);
  if (!shape)
    return lub_api_fail(app, "%s: out of memory", d->fn);
  bool recreated =
      B3_IS_NULL(shape->id) || !b3Shape_IsValid(shape->id) ||
      shape->desc_hash != version ||
      (!d->explicit_version && !desc.has_version && shape->kind != d->kind);
  if (recreated) {
    if (B3_IS_NON_NULL(shape->id) && b3Shape_IsValid(shape->id))
      b3DestroyShape(shape->id, true);
    shape_free_heavy_data(shape);
    b3ShapeDef def = make_shape_def(&desc, shape);
    if (!d->create(app, b, shape, &def, d->geom))
      return LUB_ERROR;
    if (B3_IS_NULL(shape->id))
      return lub_api_fail(app, "%s: shape creation failed", d->fn);
  } else {
    if (desc.has_version)
      log_shape_constructor_drift(d->fn, shape, h);
    shape_apply_runtime_desc(shape, &desc);
  }
  if (!shape_update_metadata(app, shape, &desc))
    return LUB_ERROR;
  shape_mark_declared(shape, d->kind, h, &desc, recreated);
  *out = shape->handle;
  return LUB_OK;
}

static uint64_t hash_vec3(uint64_t h, LubVec3d v) {
  h = phys_hash_f32(h, v.x);
  h = phys_hash_f32(h, v.y);
  h = phys_hash_f32(h, v.z);
  return h;
}

static uint64_t hash_quat(uint64_t h, b3Quat q) {
  h = phys_hash_f32(h, q.v.x);
  h = phys_hash_f32(h, q.v.y);
  h = phys_hash_f32(h, q.v.z);
  h = phys_hash_f32(h, q.s);
  return h;
}

static bool create_sphere(App *app, Phys3dBody *b, Phys3dShape *shape,
                          const b3ShapeDef *def, const void *geom) {
  (void)app;
  const P3SphereDesc *g = (const P3SphereDesc *)geom;
  b3Sphere sphere = {.center = vec3_of(g->offset), .radius = g->r};
  shape->id = b3CreateSphereShape(b->id, def, &sphere);
  return true;
}

static LubStatus p3_sphere(LubContext *ctx, LubHandle body, LubStr key,
                           const P3SphereDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys3d_sphere: desc required");
  if (desc->r <= 0.0f)
    return lub_api_fail(app, "phys3d_sphere: r must be > 0");
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, desc->r);
  h = hash_vec3(h, desc->offset);
  ShapeDeclare d = {"phys3d_sphere",
                    LUB_PHYS3D_SHAPE_KIND_SPHERE,
                    body,
                    key,
                    &desc->shape,
                    false,
                    h,
                    desc,
                    create_sphere};
  return shape_declare(app, &d, out);
}

static bool create_box(App *app, Phys3dBody *b, Phys3dShape *shape,
                       const b3ShapeDef *def, const void *geom) {
  (void)app;
  const P3BoxDesc *g = (const P3BoxDesc *)geom;
  b3Vec3 offset = vec3_of(g->offset);
  b3BoxHull hull;
  if (g->has_rotation) {
    b3Transform transform = {offset, quat_of(g->rotation)};
    hull = b3MakeTransformedBoxHull(g->hx, g->hy, g->hz, transform);
  } else if (offset.x != 0.0f || offset.y != 0.0f || offset.z != 0.0f) {
    hull = b3MakeOffsetBoxHull(g->hx, g->hy, g->hz, offset);
  } else {
    hull = b3MakeBoxHull(g->hx, g->hy, g->hz);
  }
  shape->id = b3CreateHullShape(b->id, def, &hull.base);
  return true;
}

static LubStatus p3_box(LubContext *ctx, LubHandle body, LubStr key,
                        const P3BoxDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys3d_box: desc required");
  if (desc->hx <= 0.0f || desc->hy <= 0.0f || desc->hz <= 0.0f)
    return lub_api_fail(app, "phys3d_box: hx, hy and hz must be > 0");
  b3Quat rotation =
      desc->has_rotation ? quat_of(desc->rotation) : b3Quat_identity;
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, desc->hx);
  h = phys_hash_f32(h, desc->hy);
  h = phys_hash_f32(h, desc->hz);
  h = hash_vec3(h, desc->offset);
  h = hash_quat(h, rotation);
  ShapeDeclare d = {"phys3d_box",
                    LUB_PHYS3D_SHAPE_KIND_BOX,
                    body,
                    key,
                    &desc->shape,
                    false,
                    h,
                    desc,
                    create_box};
  return shape_declare(app, &d, out);
}

static bool create_capsule(App *app, Phys3dBody *b, Phys3dShape *shape,
                           const b3ShapeDef *def, const void *geom) {
  (void)app;
  const P3CapsuleDesc *g = (const P3CapsuleDesc *)geom;
  b3Capsule capsule = {
      .center1 = vec3_of(g->a), .center2 = vec3_of(g->b), .radius = g->r};
  shape->id = b3CreateCapsuleShape(b->id, def, &capsule);
  return true;
}

static LubStatus p3_capsule(LubContext *ctx, LubHandle body, LubStr key,
                            const P3CapsuleDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys3d_capsule: desc required");
  if (desc->r <= 0.0f)
    return lub_api_fail(app, "phys3d_capsule: r must be > 0");
  if (b3DistanceSquared(vec3_of(desc->a), vec3_of(desc->b)) <= 1e-12f)
    return lub_api_fail(app, "phys3d_capsule: endpoints must be distinct");
  uint64_t h = phys_hash_init();
  h = hash_vec3(h, desc->a);
  h = hash_vec3(h, desc->b);
  h = phys_hash_f32(h, desc->r);
  ShapeDeclare d = {"phys3d_capsule",
                    LUB_PHYS3D_SHAPE_KIND_CAPSULE,
                    body,
                    key,
                    &desc->shape,
                    false,
                    h,
                    desc,
                    create_capsule};
  return shape_declare(app, &d, out);
}

static bool create_cylinder(App *app, Phys3dBody *b, Phys3dShape *shape,
                            const b3ShapeDef *def, const void *geom) {
  const P3CylinderDesc *g = (const P3CylinderDesc *)geom;
  b3HullData *hull =
      b3CreateCylinder(g->height, g->radius, g->y_offset, g->sides);
  if (!hull) {
    lub_api_fail(app, "phys3d_cylinder: b3CreateCylinder failed");
    return false;
  }
  shape->id = b3CreateHullShape(b->id, def, hull);
  // b3CreateHullShape は hull を world 所有の database に copy するので、
  // 一時の hull は解放してよい。
  b3DestroyHull(hull);
  return true;
}

static LubStatus p3_cylinder(LubContext *ctx, LubHandle body, LubStr key,
                             const P3CylinderDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys3d_cylinder: desc required");
  if (desc->height <= 0.0f || desc->radius <= 0.0f)
    return lub_api_fail(app, "phys3d_cylinder: height and radius must be > 0");
  if (desc->sides < 3 || desc->sides > 32)
    return lub_api_fail(app, "phys3d_cylinder: sides must be between 3 and 32");
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, desc->height);
  h = phys_hash_f32(h, desc->radius);
  h = phys_hash_f32(h, desc->y_offset);
  h = phys_hash_i64(h, desc->sides);
  ShapeDeclare d = {"phys3d_cylinder",
                    LUB_PHYS3D_SHAPE_KIND_CYLINDER,
                    body,
                    key,
                    &desc->shape,
                    false,
                    h,
                    desc,
                    create_cylinder};
  return shape_declare(app, &d, out);
}

typedef struct ConeGeom {
  float height, radius1, radius2;
  int slices;
} ConeGeom;

static bool create_cone(App *app, Phys3dBody *b, Phys3dShape *shape,
                        const b3ShapeDef *def, const void *geom) {
  const ConeGeom *g = (const ConeGeom *)geom;
  b3HullData *hull = b3CreateCone(g->height, g->radius1, g->radius2, g->slices);
  if (!hull) {
    lub_api_fail(app, "phys3d_cone: b3CreateCone failed");
    return false;
  }
  shape->id = b3CreateHullShape(b->id, def, hull);
  b3DestroyHull(hull);
  return true;
}

static LubStatus p3_cone(LubContext *ctx, LubHandle body, LubStr key,
                         const P3ConeDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys3d_cone: desc required");
  if (desc->height <= 0.0f || desc->radius1 <= 0.0f)
    return lub_api_fail(app, "phys3d_cone: height and radius1 must be > 0");
  if (desc->radius2 < 0.0f)
    return lub_api_fail(app, "phys3d_cone: radius2 must be >= 0");
  if (desc->slices < 4 || desc->slices > 32)
    return lub_api_fail(app, "phys3d_cone: slices must be between 4 and 32");
  ConeGeom g = {desc->height, desc->radius1, desc->radius2, desc->slices};
  // b3CreateCone は radius2 > 0 を assert するので、0 は小さな cap に丸める。
  if (g.radius2 <= 0.0f)
    g.radius2 = g.radius1 * 1e-3f;
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, g.height);
  h = phys_hash_f32(h, g.radius1);
  h = phys_hash_f32(h, g.radius2);
  h = phys_hash_i64(h, g.slices);
  ShapeDeclare d = {"phys3d_cone",
                    LUB_PHYS3D_SHAPE_KIND_CONE,
                    body,
                    key,
                    &desc->shape,
                    false,
                    h,
                    &g,
                    create_cone};
  return shape_declare(app, &d, out);
}

static bool create_hull(App *app, Phys3dBody *b, Phys3dShape *shape,
                        const b3ShapeDef *def, const void *geom) {
  const P3HullDesc *g = (const P3HullDesc *)geom;
  // b3CreateHull は maxVertexCount を [4, 255] に丸める。
  b3HullData *hull =
      b3CreateHull((const b3Vec3 *)g->points, g->point_count, g->max_vertices);
  if (!hull) {
    lub_api_fail(app, "phys3d_hull: b3CreateHull failed (degenerate or "
                      "coplanar points?)");
    return false;
  }
  shape->id = b3CreateHullShape(b->id, def, hull);
  b3DestroyHull(hull);
  return true;
}

static LubStatus p3_hull(LubContext *ctx, LubHandle body, LubStr key,
                         const P3HullDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys3d_hull: desc required");
  if (!desc->points || desc->point_count < 4)
    return lub_api_fail(app, "phys3d_hull: at least 4 points are required");
  uint64_t h = phys_hash_init();
  for (int i = 0; i < desc->point_count * 3; ++i)
    h = phys_hash_f32(h, desc->points[i]);
  h = phys_hash_i64(h, desc->max_vertices);
  ShapeDeclare d = {"phys3d_hull",
                    LUB_PHYS3D_SHAPE_KIND_HULL,
                    body,
                    key,
                    &desc->shape,
                    true,
                    h,
                    desc,
                    create_hull};
  return shape_declare(app, &d, out);
}

typedef struct MeshGeom {
  const P3MeshDesc *desc;
  b3SurfaceMaterial *materials;
  uint8_t *material_indices;
  int triangle_count;
} MeshGeom;

static bool create_mesh(App *app, Phys3dBody *b, Phys3dShape *shape,
                        const b3ShapeDef *def_in, const void *geom) {
  const MeshGeom *g = (const MeshGeom *)geom;
  const P3MeshDesc *m = g->desc;
  b3MeshDef mesh_def = {0};
  mesh_def.vertices = (b3Vec3 *)m->positions;
  mesh_def.vertexCount = m->vertex_count;
  mesh_def.indices = (int32_t *)m->indices;
  mesh_def.triangleCount = g->triangle_count;
  mesh_def.materialIndices = g->material_indices;
  mesh_def.weldVertices = m->weld_vertices;
  mesh_def.weldTolerance = m->weld_tolerance;
  mesh_def.useMedianSplit = m->use_median_split;
  mesh_def.identifyEdges = m->identify_edges;
  int degenerate[16];
  for (int i = 0; i < 16; ++i)
    degenerate[i] = -1;
  // b3CreateMesh は入力を自前の blob に clone する。
  b3MeshData *mesh = b3CreateMesh(&mesh_def, degenerate, 16);
  for (int i = 0; i < 16 && degenerate[i] >= 0; ++i)
    SDL_Log("phys3d_mesh('%s/%s'): degenerate triangle %d skipped", b->key,
            shape->key, degenerate[i]);
  if (!mesh) {
    lub_api_fail(app, "phys3d_mesh: b3CreateMesh failed");
    return false;
  }
  b3ShapeDef def = *def_in;
  if (g->materials) {
    // b3CreateShapeInternal は material 配列を shape に copy する。
    def.materials = g->materials;
    def.materialCount = m->material_count;
  }
  shape->id = b3CreateMeshShape(b->id, &def, mesh, vec3_of(m->scale));
  // b3CreateMeshShape は mesh の pointer を持つだけなので、この shape が
  // box3d の shape を壊すまで所有する。
  shape->mesh_data = mesh;
  return true;
}

static LubStatus p3_mesh(LubContext *ctx, LubHandle body, LubStr key,
                         const P3MeshDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys3d_mesh: desc required");
  if (!desc->positions || desc->vertex_count < 3)
    return lub_api_fail(
        app, "phys3d_mesh: positions must hold at least 3 x/y/z triples");
  if (!desc->indices || desc->index_count <= 0 || (desc->index_count % 3) != 0)
    return lub_api_fail(app, "phys3d_mesh: indices must hold index triples");
  int triangle_count = desc->index_count / 3;
  for (int i = 0; i < desc->index_count; ++i) {
    if (desc->indices[i] < 0 || desc->indices[i] >= desc->vertex_count)
      return lub_api_fail(app, "phys3d_mesh: index out of range (0-based)");
  }
  int material_count = desc->materials ? desc->material_count : 0;
  if (desc->materials && (material_count < 1 || material_count > 255))
    return lub_api_fail(app,
                        "phys3d_mesh: materials length must be in [1, 255]");
  MeshGeom g = {desc, NULL, NULL, triangle_count};
  if (material_count > 0) {
    g.materials = (b3SurfaceMaterial *)SDL_malloc(sizeof(*g.materials) *
                                                  (size_t)material_count);
    if (!g.materials)
      return lub_api_fail(app, "phys3d_mesh: out of memory");
    for (int i = 0; i < material_count; ++i) {
      g.materials[i] = b3DefaultSurfaceMaterial();
      g.materials[i].friction = desc->materials[i].friction;
      g.materials[i].restitution = desc->materials[i].restitution;
      g.materials[i].userMaterialId = (uint64_t)desc->materials[i].material_id;
    }
  }
  if (desc->material_indices) {
    if (desc->material_index_count != triangle_count) {
      SDL_free(g.materials);
      return lub_api_fail(
          app,
          "phys3d_mesh: material_indices length must match triangle count");
    }
    g.material_indices =
        (uint8_t *)SDL_malloc(sizeof(uint8_t) * (size_t)triangle_count);
    if (!g.material_indices) {
      SDL_free(g.materials);
      return lub_api_fail(app, "phys3d_mesh: out of memory");
    }
    int limit = material_count > 0 ? material_count : 1;
    for (int i = 0; i < triangle_count; ++i) {
      int32_t m = desc->material_indices[i];
      if (m < 0 || m >= limit) {
        SDL_free(g.materials);
        SDL_free(g.material_indices);
        return lub_api_fail(app, "phys3d_mesh: material index out of range");
      }
      g.material_indices[i] = (uint8_t)m;
    }
  }
  uint64_t h = phys_hash_init();
  for (int i = 0; i < desc->vertex_count * 3; ++i)
    h = phys_hash_f32(h, desc->positions[i]);
  for (int i = 0; i < desc->index_count; ++i)
    h = phys_hash_i64(h, desc->indices[i]);
  h = hash_vec3(h, desc->scale);
  h = phys_hash_bool(h, desc->weld_vertices);
  h = phys_hash_f32(h, desc->weld_tolerance);
  h = phys_hash_bool(h, desc->identify_edges);
  ShapeDeclare d = {"phys3d_mesh",
                    LUB_PHYS3D_SHAPE_KIND_MESH,
                    body,
                    key,
                    &desc->shape,
                    true,
                    h,
                    &g,
                    create_mesh};
  LubStatus st = shape_declare(app, &d, out);
  SDL_free(g.materials);
  SDL_free(g.material_indices);
  return st;
}

typedef struct HeightFieldGeom {
  const P3HeightFieldDesc *desc;
  float min_height, max_height;
} HeightFieldGeom;

static bool create_height_field(App *app, Phys3dBody *b, Phys3dShape *shape,
                                const b3ShapeDef *def, const void *geom) {
  const HeightFieldGeom *g = (const HeightFieldGeom *)geom;
  b3HeightFieldDef hf_def = {0};
  hf_def.heights = (float *)g->desc->heights;
  hf_def.scale = vec3_of(g->desc->scale);
  hf_def.countX = g->desc->x_count;
  hf_def.countZ = g->desc->z_count;
  hf_def.globalMinimumHeight = g->min_height;
  hf_def.globalMaximumHeight = g->max_height;
  hf_def.clockwiseWinding = g->desc->clockwise_winding;
  // b3CreateHeightField は heights を量子化して自前の blob に持つ。
  b3HeightFieldData *hf = b3CreateHeightField(&hf_def);
  if (!hf) {
    lub_api_fail(app, "phys3d_height_field: b3CreateHeightField failed");
    return false;
  }
  shape->id = b3CreateHeightFieldShape(b->id, def, hf);
  shape->height_field_data = hf;
  return true;
}

static LubStatus p3_height_field(LubContext *ctx, LubHandle body, LubStr key,
                                 const P3HeightFieldDesc *desc,
                                 LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys3d_height_field: desc required");
  if (desc->x_count < 2 || desc->z_count < 2)
    return lub_api_fail(
        app, "phys3d_height_field: x_count and z_count must be >= 2");
  int height_count = desc->x_count * desc->z_count;
  if (!desc->heights)
    return lub_api_fail(app, "phys3d_height_field: heights array is required");
  if (desc->scale.x <= 0.0f || desc->scale.y <= 0.0f || desc->scale.z <= 0.0f)
    return lub_api_fail(app, "phys3d_height_field: scale must be positive");
  HeightFieldGeom g = {desc, desc->heights[0], desc->heights[0]};
  for (int i = 1; i < height_count; ++i) {
    if (desc->heights[i] < g.min_height)
      g.min_height = desc->heights[i];
    if (desc->heights[i] > g.max_height)
      g.max_height = desc->heights[i];
  }
  if (desc->has_min_height)
    g.min_height = desc->min_height;
  if (desc->has_max_height)
    g.max_height = desc->max_height;
  if (g.max_height - g.min_height < 1e-6f)
    g.max_height = g.min_height + 1.0f;
  uint64_t h = phys_hash_init();
  for (int i = 0; i < height_count; ++i)
    h = phys_hash_f32(h, desc->heights[i]);
  h = phys_hash_i64(h, desc->x_count);
  h = phys_hash_i64(h, desc->z_count);
  h = hash_vec3(h, desc->scale);
  h = phys_hash_f32(h, g.min_height);
  h = phys_hash_f32(h, g.max_height);
  h = phys_hash_bool(h, desc->clockwise_winding);
  ShapeDeclare d = {"phys3d_height_field",
                    LUB_PHYS3D_SHAPE_KIND_HEIGHT_FIELD,
                    body,
                    key,
                    &desc->shape,
                    true,
                    h,
                    &g,
                    create_height_field};
  return shape_declare(app, &d, out);
}

typedef struct CompoundGeom {
  b3CompoundSphereDef *spheres;
  int sphere_count;
  b3CompoundCapsuleDef *capsules;
  int capsule_count;
  b3CompoundHullDef *hulls;
  b3BoxHull *box_hulls;
  int hull_count;
} CompoundGeom;

static void compound_geom_free(CompoundGeom *g) {
  SDL_free(g->spheres);
  SDL_free(g->capsules);
  SDL_free(g->hulls);
  SDL_free(g->box_hulls);
  memset(g, 0, sizeof(*g));
}

static uint64_t hash_surface_material(uint64_t h,
                                      const b3SurfaceMaterial *material) {
  h = phys_hash_f32(h, material->friction);
  h = phys_hash_f32(h, material->restitution);
  h = phys_hash_u64(h, material->userMaterialId);
  return h;
}

static uint64_t hash_transform(uint64_t h, b3Transform t) {
  h = phys_hash_f32(h, t.p.x);
  h = phys_hash_f32(h, t.p.y);
  h = phys_hash_f32(h, t.p.z);
  return hash_quat(h, t.q);
}

// children を compound の child def に写す。戻り値は constructor hash。
static bool compound_children_build(App *app, const P3CompoundDesc *desc,
                                    CompoundGeom *g, uint64_t *hash) {
  memset(g, 0, sizeof(*g));
  int count = desc->child_count;
  if (!desc->children || count <= 0) {
    lub_api_fail(app, "phys3d_compound: children must not be empty");
    return false;
  }
  g->spheres = (b3CompoundSphereDef *)SDL_calloc(count, sizeof(*g->spheres));
  g->capsules = (b3CompoundCapsuleDef *)SDL_calloc(count, sizeof(*g->capsules));
  g->hulls = (b3CompoundHullDef *)SDL_calloc(count, sizeof(*g->hulls));
  g->box_hulls = (b3BoxHull *)SDL_calloc(count, sizeof(*g->box_hulls));
  if (!g->spheres || !g->capsules || !g->hulls || !g->box_hulls) {
    compound_geom_free(g);
    lub_api_fail(app, "phys3d_compound: out of memory");
    return false;
  }
  uint64_t h = phys_hash_u64(*hash, (uint64_t)count);
  for (int i = 0; i < count; ++i) {
    const P3CompoundChild *c = &desc->children[i];
    b3Transform pose = {vec3_of(c->position), quat_of(c->rotation)};
    b3SurfaceMaterial material = b3DefaultSurfaceMaterial();
    material.friction = c->material.friction;
    material.restitution = c->material.restitution;
    material.userMaterialId = (uint64_t)c->material.material_id;
    switch (c->kind) {
    case P3_COMPOUND_SPHERE: {
      if (c->r <= 0.0f) {
        compound_geom_free(g);
        lub_api_fail(app, "phys3d_compound: sphere r must be > 0");
        return false;
      }
      b3CompoundSphereDef *out = &g->spheres[g->sphere_count++];
      out->sphere.center = b3TransformPoint(pose, vec3_of(c->center));
      out->sphere.radius = c->r;
      out->material = material;
      h = phys_hash_u64(h, 1);
      h = hash_vec3(h, lub_vec3(out->sphere.center));
      h = phys_hash_f32(h, c->r);
      break;
    }
    case P3_COMPOUND_CAPSULE: {
      if (c->r <= 0.0f) {
        compound_geom_free(g);
        lub_api_fail(app, "phys3d_compound: capsule r must be > 0");
        return false;
      }
      if (b3DistanceSquared(vec3_of(c->a), vec3_of(c->b)) <= 1e-12f) {
        compound_geom_free(g);
        lub_api_fail(app,
                     "phys3d_compound: capsule endpoints must be distinct");
        return false;
      }
      b3CompoundCapsuleDef *out = &g->capsules[g->capsule_count++];
      out->capsule.center1 = b3TransformPoint(pose, vec3_of(c->a));
      out->capsule.center2 = b3TransformPoint(pose, vec3_of(c->b));
      out->capsule.radius = c->r;
      out->material = material;
      h = phys_hash_u64(h, 2);
      h = hash_vec3(h, lub_vec3(out->capsule.center1));
      h = hash_vec3(h, lub_vec3(out->capsule.center2));
      h = phys_hash_f32(h, c->r);
      break;
    }
    case P3_COMPOUND_BOX: {
      if (c->hx <= 0.0f || c->hy <= 0.0f || c->hz <= 0.0f) {
        compound_geom_free(g);
        lub_api_fail(app, "phys3d_compound: box hx, hy and hz must be > 0");
        return false;
      }
      int hull_index = g->hull_count++;
      g->box_hulls[hull_index] = b3MakeBoxHull(c->hx, c->hy, c->hz);
      b3CompoundHullDef *out = &g->hulls[hull_index];
      out->hull = &g->box_hulls[hull_index].base;
      out->transform = pose;
      out->material = material;
      h = phys_hash_u64(h, 3);
      h = phys_hash_f32(h, c->hx);
      h = phys_hash_f32(h, c->hy);
      h = phys_hash_f32(h, c->hz);
      h = hash_transform(h, pose);
      break;
    }
    default:
      compound_geom_free(g);
      lub_api_fail(
          app, "phys3d_compound: child %d must have sphere, box, or capsule",
          i + 1);
      return false;
    }
    h = hash_surface_material(h, &material);
  }
  *hash = h;
  return true;
}

static bool create_compound(App *app, Phys3dBody *b, Phys3dShape *shape,
                            const b3ShapeDef *def, const void *geom) {
  const CompoundGeom *g = (const CompoundGeom *)geom;
  b3CompoundDef compound_def = {0};
  compound_def.spheres = g->spheres;
  compound_def.sphereCount = g->sphere_count;
  compound_def.capsules = g->capsules;
  compound_def.capsuleCount = g->capsule_count;
  compound_def.hulls = g->hulls;
  compound_def.hullCount = g->hull_count;
  // b3CreateCompound は入力を全部 clone する。
  b3CompoundData *compound = b3CreateCompound(&compound_def);
  if (!compound) {
    lub_api_fail(app, "phys3d_compound: b3CreateCompound failed");
    return false;
  }
  b3ShapeDef def_copy = *def;
  shape->id = b3CreateCompoundShape(b->id, &def_copy, compound);
  shape->compound_data = compound;
  return true;
}

static LubStatus p3_compound(LubContext *ctx, LubHandle body, LubStr key,
                             const P3CompoundDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys3d_compound: desc required");
  if (!desc->shape.has_version)
    return lub_api_fail(app, "phys3d_compound: explicit version is required");
  Phys3dBody *b = body_from_handle(app, body);
  // Box3D は compound を static で sensor でない body に限る。
  if (!body_is_live(b) || b3Body_GetType(b->id) != b3_staticBody)
    return lub_api_fail(app, "phys3d_compound: body must be static");
  if (desc->shape.sensor)
    return lub_api_fail(app, "phys3d_compound: compound cannot be a sensor");
  CompoundGeom g;
  uint64_t h = phys_hash_init();
  if (!compound_children_build(app, desc, &g, &h))
    return LUB_ERROR;
  ShapeDeclare d = {"phys3d_compound",
                    LUB_PHYS3D_SHAPE_KIND_COMPOUND,
                    body,
                    key,
                    &desc->shape,
                    true,
                    h,
                    &g,
                    create_compound};
  LubStatus st = shape_declare(app, &d, out);
  compound_geom_free(&g);
  return st;
}

static LubHandle p3_shape_find(LubContext *ctx, LubHandle body, LubStr key) {
  App *app = lub_api_app(ctx);
  Phys3dBody *b = body_from_handle(app, body);
  char kbuf[PHYS_KEY_MAX];
  if (!b || !key.ptr || key.len <= 0 || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return 0;
  Phys3dShape *s = shape_get(b, kbuf);
  return s ? s->handle : 0;
}

// ----------------------------------------------------------------- joint

static const char *joint_kind_name(int kind) {
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

static int joint_type_from_b3(b3JointType type) {
  switch (type) {
  case b3_parallelJoint:
    return LUB_PHYS3D_JOINT_TYPE_PARALLEL;
  case b3_distanceJoint:
    return LUB_PHYS3D_JOINT_TYPE_DISTANCE;
  case b3_filterJoint:
    return LUB_PHYS3D_JOINT_TYPE_FILTER;
  case b3_motorJoint:
    return LUB_PHYS3D_JOINT_TYPE_MOTOR;
  case b3_prismaticJoint:
    return LUB_PHYS3D_JOINT_TYPE_PRISMATIC;
  case b3_revoluteJoint:
    return LUB_PHYS3D_JOINT_TYPE_REVOLUTE;
  case b3_sphericalJoint:
    return LUB_PHYS3D_JOINT_TYPE_SPHERICAL;
  case b3_weldJoint:
    return LUB_PHYS3D_JOINT_TYPE_WELD;
  case b3_wheelJoint:
    return LUB_PHYS3D_JOINT_TYPE_WHEEL;
  default:
    return 0;
  }
}

// 軸は joint の正準軸 (slide 軸は frame A の x、hinge / cone 軸は z) を
// 世界座標の axis に回して表す。
static b3Vec3 joint_canonical_axis(int kind) {
  switch (kind) {
  case LUB_PHYS3D_JOINT_TYPE_PRISMATIC:
  case LUB_PHYS3D_JOINT_TYPE_WHEEL:
    return b3Vec3_axisX;
  default:
    return b3Vec3_axisZ;
  }
}

// body の local frame: 世界座標の anchor と axis を body の transform で
// local 化する。明示の frame があればそれが勝つ。
static b3Transform joint_frame_for_body(Phys3dBody *body, bool has_axis,
                                        b3Quat world_rot, bool has_anchor,
                                        LubVec3d anchor, bool has_frame,
                                        LubVec3d frame_position,
                                        LubQuat3d frame_rotation) {
  b3WorldTransform body_transform = b3Body_GetTransform(body->id);
  b3Transform out = {b3Vec3_zero, b3Quat_identity};
  out.q = has_axis ? b3NormalizeQuat(b3InvMulQuat(body_transform.q, world_rot))
                   : b3Quat_identity;
  if (has_anchor)
    out.p = b3Body_GetLocalPoint(body->id, b3ToPos(vec3_of(anchor)));
  if (has_frame) {
    out.p = vec3_of(frame_position);
    out.q = quat_of(frame_rotation);
  }
  return out;
}

typedef struct JointFrames {
  b3Transform a, b;
  b3Vec3 axis;
} JointFrames;

static uint64_t joint_constructor_hash(const P3JointDesc *d,
                                       const Phys3dBody *a, const Phys3dBody *b,
                                       b3Vec3 axis, const JointFrames *frames) {
  uint64_t h = phys_hash_init();
  h = phys_hash_u64(h, (uint64_t)d->type);
  h = phys_hash_cstr(h, a ? a->key : "");
  h = phys_hash_cstr(h, b ? b->key : "");
  h = phys_hash_bool(h, d->collide_connected);
  h = phys_hash_bool(h, d->has_axis);
  if (d->has_axis)
    h = hash_vec3(h, lub_vec3(axis));
  h = phys_hash_bool(h, d->has_anchor_a);
  if (d->has_anchor_a)
    h = hash_vec3(h, d->anchor_a);
  h = phys_hash_bool(h, d->has_anchor_b);
  if (d->has_anchor_b)
    h = hash_vec3(h, d->anchor_b);
  h = phys_hash_bool(h, d->has_frame_a);
  if (d->has_frame_a)
    h = hash_transform(h, frames->a);
  h = phys_hash_bool(h, d->has_frame_b);
  if (d->has_frame_b)
    h = hash_transform(h, frames->b);
  return h;
}

static void log_joint_constructor_drift(Phys3dJoint *j, uint64_t hash) {
  if (j->constructor_hash == hash || j->constructor_warned)
    return;
  SDL_Log("phys3d_joint('%s'): constructor fields changed without version "
          "bump",
          j->key);
  j->constructor_warned = true;
}

static void joint_mark_declared(Phys3dJoint *j, const P3JointDesc *d,
                                Phys3dBody *a, Phys3dBody *b,
                                uint64_t constructor_hash, int64_t version,
                                bool recreated) {
  if (recreated) {
    j->kind = d->type;
    j->body_a = a;
    j->body_b = b;
    j->constructor_warned = false;
  }
  j->version = version;
  j->constructor_hash = constructor_hash;
}

static void joint_apply_runtime(Phys3dJoint *j, const P3JointDesc *d) {
  switch (d->type) {
  case LUB_PHYS3D_JOINT_TYPE_DISTANCE:
    b3DistanceJoint_SetLength(j->id, d->length);
    b3DistanceJoint_EnableSpring(j->id, d->enable_spring);
    b3DistanceJoint_SetSpringHertz(j->id, d->hertz);
    b3DistanceJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    b3DistanceJoint_SetSpringForceRange(j->id, d->lower_spring_force,
                                        d->upper_spring_force);
    b3DistanceJoint_EnableLimit(j->id, d->enable_limit);
    b3DistanceJoint_SetLengthRange(j->id, d->min_length, d->max_length);
    b3DistanceJoint_EnableMotor(j->id, d->enable_motor);
    b3DistanceJoint_SetMotorSpeed(j->id, d->motor_speed);
    b3DistanceJoint_SetMaxMotorForce(j->id, d->max_force);
    break;
  case LUB_PHYS3D_JOINT_TYPE_MOTOR:
    b3MotorJoint_SetLinearVelocity(j->id, vec3_of(d->linear_velocity));
    b3MotorJoint_SetAngularVelocity(j->id, vec3_of(d->angular_velocity));
    b3MotorJoint_SetMaxVelocityForce(j->id, d->max_velocity_force);
    b3MotorJoint_SetMaxVelocityTorque(j->id, d->max_velocity_torque);
    b3MotorJoint_SetLinearHertz(j->id, d->linear_hertz);
    b3MotorJoint_SetLinearDampingRatio(j->id, d->linear_damping_ratio);
    b3MotorJoint_SetAngularHertz(j->id, d->angular_hertz);
    b3MotorJoint_SetAngularDampingRatio(j->id, d->angular_damping_ratio);
    b3MotorJoint_SetMaxSpringForce(j->id, d->max_spring_force);
    b3MotorJoint_SetMaxSpringTorque(j->id, d->max_spring_torque);
    break;
  case LUB_PHYS3D_JOINT_TYPE_PARALLEL:
    b3ParallelJoint_SetSpringHertz(j->id, d->hertz);
    b3ParallelJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    b3ParallelJoint_SetMaxTorque(j->id, d->max_torque);
    break;
  case LUB_PHYS3D_JOINT_TYPE_PRISMATIC:
    b3PrismaticJoint_EnableSpring(j->id, d->enable_spring);
    b3PrismaticJoint_SetSpringHertz(j->id, d->hertz);
    b3PrismaticJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    b3PrismaticJoint_SetTargetTranslation(j->id, d->target_translation);
    b3PrismaticJoint_EnableLimit(j->id, d->enable_limit);
    b3PrismaticJoint_SetLimits(j->id, d->lower, d->upper);
    b3PrismaticJoint_EnableMotor(j->id, d->enable_motor);
    b3PrismaticJoint_SetMotorSpeed(j->id, d->motor_speed);
    b3PrismaticJoint_SetMaxMotorForce(j->id, d->max_force);
    break;
  case LUB_PHYS3D_JOINT_TYPE_REVOLUTE:
    b3RevoluteJoint_EnableSpring(j->id, d->enable_spring);
    b3RevoluteJoint_SetSpringHertz(j->id, d->hertz);
    b3RevoluteJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    b3RevoluteJoint_SetTargetAngle(j->id, d->target_angle);
    b3RevoluteJoint_EnableLimit(j->id, d->enable_limit);
    b3RevoluteJoint_SetLimits(j->id, d->lower, d->upper);
    b3RevoluteJoint_EnableMotor(j->id, d->enable_motor);
    b3RevoluteJoint_SetMotorSpeed(j->id, d->motor_speed);
    b3RevoluteJoint_SetMaxMotorTorque(j->id, d->max_torque);
    break;
  case LUB_PHYS3D_JOINT_TYPE_SPHERICAL:
    b3SphericalJoint_EnableSpring(j->id, d->enable_spring);
    b3SphericalJoint_SetSpringHertz(j->id, d->hertz);
    b3SphericalJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    b3SphericalJoint_SetTargetRotation(j->id, quat_of(d->target_rotation));
    b3SphericalJoint_EnableConeLimit(j->id, d->enable_cone_limit);
    b3SphericalJoint_SetConeLimit(j->id, d->cone_angle);
    b3SphericalJoint_EnableTwistLimit(j->id, d->enable_twist_limit);
    b3SphericalJoint_SetTwistLimits(j->id, d->lower_twist_angle,
                                    d->upper_twist_angle);
    b3SphericalJoint_EnableMotor(j->id, d->enable_motor);
    b3SphericalJoint_SetMotorVelocity(j->id, vec3_of(d->motor_velocity));
    b3SphericalJoint_SetMaxMotorTorque(j->id, d->max_torque);
    break;
  case LUB_PHYS3D_JOINT_TYPE_WELD:
    b3WeldJoint_SetLinearHertz(j->id, d->linear_hertz);
    b3WeldJoint_SetLinearDampingRatio(j->id, d->linear_damping_ratio);
    b3WeldJoint_SetAngularHertz(j->id, d->angular_hertz);
    b3WeldJoint_SetAngularDampingRatio(j->id, d->angular_damping_ratio);
    break;
  case LUB_PHYS3D_JOINT_TYPE_WHEEL:
    b3WheelJoint_EnableSuspension(j->id, d->enable_spring);
    b3WheelJoint_SetSuspensionHertz(j->id, d->hertz);
    b3WheelJoint_SetSuspensionDampingRatio(j->id, d->damping_ratio);
    b3WheelJoint_EnableSuspensionLimit(j->id, d->enable_limit);
    b3WheelJoint_SetSuspensionLimits(j->id, d->lower, d->upper);
    b3WheelJoint_EnableSpinMotor(j->id, d->enable_motor);
    b3WheelJoint_SetSpinMotorSpeed(j->id, d->motor_speed);
    b3WheelJoint_SetMaxSpinTorque(j->id, d->max_torque);
    b3WheelJoint_EnableSteering(j->id, d->enable_steering);
    b3WheelJoint_SetSteeringHertz(j->id, d->steering_hertz);
    b3WheelJoint_SetSteeringDampingRatio(j->id, d->steering_damping_ratio);
    b3WheelJoint_SetTargetSteeringAngle(j->id, d->target_steering_angle);
    b3WheelJoint_SetMaxSteeringTorque(j->id, d->max_steering_torque);
    b3WheelJoint_EnableSteeringLimit(j->id, d->enable_steering_limit);
    b3WheelJoint_SetSteeringLimits(j->id, d->lower_steering_limit,
                                   d->upper_steering_limit);
    break;
  case LUB_PHYS3D_JOINT_TYPE_FILTER:
  default:
    break;
  }
  b3Joint_SetForceThreshold(j->id, d->force_threshold);
  b3Joint_SetTorqueThreshold(j->id, d->torque_threshold);
  if (d->has_constraint_tuning)
    b3Joint_SetConstraintTuning(j->id, d->constraint_hertz,
                                d->constraint_damping_ratio);
}

static void joint_def_apply_base(b3JointDef *base, Phys3dJoint *j,
                                 const P3JointDesc *d, Phys3dBody *a,
                                 Phys3dBody *b, const JointFrames *frames) {
  base->userData = j;
  base->bodyIdA = a->id;
  base->bodyIdB = b->id;
  base->localFrameA = frames->a;
  base->localFrameB = frames->b;
  base->forceThreshold = d->force_threshold;
  base->torqueThreshold = d->torque_threshold;
  base->collideConnected = d->collide_connected;
  if (d->has_constraint_tuning) {
    base->constraintHertz = d->constraint_hertz;
    base->constraintDampingRatio = d->constraint_damping_ratio;
  }
}

static bool joint_create(App *app, Phys3dWorld *w, Phys3dJoint *j,
                         const P3JointDesc *d, Phys3dBody *a, Phys3dBody *b,
                         const JointFrames *frames, uint64_t constructor_hash,
                         int64_t version) {
  switch (d->type) {
  case LUB_PHYS3D_JOINT_TYPE_DISTANCE: {
    b3DistanceJointDef def = b3DefaultDistanceJointDef();
    joint_def_apply_base(&def.base, j, d, a, b, frames);
    def.length = d->length;
    j->id = b3CreateDistanceJoint(w->id, &def);
    break;
  }
  case LUB_PHYS3D_JOINT_TYPE_FILTER: {
    b3FilterJointDef def = b3DefaultFilterJointDef();
    joint_def_apply_base(&def.base, j, d, a, b, frames);
    j->id = b3CreateFilterJoint(w->id, &def);
    break;
  }
  case LUB_PHYS3D_JOINT_TYPE_MOTOR: {
    b3MotorJointDef def = b3DefaultMotorJointDef();
    joint_def_apply_base(&def.base, j, d, a, b, frames);
    j->id = b3CreateMotorJoint(w->id, &def);
    break;
  }
  case LUB_PHYS3D_JOINT_TYPE_PARALLEL: {
    b3ParallelJointDef def = b3DefaultParallelJointDef();
    joint_def_apply_base(&def.base, j, d, a, b, frames);
    j->id = b3CreateParallelJoint(w->id, &def);
    break;
  }
  case LUB_PHYS3D_JOINT_TYPE_PRISMATIC: {
    b3PrismaticJointDef def = b3DefaultPrismaticJointDef();
    joint_def_apply_base(&def.base, j, d, a, b, frames);
    j->id = b3CreatePrismaticJoint(w->id, &def);
    break;
  }
  case LUB_PHYS3D_JOINT_TYPE_REVOLUTE: {
    b3RevoluteJointDef def = b3DefaultRevoluteJointDef();
    joint_def_apply_base(&def.base, j, d, a, b, frames);
    j->id = b3CreateRevoluteJoint(w->id, &def);
    break;
  }
  case LUB_PHYS3D_JOINT_TYPE_SPHERICAL: {
    b3SphericalJointDef def = b3DefaultSphericalJointDef();
    joint_def_apply_base(&def.base, j, d, a, b, frames);
    j->id = b3CreateSphericalJoint(w->id, &def);
    break;
  }
  case LUB_PHYS3D_JOINT_TYPE_WELD: {
    b3WeldJointDef def = b3DefaultWeldJointDef();
    joint_def_apply_base(&def.base, j, d, a, b, frames);
    j->id = b3CreateWeldJoint(w->id, &def);
    break;
  }
  case LUB_PHYS3D_JOINT_TYPE_WHEEL: {
    b3WheelJointDef def = b3DefaultWheelJointDef();
    joint_def_apply_base(&def.base, j, d, a, b, frames);
    j->id = b3CreateWheelJoint(w->id, &def);
    break;
  }
  default:
    break;
  }
  if (B3_IS_NULL(j->id) || !b3Joint_IsValid(j->id)) {
    lub_api_fail(app, "phys3d_joint: b3Create%sJoint failed",
                 joint_kind_name(d->type));
    return false;
  }
  b3Joint_SetUserData(j->id, j);
  joint_mark_declared(j, d, a, b, constructor_hash, version, true);
  joint_apply_runtime(j, d);
  return true;
}

static Phys3dBody *joint_body(App *app, Phys3dWorld *w, LubHandle h,
                              const char *field) {
  Phys3dBody *b = body_from_handle(app, h);
  if (!b) {
    lub_api_fail(app, "phys3d_joint: missing body field '%s'", field);
    return NULL;
  }
  if (b->world != w) {
    lub_api_fail(app, "phys3d_joint: body '%s' belongs to another world",
                 b->key);
    return NULL;
  }
  if (!body_is_live(b) || b->seen_generation != w->generation) {
    lub_api_fail(app, "phys3d_joint: declare live body '%s' before joint",
                 b->key);
    return NULL;
  }
  return b;
}

static LubStatus p3_joint(LubContext *ctx, LubHandle world, LubStr key,
                          const P3JointDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (in_callback(app, "phys3d_joint"))
    return LUB_ERROR;
  Phys3dWorld *w = check_world(app, world, "phys3d_joint");
  if (!w)
    return LUB_ERROR;
  char kbuf[PHYS_KEY_MAX];
  if (!key_copy(app, key, kbuf, sizeof(kbuf), "phys3d_joint"))
    return LUB_ERROR;
  if (!desc)
    return lub_api_fail(app, "phys3d_joint: desc required");
  if (desc->type < LUB_PHYS3D_JOINT_TYPE_DISTANCE ||
      desc->type > LUB_PHYS3D_JOINT_TYPE_WHEEL)
    return lub_api_fail(app, "phys3d_joint: unknown joint type %d",
                        (int)desc->type);
  Phys3dBody *a = joint_body(app, w, desc->body_a, "a");
  if (!a)
    return LUB_ERROR;
  Phys3dBody *b = joint_body(app, w, desc->body_b, "b");
  if (!b)
    return LUB_ERROR;
  b3Vec3 axis = b3Vec3_axisZ;
  b3Quat world_rot = b3Quat_identity;
  if (desc->has_axis) {
    axis = vec3_of(desc->axis);
    if (b3LengthSquared(axis) <= 1e-12f)
      return lub_api_fail(app, "phys3d_joint: axis must be non-zero");
    axis = b3Normalize(axis);
    world_rot =
        b3ComputeQuatBetweenUnitVectors(joint_canonical_axis(desc->type), axis);
  }
  JointFrames frames;
  frames.axis = axis;
  frames.a = joint_frame_for_body(
      a, desc->has_axis, world_rot, desc->has_anchor_a, desc->anchor_a,
      desc->has_frame_a, desc->frame_a_position, desc->frame_a_rotation);
  frames.b = joint_frame_for_body(
      b, desc->has_axis, world_rot, desc->has_anchor_b, desc->anchor_b,
      desc->has_frame_b, desc->frame_b_position, desc->frame_b_rotation);
  if (!w->begun)
    return lub_api_fail(app, "phys3d_joint: call phys3d_begin(world) first");
  Phys3dJoint *j = joint_get_or_create(w, kbuf);
  if (!j)
    return lub_api_fail(app, "phys3d_joint: out of memory");
  uint64_t constructor_hash = joint_constructor_hash(desc, a, b, axis, &frames);
  int64_t version =
      desc->has_version ? (int64_t)desc->version : (int64_t)constructor_hash;
  bool endpoints_changed = j->body_a != a || j->body_b != b;
  bool kind_changed = j->kind != desc->type;
  bool recreated = !joint_is_live(j) || j->version != version ||
                   (!desc->has_version && (kind_changed || endpoints_changed));
  if (recreated) {
    if (joint_is_live(j))
      b3DestroyJoint(j->id, true);
    j->id = b3_nullJointId;
    if (!joint_create(app, w, j, desc, a, b, &frames, constructor_hash,
                      version))
      return LUB_ERROR;
  } else {
    if (desc->has_version)
      log_joint_constructor_drift(j, constructor_hash);
    joint_mark_declared(j, desc, a, b, constructor_hash, version, false);
    if (!kind_changed)
      joint_apply_runtime(j, desc);
  }
  j->seen_generation = w->generation;
  *out = j->handle;
  return LUB_OK;
}

static LubHandle p3_joint_find(LubContext *ctx, LubHandle world, LubStr key) {
  App *app = lub_api_app(ctx);
  Phys3dWorld *w = world_from_handle(app, world);
  char kbuf[PHYS_KEY_MAX];
  if (!w || !key.ptr || key.len <= 0 || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return 0;
  Phys3dJoint *j = joint_get(w, kbuf);
  return j ? j->handle : 0;
}

static void fill_joint_view(P3JointView *out, Phys3dJoint *j) {
  memset(out, 0, sizeof(*out));
  out->joint = j ? j->handle : 0;
  out->key = str_or_empty(j ? j->key : NULL);
  out->type = j ? j->kind : 0;
  out->a = str_or_empty(j && j->body_a ? j->body_a->key : NULL);
  out->b = str_or_empty(j && j->body_b ? j->body_b->key : NULL);
  out->valid = joint_is_live(j);
}

static P3Frame frame_of(b3Transform t) {
  P3Frame f = {lub_vec3(t.p), lub_quat(t.q)};
  return f;
}

static LubStatus p3_joint_info(LubContext *ctx, LubHandle joint,
                               P3JointInfo *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  fill_joint_view(&out->view, j);
  out->collide_connected = b3Joint_GetCollideConnected(j->id);
  out->force = lub_vec3(b3Joint_GetConstraintForce(j->id));
  out->torque = lub_vec3(b3Joint_GetConstraintTorque(j->id));
  out->linear_separation = b3Joint_GetLinearSeparation(j->id);
  out->angular_separation = b3Joint_GetAngularSeparation(j->id);
  out->local_frame_a = frame_of(b3Joint_GetLocalFrameA(j->id));
  out->local_frame_b = frame_of(b3Joint_GetLocalFrameB(j->id));
  return LUB_OK;
}

static LubStatus p3_joint_force(LubContext *ctx, LubHandle joint,
                                LubVec3d *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  *out = lub_vec3(b3Joint_GetConstraintForce(j->id));
  return LUB_OK;
}

static LubStatus p3_joint_torque(LubContext *ctx, LubHandle joint,
                                 LubVec3d *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  *out = lub_vec3(b3Joint_GetConstraintTorque(j->id));
  return LUB_OK;
}

static LubStatus p3_joint_angle(LubContext *ctx, LubHandle joint, float *out,
                                bool *has) {
  App *app = lub_api_app(ctx);
  *out = 0;
  *has = false;
  Phys3dJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  if (j->kind == LUB_PHYS3D_JOINT_TYPE_REVOLUTE) {
    *out = b3RevoluteJoint_GetAngle(j->id);
    *has = true;
  }
  return LUB_OK;
}

static LubStatus p3_joint_translation(LubContext *ctx, LubHandle joint,
                                      float *out, bool *has) {
  App *app = lub_api_app(ctx);
  *out = 0;
  *has = false;
  Phys3dJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  if (j->kind == LUB_PHYS3D_JOINT_TYPE_PRISMATIC) {
    *out = b3PrismaticJoint_GetTranslation(j->id);
    *has = true;
  }
  return LUB_OK;
}

static LubStatus p3_joint_speed(LubContext *ctx, LubHandle joint, float *out,
                                bool *has) {
  App *app = lub_api_app(ctx);
  *out = 0;
  *has = false;
  Phys3dJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  if (j->kind == LUB_PHYS3D_JOINT_TYPE_PRISMATIC) {
    *out = b3PrismaticJoint_GetSpeed(j->id);
    *has = true;
  } else if (j->kind == LUB_PHYS3D_JOINT_TYPE_WHEEL) {
    *out = b3WheelJoint_GetSpinSpeed(j->id);
    *has = true;
  }
  return LUB_OK;
}

static LubStatus p3_joint_length(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has) {
  App *app = lub_api_app(ctx);
  *out = 0;
  *has = false;
  Phys3dJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  if (j->kind == LUB_PHYS3D_JOINT_TYPE_DISTANCE) {
    *out = b3DistanceJoint_GetCurrentLength(j->id);
    *has = true;
  }
  return LUB_OK;
}

static LubStatus p3_joint_motor_force(LubContext *ctx, LubHandle joint,
                                      float *out, bool *has) {
  App *app = lub_api_app(ctx);
  *out = 0;
  *has = false;
  Phys3dJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  if (j->kind == LUB_PHYS3D_JOINT_TYPE_DISTANCE) {
    *out = b3DistanceJoint_GetMotorForce(j->id);
    *has = true;
  } else if (j->kind == LUB_PHYS3D_JOINT_TYPE_PRISMATIC) {
    *out = b3PrismaticJoint_GetMotorForce(j->id);
    *has = true;
  }
  return LUB_OK;
}

static LubStatus p3_joint_motor_torque(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has, LubVec3d *vector,
                                       bool *has_vector) {
  App *app = lub_api_app(ctx);
  *out = 0;
  *has = false;
  memset(vector, 0, sizeof(*vector));
  *has_vector = false;
  Phys3dJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  if (j->kind == LUB_PHYS3D_JOINT_TYPE_REVOLUTE) {
    *out = b3RevoluteJoint_GetMotorTorque(j->id);
    *has = true;
  } else if (j->kind == LUB_PHYS3D_JOINT_TYPE_WHEEL) {
    *out = b3WheelJoint_GetSpinTorque(j->id);
    *has = true;
  } else if (j->kind == LUB_PHYS3D_JOINT_TYPE_SPHERICAL) {
    *vector = lub_vec3(b3SphericalJoint_GetMotorTorque(j->id));
    *has_vector = true;
  }
  return LUB_OK;
}

static Phys3dJoint *check_live_joint(App *app, LubHandle h, const char *fn) {
  if (in_callback(app, fn))
    return NULL;
  Phys3dJoint *j = check_joint(app, h, fn);
  if (!j)
    return NULL;
  if (!joint_is_live(j)) {
    lub_api_fail(app, "%s: joint is not live", fn);
    return NULL;
  }
  return j;
}

static LubStatus p3_joint_set_motor(LubContext *ctx, LubHandle joint,
                                    const P3JointMotor *d) {
  App *app = lub_api_app(ctx);
  Phys3dJoint *j = check_live_joint(app, joint, "phys3d_joint_set_motor");
  if (!j)
    return LUB_ERROR;
  switch (j->kind) {
  case LUB_PHYS3D_JOINT_TYPE_DISTANCE:
    b3DistanceJoint_EnableMotor(j->id, d->enabled);
    b3DistanceJoint_SetMotorSpeed(j->id, d->speed);
    b3DistanceJoint_SetMaxMotorForce(j->id, d->max_force);
    break;
  case LUB_PHYS3D_JOINT_TYPE_PRISMATIC:
    b3PrismaticJoint_EnableMotor(j->id, d->enabled);
    b3PrismaticJoint_SetMotorSpeed(j->id, d->speed);
    b3PrismaticJoint_SetMaxMotorForce(j->id, d->max_force);
    break;
  case LUB_PHYS3D_JOINT_TYPE_REVOLUTE:
    b3RevoluteJoint_EnableMotor(j->id, d->enabled);
    b3RevoluteJoint_SetMotorSpeed(j->id, d->speed);
    b3RevoluteJoint_SetMaxMotorTorque(j->id, d->max_torque);
    break;
  case LUB_PHYS3D_JOINT_TYPE_SPHERICAL:
    b3SphericalJoint_EnableMotor(j->id, d->enabled);
    b3SphericalJoint_SetMotorVelocity(
        j->id, d->has_velocity ? vec3_of(d->velocity)
                               : b3SphericalJoint_GetMotorVelocity(j->id));
    b3SphericalJoint_SetMaxMotorTorque(j->id, d->max_torque);
    break;
  case LUB_PHYS3D_JOINT_TYPE_WHEEL:
    b3WheelJoint_EnableSpinMotor(j->id, d->enabled);
    b3WheelJoint_SetSpinMotorSpeed(j->id, d->speed);
    b3WheelJoint_SetMaxSpinTorque(j->id, d->max_torque);
    break;
  case LUB_PHYS3D_JOINT_TYPE_MOTOR:
    b3MotorJoint_SetLinearVelocity(
        j->id, d->has_linear_velocity ? vec3_of(d->linear_velocity)
                                      : b3MotorJoint_GetLinearVelocity(j->id));
    b3MotorJoint_SetAngularVelocity(
        j->id, d->has_angular_velocity
                   ? vec3_of(d->angular_velocity)
                   : b3MotorJoint_GetAngularVelocity(j->id));
    b3MotorJoint_SetMaxVelocityForce(
        j->id, d->has_max_velocity_force
                   ? d->max_velocity_force
                   : b3MotorJoint_GetMaxVelocityForce(j->id));
    b3MotorJoint_SetMaxVelocityTorque(
        j->id, d->has_max_velocity_torque
                   ? d->max_velocity_torque
                   : b3MotorJoint_GetMaxVelocityTorque(j->id));
    break;
  default:
    return lub_api_fail(app, "phys3d_joint_set_motor: unsupported joint type");
  }
  b3Joint_WakeBodies(j->id);
  return LUB_OK;
}

static LubStatus p3_joint_set_limit(LubContext *ctx, LubHandle joint,
                                    const P3JointLimit *d) {
  App *app = lub_api_app(ctx);
  Phys3dJoint *j = check_live_joint(app, joint, "phys3d_joint_set_limit");
  if (!j)
    return LUB_ERROR;
  switch (j->kind) {
  case LUB_PHYS3D_JOINT_TYPE_DISTANCE:
    b3DistanceJoint_EnableLimit(j->id, d->enabled);
    b3DistanceJoint_SetLengthRange(j->id, d->min_length, d->max_length);
    break;
  case LUB_PHYS3D_JOINT_TYPE_PRISMATIC:
    b3PrismaticJoint_EnableLimit(j->id, d->enabled);
    b3PrismaticJoint_SetLimits(j->id, d->lower, d->upper);
    break;
  case LUB_PHYS3D_JOINT_TYPE_REVOLUTE:
    b3RevoluteJoint_EnableLimit(j->id, d->enabled);
    b3RevoluteJoint_SetLimits(j->id, d->lower, d->upper);
    break;
  case LUB_PHYS3D_JOINT_TYPE_SPHERICAL:
    if (d->has_cone_angle) {
      b3SphericalJoint_EnableConeLimit(j->id, d->enabled);
      b3SphericalJoint_SetConeLimit(j->id, d->cone_angle);
    }
    if (d->has_twist) {
      b3SphericalJoint_EnableTwistLimit(j->id, d->enabled);
      b3SphericalJoint_SetTwistLimits(j->id, d->lower, d->upper);
    }
    break;
  case LUB_PHYS3D_JOINT_TYPE_WHEEL:
    b3WheelJoint_EnableSuspensionLimit(j->id, d->enabled);
    b3WheelJoint_SetSuspensionLimits(j->id, d->lower, d->upper);
    break;
  default:
    return lub_api_fail(app, "phys3d_joint_set_limit: unsupported joint type");
  }
  b3Joint_WakeBodies(j->id);
  return LUB_OK;
}

static LubStatus p3_joint_set_spring(LubContext *ctx, LubHandle joint,
                                     const P3JointSpring *d) {
  App *app = lub_api_app(ctx);
  Phys3dJoint *j = check_live_joint(app, joint, "phys3d_joint_set_spring");
  if (!j)
    return LUB_ERROR;
  switch (j->kind) {
  case LUB_PHYS3D_JOINT_TYPE_DISTANCE:
    b3DistanceJoint_EnableSpring(j->id, d->enabled);
    b3DistanceJoint_SetSpringHertz(j->id, d->hertz);
    b3DistanceJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    break;
  case LUB_PHYS3D_JOINT_TYPE_PRISMATIC:
    b3PrismaticJoint_EnableSpring(j->id, d->enabled);
    b3PrismaticJoint_SetSpringHertz(j->id, d->hertz);
    b3PrismaticJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    break;
  case LUB_PHYS3D_JOINT_TYPE_REVOLUTE:
    b3RevoluteJoint_EnableSpring(j->id, d->enabled);
    b3RevoluteJoint_SetSpringHertz(j->id, d->hertz);
    b3RevoluteJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    break;
  case LUB_PHYS3D_JOINT_TYPE_SPHERICAL:
    b3SphericalJoint_EnableSpring(j->id, d->enabled);
    b3SphericalJoint_SetSpringHertz(j->id, d->hertz);
    b3SphericalJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    break;
  case LUB_PHYS3D_JOINT_TYPE_PARALLEL:
    b3ParallelJoint_SetSpringHertz(j->id, d->hertz);
    b3ParallelJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    b3ParallelJoint_SetMaxTorque(
        j->id, d->has_max_torque ? d->max_torque
                                 : b3ParallelJoint_GetMaxTorque(j->id));
    break;
  case LUB_PHYS3D_JOINT_TYPE_WHEEL:
    b3WheelJoint_EnableSuspension(j->id, d->enabled);
    b3WheelJoint_SetSuspensionHertz(j->id, d->hertz);
    b3WheelJoint_SetSuspensionDampingRatio(j->id, d->damping_ratio);
    break;
  case LUB_PHYS3D_JOINT_TYPE_WELD:
    b3WeldJoint_SetLinearHertz(j->id, d->linear_hertz);
    b3WeldJoint_SetLinearDampingRatio(j->id, d->linear_damping_ratio);
    b3WeldJoint_SetAngularHertz(j->id, d->angular_hertz);
    b3WeldJoint_SetAngularDampingRatio(j->id, d->angular_damping_ratio);
    break;
  case LUB_PHYS3D_JOINT_TYPE_MOTOR:
    b3MotorJoint_SetLinearHertz(j->id, d->linear_hertz);
    b3MotorJoint_SetLinearDampingRatio(j->id, d->linear_damping_ratio);
    b3MotorJoint_SetAngularHertz(j->id, d->angular_hertz);
    b3MotorJoint_SetAngularDampingRatio(j->id, d->angular_damping_ratio);
    break;
  default:
    return lub_api_fail(app, "phys3d_joint_set_spring: unsupported joint type");
  }
  b3Joint_WakeBodies(j->id);
  return LUB_OK;
}

static LubStatus p3_joint_set_target(LubContext *ctx, LubHandle joint,
                                     const P3JointTarget *d) {
  App *app = lub_api_app(ctx);
  Phys3dJoint *j = check_live_joint(app, joint, "phys3d_joint_set_target");
  if (!j)
    return LUB_ERROR;
  switch (j->kind) {
  case LUB_PHYS3D_JOINT_TYPE_PRISMATIC:
    b3PrismaticJoint_SetTargetTranslation(
        j->id, d->has_translation
                   ? d->translation
                   : b3PrismaticJoint_GetTargetTranslation(j->id));
    break;
  case LUB_PHYS3D_JOINT_TYPE_REVOLUTE:
    b3RevoluteJoint_SetTargetAngle(
        j->id, d->has_angle ? d->angle : b3RevoluteJoint_GetTargetAngle(j->id));
    break;
  case LUB_PHYS3D_JOINT_TYPE_SPHERICAL: {
    b3Quat target = d->has_rotation ? quat_of(d->rotation)
                                    : b3SphericalJoint_GetTargetRotation(j->id);
    b3SphericalJoint_SetTargetRotation(j->id, b3NormalizeQuat(target));
    break;
  }
  case LUB_PHYS3D_JOINT_TYPE_WHEEL:
    b3WheelJoint_SetTargetSteeringAngle(
        j->id,
        d->has_angle ? d->angle : b3WheelJoint_GetTargetSteeringAngle(j->id));
    break;
  case LUB_PHYS3D_JOINT_TYPE_MOTOR:
    b3MotorJoint_SetLinearVelocity(
        j->id, d->has_linear_velocity ? vec3_of(d->linear_velocity)
                                      : b3MotorJoint_GetLinearVelocity(j->id));
    b3MotorJoint_SetAngularVelocity(
        j->id, d->has_angular_velocity
                   ? vec3_of(d->angular_velocity)
                   : b3MotorJoint_GetAngularVelocity(j->id));
    break;
  default:
    return lub_api_fail(app, "phys3d_joint_set_target: unsupported joint type");
  }
  b3Joint_WakeBodies(j->id);
  return LUB_OK;
}

// -------------------------------------------------------------- commands

static Phys3dBody *check_live_body(App *app, LubHandle h, const char *fn) {
  if (in_callback(app, fn))
    return NULL;
  Phys3dBody *b = check_body(app, h, fn);
  if (!b)
    return NULL;
  if (!body_is_live(b)) {
    lub_api_fail(app, "%s: body is not live", fn);
    return NULL;
  }
  return b;
}

static Phys3dCommand *push_command(App *app, Phys3dBody *b,
                                   Phys3dCommandKind kind, const char *fn) {
  Phys3dCommand *cmd = command_queue_push(b->world, b, kind);
  if (!cmd)
    lub_api_fail(app, "%s: out of memory", fn);
  return cmd;
}

static LubStatus vector_command(LubContext *ctx, LubHandle body, LubVec3d v,
                                const LubVec3d *point, bool wake,
                                Phys3dCommandKind kind, const char *fn) {
  App *app = lub_api_app(ctx);
  Phys3dBody *b = check_live_body(app, body, fn);
  if (!b)
    return LUB_ERROR;
  Phys3dCommand *cmd = push_command(app, b, kind, fn);
  if (!cmd)
    return LUB_ERROR;
  cmd->vector = vec3_of(v);
  if (point) {
    cmd->point = vec3_of(*point);
    cmd->has_point = true;
  }
  cmd->wake = wake;
  return LUB_OK;
}

static LubStatus p3_add_force(LubContext *ctx, LubHandle body, LubVec3d force,
                              const LubVec3d *point, bool wake) {
  return vector_command(ctx, body, force, point, wake, PHYS3D_COMMAND_ADD_FORCE,
                        "phys3d_add_force");
}

static LubStatus p3_add_force_center(LubContext *ctx, LubHandle body,
                                     LubVec3d force, bool wake) {
  return vector_command(ctx, body, force, NULL, wake,
                        PHYS3D_COMMAND_ADD_FORCE_CENTER,
                        "phys3d_add_force_center");
}

static LubStatus p3_add_impulse(LubContext *ctx, LubHandle body,
                                LubVec3d impulse, const LubVec3d *point,
                                bool wake) {
  return vector_command(ctx, body, impulse, point, wake,
                        PHYS3D_COMMAND_ADD_IMPULSE, "phys3d_add_impulse");
}

static LubStatus p3_add_impulse_center(LubContext *ctx, LubHandle body,
                                       LubVec3d impulse, bool wake) {
  return vector_command(ctx, body, impulse, NULL, wake,
                        PHYS3D_COMMAND_ADD_IMPULSE_CENTER,
                        "phys3d_add_impulse_center");
}

static LubStatus p3_add_torque(LubContext *ctx, LubHandle body, LubVec3d torque,
                               bool wake) {
  return vector_command(ctx, body, torque, NULL, wake,
                        PHYS3D_COMMAND_ADD_TORQUE, "phys3d_add_torque");
}

static LubStatus p3_add_angular_impulse(LubContext *ctx, LubHandle body,
                                        LubVec3d impulse, bool wake) {
  return vector_command(ctx, body, impulse, NULL, wake,
                        PHYS3D_COMMAND_ADD_ANGULAR_IMPULSE,
                        "phys3d_add_angular_impulse");
}

static LubStatus p3_set_velocity(LubContext *ctx, LubHandle body,
                                 const P3SetVelocity *d) {
  App *app = lub_api_app(ctx);
  Phys3dBody *b = check_live_body(app, body, "phys3d_set_velocity");
  if (!b)
    return LUB_ERROR;
  Phys3dCommand *cmd =
      push_command(app, b, PHYS3D_COMMAND_SET_VELOCITY, "phys3d_set_velocity");
  if (!cmd)
    return LUB_ERROR;
  cmd->vector = vec3_of(d->linear);
  cmd->angular = vec3_of(d->angular);
  cmd->has_x = d->has_vx;
  cmd->has_y = d->has_vy;
  cmd->has_z = d->has_vz;
  cmd->has_wx = d->has_wx;
  cmd->has_wy = d->has_wy;
  cmd->has_wz = d->has_wz;
  cmd->wake = d->wake;
  return LUB_OK;
}

static LubStatus p3_teleport(LubContext *ctx, LubHandle body,
                             const P3Teleport *d) {
  App *app = lub_api_app(ctx);
  Phys3dBody *b = check_live_body(app, body, "phys3d_teleport");
  if (!b)
    return LUB_ERROR;
  Phys3dCommand *cmd =
      push_command(app, b, PHYS3D_COMMAND_TELEPORT, "phys3d_teleport");
  if (!cmd)
    return LUB_ERROR;
  cmd->transform.p = vec3_of(d->position);
  cmd->has_x = d->has_x;
  cmd->has_y = d->has_y;
  cmd->has_z = d->has_z;
  cmd->has_rotation = d->has_rotation;
  if (d->has_rotation)
    cmd->transform.q = quat_of(d->rotation);
  cmd->wake = d->wake;
  return LUB_OK;
}

static LubStatus p3_set_target(LubContext *ctx, LubHandle body,
                               const P3SetTarget *d) {
  App *app = lub_api_app(ctx);
  Phys3dBody *b = check_live_body(app, body, "phys3d_set_target");
  if (!b)
    return LUB_ERROR;
  Phys3dCommand *cmd =
      push_command(app, b, PHYS3D_COMMAND_SET_TARGET, "phys3d_set_target");
  if (!cmd)
    return LUB_ERROR;
  cmd->transform.p = vec3_of(d->position);
  cmd->has_x = d->has_x;
  cmd->has_y = d->has_y;
  cmd->has_z = d->has_z;
  cmd->has_rotation = d->has_rotation;
  if (d->has_rotation)
    cmd->transform.q = quat_of(d->rotation);
  cmd->time_step = d->time_step > 0.0f ? d->time_step : b->world->fixed_dt;
  cmd->wake = d->wake;
  return LUB_OK;
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
}

static int apply_body_commands(Phys3dWorld *w) {
  int count = 0;
  for (int i = 0; i < w->commands.count; ++i)
    count += apply_body_command(w, &w->commands.items[i]) ? 1 : 0;
  command_queue_clear(&w->commands);
  return count;
}

// ----------------------------------------------------------------- prune

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

// ---------------------------------------------------------- event capture

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
    // manifold の anchor は body の重心相対 (world 向き) なので body A の
    // 重心から world の点を作る。
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
    // Box3D の begin event は manifold を持たないので contact id から引く。
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
    fill_shape_snapshot(w, dst, true, src->sensorShapeId);
    fill_shape_snapshot(w, dst, false, src->visitorShapeId);
  }
  for (int i = 0; i < ev.endCount; ++i) {
    b3SensorEndTouchEvent *src = &ev.endEvents[i];
    Phys3dContactSnapshot *dst =
        event_push(&w->events.sensor_ends, &w->events.sensor_end_count,
                   &w->events.sensor_end_cap);
    if (!dst)
      continue;
    fill_shape_snapshot(w, dst, true, src->sensorShapeId);
    fill_shape_snapshot(w, dst, false, src->visitorShapeId);
  }
}

static void capture_body_events(Phys3dWorld *w) {
  b3BodyEvents ev = b3World_GetBodyEvents(w->id);
  for (int i = 0; i < ev.moveCount; ++i) {
    b3BodyMoveEvent *src = &ev.moveEvents[i];
    Phys3dBody *body = (Phys3dBody *)src->userData;
    Phys3dBodyEventSnapshot *dst = (Phys3dBodyEventSnapshot *)event_push_raw(
        (void **)&w->events.moves, &w->events.move_count, &w->events.move_cap,
        sizeof(Phys3dBodyEventSnapshot), 16);
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

static void capture_joint_events(Phys3dWorld *w) {
  b3JointEvents ev = b3World_GetJointEvents(w->id);
  for (int i = 0; i < ev.count; ++i) {
    b3JointEvent *src = &ev.jointEvents[i];
    Phys3dJointEventSnapshot *dst = (Phys3dJointEventSnapshot *)event_push_raw(
        (void **)&w->events.joints, &w->events.joint_count,
        &w->events.joint_cap, sizeof(Phys3dJointEventSnapshot), 8);
    if (!dst)
      continue;
    bool valid = B3_IS_NON_NULL(src->jointId) && b3Joint_IsValid(src->jointId);
    const char *joint_key = "";
    const char *a_key = "";
    const char *b_key = "";
    dst->type = 0;
    if (valid) {
      dst->type = joint_type_from_b3(b3Joint_GetType(src->jointId));
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

static LubStatus p3_step(LubContext *ctx, LubHandle world, float dt,
                         P3StepInfo *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  if (in_callback(app, "phys3d_step"))
    return LUB_ERROR;
  Phys3dWorld *w = check_world(app, world, "phys3d_step");
  if (!w)
    return LUB_ERROR;
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
    callbacks_clear(w);
  }
  if (w->prune)
    prune_world(w);
  int command_count = apply_body_commands(w);
  event_buffer_clear(&w->events);
  w->accumulator += dt;
  int steps = 0;
  while (w->accumulator + 1e-9 >= (double)w->fixed_dt && steps < w->max_steps) {
    Phys3dWorld *prev_mixer_world = g_mixer_world;
    g_mixer_world = callbacks_any(&w->callbacks) ? w : NULL;
    b3World_Step(w->id, w->fixed_dt, w->substeps);
    g_mixer_world = prev_mixer_world;
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
  if (steps > 0)
    capture_body_events(w);
  w->begun = false;
  callbacks_clear(w);
  out->steps = steps;
  out->commands = command_count;
  out->alpha =
      w->fixed_dt > 0.0f ? (float)(w->accumulator / (double)w->fixed_dt) : 0.0f;
  out->dropped = dropped;
  out->contact_begins = w->events.begin_count;
  out->contact_ends = w->events.end_count;
  out->contact_hits = w->events.hit_count;
  out->sensor_begins = w->events.sensor_begin_count;
  out->sensor_ends = w->events.sensor_end_count;
  out->body_moves = w->events.move_count;
  out->joint_events = w->events.joint_count;
  return LUB_OK;
}

// ---------------------------------------------------------- body getters

static LubStatus p3_pose(LubContext *ctx, LubHandle body, P3Pose *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  out->position = lub_pos(b3Body_GetPosition(b->id));
  out->rotation = lub_quat(b3Body_GetRotation(b->id));
  out->linear_velocity = lub_vec3(b3Body_GetLinearVelocity(b->id));
  out->angular_velocity = lub_vec3(b3Body_GetAngularVelocity(b->id));
  out->awake = b3Body_IsAwake(b->id);
  out->enabled = b3Body_IsEnabled(b->id);
  out->sleep = b3Body_IsSleepEnabled(b->id);
  out->sleep_threshold = b3Body_GetSleepThreshold(b->id);
  return LUB_OK;
}

static LubStatus p3_velocity(LubContext *ctx, LubHandle body, P3Velocity *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  out->linear = lub_vec3(b3Body_GetLinearVelocity(b->id));
  out->angular = lub_vec3(b3Body_GetAngularVelocity(b->id));
  return LUB_OK;
}

static LubStatus p3_mass(LubContext *ctx, LubHandle body, P3MassData *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  b3MassData md = b3Body_GetMassData(b->id);
  out->mass = md.mass;
  out->center = lub_pos(b3Body_GetWorldCenterOfMass(b->id));
  out->local_center = lub_vec3(md.center);
  out->xx = md.inertia.cx.x;
  out->yy = md.inertia.cy.y;
  out->zz = md.inertia.cz.z;
  out->xy = md.inertia.cy.x;
  out->xz = md.inertia.cz.x;
  out->yz = md.inertia.cz.y;
  return LUB_OK;
}

static LubStatus p3_center(LubContext *ctx, LubHandle body, LubVec3d *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  *out = lub_pos(b3Body_GetWorldCenterOfMass(b->id));
  return LUB_OK;
}

static LubStatus p3_world_point(LubContext *ctx, LubHandle body, LubVec3d local,
                                LubVec3d *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  *out = lub_pos(b3Body_GetWorldPoint(b->id, vec3_of(local)));
  return LUB_OK;
}

static LubStatus p3_local_point(LubContext *ctx, LubHandle body, LubVec3d world,
                                LubVec3d *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  *out = lub_vec3(b3Body_GetLocalPoint(b->id, b3ToPos(vec3_of(world))));
  return LUB_OK;
}

static LubStatus p3_velocity_at(LubContext *ctx, LubHandle body, LubVec3d world,
                                LubVec3d *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  *out = lub_vec3(b3Body_GetWorldPointVelocity(b->id, b3ToPos(vec3_of(world))));
  return LUB_OK;
}

// ------------------------------------------------------------ shape parts

static P3Filter filter_of(b3Filter f) {
  P3Filter out = {f.categoryBits, f.maskBits, f.groupIndex};
  return out;
}

static void fill_shape_part(P3ShapePart *out, b3ShapeId shape_id) {
  memset(out, 0, sizeof(*out));
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
    out->shape = shape->handle;
    out->body = shape->body->handle;
    out->kind = shape->kind;
  }
  out->body_key = str_or_empty(body_key);
  out->shape_key = str_or_empty(shape_key);
  out->tag = str_or_empty(tag);
  out->material_name = str_or_empty(material_name);
  if (valid) {
    out->material_id = (int)b3Shape_GetSurfaceMaterial(shape_id).userMaterialId;
    out->has_material = true;
    out->has_filter = true;
    out->filter = filter_of(b3Shape_GetFilter(shape_id));
  }
  out->valid = valid && shape && shape->body && shape->key;
}

static void fill_snapshot_part(P3ShapePart *out, const char *body,
                               const char *shape, const char *tag,
                               const char *material_name, int material_id,
                               bool valid) {
  memset(out, 0, sizeof(*out));
  out->body_key = str_or_empty(body);
  out->shape_key = str_or_empty(shape);
  out->tag = str_or_empty(tag);
  out->material_name = str_or_empty(material_name);
  out->material_id = material_id;
  bool has_identity = (body && body[0] != '\0') || (shape && shape[0] != '\0');
  out->has_material = valid || has_identity ||
                      (material_name && material_name[0] != '\0') ||
                      material_id != 0;
  out->valid = valid;
}

// -------------------------------------------------------------- callbacks

static bool phys3d_custom_filter_callback(b3ShapeId shape_id_a,
                                          b3ShapeId shape_id_b, void *context) {
  Phys3dWorld *w = (Phys3dWorld *)context;
  if (!w || !w->callbacks.filter)
    return true;
  P3ShapePart a, b;
  fill_shape_part(&a, shape_id_a);
  fill_shape_part(&b, shape_id_b);
  w->state->callback_depth++;
  bool collide = w->callbacks.filter(w->callbacks.user, &a, &b);
  w->state->callback_depth--;
  return collide;
}

static bool phys3d_pre_solve_callback(b3ShapeId shape_id_a,
                                      b3ShapeId shape_id_b, b3Pos point,
                                      b3Vec3 normal, void *context) {
  Phys3dWorld *w = (Phys3dWorld *)context;
  if (!w || !w->callbacks.pre_solve)
    return true;
  P3PreSolve c;
  memset(&c, 0, sizeof(c));
  fill_shape_part(&c.a, shape_id_a);
  fill_shape_part(&c.b, shape_id_b);
  c.x = (float)point.x;
  c.y = (float)point.y;
  c.z = (float)point.z;
  c.nx = normal.x;
  c.ny = normal.y;
  c.nz = normal.z;
  w->state->callback_depth++;
  bool solve = w->callbacks.pre_solve(w->callbacks.user, &c);
  w->state->callback_depth--;
  return solve;
}

static float phys3d_friction_callback(float friction_a, uint64_t material_a,
                                      float friction_b, uint64_t material_b) {
  float fa = friction_a < 0.0f ? 0.0f : friction_a;
  float fb = friction_b < 0.0f ? 0.0f : friction_b;
  float fallback = sqrtf(fa * fb);
  Phys3dWorld *w = g_mixer_world;
  if (!w || !w->callbacks.friction)
    return fallback;
  w->state->callback_depth++;
  float out =
      w->callbacks.friction(w->callbacks.user, friction_a, (int32_t)material_a,
                            friction_b, (int32_t)material_b);
  w->state->callback_depth--;
  return out;
}

static float phys3d_restitution_callback(float restitution_a,
                                         uint64_t material_a,
                                         float restitution_b,
                                         uint64_t material_b) {
  float fallback =
      restitution_a > restitution_b ? restitution_a : restitution_b;
  Phys3dWorld *w = g_mixer_world;
  if (!w || !w->callbacks.restitution)
    return fallback;
  w->state->callback_depth++;
  float out = w->callbacks.restitution(w->callbacks.user, restitution_a,
                                       (int32_t)material_a, restitution_b,
                                       (int32_t)material_b);
  w->state->callback_depth--;
  return out;
}

// ---------------------------------------------------------- shape queries

static bool ray_valid(App *app, const P3Ray *ray, const char *fn) {
  b3Vec3 d = vec3_of(ray->translation);
  if (d.x * d.x + d.y * d.y + d.z * d.z <= 1e-12f) {
    lub_api_fail(app, "%s: ray translation must be non-zero", fn);
    return false;
  }
  return true;
}

static LubStatus p3_shape_raycast(LubContext *ctx, LubHandle shape,
                                  const P3Ray *ray, P3RayHit *out, bool *hit) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  *hit = false;
  Phys3dShape *s = query_shape(app, shape);
  if (!s)
    return LUB_NOT_FOUND;
  if (!ray_valid(app, ray, "phys3d_shape_raycast"))
    return LUB_ERROR;
  b3WorldCastOutput r = b3Shape_RayCast(s->id, b3ToPos(vec3_of(ray->origin)),
                                        vec3_of(ray->translation));
  if (!r.hit)
    return LUB_OK;
  *hit = true;
  out->point = lub_pos(r.point);
  out->normal = lub_vec3(r.normal);
  out->fraction = r.fraction;
  out->iterations = r.iterations;
  out->triangle_index = r.triangleIndex;
  out->child_index = r.childIndex;
  return LUB_OK;
}

static LubStatus p3_shape_closest_point(LubContext *ctx, LubHandle shape,
                                        LubVec3d point, LubVec3d *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dShape *s = query_shape(app, shape);
  if (!s)
    return LUB_NOT_FOUND;
  *out = lub_vec3(b3Shape_GetClosestPoint(s->id, vec3_of(point)));
  return LUB_OK;
}

static P3Aabb aabb_of(b3AABB a) {
  P3Aabb out = {lub_vec3(a.lowerBound), lub_vec3(a.upperBound)};
  return out;
}

static LubStatus p3_shape_aabb(LubContext *ctx, LubHandle shape, P3Aabb *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dShape *s = query_shape(app, shape);
  if (!s)
    return LUB_NOT_FOUND;
  *out = aabb_of(b3Shape_GetAABB(s->id));
  return LUB_OK;
}

static LubStatus p3_shape_info(LubContext *ctx, LubHandle shape,
                               P3ShapeInfo *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dShape *s = query_shape(app, shape);
  if (!s)
    return LUB_NOT_FOUND;
  fill_shape_part(&out->part, s->id);
  out->part.kind = s->kind;
  out->density = b3Shape_GetDensity(s->id);
  b3SurfaceMaterial material = b3Shape_GetSurfaceMaterial(s->id);
  out->friction = material.friction;
  out->restitution = material.restitution;
  out->sensor = b3Shape_IsSensor(s->id);
  out->sensor_events = b3Shape_AreSensorEventsEnabled(s->id);
  out->contact = b3Shape_AreContactEventsEnabled(s->id);
  out->pre_solve = b3Shape_ArePreSolveEventsEnabled(s->id);
  out->hit = b3Shape_AreHitEventsEnabled(s->id);
  out->aabb = aabb_of(b3Shape_GetAABB(s->id));
  return LUB_OK;
}

static Phys3dShape *check_live_shape(App *app, LubHandle h, const char *fn) {
  if (in_callback(app, fn))
    return NULL;
  Phys3dShape *s = check_shape(app, h, fn);
  if (!s)
    return NULL;
  if (!shape_is_live(s)) {
    lub_api_fail(app, "%s: shape is not live", fn);
    return NULL;
  }
  return s;
}

static LubStatus p3_shape_set_material(LubContext *ctx, LubHandle shape,
                                       const P3MaterialDesc *d) {
  App *app = lub_api_app(ctx);
  Phys3dShape *s = check_live_shape(app, shape, "phys3d_shape_set_material");
  if (!s)
    return LUB_ERROR;
  if (d->has_density)
    b3Shape_SetDensity(s->id, d->density, true);
  b3SurfaceMaterial material = b3Shape_GetSurfaceMaterial(s->id);
  bool dirty = false;
  if (d->has_friction) {
    material.friction = d->friction;
    dirty = true;
  }
  if (d->has_restitution) {
    material.restitution = d->restitution;
    dirty = true;
  }
  if (d->has_material_id) {
    material.userMaterialId = (uint64_t)d->material_id;
    s->material_id = d->material_id;
    dirty = true;
  }
  if (dirty)
    b3Shape_SetSurfaceMaterial(s->id, material);
  if (d->has_material_name &&
      !phys_owned_string_set(&s->material_name, d->material_name))
    return lub_api_fail(app, "phys3d_shape_set_material: out of memory");
  shape_tombstone_update_shape(s);
  return LUB_OK;
}

static LubStatus p3_shape_set_filter(LubContext *ctx, LubHandle shape,
                                     const P3Filter *filter) {
  App *app = lub_api_app(ctx);
  Phys3dShape *s = check_live_shape(app, shape, "phys3d_shape_set_filter");
  if (!s)
    return LUB_ERROR;
  b3Filter f = b3Shape_GetFilter(s->id);
  f.categoryBits = filter->category_bits;
  f.maskBits = filter->mask_bits;
  f.groupIndex = filter->group_index;
  b3Shape_SetFilter(s->id, f, true);
  shape_tombstone_update_shape(s);
  return LUB_OK;
}

static LubStatus p3_shape_set_events(LubContext *ctx, LubHandle shape,
                                     const P3EventFlags *flags) {
  App *app = lub_api_app(ctx);
  Phys3dShape *s = check_live_shape(app, shape, "phys3d_shape_set_events");
  if (!s)
    return LUB_ERROR;
  if (flags->has_sensor_events)
    b3Shape_EnableSensorEvents(s->id, flags->sensor_events);
  if (flags->has_contact)
    b3Shape_EnableContactEvents(s->id, flags->contact);
  if (flags->has_pre_solve)
    b3Shape_EnablePreSolveEvents(s->id, flags->pre_solve);
  if (flags->has_hit)
    b3Shape_EnableHitEvents(s->id, flags->hit);
  shape_tombstone_update_shape(s);
  return LUB_OK;
}

// ------------------------------------------------------------- body lists

static LubStatus p3_body_shapes(LubContext *ctx, LubHandle body,
                                const P3ShapePart **items, int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  int capacity = b3Body_GetShapeCount(b->id);
  if (capacity <= 0)
    return LUB_OK;
  b3ShapeId *ids = (b3ShapeId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return lub_api_fail(app, "phys3d_body_shapes: out of memory");
  int n = b3Body_GetShapes(b->id, ids, capacity);
  P3ShapePart *parts = (P3ShapePart *)scratch_alloc(
      app, sizeof(P3ShapePart) * (size_t)(n > 0 ? n : 1));
  if (!parts) {
    SDL_free(ids);
    return lub_api_fail(app, "phys3d_body_shapes: out of memory");
  }
  for (int i = 0; i < n; ++i)
    fill_shape_part(&parts[i], ids[i]);
  SDL_free(ids);
  *items = parts;
  *count = n;
  return LUB_OK;
}

static LubStatus p3_body_joints(LubContext *ctx, LubHandle body,
                                const P3JointView **items, int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  int capacity = b3Body_GetJointCount(b->id);
  if (capacity <= 0)
    return LUB_OK;
  b3JointId *ids = (b3JointId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return lub_api_fail(app, "phys3d_body_joints: out of memory");
  int n = b3Body_GetJoints(b->id, ids, capacity);
  P3JointView *views = (P3JointView *)scratch_alloc(
      app, sizeof(P3JointView) * (size_t)(n > 0 ? n : 1));
  if (!views) {
    SDL_free(ids);
    return lub_api_fail(app, "phys3d_body_joints: out of memory");
  }
  for (int i = 0; i < n; ++i) {
    Phys3dJoint *j = B3_IS_NON_NULL(ids[i]) && b3Joint_IsValid(ids[i])
                         ? (Phys3dJoint *)b3Joint_GetUserData(ids[i])
                         : NULL;
    fill_joint_view(&views[i], j);
  }
  SDL_free(ids);
  *items = views;
  *count = n;
  return LUB_OK;
}

static LubStatus p3_body_contacts(LubContext *ctx, LubHandle body,
                                  const P3ContactData **items, int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  Phys3dBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  int capacity = b3Body_GetContactCapacity(b->id);
  if (capacity <= 0)
    return LUB_OK;
  b3ContactData *data = (b3ContactData *)SDL_malloc(sizeof(*data) * capacity);
  if (!data)
    return lub_api_fail(app, "phys3d_body_contacts: out of memory");
  int n = b3Body_GetContactData(b->id, data, capacity);
  P3ContactData *out = (P3ContactData *)scratch_alloc(
      app, sizeof(P3ContactData) * (size_t)(n > 0 ? n : 1));
  if (!out) {
    SDL_free(data);
    return lub_api_fail(app, "phys3d_body_contacts: out of memory");
  }
  for (int i = 0; i < n; ++i) {
    const b3ContactData *c = &data[i];
    fill_shape_part(&out[i].a, c->shapeIdA);
    fill_shape_part(&out[i].b, c->shapeIdB);
    const b3Manifold *manifold =
        c->manifoldCount > 0 && c->manifolds ? &c->manifolds[0] : NULL;
    out[i].normal = manifold ? lub_vec3(manifold->normal) : (LubVec3d){0, 0, 0};
    out[i].manifold_count = c->manifoldCount;
    out[i].point_count = manifold ? manifold->pointCount : 0;
    if (manifold && manifold->pointCount > 0 && B3_IS_NON_NULL(c->shapeIdA) &&
        b3Shape_IsValid(c->shapeIdA)) {
      b3Pos com = b3Body_GetWorldCenterOfMass(b3Shape_GetBody(c->shapeIdA));
      b3Vec3 anchor = manifold->points[0].anchorA;
      out[i].has_point = true;
      out[i].point =
          (LubVec3d){(float)(com.x + anchor.x), (float)(com.y + anchor.y),
                     (float)(com.z + anchor.z)};
      out[i].separation = manifold->points[0].separation;
    }
  }
  SDL_free(data);
  *items = out;
  *count = n;
  return LUB_OK;
}

// ------------------------------------------------------------ step events

static LubStatus contact_list(App *app, Phys3dContactSnapshot *src, int n,
                              const P3Contact **items, int32_t *count) {
  P3Contact *out = (P3Contact *)scratch_alloc(app, sizeof(P3Contact) *
                                                       (size_t)(n > 0 ? n : 1));
  if (!out)
    return lub_api_fail(app, "phys3d_contacts: out of memory");
  for (int i = 0; i < n; ++i) {
    Phys3dContactSnapshot *e = &src[i];
    fill_snapshot_part(&out[i].a, e->a_body, e->a_shape, e->a_tag,
                       e->a_material, e->a_material_id, e->a_valid);
    fill_snapshot_part(&out[i].b, e->b_body, e->b_shape, e->b_tag,
                       e->b_material, e->b_material_id, e->b_valid);
    out[i].normal = (LubVec3d){e->nx, e->ny, e->nz};
    out[i].point_count = e->point_count;
    out[i].point = (LubVec3d){e->x, e->y, e->z};
    out[i].approach_speed = e->approach_speed;
  }
  *items = out;
  *count = n;
  return LUB_OK;
}

static LubStatus p3_contacts(LubContext *ctx, LubHandle world, int32_t kind,
                             const P3Contact **items, int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  switch (kind) {
  case LUB_PHYS3D_EVENT_KIND_BEGIN:
    return contact_list(app, w->events.begins, w->events.begin_count, items,
                        count);
  case LUB_PHYS3D_EVENT_KIND_END:
    return contact_list(app, w->events.ends, w->events.end_count, items, count);
  case LUB_PHYS3D_EVENT_KIND_HIT:
    return contact_list(app, w->events.hits, w->events.hit_count, items, count);
  default:
    return lub_api_fail(app,
                        "phys3d_contacts: kind must be begin, end, or hit");
  }
}

static LubStatus p3_sensors(LubContext *ctx, LubHandle world, int32_t kind,
                            const P3Contact **items, int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  switch (kind) {
  case LUB_PHYS3D_EVENT_KIND_BEGIN:
    return contact_list(app, w->events.sensor_begins,
                        w->events.sensor_begin_count, items, count);
  case LUB_PHYS3D_EVENT_KIND_END:
    return contact_list(app, w->events.sensor_ends, w->events.sensor_end_count,
                        items, count);
  default:
    return lub_api_fail(app, "phys3d_sensors: kind must be begin or end");
  }
}

static LubStatus p3_body_events(LubContext *ctx, LubHandle world,
                                const P3BodyEvent **items, int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  int n = w->events.move_count;
  P3BodyEvent *out = (P3BodyEvent *)scratch_alloc(
      app, sizeof(P3BodyEvent) * (size_t)(n > 0 ? n : 1));
  if (!out)
    return lub_api_fail(app, "phys3d_body_events: out of memory");
  for (int i = 0; i < n; ++i) {
    Phys3dBodyEventSnapshot *e = &w->events.moves[i];
    out[i].body = str_or_empty(e->body);
    out[i].valid = e->valid;
    out[i].position = (LubVec3d){e->x, e->y, e->z};
    out[i].rotation = (LubQuat3d){e->qx, e->qy, e->qz, e->qw};
    out[i].fell_asleep = e->fell_asleep;
  }
  *items = out;
  *count = n;
  return LUB_OK;
}

static LubStatus p3_joint_events(LubContext *ctx, LubHandle world,
                                 const P3JointEvent **items, int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  int n = w->events.joint_count;
  P3JointEvent *out = (P3JointEvent *)scratch_alloc(
      app, sizeof(P3JointEvent) * (size_t)(n > 0 ? n : 1));
  if (!out)
    return lub_api_fail(app, "phys3d_joint_events: out of memory");
  for (int i = 0; i < n; ++i) {
    Phys3dJointEventSnapshot *e = &w->events.joints[i];
    out[i].joint = str_or_empty(e->joint);
    out[i].type = e->type;
    out[i].a = str_or_empty(e->body_a);
    out[i].b = str_or_empty(e->body_b);
    out[i].valid = e->valid;
  }
  *items = out;
  *count = n;
  return LUB_OK;
}

// ---------------------------------------------------------- world queries

static b3QueryFilter query_filter_of(const P3QueryFilter *f) {
  b3QueryFilter out = b3DefaultQueryFilter();
  if (f) {
    out.categoryBits = f->category_bits;
    out.maskBits = f->mask_bits;
  }
  return out;
}

typedef struct QueryCtx {
  Phys3dState *state;
  b3Pos origin; // collide_mover の plane を world に戻すため
  P3OverlapFn overlap;
  P3RayFn ray;
  P3PlaneFn plane;
  void *user;
} QueryCtx;

static bool overlap_trampoline(b3ShapeId shape_id, void *context) {
  QueryCtx *q = (QueryCtx *)context;
  P3ShapePart part;
  fill_shape_part(&part, shape_id);
  q->state->callback_depth++;
  bool keep = q->overlap(q->user, &part);
  q->state->callback_depth--;
  return keep;
}

static float ray_trampoline(b3ShapeId shape_id, b3Pos point, b3Vec3 normal,
                            float fraction, uint64_t user_material_id,
                            int triangle_index, int child_index,
                            void *context) {
  QueryCtx *q = (QueryCtx *)context;
  P3RayHit hit;
  memset(&hit, 0, sizeof(hit));
  fill_shape_part(&hit.shape, shape_id);
  hit.point = lub_pos(point);
  hit.normal = lub_vec3(normal);
  hit.fraction = fraction;
  hit.hit_material_id = (int32_t)user_material_id;
  hit.triangle_index = triangle_index;
  hit.child_index = child_index;
  q->state->callback_depth++;
  float r = q->ray(q->user, &hit);
  q->state->callback_depth--;
  return r;
}

static P3TreeStats stats_of(b3TreeStats s) {
  P3TreeStats out = {s.nodeVisits, s.leafVisits};
  return out;
}

static LubStatus p3_raycast_closest(LubContext *ctx, LubHandle world,
                                    const P3Ray *ray,
                                    const P3QueryFilter *filter, P3RayHit *out,
                                    bool *hit) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  *hit = false;
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  if (!ray_valid(app, ray, "phys3d_raycast"))
    return LUB_ERROR;
  b3RayResult r = b3World_CastRayClosest(w->id, b3ToPos(vec3_of(ray->origin)),
                                         vec3_of(ray->translation),
                                         query_filter_of(filter));
  if (!r.hit)
    return LUB_OK;
  *hit = true;
  fill_shape_part(&out->shape, r.shapeId);
  out->point = lub_pos(r.point);
  out->normal = lub_vec3(r.normal);
  out->fraction = r.fraction;
  out->hit_material_id = (int32_t)r.userMaterialId;
  out->triangle_index = r.triangleIndex;
  out->child_index = r.childIndex;
  out->node_visits = r.nodeVisits;
  out->leaf_visits = r.leafVisits;
  return LUB_OK;
}

static LubStatus p3_raycast(LubContext *ctx, LubHandle world, const P3Ray *ray,
                            const P3QueryFilter *filter, P3RayFn fn, void *user,
                            P3TreeStats *stats) {
  App *app = lub_api_app(ctx);
  memset(stats, 0, sizeof(*stats));
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  if (!ray_valid(app, ray, "phys3d_raycast"))
    return LUB_ERROR;
  if (!fn)
    return lub_api_fail(app, "phys3d_raycast: visitor required");
  QueryCtx q = {phys_state(app), b3ToPos(b3Vec3_zero), NULL, fn, NULL, user};
  b3TreeStats s = b3World_CastRay(w->id, b3ToPos(vec3_of(ray->origin)),
                                  vec3_of(ray->translation),
                                  query_filter_of(filter), ray_trampoline, &q);
  *stats = stats_of(s);
  return LUB_OK;
}

static LubStatus p3_overlap_aabb(LubContext *ctx, LubHandle world,
                                 const P3Aabb *aabb,
                                 const P3QueryFilter *filter, P3OverlapFn fn,
                                 void *user, P3TreeStats *stats) {
  App *app = lub_api_app(ctx);
  memset(stats, 0, sizeof(*stats));
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  if (aabb->min.x > aabb->max.x || aabb->min.y > aabb->max.y ||
      aabb->min.z > aabb->max.z)
    return lub_api_fail(app, "phys3d_overlap_aabb: min must be <= max");
  if (!fn)
    return lub_api_fail(app, "phys3d_overlap_aabb: visitor required");
  b3AABB box = {vec3_of(aabb->min), vec3_of(aabb->max)};
  QueryCtx q = {phys_state(app), b3ToPos(b3Vec3_zero), fn, NULL, NULL, user};
  b3TreeStats s = b3World_OverlapAABB(w->id, box, query_filter_of(filter),
                                      overlap_trampoline, &q);
  *stats = stats_of(s);
  return LUB_OK;
}

#define PHYS3D_PROXY_MAX_POINTS 8

// proxy の点は origin 相対にする (world 原点から遠くても精度を保つ)。
static bool make_proxy(App *app, const P3ShapeProxy *p, const char *fn,
                       b3Vec3 *points, b3ShapeProxy *proxy, b3Pos *origin) {
  switch (p->kind) {
  case P3_PROXY_SPHERE:
    if (p->r <= 0.0f) {
      lub_api_fail(app, "%s: sphere r must be > 0", fn);
      return false;
    }
    points[0] = b3Vec3_zero;
    proxy->points = points;
    proxy->count = 1;
    proxy->radius = p->r;
    *origin = b3ToPos(vec3_of(p->center));
    return true;
  case P3_PROXY_BOX: {
    if (p->hx <= 0.0f || p->hy <= 0.0f || p->hz <= 0.0f) {
      lub_api_fail(app, "%s: box hx, hy and hz must be > 0", fn);
      return false;
    }
    if (p->r < 0.0f) {
      lub_api_fail(app, "%s: box radius must be >= 0", fn);
      return false;
    }
    b3Quat rotation = p->has_rotation ? quat_of(p->rotation) : b3Quat_identity;
    int count = 0;
    for (int ix = -1; ix <= 1; ix += 2) {
      for (int iy = -1; iy <= 1; iy += 2) {
        for (int iz = -1; iz <= 1; iz += 2) {
          b3Vec3 corner = {p->hx * (float)ix, p->hy * (float)iy,
                           p->hz * (float)iz};
          points[count++] =
              p->has_rotation ? b3RotateVector(rotation, corner) : corner;
        }
      }
    }
    proxy->points = points;
    proxy->count = count;
    proxy->radius = p->r;
    *origin = b3ToPos(vec3_of(p->center));
    return true;
  }
  case P3_PROXY_CAPSULE: {
    b3Vec3 a = vec3_of(p->a);
    b3Vec3 c = vec3_of(p->b);
    if (p->r <= 0.0f) {
      lub_api_fail(app, "%s: capsule r must be > 0", fn);
      return false;
    }
    if (b3DistanceSquared(a, c) <= 1e-12f) {
      lub_api_fail(app, "%s: capsule endpoints must be distinct", fn);
      return false;
    }
    b3Vec3 mid = b3MulSV(0.5f, b3Add(a, c));
    points[0] = b3Sub(a, mid);
    points[1] = b3Sub(c, mid);
    proxy->points = points;
    proxy->count = 2;
    proxy->radius = p->r;
    *origin = b3ToPos(mid);
    return true;
  }
  default:
    lub_api_fail(app, "%s: expected sphere, box, or capsule", fn);
    return false;
  }
}

static LubStatus p3_overlap_shape(LubContext *ctx, LubHandle world,
                                  const P3ShapeProxy *proxy,
                                  const P3QueryFilter *filter, P3OverlapFn fn,
                                  void *user, P3TreeStats *stats) {
  App *app = lub_api_app(ctx);
  memset(stats, 0, sizeof(*stats));
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b3Vec3 points[PHYS3D_PROXY_MAX_POINTS];
  b3ShapeProxy p;
  b3Pos origin;
  if (!make_proxy(app, proxy, "phys3d_overlap_shape", points, &p, &origin))
    return LUB_ERROR;
  if (!fn)
    return lub_api_fail(app, "phys3d_overlap_shape: visitor required");
  QueryCtx q = {phys_state(app), b3ToPos(b3Vec3_zero), fn, NULL, NULL, user};
  b3TreeStats s = b3World_OverlapShape(
      w->id, origin, &p, query_filter_of(filter), overlap_trampoline, &q);
  *stats = stats_of(s);
  return LUB_OK;
}

static LubStatus p3_shape_cast(LubContext *ctx, LubHandle world,
                               const P3ShapeProxy *proxy, LubVec3d translation,
                               const P3QueryFilter *filter, P3RayFn fn,
                               void *user, P3TreeStats *stats) {
  App *app = lub_api_app(ctx);
  memset(stats, 0, sizeof(*stats));
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b3Vec3 points[PHYS3D_PROXY_MAX_POINTS];
  b3ShapeProxy p;
  b3Pos origin;
  if (!make_proxy(app, proxy, "phys3d_shape_cast", points, &p, &origin))
    return LUB_ERROR;
  b3Vec3 t = vec3_of(translation);
  if (b3LengthSquared(t) <= 1e-12f)
    return lub_api_fail(app, "phys3d_shape_cast: translation must be non-zero");
  if (!fn)
    return lub_api_fail(app, "phys3d_shape_cast: visitor required");
  QueryCtx q = {phys_state(app), b3ToPos(b3Vec3_zero), NULL, fn, NULL, user};
  b3TreeStats s = b3World_CastShape(
      w->id, origin, &p, t, query_filter_of(filter), ray_trampoline, &q);
  *stats = stats_of(s);
  return LUB_OK;
}

static bool mover_of(App *app, const P3Mover *m, const char *fn, b3Capsule *out,
                     b3Pos *origin) {
  b3Vec3 a = vec3_of(m->a);
  b3Vec3 c = vec3_of(m->b);
  out->radius = m->r;
  if (b3DistanceSquared(a, c) <= 1e-12f) {
    lub_api_fail(app, "%s: mover endpoints must be distinct", fn);
    return false;
  }
  if (out->radius <= 0.011f) {
    lub_api_fail(app, "%s: mover radius must be > 0.011", fn);
    return false;
  }
  b3Vec3 mid = b3MulSV(0.5f, b3Add(a, c));
  out->center1 = b3Sub(a, mid);
  out->center2 = b3Sub(c, mid);
  *origin = b3ToPos(mid);
  return true;
}

static LubStatus p3_cast_mover(LubContext *ctx, LubHandle world,
                               const P3Mover *mover, LubVec3d translation,
                               const P3QueryFilter *filter, float *fraction) {
  App *app = lub_api_app(ctx);
  *fraction = 0;
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b3Capsule capsule;
  b3Pos origin;
  if (!mover_of(app, mover, "phys3d_cast_mover", &capsule, &origin))
    return LUB_ERROR;
  b3Vec3 t = vec3_of(translation);
  if (b3LengthSquared(t) <= 1e-12f)
    return lub_api_fail(app, "phys3d_cast_mover: translation must be non-zero");
  *fraction = b3World_CastMover(w->id, origin, &capsule, t,
                                query_filter_of(filter), NULL, NULL);
  return LUB_OK;
}

static bool plane_trampoline(b3ShapeId shape_id, const b3PlaneResult *plane,
                             int plane_count, void *context) {
  QueryCtx *q = (QueryCtx *)context;
  P3MoverPlane p;
  memset(&p, 0, sizeof(p));
  fill_shape_part(&p.shape, shape_id);
  // plane の結果は query の origin 相対なので world に戻す。
  p.point = (LubVec3d){(float)(q->origin.x + plane->point.x),
                       (float)(q->origin.y + plane->point.y),
                       (float)(q->origin.z + plane->point.z)};
  p.normal = lub_vec3(plane->plane.normal);
  p.offset = (float)(plane->plane.offset + plane->plane.normal.x * q->origin.x +
                     plane->plane.normal.y * q->origin.y +
                     plane->plane.normal.z * q->origin.z);
  p.plane_count = plane_count;
  q->state->callback_depth++;
  bool keep = q->plane(q->user, &p);
  q->state->callback_depth--;
  return keep;
}

static LubStatus p3_collide_mover(LubContext *ctx, LubHandle world,
                                  const P3Mover *mover,
                                  const P3QueryFilter *filter, P3PlaneFn fn,
                                  void *user) {
  App *app = lub_api_app(ctx);
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b3Capsule capsule;
  b3Pos origin;
  if (!mover_of(app, mover, "phys3d_collide_mover", &capsule, &origin))
    return LUB_ERROR;
  if (!fn)
    return lub_api_fail(app, "phys3d_collide_mover: visitor required");
  QueryCtx q = {phys_state(app), origin, NULL, NULL, fn, user};
  b3World_CollideMover(w->id, origin, &capsule, query_filter_of(filter),
                       plane_trampoline, &q);
  return LUB_OK;
}

static LubStatus p3_profile(LubContext *ctx, LubHandle world, P3Profile *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b3Profile p = b3World_GetProfile(w->id);
  out->step = p.step;
  out->pairs = p.pairs;
  out->collide = p.collide;
  out->solve = p.solve;
  out->solver_setup = p.solverSetup;
  out->constraints = p.constraints;
  out->prepare_constraints = p.prepareConstraints;
  out->integrate_velocities = p.integrateVelocities;
  out->warm_start = p.warmStart;
  out->solve_impulses = p.solveImpulses;
  out->integrate_positions = p.integratePositions;
  out->relax_impulses = p.relaxImpulses;
  out->apply_restitution = p.applyRestitution;
  out->store_impulses = p.storeImpulses;
  out->split_islands = p.splitIslands;
  out->transforms = p.transforms;
  out->sensor_hits = p.sensorHits;
  out->joint_events = p.jointEvents;
  out->hit_events = p.hitEvents;
  out->refit = p.refit;
  out->bullets = p.bullets;
  out->sleep_islands = p.sleepIslands;
  out->sensors = p.sensors;
  return LUB_OK;
}

static LubStatus p3_counters(LubContext *ctx, LubHandle world,
                             P3Counters *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  Phys3dWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b3Counters c = b3World_GetCounters(w->id);
  out->body_count = c.bodyCount;
  out->shape_count = c.shapeCount;
  out->contact_count = c.contactCount;
  out->joint_count = c.jointCount;
  out->island_count = c.islandCount;
  out->stack_used = c.stackUsed;
  out->arena_capacity = c.arenaCapacity;
  out->static_tree_height = c.staticTreeHeight;
  out->tree_height = c.treeHeight;
  out->sat_call_count = c.satCallCount;
  out->sat_cache_hit_count = c.satCacheHitCount;
  out->byte_count = c.byteCount;
  out->task_count = c.taskCount;
  out->awake_contact_count = c.awakeContactCount;
  out->recycled_contact_count = c.recycledContactCount;
  out->distance_iterations = c.distanceIterations;
  out->push_back_iterations = c.pushBackIterations;
  out->root_iterations = c.rootIterations;
  for (int i = 0; i < 24; ++i)
    out->color_counts[i] = c.colorCounts[i];
  _Static_assert(P3_MANIFOLD_COUNT_BUCKETS == B3_CONTACT_MANIFOLD_COUNT_BUCKETS,
                 "manifold bucket count must match box3d");
  for (int i = 0; i < P3_MANIFOLD_COUNT_BUCKETS; ++i)
    out->manifold_counts[i] = c.manifoldCounts[i];
  return LUB_OK;
}

#include "physics_box3d_api.inc"
