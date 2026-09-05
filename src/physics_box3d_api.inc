// phys3d の公開 API (include/lub/lub_api.h、生成物) と core の内部形
// (phys3d_internal.h) の詰め替え。physics_box3d.c の末尾から include する。
// 回転は quat (正規化して受ける) か euler (XYZ の順に軸回転を合成) で受け、
// core には正規化済みの四元数を渡す (二重に正規化すると丸めが変わるので
// core は正規化しない)。

typedef struct GrowBuf3 {
  void *ptr;
  int32_t cap, n;
} GrowBuf3;

static void *grow3_push(GrowBuf3 *g, size_t size) {
  if (g->n >= g->cap) {
    int32_t cap = g->cap ? g->cap * 2 : 16;
    void *p = realloc(g->ptr, size * (size_t)cap);
    if (!p)
      return NULL;
    g->ptr = p;
    g->cap = cap;
  }
  void *slot = (unsigned char *)g->ptr + size * (size_t)g->n;
  memset(slot, 0, size);
  g->n++;
  return slot;
}

#define OPT3(s, f, def) ((s)->has_##f ? (s)->f : (def))

// ------------------------------------------------------------ rotation

static LubQuat3d quat3_identity(void) {
  LubQuat3d q = {0, 0, 0, 1};
  return q;
}

static LubQuat3d quat3_normalized(LubQuat3d in) {
  b3Quat q = {{in.x, in.y, in.z}, in.w};
  b3Quat n = b3NormalizeQuat(q);
  LubQuat3d out = {n.v.x, n.v.y, n.v.z, n.s};
  return out;
}

// Box3D に euler の helper は無いので、軸ごとの四元数を XYZ の順で合成する。
static LubQuat3d quat3_from_euler(LubVec3d e) {
  b3Quat qx = b3MakeQuatFromAxisAngle(b3Vec3_axisX, e.x);
  b3Quat qy = b3MakeQuatFromAxisAngle(b3Vec3_axisY, e.y);
  b3Quat qz = b3MakeQuatFromAxisAngle(b3Vec3_axisZ, e.z);
  b3Quat n = b3NormalizeQuat(b3MulQuat(b3MulQuat(qx, qy), qz));
  LubQuat3d out = {n.v.x, n.v.y, n.v.z, n.s};
  return out;
}

// quat が優先、次に euler。どちらも無ければ false (out は触らない)。
static bool rotation_of(bool has_quat, LubQuat3d quat, bool has_euler,
                        LubVec3d euler, LubQuat3d *out) {
  if (has_quat) {
    *out = quat3_normalized(quat);
    return true;
  }
  if (has_euler) {
    *out = quat3_from_euler(euler);
    return true;
  }
  return false;
}

static LubVec3d vec3d(float x, float y, float z) {
  LubVec3d v = {x, y, z};
  return v;
}

// ------------------------------------------------------------- filters

static void filter3_from_desc(const LubFilterDesc3d *f, bool has,
                              P3Filter *out) {
  if (!has || !f)
    return;
  if (f->has_category_bits)
    out->category_bits = f->category_bits;
  if (f->has_mask_bits)
    out->mask_bits = f->mask_bits;
  if (f->has_group)
    out->group_index = f->group;
}

static P3QueryFilter qfilter3_from(const LubFilterDesc3d *f, bool has) {
  P3QueryFilter q = {1u, UINT64_MAX};
  if (has && f) {
    if (f->has_category_bits)
      q.category_bits = f->category_bits;
    if (f->has_mask_bits)
      q.mask_bits = f->mask_bits;
  }
  return q;
}

static void shape_view3_of(LubShapeView3d *v, const P3ShapePart *p) {
  memset(v, 0, sizeof(*v));
  v->body = p->body_key;
  v->shape = p->shape_key;
  v->tag = p->tag;
  v->material_name = p->material_name;
  v->has_material_id = p->has_material;
  v->material_id = p->material_id;
  v->has_kind = p->kind != 0;
  v->kind = p->kind;
  if (p->has_filter) {
    v->has_category_bits = true;
    v->category_bits = p->filter.category_bits;
    v->has_mask_bits = true;
    v->mask_bits = p->filter.mask_bits;
    v->has_group = true;
    v->group = p->filter.group_index;
  }
  v->valid = p->valid;
}

// ------------------------------------------------------------ callbacks

typedef struct CbHolder3 {
  LubWorldCallbacks3d cb;
} CbHolder3;

static void cb3_holder_release(void *user) {
  CbHolder3 *h = (CbHolder3 *)user;
  if (!h)
    return;
  if (h->cb.user_release)
    h->cb.user_release(h->cb.user);
  free(h);
}

static bool cb3_filter_tramp(void *user, const P3ShapePart *a,
                             const P3ShapePart *b) {
  CbHolder3 *h = (CbHolder3 *)user;
  LubShapeView3d va, vb;
  shape_view3_of(&va, a);
  shape_view3_of(&vb, b);
  return h->cb.filter(h->cb.user, &va, &vb);
}

static bool cb3_pre_solve_tramp(void *user, const P3PreSolve *c) {
  CbHolder3 *h = (CbHolder3 *)user;
  LubPreSolveContact3d v;
  memset(&v, 0, sizeof(v));
  shape_view3_of(&v.a, &c->a);
  shape_view3_of(&v.b, &c->b);
  v.x = c->x;
  v.y = c->y;
  v.z = c->z;
  v.nx = c->nx;
  v.ny = c->ny;
  v.nz = c->nz;
  return h->cb.pre_solve(h->cb.user, &v);
}

static float cb3_friction_tramp(void *user, float fa, int32_t ma, float fb,
                                int32_t mb) {
  CbHolder3 *h = (CbHolder3 *)user;
  LubMaterialView a = {true, fa, false, 0, ma};
  LubMaterialView b = {true, fb, false, 0, mb};
  return h->cb.friction(h->cb.user, &a, &b);
}

static float cb3_restitution_tramp(void *user, float ra, int32_t ma, float rb,
                                   int32_t mb) {
  CbHolder3 *h = (CbHolder3 *)user;
  LubMaterialView a = {false, 0, true, ra, ma};
  LubMaterialView b = {false, 0, true, rb, mb};
  return h->cb.restitution(h->cb.user, &a, &b);
}

// ---------------------------------------------------------------- world

LubHandle lub_phys3d_find_world(LubContext *ctx, LubStr key) {
  return p3_world_find(ctx, key);
}

LubHandle lub_phys3d_find_body(LubContext *ctx, LubHandle world, LubStr key) {
  return p3_body_find(ctx, world, key);
}

LubHandle lub_phys3d_find_shape(LubContext *ctx, LubHandle body, LubStr key) {
  return p3_shape_find(ctx, body, key);
}

LubHandle lub_phys3d_find_joint(LubContext *ctx, LubHandle world, LubStr key) {
  return p3_joint_find(ctx, world, key);
}

LubStatus lub_phys3d_world(LubContext *ctx, LubStr key,
                           const LubWorldOpts3d *o, LubHandle *out) {
  P3WorldDesc d;
  p3_world_desc_init(&d);
  CbHolder3 *holder = NULL;
  if (o) {
    d.has_version = o->has_version;
    d.version = o->version;
    if (o->has_gravity)
      d.gravity = o->gravity;
    if (o->has_fixed_dt)
      d.fixed_dt = o->fixed_dt;
    if (o->has_substeps)
      d.substeps = o->substeps;
    if (o->has_max_steps)
      d.max_steps = o->max_steps;
    if (o->has_sleep)
      d.sleep = o->sleep;
    if (o->has_continuous)
      d.continuous = o->continuous;
    d.has_hit_event_threshold = o->has_hit_event_threshold;
    d.hit_event_threshold = o->hit_event_threshold;
    if (o->has_callbacks) {
      const LubWorldCallbacks3d *cb = &o->callbacks;
      if (cb->filter || cb->pre_solve || cb->friction || cb->restitution) {
        holder = (CbHolder3 *)calloc(1, sizeof(CbHolder3));
        if (!holder)
          return lub_api_fail(lub_api_app(ctx), "phys3d_world: out of memory");
        holder->cb = *cb;
        d.callbacks.user = holder;
        d.callbacks.release = cb3_holder_release;
        d.callbacks.filter = cb->filter ? cb3_filter_tramp : NULL;
        d.callbacks.pre_solve = cb->pre_solve ? cb3_pre_solve_tramp : NULL;
        d.callbacks.friction = cb->friction ? cb3_friction_tramp : NULL;
        d.callbacks.restitution =
            cb->restitution ? cb3_restitution_tramp : NULL;
      } else if (cb->user_release) {
        cb->user_release(cb->user);
      }
    }
  }
  LubStatus st = p3_world(ctx, key, &d, out);
  if (st != LUB_OK)
    cb3_holder_release(holder);
  return st;
}

LubStatus lub_phys3d_begin(LubContext *ctx, LubHandle world,
                           const LubBeginOpts3d *opts) {
  bool prune = opts && opts->has_prune ? opts->prune : true;
  return p3_begin(ctx, world, prune);
}

