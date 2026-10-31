using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

internal static class CompilationDiagnosticPolicy
{
    public static bool IsAllowed(
        CSharpCompilation compilation, Diagnostic diagnostic)
    {
        if (!diagnostic.Location.IsInSource)
            return false;

        return diagnostic.Id switch
        {
            "CS0266" or "CS0029" =>
                IsEnumIntegerConversion(compilation, diagnostic),
            "CS0019" => IsEnumIntegerEquality(compilation, diagnostic),
            _ => false
        };
    }

    private static bool IsEnumIntegerConversion(
        CSharpCompilation compilation, Diagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree;
        if (tree == null) return false;

        var root = tree.GetRoot();
        var node = root.FindNode(diagnostic.Location.SourceSpan,
            getInnermostNodeForTie: true);
        var expression = node as ExpressionSyntax
                         ?? node.FirstAncestorOrSelf<ExpressionSyntax>();
        if (expression == null) return false;

        var model = compilation.GetSemanticModel(tree);
        var typeInfo = model.GetTypeInfo(expression);
        return IsEnumIntegerPair(typeInfo.Type, typeInfo.ConvertedType);
    }

    private static bool IsEnumIntegerEquality(
        CSharpCompilation compilation, Diagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree;
        if (tree == null) return false;

        var root = tree.GetRoot();
        var node = root.FindNode(diagnostic.Location.SourceSpan,
            getInnermostNodeForTie: true);
        var binary = node.FirstAncestorOrSelf<BinaryExpressionSyntax>();
        if (binary == null
            || !binary.IsKind(SyntaxKind.EqualsExpression)
            && !binary.IsKind(SyntaxKind.NotEqualsExpression))
        {
            return false;
        }

        var model = compilation.GetSemanticModel(tree);
        return IsEnumIntegerPair(
            model.GetTypeInfo(binary.Left).Type,
            model.GetTypeInfo(binary.Right).Type);
    }

    private static bool IsEnumIntegerPair(
        ITypeSymbol? left, ITypeSymbol? right) =>
        left?.TypeKind == TypeKind.Enum && IsInteger(right)
        || right?.TypeKind == TypeKind.Enum && IsInteger(left);

    private static bool IsInteger(ITypeSymbol? type) => type?.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Byte
        or SpecialType.System_Int16 or SpecialType.System_UInt16
        or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64
        or SpecialType.System_IntPtr or SpecialType.System_UIntPtr;
}
