using System.Globalization;
using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// enum の文字列化 `String.FormatEnum(v, spec)` (il-spec §13)。spec ごとに名前
// (string literal) と値の file-scope 表を 1 組だけ作り、runtime の
// tcs_enum_format が Lua の String.FormatEnum と同じ規則で引く
internal sealed partial class CEmitter
{
    private readonly Dictionary<string, (string Names, string Values, int Count, bool Flags)>
        _enumTables = new(StringComparer.Ordinal);

    private CType TypeOfFormatEnum(IlCall call)
    {
        RequireArity(call.Callee, call.Args.Length, 2);
        var value = TypeOf(call.Args[0]);
        if (value != CType.I32 && value != CType.Nullable(CType.I32))
            throw new Tcs2cException($"String.FormatEnum: value is not an enum ({value})");
        if (call.Args[1] is not IlLit)
            throw new Tcs2cException("String.FormatEnum: spec is not a literal");
        return CType.String;
    }

    private string RenderFormatEnum(IlCall call)
    {
        var (names, values, count, flags) = EnumTable(((IlLit)call.Args[1]).LuaText);
        string Format(string v) =>
            $"tcs_enum_format({v}, {names}, {values}, {count}, {(flags ? "true" : "false")})";
        var type = TypeOf(call.Args[0]);
        if (type.Kind != CTypeKind.Nullable) return Format(RenderExpr(call.Args[0]));
        var temp = Temp("opt");
        return $"({{ {type.CName} {temp} = {RenderExpr(call.Args[0])}; " +
            $"{temp}.has ? {Format($"{temp}.v")} : {InternStringLiteral([])}; }})";
    }

    private (string Names, string Values, int Count, bool Flags) EnumTable(string luaText)
    {
        if (_enumTables.TryGetValue(luaText, out var existing)) return existing;
        var spec = Encoding.UTF8.GetString(DecodeLuaString(luaText));
        var flags = spec.StartsWith('*');
        var entries = (flags ? spec[1..] : spec)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e =>
            {
                var colon = e.LastIndexOf(':');
                return (Name: e[..colon], Value: int.Parse(e[(colon + 1)..],
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
            }).ToList();
        var id = _enumTables.Count;
        var names = $"tcs_enum_names_{id}";
        var values = $"tcs_enum_values_{id}";
        var literals = entries.Select(e => InternStringLiteral(Encoding.UTF8.GetBytes(e.Name)))
            .ToList();
        // 0 個の配列は C で書けないので、member の無い enum も 1 要素を置く
        _literalDecls.Add($"static TcsString *const {names}[] = {{ " +
            $"{(literals.Count == 0 ? "NULL" : string.Join(", ", literals))} }};");
        _literalDecls.Add($"static const int32_t {values}[] = {{ " +
            $"{(entries.Count == 0 ? "0" : string.Join(", ", entries.Select(e => I32Literal(e.Value))))} }};");
        return _enumTables[luaText] = (names, values, entries.Count, flags);
    }

    private static string I32Literal(int value) =>
        value == int.MinValue ? "INT32_MIN" : $"INT32_C({value})";
}
