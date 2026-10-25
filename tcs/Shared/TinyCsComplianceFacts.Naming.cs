using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public static partial class TinyCsComplianceFacts
{
    public const string RefInterfaceImplementationPrefix = "RefInterfaceImplementation(";

    /// <summary>user の型の base list にある `--ref` (host) の interface。host の
    /// member は snake_case、user の実装は C# の名前なので、interface 経由の呼び出し
    /// (`h:do_thing()`) が実装 (`Impl:DoThing`) に届かない。build を止める。
    /// host の interface は、宣言が参照専用の tree にあるもの (isReferenceTree) か、
    /// source の無い metadata で runtime (TinySystem / BCL) 以外の assembly のもの。
    /// analyzer は SimpleBaseType の node action で呼ぶ (参照専用の tree は無い)。</summary>
    public static bool TryGetRefInterfaceImplementation(SyntaxNode node,
        SemanticModel model, Func<SyntaxTree, bool>? isReferenceTree,
        out string syntaxName)
    {
        syntaxName = "";
        if (node is not BaseTypeSyntax { Parent: BaseListSyntax { Parent: TypeDeclarationSyntax } } baseType
            || model.GetTypeInfo(baseType.Type).Type is not
                INamedTypeSymbol { TypeKind: TypeKind.Interface } iface
            || !IsHostInterface(iface, isReferenceTree))
            return false;
        syntaxName = $"{RefInterfaceImplementationPrefix}{iface.ToDisplayString()})";
        return true;
    }

    /// <summary>overload は名前に parameter の型を付けて別の key にする (LuaNaming.MethodName)。
    /// その名前が同じ型の他の member と同じ綴りになる宣言 (別の interface から同名の method を
    /// 実装する場合を含む) と、名前に型を付けない host (`--ref`) の型の overload を止める。
    /// host の宣言の tree は診断しないので、host の overload は参照する名前の側で止める
    /// (拡張 method の形の呼び出しも、基底の型の同名の method も、private は呼べないので数えない)。
    /// analyzer は参照専用の tree を持たない。</summary>
    public static bool TryGetUnsupportedOverload(SyntaxNode node, SemanticModel model,
        Func<SyntaxTree, bool>? isReferenceTree, out string syntaxName)
    {
        var unsupported = node switch
        {
            MethodDeclarationSyntax decl => model.GetDeclaredSymbol(decl) is IMethodSymbol
                { MethodKind: MethodKind.Ordinary } declared && IsCollidingOverload(declared),
            SimpleNameSyntax name when isReferenceTree != null =>
                model.GetSymbolInfo(name).Symbol is IMethodSymbol
                    { MethodKind: MethodKind.Ordinary or MethodKind.ReducedExtension } method
                && (method.ReducedFrom ?? method).OriginalDefinition is var definition
                && definition.DeclaringSyntaxReferences is { Length: > 0 } refs
                && refs.All(r => isReferenceTree(r.SyntaxTree))
                && HostOverloadCount(definition) > 1,
            _ => false,
        };
        syntaxName = unsupported ? "MethodOverload" : "";
        return unsupported;
    }

    private static int HostOverloadCount(IMethodSymbol method)
    {
        var count = 0;
        for (var type = method.ContainingType; type != null; type = type.BaseType)
            count += type.GetMembers(method.Name).OfType<IMethodSymbol>()
                .Count(m => m.MethodKind == MethodKind.Ordinary
                    && m.DeclaredAccessibility != Accessibility.Private);
        return count;
    }

    private static bool IsCollidingOverload(IMethodSymbol method)
    {
        if (LuaNaming.ImplementedInterfaceMethods(method)
                .Select(LuaNaming.MethodName).Distinct().Count() > 1)
            return true;
        var name = LuaNaming.MethodName(method);
        return method.ContainingType.GetMembers().Any(other =>
            !SymbolEqualityComparer.Default.Equals(other, method)
            && (other is IMethodSymbol { MethodKind: MethodKind.Ordinary } otherMethod
                ? LuaNaming.MethodName(otherMethod)
                : other.Name) == name);
    }

    private static bool IsHostInterface(INamedTypeSymbol iface,
        Func<SyntaxTree, bool>? isReferenceTree)
    {
        var declarations = iface.OriginalDefinition.DeclaringSyntaxReferences;
        if (declarations.Length > 0)
            return isReferenceTree != null
                && declarations.All(r => isReferenceTree(r.SyntaxTree));
        var assembly = iface.ContainingAssembly?.Name ?? "";
        return assembly != RuntimeRootGlobal
            && !assembly.StartsWith("System", StringComparison.Ordinal)
            && assembly != "mscorlib" && assembly != "netstandard";
    }
}
