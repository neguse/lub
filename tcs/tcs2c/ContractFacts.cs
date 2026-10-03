using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed record ParameterFact(string Name, CType Type, IlExpr? Default = null);

internal sealed record MethodFact(
    string ClassName,
    string Name,
    bool IsStatic,
    CType ReturnType,
    IReadOnlyList<ParameterFact> Parameters,
    IlMethodInfo Metadata);

internal sealed record FieldFact(
    string ClassName,
    string Name,
    CType Type,
    bool IsStatic,
    IlExpr? Init,
    IlFieldInfo Metadata);

/// <summary>
/// IlExport の metadata を backend 内部型へ写す。source syntax や
/// SemanticModel は参照せず、backend の入力は IlExportResult だけに限定する。
/// </summary>
internal sealed class ContractFacts
{
    private readonly Dictionary<string, IlClassInfo> _classes;
    private readonly Dictionary<string, IlStructInfo> _structs = [];
    private readonly Dictionary<string, IlEnumInfo> _enums = [];
    private readonly Dictionary<(string Class, string Method), MethodFact> _methods = [];
    private readonly Dictionary<(string Class, string Field), FieldFact> _fields = [];

    public ContractFacts(IlExportResult program)
    {
        _classes = new Dictionary<string, IlClassInfo>();
        foreach (var cls in program.Classes)
            if (!_classes.TryAdd(cls.Name, cls))
                throw new Tcs2cException($"duplicate class: {cls.Name}");
        if (!program.Structs.IsDefault)
            foreach (var st in program.Structs)
                if (!_structs.TryAdd(st.Name, st))
                    throw new Tcs2cException($"duplicate struct: {st.Name}");
        if (!program.EnumTypes.IsDefault)
            foreach (var e in program.EnumTypes)
                if (!_enums.TryAdd(e.Name, e))
                    throw new Tcs2cException($"duplicate enum: {e.Name}");

        foreach (var cls in program.Classes)
        {
            foreach (var field in cls.Fields)
            {
                var fact = new FieldFact(cls.Name, field.Name, MapType(field.Type),
                    field.IsStatic, field.Init, field);
                if (!_fields.TryAdd((cls.Name, field.Name), fact))
                    throw new Tcs2cException($"duplicate field: {cls.Name}.{field.Name}");
            }

            RegisterMethods(cls.Name, cls.Methods);
        }
        // struct の instance member も同じ表に載せる (型名で引く)
        foreach (var st in _structs.Values)
            if (!st.Methods.IsDefault)
                RegisterMethods(st.Name, st.Methods);
    }

    private void RegisterMethods(string owner,
        System.Collections.Immutable.ImmutableArray<IlMethodInfo> methods)
    {
        foreach (var method in methods)
        {
            if (method.ParameterTypes.IsDefault)
                throw new Tcs2cException($"method is missing parameter types: " +
                    $"{owner}.{method.Name}");
            if (method.Parameters.Length != method.ParameterTypes.Length)
                throw new Tcs2cException($"method parameter metadata mismatch: " +
                    $"{owner}.{method.Name}");
            var parameters = ParameterFacts(method.Parameters, method.ParameterTypes,
                method.ParameterDefaults);
            var fact = new MethodFact(owner, method.Name, method.IsStatic,
                MapType(method.ReturnType), parameters, method);
            if (!_methods.TryAdd((owner, method.Name), fact))
                throw new Tcs2cException($"method overloads are not supported: " +
                    $"{owner}.{method.Name}");
        }
    }

    public IReadOnlyDictionary<string, IlClassInfo> Classes => _classes;

    /// <summary>parameter の名前 / 型 / 省略時の既定値 (IL の ParameterDefaults。
    /// 末尾の省略は呼び出し側が既定値で補う)。</summary>
    public ParameterFact[] ParameterFacts(
        System.Collections.Immutable.ImmutableArray<string> names,
        System.Collections.Immutable.ImmutableArray<string> types,
        System.Collections.Immutable.ImmutableArray<IlExpr?> defaults) =>
        names.Select((name, i) => new ParameterFact(name, MapType(types[i]),
            defaults.IsDefault || i >= defaults.Length ? null : defaults[i])).ToArray();

    public MethodFact Method(string cls, string name) =>
        _methods.TryGetValue((cls, name), out var fact)
            ? fact
            : throw new Tcs2cException($"unknown method: {cls}.{name}");

    public FieldFact Field(string cls, string name) =>
        _fields.TryGetValue((cls, name), out var fact)
            ? fact
            : throw new Tcs2cException($"unknown field: {cls}.{name}");

