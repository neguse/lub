// Bindings の Set。
// host の型に overload を置けず、stub から Vec4 / Mat4 を名指しできないので、ここで型ごとの host の関数に振り分ける。
using System.Collections.Generic;

namespace Lub;

public static class BindingsSet
{
    public static void Set(this Bindings b, string name, TextureRef value) =>
        Gfx.BindingsSetTexture(b, name, value);

    public static void Set(this Bindings b, string name, BufferRef value) =>
        Gfx.BindingsSetBuffer(b, name, value);

    public static void Set(this Bindings b, string name, FrameRange value) =>
        Gfx.BindingsSetFrameRange(b, name, value);

    public static void Set(this Bindings b, string name, List<float> value) =>
        Gfx.BindingsSetFloats(b, name, value);

    public static void Set(this Bindings b, string name, float[] value) =>
        Gfx.BindingsSetFloatArray(b, name, value);

    public static void Clear(this Bindings b) => Gfx.BindingsClear(b);

    public static void Set(this Bindings b, string name, float value) =>
        Gfx.BindingsSet4(b, name, 1, value, 0, 0, 0);

    public static void Set(this Bindings b, string name, Vec2 value) =>
        Gfx.BindingsSet4(b, name, 2, value.X, value.Y, 0, 0);

    public static void Set(this Bindings b, string name, Vec3 value) =>
        Gfx.BindingsSet4(b, name, 3, value.X, value.Y, value.Z, 0);

    public static void Set(this Bindings b, string name, Vec4 value) =>
        Gfx.BindingsSet4(b, name, 4, value.X, value.Y, value.Z, value.W);

    public static void Set(this Bindings b, string name, Mat4 value) =>
        Gfx.BindingsSet16(b, name,
            value.M00, value.M01, value.M02, value.M03,
            value.M10, value.M11, value.M12, value.M13,
            value.M20, value.M21, value.M22, value.M23,
            value.M30, value.M31, value.M32, value.M33);
}
