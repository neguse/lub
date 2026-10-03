using System.Collections.Immutable;
using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

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
            throw new Tcs2cException($"receiver is not a Dictionary: {dict}");
        return dict.Element!;
    }


    private CType TypeOfDictTable(IlTable table)
    {
        if (table.Entries.Any(e => e.NameKey is not null || e.Key is null && table.Entries.Length > 0 && table.KeyType is null))
            throw new Tcs2cException("mixed IlTable entries are not supported");
        CType? key = table.KeyType is null ? null : _facts.MapType(table.KeyType);
        CType? value = table.ElementType is null
            ? null : _facts.MapType(table.ElementType);
        foreach (var entry in table.Entries)
        {
            if (entry.Key is null)
                throw new Tcs2cException("dict IlTable entry without key");
            // 契約の key / value 型があれば各項はそれへ代入可能であれば良い
            // (object 値の box、派生 → 基底の upcast)。無ければ共通型で推論
            if (table.KeyType is not null) CheckAssignable(key!, entry.Key, "dict key");
            else key = key is null ? TypeOf(entry.Key)
                : CommonType(key, TypeOf(entry.Key), "dict keys");
            if (table.ElementType is not null) CheckAssignable(value!, entry.Value, "dict value");
            else value = value is null ? TypeOf(entry.Value)
                : CommonType(value, TypeOf(entry.Value), "dict values");
        }
        if (key is null || value is null)
            throw new Tcs2cException(
                "cannot infer Dictionary key/value types (no metadata)");
        if (key.Kind is not (CTypeKind.I32 or CTypeKind.String))
            throw new Tcs2cException($"Dictionary key type not supported: {key}");
        return CType.Dict(key, value);
    }

    private string RenderDictTable(IlTable table)
    {
        var type = TypeOfDictTable(table);
        var dictTemp = Temp("dict");
        var sb = new StringBuilder();
        sb.Append($"TcsDict *{dictTemp} = tcs_typed(tcs_dict_new(" +
            $"{(type.Key!.Kind == CTypeKind.String ? 1 : 0)}, " +
            $"sizeof({type.Element!.CName}), {LayoutRef(type.Element)}), {RuntimeTypeId(type)}); ");
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

    // (found, value) 形の multi-return intrinsic: Dict.TryGet /
    // Math.TryParseInt / Math.TryParseFloat (out 引数 multi-return は
    // ref method 側で別対応)
    private static CType? TryParseValueType(string callee) => callee switch
    {
        "Math.TryParseInt" => CType.I32,
        "Math.TryParseFloat" => CType.F32,
        _ => null,
    };

    private void EmitMultiAssign(IlMultiAssign multi)
    {
        if (EmitForeignMultiAssign(multi)) return;
        if (multi.Values.Length == multi.Targets.Length && multi.Values.Length > 0)
        {
            EmitPairwiseMultiAssign(multi);
            return;
        }
        if (multi.Values is not [IlCall { Callee: "Dict.TryGet" or "Math.TryParseInt"
                or "Math.TryParseFloat" } tryGet]
            || multi.Targets.Length != 2
            || multi.Targets[0] is not IlVar foundVar
            || multi.Targets[1] is not IlVar valueVar)
            throw new Tcs2cException(
                "only (found, value) multi-return intrinsics are supported in multi-assign");
        if (TryParseValueType(tryGet.Callee) is { } parsedType)
        {
            EmitTryParse(multi, tryGet, parsedType, foundVar, valueVar);
            return;
        }
        RequireArity("Dict.TryGet", tryGet.Args.Length, 3);
        var valueType = RequireDict(tryGet.Args[0], out var dictType);
        RequireAssignable(dictType.Key!, TypeOf(tryGet.Args[1]),
            "Dict.TryGet key");
        RequireAssignable(valueType, TypeOf(tryGet.Args[2]),
            "Dict.TryGet fallback");

        var found = multi.Declare
            ? DeclareLocal(foundVar.Name, CType.Bool) : Resolve(foundVar.Name);
        var value = multi.Declare
            ? DeclareLocal(valueVar.Name, valueType) : Resolve(valueVar.Name);
        var dictTemp = Temp("dict");
        var fallbackTemp = Temp("fallback");
        Line($"TcsDict *{dictTemp} = {RenderExpr(tryGet.Args[0])};");
        Line($"{valueType.CName} {fallbackTemp} = " +
            $"{RenderCoerced(tryGet.Args[2], valueType)};");
        Line($"{found.CName} = tcs_dict_tryget({dictTemp}, " +
            $"{DictKeyArgs(dictType.Key!, tryGet.Args[1])}, " +
            $"&{value.CName}, &{fallbackTemp});");
    }

    private Variable DeclareLocal(string name, CType type)
    {
        var variable = new Variable($"v_{Names.Id(name)}_{_serial++}", type);
        AddVariable(name, variable);
        Line($"{type.CName} {variable.CName};");
        return variable;
    }

    private void EmitTryParse(IlMultiAssign multi, IlCall call, CType parsedType,
        IlVar foundVar, IlVar valueVar)
    {
        RequireArity(call.Callee, call.Args.Length, 2);
        RequireType(CType.String, TypeOf(call.Args[0]), call.Callee);
        RequireAssignable(parsedType, TypeOf(call.Args[1]), $"{call.Callee} fallback");
        var found = multi.Declare
            ? DeclareLocal(foundVar.Name, CType.Bool) : Resolve(foundVar.Name);
        var value = multi.Declare
            ? DeclareLocal(valueVar.Name, parsedType) : Resolve(valueVar.Name);
        var text = Temp("parse_text");
        var fallback = Temp("fallback");
        var helper = parsedType == CType.I32 ? "tcs_try_parse_i32" : "tcs_try_parse_f32";
        Line($"TcsString *{text} = {RenderExpr(call.Args[0])};");
        Line($"{parsedType.CName} {fallback} = {RenderCoerced(call.Args[1], parsedType)};");
        Line($"{(found.Boxed ? "(*" + found.CName + ")" : found.CName)} = " +
            $"{helper}({text}, &{(value.Boxed ? "(*" + value.CName + ")" : value.CName)}, {fallback});");
    }

    // 分解代入 `(a, b) = (x, y)` / `var (a, b) = r`: 右辺を全部評価してから
    // 左から代入 (Lua の多重代入と同じ)
    private void EmitPairwiseMultiAssign(IlMultiAssign multi)
    {
        var temps = new List<(string Name, CType Type)>();
        foreach (var value in multi.Values)
        {
            var type = TypeOf(value);
            var temp = Temp("multi");
            Line($"{type.CName} {temp} = {RenderExpr(value)};");
            AddVariable(temp, new Variable(temp, type));
            temps.Add((temp, type));
        }
        for (var i = 0; i < multi.Targets.Length; i++)
        {
            var (temp, type) = temps[i];
            if (multi.Targets[i] is IlVar { Name: "_" }) continue;
            if (multi.Declare)
            {
                if (multi.Targets[i] is not IlVar declared)
                    throw new Tcs2cException("multi-assign declaration target is not a variable");
                var variable = DeclareLocal(declared.Name, type);
                Line($"{variable.CName} = {temp};");
                continue;
            }
            EmitAssign(new IlAssign(multi.Targets[i], new IlVar(temp)));
        }
    }
}