    // "A, B<C, D>, E" 形をトップレベルのカンマで分割する
    private static List<string> SplitTypeArgs(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '<') depth++;
            else if (text[i] == '>') depth--;
            else if (text[i] == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    public CType? TryMapType(string displayName)
    {
        try { return MapType(displayName); }
        catch (Tcs2cException) { return null; }
    }

    public CType MapType(string displayName)
    {
        var text = displayName.Trim();
        if (text.StartsWith("global::", StringComparison.Ordinal))
            text = text[8..];
        if (text.EndsWith("[]", StringComparison.Ordinal))
            return CType.Array(MapType(text[..^2]));
        // Nullable<T>: 値型なら T?、参照型の `string?` 等は参照そのもの
        if (text.EndsWith('?'))
            return MakeNullable(MapType(text[..^1]));
        foreach (var prefix in new[] { "System.Nullable<", "Nullable<" })
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.EndsWith('>'))
                return MakeNullable(MapType(text[prefix.Length..^1]));

        if (text == "System.Action" || text == "Action")
            return CType.Closure(CType.Void, []);
        foreach (var prefix in new[] { "System.Action<", "Action<" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal)
                && text.EndsWith('>'))
            {
                var args = SplitTypeArgs(text[prefix.Length..^1])
                    .Select(MapType).ToList();
                return CType.Closure(CType.Void, args);
            }
        }
        foreach (var prefix in new[] { "System.Func<", "Func<" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal)
                && text.EndsWith('>'))
            {
                var args = SplitTypeArgs(text[prefix.Length..^1])
                    .Select(MapType).ToList();
                return CType.Closure(args[^1], args[..^1]);
            }
        }

        const string genericDict = "System.Collections.Generic.Dictionary<";
        if ((text.StartsWith(genericDict, StringComparison.Ordinal)
             || text.StartsWith("Dictionary<", StringComparison.Ordinal))
            && text.EndsWith('>'))
        {
            var inner = text[(text.IndexOf('<') + 1)..^1];
            var comma = inner.IndexOf(',');
            if (comma < 0)
                throw new Tcs2cException($"unsupported IL type: {displayName}");
            var key = MapType(inner[..comma]);
            if (!IsDictKey(key))
                throw new Tcs2cException(
                    $"Dictionary key type not supported: {displayName}");
            return CType.Dict(key, MapType(inner[(comma + 1)..]));
        }

        const string genericList = "System.Collections.Generic.List<";
        if (text.StartsWith(genericList, StringComparison.Ordinal)
            && text.EndsWith('>'))
            return CType.List(MapType(text[genericList.Length..^1]));
        if (text.StartsWith("List<", StringComparison.Ordinal)
            && text.EndsWith('>'))
            return CType.List(MapType(text[5..^1]));

        return text switch
        {
            "void" => CType.Void,
            "int" or "System.Int32" => CType.I32,
            "float" or "System.Single" => CType.F32,
            "bool" or "System.Boolean" => CType.Bool,
            "string" or "System.String" => CType.String,
            "object" or "System.Object" => CType.Object,
            // char は整数 code unit (il-spec §3)
            "char" or "System.Char" => CType.I32,
            // enum は整数定数 (Lua と同じ。tostring も整数表記)
            _ when _enums.ContainsKey(text) => CType.I32,
            // TinySystem.Random の instance (user の class Random は別物)
            "TinySystem.Random" => CType.Random,
            "Random" when !_classes.ContainsKey("Random") => CType.Random,
            _ when _classes.ContainsKey(text) => CType.Ref(text),
            _ when _structs.ContainsKey(text) => CType.Struct(text),
            _ => throw new Tcs2cException($"unsupported IL type: {displayName}"),
        };
    }

    public IReadOnlyDictionary<string, IlStructInfo> Structs => _structs;

    /// <summary>Dictionary の key にできる型: int / string と、host handle を持つ
    /// 外部 data class (同一性は host_value。Lua の host 値 key に対応)。</summary>
    public bool IsDictKey(CType key) =>
        key.Kind is CTypeKind.I32 or CTypeKind.String
        || key.Kind == CTypeKind.Ref && IsExternalClass(key.Name!);

    private bool IsExternalClass(string name)
    {
        for (string? cur = name; cur != null; cur = _classes[cur].BaseName)
            if (_classes[cur].IsExternal) return true;
        return false;
    }

    private static CType MakeNullable(CType element) => element.Kind switch
    {
        CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool or CTypeKind.StructVal =>
            CType.Nullable(element),
        CTypeKind.Nullable => element,
        _ => element, // 参照型の ? は注釈のみ
    };

    public bool IsEnum(string name) => _enums.ContainsKey(name);

    public bool TryEnumConstant(string enumName, string member, out int value)
    {
        value = 0;
        if (!_enums.TryGetValue(enumName, out var info)) return false;
        foreach (var (name, v) in info.Members)
            if (name == member) { value = v; return true; }
        throw new Tcs2cException($"unknown enum member: {enumName}.{member}");
    }

    public CType StructField(string structName, string fieldName)
    {
        if (!_structs.TryGetValue(structName, out var st))
            throw new Tcs2cException($"unknown struct: {structName}");
        var field = st.Fields.FirstOrDefault(f => f.Name == fieldName)
            ?? throw new Tcs2cException(
                $"unknown struct field: {structName}.{fieldName}");
        return MapType(field.Type);
    }
}
