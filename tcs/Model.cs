using System.Globalization;
using System.Text.RegularExpressions;

namespace TinyCs.Luoc;

internal sealed class LuocException(string message) : Exception(message);

internal enum CTypeKind { Void, I32, F32, Bool, Ref, Array }

internal sealed record CType(CTypeKind Kind, string? Name = null, CType? Element = null)
{
    public static readonly CType Void = new(CTypeKind.Void);
    public static readonly CType I32 = new(CTypeKind.I32);
    public static readonly CType F32 = new(CTypeKind.F32);
    public static readonly CType Bool = new(CTypeKind.Bool);

    public static CType Ref(string name) => new(CTypeKind.Ref, name);
    public static CType Array(CType element) => new(CTypeKind.Array, Element: element);

    public string CName => Kind switch
    {
        CTypeKind.Void => "void",
        CTypeKind.I32 => "int32_t",
        CTypeKind.F32 => "float",
        CTypeKind.Bool => "bool",
        CTypeKind.Ref => $"Tcs_{Names.Id(Name!)} *",
        CTypeKind.Array => "TcsArray *",
        _ => throw new LuocException($"unsupported type: {this}"),
    };

    public string ElementCName => Kind == CTypeKind.Array
        ? Element!.CName : throw new InvalidOperationException();

    public bool CanAssignFrom(CType source) =>
        this == source || (Kind == CTypeKind.F32 && source.Kind == CTypeKind.I32);

    public override string ToString() => Kind switch
    {
        CTypeKind.Ref => $"ref {Name}",
        CTypeKind.Array => $"{Element}[]",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

internal static partial class Names
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    public static string Id(string name)
    {
        if (!IdentifierRegex().IsMatch(name))
            throw new LuocException($"identifier is not supported by the C backend: {name}");
        return name;
    }

    public static string Class(string name) => $"Tcs_{Id(name)}";
    public static string Field(string name) => $"f_{Id(name)}";
    public static string StaticField(string cls, string name) =>
        $"tcs_s_{Id(cls)}_{Id(name)}";
    public static string Method(string cls, string name) =>
        $"tcs_m_{Id(cls)}_{Id(name)}";
    public static string New(string cls) => $"tcs_new_{Id(cls)}";
}

internal static class Constants
{
    public static string I32(int value) => value == int.MinValue
        ? "INT32_MIN"
        : value < 0
            ? $"-INT32_C({-(long)value})"
            : $"INT32_C({value.ToString(CultureInfo.InvariantCulture)})";

    public static string F32(float value) =>
        $"tcs_f32(UINT32_C(0x{BitConverter.SingleToUInt32Bits(value):x8}))";
}
