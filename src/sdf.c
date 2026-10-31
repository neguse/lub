#include "sdf.h"

#include "lub/lub_api.h"
#include "surfacenets.h"

#include <math.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// The tree arrives already flat (LubSdfNodeDesc array from the C API); it
// is converted once into SdfNode (validating and precomputing as we go), so the
// per-grid-point evaluation is a small recursion over node indices:
// transforms rewrite the point on the way down, combinators merge child
// distances on the way up.

static float clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

static float clampf(float v, float lo, float hi) {
  return v < lo ? lo : (v > hi ? hi : v);
}

static float sgn(float v) { return v > 0 ? 1.0f : (v < 0 ? -1.0f : 0.0f); }

static float len2(float x, float y) { return sqrtf(x * x + y * y); }

static float len3(float x, float y, float z) {
  return sqrtf(x * x + y * y + z * z);
}

// Distance from (dx, dy) of a 2D box-like query: the usual
// "min(max(d), 0) + length(max(d, 0))".
static float box2(float dx, float dy) {
  return fminf(fmaxf(dx, dy), 0) + len2(fmaxf(dx, 0), fmaxf(dy, 0));
}

static float box3(float qx, float qy, float qz) {
  return len3(fmaxf(qx, 0), fmaxf(qy, 0), fmaxf(qz, 0)) +
         fminf(fmaxf(qx, fmaxf(qy, qz)), 0);
}

// Primitive distances. The formulas follow Inigo Quilez's catalogue of
// distance functions (iquilezles.org/articles/distfunctions); all are exact
// except ellipsoid and tri_prism, which are bounds (good enough for meshing:
// only the sign and the gradient near the surface matter).
// Parameters are the precomputed ones from conv_node.

// Hull of two spheres (a, ra) and (b, rb): a capsule whose radius changes
// linearly along its axis. When one sphere contains the other the hull is
// just the larger sphere.
// p: ax ay az bax bay baz ra rb baba (baba - (ra - rb)^2)
static float round_cone(const float *p, float x, float y, float z) {
  float pax = x - p[0], pay = y - p[1], paz = z - p[2];
  float bax = p[3], bay = p[4], baz = p[5];
  float ra = p[6], rb = p[7], l2 = p[8], a2 = p[9];
  if (a2 <= 0)
    return fminf(len3(pax, pay, paz) - ra,
                 len3(pax - bax, pay - bay, paz - baz) - rb);
  float rr = ra - rb;
  float t = pax * bax + pay * bay + paz * baz;
  float u = t - l2;
  float wx = pax * l2 - bax * t, wy = pay * l2 - bay * t,
        wz = paz * l2 - baz * t;
  float x2 = wx * wx + wy * wy + wz * wz;
  float t2 = t * t * l2, u2 = u * u * l2;
  float k = sgn(rr) * rr * rr * x2;
  if (sgn(u) * a2 * u2 > k)
    return sqrtf(x2 + u2) / l2 - rb;
  if (sgn(t) * a2 * t2 < k)
    return sqrtf(x2 + t2) / l2 - ra;
  return (sqrtf(x2 * a2 / l2) + t * rr) / l2 - ra;
}

// p: hx hy hz e
static float box_frame(const float *p, float x, float y, float z) {
  float px = fabsf(x) - p[0], py = fabsf(y) - p[1], pz = fabsf(z) - p[2];
  float e = p[3];
  float qx = fabsf(px + e) - e, qy = fabsf(py + e) - e, qz = fabsf(pz + e) - e;
  return fminf(fminf(box3(px, qy, qz), box3(qx, py, qz)), box3(qx, qy, pz));
}

// p: rx ry rz (bound, exact on the surface)
static float ellipsoid(const float *p, float x, float y, float z) {
  float k0 = len3(x / p[0], y / p[1], z / p[2]);
  float k1 = len3(x / (p[0] * p[0]), y / (p[1] * p[1]), z / (p[2] * p[2]));
  if (k1 <= 0)
    return -fminf(p[0], fminf(p[1], p[2]));
  return k0 * (k0 - 1) / k1;
}

// Flat-ended cylinder between a and b. p: ax ay az bax bay baz r baba
static float capped_cylinder(const float *p, float x, float y, float z) {
  float pax = x - p[0], pay = y - p[1], paz = z - p[2];
  float bax = p[3], bay = p[4], baz = p[5], r = p[6], baba = p[7];
  float paba = pax * bax + pay * bay + paz * baz;
  float cx = len3(pax * baba - bax * paba, pay * baba - bay * paba,
                  paz * baba - baz * paba) -
             r * baba;
  float cy = fabsf(paba - baba * 0.5f) - baba * 0.5f;
  float x2 = cx * cx, y2 = cy * cy * baba;
  float d = fmaxf(cx, cy) < 0 ? -fminf(x2, y2)
                              : (cx > 0 ? x2 : 0) + (cy > 0 ? y2 : 0);
  return sgn(d) * sqrtf(fabsf(d)) / baba;
}

// Flat-ended cone between a (radius ra) and b (radius rb).
// p: ax ay az bax bay baz ra rb baba
static float capped_cone(const float *p, float x, float y, float z) {
  float pax = x - p[0], pay = y - p[1], paz = z - p[2];
  float bax = p[3], bay = p[4], baz = p[5];
  float ra = p[6], rb = p[7], baba = p[8];
  float rba = rb - ra;
  float papa = pax * pax + pay * pay + paz * paz;
  float paba = (pax * bax + pay * bay + paz * baz) / baba;
  float px = sqrtf(fmaxf(papa - paba * paba * baba, 0));
  float cax = fmaxf(0, px - (paba < 0.5f ? ra : rb));
  float cay = fabsf(paba - 0.5f) - 0.5f;
  float k = rba * rba + baba;
  float f = clamp01((rba * (px - ra) + paba * baba) / k);
  float cbx = px - ra - f * rba;
  float cby = paba - f;
  float s = (cbx < 0 && cay < 0) ? -1.0f : 1.0f;
  return s * sqrtf(fminf(cax * cax + cay * cay * baba,
                         cbx * cbx + cby * cby * baba));
}

// Cylinder along y with rounded rims. p: r h e
static float rounded_cylinder(const float *p, float x, float y, float z) {
  float e = p[2];
  return box2(len2(x, z) - p[0] + e, fabsf(y) - p[1] + e) - e;
}

