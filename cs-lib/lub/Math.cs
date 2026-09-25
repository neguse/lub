// lub の数学ライブラリ。public class + operator オーバーロード (tcs が Lua
// metamethod に写像) で演算子面を出す。スカラー倍は (v * s) / (s * v) の
// 両 overload を明示定義する。
// モジュール名は System.Math と衝突するため class Math は作らず、
// Vec2 / Vec3 / Vec4 / Quat / Mat4 / MathUtil をフラットに置く。
// Phys2d/Phys3d の wire 型 (Vec2d / Vec3d / Quat3d) とは wire() / fromWire()
// で明示変換する。
//
// 演算子と結果を返すメソッドは呼ぶたびに新しいオブジェクトを確保する。確保
// しない版として、受け手を書き換えて this を返す XxxInPlace (受け手と引数の
// 演算) / SetXxx (引数だけから計算して代入) と、出力先を渡す XxxInto を置く。
// どれも確保版と同じ演算順で書き、値は確保版とビット単位で一致させる
// (golden がこの値に依存する)。受け手や出力先が引数と同じオブジェクトでも
// よいように、書き込む前に入力を全部読む (成分ごとの演算は同じ成分しか
// 読まないのでそのまま書ける)。
// new 1 回の式で書ける確保版はそのまま残し、一時オブジェクトを作るものと
// 計算が長いもの (Mat4 の生成など) は確保版を new T().SetXxx(...) にして
// 計算を 1 か所に置く。単位行列から数成分だけ変える Mat4.Translate と
// RotateX/Y/Z は、変わる成分だけ書くほうが速いので確保版に式を残す。
// readonly struct にはしない。tcs の struct は static member と演算子を
// 持てず (TCS1001)、Lua では struct も値ごとに table を 1 つ作るので確保は
// 消えない。tcs が struct の演算子に対応したら改めて検討する。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>2 次元ベクトル。演算子: a+b / a-b / a*b (成分積) / a*s / s*a /
/// a/b (成分商) / a/s / -a。同名メソッド (add / sub / mul / scale / div /
/// negate) も使える。演算子と結果を返すメソッドは呼ぶたびに確保する。
/// 確保しない版は受け手を書き換えて this を返す AddInPlace / SetAdd など
/// で、値は確保版と一致する。class なのは、tcs の struct が演算子と static
/// member を持てず、Lua では struct も値ごとに table を作るため。</summary>
public class Vec2
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

    /// <summary>全成分が v のベクトル。</summary>
    public static Vec2 Splat(float v) => new Vec2(v, v);

    public Vec2 Add(Vec2 b) => new Vec2(X + b.X, Y + b.Y);

    public Vec2 Sub(Vec2 b) => new Vec2(X - b.X, Y - b.Y);

    /// <summary>スカラー倍。演算子は v * s と s * v の両方が使える。</summary>
    public Vec2 Scale(float s) => new Vec2(X * s, Y * s);

    public Vec2 Negate() => new Vec2(-X, -Y);

    /// <summary>成分ごとの積 (Hadamard 積)。</summary>
    public Vec2 Mul(Vec2 b) => new Vec2(X * b.X, Y * b.Y);

    /// <summary>成分ごとの商。</summary>
    public Vec2 Div(Vec2 b) => new Vec2(X / b.X, Y / b.Y);

    public static Vec2 operator +(Vec2 a, Vec2 b) => a.Add(b);

    public static Vec2 operator -(Vec2 a, Vec2 b) => a.Sub(b);

    public static Vec2 operator *(Vec2 a, Vec2 b) => a.Mul(b);

    public static Vec2 operator *(Vec2 a, float s) => a.Scale(s);

    public static Vec2 operator *(float s, Vec2 a) => a.Scale(s);

    public static Vec2 operator /(Vec2 a, Vec2 b) => a.Div(b);

    public static Vec2 operator /(Vec2 a, float s) =>
        new Vec2(a.X / s, a.Y / s);

    public static Vec2 operator -(Vec2 a) => a.Negate();

    public float Dot(Vec2 b) => X * b.X + Y * b.Y;

    public float LengthSq() => X * X + Y * Y;

    public float Length() => (float)Math.Sqrt(LengthSq());

    /// <summary>正規化。零ベクトルは零ベクトルのまま返す。</summary>
    public Vec2 Normalize()
    {
        var len = Length();
        return len > 0 ? new Vec2(X / len, Y / len) : Zero();
    }

    public float DistanceSq(Vec2 b)
    {
        var dx = X - b.X;
        var dy = Y - b.Y;
        return dx * dx + dy * dy;
    }

    public float Distance(Vec2 b) => (float)Math.Sqrt(DistanceSq(b));

    public Vec2 Lerp(Vec2 b, float t) =>
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
    public float Angle() => (float)Math.Atan2(Y, X);

    /// <summary>Phys2d の wire 型へ変換する。</summary>
    public Vec2d Wire() => new Vec2d { X = this.X, Y = this.Y };

    /// <summary>Phys2d の wire 型から変換する。</summary>
    public static Vec2 FromWire(Vec2d v) => new Vec2(v.X, v.Y);

    // --- 確保しない版 (受け手を書き換えて this を返す) ---

    public Vec2 Set(float x, float y)
    {
        X = x;
        Y = y;
        return this;
    }

    public Vec2 CopyFrom(Vec2 v)
    {
        X = v.X;
        Y = v.Y;
        return this;
    }

    public Vec2 AddInPlace(Vec2 b)
    {
        X = X + b.X;
        Y = Y + b.Y;
        return this;
    }

    public Vec2 SubInPlace(Vec2 b)
    {
        X = X - b.X;
        Y = Y - b.Y;
        return this;
    }

    public Vec2 ScaleInPlace(float s)
    {
        X = X * s;
        Y = Y * s;
        return this;
    }

    /// <summary>this += b * s。</summary>
    public Vec2 AddScaledInPlace(Vec2 b, float s)
    {
        X = X + b.X * s;
        Y = Y + b.Y * s;
        return this;
    }

    public Vec2 NegateInPlace()
    {
        X = -X;
        Y = -Y;
        return this;
    }

    public Vec2 MulInPlace(Vec2 b)
    {
        X = X * b.X;
        Y = Y * b.Y;
        return this;
    }

    public Vec2 DivInPlace(Vec2 b)
    {
        X = X / b.X;
        Y = Y / b.Y;
        return this;
    }

    public Vec2 NormalizeInPlace()
    {
        var len = Length();
        if (len > 0)
        {
            X = X / len;
            Y = Y / len;
        }
        else
        {
            X = 0;
            Y = 0;
        }
        return this;
    }

    public Vec2 LerpInPlace(Vec2 b, float t)
    {
        X = X + (b.X - X) * t;
        Y = Y + (b.Y - Y) * t;
        return this;
    }

    public Vec2 MinInPlace(Vec2 b)
    {
        X = Math.Min(X, b.X);
        Y = Math.Min(Y, b.Y);
        return this;
    }

    public Vec2 MaxInPlace(Vec2 b)
    {
        X = Math.Max(X, b.X);
        Y = Math.Max(Y, b.Y);
        return this;
    }

    public Vec2 ClampInPlace(Vec2 lo, Vec2 hi)
    {
        X = Math.Max(lo.X, Math.Min(hi.X, X));
        Y = Math.Max(lo.Y, Math.Min(hi.Y, Y));
        return this;
    }

    public Vec2 PerpInPlace() => Set(-Y, X);

    public Vec2 SetAdd(Vec2 a, Vec2 b)
    {
        X = a.X + b.X;
        Y = a.Y + b.Y;
        return this;
    }

    public Vec2 SetSub(Vec2 a, Vec2 b)
    {
        X = a.X - b.X;
        Y = a.Y - b.Y;
        return this;
    }

    public Vec2 SetScaled(Vec2 a, float s)
    {
        X = a.X * s;
        Y = a.Y * s;
        return this;
    }

    public Vec2 SetLerp(Vec2 a, Vec2 b, float t)
    {
        X = a.X + (b.X - a.X) * t;
        Y = a.Y + (b.Y - a.Y) * t;
        return this;
    }
}

