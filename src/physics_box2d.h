#pragma once
// Box2D の即時モード層の状態。API は include/lub/lub_api.h の lub_phys2d_*、
// Lua 面は src/lua_phys2d.c。
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
  // callback (filter / pre_solve / friction / restitution / query visitor)
  // の再入深さ。> 0 の間は物理を変える API を拒む。
  int callback_depth;
  PhysScratch scratch;
  PhysDebugBuffer debug;
} PhysState;

void phys2d_state_init(PhysState *state);
void phys2d_state_shutdown(PhysState *state);
