// lub's math library.
// Exposes the operator surface through mutable structs + operator overloading.
// Scalar multiplication explicitly defines both overloads (v * s) / (s * v).
// The module name would collide with System.Math, so there is no class Math; Vec2 / Vec3 / Vec4 / Quat / Mat4 / MathUtil are placed flat.
// Conversion to and from the Phys2d/Phys3d wire types (Vec2d / Vec3d / Quat3d) is explicit, with wire() / fromWire().

using System;
using System.Collections.Generic;

namespace Lub;

/// <summary>2D vector.
/// Operators: a+b / a-b / a*b (component-wise product) / a*s / s*a / a/b (component-wise quotient) / a/s / -a.
/// Same-named methods (add / sub / mul / scale / div / negate) are also available.
/// </summary>
public struct Vec2
{
    public float X;
    public float Y;

    public Vec2(float x, float y)
    {
        this.X = x;
        this.Y = y;
    }

    public static Vec2 Zero() => new Vec2(0, 0);

    public static Vec2 One() => new Vec2(1, 1);

    /// <summary>Vector with all components set to v.
    /// </summary>
    public static Vec2 Splat(float v) => new Vec2(v, v);

    public readonly Vec2 Add(Vec2 b) => new Vec2(X + b.X, Y + b.Y);

    public readonly Vec2 Sub(Vec2 b) => new Vec2(X - b.X, Y - b.Y);

    /// <summary>Scalar multiplication.
    /// Both v * s and s * v work as operators.
    /// </summary>
    public readonly Vec2 Scale(float s) => new Vec2(X * s, Y * s);

    public readonly Vec2 Negate() => new Vec2(-X, -Y);

    /// <summary>Component-wise product (Hadamard product).
    /// </summary>
    public readonly Vec2 Mul(Vec2 b) => new Vec2(X * b.X, Y * b.Y);

    /// <summary>Component-wise quotient.
    /// </summary>
    public readonly Vec2 Div(Vec2 b) => new Vec2(X / b.X, Y / b.Y);

    public static Vec2 operator +(Vec2 a, Vec2 b) => a.Add(b);

    public static Vec2 operator -(Vec2 a, Vec2 b) => a.Sub(b);

    public static Vec2 operator *(Vec2 a, Vec2 b) => a.Mul(b);

    public static Vec2 operator *(Vec2 a, float s) => a.Scale(s);

    public static Vec2 operator *(float s, Vec2 a) => a.Scale(s);

    public static Vec2 operator /(Vec2 a, Vec2 b) => a.Div(b);

    public static Vec2 operator /(Vec2 a, float s) =>
        new Vec2(a.X / s, a.Y / s);

    public static Vec2 operator -(Vec2 a) => a.Negate();

    public readonly float Dot(Vec2 b) => X * b.X + Y * b.Y;

    public readonly float LengthSq() => X * X + Y * Y;

    public readonly float Length() => (float)Math.Sqrt(LengthSq());

    /// <summary>Normalization.
    /// A zero vector is returned as a zero vector.
    /// </summary>
    public readonly Vec2 Normalize()
    {
        var len = Length();
        return len > 0 ? new Vec2(X / len, Y / len) : Zero();
    }

    public readonly float DistanceSq(Vec2 b) => Sub(b).LengthSq();

    public readonly float Distance(Vec2 b) => (float)Math.Sqrt(DistanceSq(b));

    public readonly Vec2 Lerp(Vec2 b, float t) =>
        new Vec2(X + (b.X - X) * t, Y + (b.Y - Y) * t);

    public readonly Vec2 Min(Vec2 b) =>
        new Vec2(Math.Min(X, b.X), Math.Min(Y, b.Y));

    public readonly Vec2 Max(Vec2 b) =>
        new Vec2(Math.Max(X, b.X), Math.Max(Y, b.Y));

    public readonly Vec2 Clamp(Vec2 lo, Vec2 hi) =>
        new Vec2(Math.Max(lo.X, Math.Min(hi.X, X)),
            Math.Max(lo.Y, Math.Min(hi.Y, Y)));

    /// <summary>Perpendicular vector rotated 90 degrees counterclockwise (-y, x).
    /// </summary>
    public readonly Vec2 Perp() => new Vec2(-Y, X);

    /// <summary>Angle from the +X axis (radians).
    /// </summary>
    public readonly float Angle() => (float)Math.Atan2(Y, X);

