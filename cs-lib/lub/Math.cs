// haxe-lib/lub/lub/Math.hx の TinyC# 版。
// Haxe 版は匿名構造体を包む abstract + @:op だが、C# 版は public class +
// operator オーバーロード (tcs が Lua metamethod に写像) で同じ演算子面を出す。
// @:commutative のスカラー倍は (v * s) / (s * v) の両 overload を明示定義する。
// モジュール名は System.Math と衝突するため class Math は作らず、
// Vec2 / Vec3 / Vec4 / Quat / Mat4 / MathUtil をフラットに置く。
// Haxe 版が持つ匿名構造体との暗黙互換 (Phys2d/Phys3d 座標) は C# には無いので、
// wire 型 (Vec2d / Vec3d / Quat3d) とは wire() / fromWire() で明示変換する。
// メンバー名は Haxe 版 API と揃える (--no-naming-check でビルドされる)。

using System;
using System.Collections.Generic;

/// <summary>2 次元ベクトル。演算子: a+b / a-b / a*b (成分積) / a*s / s*a /
/// a/b (成分商) / a/s / -a。同名メソッド (add / sub / mul / scale / div /
/// negate) も使える。</summary>
public class Vec2
{
    public double x;
    public double y;

    public Vec2(double x, double y)
    {
        this.x = x;
        this.y = y;
    }

    public static Vec2 zero() => new Vec2(0, 0);

    public static Vec2 one() => new Vec2(1, 1);

    /// <summary>全成分が v のベクトル。</summary>
    public static Vec2 splat(double v) => new Vec2(v, v);

    public Vec2 add(Vec2 b) => new Vec2(x + b.x, y + b.y);

    public Vec2 sub(Vec2 b) => new Vec2(x - b.x, y - b.y);

    /// <summary>スカラー倍。演算子は v * s と s * v の両方が使える。</summary>
    public Vec2 scale(double s) => new Vec2(x * s, y * s);

    public Vec2 negate() => new Vec2(-x, -y);

    /// <summary>成分ごとの積 (Hadamard 積)。</summary>
    public Vec2 mul(Vec2 b) => new Vec2(x * b.x, y * b.y);

    /// <summary>成分ごとの商。</summary>
    public Vec2 div(Vec2 b) => new Vec2(x / b.x, y / b.y);

    public static Vec2 operator +(Vec2 a, Vec2 b) => a.add(b);

    public static Vec2 operator -(Vec2 a, Vec2 b) => a.sub(b);

    public static Vec2 operator *(Vec2 a, Vec2 b) => a.mul(b);

    public static Vec2 operator *(Vec2 a, double s) => a.scale(s);

    public static Vec2 operator *(double s, Vec2 a) => a.scale(s);

    public static Vec2 operator /(Vec2 a, Vec2 b) => a.div(b);

    public static Vec2 operator /(Vec2 a, double s) =>
        new Vec2(a.x / s, a.y / s);

    public static Vec2 operator -(Vec2 a) => a.negate();

    public double dot(Vec2 b) => x * b.x + y * b.y;

    public double lengthSq() => x * x + y * y;

    public double length() => Math.Sqrt(lengthSq());

    /// <summary>正規化。零ベクトルは零ベクトルのまま返す。</summary>
    public Vec2 normalize()
    {
        var len = length();
        return len > 0 ? new Vec2(x / len, y / len) : zero();
    }

    public double distanceSq(Vec2 b) => sub(b).lengthSq();

    public double distance(Vec2 b) => Math.Sqrt(distanceSq(b));

    public Vec2 lerp(Vec2 b, double t) =>
        new Vec2(x + (b.x - x) * t, y + (b.y - y) * t);

    public Vec2 min(Vec2 b) =>
        new Vec2(Math.Min(x, b.x), Math.Min(y, b.y));

    public Vec2 max(Vec2 b) =>
        new Vec2(Math.Max(x, b.x), Math.Max(y, b.y));

    public Vec2 clamp(Vec2 lo, Vec2 hi) =>
        new Vec2(Math.Max(lo.x, Math.Min(hi.x, x)),
            Math.Max(lo.y, Math.Min(hi.y, y)));

    /// <summary>反時計回りに 90 度回した垂直ベクトル (-y, x)。</summary>
    public Vec2 perp() => new Vec2(-y, x);

    /// <summary>+X 軸からの角度 (ラジアン)。</summary>
    public double angle() => Math.Atan2(y, x);