LubStatus lub_phys3d_world_info(LubContext *ctx, LubHandle world,
                                LubWorldInfo3d *out) {
  P3WorldInfo i;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_world_info(ctx, world, &i);
  if (st != LUB_OK)
    return st;
  out->key = i.key;
  out->valid = i.valid;
  out->version = i.version;
  out->generation = i.generation;
  out->begun = i.begun;
  out->prune = i.prune;
  out->fixed_dt = i.fixed_dt;
  out->substeps = i.substeps;
  out->max_steps = i.max_steps;
  out->accumulator = i.accumulator;
  out->pending_commands = i.pending_commands;
  if (i.valid) {
    out->has_gravity = true;
    out->gravity = i.gravity;
    out->has_sleep = out->has_continuous = out->has_warm_starting = true;
    out->sleep = i.sleep;
    out->continuous = i.continuous;
    out->warm_starting = i.warm_starting;
    out->has_restitution_threshold = out->has_hit_event_threshold =
        out->has_maximum_linear_speed = out->has_awake_body_count = true;
    out->restitution_threshold = i.restitution_threshold;
    out->hit_event_threshold = i.hit_event_threshold;
    out->maximum_linear_speed = i.maximum_linear_speed;
    out->awake_body_count = i.awake_body_count;
  }
  return LUB_OK;
}

// ----------------------------------------------------------------- body

LubStatus lub_phys3d_body(LubContext *ctx, LubHandle world, LubStr key,
                          const LubBodyDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_body: desc required");
  P3BodyDesc d;
  p3_body_desc_init(&d);
  d.has_version = s->has_version;
  d.version = s->version;
  if (s->has_type)
    d.type = s->type;
  if (s->has_motion_locks) {
    const LubMotionLocks3d *m = &s->motion_locks;
    d.lock_linear_x = m->has_linear_x && m->linear_x;
    d.lock_linear_y = m->has_linear_y && m->linear_y;
    d.lock_linear_z = m->has_linear_z && m->linear_z;
    d.lock_angular_x = m->has_angular_x && m->angular_x;
    d.lock_angular_y = m->has_angular_y && m->angular_y;
    d.lock_angular_z = m->has_angular_z && m->angular_z;
  }
  d.bullet = s->has_bullet && s->bullet;
  d.has_enabled = s->has_enabled;
  d.enabled = s->enabled;
  d.has_awake = s->has_awake;
  d.awake = s->awake;
  d.has_sleep = s->has_sleep;
  d.sleep = s->sleep;
  if (s->has_gravity_scale)
    d.gravity_scale = s->gravity_scale;
  if (s->has_linear_damping)
    d.linear_damping = s->linear_damping;
  if (s->has_angular_damping)
    d.angular_damping = s->angular_damping;
  d.has_sleep_threshold = s->has_sleep_threshold;
  d.sleep_threshold = s->sleep_threshold;
  if (s->has_initial) {
    const LubInitialState3d *i = &s->initial;
    if (i->has_x)
      d.position.x = i->x;
    if (i->has_y)
      d.position.y = i->y;
    if (i->has_z)
      d.position.z = i->z;
    rotation_of(i->has_quat, i->quat, i->has_euler, i->euler, &d.rotation);
    if (i->has_vx)
      d.linear_velocity.x = i->vx;
    if (i->has_vy)
      d.linear_velocity.y = i->vy;
    if (i->has_vz)
      d.linear_velocity.z = i->vz;
    if (i->has_wx)
      d.angular_velocity.x = i->wx;
    if (i->has_wy)
      d.angular_velocity.y = i->wy;
    if (i->has_wz)
      d.angular_velocity.z = i->wz;
    if (i->has_awake)
      d.initial_awake = i->awake;
  }
  return p3_body(ctx, world, key, &d, out);
}

// ---------------------------------------------------------------- shapes

static void shape3_desc_from(const LubShapeDesc3d *s, P3ShapeDesc *d) {
  p3_shape_desc_init(d);
  d->has_version = s->has_version;
  d->version = s->version;
  d->has_density = s->has_density;
  d->density = s->density;
  if (s->has_friction)
    d->friction = s->friction;
  if (s->has_restitution)
    d->restitution = s->restitution;
  if (s->has_material_id)
    d->material_id = s->material_id;
  d->material_name = s->material_name;
  d->tag = s->tag;
  d->sensor = s->has_sensor && s->sensor;
  d->contact = s->has_contact && s->contact;
  d->hit = s->has_hit && s->hit;
  d->sensor_events = s->has_sensor_events && s->sensor_events;
  d->pre_solve = s->has_pre_solve && s->pre_solve;
  filter3_from_desc(&s->filter, s->has_filter, &d->filter);
}

LubStatus lub_phys3d_sphere(LubContext *ctx, LubHandle body, LubStr key,
                            const LubSphereDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_sphere: desc required");
  P3SphereDesc d;
  memset(&d, 0, sizeof(d));
  shape3_desc_from(&s->base, &d.shape);
  d.r = s->r;
  if (s->has_offset)
    d.offset = s->offset;
  return p3_sphere(ctx, body, key, &d, out);
}

LubStatus lub_phys3d_box(LubContext *ctx, LubHandle body, LubStr key,
                         const LubBoxDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_box: desc required");
  P3BoxDesc d;
  memset(&d, 0, sizeof(d));
  shape3_desc_from(&s->base, &d.shape);
  d.hx = s->hx;
  d.hy = s->hy;
  d.hz = s->hz;
  if (s->has_offset)
    d.offset = s->offset;
  d.rotation = quat3_identity();
  if (s->has_quat) {
    d.has_rotation = true;
    d.rotation = quat3_normalized(s->quat);
  }
  return p3_box(ctx, body, key, &d, out);
}

LubStatus lub_phys3d_capsule(LubContext *ctx, LubHandle body, LubStr key,
                             const LubCapsuleDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_capsule: desc required");
  P3CapsuleDesc d;
  memset(&d, 0, sizeof(d));
  shape3_desc_from(&s->base, &d.shape);
  d.a = s->a;
  d.b = s->b;
  d.r = s->r;
  return p3_capsule(ctx, body, key, &d, out);
}

LubStatus lub_phys3d_cylinder(LubContext *ctx, LubHandle body, LubStr key,
                              const LubCylinderDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_cylinder: desc required");
  P3CylinderDesc d;
  memset(&d, 0, sizeof(d));
  shape3_desc_from(&s->base, &d.shape);
  d.height = s->height;
  d.radius = s->radius;
  d.sides = OPT3(s, sides, 16);
  d.y_offset = OPT3(s, y_offset, -s->height * 0.5f);
  return p3_cylinder(ctx, body, key, &d, out);
}

LubStatus lub_phys3d_cone(LubContext *ctx, LubHandle body, LubStr key,
                          const LubConeDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_cone: desc required");
  P3ConeDesc d;
  memset(&d, 0, sizeof(d));
  shape3_desc_from(&s->base, &d.shape);
  d.height = s->height;
  d.radius1 = s->radius1;
  d.radius2 = OPT3(s, radius2, 0.0f);
  d.slices = OPT3(s, slices, 16);
  return p3_cone(ctx, body, key, &d, out);
}

LubStatus lub_phys3d_hull(LubContext *ctx, LubHandle body, LubStr key,
                          const LubHullDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_hull: desc required");
  P3HullDesc d;
  memset(&d, 0, sizeof(d));
  shape3_desc_from(&s->base, &d.shape);
  d.points = s->points;
  d.point_count = s->points_count / 3;
  d.max_vertices = OPT3(s, max_vertices, 255);
  return p3_hull(ctx, body, key, &d, out);
}

static P3SurfaceMaterial *surface_materials_from(
    const LubSurfaceMaterial3d *src, int32_t count, const P3ShapeDesc *def) {
  if (!src || count <= 0)
    return NULL;
  P3SurfaceMaterial *out =
      (P3SurfaceMaterial *)calloc((size_t)count, sizeof(P3SurfaceMaterial));
  if (!out)
    return NULL;
  for (int32_t i = 0; i < count; ++i) {
    out[i].friction = src[i].has_friction ? src[i].friction : def->friction;
    out[i].restitution =
        src[i].has_restitution ? src[i].restitution : def->restitution;
    out[i].material_id =
        src[i].has_material_id ? src[i].material_id : def->material_id;
  }
  return out;
}