/// <summary>3 次元ベクトル。演算子: a+b / a-b / a*b (成分積) / a*s / s*a /
/// a/b (成分商) / a/s / -a。同名メソッド (add / sub / mul / scale / div /
/// negate) も使える。確保しない版 (AddInPlace / SetAdd / SetCross など) と
/// class である理由は Vec2 と同じ。</summary>
public class Vec3
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

    /// <summary>全成分が v のベクトル。</summary>
    public static Vec3 Splat(float v) => new Vec3(v, v, v);

    public static Vec3 Up() => new Vec3(0, 1, 0);

    public static Vec3 Right() => new Vec3(1, 0, 0);

    /// <summary>左手系 (lookAtLh / perspectiveLh) の前方 +Z。</summary>
    public static Vec3 Forward() => new Vec3(0, 0, 1);

    public Vec3 Add(Vec3 b) => new Vec3(X + b.X, Y + b.Y, Z + b.Z);

    public Vec3 Sub(Vec3 b) => new Vec3(X - b.X, Y - b.Y, Z - b.Z);

    /// <summary>スカラー倍。演算子は v * s と s * v の両方が使える。</summary>
    public Vec3 Scale(float s) => new Vec3(X * s, Y * s, Z * s);

    public Vec3 Negate() => new Vec3(-X, -Y, -Z);

    /// <summary>成分ごとの積 (Hadamard 積)。</summary>
    public Vec3 Mul(Vec3 b) => new Vec3(X * b.X, Y * b.Y, Z * b.Z);

    /// <summary>成分ごとの商。</summary>
    public Vec3 Div(Vec3 b) => new Vec3(X / b.X, Y / b.Y, Z / b.Z);

    public static Vec3 operator +(Vec3 a, Vec3 b) => a.Add(b);

    public static Vec3 operator -(Vec3 a, Vec3 b) => a.Sub(b);

    public static Vec3 operator *(Vec3 a, Vec3 b) => a.Mul(b);

    public static Vec3 operator *(Vec3 a, float s) => a.Scale(s);

    public static Vec3 operator *(float s, Vec3 a) => a.Scale(s);

    public static Vec3 operator /(Vec3 a, Vec3 b) => a.Div(b);

    public static Vec3 operator /(Vec3 a, float s) =>
        new Vec3(a.X / s, a.Y / s, a.Z / s);

    public static Vec3 operator -(Vec3 a) => a.Negate();

    public float Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;

    public Vec3 Cross(Vec3 b) =>
        new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);

    public float LengthSq() => X * X + Y * Y + Z * Z;

    public float Length() => (float)Math.Sqrt(LengthSq());

    /// <summary>正規化。零ベクトルは零ベクトルのまま返す。</summary>
    public Vec3 Normalize()
    {
        var len = Length();
        return len > 0 ? new Vec3(X / len, Y / len, Z / len) : Zero();
    }

    public float DistanceSq(Vec3 b)
    {
        var dx = X - b.X;
        var dy = Y - b.Y;
        var dz = Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    public float Distance(Vec3 b) => (float)Math.Sqrt(DistanceSq(b));

    public Vec3 Lerp(Vec3 b, float t) =>
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
    public Vec3 Reflect(Vec3 normal) => new Vec3(X, Y, Z).ReflectInPlace(normal);

    /// <summary>Phys3d の wire 型へ変換する。</summary>
    public Vec3d Wire() => new Vec3d { X = this.X, Y = this.Y, Z = this.Z };

    /// <summary>Phys3d の wire 型から変換する。</summary>
    public static Vec3 FromWire(Vec3d v) => new Vec3(v.X, v.Y, v.Z);

    // --- 確保しない版 (受け手を書き換えて this を返す) ---

    public Vec3 Set(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
        return this;
    }

    public Vec3 CopyFrom(Vec3 v)
    {
        X = v.X;
        Y = v.Y;
        Z = v.Z;
        return this;
    }

    public Vec3 AddInPlace(Vec3 b)
    {
        X = X + b.X;
        Y = Y + b.Y;
        Z = Z + b.Z;
        return this;
    }

    public Vec3 SubInPlace(Vec3 b)
    {
        X = X - b.X;
        Y = Y - b.Y;
        Z = Z - b.Z;
        return this;
    }

    public Vec3 ScaleInPlace(float s)
    {
        X = X * s;
        Y = Y * s;
        Z = Z * s;
        return this;
    }

    /// <summary>this += b * s。</summary>
    public Vec3 AddScaledInPlace(Vec3 b, float s)
    {
        X = X + b.X * s;
        Y = Y + b.Y * s;
        Z = Z + b.Z * s;
        return this;
    }

    public Vec3 NegateInPlace()
    {
        X = -X;
        Y = -Y;
        Z = -Z;
        return this;
    }

    public Vec3 MulInPlace(Vec3 b)
    {
        X = X * b.X;
        Y = Y * b.Y;
        Z = Z * b.Z;
        return this;
    }

    public Vec3 DivInPlace(Vec3 b)
    {
        X = X / b.X;
        Y = Y / b.Y;
        Z = Z / b.Z;
        return this;
    }

    public Vec3 NormalizeInPlace()
    {
        var len = Length();
        if (len > 0)
        {
            X = X / len;
            Y = Y / len;
            Z = Z / len;
        }
        else
        {
            X = 0;
            Y = 0;
            Z = 0;
        }
        return this;
    }

    public Vec3 LerpInPlace(Vec3 b, float t)
    {
        X = X + (b.X - X) * t;
        Y = Y + (b.Y - Y) * t;
        Z = Z + (b.Z - Z) * t;
        return this;
    }

    public Vec3 MinInPlace(Vec3 b)
    {
        X = Math.Min(X, b.X);
        Y = Math.Min(Y, b.Y);
        Z = Math.Min(Z, b.Z);
        return this;
    }

    public Vec3 MaxInPlace(Vec3 b)
    {
        X = Math.Max(X, b.X);
        Y = Math.Max(Y, b.Y);
        Z = Math.Max(Z, b.Z);
        return this;
    }

    public Vec3 ClampInPlace(Vec3 lo, Vec3 hi)
    {
        X = Math.Max(lo.X, Math.Min(hi.X, X));
        Y = Math.Max(lo.Y, Math.Min(hi.Y, Y));
        Z = Math.Max(lo.Z, Math.Min(hi.Z, Z));
        return this;
    }

    public Vec3 ReflectInPlace(Vec3 normal)
    {
        var k = 2.0f * Dot(normal);
        return Set(X - normal.X * k, Y - normal.Y * k, Z - normal.Z * k);
    }

    public Vec3 SetAdd(Vec3 a, Vec3 b)
    {
        X = a.X + b.X;
        Y = a.Y + b.Y;
        Z = a.Z + b.Z;
        return this;
    }

    public Vec3 SetSub(Vec3 a, Vec3 b)
    {
        X = a.X - b.X;
        Y = a.Y - b.Y;
        Z = a.Z - b.Z;
        return this;
    }

    public Vec3 SetScaled(Vec3 a, float s)
    {
        X = a.X * s;
        Y = a.Y * s;
        Z = a.Z * s;
        return this;
    }

    public Vec3 SetLerp(Vec3 a, Vec3 b, float t)
    {
        X = a.X + (b.X - a.X) * t;
        Y = a.Y + (b.Y - a.Y) * t;
        Z = a.Z + (b.Z - a.Z) * t;
        return this;
    }

    /// <summary>a × b を代入する (受け手が a や b でもよい)。</summary>
    public Vec3 SetCross(Vec3 a, Vec3 b) => Set(a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

/// <summary>4 次元ベクトル (同次座標・色など)。演算子: a+b / a-b / a*s /
/// s*a / a/s / -a。確保しない版 (AddInPlace / SetAdd など) と class である
/// 理由は Vec2 と同じ。</summary>
public class Vec4
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

    /// <summary>w を付与して Vec3 から拡張する。位置なら w=1、方向なら w=0。</summary>
    public static Vec4 FromVec3(Vec3 v, float w) => new Vec4(v.X, v.Y, v.Z, w);

    public Vec4 Add(Vec4 b) => new Vec4(X + b.X, Y + b.Y, Z + b.Z, W + b.W);

    public Vec4 Sub(Vec4 b) => new Vec4(X - b.X, Y - b.Y, Z - b.Z, W - b.W);

    /// <summary>スカラー倍。演算子は v * s と s * v の両方が使える。</summary>
    public Vec4 Scale(float s) => new Vec4(X * s, Y * s, Z * s, W * s);

    public Vec4 Negate() => new Vec4(-X, -Y, -Z, -W);

    public static Vec4 operator +(Vec4 a, Vec4 b) => a.Add(b);

    public static Vec4 operator -(Vec4 a, Vec4 b) => a.Sub(b);

    public static Vec4 operator *(Vec4 a, float s) => a.Scale(s);

    public static Vec4 operator *(float s, Vec4 a) => a.Scale(s);

    public static Vec4 operator /(Vec4 a, float s) =>
        new Vec4(a.X / s, a.Y / s, a.Z / s, a.W / s);

    public static Vec4 operator -(Vec4 a) => a.Negate();

    public float Dot(Vec4 b) => X * b.X + Y * b.Y + Z * b.Z + W * b.W;

    public float LengthSq() => X * X + Y * Y + Z * Z + W * W;

    public float Length() => (float)Math.Sqrt(LengthSq());

    /// <summary>正規化。零ベクトルは零ベクトルのまま返す。</summary>
    public Vec4 Normalize()
    {
        var len = Length();
        return len > 0 ? new Vec4(X / len, Y / len, Z / len, W / len) : Zero();
    }

    public Vec4 Lerp(Vec4 b, float t) => new Vec4(X + (b.X - X) * t,
        Y + (b.Y - Y) * t, Z + (b.Z - Z) * t, W + (b.W - W) * t);

    public Vec3 Xyz() => new Vec3(X, Y, Z);

    // --- 確保しない版 (受け手を書き換えて this を返す) ---

    public Vec4 Set(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
        return this;
    }

    public Vec4 CopyFrom(Vec4 v)
    {
        X = v.X;
        Y = v.Y;
        Z = v.Z;
        W = v.W;
        return this;
    }

    public Vec4 AddInPlace(Vec4 b)
    {
        X = X + b.X;
        Y = Y + b.Y;
        Z = Z + b.Z;
        W = W + b.W;
        return this;
    }

    public Vec4 SubInPlace(Vec4 b)
    {
        X = X - b.X;
        Y = Y - b.Y;
        Z = Z - b.Z;
        W = W - b.W;
        return this;
    }

    public Vec4 ScaleInPlace(float s)
    {
        X = X * s;
        Y = Y * s;
        Z = Z * s;
        W = W * s;
        return this;
    }

    /// <summary>this += b * s。</summary>
    public Vec4 AddScaledInPlace(Vec4 b, float s)
    {
        X = X + b.X * s;
        Y = Y + b.Y * s;
        Z = Z + b.Z * s;
        W = W + b.W * s;
        return this;
    }

    public Vec4 NegateInPlace()
    {
        X = -X;
        Y = -Y;
        Z = -Z;
        W = -W;
        return this;
    }

    public Vec4 NormalizeInPlace()
    {
        var len = Length();
        if (len > 0)
        {
            X = X / len;
            Y = Y / len;
            Z = Z / len;
            W = W / len;
        }
        else
        {
            X = 0;
            Y = 0;
            Z = 0;
            W = 0;
        }
        return this;
    }

    public Vec4 LerpInPlace(Vec4 b, float t)
    {
        X = X + (b.X - X) * t;
        Y = Y + (b.Y - Y) * t;
        Z = Z + (b.Z - Z) * t;
        W = W + (b.W - W) * t;
        return this;
    }

    public Vec4 SetAdd(Vec4 a, Vec4 b)
    {
        X = a.X + b.X;
        Y = a.Y + b.Y;
        Z = a.Z + b.Z;
        W = a.W + b.W;
        return this;
    }

    public Vec4 SetSub(Vec4 a, Vec4 b)
    {
        X = a.X - b.X;
        Y = a.Y - b.Y;
        Z = a.Z - b.Z;
        W = a.W - b.W;
        return this;
    }

    public Vec4 SetScaled(Vec4 a, float s)
    {
        X = a.X * s;
        Y = a.Y * s;
        Z = a.Z * s;
        W = a.W * s;
        return this;
    }

    public Vec4 SetLerp(Vec4 a, Vec4 b, float t)
    {
        X = a.X + (b.X - a.X) * t;
        Y = a.Y + (b.Y - a.Y) * t;
        Z = a.Z + (b.Z - a.Z) * t;
        W = a.W + (b.W - a.W) * t;
        return this;
    }
}

/// <summary>回転を表すクォータニオン (Hamilton 積、右ねじの能動回転。Phys3d と
/// 同じ規約)。演算子: a * b (回転の合成)、q * v (Vec3 の回転 = rotateVec3)。
/// 角度は全てラジアン。確保しない版 (SetMul / SetAxisAngle / RotateInto など)
/// と class である理由は Vec2 と同じ。</summary>
public class Quat
{
    public float X;
    public float Y;
    public float Z;
    public float W;

    public Quat(float x, float y, float z, float w)
    {
        this.X = x;
        this.Y = y;
        this.Z = z;
        this.W = w;
    }

    public static Quat Identity() => new Quat(0, 0, 0, 1);

    /// <summary>axis 回りに angle ラジアン回す回転。axis は内部で正規化される。</summary>
    public static Quat FromAxisAngle(Vec3 axis, float angle) =>
        new Quat(0, 0, 0, 1).SetAxisAngle(axis, angle);

    /// <summary>オイラー角 (ラジアン) から生成。適用順は roll (X) → pitch (Y)
    /// → yaw (Z)。</summary>
    public static Quat FromEuler(float yaw, float pitch, float roll) =>
        new Quat(0, 0, 0, 1).SetEuler(yaw, pitch, roll);

    /// <summary>回転の合成。a * b は「b の回転をしてから a の回転」。</summary>
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
            var inv = 1.0f / lsq;
            return new Quat(-X * inv, -Y * inv, -Z * inv, W * inv);
        }
        return Identity();
    }

    /// <summary>成分の線形補間。正規化はしないので必要なら normalize を挟む。</summary>
    public Quat Lerp(Quat b, float t) => new Quat(X + (b.X - X) * t,
        Y + (b.Y - Y) * t, Z + (b.Z - Z) * t, W + (b.W - W) * t);

    /// <summary>球面線形補間。</summary>
    public Quat Slerp(Quat b, float t) =>
        new Quat(0, 0, 0, 1).SetSlerp(this, b, t);

    /// <summary>ベクトルを回転する。演算子 q * v でも呼べる。</summary>
    public Vec3 RotateVec3(Vec3 v) => RotateInto(v, new Vec3(0, 0, 0));

    /// <summary>RotateVec3 と同じ回転を行う行列 (列ベクトルに左から掛ける)。
    /// 正規化済みの quaternion を渡す。box3d / sdf.c と同じ能動回転で、
    /// Mat4.RotateX/Y/Z もこれに揃えてある。</summary>
    public Mat4 ToMat4() => new Mat4().SetFromQuat(this);

    /// <summary>回転行列 (ToMat4 と同じ規約) から quaternion を取り出す。</summary>
    public static Quat FromMat4(Mat4 m) => new Quat(0, 0, 0, 1).SetFromMat4(m);

    /// <summary>Phys3d の wire 型へ変換する。</summary>
    public Quat3d Wire() =>
        new Quat3d { X = this.X, Y = this.Y, Z = this.Z, W = this.W };

    /// <summary>Phys3d の wire 型から変換する。</summary>
    public static Quat FromWire(Quat3d q) => new Quat(q.X, q.Y, q.Z, q.W);

    // --- 確保しない版 (受け手を書き換えて this を返す) ---

    public Quat Set(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
        return this;
    }

    public Quat CopyFrom(Quat q)
    {
        X = q.X;
        Y = q.Y;
        Z = q.Z;
        W = q.W;
        return this;
    }

    public Quat SetIdentity()
    {
        X = 0;
        Y = 0;
        Z = 0;
        W = 1;
        return this;
    }

    /// <summary>FromAxisAngle の確保しない版。axis の正規化も一時オブジェクト
    /// なしで行う。</summary>
    public Quat SetAxisAngle(Vec3 axis, float angle)
    {
        var half = angle * 0.5f;
        var s = (float)Math.Sin(half);
        // axis.Normalize() と同じ計算 (零ベクトルは零のまま)
        var len = axis.Length();
        float nx = 0;
        float ny = 0;
        float nz = 0;
        if (len > 0)
        {
            nx = axis.X / len;
            ny = axis.Y / len;
            nz = axis.Z / len;
        }
        return Set(nx * s, ny * s, nz * s, (float)Math.Cos(half));
    }

    /// <summary>FromEuler の確保しない版。</summary>
    public Quat SetEuler(float yaw, float pitch, float roll)
    {
        var cy = (float)Math.Cos(yaw * 0.5f);
        var sy = (float)Math.Sin(yaw * 0.5f);
        var cp = (float)Math.Cos(pitch * 0.5f);
        var sp = (float)Math.Sin(pitch * 0.5f);
        var cr = (float)Math.Cos(roll * 0.5f);
        var sr = (float)Math.Sin(roll * 0.5f);
        return Set(sr * cp * cy - cr * sp * sy, cr * sp * cy + sr * cp * sy,
            cr * cp * sy - sr * sp * cy, cr * cp * cy + sr * sp * sy);
    }

    /// <summary>a * b を代入する (受け手が a や b でもよい)。</summary>
    public Quat SetMul(Quat a, Quat b) =>
        Set(a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    public Quat NormalizeInPlace()
    {
        var len = Length();
        if (len > 0)
        {
            X = X / len;
            Y = Y / len;
            Z = Z / len;
            W = W / len;
            return this;
        }
        return SetIdentity();
    }

    public Quat ConjugateInPlace()
    {
        X = -X;
        Y = -Y;
        Z = -Z;
        return this;
    }

    public Quat InverseInPlace()
    {
        var lsq = LengthSq();
        if (lsq > 0)
        {
            var inv = 1.0f / lsq;
            return Set(-X * inv, -Y * inv, -Z * inv, W * inv);
        }
        return SetIdentity();
    }

    public Quat LerpInPlace(Quat b, float t)
    {
        X = X + (b.X - X) * t;
        Y = Y + (b.Y - Y) * t;
        Z = Z + (b.Z - Z) * t;
        W = W + (b.W - W) * t;
        return this;
    }

    /// <summary>a から b への球面線形補間を代入する (受け手が a や b でもよい)。</summary>
    public Quat SetSlerp(Quat a, Quat b, float t)
    {
        var ax = a.X;
        var ay = a.Y;
        var az = a.Z;
        var aw = a.W;
        var d = a.Dot(b);
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
            return Set(ax + (bx - ax) * t, ay + (by - ay) * t,
                az + (bz - az) * t, aw + (bw - aw) * t).NormalizeInPlace();
        }
        // tcs は Math.Acos 未対応なので等価な atan2 形で書く
        // (acos(d) == atan2(sqrt(1 - d^2), d) for |d| <= 1)
        var theta = (float)Math.Atan2((float)Math.Sqrt(1.0f - d * d), d);
        var sinT = (float)Math.Sin(theta);
        var s0 = (float)Math.Sin((1.0f - t) * theta) / sinT;
        var s1 = (float)Math.Sin(t * theta) / sinT;
        return Set(ax * s0 + bx * s1, ay * s0 + by * s1, az * s0 + bz * s1,
            aw * s0 + bw * s1);
    }

    /// <summary>回転行列 m (ToMat4 と同じ規約) から取り出して代入する。</summary>
    public Quat SetFromMat4(Mat4 m)
    {
        var a = m.M;
        var trace = a[0] + a[5] + a[10];
        if (trace > 0)
        {
            var s = 0.5f / (float)Math.Sqrt(trace + 1.0f);
            return Set((a[9] - a[6]) * s, (a[2] - a[8]) * s,
                (a[4] - a[1]) * s, 0.25f / s);
        }
        else if (a[0] > a[5] && a[0] > a[10])
        {
            var s = 2.0f * (float)Math.Sqrt(1.0f + a[0] - a[5] - a[10]);
            return Set(0.25f * s, (a[1] + a[4]) / s, (a[8] + a[2]) / s,
                (a[9] - a[6]) / s);
        }
        else if (a[5] > a[10])
        {
            var s = 2.0f * (float)Math.Sqrt(1.0f + a[5] - a[0] - a[10]);
            return Set((a[1] + a[4]) / s, 0.25f * s, (a[6] + a[9]) / s,
                (a[2] - a[8]) / s);
        }
        else
        {
            var s = 2.0f * (float)Math.Sqrt(1.0f + a[10] - a[0] - a[5]);
            return Set((a[8] + a[2]) / s, (a[6] + a[9]) / s, 0.25f * s,
                (a[4] - a[1]) / s);
        }
    }

    /// <summary>v を回して dst に書き、dst を返す (RotateVec3 の確保しない版。
    /// dst は v と同じでもよい)。</summary>
    public Vec3 RotateInto(Vec3 v, Vec3 dst)
    {
        var qx = X;
        var qy = Y;
        var qz = Z;
        var vx = v.X;
        var vy = v.Y;
        var vz = v.Z;
        // uv = q.xyz × v、uuv = q.xyz × uv、結果 = v + uv * 2w + uuv * 2
        var uvx = qy * vz - qz * vy;
        var uvy = qz * vx - qx * vz;
        var uvz = qx * vy - qy * vx;
        var uuvx = qy * uvz - qz * uvy;
        var uuvy = qz * uvx - qx * uvz;
        var uuvz = qx * uvy - qy * uvx;
        var w2 = 2.0f * W;
        return dst.Set(vx + (uvx * w2 + uuvx * 2.0f),
            vy + (uvy * w2 + uuvy * 2.0f), vz + (uvz * w2 + uuvz * 2.0f));
    }
}

