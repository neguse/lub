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
//   misc:    mirror_x(c) paint(cr cg cb ?metallic ?roughness c)
//            bone(name px py pz c)  -- skinning part + pivot (model space)
// Any node may carry a string field `name` (reserved elsewhere).
// Unknown ops / missing fields / bad version raise a Lua error.
//
//   sdf_mesh(tree, n [, skin_k])
//
// When the tree contains bone nodes the mesh also gets per-vertex `joints`
// (2 per vertex, 0-based bone indices) + `weights` (2 per vertex, sum 1)
// from a softmax over part distances with blend width skin_k (default 0.1),
// plus `bones` = { {name, x, y, z (pivot)}, ... }. A bone under mirror_x
// weights both sides but has a single pivot — keep animated bones outside
// mirror_x.
//
// n = cell count of the longest axis; bounds are derived from a conservative
// per-node AABB with a one-cell margin, cells stay cubic. Returns the
// surface_nets mesh table (src/surfacenets.h) plus bounds_min/bounds_max
// (flat {x,y,z} arrays) and cell.
struct lua_State;

int lub_sdf_mesh(struct lua_State *L);

#endif
