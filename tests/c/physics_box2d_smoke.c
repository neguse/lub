#include "physics_box2d.h"

#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>
#include <stdio.h>

static const char *SCRIPT =
    "local function check(cond, msg)\n"
    "  if not cond then error(msg) end\n"
    "end\n"
    "local function near(a, b)\n"
    "  return math.abs(a - b) < 0.001\n"
    "end\n"
    "do\n"
    "  local world = phys2d_world('c_keymap', {\n"
    "    gravity = { x = 0, y = 0 }, fixed_dt = 1 / 60, max_steps = 1,\n"
    "  })\n"
    "  phys2d_begin(world, { prune = false })\n"
    "  local body = phys2d_body(world, 'temp', {\n"
    "    version = 1, type = DYNAMIC, initial = { x = 1, y = 0 },\n"
    "  })\n"
    "  phys2d_box(body, 'solid', { version = 1, hx = 0.1, hy = 0.1, density = "
    "1 })\n"
    "  phys2d_step(world, 0)\n"
    "  phys2d_begin(world, { prune = false })\n"
    "  body = phys2d_body(world, 'temp', {\n"
    "    version = 1, type = DYNAMIC, initial = { x = 3, y = 0 },\n"
    "  })\n"
    "  phys2d_box(body, 'solid', { version = 1, hx = 0.1, hy = 0.1, density = "
    "1 })\n"
    "  phys2d_step(world, 0)\n"
    "  local pose = phys2d_pose(world, 'temp')\n"
    "  check(pose and near(pose.x, 1), 'key map update recreated "
    "explicit-version body')\n"
    "  phys2d_begin(world)\n"
    "  phys2d_step(world, 0)\n"
    "  local gone, err = phys2d_pose(world, 'temp')\n"
    "  check(gone == nil and err == 'not found', 'key map prune did not remove "
    "body')\n"
    "end\n"
    "do\n"
    "  local world = phys2d_world('c_clamp', {\n"
    "    gravity = { x = 0, y = 0 }, fixed_dt = 1 / 60, max_steps = 2,\n"
    "  })\n"
    "  phys2d_begin(world, { prune = false })\n"
    "  local info = phys2d_step(world, 1.0)\n"
    "  check(info.steps == 2 and info.dropped == true, 'accumulator clamp did "
    "not drop leftover dt')\n"
    "end\n"
    "do\n"
    "  local function declare(world)\n"
    "    phys2d_begin(world)\n"
    "    local ground = phys2d_body(world, 'ground', {\n"
    "      type = STATIC, initial = { x = 0, y = -0.25 },\n"
    "    })\n"
    "    phys2d_box(ground, 'solid', { hx = 4, hy = 0.25, contact = true })\n"
    "    local ball = phys2d_body(world, 'ball', {\n"
    "      type = DYNAMIC, initial = { x = 0, y = 2 },\n"
    "    })\n"
    "    phys2d_circle(ball, 'solid', { r = 0.25, density = 1, contact = true "
    "})\n"
    "  end\n"
    "  local world = phys2d_world('c_multistep', {\n"
    "    gravity = { x = 0, y = -10 }, fixed_dt = 1 / 60, max_steps = 240,\n"
    "  })\n"
    "  declare(world)\n"
    "  local info = phys2d_step(world, 3.0)\n"
    "  check(info.steps > 30, 'multi-step frame ran too few steps')\n"
    "  check(#phys2d_contacts(world, 'begin') >= 1,\n"
    "    'begin event from a non-final fixed step was lost')\n"
    "  local stale = phys2d_world('c_stale', {\n"
    "    gravity = { x = 0, y = -10 }, fixed_dt = 1 / 60, max_steps = 1,\n"
    "  })\n"
    "  local saw = false\n"
    "  for frame = 1, 120 do\n"
    "    declare(stale)\n"
    "    phys2d_step(stale, 1 / 60)\n"
    "    if #phys2d_contacts(stale, 'begin') > 0 then\n"
    "      saw = true\n"
    "      break\n"
    "    end\n"
    "  end\n"
    "  check(saw, 'stale-check setup: begin never fired')\n"
    "  declare(stale)\n"
    "  local zero = phys2d_step(stale, 0.0001)\n"
    "  check(zero.steps == 0, 'stale-check setup: expected 0 steps')\n"
    "  check(#phys2d_contacts(stale, 'begin') == 0,\n"
    "    'begin event re-reported on a 0-step frame')\n"
    "end\n"
    "do\n"
    "  local filter_errors = 0\n"
    "  local pre_solve_errors = 0\n"
    "  local friction_errors = 0\n"
    "  local restitution_errors = 0\n"
    "  local function bad_filter()\n"
    "    filter_errors = filter_errors + 1\n"
    "    error('c bad filter')\n"
    "  end\n"
    "  local function bad_pre_solve()\n"
    "    pre_solve_errors = pre_solve_errors + 1\n"
    "    error('c bad pre_solve')\n"
    "  end\n"
    "  local function bad_friction()\n"
    "    friction_errors = friction_errors + 1\n"
    "    error('c bad friction')\n"
    "  end\n"
    "  local function bad_restitution()\n"
    "    restitution_errors = restitution_errors + 1\n"
    "    error('c bad restitution')\n"
    "  end\n"
    "  local function declare_pair(world)\n"
    "    phys2d_begin(world, { prune = false })\n"
    "    local wall = phys2d_body(world, 'wall', { type = STATIC, initial = { "
    "x = 0, y = 0 } })\n"
    "    phys2d_box(wall, 'solid', {\n"
    "      hx = 0.25, hy = 0.25, friction = 0.8, restitution = 0.1,\n"
    "      contact = true, pre_solve = true,\n"
    "    })\n"
    "    local box = phys2d_body(world, 'box', { type = DYNAMIC, initial = { x "
    "= 0, y = 0 } })\n"
    "    phys2d_box(box, 'solid', {\n"
    "      hx = 0.2, hy = 0.2, density = 1, friction = 0.2, restitution = "
    "0.3,\n"
    "      contact = true, pre_solve = true,\n"
    "    })\n"
    "    phys2d_step(world, 1 / 60)\n"
    "  end\n"
    "  local world = phys2d_world('c_callbacks', {\n"
    "    gravity = { x = 0, y = 0 }, fixed_dt = 1 / 60, max_steps = 1, sleep = "
    "false,\n"
    "    callbacks = {\n"
    "      filter = bad_filter, pre_solve = bad_pre_solve,\n"
    "      friction = bad_friction, restitution = bad_restitution,\n"
    "    },\n"
    "  })\n"
    "  declare_pair(world)\n"
    "  check(filter_errors > 0 and pre_solve_errors > 0 and friction_errors > "
    "0 and restitution_errors > 0,\n"
    "    'callback fallback smoke did not exercise all callbacks')\n"
    "  local before = filter_errors + pre_solve_errors + friction_errors + "
    "restitution_errors\n"
    "  world = phys2d_world('c_callbacks', {\n"
    "    gravity = { x = 0, y = 0 }, fixed_dt = 1 / 60, max_steps = 1, sleep = "
    "false,\n"
    "  })\n"
    "  declare_pair(world)\n"
    "  local after = filter_errors + pre_solve_errors + friction_errors + "
    "restitution_errors\n"
    "  check(after == before, 'callback refs were not cleared when callbacks "
    "omitted')\n"
    "end\n"
    "phys2d_world('c_unstepped_callbacks', {\n"
    "  callbacks = { filter = function() return true end },\n"
    "})\n"
    "local saw_contact = false\n"
    "local y = 0\n"
    "for frame = 1, 180 do\n"
    "  local world = phys2d_world('c_smoke', {\n"
    "    gravity = { x = 0, y = -10 },\n"
    "    fixed_dt = 1 / 60,\n"
    "    substeps = 4,\n"
    "    max_steps = 1,\n"
    "  })\n"
    "  phys2d_begin(world)\n"
    "  local ground = phys2d_body(world, 'ground', {\n"
    "    type = STATIC,\n"
    "    initial = { x = 0, y = -0.25 },\n"
    "  })\n"
    "  local ground_shape = phys2d_box(ground, 'solid', {\n"
    "    hx = 4,\n"
    "    hy = 0.25,\n"
    "    density = 0,\n"
    "    friction = 0.8,\n"
    "    contact = true,\n"
    "    filter = { category = 63, mask_bits = '0x8000000000000000' },\n"
    "  })\n"
    "  local ball = phys2d_body(world, 'ball', {\n"
    "    type = DYNAMIC,\n"
    "    initial = { x = 0, y = 2.0 },\n"
    "  })\n"
    "  local ball_shape = phys2d_circle(ball, 'solid', {\n"
    "    r = 0.25,\n"
    "    density = 1,\n"
    "    contact = true,\n"
    "    filter = { category_bits = '0x8000000000000000', mask = { 63 } },\n"
    "  })\n"
    "  local gi = phys2d_shape_info(ground_shape).filter\n"
    "  local bi = phys2d_shape_info(ball_shape).filter\n"
    "  if gi.category_bits ~= '8000000000000000' or gi.mask_bits ~= "
    "'8000000000000000' or\n"
    "     bi.category_bits ~= '8000000000000000' or bi.mask_bits ~= "
    "'8000000000000000' then\n"
    "    error('bit 63 filter did not round-trip')\n"
    "  end\n"
    "  phys2d_step(world, 1 / 60)\n"
    "  local pose = phys2d_pose(ball)\n"
    "  y = pose.y\n"
    "  for _, contact in ipairs(phys2d_contacts(world, 'begin')) do\n"
    "    local a = contact.a.body .. '/' .. contact.a.shape\n"
    "    local b = contact.b.body .. '/' .. contact.b.shape\n"
    "    if (a == 'ball/solid' and b == 'ground/solid') or\n"
    "       (a == 'ground/solid' and b == 'ball/solid') then\n"
    "      saw_contact = true\n"
    "      return true, frame, y\n"
    "    end\n"
    "  end\n"
    "end\n"
    "return false, 180, y\n";