    /// <summary>Converts to the Phys2d wire type.
    /// </summary>
    public readonly Vec2d Wire() => new Vec2d { X = this.X, Y = this.Y };

    /// <summary>Converts from the Phys2d wire type.
    /// </summary>
    public static Vec2 FromWire(Vec2d v) => new Vec2(v.X, v.Y);
}

/// <summary>3D vector.
/// Operators: a+b / a-b / a*b (component-wise product) / a*s / s*a / a/b (component-wise quotient) / a/s / -a.
/// Same-named methods (add / sub / mul / scale / div / negate) are also available.
/// </summary>
public struct Vec3
{
    public float X;
    public float Y;
    public float Z;

    public Vec3(float x, float y, float z)
    {
        this.X = x;
        this.Y = y;
        this.Z = z;
    }

    public static Vec3 Zero() => new Vec3(0, 0, 0);

    public static Vec3 One() => new Vec3(1, 1, 1);

    /// <summary>Vector with all components set to v.
    /// </summary>
    public static Vec3 Splat(float v) => new Vec3(v, v, v);

    public static Vec3 Up() => new Vec3(0, 1, 0);

    public static Vec3 Right() => new Vec3(1, 0, 0);

    /// <summary>Forward +Z in the left-handed system (lookAtLh / perspectiveLh).
    /// </summary>
    public static Vec3 Forward() => new Vec3(0, 0, 1);

    public readonly Vec3 Add(Vec3 b) => new Vec3(X + b.X, Y + b.Y, Z + b.Z);

    public readonly Vec3 Sub(Vec3 b) => new Vec3(X - b.X, Y - b.Y, Z - b.Z);

    /// <summary>Scalar multiplication.
    /// Both v * s and s * v work as operators.
    /// </summary>
    public readonly Vec3 Scale(float s) => new Vec3(X * s, Y * s, Z * s);

    public readonly Vec3 Negate() => new Vec3(-X, -Y, -Z);

    /// <summary>Component-wise product (Hadamard product).
    /// </summary>
    public readonly Vec3 Mul(Vec3 b) => new Vec3(X * b.X, Y * b.Y, Z * b.Z);

    /// <summary>Component-wise quotient.
    /// </summary>
    public readonly Vec3 Div(Vec3 b) => new Vec3(X / b.X, Y / b.Y, Z / b.Z);

    public static Vec3 operator +(Vec3 a, Vec3 b) => a.Add(b);

    public static Vec3 operator -(Vec3 a, Vec3 b) => a.Sub(b);

    public static Vec3 operator *(Vec3 a, Vec3 b) => a.Mul(b);

    public static Vec3 operator *(Vec3 a, float s) => a.Scale(s);

    public static Vec3 operator *(float s, Vec3 a) => a.Scale(s);

    public static Vec3 operator /(Vec3 a, Vec3 b) => a.Div(b);

    public static Vec3 operator /(Vec3 a, float s) =>
        new Vec3(a.X / s, a.Y / s, a.Z / s);

    public static Vec3 operator -(Vec3 a) => a.Negate();

    public readonly float Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;

    public readonly Vec3 Cross(Vec3 b) =>
        new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public readonly float LengthSq() => X * X + Y * Y + Z * Z;

    public readonly float Length() => (float)Math.Sqrt(LengthSq());

    /// <summary>Normalization.
    /// A zero vector is returned as a zero vector.
    /// </summary>
    public readonly Vec3 Normalize()
    {
        var len = Length();
        return len > 0 ? new Vec3(X / len, Y / len, Z / len) : Zero();
    }

    public readonly float DistanceSq(Vec3 b) => Sub(b).LengthSq();

    public readonly float Distance(Vec3 b) => (float)Math.Sqrt(DistanceSq(b));

    public readonly Vec3 Lerp(Vec3 b, float t) =>
        new Vec3(X + (b.X - X) * t, Y + (b.Y - Y) * t, Z + (b.Z - Z) * t);

    public readonly Vec3 Min(Vec3 b) => new Vec3(Math.Min(X, b.X),
        Math.Min(Y, b.Y), Math.Min(Z, b.Z));

    public readonly Vec3 Max(Vec3 b) => new Vec3(Math.Max(X, b.X),
        Math.Max(Y, b.Y), Math.Max(Z, b.Z));

