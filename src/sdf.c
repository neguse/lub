#include "sdf.h"

#include "lub/lub_api.h"
#include "surfacenets.h"

#include <math.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// The tree arrives already flat (LubSdfNodeDescDesc array from the C API); it
// is converted once into SdfNode (validating and precomputing as we go), so the
// per-grid-point evaluation is a small recursion over node indices:
// transforms rewrite the point on the way down, combinators merge child
// distances on the way up.

static float clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

static float sdf_eval(const SdfNode *ns, int ni, float x, float y, float z) {
  const SdfNode *n = &ns[ni];
  switch (n->op) {
  case OP_SPHERE:
    return sqrtf(x * x + y * y + z * z) - n->p[0];
  case OP_BOX: {
    float qx = fabsf(x) - n->p[0];
    float qy = fabsf(y) - n->p[1];
    float qz = fabsf(z) - n->p[2];
    float ox = fmaxf(qx, 0), oy = fmaxf(qy, 0), oz = fmaxf(qz, 0);
    return sqrtf(ox * ox + oy * oy + oz * oz) +
           fminf(fmaxf(qx, fmaxf(qy, qz)), 0);
  }
  case OP_CAPSULE: {
    float pax = x - n->p[0], pay = y - n->p[1], paz = z - n->p[2];
    float h =
        clamp01((pax * n->p[3] + pay * n->p[4] + paz * n->p[5]) * n->p[7]);
    float dx = pax - n->p[3] * h;
    float dy = pay - n->p[4] * h;
    float dz = paz - n->p[5] * h;
    return sqrtf(dx * dx + dy * dy + dz * dz) - n->p[6];
  }
  case OP_TORUS: {
    float qx = sqrtf(x * x + z * z) - n->p[0];
    return sqrtf(qx * qx + y * y) - n->p[1];
  }
  case OP_MOVE:
    return sdf_eval(ns, n->a, x - n->p[0], y - n->p[1], z - n->p[2]);
  case OP_ROTATE: {
    const float *m = n->p; // forward R; apply R^T to take p into child space
    return sdf_eval(ns, n->a, m[0] * x + m[3] * y + m[6] * z,
                    m[1] * x + m[4] * y + m[7] * z,
                    m[2] * x + m[5] * y + m[8] * z);
  }
  case OP_SCALE:
    return sdf_eval(ns, n->a, x / n->p[0], y / n->p[0], z / n->p[0]) * n->p[0];
  case OP_MIRROR_X:
    return sdf_eval(ns, n->a, fabsf(x), y, z);
  case OP_PAINT:
  case OP_BONE:
    return sdf_eval(ns, n->a, x, y, z);
  case OP_UNION:
    return fminf(sdf_eval(ns, n->a, x, y, z), sdf_eval(ns, n->b, x, y, z));
  case OP_SMIN: {
    float a = sdf_eval(ns, n->a, x, y, z);
    float c = sdf_eval(ns, n->b, x, y, z);
    float k = n->p[0];
    float h = clamp01(0.5f + 0.5f * (c - a) / k);
    return c + (a - c) * h - k * h * (1 - h);
  }
  case OP_SUBTRACT:
    return fmaxf(sdf_eval(ns, n->a, x, y, z), -sdf_eval(ns, n->b, x, y, z));
  case OP_SSUB: {
    float d = sdf_eval(ns, n->a, x, y, z);
    float s = sdf_eval(ns, n->b, x, y, z);
    float k = n->p[0];
    float h = clamp01(0.5f - 0.5f * (d + s) / k);
    return d + (-s - d) * h + k * h * (1 - h);
  }
  case OP_INTERSECT:
    return fmaxf(sdf_eval(ns, n->a, x, y, z), sdf_eval(ns, n->b, x, y, z));
  }
  return 1e30f;
}