    /// <summary>Phys2d の wire 型へ変換する (Haxe 版の暗黙変換の代わり)。</summary>
    public Vec2d wire() => new Vec2d { x = this.x, y = this.y };

    /// <summary>Phys2d の wire 型から変換する。</summary>
    public static Vec2 fromWire(Vec2d v) => new Vec2(v.x, v.y);
}

/// <summary>3 次元ベクトル。演算子: a+b / a-b / a*b (成分積) / a*s / s*a /
/// a/b (成分商) / a/s / -a。同名メソッド (add / sub / mul / scale / div /
/// negate) も使える。</summary>
public class Vec3
{
    public double x;
    public double y;
    public double z;

    public Vec3(double x, double y, double z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public static Vec3 zero() => new Vec3(0, 0, 0);

    public static Vec3 one() => new Vec3(1, 1, 1);

    /// <summary>全成分が v のベクトル。</summary>
    public static Vec3 splat(double v) => new Vec3(v, v, v);

    public static Vec3 up() => new Vec3(0, 1, 0);

    public static Vec3 right() => new Vec3(1, 0, 0);

    /// <summary>左手系 (lookAtLh / perspectiveLh) の前方 +Z。</summary>
    public static Vec3 forward() => new Vec3(0, 0, 1);

    public Vec3 add(Vec3 b) => new Vec3(x + b.x, y + b.y, z + b.z);

    public Vec3 sub(Vec3 b) => new Vec3(x - b.x, y - b.y, z - b.z);

    /// <summary>スカラー倍。演算子は v * s と s * v の両方が使える。</summary>
    public Vec3 scale(double s) => new Vec3(x * s, y * s, z * s);

    public Vec3 negate() => new Vec3(-x, -y, -z);

    /// <summary>成分ごとの積 (Hadamard 積)。</summary>
    public Vec3 mul(Vec3 b) => new Vec3(x * b.x, y * b.y, z * b.z);

    /// <summary>成分ごとの商。</summary>
    public Vec3 div(Vec3 b) => new Vec3(x / b.x, y / b.y, z / b.z);

    public static Vec3 operator +(Vec3 a, Vec3 b) => a.add(b);

    public static Vec3 operator -(Vec3 a, Vec3 b) => a.sub(b);

    public static Vec3 operator *(Vec3 a, Vec3 b) => a.mul(b);

    public static Vec3 operator *(Vec3 a, double s) => a.scale(s);

    public static Vec3 operator *(double s, Vec3 a) => a.scale(s);

    public static Vec3 operator /(Vec3 a, Vec3 b) => a.div(b);

    public static Vec3 operator /(Vec3 a, double s) =>
        new Vec3(a.x / s, a.y / s, a.z / s);

    public static Vec3 operator -(Vec3 a) => a.negate();

    public double dot(Vec3 b) => x * b.x + y * b.y + z * b.z;

    public Vec3 cross(Vec3 b) =>
        new Vec3(y * b.z - z * b.y, z * b.x - x * b.z, x * b.y - y * b.x);

    public double lengthSq() => x * x + y * y + z * z;

    public double length() => Math.Sqrt(lengthSq());

    /// <summary>正規化。零ベクトルは零ベクトルのまま返す。</summary>
    public Vec3 normalize()
    {
        var len = length();
        return len > 0 ? new Vec3(x / len, y / len, z / len) : zero();
    }

    public double distanceSq(Vec3 b) => sub(b).lengthSq();

    public double distance(Vec3 b) => Math.Sqrt(distanceSq(b));

    public Vec3 lerp(Vec3 b, double t) =>
        new Vec3(x + (b.x - x) * t, y + (b.y - y) * t, z + (b.z - z) * t);

    public Vec3 min(Vec3 b) => new Vec3(Math.Min(x, b.x),
        Math.Min(y, b.y), Math.Min(z, b.z));

    public Vec3 max(Vec3 b) => new Vec3(Math.Max(x, b.x),
        Math.Max(y, b.y), Math.Max(z, b.z));

    public Vec3 clamp(Vec3 lo, Vec3 hi) =>
        new Vec3(Math.Max(lo.x, Math.Min(hi.x, x)),
            Math.Max(lo.y, Math.Min(hi.y, y)),
            Math.Max(lo.z, Math.Min(hi.z, z)));

    /// <summary>normal (正規化済みであること) に対する反射ベクトル。</summary>
    public Vec3 reflect(Vec3 normal) => sub(normal.scale(2.0 * dot(normal)));

    /// <summary>Phys3d の wire 型へ変換する (Haxe 版の暗黙変換の代わり)。</summary>
    public Vec3d wire() => new Vec3d { x = this.x, y = this.y, z = this.z };

    /// <summary>Phys3d の wire 型から変換する。</summary>
    public static Vec3 fromWire(Vec3d v) => new Vec3(v.x, v.y, v.z);
}

/// <summary>4 次元ベクトル (同次座標・色など)。演算子: a+b / a-b / a*s /
/// s*a / a/s / -a。</summary>
public class Vec4
{
    public double x;
    public double y;
    public double z;
    public double w;

