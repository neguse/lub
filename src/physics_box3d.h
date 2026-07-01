#pragma once

#include <lua.h>
#include <stdint.h>

typedef enum Phys3dBodyType {
  PHYS3D_STATIC = 0,
  PHYS3D_KINEMATIC = 1,
  PHYS3D_DYNAMIC = 2,
} Phys3dBodyType;

typedef struct Phys3dWorld Phys3dWorld;

#define PHYS3D_WORLD_BUCKETS 256

typedef struct Phys3dState {
  Phys3dWorld *worlds[PHYS3D_WORLD_BUCKETS];
} Phys3dState;

void phys3d_state_init(Phys3dState *state);
void phys3d_state_shutdown(Phys3dState *state);

void phys3d_lua_set_state(Phys3dState *state);
void phys3d_lua_register(lua_State *L);