// Material evaluation: like sdf_eval but carries (distance, material) pairs.
// Only called once per output vertex (not per grid point), so the extra cost
// is negligible. paint sets the material for its subtree (innermost wins via
// `inherited`), union/intersect take the winner's material, smin/ssub lerp
// materials with the same h as the distance so the material blend matches
// the geometric blend. Where a cutter wins (subtract/ssub) its own material
// shows — cut surfaces reveal what the cutter is "made of".
static float sdf_eval_mat(const SdfNode *ns, int ni, float x, float y, float z,
                          const float *inherited, float *out) {
  const SdfNode *n = &ns[ni];
  switch (n->op) {
  case OP_MOVE:
    return sdf_eval_mat(ns, n->a, x - n->p[0], y - n->p[1], z - n->p[2],
                        inherited, out);
  case OP_ROTATE: {
    const float *m = n->p;
    return sdf_eval_mat(ns, n->a, m[0] * x + m[3] * y + m[6] * z,
                        m[1] * x + m[4] * y + m[7] * z,
                        m[2] * x + m[5] * y + m[8] * z, inherited, out);
  }
  case OP_SCALE:
    return sdf_eval_mat(ns, n->a, x / n->p[0], y / n->p[0], z / n->p[0],
                        inherited, out) *
           n->p[0];
  case OP_MIRROR_X:
    return sdf_eval_mat(ns, n->a, fabsf(x), y, z, inherited, out);
  case OP_PAINT:
    return sdf_eval_mat(ns, n->a, x, y, z, n->p, out);
  case OP_BONE:
    return sdf_eval_mat(ns, n->a, x, y, z, inherited, out);
  case OP_UNION:
  case OP_INTERSECT: {
    float mb[SDF_MAT_N];
    float da = sdf_eval_mat(ns, n->a, x, y, z, inherited, out);
    float db = sdf_eval_mat(ns, n->b, x, y, z, inherited, mb);
    int b_wins = n->op == OP_UNION ? db < da : db > da;
    if (b_wins) {
      memcpy(out, mb, sizeof(mb));
      return db;
    }
    return da;
  }
  case OP_SMIN: {
    float mb[SDF_MAT_N];
    float a = sdf_eval_mat(ns, n->a, x, y, z, inherited, out);
    float c = sdf_eval_mat(ns, n->b, x, y, z, inherited, mb);
    float k = n->p[0];
    float h = clamp01(0.5f + 0.5f * (c - a) / k); // h = 1 -> a wins
    for (int i = 0; i < SDF_MAT_N; ++i)
      out[i] = mb[i] + (out[i] - mb[i]) * h;
    return c + (a - c) * h - k * h * (1 - h);
  }
  case OP_SUBTRACT: {
    float mb[SDF_MAT_N];
    float d = sdf_eval_mat(ns, n->a, x, y, z, inherited, out);
    float s = sdf_eval_mat(ns, n->b, x, y, z, inherited, mb);
    if (-s > d) {
      memcpy(out, mb, sizeof(mb));
      return -s;
    }
    return d;
  }
  case OP_SSUB: {
    float mb[SDF_MAT_N];
    float d = sdf_eval_mat(ns, n->a, x, y, z, inherited, out);
    float s = sdf_eval_mat(ns, n->b, x, y, z, inherited, mb);
    float k = n->p[0];
    float h = clamp01(0.5f - 0.5f * (d + s) / k); // h = 1 -> cutter wins
    for (int i = 0; i < SDF_MAT_N; ++i)
      out[i] = out[i] + (mb[i] - out[i]) * h;
    return d + (-s - d) * h + k * h * (1 - h);
  }
  default: // primitives
    memcpy(out, inherited, SDF_MAT_N * sizeof(float));
    return sdf_eval(ns, ni, x, y, z);
  }
}