    public Vec4(double x, double y, double z, double w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public static Vec4 zero() => new Vec4(0, 0, 0, 0);

    public static Vec4 one() => new Vec4(1, 1, 1, 1);

    /// <summary>w を付与して Vec3 から拡張する。位置なら w=1、方向なら w=0。</summary>
    public static Vec4 fromVec3(Vec3 v, double w) => new Vec4(v.x, v.y, v.z, w);

    public Vec4 add(Vec4 b) => new Vec4(x + b.x, y + b.y, z + b.z, w + b.w);

    public Vec4 sub(Vec4 b) => new Vec4(x - b.x, y - b.y, z - b.z, w - b.w);

    /// <summary>スカラー倍。演算子は v * s と s * v の両方が使える。</summary>
    public Vec4 scale(double s) => new Vec4(x * s, y * s, z * s, w * s);

    public Vec4 negate() => new Vec4(-x, -y, -z, -w);

    public static Vec4 operator +(Vec4 a, Vec4 b) => a.add(b);

    public static Vec4 operator -(Vec4 a, Vec4 b) => a.sub(b);

    public static Vec4 operator *(Vec4 a, double s) => a.scale(s);

    public static Vec4 operator *(double s, Vec4 a) => a.scale(s);

    public static Vec4 operator /(Vec4 a, double s) =>
        new Vec4(a.x / s, a.y / s, a.z / s, a.w / s);

    public static Vec4 operator -(Vec4 a) => a.negate();

    public double dot(Vec4 b) => x * b.x + y * b.y + z * b.z + w * b.w;

    public double lengthSq() => x * x + y * y + z * z + w * w;

    public double length() => Math.Sqrt(lengthSq());

    /// <summary>正規化。零ベクトルは零ベクトルのまま返す。</summary>
    public Vec4 normalize()
    {
        var len = length();
        return len > 0 ? new Vec4(x / len, y / len, z / len, w / len) : zero();
    }

    public Vec4 lerp(Vec4 b, double t) => new Vec4(x + (b.x - x) * t,
        y + (b.y - y) * t, z + (b.z - z) * t, w + (b.w - w) * t);

    public Vec3 xyz() => new Vec3(x, y, z);
}

/// <summary>回転を表すクォータニオン。演算子: a * b (回転の合成)、
/// q * v (Vec3 の回転 = rotateVec3)。角度は全てラジアン。</summary>
public class Quat
{
    public double x;
    public double y;
    public double z;
    public double w;

    public Quat(double x, double y, double z, double w)
    {
        this.x = x;
        this.y = y;
        this.z = z;
        this.w = w;
    }

    public static Quat identity() => new Quat(0, 0, 0, 1);

    /// <summary>axis 回りに angle ラジアン回す回転。axis は内部で正規化される。</summary>
    public static Quat fromAxisAngle(Vec3 axis, double angle)
    {
        var half = angle * 0.5;
        var s = Math.Sin(half);
        var n = axis.normalize();
        return new Quat(n.x * s, n.y * s, n.z * s, Math.Cos(half));
    }

    /// <summary>オイラー角 (ラジアン) から生成。適用順は roll (Z) → pitch (X)
    /// → yaw (Y)。</summary>
    public static Quat fromEuler(double yaw, double pitch, double roll)
    {
        var cy = Math.Cos(yaw * 0.5);
        var sy = Math.Sin(yaw * 0.5);
        var cp = Math.Cos(pitch * 0.5);
        var sp = Math.Sin(pitch * 0.5);
        var cr = Math.Cos(roll * 0.5);
        var sr = Math.Sin(roll * 0.5);
        return new Quat(sr * cp * cy - cr * sp * sy,
            cr * sp * cy + sr * cp * sy, cr * cp * sy - sr * sp * cy,
            cr * cp * cy + sr * sp * sy);
    }

