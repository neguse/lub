// Box2D の即時モード層。key で宣言する world / body / shape / chain / joint
// の table と、C API (include/lub/lub_api.h の lub_phys2d_*) の実装。Lua には
// 触らない (Lua 面は src/lua_phys2d.c)。
#include "physics_box2d.h"

#include "api_internal.h"
#include "phys_common.h"

#include <SDL3/SDL.h>
#include <box2d/box2d.h>
#include <box2d/math_functions.h>
#include <float.h>
#include <math.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#define PHYS2D_BODY_BUCKETS 256
#define PHYS2D_SHAPE_BUCKETS 64
#define PHYS2D_CHAIN_BUCKETS 32
#define PHYS2D_JOINT_BUCKETS 128
#define PHYS2D_TOMBSTONE_BUCKETS 256

typedef struct PhysShape {
  char *key;
  char *tag;
  char *material_name;
  struct PhysBody *body;
  b2ShapeId id;
  int32_t handle;
  uint64_t seen_generation;
  uint64_t desc_hash;
  uint64_t constructor_hash;
  bool constructor_warned;
  int material_id;
  int kind; // LubPhys2dShapeKind
  struct PhysShape *next;
} PhysShape;

typedef struct PhysChain {
  char *key;
  char *tag;
  char *material_name;
  struct PhysBody *body;
  b2ChainId id;
  int32_t handle;
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
  int32_t handle;
  uint64_t seen_generation;
  int64_t version;
  uint64_t constructor_hash;
  bool constructor_warned;
  PhysShape *shapes[PHYS2D_SHAPE_BUCKETS];
  PhysChain *chains[PHYS2D_CHAIN_BUCKETS];
  struct PhysBody *next;
} PhysBody;

typedef struct PhysJoint {
  char *key;
  struct PhysWorld *world;
  PhysBody *body_a;
  PhysBody *body_b;
  b2JointId id;
  int32_t handle;
  uint64_t seen_generation;
  int64_t version;
  uint64_t constructor_hash;
  bool constructor_warned;
  int kind; // LubPhys2dJointType
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
  PhysState *state;
  b2WorldId id;
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
  PhysBody *bodies[PHYS2D_BODY_BUCKETS];
  PhysJoint *joints[PHYS2D_JOINT_BUCKETS];
  PhysShapeTombstone *shape_tombstones[PHYS2D_TOMBSTONE_BUCKETS];
  PhysEventBuffer events;
  PhysCommandQueue commands;
  LubPhys2dCallbacks callbacks;
  bool callbacks_pending;
  uint64_t callbacks_generation;
  struct PhysWorld *next;
};

// friction / restitution の callback は Box2D が context を渡さないので、
// step 中の world をここに置く。
static PhysWorld *g_mixer_world = NULL;

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
static void fill_shape_part(PhysState *state, LubPhys2dShapePart *out,
                            b2ShapeId shape_id);

static LubStr str_or_empty(const char *s) { return phys_str(s); }

static int32_t handle_alloc(PhysState *state, void *ptr, int kind) {
  return phys_handle_alloc(&state->handles, ptr, kind);
}

static void handle_release(PhysState *state, int32_t h) {
  phys_handle_release(&state->handles, h);
}

static void *handle_get(PhysState *state, int32_t h, int kind) {
  return phys_handle_get(&state->handles, h, kind);
}

static void *scratch_alloc(PhysState *state, size_t bytes) {
  return phys_scratch_alloc(&state->scratch, bytes);
}

// ------------------------------------------------------------- callbacks

static bool callbacks_any(const LubPhys2dCallbacks *cb) {
  return cb->filter || cb->pre_solve || cb->friction || cb->restitution;
}

static void callbacks_install(PhysWorld *w) {
  if (!w || B2_IS_NULL(w->id) || !b2World_IsValid(w->id))
    return;
  b2World_SetCustomFilterCallback(
      w->id, w->callbacks.filter ? phys2d_custom_filter_callback : NULL,
      w->callbacks.filter ? w : NULL);
  b2World_SetPreSolveCallback(
      w->id, w->callbacks.pre_solve ? phys2d_pre_solve_callback : NULL,
      w->callbacks.pre_solve ? w : NULL);
  b2World_SetFrictionCallback(
      w->id, w->callbacks.friction ? phys2d_friction_callback : NULL);
  b2World_SetRestitutionCallback(
      w->id, w->callbacks.restitution ? phys2d_restitution_callback : NULL);
}

static void callbacks_clear(PhysWorld *w) {
  if (!w)
    return;
  memset(&w->callbacks, 0, sizeof(w->callbacks));
  w->callbacks_pending = false;
  w->callbacks_generation = 0;
  callbacks_install(w);
}

static void callbacks_replace(PhysWorld *w, const LubPhys2dCallbacks *cb) {
  callbacks_clear(w);
  if (!cb || !callbacks_any(cb))
    return;
  w->callbacks = *cb;
  w->callbacks_pending = !w->begun;
  w->callbacks_generation = w->begun ? w->generation : 0;
  callbacks_install(w);
}

// --------------------------------------------------------------- lookups

static PhysWorld *world_get(PhysState *state, const char *key) {
  if (!state || !key)
    return NULL;
  uint32_t i = phys_hash_str32(key) & (PHYS2D_WORLD_BUCKETS - 1);
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
  uint32_t i = phys_hash_str32(key) & (PHYS2D_WORLD_BUCKETS - 1);
  w = (PhysWorld *)SDL_calloc(1, sizeof(PhysWorld));
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
  w->id = b2_nullWorldId;
  w->handle = handle_alloc(state, w, PHYS_HANDLE_WORLD);
  w->next = state->worlds[i];
  state->worlds[i] = w;
  return w;
}

static PhysBody *body_get(PhysWorld *w, const char *key) {
  if (!w || !key)
    return NULL;
  uint32_t i = phys_hash_str32(key) & (PHYS2D_BODY_BUCKETS - 1);
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
  uint32_t i = phys_hash_str32(key) & (PHYS2D_BODY_BUCKETS - 1);
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
  b->handle = handle_alloc(w->state, b, PHYS_HANDLE_BODY);
  b->next = w->bodies[i];
  w->bodies[i] = b;
  return b;
}

static PhysShape *shape_get(PhysBody *b, const char *key) {
  if (!b || !key)
    return NULL;
  uint32_t i = phys_hash_str32(key) & (PHYS2D_SHAPE_BUCKETS - 1);
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
  uint32_t i = phys_hash_str32(key) & (PHYS2D_SHAPE_BUCKETS - 1);
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
  s->handle = handle_alloc(b->world->state, s, PHYS_HANDLE_SHAPE);
  s->next = b->shapes[i];
  b->shapes[i] = s;
  return s;
}

static PhysChain *chain_get(PhysBody *b, const char *key) {
  if (!b || !key)
    return NULL;
  uint32_t i = phys_hash_str32(key) & (PHYS2D_CHAIN_BUCKETS - 1);
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
  uint32_t i = phys_hash_str32(key) & (PHYS2D_CHAIN_BUCKETS - 1);
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
  c->handle = handle_alloc(b->world->state, c, PHYS_HANDLE_CHAIN);
  c->next = b->chains[i];
  b->chains[i] = c;
  return c;
}

static PhysJoint *joint_get(PhysWorld *w, const char *key) {
  if (!w || !key)
    return NULL;
  uint32_t i = phys_hash_str32(key) & (PHYS2D_JOINT_BUCKETS - 1);
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
  uint32_t i = phys_hash_str32(key) & (PHYS2D_JOINT_BUCKETS - 1);
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
  j->handle = handle_alloc(w->state, j, PHYS_HANDLE_JOINT);
  j->next = w->joints[i];
  w->joints[i] = j;
  return j;
}

// ------------------------------------------------------------ tombstones

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

// ---------------------------------------------------------------- events

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

// -------------------------------------------------------------- commands

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

// 失敗は NULL (out of memory)。
static PhysCommand *command_queue_push(PhysWorld *w, PhysBody *b,
                                       PhysCommandKind kind) {
  if (!w || !b || !b->key)
    return NULL;
  PhysCommandQueue *queue = &w->commands;
  if (queue->count >= queue->cap) {
    int new_cap = queue->cap ? queue->cap * 2 : 32;
    PhysCommand *new_items = (PhysCommand *)SDL_realloc(
        queue->items, sizeof(*queue->items) * new_cap);
    if (!new_items)
      return NULL;
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
    return NULL;
  }
  cmd->kind = kind;
  if (B2_IS_NON_NULL(b->id) && b2Body_IsValid(b->id))
    cmd->body_id_key = b2StoreBodyId(b->id);
  cmd->wake = true;
  return cmd;
}

// ------------------------------------------------------------------ free

static void shape_free(PhysShape *s, bool destroy_id) {
  if (!s)
    return;
  if (destroy_id && B2_IS_NON_NULL(s->id) && b2Shape_IsValid(s->id)) {
    b2DestroyShape(s->id, true);
  }
  handle_release(s->body->world->state, s->handle);
  phys_owned_string_clear(&s->tag);
  phys_owned_string_clear(&s->material_name);
  SDL_free(s->key);
  SDL_free(s);
}

static void chain_free(PhysChain *c, bool destroy_id) {
  if (!c)
    return;
  if (destroy_id && B2_IS_NON_NULL(c->id) && b2Chain_IsValid(c->id)) {
    b2DestroyChain(c->id);
  }
  handle_release(c->body->world->state, c->handle);
  phys_owned_string_clear(&c->tag);
  phys_owned_string_clear(&c->material_name);
  SDL_free(c->key);
  SDL_free(c);
}

static void joint_free(PhysJoint *j, bool destroy_id) {
  if (!j)
    return;
  if (destroy_id && B2_IS_NON_NULL(j->id) && b2Joint_IsValid(j->id)) {
    b2DestroyJoint(j->id);
  }
  handle_release(j->world->state, j->handle);
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
  handle_release(b->world->state, b->handle);
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
  callbacks_clear(w);
  world_destroy_box2d_and_contents(w);
  event_buffer_free(&w->events);
  command_queue_free(&w->commands);
  handle_release(w->state, w->handle);
  SDL_free(w->key);
  SDL_free(w);
}

static void debug_free(PhysDebugBuffer *buffer) {
  SDL_free(buffer->segments.items);
  SDL_free(buffer->circles.items);
  SDL_free(buffer->capsules.items);
  SDL_free(buffer->polygons.items);
  SDL_free(buffer->points.items);
  memset(buffer, 0, sizeof(*buffer));
}

void phys2d_state_init(PhysState *state) {
  memset(state, 0, sizeof(*state));
  phys_handles_init(&state->handles);
}

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
  phys_handles_free(&state->handles);
  phys_scratch_free(&state->scratch);
  debug_free(&state->debug);
  memset(state, 0, sizeof(*state));
}

// -------------------------------------------------------- handle resolve

static PhysState *phys_state(App *app) { return &app->phys; }

static PhysWorld *world_from_handle(App *app, LubHandle h) {
  return (PhysWorld *)handle_get(phys_state(app), h, PHYS_HANDLE_WORLD);
}

static PhysBody *body_from_handle(App *app, LubHandle h) {
  return (PhysBody *)handle_get(phys_state(app), h, PHYS_HANDLE_BODY);
}

static PhysShape *shape_from_handle(App *app, LubHandle h) {
  return (PhysShape *)handle_get(phys_state(app), h, PHYS_HANDLE_SHAPE);
}

static PhysChain *chain_from_handle(App *app, LubHandle h) {
  return (PhysChain *)handle_get(phys_state(app), h, PHYS_HANDLE_CHAIN);
}

static PhysJoint *joint_from_handle(App *app, LubHandle h) {
  return (PhysJoint *)handle_get(phys_state(app), h, PHYS_HANDLE_JOINT);
}

// 変更する API 用: 無ければ LUB_ERROR。
static PhysWorld *check_world(App *app, LubHandle h, const char *fn) {
  PhysWorld *w = world_from_handle(app, h);
  if (!w)
    lub_api_fail(app, "%s: phys2d world not found", fn);
  return w;
}

static PhysBody *check_body(App *app, LubHandle h, const char *fn) {
  PhysBody *b = body_from_handle(app, h);
  if (!b)
    lub_api_fail(app, "%s: phys2d body not found", fn);
  return b;
}

static PhysShape *check_shape(App *app, LubHandle h, const char *fn) {
  PhysShape *s = shape_from_handle(app, h);
  if (!s)
    lub_api_fail(app, "%s: phys2d shape not found", fn);
  return s;
}

static PhysJoint *check_joint(App *app, LubHandle h, const char *fn) {
  PhysJoint *j = joint_from_handle(app, h);
  if (!j)
    lub_api_fail(app, "%s: phys2d joint not found", fn);
  return j;
}

// 問い合わせ API 用: live でなければ NULL (LUB_NOT_FOUND)。
static PhysWorld *query_world(App *app, LubHandle h) {
  PhysWorld *w = world_from_handle(app, h);
  if (!w || B2_IS_NULL(w->id) || !b2World_IsValid(w->id))
    return NULL;
  return w;
}

static PhysBody *query_body(App *app, LubHandle h) {
  PhysBody *b = body_from_handle(app, h);
  return body_is_live(b) ? b : NULL;
}

static PhysShape *query_shape(App *app, LubHandle h) {
  PhysShape *s = shape_from_handle(app, h);
  return shape_is_live(s) ? s : NULL;
}

static PhysChain *query_chain(App *app, LubHandle h) {
  PhysChain *c = chain_from_handle(app, h);
  return chain_is_live(c) ? c : NULL;
}

static PhysJoint *query_joint(App *app, LubHandle h) {
  PhysJoint *j = joint_from_handle(app, h);
  return joint_is_live(j) ? j : NULL;
}

