// gltf.c — glTF 2.0 mesh loader. Triangle primitives required. POSITION
// required; NORMAL / TEXCOORD_0 / TANGENT / indices optional. Other attributes
// (COLOR_0 / JOINTS / WEIGHTS / TEXCOORD_1+) are ignored with a one-time
// warning.

#define CGLTF_IMPLEMENTATION
#include "cgltf.h"

#include "gltf.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void path_dirname(const char *path, char *out, size_t out_size) {
  if (out_size == 0)
    return;
  out[0] = '\0';
  const char *slash = strrchr(path, '/');
  const char *backslash = strrchr(path, '\\');
  const char *sep = slash > backslash ? slash : backslash;
  if (!sep)
    return;
  size_t n = (size_t)(sep - path + 1);
  if (n >= out_size)
    n = out_size - 1;
  memcpy(out, path, n);
  out[n] = '\0';
}

static bool uri_is_external_file(const char *uri) {
  if (!uri || !uri[0])
    return false;
  return strncmp(uri, "data:", 5) != 0 && strstr(uri, "://") == NULL;
}

static char *joined_uri(const char *base_dir, const char *uri) {
  if (!uri_is_external_file(uri))
    return NULL;
  size_t bl = base_dir ? strlen(base_dir) : 0;
  size_t ul = strlen(uri);
  char *s = (char *)malloc(bl + ul + 1);
  if (!s)
    return NULL;
  if (bl)
    memcpy(s, base_dir, bl);
  memcpy(s + bl, uri, ul + 1);
  return s;
}

static float *unpack_floats(cgltf_accessor *acc, size_t components) {
  size_t n = acc->count * components;
  float *buf = (float *)malloc((n ? n : 1) * sizeof(float));
  if (!buf)
    return NULL;
  cgltf_accessor_unpack_floats(acc, buf, n);
  return buf;
}

static const char *texture_uri(const cgltf_texture_view *view) {
  if (view && view->texture && view->texture->image)
    return view->texture->image->uri;
  return NULL;
}

static void load_material(GltfMaterial *out, const cgltf_material *mat,
                          const char *base_dir) {
  static const cgltf_float default_color[4] = {1, 1, 1, 1};
  const cgltf_pbr_metallic_roughness *pbr =
      mat ? &mat->pbr_metallic_roughness : NULL;
  memcpy(out->base_color_factor, pbr ? pbr->base_color_factor : default_color,
         sizeof(out->base_color_factor));
  out->metallic_factor = pbr ? pbr->metallic_factor : 1.0f;
  out->roughness_factor = pbr ? pbr->roughness_factor : 1.0f;
  out->alpha_mode = 0;
  if (mat && mat->alpha_mode == cgltf_alpha_mode_mask)
    out->alpha_mode = 1;
  else if (mat && mat->alpha_mode == cgltf_alpha_mode_blend)
    out->alpha_mode = 2;
  out->alpha_cutoff = mat ? mat->alpha_cutoff : 0.5f;
  out->double_sided = mat ? mat->double_sided != 0 : false;
  out->normal_scale = mat ? mat->normal_texture.scale : 1.0f;
  out->base_color_path =
      joined_uri(base_dir, texture_uri(pbr ? &pbr->base_color_texture : NULL));
  out->metallic_roughness_path = joined_uri(
      base_dir, texture_uri(pbr ? &pbr->metallic_roughness_texture : NULL));
  out->normal_path =
      joined_uri(base_dir, texture_uri(mat ? &mat->normal_texture : NULL));
  out->name = (mat && mat->name) ? strdup(mat->name) : NULL;
}

