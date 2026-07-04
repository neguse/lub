#ifndef LUB_SDF_H
#define LUB_SDF_H

// SDF tree -> triangle mesh (design: docs/log/2026-07-04-sdf-tree-design.md).
//
//   sdf_mesh(tree, n) -> mesh
//
// tree = { version = 1, root = <node> }, a plain data tree with no function
// references. Node vocabulary (fields in parens):
//   prim:    sphere(r) box(hx hy hz) capsule(ax ay az bx by bz r)
//            torus(rmajor rminor)
//   xform:   move(x y z c) rotate(qx qy qz qw c) scale(s c)   -- c = child
//   combine: union(a b) smin(k a b) subtract(a b) ssub(k a b) intersect(a b)
//   misc:    mirror_x(c)
// Any node may carry a string field `name` (reserved, ignored here).
// Unknown ops / missing fields / bad version raise a Lua error.
//
// n = cell count of the longest axis; bounds are derived from a conservative
// per-node AABB with a one-cell margin, cells stay cubic. Returns the
// surface_nets mesh table (src/surfacenets.h) plus bounds_min/bounds_max
// (flat {x,y,z} arrays) and cell.
struct lua_State;

int lub_sdf_mesh(struct lua_State *L);

#endif
