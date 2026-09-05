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
using static Lub;

/// <summary>2 次元ベクトル。演算子: a+b / a-b / a*b (成分積) / a*s / s*a /
/// a/b (成分商) / a/s / -a。同名メソッド (add / sub / mul / scale / div /
/// negate) も使える。</summary>
public class Vec2
{
    public double X;
    public double Y;

    public Vec2(double x, double y)
    {
        this.X = x;
        this.Y = y;
    }

    public static Vec2 Zero() => new Vec2(0, 0);

    public static Vec2 One() => new Vec2(1, 1);

    /// <summary>全成分が v のベクトル。</summary>
    public static Vec2 Splat(double v) => new Vec2(v, v);

    public Vec2 Add(Vec2 b) => new Vec2(X + b.X, Y + b.Y);

    public Vec2 Sub(Vec2 b) => new Vec2(X - b.X, Y - b.Y);

    /// <summary>スカラー倍。演算子は v * s と s * v の両方が使える。</summary>
    public Vec2 Scale(double s) => new Vec2(X * s, Y * s);

    public Vec2 Negate() => new Vec2(-X, -Y);

    /// <summary>成分ごとの積 (Hadamard 積)。</summary>
    public Vec2 Mul(Vec2 b) => new Vec2(X * b.X, Y * b.Y);

    /// <summary>成分ごとの商。</summary>
    public Vec2 Div(Vec2 b) => new Vec2(X / b.X, Y / b.Y);

    public static Vec2 operator +(Vec2 a, Vec2 b) => a.Add(b);

    public static Vec2 operator -(Vec2 a, Vec2 b) => a.Sub(b);

    public static Vec2 operator *(Vec2 a, Vec2 b) => a.Mul(b);

    public static Vec2 operator *(Vec2 a, double s) => a.Scale(s);

    public static Vec2 operator *(double s, Vec2 a) => a.Scale(s);

    public static Vec2 operator /(Vec2 a, Vec2 b) => a.Div(b);

    public static Vec2 operator /(Vec2 a, double s) =>
        new Vec2(a.X / s, a.Y / s);

    public static Vec2 operator -(Vec2 a) => a.Negate();

    public double Dot(Vec2 b) => X * b.X + Y * b.Y;

    public double LengthSq() => X * X + Y * Y;

    public double Length() => Math.Sqrt(LengthSq());

    /// <summary>正規化。零ベクトルは零ベクトルのまま返す。</summary>
    public Vec2 Normalize()
    {
        var len = Length();
        return len > 0 ? new Vec2(X / len, Y / len) : Zero();
    }

    public double DistanceSq(Vec2 b) => Sub(b).LengthSq();

    public double Distance(Vec2 b) => Math.Sqrt(DistanceSq(b));

    public Vec2 Lerp(Vec2 b, double t) =>
        new Vec2(X + (b.X - X) * t, Y + (b.Y - Y) * t);

    public Vec2 Min(Vec2 b) =>
        new Vec2(Math.Min(X, b.X), Math.Min(Y, b.Y));

    public Vec2 Max(Vec2 b) =>
        new Vec2(Math.Max(X, b.X), Math.Max(Y, b.Y));

    public Vec2 Clamp(Vec2 lo, Vec2 hi) =>
        new Vec2(Math.Max(lo.X, Math.Min(hi.X, X)),
            Math.Max(lo.Y, Math.Min(hi.Y, Y)));

    /// <summary>反時計回りに 90 度回した垂直ベクトル (-y, x)。</summary>
    public Vec2 Perp() => new Vec2(-Y, X);

    /// <summary>+X 軸からの角度 (ラジアン)。</summary>
    public double Angle() => Math.Atan2(Y, X);

    /// <summary>Phys2d の wire 型へ変換する (Haxe 版の暗黙変換の代わり)。</summary>
    public Vec2d Wire() => new Vec2d { X = this.X, Y = this.Y };

    /// <summary>Phys2d の wire 型から変換する。</summary>
    public static Vec2 FromWire(Vec2d v) => new Vec2(v.X, v.Y);
}

/// <summary>3 次元ベクトル。演算子: a+b / a-b / a*b (成分積) / a*s / s*a /
/// a/b (成分商) / a/s / -a。同名メソッド (add / sub / mul / scale / div /
/// negate) も使える。</summary>
public class Vec3
{
    public double X;
    public double Y;
    public double Z;

