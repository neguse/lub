// phys2d の core が使う内部の型。段階 3 の手書き header の形をそのまま内部に
// 移したもので、公開 API (include/lub/lub_api.h、生成物) との詰め替えは
// physics_box2d.c の末尾で行う。
#pragma once
#include "lub/lub_api.h"
#include <stdbool.h>
#include <stdint.h>

enum {
  P2_PROXY_CIRCLE = 1,
  P2_PROXY_CAPSULE = 2,
  P2_PROXY_SEGMENT = 3,
  P2_PROXY_BOX = 4,
  P2_PROXY_POLYGON = 5
};

// ---------------------------------------------------------------- phys2d
// Box2D の即時モード API。world / body / shape / chain / joint は key で毎
// フレーム宣言し、begin から step までに宣言されなかったものは step で prune
// される (begin の prune=false で抑止)。handle は entry の寿命に結ぶ:
// prune されるまで同じ値で、prune 後は stale (find で引き直す)。
// filter の bit は Box2D の 64 bit mask をそのまま持つ (Lua 面は hex 文字列、
// C# は ulong)。
//
// callback (world desc の callbacks、query の visitor) は呼び出し元と同じ
// thread で呼ばれ、渡す struct は呼び出しの間だけ有効。callback の中から
// 物理を変える API を呼ぶと LUB_ERROR。

typedef struct P2Filter {
  uint64_t category_bits; // 既定 1
  uint64_t mask_bits;     // 既定 全 bit
  int32_t group_index;
} P2Filter;

// shape の識別。event / query / callback が返す。文字列は runtime が持ち、
// その shape (か tombstone) が生きている間有効。
typedef struct P2ShapePart {
  LubHandle shape; // shape か chain の handle。0 = 無し
  LubHandle body;
  LubStr body_key;
  LubStr shape_key;
  LubStr chain_key; // len 0 = chain segment ではない
  LubStr tag;
  LubStr material_name;
  int32_t material_id;
  int32_t kind; // P2ShapeKind。0 = 不明
  bool valid;   // shape が live
  bool has_material;
  bool has_filter;
  P2Filter filter;
} P2ShapePart;

typedef struct P2ManifoldPoint {
  float x, y;
  float anchor_a_x, anchor_a_y;
  float anchor_b_x, anchor_b_y;
  float separation;
  float normal_impulse, tangent_impulse, total_normal_impulse;
  float normal_velocity;
  int32_t id;
  bool persisted;
} P2ManifoldPoint;

typedef struct P2PreSolve {
  P2ShapePart a, b;
  float nx, ny;
  float rolling_impulse;
  int32_t point_count; // 0..2
  P2ManifoldPoint points[2];
} P2PreSolve;

// 全部 NULL = callback 無し。生存期間は次の world 宣言か step まで。
typedef struct P2Callbacks {
  void *user;
  void (*release)(void *user); // core が callback を手放すとき (NULL 可)
  bool (*filter)(void *user, const P2ShapePart *a, const P2ShapePart *b);
  bool (*pre_solve)(void *user, const P2PreSolve *contact);
  float (*friction)(void *user, float friction_a, int32_t material_a,
                    float friction_b, int32_t material_b);
  float (*restitution)(void *user, float restitution_a, int32_t material_a,
                       float restitution_b, int32_t material_b);
} P2Callbacks;

typedef struct P2WorldDesc {
  bool has_version;
  int32_t version;
  float gravity_x, gravity_y; // 既定 (0, -9.8)
  float fixed_dt;             // 既定 1/60
  int32_t substeps;           // 既定 4
  int32_t max_steps;          // 既定 4
  bool sleep;                 // 既定 true
  bool continuous;            // 既定 true
  bool has_hit_event_threshold;
  float hit_event_threshold;
  P2Callbacks callbacks;
} P2WorldDesc;

