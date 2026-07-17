using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs.Luoc;

internal sealed record ParameterFact(string Name, CType Type);

internal sealed record MethodFact(
    string ClassName,
    string Name,
    bool IsStatic,
    CType ReturnType,
    IReadOnlyList<ParameterFact> Parameters);

internal sealed record FieldFact(
    string ClassName,
    string Name,
    CType Type,
    bool IsStatic,
    object? ConstantInitializer,
    bool HasInitializer);

internal sealed record ArrayLengthFact(string? Variable, int? Constant)
{
    public static ArrayLengthFact FromVariable(string name) => new(name, null);
    public static ArrayLengthFact FromConstant(int value) => new(null, value);
}

internal sealed record LocalFact(
    string ClassName,
    string MethodName,
    string Name,
    CType Type,
    ArrayLengthFact? ArrayLength);

internal sealed class SourceFacts
{
    private readonly Dictionary<(string Class, string Method), MethodFact> _methods;
    private readonly Dictionary<(string Class, string Field), FieldFact> _fields;
    private readonly Dictionary<(string Text, int Start, string Name), LocalFact> _locals;

    private SourceFacts(
        Dictionary<(string, string), MethodFact> methods,
        Dictionary<(string, string), FieldFact> fields,
        Dictionary<(string, int, string), LocalFact> locals)
    {
        _methods = methods;
        _fields = fields;
        _locals = locals;
    }

    public MethodFact Method(string cls, string name) =>
        _methods.TryGetValue((cls, name), out var fact)
            ? fact
            : throw new LuocException($"source declaration not found for method {cls}.{name}");

    public FieldFact Field(string cls, string name) =>
        _fields.TryGetValue((cls, name), out var fact)
            ? fact
            : throw new LuocException($"source declaration not found for field {cls}.{name}");

    public LocalFact Local(string cls, string method, IlLocal local)
    {
        if (local.Origin is null)
            throw new LuocException($"local {cls}.{method}.{local.Name} has no source origin");
        var key = (local.Origin.SyntaxTree.GetText().ToString(),
            local.Origin.SpanStart, local.Name);
        return _locals.TryGetValue(key, out var fact)
            ? fact
            : throw new LuocException($"source declaration not found for local " +
                $"{cls}.{method}.{local.Name} at {local.Origin.SpanStart}");
    }

