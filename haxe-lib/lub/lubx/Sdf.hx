package lubx;

import lub.Math.Quat;
import lub.Math.Vec3;
import lub.Mesh;

/**
	SDF ツリーのノード。中身は `sdf_mesh` (C) が読む素の data table
	(関数参照なし・直列化可能。schema は `src/sdf.h` 参照)。

	メソッドは常に新しいノードを返す (イミュータブル)。部分ツリーは
	共有してよい。検証は C 側 flatten が一手に引き受け、不正なツリーは
	その場で Lua エラーになる。
**/
abstract SdfNode(Dynamic) from Dynamic to Dynamic {
	public inline function move(x:Float, y:Float, z:Float):SdfNode
		return {
			op: "move",
			x: x,
			y: y,
			z: z,
			c: this
		};

	/** `axis` 回りに `rad` ラジアン回す。 **/
	public inline function rotate(axis:Vec3, rad:Float):SdfNode {
		var q:{
			x:Float,
			y:Float,
			z:Float,
			w:Float
		} = Quat.fromAxisAngle(axis, rad);
		return {
			op: "rotate",
			qx: q.x,
			qy: q.y,
			qz: q.z,
			qw: q.w,
			c: this
		};
	}

	/** uniform スケール (`s` > 0)。 **/
	public inline function scale(s:Float):SdfNode
		return {op: "scale", s: s, c: this};

	/** X 対称 (|x| 折り畳み)。 **/
	public inline function mirrorX():SdfNode
		return {op: "mirror_x", c: this};

	public inline function union(b:SdfNode):SdfNode
		return {op: "union", a: this, b: b};

	/** smooth union。`k` が blend 幅。 **/
	public inline function smin(b:SdfNode, k:Float):SdfNode
		return {
			op: "smin",
			k: k,
			a: this,
			b: b
		};

	/** `b` をくり抜く。 **/
	public inline function subtract(b:SdfNode):SdfNode
		return {op: "subtract", a: this, b: b};

	/** smooth subtraction。`k` が縁の丸まり幅。 **/
	public inline function ssub(b:SdfNode, k:Float):SdfNode
		return {
			op: "ssub",
			k: k,
			a: this,
			b: b
		};

	public inline function intersect(b:SdfNode):SdfNode
		return {op: "intersect", a: this, b: b};
}

/**
	SDF モデリングの builder。プリミティブを作り、`SdfNode` のメソッド
	チェーンで変形・合成し、`Sdf.mesh` でメッシュ化する:

	```haxe
	var body = Sdf.sphere(0.72).move(0, -0.42, 0);
	var head = Sdf.sphere(0.46).move(0, 0.48, 0);
	var mesh = Sdf.mesh(body.smin(head, 0.22), 64);
	```
**/
class Sdf {
	public static inline function sphere(r:Float):SdfNode
		return {op: "sphere", r: r};

	/** half extents の直方体。 **/
	public static inline function box(hx:Float, hy:Float, hz:Float):SdfNode
		return {
			op: "box",
			hx: hx,
			hy: hy,
			hz: hz
		};

	/** 線分 `a`-`b` を軸とする半径 `r` のカプセル。 **/
	public static inline function capsule(a:Vec3, b:Vec3, r:Float):SdfNode
		return {
			op: "capsule",
			ax: a.x,
			ay: a.y,
			az: a.z,
			bx: b.x,
			by: b.y,
			bz: b.z,
			r: r
		};

	/** XZ 平面に寝たトーラス。 **/
	public static inline function torus(rMajor:Float, rMinor:Float):SdfNode
		return {op: "torus", rmajor: rMajor, rminor: rMinor};

	/** ツリーをメッシュ化する。`n` は最長軸の cell 数 (bounds は自動)。 **/
	public static inline function mesh(root:SdfNode, n:Int):MeshData
		return Mesh.sdfMesh({version: 1, root: root}, n);
}