typedef struct P2BodyDesc {
  bool has_version;
  int32_t version;
  int32_t type; // P2BodyType
  bool fixed_rotation;
  bool bullet;
  bool has_enabled, enabled;
  bool has_awake, awake;
  bool has_sleep, sleep;
  float gravity_scale; // 既定 1
  float linear_damping, angular_damping;
  bool has_sleep_threshold;
  float sleep_threshold;
  // initial (再生成のときだけ使う)
  float x, y, angle;
  float vx, vy, w;
  bool initial_awake; // 既定 true
} P2BodyDesc;

typedef struct P2ShapeDesc {
  bool has_version;
  int32_t version;
  bool has_density;
  float density;  // 既定 dynamic 1 / それ以外 0
  float friction; // 既定 0.6
  float restitution;
  int32_t material_id;
  LubStr material_name; // len 0 = 無し
  LubStr tag;           // len 0 = 無し
  bool sensor, contact, hit, sensor_events, pre_solve;
  P2Filter filter;
} P2ShapeDesc;

typedef struct P2BoxDesc {
  P2ShapeDesc shape;
  float hx, hy, cx, cy, angle;
} P2BoxDesc;

typedef struct P2CircleDesc {
  P2ShapeDesc shape;
  float r, cx, cy;
} P2CircleDesc;

typedef struct P2CapsuleDesc {
  P2ShapeDesc shape;
  float ax, ay, bx, by, r;
} P2CapsuleDesc;

typedef struct P2SegmentDesc {
  P2ShapeDesc shape;
  float ax, ay, bx, by;
} P2SegmentDesc;

typedef struct P2PolygonDesc {
  P2ShapeDesc shape;
  const float *points; // x, y の組。3..8 点
  int32_t point_count;
  float radius, cx, cy, angle;
} P2PolygonDesc;

typedef struct P2SurfaceMaterial {
  float friction, restitution;
  int32_t material_id;
} P2SurfaceMaterial;

typedef struct P2ChainDesc {
  int32_t version;     // 必須 (chain は version 無しの宣言を受けない)
  const float *points; // x, y の組。4 点以上
  int32_t point_count;
  bool loop;
  float friction; // 既定 0.6
  float restitution;
  int32_t material_id;
  LubStr material_name;
  LubStr tag;
  bool sensor_events;
  P2Filter filter;
  const P2SurfaceMaterial *materials; // NULL = 無し
  int32_t material_count;             // 1 か point_count
} P2ChainDesc;

typedef struct P2JointDesc {
  bool has_version;
  int32_t version;
  int32_t type; // P2JointType
  LubHandle body_a, body_b;
  LubVec2d local_anchor_a, local_anchor_b;
  LubVec2d local_axis_a; // 既定 (1, 0)
  LubVec2d linear_offset;
  LubVec2d target;
  float reference_angle;
  float length;     // 既定 1
  float min_length; // 既定 0
  float max_length; // 既定 1
  float lower;      // 既定 0
  float upper;      // 既定 1
  float target_angle, target_translation, angular_offset;
  float hertz, damping_ratio;
  float linear_hertz, angular_hertz;
  float linear_damping_ratio, angular_damping_ratio;
  float max_force;  // 既定 1
  float max_torque; // 既定 1
  float motor_speed;
  float correction_factor; // 既定 0.3
  float draw_size;         // 既定 0.25
  bool collide_connected, enable_spring, enable_limit, enable_motor;
} P2JointDesc;

// desc に既定値を入れる (callbacks / 文字列は空)。

typedef struct P2WorldInfo {
  LubStr key;
  bool valid;
  int32_t version;
  int32_t generation;
  bool begun, prune;
  float fixed_dt;
  int32_t substeps, max_steps;
  float accumulator;
  int32_t pending_commands;
  bool callback_filter, callback_pre_solve, callback_friction,
      callback_restitution;
  // 以下は valid のとき
  float gravity_x, gravity_y;
  bool sleep, continuous, warm_starting;
  float restitution_threshold, hit_event_threshold, maximum_linear_speed;
  int32_t awake_body_count;
} P2WorldInfo;