LubStatus lub_phys3d_mesh(LubContext *ctx, LubHandle body, LubStr key,
                          const LubMeshDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_mesh: desc required");
  P3MeshDesc d;
  memset(&d, 0, sizeof(d));
  shape3_desc_from(&s->base, &d.shape);
  d.positions = s->positions;
  d.vertex_count = s->positions_count / 3;
  d.indices = s->indices;
  d.index_count = s->indices_count;
  d.scale = s->has_scale ? s->scale : vec3d(1, 1, 1);
  d.weld_vertices = s->has_weld_vertices && s->weld_vertices;
  d.weld_tolerance = OPT3(s, weld_tolerance, 0.0f);
  d.use_median_split = s->has_use_median_split && s->use_median_split;
  d.identify_edges = s->has_identify_edges ? s->identify_edges : true;
  P3SurfaceMaterial *mats =
      surface_materials_from(s->materials, s->materials_count, &d.shape);
  if (s->materials && s->materials_count > 0 && !mats)
    return lub_api_fail(lub_api_app(ctx), "phys3d_mesh: out of memory");
  d.materials = mats;
  d.material_count = mats ? s->materials_count : 0;
  d.material_indices = s->material_indices;
  d.material_index_count = s->material_indices ? s->material_indices_count : 0;
  LubStatus st = p3_mesh(ctx, body, key, &d, out);
  free(mats);
  return st;
}

LubStatus lub_phys3d_height_field(LubContext *ctx, LubHandle body, LubStr key,
                                  const LubHeightFieldDesc3d *s,
                                  LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_height_field: desc required");
  P3HeightFieldDesc d;
  memset(&d, 0, sizeof(d));
  shape3_desc_from(&s->base, &d.shape);
  d.x_count = s->x_count;
  d.z_count = s->z_count;
  d.heights = s->heights;
  float cell = OPT3(s, cell_width, 1.0f);
  d.scale = s->has_scale ? s->scale : vec3d(cell, 1.0f, cell);
  d.has_min_height = s->has_min_height;
  d.min_height = s->min_height;
  d.has_max_height = s->has_max_height;
  d.max_height = s->max_height;
  d.clockwise_winding = s->has_clockwise_winding && s->clockwise_winding;
  return p3_height_field(ctx, body, key, &d, out);
}

LubStatus lub_phys3d_compound(LubContext *ctx, LubHandle body, LubStr key,
                              const LubCompoundDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_compound: desc required");
  P3CompoundDesc d;
  memset(&d, 0, sizeof(d));
  shape3_desc_from(&s->base, &d.shape);
  int32_t n = s->children ? s->children_count : 0;
  P3CompoundChild *children =
      (P3CompoundChild *)calloc((size_t)(n > 0 ? n : 1), sizeof(*children));
  if (!children)
    return lub_api_fail(lub_api_app(ctx), "phys3d_compound: out of memory");
  for (int32_t i = 0; i < n; ++i) {
    const LubCompoundChild3d *c = &s->children[i];
    P3CompoundChild *o = &children[i];
    o->rotation = quat3_identity();
    if (c->has_pose) {
      o->position = vec3d(OPT3(&c->pose, x, 0.0f), OPT3(&c->pose, y, 0.0f),
                          OPT3(&c->pose, z, 0.0f));
      rotation_of(c->pose.has_quat, c->pose.quat, c->pose.has_euler,
                  c->pose.euler, &o->rotation);
    }
    o->material.friction = OPT3(c, friction, d.shape.friction);
    o->material.restitution = OPT3(c, restitution, d.shape.restitution);
    o->material.material_id = OPT3(c, material_id, d.shape.material_id);
    if (c->has_sphere) {
      o->kind = P3_COMPOUND_SPHERE;
      o->r = c->sphere.r;
      if (c->sphere.has_center)
        o->center = c->sphere.center;
    } else if (c->has_capsule) {
      o->kind = P3_COMPOUND_CAPSULE;
      o->a = c->capsule.a;
      o->b = c->capsule.b;
      o->r = c->capsule.r;
    } else if (c->has_box) {
      o->kind = P3_COMPOUND_BOX;
      o->hx = c->box.hx;
      o->hy = c->box.hy;
      o->hz = c->box.hz;
    } else {
      free(children);
      return lub_api_fail(lub_api_app(ctx),
                          "phys3d_compound: child %d must have sphere, box, "
                          "or capsule",
                          (int)i + 1);
    }
  }
  d.children = children;
  d.child_count = n;
  LubStatus st = p3_compound(ctx, body, key, &d, out);
  free(children);
  return st;
}

static GrowBuf3 g_shape_views3;

static LubStatus shape_views3_out(LubStatus st, const P3ShapePart *items,
                                  int32_t count, const LubShapeView3d **out,
                                  int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (st != LUB_OK)
    return st;
  g_shape_views3.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubShapeView3d *v =
        (LubShapeView3d *)grow3_push(&g_shape_views3, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    shape_view3_of(v, &items[i]);
  }
  *out = (const LubShapeView3d *)g_shape_views3.ptr;
  *out_count = g_shape_views3.n;
  return LUB_OK;
}

// ---------------------------------------------------------------- joints

static void frame_from(const LubFrameDesc3d *f, LubVec3d *position,
                       LubQuat3d *rotation) {
  *position = vec3d(OPT3(f, x, 0.0f), OPT3(f, y, 0.0f), OPT3(f, z, 0.0f));
  *rotation = quat3_identity();
  rotation_of(f->has_quat, f->quat, f->has_euler, f->euler, rotation);
}

LubStatus lub_phys3d_joint(LubContext *ctx, LubHandle world, LubStr key,
                           const LubJointDesc3d *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_joint: desc required");
  P3JointDesc d;
  p3_joint_desc_init(&d, s->has_type ? s->type
                                     : LUB_PHYS3D_JOINT_TYPE_REVOLUTE);
  d.has_version = s->has_version;
  d.version = s->version;
  d.body_a = s->body_a;
  d.body_b = s->body_b;
  if (s->has_axis) {
    d.has_axis = true;
    d.axis = s->axis;
  }
  d.has_anchor_a = s->has_anchor_a;
  d.anchor_a = s->anchor_a;
  d.has_anchor_b = s->has_anchor_b;
  d.anchor_b = s->anchor_b;
  if (s->has_frame_a) {
    d.has_frame_a = true;
    frame_from(&s->frame_a, &d.frame_a_position, &d.frame_a_rotation);
  }
  if (s->has_frame_b) {
    d.has_frame_b = true;
    frame_from(&s->frame_b, &d.frame_b_position, &d.frame_b_rotation);
  }
  d.collide_connected = s->has_collide_connected && s->collide_connected;
  if (s->has_force_threshold)
    d.force_threshold = s->force_threshold;
  if (s->has_torque_threshold)
    d.torque_threshold = s->torque_threshold;
  if (s->has_constraint_hertz) {
    d.has_constraint_tuning = true;
    d.constraint_hertz = s->constraint_hertz;
    d.constraint_damping_ratio = 2.0f;
  }
  if (s->has_constraint_damping_ratio) {
    if (!d.has_constraint_tuning) {
      d.has_constraint_tuning = true;
      d.constraint_hertz = 60.0f;
    }
    d.constraint_damping_ratio = s->constraint_damping_ratio;
  }
  if (s->has_length)
    d.length = s->length;
  if (s->has_min_length)
    d.min_length = s->min_length;
  if (s->has_max_length)
    d.max_length = s->max_length;
  if (s->has_lower)
    d.lower = s->lower;
  if (s->has_upper)
    d.upper = s->upper;
  if (s->has_hertz)
    d.hertz = s->hertz;
  if (s->has_damping_ratio)
    d.damping_ratio = s->damping_ratio;
  if (s->has_linear_hertz)
    d.linear_hertz = s->linear_hertz;
  if (s->has_angular_hertz)
    d.angular_hertz = s->angular_hertz;
  if (s->has_linear_damping_ratio)
    d.linear_damping_ratio = s->linear_damping_ratio;
  if (s->has_angular_damping_ratio)
    d.angular_damping_ratio = s->angular_damping_ratio;
  if (s->has_max_force)
    d.max_force = s->max_force;
  if (s->has_max_torque)
    d.max_torque = s->max_torque;
  if (s->has_max_velocity_force)
    d.max_velocity_force = s->max_velocity_force;
  if (s->has_max_velocity_torque)
    d.max_velocity_torque = s->max_velocity_torque;
  if (s->has_max_spring_force)
    d.max_spring_force = s->max_spring_force;
  if (s->has_max_spring_torque)
    d.max_spring_torque = s->max_spring_torque;
  if (s->has_motor_speed)
    d.motor_speed = s->motor_speed;
  if (s->has_target_angle)
    d.target_angle = s->target_angle;
  if (s->has_target_translation)
    d.target_translation = s->target_translation;
  if (s->has_target_rotation)
    d.target_rotation = quat3_normalized(s->target_rotation);
  if (s->has_linear_velocity)
    d.linear_velocity = s->linear_velocity;
  if (s->has_angular_velocity)
    d.angular_velocity = s->angular_velocity;
  if (s->has_motor_velocity)
    d.motor_velocity = s->motor_velocity;
  if (s->has_enable_spring)
    d.enable_spring = s->enable_spring;
  if (s->has_enable_limit)
    d.enable_limit = s->enable_limit;
  if (s->has_enable_motor)
    d.enable_motor = s->enable_motor;
  if (s->has_cone_angle)
    d.cone_angle = s->cone_angle;
  if (s->has_enable_cone_limit)
    d.enable_cone_limit = s->enable_cone_limit;
  if (s->has_enable_twist_limit)
    d.enable_twist_limit = s->enable_twist_limit;
  if (s->has_lower_twist_angle)
    d.lower_twist_angle = s->lower_twist_angle;
  if (s->has_upper_twist_angle)
    d.upper_twist_angle = s->upper_twist_angle;
  if (s->has_spring) {
    const LubJointSpringDesc3d *p = &s->spring;
    d.enable_spring = p->has_enabled ? p->enabled : true;
    if (p->has_hertz)
      d.hertz = p->hertz;
    if (p->has_damping_ratio)
      d.damping_ratio = p->damping_ratio;
    if (p->has_linear_hertz)
      d.linear_hertz = p->linear_hertz;
    if (p->has_linear_damping_ratio)
      d.linear_damping_ratio = p->linear_damping_ratio;
    if (p->has_angular_hertz)
      d.angular_hertz = p->angular_hertz;
    if (p->has_angular_damping_ratio)
      d.angular_damping_ratio = p->angular_damping_ratio;
    if (p->has_max_torque)
      d.max_torque = p->max_torque;
  }
  if (s->has_limit) {
    const LubJointLimitDesc3d *p = &s->limit;
    d.enable_limit = p->has_enabled ? p->enabled : true;
    if (p->has_lower)
      d.lower = p->lower;
    if (p->has_upper)
      d.upper = p->upper;
    if (p->has_min_length)
      d.min_length = p->min_length;
    if (p->has_max_length)
      d.max_length = p->max_length;
    if (p->has_cone_angle) {
      d.enable_cone_limit = true;
      d.cone_angle = p->cone_angle;
    }
    if (p->has_lower_twist_angle || p->has_upper_twist_angle) {
      d.enable_twist_limit = true;
      if (p->has_lower_twist_angle)
        d.lower_twist_angle = p->lower_twist_angle;
      if (p->has_upper_twist_angle)
        d.upper_twist_angle = p->upper_twist_angle;
    }
  }
  if (s->has_motor) {
    const LubJointMotorDesc3d *p = &s->motor;
    d.enable_motor = p->has_enabled ? p->enabled : true;
    if (p->has_speed)
      d.motor_speed = p->speed;
    if (p->has_max_force)
      d.max_force = p->max_force;
    if (p->has_max_torque)
      d.max_torque = p->max_torque;
    if (p->has_velocity)
      d.motor_velocity = p->velocity;
    if (p->has_linear_velocity)
      d.linear_velocity = p->linear_velocity;
    if (p->has_angular_velocity)
      d.angular_velocity = p->angular_velocity;
    if (p->has_max_velocity_force)
      d.max_velocity_force = p->max_velocity_force;
    if (p->has_max_velocity_torque)
      d.max_velocity_torque = p->max_velocity_torque;
  }
  return p3_joint(ctx, world, key, &d, out);
}