int main(void) {
  PhysState phys;
  phys2d_state_init(&phys);

  lua_State *L = luaL_newstate();
  if (!L) {
    fprintf(stderr, "luaL_newstate failed\n");
    phys2d_state_shutdown(&phys);
    return 1;
  }

  luaL_openlibs(L);
  phys2d_lua_set_state(&phys);
  phys2d_lua_register(L);

  if (luaL_loadstring(L, SCRIPT) != LUA_OK) {
    fprintf(stderr, "load failed: %s\n", lua_tostring(L, -1));
    phys2d_state_shutdown(&phys);
    lua_close(L);
    return 1;
  }

  if (lua_pcall(L, 0, 3, 0) != LUA_OK) {
    fprintf(stderr, "script failed: %s\n", lua_tostring(L, -1));
    phys2d_state_shutdown(&phys);
    lua_close(L);
    return 1;
  }

  int ok = lua_toboolean(L, -3);
  int frame = (int)lua_tointeger(L, -2);
  double y = lua_tonumber(L, -1);
  lua_pop(L, 3);

  phys2d_state_shutdown(&phys);
  lua_close(L);

  if (!ok) {
    fprintf(stderr, "PHYS2D_C_SMOKE_FAIL frame=%d y=%.4f\n", frame, y);
    return 1;
  }

  printf("PHYS2D_C_SMOKE_OK frame=%d y=%.4f\n", frame, y);
  return 0;
}