typedef struct P2StepInfo {
  int32_t steps;
  int32_t commands;
  float alpha;
  bool dropped;
  int32_t contact_begins, contact_ends, contact_hits;
  int32_t sensor_begins, sensor_ends;
  int32_t body_moves;
} P2StepInfo;

typedef struct P2Pose {
  float x, y, angle;
  float vx, vy, w;
  bool awake, enabled, sleep;
  float sleep_threshold;
} P2Pose;

typedef struct P2Velocity {
  float x, y, w;
} P2Velocity;

typedef struct P2MassData {
  float mass, inertia;
  float center_x, center_y; // world
  float local_center_x, local_center_y;
} P2MassData;

typedef struct P2Aabb {
  float min_x, min_y, max_x, max_y;
} P2Aabb;

typedef struct P2ShapeInfo {
  P2ShapePart part;
  float density, friction, restitution;
  bool sensor, sensor_events, contact, pre_solve, hit;
  P2Aabb aabb;
} P2ShapeInfo;

typedef struct P2JointView {
  LubHandle joint;
  LubStr key;
  int32_t type; // P2JointType。0 = 不明
  LubStr a, b;  // body の key
  bool valid;
} P2JointView;

typedef struct P2JointInfo {
  P2JointView view;
  bool collide_connected;
  float force_x, force_y, torque;
  float linear_separation, angular_separation;
  bool has_local_anchors;
  LubVec2d local_anchor_a, local_anchor_b;
  bool has_local_axis;
  LubVec2d local_axis_a;
  bool has_reference_angle;
  float reference_angle;
} P2JointInfo;

// step が集めた contact / sensor event。a / b の filter は無い。
typedef struct P2Contact {
  P2ShapePart a, b; // sensor event では a = sensor、b = visitor
  float nx, ny;
  int32_t point_count;
  float x, y;
  float approach_speed; // hit のとき
} P2Contact;

typedef struct P2BodyEvent {
  LubStr body;
  bool valid;
  float x, y, angle;
  bool fell_asleep;
} P2BodyEvent;

// body に今触れている contact (live)。
typedef struct P2ContactData {
  P2ShapePart a, b;
  float nx, ny;
  int32_t point_count;
  float x, y, separation;
} P2ContactData;

typedef struct P2Ray {
  float x, y;         // origin
  float dx, dy;       // translation
  float max_fraction; // 既定 1
} P2Ray;

typedef struct P2RayHit {
  P2ShapePart shape; // world query のとき
  float x, y, nx, ny, fraction;
  int32_t iterations;               // shape raycast のとき
  int32_t node_visits, leaf_visits; // raycast_closest のとき
} P2RayHit;

typedef struct P2TreeStats {
  int32_t node_visits, leaf_visits;
} P2TreeStats;

typedef struct P2QueryFilter {
  uint64_t category_bits; // 既定 1
  uint64_t mask_bits;     // 既定 全 bit
} P2QueryFilter;

typedef struct P2ShapeProxy {
  int32_t kind;         // P2ProxyKind
  float x, y, angle;    // 配置
  float r;              // circle / capsule / polygon / box の丸め
  float cx, cy;         // circle / box の中心
  float ax, ay, bx, by; // capsule / segment
  float hx, hy;         // box
  const float *points;  // polygon (x, y の組)
  int32_t point_count;
} P2ShapeProxy;

typedef struct P2Mover {
  float ax, ay, bx, by, r;
} P2Mover;

typedef struct P2MoverPlane {
  P2ShapePart shape;
  bool hit;
  float x, y, nx, ny, offset;
} P2MoverPlane;

typedef struct P2ExplosionDesc {
  float x, y;
  float radius, falloff, impulse_per_length;
  uint64_t mask_bits; // 既定 全 bit
} P2ExplosionDesc;

typedef struct P2DebugDesc {
  bool shapes; // 既定 true
  bool joints, joint_extras, bounds, mass, body_names, contacts, graph_colors,
      contact_normals, contact_impulses, contact_features, friction_impulses,
      islands;
  bool has_drawing_bounds;
  P2Aabb drawing_bounds;
} P2DebugDesc;

