#include "common.h"

#include <lauxlib.h>
#include <lua.h>

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#if defined(SPIKE_AOT_HASH) == defined(SPIKE_AOT_SLOT)
#error "define exactly one AOT layout"
#endif

#if defined(SPIKE_AOT_HASH)
#define SPIKE_VARIANT "aot-hash"
#else
#define SPIKE_VARIANT "aot-slot"
#endif

typedef struct Field {
    lua_Integer slot;
    const char *key;
} Field;

static const Field FIELD_X = { 1, "x" };
static const Field FIELD_Y = { 2, "y" };
static const Field FIELD_VX = { 3, "vx" };
static const Field FIELD_VY = { 4, "vy" };
static const Field FIELD_FRAME = { 5, "frame" };
static const Field FIELD_PX = { 1, "px" };
static const Field FIELD_PY = { 2, "py" };

typedef struct RunResult {
    double elapsed_seconds;
    uint32_t digest;
} RunResult;

static lua_State *
new_state(void)
{
    lua_State *state = luaL_newstate();

    if (state == NULL) {
        fputs("failed to create Lua state\n", stderr);
        exit(EXIT_FAILURE);
    }
    return state;
}

static void
new_record(lua_State *state, int field_count)
{
#if defined(SPIKE_AOT_HASH)
    lua_createtable(state, 0, field_count);
#else
    lua_createtable(state, field_count, 0);
#endif
}

static float
get_number(lua_State *state, int table_index, const Field *field)
{
    float value;

    table_index = lua_absindex(state, table_index);
#if defined(SPIKE_AOT_HASH)
    lua_getfield(state, table_index, field->key);
#else
    lua_rawgeti(state, table_index, field->slot);
#endif
    value = (float)lua_tonumber(state, -1);
    lua_pop(state, 1);
    return value;
}

static void
set_number(lua_State *state, int table_index, const Field *field, float value)
{
    table_index = lua_absindex(state, table_index);
    lua_pushnumber(state, (lua_Number)value);
#if defined(SPIKE_AOT_HASH)
    lua_setfield(state, table_index, field->key);
#else
    lua_rawseti(state, table_index, field->slot);
#endif
}

static lua_Integer
get_integer(lua_State *state, int table_index, const Field *field)
{
    lua_Integer value;

    table_index = lua_absindex(state, table_index);
#if defined(SPIKE_AOT_HASH)
    lua_getfield(state, table_index, field->key);
#else
    lua_rawgeti(state, table_index, field->slot);
#endif
    value = lua_tointeger(state, -1);
    lua_pop(state, 1);
    return value;
}

static void
set_integer(lua_State *state, int table_index, const Field *field,
    lua_Integer value)
{
    table_index = lua_absindex(state, table_index);
    lua_pushinteger(state, value);
#if defined(SPIKE_AOT_HASH)
    lua_setfield(state, table_index, field->key);
#else
    lua_rawseti(state, table_index, field->slot);
#endif
}

static void
initialize_entity(lua_State *state, int table_index, SpikeRng *rng)
{
    set_number(state, table_index, &FIELD_X,
        spike_frand(rng, 0.0f, 400.0f));
    set_number(state, table_index, &FIELD_Y,
        spike_frand(rng, 0.0f, 240.0f));
    set_number(state, table_index, &FIELD_VX,
        spike_frand(rng, -30.0f, 30.0f));
    set_number(state, table_index, &FIELD_VY,
        spike_frand(rng, -30.0f, 30.0f));
}

static void
update_entity(lua_State *state, int table_index)
{
    float x = get_number(state, table_index, &FIELD_X);
    float y = get_number(state, table_index, &FIELD_Y);
    float vx = get_number(state, table_index, &FIELD_VX);
    float vy = get_number(state, table_index, &FIELD_VY);

    x = x + vx * SPIKE_DT;
    y = y + vy * SPIKE_DT;
    if (x < 0.0f) {
        x = 0.0f;
        vx = -vx;
    }
    if (x > 400.0f) {
        x = 400.0f;
        vx = -vx;
    }
    if (y < 0.0f) {
        y = 0.0f;
        vy = -vy;
    }
    if (y > 240.0f) {
        y = 240.0f;
        vy = -vy;
    }

    set_number(state, table_index, &FIELD_X, x);
    set_number(state, table_index, &FIELD_Y, y);
    set_number(state, table_index, &FIELD_VX, vx);
    set_number(state, table_index, &FIELD_VY, vy);
}

static uint32_t
digest_record(lua_State *state, int table_index, uint32_t digest,
    const Field *position_x, const Field *position_y)
{
    digest = spike_digest_float(digest,
        get_number(state, table_index, position_x));
    digest = spike_digest_float(digest,
        get_number(state, table_index, position_y));
    digest = spike_digest_float(digest,
        get_number(state, table_index, &FIELD_VX));
    digest = spike_digest_float(digest,
        get_number(state, table_index, &FIELD_VY));
    return digest;
}

