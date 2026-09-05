// phys2d の公開 API (include/lub/lub_api.h、生成物) と core の内部形
// (phys2d_internal.h) の詰め替え。physics_box2d.c の末尾から include する
// (core の static 関数を使うため)。
//
// query の結果配列 (raycast_all 等) は runtime 所有で、同じ関数の次の呼び出し
// まで有効。callback (world の filter 等) は holder に包み、core が手放すとき
// に user_release を呼ぶ。

// ---------------------------------------------------------- growable

typedef struct GrowBuf {
  void *ptr;
  int32_t cap, n;
} GrowBuf;

static void *grow_push(GrowBuf *g, size_t size) {
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

// ------------------------------------------------------------- filters

static void filter_from_desc(const LubFilterDesc *f, bool has, P2Filter *out) {
  if (!has || !f)
    return;
  if (f->has_category_bits)
    out->category_bits = f->category_bits;
  if (f->has_mask_bits)
    out->mask_bits = f->mask_bits;
  if (f->has_group)
    out->group_index = f->group;
}

static P2QueryFilter qfilter_from(const LubFilterDesc *f, bool has) {
  P2QueryFilter q = {1u, UINT64_MAX};
  if (has && f) {
    if (f->has_category_bits)
      q.category_bits = f->category_bits;
    if (f->has_mask_bits)
      q.mask_bits = f->mask_bits;
  }
  return q;
}

static void shape_view_of(LubShapeView *v, const P2ShapePart *p) {
  memset(v, 0, sizeof(*v));
  v->body = p->body_key;
  v->shape = p->shape_key;
  v->tag = p->tag;
  v->chain = p->chain_key;
  if (p->kind == LUB_PHYS2D_SHAPE_KIND_CHAIN_SEGMENT) {
    v->has_segment = true;
    v->segment = true;
  }
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

static LubVec2d vec2d(float x, float y) {
  LubVec2d v = {x, y};
  return v;
}

// ------------------------------------------------------------ callbacks

typedef struct CbHolder {
  LubWorldCallbacks cb;
} CbHolder;

static void cb_holder_release(void *user) {
  CbHolder *h = (CbHolder *)user;
  if (!h)
    return;
  if (h->cb.user_release)
    h->cb.user_release(h->cb.user);
  free(h);
}

static bool cb_filter_tramp(void *user, const P2ShapePart *a,
                            const P2ShapePart *b) {
  CbHolder *h = (CbHolder *)user;
  LubShapeView va, vb;
  shape_view_of(&va, a);
  shape_view_of(&vb, b);
  return h->cb.filter(h->cb.user, &va, &vb);
}

static bool cb_pre_solve_tramp(void *user, const P2PreSolve *c) {
  CbHolder *h = (CbHolder *)user;
  LubPreSolveContact v;
  memset(&v, 0, sizeof(v));
  shape_view_of(&v.a, &c->a);
  shape_view_of(&v.b, &c->b);
  v.nx = c->nx;
  v.ny = c->ny;
  v.rolling_impulse = c->rolling_impulse;
  v.point_count = c->point_count;
  LubManifoldPoint pts[2];
  for (int i = 0; i < c->point_count && i < 2; ++i) {
    const P2ManifoldPoint *m = &c->points[i];
    pts[i].x = m->x;
    pts[i].y = m->y;
    pts[i].anchor_a_x = m->anchor_a_x;
    pts[i].anchor_a_y = m->anchor_a_y;
    pts[i].anchor_b_x = m->anchor_b_x;
    pts[i].anchor_b_y = m->anchor_b_y;
    pts[i].separation = m->separation;
    pts[i].normal_impulse = m->normal_impulse;
    pts[i].tangent_impulse = m->tangent_impulse;
    pts[i].total_normal_impulse = m->total_normal_impulse;
    pts[i].normal_velocity = m->normal_velocity;
    pts[i].id = m->id;
    pts[i].persisted = m->persisted;
  }
  v.points = pts;
  v.points_count = c->point_count < 2 ? c->point_count : 2;
  if (c->point_count > 0) {
    v.has_x = v.has_y = v.has_separation = v.has_normal_velocity = true;
    v.x = pts[0].x;
    v.y = pts[0].y;
    v.separation = pts[0].separation;
    v.normal_velocity = pts[0].normal_velocity;
  }
  return h->cb.pre_solve(h->cb.user, &v);
}

static float cb_friction_tramp(void *user, float fa, int32_t ma, float fb,
                               int32_t mb) {
  CbHolder *h = (CbHolder *)user;
  LubMaterialView a = {true, fa, false, 0, ma};
  LubMaterialView b = {true, fb, false, 0, mb};
  return h->cb.friction(h->cb.user, &a, &b);
}

static float cb_restitution_tramp(void *user, float ra, int32_t ma, float rb,
                                  int32_t mb) {
  CbHolder *h = (CbHolder *)user;
  LubMaterialView a = {false, 0, true, ra, ma};
  LubMaterialView b = {false, 0, true, rb, mb};
  return h->cb.restitution(h->cb.user, &a, &b);
}

// ---------------------------------------------------------------- world

LubHandle lub_phys2d_find_world(LubContext *ctx, LubStr key) {
  return p2_world_find(ctx, key);
}

LubHandle lub_phys2d_find_body(LubContext *ctx, LubHandle world, LubStr key) {
  return p2_body_find(ctx, world, key);
}

LubHandle lub_phys2d_find_shape(LubContext *ctx, LubHandle body, LubStr key) {
  return p2_shape_find(ctx, body, key);
}

LubHandle lub_phys2d_find_chain(LubContext *ctx, LubHandle body, LubStr key) {
  return p2_chain_find(ctx, body, key);
}

LubHandle lub_phys2d_find_joint(LubContext *ctx, LubHandle world, LubStr key) {
  return p2_joint_find(ctx, world, key);
}

LubStatus lub_phys2d_world(LubContext *ctx, LubStr key,
                           const LubWorldOpts *o, LubHandle *out) {
  P2WorldDesc d;
  p2_world_desc_init(&d);
  CbHolder *holder = NULL;
  if (o) {
    d.has_version = o->has_version;
    d.version = o->version;
    if (o->has_gravity) {
      d.gravity_x = o->gravity.x;
      d.gravity_y = o->gravity.y;
    }
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
      const LubWorldCallbacks *cb = &o->callbacks;
      if (cb->filter || cb->pre_solve || cb->friction || cb->restitution) {
        holder = (CbHolder *)calloc(1, sizeof(CbHolder));
        if (!holder)
          return lub_api_fail(lub_api_app(ctx), "phys2d_world: out of memory");
        holder->cb = *cb;
        d.callbacks.user = holder;
        d.callbacks.release = cb_holder_release;
        d.callbacks.filter = cb->filter ? cb_filter_tramp : NULL;
        d.callbacks.pre_solve = cb->pre_solve ? cb_pre_solve_tramp : NULL;
        d.callbacks.friction = cb->friction ? cb_friction_tramp : NULL;
        d.callbacks.restitution =
            cb->restitution ? cb_restitution_tramp : NULL;
      } else if (cb->user_release) {
        cb->user_release(cb->user);
      }
    }
  }
  LubStatus st = p2_world(ctx, key, &d, out);
  if (st != LUB_OK)
    cb_holder_release(holder);
  return st;
}

LubStatus lub_phys2d_begin(LubContext *ctx, LubHandle world,
                           const LubBeginOpts *opts) {
  bool prune = opts && opts->has_prune ? opts->prune : true;
  return p2_begin(ctx, world, prune);
}

LubStatus lub_phys2d_world_info(LubContext *ctx, LubHandle world,
                                LubWorldInfo *out) {
  P2WorldInfo i;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_world_info(ctx, world, &i);
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
  out->callbacks.filter = i.callback_filter;
  out->callbacks.pre_solve = i.callback_pre_solve;
  out->callbacks.friction = i.callback_friction;
  out->callbacks.restitution = i.callback_restitution;
  if (i.valid) {
    out->has_gravity = true;
    out->gravity = vec2d(i.gravity_x, i.gravity_y);
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

LubStatus lub_phys2d_body(LubContext *ctx, LubHandle world, LubStr key,
                          const LubBodyDesc *s, LubHandle *out) {
  P2BodyDesc d;
  p2_body_desc_init(&d);
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_body: desc required");
  d.has_version = s->has_version;
  d.version = s->version;
  if (s->has_type)
    d.type = s->type;
  d.fixed_rotation = s->has_fixed_rotation && s->fixed_rotation;
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
    const LubInitialState *i = &s->initial;
    if (i->has_x)
      d.x = i->x;
    if (i->has_y)
      d.y = i->y;
    if (i->has_angle)
      d.angle = i->angle;
    if (i->has_vx)
      d.vx = i->vx;
    if (i->has_vy)
      d.vy = i->vy;
    if (i->has_w)
      d.w = i->w;
    if (i->has_awake)
      d.initial_awake = i->awake;
  }
  return p2_body(ctx, world, key, &d, out);
}

// ---------------------------------------------------------------- shapes

static void shape_desc_from(const LubShapeDesc *s, P2ShapeDesc *d) {
  p2_shape_desc_init(d);
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
  filter_from_desc(&s->filter, s->has_filter, &d->filter);
}

#define OPT(s, f, def) ((s)->has_##f ? (s)->f : (def))

LubStatus lub_phys2d_box(LubContext *ctx, LubHandle body, LubStr key,
                         const LubBoxDesc *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_box: desc required");
  P2BoxDesc d;
  memset(&d, 0, sizeof(d));
  shape_desc_from(&s->base, &d.shape);
  d.hx = s->hx;
  d.hy = s->hy;
  d.cx = OPT(s, cx, 0.0f);
  d.cy = OPT(s, cy, 0.0f);
  d.angle = OPT(s, angle, 0.0f);
  return p2_box(ctx, body, key, &d, out);
}

LubStatus lub_phys2d_circle(LubContext *ctx, LubHandle body, LubStr key,
                            const LubCircleDesc *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_circle: desc required");
  P2CircleDesc d;
  memset(&d, 0, sizeof(d));
  shape_desc_from(&s->base, &d.shape);
  d.r = s->r;
  d.cx = OPT(s, cx, 0.0f);
  d.cy = OPT(s, cy, 0.0f);
  return p2_circle(ctx, body, key, &d, out);
}

LubStatus lub_phys2d_capsule(LubContext *ctx, LubHandle body, LubStr key,
                             const LubCapsuleDesc *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_capsule: desc required");
  P2CapsuleDesc d;
  memset(&d, 0, sizeof(d));
  shape_desc_from(&s->base, &d.shape);
  d.ax = s->ax;
  d.ay = s->ay;
  d.bx = s->bx;
  d.by = s->by;
  d.r = s->r;
  return p2_capsule(ctx, body, key, &d, out);
}

LubStatus lub_phys2d_segment(LubContext *ctx, LubHandle body, LubStr key,
                             const LubSegmentDesc *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_segment: desc required");
  P2SegmentDesc d;
  memset(&d, 0, sizeof(d));
  shape_desc_from(&s->base, &d.shape);
  d.ax = s->ax;
  d.ay = s->ay;
  d.bx = s->bx;
  d.by = s->by;
  return p2_segment(ctx, body, key, &d, out);
}

LubStatus lub_phys2d_polygon(LubContext *ctx, LubHandle body, LubStr key,
                             const LubPolygonDesc *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_polygon: desc required");
  P2PolygonDesc d;
  memset(&d, 0, sizeof(d));
  shape_desc_from(&s->base, &d.shape);
  d.points = s->points;
  d.point_count = s->points_count / 2;
  d.radius = OPT(s, radius, 0.0f);
  d.cx = OPT(s, cx, 0.0f);
  d.cy = OPT(s, cy, 0.0f);
  d.angle = OPT(s, angle, 0.0f);
  return p2_polygon(ctx, body, key, &d, out);
}

LubStatus lub_phys2d_chain(LubContext *ctx, LubHandle body, LubStr key,
                           const LubChainDesc *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_chain: desc required");
  P2ChainDesc d;
  p2_chain_desc_init(&d);
  d.version = s->version;
  d.points = s->points;
  d.point_count = s->points_count / 2;
  d.loop = s->has_loop && s->loop;
  if (s->has_friction)
    d.friction = s->friction;
  if (s->has_restitution)
    d.restitution = s->restitution;
  if (s->has_material_id)
    d.material_id = s->material_id;
  d.material_name = s->material_name;
  d.tag = s->tag;
  d.sensor_events = s->has_sensor_events && s->sensor_events;
  filter_from_desc(&s->filter, s->has_filter, &d.filter);
  P2SurfaceMaterial *mats = NULL;
  if (s->materials && s->materials_count > 0) {
    mats = (P2SurfaceMaterial *)calloc((size_t)s->materials_count,
                                       sizeof(P2SurfaceMaterial));
    if (!mats)
      return lub_api_fail(lub_api_app(ctx), "phys2d_chain: out of memory");
    for (int32_t i = 0; i < s->materials_count; ++i) {
      const LubChainMaterial *m = &s->materials[i];
      mats[i].friction = m->has_friction ? m->friction : d.friction;
      mats[i].restitution =
          m->has_restitution ? m->restitution : d.restitution;
      mats[i].material_id =
          m->has_material_id ? m->material_id : d.material_id;
    }
    d.materials = mats;
    d.material_count = s->materials_count;
  }
  LubStatus st = p2_chain(ctx, body, key, &d, out);
  free(mats);
  return st;
}

static GrowBuf g_shape_views;

static LubStatus shape_views_out(LubStatus st, const P2ShapePart *items,
                                 int32_t count, const LubShapeView **out,
                                 int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (st != LUB_OK)
    return st;
  g_shape_views.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubShapeView *v = (LubShapeView *)grow_push(&g_shape_views, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    shape_view_of(v, &items[i]);
  }
  *out = (const LubShapeView *)g_shape_views.ptr;
  *out_count = g_shape_views.n;
  return LUB_OK;
}

LubStatus lub_phys2d_chain_segments(LubContext *ctx, LubHandle chain,
                                    const LubShapeView **out,
                                    int32_t *out_count) {
  const P2ShapePart *items = NULL;
  int32_t count = 0;
  LubStatus st = p2_chain_segments(ctx, chain, &items, &count);
  return shape_views_out(st, items, count, out, out_count);
}

// ---------------------------------------------------------------- joints

LubStatus lub_phys2d_joint(LubContext *ctx, LubHandle world, LubStr key,
                           const LubJointDesc *s, LubHandle *out) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_joint: desc required");
  P2JointDesc d;
  p2_joint_desc_init(&d);
  d.has_version = s->has_version;
  d.version = s->version;
  if (s->has_type)
    d.type = s->type;
  d.body_a = s->body_a;
  d.body_b = s->body_b;
  // anchor_a は local_anchor_a の別名 (Lua 面の従来の名前)
  if (s->has_local_anchor_a)
    d.local_anchor_a = s->local_anchor_a;
  else if (s->has_anchor_a)
    d.local_anchor_a = s->anchor_a;
  if (s->has_local_anchor_b)
    d.local_anchor_b = s->local_anchor_b;
  else if (s->has_anchor_b)
    d.local_anchor_b = s->anchor_b;
  if (s->has_local_axis_a)
    d.local_axis_a = s->local_axis_a;
  d.reference_angle = OPT(s, reference_angle, 0.0f);
  d.collide_connected = s->has_collide_connected && s->collide_connected;
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
  d.target_angle = OPT(s, target_angle, 0.0f);
  d.target_translation = OPT(s, target_translation, 0.0f);
  if (s->has_linear_offset)
    d.linear_offset = s->linear_offset;
  d.angular_offset = OPT(s, angular_offset, 0.0f);
  d.hertz = OPT(s, hertz, 0.0f);
  d.damping_ratio = OPT(s, damping_ratio, 0.0f);
  if (s->has_max_force)
    d.max_force = s->max_force;
  if (s->has_max_torque)
    d.max_torque = s->max_torque;
  d.motor_speed = OPT(s, motor_speed, 0.0f);
  if (s->has_correction_factor)
    d.correction_factor = s->correction_factor;
  if (s->has_target)
    d.target = s->target;
  if (s->has_spring) {
    const LubJointSpringDesc *p = &s->spring;
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
  }
  if (s->has_limit) {
    const LubJointLimitDesc *p = &s->limit;
    d.enable_limit = p->has_enabled ? p->enabled : true;
    if (p->has_lower)
      d.lower = p->lower;
    if (p->has_upper)
      d.upper = p->upper;
    if (p->has_min_length)
      d.min_length = p->min_length;
    if (p->has_max_length)
      d.max_length = p->max_length;
  }
  if (s->has_motor) {
    const LubJointMotorDesc *p = &s->motor;
    d.enable_motor = p->has_enabled ? p->enabled : true;
    if (p->has_speed)
      d.motor_speed = p->speed;
    if (p->has_max_force)
      d.max_force = p->max_force;
    if (p->has_max_torque)
      d.max_torque = p->max_torque;
    if (p->has_linear_offset)
      d.linear_offset = p->linear_offset;
    if (p->has_angular_offset)
      d.angular_offset = p->angular_offset;
    if (p->has_correction_factor)
      d.correction_factor = p->correction_factor;
  }
  return p2_joint(ctx, world, key, &d, out);
}

static void joint_view_of(LubJointView *v, const P2JointView *j) {
  memset(v, 0, sizeof(*v));
  v->joint = j->key;
  v->type = j->type;
  v->a = j->a;
  v->b = j->b;
  v->valid = j->valid;
}

LubStatus lub_phys2d_joint_info(LubContext *ctx, LubHandle joint,
                                LubJointInfo *out) {
  P2JointInfo i;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_joint_info(ctx, joint, &i);
  if (st != LUB_OK)
    return st;
  joint_view_of(&out->base, &i.view);
  out->collide_connected = i.collide_connected;
  out->force = vec2d(i.force_x, i.force_y);
  out->torque = i.torque;
  out->linear_separation = i.linear_separation;
  out->angular_separation = i.angular_separation;
  out->has_local_anchor_a = out->has_local_anchor_b = i.has_local_anchors;
  out->local_anchor_a = i.local_anchor_a;
  out->local_anchor_b = i.local_anchor_b;
  out->has_local_axis_a = i.has_local_axis;
  out->local_axis_a = i.local_axis_a;
  out->has_reference_angle = i.has_reference_angle;
  out->reference_angle = i.reference_angle;
  return LUB_OK;
}

LubStatus lub_phys2d_joint_force(LubContext *ctx, LubHandle joint,
                                 LubVec2d *out) {
  return p2_joint_force(ctx, joint, &out->x, &out->y);
}

LubStatus lub_phys2d_joint_torque(LubContext *ctx, LubHandle joint,
                                  float *out) {
  return p2_joint_torque(ctx, joint, out);
}

LubStatus lub_phys2d_joint_angle(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has) {
  return p2_joint_angle(ctx, joint, out, has);
}

LubStatus lub_phys2d_joint_translation(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has) {
  return p2_joint_translation(ctx, joint, out, has);
}

LubStatus lub_phys2d_joint_speed(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has) {
  return p2_joint_speed(ctx, joint, out, has);
}

LubStatus lub_phys2d_joint_length(LubContext *ctx, LubHandle joint, float *out,
                                  bool *has) {
  return p2_joint_length(ctx, joint, out, has);
}

LubStatus lub_phys2d_joint_motor_force(LubContext *ctx, LubHandle joint,
                                       float *out, bool *has) {
  return p2_joint_motor_force(ctx, joint, out, has);
}

LubStatus lub_phys2d_joint_motor_torque(LubContext *ctx, LubHandle joint,
                                        float *out, bool *has) {
  return p2_joint_motor_torque(ctx, joint, out, has);
}

LubStatus lub_phys2d_joint_set_motor(LubContext *ctx, LubHandle joint,
                                     const LubJointMotorDesc *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_joint_set_motor: desc required");
  P2JointMotor d;
  memset(&d, 0, sizeof(d));
  d.enabled = s->has_enabled ? s->enabled : true;
  d.speed = OPT(s, speed, 0.0f);
  d.max_force = OPT(s, max_force, 0.0f);
  d.max_torque = OPT(s, max_torque, 0.0f);
  d.has_correction_factor = s->has_correction_factor;
  d.correction_factor = s->correction_factor;
  return p2_joint_set_motor(ctx, joint, &d);
}

LubStatus lub_phys2d_joint_set_limit(LubContext *ctx, LubHandle joint,
                                     const LubJointLimitDesc *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_joint_set_limit: desc required");
  P2JointLimit d;
  memset(&d, 0, sizeof(d));
  d.enabled = s->has_enabled ? s->enabled : true;
  d.lower = OPT(s, lower, 0.0f);
  d.upper = OPT(s, upper, 0.0f);
  d.min_length = OPT(s, min_length, 0.0f);
  d.max_length = OPT(s, max_length, 0.0f);
  return p2_joint_set_limit(ctx, joint, &d);
}

LubStatus lub_phys2d_joint_set_spring(LubContext *ctx, LubHandle joint,
                                      const LubJointSpringDesc *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_joint_set_spring: desc required");
  P2JointSpring d;
  memset(&d, 0, sizeof(d));
  d.enabled = s->has_enabled ? s->enabled : true;
  d.hertz = OPT(s, hertz, 0.0f);
  d.damping_ratio = OPT(s, damping_ratio, 0.0f);
  d.linear_hertz = OPT(s, linear_hertz, 0.0f);
  d.linear_damping_ratio = OPT(s, linear_damping_ratio, 0.0f);
  d.angular_hertz = OPT(s, angular_hertz, 0.0f);
  d.angular_damping_ratio = OPT(s, angular_damping_ratio, 0.0f);
  return p2_joint_set_spring(ctx, joint, &d);
}

LubStatus lub_phys2d_joint_set_target(LubContext *ctx, LubHandle joint,
                                      const LubJointTargetDesc *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_joint_set_target: desc required");
  P2JointTarget d;
  memset(&d, 0, sizeof(d));
  d.has_x = s->has_x;
  d.x = s->x;
  d.has_y = s->has_y;
  d.y = s->y;
  d.has_translation = s->has_translation;
  d.translation = s->translation;
  d.has_angle = s->has_angle;
  d.angle = s->angle;
  d.has_linear_offset = s->has_linear_offset;
  d.linear_offset_x = s->linear_offset.x;
  d.linear_offset_y = s->linear_offset.y;
  d.has_angular_offset = s->has_angular_offset;
  d.angular_offset = s->angular_offset;
  return p2_joint_set_target(ctx, joint, &d);
}

// ------------------------------------------------------------ step / pose

LubStatus lub_phys2d_step(LubContext *ctx, LubHandle world, float dt,
                          LubStepInfo *out) {
  P2StepInfo i;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_step(ctx, world, dt, &i);
  if (st != LUB_OK)
    return st;
  out->steps = i.steps;
  out->commands = i.commands;
  out->alpha = i.alpha;
  out->dropped = i.dropped;
  out->contact_begins = i.contact_begins;
  out->contact_ends = i.contact_ends;
  out->contact_hits = i.contact_hits;
  out->sensor_begins = i.sensor_begins;
  out->sensor_ends = i.sensor_ends;
  out->body_moves = i.body_moves;
  out->body_events = i.body_moves;
  return LUB_OK;
}

LubStatus lub_phys2d_pose(LubContext *ctx, LubHandle body, LubPose *out) {
  P2Pose p;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_pose(ctx, body, &p);
  if (st != LUB_OK)
    return st;
  out->x = p.x;
  out->y = p.y;
  out->angle = p.angle;
  out->vx = p.vx;
  out->vy = p.vy;
  out->w = p.w;
  out->awake = p.awake;
  out->enabled = p.enabled;
  out->sleep = p.sleep;
  out->sleep_threshold = p.sleep_threshold;
  return LUB_OK;
}

LubStatus lub_phys2d_pose_by_key(LubContext *ctx, LubHandle world, LubStr key,
                                 LubPose *out) {
  LubHandle body = p2_body_find(ctx, world, key);
  if (!body) {
    memset(out, 0, sizeof(*out));
    return LUB_NOT_FOUND;
  }
  return lub_phys2d_pose(ctx, body, out);
}

LubStatus lub_phys2d_velocity(LubContext *ctx, LubHandle body,
                              LubVelocity *out) {
  P2Velocity v;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_velocity(ctx, body, &v);
  if (st != LUB_OK)
    return st;
  out->x = v.x;
  out->y = v.y;
  out->w = v.w;
  return LUB_OK;
}

LubStatus lub_phys2d_mass(LubContext *ctx, LubHandle body, LubMassData *out) {
  P2MassData m;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_mass(ctx, body, &m);
  if (st != LUB_OK)
    return st;
  out->mass = m.mass;
  out->inertia = m.inertia;
  out->center = vec2d(m.center_x, m.center_y);
  out->local_center = vec2d(m.local_center_x, m.local_center_y);
  return LUB_OK;
}

LubStatus lub_phys2d_center(LubContext *ctx, LubHandle body, LubVec2d *out) {
  return p2_center(ctx, body, &out->x, &out->y);
}

LubStatus lub_phys2d_world_point(LubContext *ctx, LubHandle body,
                                 const LubVec2d *p, LubVec2d *out) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys2d_world_point: point required");
  return p2_world_point(ctx, body, p->x, p->y, &out->x, &out->y);
}

