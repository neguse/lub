#include "lua_api.h"
#include "enums_lua.h"
#include "app.h"
#include "backend.h"
#include "pass.h"
#include "resources.h"
#include "shader.h"
#include "pipeline.h"
#include "enums.h"
#include "capture.h"
#include <lua.h>
#include <lualib.h>
#include <lauxlib.h>
#include <SDL3/SDL.h>
#include <string.h>
#include <stdlib.h>

static App *g_app_for_lua = NULL;

// Helper: read a vec4 (rgba) from table at idx field name `key`. Defaults
// for missing fields go to `defaults[]`. Caller pops nothing — the helper
// pushes/pops internally and returns with stack unchanged.
static void desc_get_float4(lua_State *L, int idx, const char *key,
                            float out[4], const float defaults[4]) {
    out[0] = defaults[0]; out[1] = defaults[1];
    out[2] = defaults[2]; out[3] = defaults[3];
    lua_getfield(L, idx, key);
    if (lua_istable(L, -1)) {
        for (int i = 0; i < 4; ++i) {
            lua_rawgeti(L, -1, i + 1);
            if (lua_isnumber(L, -1)) out[i] = (float)lua_tonumber(L, -1);
            lua_pop(L, 1);
        }
    }
    lua_pop(L, 1);
}

// Helper: check if value at stack index is a sentinel table with __sgl_kind == kind
static int is_sentinel(lua_State *L, int idx, const char *kind) {
    if (!lua_istable(L, idx)) return 0;
    lua_getfield(L, idx, "__sgl_kind");
    int ok = lua_isstring(L, -1) && strcmp(lua_tostring(L, -1), kind) == 0;
    lua_pop(L, 1);
    return ok;
}

// Helper: push a BufferRef sentinel table { __sgl_kind = "buffer", key = key }
static void push_buffer_ref(lua_State *L, const char *key) {
    lua_newtable(L);
    lua_pushstring(L, "buffer"); lua_setfield(L, -2, "__sgl_kind");
    lua_pushstring(L, key);      lua_setfield(L, -2, "key");
}

// Helper: push a ShaderRef sentinel table { __sgl_kind = "shader", key = key }
static void push_shader_ref(lua_State *L, const char *key) {
    lua_newtable(L);
    lua_pushstring(L, "shader"); lua_setfield(L, -2, "__sgl_kind");
    lua_pushstring(L, key);      lua_setfield(L, -2, "key");
}

// Helper: push a TextureRef sentinel table { __sgl_kind = "texture", key = key }
static void push_texture_ref(lua_State *L, const char *key) {
    lua_newtable(L);
    lua_pushstring(L, "texture"); lua_setfield(L, -2, "__sgl_kind");
    lua_pushstring(L, key);       lua_setfield(L, -2, "key");
}

static int l_begin_pass(lua_State *L) {
    luaL_checktype(L, 1, LUA_TTABLE);
    lua_getfield(L, 1, "target");
    int is_main = is_sentinel(L, -1, "main_tex");
    lua_pop(L, 1);
    if (!is_main) return luaL_error(L, "begin_pass: target must be main_tex");

    static const float defaults[4] = {0, 0, 0, 1};
    float c[4];
    desc_get_float4(L, 1, "clear_color", c, defaults);

    pass_state_begin_main(&g_app_for_lua->pass, c[0], c[1], c[2], c[3]);
    return 0;
}

static int l_use_buffer(lua_State *L) {
    const char *key = luaL_checkstring(L, 1);
    int type = (int)luaL_checkinteger(L, 2);
    luaL_checktype(L, 3, LUA_TTABLE);
    int version = (int)luaL_checkinteger(L, 4);

    if (type != SGL_BUFFER_VERTEX && type != SGL_BUFFER_INDEX) {
        return luaL_error(L, "use_buffer: only VERTEX/INDEX supported in PoC");
    }

    ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_BUFFER);
    if (!e) return luaL_error(L, "use_buffer: key '%s' already used as different kind", key);

    res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

    if (e->version == version && e->u.buf.h != 0) {
        // Skip upload — return existing BufferRef
        push_buffer_ref(L, key);
        return 1;
    }

    // Read Lua table of numbers into a float buffer
    int n = (int)lua_rawlen(L, 3);
    if (n <= 0) return luaL_error(L, "use_buffer: empty data");
    float *data = (float*)malloc((size_t)n * sizeof(float));
    if (!data) return luaL_error(L, "use_buffer: out of memory");
    for (int i = 0; i < n; ++i) {
        lua_rawgeti(L, 3, i + 1);
        data[i] = (float)lua_tonumber(L, -1);
        lua_pop(L, 1);
    }

    if (e->u.buf.h != 0) g_backend->destroy_buffer(e->u.buf.h);
    e->u.buf.h = g_backend->make_buffer((SglBufferType)type, data, (size_t)n * sizeof(float));
    e->u.buf.type = (SglBufferType)type;
    e->u.buf.size_bytes = (size_t)n * sizeof(float);
    e->version = version;
    free(data);

    push_buffer_ref(L, key);
    return 1;
}

