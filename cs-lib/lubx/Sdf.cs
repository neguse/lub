// Sdf of the implementation library lubx.
// Holds a typed tree (op / params / child references), and Sdf.Mesh flattens it into the C API's flat node array (SdfNodeDesc of Lub.Mesh.SdfMesh).
using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>Node of an SDF tree.
/// Params is the per-op number list (same order as SdfNodeDesc), C is the child of a 1-child op, A / B are the children of a 2-child op.
/// Builder methods always return a new node (immutable).
/// Subtrees may be shared.
/// SdfPanel rewrites Params in place.
/// </summary>
public class SdfNode
{
    public Lub.Mesh.SdfOp Op;
    public List<float> Params;
    public string? Name;
    public SdfNode? C;
    public SdfNode? A;
    public SdfNode? B;

    public SdfNode(Lub.Mesh.SdfOp op, List<float> parameters)
    {
        this.Op = op;
        this.Params = parameters;
    }

    static SdfNode Unary(Lub.Mesh.SdfOp op, List<float> parameters, SdfNode c)
    {
        var n = new SdfNode(op, parameters);
        n.C = c;
        return n;
    }

    static SdfNode Binary(Lub.Mesh.SdfOp op, List<float> parameters, SdfNode a,
        SdfNode b)
    {
        var n = new SdfNode(op, parameters);
        n.A = a;
        n.B = b;
        return n;
    }

    public SdfNode Move(float x, float y, float z) =>
        Unary(Lub.Mesh.SdfOp.Move, new List<float> { x, y, z }, this);

    /// <summary>Rotates by `rad` radians around `axis`.
    /// </summary>
    public SdfNode Rotate(Vec3 axis, float rad)
    {
        var q = Quat.FromAxisAngle(axis, rad);
        return Unary(Lub.Mesh.SdfOp.Rotate, new List<float> { q.X, q.Y, q.Z, q.W },
            this);
    }

    /// <summary>Uniform scale (`s` &gt; 0).
    /// </summary>
    public SdfNode Scale(float s) =>
        Unary(Lub.Mesh.SdfOp.Scale, new List<float> { s }, this);

    /// <summary>X symmetry (|x| fold).
    /// </summary>
    public SdfNode MirrorX() =>
        Unary(Lub.Mesh.SdfOp.MirrorX, new List<float>(), this);

    /// <summary>Y symmetry (|y| fold).
    /// </summary>
    public SdfNode MirrorY() =>
        Unary(Lub.Mesh.SdfOp.MirrorY, new List<float>(), this);

    /// <summary>Z symmetry (|z| fold).
    /// </summary>
    public SdfNode MirrorZ() =>
        Unary(Lub.Mesh.SdfOp.MirrorZ, new List<float>(), this);

    /// <summary>Cuts open at the origin and stretches by `hx` / `hy` / `hz` along each axis (the gap extends the cross section as is).
    /// Makes a rounded box from a sphere, a stadium shape from a torus, etc.
    /// </summary>
    public SdfNode Elongate(float hx, float hy, float hz) =>
        Unary(Lub.Mesh.SdfOp.Elongate, new List<float> { hx, hy, hz }, this);

    /// <summary>Twists around the Y axis (`k` radians per unit height).
    /// The distance is no longer exact (strong twisting roughens the surface).
    /// </summary>
    public SdfNode Twist(float k) =>
        Unary(Lub.Mesh.SdfOp.Twist, new List<float> { k }, this);

    /// <summary>Bends toward +Y along the X axis (`k` radians per unit length).
    /// The distance is no longer exact (strong bending roughens the surface).
    /// </summary>
    public SdfNode Bend(float k) =>
        Unary(Lub.Mesh.SdfOp.Bend, new List<float> { k }, this);