    /// <summary>回転の合成。a * b は「b の回転をしてから a の回転」。</summary>
    public Quat mul(Quat b) =>
        new Quat(w * b.x + x * b.w + y * b.z - z * b.y,
            w * b.y - x * b.z + y * b.w + z * b.x,
            w * b.z + x * b.y - y * b.x + z * b.w,
            w * b.w - x * b.x - y * b.y - z * b.z);

    public static Quat operator *(Quat a, Quat b) => a.mul(b);

    public static Vec3 operator *(Quat q, Vec3 v) => q.rotateVec3(v);

    public double dot(Quat b) => x * b.x + y * b.y + z * b.z + w * b.w;

    public double lengthSq() => x * x + y * y + z * z + w * w;

    public double length() => Math.Sqrt(lengthSq());

    /// <summary>正規化。零クォータニオンは identity を返す。</summary>
    public Quat normalize()
    {
        var len = length();
        return len > 0 ? new Quat(x / len, y / len, z / len, w / len)
            : identity();
    }

    public Quat conjugate() => new Quat(-x, -y, -z, w);

    public Quat inverse()
    {
        var lsq = lengthSq();
        if (lsq > 0)
        {
            var inv = 1.0 / lsq;
            return new Quat(-x * inv, -y * inv, -z * inv, w * inv);
        }
        return identity();
    }

    /// <summary>成分の線形補間。正規化はしないので必要なら normalize を挟む。</summary>
    public Quat lerp(Quat b, double t) => new Quat(x + (b.x - x) * t,
        y + (b.y - y) * t, z + (b.z - z) * t, w + (b.w - w) * t);

    /// <summary>球面線形補間。</summary>
    public Quat slerp(Quat b, double t)
    {
        var d = dot(b);
        var bx = b.x;
        var by = b.y;
        var bz = b.z;
        var bw = b.w;
        if (d < 0)
        {
            d = -d;
            bx = -bx;
            by = -by;
            bz = -bz;
            bw = -bw;
        }
        if (d > 0.9995)
        {
            return new Quat(x + (bx - x) * t, y + (by - y) * t,
                z + (bz - z) * t, w + (bw - w) * t).normalize();
        }
        // tcs は Math.Acos 未対応なので等価な atan2 形で書く
        // (acos(d) == atan2(sqrt(1 - d^2), d) for |d| <= 1)
        var theta = Math.Atan2(Math.Sqrt(1.0 - d * d), d);
        var sinT = Math.Sin(theta);
        var s0 = Math.Sin((1.0 - t) * theta) / sinT;
        var s1 = Math.Sin(t * theta) / sinT;
        return new Quat(x * s0 + bx * s1, y * s0 + by * s1, z * s0 + bz * s1,
            w * s0 + bw * s1);
    }

    /// <summary>ベクトルを回転する。演算子 q * v でも呼べる。</summary>
    public Vec3 rotateVec3(Vec3 v)
    {
        var qv = new Vec3(x, y, z);
        var uv = qv.cross(v);
        var uuv = qv.cross(uv);
        return v.add(uv.scale(2.0 * w).add(uuv.scale(2.0)));
    }

    public Mat4 toMat4()
    {
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

    public static Quat fromMat4(Mat4 m)
    {
        var trace = m.m[0] + m.m[5] + m.m[10];
        if (trace > 0)
        {
            var s = 0.5 / Math.Sqrt(trace + 1.0);
            return new Quat((m.m[6] - m.m[9]) * s, (m.m[8] - m.m[2]) * s,
                (m.m[1] - m.m[4]) * s, 0.25 / s);
        }
        else if (m.m[0] > m.m[5] && m.m[0] > m.m[10])
        {
            var s = 2.0 * Math.Sqrt(1.0 + m.m[0] - m.m[5] - m.m[10]);
            return new Quat(0.25 * s, (m.m[1] + m.m[4]) / s,
                (m.m[8] + m.m[2]) / s, (m.m[6] - m.m[9]) / s);
        }
        else if (m.m[5] > m.m[10])
        {
            var s = 2.0 * Math.Sqrt(1.0 + m.m[5] - m.m[0] - m.m[10]);
            return new Quat((m.m[1] + m.m[4]) / s, 0.25 * s,
                (m.m[6] + m.m[9]) / s, (m.m[8] - m.m[2]) / s);
        }
        else
        {
            var s = 2.0 * Math.Sqrt(1.0 + m.m[10] - m.m[0] - m.m[5]);
            return new Quat((m.m[8] + m.m[2]) / s, (m.m[6] + m.m[9]) / s,
                0.25 * s, (m.m[1] - m.m[4]) / s);
        }
    }

    /// <summary>Phys3d の wire 型へ変換する (Haxe 版の暗黙変換の代わり)。</summary>
    public Quat3d wire() =>
        new Quat3d { x = this.x, y = this.y, z = this.z, w = this.w };

    /// <summary>Phys3d の wire 型から変換する。</summary>
    public static Quat fromWire(Quat3d q) => new Quat(q.x, q.y, q.z, q.w);
}

/// <summary>4x4 行列 (行優先 / row-major、m[row * 4 + col])。演算子:
/// a * b (行列積)、m * v (Vec4 との積 = mulVec4)。MVP 合成は
/// proj * view * model の順。</summary>
public class Mat4
{
    public List<double> m;

