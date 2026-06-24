package lub;

// このモジュール名 lub.Math が標準の Math クラスを覆い隠すため、
// 無修飾の `Math.sqrt` 等が解決できるよう明示的に取り込む。
// (Haxe 4 は暗黙に std を引くが、Haxe 5 preview はパッケージ内モジュールを優先する)
import Math;

class Vec2 {
	public var x:Float;
	public var y:Float;

	public inline function new(x:Float, y:Float) {
		this.x = x;
		this.y = y;
	}

	public static inline function zero():Vec2
		return new Vec2(0, 0);

	public static inline function one():Vec2
		return new Vec2(1, 1);

	public static inline function splat(v:Float):Vec2
		return new Vec2(v, v);

	public inline function add(b:Vec2):Vec2
		return new Vec2(x + b.x, y + b.y);

	public inline function sub(b:Vec2):Vec2
		return new Vec2(x - b.x, y - b.y);

	public inline function scale(s:Float):Vec2
		return new Vec2(x * s, y * s);

	public inline function negate():Vec2
		return new Vec2(-x, -y);

	public inline function mul(b:Vec2):Vec2
		return new Vec2(x * b.x, y * b.y);

	public inline function div(b:Vec2):Vec2
		return new Vec2(x / b.x, y / b.y);

	public inline function dot(b:Vec2):Float
		return x * b.x + y * b.y;

	public inline function lengthSq():Float
		return x * x + y * y;

	public inline function length():Float
		return Math.sqrt(lengthSq());

	public function normalize():Vec2 {
		var len = length();
		return if (len > 0) new Vec2(x / len, y / len) else zero();
	}

	public inline function distanceSq(b:Vec2):Float
		return sub(b).lengthSq();

	public inline function distance(b:Vec2):Float
		return Math.sqrt(distanceSq(b));

	public inline function lerp(b:Vec2, t:Float):Vec2
		return new Vec2(x + (b.x - x) * t, y + (b.y - y) * t);

	public inline function min(b:Vec2):Vec2
		return new Vec2(Math.min(x, b.x), Math.min(y, b.y));

	public inline function max(b:Vec2):Vec2
		return new Vec2(Math.max(x, b.x), Math.max(y, b.y));

	public function clamp(lo:Vec2, hi:Vec2):Vec2
		return new Vec2(Math.max(lo.x, Math.min(hi.x, x)), Math.max(lo.y, Math.min(hi.y, y)));

	public inline function perp():Vec2
		return new Vec2(-y, x);

	public inline function angle():Float
		return Math.atan2(y, x);
}

class Vec3 {
	public var x:Float;
	public var y:Float;
	public var z:Float;

	public inline function new(x:Float, y:Float, z:Float) {
		this.x = x;
		this.y = y;
		this.z = z;
	}

	public static inline function zero():Vec3
		return new Vec3(0, 0, 0);

	public static inline function one():Vec3
		return new Vec3(1, 1, 1);

	public static inline function splat(v:Float):Vec3
		return new Vec3(v, v, v);

	public static inline function up():Vec3
		return new Vec3(0, 1, 0);

	public static inline function right():Vec3
		return new Vec3(1, 0, 0);

	public static inline function forward():Vec3
		return new Vec3(0, 0, 1);

	public inline function add(b:Vec3):Vec3
		return new Vec3(x + b.x, y + b.y, z + b.z);

	public inline function sub(b:Vec3):Vec3
		return new Vec3(x - b.x, y - b.y, z - b.z);

	public inline function scale(s:Float):Vec3
		return new Vec3(x * s, y * s, z * s);

	public inline function negate():Vec3
		return new Vec3(-x, -y, -z);

	public inline function mul(b:Vec3):Vec3
		return new Vec3(x * b.x, y * b.y, z * b.z);

	public inline function div(b:Vec3):Vec3
		return new Vec3(x / b.x, y / b.y, z / b.z);

	public inline function dot(b:Vec3):Float
		return x * b.x + y * b.y + z * b.z;

	public inline function cross(b:Vec3):Vec3
		return new Vec3(y * b.z - z * b.y, z * b.x - x * b.z, x * b.y - y * b.x);

	public inline function lengthSq():Float
		return x * x + y * y + z * z;

	public inline function length():Float
		return Math.sqrt(lengthSq());