    /// <summary>Repeats at spacing `sx` / `sy` / `sz`, `nx` / `ny` / `nz` copies on each side of each axis (2n+1 including the one at the origin).
    /// Axes with spacing 0 are not repeated.
    /// Size the child to fit within half the spacing (if it sticks out, seams with its neighbors appear).
    /// </summary>
    public SdfNode Repeat(float sx, float sy, float sz, int nx, int ny, int nz) =>
        Unary(Lub.Mesh.SdfOp.Repeat,
            new List<float> { sx, sy, sz, nx, ny, nz }, this);

    /// <summary>Repeats `n` copies around the Y axis (the first is the child itself on the +X side).
    /// Place the child on the +X side and size it so it does not stick into the neighboring sectors (360/n degrees).
    /// Used for petals, gear teeth, rays, etc.
    /// </summary>
    public SdfNode PolarRepeat(int n) =>
        Unary(Lub.Mesh.SdfOp.PolarRepeat, new List<float> { n }, this);

    /// <summary>Solid of revolution (lathe) made by offsetting the child's z = 0 cross section (XY plane) by `o` in X and rotating it around the Y axis.
    /// Place the cross section on the x &gt;= -o side.
    /// Makes pots, cups, bottles, donut shapes, etc. from a cross section.
    /// </summary>
    public SdfNode Revolve(float o) =>
        Unary(Lub.Mesh.SdfOp.Revolve, new List<float> { o }, this);

    /// <summary>Extrudes the child's z = 0 cross section (XY plane) in Z with half thickness `h` (edges stay sharp; round them with Round).
    /// </summary>
    public SdfNode Extrude(float h) =>
        Unary(Lub.Mesh.SdfOp.Extrude, new List<float> { h }, this);

    /// <summary>Makes the surface bumpy by at most `amp` with fbm (`octaves` layers of value noise, 1 to 8).
    /// `freq` is the fineness of the first layer (waves per unit length), and changing `seed` changes the pattern.
    /// Rocks, clouds, trunks, etc.
    /// The distance is no longer exact (making amp large relative to freq roughens the surface).
    /// </summary>
    public SdfNode Displace(float amp, float freq, int octaves, int seed) =>
        Unary(Lub.Mesh.SdfOp.Displace,
            new List<float> { amp, freq, octaves, seed }, this);

    /// <summary>Inflates the surface outward by `r` and rounds the corners (carves inward if negative).
    /// </summary>
    public SdfNode Round(float r) =>
        Unary(Lub.Mesh.SdfOp.Round, new List<float> { r }, this);

    /// <summary>Turns the surface into a shell of thickness `t` (hollow inside, visible when cut with Subtract etc.).
    /// </summary>
    public SdfNode Onion(float t) =>
        Unary(Lub.Mesh.SdfOp.Onion, new List<float> { t }, this);

    /// <summary>Gives the subtree a material (albedo + metallic/roughness).
    /// The innermost paint wins.
    /// `rgb` is 0xRRGGBB (metallic defaults to 0.0, roughness to 0.8).
    /// With smin/ssub the materials blend with the same blend as the distance, and the cut surfaces of subtract/ssub show the cutter's material.
    /// </summary>
    public SdfNode Paint(int rgb, float? metallic = null,
        float? roughness = null) =>
        Unary(Lub.Mesh.SdfOp.Paint, new List<float>
        {
            ((rgb >> 16) & 0xFF) / 255.0f,
            ((rgb >> 8) & 0xFF) / 255.0f,
            (rgb & 0xFF) / 255.0f,
            metallic ?? 0.0f,
            roughness ?? 0.8f,
        }, this);

    public SdfNode Union(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Union, new List<float>(), this, b);

    /// <summary>smooth union.
    /// `k` is the blend width.
    /// </summary>
    public SdfNode Smin(SdfNode b, float k) =>
        Binary(Lub.Mesh.SdfOp.Smin, new List<float> { k }, this, b);

    /// <summary>Carves out `b`.
    /// </summary>
    public SdfNode Subtract(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Subtract, new List<float>(), this, b);

    /// <summary>smooth subtraction.
    /// `k` is the edge rounding width.
    /// </summary>
    public SdfNode Ssub(SdfNode b, float k) =>
        Binary(Lub.Mesh.SdfOp.Ssub, new List<float> { k }, this, b);