static bool in_callback(App *app, const char *fn) {
  if (phys_state(app)->callback_depth <= 0)
    return false;
  lub_api_fail(
      app, "%s: physics mutation is not allowed inside phys2d callback", fn);
  return true;
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

static bool joint_is_live(PhysJoint *j) {
  return j && B2_IS_NON_NULL(j->id) && b2Joint_IsValid(j->id);
}

static PhysChain *chain_find_by_id(PhysState *state, b2ChainId id) {
  if (B2_IS_NULL(id))
    return NULL;
  for (int wi = 0; wi < PHYS2D_WORLD_BUCKETS; ++wi) {
    for (PhysWorld *w = state ? state->worlds[wi] : NULL; w; w = w->next) {
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

// key の LubStr を NUL 終端の buffer に写す。長すぎれば false。
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

// ------------------------------------------------------------ desc init

void lub_phys2d_world_desc_init(LubPhys2dWorldDesc *desc) {
  memset(desc, 0, sizeof(*desc));
  desc->gravity_x = 0.0f;
  desc->gravity_y = -9.8f;
  desc->fixed_dt = 1.0f / 60.0f;
  desc->substeps = 4;
  desc->max_steps = 4;
  desc->sleep = true;
  desc->continuous = true;
}

void lub_phys2d_body_desc_init(LubPhys2dBodyDesc *desc) {
  memset(desc, 0, sizeof(*desc));
  desc->type = LUB_PHYS2D_BODY_TYPE_STATIC;
  desc->enabled = true;
  desc->awake = true;
  desc->sleep = true;
  desc->gravity_scale = 1.0f;
  desc->initial_awake = true;
}

void lub_phys2d_shape_desc_init(LubPhys2dShapeDesc *desc) {
  memset(desc, 0, sizeof(*desc));
  desc->density = 1.0f;
  desc->friction = 0.6f;
  desc->filter.category_bits = 1u;
  desc->filter.mask_bits = UINT64_MAX;
}

void lub_phys2d_chain_desc_init(LubPhys2dChainDesc *desc) {
  memset(desc, 0, sizeof(*desc));
  desc->friction = 0.6f;
  desc->filter.category_bits = 1u;
  desc->filter.mask_bits = UINT64_MAX;
}

void lub_phys2d_joint_desc_init(LubPhys2dJointDesc *desc) {
  memset(desc, 0, sizeof(*desc));
  desc->type = LUB_PHYS2D_JOINT_TYPE_REVOLUTE;
  desc->local_axis_a.x = 1.0f;
  desc->length = 1.0f;
  desc->max_length = 1.0f;
  desc->upper = 1.0f;
  desc->max_force = 1.0f;
  desc->max_torque = 1.0f;
  desc->correction_factor = 0.3f;
  desc->draw_size = 0.25f;
}

// ----------------------------------------------------------------- world

static bool world_create_or_recreate(App *app, PhysWorld *w,
                                     const LubPhys2dWorldDesc *desc) {
  int64_t version = desc->has_version ? desc->version : 0;
  bool needs_create =
      B2_IS_NULL(w->id) || !b2World_IsValid(w->id) || w->version != version;
  b2Vec2 gravity = {desc->gravity_x, desc->gravity_y};
  if (needs_create) {
    callbacks_clear(w);
    world_destroy_box2d_and_contents(w);
    b2WorldDef def = b2DefaultWorldDef();
    def.gravity = gravity;
    def.enableSleep = desc->sleep;
    w->id = b2CreateWorld(&def);
    if (B2_IS_NULL(w->id)) {
      lub_api_fail(app, "phys2d_world: b2CreateWorld failed");
      return false;
    }
    w->version = version;
  } else {
    b2World_SetGravity(w->id, gravity);
    b2World_EnableSleeping(w->id, desc->sleep);
  }
  b2World_EnableContinuous(w->id, desc->continuous);
  if (desc->has_hit_event_threshold)
    b2World_SetHitEventThreshold(w->id, desc->hit_event_threshold);
  w->fixed_dt = desc->fixed_dt > 0.0f ? desc->fixed_dt : 1.0f / 60.0f;
  w->substeps = desc->substeps > 0 ? desc->substeps : 4;
  w->max_steps = desc->max_steps > 0 ? desc->max_steps : 4;
  return true;
}

LubStatus lub_phys2d_world(LubContext *ctx, LubStr key,
                           const LubPhys2dWorldDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (in_callback(app, "phys2d_world"))
    return LUB_ERROR;
  char kbuf[PHYS_KEY_MAX];
  if (!key_copy(app, key, kbuf, sizeof(kbuf), "phys2d_world"))
    return LUB_ERROR;
  LubPhys2dWorldDesc def;
  if (!desc) {
    lub_phys2d_world_desc_init(&def);
    desc = &def;
  }
  PhysWorld *w = world_get_or_create(phys_state(app), kbuf);
  if (!w)
    return lub_api_fail(app, "phys2d_world: out of memory");
  if (!world_create_or_recreate(app, w, desc))
    return LUB_ERROR;
  callbacks_replace(w, &desc->callbacks);
  *out = w->handle;
  return LUB_OK;
}

LubHandle lub_phys2d_world_find(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  char kbuf[PHYS_KEY_MAX];
  if (!key.ptr || key.len <= 0 || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return 0;
  PhysWorld *w = world_get(phys_state(app), kbuf);
  return w ? w->handle : 0;
}

LubStatus lub_phys2d_begin(LubContext *ctx, LubHandle world, bool prune) {
  App *app = lub_api_app(ctx);
  if (in_callback(app, "phys2d_begin"))
    return LUB_ERROR;
  PhysWorld *w = check_world(app, world, "phys2d_begin");
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

LubStatus lub_phys2d_world_info(LubContext *ctx, LubHandle world,
                                LubPhys2dWorldInfo *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  bool valid = B2_IS_NON_NULL(w->id) && b2World_IsValid(w->id);
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
  b2Vec2 gravity = b2World_GetGravity(w->id);
  out->gravity_x = gravity.x;
  out->gravity_y = gravity.y;
  out->sleep = b2World_IsSleepingEnabled(w->id);
  out->continuous = b2World_IsContinuousEnabled(w->id);
  out->warm_starting = b2World_IsWarmStartingEnabled(w->id);
  out->restitution_threshold = b2World_GetRestitutionThreshold(w->id);
  out->hit_event_threshold = b2World_GetHitEventThreshold(w->id);
  out->maximum_linear_speed = b2World_GetMaximumLinearSpeed(w->id);
  out->awake_body_count = b2World_GetAwakeBodyCount(w->id);
  return LUB_OK;
}

// ------------------------------------------------------------------ body

static uint64_t body_constructor_hash(const LubPhys2dBodyDesc *desc) {
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, desc->x);
  h = phys_hash_f32(h, desc->y);
  h = phys_hash_f32(h, desc->angle);
  h = phys_hash_f32(h, desc->vx);
  h = phys_hash_f32(h, desc->vy);
  h = phys_hash_f32(h, desc->w);
  h = phys_hash_bool(h, desc->initial_awake);
  return h;
}

static void log_body_constructor_drift(PhysBody *b, uint64_t hash) {
  if (b->constructor_hash == hash || b->constructor_warned)
    return;
  SDL_Log("phys2d_body('%s'): constructor fields changed without version bump",
          b->key);
  b->constructor_warned = true;
}

static b2BodyType body_type_from(int32_t type) {
  switch (type) {
  case LUB_PHYS2D_BODY_TYPE_KINEMATIC:
    return b2_kinematicBody;
  case LUB_PHYS2D_BODY_TYPE_DYNAMIC:
    return b2_dynamicBody;
  default:
    return b2_staticBody;
  }
}

static bool body_create(App *app, PhysBody *b, const LubPhys2dBodyDesc *desc,
                        uint64_t constructor_hash, int64_t version) {
  b2BodyDef def = b2DefaultBodyDef();
  def.type = body_type_from(desc->type);
  def.position = (b2Vec2){desc->x, desc->y};
  def.rotation = b2MakeRot(desc->angle);
  def.linearVelocity = (b2Vec2){desc->vx, desc->vy};
  def.angularVelocity = desc->w;
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
  if (B2_IS_NULL(b->id)) {
    lub_api_fail(app, "phys2d_body: b2CreateBody failed");
    return false;
  }
  if (desc->has_awake)
    b2Body_SetAwake(b->id, desc->awake);
  b->version = version;
  b->constructor_hash = constructor_hash;
  b->constructor_warned = false;
  return true;
}

static void body_apply_runtime(PhysBody *b, const LubPhys2dBodyDesc *desc) {
  b2Body_SetType(b->id, body_type_from(desc->type));
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

LubStatus lub_phys2d_body(LubContext *ctx, LubHandle world, LubStr key,
                          const LubPhys2dBodyDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (in_callback(app, "phys2d_body"))
    return LUB_ERROR;
  PhysWorld *w = check_world(app, world, "phys2d_body");
  if (!w)
    return LUB_ERROR;
  char kbuf[PHYS_KEY_MAX];
  if (!key_copy(app, key, kbuf, sizeof(kbuf), "phys2d_body"))
    return LUB_ERROR;
  if (!desc)
    return lub_api_fail(app, "phys2d_body: desc required");
  if (desc->type < LUB_PHYS2D_BODY_TYPE_STATIC ||
      desc->type > LUB_PHYS2D_BODY_TYPE_DYNAMIC)
    return lub_api_fail(app, "phys2d_body: unknown body type %d",
                        (int)desc->type);
  if (desc->has_sleep_threshold && desc->sleep_threshold < 0.0f)
    return lub_api_fail(app, "phys2d_body: sleep_threshold must be >= 0");
  if (!w->begun)
    return lub_api_fail(app, "phys2d_body: call phys2d_begin(world) first");
  PhysBody *b = body_get_or_create(w, kbuf);
  if (!b)
    return lub_api_fail(app, "phys2d_body: out of memory");
  uint64_t constructor_hash = body_constructor_hash(desc);
  int64_t version =
      desc->has_version ? (int64_t)desc->version : (int64_t)constructor_hash;
  if (B2_IS_NULL(b->id) || !b2Body_IsValid(b->id) || b->version != version) {
    if (B2_IS_NON_NULL(b->id) && b2Body_IsValid(b->id)) {
      world_remove_joints_for_body(w, b, true);
      b2DestroyBody(b->id);
      body_free_shapes(b, false);
      body_free_chains(b, false);
      b->id = b2_nullBodyId;
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

LubHandle lub_phys2d_body_find(LubContext *ctx, LubHandle world, LubStr key) {
  App *app = lub_api_app(ctx);
  PhysWorld *w = world_from_handle(app, world);
  char kbuf[PHYS_KEY_MAX];
  if (!w || !key.ptr || key.len <= 0 || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return 0;
  PhysBody *b = body_get(w, kbuf);
  return b ? b->handle : 0;
}

// ----------------------------------------------------------------- shape

static void shape_apply_density_default(PhysBody *body,
                                        LubPhys2dShapeDesc *desc) {
  if (!body || !desc || desc->has_density || !body_is_live(body))
    return;
  desc->density = b2Body_GetType(body->id) == b2_dynamicBody ? 1.0f : 0.0f;
}

static b2ShapeDef make_shape_def(const LubPhys2dShapeDesc *desc,
                                 PhysShape *shape) {
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
  def.filter.categoryBits = desc->filter.category_bits;
  def.filter.maskBits = desc->filter.mask_bits;
  def.filter.groupIndex = desc->filter.group_index;
  return def;
}

static uint64_t shape_base_hash(const LubPhys2dShapeDesc *desc, int kind) {
  uint64_t h = phys_hash_init();
  h = phys_hash_u64(h, (uint64_t)kind);
  h = phys_hash_bool(h, desc->sensor);
  h = phys_hash_u64(h, desc->filter.category_bits);
  h = phys_hash_u64(h, desc->filter.mask_bits);
  h = phys_hash_i64(h, desc->filter.group_index);
  return h;
}

static void shape_apply_runtime_desc(PhysShape *shape,
                                     const LubPhys2dShapeDesc *desc) {
  b2Shape_SetDensity(shape->id, desc->density, true);
  b2Shape_SetFriction(shape->id, desc->friction);
  b2Shape_SetRestitution(shape->id, desc->restitution);
  b2Shape_SetMaterial(shape->id, desc->material_id);
  b2Shape_EnableSensorEvents(shape->id, desc->sensor_events);
  b2Shape_EnableContactEvents(shape->id, desc->contact);
  b2Shape_EnablePreSolveEvents(shape->id, desc->pre_solve);
  b2Shape_EnableHitEvents(shape->id, desc->hit);
}

static int64_t shape_effective_version(const LubPhys2dShapeDesc *desc,
                                       uint64_t fallback_hash) {
  return desc->has_version ? (int64_t)desc->version : (int64_t)fallback_hash;
}

static void log_shape_constructor_drift(const char *fn, PhysShape *shape,
                                        uint64_t hash) {
  if (shape->constructor_hash == hash || shape->constructor_warned)
    return;
  SDL_Log("%s('%s/%s'): constructor fields changed without version bump", fn,
          shape->body ? shape->body->key : "?", shape->key);
  shape->constructor_warned = true;
}

static bool shape_update_metadata(App *app, PhysShape *shape,
                                  const LubPhys2dShapeDesc *desc) {
  if (!phys_owned_string_set(&shape->tag, desc->tag) ||
      !phys_owned_string_set(&shape->material_name, desc->material_name)) {
    lub_api_fail(app, "phys2d shape metadata: out of memory");
    return false;
  }
  shape->material_id = desc->material_id;
  return true;
}

static void shape_mark_declared(PhysShape *shape, int kind,
                                uint64_t fallback_hash,
                                const LubPhys2dShapeDesc *desc,
                                bool recreated) {
  if (recreated)
    shape->kind = kind;
  shape->desc_hash = (uint64_t)shape_effective_version(desc, fallback_hash);
  shape->constructor_hash = fallback_hash;
  if (recreated)
    shape->constructor_warned = false;
  shape->seen_generation = shape->body->world->generation;
  shape_tombstone_update_shape(shape);
}

// 共通の宣言手順。geometry_hash は kind ごとの constructor field の hash。
// create は recreated のときだけ呼ばれ、shape->id を作る。
typedef struct ShapeDeclare {
  const char *fn;
  int kind;
  LubHandle body;
  LubStr key;
  const LubPhys2dShapeDesc *desc;
  uint64_t geometry_hash;
  const void *geom;
  b2ShapeId (*create)(PhysBody *b, const b2ShapeDef *def, const void *geom);
} ShapeDeclare;

static LubStatus shape_declare(App *app, const ShapeDeclare *d,
                               LubHandle *out) {
  *out = 0;
  if (in_callback(app, d->fn))
    return LUB_ERROR;
  PhysBody *b = check_body(app, d->body, d->fn);
  if (!b)
    return LUB_ERROR;
  char kbuf[PHYS_KEY_MAX];
  if (!key_copy(app, d->key, kbuf, sizeof(kbuf), d->fn))
    return LUB_ERROR;
  LubPhys2dShapeDesc desc = *d->desc;
  shape_apply_density_default(b, &desc);
  uint64_t h = shape_base_hash(&desc, d->kind);
  h = phys_hash_u64(h, d->geometry_hash);
  int64_t version = shape_effective_version(&desc, h);
  PhysShape *shape = shape_get_or_create(b, kbuf);
  if (!shape)
    return lub_api_fail(app, "%s: out of memory", d->fn);
  bool recreated = B2_IS_NULL(shape->id) || !b2Shape_IsValid(shape->id) ||
                   shape->desc_hash != (uint64_t)version ||
                   (!desc.has_version && shape->kind != d->kind);
  if (recreated) {
    if (B2_IS_NON_NULL(shape->id) && b2Shape_IsValid(shape->id))
      b2DestroyShape(shape->id, true);
    b2ShapeDef def = make_shape_def(&desc, shape);
    shape->id = d->create(b, &def, d->geom);
    if (B2_IS_NULL(shape->id))
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

static b2ShapeId create_box(PhysBody *b, const b2ShapeDef *def,
                            const void *geom) {
  const LubPhys2dBoxDesc *g = (const LubPhys2dBoxDesc *)geom;
  b2Vec2 center = {g->cx, g->cy};
  b2Polygon polygon =
      (g->cx != 0.0f || g->cy != 0.0f || g->angle != 0.0f)
          ? b2MakeOffsetBox(g->hx, g->hy, center, b2MakeRot(g->angle))
          : b2MakeBox(g->hx, g->hy);
  return b2CreatePolygonShape(b->id, def, &polygon);
}

LubStatus lub_phys2d_box(LubContext *ctx, LubHandle body, LubStr key,
                         const LubPhys2dBoxDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys2d_box: desc required");
  if (desc->hx <= 0.0f || desc->hy <= 0.0f)
    return lub_api_fail(app, "phys2d_box: hx and hy must be > 0");
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, desc->hx);
  h = phys_hash_f32(h, desc->hy);
  h = phys_hash_f32(h, desc->cx);
  h = phys_hash_f32(h, desc->cy);
  h = phys_hash_f32(h, desc->angle);
  ShapeDeclare d = {
      "phys2d_box", LUB_PHYS2D_SHAPE_KIND_BOX, body, key, &desc->shape, h, desc,
      create_box};
  return shape_declare(app, &d, out);
}

static b2ShapeId create_circle(PhysBody *b, const b2ShapeDef *def,
                               const void *geom) {
  const LubPhys2dCircleDesc *g = (const LubPhys2dCircleDesc *)geom;
  b2Circle circle = {.center = {g->cx, g->cy}, .radius = g->r};
  return b2CreateCircleShape(b->id, def, &circle);
}

LubStatus lub_phys2d_circle(LubContext *ctx, LubHandle body, LubStr key,
                            const LubPhys2dCircleDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys2d_circle: desc required");
  if (desc->r <= 0.0f)
    return lub_api_fail(app, "phys2d_circle: r must be > 0");
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, desc->r);
  h = phys_hash_f32(h, desc->cx);
  h = phys_hash_f32(h, desc->cy);
  ShapeDeclare d = {"phys2d_circle",
                    LUB_PHYS2D_SHAPE_KIND_CIRCLE,
                    body,
                    key,
                    &desc->shape,
                    h,
                    desc,
                    create_circle};
  return shape_declare(app, &d, out);
}

static b2ShapeId create_capsule(PhysBody *b, const b2ShapeDef *def,
                                const void *geom) {
  const LubPhys2dCapsuleDesc *g = (const LubPhys2dCapsuleDesc *)geom;
  b2Capsule capsule = {
      .center1 = {g->ax, g->ay}, .center2 = {g->bx, g->by}, .radius = g->r};
  return b2CreateCapsuleShape(b->id, def, &capsule);
}

LubStatus lub_phys2d_capsule(LubContext *ctx, LubHandle body, LubStr key,
                             const LubPhys2dCapsuleDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys2d_capsule: desc required");
  float dx = desc->bx - desc->ax;
  float dy = desc->by - desc->ay;
  if (desc->r <= 0.0f)
    return lub_api_fail(app, "phys2d_capsule: r must be > 0");
  if (dx * dx + dy * dy <= 1e-12f)
    return lub_api_fail(app, "phys2d_capsule: endpoints must be distinct");
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, desc->ax);
  h = phys_hash_f32(h, desc->ay);
  h = phys_hash_f32(h, desc->bx);
  h = phys_hash_f32(h, desc->by);
  h = phys_hash_f32(h, desc->r);
  ShapeDeclare d = {"phys2d_capsule",
                    LUB_PHYS2D_SHAPE_KIND_CAPSULE,
                    body,
                    key,
                    &desc->shape,
                    h,
                    desc,
                    create_capsule};
  return shape_declare(app, &d, out);
}

static b2ShapeId create_segment(PhysBody *b, const b2ShapeDef *def,
                                const void *geom) {
  const LubPhys2dSegmentDesc *g = (const LubPhys2dSegmentDesc *)geom;
  b2Segment segment = {.point1 = {g->ax, g->ay}, .point2 = {g->bx, g->by}};
  return b2CreateSegmentShape(b->id, def, &segment);
}

LubStatus lub_phys2d_segment(LubContext *ctx, LubHandle body, LubStr key,
                             const LubPhys2dSegmentDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys2d_segment: desc required");
  float dx = desc->bx - desc->ax;
  float dy = desc->by - desc->ay;
  if (dx * dx + dy * dy <= 1e-12f)
    return lub_api_fail(app, "phys2d_segment: endpoints must be distinct");
  uint64_t h = phys_hash_init();
  h = phys_hash_f32(h, desc->ax);
  h = phys_hash_f32(h, desc->ay);
  h = phys_hash_f32(h, desc->bx);
  h = phys_hash_f32(h, desc->by);
  ShapeDeclare d = {"phys2d_segment",
                    LUB_PHYS2D_SHAPE_KIND_SEGMENT,
                    body,
                    key,
                    &desc->shape,
                    h,
                    desc,
                    create_segment};
  return shape_declare(app, &d, out);
}

typedef struct PolygonGeom {
  b2Hull hull;
  float radius;
  b2Vec2 center;
  float angle;
} PolygonGeom;

static b2ShapeId create_polygon(PhysBody *b, const b2ShapeDef *def,
                                const void *geom) {
  const PolygonGeom *g = (const PolygonGeom *)geom;
  b2Polygon polygon =
      (g->center.x != 0.0f || g->center.y != 0.0f || g->angle != 0.0f)
          ? (g->radius > 0.0f
                 ? b2MakeOffsetRoundedPolygon(&g->hull, g->center,
                                              b2MakeRot(g->angle), g->radius)
                 : b2MakeOffsetPolygon(&g->hull, g->center,
                                       b2MakeRot(g->angle)))
          : b2MakePolygon(&g->hull, g->radius);
  return b2CreatePolygonShape(b->id, def, &polygon);
}

static bool read_points(App *app, const float *points, int32_t count, int min,
                        int max, b2Vec2 *out, const char *fn) {
  if (!points || count < min) {
    lub_api_fail(app, "%s: at least %d points are required", fn, min);
    return false;
  }
  if (count > max) {
    lub_api_fail(app, "%s: at most %d points are supported", fn, max);
    return false;
  }
  for (int i = 0; i < count; ++i) {
    out[i].x = points[i * 2];
    out[i].y = points[i * 2 + 1];
  }
  return true;
}

LubStatus lub_phys2d_polygon(LubContext *ctx, LubHandle body, LubStr key,
                             const LubPhys2dPolygonDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!desc)
    return lub_api_fail(app, "phys2d_polygon: desc required");
  b2Vec2 points[B2_MAX_POLYGON_VERTICES];
  if (!read_points(app, desc->points, desc->point_count, 3,
                   B2_MAX_POLYGON_VERTICES, points, "phys2d_polygon"))
    return LUB_ERROR;
  if (desc->radius < 0.0f)
    return lub_api_fail(app, "phys2d_polygon: radius must be >= 0");
  PolygonGeom g;
  g.hull = b2ComputeHull(points, desc->point_count);
  if (g.hull.count < 3)
    return lub_api_fail(app, "phys2d_polygon: points must form a convex hull");
  g.radius = desc->radius;
  g.center = (b2Vec2){desc->cx, desc->cy};
  g.angle = desc->angle;
  uint64_t h = phys_hash_init();
  h = phys_hash_u64(h, (uint64_t)desc->point_count);
  for (int i = 0; i < desc->point_count; ++i) {
    h = phys_hash_f32(h, points[i].x);
    h = phys_hash_f32(h, points[i].y);
  }
  h = phys_hash_f32(h, desc->radius);
  h = phys_hash_f32(h, desc->cx);
  h = phys_hash_f32(h, desc->cy);
  h = phys_hash_f32(h, desc->angle);
  ShapeDeclare d = {"phys2d_polygon",
                    LUB_PHYS2D_SHAPE_KIND_POLYGON,
                    body,
                    key,
                    &desc->shape,
                    h,
                    &g,
                    create_polygon};
  return shape_declare(app, &d, out);
}

LubHandle lub_phys2d_shape_find(LubContext *ctx, LubHandle body, LubStr key) {
  App *app = lub_api_app(ctx);
  PhysBody *b = body_from_handle(app, body);
  char kbuf[PHYS_KEY_MAX];
  if (!b || !key.ptr || key.len <= 0 || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return 0;
  PhysShape *s = shape_get(b, kbuf);
  return s ? s->handle : 0;
}

// ----------------------------------------------------------------- chain

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

static uint64_t chain_constructor_hash(const b2Vec2 *points, int point_count,
                                       const LubPhys2dChainDesc *desc,
                                       const b2SurfaceMaterial *materials,
                                       int material_count, bool has_materials) {
  uint64_t h = phys_hash_init();
  h = phys_hash_u64(h, (uint64_t)point_count);
  for (int i = 0; i < point_count; ++i) {
    h = phys_hash_f32(h, points[i].x);
    h = phys_hash_f32(h, points[i].y);
  }
  h = phys_hash_bool(h, desc->loop);
  h = phys_hash_bool(h, desc->sensor_events);
  h = phys_hash_u64(h, desc->filter.category_bits);
  h = phys_hash_u64(h, desc->filter.mask_bits);
  h = phys_hash_i64(h, desc->filter.group_index);
  h = phys_hash_bool(h, has_materials);
  if (has_materials) {
    h = phys_hash_u64(h, (uint64_t)material_count);
    for (int i = 0; i < material_count; ++i) {
      h = phys_hash_f32(h, materials[i].friction);
      h = phys_hash_f32(h, materials[i].restitution);
      h = phys_hash_i64(h, materials[i].userMaterialId);
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

LubStatus lub_phys2d_chain(LubContext *ctx, LubHandle body, LubStr key,
                           const LubPhys2dChainDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (in_callback(app, "phys2d_chain"))
    return LUB_ERROR;
  PhysBody *b = check_body(app, body, "phys2d_chain");
  if (!b)
    return LUB_ERROR;
  char kbuf[PHYS_KEY_MAX];
  if (!key_copy(app, key, kbuf, sizeof(kbuf), "phys2d_chain"))
    return LUB_ERROR;
  if (!desc)
    return lub_api_fail(app, "phys2d_chain: desc required");
  int point_count = desc->point_count;
  if (!desc->points || point_count < 4)
    return lub_api_fail(app, "phys2d_chain: at least 4 points are required");
  bool has_materials = desc->materials != NULL && desc->material_count > 0;
  int material_count = has_materials ? desc->material_count : 1;
  if (has_materials && material_count != 1 && material_count != point_count)
    return lub_api_fail(
        app, "phys2d_chain: materials length must be 1 or point count");
  b2Vec2 *points = (b2Vec2 *)SDL_malloc(sizeof(*points) * point_count);
  b2SurfaceMaterial *materials =
      (b2SurfaceMaterial *)SDL_malloc(sizeof(*materials) * material_count);
  if (!points || !materials) {
    SDL_free(points);
    SDL_free(materials);
    return lub_api_fail(app, "phys2d_chain: out of memory");
  }
  for (int i = 0; i < point_count; ++i)
    points[i] = (b2Vec2){desc->points[i * 2], desc->points[i * 2 + 1]};
  for (int i = 0; i < material_count; ++i) {
    materials[i] = b2DefaultSurfaceMaterial();
    materials[i].friction = desc->friction;
    materials[i].restitution = desc->restitution;
    materials[i].userMaterialId = desc->material_id;
    if (has_materials) {
      materials[i].friction = desc->materials[i].friction;
      materials[i].restitution = desc->materials[i].restitution;
      materials[i].userMaterialId = desc->materials[i].material_id;
    }
  }
  int64_t version = desc->version;
  uint64_t constructor_hash = chain_constructor_hash(
      points, point_count, desc, materials, material_count, has_materials);

  PhysChain *chain = chain_get_or_create(b, kbuf);
  if (!chain) {
    SDL_free(points);
    SDL_free(materials);
    return lub_api_fail(app, "phys2d_chain: out of memory");
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
    def.isLoop = desc->loop;
    def.enableSensorEvents = desc->sensor_events;
    def.filter.categoryBits = desc->filter.category_bits;
    def.filter.maskBits = desc->filter.mask_bits;
    def.filter.groupIndex = desc->filter.group_index;
    chain->id = b2CreateChain(b->id, &def);
    if (B2_IS_NULL(chain->id)) {
      SDL_free(points);
      SDL_free(materials);
      return lub_api_fail(app, "phys2d_chain: b2CreateChain failed");
    }
    chain->version = version;
    chain->constructor_hash = constructor_hash;
    chain->constructor_warned = false;
  } else {
    log_chain_constructor_drift(chain, constructor_hash);
    chain->constructor_hash = constructor_hash;
    if (!has_materials) {
      b2Chain_SetFriction(chain->id, desc->friction);
      b2Chain_SetRestitution(chain->id, desc->restitution);
      b2Chain_SetMaterial(chain->id, desc->material_id);
    }
  }
  SDL_free(points);
  SDL_free(materials);
  if (!phys_owned_string_set(&chain->tag, desc->tag) ||
      !phys_owned_string_set(&chain->material_name, desc->material_name))
    return lub_api_fail(app, "phys2d_chain metadata: out of memory");
  chain->material_id = desc->material_id;
  chain_update_tombstones(chain);
  chain->seen_generation = b->world->generation;
  *out = chain->handle;
  return LUB_OK;
}

LubHandle lub_phys2d_chain_find(LubContext *ctx, LubHandle body, LubStr key) {
  App *app = lub_api_app(ctx);
  PhysBody *b = body_from_handle(app, body);
  char kbuf[PHYS_KEY_MAX];
  if (!b || !key.ptr || key.len <= 0 || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return 0;
  PhysChain *c = chain_get(b, kbuf);
  return c ? c->handle : 0;
}

LubStatus lub_phys2d_chain_segments(LubContext *ctx, LubHandle chain,
                                    const LubPhys2dShapePart **items,
                                    int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  PhysChain *c = query_chain(app, chain);
  if (!c)
    return LUB_NOT_FOUND;
  int capacity = b2Chain_GetSegmentCount(c->id);
  if (capacity <= 0)
    return LUB_OK;
  b2ShapeId *ids = (b2ShapeId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return lub_api_fail(app, "phys2d_chain_segments: out of memory");
  int n = b2Chain_GetSegments(c->id, ids, capacity);
  LubPhys2dShapePart *parts = (LubPhys2dShapePart *)scratch_alloc(
      phys_state(app), sizeof(LubPhys2dShapePart) * (size_t)(n > 0 ? n : 1));
  if (!parts) {
    SDL_free(ids);
    return lub_api_fail(app, "phys2d_chain_segments: out of memory");
  }
  for (int i = 0; i < n; ++i) {
    fill_shape_part(phys_state(app), &parts[i], ids[i]);
    parts[i].kind = LUB_PHYS2D_SHAPE_KIND_CHAIN_SEGMENT;
  }
  SDL_free(ids);
  *items = parts;
  *count = n;
  return LUB_OK;
}

// ----------------------------------------------------------------- joint

static const char *joint_kind_name(int kind) {
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

static b2Vec2 vec2_of(LubVec2 v) { return (b2Vec2){v.x, v.y}; }

static uint64_t joint_constructor_hash(const LubPhys2dJointDesc *desc,
                                       const PhysBody *a, const PhysBody *b,
                                       b2Vec2 axis) {
  uint64_t h = phys_hash_init();
  h = phys_hash_u64(h, (uint64_t)desc->type);
  h = phys_hash_cstr(h, a ? a->key : "");
  h = phys_hash_cstr(h, b ? b->key : "");
  h = phys_hash_f32(h, desc->local_anchor_a.x);
  h = phys_hash_f32(h, desc->local_anchor_a.y);
  h = phys_hash_f32(h, desc->local_anchor_b.x);
  h = phys_hash_f32(h, desc->local_anchor_b.y);
  h = phys_hash_f32(h, axis.x);
  h = phys_hash_f32(h, axis.y);
  h = phys_hash_f32(h, desc->reference_angle);
  h = phys_hash_bool(h, desc->collide_connected);
  return h;
}

static void log_joint_constructor_drift(PhysJoint *j, uint64_t hash) {
  if (j->constructor_hash == hash || j->constructor_warned)
    return;
  SDL_Log("phys2d_joint('%s'): constructor fields changed without version bump",
          j->key);
  j->constructor_warned = true;
}

static void joint_mark_declared(PhysJoint *j, const LubPhys2dJointDesc *desc,
                                PhysBody *a, PhysBody *b,
                                uint64_t constructor_hash, int64_t version,
                                bool recreated) {
  if (recreated) {
    j->kind = desc->type;
    j->body_a = a;
    j->body_b = b;
    j->constructor_warned = false;
  }
  j->version = version;
  j->constructor_hash = constructor_hash;
}

static void joint_apply_runtime(PhysJoint *j, const LubPhys2dJointDesc *desc) {
  switch (desc->type) {
  case LUB_PHYS2D_JOINT_TYPE_DISTANCE:
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
  case LUB_PHYS2D_JOINT_TYPE_MOTOR:
    b2MotorJoint_SetLinearOffset(j->id, vec2_of(desc->linear_offset));
    b2MotorJoint_SetAngularOffset(j->id, desc->angular_offset);
    b2MotorJoint_SetMaxForce(j->id, desc->max_force);
    b2MotorJoint_SetMaxTorque(j->id, desc->max_torque);
    b2MotorJoint_SetCorrectionFactor(j->id, desc->correction_factor);
    break;
  case LUB_PHYS2D_JOINT_TYPE_MOUSE:
    b2MouseJoint_SetTarget(j->id, vec2_of(desc->target));
    b2MouseJoint_SetSpringHertz(j->id, desc->hertz);
    b2MouseJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b2MouseJoint_SetMaxForce(j->id, desc->max_force);
    break;
  case LUB_PHYS2D_JOINT_TYPE_PRISMATIC:
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
  case LUB_PHYS2D_JOINT_TYPE_REVOLUTE:
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
  case LUB_PHYS2D_JOINT_TYPE_WELD:
    b2WeldJoint_SetLinearHertz(j->id, desc->linear_hertz);
    b2WeldJoint_SetLinearDampingRatio(j->id, desc->linear_damping_ratio);
    b2WeldJoint_SetAngularHertz(j->id, desc->angular_hertz);
    b2WeldJoint_SetAngularDampingRatio(j->id, desc->angular_damping_ratio);
    break;
  case LUB_PHYS2D_JOINT_TYPE_WHEEL:
    b2WheelJoint_EnableSpring(j->id, desc->enable_spring);
    b2WheelJoint_SetSpringHertz(j->id, desc->hertz);
    b2WheelJoint_SetSpringDampingRatio(j->id, desc->damping_ratio);
    b2WheelJoint_EnableLimit(j->id, desc->enable_limit);
    b2WheelJoint_SetLimits(j->id, desc->lower, desc->upper);
    b2WheelJoint_EnableMotor(j->id, desc->enable_motor);
    b2WheelJoint_SetMotorSpeed(j->id, desc->motor_speed);
    b2WheelJoint_SetMaxMotorTorque(j->id, desc->max_torque);
    break;
  case LUB_PHYS2D_JOINT_TYPE_FILTER:
  default:
    break;
  }
}

static bool joint_create(App *app, PhysWorld *w, PhysJoint *j,
                         const LubPhys2dJointDesc *desc, PhysBody *a,
                         PhysBody *b, b2Vec2 axis, uint64_t constructor_hash,
                         int64_t version) {
  b2Vec2 anchor_a = vec2_of(desc->local_anchor_a);
  b2Vec2 anchor_b = vec2_of(desc->local_anchor_b);
  switch (desc->type) {
  case LUB_PHYS2D_JOINT_TYPE_DISTANCE: {
    b2DistanceJointDef def = b2DefaultDistanceJointDef();
    def.bodyIdA = a->id;
    def.bodyIdB = b->id;
    def.localAnchorA = anchor_a;
    def.localAnchorB = anchor_b;
    def.length = desc->length;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateDistanceJoint(w->id, &def);
    break;
  }
  case LUB_PHYS2D_JOINT_TYPE_FILTER: {
    b2FilterJointDef def = b2DefaultFilterJointDef();
    def.bodyIdA = a->id;
    def.bodyIdB = b->id;
    def.userData = j;
    j->id = b2CreateFilterJoint(w->id, &def);
    break;
  }
  case LUB_PHYS2D_JOINT_TYPE_MOTOR: {
    b2MotorJointDef def = b2DefaultMotorJointDef();
    def.bodyIdA = a->id;
    def.bodyIdB = b->id;
    def.linearOffset = vec2_of(desc->linear_offset);
    def.angularOffset = desc->angular_offset;
    def.maxForce = desc->max_force;
    def.maxTorque = desc->max_torque;
    def.correctionFactor = desc->correction_factor;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateMotorJoint(w->id, &def);
    break;
  }
  case LUB_PHYS2D_JOINT_TYPE_MOUSE: {
    b2MouseJointDef def = b2DefaultMouseJointDef();
    def.bodyIdA = a->id;
    def.bodyIdB = b->id;
    def.target = vec2_of(desc->target);
    def.hertz = desc->hertz;
    def.dampingRatio = desc->damping_ratio;
    def.maxForce = desc->max_force;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateMouseJoint(w->id, &def);
    break;
  }
  case LUB_PHYS2D_JOINT_TYPE_PRISMATIC: {
    b2PrismaticJointDef def = b2DefaultPrismaticJointDef();
    def.bodyIdA = a->id;
    def.bodyIdB = b->id;
    def.localAnchorA = anchor_a;
    def.localAnchorB = anchor_b;
    def.localAxisA = axis;
    def.referenceAngle = desc->reference_angle;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreatePrismaticJoint(w->id, &def);
    break;
  }
  case LUB_PHYS2D_JOINT_TYPE_REVOLUTE: {
    b2RevoluteJointDef def = b2DefaultRevoluteJointDef();
    def.bodyIdA = a->id;
    def.bodyIdB = b->id;
    def.localAnchorA = anchor_a;
    def.localAnchorB = anchor_b;
    def.referenceAngle = desc->reference_angle;
    def.drawSize = desc->draw_size;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateRevoluteJoint(w->id, &def);
    break;
  }
  case LUB_PHYS2D_JOINT_TYPE_WELD: {
    b2WeldJointDef def = b2DefaultWeldJointDef();
    def.bodyIdA = a->id;
    def.bodyIdB = b->id;
    def.localAnchorA = anchor_a;
    def.localAnchorB = anchor_b;
    def.referenceAngle = desc->reference_angle;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateWeldJoint(w->id, &def);
    break;
  }
  case LUB_PHYS2D_JOINT_TYPE_WHEEL: {
    b2WheelJointDef def = b2DefaultWheelJointDef();
    def.bodyIdA = a->id;
    def.bodyIdB = b->id;
    def.localAnchorA = anchor_a;
    def.localAnchorB = anchor_b;
    def.localAxisA = axis;
    def.collideConnected = desc->collide_connected;
    def.userData = j;
    j->id = b2CreateWheelJoint(w->id, &def);
    break;
  }
  default:
    break;
  }
  if (B2_IS_NULL(j->id) || !b2Joint_IsValid(j->id)) {
    lub_api_fail(app, "phys2d_joint: b2Create%sJoint failed",
                 joint_kind_name(desc->type));
    return false;
  }
  b2Joint_SetUserData(j->id, j);
  joint_mark_declared(j, desc, a, b, constructor_hash, version, true);
  joint_apply_runtime(j, desc);
  return true;
}

static PhysBody *joint_body(App *app, PhysWorld *w, LubHandle h,
                            const char *field) {
  PhysBody *b = body_from_handle(app, h);
  if (!b) {
    lub_api_fail(app, "phys2d_joint: missing body field '%s'", field);
    return NULL;
  }
  if (b->world != w) {
    lub_api_fail(app, "phys2d_joint: body '%s' belongs to another world",
                 b->key);
    return NULL;
  }
  if (!body_is_live(b) || b->seen_generation != w->generation) {
    lub_api_fail(app, "phys2d_joint: declare live body '%s' before joint",
                 b->key);
    return NULL;
  }
  return b;
}

LubStatus lub_phys2d_joint(LubContext *ctx, LubHandle world, LubStr key,
                           const LubPhys2dJointDesc *desc, LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (in_callback(app, "phys2d_joint"))
    return LUB_ERROR;
  PhysWorld *w = check_world(app, world, "phys2d_joint");
  if (!w)
    return LUB_ERROR;
  char kbuf[PHYS_KEY_MAX];
  if (!key_copy(app, key, kbuf, sizeof(kbuf), "phys2d_joint"))
    return LUB_ERROR;
  if (!desc)
    return lub_api_fail(app, "phys2d_joint: desc required");
  if (desc->type < LUB_PHYS2D_JOINT_TYPE_DISTANCE ||
      desc->type > LUB_PHYS2D_JOINT_TYPE_WHEEL)
    return lub_api_fail(app, "phys2d_joint: unknown joint type %d",
                        (int)desc->type);
  PhysBody *a = joint_body(app, w, desc->body_a, "a");
  if (!a)
    return LUB_ERROR;
  PhysBody *b = joint_body(app, w, desc->body_b, "b");
  if (!b)
    return LUB_ERROR;
  b2Vec2 axis = vec2_of(desc->local_axis_a);
  if (axis.x * axis.x + axis.y * axis.y <= 1e-12f)
    return lub_api_fail(app, "phys2d_joint: local axis must be non-zero");
  axis = b2Normalize(axis);
  if (!w->begun)
    return lub_api_fail(app, "phys2d_joint: call phys2d_begin(world) first");
  PhysJoint *j = joint_get_or_create(w, kbuf);
  if (!j)
    return lub_api_fail(app, "phys2d_joint: out of memory");
  uint64_t constructor_hash = joint_constructor_hash(desc, a, b, axis);
  int64_t version =
      desc->has_version ? (int64_t)desc->version : (int64_t)constructor_hash;
  bool endpoints_changed = j->body_a != a || j->body_b != b;
  bool kind_changed = j->kind != desc->type;
  bool recreated = !joint_is_live(j) || j->version != version ||
                   (!desc->has_version && (kind_changed || endpoints_changed));
  if (recreated) {
    if (joint_is_live(j))
      b2DestroyJoint(j->id);
    j->id = b2_nullJointId;
    if (!joint_create(app, w, j, desc, a, b, axis, constructor_hash, version))
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

LubHandle lub_phys2d_joint_find(LubContext *ctx, LubHandle world, LubStr key) {
  App *app = lub_api_app(ctx);
  PhysWorld *w = world_from_handle(app, world);
  char kbuf[PHYS_KEY_MAX];
  if (!w || !key.ptr || key.len <= 0 || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return 0;
  PhysJoint *j = joint_get(w, kbuf);
  return j ? j->handle : 0;
}

static void fill_joint_view(LubPhys2dJointView *out, PhysJoint *j) {
  memset(out, 0, sizeof(*out));
  out->joint = j ? j->handle : 0;
  out->key = str_or_empty(j ? j->key : NULL);
  out->type = j ? j->kind : 0;
  out->a = str_or_empty(j && j->body_a ? j->body_a->key : NULL);
  out->b = str_or_empty(j && j->body_b ? j->body_b->key : NULL);
  out->valid = joint_is_live(j);
}

LubStatus lub_phys2d_joint_info(LubContext *ctx, LubHandle joint,
                                LubPhys2dJointInfo *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  fill_joint_view(&out->view, j);
  out->collide_connected = b2Joint_GetCollideConnected(j->id);
  b2Vec2 force = b2Joint_GetConstraintForce(j->id);
  out->force_x = force.x;
  out->force_y = force.y;
  out->torque = b2Joint_GetConstraintTorque(j->id);
  out->linear_separation = b2Joint_GetLinearSeparation(j->id);
  out->angular_separation = b2Joint_GetAngularSeparation(j->id);
  int k = j->kind;
  if (k == LUB_PHYS2D_JOINT_TYPE_DISTANCE ||
      k == LUB_PHYS2D_JOINT_TYPE_PRISMATIC ||
      k == LUB_PHYS2D_JOINT_TYPE_REVOLUTE || k == LUB_PHYS2D_JOINT_TYPE_WELD ||
      k == LUB_PHYS2D_JOINT_TYPE_WHEEL) {
    out->has_local_anchors = true;
    b2Vec2 a = b2Joint_GetLocalAnchorA(j->id);
    b2Vec2 b = b2Joint_GetLocalAnchorB(j->id);
    out->local_anchor_a = (LubVec2){a.x, a.y};
    out->local_anchor_b = (LubVec2){b.x, b.y};
  }
  if (k == LUB_PHYS2D_JOINT_TYPE_PRISMATIC ||
      k == LUB_PHYS2D_JOINT_TYPE_WHEEL) {
    out->has_local_axis = true;
    b2Vec2 ax = b2Joint_GetLocalAxisA(j->id);
    out->local_axis_a = (LubVec2){ax.x, ax.y};
  }
  if (k == LUB_PHYS2D_JOINT_TYPE_PRISMATIC ||
      k == LUB_PHYS2D_JOINT_TYPE_REVOLUTE || k == LUB_PHYS2D_JOINT_TYPE_WELD) {
    out->has_reference_angle = true;
    out->reference_angle = b2Joint_GetReferenceAngle(j->id);
  }
  return LUB_OK;
}

LubStatus lub_phys2d_joint_force(LubContext *ctx, LubHandle joint, float *x,
                                 float *y) {
  App *app = lub_api_app(ctx);
  *x = *y = 0;
  PhysJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  b2Vec2 f = b2Joint_GetConstraintForce(j->id);
  *x = f.x;
  *y = f.y;
  return LUB_OK;
}

LubStatus lub_phys2d_joint_torque(LubContext *ctx, LubHandle joint,
                                  float *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  PhysJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  *out = b2Joint_GetConstraintTorque(j->id);
  return LUB_OK;
}

#define JOINT_MEASURE(name, KIND, GET)                                         \
  LubStatus lub_phys2d_joint_##name(LubContext *ctx, LubHandle joint,          \
                                    float *out, bool *has) {                   \
    App *app = lub_api_app(ctx);                                               \
    *out = 0;                                                                  \
    *has = false;                                                              \
    PhysJoint *j = query_joint(app, joint);                                    \
    if (!j)                                                                    \
      return LUB_NOT_FOUND;                                                    \
    if (j->kind != KIND)                                                       \
      return LUB_OK;                                                           \
    *out = GET(j->id);                                                         \
    *has = true;                                                               \
    return LUB_OK;                                                             \
  }

JOINT_MEASURE(angle, LUB_PHYS2D_JOINT_TYPE_REVOLUTE, b2RevoluteJoint_GetAngle)
JOINT_MEASURE(translation, LUB_PHYS2D_JOINT_TYPE_PRISMATIC,
              b2PrismaticJoint_GetTranslation)
JOINT_MEASURE(speed, LUB_PHYS2D_JOINT_TYPE_PRISMATIC, b2PrismaticJoint_GetSpeed)
JOINT_MEASURE(length, LUB_PHYS2D_JOINT_TYPE_DISTANCE,
              b2DistanceJoint_GetCurrentLength)

LubStatus lub_phys2d_joint_motor_force(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has) {
  App *app = lub_api_app(ctx);
  *out = 0;
  *has = false;
  PhysJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  if (j->kind == LUB_PHYS2D_JOINT_TYPE_DISTANCE) {
    *out = b2DistanceJoint_GetMotorForce(j->id);
    *has = true;
  } else if (j->kind == LUB_PHYS2D_JOINT_TYPE_PRISMATIC) {
    *out = b2PrismaticJoint_GetMotorForce(j->id);
    *has = true;
  }
  return LUB_OK;
}

LubStatus lub_phys2d_joint_motor_torque(LubContext *ctx, LubHandle joint,
                                        float *out, bool *has) {
  App *app = lub_api_app(ctx);
  *out = 0;
  *has = false;
  PhysJoint *j = query_joint(app, joint);
  if (!j)
    return LUB_NOT_FOUND;
  if (j->kind == LUB_PHYS2D_JOINT_TYPE_REVOLUTE) {
    *out = b2RevoluteJoint_GetMotorTorque(j->id);
    *has = true;
  } else if (j->kind == LUB_PHYS2D_JOINT_TYPE_WHEEL) {
    *out = b2WheelJoint_GetMotorTorque(j->id);
    *has = true;
  }
  return LUB_OK;
}

static PhysJoint *check_live_joint(App *app, LubHandle h, const char *fn) {
  if (in_callback(app, fn))
    return NULL;
  PhysJoint *j = check_joint(app, h, fn);
  if (!j)
    return NULL;
  if (!joint_is_live(j)) {
    lub_api_fail(app, "%s: joint is not live", fn);
    return NULL;
  }
  return j;
}

LubStatus lub_phys2d_joint_set_motor(LubContext *ctx, LubHandle joint,
                                     const LubPhys2dJointMotor *d) {
  App *app = lub_api_app(ctx);
  PhysJoint *j = check_live_joint(app, joint, "phys2d_joint_set_motor");
  if (!j)
    return LUB_ERROR;
  switch (j->kind) {
  case LUB_PHYS2D_JOINT_TYPE_DISTANCE:
    b2DistanceJoint_EnableMotor(j->id, d->enabled);
    b2DistanceJoint_SetMotorSpeed(j->id, d->speed);
    b2DistanceJoint_SetMaxMotorForce(j->id, d->max_force);
    break;
  case LUB_PHYS2D_JOINT_TYPE_PRISMATIC:
    b2PrismaticJoint_EnableMotor(j->id, d->enabled);
    b2PrismaticJoint_SetMotorSpeed(j->id, d->speed);
    b2PrismaticJoint_SetMaxMotorForce(j->id, d->max_force);
    break;
  case LUB_PHYS2D_JOINT_TYPE_REVOLUTE:
    b2RevoluteJoint_EnableMotor(j->id, d->enabled);
    b2RevoluteJoint_SetMotorSpeed(j->id, d->speed);
    b2RevoluteJoint_SetMaxMotorTorque(j->id, d->max_torque);
    break;
  case LUB_PHYS2D_JOINT_TYPE_WHEEL:
    b2WheelJoint_EnableMotor(j->id, d->enabled);
    b2WheelJoint_SetMotorSpeed(j->id, d->speed);
    b2WheelJoint_SetMaxMotorTorque(j->id, d->max_torque);
    break;
  case LUB_PHYS2D_JOINT_TYPE_MOTOR:
    b2MotorJoint_SetMaxForce(j->id, d->max_force);
    b2MotorJoint_SetMaxTorque(j->id, d->max_torque);
    b2MotorJoint_SetCorrectionFactor(
        j->id, d->has_correction_factor
                   ? d->correction_factor
                   : b2MotorJoint_GetCorrectionFactor(j->id));
    break;
  default:
    return lub_api_fail(app, "phys2d_joint_set_motor: unsupported joint type");
  }
  b2Joint_WakeBodies(j->id);
  return LUB_OK;
}

LubStatus lub_phys2d_joint_set_limit(LubContext *ctx, LubHandle joint,
                                     const LubPhys2dJointLimit *d) {
  App *app = lub_api_app(ctx);
  PhysJoint *j = check_live_joint(app, joint, "phys2d_joint_set_limit");
  if (!j)
    return LUB_ERROR;
  switch (j->kind) {
  case LUB_PHYS2D_JOINT_TYPE_DISTANCE:
    b2DistanceJoint_EnableLimit(j->id, d->enabled);
    b2DistanceJoint_SetLengthRange(j->id, d->min_length, d->max_length);
    break;
  case LUB_PHYS2D_JOINT_TYPE_PRISMATIC:
    b2PrismaticJoint_EnableLimit(j->id, d->enabled);
    b2PrismaticJoint_SetLimits(j->id, d->lower, d->upper);
    break;
  case LUB_PHYS2D_JOINT_TYPE_REVOLUTE:
    b2RevoluteJoint_EnableLimit(j->id, d->enabled);
    b2RevoluteJoint_SetLimits(j->id, d->lower, d->upper);
    break;
  case LUB_PHYS2D_JOINT_TYPE_WHEEL:
    b2WheelJoint_EnableLimit(j->id, d->enabled);
    b2WheelJoint_SetLimits(j->id, d->lower, d->upper);
    break;
  default:
    return lub_api_fail(app, "phys2d_joint_set_limit: unsupported joint type");
  }
  b2Joint_WakeBodies(j->id);
  return LUB_OK;
}

LubStatus lub_phys2d_joint_set_spring(LubContext *ctx, LubHandle joint,
                                      const LubPhys2dJointSpring *d) {
  App *app = lub_api_app(ctx);
  PhysJoint *j = check_live_joint(app, joint, "phys2d_joint_set_spring");
  if (!j)
    return LUB_ERROR;
  switch (j->kind) {
  case LUB_PHYS2D_JOINT_TYPE_DISTANCE:
    b2DistanceJoint_EnableSpring(j->id, d->enabled);
    b2DistanceJoint_SetSpringHertz(j->id, d->hertz);
    b2DistanceJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    break;
  case LUB_PHYS2D_JOINT_TYPE_PRISMATIC:
    b2PrismaticJoint_EnableSpring(j->id, d->enabled);
    b2PrismaticJoint_SetSpringHertz(j->id, d->hertz);
    b2PrismaticJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    break;
  case LUB_PHYS2D_JOINT_TYPE_REVOLUTE:
    b2RevoluteJoint_EnableSpring(j->id, d->enabled);
    b2RevoluteJoint_SetSpringHertz(j->id, d->hertz);
    b2RevoluteJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    break;
  case LUB_PHYS2D_JOINT_TYPE_WHEEL:
    b2WheelJoint_EnableSpring(j->id, d->enabled);
    b2WheelJoint_SetSpringHertz(j->id, d->hertz);
    b2WheelJoint_SetSpringDampingRatio(j->id, d->damping_ratio);
    break;
  case LUB_PHYS2D_JOINT_TYPE_WELD:
    b2WeldJoint_SetLinearHertz(j->id, d->linear_hertz);
    b2WeldJoint_SetLinearDampingRatio(j->id, d->linear_damping_ratio);
    b2WeldJoint_SetAngularHertz(j->id, d->angular_hertz);
    b2WeldJoint_SetAngularDampingRatio(j->id, d->angular_damping_ratio);
    break;
  default:
    return lub_api_fail(app, "phys2d_joint_set_spring: unsupported joint type");
  }
  b2Joint_WakeBodies(j->id);
  return LUB_OK;
}

LubStatus lub_phys2d_joint_set_target(LubContext *ctx, LubHandle joint,
                                      const LubPhys2dJointTarget *d) {
  App *app = lub_api_app(ctx);
  PhysJoint *j = check_live_joint(app, joint, "phys2d_joint_set_target");
  if (!j)
    return LUB_ERROR;
  switch (j->kind) {
  case LUB_PHYS2D_JOINT_TYPE_MOUSE: {
    b2Vec2 target = b2MouseJoint_GetTarget(j->id);
    if (d->has_x)
      target.x = d->x;
    if (d->has_y)
      target.y = d->y;
    b2MouseJoint_SetTarget(j->id, target);
    break;
  }
  case LUB_PHYS2D_JOINT_TYPE_PRISMATIC:
    b2PrismaticJoint_SetTargetTranslation(
        j->id, d->has_translation
                   ? d->translation
                   : b2PrismaticJoint_GetTargetTranslation(j->id));
    break;
  case LUB_PHYS2D_JOINT_TYPE_REVOLUTE:
    b2RevoluteJoint_SetTargetAngle(
        j->id, d->has_angle ? d->angle : b2RevoluteJoint_GetTargetAngle(j->id));
    break;
  case LUB_PHYS2D_JOINT_TYPE_MOTOR: {
    b2Vec2 linear = b2MotorJoint_GetLinearOffset(j->id);
    if (d->has_linear_offset)
      linear = (b2Vec2){d->linear_offset_x, d->linear_offset_y};
    b2MotorJoint_SetLinearOffset(j->id, linear);
    b2MotorJoint_SetAngularOffset(
        j->id, d->has_angular_offset ? d->angular_offset
                                     : b2MotorJoint_GetAngularOffset(j->id));
    break;
  }
  default:
    return lub_api_fail(app, "phys2d_joint_set_target: unsupported joint type");
  }
  b2Joint_WakeBodies(j->id);
  return LUB_OK;
}

// -------------------------------------------------------------- commands

static PhysBody *check_live_body(App *app, LubHandle h, const char *fn) {
  if (in_callback(app, fn))
    return NULL;
  PhysBody *b = check_body(app, h, fn);
  if (!b)
    return NULL;
  if (!body_is_live(b)) {
    lub_api_fail(app, "%s: body is not live", fn);
    return NULL;
  }
  return b;
}

static PhysCommand *push_command(App *app, PhysBody *b, PhysCommandKind kind,
                                 const char *fn) {
  PhysCommand *cmd = command_queue_push(b->world, b, kind);
  if (!cmd)
    lub_api_fail(app, "%s: out of memory", fn);
  return cmd;
}

static LubStatus add_vector_command(LubContext *ctx, LubHandle body, float x,
                                    float y, const LubVec2 *point, bool wake,
                                    PhysCommandKind kind, const char *fn) {
  App *app = lub_api_app(ctx);
  PhysBody *b = check_live_body(app, body, fn);
  if (!b)
    return LUB_ERROR;
  PhysCommand *cmd = push_command(app, b, kind, fn);
  if (!cmd)
    return LUB_ERROR;
  cmd->vector = (b2Vec2){x, y};
  if (point) {
    cmd->point = (b2Vec2){point->x, point->y};
    cmd->has_point = true;
  }
  cmd->wake = wake;
  return LUB_OK;
}

LubStatus lub_phys2d_add_force(LubContext *ctx, LubHandle body, float fx,
                               float fy, const LubVec2 *point, bool wake) {
  return add_vector_command(ctx, body, fx, fy, point, wake,
                            PHYS_COMMAND_ADD_FORCE, "phys2d_add_force");
}

LubStatus lub_phys2d_add_force_center(LubContext *ctx, LubHandle body, float fx,
                                      float fy, bool wake) {
  return add_vector_command(ctx, body, fx, fy, NULL, wake,
                            PHYS_COMMAND_ADD_FORCE_CENTER,
                            "phys2d_add_force_center");
}

LubStatus lub_phys2d_add_impulse(LubContext *ctx, LubHandle body, float ix,
                                 float iy, const LubVec2 *point, bool wake) {
  return add_vector_command(ctx, body, ix, iy, point, wake,
                            PHYS_COMMAND_ADD_IMPULSE, "phys2d_add_impulse");
}

LubStatus lub_phys2d_add_impulse_center(LubContext *ctx, LubHandle body,
                                        float ix, float iy, bool wake) {
  return add_vector_command(ctx, body, ix, iy, NULL, wake,
                            PHYS_COMMAND_ADD_IMPULSE_CENTER,
                            "phys2d_add_impulse_center");
}

static LubStatus add_scalar_command(LubContext *ctx, LubHandle body, float v,
                                    bool wake, PhysCommandKind kind,
                                    const char *fn) {
  App *app = lub_api_app(ctx);
  PhysBody *b = check_live_body(app, body, fn);
  if (!b)
    return LUB_ERROR;
  PhysCommand *cmd = push_command(app, b, kind, fn);
  if (!cmd)
    return LUB_ERROR;
  cmd->scalar = v;
  cmd->wake = wake;
  return LUB_OK;
}

LubStatus lub_phys2d_add_torque(LubContext *ctx, LubHandle body, float torque,
                                bool wake) {
  return add_scalar_command(ctx, body, torque, wake, PHYS_COMMAND_ADD_TORQUE,
                            "phys2d_add_torque");
}

LubStatus lub_phys2d_add_angular_impulse(LubContext *ctx, LubHandle body,
                                         float impulse, bool wake) {
  return add_scalar_command(ctx, body, impulse, wake,
                            PHYS_COMMAND_ADD_ANGULAR_IMPULSE,
                            "phys2d_add_angular_impulse");
}

LubStatus lub_phys2d_set_velocity(LubContext *ctx, LubHandle body,
                                  const LubPhys2dSetVelocity *d) {
  App *app = lub_api_app(ctx);
  PhysBody *b = check_live_body(app, body, "phys2d_set_velocity");
  if (!b)
    return LUB_ERROR;
  PhysCommand *cmd =
      push_command(app, b, PHYS_COMMAND_SET_VELOCITY, "phys2d_set_velocity");
  if (!cmd)
    return LUB_ERROR;
  cmd->vector = (b2Vec2){d->vx, d->vy};
  cmd->has_x = d->has_vx;
  cmd->has_y = d->has_vy;
  cmd->scalar = d->w;
  cmd->has_w = d->has_w;
  cmd->wake = d->wake;
  return LUB_OK;
}

LubStatus lub_phys2d_teleport(LubContext *ctx, LubHandle body,
                              const LubPhys2dTeleport *d) {
  App *app = lub_api_app(ctx);
  PhysBody *b = check_live_body(app, body, "phys2d_teleport");
  if (!b)
    return LUB_ERROR;
  PhysCommand *cmd =
      push_command(app, b, PHYS_COMMAND_TELEPORT, "phys2d_teleport");
  if (!cmd)
    return LUB_ERROR;
  cmd->transform.p = (b2Vec2){d->x, d->y};
  cmd->has_x = d->has_x;
  cmd->has_y = d->has_y;
  cmd->scalar = d->angle;
  cmd->has_angle = d->has_angle;
  cmd->wake = d->wake;
  return LUB_OK;
}

LubStatus lub_phys2d_set_target(LubContext *ctx, LubHandle body,
                                const LubPhys2dSetTarget *d) {
  App *app = lub_api_app(ctx);
  PhysBody *b = check_live_body(app, body, "phys2d_set_target");
  if (!b)
    return LUB_ERROR;
  PhysCommand *cmd =
      push_command(app, b, PHYS_COMMAND_SET_TARGET, "phys2d_set_target");
  if (!cmd)
    return LUB_ERROR;
  cmd->transform.p = (b2Vec2){d->x, d->y};
  cmd->has_x = d->has_x;
  cmd->has_y = d->has_y;
  cmd->scalar = d->angle;
  cmd->has_angle = d->has_angle;
  cmd->time_step = d->time_step > 0.0f ? d->time_step : b->world->fixed_dt;
  cmd->wake = d->wake;
  return LUB_OK;
}

LubStatus lub_phys2d_set_mass_data(LubContext *ctx, LubHandle body,
                                   const LubPhys2dMassDataDesc *d, bool wake) {
  App *app = lub_api_app(ctx);
  PhysBody *b = check_live_body(app, body, "phys2d_set_mass_data");
  if (!b)
    return LUB_ERROR;
  if (d->mass < 0.0f)
    return lub_api_fail(app, "phys2d_set_mass_data: mass must be >= 0");
  if (d->inertia < 0.0f)
    return lub_api_fail(app, "phys2d_set_mass_data: inertia must be >= 0");
  PhysCommand *cmd =
      push_command(app, b, PHYS_COMMAND_SET_MASS_DATA, "phys2d_set_mass_data");
  if (!cmd)
    return LUB_ERROR;
  cmd->mass_data.mass = d->mass;
  cmd->mass_data.rotationalInertia = d->inertia;
  cmd->mass_data.center = (b2Vec2){d->center_x, d->center_y};
  cmd->wake = wake;
  return LUB_OK;
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

// ----------------------------------------------------------------- prune

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

// ---------------------------------------------------------- event capture

static void fill_shape_snapshot(PhysWorld *w, PhysContactSnapshot *out,
                                bool is_a, b2ShapeId shape_id) {
  bool valid = B2_IS_NON_NULL(shape_id) && b2Shape_IsValid(shape_id);
  PhysShape *shape = valid ? (PhysShape *)b2Shape_GetUserData(shape_id) : NULL;
  PhysChain *chain = NULL;
  if (valid && !shape)
    chain = chain_find_by_id(w->state, b2Shape_GetParentChain(shape_id));
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
    fill_shape_snapshot(w, dst, true, src->sensorShapeId);
    fill_shape_snapshot(w, dst, false, src->visitorShapeId);
  }
  for (int i = 0; i < ev.endCount; ++i) {
    b2SensorEndTouchEvent *src = &ev.endEvents[i];
    PhysContactSnapshot *dst =
        event_push(&w->events.sensor_ends, &w->events.sensor_end_count,
                   &w->events.sensor_end_cap);
    if (!dst)
      continue;
    fill_shape_snapshot(w, dst, true, src->sensorShapeId);
    fill_shape_snapshot(w, dst, false, src->visitorShapeId);
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

// Box2D は b2World_Step の冒頭でイベント配列をクリアする。
// 1フレームに複数固定ステップ回すときは各ステップ直後に回収しないと
// 最後のステップ以外のイベントが失われる。
static void capture_step_events(PhysWorld *w) {
  capture_contact_events(w);
  capture_sensor_events(w);
}

LubStatus lub_phys2d_step(LubContext *ctx, LubHandle world, float dt,
                          LubPhys2dStepInfo *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  if (in_callback(app, "phys2d_step"))
    return LUB_ERROR;
  PhysWorld *w = check_world(app, world, "phys2d_step");
  if (!w)
    return LUB_ERROR;
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
    callbacks_clear(w);
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
  return LUB_OK;
}

// ---------------------------------------------------------- body getters

LubStatus lub_phys2d_pose(LubContext *ctx, LubHandle body, LubPhys2dPose *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  b2Vec2 p = b2Body_GetPosition(b->id);
  b2Rot q = b2Body_GetRotation(b->id);
  b2Vec2 v = b2Body_GetLinearVelocity(b->id);
  out->x = p.x;
  out->y = p.y;
  out->angle = b2Rot_GetAngle(q);
  out->vx = v.x;
  out->vy = v.y;
  out->w = b2Body_GetAngularVelocity(b->id);
  out->awake = b2Body_IsAwake(b->id);
  out->enabled = b2Body_IsEnabled(b->id);
  out->sleep = b2Body_IsSleepEnabled(b->id);
  out->sleep_threshold = b2Body_GetSleepThreshold(b->id);
  return LUB_OK;
}

LubStatus lub_phys2d_velocity(LubContext *ctx, LubHandle body,
                              LubPhys2dVelocity *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  b2Vec2 v = b2Body_GetLinearVelocity(b->id);
  out->x = v.x;
  out->y = v.y;
  out->w = b2Body_GetAngularVelocity(b->id);
  return LUB_OK;
}

LubStatus lub_phys2d_mass(LubContext *ctx, LubHandle body,
                          LubPhys2dMassData *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  b2MassData md = b2Body_GetMassData(b->id);
  b2Vec2 center = b2Body_GetWorldCenterOfMass(b->id);
  out->mass = md.mass;
  out->inertia = md.rotationalInertia;
  out->center_x = center.x;
  out->center_y = center.y;
  out->local_center_x = md.center.x;
  out->local_center_y = md.center.y;
  return LUB_OK;
}

LubStatus lub_phys2d_center(LubContext *ctx, LubHandle body, float *x,
                            float *y) {
  App *app = lub_api_app(ctx);
  *x = *y = 0;
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  b2Vec2 c = b2Body_GetWorldCenterOfMass(b->id);
  *x = c.x;
  *y = c.y;
  return LUB_OK;
}

LubStatus lub_phys2d_world_point(LubContext *ctx, LubHandle body, float lx,
                                 float ly, float *x, float *y) {
  App *app = lub_api_app(ctx);
  *x = *y = 0;
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  b2Vec2 p = b2Body_GetWorldPoint(b->id, (b2Vec2){lx, ly});
  *x = p.x;
  *y = p.y;
  return LUB_OK;
}

LubStatus lub_phys2d_local_point(LubContext *ctx, LubHandle body, float wx,
                                 float wy, float *x, float *y) {
  App *app = lub_api_app(ctx);
  *x = *y = 0;
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  b2Vec2 p = b2Body_GetLocalPoint(b->id, (b2Vec2){wx, wy});
  *x = p.x;
  *y = p.y;
  return LUB_OK;
}

LubStatus lub_phys2d_velocity_at(LubContext *ctx, LubHandle body, float wx,
                                 float wy, float *x, float *y) {
  App *app = lub_api_app(ctx);
  *x = *y = 0;
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  b2Vec2 p = b2Body_GetWorldPointVelocity(b->id, (b2Vec2){wx, wy});
  *x = p.x;
  *y = p.y;
  return LUB_OK;
}

// ------------------------------------------------------------ shape parts

static LubPhys2dFilter filter_of(b2Filter f) {
  LubPhys2dFilter out = {f.categoryBits, f.maskBits, f.groupIndex};
  return out;
}

// live な shape id から識別を組む。chain segment は親 chain の識別になる。
static void fill_shape_part(PhysState *state, LubPhys2dShapePart *out,
                            b2ShapeId shape_id) {
  memset(out, 0, sizeof(*out));
  bool valid = B2_IS_NON_NULL(shape_id) && b2Shape_IsValid(shape_id);
  PhysShape *shape = valid ? (PhysShape *)b2Shape_GetUserData(shape_id) : NULL;
  PhysChain *chain = NULL;
  if (valid && !shape)
    chain = chain_find_by_id(state, b2Shape_GetParentChain(shape_id));
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
    out->shape = shape->handle;
    out->body = shape->body->handle;
    out->kind = shape->kind;
  } else if (chain && chain->body) {
    body_key = chain->body->key;
    shape_key = chain->key;
    chain_key = chain->key;
    tag = chain->tag;
    material_name = chain->material_name;
    out->shape = chain->handle;
    out->body = chain->body->handle;
    out->kind = LUB_PHYS2D_SHAPE_KIND_CHAIN_SEGMENT;
  }
  out->body_key = str_or_empty(body_key);
  out->shape_key = str_or_empty(shape_key);
  out->chain_key = str_or_empty(chain_key);
  out->tag = str_or_empty(tag);
  out->material_name = str_or_empty(material_name);
  if (valid) {
    out->material_id = b2Shape_GetMaterial(shape_id);
    out->has_material = true;
    out->has_filter = true;
    out->filter = filter_of(b2Shape_GetFilter(shape_id));
  }
  out->valid = valid && ((shape && shape->body && shape->key) ||
                         (chain && chain->body && chain->key));
}

// step の snapshot から識別を組む (filter 無し)。
static void fill_snapshot_part(LubPhys2dShapePart *out, const char *body,
                               const char *shape, const char *tag,
                               const char *material_name, int material_id,
                               bool valid) {
  memset(out, 0, sizeof(*out));
  out->body_key = str_or_empty(body);
  out->shape_key = str_or_empty(shape);
  out->chain_key = str_or_empty(NULL);
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

static bool phys2d_custom_filter_callback(b2ShapeId shape_id_a,
                                          b2ShapeId shape_id_b, void *context) {
  PhysWorld *w = (PhysWorld *)context;
  if (!w || !w->callbacks.filter)
    return true;
  LubPhys2dShapePart a, b;
  fill_shape_part(w->state, &a, shape_id_a);
  fill_shape_part(w->state, &b, shape_id_b);
  w->state->callback_depth++;
  bool collide = w->callbacks.filter(w->callbacks.user, &a, &b);
  w->state->callback_depth--;
  return collide;
}

static void fill_manifold_point(LubPhys2dManifoldPoint *out,
                                const b2ManifoldPoint *p) {
  out->x = p->point.x;
  out->y = p->point.y;
  out->anchor_a_x = p->anchorA.x;
  out->anchor_a_y = p->anchorA.y;
  out->anchor_b_x = p->anchorB.x;
  out->anchor_b_y = p->anchorB.y;
  out->separation = p->separation;
  out->normal_impulse = p->normalImpulse;
  out->tangent_impulse = p->tangentImpulse;
  out->total_normal_impulse = p->totalNormalImpulse;
  out->normal_velocity = p->normalVelocity;
  out->id = (int32_t)p->id;
  out->persisted = p->persisted;
}

static bool phys2d_pre_solve_callback(b2ShapeId shape_id_a,
                                      b2ShapeId shape_id_b,
                                      b2Manifold *manifold, void *context) {
  PhysWorld *w = (PhysWorld *)context;
  if (!w || !w->callbacks.pre_solve)
    return true;
  LubPhys2dPreSolve c;
  memset(&c, 0, sizeof(c));
  fill_shape_part(w->state, &c.a, shape_id_a);
  fill_shape_part(w->state, &c.b, shape_id_b);
  int point_count = manifold ? manifold->pointCount : 0;
  if (point_count < 0)
    point_count = 0;
  if (point_count > 2)
    point_count = 2;
  c.nx = manifold ? manifold->normal.x : 0.0f;
  c.ny = manifold ? manifold->normal.y : 0.0f;
  c.rolling_impulse = manifold ? manifold->rollingImpulse : 0.0f;
  c.point_count = point_count;
  for (int i = 0; i < point_count; ++i)
    fill_manifold_point(&c.points[i], &manifold->points[i]);
  w->state->callback_depth++;
  bool solve = w->callbacks.pre_solve(w->callbacks.user, &c);
  w->state->callback_depth--;
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

static float phys2d_friction_callback(float friction_a, int material_a,
                                      float friction_b, int material_b) {
  float fallback = default_friction(friction_a, friction_b);
  PhysWorld *w = g_mixer_world;
  if (!w || !w->callbacks.friction)
    return fallback;
  w->state->callback_depth++;
  float out = w->callbacks.friction(w->callbacks.user, friction_a, material_a,
                                    friction_b, material_b);
  w->state->callback_depth--;
  return out;
}

static float phys2d_restitution_callback(float restitution_a, int material_a,
                                         float restitution_b, int material_b) {
  float fallback = default_restitution(restitution_a, restitution_b);
  PhysWorld *w = g_mixer_world;
  if (!w || !w->callbacks.restitution)
    return fallback;
  w->state->callback_depth++;
  float out = w->callbacks.restitution(w->callbacks.user, restitution_a,
                                       material_a, restitution_b, material_b);
  w->state->callback_depth--;
  return out;
}

// ---------------------------------------------------------- shape queries

LubStatus lub_phys2d_shape_test_point(LubContext *ctx, LubHandle shape, float x,
                                      float y, bool *out) {
  App *app = lub_api_app(ctx);
  *out = false;
  PhysShape *s = query_shape(app, shape);
  if (!s)
    return LUB_NOT_FOUND;
  *out = b2Shape_TestPoint(s->id, (b2Vec2){x, y});
  return LUB_OK;
}

LubStatus lub_phys2d_shape_raycast(LubContext *ctx, LubHandle shape,
                                   const LubPhys2dRay *ray,
                                   LubPhys2dRayHit *out, bool *hit) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  *hit = false;
  PhysShape *s = query_shape(app, shape);
  if (!s)
    return LUB_NOT_FOUND;
  b2RayCastInput input;
  input.origin = (b2Vec2){ray->x, ray->y};
  input.translation = (b2Vec2){ray->dx, ray->dy};
  input.maxFraction = ray->max_fraction < 0.0f ? 0.0f : ray->max_fraction;
  if (input.translation.x * input.translation.x +
          input.translation.y * input.translation.y <=
      1e-12f)
    return lub_api_fail(
        app, "phys2d_shape_raycast: ray translation must be non-zero");
  b2CastOutput r = b2Shape_RayCast(s->id, &input);
  if (!r.hit)
    return LUB_OK;
  *hit = true;
  out->x = r.point.x;
  out->y = r.point.y;
  out->nx = r.normal.x;
  out->ny = r.normal.y;
  out->fraction = r.fraction;
  out->iterations = r.iterations;
  return LUB_OK;
}

LubStatus lub_phys2d_shape_closest_point(LubContext *ctx, LubHandle shape,
                                         float x, float y, float *ox,
                                         float *oy) {
  App *app = lub_api_app(ctx);
  *ox = *oy = 0;
  PhysShape *s = query_shape(app, shape);
  if (!s)
    return LUB_NOT_FOUND;
  b2Vec2 p = b2Shape_GetClosestPoint(s->id, (b2Vec2){x, y});
  *ox = p.x;
  *oy = p.y;
  return LUB_OK;
}

static LubPhys2dAabb aabb_of(b2AABB a) {
  LubPhys2dAabb out = {a.lowerBound.x, a.lowerBound.y, a.upperBound.x,
                       a.upperBound.y};
  return out;
}

LubStatus lub_phys2d_shape_aabb(LubContext *ctx, LubHandle shape,
                                LubPhys2dAabb *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysShape *s = query_shape(app, shape);
  if (!s)
    return LUB_NOT_FOUND;
  *out = aabb_of(b2Shape_GetAABB(s->id));
  return LUB_OK;
}

LubStatus lub_phys2d_shape_info(LubContext *ctx, LubHandle shape,
                                LubPhys2dShapeInfo *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysShape *s = query_shape(app, shape);
  if (!s)
    return LUB_NOT_FOUND;
  fill_shape_part(phys_state(app), &out->part, s->id);
  out->part.kind = s->kind;
  out->density = b2Shape_GetDensity(s->id);
  out->friction = b2Shape_GetFriction(s->id);
  out->restitution = b2Shape_GetRestitution(s->id);
  out->sensor = b2Shape_IsSensor(s->id);
  out->sensor_events = b2Shape_AreSensorEventsEnabled(s->id);
  out->contact = b2Shape_AreContactEventsEnabled(s->id);
  out->pre_solve = b2Shape_ArePreSolveEventsEnabled(s->id);
  out->hit = b2Shape_AreHitEventsEnabled(s->id);
  out->aabb = aabb_of(b2Shape_GetAABB(s->id));
  return LUB_OK;
}

static PhysShape *check_live_shape(App *app, LubHandle h, const char *fn) {
  if (in_callback(app, fn))
    return NULL;
  PhysShape *s = check_shape(app, h, fn);
  if (!s)
    return NULL;
  if (!shape_is_live(s)) {
    lub_api_fail(app, "%s: shape is not live", fn);
    return NULL;
  }
  return s;
}

LubStatus lub_phys2d_shape_set_material(LubContext *ctx, LubHandle shape,
                                        const LubPhys2dMaterialDesc *d) {
  App *app = lub_api_app(ctx);
  PhysShape *s = check_live_shape(app, shape, "phys2d_shape_set_material");
  if (!s)
    return LUB_ERROR;
  if (d->has_density)
    b2Shape_SetDensity(s->id, d->density, true);
  if (d->has_friction)
    b2Shape_SetFriction(s->id, d->friction);
  if (d->has_restitution)
    b2Shape_SetRestitution(s->id, d->restitution);
  if (d->has_material_id) {
    b2Shape_SetMaterial(s->id, d->material_id);
    s->material_id = d->material_id;
  }
  if (d->has_material_name &&
      !phys_owned_string_set(&s->material_name, d->material_name))
    return lub_api_fail(app, "phys2d_shape_set_material: out of memory");
  shape_tombstone_update_shape(s);
  return LUB_OK;
}

LubStatus lub_phys2d_shape_set_filter(LubContext *ctx, LubHandle shape,
                                      const LubPhys2dFilter *filter) {
  App *app = lub_api_app(ctx);
  PhysShape *s = check_live_shape(app, shape, "phys2d_shape_set_filter");
  if (!s)
    return LUB_ERROR;
  b2Filter f = b2Shape_GetFilter(s->id);
  f.categoryBits = filter->category_bits;
  f.maskBits = filter->mask_bits;
  f.groupIndex = filter->group_index;
  b2Shape_SetFilter(s->id, f);
  shape_tombstone_update_shape(s);
  return LUB_OK;
}

LubStatus lub_phys2d_shape_set_events(LubContext *ctx, LubHandle shape,
                                      const LubPhys2dEventFlags *flags) {
  App *app = lub_api_app(ctx);
  PhysShape *s = check_live_shape(app, shape, "phys2d_shape_set_events");
  if (!s)
    return LUB_ERROR;
  if (flags->has_sensor_events)
    b2Shape_EnableSensorEvents(s->id, flags->sensor_events);
  if (flags->has_contact)
    b2Shape_EnableContactEvents(s->id, flags->contact);
  if (flags->has_pre_solve)
    b2Shape_EnablePreSolveEvents(s->id, flags->pre_solve);
  if (flags->has_hit)
    b2Shape_EnableHitEvents(s->id, flags->hit);
  shape_tombstone_update_shape(s);
  return LUB_OK;
}

// ------------------------------------------------------------- body lists

LubStatus lub_phys2d_body_shapes(LubContext *ctx, LubHandle body,
                                 const LubPhys2dShapePart **items,
                                 int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  int capacity = b2Body_GetShapeCount(b->id);
  if (capacity <= 0)
    return LUB_OK;
  b2ShapeId *ids = (b2ShapeId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return lub_api_fail(app, "phys2d_body_shapes: out of memory");
  int n = b2Body_GetShapes(b->id, ids, capacity);
  LubPhys2dShapePart *parts = (LubPhys2dShapePart *)scratch_alloc(
      phys_state(app), sizeof(LubPhys2dShapePart) * (size_t)(n > 0 ? n : 1));
  if (!parts) {
    SDL_free(ids);
    return lub_api_fail(app, "phys2d_body_shapes: out of memory");
  }
  for (int i = 0; i < n; ++i)
    fill_shape_part(phys_state(app), &parts[i], ids[i]);
  SDL_free(ids);
  *items = parts;
  *count = n;
  return LUB_OK;
}

LubStatus lub_phys2d_body_joints(LubContext *ctx, LubHandle body,
                                 const LubPhys2dJointView **items,
                                 int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  int capacity = b2Body_GetJointCount(b->id);
  if (capacity <= 0)
    return LUB_OK;
  b2JointId *ids = (b2JointId *)SDL_malloc(sizeof(*ids) * capacity);
  if (!ids)
    return lub_api_fail(app, "phys2d_body_joints: out of memory");
  int n = b2Body_GetJoints(b->id, ids, capacity);
  LubPhys2dJointView *views = (LubPhys2dJointView *)scratch_alloc(
      phys_state(app), sizeof(LubPhys2dJointView) * (size_t)(n > 0 ? n : 1));
  if (!views) {
    SDL_free(ids);
    return lub_api_fail(app, "phys2d_body_joints: out of memory");
  }
  for (int i = 0; i < n; ++i) {
    PhysJoint *j = B2_IS_NON_NULL(ids[i]) && b2Joint_IsValid(ids[i])
                       ? (PhysJoint *)b2Joint_GetUserData(ids[i])
                       : NULL;
    fill_joint_view(&views[i], j);
  }
  SDL_free(ids);
  *items = views;
  *count = n;
  return LUB_OK;
}

LubStatus lub_phys2d_body_contacts(LubContext *ctx, LubHandle body,
                                   const LubPhys2dContactData **items,
                                   int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  PhysBody *b = query_body(app, body);
  if (!b)
    return LUB_NOT_FOUND;
  int capacity = b2Body_GetContactCapacity(b->id);
  if (capacity <= 0)
    return LUB_OK;
  b2ContactData *data = (b2ContactData *)SDL_malloc(sizeof(*data) * capacity);
  if (!data)
    return lub_api_fail(app, "phys2d_body_contacts: out of memory");
  int n = b2Body_GetContactData(b->id, data, capacity);
  LubPhys2dContactData *out = (LubPhys2dContactData *)scratch_alloc(
      phys_state(app), sizeof(LubPhys2dContactData) * (size_t)(n > 0 ? n : 1));
  if (!out) {
    SDL_free(data);
    return lub_api_fail(app, "phys2d_body_contacts: out of memory");
  }
  for (int i = 0; i < n; ++i) {
    fill_shape_part(phys_state(app), &out[i].a, data[i].shapeIdA);
    fill_shape_part(phys_state(app), &out[i].b, data[i].shapeIdB);
    out[i].nx = data[i].manifold.normal.x;
    out[i].ny = data[i].manifold.normal.y;
    out[i].point_count = data[i].manifold.pointCount;
    if (data[i].manifold.pointCount > 0) {
      out[i].x = data[i].manifold.points[0].point.x;
      out[i].y = data[i].manifold.points[0].point.y;
      out[i].separation = data[i].manifold.points[0].separation;
    }
  }
  SDL_free(data);
  *items = out;
  *count = n;
  return LUB_OK;
}

// ------------------------------------------------------------ step events

static LubStatus contact_list(App *app, PhysContactSnapshot *src, int n,
                              const LubPhys2dContact **items, int32_t *count) {
  LubPhys2dContact *out = (LubPhys2dContact *)scratch_alloc(
      phys_state(app), sizeof(LubPhys2dContact) * (size_t)(n > 0 ? n : 1));
  if (!out)
    return lub_api_fail(app, "phys2d_contacts: out of memory");
  for (int i = 0; i < n; ++i) {
    PhysContactSnapshot *e = &src[i];
    fill_snapshot_part(&out[i].a, e->a_body, e->a_shape, e->a_tag,
                       e->a_material, e->a_material_id, e->a_valid);
    fill_snapshot_part(&out[i].b, e->b_body, e->b_shape, e->b_tag,
                       e->b_material, e->b_material_id, e->b_valid);
    out[i].nx = e->nx;
    out[i].ny = e->ny;
    out[i].point_count = e->point_count;
    out[i].x = e->x;
    out[i].y = e->y;
    out[i].approach_speed = e->approach_speed;
  }
  *items = out;
  *count = n;
  return LUB_OK;
}

LubStatus lub_phys2d_contacts(LubContext *ctx, LubHandle world, int32_t kind,
                              const LubPhys2dContact **items, int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  switch (kind) {
  case LUB_PHYS2D_EVENT_KIND_BEGIN:
    return contact_list(app, w->events.begins, w->events.begin_count, items,
                        count);
  case LUB_PHYS2D_EVENT_KIND_END:
    return contact_list(app, w->events.ends, w->events.end_count, items, count);
  case LUB_PHYS2D_EVENT_KIND_HIT:
    return contact_list(app, w->events.hits, w->events.hit_count, items, count);
  default:
    return lub_api_fail(app,
                        "phys2d_contacts: kind must be begin, end, or hit");
  }
}

LubStatus lub_phys2d_sensors(LubContext *ctx, LubHandle world, int32_t kind,
                             const LubPhys2dContact **items, int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  switch (kind) {
  case LUB_PHYS2D_EVENT_KIND_BEGIN:
    return contact_list(app, w->events.sensor_begins,
                        w->events.sensor_begin_count, items, count);
  case LUB_PHYS2D_EVENT_KIND_END:
    return contact_list(app, w->events.sensor_ends, w->events.sensor_end_count,
                        items, count);
  default:
    return lub_api_fail(app, "phys2d_sensors: kind must be begin or end");
  }
}

LubStatus lub_phys2d_body_events(LubContext *ctx, LubHandle world,
                                 const LubPhys2dBodyEvent **items,
                                 int32_t *count) {
  App *app = lub_api_app(ctx);
  *items = NULL;
  *count = 0;
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  int n = w->events.move_count;
  LubPhys2dBodyEvent *out = (LubPhys2dBodyEvent *)scratch_alloc(
      phys_state(app), sizeof(LubPhys2dBodyEvent) * (size_t)(n > 0 ? n : 1));
  if (!out)
    return lub_api_fail(app, "phys2d_body_events: out of memory");
  for (int i = 0; i < n; ++i) {
    PhysBodyEventSnapshot *e = &w->events.moves[i];
    out[i].body = str_or_empty(e->body);
    out[i].valid = e->valid;
    out[i].x = e->x;
    out[i].y = e->y;
    out[i].angle = e->angle;
    out[i].fell_asleep = e->fell_asleep;
  }
  *items = out;
  *count = n;
  return LUB_OK;
}

// ---------------------------------------------------------- world queries

static b2QueryFilter query_filter_of(const LubPhys2dQueryFilter *f) {
  b2QueryFilter out = b2DefaultQueryFilter();
  if (f) {
    out.categoryBits = f->category_bits;
    out.maskBits = f->mask_bits;
  }
  return out;
}

// world raycast の translation は max_fraction を掛けたもの。
static bool ray_translation(App *app, const LubPhys2dRay *ray, b2Vec2 *out,
                            const char *fn) {
  float mf = ray->max_fraction < 0.0f ? 0.0f : ray->max_fraction;
  out->x = ray->dx * mf;
  out->y = ray->dy * mf;
  if (out->x * out->x + out->y * out->y <= 1e-12f) {
    lub_api_fail(app, "%s: ray translation must be non-zero", fn);
    return false;
  }
  return true;
}

typedef struct QueryCtx {
  PhysState *state;
  LubPhys2dOverlapFn overlap;
  LubPhys2dRayFn ray;
  LubPhys2dPlaneFn plane;
  void *user;
} QueryCtx;

static bool overlap_trampoline(b2ShapeId shape_id, void *context) {
  QueryCtx *q = (QueryCtx *)context;
  LubPhys2dShapePart part;
  fill_shape_part(q->state, &part, shape_id);
  q->state->callback_depth++;
  bool keep = q->overlap(q->user, &part);
  q->state->callback_depth--;
  return keep;
}

static float ray_trampoline(b2ShapeId shape_id, b2Vec2 point, b2Vec2 normal,
                            float fraction, void *context) {
  QueryCtx *q = (QueryCtx *)context;
  LubPhys2dRayHit hit;
  memset(&hit, 0, sizeof(hit));
  fill_shape_part(q->state, &hit.shape, shape_id);
  hit.x = point.x;
  hit.y = point.y;
  hit.nx = normal.x;
  hit.ny = normal.y;
  hit.fraction = fraction;
  q->state->callback_depth++;
  float r = q->ray(q->user, &hit);
  q->state->callback_depth--;
  return r;
}

static LubPhys2dTreeStats stats_of(b2TreeStats s) {
  LubPhys2dTreeStats out = {s.nodeVisits, s.leafVisits};
  return out;
}

LubStatus lub_phys2d_raycast_closest(LubContext *ctx, LubHandle world,
                                     const LubPhys2dRay *ray,
                                     const LubPhys2dQueryFilter *filter,
                                     LubPhys2dRayHit *out, bool *hit) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  *hit = false;
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b2Vec2 translation;
  if (!ray_translation(app, ray, &translation, "phys2d_raycast"))
    return LUB_ERROR;
  b2RayResult r = b2World_CastRayClosest(w->id, (b2Vec2){ray->x, ray->y},
                                         translation, query_filter_of(filter));
  if (!r.hit)
    return LUB_OK;
  *hit = true;
  fill_shape_part(phys_state(app), &out->shape, r.shapeId);
  out->x = r.point.x;
  out->y = r.point.y;
  out->nx = r.normal.x;
  out->ny = r.normal.y;
  out->fraction = r.fraction;
  out->node_visits = r.nodeVisits;
  out->leaf_visits = r.leafVisits;
  return LUB_OK;
}

LubStatus lub_phys2d_raycast(LubContext *ctx, LubHandle world,
                             const LubPhys2dRay *ray,
                             const LubPhys2dQueryFilter *filter,
                             LubPhys2dRayFn fn, void *user,
                             LubPhys2dTreeStats *stats) {
  App *app = lub_api_app(ctx);
  memset(stats, 0, sizeof(*stats));
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b2Vec2 translation;
  if (!ray_translation(app, ray, &translation, "phys2d_raycast"))
    return LUB_ERROR;
  if (!fn)
    return lub_api_fail(app, "phys2d_raycast: visitor required");
  QueryCtx q = {phys_state(app), NULL, fn, NULL, user};
  b2TreeStats s = b2World_CastRay(w->id, (b2Vec2){ray->x, ray->y}, translation,
                                  query_filter_of(filter), ray_trampoline, &q);
  *stats = stats_of(s);
  return LUB_OK;
}

LubStatus lub_phys2d_overlap_aabb(LubContext *ctx, LubHandle world,
                                  const LubPhys2dAabb *aabb,
                                  const LubPhys2dQueryFilter *filter,
                                  LubPhys2dOverlapFn fn, void *user,
                                  LubPhys2dTreeStats *stats) {
  App *app = lub_api_app(ctx);
  memset(stats, 0, sizeof(*stats));
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  if (aabb->min_x > aabb->max_x || aabb->min_y > aabb->max_y)
    return lub_api_fail(app, "phys2d_overlap_aabb: min must be <= max");
  if (!fn)
    return lub_api_fail(app, "phys2d_overlap_aabb: visitor required");
  b2AABB box = {{aabb->min_x, aabb->min_y}, {aabb->max_x, aabb->max_y}};
  QueryCtx q = {phys_state(app), fn, NULL, NULL, user};
  b2TreeStats s = b2World_OverlapAABB(w->id, box, query_filter_of(filter),
                                      overlap_trampoline, &q);
  *stats = stats_of(s);
  return LUB_OK;
}

static bool make_proxy(App *app, const LubPhys2dShapeProxy *p,
                       b2ShapeProxy *out) {
  b2Vec2 position = {p->x, p->y};
  b2Rot rot = b2MakeRot(p->angle);
  switch (p->kind) {
  case LUB_PHYS2D_PROXY_KIND_CIRCLE: {
    if (p->r <= 0.0f) {
      lub_api_fail(app, "phys2d_shape_cast: circle r must be > 0");
      return false;
    }
    b2Vec2 center = {p->cx, p->cy};
    *out = b2MakeOffsetProxy(&center, 1, p->r, position, rot);
    return true;
  }
  case LUB_PHYS2D_PROXY_KIND_CAPSULE:
  case LUB_PHYS2D_PROXY_KIND_SEGMENT: {
    bool capsule = p->kind == LUB_PHYS2D_PROXY_KIND_CAPSULE;
    if (capsule && p->r <= 0.0f) {
      lub_api_fail(app, "phys2d_shape_cast: capsule r must be > 0");
      return false;
    }
    b2Vec2 points[2] = {{p->ax, p->ay}, {p->bx, p->by}};
    float dx = points[1].x - points[0].x;
    float dy = points[1].y - points[0].y;
    if (dx * dx + dy * dy <= 1e-12f) {
      lub_api_fail(app, "phys2d_shape_cast: %s endpoints must be distinct",
                   capsule ? "capsule" : "segment");
      return false;
    }
    *out = b2MakeOffsetProxy(points, 2, capsule ? p->r : 0.0f, position, rot);
    return true;
  }
  case LUB_PHYS2D_PROXY_KIND_BOX: {
    if (p->hx <= 0.0f || p->hy <= 0.0f) {
      lub_api_fail(app, "phys2d_shape_cast: box hx and hy must be > 0");
      return false;
    }
    if (p->r < 0.0f) {
      lub_api_fail(app, "phys2d_shape_cast: box radius must be >= 0");
      return false;
    }
    b2Vec2 points[4] = {{p->cx - p->hx, p->cy - p->hy},
                        {p->cx + p->hx, p->cy - p->hy},
                        {p->cx + p->hx, p->cy + p->hy},
                        {p->cx - p->hx, p->cy + p->hy}};
    *out = b2MakeOffsetProxy(points, 4, p->r, position, rot);
    return true;
  }
  case LUB_PHYS2D_PROXY_KIND_POLYGON: {
    b2Vec2 points[B2_MAX_POLYGON_VERTICES];
    if (!read_points(app, p->points, p->point_count, 3, B2_MAX_POLYGON_VERTICES,
                     points, "phys2d_shape_cast"))
      return false;
    b2Hull hull = b2ComputeHull(points, p->point_count);
    if (hull.count < 3) {
      lub_api_fail(app, "phys2d_shape_cast: points must form a convex hull");
      return false;
    }
    if (p->r < 0.0f) {
      lub_api_fail(app, "phys2d_shape_cast: polygon radius must be >= 0");
      return false;
    }
    *out = b2MakeOffsetProxy(hull.points, hull.count, p->r, position, rot);
    return true;
  }
  default:
    lub_api_fail(app, "phys2d_shape_cast: unknown shape type %d", (int)p->kind);
    return false;
  }
}

LubStatus lub_phys2d_shape_cast(LubContext *ctx, LubHandle world,
                                const LubPhys2dShapeProxy *proxy, float dx,
                                float dy, const LubPhys2dQueryFilter *filter,
                                LubPhys2dRayFn fn, void *user,
                                LubPhys2dTreeStats *stats) {
  App *app = lub_api_app(ctx);
  memset(stats, 0, sizeof(*stats));
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b2ShapeProxy p;
  if (!make_proxy(app, proxy, &p))
    return LUB_ERROR;
  if (dx * dx + dy * dy <= 1e-12f)
    return lub_api_fail(app, "phys2d_shape_cast: translation must be non-zero");
  if (!fn)
    return lub_api_fail(app, "phys2d_shape_cast: visitor required");
  QueryCtx q = {phys_state(app), NULL, fn, NULL, user};
  b2TreeStats s = b2World_CastShape(
      w->id, &p, (b2Vec2){dx, dy}, query_filter_of(filter), ray_trampoline, &q);
  *stats = stats_of(s);
  return LUB_OK;
}

static bool mover_of(App *app, const LubPhys2dMover *m, b2Capsule *out,
                     const char *fn) {
  out->center1 = (b2Vec2){m->ax, m->ay};
  out->center2 = (b2Vec2){m->bx, m->by};
  out->radius = m->r;
  float dx = out->center2.x - out->center1.x;
  float dy = out->center2.y - out->center1.y;
  if (dx * dx + dy * dy <= 1e-12f) {
    lub_api_fail(app, "%s: mover endpoints must be distinct", fn);
    return false;
  }
  if (out->radius <= 0.011f) {
    lub_api_fail(app, "%s: mover radius must be > 0.011", fn);
    return false;
  }
  return true;
}

LubStatus lub_phys2d_cast_mover(LubContext *ctx, LubHandle world,
                                const LubPhys2dMover *mover, float dx, float dy,
                                const LubPhys2dQueryFilter *filter,
                                float *fraction) {
  App *app = lub_api_app(ctx);
  *fraction = 0;
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b2Capsule capsule;
  if (!mover_of(app, mover, &capsule, "phys2d_cast_mover"))
    return LUB_ERROR;
  if (dx * dx + dy * dy <= 1e-12f)
    return lub_api_fail(app, "phys2d_cast_mover: translation must be non-zero");
  *fraction = b2World_CastMover(w->id, &capsule, (b2Vec2){dx, dy},
                                query_filter_of(filter));
  return LUB_OK;
}

static bool plane_trampoline(b2ShapeId shape_id, const b2PlaneResult *plane,
                             void *context) {
  QueryCtx *q = (QueryCtx *)context;
  LubPhys2dMoverPlane p;
  memset(&p, 0, sizeof(p));
  fill_shape_part(q->state, &p.shape, shape_id);
  p.hit = plane->hit;
  p.x = plane->point.x;
  p.y = plane->point.y;
  p.nx = plane->plane.normal.x;
  p.ny = plane->plane.normal.y;
  p.offset = plane->plane.offset;
  q->state->callback_depth++;
  bool keep = q->plane(q->user, &p);
  q->state->callback_depth--;
  return keep;
}

LubStatus lub_phys2d_collide_mover(LubContext *ctx, LubHandle world,
                                   const LubPhys2dMover *mover,
                                   const LubPhys2dQueryFilter *filter,
                                   LubPhys2dPlaneFn fn, void *user) {
  App *app = lub_api_app(ctx);
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b2Capsule capsule;
  if (!mover_of(app, mover, &capsule, "phys2d_collide_mover"))
    return LUB_ERROR;
  if (!fn)
    return lub_api_fail(app, "phys2d_collide_mover: visitor required");
  QueryCtx q = {phys_state(app), NULL, NULL, fn, user};
  b2World_CollideMover(w->id, &capsule, query_filter_of(filter),
                       plane_trampoline, &q);
  return LUB_OK;
}

LubStatus lub_phys2d_explode(LubContext *ctx, LubHandle world,
                             const LubPhys2dExplosionDesc *desc) {
  App *app = lub_api_app(ctx);
  if (in_callback(app, "phys2d_explode"))
    return LUB_ERROR;
  PhysWorld *w = check_world(app, world, "phys2d_explode");
  if (!w)
    return LUB_ERROR;
  if (desc->radius < 0.0f)
    return lub_api_fail(app, "phys2d_explode: radius must be >= 0");
  if (desc->falloff < 0.0f)
    return lub_api_fail(app, "phys2d_explode: falloff must be >= 0");
  b2ExplosionDef def = b2DefaultExplosionDef();
  def.position = (b2Vec2){desc->x, desc->y};
  def.radius = desc->radius;
  def.falloff = desc->falloff;
  def.impulsePerLength = desc->impulse_per_length;
  def.maskBits = desc->mask_bits;
  b2World_Explode(w->id, &def);
  return LUB_OK;
}

// ----------------------------------------------------------------- debug

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
  debug_push_polygon((PhysDebugBuffer *)context, b2Transform_identity, vertices,
                     vertex_count, false, color);
}

static void debug_draw_solid_polygon(b2Transform transform,
                                     const b2Vec2 *vertices, int vertex_count,
                                     float radius, b2HexColor color,
                                     void *context) {
  (void)radius;
  debug_push_polygon((PhysDebugBuffer *)context, transform, vertices,
                     vertex_count, true, color);
}

static void debug_draw_circle(b2Vec2 center, float radius, b2HexColor color,
                              void *context) {
  debug_push_circle((PhysDebugBuffer *)context, center, radius, color, 1.0f);
}

static void debug_draw_solid_circle(b2Transform transform, float radius,
                                    b2HexColor color, void *context) {
  debug_push_circle((PhysDebugBuffer *)context, transform.p, radius, color,
                    0.55f);
}

static void debug_draw_solid_capsule(b2Vec2 p1, b2Vec2 p2, float radius,
                                     b2HexColor color, void *context) {
  debug_push_capsule((PhysDebugBuffer *)context, p1, p2, radius, color, 0.55f);
}

static void debug_draw_segment(b2Vec2 p1, b2Vec2 p2, b2HexColor color,
                               void *context) {
  debug_push_segment((PhysDebugBuffer *)context, p1, p2, color);
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
  debug_push_point((PhysDebugBuffer *)context, p, size, color);
}

LubStatus lub_phys2d_debug(LubContext *ctx, LubHandle world,
                           const LubPhys2dDebugDesc *desc,
                           LubPhys2dDebugData *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
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
  if (desc) {
    draw.drawShapes = desc->shapes;
    draw.drawJoints = desc->joints;
    draw.drawJointExtras = desc->joint_extras;
    draw.drawBounds = desc->bounds;
    draw.drawMass = desc->mass;
    draw.drawBodyNames = desc->body_names;
    draw.drawContacts = desc->contacts;
    draw.drawGraphColors = desc->graph_colors;
    draw.drawContactNormals = desc->contact_normals;
    draw.drawContactImpulses = desc->contact_impulses;
    draw.drawContactFeatures = desc->contact_features;
    draw.drawFrictionImpulses = desc->friction_impulses;
    draw.drawIslands = desc->islands;
    if (desc->has_drawing_bounds) {
      draw.drawingBounds.lowerBound =
          (b2Vec2){desc->drawing_bounds.min_x, desc->drawing_bounds.min_y};
      draw.drawingBounds.upperBound =
          (b2Vec2){desc->drawing_bounds.max_x, desc->drawing_bounds.max_y};
      draw.useDrawingBounds = true;
    }
  }
  PhysDebugBuffer *buffer = &phys_state(app)->debug;
  buffer->segments.count = 0;
  buffer->circles.count = 0;
  buffer->capsules.count = 0;
  buffer->polygons.count = 0;
  buffer->points.count = 0;
  buffer->failed = false;
  draw.context = buffer;
  b2World_Draw(w->id, &draw);
  if (buffer->failed)
    return lub_api_fail(app, "phys2d_debug: out of memory");
  out->segments = buffer->segments.items;
  out->segment_count = buffer->segments.count;
  out->circles = buffer->circles.items;
  out->circle_count = buffer->circles.count;
  out->capsules = buffer->capsules.items;
  out->capsule_count = buffer->capsules.count;
  out->polygons = buffer->polygons.items;
  out->polygon_count = buffer->polygons.count;
  out->points = buffer->points.items;
  out->point_count = buffer->points.count;
  return LUB_OK;
}

LubStatus lub_phys2d_profile(LubContext *ctx, LubHandle world,
                             LubPhys2dProfile *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b2Profile p = b2World_GetProfile(w->id);
  out->step = p.step;
  out->pairs = p.pairs;
  out->collide = p.collide;
  out->solve = p.solve;
  out->merge_islands = p.mergeIslands;
  out->prepare_stages = p.prepareStages;
  out->solve_constraints = p.solveConstraints;
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
  out->hit_events = p.hitEvents;
  out->refit = p.refit;
  out->bullets = p.bullets;
  out->sleep_islands = p.sleepIslands;
  out->sensors = p.sensors;
  return LUB_OK;
}

LubStatus lub_phys2d_counters(LubContext *ctx, LubHandle world,
                              LubPhys2dCounters *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  PhysWorld *w = query_world(app, world);
  if (!w)
    return LUB_NOT_FOUND;
  b2Counters c = b2World_GetCounters(w->id);
  out->body_count = c.bodyCount;
  out->shape_count = c.shapeCount;
  out->contact_count = c.contactCount;
  out->joint_count = c.jointCount;
  out->island_count = c.islandCount;
  out->stack_used = c.stackUsed;
  out->static_tree_height = c.staticTreeHeight;
  out->tree_height = c.treeHeight;
  out->byte_count = c.byteCount;
  out->task_count = c.taskCount;
  for (int i = 0; i < 12; ++i)
    out->color_counts[i] = c.colorCounts[i];
  return LUB_OK;
}
