#pragma once
// State of the Box2D immediate-mode layer.
// The API is lub_phys2d_* in include/lub/lub_api.h, the tcs VM surface is
// src/gen/vm_api_gen.c (generated code).
#include "phys_common.h"
#include <stdbool.h>
#include <stdint.h>

typedef struct PhysWorld PhysWorld;

#define PHYS2D_WORLD_BUCKETS 256

typedef struct PhysDebugArray {
  float *items;
  int count;
  int cap;
} PhysDebugArray;

typedef struct PhysDebugBuffer {
  PhysDebugArray segments;
  PhysDebugArray circles;
  PhysDebugArray capsules;
  PhysDebugArray polygons;
  PhysDebugArray points;
  bool failed;
} PhysDebugBuffer;

typedef struct PhysState {
  PhysWorld *worlds[PHYS2D_WORLD_BUCKETS];
  PhysHandles handles;
  // Reentry depth of callbacks (filter / pre_solve / friction / restitution /
  // query visitor). While > 0, APIs that change physics are rejected.
  int callback_depth;
  PhysScratch scratch;
  PhysDebugBuffer debug;
} PhysState;

void phys2d_state_init(PhysState *state);
void phys2d_state_shutdown(PhysState *state);