LubStatus lub_phys2d_local_point(LubContext *ctx, LubHandle body,
                                 const LubVec2d *p, LubVec2d *out) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys2d_local_point: point required");
  return p2_local_point(ctx, body, p->x, p->y, &out->x, &out->y);
}

LubStatus lub_phys2d_velocity_at(LubContext *ctx, LubHandle body,
                                 const LubVec2d *p, LubVec2d *out) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys2d_velocity_at: point required");
  return p2_velocity_at(ctx, body, p->x, p->y, &out->x, &out->y);
}

LubStatus lub_phys2d_body_shapes(LubContext *ctx, LubHandle body,
                                 const LubShapeView **out, int32_t *out_count) {
  const P2ShapePart *items = NULL;
  int32_t count = 0;
  LubStatus st = p2_body_shapes(ctx, body, &items, &count);
  return shape_views_out(st, items, count, out, out_count);
}

static GrowBuf g_joint_views;

LubStatus lub_phys2d_body_joints(LubContext *ctx, LubHandle body,
                                 const LubJointView **out, int32_t *out_count) {
  const P2JointView *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p2_body_joints(ctx, body, &items, &count);
  if (st != LUB_OK)
    return st;
  g_joint_views.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubJointView *v = (LubJointView *)grow_push(&g_joint_views, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    joint_view_of(v, &items[i]);
  }
  *out = (const LubJointView *)g_joint_views.ptr;
  *out_count = g_joint_views.n;
  return LUB_OK;
}