	public function normalize():Vec3 {
		var len = length();
		return if (len > 0) new Vec3(x / len, y / len, z / len) else zero();
	}

	public inline function distanceSq(b:Vec3):Float
		return sub(b).lengthSq();

	public inline function distance(b:Vec3):Float
		return Math.sqrt(distanceSq(b));

	public inline function lerp(b:Vec3, t:Float):Vec3
		return new Vec3(x + (b.x - x) * t, y + (b.y - y) * t, z + (b.z - z) * t);

	public inline function min(b:Vec3):Vec3
		return new Vec3(Math.min(x, b.x), Math.min(y, b.y), Math.min(z, b.z));

	public inline function max(b:Vec3):Vec3
		return new Vec3(Math.max(x, b.x), Math.max(y, b.y), Math.max(z, b.z));

	public function clamp(lo:Vec3, hi:Vec3):Vec3
		return new Vec3(Math.max(lo.x, Math.min(hi.x, x)), Math.max(lo.y, Math.min(hi.y, y)), Math.max(lo.z, Math.min(hi.z, z)));

	public inline function reflect(normal:Vec3):Vec3
		return sub(normal.scale(2.0 * dot(normal)));
}

class Vec4 {
	public var x:Float;
	public var y:Float;
	public var z:Float;
	public var w:Float;

	public inline function new(x:Float, y:Float, z:Float, w:Float) {
		this.x = x;
		this.y = y;
		this.z = z;
		this.w = w;
	}

	public static inline function zero():Vec4
		return new Vec4(0, 0, 0, 0);

	public static inline function one():Vec4
		return new Vec4(1, 1, 1, 1);

	public static inline function fromVec3(v:Vec3, w:Float):Vec4
		return new Vec4(v.x, v.y, v.z, w);

	public inline function add(b:Vec4):Vec4
		return new Vec4(x + b.x, y + b.y, z + b.z, w + b.w);

	public inline function sub(b:Vec4):Vec4
		return new Vec4(x - b.x, y - b.y, z - b.z, w - b.w);

	public inline function scale(s:Float):Vec4
		return new Vec4(x * s, y * s, z * s, w * s);

	public inline function negate():Vec4
		return new Vec4(-x, -y, -z, -w);

	public inline function dot(b:Vec4):Float
		return x * b.x + y * b.y + z * b.z + w * b.w;

	public inline function lengthSq():Float
		return x * x + y * y + z * z + w * w;

	public inline function length():Float
		return Math.sqrt(lengthSq());

	public function normalize():Vec4 {
		var len = length();
		return if (len > 0) new Vec4(x / len, y / len, z / len, w / len) else zero();
	}

	public inline function lerp(b:Vec4, t:Float):Vec4
		return new Vec4(x + (b.x - x) * t, y + (b.y - y) * t, z + (b.z - z) * t, w + (b.w - w) * t);

	public inline function xyz():Vec3
		return new Vec3(x, y, z);
}

class Quat {
	public var x:Float;
	public var y:Float;
	public var z:Float;
	public var w:Float;

	public inline function new(x:Float, y:Float, z:Float, w:Float) {
		this.x = x;
		this.y = y;
		this.z = z;
		this.w = w;
	}

	public static inline function identity():Quat
		return new Quat(0, 0, 0, 1);

	public static function fromAxisAngle(axis:Vec3, angle:Float):Quat {
		var half = angle * 0.5;
		var s = Math.sin(half);
		var n = axis.normalize();
		return new Quat(n.x * s, n.y * s, n.z * s, Math.cos(half));
	}

	public static function fromEuler(yaw:Float, pitch:Float, roll:Float):Quat {
		var cy = Math.cos(yaw * 0.5);
		var sy = Math.sin(yaw * 0.5);
		var cp = Math.cos(pitch * 0.5);
		var sp = Math.sin(pitch * 0.5);
		var cr = Math.cos(roll * 0.5);
		var sr = Math.sin(roll * 0.5);
		return new Quat(sr * cp * cy - cr * sp * sy, cr * sp * cy + sr * cp * sy, cr * cp * sy - sr * sp * cy, cr * cp * cy + sr * sp * sy);
	}

	public inline function mul(b:Quat):Quat
		return new Quat(w * b.x
			+ x * b.w
			+ y * b.z
			- z * b.y, w * b.y
			- x * b.z
			+ y * b.w
			+ z * b.x, w * b.z
			+ x * b.y
			- y * b.x
			+ z * b.w,
			w * b.w
			- x * b.x
			- y * b.y
			- z * b.z);

