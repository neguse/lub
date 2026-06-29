using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public static class TinyCsDiagnosticIds
{
    public const string UnsupportedSyntax = "TCS1001";
    public const string UnsupportedApi = "TCS1002";
    public const string UnsupportedCollectionNull = "TCS1003";
}

public static class TinyCsComplianceFacts
{
    public static readonly SyntaxKind[] UnsupportedSyntaxKinds =
    [
        SyntaxKind.StructDeclaration,
        SyntaxKind.RecordStructDeclaration,
        SyntaxKind.TryStatement,
        SyntaxKind.ThrowStatement,
        SyntaxKind.UsingStatement,
        SyntaxKind.LocalDeclarationStatement,
        SyntaxKind.LocalFunctionStatement,
        SyntaxKind.ListPattern,
        SyntaxKind.SlicePattern,
    ];

    public static readonly SyntaxKind[] CollectionNullSyntaxKinds =
    [
        SyntaxKind.ObjectCreationExpression,
        SyntaxKind.ImplicitObjectCreationExpression,
        SyntaxKind.InvocationExpression,
        SyntaxKind.SimpleAssignmentExpression,
    ];

    private static readonly string[] UnsupportedNamespacePrefixes =
    [
        "System.IO",
        "System.Net",
        "System.Reflection",
        "System.Text",
        "System.Threading",
    ];

    private static readonly HashSet<string> UnsupportedFullTypeNames =
        new(StringComparer.Ordinal)
        {
            "System.DateTime",
            "System.DateTimeOffset",
            "System.TimeSpan",
            "System.Guid",
        };

    private static readonly HashSet<string> SupportedMathMembers =
        new(StringComparer.Ordinal)
        {
            "PI",
            "Min",
            "Max",
            "Clamp",
            "Abs",
            "Floor",
            "Ceiling",
            "Sqrt",
            "Sin",
            "Cos",
            "Atan2",
            "Pow",
        };

    private static readonly HashSet<string> SupportedStringMembers =
        new(StringComparer.Ordinal)
        {
            "Length",
            "Contains",
            "Replace",
            "StartsWith",
            "EndsWith",
            "Trim",
            "Substring",
            "Split",
            "ToUpper",
            "ToLower",
            "ToString",
            "IndexOf",
            "Join",
        };

    private static readonly HashSet<string> SupportedListMembers =
        new(StringComparer.Ordinal)
        {
            "Count",
            "Add",
            "Remove",
            "RemoveAt",
            "Clear",
            "Contains",
            "IndexOf",
            "Sort",
        };

    private static readonly HashSet<string> SupportedDictionaryMembers =
        new(StringComparer.Ordinal)
        {
            "Count",
            "Keys",
            "Values",
            "Add",
            "Remove",
            "ContainsKey",
            "TryGetValue",
        };

    private static readonly HashSet<string> SupportedLinqMembers =
        new(StringComparer.Ordinal)
        {
            "Where",
            "Select",
            "Any",
            "All",
            "First",
            "FirstOrDefault",
            "Last",
            "LastOrDefault",
            "OrderBy",
            "OrderByDescending",
            "Take",
            "Skip",
            "Min",
            "Max",
            "Sum",
            "Count",
            "ToList",
            "ToDictionary",
        };

    public static bool TryGetUnsupportedSyntax(SyntaxNode node,
        out string syntaxName)
    {
        syntaxName = node switch
        {
            StructDeclarationSyntax => "StructDeclaration",
            RecordDeclarationSyntax record
                when record.Kind() == SyntaxKind.RecordStructDeclaration
                    => "RecordStructDeclaration",
            TryStatementSyntax => "TryStatement",
            ThrowStatementSyntax => "ThrowStatement",
            UsingStatementSyntax => "UsingStatement",
            LocalDeclarationStatementSyntax local
                when local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                    => "UsingDeclaration",
            LocalFunctionStatementSyntax => "LocalFunctionStatement",
            ListPatternSyntax => "ListPattern",
            SlicePatternSyntax => "SlicePattern",
            _ => "",
        };

        return syntaxName.Length > 0;
    }

    public static IEnumerable<string> AnalyzeUnsupportedSyntaxes(
        SyntaxTree tree)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            if (!TryGetUnsupportedSyntax(node, out var syntaxName))
                continue;