static void joint_view3_of(LubJointView3d *v, const P3JointView *j) {
  memset(v, 0, sizeof(*v));
  v->joint = j->key;
  v->type = j->type;
  v->a = j->a;
  v->b = j->b;
  v->valid = j->valid;
}

static void frame3_of(LubFrame3d *f, const P3Frame *p) {
  f->x = p->position.x;
  f->y = p->position.y;
  f->z = p->position.z;
  f->qx = p->rotation.x;
  f->qy = p->rotation.y;
  f->qz = p->rotation.z;
  f->qw = p->rotation.w;
}

LubStatus lub_phys3d_joint_info(LubContext *ctx, LubHandle joint,
                                LubJointInfo3d *out) {
  P3JointInfo i;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_joint_info(ctx, joint, &i);
  if (st != LUB_OK)
    return st;
  joint_view3_of(&out->base, &i.view);
  out->collide_connected = i.collide_connected;
  out->force = i.force;
  out->torque = i.torque;
  out->linear_separation = i.linear_separation;
  out->angular_separation = i.angular_separation;
  frame3_of(&out->local_frame_a, &i.local_frame_a);
  frame3_of(&out->local_frame_b, &i.local_frame_b);
  return LUB_OK;
}

LubStatus lub_phys3d_joint_force(LubContext *ctx, LubHandle joint,
                                 LubVec3d *out) {
  return p3_joint_force(ctx, joint, out);
}

LubStatus lub_phys3d_joint_torque(LubContext *ctx, LubHandle joint,
                                  LubVec3d *out) {
  return p3_joint_torque(ctx, joint, out);
}

LubStatus lub_phys3d_joint_angle(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has) {
  return p3_joint_angle(ctx, joint, out, has);
}

LubStatus lub_phys3d_joint_translation(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has) {
  return p3_joint_translation(ctx, joint, out, has);
}

LubStatus lub_phys3d_joint_speed(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has) {
  return p3_joint_speed(ctx, joint, out, has);
}

LubStatus lub_phys3d_joint_length(LubContext *ctx, LubHandle joint, float *out,
                                  bool *has) {
  return p3_joint_length(ctx, joint, out, has);
}

LubStatus lub_phys3d_joint_motor_force(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has) {
  return p3_joint_motor_force(ctx, joint, out, has);
}

LubStatus lub_phys3d_joint_motor_torque(LubContext *ctx, LubHandle joint,
                                        float *out, bool *has) {
  LubVec3d vector;
  bool has_vector = false;
  return p3_joint_motor_torque(ctx, joint, out, has, &vector, &has_vector);
}

LubStatus lub_phys3d_joint_motor_torque_vector(LubContext *ctx,
                                               LubHandle joint, LubVec3d *out,
                                               bool *has) {
  float scalar = 0;
  bool has_scalar = false;
  memset(out, 0, sizeof(*out));
  return p3_joint_motor_torque(ctx, joint, &scalar, &has_scalar, out, has);
}

LubStatus lub_phys3d_joint_set_motor(LubContext *ctx, LubHandle joint,
                                     const LubJointMotorDesc3d *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_joint_set_motor: desc required");
  P3JointMotor d;
  memset(&d, 0, sizeof(d));
  d.enabled = s->has_enabled ? s->enabled : true;
  d.speed = OPT3(s, speed, 0.0f);
  d.max_force = OPT3(s, max_force, 0.0f);
  d.max_torque = OPT3(s, max_torque, 0.0f);
  d.has_velocity = s->has_velocity;
  d.velocity = s->velocity;
  d.has_linear_velocity = s->has_linear_velocity;
  d.linear_velocity = s->linear_velocity;
  d.has_angular_velocity = s->has_angular_velocity;
  d.angular_velocity = s->angular_velocity;
  d.has_max_velocity_force = s->has_max_velocity_force;
  d.max_velocity_force = s->max_velocity_force;
  d.has_max_velocity_torque = s->has_max_velocity_torque;
  d.max_velocity_torque = s->max_velocity_torque;
  return p3_joint_set_motor(ctx, joint, &d);
}

LubStatus lub_phys3d_joint_set_limit(LubContext *ctx, LubHandle joint,
                                     const LubJointLimitDesc3d *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_joint_set_limit: desc required");
  P3JointLimit d;
  memset(&d, 0, sizeof(d));
  d.enabled = s->has_enabled ? s->enabled : true;
  d.lower = OPT3(s, lower, 0.0f);
  d.upper = OPT3(s, upper, 0.0f);
  d.min_length = OPT3(s, min_length, 0.0f);
  d.max_length = OPT3(s, max_length, FLT_MAX);
  d.has_cone_angle = s->has_cone_angle;
  d.cone_angle = s->cone_angle;
  if (s->has_lower_twist_angle || s->has_upper_twist_angle) {
    d.has_twist = true;
    d.lower = OPT3(s, lower_twist_angle, d.lower);
    d.upper = OPT3(s, upper_twist_angle, d.upper);
  }
  return p3_joint_set_limit(ctx, joint, &d);
}

LubStatus lub_phys3d_joint_set_spring(LubContext *ctx, LubHandle joint,
                                      const LubJointSpringDesc3d *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_joint_set_spring: desc required");
  P3JointSpring d;
  memset(&d, 0, sizeof(d));
  d.enabled = s->has_enabled ? s->enabled : true;
  d.hertz = OPT3(s, hertz, 0.0f);
  d.damping_ratio = OPT3(s, damping_ratio, 0.0f);
  d.linear_hertz = OPT3(s, linear_hertz, d.hertz);
  d.linear_damping_ratio = OPT3(s, linear_damping_ratio, d.damping_ratio);
  d.angular_hertz = OPT3(s, angular_hertz, d.hertz);
  d.angular_damping_ratio = OPT3(s, angular_damping_ratio, d.damping_ratio);
  d.has_max_torque = s->has_max_torque;
  d.max_torque = s->max_torque;
  return p3_joint_set_spring(ctx, joint, &d);
}

