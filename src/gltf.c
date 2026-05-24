// gltf.c — glTF 2.0 mesh loader. Reads mesh[0].primitives[0] only.
// Triangle primitive required. POSITION required; NORMAL / TEXCOORD_0 / indices
// optional. Other attributes (TANGENT / COLOR_0 / JOINTS / WEIGHTS / TEXCOORD_1+)
// are ignored with a one-time warning.

#define CGLTF_IMPLEMENTATION
#include "cgltf.h"

#include "gltf.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <lua.h>
#include <lauxlib.h>

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
    cgltf_mesh *mesh = &data->meshes[0];
    if (mesh->primitives_count == 0) {
        fprintf(stderr, "load_gltf: mesh[0] has no primitives in %s\n", path);
        cgltf_free(data);
        lua_pushnil(L);
        return 1;
    }
    cgltf_primitive *prim = &mesh->primitives[0];
    if (prim->type != cgltf_primitive_type_triangles) {
        fprintf(stderr, "load_gltf: mesh[0].primitives[0] is not triangles (%d) in %s\n",
                (int)prim->type, path);
        cgltf_free(data);
        lua_pushnil(L);
        return 1;
    }

    // Locate attribute accessors.
    cgltf_accessor *acc_pos = NULL;
    cgltf_accessor *acc_nrm = NULL;
    cgltf_accessor *acc_uv  = NULL;
    bool warned_unknown = false;
    for (cgltf_size i = 0; i < prim->attributes_count; ++i) {
        cgltf_attribute *a = &prim->attributes[i];
        switch (a->type) {
            case cgltf_attribute_type_position:
                acc_pos = a->data; break;
            case cgltf_attribute_type_normal:
                acc_nrm = a->data; break;
            case cgltf_attribute_type_texcoord:
                if (a->index == 0) acc_uv = a->data;
                break;
            default:
                if (!warned_unknown) {
                    fprintf(stderr, "load_gltf: ignoring unsupported attribute(s) in %s\n", path);
                    warned_unknown = true;
                }
                break;
        }
    }
    if (!acc_pos) {
        fprintf(stderr, "load_gltf: POSITION attribute missing in %s\n", path);
        cgltf_free(data);
        lua_pushnil(L);
        return 1;
    }

    size_t vert_count = acc_pos->count;
    cgltf_accessor *acc_idx = prim->indices; // may be NULL for non-indexed

    // Build result table.
    lua_createtable(L, 0, 6);

    // positions: vec3 * vert_count
    {
        size_t n = vert_count * 3;
        float *buf = (float*)malloc(n * sizeof(float));
        if (!buf) {
            cgltf_free(data);
            return luaL_error(L, "load_gltf: out of memory");
        }
        cgltf_accessor_unpack_floats(acc_pos, buf, n);
        lua_createtable(L, (int)n, 0);
        for (size_t i = 0; i < n; ++i) {
            lua_pushnumber(L, buf[i]);
            lua_rawseti(L, -2, (int)(i + 1));
        }
        free(buf);
        lua_setfield(L, -2, "positions");
    }

    if (acc_nrm) {
        size_t n = vert_count * 3;
        float *buf = (float*)malloc(n * sizeof(float));
        if (!buf) {
            cgltf_free(data);
            return luaL_error(L, "load_gltf: out of memory");
        }
        cgltf_accessor_unpack_floats(acc_nrm, buf, n);
        lua_createtable(L, (int)n, 0);
        for (size_t i = 0; i < n; ++i) {
            lua_pushnumber(L, buf[i]);
            lua_rawseti(L, -2, (int)(i + 1));
        }
        free(buf);
        lua_setfield(L, -2, "normals");
    }

    if (acc_uv) {
        size_t n = vert_count * 2;
        float *buf = (float*)malloc(n * sizeof(float));
        if (!buf) {
            cgltf_free(data);
            return luaL_error(L, "load_gltf: out of memory");
        }
        cgltf_accessor_unpack_floats(acc_uv, buf, n);
        lua_createtable(L, (int)n, 0);
        for (size_t i = 0; i < n; ++i) {
            lua_pushnumber(L, buf[i]);
            lua_rawseti(L, -2, (int)(i + 1));
        }
        free(buf);
        lua_setfield(L, -2, "uvs");
    }

    size_t index_count = 0;
    if (acc_idx) {
        index_count = acc_idx->count;
        lua_createtable(L, (int)index_count, 0);
        for (size_t i = 0; i < index_count; ++i) {
            cgltf_size idx = cgltf_accessor_read_index(acc_idx, i);
            lua_pushinteger(L, (lua_Integer)idx);
            lua_rawseti(L, -2, (int)(i + 1));
        }
        lua_setfield(L, -2, "indices");
    }

    lua_pushinteger(L, (lua_Integer)vert_count);
    lua_setfield(L, -2, "vert_count");
    lua_pushinteger(L, (lua_Integer)index_count);
    lua_setfield(L, -2, "index_count");

    cgltf_free(data);
    return 1;
}
