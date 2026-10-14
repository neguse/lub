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
