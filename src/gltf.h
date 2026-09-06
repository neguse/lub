#pragma once
// glTF 2.0 mesh loader (cgltf)。Lua には触らず、平らな配列の C 構造に読む。
// Lua 面の table (positions / normals / ... / primitives / material) は
// src/lua_api.c がこの構造から組み立てる。Triangle primitives required.
// POSITION required; NORMAL / TEXCOORD_0 / TANGENT / indices optional。
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct GltfMaterial {
  float base_color_factor[4];
  float metallic_factor;
  float roughness_factor;
  int alpha_mode; // 0 = opaque, 1 = mask, 2 = blend
  float alpha_cutoff;
  bool double_sided;
  float normal_scale;
  char *base_color_path;         // base_dir + uri。NULL = 無し
  char *metallic_roughness_path; // 同上
  char *normal_path;             // 同上
  char *name;                    // NULL = 無し
} GltfMaterial;

typedef struct GltfPrimitive {
  float *positions;  // vec3 * vert_count
  float *normals;    // vec3 * vert_count。NULL = 無し
  float *uvs;        // vec2 * vert_count。NULL = 無し
  float *tangents;   // vec4 * vert_count。NULL = 無し
  uint32_t *indices; // index_count。NULL = non-indexed
  int vert_count;
  int index_count;
  int material_index; // -1 = 無し
} GltfPrimitive;

typedef struct GltfMesh {
  GltfPrimitive *primitives;
  int primitive_count;
  GltfMaterial *materials;
  int material_count;
} GltfMesh;

// 失敗は NULL を返し err に理由を書く。
GltfMesh *gltf_load(const char *path, char *err, size_t err_size);
void gltf_free(GltfMesh *mesh);
