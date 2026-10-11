using Microsoft.CodeAnalysis;

namespace TinyCs;

// enum 値の文字列化 (ToString / 文字列連結 / 補間 / Console.WriteLine)。.NET の
// Enum.ToString と同じく、定義された値は member 名、[Flags] (と書式 "F") は
// 名前の ", " 連結、どちらでも表せない値は数値にする。名前の表は文字列化する
// 地点にだけ置く (`String.FormatEnum(v, spec)` の第 2 引数。il-spec §13)
public partial class LuaEmitter
{
    // 値が enum か enum の T? なら String.FormatEnum、それ以外は null。
    // flags は書式 "F" (Flags でない enum も名前の組み合わせで書く)
    private static IlExpr? WrapEnumToString(ITypeSymbol? type, IlExpr value,
        bool flags = false) =>
        EnumFormatSpec(UnwrapNullable(type), flags) is { } spec
            ? new IlCall("String.FormatEnum",
                [value, new IlLit(ModuleArtifactText.EscapeLuaString(spec))])
            : null;

    // enum の書式指定 (`e.ToString("G")` と補間の `{e:G}`)。G は名前、F は
    // 名前の組み合わせ、D は数値。それ以外 (X 等) は null (ToString は TCS1002)
    private static IlExpr? WrapEnumFormat(ITypeSymbol? type, IlExpr value,
        string format) => format switch
    {
        "G" or "g" => WrapEnumToString(type, value),
        "F" or "f" => WrapEnumToString(type, value, flags: true),
        "D" or "d" => new IlCall("tostring", [value]),
        _ => null,
    };

    // 実行時の値 (Lua の integer / C の int32_t)。enum の基底型によらず 32 bit
    // の 2 の補数に写す (uint の 0x80000000 は int.MinValue)。long / ulong の
    // enum は TCS1001
    internal static int EnumRuntimeValue(IFieldSymbol member) =>
        member.ConstantValue switch
        {
            byte or ushort or uint or ulong => unchecked((int)Convert.ToUInt64(
                member.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)),
            _ => unchecked((int)Convert.ToInt64(
                member.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)),
        };

    // spec は `名前:値` を `,` で並べた列。Flags (と書式 "F") は先頭に `*` を
    // 付け、全 member を .NET の EnumInfo と同じ並び (値の符号なし昇順。宣言順で
    // 既に整列していればそのまま、でなければ Array.Sort) で持つ。それ以外は
    // 値ごとに .NET が選ぶ名前を 1 つだけ持つ (同じ値の member は並びの上で
    // 32 個以下なら最初、それより多ければ二分探索で当たったもの)
    internal static string? EnumFormatSpec(ITypeSymbol? type, bool forceFlags = false)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType
            || enumType.EnumUnderlyingType is not { } underlying)
            return null;
        var bits = underlying.SpecialType switch
        {
            SpecialType.System_SByte or SpecialType.System_Byte => 8,
            SpecialType.System_Int16 or SpecialType.System_UInt16 => 16,
            SpecialType.System_Int32 or SpecialType.System_UInt32 => 32,
            _ => 0,
        };
        if (bits == 0) return null;
        var fields = enumType.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue).ToArray();
        var names = fields.Select(f => f.Name).ToArray();
        var values = fields.Select(EnumRuntimeValue).ToArray();
        var mask = (1UL << bits) - 1;
        var keys = values.Select(v => unchecked((ulong)v) & mask).ToArray();
        var order = Enumerable.Range(0, keys.Length).ToArray();
        if (!keys.Zip(keys.Skip(1)).All(p => p.First <= p.Second))
            Array.Sort((ulong[])keys.Clone(), order);
        var sortedKeys = order.Select(i => keys[i]).ToArray();

        var entries = new List<string>();
        if (forceFlags || enumType.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
        {
            entries.AddRange(order.Select(i => $"{names[i]}:{values[i]}"));
            return "*" + string.Join(",", entries);
        }
        foreach (var key in sortedKeys.Distinct())
        {
            var span = (ReadOnlySpan<ulong>)sortedKeys;
            var at = sortedKeys.Length <= 32 ? span.IndexOf(key) : span.BinarySearch(key);
            entries.Add($"{names[order[at]]}:{values[order[at]]}");
        }
        return string.Join(",", entries);
    }
}