// Arc of a torus lying in the xy plane, centred on +y and opening `angle`
// to each side. p: sin cos ra rb
static float capped_torus(const float *p, float x, float y, float z) {
  x = fabsf(x);
  float s = p[0], c = p[1], ra = p[2], rb = p[3];
  float k = c * x > s * y ? x * s + y * c : len2(x, y);
  return sqrtf(fmaxf(x * x + y * y + z * z + ra * ra - 2 * ra * k, 0)) - rb;
}

// Chain link: a ring in the xy plane stretched by le along y. p: le r1 r2
static float link(const float *p, float x, float y, float z) {
  float qy = fmaxf(fabsf(y) - p[0], 0);
  return len2(len2(x, qy) - p[1], z) - p[2];
}

// Hexagonal prism along z; h is the apothem (flat top and bottom).
// p: h depth
static float hex_prism(const float *p, float x, float y, float z) {
  const float kx = -0.8660254f, ky = 0.5f, kz = 0.57735027f;
  float h = p[0];
  x = fabsf(x);
  y = fabsf(y);
  z = fabsf(z);
  float dk = 2 * fminf(kx * x + ky * y, 0);
  x -= dk * kx;
  y -= dk * ky;
  float cx = clampf(x, -kz * h, kz * h);
  float dx = len2(x - cx, y - h) * sgn(y - h);
  return box2(dx, z - p[1]);
}

// Triangular prism along z, apex up. p: h depth (bound)
static float tri_prism(const float *p, float x, float y, float z) {
  float qx = fabsf(x), qz = fabsf(z);
  return fmaxf(qz - p[1], fmaxf(qx * 0.8660254f + y * 0.5f, -y) - p[0] * 0.5f);
}

// p: s (distance from the centre to each vertex)
static float octahedron(const float *p, float x, float y, float z) {
  float s = p[0];
  x = fabsf(x);
  y = fabsf(y);
  z = fabsf(z);
  float m = x + y + z - s;
  float qx, qy, qz;
  if (3 * x < m) {
    qx = x;
    qy = y;
    qz = z;
  } else if (3 * y < m) {
    qx = y;
    qy = z;
    qz = x;
  } else if (3 * z < m) {
    qx = z;
    qy = x;
    qz = y;
  } else {
    return m * 0.57735027f;
  }
  float k = clampf(0.5f * (qz - qy + s), 0, s);
  return len3(qx, qy - s + k, qz - k);
}

// Square pyramid: base of half-width w on y = 0, apex at y = h.
// p: w h
static float pyramid(const float *p, float x, float y, float z) {
  // iq's sdPyramid has a unit base; scale the query to it
  float sc = 2 * p[0];
  float h = p[1] / sc;
  x = fabsf(x / sc);
  y = y / sc;
  z = fabsf(z / sc);
  if (z > x) {
    float t = x;
    x = z;
    z = t;
  }
  x -= 0.5f;
  z -= 0.5f;
  float m2 = h * h + 0.25f;
  float qx = z, qy = h * y - 0.5f * x, qz = h * x + 0.5f * y;
  float s = fmaxf(-qx, 0);
  float t = clamp01((qy - 0.5f * z) / (m2 + 0.25f));
  float a = m2 * (qx + s) * (qx + s) + qy * qy;
  float b =
      m2 * (qx + 0.5f * t) * (qx + 0.5f * t) + (qy - m2 * t) * (qy - m2 * t);
  float d2 = fminf(qy, -qx * m2 - qy * 0.5f) > 0 ? 0 : fminf(a, b);
  return sqrtf((d2 + qz * qz) / m2) * sgn(fmaxf(qz, -y)) * sc;
}

// The part of a sphere of radius r above the plane y = h. p: r h w
static float cut_sphere(const float *p, float x, float y, float z) {
  float r = p[0], h = p[1], w = p[2];
  float qx = len2(x, z), qy = y;
  float s =
      fmaxf((h - r) * qx * qx + w * w * (h + r - 2 * qy), h * qx - w * qy);
  if (s < 0)
    return len2(qx, qy) - r;
  return qx < w ? h - qy : len2(qx - w, qy - h);
}

// Sphere sector of radius r around +y, opening `angle` from the axis.
// p: sin cos r
static float solid_angle(const float *p, float x, float y, float z) {
  float s = p[0], c = p[1], r = p[2];
  float qx = len2(x, z), qy = y;
  float l = len2(qx, qy) - r;
  float t = clampf(qx * s + qy * c, 0, r);
  float m = len2(qx - s * t, qy - c * t);
  return fmaxf(l, m * sgn(c * qx - s * qy));
}

// Rhombus with half-diagonals la (x) and lb (z), rounded by r, extruded by
// half-height h along y. p: la lb h r
static float rhombus(const float *p, float x, float y, float z) {
  float la = p[0], lb = p[1], h = p[2], r = p[3];
  x = fabsf(x);
  y = fabsf(y);
  z = fabsf(z);
  float f = clampf(
      (la * (la - 2 * x) - lb * (lb - 2 * z)) / (la * la + lb * lb), -1, 1);
  float dx = len2(x - 0.5f * la * (1 - f), z - 0.5f * lb * (1 + f)) *
                 sgn(x * lb + z * la - la * lb) -
             r;
  return box2(dx, y - h);
}

// Pointed lens (two circular arcs) between a and b, widest radius w at the
// middle, revolved around the a-b axis.
// p: cx cy cz vx vy vz (unit axis) half_length w d
static float vesica(const float *p, float x, float y, float z) {
  float px = x - p[0], py = y - p[1], pz = z - p[2];
  float vx = p[3], vy = p[4], vz = p[5];
  float r = p[6], w = p[7], d = p[8];
  float t = px * vx + py * vy + pz * vz;
  float qx = len3(px - t * vx, py - t * vy, pz - t * vz), qy = fabsf(t);
  if (r * qx < d * (qy - r))
    return len2(qx, qy - r);
  return len2(qx + d, qy) - (d + w);
}

// Value noise in [-1, 1] on an integer lattice, smoothly interpolated, and
// its fbm (octaves halve in amplitude and double in frequency). Pure
// arithmetic, so the same tree meshes the same on every platform.
static float lattice(int x, int y, int z, unsigned seed) {
  unsigned h = (unsigned)x * 0x8da6b343u ^ (unsigned)y * 0xd8163841u ^
               (unsigned)z * 0xcb1ab31fu ^ seed * 0x9e3779b9u;
  h ^= h >> 15;
  h *= 0x2c1b3c6du;
  h ^= h >> 12;
  h *= 0x297a2d39u;
  h ^= h >> 15;
  return (float)(h & 0xffffffu) / (float)0xffffffu * 2.0f - 1.0f;
}