static GrowBuf g_contact_datas;

LubStatus lub_phys2d_body_contacts(LubContext *ctx, LubHandle body,
                                   const LubContactData **out,
                                   int32_t *out_count) {
  const P2ContactData *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p2_body_contacts(ctx, body, &items, &count);
  if (st != LUB_OK)
    return st;
  g_contact_datas.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubContactData *v =
        (LubContactData *)grow_push(&g_contact_datas, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    shape_view_of(&v->a, &items[i].a);
    shape_view_of(&v->b, &items[i].b);
    v->nx = items[i].nx;
    v->ny = items[i].ny;
    v->point_count = items[i].point_count;
    if (items[i].point_count > 0) {
      v->has_x = v->has_y = v->has_separation = true;
      v->x = items[i].x;
      v->y = items[i].y;
      v->separation = items[i].separation;
    }
  }
  *out = (const LubContactData *)g_contact_datas.ptr;
  *out_count = g_contact_datas.n;
  return LUB_OK;
}

// ----------------------------------------------------------------- shape

static void ray_from(const LubRaycastDesc *q, P2Ray *r) {
  memset(r, 0, sizeof(*r));
  r->x = OPT(q, x, 0.0f);
  r->y = OPT(q, y, 0.0f);
  r->dx = OPT(q, dx, 0.0f);
  r->dy = OPT(q, dy, 0.0f);
  r->max_fraction = OPT(q, max_fraction, 1.0f);
}