static RunResult
run_sprite_update(size_t count)
{
    SpikeRng rng = { UINT32_C(12345) };
    lua_State *state = new_state();
    int root;
    size_t i;
    int frame;
    double started;
    RunResult result;

    lua_createtable(state, (int)count, 0);
    root = lua_gettop(state);
    for (i = 0; i < count; ++i) {
        new_record(state, 5);
        set_number(state, -1, &FIELD_X,
            spike_frand(&rng, 0.0f, 400.0f));
        set_number(state, -1, &FIELD_Y,
            spike_frand(&rng, 0.0f, 240.0f));
        set_number(state, -1, &FIELD_VX,
            spike_frand(&rng, -60.0f, 60.0f));
        set_number(state, -1, &FIELD_VY,
            spike_frand(&rng, -60.0f, 60.0f));
        set_integer(state, -1, &FIELD_FRAME,
            (lua_Integer)(spike_lcg(&rng) % UINT32_C(8)));
        lua_rawseti(state, root, (lua_Integer)i + 1);
    }
    (void)lua_gc(state, LUA_GCCOLLECT);

    started = spike_now_seconds();
    for (frame = 0; frame < SPIKE_FRAMES; ++frame) {
        for (i = 0; i < count; ++i) {
            int record;
            float x;
            float y;
            float vx;
            float vy;
            lua_Integer sprite_frame;

            lua_rawgeti(state, root, (lua_Integer)i + 1);
            record = lua_gettop(state);
            x = get_number(state, record, &FIELD_X);
            y = get_number(state, record, &FIELD_Y);
            vx = get_number(state, record, &FIELD_VX);
            vy = get_number(state, record, &FIELD_VY);
            sprite_frame = get_integer(state, record, &FIELD_FRAME);

            x = x + vx * SPIKE_DT;
            y = y + vy * SPIKE_DT;
            if (x < 0.0f) {
                x = 0.0f;
                vx = -vx;
            }
            if (x > 400.0f) {
                x = 400.0f;
                vx = -vx;
            }
            if (y < 0.0f) {
                y = 0.0f;
                vy = -vy;
            }
            if (y > 240.0f) {
                y = 240.0f;
                vy = -vy;
            }
            sprite_frame = (sprite_frame + 1) % 8;

            set_number(state, record, &FIELD_X, x);
            set_number(state, record, &FIELD_Y, y);
            set_number(state, record, &FIELD_VX, vx);
            set_number(state, record, &FIELD_VY, vy);
            set_integer(state, record, &FIELD_FRAME, sprite_frame);
            lua_pop(state, 1);
        }
    }
    result.elapsed_seconds = spike_now_seconds() - started;

    result.digest = spike_digest_init();
    for (i = 0; i < count; ++i) {
        lua_rawgeti(state, root, (lua_Integer)i + 1);
        result.digest = digest_record(state, -1, result.digest,
            &FIELD_X, &FIELD_Y);
        lua_pop(state, 1);
    }

    lua_close(state);
    return result;
}

static RunResult
run_spawn_churn(int pooled)
{
    SpikeRng rng = { UINT32_C(12345) };
    lua_State *state = new_state();
    int root;
    size_t head = 0;
    size_t i;
    int frame;
    double started;
    RunResult result;

    lua_createtable(state, SPIKE_SPAWN_CAPACITY, 0);
    root = lua_gettop(state);
    for (i = 0; i < SPIKE_SPAWN_CAPACITY; ++i) {
        new_record(state, 4);
        initialize_entity(state, -1, &rng);
        lua_rawseti(state, root, (lua_Integer)i + 1);
    }
    (void)lua_gc(state, LUA_GCCOLLECT);

    started = spike_now_seconds();
    for (frame = 0; frame < SPIKE_FRAMES; ++frame) {
        for (i = 0; i < SPIKE_SPAWN_PER_FRAME; ++i) {
            size_t slot = (head + i) % SPIKE_SPAWN_CAPACITY;

            if (pooled) {
                lua_rawgeti(state, root, (lua_Integer)slot + 1);
                initialize_entity(state, -1, &rng);
                lua_pop(state, 1);
            }
            else {
                new_record(state, 4);
                initialize_entity(state, -1, &rng);
                lua_rawseti(state, root, (lua_Integer)slot + 1);
            }
        }
        head = (head + SPIKE_SPAWN_PER_FRAME) % SPIKE_SPAWN_CAPACITY;
        for (i = 0; i < SPIKE_SPAWN_CAPACITY; ++i) {
            size_t slot = (head + i) % SPIKE_SPAWN_CAPACITY;

            lua_rawgeti(state, root, (lua_Integer)slot + 1);
            update_entity(state, -1);
            lua_pop(state, 1);
        }
    }
    result.elapsed_seconds = spike_now_seconds() - started;

    result.digest = spike_digest_init();
    for (i = 0; i < SPIKE_SPAWN_CAPACITY; ++i) {
        size_t slot = (head + i) % SPIKE_SPAWN_CAPACITY;

        lua_rawgeti(state, root, (lua_Integer)slot + 1);
        result.digest = digest_record(state, -1, result.digest,
            &FIELD_X, &FIELD_Y);
        lua_pop(state, 1);
    }

    lua_close(state);
    return result;
}