    public readonly Vec3 Clamp(Vec3 lo, Vec3 hi) =>
        new Vec3(Math.Max(lo.X, Math.Min(hi.X, X)),
            Math.Max(lo.Y, Math.Min(hi.Y, Y)),
            Math.Max(lo.Z, Math.Min(hi.Z, Z)));

    /// <summary>Reflection vector about normal (which must be normalized).
    /// </summary>
    public readonly Vec3 Reflect(Vec3 normal) => Sub(normal.Scale(2.0f * Dot(normal)));

    /// <summary>Converts to the Phys3d wire type.
    /// </summary>
    public readonly Vec3d Wire() => new Vec3d { X = this.X, Y = this.Y, Z = this.Z };

    /// <summary>Converts from the Phys3d wire type.
    /// </summary>
    public static Vec3 FromWire(Vec3d v) => new Vec3(v.X, v.Y, v.Z);
}

/// <summary>4D vector (homogeneous coordinates, colors, etc.).
/// Operators: a+b / a-b / a*s / s*a / a/s / -a.
/// </summary>
public struct Vec4
{
    public float X;
    public float Y;
    public float Z;
    public float W;

    public Vec4(float x, float y, float z, float w)
    {
        this.X = x;
        this.Y = y;
        this.Z = z;
        this.W = w;
    }

    public static Vec4 Zero() => new Vec4(0, 0, 0, 0);

    public static Vec4 One() => new Vec4(1, 1, 1, 1);

    /// <summary>Extends a Vec3 by adding w.
    /// w=1 for a position, w=0 for a direction.
    /// </summary>
    public static Vec4 FromVec3(Vec3 v, float w) => new Vec4(v.X, v.Y, v.Z, w);

    public readonly Vec4 Add(Vec4 b) => new Vec4(X + b.X, Y + b.Y, Z + b.Z, W + b.W);

    public readonly Vec4 Sub(Vec4 b) => new Vec4(X - b.X, Y - b.Y, Z - b.Z, W - b.W);

    /// <summary>Scalar multiplication.
    /// Both v * s and s * v work as operators.
    /// </summary>
    public readonly Vec4 Scale(float s) => new Vec4(X * s, Y * s, Z * s, W * s);

    public readonly Vec4 Negate() => new Vec4(-X, -Y, -Z, -W);

    public static Vec4 operator +(Vec4 a, Vec4 b) => a.Add(b);

    public static Vec4 operator -(Vec4 a, Vec4 b) => a.Sub(b);

    public static Vec4 operator *(Vec4 a, float s) => a.Scale(s);

    public static Vec4 operator *(float s, Vec4 a) => a.Scale(s);

    public static Vec4 operator /(Vec4 a, float s) =>
        new Vec4(a.X / s, a.Y / s, a.Z / s, a.W / s);

    public static Vec4 operator -(Vec4 a) => a.Negate();

    public readonly float Dot(Vec4 b) => X * b.X + Y * b.Y + Z * b.Z + W * b.W;

    public readonly float LengthSq() => X * X + Y * Y + Z * Z + W * W;

    public readonly float Length() => (float)Math.Sqrt(LengthSq());

    /// <summary>Normalization.
    /// A zero vector is returned as a zero vector.
    /// </summary>
    public readonly Vec4 Normalize()
    {
        var len = Length();
        return len > 0 ? new Vec4(X / len, Y / len, Z / len, W / len) : Zero();
    }

    public readonly Vec4 Lerp(Vec4 b, float t) => new Vec4(X + (b.X - X) * t,
        Y + (b.Y - Y) * t, Z + (b.Z - Z) * t, W + (b.W - W) * t);

    public readonly Vec3 Xyz() => new Vec3(X, Y, Z);
}

