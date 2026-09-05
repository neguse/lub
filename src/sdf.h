#pragma once
// SDF tree -> triangle mesh (design: docs/log/2026-07-04-sdf-tree-design.md)。
// 木は C API (include/lub/lub_api.h の LubSdfNodeDesc 配列) で受け、ここで
// 評価用の SdfNode に変換して surface nets で mesh にする。Lua には触らない
// (Lua binding は src/lua_api.c)。
//
// n = cell count of the longest axis; bounds are derived from a conservative
// per-node AABB with a one-cell margin, cells stay cubic. bone ノードがあれば
// 頂点ごとに最寄り 2 部位の joints / weights (softmax、blend 幅 skin_k) と
// bones (name + pivot) も焼く。
#include "lub/lub_api.h"
#include "surfacenets.h"
#include <stdbool.h>
#include <stddef.h>

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

typedef struct SdfNode {
  int op;
  int a, b; // flat child indices, -1 = none; xform/mirror child in a
  // params. capsule: ax ay az bax bay baz r inv_baba;
  // rotate: forward rotation matrix, row-major (eval applies the transpose)
  float p[10];
} SdfNode;

// bone ノードが宣言する skinning 部位。path は root からその bone までに
// 通過した xform/mirror ノードの flat index 列で、頂点の部位距離を測るとき
// 同じ点変換を再現するのに使う。
typedef struct SdfPart {
  int node; // OP_BONE の flat index (距離評価はここから子へ)
  char name[32];
  float pivot[3];
  int path[SDF_MAX_DEPTH];
  int path_len;
} SdfPart;

typedef struct SdfTree {
  SdfNode *nodes; // malloc、sdf_tree_free で解放
  int len;
  int root;
  SdfPart parts[SDF_MAX_PARTS];
  int part_count;
} SdfTree;

typedef struct SdfMeshOut {
  SnMesh mesh;
  float *colors;      // 3 * vert_count
  float *metal_rough; // 2 * vert_count
  float *joints;      // 2 * vert_count (part があるとき)
  float *weights;     // 2 * vert_count
  float mn[3], mx[3];
  float cell;
} SdfMeshOut;

// 公開形の木を評価用に変換する (検証・精度計算・bone path)。失敗は false
// で err に理由。
bool sdf_tree_convert(const LubSdfNodeDesc *nodes, int count, int root,
                      SdfTree *out, char *err, size_t err_size);
void sdf_tree_free(SdfTree *t);

// 木を mesh にする。失敗は false で err に理由。
bool sdf_mesh_build(const SdfTree *t, int n, float skin_k, SdfMeshOut *out,
                    char *err, size_t err_size);
void sdf_mesh_out_free(SdfMeshOut *o);