static bool load_primitive(GltfPrimitive *out, cgltf_primitive *prim,
                           cgltf_material *materials,
                           cgltf_size materials_count, const char *path,
                           bool *warned_unknown, char *err, size_t err_size) {
  if (prim->type != cgltf_primitive_type_triangles) {
    snprintf(err, err_size, "load_gltf: non-triangle primitive (%d) in %s",
             (int)prim->type, path);
    return false;
  }
  cgltf_accessor *acc_pos = NULL, *acc_nrm = NULL, *acc_uv = NULL,
                 *acc_tan = NULL;
  for (cgltf_size i = 0; i < prim->attributes_count; ++i) {
    cgltf_attribute *a = &prim->attributes[i];
    switch (a->type) {
    case cgltf_attribute_type_position:
      acc_pos = a->data;
      break;
    case cgltf_attribute_type_normal:
      acc_nrm = a->data;
      break;
    case cgltf_attribute_type_texcoord:
      if (a->index == 0)
        acc_uv = a->data;
      break;
    case cgltf_attribute_type_tangent:
      acc_tan = a->data;
      break;
    default:
      if (!*warned_unknown) {
        fprintf(stderr, "load_gltf: ignoring unsupported attribute(s) in %s\n",
                path);
        *warned_unknown = true;
      }
      break;
    }
  }
  if (!acc_pos) {
    snprintf(err, err_size, "load_gltf: POSITION attribute missing in %s",
             path);
    return false;
  }
  memset(out, 0, sizeof(*out));
  out->positions = unpack_floats(acc_pos, 3);
  if (!out->positions) {
    snprintf(err, err_size, "load_gltf: out of memory");
    return false;
  }
  out->vert_count = (int)acc_pos->count;
  if (acc_nrm)
    out->normals = unpack_floats(acc_nrm, 3);
  if (acc_uv)
    out->uvs = unpack_floats(acc_uv, 2);
  if (acc_tan)
    out->tangents = unpack_floats(acc_tan, 4);
  if (prim->indices) {
    cgltf_size n = prim->indices->count;
    out->indices = (uint32_t *)malloc((n ? n : 1) * sizeof(uint32_t));
    if (!out->indices) {
      snprintf(err, err_size, "load_gltf: out of memory");
      return false;
    }
    for (cgltf_size i = 0; i < n; ++i)
      out->indices[i] = (uint32_t)cgltf_accessor_read_index(prim->indices, i);
    out->index_count = (int)n;
  }
  out->material_index = -1;
  if (prim->material && materials && prim->material >= materials &&
      prim->material < materials + materials_count)
    out->material_index = (int)(prim->material - materials);
  return true;
}

static void free_primitive(GltfPrimitive *p) {
  free(p->positions);
  free(p->normals);
  free(p->uvs);
  free(p->tangents);
  free(p->indices);
}

void gltf_free(GltfMesh *mesh) {
  if (!mesh)
    return;
  for (int i = 0; i < mesh->primitive_count; ++i)
    free_primitive(&mesh->primitives[i]);
  free(mesh->primitives);
  for (int i = 0; i < mesh->material_count; ++i) {
    free(mesh->materials[i].base_color_path);
    free(mesh->materials[i].metallic_roughness_path);
    free(mesh->materials[i].normal_path);
    free(mesh->materials[i].name);
  }
  free(mesh->materials);
  free(mesh);
}

GltfMesh *gltf_load(const char *path, char *err, size_t err_size) {
  cgltf_options options = {0};
  cgltf_data *data = NULL;
  cgltf_result r = cgltf_parse_file(&options, path, &data);
  if (r != cgltf_result_success) {
    snprintf(err, err_size, "load_gltf: parse failed (%d): %s", (int)r, path);
    return NULL;
  }
  r = cgltf_load_buffers(&options, data, path);
  if (r != cgltf_result_success) {
    snprintf(err, err_size, "load_gltf: load_buffers failed (%d): %s", (int)r,
             path);
    cgltf_free(data);
    return NULL;
  }
  cgltf_size primitive_count = 0;
  for (cgltf_size mi = 0; mi < data->meshes_count; ++mi)
    primitive_count += data->meshes[mi].primitives_count;
  if (data->meshes_count == 0 || primitive_count == 0) {
    snprintf(err, err_size, "load_gltf: no primitives in %s", path);
    cgltf_free(data);
    return NULL;
  }

  char base_dir[4096];
  path_dirname(path, base_dir, sizeof(base_dir));

  GltfMesh *mesh = (GltfMesh *)calloc(1, sizeof(GltfMesh));
  if (!mesh) {
    snprintf(err, err_size, "load_gltf: out of memory");
    cgltf_free(data);
    return NULL;
  }
  mesh->primitives =
      (GltfPrimitive *)calloc(primitive_count, sizeof(GltfPrimitive));
  mesh->materials = (GltfMaterial *)calloc(
      data->materials_count ? data->materials_count : 1, sizeof(GltfMaterial));
  if (!mesh->primitives || !mesh->materials) {
    snprintf(err, err_size, "load_gltf: out of memory");
    gltf_free(mesh);
    cgltf_free(data);
    return NULL;
  }
  bool warned_unknown = false;
  for (cgltf_size mi = 0; mi < data->meshes_count; ++mi) {
    cgltf_mesh *m = &data->meshes[mi];
    for (cgltf_size pi = 0; pi < m->primitives_count; ++pi) {
      if (!load_primitive(&mesh->primitives[mesh->primitive_count],
                          &m->primitives[pi], data->materials,
                          data->materials_count, path, &warned_unknown, err,
                          err_size)) {
        mesh->primitive_count++;
        gltf_free(mesh);
        cgltf_free(data);
        return NULL;
      }
      mesh->primitive_count++;
    }
  }
  for (cgltf_size i = 0; i < data->materials_count; ++i)
    load_material(&mesh->materials[i], &data->materials[i], base_dir);
  mesh->material_count = (int)data->materials_count;
  cgltf_free(data);
  return mesh;
}