static float smooth01(float t) { return t * t * (3 - 2 * t); }

static float value_noise(float x, float y, float z, unsigned seed) {
  float fx = floorf(x), fy = floorf(y), fz = floorf(z);
  int ix = (int)fx, iy = (int)fy, iz = (int)fz;
  float tx = smooth01(x - fx), ty = smooth01(y - fy), tz = smooth01(z - fz);
  float c[8];
  for (int k = 0; k < 8; ++k)
    c[k] =
        lattice(ix + (k & 1), iy + ((k >> 1) & 1), iz + ((k >> 2) & 1), seed);
  float x00 = c[0] + (c[1] - c[0]) * tx, x10 = c[2] + (c[3] - c[2]) * tx;
  float x01 = c[4] + (c[5] - c[4]) * tx, x11 = c[6] + (c[7] - c[6]) * tx;
  float y0 = x00 + (x10 - x00) * ty, y1 = x01 + (x11 - x01) * ty;
  return y0 + (y1 - y0) * tz;
}

// p: amp freq octaves seed
static float fbm(const float *p, float x, float y, float z) {
  float f = p[1], a = 1, sum = 0, norm = 0;
  unsigned seed = (unsigned)p[3];
  for (int o = 0; o < (int)p[2]; ++o) {
    sum += a * value_noise(x * f, y * f, z * f, seed + (unsigned)o);
    norm += a;
    a *= 0.5f;
    f *= 2;
  }
  return norm > 0 ? sum / norm : 0;
}

static float sdf_prim(const SdfNode *n, float x, float y, float z) {
  const float *p = n->p;
  switch (n->op) {
  case OP_SPHERE:
    return len3(x, y, z) - p[0];
  case OP_BOX:
    return box3(fabsf(x) - p[0], fabsf(y) - p[1], fabsf(z) - p[2]);
  case OP_CAPSULE: {
    float pax = x - p[0], pay = y - p[1], paz = z - p[2];
    float h = clamp01((pax * p[3] + pay * p[4] + paz * p[5]) * p[7]);
    return len3(pax - p[3] * h, pay - p[4] * h, paz - p[5] * h) - p[6];
  }
  case OP_TORUS:
    return len2(len2(x, z) - p[0], y) - p[1];
  case OP_ROUND_CONE:
    return round_cone(p, x, y, z);
  case OP_ROUND_BOX: {
    float e = p[3];
    return box3(fabsf(x) - p[0] + e, fabsf(y) - p[1] + e, fabsf(z) - p[2] + e) -
           e;
  }
  case OP_BOX_FRAME:
    return box_frame(p, x, y, z);
  case OP_ELLIPSOID:
    return ellipsoid(p, x, y, z);
  case OP_CAPPED_CYLINDER:
    return capped_cylinder(p, x, y, z);
  case OP_CAPPED_CONE:
    return capped_cone(p, x, y, z);
  case OP_ROUNDED_CYLINDER:
    return rounded_cylinder(p, x, y, z);
  case OP_CAPPED_TORUS:
    return capped_torus(p, x, y, z);
  case OP_LINK:
    return link(p, x, y, z);
  case OP_HEX_PRISM:
    return hex_prism(p, x, y, z);
  case OP_TRI_PRISM:
    return tri_prism(p, x, y, z);
  case OP_OCTAHEDRON:
    return octahedron(p, x, y, z);
  case OP_PYRAMID:
    return pyramid(p, x, y, z);
  case OP_CUT_SPHERE:
    return cut_sphere(p, x, y, z);
  case OP_SOLID_ANGLE:
    return solid_angle(p, x, y, z);
  case OP_RHOMBUS:
    return rhombus(p, x, y, z);
  case OP_VESICA:
    return vesica(p, x, y, z);
  }
  return 1e30f;
}

// Point transforms (one child). Rewrites the point into the child's space
// and multiplies *scale by the factor the child's distance must be scaled
// back with. Returns false for nodes that are not point transforms.
static bool sdf_xform(const SdfNode *n, float *x, float *y, float *z,
                      float *scale) {
  const float *p = n->p;
  switch (n->op) {
  case OP_MOVE:
    *x -= p[0];
    *y -= p[1];
    *z -= p[2];
    return true;
  case OP_ROTATE: { // forward R; apply R^T to take p into child space
    float nx = p[0] * *x + p[3] * *y + p[6] * *z;
    float ny = p[1] * *x + p[4] * *y + p[7] * *z;
    float nz = p[2] * *x + p[5] * *y + p[8] * *z;
    *x = nx;
    *y = ny;
    *z = nz;
    return true;
  }
  case OP_SCALE:
    *x /= p[0];
    *y /= p[0];
    *z /= p[0];
    *scale *= p[0];
    return true;
  case OP_MIRROR_X:
    *x = fabsf(*x);
    return true;
  case OP_MIRROR_Y:
    *y = fabsf(*y);
    return true;
  case OP_MIRROR_Z:
    *z = fabsf(*z);
    return true;
  case OP_ELONGATE:
    *x -= clampf(*x, -p[0], p[0]);
    *y -= clampf(*y, -p[1], p[1]);
    *z -= clampf(*z, -p[2], p[2]);
    return true;
  case OP_TWIST: { // about y
    float c = cosf(p[0] * *y), s = sinf(p[0] * *y);
    float nx = c * *x - s * *z, nz = s * *x + c * *z;
    *x = nx;
    *z = nz;
    return true;
  }
  case OP_BEND: { // bends the x axis toward +y
    float c = cosf(p[0] * *x), s = sinf(p[0] * *x);
    float nx = c * *x + s * *y, ny = -s * *x + c * *y;
    *x = nx;
    *y = ny;
    return true;
  }
  case OP_REPEAT: { // p: sx sy sz nx ny nz (s = 0 leaves that axis alone)
    float *v[3] = {x, y, z};
    for (int i = 0; i < 3; ++i) {
      float s = p[i];
      if (s > 0)
        *v[i] -= s * clampf(roundf(*v[i] / s), -p[3 + i], p[3 + i]);
    }
    return true;
  }
  case OP_POLAR_REPEAT: { // p: n copies around y, the first one on +x
    float step = 6.2831853f / p[0];
    float a = atan2f(*z, *x) + step * 0.5f;
    float r = len2(*x, *z);
    a = a - step * floorf(a / step) - step * 0.5f;
    *x = cosf(a) * r;
    *z = sinf(a) * r;
    return true;
  }
  case OP_REVOLVE: { // the child's z = 0 cross-section, spun about y
    *x = len2(*x, *z) - p[0];
    *z = 0;
    return true;
  }
  }
  return false;
}