	public inline function dot(b:Quat):Float
		return x * b.x + y * b.y + z * b.z + w * b.w;

	public inline function lengthSq():Float
		return x * x + y * y + z * z + w * w;

	public inline function length():Float
		return Math.sqrt(lengthSq());

	public function normalize():Quat {
		var len = length();
		return if (len > 0) new Quat(x / len, y / len, z / len, w / len) else identity();
	}

	public inline function conjugate():Quat
		return new Quat(-x, -y, -z, w);

	public function inverse():Quat {
		var lsq = lengthSq();
		return if (lsq > 0) {
			var inv = 1.0 / lsq;
			new Quat(-x * inv, -y * inv, -z * inv, w * inv);
		} else identity();
	}

	public inline function lerp(b:Quat, t:Float):Quat
		return new Quat(x + (b.x - x) * t, y + (b.y - y) * t, z + (b.z - z) * t, w + (b.w - w) * t);

	public function slerp(b:Quat, t:Float):Quat {
		var d = dot(b);
		var bx = b.x;
		var by = b.y;
		var bz = b.z;
		var bw = b.w;
		if (d < 0) {
			d = -d;
			bx = -bx;
			by = -by;
			bz = -bz;
			bw = -bw;
		}
		if (d > 0.9995)
			return new Quat(x + (bx - x) * t, y + (by - y) * t, z + (bz - z) * t, w + (bw - w) * t).normalize();
		var theta = Math.acos(d);
		var sinT = Math.sin(theta);
		var s0 = Math.sin((1.0 - t) * theta) / sinT;
		var s1 = Math.sin(t * theta) / sinT;
		return new Quat(x * s0 + bx * s1, y * s0 + by * s1, z * s0 + bz * s1, w * s0 + bw * s1);
	}

	public inline function rotateVec3(v:Vec3):Vec3 {
		var qv = new Vec3(x, y, z);
		var uv = qv.cross(v);
		var uuv = qv.cross(uv);
		return v.add(uv.scale(2.0 * w).add(uuv.scale(2.0)));
	}

	public function toMat4():Mat4 {
		var x2 = x + x;
		var y2 = y + y;
		var z2 = z + z;
		var xx = x * x2;
		var xy = x * y2;
		var xz = x * z2;
		var yy = y * y2;
		var yz = y * z2;
		var zz = z * z2;
		var wx = w * x2;
		var wy = w * y2;
		var wz = w * z2;
		var r = Mat4.zero();
		r.m[0] = 1 - (yy + zz);
		r.m[1] = xy + wz;
		r.m[2] = xz - wy;
		r.m[3] = 0;
		r.m[4] = xy - wz;
		r.m[5] = 1 - (xx + zz);
		r.m[6] = yz + wx;
		r.m[7] = 0;
		r.m[8] = xz + wy;
		r.m[9] = yz - wx;
		r.m[10] = 1 - (xx + yy);
		r.m[11] = 0;
		r.m[12] = 0;
		r.m[13] = 0;
		r.m[14] = 0;
		r.m[15] = 1;
		return r;
	}

	public static function fromMat4(m:Mat4):Quat {
		var trace = m.m[0] + m.m[5] + m.m[10];
		if (trace > 0) {
			var s = 0.5 / Math.sqrt(trace + 1.0);
			return new Quat((m.m[6] - m.m[9]) * s, (m.m[8] - m.m[2]) * s, (m.m[1] - m.m[4]) * s, 0.25 / s);
		} else if (m.m[0] > m.m[5] && m.m[0] > m.m[10]) {
			var s = 2.0 * Math.sqrt(1.0 + m.m[0] - m.m[5] - m.m[10]);
			return new Quat(0.25 * s, (m.m[1] + m.m[4]) / s, (m.m[8] + m.m[2]) / s, (m.m[6] - m.m[9]) / s);
		} else if (m.m[5] > m.m[10]) {
			var s = 2.0 * Math.sqrt(1.0 + m.m[5] - m.m[0] - m.m[10]);
			return new Quat((m.m[1] + m.m[4]) / s, 0.25 * s, (m.m[6] + m.m[9]) / s, (m.m[8] - m.m[2]) / s);
		} else {
			var s = 2.0 * Math.sqrt(1.0 + m.m[10] - m.m[0] - m.m[5]);
			return new Quat((m.m[8] + m.m[2]) / s, (m.m[6] + m.m[9]) / s, 0.25 * s, (m.m[1] - m.m[4]) / s);
		}
	}
}

