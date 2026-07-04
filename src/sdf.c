#include "sdf.h"

#include "surfacenets.h"

#include <lauxlib.h>
#include <lua.h>
#include <math.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

// The Lua tree is flattened once into this array (validating as we go), so
// the per-grid-point evaluation never touches the Lua API. Evaluation is a
// small recursion over node indices: transforms rewrite the point on the way
// down, combinators merge child distances on the way up.

enum {
  OP_SPHERE,
  OP_BOX,
  OP_CAPSULE,
  OP_TORUS,
  OP_MOVE,
  OP_ROTATE,
  OP_SCALE,
  OP_MIRROR_X,
  OP_PAINT,
  OP_BONE,
  OP_UNION,
  OP_SMIN,
  OP_SUBTRACT,
  OP_SSUB,
  OP_INTERSECT,
};

// per-vertex material: albedo rgb + metallic + roughness
#define SDF_MAT_N 5
// skinning parts (bone nodes) per tree
#define SDF_MAX_PARTS 8

#define SDF_MAX_DEPTH 64
#define SDF_MAX_NODES 4096

typedef struct {
  int op;
  int a, b; // flat child indices, -1 = none; xform/mirror child in a
  // params. capsule: ax ay az bax bay baz r inv_baba;
  // rotate: forward rotation matrix, row-major (eval applies the transpose)
  float p[10];
} SdfNode;

// bone ノードが宣言する skinning 部位。path は root からその bone までに
// 通過した xform/mirror ノードの flat index 列で、頂点の部位距離を測るとき
// 同じ点変換を再現するのに使う。
typedef struct {
  int node; // OP_BONE の flat index (距離評価はここから子へ)
  char name[32];
  float pivot[3];
  int path[SDF_MAX_DEPTH];
  int path_len;
} SdfPart;

typedef struct {
  lua_State *L;
  SdfNode *nodes;
  int len, cap;
  int depth;
  SdfPart parts[SDF_MAX_PARTS];
  int part_count;
  int xpath[SDF_MAX_DEPTH]; // 現在の再帰位置までの xform/mirror ノード列
  int xpath_len;
} SdfBuild;

static float need_num(lua_State *L, int t, const char *op, const char *k) {
  lua_getfield(L, t, k);
  if (!lua_isnumber(L, -1))
    luaL_error(L, "sdf_mesh: node '%s' needs number field '%s'", op, k);
  float v = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return v;
}

static float opt_num(lua_State *L, int t, const char *k, float def) {
  lua_getfield(L, t, k);
  float v = lua_isnumber(L, -1) ? (float)lua_tonumber(L, -1) : def;
  lua_pop(L, 1);
  return v;
}

static int node_append(SdfBuild *b) {
  if (b->len >= SDF_MAX_NODES)
    luaL_error(b->L, "sdf_mesh: tree has more than %d nodes", SDF_MAX_NODES);
  if (b->len >= b->cap) {
    int cap = b->cap ? b->cap * 2 : 64;
    SdfNode *p = (SdfNode *)realloc(b->nodes, (size_t)cap * sizeof(SdfNode));
    if (!p)
      luaL_error(b->L, "sdf_mesh: out of memory");
    b->nodes = p;
    b->cap = cap;
  }
  SdfNode *n = &b->nodes[b->len];
  memset(n, 0, sizeof(*n));
  n->a = n->b = -1;
  return b->len++;
}

static int flatten(SdfBuild *b, int t);

static int need_child(SdfBuild *b, int t, const char *op, const char *k) {
  lua_State *L = b->L;
  lua_getfield(L, t, k);
  if (!lua_istable(L, -1))
    luaL_error(L, "sdf_mesh: node '%s' needs child table '%s'", op, k);
  int child = flatten(b, lua_gettop(L));
  lua_pop(L, 1);
  return child;
}