/// <summary>Quaternion representing a rotation (Hamilton product, active rotation by the right-hand rule; same convention as Phys3d).
/// Operators: a * b (composition of rotations), q * v (rotation of a Vec3 = rotateVec3).
/// All angles are in radians.
/// </summary>
public readonly struct Quat
{
    public readonly float X;
    public readonly float Y;
    public readonly float Z;
    public readonly float W;

    public Quat(float x, float y, float z, float w)
    {
        this.X = x;
        this.Y = y;
        this.Z = z;
        this.W = w;
    }

    public static Quat Identity() => new Quat(0, 0, 0, 1);

    /// <summary>Rotation by angle radians around axis.
    /// axis is normalized internally.
    /// </summary>
    public static Quat FromAxisAngle(Vec3 axis, float angle)
    {
        var half = angle * 0.5f;
        var s = (float)Math.Sin(half);
        var n = axis.Normalize();
        return new Quat(n.X * s, n.Y * s, n.Z * s, (float)Math.Cos(half));
    }

    /// <summary>Creates from Euler angles (radians).
    /// Applied in the order roll (X) → pitch (Y) → yaw (Z).
    /// </summary>
    public static Quat FromEuler(float yaw, float pitch, float roll)
    {
        var cy = (float)Math.Cos(yaw * 0.5f);
        var sy = (float)Math.Sin(yaw * 0.5f);
        var cp = (float)Math.Cos(pitch * 0.5f);
        var sp = (float)Math.Sin(pitch * 0.5f);
        var cr = (float)Math.Cos(roll * 0.5f);
        var sr = (float)Math.Sin(roll * 0.5f);
        return new Quat(sr * cp * cy - cr * sp * sy,
            cr * sp * cy + sr * cp * sy, cr * cp * sy - sr * sp * cy,
            cr * cp * cy + sr * sp * sy);
    }

    /// <summary>Composition of rotations.
    /// a * b means "rotate by b, then by a".
    /// </summary>
    public Quat Mul(Quat b) =>
        new Quat(W * b.X + X * b.W + Y * b.Z - Z * b.Y,
            W * b.Y - X * b.Z + Y * b.W + Z * b.X,
            W * b.Z + X * b.Y - Y * b.X + Z * b.W,
            W * b.W - X * b.X - Y * b.Y - Z * b.Z);

    public static Quat operator *(Quat a, Quat b) => a.Mul(b);

    public static Vec3 operator *(Quat q, Vec3 v) => q.RotateVec3(v);

    public float Dot(Quat b) => X * b.X + Y * b.Y + Z * b.Z + W * b.W;

    public float LengthSq() => X * X + Y * Y + Z * Z + W * W;

    public float Length() => (float)Math.Sqrt(LengthSq());

    /// <summary>Normalization.
    /// A zero quaternion returns identity.
    /// </summary>
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
            var inv = 1.0f / lsq;
            return new Quat(-X * inv, -Y * inv, -Z * inv, W * inv);
        }
        return Identity();
    }

    /// <summary>Component-wise linear interpolation.
    /// It does not normalize, so insert normalize if needed.
    /// </summary>
    public Quat Lerp(Quat b, float t) => new Quat(X + (b.X - X) * t,
        Y + (b.Y - Y) * t, Z + (b.Z - Z) * t, W + (b.W - W) * t);

    /// <summary>Spherical linear interpolation.
    /// </summary>
    public Quat Slerp(Quat b, float t)
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
        if (d > 0.9995f)
        {
            return new Quat(X + (bx - X) * t, Y + (by - Y) * t,
                Z + (bz - Z) * t, W + (bw - W) * t).Normalize();
        }
        // tcs does not support Math.Acos, so this is written in the equivalent atan2 form (acos(d) == atan2(sqrt(1 - d^2), d) for |d| <= 1)
        var theta = (float)Math.Atan2((float)Math.Sqrt(1.0f - d * d), d);
        var sinT = (float)Math.Sin(theta);
        var s0 = (float)Math.Sin((1.0f - t) * theta) / sinT;
        var s1 = (float)Math.Sin(t * theta) / sinT;
        return new Quat(X * s0 + bx * s1, Y * s0 + by * s1, Z * s0 + bz * s1,
            W * s0 + bw * s1);
    }

    /// <summary>Rotates a vector.
    /// Can also be called with the operator q * v.
    /// </summary>
    public Vec3 RotateVec3(Vec3 v)
    {
        var qv = new Vec3(X, Y, Z);
        var uv = qv.Cross(v);
        var uuv = qv.Cross(uv);
        return v.Add(uv.Scale(2.0f * W).Add(uuv.Scale(2.0f)));
    }

    /// <summary>Matrix that performs the same rotation as RotateVec3 (multiplied from the left onto column vectors).
    /// Pass a normalized quaternion.
    /// Same active rotation as box3d / sdf.c, and Mat4.RotateX/Y/Z are aligned to it as well.
    /// </summary>
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
        r.M00 = 1 - (yy + zz);
        r.M01 = xy - wz;
        r.M02 = xz + wy;
        r.M03 = 0;
        r.M10 = xy + wz;
        r.M11 = 1 - (xx + zz);
        r.M12 = yz - wx;
        r.M13 = 0;
        r.M20 = xz - wy;
        r.M21 = yz + wx;
        r.M22 = 1 - (xx + yy);
        r.M23 = 0;
        r.M30 = 0;
        r.M31 = 0;
        r.M32 = 0;
        r.M33 = 1;
        return r;
    }

    /// <summary>Extracts a quaternion from a rotation matrix (same convention as ToMat4).
    /// </summary>
    public static Quat FromMat4(Mat4 m)
    {
        var trace = m.M00 + m.M11 + m.M22;
        if (trace > 0)
        {
            var s = 0.5f / (float)Math.Sqrt(trace + 1.0f);
            return new Quat((m.M21 - m.M12) * s, (m.M02 - m.M20) * s,
                (m.M10 - m.M01) * s, 0.25f / s);
        }
        else if (m.M00 > m.M11 && m.M00 > m.M22)
        {
            var s = 2.0f * (float)Math.Sqrt(1.0f + m.M00 - m.M11 - m.M22);
            return new Quat(0.25f * s, (m.M01 + m.M10) / s,
                (m.M20 + m.M02) / s, (m.M21 - m.M12) / s);
        }
        else if (m.M11 > m.M22)
        {
            var s = 2.0f * (float)Math.Sqrt(1.0f + m.M11 - m.M00 - m.M22);
            return new Quat((m.M01 + m.M10) / s, 0.25f * s,
                (m.M12 + m.M21) / s, (m.M02 - m.M20) / s);
        }
        else
        {
            var s = 2.0f * (float)Math.Sqrt(1.0f + m.M22 - m.M00 - m.M11);
            return new Quat((m.M20 + m.M02) / s, (m.M12 + m.M21) / s,
                0.25f * s, (m.M10 - m.M01) / s);
        }
    }

    /// <summary>Converts to the Phys3d wire type.
    /// </summary>
    public Quat3d Wire() =>
        new Quat3d { X = this.X, Y = this.Y, Z = this.Z, W = this.W };

    /// <summary>Converts from the Phys3d wire type.
    /// </summary>
    public static Quat FromWire(Quat3d q) => new Quat(q.X, q.Y, q.Z, q.W);
}