static int l_use_texture(lua_State *L) {
    const char *key = luaL_checkstring(L, 1);
    int w = (int)luaL_checkinteger(L, 2);
    int h = (int)luaL_checkinteger(L, 3);
    int fmt = (int)luaL_checkinteger(L, 4);
    int has_data = !lua_isnoneornil(L, 5);
    if (has_data) luaL_checktype(L, 5, LUA_TTABLE);
    int version = (int)luaL_checkinteger(L, 6);

    if (w <= 0 || h <= 0) return luaL_error(L, "use_texture: invalid size %dx%d", w, h);

    ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_TEXTURE);
    if (!e) return luaL_error(L, "use_texture: key '%s' already used as different kind", key);
    res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

    if (e->version == version && e->u.tex.h != 0) {
        push_texture_ref(L, key);
        return 1;
    }

    int bpp;
    switch (fmt) {
        case SGL_PF_RGBA8: bpp = 4; break;
        case SGL_PF_R8:    bpp = 1; break;
        default: return luaL_error(L, "use_texture: format not supported in PoC (only RGBA8/R8)");
    }

    uint8_t *pixels = NULL;
    if (has_data) {
        int n = (int)lua_rawlen(L, 5);
        if (n != w * h * bpp) {
            return luaL_error(L, "use_texture: data size mismatch: got %d, expected %d", n, w * h * bpp);
        }
        pixels = (uint8_t*)malloc((size_t)n);
        if (!pixels) return luaL_error(L, "use_texture: out of memory");
        for (int i = 0; i < n; ++i) {
            lua_rawgeti(L, 5, i + 1);
            int v = (int)lua_tointeger(L, -1);
            if (v < 0) v = 0; else if (v > 255) v = 255;
            pixels[i] = (uint8_t)v;
            lua_pop(L, 1);
        }
    }

    if (e->u.tex.h != 0) g_backend->destroy_image(e->u.tex.h);
    e->u.tex.h = 0;

    ImageDesc d = {
        .fmt = (SglPixelFormat)fmt,
        .w = w, .h = h,
        .data = pixels,
        .data_bytes = pixels ? (size_t)w * (size_t)h * (size_t)bpp : 0,
    };
    e->u.tex.h = g_backend->make_image(&d);
    e->u.tex.w   = w;
    e->u.tex.h_  = h;
    e->u.tex.fmt = (SglPixelFormat)fmt;
    e->version   = version;

    if (pixels) free(pixels);

    push_texture_ref(L, key);
    return 1;
}

static int l_use_shader(lua_State *L) {
    const char *key = luaL_checkstring(L, 1);
    const char *vs  = luaL_checkstring(L, 2);
    const char *fs  = luaL_checkstring(L, 3);
    int version = (int)luaL_checkinteger(L, 4);

    ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_SHADER);
    if (!e) return luaL_error(L, "use_shader: key '%s' already used as different kind", key);
    res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

    if (e->version == version && e->u.sh.h != 0) {
        push_shader_ref(L, key);
        return 1;
    }

    char err[1024];
    ShaderBlob vsb = {0}, fsb = {0};
    ShaderReflection refl;
    if (!shader_compile(vs, fs, &vsb, &fsb, &refl, err, sizeof(err))) {
        shader_blob_free(&vsb);
        shader_blob_free(&fsb);
        return luaL_error(L, "shader compile error: %s", err);
    }

    ShaderDesc sd = {
        .vs_spirv = vsb.spirv, .vs_bytes = vsb.bytes,
        .fs_spirv = fsb.spirv, .fs_bytes = fsb.bytes,
        .refl = &refl,
    };
    if (e->u.sh.h != 0) g_backend->destroy_shader(e->u.sh.h);
    e->u.sh.h = g_backend->make_shader(&sd);
    e->u.sh.refl = refl;
    e->version = version;

    shader_blob_free(&vsb);
    shader_blob_free(&fsb);

    push_shader_ref(L, key);
    return 1;
}