static int flatten(SdfBuild *b, int t) {
  lua_State *L = b->L;
  t = lua_absindex(L, t);
  if (++b->depth > SDF_MAX_DEPTH)
    luaL_error(L, "sdf_mesh: tree deeper than %d", SDF_MAX_DEPTH);
  luaL_checkstack(L, 8, "sdf_mesh");

  lua_getfield(L, t, "op");
  const char *op = lua_tostring(L, -1);
  if (!op)
    luaL_error(L, "sdf_mesh: node without string field 'op'");
  int ni = node_append(b);
  SdfNode *n = &b->nodes[ni]; // valid until the next node_append

  if (strcmp(op, "sphere") == 0) {
    n->op = OP_SPHERE;
    n->p[0] = need_num(L, t, op, "r");
  } else if (strcmp(op, "box") == 0) {
    n->op = OP_BOX;
    n->p[0] = need_num(L, t, op, "hx");
    n->p[1] = need_num(L, t, op, "hy");
    n->p[2] = need_num(L, t, op, "hz");
  } else if (strcmp(op, "capsule") == 0) {
    n->op = OP_CAPSULE;
    float ax = need_num(L, t, op, "ax");
    float ay = need_num(L, t, op, "ay");
    float az = need_num(L, t, op, "az");
    float bx = need_num(L, t, op, "bx");
    float by = need_num(L, t, op, "by");
    float bz = need_num(L, t, op, "bz");
    n->p[0] = ax;
    n->p[1] = ay;
    n->p[2] = az;
    n->p[3] = bx - ax;
    n->p[4] = by - ay;
    n->p[5] = bz - az;
    n->p[6] = need_num(L, t, op, "r");
    float baba = n->p[3] * n->p[3] + n->p[4] * n->p[4] + n->p[5] * n->p[5];
    n->p[7] = baba > 0 ? 1.0f / baba : 0; // a == b degenerates to a sphere
  } else if (strcmp(op, "torus") == 0) {
    n->op = OP_TORUS;
    n->p[0] = need_num(L, t, op, "rmajor");
    n->p[1] = need_num(L, t, op, "rminor");
  } else if (strcmp(op, "move") == 0) {
    n->op = OP_MOVE;
    n->p[0] = need_num(L, t, op, "x");
    n->p[1] = need_num(L, t, op, "y");
    n->p[2] = need_num(L, t, op, "z");
  } else if (strcmp(op, "rotate") == 0) {
    n->op = OP_ROTATE;
    float qx = need_num(L, t, op, "qx");
    float qy = need_num(L, t, op, "qy");
    float qz = need_num(L, t, op, "qz");
    float qw = need_num(L, t, op, "qw");
    float ql = sqrtf(qx * qx + qy * qy + qz * qz + qw * qw);
    if (ql <= 0)
      luaL_error(L, "sdf_mesh: rotate quaternion has zero length");
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
  } else if (strcmp(op, "scale") == 0) {
    n->op = OP_SCALE;
    n->p[0] = need_num(L, t, op, "s");
    if (n->p[0] <= 0)
      luaL_error(L, "sdf_mesh: scale 's' must be > 0");
  } else if (strcmp(op, "mirror_x") == 0) {
    n->op = OP_MIRROR_X;
  } else if (strcmp(op, "paint") == 0) {
    n->op = OP_PAINT;
    n->p[0] = need_num(L, t, op, "cr");
    n->p[1] = need_num(L, t, op, "cg");
    n->p[2] = need_num(L, t, op, "cb");
    n->p[3] = opt_num(L, t, "metallic", 0.0f);
    n->p[4] = opt_num(L, t, "roughness", 0.8f);
  } else if (strcmp(op, "bone") == 0) {
    n->op = OP_BONE;
    n->p[0] = need_num(L, t, op, "px");
    n->p[1] = need_num(L, t, op, "py");
    n->p[2] = need_num(L, t, op, "pz");
    if (b->part_count >= SDF_MAX_PARTS)
      luaL_error(L, "sdf_mesh: more than %d bones", SDF_MAX_PARTS);
    lua_getfield(L, t, "name");
    const char *bn = lua_tostring(L, -1);
    if (!bn)
      luaL_error(L, "sdf_mesh: bone needs string field 'name'");
    SdfPart *part = &b->parts[b->part_count++];
    part->node = ni;
    strncpy(part->name, bn, sizeof(part->name) - 1);
    part->name[sizeof(part->name) - 1] = '\0';
    part->pivot[0] = n->p[0];
    part->pivot[1] = n->p[1];
    part->pivot[2] = n->p[2];
    memcpy(part->path, b->xpath, (size_t)b->xpath_len * sizeof(int));
    part->path_len = b->xpath_len;
    lua_pop(L, 1);
  } else if (strcmp(op, "union") == 0) {
    n->op = OP_UNION;
  } else if (strcmp(op, "smin") == 0 || strcmp(op, "ssub") == 0) {
    n->op = op[1] == 'm' ? OP_SMIN : OP_SSUB;
    n->p[0] = need_num(L, t, op, "k");
    if (n->p[0] <= 0)
      luaL_error(L, "sdf_mesh: node '%s' needs 'k' > 0", op);
  } else if (strcmp(op, "subtract") == 0) {
    n->op = OP_SUBTRACT;
  } else if (strcmp(op, "intersect") == 0) {
    n->op = OP_INTERSECT;
  } else {
    luaL_error(L, "sdf_mesh: unknown op '%s'", op);
  }

  int kind = b->nodes[ni].op;
  if (kind == OP_MOVE || kind == OP_ROTATE || kind == OP_SCALE ||
      kind == OP_MIRROR_X || kind == OP_PAINT || kind == OP_BONE) {
    bool is_xform = kind != OP_PAINT && kind != OP_BONE;
    if (is_xform)
      b->xpath[b->xpath_len++] = ni;
    int a = need_child(b, t, op, "c");
    b->nodes[ni].a = a;
    if (is_xform)
      --b->xpath_len;
  } else if (kind >= OP_UNION) {
    int a = need_child(b, t, op, "a");
    int c = need_child(b, t, op, "b");
    b->nodes[ni].a = a;
    b->nodes[ni].b = c;
  }
  lua_pop(L, 1); // op string (kept anchored for the error messages above)
  --b->depth;
  return ni;
}

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