// Combinators from the Mercury hg_sdf library (mercury.sexy/hg_sdf):
// chamfered and stepped joins, and grooves / tongues / pipes / engravings of
// b's surface into a. *side says whose surface the result lies on (0 = a,
// 1 = b) so the material follows it.
static float hg_op(const SdfNode *n, float a, float b, int *side) {
  const float *p = n->p;
  const float h = 0.70710678f;
  switch (n->op) {
  case OP_UNION_CHAMFER: {
    float m = fminf(a, b), c = (a - p[0] + b) * h;
    *side = b < a;
    return fminf(m, c);
  }
  case OP_INTERSECT_CHAMFER: {
    float m = fmaxf(a, b), c = (a + p[0] + b) * h;
    *side = b > a;
    return fmaxf(m, c);
  }
  case OP_SUBTRACT_CHAMFER: {
    float m = fmaxf(a, -b), c = (a + p[0] - b) * h;
    *side = -b > a;
    return fmaxf(m, c);
  }
  case OP_UNION_STAIRS: {
    float s = p[0] / p[1], u = b - p[0];
    float t = u - a + s, w = 2 * s;
    float st = 0.5f * (u + a + fabsf(t - w * floorf(t / w) - s));
    *side = b < a;
    return fminf(fminf(a, b), st);
  }
  case OP_GROOVE: {
    float cut = fminf(a + p[0], p[1] - fabsf(b));
    *side = cut > a;
    return fmaxf(a, cut);
  }
  case OP_TONGUE: {
    float t = fmaxf(a - p[0], fabsf(b) - p[1]);
    *side = t < a;
    return fminf(a, t);
  }
  case OP_PIPE:
    *side = fabsf(b) < fabsf(a);
    return len2(a, b) - p[0];
  case OP_ENGRAVE: {
    float e = (a + p[0] - fabsf(b)) * h;
    *side = e > a;
    return fmaxf(a, e);
  }
  }
  *side = 0;
  return a;
}

static float sdf_eval(const SdfNode *ns, int ni, float x, float y, float z) {
  const SdfNode *n = &ns[ni];
  float scale = 1;
  if (sdf_xform(n, &x, &y, &z, &scale))
    return sdf_eval(ns, n->a, x, y, z) * scale;
  switch (n->op) {
  case OP_ROUND:
    return sdf_eval(ns, n->a, x, y, z) - n->p[0];
  case OP_ONION:
    return fabsf(sdf_eval(ns, n->a, x, y, z)) - n->p[0];
  case OP_EXTRUDE:
    return box2(sdf_eval(ns, n->a, x, y, 0), fabsf(z) - n->p[0]);
  case OP_DISPLACE:
    return sdf_eval(ns, n->a, x, y, z) + n->p[0] * fbm(n->p, x, y, z);
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
  case OP_SMAX: {
    float a = sdf_eval(ns, n->a, x, y, z);
    float c = sdf_eval(ns, n->b, x, y, z);
    float k = n->p[0];
    float h = clamp01(0.5f - 0.5f * (c - a) / k);
    return c + (a - c) * h + k * h * (1 - h);
  }
  case OP_XOR: {
    float a = sdf_eval(ns, n->a, x, y, z);
    float c = sdf_eval(ns, n->b, x, y, z);
    return fmaxf(fminf(a, c), -fmaxf(a, c));
  }
  case OP_UNION_CHAMFER:
  case OP_INTERSECT_CHAMFER:
  case OP_SUBTRACT_CHAMFER:
  case OP_UNION_STAIRS:
  case OP_GROOVE:
  case OP_TONGUE:
  case OP_PIPE:
  case OP_ENGRAVE: {
    int side;
    return hg_op(n, sdf_eval(ns, n->a, x, y, z), sdf_eval(ns, n->b, x, y, z),
                 &side);
  }
  }
  return sdf_prim(n, x, y, z);
}