    public Vec3(double x, double y, double z)
    {
        this.X = x;
        this.Y = y;
        this.Z = z;
    }

    public static Vec3 Zero() => new Vec3(0, 0, 0);

    public static Vec3 One() => new Vec3(1, 1, 1);

    /// <summary>全成分が v のベクトル。</summary>
    public static Vec3 Splat(double v) => new Vec3(v, v, v);

    public static Vec3 Up() => new Vec3(0, 1, 0);

    public static Vec3 Right() => new Vec3(1, 0, 0);

    /// <summary>左手系 (lookAtLh / perspectiveLh) の前方 +Z。</summary>
    public static Vec3 Forward() => new Vec3(0, 0, 1);

    public Vec3 Add(Vec3 b) => new Vec3(X + b.X, Y + b.Y, Z + b.Z);

    public Vec3 Sub(Vec3 b) => new Vec3(X - b.X, Y - b.Y, Z - b.Z);

    /// <summary>スカラー倍。演算子は v * s と s * v の両方が使える。</summary>
    public Vec3 Scale(double s) => new Vec3(X * s, Y * s, Z * s);

    public Vec3 Negate() => new Vec3(-X, -Y, -Z);

    /// <summary>成分ごとの積 (Hadamard 積)。</summary>
    public Vec3 Mul(Vec3 b) => new Vec3(X * b.X, Y * b.Y, Z * b.Z);

    /// <summary>成分ごとの商。</summary>
    public Vec3 Div(Vec3 b) => new Vec3(X / b.X, Y / b.Y, Z / b.Z);

    public static Vec3 operator +(Vec3 a, Vec3 b) => a.Add(b);

    public static Vec3 operator -(Vec3 a, Vec3 b) => a.Sub(b);

    public static Vec3 operator *(Vec3 a, Vec3 b) => a.Mul(b);

    public static Vec3 operator *(Vec3 a, double s) => a.Scale(s);

    public static Vec3 operator *(double s, Vec3 a) => a.Scale(s);

    public static Vec3 operator /(Vec3 a, Vec3 b) => a.Div(b);

    public static Vec3 operator /(Vec3 a, double s) =>
        new Vec3(a.X / s, a.Y / s, a.Z / s);

    public static Vec3 operator -(Vec3 a) => a.Negate();

    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;

    public Vec3 Cross(Vec3 b) =>
        new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public double LengthSq() => X * X + Y * Y + Z * Z;

    public double Length() => Math.Sqrt(LengthSq());

    /// <summary>正規化。零ベクトルは零ベクトルのまま返す。</summary>
    public Vec3 Normalize()
    {
        var len = Length();
        return len > 0 ? new Vec3(X / len, Y / len, Z / len) : Zero();
    }

    public double DistanceSq(Vec3 b) => Sub(b).LengthSq();

    public double Distance(Vec3 b) => Math.Sqrt(DistanceSq(b));

    public Vec3 Lerp(Vec3 b, double t) =>
        new Vec3(X + (b.X - X) * t, Y + (b.Y - Y) * t, Z + (b.Z - Z) * t);

    public Vec3 Min(Vec3 b) => new Vec3(Math.Min(X, b.X),
        Math.Min(Y, b.Y), Math.Min(Z, b.Z));

    public Vec3 Max(Vec3 b) => new Vec3(Math.Max(X, b.X),
        Math.Max(Y, b.Y), Math.Max(Z, b.Z));

    public Vec3 Clamp(Vec3 lo, Vec3 hi) =>
        new Vec3(Math.Max(lo.X, Math.Min(hi.X, X)),
            Math.Max(lo.Y, Math.Min(hi.Y, Y)),
            Math.Max(lo.Z, Math.Min(hi.Z, Z)));

    /// <summary>normal (正規化済みであること) に対する反射ベクトル。</summary>
    public Vec3 Reflect(Vec3 normal) => Sub(normal.Scale(2.0 * Dot(normal)));

    /// <summary>Phys3d の wire 型へ変換する (Haxe 版の暗黙変換の代わり)。</summary>
    public Vec3d Wire() => new Vec3d { X = this.X, Y = this.Y, Z = this.Z };

    /// <summary>Phys3d の wire 型から変換する。</summary>
    public static Vec3 FromWire(Vec3d v) => new Vec3(v.X, v.Y, v.Z);
}