class Mat4 {
	public var m:Array<Float>;

	public inline function new() {
		m = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
	}

	public static inline function identity():Mat4
		return new Mat4();

	public static function zero():Mat4 {
		var r = new Mat4();
		for (i in 0...16)
			r.m[i] = 0;
		return r;
	}

	public function mul(b:Mat4):Mat4 {
		var r = zero();
		var a = m;
		var bm = b.m;
		r.m[0] = a[0] * bm[0] + a[1] * bm[4] + a[2] * bm[8] + a[3] * bm[12];
		r.m[1] = a[0] * bm[1] + a[1] * bm[5] + a[2] * bm[9] + a[3] * bm[13];
		r.m[2] = a[0] * bm[2] + a[1] * bm[6] + a[2] * bm[10] + a[3] * bm[14];
		r.m[3] = a[0] * bm[3] + a[1] * bm[7] + a[2] * bm[11] + a[3] * bm[15];
		r.m[4] = a[4] * bm[0] + a[5] * bm[4] + a[6] * bm[8] + a[7] * bm[12];
		r.m[5] = a[4] * bm[1] + a[5] * bm[5] + a[6] * bm[9] + a[7] * bm[13];
		r.m[6] = a[4] * bm[2] + a[5] * bm[6] + a[6] * bm[10] + a[7] * bm[14];
		r.m[7] = a[4] * bm[3] + a[5] * bm[7] + a[6] * bm[11] + a[7] * bm[15];
		r.m[8] = a[8] * bm[0] + a[9] * bm[4] + a[10] * bm[8] + a[11] * bm[12];
		r.m[9] = a[8] * bm[1] + a[9] * bm[5] + a[10] * bm[9] + a[11] * bm[13];
		r.m[10] = a[8] * bm[2] + a[9] * bm[6] + a[10] * bm[10] + a[11] * bm[14];
		r.m[11] = a[8] * bm[3] + a[9] * bm[7] + a[10] * bm[11] + a[11] * bm[15];
		r.m[12] = a[12] * bm[0] + a[13] * bm[4] + a[14] * bm[8] + a[15] * bm[12];
		r.m[13] = a[12] * bm[1] + a[13] * bm[5] + a[14] * bm[9] + a[15] * bm[13];
		r.m[14] = a[12] * bm[2] + a[13] * bm[6] + a[14] * bm[10] + a[15] * bm[14];
		r.m[15] = a[12] * bm[3] + a[13] * bm[7] + a[14] * bm[11] + a[15] * bm[15];
		return r;
	}

	public inline function mulVec4(v:Vec4):Vec4 {
		return new Vec4(m[0] * v.x + m[1] * v.y + m[2] * v.z + m[3] * v.w, m[4] * v.x + m[5] * v.y + m[6] * v.z + m[7] * v.w,
			m[8] * v.x + m[9] * v.y + m[10] * v.z + m[11] * v.w, m[12] * v.x + m[13] * v.y + m[14] * v.z + m[15] * v.w);
	}

	public inline function mulPoint(v:Vec3):Vec3 {
		return new Vec3(m[0] * v.x + m[1] * v.y + m[2] * v.z + m[3], m[4] * v.x + m[5] * v.y + m[6] * v.z + m[7],
			m[8] * v.x + m[9] * v.y + m[10] * v.z + m[11]);
	}

	public inline function mulDir(v:Vec3):Vec3 {
		return new Vec3(m[0] * v.x + m[1] * v.y + m[2] * v.z, m[4] * v.x + m[5] * v.y + m[6] * v.z, m[8] * v.x + m[9] * v.y + m[10] * v.z);
	}

	public inline function mat3MulVec3(v:Vec3):Vec3
		return mulDir(v);

	public function transpose():Mat4 {
		var r = zero();
		for (row in 0...4)
			for (col in 0...4)
				r.m[col * 4 + row] = m[row * 4 + col];
		return r;
	}