    public static SourceFacts Create(string[] sources, IReadOnlyList<string> paths)
    {
        var trees = sources.Select((source, i) =>
            CSharpSyntaxTree.ParseText(source, path: paths[i])).ToArray();
        var compilation = CSharpCompilation.Create("luoc-source-facts", trees,
            Transpiler.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: false, concurrentBuild: false));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString()).ToArray();
        if (errors.Length > 0)
            throw new LuocException("C# compilation failed:\n" + string.Join("\n", errors));

        var methods = new Dictionary<(string, string), MethodFact>();
        var fields = new Dictionary<(string, string), FieldFact>();
        var locals = new Dictionary<(string, int, string), LocalFact>();

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (cls.Parent is ClassDeclarationSyntax)
                    throw new LuocException("nested classes are not supported");
                var clsName = cls.Identifier.ValueText;
                ReadFields(model, clsName, cls, fields);
                ReadMethods(model, tree, clsName, cls, methods, locals);
            }
        }
        return new SourceFacts(methods, fields, locals);
    }

    private static void ReadFields(SemanticModel model, string clsName,
        ClassDeclarationSyntax cls,
        Dictionary<(string, string), FieldFact> fields)
    {
        foreach (var declaration in cls.Members.OfType<FieldDeclarationSyntax>())
        foreach (var variable in declaration.Declaration.Variables)
        {
            var symbol = model.GetDeclaredSymbol(variable) as IFieldSymbol
                ?? throw new LuocException($"cannot resolve field {clsName}.{variable.Identifier.ValueText}");
            var initializer = variable.Initializer?.Value;
            object? value = null;
            if (initializer is not null)
            {
                var constant = model.GetConstantValue(initializer);
                if (!constant.HasValue)
                    throw new LuocException($"non-constant field initializer is not supported: " +
                        $"{clsName}.{symbol.Name}");
                value = constant.Value;
            }
            AddUnique(fields, (clsName, symbol.Name),
                new FieldFact(clsName, symbol.Name, MapType(symbol.Type),
                    symbol.IsStatic, value, initializer is not null), "field");
        }
    }

    private static void ReadMethods(SemanticModel model, SyntaxTree tree,
        string clsName, ClassDeclarationSyntax cls,
        Dictionary<(string, string), MethodFact> methods,
        Dictionary<(string, int, string), LocalFact> locals)
    {
        foreach (var declaration in cls.Members.OfType<MethodDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(declaration) as IMethodSymbol
                ?? throw new LuocException($"cannot resolve method {clsName}.{declaration.Identifier.ValueText}");
            var parameters = symbol.Parameters
                .Select(p => new ParameterFact(p.Name, MapType(p.Type))).ToArray();
            var fact = new MethodFact(clsName, symbol.Name, symbol.IsStatic,
                MapType(symbol.ReturnType), parameters);
            AddUnique(methods, (clsName, symbol.Name), fact,
                "method (overloads are not supported)");

            foreach (var variable in declaration.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>())
            {
                if (model.GetDeclaredSymbol(variable) is not ILocalSymbol local)
                    continue;
                var origin = variable.Ancestors().FirstOrDefault(n =>
                    n is LocalDeclarationStatementSyntax or ForStatementSyntax);
                if (origin is null)
                    throw new LuocException($"unsupported local declaration: {local.Name}");
                var type = MapType(local.Type);
                var length = type.Kind == CTypeKind.Array
                    ? ReadArrayLength(model, variable) : null;
                var key = (tree.GetText().ToString(), origin.SpanStart, local.Name);
                AddUnique(locals, key,
                    new LocalFact(clsName, symbol.Name, local.Name, type, length), "local");
            }
        }
    }

    private static ArrayLengthFact ReadArrayLength(SemanticModel model,
        VariableDeclaratorSyntax variable)
    {
        if (variable.Initializer?.Value is not ArrayCreationExpressionSyntax array
            || array.Type.RankSpecifiers.Count != 1
            || array.Type.RankSpecifiers[0].Sizes.Count != 1)
            throw new LuocException($"only one-dimensional new T[n] arrays are supported: " +
                variable.Identifier.ValueText);
        var size = array.Type.RankSpecifiers[0].Sizes[0];
        if (size is IdentifierNameSyntax identifier
            && model.GetSymbolInfo(identifier).Symbol is ILocalSymbol)
            return ArrayLengthFact.FromVariable(identifier.Identifier.ValueText);
        var constant = model.GetConstantValue(size);
        if (constant is { HasValue: true, Value: int value })
            return ArrayLengthFact.FromConstant(value);
        throw new LuocException($"array length must be an int local or constant: " +
            variable.Identifier.ValueText);
    }

    private static CType MapType(ITypeSymbol type) => type switch
    {
        { SpecialType: SpecialType.System_Void } => CType.Void,
        { SpecialType: SpecialType.System_Int32 } => CType.I32,
        { SpecialType: SpecialType.System_Single } => CType.F32,
        { SpecialType: SpecialType.System_Boolean } => CType.Bool,
        IArrayTypeSymbol { Rank: 1 } array => CType.Array(MapType(array.ElementType)),
        INamedTypeSymbol { SpecialType: SpecialType.None } named => CType.Ref(named.Name),
        _ => throw new LuocException($"type is not supported by the first C backend slice: " +
            type.ToDisplayString()),
    };

    private static void AddUnique<TKey, TValue>(Dictionary<TKey, TValue> dictionary,
        TKey key, TValue value, string kind) where TKey : notnull
    {
        if (!dictionary.TryAdd(key, value))
            throw new LuocException($"duplicate {kind}: {key}");
    }
}