/// <summary>4 次元ベクトル (同次座標・色など)。演算子: a+b / a-b / a*s /
/// s*a / a/s / -a。</summary>
public class Vec4
{
    public double X;
    public double Y;
    public double Z;
    public double W;

    public Vec4(double x, double y, double z, double w)
    {
        this.X = x;
        this.Y = y;
        this.Z = z;
        this.W = w;
    }

    public static Vec4 Zero() => new Vec4(0, 0, 0, 0);

    public static Vec4 One() => new Vec4(1, 1, 1, 1);

    /// <summary>w を付与して Vec3 から拡張する。位置なら w=1、方向なら w=0。</summary>
    public static Vec4 FromVec3(Vec3 v, double w) => new Vec4(v.X, v.Y, v.Z, w);

    public Vec4 Add(Vec4 b) => new Vec4(X + b.X, Y + b.Y, Z + b.Z, W + b.W);

    public Vec4 Sub(Vec4 b) => new Vec4(X - b.X, Y - b.Y, Z - b.Z, W - b.W);

    /// <summary>スカラー倍。演算子は v * s と s * v の両方が使える。</summary>
    public Vec4 Scale(double s) => new Vec4(X * s, Y * s, Z * s, W * s);

    public Vec4 Negate() => new Vec4(-X, -Y, -Z, -W);

    public static Vec4 operator +(Vec4 a, Vec4 b) => a.Add(b);

    public static Vec4 operator -(Vec4 a, Vec4 b) => a.Sub(b);

    public static Vec4 operator *(Vec4 a, double s) => a.Scale(s);

    public static Vec4 operator *(double s, Vec4 a) => a.Scale(s);

    public static Vec4 operator /(Vec4 a, double s) =>
        new Vec4(a.X / s, a.Y / s, a.Z / s, a.W / s);

    public static Vec4 operator -(Vec4 a) => a.Negate();

    public double Dot(Vec4 b) => X * b.X + Y * b.Y + Z * b.Z + W * b.W;

    public double LengthSq() => X * X + Y * Y + Z * Z + W * W;

    public double Length() => Math.Sqrt(LengthSq());

    /// <summary>正規化。零ベクトルは零ベクトルのまま返す。</summary>
    public Vec4 Normalize()
    {
        var len = Length();
        return len > 0 ? new Vec4(X / len, Y / len, Z / len, W / len) : Zero();
    }

    public Vec4 Lerp(Vec4 b, double t) => new Vec4(X + (b.X - X) * t,
        Y + (b.Y - Y) * t, Z + (b.Z - Z) * t, W + (b.W - W) * t);

    public Vec3 Xyz() => new Vec3(X, Y, Z);
}

/// <summary>回転を表すクォータニオン。演算子: a * b (回転の合成)、
/// q * v (Vec3 の回転 = rotateVec3)。角度は全てラジアン。</summary>
public class Quat
{
    public double X;
    public double Y;
    public double Z;
    public double W;

    public Quat(double x, double y, double z, double w)
    {
        this.X = x;
        this.Y = y;
        this.Z = z;
        this.W = w;
    }

    public static Quat Identity() => new Quat(0, 0, 0, 1);

    /// <summary>axis 回りに angle ラジアン回す回転。axis は内部で正規化される。</summary>
    public static Quat FromAxisAngle(Vec3 axis, double angle)
    {
        var half = angle * 0.5;
        var s = Math.Sin(half);
        var n = axis.Normalize();
        return new Quat(n.X * s, n.Y * s, n.Z * s, Math.Cos(half));
    }

    /// <summary>オイラー角 (ラジアン) から生成。適用順は roll (Z) → pitch (X)
    /// → yaw (Y)。</summary>
    public static Quat FromEuler(double yaw, double pitch, double roll)
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
    public Quat Mul(Quat b) =>
        new Quat(W * b.X + X * b.W + Y * b.Z - Z * b.Y,
            W * b.Y - X * b.Z + Y * b.W + Z * b.X,
            W * b.Z + X * b.Y - Y * b.X + Z * b.W,
            W * b.W - X * b.X - Y * b.Y - Z * b.Z);

    public static Quat operator *(Quat a, Quat b) => a.Mul(b);

    public static Vec3 operator *(Quat q, Vec3 v) => q.RotateVec3(v);

    public double Dot(Quat b) => X * b.X + Y * b.Y + Z * b.Z + W * b.W;

    public double LengthSq() => X * X + Y * Y + Z * Z + W * W;

