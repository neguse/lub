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

#include <lauxlib.h>
#include <lua.h>

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

static void push_joined_uri(lua_State *L, const char *base_dir,
                            const char *uri) {
  if (!uri_is_external_file(uri)) {
    lua_pushnil(L);
    return;
  }
  lua_pushfstring(L, "%s%s", base_dir ? base_dir : "", uri);
}

static void push_float_array(lua_State *L, const cgltf_float *v, int n) {
  lua_createtable(L, n, 0);
  for (int i = 0; i < n; ++i) {
    lua_pushnumber(L, v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static bool push_accessor_float_table(lua_State *L, cgltf_accessor *acc,
                                      size_t components) {
  size_t n = acc->count * components;
  float *buf = (float *)malloc(n * sizeof(float));
  if (!buf)
    return false;
  cgltf_accessor_unpack_floats(acc, buf, n);
  lua_createtable(L, (int)n, 0);
  for (size_t i = 0; i < n; ++i) {
    lua_pushnumber(L, buf[i]);
    lua_rawseti(L, -2, (int)(i + 1));
  }
  free(buf);
  return true;
}

static bool push_index_table(lua_State *L, cgltf_accessor *acc_idx) {
  cgltf_size index_count = acc_idx->count;
  lua_createtable(L, (int)index_count, 0);
  for (cgltf_size i = 0; i < index_count; ++i) {
    cgltf_size idx = cgltf_accessor_read_index(acc_idx, i);
    lua_pushinteger(L, (lua_Integer)idx);
    lua_rawseti(L, -2, (int)(i + 1));
  }
  return true;
}

static void set_texture_path(lua_State *L, const char *field,
                             const char *base_dir,
                             const cgltf_texture_view *view) {
  const char *uri = NULL;
  if (view && view->texture && view->texture->image)
    uri = view->texture->image->uri;
  push_joined_uri(L, base_dir, uri);
  lua_setfield(L, -2, field);
}

static void push_material_table(lua_State *L, const cgltf_material *mat,
                                const char *base_dir) {
  static const cgltf_float default_color[4] = {1, 1, 1, 1};
  lua_createtable(L, 0, 11);

  const cgltf_pbr_metallic_roughness *pbr =
      mat ? &mat->pbr_metallic_roughness : NULL;
  push_float_array(L, pbr ? pbr->base_color_factor : default_color, 4);
  lua_setfield(L, -2, "base_color_factor");

  lua_pushnumber(L, pbr ? pbr->metallic_factor : 1.0f);
  lua_setfield(L, -2, "metallic_factor");
  lua_pushnumber(L, pbr ? pbr->roughness_factor : 1.0f);
  lua_setfield(L, -2, "roughness_factor");

  int alpha_mode = 0;
  if (mat && mat->alpha_mode == cgltf_alpha_mode_mask)
    alpha_mode = 1;
  else if (mat && mat->alpha_mode == cgltf_alpha_mode_blend)
    alpha_mode = 2;
  lua_pushinteger(L, alpha_mode);
  lua_setfield(L, -2, "alpha_mode");
  lua_pushnumber(L, mat ? mat->alpha_cutoff : 0.5f);
  lua_setfield(L, -2, "alpha_cutoff");
  lua_pushboolean(L, mat ? mat->double_sided : 0);
  lua_setfield(L, -2, "double_sided");
  lua_pushnumber(L, mat ? mat->normal_texture.scale : 1.0f);
  lua_setfield(L, -2, "normal_scale");

  set_texture_path(L, "base_color_path", base_dir,
                   pbr ? &pbr->base_color_texture : NULL);
  set_texture_path(L, "metallic_roughness_path", base_dir,
                   pbr ? &pbr->metallic_roughness_texture : NULL);
  set_texture_path(L, "normal_path", base_dir,
                   mat ? &mat->normal_texture : NULL);

  if (mat && mat->name) {
    lua_pushstring(L, mat->name);
    lua_setfield(L, -2, "name");
  }
}

static bool push_primitive_table(lua_State *L, cgltf_primitive *prim,
                                 cgltf_material *materials,
                                 cgltf_size materials_count,
                                 const char *base_dir, const char *path,
                                 bool *warned_unknown) {
  if (prim->type != cgltf_primitive_type_triangles) {
    fprintf(stderr, "load_gltf: non-triangle primitive (%d) in %s\n",
            (int)prim->type, path);
    return false;
  }

  cgltf_accessor *acc_pos = NULL;
  cgltf_accessor *acc_nrm = NULL;
  cgltf_accessor *acc_uv = NULL;
  cgltf_accessor *acc_tan = NULL;
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
    fprintf(stderr, "load_gltf: POSITION attribute missing in %s\n", path);
    return false;
  }

  lua_createtable(L, 0, 10);

  if (!push_accessor_float_table(L, acc_pos, 3))
    return false;
  lua_setfield(L, -2, "positions");

  if (acc_nrm) {
    if (!push_accessor_float_table(L, acc_nrm, 3))
      return false;
    lua_setfield(L, -2, "normals");
  }

  if (acc_uv) {
    if (!push_accessor_float_table(L, acc_uv, 2))
      return false;
    lua_setfield(L, -2, "uvs");
  }

  if (acc_tan) {
    if (!push_accessor_float_table(L, acc_tan, 4))
      return false;
    lua_setfield(L, -2, "tangents");
  }

  size_t index_count = 0;
  if (prim->indices) {
    index_count = prim->indices->count;
    if (!push_index_table(L, prim->indices))
      return false;
    lua_setfield(L, -2, "indices");
  }

  lua_pushinteger(L, (lua_Integer)acc_pos->count);
  lua_setfield(L, -2, "vert_count");
  lua_pushinteger(L, (lua_Integer)index_count);
  lua_setfield(L, -2, "index_count");

  int material_index = -1;
  if (prim->material && materials && prim->material >= materials &&
      prim->material < materials + materials_count) {
    material_index = (int)(prim->material - materials);
  }
  lua_pushinteger(L, material_index);
  lua_setfield(L, -2, "material_index");
  push_material_table(L, prim->material, base_dir);
  lua_setfield(L, -2, "material");

  return true;
}

static void copy_field(lua_State *L, int src_idx, int dst_idx,
                       const char *field) {
  lua_getfield(L, src_idx, field);
  lua_setfield(L, dst_idx, field);
}

int lub_load_gltf(lua_State *L) {
  const char *path = luaL_checkstring(L, 1);
  cgltf_options options = {0};
  cgltf_data *data = NULL;

  cgltf_result r = cgltf_parse_file(&options, path, &data);
  if (r != cgltf_result_success) {
    fprintf(stderr, "load_gltf: parse failed (%d): %s\n", (int)r, path);
    lua_pushnil(L);
    return 1;
  }

  r = cgltf_load_buffers(&options, data, path);
  if (r != cgltf_result_success) {
    fprintf(stderr, "load_gltf: load_buffers failed (%d): %s\n", (int)r, path);
    cgltf_free(data);
    lua_pushnil(L);
    return 1;
  }

  if (data->meshes_count == 0) {
    fprintf(stderr, "load_gltf: no meshes in %s\n", path);
    cgltf_free(data);
    lua_pushnil(L);
    return 1;
  }

  cgltf_size primitive_count = 0;
  for (cgltf_size mi = 0; mi < data->meshes_count; ++mi)
    primitive_count += data->meshes[mi].primitives_count;
  if (primitive_count == 0) {
    fprintf(stderr, "load_gltf: no primitives in %s\n", path);
    cgltf_free(data);
    lua_pushnil(L);
    return 1;
  }

  char base_dir[4096];
  path_dirname(path, base_dir, sizeof(base_dir));

  lua_createtable(L, 0, 8);
  int result_idx = lua_gettop(L);

  lua_createtable(L, (int)primitive_count, 0);
  bool warned_unknown = false;
  int out_prim = 1;
  for (cgltf_size mi = 0; mi < data->meshes_count; ++mi) {
    cgltf_mesh *mesh = &data->meshes[mi];
    for (cgltf_size pi = 0; pi < mesh->primitives_count; ++pi) {
      if (!push_primitive_table(L, &mesh->primitives[pi], data->materials,
                                data->materials_count, base_dir, path,
                                &warned_unknown)) {
        cgltf_free(data);
        return luaL_error(L, "load_gltf: failed to load primitive in %s", path);
      }
      lua_rawseti(L, -2, out_prim++);
    }
  }
  lua_setfield(L, result_idx, "primitives");

  lua_pushinteger(L, (lua_Integer)primitive_count);
  lua_setfield(L, result_idx, "primitive_count");

  lua_getfield(L, result_idx, "primitives");
  lua_rawgeti(L, -1, 1);
  int first_idx = lua_gettop(L);
  copy_field(L, first_idx, result_idx, "positions");
  copy_field(L, first_idx, result_idx, "normals");
  copy_field(L, first_idx, result_idx, "uvs");
  copy_field(L, first_idx, result_idx, "tangents");
  copy_field(L, first_idx, result_idx, "indices");
  copy_field(L, first_idx, result_idx, "vert_count");
  copy_field(L, first_idx, result_idx, "index_count");
  copy_field(L, first_idx, result_idx, "material");
  lua_pop(L, 2);

  cgltf_free(data);
  return 1;
}