// 部位 (bone subtree) までの距離。root からの xform 列を再現して点を部位の
// 局所空間へ落としてから評価する (scale は距離も直す)。
static float sdf_eval_part(const SdfNode *ns, const SdfPart *part, float x,
                           float y, float z) {
  float scale = 1;
  for (int i = 0; i < part->path_len; ++i) {
    const SdfNode *n = &ns[part->path[i]];
    switch (n->op) {
    case OP_MOVE:
      x -= n->p[0];
      y -= n->p[1];
      z -= n->p[2];
      break;
    case OP_ROTATE: {
      const float *m = n->p;
      float nx = m[0] * x + m[3] * y + m[6] * z;
      float ny = m[1] * x + m[4] * y + m[7] * z;
      float nz = m[2] * x + m[5] * y + m[8] * z;
      x = nx;
      y = ny;
      z = nz;
      break;
    }
    case OP_SCALE:
      x /= n->p[0];
      y /= n->p[0];
      z /= n->p[0];
      scale *= n->p[0];
      break;
    case OP_MIRROR_X:
      x = fabsf(x);
      break;
    }
  }
  return sdf_eval(ns, part->node, x, y, z) * scale;
}

// Conservative AABB per node; exactness is not required (the grid adds a
// one-cell margin on top). smin/ssub bulge outward by at most k/4.
static void sdf_aabb(const SdfNode *ns, int ni, float mn[3], float mx[3]) {
  const SdfNode *n = &ns[ni];
  switch (n->op) {
  case OP_SPHERE:
    for (int i = 0; i < 3; ++i) {
      mn[i] = -n->p[0];
      mx[i] = n->p[0];
    }
    return;
  case OP_BOX:
    for (int i = 0; i < 3; ++i) {
      mn[i] = -n->p[i];
      mx[i] = n->p[i];
    }
    return;
  case OP_CAPSULE:
    for (int i = 0; i < 3; ++i) {
      float a = n->p[i], b = n->p[i] + n->p[3 + i];
      mn[i] = fminf(a, b) - n->p[6];
      mx[i] = fmaxf(a, b) + n->p[6];
    }
    return;
  case OP_TORUS: {
    float r = n->p[0] + n->p[1];
    mn[0] = -r;
    mx[0] = r;
    mn[1] = -n->p[1];
    mx[1] = n->p[1];
    mn[2] = -r;
    mx[2] = r;
    return;
  }
  case OP_MOVE:
    sdf_aabb(ns, n->a, mn, mx);
    for (int i = 0; i < 3; ++i) {
      mn[i] += n->p[i];
      mx[i] += n->p[i];
    }
    return;
  case OP_PAINT:
  case OP_BONE:
    sdf_aabb(ns, n->a, mn, mx);
    return;
  case OP_ROTATE: {
    float cmn[3], cmx[3];
    sdf_aabb(ns, n->a, cmn, cmx);
    const float *m = n->p;
    for (int i = 0; i < 3; ++i) {
      mn[i] = 1e30f;
      mx[i] = -1e30f;
    }
    for (int c = 0; c < 8; ++c) {
      float v[3] = {c & 1 ? cmx[0] : cmn[0], c & 2 ? cmx[1] : cmn[1],
                    c & 4 ? cmx[2] : cmn[2]};
      for (int i = 0; i < 3; ++i) {
        float w = m[i * 3] * v[0] + m[i * 3 + 1] * v[1] + m[i * 3 + 2] * v[2];
        mn[i] = fminf(mn[i], w);
        mx[i] = fmaxf(mx[i], w);
      }
    }
    return;
  }
  case OP_SCALE:
    sdf_aabb(ns, n->a, mn, mx);
    for (int i = 0; i < 3; ++i) {
      mn[i] *= n->p[0];
      mx[i] *= n->p[0];
    }
    return;
  case OP_MIRROR_X: {
    sdf_aabb(ns, n->a, mn, mx);
    float m = fmaxf(fabsf(mn[0]), fabsf(mx[0]));
    mn[0] = -m;
    mx[0] = m;
    return;
  }
  case OP_UNION:
  case OP_SMIN: {
    float bmn[3], bmx[3];
    sdf_aabb(ns, n->a, mn, mx);
    sdf_aabb(ns, n->b, bmn, bmx);
    float k = n->op == OP_SMIN ? n->p[0] * 0.25f : 0;
    for (int i = 0; i < 3; ++i) {
      mn[i] = fminf(mn[i], bmn[i]) - k;
      mx[i] = fmaxf(mx[i], bmx[i]) + k;
    }
    return;
  }
  case OP_SUBTRACT:
  case OP_SSUB: {
    sdf_aabb(ns, n->a, mn, mx);
    float k = n->op == OP_SSUB ? n->p[0] * 0.25f : 0;
    for (int i = 0; i < 3; ++i) {
      mn[i] -= k;
      mx[i] += k;
    }
    return;
  }
  case OP_INTERSECT: {
    float bmn[3], bmx[3];
    sdf_aabb(ns, n->a, mn, mx);
    sdf_aabb(ns, n->b, bmn, bmx);
    for (int i = 0; i < 3; ++i) {
      mn[i] = fmaxf(mn[i], bmn[i]);
      mx[i] = fmaxf(fminf(mx[i], bmx[i]), mn[i]); // disjoint -> degenerate
    }
    return;
  }
  }
}