/// <summary>4x4 行列 (行優先 / row-major、m[row * 4 + col])。演算子:
/// a * b (行列積)、m * v (Vec4 との積 = mulVec4)。MVP 合成は
/// proj * view * model の順。生成と積は new Mat4().SetXxx(...) と同じで、
/// 行列を使い回すなら SetMul / SetTrs / SetLookAtLh などで書き換える。
/// class である理由は Vec2 と同じ。</summary>
public class Mat4
{
    public List<float> M;

    /// <summary>単位行列で初期化する。</summary>
    public Mat4()
    {
        M = new List<float>
        {
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1,
        };
    }

    public static Mat4 Identity() => new Mat4();

    public static Mat4 Zero()
    {
        // 単位行列の対角を 0 にすれば零行列
        var r = new Mat4();
        r.M[0] = 0;
        r.M[5] = 0;
        r.M[10] = 0;
        r.M[15] = 0;
        return r;
    }

    /// <summary>行列積。演算子 a * b でも呼べる。</summary>
    public Mat4 Mul(Mat4 b) => new Mat4().SetMul(this, b);

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

    public Mat4 Transpose() => new Mat4().SetTranspose(this);

    public float Determinant()
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
    public Mat4 Inverse() => new Mat4().SetInverse(this);

    /// <summary>回転 + 平行移動だけの view 行列を、回転転置 + eye 差し替えで
    /// 逆変換する。</summary>
    public Mat4 RigidInverse(Vec3 eye) =>
        new Mat4().SetRigidInverse(this, eye);

