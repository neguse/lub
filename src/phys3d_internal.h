// phys3d の core が使う内部の型。段階 3 の手書き header の形をそのまま内部に
// 移したもので、公開 API (include/lub/lub_api.h、生成物) との詰め替えは
// physics_box3d.c の末尾で行う。
#pragma once
#include "lub/lub_api.h"
#include <stdbool.h>
#include <stdint.h>

enum { P3_PROXY_SPHERE = 1, P3_PROXY_BOX = 2, P3_PROXY_CAPSULE = 3 };
enum { P3_COMPOUND_SPHERE = 1, P3_COMPOUND_CAPSULE = 2, P3_COMPOUND_BOX = 3 };

// ---------------------------------------------------------------- phys3d
// Box3D の即時モード API。宣言 / prune / handle / callback の規則は phys2d と
// 同じ。位置は float (Box3D の倍精度位置は面に出さない)。回転は
// 正規化した四元数。

typedef struct P3Filter {
  uint64_t category_bits; // 既定 1
  uint64_t mask_bits;     // 既定 全 bit
  int32_t group_index;
} P3Filter;

typedef struct P3ShapePart {
  LubHandle shape; // 0 = 無し
  LubHandle body;
  LubStr body_key;
  LubStr shape_key;
  LubStr tag;
  LubStr material_name;
  int32_t material_id;
  int32_t kind; // P3ShapeKind。0 = 不明
  bool valid;
  bool has_material;
  bool has_filter;
  P3Filter filter;
} P3ShapePart;

// Box3D の pre-solve は manifold を持たず、点と法線が 1 つ。
typedef struct P3PreSolve {
  P3ShapePart a, b;
  float x, y, z;
  float nx, ny, nz;
} P3PreSolve;

typedef struct P3Callbacks {
  void *user;
  void (*release)(void *user); // core が callback を手放すとき (NULL 可)
  bool (*filter)(void *user, const P3ShapePart *a, const P3ShapePart *b);
  bool (*pre_solve)(void *user, const P3PreSolve *contact);
  float (*friction)(void *user, float friction_a, int32_t material_a,
                    float friction_b, int32_t material_b);
  float (*restitution)(void *user, float restitution_a, int32_t material_a,
                       float restitution_b, int32_t material_b);
} P3Callbacks;

typedef struct P3WorldDesc {
  bool has_version;
  int32_t version;
  LubVec3d gravity; // 既定 (0, -9.8, 0)
  float fixed_dt;   // 既定 1/60
  int32_t substeps;
  int32_t max_steps;
  bool sleep;
  bool continuous;
  bool has_hit_event_threshold;
  float hit_event_threshold;
  P3Callbacks callbacks;
} P3WorldDesc;

typedef struct P3BodyDesc {
  bool has_version;
  int32_t version;
  int32_t type; // P3BodyType
  bool lock_linear_x, lock_linear_y, lock_linear_z;
  bool lock_angular_x, lock_angular_y, lock_angular_z;
  bool bullet;
  bool has_enabled, enabled;
  bool has_awake, awake;
  bool has_sleep, sleep;
  float gravity_scale; // 既定 1
  float linear_damping, angular_damping;
  bool has_sleep_threshold;
  float sleep_threshold;
  // initial (再生成のときだけ使う)
  LubVec3d position;
  LubQuat3d rotation; // 既定 identity
  LubVec3d linear_velocity;
  LubVec3d angular_velocity;
  bool initial_awake; // 既定 true
} P3BodyDesc;

typedef struct P3ShapeDesc {
  bool has_version;
  int32_t version;
  bool has_density;
  float density;
  float friction; // 既定 0.6
  float restitution;
  int32_t material_id;
  LubStr material_name;
  LubStr tag;
  bool sensor, contact, hit, sensor_events, pre_solve;
  P3Filter filter;
} P3ShapeDesc;

typedef struct P3SphereDesc {
  P3ShapeDesc shape;
  float r;
  LubVec3d offset;
} P3SphereDesc;

typedef struct P3BoxDesc {
  P3ShapeDesc shape;
  float hx, hy, hz;
  LubVec3d offset;
  bool has_rotation;
  LubQuat3d rotation;
} P3BoxDesc;

