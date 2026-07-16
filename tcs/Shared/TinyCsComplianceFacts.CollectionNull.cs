using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TinyCs;

// TCS1003: Lua table で表現できない collection null 保存の検出
public static partial class TinyCsComplianceFacts
{
    public static readonly SyntaxKind[] CollectionNullSyntaxKinds =
    [
        SyntaxKind.ObjectCreationExpression,
        SyntaxKind.ImplicitObjectCreationExpression,
        SyntaxKind.InvocationExpression,
        SyntaxKind.SimpleAssignmentExpression,
    ];

    // Lua 5.5 reserved words (deps/lua llex.c luaX_tokens). C# identifiers
    // emitted under these names produce syntactically invalid Lua
    // (`function C:end()`, `local repeat`), so declarations are rejected.

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
}