/// <summary>4x4 matrix (row-major, m[row * 4 + col]).
/// Operators:
/// a * b (matrix product), m * v (product with a Vec4 = mulVec4).
/// MVP is composed in the order proj * view * model.
/// </summary>
public struct Mat4
{
    public float M00, M01, M02, M03;
    public float M10, M11, M12, M13;
    public float M20, M21, M22, M23;
    public float M30, M31, M32, M33;

    public static Mat4 Identity()
    {
        var r = new Mat4();
        r.M00 = 1;
        r.M11 = 1;
        r.M22 = 1;
        r.M33 = 1;
        return r;
    }

    public static Mat4 Zero() => new Mat4();

    /// <summary>Reads 16 row-major elements from list at offset.
    /// </summary>
    public static Mat4 FromList(List<float> v, int offset)
    {
        var r = new Mat4();
        r.M00 = v[offset + 0];
        r.M01 = v[offset + 1];
        r.M02 = v[offset + 2];
        r.M03 = v[offset + 3];
        r.M10 = v[offset + 4];
        r.M11 = v[offset + 5];
        r.M12 = v[offset + 6];
        r.M13 = v[offset + 7];
        r.M20 = v[offset + 8];
        r.M21 = v[offset + 9];
        r.M22 = v[offset + 10];
        r.M23 = v[offset + 11];
        r.M30 = v[offset + 12];
        r.M31 = v[offset + 13];
        r.M32 = v[offset + 14];
        r.M33 = v[offset + 15];
        return r;
    }

    /// <summary>The 16 elements in row-major order.
    /// Used when passing to uniforms or buffers.
    /// </summary>
    public readonly List<float> ToList() => new List<float>
    {
        M00, M01, M02, M03, M10, M11, M12, M13,
        M20, M21, M22, M23, M30, M31, M32, M33,
    };