typedef struct P3CapsuleDesc {
  P3ShapeDesc shape;
  LubVec3d a, b;
  float r;
} P3CapsuleDesc;

typedef struct P3CylinderDesc {
  P3ShapeDesc shape;
  float height, radius;
  int32_t sides;  // 3..32、既定 16
  float y_offset; // 既定 -height / 2 (胴を原点中心に)
} P3CylinderDesc;

typedef struct P3ConeDesc {
  P3ShapeDesc shape;
  float height, radius1, radius2;
  int32_t slices; // 4..32、既定 16
} P3ConeDesc;

typedef struct P3HullDesc {
  P3ShapeDesc shape;   // version 必須
  const float *points; // x, y, z の組。4 点以上
  int32_t point_count;
  int32_t max_vertices; // 既定 255
} P3HullDesc;

typedef struct P3SurfaceMaterial {
  float friction, restitution;
  int32_t material_id;
} P3SurfaceMaterial;

typedef struct P3MeshDesc {
  P3ShapeDesc shape;      // version 必須
  const float *positions; // x, y, z の組
  int32_t vertex_count;
  const int32_t *indices; // 0 始まり、3 の倍数
  int32_t index_count;
  LubVec3d scale; // 既定 (1, 1, 1)
  bool weld_vertices;
  float weld_tolerance;
  bool use_median_split;
  bool identify_edges;                // 既定 true
  const P3SurfaceMaterial *materials; // NULL = 無し。1..255
  int32_t material_count;
  const int32_t *material_indices; // NULL = 無し。三角形ごと
  int32_t material_index_count;
} P3MeshDesc;

typedef struct P3HeightFieldDesc {
  P3ShapeDesc shape;        // version 必須
  int32_t x_count, z_count; // >= 2
  const float *heights;     // x_count * z_count
  LubVec3d scale;           // 既定 (cell_width, 1, cell_width)
  bool has_min_height, has_max_height;
  float min_height, max_height; // 無ければ heights から
  bool clockwise_winding;
} P3HeightFieldDesc;

typedef struct P3CompoundChild {
  int32_t kind; // P3CompoundChildKind
  LubVec3d position;
  LubQuat3d rotation; // 既定 identity
  P3SurfaceMaterial material;
  float r;          // sphere / capsule
  LubVec3d center;  // sphere
  LubVec3d a, b;    // capsule
  float hx, hy, hz; // box
} P3CompoundChild;

typedef struct P3CompoundDesc {
  P3ShapeDesc shape; // version 必須。static body 限定、sensor 不可
  const P3CompoundChild *children;
  int32_t child_count;
} P3CompoundDesc;

typedef struct P3JointDesc {
  bool has_version;
  int32_t version;
  int32_t type; // P3JointType
  LubHandle body_a, body_b;
  // 世界座標の axis / anchor から local frame を作る。frame_a / frame_b を
  // 明示すればそちらが勝つ。
  bool has_axis;
  LubVec3d axis;
  bool has_anchor_a, has_anchor_b;
  LubVec3d anchor_a, anchor_b;
  bool has_frame_a, has_frame_b;
  LubVec3d frame_a_position, frame_b_position;
  LubQuat3d frame_a_rotation, frame_b_rotation;
  float force_threshold, torque_threshold; // 既定 FLT_MAX
  bool has_constraint_tuning;
  float constraint_hertz, constraint_damping_ratio;
  bool collide_connected;
  float length;     // 既定 1
  float min_length; // 既定 0
  float max_length; // 既定 FLT_MAX
  float lower, upper;
  float hertz, damping_ratio;
  float linear_hertz, angular_hertz;
  float linear_damping_ratio, angular_damping_ratio;
  float max_force, max_torque, motor_speed;
  float target_angle, target_translation;
  bool enable_spring, enable_limit, enable_motor;
  float lower_spring_force, upper_spring_force; // 既定 -FLT_MAX / FLT_MAX
  LubVec3d linear_velocity, angular_velocity;   // motor
  float max_velocity_force, max_velocity_torque;
  float max_spring_force, max_spring_torque;
  LubQuat3d target_rotation; // spherical。既定 identity
  bool enable_cone_limit;
  float cone_angle;
  bool enable_twist_limit;
  float lower_twist_angle, upper_twist_angle;
  LubVec3d motor_velocity;
  bool enable_steering; // wheel
  float steering_hertz, steering_damping_ratio;
  float target_steering_angle, max_steering_torque;
  bool enable_steering_limit;
  float lower_steering_limit, upper_steering_limit;
} P3JointDesc;

