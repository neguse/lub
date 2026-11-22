#pragma once
// SDF tree -> triangle mesh.
// The tree is received through the C API (LubSdfNodeDesc array in
// include/lub/lub_api.h), converted here into SdfNode for evaluation, and
// turned into a mesh with surface nets. Does not touch the VM (the tcs VM host
// functions are in src/gen/vm_api_gen.c).
//
// n = cell count of the longest axis; bounds are derived from a conservative
// per-node AABB with a one-cell margin, cells stay cubic. If there are bone
// nodes, also bakes the per-vertex joints / weights of the two nearest parts
// (softmax, blend width skin_k) and bones (name + pivot).
#include "lub/lub_api.h"
#include "surfacenets.h"
#include <stdbool.h>
#include <stddef.h>

enum {
  // primitives
  OP_SPHERE,
  OP_BOX,
  OP_CAPSULE,
  OP_TORUS,
  OP_ROUND_CONE,
  OP_ROUND_BOX,
  OP_BOX_FRAME,
  OP_ELLIPSOID,
  OP_CAPPED_CYLINDER,
  OP_CAPPED_CONE,
  OP_ROUNDED_CYLINDER,
  OP_CAPPED_TORUS,
  OP_LINK,
  OP_HEX_PRISM,
  OP_TRI_PRISM,
  OP_OCTAHEDRON,
  OP_PYRAMID,
  OP_CUT_SPHERE,
  OP_SOLID_ANGLE,
  OP_RHOMBUS,
  OP_VESICA,
  // one child: point transforms (OP_MOVE .. OP_REVOLVE), then distance and
  // material modifiers
  OP_MOVE,
  OP_ROTATE,
  OP_SCALE,
  OP_MIRROR_X,
  OP_MIRROR_Y,
  OP_MIRROR_Z,
  OP_ELONGATE,
  OP_TWIST,
  OP_BEND,
  OP_REPEAT,
  OP_POLAR_REPEAT,
  OP_REVOLVE,
  OP_EXTRUDE,
  OP_DISPLACE,
  OP_ROUND,
  OP_ONION,
  OP_PAINT,
  OP_BONE,
  // two children
  OP_UNION,
  OP_SMIN,
  OP_SUBTRACT,
  OP_SSUB,
  OP_INTERSECT,
  OP_SMAX,
  OP_XOR,
  OP_UNION_CHAMFER,
  OP_INTERSECT_CHAMFER,
  OP_SUBTRACT_CHAMFER,
  OP_UNION_STAIRS,
  OP_GROOVE,
  OP_TONGUE,
  OP_PIPE,
  OP_ENGRAVE,
};

// per-vertex material: albedo rgb + metallic + roughness
#define SDF_MAT_N 5
// skinning parts (bone nodes) per tree
#define SDF_MAX_PARTS 16
#define SDF_MAX_DEPTH 64
#define SDF_MAX_NODES 4096

typedef struct SdfNode {
  int op;
  int a, b; // flat child indices, -1 = none; xform/mirror child in a
  // params: the public ones, some precomputed (see conv_node in sdf.c);
  // rotate: forward rotation matrix, row-major (eval applies the transpose)
  float p[10];
} SdfNode;

// Skinning part declared by a bone node.
// path is the sequence of flat indices of the xform/mirror nodes passed from
// the root to that bone, used to reproduce the same point transform when
// measuring the distance of a vertex to the part.
typedef struct SdfPart {
  int node; // Flat index of OP_BONE (distance evaluation goes from here to the
            // children)
  char name[32];
  float pivot[3];
  int path[SDF_MAX_DEPTH];
  int path_len;
} SdfPart;

typedef struct SdfTree {
  SdfNode *nodes; // malloc, freed by sdf_tree_free
  int len;
  int root;
  SdfPart parts[SDF_MAX_PARTS];
  int part_count;
} SdfTree;

typedef struct SdfMeshOut {
  SnMesh mesh;
  float *colors;      // 3 * vert_count
  float *metal_rough; // 2 * vert_count
  float *joints;      // 2 * vert_count (when there are parts)
  float *weights;     // 2 * vert_count
  float mn[3], mx[3];
  float cell;
} SdfMeshOut;

// Converts the public form of the tree into the evaluation form (validation,
// precision calculation, bone paths). On failure, returns false with the reason
// in err.
bool sdf_tree_convert(const LubSdfNodeDesc *nodes, int count, int root,
                      SdfTree *out, char *err, size_t err_size);
void sdf_tree_free(SdfTree *t);

// Turns the tree into a mesh.
// On failure, returns false with the reason in err.
bool sdf_mesh_build(const SdfTree *t, int n, float skin_k, SdfMeshOut *out,
                    char *err, size_t err_size);
void sdf_mesh_out_free(SdfMeshOut *o);