LubStatus lub_phys2d_shape_test_point(LubContext *ctx, LubHandle shape,
                                      const LubVec2d *p, bool *out) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys2d_shape_test_point: point required");
  return p2_shape_test_point(ctx, shape, p->x, p->y, out);
}

LubStatus lub_phys2d_shape_raycast(LubContext *ctx, LubHandle shape,
                                   const LubRaycastDesc *query,
                                   LubShapeRayHit *out, bool *has) {
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys2d_shape_raycast: query required");
  P2Ray ray;
  ray_from(query, &ray);
  P2RayHit h;
  LubStatus st = p2_shape_raycast(ctx, shape, &ray, &h, has);
  if (st != LUB_OK || !*has)
    return st;
  out->x = h.x;
  out->y = h.y;
  out->nx = h.nx;
  out->ny = h.ny;
  out->fraction = h.fraction;
  out->iterations = h.iterations;
  return LUB_OK;
}

LubStatus lub_phys2d_shape_closest_point(LubContext *ctx, LubHandle shape,
                                         const LubVec2d *p, LubVec2d *out) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys2d_shape_closest_point: point required");
  return p2_shape_closest_point(ctx, shape, p->x, p->y, &out->x, &out->y);
}

LubStatus lub_phys2d_shape_aabb(LubContext *ctx, LubHandle shape,
                                LubAabb *out) {
  P2Aabb a;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_shape_aabb(ctx, shape, &a);
  if (st != LUB_OK)
    return st;
  out->min_x = a.min_x;
  out->min_y = a.min_y;
  out->max_x = a.max_x;
  out->max_y = a.max_y;
  return LUB_OK;
}

