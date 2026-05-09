#include "lua_api.h"
#include "enums_lua.h"
#include "app.h"
#include "pass.h"
#include "resources.h"
#include "shader.h"
#include "pipeline.h"
#include "enums.h"
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

    if (e->version == version && e->u.buf.h.id != 0) {
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

    if (e->u.buf.h.id != 0) sg_destroy_buffer(e->u.buf.h);
    e->u.buf.h = sg_make_buffer(&(sg_buffer_desc){
        .size = (size_t)n * sizeof(float),
        .usage = {
            .vertex_buffer = (type == SGL_BUFFER_VERTEX),
            .index_buffer  = (type == SGL_BUFFER_INDEX),
            .immutable     = true,
        },
        .data = { .ptr = data, .size = (size_t)n * sizeof(float) },
    });
    e->u.buf.type = (SglBufferType)type;
    e->u.buf.size_bytes = (size_t)n * sizeof(float);
    e->version = version;
    free(data);

    push_buffer_ref(L, key);
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

    if (e->version == version && e->u.sh.h.id != 0) {
        push_shader_ref(L, key);
        return 1;
    }

    char err[1024];
    sg_shader sh;
    ShaderReflection refl;
    if (!shader_compile_and_create(vs, fs, &sh, &refl, err, sizeof(err))) {
        return luaL_error(L, "shader compile error: %s", err);
    }
    if (e->u.sh.h.id != 0) sg_destroy_shader(e->u.sh.h);
    e->u.sh.h = sh;
    e->u.sh.refl = refl;
    e->version = version;

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

    sg_pipeline pip = pipeline_cache_get(
        &g_app_for_lua->pip_cache,
        sh_e->u.sh.h, &sh_e->u.sh.refl,
        (SglBlend)blend, depth_test, depth_write,
        (SglCull)cull, (SglPrimitive)prim,
        SG_PIXELFORMAT_RGBA8, SG_PIXELFORMAT_DEPTH_STENCIL);
    sg_apply_pipeline(pip);

    // bindings: walk resources table and resolve by kind
    sg_bindings bind = {0};
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
                    bind.vertex_buffers[0] = be->u.buf.h;
                }
            }
            // texture / uniforms processing added in Task 10 / Task 11
        }
        lua_pop(L, 1); // value, key stays for lua_next
    }
    sg_apply_bindings(&bind);
    sg_draw(0, count, 1);
    return 0;
}

static int l_end_pass(lua_State *L) {
    (void)L;
    pass_state_end(&g_app_for_lua->pass);
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
    lua_pushcfunction(L, l_use_shader);
    lua_setglobal(L, "use_shader");
    lua_pushcfunction(L, l_draw);
    lua_setglobal(L, "draw");
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