static int l_draw(lua_State *L) {
    if (!pass_state_in_pass(&g_app_for_lua->pass)) {
        return luaL_error(L, "draw: must be called inside begin_pass/end_pass");
    }
    int count = (int)luaL_checkinteger(L, 1);
    luaL_checktype(L, 2, LUA_TTABLE); // resources
    luaL_checktype(L, 3, LUA_TTABLE); // options

    // options.shader is required and must be a ShaderRef
    lua_getfield(L, 3, "shader");
    if (!is_sentinel(L, -1, "shader")) {
        lua_pop(L, 1);
        return luaL_error(L, "draw: options.shader required (ShaderRef)");
    }
    lua_getfield(L, -1, "key");
    const char *shader_key = lua_tostring(L, -1);
    char shader_key_buf[128];
    if (shader_key) {
        strncpy(shader_key_buf, shader_key, sizeof(shader_key_buf) - 1);
        shader_key_buf[sizeof(shader_key_buf) - 1] = '\0';
    } else {
        shader_key_buf[0] = '\0';
    }
    lua_pop(L, 2); // pop "key" string and the shader ref

    ResEntry *sh_e = res_table_get(&g_app_for_lua->res, shader_key_buf);
    if (!sh_e || sh_e->kind != RES_SHADER) {
        return luaL_error(L, "draw: shader not found: %s", shader_key_buf);
    }

    // pipeline state options (with defaults)
    int blend = SGL_BLEND_NONE;
    int cull  = SGL_CULL_BACK;
    int prim  = SGL_PRIM_TRIANGLES;
    bool depth_test = true;
    bool depth_write = true;

    lua_getfield(L, 3, "blend");
    if (lua_isinteger(L, -1)) blend = (int)lua_tointeger(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 3, "cull");
    if (lua_isinteger(L, -1)) cull = (int)lua_tointeger(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 3, "primitive");
    if (lua_isinteger(L, -1)) prim = (int)lua_tointeger(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 3, "depth");
    if (!lua_isnoneornil(L, -1)) depth_test = lua_toboolean(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 3, "depth_write");
    if (!lua_isnoneornil(L, -1)) depth_write = lua_toboolean(L, -1);
    lua_pop(L, 1);

    BackendPipeline pip = pipeline_cache_get(
        &g_app_for_lua->pip_cache,
        sh_e->u.sh.h, &sh_e->u.sh.refl,
        (SglBlend)blend, depth_test, depth_write,
        (SglCull)cull, (SglPrimitive)prim,
        g_backend->swapchain_color_format(g_app_for_lua));
    g_backend->apply_pipeline(pip);

    // bindings: walk resources table, populate BindingsDesc.
    BindingsDesc bind = {0};
    bind.refl = &sh_e->u.sh.refl;

    lua_pushnil(L);
    while (lua_next(L, 2) != 0) {
        // stack: -2 = key, -1 = value
        if (lua_istable(L, -1)) {
            lua_getfield(L, -1, "__sgl_kind");
            const char *kind = lua_isstring(L, -1) ? lua_tostring(L, -1) : "";
            char kind_buf[16];
            strncpy(kind_buf, kind, sizeof(kind_buf) - 1);
            kind_buf[sizeof(kind_buf) - 1] = '\0';
            lua_pop(L, 1);

            if (strcmp(kind_buf, "buffer") == 0) {
                lua_getfield(L, -1, "key");
                const char *bk = lua_tostring(L, -1);
                ResEntry *be = bk ? res_table_get(&g_app_for_lua->res, bk) : NULL;
                lua_pop(L, 1);
                if (be && be->kind == RES_BUFFER && be->u.buf.type == SGL_BUFFER_VERTEX) {
                    bind.vbuf = be->u.buf.h;
                }
            } else if (strcmp(kind_buf, "texture") == 0) {
                // resource_name (the Lua key, like "diffuse") is on the stack at index -2.
                // Use lua_type rather than lua_isstring — the latter returns true for
                // numbers and lua_tostring would coerce the value in place, which corrupts
                // lua_next iteration.
                const char *res_name = lua_type(L, -2) == LUA_TSTRING ? lua_tostring(L, -2) : NULL;
                lua_getfield(L, -1, "key");
                const char *tk = lua_tostring(L, -1);
                ResEntry *te = tk ? res_table_get(&g_app_for_lua->res, tk) : NULL;
                lua_pop(L, 1);
                if (te && te->kind == RES_TEXTURE && res_name &&
                    bind.texture_count < (int)(sizeof(bind.textures)/sizeof(bind.textures[0])))
                {
                    bind.textures[bind.texture_count].name = res_name;
                    bind.textures[bind.texture_count].image = te->u.tex.h;
                    bind.texture_count++;
                }
            }
            // uniforms processing handled separately below (resources.uniforms key)
        }
        lua_pop(L, 1); // value, key stays for lua_next
    }
    g_backend->apply_bindings(&bind);

    // uniforms: read resources.uniforms = { ub_member_name = {floats...} } and pack
    // into the shader's first uniform block. PoC: only ub[0] supported.
    lua_getfield(L, 2, "uniforms");
    if (lua_istable(L, -1) && sh_e->u.sh.refl.ub_count > 0) {
        const ShaderUniformBlock *ub = &sh_e->u.sh.refl.ubs[0];
        int total_floats = ub->size_floats;
        if (total_floats < 0) total_floats = 0;
        // Stack-buffer up to a sensible cap; for matrices total is small.
        enum { UB_MAX_FLOATS = 256 };
        float buf[UB_MAX_FLOATS];
        memset(buf, 0, sizeof(buf));
        if (total_floats > UB_MAX_FLOATS) {
            return luaL_error(L, "draw: uniform block too large (%d floats > %d)",
                              total_floats, UB_MAX_FLOATS);
        }
        for (int m = 0; m < ub->member_count; ++m) {
            const ShaderUniformMember *mem = &ub->members[m];
            lua_getfield(L, -1, mem->name);
            if (lua_istable(L, -1)) {
                int n_provided = (int)lua_rawlen(L, -1);
                int copy = n_provided < mem->comp_count ? n_provided : mem->comp_count;
                for (int j = 0; j < copy; ++j) {
                    lua_rawgeti(L, -1, j + 1);
                    if (lua_isnumber(L, -1)) {
                        buf[mem->offset_floats + j] = (float)lua_tonumber(L, -1);
                    }
                    lua_pop(L, 1);
                }
            }
            lua_pop(L, 1); // pop the field (or nil)
        }
        g_backend->apply_uniforms(ub->slot, buf,
                                  (size_t)total_floats * sizeof(float));
    }
    lua_pop(L, 1); // pop "uniforms" field (or nil)

    g_backend->draw(0, count);
    return 0;
}