LubStatus lub_phys2d_shape_info(LubContext *ctx, LubHandle shape,
                                LubShapeInfo *out) {
  P2ShapeInfo i;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_shape_info(ctx, shape, &i);
  if (st != LUB_OK)
    return st;
  shape_view_of(&out->base, &i.part);
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
  out->aabb.min_x = i.aabb.min_x;
  out->aabb.min_y = i.aabb.min_y;
  out->aabb.max_x = i.aabb.max_x;
  out->aabb.max_y = i.aabb.max_y;
  return LUB_OK;
}

LubStatus lub_phys2d_shape_set_material(LubContext *ctx, LubHandle shape,
                                        const LubMaterialDesc *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_shape_set_material: desc required");
  P2MaterialDesc d;
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
  return p2_shape_set_material(ctx, shape, &d);
}

LubStatus lub_phys2d_shape_set_filter(LubContext *ctx, LubHandle shape,
                                      const LubFilterDesc *f) {
  P2Filter d = {1u, UINT64_MAX, 0};
  filter_from_desc(f, f != NULL, &d);
  return p2_shape_set_filter(ctx, shape, &d);
}

LubStatus lub_phys2d_shape_set_events(LubContext *ctx, LubHandle shape,
                                      const LubShapeEventsDesc *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_shape_set_events: desc required");
  P2EventFlags d;
  memset(&d, 0, sizeof(d));
  d.has_sensor_events = s->has_sensor_events;
  d.sensor_events = s->sensor_events;
  d.has_contact = s->has_contact;
  d.contact = s->contact;
  d.has_pre_solve = s->has_pre_solve;
  d.pre_solve = s->pre_solve;
  d.has_hit = s->has_hit;
  d.hit = s->hit;
  return p2_shape_set_events(ctx, shape, &d);
}

// ---------------------------------------------------------------- events

static GrowBuf g_contact_events;
static GrowBuf g_sensor_events;

LubStatus lub_phys2d_contacts(LubContext *ctx, LubHandle world,
                              const int32_t *kind_p,
                              const LubContactEvent **out, int32_t *out_count) {
  int32_t kind = kind_p ? *kind_p : LUB_PHYS2D_EVENT_KIND_BEGIN;
  const P2Contact *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p2_contacts(ctx, world, kind, &items, &count);
  if (st != LUB_OK)
    return st;
  g_contact_events.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubContactEvent *v =
        (LubContactEvent *)grow_push(&g_contact_events, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    shape_view_of(&v->a, &items[i].a);
    shape_view_of(&v->b, &items[i].b);
    v->nx = items[i].nx;
    v->ny = items[i].ny;
    v->point_count = items[i].point_count;
    v->x = items[i].x;
    v->y = items[i].y;
    if (kind == LUB_PHYS2D_EVENT_KIND_HIT) {
      v->has_approach_speed = true;
      v->approach_speed = items[i].approach_speed;
    }
  }
  *out = (const LubContactEvent *)g_contact_events.ptr;
  *out_count = g_contact_events.n;
  return LUB_OK;
}

LubStatus lub_phys2d_body_events(LubContext *ctx, LubHandle world,
                                 const LubBodyEvent **out, int32_t *out_count) {
  const P2BodyEvent *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p2_body_events(ctx, world, &items, &count);
  if (st != LUB_OK)
    return st;
  // 同じ layout なので配列をそのまま返す
  *out = (const LubBodyEvent *)items;
  *out_count = count;
  return LUB_OK;
}

LubStatus lub_phys2d_sensors(LubContext *ctx, LubHandle world,
                             const int32_t *kind_p, const LubSensorEvent **out,
                             int32_t *out_count) {
  int32_t kind = kind_p ? *kind_p : LUB_PHYS2D_EVENT_KIND_BEGIN;
  const P2Contact *items = NULL;
  int32_t count = 0;
  *out = NULL;
  *out_count = 0;
  LubStatus st = p2_sensors(ctx, world, kind, &items, &count);
  if (st != LUB_OK)
    return st;
  g_sensor_events.n = 0;
  for (int32_t i = 0; i < count; ++i) {
    LubSensorEvent *v =
        (LubSensorEvent *)grow_push(&g_sensor_events, sizeof(*v));
    if (!v)
      return LUB_ERROR;
    shape_view_of(&v->sensor, &items[i].a);
    shape_view_of(&v->visitor, &items[i].b);
  }
  *out = (const LubSensorEvent *)g_sensor_events.ptr;
  *out_count = g_sensor_events.n;
  return LUB_OK;
}