LubStatus lub_phys3d_joint_set_target(LubContext *ctx, LubHandle joint,
                                      const LubJointTargetDesc3d *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_joint_set_target: desc required");
  P3JointTarget d;
  memset(&d, 0, sizeof(d));
  d.has_translation = s->has_translation;
  d.translation = s->translation;
  if (s->has_angle) {
    d.has_angle = true;
    d.angle = s->angle;
  } else if (s->has_steering_angle) {
    d.has_angle = true;
    d.angle = s->steering_angle;
  }
  d.rotation = quat3_identity();
  d.has_rotation =
      rotation_of(s->has_quat, s->quat, s->has_euler, s->euler, &d.rotation);
  d.has_linear_velocity = s->has_linear_velocity;
  d.linear_velocity = s->linear_velocity;
  d.has_angular_velocity = s->has_angular_velocity;
  d.angular_velocity = s->angular_velocity;
  return p3_joint_set_target(ctx, joint, &d);
}

static GrowBuf3 g_joint_views3;

LubStatus lub_phys3d_body_joints(LubContext *ctx, LubHandle body,
                                 const LubJointView3d **out,
                                 int32_t *out_count) {
  const P3JointView *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p3_body_joints(ctx, body, &items, &count);
  if (st != LUB_OK)
    return st;
  g_joint_views3.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubJointView3d *v = (LubJointView3d *)grow3_push(&g_joint_views3, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    joint_view3_of(v, &items[i]);
  }
  *out = (const LubJointView3d *)g_joint_views3.ptr;
  *out_count = g_joint_views3.n;
  return LUB_OK;
}

// ---------------------------------------------------------------- movers

static void mover3_from(const LubMoverDesc3d *q, P3Mover *m,
                        LubVec3d *translation) {
  m->a = q->a;
  m->b = q->b;
  m->r = q->r;
  float mf = OPT3(q, max_fraction, 1.0f);
  *translation = vec3d(OPT3(q, dx, 0.0f) * mf, OPT3(q, dy, 0.0f) * mf,
                       OPT3(q, dz, 0.0f) * mf);
}

LubStatus lub_phys3d_cast_mover(LubContext *ctx, LubHandle world,
                                const LubMoverDesc3d *query,
                                LubMoverCast3d *out, bool *has) {
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys3d_cast_mover: query required");
  P3Mover m;
  LubVec3d t;
  mover3_from(query, &m, &t);
  P3QueryFilter qf = qfilter3_from(&query->filter, query->has_filter);
  float fraction = 0;
  LubStatus st = p3_cast_mover(ctx, world, &m, t, &qf, &fraction);
  if (st != LUB_OK)
    return st;
  out->fraction = fraction;
  out->dx = t.x * fraction;
  out->dy = t.y * fraction;
  out->dz = t.z * fraction;
  *has = true;
  return LUB_OK;
}

typedef struct PlaneShim3 {
  bool (*visitor)(void *user, const LubMoverPlane3d *plane);
  void *user;
} PlaneShim3;

static GrowBuf3 g_mover_planes3;

static bool plane3_shim_tramp(void *user, const P3MoverPlane *p) {
  PlaneShim3 *s = (PlaneShim3 *)user;
  LubMoverPlane3d v;
  memset(&v, 0, sizeof(v));
  shape_view3_of(&v.base, &p->shape);
  v.x = p->point.x;
  v.y = p->point.y;
  v.z = p->point.z;
  v.nx = p->normal.x;
  v.ny = p->normal.y;
  v.nz = p->normal.z;
  v.offset = p->offset;
  v.plane_count = p->plane_count;
  bool keep = s->visitor ? s->visitor(s->user, &v) : true;
  LubMoverPlane3d *slot =
      (LubMoverPlane3d *)grow3_push(&g_mover_planes3, sizeof(*slot));
  if (slot)
    *slot = v;
  return keep;
}

LubStatus lub_phys3d_collide_mover(LubContext *ctx, LubHandle world,
                                   const LubMoverDesc3d *query,
                                   LubPhys3dCollideMoverVisitorFn visitor,
                                   void *visitor_user,
                                   const LubMoverPlane3d **out,
                                   int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys3d_collide_mover: query required");
  P3Mover m;
  LubVec3d t;
  mover3_from(query, &m, &t);
  P3QueryFilter qf = qfilter3_from(&query->filter, query->has_filter);
  g_mover_planes3.n = 0;
  PlaneShim3 s = {visitor, visitor_user};
  LubStatus st = p3_collide_mover(ctx, world, &m, &qf, plane3_shim_tramp, &s);
  if (st != LUB_OK)
    return st;
  *out = (const LubMoverPlane3d *)g_mover_planes3.ptr;
  *out_count = g_mover_planes3.n;
  return LUB_OK;
}

// ------------------------------------------------------------ step / pose

LubStatus lub_phys3d_step(LubContext *ctx, LubHandle world, float dt,
                          LubStepInfo3d *out) {
  P3StepInfo i;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_step(ctx, world, dt, &i);
  if (st != LUB_OK)
    return st;
  out->base.steps = i.steps;
  out->base.commands = i.commands;
  out->base.alpha = i.alpha;
  out->base.dropped = i.dropped;
  out->base.contact_begins = i.contact_begins;
  out->base.contact_ends = i.contact_ends;
  out->base.contact_hits = i.contact_hits;
  out->base.sensor_begins = i.sensor_begins;
  out->base.sensor_ends = i.sensor_ends;
  out->base.body_moves = i.body_moves;
  out->base.body_events = i.body_moves;
  out->joint_events = i.joint_events;
  return LUB_OK;
}

LubStatus lub_phys3d_pose(LubContext *ctx, LubHandle body, LubPose3d *out) {
  P3Pose p;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_pose(ctx, body, &p);
  if (st != LUB_OK)
    return st;
  out->x = p.position.x;
  out->y = p.position.y;
  out->z = p.position.z;
  out->qx = p.rotation.x;
  out->qy = p.rotation.y;
  out->qz = p.rotation.z;
  out->qw = p.rotation.w;
  out->vx = p.linear_velocity.x;
  out->vy = p.linear_velocity.y;
  out->vz = p.linear_velocity.z;
  out->wx = p.angular_velocity.x;
  out->wy = p.angular_velocity.y;
  out->wz = p.angular_velocity.z;
  out->awake = p.awake;
  out->enabled = p.enabled;
  out->sleep = p.sleep;
  out->sleep_threshold = p.sleep_threshold;
  return LUB_OK;
}

LubStatus lub_phys3d_pose_by_key(LubContext *ctx, LubHandle world, LubStr key,
                                 LubPose3d *out) {
  LubHandle body = p3_body_find(ctx, world, key);
  if (!body) {
    memset(out, 0, sizeof(*out));
    return LUB_NOT_FOUND;
  }
  return lub_phys3d_pose(ctx, body, out);
}

LubStatus lub_phys3d_velocity(LubContext *ctx, LubHandle body,
                              LubVelocity3d *out) {
  P3Velocity v;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_velocity(ctx, body, &v);
  if (st != LUB_OK)
    return st;
  out->x = v.linear.x;
  out->y = v.linear.y;
  out->z = v.linear.z;
  out->wx = v.angular.x;
  out->wy = v.angular.y;
  out->wz = v.angular.z;
  return LUB_OK;
}

LubStatus lub_phys3d_mass(LubContext *ctx, LubHandle body, LubMassData3d *out) {
  P3MassData m;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_mass(ctx, body, &m);
  if (st != LUB_OK)
    return st;
  out->mass = m.mass;
  out->center = m.center;
  out->local_center = m.local_center;
  out->inertia.xx = m.xx;
  out->inertia.yy = m.yy;
  out->inertia.zz = m.zz;
  out->inertia.xy = m.xy;
  out->inertia.xz = m.xz;
  out->inertia.yz = m.yz;
  return LUB_OK;
}

LubStatus lub_phys3d_center(LubContext *ctx, LubHandle body, LubVec3d *out) {
  return p3_center(ctx, body, out);
}

LubStatus lub_phys3d_world_point(LubContext *ctx, LubHandle body,
                                 const LubVec3d *p, LubVec3d *out) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys3d_world_point: point required");
  return p3_world_point(ctx, body, *p, out);
}

LubStatus lub_phys3d_local_point(LubContext *ctx, LubHandle body,
                                 const LubVec3d *p, LubVec3d *out) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys3d_local_point: point required");
  return p3_local_point(ctx, body, *p, out);
}

LubStatus lub_phys3d_velocity_at(LubContext *ctx, LubHandle body,
                                 const LubVec3d *p, LubVec3d *out) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys3d_velocity_at: point required");
  return p3_velocity_at(ctx, body, *p, out);
}

// -------------------------------------------------------------- commands

static bool opt_wake3(const LubCommandOpts3d *o) {
  return o && o->has_wake ? o->wake : true;
}

LubStatus lub_phys3d_add_force(LubContext *ctx, LubHandle body,
                               const LubVec3d *force,
                               const LubCommandOpts3d *o) {
  if (!force)
    return lub_api_fail(lub_api_app(ctx), "phys3d_add_force: force required");
  const LubVec3d *point = o && o->has_point ? &o->point : NULL;
  return p3_add_force(ctx, body, *force, point, opt_wake3(o));
}