// ---------------------------------------------------------------------------
// convert (LubSdfNodeDescDesc -> SdfNode, bone parts)

typedef struct {
  const LubSdfNodeDesc *in;
  int count;
  SdfTree *t;
  int depth;
  int xpath[SDF_MAX_DEPTH];
  int xpath_len;
  char *err;
  size_t err_size;
} SdfConv;

static bool conv_fail(SdfConv *c, const char *fmt, const char *arg) {
  snprintf(c->err, c->err_size, fmt, arg);
  return false;
}

static const char *op_name(int op) {
  static const char *const names[] = {
      "sphere", "box",   "capsule",  "torus", "move",
      "rotate", "scale", "mirror_x", "paint", "bone",
      "union",  "smin",  "subtract", "ssub",  "intersect"};
  return op >= 0 && op < (int)(sizeof(names) / sizeof(names[0])) ? names[op]
                                                                 : "?";
}

// in[i] を t->nodes[i] に変換する。子は再帰 (index はそのまま)。
static bool conv_node(SdfConv *c, int i) {
  if (i < 0 || i >= c->count)
    return conv_fail(c, "sdf_mesh: node index out of range%s", "");
  if (++c->depth > SDF_MAX_DEPTH)
    return conv_fail(c, "sdf_mesh: tree deeper than the limit%s", "");
  const LubSdfNodeDesc *in = &c->in[i];
  SdfNode *n = &c->t->nodes[i];
  const float *q = in->params;
  memset(n, 0, sizeof(*n));
  n->a = -1;
  n->b = -1;
  switch (in->op) {
  case LUB_MESH_SDF_OP_SPHERE:
    n->op = OP_SPHERE;
    n->p[0] = q[0];
    break;
  case LUB_MESH_SDF_OP_BOX:
    n->op = OP_BOX;
    n->p[0] = q[0];
    n->p[1] = q[1];
    n->p[2] = q[2];
    break;
  case LUB_MESH_SDF_OP_CAPSULE: {
    n->op = OP_CAPSULE;
    n->p[0] = q[0];
    n->p[1] = q[1];
    n->p[2] = q[2];
    n->p[3] = q[3] - q[0];
    n->p[4] = q[4] - q[1];
    n->p[5] = q[5] - q[2];
    n->p[6] = q[6];
    float baba = n->p[3] * n->p[3] + n->p[4] * n->p[4] + n->p[5] * n->p[5];
    n->p[7] = baba > 0 ? 1.0f / baba : 0; // a == b degenerates to a sphere
    break;
  }
  case LUB_MESH_SDF_OP_TORUS:
    n->op = OP_TORUS;
    n->p[0] = q[0];
    n->p[1] = q[1];
    break;
  case LUB_MESH_SDF_OP_MOVE:
    n->op = OP_MOVE;
    n->p[0] = q[0];
    n->p[1] = q[1];
    n->p[2] = q[2];
    break;
  case LUB_MESH_SDF_OP_ROTATE: {
    n->op = OP_ROTATE;
    float qx = q[0], qy = q[1], qz = q[2], qw = q[3];
    float ql = sqrtf(qx * qx + qy * qy + qz * qz + qw * qw);
    if (ql <= 0)
      return conv_fail(c, "sdf_mesh: rotate quaternion has zero length%s", "");
    qx /= ql;
    qy /= ql;
    qz /= ql;
    qw /= ql;
    n->p[0] = 1 - 2 * (qy * qy + qz * qz);
    n->p[1] = 2 * (qx * qy - qz * qw);
    n->p[2] = 2 * (qx * qz + qy * qw);
    n->p[3] = 2 * (qx * qy + qz * qw);
    n->p[4] = 1 - 2 * (qx * qx + qz * qz);
    n->p[5] = 2 * (qy * qz - qx * qw);
    n->p[6] = 2 * (qx * qz - qy * qw);
    n->p[7] = 2 * (qy * qz + qx * qw);
    n->p[8] = 1 - 2 * (qx * qx + qy * qy);
    break;
  }
  case LUB_MESH_SDF_OP_SCALE:
    n->op = OP_SCALE;
    n->p[0] = q[0];
    if (n->p[0] <= 0)
      return conv_fail(c, "sdf_mesh: scale 's' must be > 0%s", "");
    break;
  case LUB_MESH_SDF_OP_MIRROR_X:
    n->op = OP_MIRROR_X;
    break;
  case LUB_MESH_SDF_OP_PAINT:
    n->op = OP_PAINT;
    n->p[0] = q[0];
    n->p[1] = q[1];
    n->p[2] = q[2];
    n->p[3] = q[3];
    n->p[4] = q[4];
    break;
  case LUB_MESH_SDF_OP_BONE: {
    n->op = OP_BONE;
    n->p[0] = q[0];
    n->p[1] = q[1];
    n->p[2] = q[2];
    if (c->t->part_count >= SDF_MAX_PARTS)
      return conv_fail(c, "sdf_mesh: more than the bone limit%s", "");
    if (!in->name.ptr || in->name.len <= 0)
      return conv_fail(c, "sdf_mesh: bone needs a name%s", "");
    SdfPart *part = &c->t->parts[c->t->part_count++];
    part->node = i;
    {
      size_t nl = (size_t)in->name.len;
      if (nl >= sizeof(part->name))
        nl = sizeof(part->name) - 1;
      memcpy(part->name, in->name.ptr, nl);
      part->name[nl] = '\0';
    }
    part->pivot[0] = n->p[0];
    part->pivot[1] = n->p[1];
    part->pivot[2] = n->p[2];
    memcpy(part->path, c->xpath, (size_t)c->xpath_len * sizeof(int));
    part->path_len = c->xpath_len;
    break;
  }
  case LUB_MESH_SDF_OP_UNION:
    n->op = OP_UNION;
    break;
  case LUB_MESH_SDF_OP_SMIN:
  case LUB_MESH_SDF_OP_SSUB:
    n->op = in->op == LUB_MESH_SDF_OP_SMIN ? OP_SMIN : OP_SSUB;
    n->p[0] = q[0];
    if (n->p[0] <= 0)
      return conv_fail(c, "sdf_mesh: node '%s' needs 'k' > 0", op_name(n->op));
    break;
  case LUB_MESH_SDF_OP_SUBTRACT:
    n->op = OP_SUBTRACT;
    break;
  case LUB_MESH_SDF_OP_INTERSECT:
    n->op = OP_INTERSECT;
    break;
  default:
    return conv_fail(c, "sdf_mesh: unknown op%s", "");
  }

  int kind = n->op;
  if (kind == OP_MOVE || kind == OP_ROTATE || kind == OP_SCALE ||
      kind == OP_MIRROR_X || kind == OP_PAINT || kind == OP_BONE) {
    bool is_xform = kind != OP_PAINT && kind != OP_BONE;
    if (in->a < 0)
      return conv_fail(c, "sdf_mesh: node '%s' needs a child", op_name(kind));
    if (is_xform)
      c->xpath[c->xpath_len++] = i;
    bool ok = conv_node(c, in->a);
    if (is_xform)
      --c->xpath_len;
    if (!ok)
      return false;
    c->t->nodes[i].a = in->a;
  } else if (kind >= OP_UNION) {
    if (in->a < 0 || in->b < 0)
      return conv_fail(c, "sdf_mesh: node '%s' needs two children",
                       op_name(kind));
    if (!conv_node(c, in->a) || !conv_node(c, in->b))
      return false;
    c->t->nodes[i].a = in->a;
    c->t->nodes[i].b = in->b;
  }
  --c->depth;
  return true;
}