// Material evaluation: like sdf_eval but carries (distance, material) pairs.
// Only called once per output vertex (not per grid point), so the extra cost
// is negligible. paint sets the material for its subtree (innermost wins via
// `inherited`), union/intersect take the winner's material, smin/ssub/smax
// lerp materials with the same h as the distance so the material blend
// matches the geometric blend. Where a cutter wins (subtract/ssub) its own
// material shows — cut surfaces reveal what the cutter is "made of".
static float sdf_eval_mat(const SdfNode *ns, int ni, float x, float y, float z,
                          const float *inherited, float *out) {
  const SdfNode *n = &ns[ni];
  float scale = 1;
  if (sdf_xform(n, &x, &y, &z, &scale))
    return sdf_eval_mat(ns, n->a, x, y, z, inherited, out) * scale;
  switch (n->op) {
  case OP_ROUND:
    return sdf_eval_mat(ns, n->a, x, y, z, inherited, out) - n->p[0];
  case OP_ONION:
    return fabsf(sdf_eval_mat(ns, n->a, x, y, z, inherited, out)) - n->p[0];
  case OP_EXTRUDE:
    return box2(sdf_eval_mat(ns, n->a, x, y, 0, inherited, out),
                fabsf(z) - n->p[0]);
  case OP_DISPLACE:
    return sdf_eval_mat(ns, n->a, x, y, z, inherited, out) +
           n->p[0] * fbm(n->p, x, y, z);
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
  case OP_SMAX: {
    float mb[SDF_MAT_N];
    float a = sdf_eval_mat(ns, n->a, x, y, z, inherited, out);
    float c = sdf_eval_mat(ns, n->b, x, y, z, inherited, mb);
    float k = n->p[0];
    float h = clamp01(0.5f - 0.5f * (c - a) / k); // h = 1 -> a wins
    for (int i = 0; i < SDF_MAT_N; ++i)
      out[i] = mb[i] + (out[i] - mb[i]) * h;
    return c + (a - c) * h + k * h * (1 - h);
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
  case OP_XOR: { // the surface nearest the point shows
    float mb[SDF_MAT_N];
    float a = sdf_eval_mat(ns, n->a, x, y, z, inherited, out);
    float c = sdf_eval_mat(ns, n->b, x, y, z, inherited, mb);
    if (fabsf(c) < fabsf(a))
      memcpy(out, mb, sizeof(mb));
    return fmaxf(fminf(a, c), -fmaxf(a, c));
  }
  case OP_UNION_CHAMFER:
  case OP_INTERSECT_CHAMFER:
  case OP_SUBTRACT_CHAMFER:
  case OP_UNION_STAIRS:
  case OP_GROOVE:
  case OP_TONGUE:
  case OP_PIPE:
  case OP_ENGRAVE: {
    float mb[SDF_MAT_N];
    float a = sdf_eval_mat(ns, n->a, x, y, z, inherited, out);
    float c = sdf_eval_mat(ns, n->b, x, y, z, inherited, mb);
    int side;
    float d = hg_op(n, a, c, &side);
    if (side)
      memcpy(out, mb, sizeof(mb));
    return d;
  }
  }
  memcpy(out, inherited, SDF_MAT_N * sizeof(float));
  return sdf_prim(n, x, y, z);
}

// 部位 (bone subtree) までの距離。
// root からの xform 列を再現して点を部位の局所空間へ落としてから評価する (scale は距離も直す)。
static float sdf_eval_part(const SdfNode *ns, const SdfPart *part, float x,
                           float y, float z) {
  float scale = 1;
  for (int i = 0; i < part->path_len; ++i)
    sdf_xform(&ns[part->path[i]], &x, &y, &z, &scale);
  return sdf_eval(ns, part->node, x, y, z) * scale;
}

static void aabb_set(float mn[3], float mx[3], float x0, float x1, float y0,
                     float y1, float z0, float z1) {
  mn[0] = x0;
  mx[0] = x1;
  mn[1] = y0;
  mx[1] = y1;
  mn[2] = z0;
  mx[2] = z1;
}

// AABB of a segment a..a+ba swept by radii ra (at a) and rb (at b).
static void aabb_segment(const float *a, const float *ba, float ra, float rb,
                         float mn[3], float mx[3]) {
  for (int i = 0; i < 3; ++i) {
    float s = a[i], e = a[i] + ba[i];
    mn[i] = fminf(s - ra, e - rb);
    mx[i] = fmaxf(s + ra, e + rb);
  }
}

// Conservative AABB per node; exactness is not required (the grid adds a
// one-cell margin on top). smin/ssub bulge outward by at most k/4.
static void sdf_aabb(const SdfNode *ns, int ni, float mn[3], float mx[3]) {
  const SdfNode *n = &ns[ni];
  const float *p = n->p;
  switch (n->op) {
  case OP_SPHERE:
    aabb_set(mn, mx, -p[0], p[0], -p[0], p[0], -p[0], p[0]);
    return;
  case OP_BOX:
  case OP_ROUND_BOX:
  case OP_BOX_FRAME:
  case OP_ELLIPSOID:
    aabb_set(mn, mx, -p[0], p[0], -p[1], p[1], -p[2], p[2]);
    return;
  case OP_CAPSULE:
    aabb_segment(p, p + 3, p[6], p[6], mn, mx);
    return;
  case OP_CAPPED_CYLINDER: // the flat caps never reach past the radius
    aabb_segment(p, p + 3, p[6], p[6], mn, mx);
    return;
  case OP_ROUND_CONE:
  case OP_CAPPED_CONE:
    aabb_segment(p, p + 3, p[6], p[7], mn, mx);
    return;
  case OP_TORUS: {
    float r = p[0] + p[1];
    aabb_set(mn, mx, -r, r, -p[1], p[1], -r, r);
    return;
  }
  case OP_ROUNDED_CYLINDER:
    aabb_set(mn, mx, -p[0], p[0], -p[1], p[1], -p[0], p[0]);
    return;
  case OP_CAPPED_TORUS: {
    float r = p[2] + p[3];
    aabb_set(mn, mx, -r, r, -r, r, -p[3], p[3]);
    return;
  }
  case OP_LINK: {
    float r = p[1] + p[2];
    aabb_set(mn, mx, -r, r, -p[0] - r, p[0] + r, -p[2], p[2]);
    return;
  }
  case OP_HEX_PRISM: {
    float r = p[0] * 1.1547005f; // circumradius
    aabb_set(mn, mx, -r, r, -p[0], p[0], -p[1], p[1]);
    return;
  }
  case OP_TRI_PRISM: {
    float w = p[0] * 0.57735027f, top = p[0];
    aabb_set(mn, mx, -2 * w, 2 * w, -p[0] * 0.5f, top, -p[1], p[1]);
    return;
  }
  case OP_OCTAHEDRON:
    aabb_set(mn, mx, -p[0], p[0], -p[0], p[0], -p[0], p[0]);
    return;
  case OP_PYRAMID:
    aabb_set(mn, mx, -p[0], p[0], 0, p[1], -p[0], p[0]);
    return;
  case OP_CUT_SPHERE:
    aabb_set(mn, mx, -p[0], p[0], p[1], p[0], -p[0], p[0]);
    return;
  case OP_SOLID_ANGLE:
    aabb_set(mn, mx, -p[2], p[2], -p[2], p[2], -p[2], p[2]);
    return;
  case OP_RHOMBUS:
    aabb_set(mn, mx, -p[0] - p[3], p[0] + p[3], -p[2], p[2], -p[1] - p[3],
             p[1] + p[3]);
    return;
  case OP_VESICA: {
    float e[3], ba[3];
    for (int i = 0; i < 3; ++i) {
      e[i] = p[i] - p[3 + i] * p[6];
      ba[i] = 2 * p[3 + i] * p[6];
    }
    // the widest point is w; with w > half-length the arcs bulge past it,
    // so use the larger of the two
    float r = fmaxf(p[7], p[6]);
    aabb_segment(e, ba, r, r, mn, mx);
    return;
  }
  case OP_MOVE:
    sdf_aabb(ns, n->a, mn, mx);
    for (int i = 0; i < 3; ++i) {
      mn[i] += p[i];
      mx[i] += p[i];
    }
    return;
  case OP_PAINT:
  case OP_BONE:
    sdf_aabb(ns, n->a, mn, mx);
    return;
  case OP_ROUND:
  case OP_ONION: {
    sdf_aabb(ns, n->a, mn, mx);
    float g = fmaxf(p[0], 0);
    for (int i = 0; i < 3; ++i) {
      mn[i] -= g;
      mx[i] += g;
    }
    return;
  }
  case OP_ROTATE: {
    float cmn[3], cmx[3];
    sdf_aabb(ns, n->a, cmn, cmx);
    for (int i = 0; i < 3; ++i) {
      mn[i] = 1e30f;
      mx[i] = -1e30f;
    }
    for (int c = 0; c < 8; ++c) {
      float v[3] = {c & 1 ? cmx[0] : cmn[0], c & 2 ? cmx[1] : cmn[1],
                    c & 4 ? cmx[2] : cmn[2]};
      for (int i = 0; i < 3; ++i) {
        float w = p[i * 3] * v[0] + p[i * 3 + 1] * v[1] + p[i * 3 + 2] * v[2];
        mn[i] = fminf(mn[i], w);
        mx[i] = fmaxf(mx[i], w);
      }
    }
    return;
  }
  case OP_SCALE:
    sdf_aabb(ns, n->a, mn, mx);
    for (int i = 0; i < 3; ++i) {
      mn[i] *= p[0];
      mx[i] *= p[0];
    }
    return;
  case OP_MIRROR_X:
  case OP_MIRROR_Y:
  case OP_MIRROR_Z: {
    sdf_aabb(ns, n->a, mn, mx);
    int i = n->op - OP_MIRROR_X;
    float m = fmaxf(fabsf(mn[i]), fabsf(mx[i]));
    mn[i] = -m;
    mx[i] = m;
    return;
  }
  case OP_ELONGATE:
    sdf_aabb(ns, n->a, mn, mx);
    for (int i = 0; i < 3; ++i) {
      mn[i] -= p[i];
      mx[i] += p[i];
    }
    return;
  case OP_TWIST: { // any xz direction within the child's xz reach
    sdf_aabb(ns, n->a, mn, mx);
    float r = len2(fmaxf(fabsf(mn[0]), fabsf(mx[0])),
                   fmaxf(fabsf(mn[2]), fabsf(mx[2])));
    mn[0] = mn[2] = -r;
    mx[0] = mx[2] = r;
    return;
  }
  case OP_BEND: { // any xy direction within the child's xy reach
    sdf_aabb(ns, n->a, mn, mx);
    float r = len2(fmaxf(fabsf(mn[0]), fabsf(mx[0])),
                   fmaxf(fabsf(mn[1]), fabsf(mx[1])));
    mn[0] = mn[1] = -r;
    mx[0] = mx[1] = r;
    return;
  }
  case OP_POLAR_REPEAT: { // any direction around y within the child's reach
    sdf_aabb(ns, n->a, mn, mx);
    float r = len2(fmaxf(fabsf(mn[0]), fabsf(mx[0])),
                   fmaxf(fabsf(mn[2]), fabsf(mx[2])));
    mn[0] = mn[2] = -r;
    mx[0] = mx[2] = r;
    return;
  }
  case OP_REVOLVE: { // the child's x range becomes a radius around y
    sdf_aabb(ns, n->a, mn, mx);
    float r = fmaxf(fmaxf(p[0] + mx[0], -(p[0] + mn[0])), 0);
    mn[0] = mn[2] = -r;
    mx[0] = mx[2] = r;
    return;
  }
  case OP_EXTRUDE:
    sdf_aabb(ns, n->a, mn, mx);
    mn[2] = -p[0];
    mx[2] = p[0];
    return;
  case OP_DISPLACE:
    sdf_aabb(ns, n->a, mn, mx);
    for (int i = 0; i < 3; ++i) {
      mn[i] -= fabsf(p[0]);
      mx[i] += fabsf(p[0]);
    }
    return;
  case OP_REPEAT:
    sdf_aabb(ns, n->a, mn, mx);
    for (int i = 0; i < 3; ++i) {
      if (p[i] > 0) {
        mn[i] -= p[i] * p[3 + i];
        mx[i] += p[i] * p[3 + i];
      }
    }
    return;
  case OP_UNION:
  case OP_SMIN:
  case OP_XOR:
  case OP_UNION_CHAMFER:
  case OP_UNION_STAIRS: {
    float bmn[3], bmx[3];
    sdf_aabb(ns, n->a, mn, mx);
    sdf_aabb(ns, n->b, bmn, bmx);
    // chamfers and stairs fill the corner up to r from both shapes
    float k = 0;
    if (n->op == OP_SMIN)
      k = p[0] * 0.25f;
    else if (n->op == OP_UNION_CHAMFER || n->op == OP_UNION_STAIRS)
      k = p[0];
    for (int i = 0; i < 3; ++i) {
      mn[i] = fminf(mn[i], bmn[i]) - k;
      mx[i] = fmaxf(mx[i], bmx[i]) + k;
    }
    return;
  }
  case OP_SUBTRACT_CHAMFER:
  case OP_GROOVE:
  case OP_ENGRAVE: // these only take material away from a
    sdf_aabb(ns, n->a, mn, mx);
    return;
  case OP_TONGUE: // grows a by at most ra
    sdf_aabb(ns, n->a, mn, mx);
    for (int i = 0; i < 3; ++i) {
      mn[i] -= p[0];
      mx[i] += p[0];
    }
    return;
  case OP_PIPE: { // within r of both surfaces
    float bmn[3], bmx[3];
    sdf_aabb(ns, n->a, mn, mx);
    sdf_aabb(ns, n->b, bmn, bmx);
    for (int i = 0; i < 3; ++i) {
      mn[i] = fmaxf(mn[i], bmn[i]) - p[0];
      mx[i] = fmaxf(fminf(mx[i], bmx[i]) + p[0], mn[i]);
    }
    return;
  }
  case OP_SUBTRACT:
  case OP_SSUB: {
    sdf_aabb(ns, n->a, mn, mx);
    float k = n->op == OP_SSUB ? p[0] * 0.25f : 0;
    for (int i = 0; i < 3; ++i) {
      mn[i] -= k;
      mx[i] += k;
    }
    return;
  }
  case OP_INTERSECT:
  case OP_INTERSECT_CHAMFER:
  case OP_SMAX: { // these only shrink, so the intersection still bounds them
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
// convert (LubSdfNodeDesc -> SdfNode, bone parts)

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
  static const char *const names[] = {"sphere",
                                      "box",
                                      "capsule",
                                      "torus",
                                      "round_cone",
                                      "round_box",
                                      "box_frame",
                                      "ellipsoid",
                                      "capped_cylinder",
                                      "capped_cone",
                                      "rounded_cylinder",
                                      "capped_torus",
                                      "link",
                                      "hex_prism",
                                      "tri_prism",
                                      "octahedron",
                                      "pyramid",
                                      "cut_sphere",
                                      "solid_angle",
                                      "rhombus",
                                      "vesica",
                                      "move",
                                      "rotate",
                                      "scale",
                                      "mirror_x",
                                      "mirror_y",
                                      "mirror_z",
                                      "elongate",
                                      "twist",
                                      "bend",
                                      "repeat",
                                      "polar_repeat",
                                      "revolve",
                                      "extrude",
                                      "displace",
                                      "round",
                                      "onion",
                                      "paint",
                                      "bone",
                                      "union",
                                      "smin",
                                      "subtract",
                                      "ssub",
                                      "intersect",
                                      "smax",
                                      "xor",
                                      "union_chamfer",
                                      "intersect_chamfer",
                                      "subtract_chamfer",
                                      "union_stairs",
                                      "groove",
                                      "tongue",
                                      "pipe",
                                      "engrave"};
  return op >= 0 && op < (int)(sizeof(names) / sizeof(names[0])) ? names[op]
                                                                 : "?";
}

// The hg_sdf joins that take a single r.
static int hg_r_op(int op) {
  switch (op) {
  case LUB_MESH_SDF_OP_UNION_CHAMFER:
    return OP_UNION_CHAMFER;
  case LUB_MESH_SDF_OP_INTERSECT_CHAMFER:
    return OP_INTERSECT_CHAMFER;
  case LUB_MESH_SDF_OP_SUBTRACT_CHAMFER:
    return OP_SUBTRACT_CHAMFER;
  case LUB_MESH_SDF_OP_PIPE:
    return OP_PIPE;
  default:
    return OP_ENGRAVE;
  }
}

// Copies the first `count` public params unchanged.
static void copy_params(SdfNode *n, const float *q, int count) {
  for (int k = 0; k < count; ++k)
    n->p[k] = q[k];
}

// in[i] を t->nodes[i] に変換する。
// 子は再帰 (index はそのまま)。
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
  case LUB_MESH_SDF_OP_ROUND_CONE: {
    n->op = OP_ROUND_CONE;
    n->p[0] = q[0];
    n->p[1] = q[1];
    n->p[2] = q[2];
    n->p[3] = q[3] - q[0];
    n->p[4] = q[4] - q[1];
    n->p[5] = q[5] - q[2];
    n->p[6] = q[6];
    n->p[7] = q[7];
    if (n->p[6] < 0 || n->p[7] < 0)
      return conv_fail(c, "sdf_mesh: round_cone radii must be >= 0%s", "");
    float baba = n->p[3] * n->p[3] + n->p[4] * n->p[4] + n->p[5] * n->p[5];
    float rr = n->p[6] - n->p[7];
    n->p[8] = baba;
    // <= 0: one sphere contains the other (or a == b), eval uses the spheres
    n->p[9] = baba - rr * rr;
    break;
  }
  case LUB_MESH_SDF_OP_ROUND_BOX:
  case LUB_MESH_SDF_OP_BOX_FRAME:
    n->op = in->op == LUB_MESH_SDF_OP_ROUND_BOX ? OP_ROUND_BOX : OP_BOX_FRAME;
    copy_params(n, q, 4);
    if (q[3] < 0 || q[3] > fminf(q[0], fminf(q[1], q[2])))
      return conv_fail(c, "sdf_mesh: '%s' needs 0 <= e <= the half extents",
                       op_name(n->op));
    break;
  case LUB_MESH_SDF_OP_ELLIPSOID:
    n->op = OP_ELLIPSOID;
    copy_params(n, q, 3);
    if (q[0] <= 0 || q[1] <= 0 || q[2] <= 0)
      return conv_fail(c, "sdf_mesh: ellipsoid radii must be > 0%s", "");
    break;
  case LUB_MESH_SDF_OP_CAPPED_CYLINDER:
  case LUB_MESH_SDF_OP_CAPPED_CONE: {
    bool cone = in->op == LUB_MESH_SDF_OP_CAPPED_CONE;
    n->op = cone ? OP_CAPPED_CONE : OP_CAPPED_CYLINDER;
    copy_params(n, q, 3);
    for (int k = 0; k < 3; ++k)
      n->p[3 + k] = q[3 + k] - q[k];
    float baba = n->p[3] * n->p[3] + n->p[4] * n->p[4] + n->p[5] * n->p[5];
    if (!(baba > 0))
      return conv_fail(c, "sdf_mesh: '%s' needs a != b", op_name(n->op));
    n->p[6] = q[6];
    if (cone) {
      n->p[7] = q[7];
      n->p[8] = baba;
    } else {
      n->p[7] = baba;
    }
    if (q[6] < 0 || (cone && q[7] < 0))
      return conv_fail(c, "sdf_mesh: '%s' radii must be >= 0", op_name(n->op));
    break;
  }
  case LUB_MESH_SDF_OP_ROUNDED_CYLINDER:
    n->op = OP_ROUNDED_CYLINDER;
    copy_params(n, q, 3);
    if (q[2] < 0 || q[2] > fminf(q[0], q[1]))
      return conv_fail(c, "sdf_mesh: rounded_cylinder needs 0 <= e <= r, h%s",
                       "");
    break;
  case LUB_MESH_SDF_OP_CAPPED_TORUS:
  case LUB_MESH_SDF_OP_SOLID_ANGLE: {
    bool torus = in->op == LUB_MESH_SDF_OP_CAPPED_TORUS;
    n->op = torus ? OP_CAPPED_TORUS : OP_SOLID_ANGLE;
    n->p[0] = sinf(q[0]);
    n->p[1] = cosf(q[0]);
    n->p[2] = q[1];
    if (torus)
      n->p[3] = q[2];
    break;
  }
  case LUB_MESH_SDF_OP_LINK:
    n->op = OP_LINK;
    copy_params(n, q, 3);
    break;
  case LUB_MESH_SDF_OP_HEX_PRISM:
  case LUB_MESH_SDF_OP_TRI_PRISM:
    n->op = in->op == LUB_MESH_SDF_OP_HEX_PRISM ? OP_HEX_PRISM : OP_TRI_PRISM;
    copy_params(n, q, 2);
    break;
  case LUB_MESH_SDF_OP_OCTAHEDRON:
    n->op = OP_OCTAHEDRON;
    n->p[0] = q[0];
    break;
  case LUB_MESH_SDF_OP_PYRAMID:
    n->op = OP_PYRAMID;
    copy_params(n, q, 2);
    if (q[0] <= 0 || q[1] <= 0)
      return conv_fail(c, "sdf_mesh: pyramid needs w > 0 and h > 0%s", "");
    break;
  case LUB_MESH_SDF_OP_CUT_SPHERE:
    n->op = OP_CUT_SPHERE;
    copy_params(n, q, 2);
    if (!(fabsf(q[1]) < q[0]))
      return conv_fail(c, "sdf_mesh: cut_sphere needs -r < h < r%s", "");
    n->p[2] = sqrtf(q[0] * q[0] - q[1] * q[1]);
    break;
  case LUB_MESH_SDF_OP_RHOMBUS:
    n->op = OP_RHOMBUS;
    copy_params(n, q, 4);
    if (q[0] <= 0 || q[1] <= 0)
      return conv_fail(c, "sdf_mesh: rhombus needs la > 0 and lb > 0%s", "");
    break;
  case LUB_MESH_SDF_OP_VESICA: {
    n->op = OP_VESICA;
    float vx = q[3] - q[0], vy = q[4] - q[1], vz = q[5] - q[2];
    float l = sqrtf(vx * vx + vy * vy + vz * vz);
    float w = q[6];
    if (!(l > 0) || !(w > 0))
      return conv_fail(c, "sdf_mesh: vesica needs a != b and w > 0%s", "");
    n->p[0] = (q[0] + q[3]) * 0.5f;
    n->p[1] = (q[1] + q[4]) * 0.5f;
    n->p[2] = (q[2] + q[5]) * 0.5f;
    n->p[3] = vx / l;
    n->p[4] = vy / l;
    n->p[5] = vz / l;
    float r = 0.5f * l;
    n->p[6] = r;
    n->p[7] = w;
    n->p[8] = 0.5f * (r * r - w * w) / w;
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
  case LUB_MESH_SDF_OP_MIRROR_Y:
    n->op = OP_MIRROR_Y;
    break;
  case LUB_MESH_SDF_OP_MIRROR_Z:
    n->op = OP_MIRROR_Z;
    break;
  case LUB_MESH_SDF_OP_ELONGATE:
    n->op = OP_ELONGATE;
    copy_params(n, q, 3);
    if (q[0] < 0 || q[1] < 0 || q[2] < 0)
      return conv_fail(c, "sdf_mesh: elongate needs h >= 0%s", "");
    break;
  case LUB_MESH_SDF_OP_TWIST:
  case LUB_MESH_SDF_OP_BEND:
    n->op = in->op == LUB_MESH_SDF_OP_TWIST ? OP_TWIST : OP_BEND;
    n->p[0] = q[0];
    break;
  case LUB_MESH_SDF_OP_REPEAT:
    n->op = OP_REPEAT;
    for (int k = 0; k < 3; ++k) {
      if (q[k] < 0 || q[3 + k] < 0)
        return conv_fail(c, "sdf_mesh: repeat needs s >= 0 and n >= 0%s", "");
      n->p[k] = q[k];
      n->p[3 + k] = floorf(q[3 + k] + 0.5f);
    }
    break;
  case LUB_MESH_SDF_OP_POLAR_REPEAT:
    n->op = OP_POLAR_REPEAT;
    n->p[0] = floorf(q[0] + 0.5f);
    if (n->p[0] < 1)
      return conv_fail(c, "sdf_mesh: polar_repeat needs n >= 1%s", "");
    break;
  case LUB_MESH_SDF_OP_REVOLVE:
    n->op = OP_REVOLVE;
    n->p[0] = q[0];
    break;
  case LUB_MESH_SDF_OP_EXTRUDE:
    n->op = OP_EXTRUDE;
    n->p[0] = q[0];
    if (!(q[0] > 0))
      return conv_fail(c, "sdf_mesh: extrude needs h > 0%s", "");
    break;
  case LUB_MESH_SDF_OP_DISPLACE:
    n->op = OP_DISPLACE;
    copy_params(n, q, 4);
    n->p[2] = floorf(q[2] + 0.5f);
    n->p[3] = floorf(q[3] + 0.5f);
    if (!(q[1] > 0) || n->p[2] < 1 || n->p[2] > 8 || n->p[3] < 0)
      return conv_fail(
          c, "sdf_mesh: displace needs freq > 0, octaves 1..8, seed >= 0%s",
          "");
    break;
  case LUB_MESH_SDF_OP_ROUND:
    n->op = OP_ROUND;
    n->p[0] = q[0];
    break;
  case LUB_MESH_SDF_OP_ONION:
    n->op = OP_ONION;
    n->p[0] = q[0];
    if (!(q[0] > 0))
      return conv_fail(c, "sdf_mesh: onion needs t > 0%s", "");
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
  case LUB_MESH_SDF_OP_SMAX:
    n->op = in->op == LUB_MESH_SDF_OP_SMIN
                ? OP_SMIN
                : (in->op == LUB_MESH_SDF_OP_SSUB ? OP_SSUB : OP_SMAX);
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
  case LUB_MESH_SDF_OP_XOR:
    n->op = OP_XOR;
    break;
  case LUB_MESH_SDF_OP_UNION_CHAMFER:
  case LUB_MESH_SDF_OP_INTERSECT_CHAMFER:
  case LUB_MESH_SDF_OP_SUBTRACT_CHAMFER:
  case LUB_MESH_SDF_OP_PIPE:
  case LUB_MESH_SDF_OP_ENGRAVE:
    n->op = hg_r_op(in->op);
    n->p[0] = q[0];
    if (!(q[0] > 0))
      return conv_fail(c, "sdf_mesh: node '%s' needs r > 0", op_name(n->op));
    break;
  case LUB_MESH_SDF_OP_UNION_STAIRS:
    n->op = OP_UNION_STAIRS;
    n->p[0] = q[0];
    n->p[1] = floorf(q[1] + 0.5f);
    if (!(q[0] > 0) || n->p[1] < 1)
      return conv_fail(c, "sdf_mesh: union_stairs needs r > 0 and n >= 1%s",
                       "");
    break;
  case LUB_MESH_SDF_OP_GROOVE:
  case LUB_MESH_SDF_OP_TONGUE:
    n->op = in->op == LUB_MESH_SDF_OP_GROOVE ? OP_GROOVE : OP_TONGUE;
    copy_params(n, q, 2);
    if (!(q[0] > 0) || !(q[1] > 0))
      return conv_fail(c, "sdf_mesh: node '%s' needs ra > 0 and rb > 0",
                       op_name(n->op));
    break;
  default:
    return conv_fail(c, "sdf_mesh: unknown op%s", "");
  }

  int kind = n->op;
  if (kind >= OP_MOVE && kind < OP_UNION) {
    // point transforms go on the bone path; round/onion/paint/bone do not
    bool is_xform = kind <= OP_REVOLVE;
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
  // w0 = softmax(-d/k) の 2 部位版 = 1 / (1 + e^((d0-d1)/k))。
  // k が blend 幅。
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