    /// <summary>Matrix product.
    /// Can also be called with the operator a * b.
    /// </summary>
    public readonly Mat4 Mul(Mat4 b)
    {
        var r = Zero();
        r.M00 = M00 * b.M00 + M01 * b.M10 + M02 * b.M20 + M03 * b.M30;
        r.M01 = M00 * b.M01 + M01 * b.M11 + M02 * b.M21 + M03 * b.M31;
        r.M02 = M00 * b.M02 + M01 * b.M12 + M02 * b.M22 + M03 * b.M32;
        r.M03 = M00 * b.M03 + M01 * b.M13 + M02 * b.M23 + M03 * b.M33;
        r.M10 = M10 * b.M00 + M11 * b.M10 + M12 * b.M20 + M13 * b.M30;
        r.M11 = M10 * b.M01 + M11 * b.M11 + M12 * b.M21 + M13 * b.M31;
        r.M12 = M10 * b.M02 + M11 * b.M12 + M12 * b.M22 + M13 * b.M32;
        r.M13 = M10 * b.M03 + M11 * b.M13 + M12 * b.M23 + M13 * b.M33;
        r.M20 = M20 * b.M00 + M21 * b.M10 + M22 * b.M20 + M23 * b.M30;
        r.M21 = M20 * b.M01 + M21 * b.M11 + M22 * b.M21 + M23 * b.M31;
        r.M22 = M20 * b.M02 + M21 * b.M12 + M22 * b.M22 + M23 * b.M32;
        r.M23 = M20 * b.M03 + M21 * b.M13 + M22 * b.M23 + M23 * b.M33;
        r.M30 = M30 * b.M00 + M31 * b.M10 + M32 * b.M20
            + M33 * b.M30;
        r.M31 = M30 * b.M01 + M31 * b.M11 + M32 * b.M21
            + M33 * b.M31;
        r.M32 = M30 * b.M02 + M31 * b.M12 + M32 * b.M22
            + M33 * b.M32;
        r.M33 = M30 * b.M03 + M31 * b.M13 + M32 * b.M23
            + M33 * b.M33;
        return r;
    }

    /// <summary>Product with a Vec4.
    /// Can also be called with the operator m * v.
    /// </summary>
    public readonly Vec4 MulVec4(Vec4 v)
    {
        return new Vec4(
            M00 * v.X + M01 * v.Y + M02 * v.Z + M03 * v.W,
            M10 * v.X + M11 * v.Y + M12 * v.Z + M13 * v.W,
            M20 * v.X + M21 * v.Y + M22 * v.Z + M23 * v.W,
            M30 * v.X + M31 * v.Y + M32 * v.Z + M33 * v.W);
    }

    public static Mat4 operator *(Mat4 a, Mat4 b) => a.Mul(b);

    public static Vec4 operator *(Mat4 a, Vec4 v) => a.MulVec4(v);

    /// <summary>Transforms as a position (treated as w=1; translation applies).
    /// </summary>
    public readonly Vec3 MulPoint(Vec3 v)
    {
        return new Vec3(M00 * v.X + M01 * v.Y + M02 * v.Z + M03,
            M10 * v.X + M11 * v.Y + M12 * v.Z + M13,
            M20 * v.X + M21 * v.Y + M22 * v.Z + M23);
    }

    /// <summary>Transforms as a direction (treated as w=0; translation is ignored).
    /// </summary>
    public readonly Vec3 MulDir(Vec3 v)
    {
        return new Vec3(M00 * v.X + M01 * v.Y + M02 * v.Z,
            M10 * v.X + M11 * v.Y + M12 * v.Z,
            M20 * v.X + M21 * v.Y + M22 * v.Z);
    }

    public readonly Vec3 Mat3MulVec3(Vec3 v) => MulDir(v);

    public readonly Mat4 Transpose()
    {
        var r = Zero();
        r.M00 = M00;
        r.M10 = M01;
        r.M20 = M02;
        r.M30 = M03;
        r.M01 = M10;
        r.M11 = M11;
        r.M21 = M12;
        r.M31 = M13;
        r.M02 = M20;
        r.M12 = M21;
        r.M22 = M22;
        r.M32 = M23;
        r.M03 = M30;
        r.M13 = M31;
        r.M23 = M32;
        r.M33 = M33;
        return r;
    }