LubStatus lub_phys3d_add_force_center(LubContext *ctx, LubHandle body,
                                      const LubVec3d *force,
                                      const LubCommandOpts3d *o) {
  if (!force)
    return lub_api_fail(lub_api_app(ctx), "phys3d_add_force_center: force required");
  return p3_add_force_center(ctx, body, *force, opt_wake3(o));
}

LubStatus lub_phys3d_add_impulse(LubContext *ctx, LubHandle body,
                                 const LubVec3d *impulse,
                                 const LubCommandOpts3d *o) {
  if (!impulse)
    return lub_api_fail(lub_api_app(ctx), "phys3d_add_impulse: impulse required");
  const LubVec3d *point = o && o->has_point ? &o->point : NULL;
  return p3_add_impulse(ctx, body, *impulse, point, opt_wake3(o));
}

LubStatus lub_phys3d_add_impulse_center(LubContext *ctx, LubHandle body,
                                        const LubVec3d *impulse,
                                        const LubCommandOpts3d *o) {
  if (!impulse)
    return lub_api_fail(lub_api_app(ctx), "phys3d_add_impulse_center: impulse required");
  return p3_add_impulse_center(ctx, body, *impulse, opt_wake3(o));
}

LubStatus lub_phys3d_add_torque(LubContext *ctx, LubHandle body,
                                const LubVec3d *torque,
                                const LubCommandOpts3d *o) {
  if (!torque)
    return lub_api_fail(lub_api_app(ctx), "phys3d_add_torque: torque required");
  return p3_add_torque(ctx, body, *torque, opt_wake3(o));
}

LubStatus lub_phys3d_add_angular_impulse(LubContext *ctx, LubHandle body,
                                         const LubVec3d *impulse,
                                         const LubCommandOpts3d *o) {
  if (!impulse)
    return lub_api_fail(lub_api_app(ctx), "phys3d_add_angular_impulse: impulse required");
  return p3_add_angular_impulse(ctx, body, *impulse, opt_wake3(o));
}

LubStatus lub_phys3d_set_velocity(LubContext *ctx, LubHandle body,
                                  const LubVelocityDesc3d *v) {
  if (!v)
    return lub_api_fail(lub_api_app(ctx), "phys3d_set_velocity: desc required");
  P3SetVelocity d;
  memset(&d, 0, sizeof(d));
  d.has_vx = v->has_vx;
  d.linear.x = v->vx;
  d.has_vy = v->has_vy;
  d.linear.y = v->vy;
  d.has_vz = v->has_vz;
  d.linear.z = v->vz;
  d.has_wx = v->has_wx;
  d.angular.x = v->wx;
  d.has_wy = v->has_wy;
  d.angular.y = v->wy;
  d.has_wz = v->has_wz;
  d.angular.z = v->wz;
  d.wake = true;
  return p3_set_velocity(ctx, body, &d);
}

LubStatus lub_phys3d_teleport(LubContext *ctx, LubHandle body,
                              const LubPoseDesc3d *p) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys3d_teleport: desc required");
  P3Teleport d;
  memset(&d, 0, sizeof(d));
  d.has_x = p->has_x;
  d.position.x = p->x;
  d.has_y = p->has_y;
  d.position.y = p->y;
  d.has_z = p->has_z;
  d.position.z = p->z;
  d.rotation = quat3_identity();
  d.has_rotation =
      rotation_of(p->has_quat, p->quat, p->has_euler, p->euler, &d.rotation);
  d.wake = true;
  return p3_teleport(ctx, body, &d);
}

LubStatus lub_phys3d_set_target(LubContext *ctx, LubHandle body,
                                const LubTargetDesc3d *p) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys3d_set_target: desc required");
  P3SetTarget d;
  memset(&d, 0, sizeof(d));
  d.has_x = p->has_x;
  d.position.x = p->x;
  d.has_y = p->has_y;
  d.position.y = p->y;
  d.has_z = p->has_z;
  d.position.z = p->z;
  d.rotation = quat3_identity();
  d.has_rotation =
      rotation_of(p->has_quat, p->quat, p->has_euler, p->euler, &d.rotation);
  d.time_step = OPT3(p, time_step, 0.0f);
  d.wake = p->has_wake ? p->wake : true;
  return p3_set_target(ctx, body, &d);
}

// ---------------------------------------------------------------- events

static GrowBuf3 g_contact_events3;
static GrowBuf3 g_sensor_events3;
static GrowBuf3 g_body_events3;
static GrowBuf3 g_joint_events3;

LubStatus lub_phys3d_contacts(LubContext *ctx, LubHandle world,
                              const int32_t *kind_p,
                              const LubContactEvent3d **out,
                              int32_t *out_count) {
  int32_t kind = kind_p ? *kind_p : LUB_PHYS3D_EVENT_KIND_BEGIN;
  const P3Contact *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p3_contacts(ctx, world, kind, &items, &count);
  if (st != LUB_OK)
    return st;
  g_contact_events3.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubContactEvent3d *v =
        (LubContactEvent3d *)grow3_push(&g_contact_events3, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    shape_view3_of(&v->a, &items[i].a);
    shape_view3_of(&v->b, &items[i].b);
    v->nx = items[i].normal.x;
    v->ny = items[i].normal.y;
    v->nz = items[i].normal.z;
    v->point_count = items[i].point_count;
    v->x = items[i].point.x;
    v->y = items[i].point.y;
    v->z = items[i].point.z;
    if (kind == LUB_PHYS3D_EVENT_KIND_HIT) {
      v->has_approach_speed = true;
      v->approach_speed = items[i].approach_speed;
    }
  }
  *out = (const LubContactEvent3d *)g_contact_events3.ptr;
  *out_count = g_contact_events3.n;
  return LUB_OK;
}

LubStatus lub_phys3d_body_events(LubContext *ctx, LubHandle world,
                                 const LubBodyEvent3d **out,
                                 int32_t *out_count) {
  const P3BodyEvent *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p3_body_events(ctx, world, &items, &count);
  if (st != LUB_OK)
    return st;
  g_body_events3.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubBodyEvent3d *v = (LubBodyEvent3d *)grow3_push(&g_body_events3, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    v->body = items[i].body;
    v->valid = items[i].valid;
    v->x = items[i].position.x;
    v->y = items[i].position.y;
    v->z = items[i].position.z;
    v->qx = items[i].rotation.x;
    v->qy = items[i].rotation.y;
    v->qz = items[i].rotation.z;
    v->qw = items[i].rotation.w;
    v->fell_asleep = items[i].fell_asleep;
  }
  *out = (const LubBodyEvent3d *)g_body_events3.ptr;
  *out_count = g_body_events3.n;
  return LUB_OK;
}

LubStatus lub_phys3d_sensors(LubContext *ctx, LubHandle world,
                             const int32_t *kind_p,
                             const LubSensorEvent3d **out, int32_t *out_count) {
  int32_t kind = kind_p ? *kind_p : LUB_PHYS3D_EVENT_KIND_BEGIN;
  const P3Contact *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p3_sensors(ctx, world, kind, &items, &count);
  if (st != LUB_OK)
    return st;
  g_sensor_events3.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubSensorEvent3d *v =
        (LubSensorEvent3d *)grow3_push(&g_sensor_events3, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    shape_view3_of(&v->sensor, &items[i].a);
    shape_view3_of(&v->visitor, &items[i].b);
  }
  *out = (const LubSensorEvent3d *)g_sensor_events3.ptr;
  *out_count = g_sensor_events3.n;
  return LUB_OK;
}

LubStatus lub_phys3d_joint_events(LubContext *ctx, LubHandle world,
                                  const LubJointEvent3d **out,
                                  int32_t *out_count) {
  const P3JointEvent *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p3_joint_events(ctx, world, &items, &count);
  if (st != LUB_OK)
    return st;
  g_joint_events3.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubJointEvent3d *v =
        (LubJointEvent3d *)grow3_push(&g_joint_events3, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    v->base.joint = items[i].joint;
    v->base.type = items[i].type;
    v->base.a = items[i].a;
    v->base.b = items[i].b;
    v->base.valid = items[i].valid;
  }
  *out = (const LubJointEvent3d *)g_joint_events3.ptr;
  *out_count = g_joint_events3.n;
  return LUB_OK;
}

// --------------------------------------------------------------- queries

static void ray3_from(const LubRaycastDesc3d *q, P3Ray *r) {
  memset(r, 0, sizeof(*r));
  r->origin = vec3d(OPT3(q, x, 0.0f), OPT3(q, y, 0.0f), OPT3(q, z, 0.0f));
  float mf = OPT3(q, max_fraction, 1.0f);
  r->translation = vec3d(OPT3(q, dx, 0.0f) * mf, OPT3(q, dy, 0.0f) * mf,
                         OPT3(q, dz, 0.0f) * mf);
}