// --------------------------------------------------------------- queries

static void ray_hit_of(LubRayHit *v, const P2RayHit *h, bool with_stats) {
  memset(v, 0, sizeof(*v));
  shape_view_of(&v->base, &h->shape);
  v->x = h->x;
  v->y = h->y;
  v->nx = h->nx;
  v->ny = h->ny;
  v->fraction = h->fraction;
  if (with_stats) {
    v->has_node_visits = v->has_leaf_visits = true;
    v->node_visits = h->node_visits;
    v->leaf_visits = h->leaf_visits;
  }
}

typedef struct RayShim {
  GrowBuf *buf;
  float (*visitor)(void *user, const LubRayHit *hit);
  void *user;
  bool closest; // 最寄り 1 件だけを残す
  bool has_hit;
  LubRayHit best;
} RayShim;

static float ray_shim_tramp(void *user, const P2RayHit *h) {
  RayShim *s = (RayShim *)user;
  LubRayHit v;
  ray_hit_of(&v, h, false);
  if (s->closest) {
    if (!s->has_hit || v.fraction < s->best.fraction) {
      s->best = v;
      s->has_hit = true;
    }
    return v.fraction; // ここまでに詰める
  }
  float r = s->visitor ? s->visitor(s->user, &v) : 1.0f;
  if (r >= 0.0f) {
    LubRayHit *slot = (LubRayHit *)grow_push(s->buf, sizeof(*slot));
    if (slot)
      *slot = v;
  }
  return r;
}

static GrowBuf g_ray_hits;

LubStatus lub_phys2d_raycast(LubContext *ctx, LubHandle world,
                             const LubRaycastDesc *query, LubRayHit *out,
                             bool *has) {
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys2d_raycast: query required");
  P2Ray ray;
  ray_from(query, &ray);
  P2QueryFilter qf = qfilter_from(&query->filter, query->has_filter);
  P2RayHit h;
  LubStatus st = p2_raycast_closest(ctx, world, &ray, &qf, &h, has);
  if (st != LUB_OK || !*has)
    return st;
  ray_hit_of(out, &h, true);
  return LUB_OK;
}

LubStatus lub_phys2d_raycast_all(LubContext *ctx, LubHandle world,
                                 const LubRaycastDesc *query,
                                 LubPhys2dRaycastAllVisitorFn visitor,
                                 void *visitor_user, const LubRayHit **out,
                                 int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys2d_raycast: query required");
  P2Ray ray;
  ray_from(query, &ray);
  P2QueryFilter qf = qfilter_from(&query->filter, query->has_filter);
  g_ray_hits.n = 0;
  RayShim s;
  memset(&s, 0, sizeof(s));
  s.buf = &g_ray_hits;
  s.visitor = visitor;
  s.user = visitor_user;
  P2TreeStats stats;
  LubStatus st = p2_raycast(ctx, world, &ray, &qf, ray_shim_tramp, &s, &stats);
  if (st != LUB_OK)
    return st;
  *out = (const LubRayHit *)g_ray_hits.ptr;
  *out_count = g_ray_hits.n;
  return LUB_OK;
}

typedef struct OverlapShim {
  bool (*visitor)(void *user, const LubShapeView *shape);
  void *user;
} OverlapShim;

static bool overlap_shim_tramp(void *user, const P2ShapePart *p) {
  OverlapShim *s = (OverlapShim *)user;
  LubShapeView v;
  shape_view_of(&v, p);
  bool keep = s->visitor ? s->visitor(s->user, &v) : true;
  LubShapeView *slot = (LubShapeView *)grow_push(&g_shape_views, sizeof(*slot));
  if (slot)
    *slot = v;
  return keep;
}

LubStatus lub_phys2d_overlap_aabb(LubContext *ctx, LubHandle world,
                                  const LubAabbDesc *query,
                                  LubPhys2dOverlapAabbVisitorFn visitor,
                                  void *visitor_user, const LubShapeView **out,
                                  int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys2d_overlap_aabb: query required");
  P2Aabb aabb = {query->min_x, query->min_y, query->max_x, query->max_y};
  P2QueryFilter qf = qfilter_from(&query->filter, query->has_filter);
  g_shape_views.n = 0;
  OverlapShim s = {visitor, visitor_user};
  P2TreeStats stats;
  LubStatus st =
      p2_overlap_aabb(ctx, world, &aabb, &qf, overlap_shim_tramp, &s, &stats);
  if (st != LUB_OK)
    return st;
  *out = (const LubShapeView *)g_shape_views.ptr;
  *out_count = g_shape_views.n;
  return LUB_OK;
}

static bool proxy_from(LubContext *ctx, const LubShapeCastDesc *q,
                       P2ShapeProxy *p) {
  memset(p, 0, sizeof(*p));
  int32_t kind = q->has_kind ? q->kind : 0;
  switch (kind) {
  case LUB_PHYS2D_PROXY_KIND_BOX:
    p->kind = P2_PROXY_BOX;
    break;
  case LUB_PHYS2D_PROXY_KIND_CIRCLE:
    p->kind = P2_PROXY_CIRCLE;
    break;
  case LUB_PHYS2D_PROXY_KIND_CAPSULE:
    p->kind = P2_PROXY_CAPSULE;
    break;
  case LUB_PHYS2D_PROXY_KIND_SEGMENT:
    p->kind = P2_PROXY_SEGMENT;
    break;
  case LUB_PHYS2D_PROXY_KIND_POLYGON:
    p->kind = P2_PROXY_POLYGON;
    break;
  default:
    lub_api_fail(lub_api_app(ctx), "phys2d_shape_cast: kind required");
    return false;
  }
  p->x = OPT(q, x, 0.0f);
  p->y = OPT(q, y, 0.0f);
  p->angle = OPT(q, angle, 0.0f);
  p->r = OPT(q, radius, 0.0f);
  p->cx = OPT(q, cx, 0.0f);
  p->cy = OPT(q, cy, 0.0f);
  p->ax = OPT(q, ax, 0.0f);
  p->ay = OPT(q, ay, 0.0f);
  p->bx = OPT(q, bx, 0.0f);
  p->by = OPT(q, by, 0.0f);
  p->hx = OPT(q, hx, 0.0f);
  p->hy = OPT(q, hy, 0.0f);
  p->points = q->points;
  p->point_count = q->points_count / 2;
  return true;
}

LubStatus lub_phys2d_shape_cast(LubContext *ctx, LubHandle world,
                                const LubShapeCastDesc *query, LubRayHit *out,
                                bool *has) {
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys2d_shape_cast: query required");
  P2ShapeProxy p;
  if (!proxy_from(ctx, query, &p))
    return LUB_ERROR;
  P2QueryFilter qf = qfilter_from(&query->filter, query->has_filter);
  RayShim s;
  memset(&s, 0, sizeof(s));
  s.closest = true;
  P2TreeStats stats;
  float dx = OPT(query, dx, 0.0f), dy = OPT(query, dy, 0.0f);
  float mf = OPT(query, max_fraction, 1.0f);
  LubStatus st = p2_shape_cast(ctx, world, &p, dx * mf, dy * mf, &qf,
                               ray_shim_tramp, &s, &stats);
  if (st != LUB_OK)
    return st;
  if (s.has_hit) {
    *out = s.best;
    *has = true;
  }
  return LUB_OK;
}