    public SdfNode Intersect(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Intersect, new List<float>(), this, b);

    /// <summary>smooth intersection.
    /// `k` is the edge rounding width.
    /// </summary>
    public SdfNode Smax(SdfNode b, float k) =>
        Binary(Lub.Mesh.SdfOp.Smax, new List<float> { k }, this, b);

    /// <summary>Turns the seam of the union into a 45-degree chamfer of width `r` (opUnionChamfer of hg_sdf).
    /// </summary>
    public SdfNode UnionChamfer(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.UnionChamfer, new List<float> { r }, this, b);

    /// <summary>Chamfers the corners of the intersection with width `r`.
    /// </summary>
    public SdfNode IntersectChamfer(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.IntersectChamfer, new List<float> { r }, this, b);

    /// <summary>Chamfers the edge left by carving out `b` with width `r`.
    /// </summary>
    public SdfNode SubtractChamfer(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.SubtractChamfer, new List<float> { r }, this, b);

    /// <summary>Turns the seam of the union into a staircase of `n` steps with width `r`.
    /// </summary>
    public SdfNode UnionStairs(SdfNode b, float r, int n) =>
        Binary(Lub.Mesh.SdfOp.UnionStairs, new List<float> { r, n }, this, b);

    /// <summary>Carves a groove of depth `ra` and half-width `rb` into itself along the surface of `b`.
    /// </summary>
    public SdfNode Groove(SdfNode b, float ra, float rb) =>
        Binary(Lub.Mesh.SdfOp.Groove, new List<float> { ra, rb }, this, b);

    /// <summary>Raises a band of height `ra` and half-width `rb` from itself along the surface of `b` (the inverse of Groove).
    /// </summary>
    public SdfNode Tongue(SdfNode b, float ra, float rb) =>
        Binary(Lub.Mesh.SdfOp.Tongue, new List<float> { ra, rb }, this, b);

    /// <summary>Pipe of radius `r` along the line where its own surface and the surface of `b` intersect.
    /// </summary>
    public SdfNode Pipe(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.Pipe, new List<float> { r }, this, b);

    /// <summary>Engraves a V-shaped groove of depth `r` into itself along the surface of `b`.
    /// </summary>
    public SdfNode Engrave(SdfNode b, float r) =>
        Binary(Lub.Mesh.SdfOp.Engrave, new List<float> { r }, this, b);

    /// <summary>Parts contained in only one of the two (the union with the overlap carved out).
    /// </summary>
    public SdfNode Xor(SdfNode b) =>
        Binary(Lub.Mesh.SdfOp.Xor, new List<float>(), this, b);

    /// <summary>Declares the subtree as a skinning part.
    /// `pivot` is the joint position (model space).
    /// When meshing, weights are baked from the per-vertex distance to each part.
    /// Inside mirror_x the weights apply to both sides but the pivot is on one side, so place bones that move outside the mirror, one by one.
    /// </summary>
    public SdfNode Bone(string name, Vec3 pivot)
    {
        var n = Unary(Lub.Mesh.SdfOp.Bone,
            new List<float> { pivot.X, pivot.Y, pivot.Z }, this);
        n.Name = name;
        return n;
    }
}

/// <summary>Builder for SDF modeling.
/// Create primitives, transform and combine them with `SdfNode` method chains, and mesh them with `Sdf.Mesh`:
/// <code>
/// var body = Sdf.Sphere(0.72).Move(0, -0.42, 0);
/// var head = Sdf.Sphere(0.46).Move(0, 0.48, 0);
/// var mesh = Sdf.Mesh(body.Smin(head, 0.22), 64);
/// </code></summary>
public static class Sdf
{
    public static SdfNode Sphere(float r) =>
        new SdfNode(Lub.Mesh.SdfOp.Sphere, new List<float> { r });