static void ray_hit3_of(LubRayHit3d *v, const P3RayHit *h, bool with_stats) {
  memset(v, 0, sizeof(*v));
  shape_view3_of(&v->base, &h->shape);
  v->x = h->point.x;
  v->y = h->point.y;
  v->z = h->point.z;
  v->nx = h->normal.x;
  v->ny = h->normal.y;
  v->nz = h->normal.z;
  v->fraction = h->fraction;
  v->hit_material_id = h->hit_material_id;
  v->triangle_index = h->triangle_index;
  v->child_index = h->child_index;
  if (with_stats) {
    v->has_node_visits = v->has_leaf_visits = true;
    v->node_visits = h->node_visits;
    v->leaf_visits = h->leaf_visits;
  }
}

typedef struct RayShim3 {
  GrowBuf3 *buf;
  float (*visitor)(void *user, const LubRayHit3d *hit);
  void *user;
  bool closest;
  bool has_hit;
  LubRayHit3d best;
} RayShim3;

static float ray3_shim_tramp(void *user, const P3RayHit *h) {
  RayShim3 *s = (RayShim3 *)user;
  LubRayHit3d v;
  ray_hit3_of(&v, h, false);
  if (s->closest) {
    if (!s->has_hit || v.fraction < s->best.fraction) {
      s->best = v;
      s->has_hit = true;
    }
    return v.fraction;
  }
  float r = s->visitor ? s->visitor(s->user, &v) : 1.0f;
  if (r >= 0.0f) {
    LubRayHit3d *slot = (LubRayHit3d *)grow3_push(s->buf, sizeof(*slot));
    if (slot)
      *slot = v;
  }
  return r;
}

static GrowBuf3 g_ray_hits3;

LubStatus lub_phys3d_raycast(LubContext *ctx, LubHandle world,
                             const LubRaycastDesc3d *query, LubRayHit3d *out,
                             bool *has) {
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys3d_raycast: query required");
  P3Ray ray;
  ray3_from(query, &ray);
  P3QueryFilter qf = qfilter3_from(&query->filter, query->has_filter);
  P3RayHit h;
  LubStatus st = p3_raycast_closest(ctx, world, &ray, &qf, &h, has);
  if (st != LUB_OK || !*has)
    return st;
  ray_hit3_of(out, &h, true);
  return LUB_OK;
}

LubStatus lub_phys3d_raycast_all(LubContext *ctx, LubHandle world,
                                 const LubRaycastDesc3d *query,
                                 LubPhys3dRaycastAllVisitorFn visitor,
                                 void *visitor_user, const LubRayHit3d **out,
                                 int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys3d_raycast: query required");
  P3Ray ray;
  ray3_from(query, &ray);
  P3QueryFilter qf = qfilter3_from(&query->filter, query->has_filter);
  g_ray_hits3.n = 0;
  RayShim3 s;
  memset(&s, 0, sizeof(s));
  s.buf = &g_ray_hits3;
  s.visitor = visitor;
  s.user = visitor_user;
  P3TreeStats stats;
  LubStatus st =
      p3_raycast(ctx, world, &ray, &qf, ray3_shim_tramp, &s, &stats);
  if (st != LUB_OK)
    return st;
  *out = (const LubRayHit3d *)g_ray_hits3.ptr;
  *out_count = g_ray_hits3.n;
  return LUB_OK;
}

typedef struct OverlapShim3 {
  bool (*visitor)(void *user, const LubShapeView3d *shape);
  void *user;
} OverlapShim3;

static bool overlap3_shim_tramp(void *user, const P3ShapePart *p) {
  OverlapShim3 *s = (OverlapShim3 *)user;
  LubShapeView3d v;
  shape_view3_of(&v, p);
  bool keep = s->visitor ? s->visitor(s->user, &v) : true;
  LubShapeView3d *slot =
      (LubShapeView3d *)grow3_push(&g_shape_views3, sizeof(*slot));
  if (slot)
    *slot = v;
  return keep;
}

LubStatus lub_phys3d_overlap_aabb(LubContext *ctx, LubHandle world,
                                  const LubAabbDesc3d *query,
                                  LubPhys3dOverlapAabbVisitorFn visitor,
                                  void *visitor_user,
                                  const LubShapeView3d **out,
                                  int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys3d_overlap_aabb: query required");
  P3Aabb aabb;
  aabb.min = vec3d(query->min_x, query->min_y, query->min_z);
  aabb.max = vec3d(query->max_x, query->max_y, query->max_z);
  P3QueryFilter qf = qfilter3_from(&query->filter, query->has_filter);
  g_shape_views3.n = 0;
  OverlapShim3 s = {visitor, visitor_user};
  P3TreeStats stats;
  LubStatus st =
      p3_overlap_aabb(ctx, world, &aabb, &qf, overlap3_shim_tramp, &s, &stats);
  if (st != LUB_OK)
    return st;
  *out = (const LubShapeView3d *)g_shape_views3.ptr;
  *out_count = g_shape_views3.n;
  return LUB_OK;
}

static bool proxy3_from(LubContext *ctx, const LubShapeProxyDesc3d *q,
                        P3ShapeProxy *p, LubVec3d *translation) {
  memset(p, 0, sizeof(*p));
  p->rotation = quat3_identity();
  if (q->has_sphere) {
    p->kind = P3_PROXY_SPHERE;
    p->r = q->sphere.r;
    if (q->sphere.has_center)
      p->center = q->sphere.center;
  } else if (q->has_box) {
    p->kind = P3_PROXY_BOX;
    p->hx = q->box.hx;
    p->hy = q->box.hy;
    p->hz = q->box.hz;
    p->r = OPT3(&q->box, radius, 0.0f);
    if (q->box.has_center)
      p->center = q->box.center;
    if (q->box.has_quat) {
      p->has_rotation = true;
      p->rotation = quat3_normalized(q->box.quat);
    }
  } else if (q->has_capsule) {
    p->kind = P3_PROXY_CAPSULE;
    p->a = q->capsule.a;
    p->b = q->capsule.b;
    p->r = q->capsule.r;
  } else {
    lub_api_fail(lub_api_app(ctx), "phys3d query: sphere, box, or capsule required");
    return false;
  }
  float mf = OPT3(q, max_fraction, 1.0f);
  *translation = vec3d(OPT3(q, dx, 0.0f) * mf, OPT3(q, dy, 0.0f) * mf,
                       OPT3(q, dz, 0.0f) * mf);
  return true;
}

LubStatus lub_phys3d_overlap_shape(LubContext *ctx, LubHandle world,
                                   const LubShapeProxyDesc3d *query,
                                   LubPhys3dOverlapShapeVisitorFn visitor,
                                   void *visitor_user,
                                   const LubShapeView3d **out,
                                   int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys3d_overlap_shape: query required");
  P3ShapeProxy p;
  LubVec3d t;
  if (!proxy3_from(ctx, query, &p, &t))
    return LUB_ERROR;
  P3QueryFilter qf = qfilter3_from(&query->filter, query->has_filter);
  g_shape_views3.n = 0;
  OverlapShim3 s = {visitor, visitor_user};
  P3TreeStats stats;
  LubStatus st =
      p3_overlap_shape(ctx, world, &p, &qf, overlap3_shim_tramp, &s, &stats);
  if (st != LUB_OK)
    return st;
  *out = (const LubShapeView3d *)g_shape_views3.ptr;
  *out_count = g_shape_views3.n;
  return LUB_OK;
}

LubStatus lub_phys3d_shape_cast(LubContext *ctx, LubHandle world,
                                const LubShapeProxyDesc3d *query,
                                LubRayHit3d *out, bool *has) {
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys3d_shape_cast: query required");
  P3ShapeProxy p;
  LubVec3d t;
  if (!proxy3_from(ctx, query, &p, &t))
    return LUB_ERROR;
  P3QueryFilter qf = qfilter3_from(&query->filter, query->has_filter);
  RayShim3 s;
  memset(&s, 0, sizeof(s));
  s.closest = true;
  P3TreeStats stats;
  LubStatus st =
      p3_shape_cast(ctx, world, &p, t, &qf, ray3_shim_tramp, &s, &stats);
  if (st != LUB_OK)
    return st;
  if (s.has_hit) {
    *out = s.best;
    *has = true;
  }
  return LUB_OK;
}

LubStatus lub_phys3d_shape_cast_all(LubContext *ctx, LubHandle world,
                                    const LubShapeProxyDesc3d *query,
                                    LubPhys3dShapeCastAllVisitorFn visitor,
                                    void *visitor_user,
                                    const LubRayHit3d **out,
                                    int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys3d_shape_cast: query required");
  P3ShapeProxy p;
  LubVec3d t;
  if (!proxy3_from(ctx, query, &p, &t))
    return LUB_ERROR;
  P3QueryFilter qf = qfilter3_from(&query->filter, query->has_filter);
  g_ray_hits3.n = 0;
  RayShim3 s;
  memset(&s, 0, sizeof(s));
  s.buf = &g_ray_hits3;
  s.visitor = visitor;
  s.user = visitor_user;
  P3TreeStats stats;
  LubStatus st =
      p3_shape_cast(ctx, world, &p, t, &qf, ray3_shim_tramp, &s, &stats);
  if (st != LUB_OK)
    return st;
  *out = (const LubRayHit3d *)g_ray_hits3.ptr;
  *out_count = g_ray_hits3.n;
  return LUB_OK;
}

