#pragma once
// glTF 2.0 mesh loader (cgltf).
// Does not touch the VM; reads into a C structure of flat arrays.
// src/api_io.c builds the C API views (LubGltfMesh / LubGltfPrimitive /
// LubGltfMaterial) from this structure. Triangle primitives required. POSITION
// required; NORMAL / TEXCOORD_0 / TANGENT / indices optional.
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
  char *base_color_path;         // base_dir + uri. NULL = none
  char *metallic_roughness_path; // Same as above
  char *normal_path;             // Same as above
  char *name;                    // NULL = none
} GltfMaterial;

typedef struct GltfPrimitive {
  float *positions;  // vec3 * vert_count
  float *normals;    // vec3 * vert_count. NULL = none
  float *uvs;        // vec2 * vert_count. NULL = none
  float *tangents;   // vec4 * vert_count. NULL = none
  uint32_t *indices; // index_count. NULL = non-indexed
  int vert_count;
  int index_count;
  int material_index; // -1 = none
} GltfPrimitive;

typedef struct GltfMesh {
  GltfPrimitive *primitives;
  int primitive_count;
  GltfMaterial *materials;
  int material_count;
} GltfMesh;

// Whether the uri (percent-decoded) of an external buffer may be read.
typedef bool (*GltfUriCheck)(const char *uri);

// Read a file.
// Material image paths and paths in err are written based on path, the name the
// caller uses. If uri_ok is given, pass it the uri before reading an external
// buffer, and fail if it returns false. On failure, return NULL and write the
// reason to err.
GltfMesh *gltf_load(const char *file, const char *path, GltfUriCheck uri_ok,
                    char *err, size_t err_size);
void gltf_free(GltfMesh *mesh);