static int l_end_pass(lua_State *L) {
    (void)L;
    pass_state_end(&g_app_for_lua->pass);
    return 0;
}

static int l_capture(lua_State *L) {
    const char *path = luaL_checkstring(L, 1);
    capture_schedule(&g_app_for_lua->capture, path, 0); // 0 = next frame
    return 0;
}

void lua_api_register(lua_State *L) {
    enums_register(L);
    // main_tex は { __sgl_kind = "main_tex" } という sentinel テーブル
    lua_newtable(L);
    lua_pushstring(L, "main_tex");
    lua_setfield(L, -2, "__sgl_kind");
    lua_setglobal(L, "main_tex");

    lua_pushcfunction(L, l_begin_pass);
    lua_setglobal(L, "begin_pass");
    lua_pushcfunction(L, l_end_pass);
    lua_setglobal(L, "end_pass");
    lua_pushcfunction(L, l_use_buffer);
    lua_setglobal(L, "use_buffer");
    lua_pushcfunction(L, l_use_texture);
    lua_setglobal(L, "use_texture");
    lua_pushcfunction(L, l_use_shader);
    lua_setglobal(L, "use_shader");
    lua_pushcfunction(L, l_draw);
    lua_setglobal(L, "draw");
    lua_pushcfunction(L, l_capture);
    lua_setglobal(L, "capture");
}

static void push_event_table(lua_State *L, const SDL_Event *e) {
    lua_newtable(L);
    lua_pushinteger(L, e->type);
    lua_setfield(L, -2, "type");
    // 詳細フィールドは後段で増やす
}

static void call_global_if_present(lua_State *L, const char *name, int nargs) {
    lua_getglobal(L, name);
    if (!lua_isfunction(L, -1)) {
        lua_pop(L, 1 + nargs);
        return;
    }
    if (nargs > 0) {
        lua_insert(L, -1 - nargs);
    }
    if (lua_pcall(L, nargs, 0, 0) != LUA_OK) {
        SDL_Log("lua error in %s: %s", name, lua_tostring(L, -1));
        lua_pop(L, 1);
    }
}

bool lua_ctx_init(LuaCtx *ctx, const char *script_path, App *app) {
    g_app_for_lua = app;
    ctx->L = luaL_newstate();
    if (!ctx->L) {
        SDL_Log("luaL_newstate failed (out of memory)");
        return false;
    }
    luaL_openlibs(ctx->L);
    lua_api_register(ctx->L);
    if (luaL_dofile(ctx->L, script_path) != LUA_OK) {
        SDL_Log("lua load error: %s", lua_tostring(ctx->L, -1));
        lua_close(ctx->L);
        ctx->L = NULL;
        return false;
    }
    return true;
}

void lua_ctx_call_init(LuaCtx *ctx)  { if (!ctx->L) return; call_global_if_present(ctx->L, "on_init", 0); }
void lua_ctx_call_frame(LuaCtx *ctx) { if (!ctx->L) return; call_global_if_present(ctx->L, "on_frame", 0); }
void lua_ctx_call_quit(LuaCtx *ctx)  { if (!ctx->L) return; call_global_if_present(ctx->L, "on_quit", 0); }

void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e) {
    if (!ctx->L) return;
    push_event_table(ctx->L, e);
    call_global_if_present(ctx->L, "on_event", 1);
}

void lua_ctx_shutdown(LuaCtx *ctx) {
    if (ctx->L) lua_close(ctx->L);
    ctx->L = NULL;
}
