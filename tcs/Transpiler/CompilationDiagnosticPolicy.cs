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
            "CS0535" => IsFieldBackedInterfaceFacade(compilation, diagnostic),
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

    private static bool IsFieldBackedInterfaceFacade(
        CSharpCompilation compilation, Diagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree;
        if (tree == null) return false;

        var root = tree.GetRoot();
        var node = root.FindNode(diagnostic.Location.SourceSpan,
            getInnermostNodeForTie: true);
        var declaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (declaration == null) return false;

        var model = compilation.GetSemanticModel(tree);
        if (model.GetDeclaredSymbol(declaration) is not
            { TypeKind: TypeKind.Class } classSymbol)
        {
            return false;
        }

        var missingMembers = classSymbol.AllInterfaces
            .SelectMany(@interface => @interface.GetMembers())
            .Where(IsRequiredInterfaceMember)
            .Where(member =>
                classSymbol.FindImplementationForInterfaceMember(member) == null)
            .Distinct(SymbolEqualityComparer.Default)
            .ToArray();

        return missingMembers.Length > 0
               && missingMembers.All(member =>
                   member is IPropertySymbol property
                   && HasCompatibleField(classSymbol, property));
    }

    private static bool IsRequiredInterfaceMember(ISymbol member) => member switch
    {
        IMethodSymbol { IsAbstract: true, AssociatedSymbol: null } => true,
        IPropertySymbol { IsAbstract: true } => true,
        IEventSymbol { IsAbstract: true } => true,
        _ => false
    };

    private static bool HasCompatibleField(
        INamedTypeSymbol classSymbol, IPropertySymbol property)
    {
        if (property.IsStatic || property.IsIndexer
            || property.ReturnsByRef || property.ReturnsByRefReadonly)
        {
            return false;
        }

        return classSymbol.GetMembers(property.Name)
            .OfType<IFieldSymbol>()
            .Any(field =>
                SymbolEqualityComparer.Default.Equals(
                    field.ContainingType, classSymbol)
                && field.DeclaredAccessibility == Accessibility.Public
                && !field.IsStatic
                && !field.IsConst
                && SymbolEqualityComparer.Default.Equals(
                    field.Type, property.Type)
                && (property.SetMethod == null || !field.IsReadOnly));
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
