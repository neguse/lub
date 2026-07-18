using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed record ParameterFact(string Name, CType Type);

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
/// T228 の IlExport metadata を backend 内部型へ写す。source syntax や
/// SemanticModel は参照せず、backend の入力は IlExportResult だけに限定する。
/// </summary>
internal sealed class ContractFacts
{
    private readonly Dictionary<string, IlClassInfo> _classes;
    private readonly Dictionary<(string Class, string Method), MethodFact> _methods = [];
    private readonly Dictionary<(string Class, string Field), FieldFact> _fields = [];

    public ContractFacts(IlExportResult program)
    {
        _classes = new Dictionary<string, IlClassInfo>();
        foreach (var cls in program.Classes)
            if (!_classes.TryAdd(cls.Name, cls))
                throw new Tcs2cException($"duplicate class: {cls.Name}");

        foreach (var cls in program.Classes)
        {
            foreach (var field in cls.Fields)
            {
                var fact = new FieldFact(cls.Name, field.Name, MapType(field.Type),
                    field.IsStatic, field.Init, field);
                if (!_fields.TryAdd((cls.Name, field.Name), fact))
                    throw new Tcs2cException($"duplicate field: {cls.Name}.{field.Name}");
            }

            foreach (var method in cls.Methods)
            {
                if (method.ParameterTypes.IsDefault)
                    throw new Tcs2cException($"method is missing T228 parameter types: " +
                        $"{cls.Name}.{method.Name}");
                if (method.Parameters.Length != method.ParameterTypes.Length)
                    throw new Tcs2cException($"method parameter metadata mismatch: " +
                        $"{cls.Name}.{method.Name}");
                var parameters = method.Parameters.Select((name, i) =>
                    new ParameterFact(name, MapType(method.ParameterTypes[i]))).ToArray();
                var fact = new MethodFact(cls.Name, method.Name, method.IsStatic,
                    MapType(method.ReturnType), parameters, method);
                if (!_methods.TryAdd((cls.Name, method.Name), fact))
                    throw new Tcs2cException($"method overloads are not supported: " +
                        $"{cls.Name}.{method.Name}");
            }
        }
    }

    public IReadOnlyDictionary<string, IlClassInfo> Classes => _classes;

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
            if (key.Kind is not (CTypeKind.I32 or CTypeKind.String))
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
            _ when _classes.ContainsKey(text) => CType.Ref(text),
            _ => throw new Tcs2cException($"unsupported IL type: {displayName}"),
        };
    }
}