bool sdf_tree_convert(const LubSdfNodeDesc *nodes, int count, int root,
                      SdfTree *out, char *err, size_t err_size) {
  memset(out, 0, sizeof(*out));
  if (!nodes || count <= 0 || root < 0 || root >= count) {
    snprintf(err, err_size, "sdf_mesh: empty tree");
    return false;
  }
  if (count > SDF_MAX_NODES) {
    snprintf(err, err_size, "sdf_mesh: tree has more than %d nodes",
             SDF_MAX_NODES);
    return false;
  }
  out->nodes = (SdfNode *)calloc((size_t)count, sizeof(SdfNode));
  if (!out->nodes) {
    snprintf(err, err_size, "sdf_mesh: out of memory");
    return false;
  }
  out->len = count;
  out->root = root;
  SdfConv c = {nodes, count, out, 0, {0}, 0, err, err_size};
  if (!conv_node(&c, root)) {
    sdf_tree_free(out);
    return false;
  }
  return true;
}

void sdf_tree_free(SdfTree *t) {
  free(t->nodes);
  t->nodes = NULL;
  t->len = 0;
}

// ---------------------------------------------------------------------------
// build

void sdf_mesh_out_free(SdfMeshOut *o) {
  sn_mesh_free(&o->mesh);
  free(o->colors);
  free(o->metal_rough);
  free(o->joints);
  free(o->weights);
  memset(o, 0, sizeof(*o));
}

