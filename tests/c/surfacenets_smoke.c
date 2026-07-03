// surface_nets smoke: mesh a sphere SDF grid and check the output mesh —
// vertices sit on the sphere, normals are radial, indices are in range and
// wound CCW seen from outside, and an all-outside grid yields an empty mesh.
#include "surfacenets.h"

#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>
#include <stdio.h>

static const char *SCRIPT =
    "local function check(cond, msg)\n"
    "  if not cond then error(msg, 2) end\n"
    "end\n"
    "local n = 24\n"
    "local r = 0.5\n"
    "local half = 0.75\n"
    "local cell = 2 * half / (n - 1)\n"
    "local g = {}\n"
    "local i = 1\n"
    "for z = 0, n - 1 do\n"
    "  local pz = -half + z * cell\n"
    "  for y = 0, n - 1 do\n"
    "    local py = -half + y * cell\n"
    "    for x = 0, n - 1 do\n"
    "      local px = -half + x * cell\n"
    "      g[i] = math.sqrt(px * px + py * py + pz * pz) - r\n"
    "      i = i + 1\n"
    "    end\n"
    "  end\n"
    "end\n"
    "local m = surface_nets(g, n, n, n, cell, -half, -half, -half)\n"
    "check(m.vert_count > 0, 'no vertices')\n"
    "check(m.index_count > 0, 'no indices')\n"
    "check(m.index_count % 3 == 0, 'index_count not a multiple of 3')\n"
    "check(#m.positions == m.vert_count * 3, 'positions length mismatch')\n"
    "check(#m.normals == m.vert_count * 3, 'normals length mismatch')\n"
    "check(#m.indices == m.index_count, 'indices length mismatch')\n"
    "for v = 0, m.vert_count - 1 do\n"
    "  local px = m.positions[v * 3 + 1]\n"
    "  local py = m.positions[v * 3 + 2]\n"
    "  local pz = m.positions[v * 3 + 3]\n"
    "  local len = math.sqrt(px * px + py * py + pz * pz)\n"
    "  check(math.abs(len - r) < cell, 'vertex off the sphere surface')\n"
    "  local nx = m.normals[v * 3 + 1]\n"
    "  local ny = m.normals[v * 3 + 2]\n"
    "  local nz = m.normals[v * 3 + 3]\n"
    "  local nlen = math.sqrt(nx * nx + ny * ny + nz * nz)\n"
    "  check(math.abs(nlen - 1) < 0.001, 'normal not unit length')\n"
    "  local dot = (nx * px + ny * py + nz * pz) / len\n"
    "  check(dot > 0.9, 'normal not radial')\n"
    "end\n"
    "for k = 1, m.index_count do\n"
    "  local ix = m.indices[k]\n"
    "  check(ix >= 0 and ix < m.vert_count, 'index out of range')\n"
    "end\n"
    "local function pos(ix)\n"
    "  return m.positions[ix * 3 + 1], m.positions[ix * 3 + 2],\n"
    "      m.positions[ix * 3 + 3]\n"
    "end\n"
    "for t = 0, m.index_count / 3 - 1 do\n"
    "  local ax, ay, az = pos(m.indices[t * 3 + 1])\n"
    "  local bx, by, bz = pos(m.indices[t * 3 + 2])\n"
    "  local cx, cy, cz = pos(m.indices[t * 3 + 3])\n"
    "  local ux, uy, uz = bx - ax, by - ay, bz - az\n"
    "  local vx, vy, vz = cx - ax, cy - ay, cz - az\n"
    "  local fx = uy * vz - uz * vy\n"
    "  local fy = uz * vx - ux * vz\n"
    "  local fz = ux * vy - uy * vx\n"
    "  local mx = (ax + bx + cx) / 3\n"
    "  local my = (ay + by + cy) / 3\n"
    "  local mz = (az + bz + cz) / 3\n"
    "  check(fx * mx + fy * my + fz * mz > 0,\n"
    "      'triangle winding is not CCW seen from outside')\n"
    "end\n"
    "local g2 = {}\n"
    "for k = 1, 8 do g2[k] = 1 end\n"
    "local m2 = surface_nets(g2, 2, 2, 2)\n"
    "check(m2.vert_count == 0 and m2.index_count == 0,\n"
    "    'all-outside grid should produce an empty mesh')\n";

int main(void) {
  lua_State *L = luaL_newstate();
  if (!L) {
    fprintf(stderr, "luaL_newstate failed\n");
    return 1;
  }
  luaL_openlibs(L);
  lua_pushcfunction(L, lub_surface_nets);
  lua_setglobal(L, "surface_nets");

  if (luaL_dostring(L, SCRIPT) != LUA_OK) {
    fprintf(stderr, "surfacenets smoke FAILED: %s\n", lua_tostring(L, -1));
    lua_close(L);
    return 1;
  }
  lua_close(L);
  printf("surfacenets smoke OK\n");
  return 0;
}