// 平らな float 配列。次の lub_phys2d_debug まで有効。色は r g b a。
typedef struct P2DebugData {
  const float *segments; // x1 y1 x2 y2 + 色
  int32_t segment_count; // float の個数
  const float *circles;  // cx cy r + 色
  int32_t circle_count;
  const float *capsules; // x1 y1 x2 y2 r + 色
  int32_t capsule_count;
  const float *polygons; // n solid + 色 + x0 y0 ... (n 点)
  int32_t polygon_count;
  const float *points; // x y size + 色
  int32_t point_count;
} P2DebugData;

typedef struct P2Profile {
  float step, pairs, collide, solve, merge_islands, prepare_stages,
      solve_constraints, prepare_constraints, integrate_velocities, warm_start,
      solve_impulses, integrate_positions, relax_impulses, apply_restitution,
      store_impulses, split_islands, transforms, hit_events, refit, bullets,
      sleep_islands, sensors;
} P2Profile;

typedef struct P2Counters {
  int32_t body_count, shape_count, contact_count, joint_count, island_count,
      stack_used, static_tree_height, tree_height, byte_count, task_count;
  int32_t color_counts[12];
} P2Counters;

typedef struct P2SetVelocity {
  bool has_vx, has_vy, has_w;
  float vx, vy, w;
  bool wake;
} P2SetVelocity;

typedef struct P2Teleport {
  bool has_x, has_y, has_angle;
  float x, y, angle;
  bool wake;
} P2Teleport;

typedef struct P2SetTarget {
  bool has_x, has_y, has_angle;
  float x, y, angle;
  float time_step; // <= 0 で world の fixed_dt
  bool wake;
} P2SetTarget;

typedef struct P2MassDataDesc {
  float mass, inertia;
  float center_x, center_y; // local
} P2MassDataDesc;

typedef struct P2JointMotor {
  bool enabled;
  float speed, max_force, max_torque;
  bool has_correction_factor; // motor joint
  float correction_factor;
} P2JointMotor;

typedef struct P2JointLimit {
  bool enabled;
  float lower, upper;           // prismatic / revolute / wheel
  float min_length, max_length; // distance
} P2JointLimit;

typedef struct P2JointSpring {
  bool enabled;
  float hertz, damping_ratio;
  float linear_hertz, linear_damping_ratio; // weld
  float angular_hertz, angular_damping_ratio;
} P2JointSpring;

typedef struct P2JointTarget {
  bool has_x, has_y; // mouse。無い成分は今の値
  float x, y;
  bool has_translation; // prismatic
  float translation;
  bool has_angle; // revolute
  float angle;
  bool has_linear_offset; // motor
  float linear_offset_x, linear_offset_y;
  bool has_angular_offset;
  float angular_offset;
} P2JointTarget;

typedef struct P2MaterialDesc {
  bool has_density, has_friction, has_restitution, has_material_id,
      has_material_name;
  float density, friction, restitution;
  int32_t material_id;
  LubStr material_name; // has_material_name で len 0 = 名前を消す
} P2MaterialDesc;

typedef struct P2EventFlags {
  bool has_sensor_events, sensor_events;
  bool has_contact, contact;
  bool has_pre_solve, pre_solve;
  bool has_hit, hit;
} P2EventFlags;

// query の visitor。false / 0 で打ち切り。raycast は Box2D の規約:
// -1 = この hit を無視、0 = 打ち切り、fraction = ここまでに詰める、1 = 続行。
typedef bool (*P2OverlapFn)(void *user, const P2ShapePart *shape);
typedef float (*P2RayFn)(void *user, const P2RayHit *hit);
typedef bool (*P2PlaneFn)(void *user, const P2MoverPlane *plane);

// 配列の view は同じ subsystem の次の呼び出しまで有効。

// joint の種類に無い量は has = false。

// kind は BEGIN / END。

// body への command。次の step の冒頭でまとめて適用する。point == NULL は
// 重心。