    /// <summary>Box with half extents.
    /// </summary>
    public static SdfNode Box(float hx, float hy, float hz) =>
        new SdfNode(Lub.Mesh.SdfOp.Box, new List<float> { hx, hy, hz });

    /// <summary>Capsule of radius `r` with the segment `a`-`b` as its axis.
    /// </summary>
    public static SdfNode Capsule(Vec3 a, Vec3 b, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.Capsule,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, r });

    /// <summary>Capsule with the segment `a`-`b` as its axis whose radius changes linearly from `ra` at `a` to `rb` at `b` (convex hull of 2 spheres).
    /// Used for tapering horns, tails, fingers, etc.
    /// Lining them up along a polyline with matching end radii gives a smooth tube with no steps at the joints.
    /// </summary>
    public static SdfNode RoundCone(Vec3 a, Vec3 b, float ra, float rb) =>
        new SdfNode(Lub.Mesh.SdfOp.RoundCone,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, ra, rb });

    /// <summary>Torus lying in the XZ plane.
    /// </summary>
    public static SdfNode Torus(float rMajor, float rMinor) =>
        new SdfNode(Lub.Mesh.SdfOp.Torus, new List<float> { rMajor, rMinor });

    /// <summary>Box with half extents whose edges and corners are rounded with radius `r` (the outer shape stays at half extents; `r` is between 0 and half extents).
    /// </summary>
    public static SdfNode RoundBox(float hx, float hy, float hz, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.RoundBox, new List<float> { hx, hy, hz, r });

    /// <summary>Frame that keeps only the 12 edges of a box with half extents, as square bars of thickness (half-width) `e`.
    /// </summary>
    public static SdfNode BoxFrame(float hx, float hy, float hz, float e) =>
        new SdfNode(Lub.Mesh.SdfOp.BoxFrame, new List<float> { hx, hy, hz, e });

    /// <summary>Ellipsoid with radii `rx` / `ry` / `rz` along each axis.
    /// The distance is approximate (the surface position is exact).
    /// </summary>
    public static SdfNode Ellipsoid(float rx, float ry, float rz) =>
        new SdfNode(Lub.Mesh.SdfOp.Ellipsoid, new List<float> { rx, ry, rz });

    /// <summary>Flat-ended cylinder of radius `r` with the segment `a`-`b` as its axis.
    /// </summary>
    public static SdfNode CappedCylinder(Vec3 a, Vec3 b, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.CappedCylinder,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, r });

    /// <summary>Flat-ended truncated cone with the segment `a`-`b` as its axis whose radius changes from `ra` at `a` to `rb` at `b` (a cone when `rb` = 0).
    /// </summary>
    public static SdfNode CappedCone(Vec3 a, Vec3 b, float ra, float rb) =>
        new SdfNode(Lub.Mesh.SdfOp.CappedCone,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, ra, rb });

    /// <summary>Cylinder along the Y axis with radius `r` and half height `h`, with its top and bottom rims rounded with radius `e` (the outer shape stays r and h).
    /// </summary>
    public static SdfNode RoundedCylinder(float r, float h, float e) =>
        new SdfNode(Lub.Mesh.SdfOp.RoundedCylinder, new List<float> { r, h, e });

    /// <summary>Arc of a torus in the XY plane (facing front).
    /// Opens `angle` radians to each side around +Y (π for the full circle).
    /// For a smiling mouth etc., rotate it by π around the Z axis to face downward.
    /// </summary>
    public static SdfNode CappedTorus(float angle, float rMajor, float rMinor) =>
        new SdfNode(Lub.Mesh.SdfOp.CappedTorus,
            new List<float> { angle, rMajor, rMinor });

    /// <summary>Chain link: a ring in the XY plane (centerline radius `rMajor`, thickness `rMinor`) stretched by `le` in the Y direction.
    /// </summary>
    public static SdfNode Link(float le, float rMajor, float rMinor) =>
        new SdfNode(Lub.Mesh.SdfOp.Link, new List<float> { le, rMajor, rMinor });

    /// <summary>Hexagonal prism along the Z axis.
    /// `h` is the distance from the center to the flat top and bottom edges, `depth` is half the depth.
    /// </summary>
    public static SdfNode HexPrism(float h, float depth) =>
        new SdfNode(Lub.Mesh.SdfOp.HexPrism, new List<float> { h, depth });

    /// <summary>Equilateral triangular prism along the Z axis (apex up).
    /// The base is at y = -h/2, the apex at y = h, and `depth` is half the depth.
    /// The distance is approximate (the surface position is exact).
    /// </summary>
    public static SdfNode TriPrism(float h, float depth) =>
        new SdfNode(Lub.Mesh.SdfOp.TriPrism, new List<float> { h, depth });

    /// <summary>Regular octahedron with distance `s` from the center to each of its 6 vertices.
    /// </summary>
    public static SdfNode Octahedron(float s) =>
        new SdfNode(Lub.Mesh.SdfOp.Octahedron, new List<float> { s });

    /// <summary>Square pyramid: the base is a square of half-width `w` at y = 0, and the apex is at (0, `h`, 0).
    /// </summary>
    public static SdfNode Pyramid(float w, float h) =>
        new SdfNode(Lub.Mesh.SdfOp.Pyramid, new List<float> { w, h });

    /// <summary>The part of a sphere of radius `r` above the plane y = `h` (-r &lt; h &lt; r).
    /// </summary>
    public static SdfNode CutSphere(float r, float h) =>
        new SdfNode(Lub.Mesh.SdfOp.CutSphere, new List<float> { r, h });

    /// <summary>The sector of a sphere of radius `r` within `angle` radians of the +Y axis (a cone with its apex at the origin, capped by the sphere).
    /// </summary>
    public static SdfNode SolidAngle(float angle, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.SolidAngle, new List<float> { angle, r });

    /// <summary>Rhombus in the XZ plane (half-diagonals `la` in x and `lb` in z) with corners rounded by radius `r`, extruded in Y with half thickness `h`.
    /// </summary>
    public static SdfNode Rhombus(float la, float lb, float h, float r) =>
        new SdfNode(Lub.Mesh.SdfOp.Rhombus, new List<float> { la, lb, h, r });

    /// <summary>Lens with `a` and `b` as its pointed ends and radius `w` at the middle (2 arcs revolved around the axis).
    /// Used for leaves, petals, eyes, etc.
    /// </summary>
    public static SdfNode Vesica(Vec3 a, Vec3 b, float w) =>
        new SdfNode(Lub.Mesh.SdfOp.Vesica,
            new List<float> { a.X, a.Y, a.Z, b.X, b.Y, b.Z, w });

    /// <summary>Display name of the op (for SdfPanel labels).
    /// </summary>
    public static string OpName(Lub.Mesh.SdfOp op)
    {
        switch (op)
        {
            case Lub.Mesh.SdfOp.Sphere: return "sphere";
            case Lub.Mesh.SdfOp.Box: return "box";
            case Lub.Mesh.SdfOp.Capsule: return "capsule";
            case Lub.Mesh.SdfOp.Torus: return "torus";
            case Lub.Mesh.SdfOp.RoundCone: return "round_cone";
            case Lub.Mesh.SdfOp.RoundBox: return "round_box";
            case Lub.Mesh.SdfOp.BoxFrame: return "box_frame";
            case Lub.Mesh.SdfOp.Ellipsoid: return "ellipsoid";
            case Lub.Mesh.SdfOp.CappedCylinder: return "capped_cylinder";
            case Lub.Mesh.SdfOp.CappedCone: return "capped_cone";
            case Lub.Mesh.SdfOp.RoundedCylinder: return "rounded_cylinder";
            case Lub.Mesh.SdfOp.CappedTorus: return "capped_torus";
            case Lub.Mesh.SdfOp.Link: return "link";
            case Lub.Mesh.SdfOp.HexPrism: return "hex_prism";
            case Lub.Mesh.SdfOp.TriPrism: return "tri_prism";
            case Lub.Mesh.SdfOp.Octahedron: return "octahedron";
            case Lub.Mesh.SdfOp.Pyramid: return "pyramid";
            case Lub.Mesh.SdfOp.CutSphere: return "cut_sphere";
            case Lub.Mesh.SdfOp.SolidAngle: return "solid_angle";
            case Lub.Mesh.SdfOp.Rhombus: return "rhombus";
            case Lub.Mesh.SdfOp.Vesica: return "vesica";
            case Lub.Mesh.SdfOp.Move: return "move";
            case Lub.Mesh.SdfOp.Rotate: return "rotate";
            case Lub.Mesh.SdfOp.Scale: return "scale";
            case Lub.Mesh.SdfOp.MirrorX: return "mirror_x";
            case Lub.Mesh.SdfOp.MirrorY: return "mirror_y";
            case Lub.Mesh.SdfOp.MirrorZ: return "mirror_z";
            case Lub.Mesh.SdfOp.Elongate: return "elongate";
            case Lub.Mesh.SdfOp.Twist: return "twist";
            case Lub.Mesh.SdfOp.Bend: return "bend";
            case Lub.Mesh.SdfOp.Repeat: return "repeat";
            case Lub.Mesh.SdfOp.PolarRepeat: return "polar_repeat";
            case Lub.Mesh.SdfOp.Revolve: return "revolve";
            case Lub.Mesh.SdfOp.Extrude: return "extrude";
            case Lub.Mesh.SdfOp.Displace: return "displace";
            case Lub.Mesh.SdfOp.Round: return "round";
            case Lub.Mesh.SdfOp.Onion: return "onion";
            case Lub.Mesh.SdfOp.Paint: return "paint";
            case Lub.Mesh.SdfOp.Bone: return "bone";
            case Lub.Mesh.SdfOp.Union: return "union";
            case Lub.Mesh.SdfOp.Smin: return "smin";
            case Lub.Mesh.SdfOp.Subtract: return "subtract";
            case Lub.Mesh.SdfOp.Ssub: return "ssub";
            case Lub.Mesh.SdfOp.Intersect: return "intersect";
            case Lub.Mesh.SdfOp.Smax: return "smax";
            case Lub.Mesh.SdfOp.Xor: return "xor";
            case Lub.Mesh.SdfOp.UnionChamfer: return "union_chamfer";
            case Lub.Mesh.SdfOp.IntersectChamfer: return "intersect_chamfer";
            case Lub.Mesh.SdfOp.SubtractChamfer: return "subtract_chamfer";
            case Lub.Mesh.SdfOp.UnionStairs: return "union_stairs";
            case Lub.Mesh.SdfOp.Groove: return "groove";
            case Lub.Mesh.SdfOp.Tongue: return "tongue";
            case Lub.Mesh.SdfOp.Pipe: return "pipe";
            case Lub.Mesh.SdfOp.Engrave: return "engrave";
            default: return "unknown";
        }
    }

    // Flatten the tree into a flat array in post-order and return the index of that node.
    static int Flatten(SdfNode node, List<SdfNodeDesc> nodes)
    {
        var d = new SdfNodeDesc();
        d.Op = node.Op;
        d.Params = node.Params;
        if (node.Name != null)
            d.Name = node.Name;
        if (node.C != null)
            d.A = Flatten(node.C, nodes);
        if (node.A != null)
            d.A = Flatten(node.A, nodes);
        if (node.B != null)
            d.B = Flatten(node.B, nodes);
        nodes.Add(d);
        return nodes.Count - 1;
    }

    /// <summary>Meshes the tree.
    /// `n` is the number of cells along the longest axis (bounds are automatic).
    /// If there are bone nodes, skinning information is baked too (`skinK` = weight blend width).
    /// </summary>
    public static MeshData Mesh(SdfNode root, int n, float? skinK = null)
    {
        var nodes = new List<SdfNodeDesc>();
        var r = Flatten(root, nodes);
        return Lub.Mesh.SdfMesh(nodes, r, n, skinK);
    }
}