bool sdf_mesh_build(const SdfTree *t, int n, float skin_k, SdfMeshOut *out,
                    char *err, size_t err_size) {
  memset(out, 0, sizeof(*out));
  if (n < 4 || n > 512) {
    snprintf(err, err_size, "sdf_mesh: n must be in [4, 512] (got %d)", n);
    return false;
  }
  const SdfNode *nodes = t->nodes;
  int root = t->root;
  float mn[3], mx[3];
  sdf_aabb(nodes, root, mn, mx);
  float ext[3], extmax = 0;
  for (int i = 0; i < 3; ++i) {
    ext[i] = mx[i] - mn[i];
    extmax = fmaxf(extmax, ext[i]);
  }

  SnMesh m = {0};
  float cell = 0;
  if (extmax > 0) {
    cell = extmax / (float)(n - 3);
    int cnt[3];
    float org[3];
    size_t total = 1;
    for (int i = 0; i < 3; ++i) {
      cnt[i] = (int)ceilf(ext[i] / cell) + 3;
      if (cnt[i] < 3)
        cnt[i] = 3;
      float span = (float)(cnt[i] - 1) * cell;
      org[i] = mn[i] - (span - ext[i]) * 0.5f;
      total *= (size_t)cnt[i];
    }
    if (total > (size_t)1 << 27) {
      snprintf(err, err_size, "sdf_mesh: grid too large (%dx%dx%d)", cnt[0],
               cnt[1], cnt[2]);
      return false;
    }
    float *grid = (float *)malloc(total * sizeof(float));
    if (!grid) {
      snprintf(err, err_size, "sdf_mesh: out of memory");
      return false;
    }
    size_t gi = 0;
    for (int z = 0; z < cnt[2]; ++z) {
      float pz = org[2] + (float)z * cell;
      for (int y = 0; y < cnt[1]; ++y) {
        float py = org[1] + (float)y * cell;
        for (int x = 0; x < cnt[0]; ++x)
          grid[gi++] = sdf_eval(nodes, root, org[0] + (float)x * cell, py, pz);
      }
    }
    int ok = sn_mesh_from_grid(grid, cnt[0], cnt[1], cnt[2], cell, org[0],
                               org[1], org[2], &m);
    free(grid);
    if (!ok) {
      snprintf(err, err_size, "sdf_mesh: out of memory");
      return false;
    }
  }
  out->mesh = m;
  size_t vc = m.vert_count;

  // bake per-vertex materials (albedo rgb + metallic/roughness) by
  // re-evaluating the tree with materials at each vertex position
  static const float DEFAULT_MAT[SDF_MAT_N] = {0.8f, 0.8f, 0.8f, 0.0f, 0.8f};
  out->colors = (float *)malloc((vc ? vc : 1) * 3 * sizeof(float));
  out->metal_rough = (float *)malloc((vc ? vc : 1) * 2 * sizeof(float));
  if (!out->colors || !out->metal_rough) {
    sdf_mesh_out_free(out);
    snprintf(err, err_size, "sdf_mesh: out of memory");
    return false;
  }
  for (size_t v = 0; v < vc; ++v) {
    float mat[SDF_MAT_N];
    sdf_eval_mat(nodes, root, m.positions[v * 3], m.positions[v * 3 + 1],
                 m.positions[v * 3 + 2], DEFAULT_MAT, mat);
    out->colors[v * 3] = mat[0];
    out->colors[v * 3 + 1] = mat[1];
    out->colors[v * 3 + 2] = mat[2];
    out->metal_rough[v * 2] = mat[3];
    out->metal_rough[v * 2 + 1] = mat[4];
  }

  // skinning: bone ノードがあれば頂点ごとに最寄り 2 部位の重みを焼く。
  // w0 = softmax(-d/k) の 2 部位版 = 1 / (1 + e^((d0-d1)/k))。k が blend 幅。
  if (t->part_count > 0) {
    if (skin_k <= 0)
      skin_k = 0.1f;
    out->joints = (float *)malloc((vc ? vc : 1) * 2 * sizeof(float));
    out->weights = (float *)malloc((vc ? vc : 1) * 2 * sizeof(float));
    if (!out->joints || !out->weights) {
      sdf_mesh_out_free(out);
      snprintf(err, err_size, "sdf_mesh: out of memory");
      return false;
    }
    for (size_t v = 0; v < vc; ++v) {
      float px = m.positions[v * 3];
      float py = m.positions[v * 3 + 1];
      float pz = m.positions[v * 3 + 2];
      int j0 = 0, j1 = 0;
      float d0 = 1e30f, d1 = 1e30f;
      for (int pi = 0; pi < t->part_count; ++pi) {
        float d = sdf_eval_part(nodes, &t->parts[pi], px, py, pz);
        if (d < d0) {
          d1 = d0;
          j1 = j0;
          d0 = d;
          j0 = pi;
        } else if (d < d1) {
          d1 = d;
          j1 = pi;
        }
      }
      float w0 =
          t->part_count > 1 ? 1.0f / (1.0f + expf((d0 - d1) / skin_k)) : 1.0f;
      out->joints[v * 2] = (float)j0;
      out->joints[v * 2 + 1] = (float)j1;
      out->weights[v * 2] = w0;
      out->weights[v * 2 + 1] = 1.0f - w0;
    }
  }
  memcpy(out->mn, mn, sizeof(mn));
  memcpy(out->mx, mx, sizeof(mx));
  out->cell = cell;
  return true;
}
