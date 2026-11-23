#pragma once
// State of the Box3D immediate-mode layer.
// The API is lub_phys3d_* in include/lub/lub_api.h, the tcs VM surface is
// src/gen/vm_api_gen.c (generated code).
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