LubStatus lub_phys2d_shape_cast_all(LubContext *ctx, LubHandle world,
                                    const LubShapeCastDesc *query,
                                    LubPhys2dShapeCastAllVisitorFn visitor,
                                    void *visitor_user, const LubRayHit **out,
                                    int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys2d_shape_cast: query required");
  P2ShapeProxy p;
  if (!proxy_from(ctx, query, &p))
    return LUB_ERROR;
  P2QueryFilter qf = qfilter_from(&query->filter, query->has_filter);
  g_ray_hits.n = 0;
  RayShim s;
  memset(&s, 0, sizeof(s));
  s.buf = &g_ray_hits;
  s.visitor = visitor;
  s.user = visitor_user;
  P2TreeStats stats;
  float dx = OPT(query, dx, 0.0f), dy = OPT(query, dy, 0.0f);
  float mf = OPT(query, max_fraction, 1.0f);
  LubStatus st = p2_shape_cast(ctx, world, &p, dx * mf, dy * mf, &qf,
                               ray_shim_tramp, &s, &stats);
  if (st != LUB_OK)
    return st;
  *out = (const LubRayHit *)g_ray_hits.ptr;
  *out_count = g_ray_hits.n;
  return LUB_OK;
}

static void mover_from(const LubMoverDesc *q, P2Mover *m, float *dx,
                       float *dy) {
  m->ax = q->ax;
  m->ay = q->ay;
  m->bx = q->bx;
  m->by = q->by;
  m->r = q->r;
  float mf = OPT(q, max_fraction, 1.0f);
  *dx = OPT(q, dx, 0.0f) * mf;
  *dy = OPT(q, dy, 0.0f) * mf;
}

LubStatus lub_phys2d_cast_mover(LubContext *ctx, LubHandle world,
                                const LubMoverDesc *query, LubMoverCast *out,
                                bool *has) {
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys2d_cast_mover: query required");
  P2Mover m;
  float dx, dy;
  mover_from(query, &m, &dx, &dy);
  P2QueryFilter qf = qfilter_from(&query->filter, query->has_filter);
  float fraction = 0;
  LubStatus st = p2_cast_mover(ctx, world, &m, dx, dy, &qf, &fraction);
  if (st != LUB_OK)
    return st;
  out->fraction = fraction;
  out->dx = dx * fraction;
  out->dy = dy * fraction;
  *has = true;
  return LUB_OK;
}

typedef struct PlaneShim {
  bool (*visitor)(void *user, const LubMoverPlane *plane);
  void *user;
} PlaneShim;

static GrowBuf g_mover_planes;

static bool plane_shim_tramp(void *user, const P2MoverPlane *p) {
  PlaneShim *s = (PlaneShim *)user;
  LubMoverPlane v;
  memset(&v, 0, sizeof(v));
  shape_view_of(&v.base, &p->shape);
  v.hit = p->hit;
  v.x = p->x;
  v.y = p->y;
  v.nx = p->nx;
  v.ny = p->ny;
  v.offset = p->offset;
  bool keep = s->visitor ? s->visitor(s->user, &v) : true;
  LubMoverPlane *slot = (LubMoverPlane *)grow_push(&g_mover_planes, sizeof(*slot));
  if (slot)
    *slot = v;
  return keep;
}

LubStatus lub_phys2d_collide_mover(LubContext *ctx, LubHandle world,
                                   const LubMoverDesc *query,
                                   LubPhys2dCollideMoverVisitorFn visitor,
                                   void *visitor_user,
                                   const LubMoverPlane **out,
                                   int32_t *out_count) {
  *out = NULL;
  *out_count = 0;
  if (!query)
    return lub_api_fail(lub_api_app(ctx), "phys2d_collide_mover: query required");
  P2Mover m;
  float dx, dy;
  mover_from(query, &m, &dx, &dy);
  P2QueryFilter qf = qfilter_from(&query->filter, query->has_filter);
  g_mover_planes.n = 0;
  PlaneShim s = {visitor, visitor_user};
  LubStatus st = p2_collide_mover(ctx, world, &m, &qf, plane_shim_tramp, &s);
  if (st != LUB_OK)
    return st;
  *out = (const LubMoverPlane *)g_mover_planes.ptr;
  *out_count = g_mover_planes.n;
  return LUB_OK;
}

LubStatus lub_phys2d_explode(LubContext *ctx, LubHandle world,
                             const LubExplosionDesc *s) {
  if (!s)
    return lub_api_fail(lub_api_app(ctx), "phys2d_explode: desc required");
  P2ExplosionDesc d;
  memset(&d, 0, sizeof(d));
  d.x = OPT(s, x, 0.0f);
  d.y = OPT(s, y, 0.0f);
  d.radius = OPT(s, radius, 0.0f);
  d.falloff = OPT(s, falloff, 0.0f);
  d.impulse_per_length = OPT(s, impulse_per_length, 0.0f);
  d.mask_bits = UINT64_MAX;
  if (s->has_filter && s->filter.has_mask_bits)
    d.mask_bits = s->filter.mask_bits;
  return p2_explode(ctx, world, &d);
}

LubStatus lub_phys2d_debug(LubContext *ctx, LubHandle world,
                           const LubDebugOpts *o, LubDebugData *out) {
  P2DebugDesc d;
  memset(&d, 0, sizeof(d));
  d.shapes = true;
  if (o) {
    if (o->has_shapes)
      d.shapes = o->shapes;
    d.joints = o->has_joints && o->joints;
    d.joint_extras = o->has_joint_extras && o->joint_extras;
    d.bounds = o->has_bounds && o->bounds;
    d.mass = o->has_mass && o->mass;
    d.body_names = o->has_body_names && o->body_names;
    d.contacts = o->has_contacts && o->contacts;
    d.graph_colors = o->has_graph_colors && o->graph_colors;
    d.contact_normals = o->has_contact_normals && o->contact_normals;
    d.contact_impulses = o->has_contact_impulses && o->contact_impulses;
    d.contact_features = o->has_contact_features && o->contact_features;
    d.friction_impulses = o->has_friction_impulses && o->friction_impulses;
    d.islands = o->has_islands && o->islands;
    if (o->has_drawing_bounds) {
      d.has_drawing_bounds = true;
      d.drawing_bounds.min_x = o->drawing_bounds.min_x;
      d.drawing_bounds.min_y = o->drawing_bounds.min_y;
      d.drawing_bounds.max_x = o->drawing_bounds.max_x;
      d.drawing_bounds.max_y = o->drawing_bounds.max_y;
    }
  }
  P2DebugData r;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_debug(ctx, world, &d, &r);
  if (st != LUB_OK)
    return st;
  out->segments = r.segments;
  out->segments_count = r.segment_count;
  out->circles = r.circles;
  out->circles_count = r.circle_count;
  out->capsules = r.capsules;
  out->capsules_count = r.capsule_count;
  out->polygons = r.polygons;
  out->polygons_count = r.polygon_count;
  out->points = r.points;
  out->points_count = r.point_count;
  return LUB_OK;
}

