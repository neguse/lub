using System.Collections.Immutable;
using System.Text;
using TinyCs;

namespace TinyCs.Luoc;

internal sealed partial class CEmitter
{
    // dict key を (key_i, key_s) の C 引数対に render する
    private string DictKeyArgs(CType keyType, IlExpr key)
    {
        var rendered = RenderExpr(key);
        return keyType.Kind == CTypeKind.String
            ? $"0, {rendered}" : $"{rendered}, NULL";
    }

    private CType RequireDict(IlExpr recv, out CType dict)
    {
        dict = TypeOf(recv);
        if (dict.Kind != CTypeKind.Dict)
            throw new LuocException($"receiver is not a Dictionary: {dict}");
        return dict.Element!;
    }


    private CType TypeOfDictTable(IlTable table)
    {
        if (table.Entries.Any(e => e.NameKey is not null || e.Key is null && table.Entries.Length > 0 && table.KeyType is null))
            throw new LuocException("mixed IlTable entries are not supported");
        CType? key = table.KeyType is null ? null : _facts.MapType(table.KeyType);
        CType? value = table.ElementType is null
            ? null : _facts.MapType(table.ElementType);
        foreach (var entry in table.Entries)
        {
            if (entry.Key is null)
                throw new LuocException("dict IlTable entry without key");
            var k = TypeOf(entry.Key);
            var v = TypeOf(entry.Value);
            key = key is null ? k : CommonType(key, k, "dict keys");
            value = value is null ? v : CommonType(value, v, "dict values");
        }
        if (key is null || value is null)
            throw new LuocException(
                "cannot infer Dictionary key/value types (no metadata)");
        if (key.Kind is not (CTypeKind.I32 or CTypeKind.String))
            throw new LuocException($"Dictionary key type not supported: {key}");
        return CType.Dict(key, value);
    }

    private string RenderDictTable(IlTable table)
    {
        var type = TypeOfDictTable(table);
        var dictTemp = Temp("dict");
        var sb = new StringBuilder();
        sb.Append($"TcsDict *{dictTemp} = tcs_dict_new(" +
            $"{(type.Key!.Kind == CTypeKind.String ? 1 : 0)}, " +
            $"sizeof({type.Element!.CName})); ");
        foreach (var entry in table.Entries)
        {
            var valueTemp = Temp("dict_value");
            sb.Append($"{type.Element!.CName} {valueTemp} = " +
                $"{RenderCoerced(entry.Value, type.Element!)}; ");
            sb.Append($"*({type.Element!.CName} *)tcs_dict_put({dictTemp}, " +
                $"{DictKeyArgs(type.Key!, entry.Key!)}) = {valueTemp}; ");
        }
        return $"({{ {sb}{dictTemp}; }})";
    }


    private string RenderDictSimple(IlCall call, string function)
    {
        _ = TypeOfCall(call);
        _ = RequireDict(call.Args[0], out var dictType);
        var dictTemp = Temp("dict");
        return $"({{ TcsDict *{dictTemp} = {RenderExpr(call.Args[0])}; " +
            $"{function}({dictTemp}, {DictKeyArgs(dictType.Key!, call.Args[1])}); }})";
    }

    // 現状 Dict.TryGet の (found, value) 形のみ (out 引数 multi-return は
    // ref method 側で別対応)
    private void EmitMultiAssign(IlMultiAssign multi)
    {
        if (multi.Values is not [IlCall { Callee: "Dict.TryGet" } tryGet]
            || multi.Targets.Length != 2
            || multi.Targets[0] is not IlVar foundVar
            || multi.Targets[1] is not IlVar valueVar)
            throw new LuocException(
                "only Dict.TryGet multi-assign is supported");
        RequireArity("Dict.TryGet", tryGet.Args.Length, 3);
        var valueType = RequireDict(tryGet.Args[0], out var dictType);
        RequireAssignable(dictType.Key!, TypeOf(tryGet.Args[1]),
            "Dict.TryGet key");
        RequireAssignable(valueType, TypeOf(tryGet.Args[2]),
            "Dict.TryGet fallback");

        Variable Declare(string name, CType type)
        {
            var variable = new Variable(
                $"v_{Names.Id(name)}_{_serial++}", type);
            AddVariable(name, variable);
            Line($"{type.CName} {variable.CName};");
            return variable;
        }
        var found = multi.Declare
            ? Declare(foundVar.Name, CType.Bool) : Resolve(foundVar.Name);
        var value = multi.Declare
            ? Declare(valueVar.Name, valueType) : Resolve(valueVar.Name);
        var dictTemp = Temp("dict");
        var fallbackTemp = Temp("fallback");
        Line($"TcsDict *{dictTemp} = {RenderExpr(tryGet.Args[0])};");
        Line($"{valueType.CName} {fallbackTemp} = " +
            $"{RenderCoerced(tryGet.Args[2], valueType)};");
        Line($"{found.CName} = tcs_dict_tryget({dictTemp}, " +
            $"{DictKeyArgs(dictType.Key!, tryGet.Args[1])}, " +
            $"&{value.CName}, &{fallbackTemp});");
    }
}