LubStatus lub_phys3d_body_shapes(LubContext *ctx, LubHandle body,
                                 const LubShapeView3d **out,
                                 int32_t *out_count) {
  const P3ShapePart *items = NULL;
  int32_t count = 0;
  LubStatus st = p3_body_shapes(ctx, body, &items, &count);
  return shape_views3_out(st, items, count, out, out_count);
}

static GrowBuf3 g_contact_datas3;

LubStatus lub_phys3d_body_contacts(LubContext *ctx, LubHandle body,
                                   const LubContactData3d **out,
                                   int32_t *out_count) {
  const P3ContactData *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p3_body_contacts(ctx, body, &items, &count);
  if (st != LUB_OK)
    return st;
  g_contact_datas3.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubContactData3d *v =
        (LubContactData3d *)grow3_push(&g_contact_datas3, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    shape_view3_of(&v->a, &items[i].a);
    shape_view3_of(&v->b, &items[i].b);
    v->nx = items[i].normal.x;
    v->ny = items[i].normal.y;
    v->nz = items[i].normal.z;
    v->manifold_count = items[i].manifold_count;
    v->point_count = items[i].point_count;
    if (items[i].has_point) {
      v->has_x = v->has_y = v->has_z = v->has_separation = true;
      v->x = items[i].point.x;
      v->y = items[i].point.y;
      v->z = items[i].point.z;
      v->separation = items[i].separation;
    }
  }
  *out = (const LubContactData3d *)g_contact_datas3.ptr;
  *out_count = g_contact_datas3.n;
  return LUB_OK;
}

LubStatus lub_phys3d_shape_raycast(LubContext *ctx, LubHandle shape,
                                   const LubRaycastDesc3d *query,
                                   LubShapeRayHit3d *out, bool *has) {
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys3d_shape_raycast: query required");
  P3Ray ray;
  ray3_from(query, &ray);
  P3RayHit h;
  LubStatus st = p3_shape_raycast(ctx, shape, &ray, &h, has);
  if (st != LUB_OK || !*has)
    return st;
  out->x = h.point.x;
  out->y = h.point.y;
  out->z = h.point.z;
  out->nx = h.normal.x;
  out->ny = h.normal.y;
  out->nz = h.normal.z;
  out->fraction = h.fraction;
  out->iterations = h.iterations;
  out->triangle_index = h.triangle_index;
  out->child_index = h.child_index;
  return LUB_OK;
}

LubStatus lub_phys3d_shape_closest_point(LubContext *ctx, LubHandle shape,
                                         const LubVec3d *p, LubVec3d *out) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys3d_shape_closest_point: point required");
  return p3_shape_closest_point(ctx, shape, *p, out);
}

LubStatus lub_phys3d_shape_aabb(LubContext *ctx, LubHandle shape,
                                LubAabb3d *out) {
  P3Aabb a;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_shape_aabb(ctx, shape, &a);
  if (st != LUB_OK)
    return st;
  out->min_x = a.min.x;
  out->min_y = a.min.y;
  out->min_z = a.min.z;
  out->max_x = a.max.x;
  out->max_y = a.max.y;
  out->max_z = a.max.z;
  return LUB_OK;
}

LubStatus lub_phys3d_shape_info(LubContext *ctx, LubHandle shape,
                                LubShapeInfo3d *out) {
  P3ShapeInfo i;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_shape_info(ctx, shape, &i);
  if (st != LUB_OK)
    return st;
  shape_view3_of(&out->base, &i.part);
  out->density = i.density;
  out->friction = i.friction;
  out->restitution = i.restitution;
  out->sensor = i.sensor;
  out->sensor_events = i.sensor_events;
  out->contact = i.contact;
  out->pre_solve = i.pre_solve;
  out->hit = i.hit;
  if (i.part.has_filter) {
    out->filter.category_bits = i.part.filter.category_bits;
    out->filter.mask_bits = i.part.filter.mask_bits;
    out->filter.group = i.part.filter.group_index;
  }
  out->aabb.min_x = i.aabb.min.x;
  out->aabb.min_y = i.aabb.min.y;
  out->aabb.min_z = i.aabb.min.z;
  out->aabb.max_x = i.aabb.max.x;
  out->aabb.max_y = i.aabb.max.y;
  out->aabb.max_z = i.aabb.max.z;
  return LUB_OK;
}

LubStatus lub_phys3d_shape_set_material(LubContext *ctx, LubHandle shape,
                                        const LubMaterialDesc3d *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_shape_set_material: desc required");
  P3MaterialDesc d;
  memset(&d, 0, sizeof(d));
  d.has_density = s->has_density;
  d.density = s->density;
  d.has_friction = s->has_friction;
  d.friction = s->friction;
  d.has_restitution = s->has_restitution;
  d.restitution = s->restitution;
  d.has_material_id = s->has_material_id;
  d.material_id = s->material_id;
  d.has_material_name = s->material_name.ptr != NULL;
  d.material_name = s->material_name;
  return p3_shape_set_material(ctx, shape, &d);
}

LubStatus lub_phys3d_shape_set_filter(LubContext *ctx, LubHandle shape,
                                      const LubFilterDesc3d *f) {
  P3Filter d = {1u, UINT64_MAX, 0};
  filter3_from_desc(f, f != NULL, &d);
  return p3_shape_set_filter(ctx, shape, &d);
}

LubStatus lub_phys3d_shape_set_events(LubContext *ctx, LubHandle shape,
                                      const LubShapeEventsDesc3d *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys3d_shape_set_events: desc required");
  P3EventFlags d;
  memset(&d, 0, sizeof(d));
  d.has_sensor_events = s->has_sensor_events;
  d.sensor_events = s->sensor_events;
  d.has_contact = s->has_contact;
  d.contact = s->contact;
  d.has_pre_solve = s->has_pre_solve;
  d.pre_solve = s->pre_solve;
  d.has_hit = s->has_hit;
  d.hit = s->hit;
  return p3_shape_set_events(ctx, shape, &d);
}

LubStatus lub_phys3d_profile(LubContext *ctx, LubHandle world,
                             LubProfile3d *out) {
  P3Profile p;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_profile(ctx, world, &p);
  if (st != LUB_OK)
    return st;
  out->step = p.step;
  out->pairs = p.pairs;
  out->collide = p.collide;
  out->solve = p.solve;
  out->solver_setup = p.solver_setup;
  out->constraints = p.constraints;
  out->prepare_constraints = p.prepare_constraints;
  out->integrate_velocities = p.integrate_velocities;
  out->warm_start = p.warm_start;
  out->solve_impulses = p.solve_impulses;
  out->integrate_positions = p.integrate_positions;
  out->relax_impulses = p.relax_impulses;
  out->apply_restitution = p.apply_restitution;
  out->store_impulses = p.store_impulses;
  out->split_islands = p.split_islands;
  out->transforms = p.transforms;
  out->sensor_hits = p.sensor_hits;
  out->joint_events = p.joint_events;
  out->hit_events = p.hit_events;
  out->refit = p.refit;
  out->bullets = p.bullets;
  out->sleep_islands = p.sleep_islands;
  out->sensors = p.sensors;
  return LUB_OK;
}

LubStatus lub_phys3d_counters(LubContext *ctx, LubHandle world,
                              LubCounters3d *out) {
  P3Counters c;
  memset(out, 0, sizeof(*out));
  LubStatus st = p3_counters(ctx, world, &c);
  if (st != LUB_OK)
    return st;
  out->body_count = c.body_count;
  out->shape_count = c.shape_count;
  out->contact_count = c.contact_count;
  out->joint_count = c.joint_count;
  out->island_count = c.island_count;
  out->stack_used = c.stack_used;
  out->arena_capacity = c.arena_capacity;
  out->static_tree_height = c.static_tree_height;
  out->tree_height = c.tree_height;
  out->sat_call_count = c.sat_call_count;
  out->sat_cache_hit_count = c.sat_cache_hit_count;
  out->byte_count = c.byte_count;
  out->task_count = c.task_count;
  out->awake_contact_count = c.awake_contact_count;
  out->recycled_contact_count = c.recycled_contact_count;
  out->distance_iterations = c.distance_iterations;
  out->push_back_iterations = c.push_back_iterations;
  out->root_iterations = c.root_iterations;
  memcpy(out->color_counts, c.color_counts, sizeof(out->color_counts));
  out->color_counts_count = 24;
  memcpy(out->manifold_counts, c.manifold_counts, sizeof(out->manifold_counts));
  out->manifold_counts_count = P3_MANIFOLD_COUNT_BUCKETS;
  return LUB_OK;
}

#undef OPT3