    /// <summary>単位行列で初期化する。</summary>
    public Mat4()
    {
        m = new List<double>
        {
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
        };
    }

    public static Mat4 identity() => new Mat4();

    public static Mat4 zero()
    {
        var r = new Mat4();
        for (var i = 0; i < 16; i++)
        {
            r.m[i] = 0;
        }
        return r;
    }

    /// <summary>行列積。演算子 a * b でも呼べる。</summary>
    public Mat4 mul(Mat4 b)
    {
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
        r.m[12] = a[12] * bm[0] + a[13] * bm[4] + a[14] * bm[8]
            + a[15] * bm[12];
        r.m[13] = a[12] * bm[1] + a[13] * bm[5] + a[14] * bm[9]
            + a[15] * bm[13];
        r.m[14] = a[12] * bm[2] + a[13] * bm[6] + a[14] * bm[10]
            + a[15] * bm[14];
        r.m[15] = a[12] * bm[3] + a[13] * bm[7] + a[14] * bm[11]
            + a[15] * bm[15];
        return r;
    }

    /// <summary>Vec4 との積。演算子 m * v でも呼べる。</summary>
    public Vec4 mulVec4(Vec4 v)
    {
        var a = m;
        return new Vec4(
            a[0] * v.x + a[1] * v.y + a[2] * v.z + a[3] * v.w,
            a[4] * v.x + a[5] * v.y + a[6] * v.z + a[7] * v.w,
            a[8] * v.x + a[9] * v.y + a[10] * v.z + a[11] * v.w,
            a[12] * v.x + a[13] * v.y + a[14] * v.z + a[15] * v.w);
    }

    public static Mat4 operator *(Mat4 a, Mat4 b) => a.mul(b);

    public static Vec4 operator *(Mat4 a, Vec4 v) => a.mulVec4(v);

    /// <summary>位置として変換する (w=1 扱い。平行移動が効く)。</summary>
    public Vec3 mulPoint(Vec3 v)
    {
        var a = m;
        return new Vec3(a[0] * v.x + a[1] * v.y + a[2] * v.z + a[3],
            a[4] * v.x + a[5] * v.y + a[6] * v.z + a[7],
            a[8] * v.x + a[9] * v.y + a[10] * v.z + a[11]);
    }

    /// <summary>方向として変換する (w=0 扱い。平行移動は無視)。</summary>
    public Vec3 mulDir(Vec3 v)
    {
        var a = m;
        return new Vec3(a[0] * v.x + a[1] * v.y + a[2] * v.z,
            a[4] * v.x + a[5] * v.y + a[6] * v.z,
            a[8] * v.x + a[9] * v.y + a[10] * v.z);
    }

    public Vec3 mat3MulVec3(Vec3 v) => mulDir(v);

    public Mat4 transpose()
    {
        var r = zero();
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                r.m[col * 4 + row] = m[row * 4 + col];
            }
        }
        return r;
    }

    public double determinant()
    {
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
        return a00
            * (a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31)
                + a13 * (a21 * a32 - a22 * a31))
            - a01
            * (a10 * (a22 * a33 - a23 * a32) - a12 * (a20 * a33 - a23 * a30)
                + a13 * (a20 * a32 - a22 * a30))
            + a02
            * (a10 * (a21 * a33 - a23 * a31) - a11 * (a20 * a33 - a23 * a30)
                + a13 * (a20 * a31 - a21 * a30))
            - a03
            * (a10 * (a21 * a32 - a22 * a31) - a11 * (a20 * a32 - a22 * a30)
                + a12 * (a20 * a31 - a21 * a30));
    }