            yield return FormatWarning(node,
                TinyCsDiagnosticIds.UnsupportedSyntax,
                $"unsupported syntax: {syntaxName}");
        }
    }

    public static bool TryGetUnsupportedApi(ISymbol? symbol, out string apiName)
    {
        apiName = "";
        if (symbol == null) return false;

        var targetSymbol = symbol is IMethodSymbol { ReducedFrom: not null } method
            ? method.ReducedFrom
            : symbol;
        var containingType = GetContainingType(targetSymbol);
        if (containingType == null) return false;

        var typeName = FormatTypeName(containingType);
        var namespaceName = containingType.ContainingNamespace?.ToDisplayString()
            ?? "";

        if (IsPartiallySupportedApi(targetSymbol, containingType))
        {
            if (IsSupportedApiMember(targetSymbol, containingType))
                return false;

            apiName = FormatApiName(targetSymbol, containingType, typeName);
            return true;
        }

        if (!UnsupportedFullTypeNames.Contains(typeName)
            && !IsUnsupportedNamespace(namespaceName))
        {
            return false;
        }

        apiName = FormatApiName(targetSymbol, containingType, typeName);
        return true;
    }

    public static IEnumerable<string> AnalyzeUnsupportedCollectionNulls(
        SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            if (!CollectionNullSyntaxKinds.Contains(node.Kind()))
                continue;

            if (!TryGetUnsupportedCollectionNull(node, model,
                    out var description))
            {
                continue;
            }

            yield return FormatWarning(node,
                TinyCsDiagnosticIds.UnsupportedCollectionNull,
                $"unsupported collection null: {description}");
        }
    }

    public static IEnumerable<string> AnalyzeUnsupportedApis(
        SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            if (!TryGetUnsupportedApi(node, model, out var apiName))
                continue;

            yield return FormatWarning(node,
                TinyCsDiagnosticIds.UnsupportedApi,
                $"unsupported API: {apiName}");
        }
    }

    public static bool TryGetUnsupportedCollectionNull(SyntaxNode node,
        SemanticModel model, out string description)
    {
        description = "";

        return node switch
        {
            ObjectCreationExpressionSyntax creation =>
                TryGetUnsupportedCollectionInitializerNull(
                    model.GetTypeInfo(creation).Type,
                    creation.Initializer, model, out description),
            ImplicitObjectCreationExpressionSyntax creation =>
                TryGetUnsupportedCollectionInitializerNull(
                    model.GetTypeInfo(creation).Type,
                    creation.Initializer, model, out description),
            InvocationExpressionSyntax invocation =>
                TryGetUnsupportedCollectionInvocationNull(invocation, model,
                    out description),
            AssignmentExpressionSyntax assignment
                when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) =>
                TryGetUnsupportedCollectionAssignmentNull(assignment, model,
                    out description),
            _ => false,
        };
    }

    public static bool TryGetUnsupportedApi(SyntaxNode node,
        SemanticModel model, out string apiName)
    {
        apiName = "";

        ISymbol? symbol = node switch
        {
            InvocationExpressionSyntax invocation =>
                GetInvokedMethod(invocation, model),
            ObjectCreationExpressionSyntax creation =>
                model.GetSymbolInfo(creation).Symbol,
            ImplicitObjectCreationExpressionSyntax creation =>
                model.GetSymbolInfo(creation).Symbol,
            MemberAccessExpressionSyntax memberAccess
                when !IsInvocationTarget(memberAccess) =>
                model.GetSymbolInfo(memberAccess).Symbol,
            _ => null,
        };

        return TryGetUnsupportedApi(symbol, out apiName);
    }

    private static INamedTypeSymbol? GetContainingType(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol type => type,
            IMethodSymbol method => method.ContainingType,
            IPropertySymbol property => property.ContainingType,
            IFieldSymbol field => field.ContainingType,
            IEventSymbol evt => evt.ContainingType,
            _ => symbol.ContainingType,
        };

    private static bool TryGetUnsupportedCollectionInitializerNull(
        ITypeSymbol? collectionType, InitializerExpressionSyntax? initializer,
        SemanticModel model, out string description)
    {
        description = "";
        if (initializer == null) return false;

        if (IsListType(collectionType))
        {
            foreach (var expr in initializer.Expressions)
            {
                if (!IsNilLiteralOrDefault(expr, model)) continue;
                description =
                    "List<T> cannot store null elements in Lua sequence tables";
                return true;
            }
        }

        if (IsDictType(collectionType))
        {
            foreach (var expr in initializer.Expressions)
            {
                var value = expr switch
                {
                    InitializerExpressionSyntax init
                        when init.Expressions.Count == 2 => init.Expressions[1],
                    AssignmentExpressionSyntax assign => assign.Right,
                    _ => null,
                };

                if (value == null || !IsNilLiteralOrDefault(value, model))
                    continue;

                description =
                    "Dictionary<K,V> cannot store null values because nil removes keys";
                return true;
            }
        }

        return false;
    }

    private static bool TryGetUnsupportedCollectionInvocationNull(
        InvocationExpressionSyntax invocation, SemanticModel model,
        out string description)
    {
        description = "";

        var method = GetInvokedMethod(invocation, model);
        if (method == null) return false;

        var args = invocation.ArgumentList.Arguments;
        if (method.Name == "Add"
            && IsListType(method.ContainingType)
            && args.Count >= 1
            && IsNilLiteralOrDefault(args[0].Expression, model))
        {
            description =
                "List<T> cannot store null elements in Lua sequence tables";
            return true;
        }

        if (method.Name == "Add"
            && IsDictType(method.ContainingType)
            && args.Count >= 2
            && IsNilLiteralOrDefault(args[1].Expression, model))
        {
            description =
                "Dictionary<K,V> cannot store null values because nil removes keys";
            return true;
        }

        if (method.Name == "ToDictionary"
            && IsSystemLinqEnumerable(method.ContainingType)
            && method.ReducedFrom != null
            && args.Count >= 2
            && IsNilReturningLambda(args[1].Expression, model))
        {
            description =
                "Dictionary<K,V> cannot store null values because nil removes keys";
            return true;
        }

        if (method.Name == "ToDictionary"
            && IsTinySystemList(method.ContainingType)
            && args.Count >= 3
            && IsNilReturningLambda(args[2].Expression, model))
        {
            description =
                "Dictionary<K,V> cannot store null values because nil removes keys";
            return true;
        }

        return false;
    }

    private static bool TryGetUnsupportedCollectionAssignmentNull(
        AssignmentExpressionSyntax assignment, SemanticModel model,
        out string description)
    {
        description = "";

        if (assignment.Left is not ElementAccessExpressionSyntax element
            || !IsNilLiteralOrDefault(assignment.Right, model))
        {
            return false;
        }

        var receiverType = model.GetTypeInfo(element.Expression).Type;
        if (IsListType(receiverType))
        {
            description =
                "List<T> cannot store null elements in Lua sequence tables";
            return true;
        }

        if (IsDictType(receiverType))
        {
            description =
                "Dictionary<K,V> cannot store null values because nil removes keys";
            return true;
        }

        return false;
    }

    private static IMethodSymbol? GetInvokedMethod(
        InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var symbolInfo = model.GetSymbolInfo(invocation);
        return symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private static bool IsInvocationTarget(
        MemberAccessExpressionSyntax memberAccess) =>
        memberAccess.Parent is InvocationExpressionSyntax invocation
        && invocation.Expression == memberAccess;

    private static bool IsNilReturningLambda(ExpressionSyntax expr,
        SemanticModel model)
    {
        expr = StripNilTransparentSyntax(expr);
        return expr switch
        {
            SimpleLambdaExpressionSyntax lambda
                when lambda.ExpressionBody != null =>
                    IsNilLiteralOrDefault(lambda.ExpressionBody, model),
            ParenthesizedLambdaExpressionSyntax lambda
                when lambda.ExpressionBody != null =>
                    IsNilLiteralOrDefault(lambda.ExpressionBody, model),
            _ => false,
        };
    }

    private static bool IsNilLiteralOrDefault(ExpressionSyntax expr,
        SemanticModel model)
    {
        expr = StripNilTransparentSyntax(expr);
        if (expr.IsKind(SyntaxKind.NullLiteralExpression)) return true;
        if (!expr.IsKind(SyntaxKind.DefaultLiteralExpression)
            && expr is not DefaultExpressionSyntax)
        {
            return false;
        }

        var typeInfo = model.GetTypeInfo(expr);
        return CanBeNil(typeInfo.ConvertedType ?? typeInfo.Type);
    }

    private static ExpressionSyntax StripNilTransparentSyntax(
        ExpressionSyntax expr)
    {
        while (true)
        {
            expr = expr switch
            {
                ParenthesizedExpressionSyntax parenthesized =>
                    parenthesized.Expression,
                CastExpressionSyntax cast => cast.Expression,
                PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression)
                    => postfix.Operand,
                _ => expr,
            };

            if (expr is not ParenthesizedExpressionSyntax
                && expr is not CastExpressionSyntax
                && !expr.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            {
                return expr;
            }
        }
    }

    private static bool CanBeNil(ITypeSymbol? type)
    {
        if (type == null) return false;
        if (type.IsReferenceType) return true;
        if (type is ITypeParameterSymbol { HasReferenceTypeConstraint: true })
            return true;
        return type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    private static bool IsListType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.ToDisplayString(
            SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Collections.Generic.List<T>";

    private static bool IsDictType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.ToDisplayString(
            SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Collections.Generic.Dictionary<TKey, TValue>";

    private static bool IsSystemLinqEnumerable(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Linq.Enumerable";

    private static bool IsTinySystemList(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "TinySystem.List";

    private static bool IsPartiallySupportedApi(ISymbol symbol,
        INamedTypeSymbol containingType)
    {
        if (IsStringType(containingType)
            || IsMathType(containingType)
            || IsListType(containingType)
            || IsDictType(containingType)
            || IsSystemLinqEnumerable(containingType))
        {
            return true;
        }

        return false;
    }

    private static bool IsSupportedApiMember(ISymbol symbol,
        INamedTypeSymbol containingType)
    {
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor })
            return IsListType(containingType) || IsDictType(containingType);

        var memberName = symbol.Name;
        if (IsStringType(containingType))
            return SupportedStringMembers.Contains(memberName);
        if (IsMathType(containingType))
            return SupportedMathMembers.Contains(memberName);
        if (IsListType(containingType))
            return SupportedListMembers.Contains(memberName);
        if (IsDictType(containingType))
            return SupportedDictionaryMembers.Contains(memberName);
        if (IsSystemLinqEnumerable(containingType))
            return SupportedLinqMembers.Contains(memberName);

        return false;
    }

    private static bool IsStringType(ITypeSymbol? type) =>
        type?.SpecialType == SpecialType.System_String;

    private static bool IsMathType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == "System.Math";

    public static string FormatWarning(SyntaxNode node, string diagnosticId,
        string message)
    {
        var loc = node.GetLocation().GetLineSpan();
        var line = loc.StartLinePosition.Line + 1;
        var col = loc.StartLinePosition.Character + 1;
        var file = loc.Path;
        var prefix = string.IsNullOrEmpty(file) ? "" : file;
        return $"{prefix}({line},{col}): warning {diagnosticId}: {message}";
    }

    private static bool IsUnsupportedNamespace(string namespaceName)
    {
        foreach (var prefix in UnsupportedNamespacePrefixes)
        {
            if (namespaceName == prefix
                || namespaceName.StartsWith(prefix + ".",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatTypeName(INamedTypeSymbol type) =>
        type.IsGenericType
            ? type.OriginalDefinition.ToDisplayString(
                SymbolDisplayFormat.CSharpErrorMessageFormat)
            : type.ToDisplayString(
                SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static string FormatApiName(ISymbol symbol, INamedTypeSymbol type,
        string typeName) =>
        symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } => typeName,
            IMethodSymbol method => $"{typeName}.{method.Name}",
            IPropertySymbol property => $"{typeName}.{property.Name}",
            IFieldSymbol field => $"{typeName}.{field.Name}",
            IEventSymbol evt => $"{typeName}.{evt.Name}",
            INamedTypeSymbol => typeName,
            _ when SymbolEqualityComparer.Default.Equals(symbol.ContainingType, type)
                => $"{typeName}.{symbol.Name}",
            _ => typeName,
        };
}
