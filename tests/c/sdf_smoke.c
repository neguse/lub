// sdf_mesh smoke: mesh SDF trees and check the output — a plain sphere tree
// reproduces the analytic sphere (vertices, normals, winding), a nested
// move+rotate+smin tree stays inside its reported bounds, and invalid trees
// (unknown op, bad version, missing field) raise errors instead of passing
// silently.
#include "sdf.h"

#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>
#include <stdio.h>

static const char *SCRIPT =
    "local function check(cond, msg)\n"
    "  if not cond then error(msg, 2) end\n"
    "end\n"
    "-- sphere: analytic checks\n"
    "local r = 0.5\n"
    "local m = sdf_mesh({version = 1, root = {op = 'sphere', r = r}}, 32)\n"
    "check(m.vert_count > 0, 'no vertices')\n"
    "check(m.index_count % 3 == 0, 'index_count not a multiple of 3')\n"
    "check(#m.positions == m.vert_count * 3, 'positions length mismatch')\n"
    "check(#m.normals == m.vert_count * 3, 'normals length mismatch')\n"
    "check(m.cell > 0, 'cell missing')\n"
    "check(m.bounds_min[1] == -r and m.bounds_max[2] == r, 'sphere bounds')\n"
    "for v = 0, m.vert_count - 1 do\n"
    "  local px = m.positions[v * 3 + 1]\n"
    "  local py = m.positions[v * 3 + 2]\n"
    "  local pz = m.positions[v * 3 + 3]\n"
    "  local len = math.sqrt(px * px + py * py + pz * pz)\n"
    "  check(math.abs(len - r) < m.cell, 'vertex off the sphere surface')\n"
    "  local nx = m.normals[v * 3 + 1]\n"
    "  local ny = m.normals[v * 3 + 2]\n"
    "  local nz = m.normals[v * 3 + 3]\n"
    "  check((nx * px + ny * py + nz * pz) / len > 0.9, 'normal not radial')\n"
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
    "  check(fx * (ax + bx + cx) + fy * (ay + by + cy) + fz * (az + bz + cz)\n"
    "      > 0, 'winding not CCW from outside')\n"
    "end\n"
    "-- nested move+rotate+smin+mirror: bounds contain every vertex\n"
    "local s2 = math.sqrt(0.5)\n"
    "local tree = {\n"
    "  version = 1,\n"
    "  root = {\n"
    "    op = 'smin', k = 0.2,\n"
    "    a = { op = 'move', x = 0.4, y = 0.6, z = -0.2,\n"
    "          c = { op = 'rotate', qx = 0, qy = 0, qz = s2, qw = s2,\n"
    "                c = { op = 'box', hx = 0.5, hy = 0.2, hz = 0.2 } } },\n"
    "    b = { op = 'mirror_x',\n"
    "          c = { op = 'capsule', ax = 0.3, ay = 0, az = 0,\n"
    "                bx = 0.9, by = 0.5, bz = 0, r = 0.15 } },\n"
    "  },\n"
    "}\n"
    "local m2 = sdf_mesh(tree, 48)\n"
    "check(m2.vert_count > 0, 'nested tree produced no mesh')\n"
    "for v = 0, m2.vert_count - 1 do\n"
    "  for i = 1, 3 do\n"
    "    local p = m2.positions[v * 3 + i]\n"
    "    check(p >= m2.bounds_min[i] - m2.cell and\n"
    "        p <= m2.bounds_max[i] + m2.cell, 'vertex outside reported "
    "bounds')\n"
    "  end\n"
    "end\n"
    "-- mirror produced both capsules: some vertex has x < -0.3\n"
    "local mirrored = false\n"
    "for v = 0, m2.vert_count - 1 do\n"
    "  if m2.positions[v * 3 + 1] < -0.3 then mirrored = true end\n"
    "end\n"
    "check(mirrored, 'mirror_x did not mirror')\n"
    "-- scale: sphere r=0.25 scaled by 2 == sphere r=0.5 bounds\n"
    "local m3 = sdf_mesh({version = 1, root = {op = 'scale', s = 2,\n"
    "    c = {op = 'sphere', r = 0.25}}}, 24)\n"
    "check(math.abs(m3.bounds_max[1] - 0.5) < 1e-5, 'scale bounds wrong')\n"
    "-- paint: default material, innermost wins, cut face takes cutter's\n"
    "check(#m.colors == m.vert_count * 3, 'colors length mismatch')\n"
    "check(#m.metal_rough == m.vert_count * 2, 'metal_rough length mismatch')\n"
    "check(math.abs(m.colors[1] - 0.8) < 1e-5, 'default albedo wrong')\n"
    "local pt = {version = 1, root = {op = 'ssub', k = 0.05,\n"
    "  a = {op = 'paint', cr = 1, cg = 0, cb = 0,\n"
    "       c = {op = 'sphere', r = 0.5}},\n"
    "  b = {op = 'paint', cr = 0, cg = 0, cb = 1, metallic = 1,\n"
    "       roughness = 0.1,\n"
    "       c = {op = 'move', x = 0, y = 0, z = -0.5,\n"
    "            c = {op = 'sphere', r = 0.15}}}}}\n"
    "local m4 = sdf_mesh(pt, 40)\n"
    "check(#m4.colors == m4.vert_count * 3, 'paint colors length mismatch')\n"
    "local reds, blues = 0, 0\n"
    "for v = 0, m4.vert_count - 1 do\n"
    "  local cr = m4.colors[v * 3 + 1]\n"
    "  local cb = m4.colors[v * 3 + 3]\n"
    "  if cr > 0.9 and cb < 0.1 then reds = reds + 1 end\n"
    "  if cb > 0.9 and cr < 0.1 then\n"
    "    blues = blues + 1\n"
    "    check(m4.metal_rough[v * 2 + 1] > 0.9, 'cutter metallic not baked')\n"
    "  end\n"
    "end\n"
    "check(reds > 0, 'painted body color missing')\n"
    "check(blues > 0, 'cut face did not take cutter material')\n"
    "-- invalid trees must error\n"
    "local ok1 = pcall(sdf_mesh, {version = 1, root = {op = 'blob'}}, 16)\n"
    "check(not ok1, 'unknown op did not error')\n"
    "local ok2 = pcall(sdf_mesh, {version = 2,\n"
    "    root = {op = 'sphere', r = 1}}, 16)\n"
    "check(not ok2, 'bad version did not error')\n"
    "local ok3 = pcall(sdf_mesh, {version = 1, root = {op = 'sphere'}}, 16)\n"
    "check(not ok3, 'missing field did not error')\n"
    "local ok4 = pcall(sdf_mesh, {version = 1,\n"
    "    root = {op = 'smin', k = 0.1, a = {op = 'sphere', r = 1}}}, 16)\n"
    "check(not ok4, 'missing child did not error')\n";

int main(void) {
  lua_State *L = luaL_newstate();
  if (!L) {
    fprintf(stderr, "luaL_newstate failed\n");
    return 1;
  }
  luaL_openlibs(L);
  lua_pushcfunction(L, lub_sdf_mesh);
  lua_setglobal(L, "sdf_mesh");

  if (luaL_dostring(L, SCRIPT) != LUA_OK) {
    fprintf(stderr, "sdf smoke FAILED: %s\n", lua_tostring(L, -1));
    lua_close(L);
    return 1;
  }
  lua_close(L);
  printf("sdf smoke OK\n");
  return 0;
}