    // Translate と RotateX/Y/Z は単位行列から変わる成分だけを書く (16 成分を
    // 全部書く SetXxx を通すより速い)。値は SetXxx と一致する。
    public static Mat4 Translate(Vec3 v)
    {
        var r = new Mat4();
        var m = r.M;
        m[3] = v.X;
        m[7] = v.Y;
        m[11] = v.Z;
        return r;
    }

    /// <summary>Translate の成分版 (Vec3 を作らない)。</summary>
    public static Mat4 TranslateXyz(float x, float y, float z)
    {
        var r = new Mat4();
        var m = r.M;
        m[3] = x;
        m[7] = y;
        m[11] = z;
        return r;
    }

    public static Mat4 Scale(Vec3 v) => new Mat4().SetScale(v.X, v.Y, v.Z);

    /// <summary>Scale の成分版 (Vec3 を作らない)。</summary>
    public static Mat4 ScaleXyz(float x, float y, float z) =>
        new Mat4().SetScale(x, y, z);

    /// <summary>均一スケール s + 平行移動 t を 1 つの行列にまとめる。</summary>
    public static Mat4 ScaleTrans(float s, Vec3 t) =>
        new Mat4().SetScaleTrans(s, t.X, t.Y, t.Z);

    /// <summary>X 軸回りの回転 (ラジアン)。+π/2 は +Y を +Z に回す。</summary>
    public static Mat4 RotateX(float angle)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        var r = new Mat4();
        var m = r.M;
        m[5] = c;
        m[6] = -s;
        m[9] = s;
        m[10] = c;
        return r;
    }

    /// <summary>Y 軸回りの回転 (ラジアン)。+π/2 は +Z を +X に回す。</summary>
    public static Mat4 RotateY(float angle)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        var r = new Mat4();
        var m = r.M;
        m[0] = c;
        m[2] = s;
        m[8] = -s;
        m[10] = c;
        return r;
    }

    /// <summary>Z 軸回りの回転 (ラジアン)。+π/2 は +X を +Y に回す。</summary>
    public static Mat4 RotateZ(float angle)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        var r = new Mat4();
        var m = r.M;
        m[0] = c;
        m[1] = -s;
        m[4] = s;
        m[5] = c;
        return r;
    }

    /// <summary>任意軸 axis 回りの回転 (ラジアン)。Quat.FromAxisAngle と同じ向き。</summary>
    public static Mat4 Rotate(float angle, Vec3 axis) =>
        new Mat4().SetRotate(angle, axis);

    public static Mat4 FromQuat(Quat q) => q.ToMat4();

    /// <summary>左手系の view 行列。</summary>
    public static Mat4 LookAtLh(Vec3 eye, Vec3 target, Vec3 up) =>
        new Mat4().SetLookAtLh(eye, target, up);

    /// <summary>左手系の透視投影。fovDeg は垂直視野角 (度)。depth は [0, 1]。</summary>
    public static Mat4 PerspectiveLh(float fovDeg, float aspect, float nz,
        float fz) => new Mat4().SetPerspectiveLh(fovDeg, aspect, nz, fz);

    /// <summary>左手系の平行投影。w / h は view volume の幅と高さ。depth は
    /// [0, 1]。</summary>
    public static Mat4 OrthoLh(float w, float h, float nz, float fz) =>
        new Mat4().SetOrthoLh(w, h, nz, fz);

    // --- 確保しない版 (受け手を書き換えて this を返す) ---
    // Set に渡す引数は書き込む前に全部評価されるので、受け手が引数の行列と
    // 同じでもよい。

    /// <summary>16 成分を行優先 (m[row][col]) で代入する。</summary>
    public Mat4 Set(float m00, float m01, float m02, float m03,
        float m10, float m11, float m12, float m13,
        float m20, float m21, float m22, float m23,
        float m30, float m31, float m32, float m33)
    {
        var m = M;
        m[0] = m00;
        m[1] = m01;
        m[2] = m02;
        m[3] = m03;
        m[4] = m10;
        m[5] = m11;
        m[6] = m12;
        m[7] = m13;
        m[8] = m20;
        m[9] = m21;
        m[10] = m22;
        m[11] = m23;
        m[12] = m30;
        m[13] = m31;
        m[14] = m32;
        m[15] = m33;
        return this;
    }

    public Mat4 CopyFrom(Mat4 a)
    {
        var m = M;
        var src = a.M;
        for (var i = 0; i < 16; i++)
        {
            m[i] = src[i];
        }
        return this;
    }

    public Mat4 SetIdentity() =>
        Set(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    public Mat4 SetZero() =>
        Set(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>a * b を代入する (受け手が a や b でもよい)。</summary>
    public Mat4 SetMul(Mat4 a, Mat4 b)
    {
        // 成分を先にローカルへ読む (Lua では table を引く回数が半分になる)
        var p = a.M;
        var q = b.M;
        var a00 = p[0];
        var a01 = p[1];
        var a02 = p[2];
        var a03 = p[3];
        var a10 = p[4];
        var a11 = p[5];
        var a12 = p[6];
        var a13 = p[7];
        var a20 = p[8];
        var a21 = p[9];
        var a22 = p[10];
        var a23 = p[11];
        var a30 = p[12];
        var a31 = p[13];
        var a32 = p[14];
        var a33 = p[15];
        var b00 = q[0];
        var b01 = q[1];
        var b02 = q[2];
        var b03 = q[3];
        var b10 = q[4];
        var b11 = q[5];
        var b12 = q[6];
        var b13 = q[7];
        var b20 = q[8];
        var b21 = q[9];
        var b22 = q[10];
        var b23 = q[11];
        var b30 = q[12];
        var b31 = q[13];
        var b32 = q[14];
        var b33 = q[15];
        return Set(a00 * b00 + a01 * b10 + a02 * b20 + a03 * b30,
            a00 * b01 + a01 * b11 + a02 * b21 + a03 * b31,
            a00 * b02 + a01 * b12 + a02 * b22 + a03 * b32,
            a00 * b03 + a01 * b13 + a02 * b23 + a03 * b33,
            a10 * b00 + a11 * b10 + a12 * b20 + a13 * b30,
            a10 * b01 + a11 * b11 + a12 * b21 + a13 * b31,
            a10 * b02 + a11 * b12 + a12 * b22 + a13 * b32,
            a10 * b03 + a11 * b13 + a12 * b23 + a13 * b33,
            a20 * b00 + a21 * b10 + a22 * b20 + a23 * b30,
            a20 * b01 + a21 * b11 + a22 * b21 + a23 * b31,
            a20 * b02 + a21 * b12 + a22 * b22 + a23 * b32,
            a20 * b03 + a21 * b13 + a22 * b23 + a23 * b33,
            a30 * b00 + a31 * b10 + a32 * b20 + a33 * b30,
            a30 * b01 + a31 * b11 + a32 * b21 + a33 * b31,
            a30 * b02 + a31 * b12 + a32 * b22 + a33 * b32,
            a30 * b03 + a31 * b13 + a32 * b23 + a33 * b33);
    }

    public Mat4 SetTranspose(Mat4 a)
    {
        var p = a.M;
        return Set(p[0], p[4], p[8], p[12], p[1], p[5], p[9], p[13],
            p[2], p[6], p[10], p[14], p[3], p[7], p[11], p[15]);
    }

    /// <summary>a の逆行列を代入する。特異行列 (det=0) なら単位行列。</summary>
    public Mat4 SetInverse(Mat4 a)
    {
        var m = a.M;
        var a00 = m[0];
        var a01 = m[1];
        var a02 = m[2];
        var a03 = m[3];
        var a10 = m[4];
        var a11 = m[5];
        var a12 = m[6];
        var a13 = m[7];
        var a20 = m[8];
        var a21 = m[9];
        var a22 = m[10];
        var a23 = m[11];
        var a30 = m[12];
        var a31 = m[13];
        var a32 = m[14];
        var a33 = m[15];

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
            return SetIdentity();
        }
        var inv = 1.0f / det;
        return Set((a11 * b11 - a12 * b10 + a13 * b09) * inv,
            (-a01 * b11 + a02 * b10 - a03 * b09) * inv,
            (a31 * b05 - a32 * b04 + a33 * b03) * inv,
            (-a21 * b05 + a22 * b04 - a23 * b03) * inv,
            (-a10 * b11 + a12 * b08 - a13 * b07) * inv,
            (a00 * b11 - a02 * b08 + a03 * b07) * inv,
            (-a30 * b05 + a32 * b02 - a33 * b01) * inv,
            (a20 * b05 - a22 * b02 + a23 * b01) * inv,
            (a10 * b10 - a11 * b08 + a13 * b06) * inv,
            (-a00 * b10 + a01 * b08 - a03 * b06) * inv,
            (a30 * b04 - a31 * b02 + a33 * b00) * inv,
            (-a20 * b04 + a21 * b02 - a23 * b00) * inv,
            (-a10 * b09 + a11 * b07 - a12 * b06) * inv,
            (a00 * b09 - a01 * b07 + a02 * b06) * inv,
            (-a30 * b03 + a31 * b01 - a32 * b00) * inv,
            (a20 * b03 - a21 * b01 + a22 * b00) * inv);
    }

    /// <summary>RigidInverse の確保しない版。</summary>
    public Mat4 SetRigidInverse(Mat4 a, Vec3 eye)
    {
        var p = a.M;
        return Set(p[0], p[4], p[8], eye.X, p[1], p[5], p[9], eye.Y,
            p[2], p[6], p[10], eye.Z, 0, 0, 0, 1);
    }

    public Mat4 SetTranslate(float x, float y, float z) =>
        Set(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1);

    public Mat4 SetScale(float x, float y, float z) =>
        Set(x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1);

    /// <summary>均一スケール s + 平行移動 (x, y, z)。</summary>
    public Mat4 SetScaleTrans(float s, float x, float y, float z) =>
        Set(s, 0, 0, x, 0, s, 0, y, 0, 0, s, z, 0, 0, 0, 1);

    public Mat4 SetRotateX(float angle)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        return Set(1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0, 0, 0, 0, 1);
    }

    public Mat4 SetRotateY(float angle)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        return Set(c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0, 0, 0, 0, 1);
    }

    public Mat4 SetRotateZ(float angle)
    {
        var c = (float)Math.Cos(angle);
        var s = (float)Math.Sin(angle);
        return Set(c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    }

    /// <summary>Rotate の確保しない版 (一時 Quat を作らない)。</summary>
    public Mat4 SetRotate(float angle, Vec3 axis)
    {
        // Quat.SetAxisAngle と同じ計算
        var half = angle * 0.5f;
        var s = (float)Math.Sin(half);
        var len = axis.Length();
        float nx = 0;
        float ny = 0;
        float nz = 0;
        if (len > 0)
        {
            nx = axis.X / len;
            ny = axis.Y / len;
            nz = axis.Z / len;
        }
        return SetRotation(nx * s, ny * s, nz * s, (float)Math.Cos(half));
    }

    /// <summary>q の回転行列を代入する (Quat.ToMat4 の確保しない版)。</summary>
    public Mat4 SetFromQuat(Quat q) => SetRotation(q.X, q.Y, q.Z, q.W);

    /// <summary>Translate(p) * q.ToMat4() * Scale(s) を代入する。モデル行列を
    /// 一時オブジェクトなしで作る。q は正規化済みであること。行列積を通さない
    /// ので、積で作った行列と値は同じだが 0 の符号などは違うことがある。</summary>
    public Mat4 SetTrs(float px, float py, float pz, float qx, float qy,
        float qz, float qw, float sx, float sy, float sz)
    {
        var x2 = qx + qx;
        var y2 = qy + qy;
        var z2 = qz + qz;
        var xx = qx * x2;
        var xy = qx * y2;
        var xz = qx * z2;
        var yy = qy * y2;
        var yz = qy * z2;
        var zz = qz * z2;
        var wx = qw * x2;
        var wy = qw * y2;
        var wz = qw * z2;
        return Set((1 - (yy + zz)) * sx, (xy - wz) * sy, (xz + wy) * sz, px,
            (xy + wz) * sx, (1 - (xx + zz)) * sy, (yz - wx) * sz, py,
            (xz - wy) * sx, (yz + wx) * sy, (1 - (xx + yy)) * sz, pz,
            0, 0, 0, 1);
    }

    /// <summary>LookAtLh の確保しない版。</summary>
    public Mat4 SetLookAtLh(Vec3 eye, Vec3 target, Vec3 up)
    {
        var ex = eye.X;
        var ey = eye.Y;
        var ez = eye.Z;
        // z = normalize(target - eye)
        var zx = target.X - ex;
        var zy = target.Y - ey;
        var zz = target.Z - ez;
        var zl = (float)Math.Sqrt(zx * zx + zy * zy + zz * zz);
        if (zl > 0)
        {
            zx = zx / zl;
            zy = zy / zl;
            zz = zz / zl;
        }
        else
        {
            zx = 0;
            zy = 0;
            zz = 0;
        }
        // x = normalize(up × z)
        var xx = up.Y * zz - up.Z * zy;
        var xy = up.Z * zx - up.X * zz;
        var xz = up.X * zy - up.Y * zx;
        var xl = (float)Math.Sqrt(xx * xx + xy * xy + xz * xz);
        if (xl > 0)
        {
            xx = xx / xl;
            xy = xy / xl;
            xz = xz / xl;
        }
        else
        {
            xx = 0;
            xy = 0;
            xz = 0;
        }
        // y = z × x
        var yx = zy * xz - zz * xy;
        var yy = zz * xx - zx * xz;
        var yz = zx * xy - zy * xx;
        return Set(xx, xy, xz, -(xx * ex + xy * ey + xz * ez),
            yx, yy, yz, -(yx * ex + yy * ey + yz * ez),
            zx, zy, zz, -(zx * ex + zy * ey + zz * ez),
            0, 0, 0, 1);
    }

    /// <summary>PerspectiveLh の確保しない版。</summary>
    public Mat4 SetPerspectiveLh(float fovDeg, float aspect, float nz, float fz)
    {
        var f = 1.0f / (float)Math.Tan(fovDeg * (float)Math.PI / 360.0f);
        return Set(f / aspect, 0, 0, 0, 0, f, 0, 0,
            0, 0, fz / (fz - nz), -fz * nz / (fz - nz), 0, 0, 1, 0);
    }

    /// <summary>OrthoLh の確保しない版。</summary>
    public Mat4 SetOrthoLh(float w, float h, float nz, float fz) =>
        Set(2 / w, 0, 0, 0, 0, 2 / h, 0, 0,
            0, 0, 1 / (fz - nz), -nz / (fz - nz), 0, 0, 0, 1);

    /// <summary>MulVec4 の結果を dst に書き、dst を返す (dst は v と同じでもよい)。</summary>
    public Vec4 MulVec4Into(Vec4 v, Vec4 dst)
    {
        var a = M;
        return dst.Set(a[0] * v.X + a[1] * v.Y + a[2] * v.Z + a[3] * v.W,
            a[4] * v.X + a[5] * v.Y + a[6] * v.Z + a[7] * v.W,
            a[8] * v.X + a[9] * v.Y + a[10] * v.Z + a[11] * v.W,
            a[12] * v.X + a[13] * v.Y + a[14] * v.Z + a[15] * v.W);
    }

    /// <summary>MulPoint の結果を dst に書き、dst を返す (dst は v と同じでもよい)。</summary>
    public Vec3 MulPointInto(Vec3 v, Vec3 dst)
    {
        var a = M;
        return dst.Set(a[0] * v.X + a[1] * v.Y + a[2] * v.Z + a[3],
            a[4] * v.X + a[5] * v.Y + a[6] * v.Z + a[7],
            a[8] * v.X + a[9] * v.Y + a[10] * v.Z + a[11]);
    }

    /// <summary>MulDir の結果を dst に書き、dst を返す (dst は v と同じでもよい)。</summary>
    public Vec3 MulDirInto(Vec3 v, Vec3 dst)
    {
        var a = M;
        return dst.Set(a[0] * v.X + a[1] * v.Y + a[2] * v.Z,
            a[4] * v.X + a[5] * v.Y + a[6] * v.Z,
            a[8] * v.X + a[9] * v.Y + a[10] * v.Z);
    }

    // Quat.ToMat4 の式。回転成分 (x, y, z, w) から回転行列を作る。
    private Mat4 SetRotation(float x, float y, float z, float w)
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
        return Set(1 - (yy + zz), xy - wz, xz + wy, 0,
            xy + wz, 1 - (xx + zz), yz - wx, 0,
            xz - wy, yz + wx, 1 - (xx + yy), 0,
            0, 0, 0, 1);
    }
}

/// <summary>スカラー演算のユーティリティ。角度変換以外は GLSL の同名関数と
/// 同義。</summary>
public static class MathUtil
{
    /// <summary>度 → ラジアン。</summary>
    public static float Radians(float deg) => deg * ((float)Math.PI / 180.0f);

    /// <summary>ラジアン → 度。</summary>
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