    public readonly float Determinant()
    {
        var a00 = M00;
        var a01 = M01;
        var a02 = M02;
        var a03 = M03;
        var a10 = M10;
        var a11 = M11;
        var a12 = M12;
        var a13 = M13;
        var a20 = M20;
        var a21 = M21;
        var a22 = M22;
        var a23 = M23;
        var a30 = M30;
        var a31 = M31;
        var a32 = M32;
        var a33 = M33;
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

    /// <summary>Inverse matrix.
    /// Returns the identity matrix for a singular matrix (det=0).
    /// </summary>
    public readonly Mat4 Inverse()
    {
        var a00 = M00;
        var a01 = M01;
        var a02 = M02;
        var a03 = M03;
        var a10 = M10;
        var a11 = M11;
        var a12 = M12;
        var a13 = M13;
        var a20 = M20;
        var a21 = M21;
        var a22 = M22;
        var a23 = M23;
        var a30 = M30;
        var a31 = M31;
        var a32 = M32;
        var a33 = M33;

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
        var inv = 1.0f / det;
        var r = Zero();
        r.M00 = (a11 * b11 - a12 * b10 + a13 * b09) * inv;
        r.M01 = (-a01 * b11 + a02 * b10 - a03 * b09) * inv;
        r.M02 = (a31 * b05 - a32 * b04 + a33 * b03) * inv;
        r.M03 = (-a21 * b05 + a22 * b04 - a23 * b03) * inv;
        r.M10 = (-a10 * b11 + a12 * b08 - a13 * b07) * inv;
        r.M11 = (a00 * b11 - a02 * b08 + a03 * b07) * inv;
        r.M12 = (-a30 * b05 + a32 * b02 - a33 * b01) * inv;
        r.M13 = (a20 * b05 - a22 * b02 + a23 * b01) * inv;
        r.M20 = (a10 * b10 - a11 * b08 + a13 * b06) * inv;
        r.M21 = (-a00 * b10 + a01 * b08 - a03 * b06) * inv;
        r.M22 = (a30 * b04 - a31 * b02 + a33 * b00) * inv;
        r.M23 = (-a20 * b04 + a21 * b02 - a23 * b00) * inv;
        r.M30 = (-a10 * b09 + a11 * b07 - a12 * b06) * inv;
        r.M31 = (a00 * b09 - a01 * b07 + a02 * b06) * inv;
        r.M32 = (-a30 * b03 + a31 * b01 - a32 * b00) * inv;
        r.M33 = (a20 * b03 - a21 * b01 + a22 * b00) * inv;
        return r;
    }

    /// <summary>Inverts a view matrix that has only rotation + translation, by transposing the rotation + replacing eye.
    /// </summary>
    public readonly Mat4 RigidInverse(Vec3 eye)
    {
        var r = Zero();
        r.M00 = M00;
        r.M01 = M10;
        r.M02 = M20;
        r.M03 = eye.X;
        r.M10 = M01;
        r.M11 = M11;
        r.M12 = M21;
        r.M13 = eye.Y;
        r.M20 = M02;
        r.M21 = M12;
        r.M22 = M22;
        r.M23 = eye.Z;
        r.M30 = 0;
        r.M31 = 0;
        r.M32 = 0;
        r.M33 = 1;
        return r;
    }

    public static Mat4 Translate(Vec3 v)
    {
        var r = Identity();
        r.M03 = v.X;
        r.M13 = v.Y;
        r.M23 = v.Z;
        return r;
    }

    public static Mat4 Scale(Vec3 v)
    {
        var r = Zero();
        r.M00 = v.X;
        r.M11 = v.Y;
        r.M22 = v.Z;
        r.M33 = 1;
        return r;
    }

    /// <summary>Combines uniform scale s + translation t into one matrix.
    /// </summary>
    public static Mat4 ScaleTrans(float s, Vec3 t)
    {
        var r = Zero();
        r.M00 = s;
        r.M03 = t.X;
        r.M11 = s;
        r.M13 = t.Y;
        r.M22 = s;
        r.M23 = t.Z;
        r.M33 = 1;
        return r;
    }

    /// <summary>Rotation around the X axis (radians).
    /// +π/2 rotates +Y to +Z.
    /// </summary>
    public static Mat4 RotateX(float angle)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        var r = Identity();
        r.M11 = c;
        r.M12 = -s;
        r.M21 = s;
        r.M22 = c;
        return r;
    }

