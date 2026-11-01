using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public sealed record IlForeignParameter(string Name, string Type, bool IsOut, IlExpr? Default);
public sealed record IlForeignMethod(string Name, string ReturnType,
    ImmutableArray<IlForeignParameter> Parameters, string? Receiver = null);
public sealed record IlForeignValue(string Name, string Type, int? Constant = null);

public static partial class IlExport
{
    private static IlExportResult ExportForeign(CSharpCompilation compilation,
        SyntaxTree[] trees, SyntaxTree[] references, LuaEmitter emitter,
        Dictionary<string, List<(string Name, string Type)>> structLayouts, IlExportResult result)
    {
        if (references.Length == 0) return result;
        var methods = new Dictionary<string, IlForeignMethod>();
        var signatures = new Dictionary<string, string>();
        var values = new Dictionary<string, IlForeignValue>();
        var enums = new Dictionary<string, IlEnumInfo>();
        var classes = result.Classes.ToList();
        var pending = new Queue<INamedTypeSymbol>();
        // 別 namespace の同じ simple 名の型 (A.Opts と B.Opts) を別の型として辿る。
        // generic は構築型ではなく定義で 1 つにまとめる
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        bool Foreign(ISymbol symbol) => symbol.DeclaringSyntaxReferences
            .Any(r => references.Contains(r.SyntaxTree));
        void Type(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array) { Type(array.ElementType); return; }
            if (type is not INamedTypeSymbol named) return;
            foreach (var argument in named.TypeArguments) Type(argument);
            if (!Foreign(named) || named.IsStatic) return;
            if (named.TypeKind == TypeKind.Enum)
            {
                var members = new List<(string, int)>();
                foreach (var field in named.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                {
                    var key = $"{LuaNaming.RefTypePath(named)}.{LuaNaming.MemberName(field)}";
                    values[key] = new IlForeignValue(key, named.ToDisplayString(), Convert.ToInt32(field.ConstantValue));
                    members.Add((LuaNaming.Const(field.Name), Convert.ToInt32(field.ConstantValue)));
                }
                // foreign enum も user enum と同じ定数表に載せる (C backend は整数)
                enums[named.ToDisplayString()] = new IlEnumInfo(named.ToDisplayString(), [.. members]);
            }
            else if (visited.Add(named.OriginalDefinition)) pending.Enqueue(named);
        }
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<ExpressionSyntax>())
            {
                if (node is not (InvocationExpressionSyntax or MemberAccessExpressionSyntax
                    or IdentifierNameSyntax or ObjectCreationExpressionSyntax)) continue;
                var symbol = model.GetSymbolInfo(node).Symbol;
                if (symbol == null || !Foreign(symbol)) continue;
                if (symbol is INamedTypeSymbol named) Type(named);
                if (symbol is IFieldSymbol field)
                {
                    Type(field.Type);
                    if (field.IsStatic && field.ContainingType.TypeKind != TypeKind.Enum)
                    {
                        var key = $"{LuaNaming.RefTypePath(field.ContainingType)}.{LuaNaming.MemberName(field)}";
                        values[key] = new IlForeignValue(key, field.Type.ToDisplayString());
                    }
                    else Type(field.ContainingType);
                }
                if (symbol is not IMethodSymbol method) continue;
                Type(method.ContainingType);
                // static は `Class.name(args)`、instance (Receiver = class 名) は
                // IlInvoke の受け手を先頭引数に取る host 関数になる
                if (method.MethodKind != MethodKind.Ordinary
                    || !method.IsStatic && method.ContainingType.TypeKind != TypeKind.Class) continue;
                Type(method.ReturnType);
                foreach (var parameter in method.Parameters) Type(parameter.Type);
                var methodKey = $"{LuaNaming.RefTypePath(method.ContainingType)}.{LuaNaming.MemberName(method)}";
                var signature = method.ToDisplayString();
                if (signatures.TryGetValue(methodKey, out var existing) && existing != signature)
                    return result with { Diagnostics = [.. result.Diagnostics,
                        $"C foreign overloads require distinct names: {methodKey}"] };
                signatures[methodKey] = signature;
                var parameters = method.Parameters.Select(p =>
                {
                    var syntax = p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as ParameterSyntax;
                    var defaultValue = syntax?.Default is { } d
                        ? emitter.ExportExprIl(compilation.GetSemanticModel(d.SyntaxTree), d.Value) : null;
                    return new IlForeignParameter(p.Name, p.Type.ToDisplayString(), p.RefKind == RefKind.Out, defaultValue);
                });
                methods[methodKey] = new IlForeignMethod(methodKey, method.ReturnType.ToDisplayString(), [.. parameters],
                    method.IsStatic ? null : emitter.TypeName(method.ContainingType));
            }
        }
        while (pending.TryDequeue(out var type))
        {
            if (type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not ClassDeclarationSyntax declaration)
                continue;
            var cls = ExportClass(emitter, compilation.GetSemanticModel(declaration.SyntaxTree), declaration, structLayouts);
            classes.Add(cls with { Methods = [], Ctors = [], IsExternal = true });
            if (type.BaseType is { } parent) Type(parent);
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>()) Type(field.Type);
        }
        return result with { Classes = [.. classes], ForeignMethods = [.. methods.Values],
            ForeignValues = [.. values.Values],
            EnumTypes = [.. (result.EnumTypes.IsDefault ? [] : result.EnumTypes), .. enums.Values] };
    }
}

/// <summary>module 単位の混在 (doc/object-model.md §10): C (tcs2c) の module の公開 static method を、VM の module が
/// `--ref` で呼ぶときの host の名前。VM の emitter の host の名前 (ExportForeign の key) と同じ規則で作る。</summary>
public sealed record IlHostFunction(string Name, string TypeDisplay, string Method);

public static partial class IlExport
{
    public static List<IlHostFunction> HostSurface(string[] sources)
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        var compilation = CSharpCompilation.Create("HostSurface", trees, Transpiler.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, concurrentBuild: false));
        var result = new List<IlHostFunction>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type || type.IsGenericType) continue;
                foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (!m.IsStatic || m.MethodKind != MethodKind.Ordinary || m.DeclaredAccessibility != Accessibility.Public)
                        continue;
                    result.Add(new IlHostFunction($"{LuaNaming.RefTypePath(type)}.{LuaNaming.MemberName(m)}",
                        type.ToDisplayString(), m.Name));
                }
            }
        }
        return result;
    }
}