	public function determinant():Float {
		var a = m;
		var a00 = a[0];
		var a01 = a[1];
		var a02 = a[2];
		var a03 = a[3];
		var a10 = a[4];
		var a11 = a[5];
		var a12 = a[6];
		var a13 = a[7];
		var a20 = a[8];
		var a21 = a[9];
		var a22 = a[10];
		var a23 = a[11];
		var a30 = a[12];
		var a31 = a[13];
		var a32 = a[14];
		var a33 = a[15];
		return a00 * (a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31))
			- a01 * (a10 * (a22 * a33 - a23 * a32) - a12 * (a20 * a33 - a23 * a30) + a13 * (a20 * a32 - a22 * a30))
			+ a02 * (a10 * (a21 * a33 - a23 * a31) - a11 * (a20 * a33 - a23 * a30) + a13 * (a20 * a31 - a21 * a30))
			- a03 * (a10 * (a21 * a32 - a22 * a31) - a11 * (a20 * a32 - a22 * a30) + a12 * (a20 * a31 - a21 * a30));
	}

	public function inverse():Mat4 {
		var a = m;
		var a00 = a[0];
		var a01 = a[1];
		var a02 = a[2];
		var a03 = a[3];
		var a10 = a[4];
		var a11 = a[5];
		var a12 = a[6];
		var a13 = a[7];
		var a20 = a[8];
		var a21 = a[9];
		var a22 = a[10];
		var a23 = a[11];
		var a30 = a[12];
		var a31 = a[13];
		var a32 = a[14];
		var a33 = a[15];

		var b00 = a00 * a11 - a01 * a10;
		var b01 = a00 * a12 - a02 * a10;
		var b02 = a00 * a13 - a03 * a10;
		var b03 = a01 * a12 - a02 * a11;
		var b04 = a01 * a13 - a03 * a11;
		var b05 = a02 * a13 - a03 * a12;
		var b06 = a20 * a31 - a21 * a30;
		var b07 = a20 * a32 - a22 * a30;
		var b08 = a20 * a33 - a23 * a30;
		var b09 = a21 * a32 - a22 * a31;
		var b10 = a21 * a33 - a23 * a31;
		var b11 = a22 * a33 - a23 * a32;

		var det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
		if (det == 0)
			return identity();
		var inv = 1.0 / det;
		var r = zero();
		r.m[0] = (a11 * b11 - a12 * b10 + a13 * b09) * inv;
		r.m[1] = (-a01 * b11 + a02 * b10 - a03 * b09) * inv;
		r.m[2] = (a31 * b05 - a32 * b04 + a33 * b03) * inv;
		r.m[3] = (-a21 * b05 + a22 * b04 - a23 * b03) * inv;
		r.m[4] = (-a10 * b11 + a12 * b08 - a13 * b07) * inv;
		r.m[5] = (a00 * b11 - a02 * b08 + a03 * b07) * inv;
		r.m[6] = (-a30 * b05 + a32 * b02 - a33 * b01) * inv;
		r.m[7] = (a20 * b05 - a22 * b02 + a23 * b01) * inv;
		r.m[8] = (a10 * b10 - a11 * b08 + a13 * b06) * inv;
		r.m[9] = (-a00 * b10 + a01 * b08 - a03 * b06) * inv;
		r.m[10] = (a30 * b04 - a31 * b02 + a33 * b00) * inv;
		r.m[11] = (-a20 * b04 + a21 * b02 - a23 * b00) * inv;
		r.m[12] = (-a10 * b09 + a11 * b07 - a12 * b06) * inv;
		r.m[13] = (a00 * b09 - a01 * b07 + a02 * b06) * inv;
		r.m[14] = (-a30 * b03 + a31 * b01 - a32 * b00) * inv;
		r.m[15] = (a20 * b03 - a21 * b01 + a22 * b00) * inv;
		return r;
	}

	public function rigidInverse(eye:Vec3):Mat4 {
		var r = zero();
		r.m[0] = m[0];
		r.m[1] = m[4];
		r.m[2] = m[8];
		r.m[3] = eye.x;
		r.m[4] = m[1];
		r.m[5] = m[5];
		r.m[6] = m[9];
		r.m[7] = eye.y;
		r.m[8] = m[2];
		r.m[9] = m[6];
		r.m[10] = m[10];
		r.m[11] = eye.z;
		r.m[12] = 0;
		r.m[13] = 0;
		r.m[14] = 0;
		r.m[15] = 1;
		return r;
	}

	public static function translate(v:Vec3):Mat4 {
		var r = new Mat4();
		r.m[3] = v.x;
		r.m[7] = v.y;
		r.m[11] = v.z;
		return r;
	}

	public static function scale(v:Vec3):Mat4 {
		var r = zero();
		r.m[0] = v.x;
		r.m[5] = v.y;
		r.m[10] = v.z;
		r.m[15] = 1;
		return r;
	}

	public static function scaleTrans(s:Float, t:Vec3):Mat4 {
		var r = zero();
		r.m[0] = s;
		r.m[3] = t.x;
		r.m[5] = s;
		r.m[7] = t.y;
		r.m[10] = s;
		r.m[11] = t.z;
		r.m[15] = 1;
		return r;
	}

	public static function rotateX(angle:Float):Mat4 {
		var c = Math.cos(angle);
		var s = Math.sin(angle);
		var r = new Mat4();
		r.m[5] = c;
		r.m[6] = s;
		r.m[9] = -s;
		r.m[10] = c;
		return r;
	}

	public static function rotateY(angle:Float):Mat4 {
		var c = Math.cos(angle);
		var s = Math.sin(angle);
		var r = new Mat4();
		r.m[0] = c;
		r.m[2] = -s;
		r.m[8] = s;
		r.m[10] = c;
		return r;
	}

	public static function rotateZ(angle:Float):Mat4 {
		var c = Math.cos(angle);
		var s = Math.sin(angle);
		var r = new Mat4();
		r.m[0] = c;
		r.m[1] = s;
		r.m[4] = -s;
		r.m[5] = c;
		return r;
	}

	public static function rotate(angle:Float, axis:Vec3):Mat4 {
		return Quat.fromAxisAngle(axis, angle).toMat4();
	}

	public static function fromQuat(q:Quat):Mat4
		return q.toMat4();

	public static function lookAtLh(eye:Vec3, target:Vec3, up:Vec3):Mat4 {
		var z = target.sub(eye).normalize();
		var x = up.cross(z).normalize();
		var y = z.cross(x);
		var r = zero();
		r.m[0] = x.x;
		r.m[1] = x.y;
		r.m[2] = x.z;
		r.m[3] = -x.dot(eye);
		r.m[4] = y.x;
		r.m[5] = y.y;
		r.m[6] = y.z;
		r.m[7] = -y.dot(eye);
		r.m[8] = z.x;
		r.m[9] = z.y;
		r.m[10] = z.z;
		r.m[11] = -z.dot(eye);
		r.m[12] = 0;
		r.m[13] = 0;
		r.m[14] = 0;
		r.m[15] = 1;
		return r;
	}

	public static function perspectiveLh(fovDeg:Float, aspect:Float, nz:Float, fz:Float):Mat4 {
		var f = 1.0 / Math.tan(fovDeg * Math.PI / 360.0);
		var r = zero();
		r.m[0] = f / aspect;
		r.m[5] = f;
		r.m[10] = fz / (fz - nz);
		r.m[11] = -fz * nz / (fz - nz);
		r.m[14] = 1;
		return r;
	}

	public static function orthoLh(w:Float, h:Float, nz:Float, fz:Float):Mat4 {
		var r = zero();
		r.m[0] = 2 / w;
		r.m[5] = 2 / h;
		r.m[10] = 1 / (fz - nz);
		r.m[11] = -nz / (fz - nz);
		r.m[15] = 1;
		return r;
	}
}

class MathUtil {
	public static inline function radians(deg:Float):Float
		return deg * (Math.PI / 180.0);

	public static inline function degrees(rad:Float):Float
		return rad * (180.0 / Math.PI);

	public static inline function clamp(v:Float, lo:Float, hi:Float):Float
		return Math.max(lo, Math.min(hi, v));

	public static inline function saturate(v:Float):Float
		return clamp(v, 0.0, 1.0);

	public static inline function lerp(a:Float, b:Float, t:Float):Float
		return a + (b - a) * t;

	public static function smoothstep(edge0:Float, edge1:Float, x:Float):Float {
		var t = clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
		return t * t * (3.0 - 2.0 * t);
	}

	public static inline function step(edge:Float, x:Float):Float
		return if (x < edge) 0.0 else 1.0;
}