static RunResult
run_particles(void)
{
    SpikeRng rng = { UINT32_C(12345) };
    lua_State *state = new_state();
    int root;
    size_t i;
    int frame;
    double started;
    RunResult result;

    lua_createtable(state, SPIKE_PARTICLE_COUNT, 0);
    root = lua_gettop(state);
    for (i = 0; i < SPIKE_PARTICLE_COUNT; ++i) {
        new_record(state, 4);
        set_number(state, -1, &FIELD_PX,
            spike_frand(&rng, 100.0f, 300.0f));
        set_number(state, -1, &FIELD_PY,
            spike_frand(&rng, 50.0f, 200.0f));
        set_number(state, -1, &FIELD_VX,
            spike_frand(&rng, -40.0f, 40.0f));
        set_number(state, -1, &FIELD_VY,
            spike_frand(&rng, -40.0f, 40.0f));
        lua_rawseti(state, root, (lua_Integer)i + 1);
    }
    (void)lua_gc(state, LUA_GCCOLLECT);

    started = spike_now_seconds();
    for (frame = 0; frame < SPIKE_FRAMES; ++frame) {
        for (i = 0; i < SPIKE_PARTICLE_COUNT; ++i) {
            int record;
            float px;
            float py;
            float vx;
            float vy;
            float dx;
            float dy;
            float d2;

            lua_rawgeti(state, root, (lua_Integer)i + 1);
            record = lua_gettop(state);
            px = get_number(state, record, &FIELD_PX);
            py = get_number(state, record, &FIELD_PY);
            vx = get_number(state, record, &FIELD_VX);
            vy = get_number(state, record, &FIELD_VY);

            vy = vy + 98.0f * SPIKE_DT;
            px = px + vx * SPIKE_DT;
            py = py + vy * SPIKE_DT;
            if (px < 0.0f) {
                px = 0.0f;
                vx = -vx * 0.9f;
            }
            if (px > 400.0f) {
                px = 400.0f;
                vx = -vx * 0.9f;
            }
            if (py < 0.0f) {
                py = 0.0f;
                vy = -vy * 0.9f;
            }
            if (py > 240.0f) {
                py = 240.0f;
                vy = -vy * 0.9f;
            }
            dx = px - 200.0f;
            dy = py - 120.0f;
            d2 = dx * dx + dy * dy;
            if (d2 < 400.0f) {
                px = px + dx * 0.1f;
                py = py + dy * 0.1f;
            }

            set_number(state, record, &FIELD_PX, px);
            set_number(state, record, &FIELD_PY, py);
            set_number(state, record, &FIELD_VX, vx);
            set_number(state, record, &FIELD_VY, vy);
            lua_pop(state, 1);
        }
    }
    result.elapsed_seconds = spike_now_seconds() - started;

    result.digest = spike_digest_init();
    for (i = 0; i < SPIKE_PARTICLE_COUNT; ++i) {
        lua_rawgeti(state, root, (lua_Integer)i + 1);
        result.digest = digest_record(state, -1, result.digest,
            &FIELD_PX, &FIELD_PY);
        lua_pop(state, 1);
    }

    lua_close(state);
    return result;
}

int
main(int argc, char **argv)
{
    SpikeWorkload workload;
    RunResult result;

    if (!spike_parse_workload(argc, argv, &workload)) {
        return EXIT_FAILURE;
    }

    switch (workload.kernel) {
    case SPIKE_SPRITE_UPDATE:
        result = run_sprite_update(workload.count);
        break;
    case SPIKE_SPAWN_CHURN_NAIVE:
        result = run_spawn_churn(0);
        break;
    case SPIKE_SPAWN_CHURN_POOL:
        result = run_spawn_churn(1);
        break;
    case SPIKE_PARTICLES:
        result = run_particles();
        break;
    default:
        return EXIT_FAILURE;
    }

    spike_print_result(&workload, SPIKE_VARIANT, result.elapsed_seconds,
        result.digest);
    return EXIT_SUCCESS;
}