    /// <summary>Rotation around the Y axis (radians).
    /// +π/2 rotates +Z to +X.
    /// </summary>
    public static Mat4 RotateY(float angle)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        var r = Identity();
        r.M00 = c;
        r.M02 = s;
        r.M20 = -s;
        r.M22 = c;
        return r;
    }

    /// <summary>Rotation around the Z axis (radians).
    /// +π/2 rotates +X to +Y.
    /// </summary>
    public static Mat4 RotateZ(float angle)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        var r = Identity();
        r.M00 = c;
        r.M01 = -s;
        r.M10 = s;
        r.M11 = c;
        return r;
    }

    /// <summary>Rotation around an arbitrary axis (radians).
    /// Same direction as Quat.FromAxisAngle.
    /// </summary>
    public static Mat4 Rotate(float angle, Vec3 axis) =>
        Quat.FromAxisAngle(axis, angle).ToMat4();

    public static Mat4 FromQuat(Quat q) => q.ToMat4();

    /// <summary>Left-handed view matrix.
    /// </summary>
    public static Mat4 LookAtLh(Vec3 eye, Vec3 target, Vec3 up)
    {
        var z = target.Sub(eye).Normalize();
        var x = up.Cross(z).Normalize();
        var y = z.Cross(x);
        var r = Zero();
        r.M00 = x.X;
        r.M01 = x.Y;
        r.M02 = x.Z;
        r.M03 = -x.Dot(eye);
        r.M10 = y.X;
        r.M11 = y.Y;
        r.M12 = y.Z;
        r.M13 = -y.Dot(eye);
        r.M20 = z.X;
        r.M21 = z.Y;
        r.M22 = z.Z;
        r.M23 = -z.Dot(eye);
        r.M30 = 0;
        r.M31 = 0;
        r.M32 = 0;
        r.M33 = 1;
        return r;
    }

    /// <summary>Left-handed perspective projection.
    /// fovDeg is the vertical field of view (degrees).
    /// depth is [0, 1].
    /// </summary>
    public static Mat4 PerspectiveLh(float fovDeg, float aspect, float nz,
        float fz)
    {
        var f = 1.0f / (float)Math.Tan(fovDeg * (float)Math.PI / 360.0f);
        var r = Zero();
        r.M00 = f / aspect;
        r.M11 = f;
        r.M22 = fz / (fz - nz);
        r.M23 = -fz * nz / (fz - nz);
        r.M32 = 1;
        return r;
    }

    /// <summary>Left-handed orthographic projection.
    /// w / h are the width and height of the view volume.
    /// depth is [0, 1].
    /// </summary>
    public static Mat4 OrthoLh(float w, float h, float nz, float fz)
    {
        var r = Zero();
        r.M00 = 2 / w;
        r.M11 = 2 / h;
        r.M22 = 1 / (fz - nz);
        r.M23 = -nz / (fz - nz);
        r.M33 = 1;
        return r;
    }
}

/// <summary>Half-line with an origin and a direction.
/// Dir is stored normalized.
/// </summary>
public readonly struct Ray
{
    public readonly Vec3 Origin;
    public readonly Vec3 Dir;

    public Ray(Vec3 origin, Vec3 dir)
    {
        this.Origin = origin;
        this.Dir = dir.Normalize();
    }

    /// <summary>Point advanced by t from the origin.
    /// </summary>
    public Vec3 At(float t) => Origin + Dir * t;

    /// <summary>Distance t to the intersection with the plane (point on the plane: point, normal: normal) (At(t) is the intersection).
    /// null if the ray is parallel to the plane or the intersection is behind the origin.
    /// </summary>
    public float? IntersectPlane(Vec3 point, Vec3 normal)
    {
        var denom = Dir.Dot(normal);
        if (Math.Abs(denom) < 1e-6f)
        {
            return null;
        }
        var t = (point - Origin).Dot(normal) / denom;
        if (t < 0.0f)
        {
            return null;
        }
        return t;
    }
}

/// <summary>Scalar math utilities.
/// Except for angle conversion, these mean the same as the GLSL functions of the same names.
/// </summary>
public static class MathUtil
{
    /// <summary>Degrees → radians.
    /// </summary>
    public static float Radians(float deg) => deg * ((float)Math.PI / 180.0f);

    /// <summary>Radians → degrees.
    /// </summary>
    public static float Degrees(float rad) => rad * (180.0f / (float)Math.PI);

    public static float Clamp(float v, float lo, float hi) =>
        Math.Max(lo, Math.Min(hi, v));

    public static float Saturate(float v) => Clamp(v, 0.0f, 1.0f);

    public static float Lerp(float a, float b, float t) =>
        a + (b - a) * t;

    public static float Smoothstep(float edge0, float edge1, float x)
    {
        var t = Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    public static float Step(float edge, float x) => x < edge ? 0.0f : 1.0f;
}
