#pragma once
// Box3D の即時モード層の状態。API は include/lub/lub_api.h の lub_phys3d_*、
// Lua 面は src/lua_phys3d.c。
#include "phys_common.h"
#include <stdbool.h>
#include <stdint.h>

typedef struct Phys3dWorld Phys3dWorld;

#define PHYS3D_WORLD_BUCKETS 256

typedef struct Phys3dState {
  Phys3dWorld *worlds[PHYS3D_WORLD_BUCKETS];
  PhysHandles handles;
  int callback_depth;
  PhysScratch scratch;
} Phys3dState;

void phys3d_state_init(Phys3dState *state);
void phys3d_state_shutdown(Phys3dState *state);