static void push_vec3_table(lua_State *L, const float v[3]) {
  lua_createtable(L, 3, 0);
  for (int i = 0; i < 3; ++i) {
    lua_pushnumber(L, v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

int lub_sdf_mesh(lua_State *L) {
  luaL_checktype(L, 1, LUA_TTABLE);
  int n = (int)luaL_checkinteger(L, 2);
  if (n < 4 || n > 512)
    return luaL_error(L, "sdf_mesh: n must be in [4, 512] (got %d)", n);

  lua_getfield(L, 1, "version");
  if (!lua_isinteger(L, -1) || lua_tointeger(L, -1) != 1)
    return luaL_error(L, "sdf_mesh: tree.version must be 1");
  lua_pop(L, 1);
  lua_getfield(L, 1, "root");
  if (!lua_istable(L, -1))
    return luaL_error(L, "sdf_mesh: tree.root must be a node table");

  // NOTE: flatten raises Lua errors on invalid trees; b.nodes leaks on that
  // path. Acceptable: authoring-time errors are rare and small, and keeping
  // flatten free of pcall plumbing keeps the code simple.
  SdfBuild b = {L, NULL, 0, 0, 0};
  int root = flatten(&b, -1);
  lua_pop(L, 1); // root table

  float mn[3], mx[3];
  sdf_aabb(b.nodes, root, mn, mx);
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
      free(b.nodes);
      return luaL_error(L, "sdf_mesh: grid too large (%dx%dx%d)", cnt[0],
                        cnt[1], cnt[2]);
    }
    float *grid = (float *)malloc(total * sizeof(float));
    if (!grid) {
      free(b.nodes);
      return luaL_error(L, "sdf_mesh: out of memory");
    }
    size_t gi = 0;
    for (int z = 0; z < cnt[2]; ++z) {
      float pz = org[2] + (float)z * cell;
      for (int y = 0; y < cnt[1]; ++y) {
        float py = org[1] + (float)y * cell;
        for (int x = 0; x < cnt[0]; ++x)
          grid[gi++] =
              sdf_eval(b.nodes, root, org[0] + (float)x * cell, py, pz);
      }
    }
    int ok = sn_mesh_from_grid(grid, cnt[0], cnt[1], cnt[2], cell, org[0],
                               org[1], org[2], &m);
    free(grid);
    if (!ok) {
      free(b.nodes);
      return luaL_error(L, "sdf_mesh: out of memory");
    }
  }
  sn_mesh_push(L, &m);

  // bake per-vertex materials (albedo rgb + metallic/roughness) by
  // re-evaluating the tree with materials at each vertex position
  static const float DEFAULT_MAT[SDF_MAT_N] = {0.8f, 0.8f, 0.8f, 0.0f, 0.8f};
  lua_createtable(L, (int)(m.vert_count * 3), 0);
  lua_createtable(L, (int)(m.vert_count * 2), 0);
  for (size_t v = 0; v < m.vert_count; ++v) {
    float mat[SDF_MAT_N];
    sdf_eval_mat(b.nodes, root, m.positions[v * 3], m.positions[v * 3 + 1],
                 m.positions[v * 3 + 2], DEFAULT_MAT, mat);
    for (int i = 0; i < 3; ++i) {
      lua_pushnumber(L, mat[i]);
      lua_rawseti(L, -3, (int)(v * 3 + i + 1));
    }
    for (int i = 0; i < 2; ++i) {
      lua_pushnumber(L, mat[3 + i]);
      lua_rawseti(L, -2, (int)(v * 2 + i + 1));
    }
  }
  lua_setfield(L, -3, "metal_rough");
  lua_setfield(L, -2, "colors");

  // skinning: bone ノードがあれば頂点ごとに最寄り 2 部位の重みを焼く。
  // w0 = softmax(-d/k) の 2 部位版 = 1 / (1 + e^((d0-d1)/k))。k が blend 幅。
  if (b.part_count > 0) {
    float skin_k = (float)luaL_optnumber(L, 3, 0.1);
    if (skin_k <= 0)
      skin_k = 0.1f;
    lua_createtable(L, (int)(m.vert_count * 2), 0); // joints (0-based index)
    lua_createtable(L, (int)(m.vert_count * 2), 0); // weights
    for (size_t v = 0; v < m.vert_count; ++v) {
      float px = m.positions[v * 3];
      float py = m.positions[v * 3 + 1];
      float pz = m.positions[v * 3 + 2];
      int j0 = 0, j1 = 0;
      float d0 = 1e30f, d1 = 1e30f;
      for (int pi = 0; pi < b.part_count; ++pi) {
        float d = sdf_eval_part(b.nodes, &b.parts[pi], px, py, pz);
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
          b.part_count > 1 ? 1.0f / (1.0f + expf((d0 - d1) / skin_k)) : 1.0f;
      lua_pushinteger(L, j0);
      lua_rawseti(L, -3, (int)(v * 2 + 1));
      lua_pushinteger(L, j1);
      lua_rawseti(L, -3, (int)(v * 2 + 2));
      lua_pushnumber(L, w0);
      lua_rawseti(L, -2, (int)(v * 2 + 1));
      lua_pushnumber(L, 1.0f - w0);
      lua_rawseti(L, -2, (int)(v * 2 + 2));
    }
    lua_setfield(L, -3, "weights");
    lua_setfield(L, -2, "joints");

    lua_createtable(L, b.part_count, 0);
    for (int pi = 0; pi < b.part_count; ++pi) {
      lua_createtable(L, 0, 4);
      lua_pushstring(L, b.parts[pi].name);
      lua_setfield(L, -2, "name");
      lua_pushnumber(L, b.parts[pi].pivot[0]);
      lua_setfield(L, -2, "x");
      lua_pushnumber(L, b.parts[pi].pivot[1]);
      lua_setfield(L, -2, "y");
      lua_pushnumber(L, b.parts[pi].pivot[2]);
      lua_setfield(L, -2, "z");
      lua_rawseti(L, -2, pi + 1);
    }
    lua_setfield(L, -2, "bones");
  }

  free(b.nodes);
  sn_mesh_free(&m);
  push_vec3_table(L, mn);
  lua_setfield(L, -2, "bounds_min");
  push_vec3_table(L, mx);
  lua_setfield(L, -2, "bounds_max");
  lua_pushnumber(L, cell);
  lua_setfield(L, -2, "cell");
  return 1;
}
