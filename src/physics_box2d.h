#pragma once

#include <lua.h>
#include <stdint.h>

typedef enum Phys2dBodyType {
  PHYS2D_STATIC = 0,
  PHYS2D_KINEMATIC = 1,
  PHYS2D_DYNAMIC = 2,
} Phys2dBodyType;

typedef struct PhysWorld PhysWorld;

#define PHYS2D_WORLD_BUCKETS 256

typedef struct PhysState {
  PhysWorld *worlds[PHYS2D_WORLD_BUCKETS];
} PhysState;

void phys2d_state_init(PhysState *state);
void phys2d_state_shutdown(PhysState *state);

void phys2d_lua_set_state(PhysState *state);
void phys2d_lua_register(lua_State *L);