    public double Length() => Math.Sqrt(LengthSq());

    /// <summary>正規化。零クォータニオンは identity を返す。</summary>
    public Quat Normalize()
    {
        var len = Length();
        return len > 0 ? new Quat(X / len, Y / len, Z / len, W / len)
            : Identity();
    }

    public Quat Conjugate() => new Quat(-X, -Y, -Z, W);

    public Quat Inverse()
    {
        var lsq = LengthSq();
        if (lsq > 0)
        {
            var inv = 1.0 / lsq;
            return new Quat(-X * inv, -Y * inv, -Z * inv, W * inv);
        }
        return Identity();
    }

    /// <summary>成分の線形補間。正規化はしないので必要なら normalize を挟む。</summary>
    public Quat Lerp(Quat b, double t) => new Quat(X + (b.X - X) * t,
        Y + (b.Y - Y) * t, Z + (b.Z - Z) * t, W + (b.W - W) * t);

    /// <summary>球面線形補間。</summary>
    public Quat Slerp(Quat b, double t)
    {
        var d = Dot(b);
        var bx = b.X;
        var by = b.Y;
        var bz = b.Z;
        var bw = b.W;
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
            return new Quat(X + (bx - X) * t, Y + (by - Y) * t,
                Z + (bz - Z) * t, W + (bw - W) * t).Normalize();
        }
        // tcs は Math.Acos 未対応なので等価な atan2 形で書く
        // (acos(d) == atan2(sqrt(1 - d^2), d) for |d| <= 1)
        var theta = Math.Atan2(Math.Sqrt(1.0 - d * d), d);
        var sinT = Math.Sin(theta);
        var s0 = Math.Sin((1.0 - t) * theta) / sinT;
        var s1 = Math.Sin(t * theta) / sinT;
        return new Quat(X * s0 + bx * s1, Y * s0 + by * s1, Z * s0 + bz * s1,
            W * s0 + bw * s1);
    }

    /// <summary>ベクトルを回転する。演算子 q * v でも呼べる。</summary>
    public Vec3 RotateVec3(Vec3 v)
    {
        var qv = new Vec3(X, Y, Z);
        var uv = qv.Cross(v);
        var uuv = qv.Cross(uv);
        return v.Add(uv.Scale(2.0 * W).Add(uuv.Scale(2.0)));
    }

    public Mat4 ToMat4()
    {
        var x2 = X + X;
        var y2 = Y + Y;
        var z2 = Z + Z;
        var xx = X * x2;
        var xy = X * y2;
        var xz = X * z2;
        var yy = Y * y2;
        var yz = Y * z2;
        var zz = Z * z2;
        var wx = W * x2;
        var wy = W * y2;
        var wz = W * z2;
        var r = Mat4.Zero();
        r.M[0] = 1 - (yy + zz);
        r.M[1] = xy + wz;
        r.M[2] = xz - wy;
        r.M[3] = 0;
        r.M[4] = xy - wz;
        r.M[5] = 1 - (xx + zz);
        r.M[6] = yz + wx;
        r.M[7] = 0;
        r.M[8] = xz + wy;
        r.M[9] = yz - wx;
        r.M[10] = 1 - (xx + yy);
        r.M[11] = 0;
        r.M[12] = 0;
        r.M[13] = 0;
        r.M[14] = 0;
        r.M[15] = 1;
        return r;
    }

    public static Quat FromMat4(Mat4 m)
    {
        var trace = m.M[0] + m.M[5] + m.M[10];
        if (trace > 0)
        {
            var s = 0.5 / Math.Sqrt(trace + 1.0);
            return new Quat((m.M[6] - m.M[9]) * s, (m.M[8] - m.M[2]) * s,
                (m.M[1] - m.M[4]) * s, 0.25 / s);
        }
        else if (m.M[0] > m.M[5] && m.M[0] > m.M[10])
        {
            var s = 2.0 * Math.Sqrt(1.0 + m.M[0] - m.M[5] - m.M[10]);
            return new Quat(0.25 * s, (m.M[1] + m.M[4]) / s,
                (m.M[8] + m.M[2]) / s, (m.M[6] - m.M[9]) / s);
        }
        else if (m.M[5] > m.M[10])
        {
            var s = 2.0 * Math.Sqrt(1.0 + m.M[5] - m.M[0] - m.M[10]);
            return new Quat((m.M[1] + m.M[4]) / s, 0.25 * s,
                (m.M[6] + m.M[9]) / s, (m.M[8] - m.M[2]) / s);
        }
        else
        {
            var s = 2.0 * Math.Sqrt(1.0 + m.M[10] - m.M[0] - m.M[5]);
            return new Quat((m.M[8] + m.M[2]) / s, (m.M[6] + m.M[9]) / s,
                0.25 * s, (m.M[1] - m.M[4]) / s);
        }
    }

    /// <summary>Phys3d の wire 型へ変換する (Haxe 版の暗黙変換の代わり)。</summary>
    public Quat3d Wire() =>
        new Quat3d { X = this.X, Y = this.Y, Z = this.Z, W = this.W };

    /// <summary>Phys3d の wire 型から変換する。</summary>
    public static Quat FromWire(Quat3d q) => new Quat(q.X, q.Y, q.Z, q.W);
}