    /// <summary>逆行列。特異行列 (det=0) の場合は単位行列を返す。</summary>
    public Mat4 inverse()
    {
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

        var det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07
            + b05 * b06;
        if (det == 0)
        {
            return identity();
        }
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

    /// <summary>回転 + 平行移動だけの view 行列を、回転転置 + eye 差し替えで
    /// 逆変換する。</summary>
    public Mat4 rigidInverse(Vec3 eye)
    {
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

    public static Mat4 translate(Vec3 v)
    {
        var r = new Mat4();
        r.m[3] = v.x;
        r.m[7] = v.y;
        r.m[11] = v.z;
        return r;
    }

    public static Mat4 scale(Vec3 v)
    {
        var r = zero();
        r.m[0] = v.x;
        r.m[5] = v.y;
        r.m[10] = v.z;
        r.m[15] = 1;
        return r;
    }

    /// <summary>均一スケール s + 平行移動 t を 1 つの行列にまとめる。</summary>
    public static Mat4 scaleTrans(double s, Vec3 t)
    {
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

    /// <summary>X 軸回りの回転 (ラジアン)。</summary>
    public static Mat4 rotateX(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var r = new Mat4();
        r.m[5] = c;
        r.m[6] = s;
        r.m[9] = -s;
        r.m[10] = c;
        return r;
    }

    /// <summary>Y 軸回りの回転 (ラジアン)。</summary>
    public static Mat4 rotateY(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var r = new Mat4();
        r.m[0] = c;
        r.m[2] = -s;
        r.m[8] = s;
        r.m[10] = c;
        return r;
    }

    /// <summary>Z 軸回りの回転 (ラジアン)。</summary>
    public static Mat4 rotateZ(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var r = new Mat4();
        r.m[0] = c;
        r.m[1] = s;
        r.m[4] = -s;
        r.m[5] = c;
        return r;
    }

    /// <summary>任意軸 axis 回りの回転 (ラジアン)。</summary>
    public static Mat4 rotate(double angle, Vec3 axis) =>
        Quat.fromAxisAngle(axis, angle).toMat4();

    public static Mat4 fromQuat(Quat q) => q.toMat4();

    /// <summary>左手系の view 行列。</summary>
    public static Mat4 lookAtLh(Vec3 eye, Vec3 target, Vec3 up)
    {
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

    /// <summary>左手系の透視投影。fovDeg は垂直視野角 (度)。depth は [0, 1]。</summary>
    public static Mat4 perspectiveLh(double fovDeg, double aspect, double nz,
        double fz)
    {
        var f = 1.0 / Math.Tan(fovDeg * Math.PI / 360.0);
        var r = zero();
        r.m[0] = f / aspect;
        r.m[5] = f;
        r.m[10] = fz / (fz - nz);
        r.m[11] = -fz * nz / (fz - nz);
        r.m[14] = 1;
        return r;
    }

    /// <summary>左手系の平行投影。w / h は view volume の幅と高さ。depth は
    /// [0, 1]。</summary>
    public static Mat4 orthoLh(double w, double h, double nz, double fz)
    {
        var r = zero();
        r.m[0] = 2 / w;
        r.m[5] = 2 / h;
        r.m[10] = 1 / (fz - nz);
        r.m[11] = -nz / (fz - nz);
        r.m[15] = 1;
        return r;
    }
}

/// <summary>スカラー演算のユーティリティ。角度変換以外は GLSL の同名関数と
/// 同義。</summary>
public static class MathUtil
{
    /// <summary>度 → ラジアン。</summary>
    public static double radians(double deg) => deg * (Math.PI / 180.0);

    /// <summary>ラジアン → 度。</summary>
    public static double degrees(double rad) => rad * (180.0 / Math.PI);

    public static double clamp(double v, double lo, double hi) =>
        Math.Max(lo, Math.Min(hi, v));

    public static double saturate(double v) => clamp(v, 0.0, 1.0);

    public static double lerp(double a, double b, double t) =>
        a + (b - a) * t;

    public static double smoothstep(double edge0, double edge1, double x)
    {
        var t = clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    public static double step(double edge, double x) => x < edge ? 0.0 : 1.0;
}