LubStatus lub_phys2d_profile(LubContext *ctx, LubHandle world,
                             LubProfile *out) {
  P2Profile p;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_profile(ctx, world, &p);
  if (st != LUB_OK)
    return st;
  out->step = p.step;
  out->pairs = p.pairs;
  out->collide = p.collide;
  out->solve = p.solve;
  out->merge_islands = p.merge_islands;
  out->prepare_stages = p.prepare_stages;
  out->solve_constraints = p.solve_constraints;
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
  out->hit_events = p.hit_events;
  out->refit = p.refit;
  out->bullets = p.bullets;
  out->sleep_islands = p.sleep_islands;
  out->sensors = p.sensors;
  return LUB_OK;
}

LubStatus lub_phys2d_counters(LubContext *ctx, LubHandle world,
                              LubCounters *out) {
  P2Counters c;
  memset(out, 0, sizeof(*out));
  LubStatus st = p2_counters(ctx, world, &c);
  if (st != LUB_OK)
    return st;
  out->body_count = c.body_count;
  out->shape_count = c.shape_count;
  out->contact_count = c.contact_count;
  out->joint_count = c.joint_count;
  out->island_count = c.island_count;
  out->stack_used = c.stack_used;
  out->static_tree_height = c.static_tree_height;
  out->tree_height = c.tree_height;
  out->byte_count = c.byte_count;
  out->task_count = c.task_count;
  memcpy(out->color_counts, c.color_counts, sizeof(out->color_counts));
  out->color_counts_count = 12;
  return LUB_OK;
}

// -------------------------------------------------------------- commands

static bool opt_wake(const LubCommandOpts *o) {
  return o && o->has_wake ? o->wake : true;
}

LubStatus lub_phys2d_add_force(LubContext *ctx, LubHandle body,
                               const LubVec2d *force,
                               const LubCommandOpts *o) {
  if (!force)
    return lub_api_fail(lub_api_app(ctx), "phys2d_add_force: force required");
  const LubVec2d *point = o && o->has_point ? &o->point : NULL;
  return p2_add_force(ctx, body, force->x, force->y, point, opt_wake(o));
}

LubStatus lub_phys2d_add_force_center(LubContext *ctx, LubHandle body,
                                      const LubVec2d *force,
                                      const LubCommandOpts *o) {
  if (!force)
    return lub_api_fail(lub_api_app(ctx), "phys2d_add_force_center: force required");
  return p2_add_force_center(ctx, body, force->x, force->y, opt_wake(o));
}

LubStatus lub_phys2d_add_impulse(LubContext *ctx, LubHandle body,
                                 const LubVec2d *impulse,
                                 const LubCommandOpts *o) {
  if (!impulse)
    return lub_api_fail(lub_api_app(ctx), "phys2d_add_impulse: impulse required");
  const LubVec2d *point = o && o->has_point ? &o->point : NULL;
  return p2_add_impulse(ctx, body, impulse->x, impulse->y, point, opt_wake(o));
}

LubStatus lub_phys2d_add_impulse_center(LubContext *ctx, LubHandle body,
                                        const LubVec2d *impulse,
                                        const LubCommandOpts *o) {
  if (!impulse)
    return lub_api_fail(lub_api_app(ctx), "phys2d_add_impulse_center: impulse required");
  return p2_add_impulse_center(ctx, body, impulse->x, impulse->y, opt_wake(o));
}

LubStatus lub_phys2d_add_torque(LubContext *ctx, LubHandle body, float torque,
                                const LubCommandOpts *o) {
  return p2_add_torque(ctx, body, torque, opt_wake(o));
}

LubStatus lub_phys2d_add_angular_impulse(LubContext *ctx, LubHandle body,
                                         float impulse,
                                         const LubCommandOpts *o) {
  return p2_add_angular_impulse(ctx, body, impulse, opt_wake(o));
}

LubStatus lub_phys2d_set_velocity(LubContext *ctx, LubHandle body,
                                  const LubVelocityDesc *v,
                                  const LubCommandOpts *o) {
  if (!v)
    return lub_api_fail(lub_api_app(ctx), "phys2d_set_velocity: velocity required");
  P2SetVelocity d;
  memset(&d, 0, sizeof(d));
  d.has_vx = v->has_vx;
  d.vx = v->vx;
  d.has_vy = v->has_vy;
  d.vy = v->vy;
  d.has_w = v->has_w;
  d.w = v->w;
  d.wake = opt_wake(o);
  return p2_set_velocity(ctx, body, &d);
}

LubStatus lub_phys2d_teleport(LubContext *ctx, LubHandle body,
                              const LubPoseDesc *p, const LubCommandOpts *o) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys2d_teleport: pose required");
  P2Teleport d;
  memset(&d, 0, sizeof(d));
  d.has_x = p->has_x;
  d.x = p->x;
  d.has_y = p->has_y;
  d.y = p->y;
  d.has_angle = p->has_angle;
  d.angle = p->angle;
  d.wake = opt_wake(o);
  return p2_teleport(ctx, body, &d);
}

LubStatus lub_phys2d_set_target(LubContext *ctx, LubHandle body,
                                const LubPoseDesc *p,
                                const LubCommandOpts *o) {
  if (!p)
    return lub_api_fail(lub_api_app(ctx), "phys2d_set_target: target required");
  P2SetTarget d;
  memset(&d, 0, sizeof(d));
  d.has_x = p->has_x;
  d.x = p->x;
  d.has_y = p->has_y;
  d.y = p->y;
  d.has_angle = p->has_angle;
  d.angle = p->angle;
  d.time_step = o && o->has_time_step ? o->time_step : 0.0f;
  d.wake = opt_wake(o);
  return p2_set_target(ctx, body, &d);
}

LubStatus lub_phys2d_set_mass_data(LubContext *ctx, LubHandle body,
                                   const LubMassDataDesc *m,
                                   const LubCommandOpts *o) {
  if (!m)
    return lub_api_fail(lub_api_app(ctx), "phys2d_set_mass_data: mass_data required");
  P2MassData cur;
  LubStatus st = p2_mass(ctx, body, &cur);
  if (st != LUB_OK)
    return st;
  P2MassDataDesc d;
  d.mass = m->has_mass ? m->mass : cur.mass;
  d.inertia = m->has_inertia ? m->inertia : cur.inertia;
  d.center_x = m->has_local_center ? m->local_center.x : cur.local_center_x;
  d.center_y = m->has_local_center ? m->local_center.y : cur.local_center_y;
  return p2_set_mass_data(ctx, body, &d, opt_wake(o));
}

#undef OPT