// type ごとの既定値 (parallel / wheel は spring が既定で有効) を入れる。

typedef struct P3WorldInfo {
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
  LubVec3d gravity;
  bool sleep, continuous, warm_starting;
  float restitution_threshold, hit_event_threshold, maximum_linear_speed;
  int32_t awake_body_count;
} P3WorldInfo;

typedef struct P3StepInfo {
  int32_t steps;
  int32_t commands;
  float alpha;
  bool dropped;
  int32_t contact_begins, contact_ends, contact_hits;
  int32_t sensor_begins, sensor_ends;
  int32_t body_moves;
  int32_t joint_events;
} P3StepInfo;

typedef struct P3Pose {
  LubVec3d position;
  LubQuat3d rotation;
  LubVec3d linear_velocity, angular_velocity;
  bool awake, enabled, sleep;
  float sleep_threshold;
} P3Pose;

typedef struct P3Velocity {
  LubVec3d linear, angular;
} P3Velocity;

typedef struct P3MassData {
  float mass;
  LubVec3d center; // world
  LubVec3d local_center;
  // 慣性テンソル (local center まわり、対称) の成分
  float xx, yy, zz, xy, xz, yz;
} P3MassData;

typedef struct P3Aabb {
  LubVec3d min, max;
} P3Aabb;

typedef struct P3ShapeInfo {
  P3ShapePart part;
  float density, friction, restitution;
  bool sensor, sensor_events, contact, pre_solve, hit;
  P3Aabb aabb;
} P3ShapeInfo;

typedef struct P3JointView {
  LubHandle joint;
  LubStr key;
  int32_t type; // P3JointType。0 = 不明
  LubStr a, b;
  bool valid;
} P3JointView;

typedef struct P3Frame {
  LubVec3d position;
  LubQuat3d rotation;
} P3Frame;

typedef struct P3JointInfo {
  P3JointView view;
  bool collide_connected;
  LubVec3d force, torque;
  float linear_separation, angular_separation;
  P3Frame local_frame_a, local_frame_b;
} P3JointInfo;

typedef struct P3Contact {
  P3ShapePart a, b;
  LubVec3d normal;
  int32_t point_count;
  LubVec3d point;
  float approach_speed;
} P3Contact;

typedef struct P3BodyEvent {
  LubStr body;
  bool valid;
  LubVec3d position;
  LubQuat3d rotation;
  bool fell_asleep;
} P3BodyEvent;

typedef struct P3JointEvent {
  LubStr joint;
  int32_t type; // P3JointType。0 = 不明
  LubStr a, b;
  bool valid;
} P3JointEvent;

typedef struct P3ContactData {
  P3ShapePart a, b;
  LubVec3d normal;
  int32_t manifold_count;
  int32_t point_count;
  bool has_point;
  LubVec3d point;
  float separation;
} P3ContactData;

typedef struct P3Ray {
  LubVec3d origin;
  LubVec3d translation; // max_fraction を掛けた後の値
} P3Ray;

typedef struct P3RayHit {
  P3ShapePart shape; // world query のとき
  LubVec3d point, normal;
  float fraction;
  int32_t iterations; // shape raycast のとき
  int32_t hit_material_id;
  int32_t triangle_index, child_index;
  int32_t node_visits, leaf_visits; // raycast_closest のとき
} P3RayHit;

typedef struct P3TreeStats {
  int32_t node_visits, leaf_visits;
} P3TreeStats;

typedef struct P3QueryFilter {
  uint64_t category_bits;
  uint64_t mask_bits;
} P3QueryFilter;

typedef struct P3ShapeProxy {
  int32_t kind;    // P3ProxyKind
  float r;         // sphere / capsule / box の丸め
  LubVec3d center; // sphere / box
  float hx, hy, hz;
  bool has_rotation;
  LubQuat3d rotation; // box
  LubVec3d a, b;      // capsule
} P3ShapeProxy;