/// <summary>4x4 行列 (行優先 / row-major、m[row * 4 + col])。演算子:
/// a * b (行列積)、m * v (Vec4 との積 = mulVec4)。MVP 合成は
/// proj * view * model の順。</summary>
public class Mat4
{
    public List<double> M;

    /// <summary>単位行列で初期化する。</summary>
    public Mat4()
    {
        M = new List<double>
        {
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
        };
    }

    public static Mat4 Identity() => new Mat4();

    public static Mat4 Zero()
    {
        var r = new Mat4();
        for (var i = 0; i < 16; i++)
        {
            r.M[i] = 0;
        }
        return r;
    }

    /// <summary>行列積。演算子 a * b でも呼べる。</summary>
    public Mat4 Mul(Mat4 b)
    {
        var r = Zero();
        var a = M;
        var bm = b.M;
        r.M[0] = a[0] * bm[0] + a[1] * bm[4] + a[2] * bm[8] + a[3] * bm[12];
        r.M[1] = a[0] * bm[1] + a[1] * bm[5] + a[2] * bm[9] + a[3] * bm[13];
        r.M[2] = a[0] * bm[2] + a[1] * bm[6] + a[2] * bm[10] + a[3] * bm[14];
        r.M[3] = a[0] * bm[3] + a[1] * bm[7] + a[2] * bm[11] + a[3] * bm[15];
        r.M[4] = a[4] * bm[0] + a[5] * bm[4] + a[6] * bm[8] + a[7] * bm[12];
        r.M[5] = a[4] * bm[1] + a[5] * bm[5] + a[6] * bm[9] + a[7] * bm[13];
        r.M[6] = a[4] * bm[2] + a[5] * bm[6] + a[6] * bm[10] + a[7] * bm[14];
        r.M[7] = a[4] * bm[3] + a[5] * bm[7] + a[6] * bm[11] + a[7] * bm[15];
        r.M[8] = a[8] * bm[0] + a[9] * bm[4] + a[10] * bm[8] + a[11] * bm[12];
        r.M[9] = a[8] * bm[1] + a[9] * bm[5] + a[10] * bm[9] + a[11] * bm[13];
        r.M[10] = a[8] * bm[2] + a[9] * bm[6] + a[10] * bm[10] + a[11] * bm[14];
        r.M[11] = a[8] * bm[3] + a[9] * bm[7] + a[10] * bm[11] + a[11] * bm[15];
        r.M[12] = a[12] * bm[0] + a[13] * bm[4] + a[14] * bm[8]
            + a[15] * bm[12];
        r.M[13] = a[12] * bm[1] + a[13] * bm[5] + a[14] * bm[9]
            + a[15] * bm[13];
        r.M[14] = a[12] * bm[2] + a[13] * bm[6] + a[14] * bm[10]
            + a[15] * bm[14];
        r.M[15] = a[12] * bm[3] + a[13] * bm[7] + a[14] * bm[11]
            + a[15] * bm[15];
        return r;
    }

    /// <summary>Vec4 との積。演算子 m * v でも呼べる。</summary>
    public Vec4 MulVec4(Vec4 v)
    {
        var a = M;
        return new Vec4(
            a[0] * v.X + a[1] * v.Y + a[2] * v.Z + a[3] * v.W,
            a[4] * v.X + a[5] * v.Y + a[6] * v.Z + a[7] * v.W,
            a[8] * v.X + a[9] * v.Y + a[10] * v.Z + a[11] * v.W,
            a[12] * v.X + a[13] * v.Y + a[14] * v.Z + a[15] * v.W);
    }

