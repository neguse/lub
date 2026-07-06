package lubx;

import lub.Math;

/** 手続き 3D プリミティブの頂点生成。interleaved
	pos.xyz + normal.xyz + color.rgba (STRIDE=10) を out に push する。
	Gfx.useBuffer(Gfx.VERTEX) にそのまま渡せる。 **/
class Shapes {
	public static inline var STRIDE:Int = 10; // pos.xyz + normal.xyz + color.rgba

	/** 頂点1つ。 **/
	public static inline function vertex(out:Array<Float>, x:Float, y:Float, z:Float, nx:Float, ny:Float, nz:Float, col:Array<Float>):Void {
		out.push(x);
		out.push(y);
		out.push(z);
		out.push(nx);
		out.push(ny);
		out.push(nz);
		out.push(col[0]);
		out.push(col[1]);
		out.push(col[2]);
		out.push(col[3]);
	}

	/** 三角形 (a, b, c は [x,y,z]、n は [nx,ny,nz])。 **/
	public static function tri(out:Array<Float>, a:Array<Float>, b:Array<Float>, c:Array<Float>, n:Array<Float>, col:Array<Float>):Void {
		vertex(out, a[0], a[1], a[2], n[0], n[1], n[2], col);
		vertex(out, b[0], b[1], b[2], n[0], n[1], n[2], col);
		vertex(out, c[0], c[1], c[2], n[0], n[1], n[2], col);
	}

	/** 四角形 = tri(a,b,c) + tri(a,c,d)。 **/
	public static function quad(out:Array<Float>, a:Array<Float>, b:Array<Float>, c:Array<Float>, d:Array<Float>, n:Array<Float>, col:Array<Float>):Void {
		tri(out, a, b, c, n, col);
		tri(out, a, c, d, n, col);
	}

	/** 中心 (cx,cy,cz)、辺長 (sx,sy,sz) の直方体。面の順序・法線は Shadow11.addBox と同一。 **/
	public static function box(out:Array<Float>, cx:Float, cy:Float, cz:Float, sx:Float, sy:Float, sz:Float, col:Array<Float>):Void {
		var x0 = cx - sx * 0.5;
		var x1 = cx + sx * 0.5;
		var y0 = cy - sy * 0.5;
		var y1 = cy + sy * 0.5;
		var z0 = cz - sz * 0.5;
		var z1 = cz + sz * 0.5;

		var p000:Array<Float> = [x0, y0, z0];
		var p100:Array<Float> = [x1, y0, z0];
		var p010:Array<Float> = [x0, y1, z0];
		var p110:Array<Float> = [x1, y1, z0];
		var p001:Array<Float> = [x0, y0, z1];
		var p101:Array<Float> = [x1, y0, z1];
		var p011:Array<Float> = [x0, y1, z1];
		var p111:Array<Float> = [x1, y1, z1];

		quad(out, p000, p010, p110, p100, [0, 0, -1], col);
		quad(out, p001, p101, p111, p011, [0, 0, 1], col);
		quad(out, p000, p001, p011, p010, [-1, 0, 0], col);
		quad(out, p100, p110, p111, p101, [1, 0, 0], col);
		quad(out, p010, p011, p111, p110, [0, 1, 0], col);
		quad(out, p000, p100, p101, p001, [0, -1, 0], col);
	}

	static function spherePoint(cx:Float, cy:Float, cz:Float, r:Float, u:Float, vv:Float):Array<Float> {
		var cv = Math.cos(vv);
		var nx = Math.cos(u) * cv;
		var ny = Math.sin(vv);
		var nz = Math.sin(u) * cv;
		return [cx + nx * r, cy + ny * r, cz + nz * r, nx, ny, nz];
	}

	/** UV 球。rings/segs のデフォルトと頂点順は Shadow11.addSphere と同一 (rings=12, segs=24)。 **/
	public static function sphere(out:Array<Float>, cx:Float, cy:Float, cz:Float, r:Float, col:Array<Float>, rings:Int = 12, segs:Int = 24):Void {
		for (ring in 0...rings) {
			var v0 = -Math.PI * 0.5 + ring / rings * Math.PI;
			var v1 = -Math.PI * 0.5 + (ring + 1) / rings * Math.PI;
			for (seg in 0...segs) {
				var u0 = seg / segs * Math.PI * 2;
				var u1 = (seg + 1) / segs * Math.PI * 2;
				var a = spherePoint(cx, cy, cz, r, u0, v0);
				var b = spherePoint(cx, cy, cz, r, u1, v0);
				var c = spherePoint(cx, cy, cz, r, u1, v1);
				var d = spherePoint(cx, cy, cz, r, u0, v1);
				vertex(out, a[0], a[1], a[2], a[3], a[4], a[5], col);
				vertex(out, b[0], b[1], b[2], b[3], b[4], b[5], col);
				vertex(out, c[0], c[1], c[2], c[3], c[4], c[5], col);
				vertex(out, a[0], a[1], a[2], a[3], a[4], a[5], col);
				vertex(out, c[0], c[1], c[2], c[3], c[4], c[5], col);
				vertex(out, d[0], d[1], d[2], d[3], d[4], d[5], col);
			}
		}
	}
}