typedef struct P3Mover {
  LubVec3d a, b;
  float r;
} P3Mover;

typedef struct P3MoverPlane {
  P3ShapePart shape;
  LubVec3d point, normal; // world
  float offset;
  int32_t plane_count;
} P3MoverPlane;

typedef struct P3Profile {
  float step, pairs, collide, solve, solver_setup, constraints,
      prepare_constraints, integrate_velocities, warm_start, solve_impulses,
      integrate_positions, relax_impulses, apply_restitution, store_impulses,
      split_islands, transforms, sensor_hits, joint_events, hit_events, refit,
      bullets, sleep_islands, sensors;
} P3Profile;

#define P3_MANIFOLD_COUNT_BUCKETS 8

typedef struct P3Counters {
  int32_t body_count, shape_count, contact_count, joint_count, island_count,
      stack_used, arena_capacity, static_tree_height, tree_height,
      sat_call_count, sat_cache_hit_count, byte_count, task_count,
      awake_contact_count, recycled_contact_count, distance_iterations,
      push_back_iterations, root_iterations;
  int32_t color_counts[24];
  int32_t manifold_counts[P3_MANIFOLD_COUNT_BUCKETS];
} P3Counters;

typedef struct P3SetVelocity {
  bool has_vx, has_vy, has_vz;
  bool has_wx, has_wy, has_wz;
  LubVec3d linear, angular;
  bool wake;
} P3SetVelocity;

typedef struct P3Teleport {
  bool has_x, has_y, has_z;
  LubVec3d position;
  bool has_rotation;
  LubQuat3d rotation;
  bool wake;
} P3Teleport;

typedef struct P3SetTarget {
  bool has_x, has_y, has_z;
  LubVec3d position;
  bool has_rotation;
  LubQuat3d rotation;
  float time_step; // <= 0 で world の fixed_dt
  bool wake;
} P3SetTarget;

typedef struct P3JointMotor {
  bool enabled;
  float speed, max_force, max_torque;
  bool has_velocity; // spherical
  LubVec3d velocity;
  bool has_linear_velocity, has_angular_velocity; // motor
  LubVec3d linear_velocity, angular_velocity;
  bool has_max_velocity_force, has_max_velocity_torque;
  float max_velocity_force, max_velocity_torque;
} P3JointMotor;

typedef struct P3JointLimit {
  bool enabled;
  float lower, upper;
  float min_length, max_length; // distance。max の既定 FLT_MAX
  bool has_cone_angle;          // spherical
  float cone_angle;
  bool has_twist; // spherical: lower / upper を twist に使う
} P3JointLimit;

typedef struct P3JointSpring {
  bool enabled;
  float hertz, damping_ratio;
  float linear_hertz, linear_damping_ratio; // weld / motor
  float angular_hertz, angular_damping_ratio;
  bool has_max_torque; // parallel
  float max_torque;
} P3JointSpring;

typedef struct P3JointTarget {
  bool has_translation; // prismatic
  float translation;
  bool has_angle; // revolute / wheel (steering)
  float angle;
  bool has_rotation; // spherical
  LubQuat3d rotation;
  bool has_linear_velocity, has_angular_velocity; // motor
  LubVec3d linear_velocity, angular_velocity;
} P3JointTarget;

typedef struct P3MaterialDesc {
  bool has_density, has_friction, has_restitution, has_material_id,
      has_material_name;
  float density, friction, restitution;
  int32_t material_id;
  LubStr material_name;
} P3MaterialDesc;

typedef struct P3EventFlags {
  bool has_sensor_events, sensor_events;
  bool has_contact, contact;
  bool has_pre_solve, pre_solve;
  bool has_hit, hit;
} P3EventFlags;

typedef bool (*P3OverlapFn)(void *user, const P3ShapePart *shape);
typedef float (*P3RayFn)(void *user, const P3RayHit *hit);
typedef bool (*P3PlaneFn)(void *user, const P3MoverPlane *plane);

// spherical は vector (has_vector)、revolute / wheel は scalar (has)。