    public static Mat4 operator *(Mat4 a, Mat4 b) => a.Mul(b);

    public static Vec4 operator *(Mat4 a, Vec4 v) => a.MulVec4(v);

    /// <summary>位置として変換する (w=1 扱い。平行移動が効く)。</summary>
    public Vec3 MulPoint(Vec3 v)
    {
        var a = M;
        return new Vec3(a[0] * v.X + a[1] * v.Y + a[2] * v.Z + a[3],
            a[4] * v.X + a[5] * v.Y + a[6] * v.Z + a[7],
            a[8] * v.X + a[9] * v.Y + a[10] * v.Z + a[11]);
    }

    /// <summary>方向として変換する (w=0 扱い。平行移動は無視)。</summary>
    public Vec3 MulDir(Vec3 v)
    {
        var a = M;
        return new Vec3(a[0] * v.X + a[1] * v.Y + a[2] * v.Z,
            a[4] * v.X + a[5] * v.Y + a[6] * v.Z,
            a[8] * v.X + a[9] * v.Y + a[10] * v.Z);
    }

    public Vec3 Mat3MulVec3(Vec3 v) => MulDir(v);

    public Mat4 Transpose()
    {
        var r = Zero();
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                r.M[col * 4 + row] = M[row * 4 + col];
            }
        }
        return r;
    }

    public double Determinant()
    {
        var a = M;
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
    public Mat4 Inverse()
    {
        var a = M;
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
            return Identity();
        }
        var inv = 1.0 / det;
        var r = Zero();
        r.M[0] = (a11 * b11 - a12 * b10 + a13 * b09) * inv;
        r.M[1] = (-a01 * b11 + a02 * b10 - a03 * b09) * inv;
        r.M[2] = (a31 * b05 - a32 * b04 + a33 * b03) * inv;
        r.M[3] = (-a21 * b05 + a22 * b04 - a23 * b03) * inv;
        r.M[4] = (-a10 * b11 + a12 * b08 - a13 * b07) * inv;
        r.M[5] = (a00 * b11 - a02 * b08 + a03 * b07) * inv;
        r.M[6] = (-a30 * b05 + a32 * b02 - a33 * b01) * inv;
        r.M[7] = (a20 * b05 - a22 * b02 + a23 * b01) * inv;
        r.M[8] = (a10 * b10 - a11 * b08 + a13 * b06) * inv;
        r.M[9] = (-a00 * b10 + a01 * b08 - a03 * b06) * inv;
        r.M[10] = (a30 * b04 - a31 * b02 + a33 * b00) * inv;
        r.M[11] = (-a20 * b04 + a21 * b02 - a23 * b00) * inv;
        r.M[12] = (-a10 * b09 + a11 * b07 - a12 * b06) * inv;
        r.M[13] = (a00 * b09 - a01 * b07 + a02 * b06) * inv;
        r.M[14] = (-a30 * b03 + a31 * b01 - a32 * b00) * inv;
        r.M[15] = (a20 * b03 - a21 * b01 + a22 * b00) * inv;
        return r;
    }

    /// <summary>回転 + 平行移動だけの view 行列を、回転転置 + eye 差し替えで
    /// 逆変換する。</summary>
    public Mat4 RigidInverse(Vec3 eye)
    {
        var r = Zero();
        r.M[0] = M[0];
        r.M[1] = M[4];
        r.M[2] = M[8];
        r.M[3] = eye.X;
        r.M[4] = M[1];
        r.M[5] = M[5];
        r.M[6] = M[9];
        r.M[7] = eye.Y;
        r.M[8] = M[2];
        r.M[9] = M[6];
        r.M[10] = M[10];
        r.M[11] = eye.Z;
        r.M[12] = 0;
        r.M[13] = 0;
        r.M[14] = 0;
        r.M[15] = 1;
        return r;
    }

    public static Mat4 Translate(Vec3 v)
    {
        var r = new Mat4();
        r.M[3] = v.X;
        r.M[7] = v.Y;
        r.M[11] = v.Z;
        return r;
    }

    public static Mat4 Scale(Vec3 v)
    {
        var r = Zero();
        r.M[0] = v.X;
        r.M[5] = v.Y;
        r.M[10] = v.Z;
        r.M[15] = 1;
        return r;
    }

    /// <summary>均一スケール s + 平行移動 t を 1 つの行列にまとめる。</summary>
    public static Mat4 ScaleTrans(double s, Vec3 t)
    {
        var r = Zero();
        r.M[0] = s;
        r.M[3] = t.X;
        r.M[5] = s;
        r.M[7] = t.Y;
        r.M[10] = s;
        r.M[11] = t.Z;
        r.M[15] = 1;
        return r;
    }

    /// <summary>X 軸回りの回転 (ラジアン)。</summary>
    public static Mat4 RotateX(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var r = new Mat4();
        r.M[5] = c;
        r.M[6] = s;
        r.M[9] = -s;
        r.M[10] = c;
        return r;
    }

    /// <summary>Y 軸回りの回転 (ラジアン)。</summary>
    public static Mat4 RotateY(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var r = new Mat4();
        r.M[0] = c;
        r.M[2] = -s;
        r.M[8] = s;
        r.M[10] = c;
        return r;
    }

    /// <summary>Z 軸回りの回転 (ラジアン)。</summary>
    public static Mat4 RotateZ(double angle)
    {
        var c = Math.Cos(angle);
        var s = Math.Sin(angle);
        var r = new Mat4();
        r.M[0] = c;
        r.M[1] = s;
        r.M[4] = -s;
        r.M[5] = c;
        return r;
    }

    /// <summary>任意軸 axis 回りの回転 (ラジアン)。</summary>
    public static Mat4 Rotate(double angle, Vec3 axis) =>
        Quat.FromAxisAngle(axis, angle).ToMat4();

    public static Mat4 FromQuat(Quat q) => q.ToMat4();

    /// <summary>左手系の view 行列。</summary>
    public static Mat4 LookAtLh(Vec3 eye, Vec3 target, Vec3 up)
    {
        var z = target.Sub(eye).Normalize();
        var x = up.Cross(z).Normalize();
        var y = z.Cross(x);
        var r = Zero();
        r.M[0] = x.X;
        r.M[1] = x.Y;
        r.M[2] = x.Z;
        r.M[3] = -x.Dot(eye);
        r.M[4] = y.X;
        r.M[5] = y.Y;
        r.M[6] = y.Z;
        r.M[7] = -y.Dot(eye);
        r.M[8] = z.X;
        r.M[9] = z.Y;
        r.M[10] = z.Z;
        r.M[11] = -z.Dot(eye);
        r.M[12] = 0;
        r.M[13] = 0;
        r.M[14] = 0;
        r.M[15] = 1;
        return r;
    }

    /// <summary>左手系の透視投影。fovDeg は垂直視野角 (度)。depth は [0, 1]。</summary>
    public static Mat4 PerspectiveLh(double fovDeg, double aspect, double nz,
        double fz)
    {
        var f = 1.0 / Math.Tan(fovDeg * Math.PI / 360.0);
        var r = Zero();
        r.M[0] = f / aspect;
        r.M[5] = f;
        r.M[10] = fz / (fz - nz);
        r.M[11] = -fz * nz / (fz - nz);
        r.M[14] = 1;
        return r;
    }

    /// <summary>左手系の平行投影。w / h は view volume の幅と高さ。depth は
    /// [0, 1]。</summary>
    public static Mat4 OrthoLh(double w, double h, double nz, double fz)
    {
        var r = Zero();
        r.M[0] = 2 / w;
        r.M[5] = 2 / h;
        r.M[10] = 1 / (fz - nz);
        r.M[11] = -nz / (fz - nz);
        r.M[15] = 1;
        return r;
    }
}

/// <summary>スカラー演算のユーティリティ。角度変換以外は GLSL の同名関数と
/// 同義。</summary>
public static class MathUtil
{
    /// <summary>度 → ラジアン。</summary>
    public static double Radians(double deg) => deg * (Math.PI / 180.0);

    /// <summary>ラジアン → 度。</summary>
    public static double Degrees(double rad) => rad * (180.0 / Math.PI);

    public static double Clamp(double v, double lo, double hi) =>
        Math.Max(lo, Math.Min(hi, v));

    public static double Saturate(double v) => Clamp(v, 0.0, 1.0);

    public static double Lerp(double a, double b, double t) =>
        a + (b - a) * t;

    public static double Smoothstep(double edge0, double edge1, double x)
    {
        var t = Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    public static double Step(double edge, double x) => x < edge ? 0.0 : 1.0;
}
