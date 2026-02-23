using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public partial class LuaEmitter
{
    // List/Dict method names that map to runtime library calls
    private static readonly HashSet<string> ListRuntimeMethods =
        ["Where", "Select", "Any", "All", "First", "FirstOrDefault",
         "OrderBy", "Min", "Max", "Sum", "ToList", "Contains", "IndexOf"];

    private string VisitExpression(SemanticModel model, ExpressionSyntax expr)
    {
        return expr switch
        {
            LiteralExpressionSyntax lit => VisitLiteral(lit),
            IdentifierNameSyntax id => ResolveIdentifier(model, id),
            BinaryExpressionSyntax bin => VisitBinary(model, bin),
            PrefixUnaryExpressionSyntax prefix => VisitPrefixUnary(model, prefix),
            PostfixUnaryExpressionSyntax postfix => VisitPostfixUnary(model, postfix),
            ParenthesizedExpressionSyntax paren =>
                $"({VisitExpression(model, paren.Expression)})",
            InvocationExpressionSyntax invocation => VisitInvocation(model, invocation),
            MemberAccessExpressionSyntax ma => VisitMemberAccess(model, ma),
            AssignmentExpressionSyntax assignment => VisitAssignment(model, assignment),
            ObjectCreationExpressionSyntax creation => VisitObjectCreation(model, creation),
            ImplicitObjectCreationExpressionSyntax ic =>
                VisitImplicitObjectCreation(model, ic),
            ThisExpressionSyntax => "self",
            BaseExpressionSyntax => "self",
            CastExpressionSyntax cast => VisitExpression(model, cast.Expression),
            ConditionalExpressionSyntax ternary => VisitTernary(model, ternary),
            InterpolatedStringExpressionSyntax interp =>
                VisitInterpolatedString(model, interp),
            SimpleLambdaExpressionSyntax lambda => VisitSimpleLambda(model, lambda),
            ParenthesizedLambdaExpressionSyntax lambda =>
                VisitParenthesizedLambda(model, lambda),
            ElementAccessExpressionSyntax elemAccess =>
                VisitElementAccess(model, elemAccess),
            _ => $"--[[ TODO: {expr.Kind()} ]]"
        };
    }

    private string ResolveIdentifier(SemanticModel model, IdentifierNameSyntax id)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        if (symbol is IMethodSymbol method && method.ContainingType != null)
        {
            var sep = method.IsStatic ? "." : ":";
            return $"{method.ContainingType.Name}{sep}{method.Name}";
        }
        if (symbol is IFieldSymbol { IsStatic: false }
            or IPropertySymbol { IsStatic: false })
            return $"self.{id.Identifier.Text}";
        return id.Identifier.Text;
    }

    private static string VisitLiteral(LiteralExpressionSyntax lit) => lit.Kind() switch
    {
        SyntaxKind.NumericLiteralExpression => lit.Token.Text,
        SyntaxKind.StringLiteralExpression => lit.Token.Text,
        SyntaxKind.TrueLiteralExpression => "true",
        SyntaxKind.FalseLiteralExpression => "false",
        SyntaxKind.NullLiteralExpression => "nil",
        _ => $"--[[ TODO literal: {lit.Kind()} ]]"
    };

    private string VisitBinary(SemanticModel model, BinaryExpressionSyntax bin)
    {
        var left = VisitExpression(model, bin.Left);
        var right = VisitExpression(model, bin.Right);
        var op = bin.Kind() switch
        {
            SyntaxKind.AddExpression => "+",
            SyntaxKind.SubtractExpression => "-",
            SyntaxKind.MultiplyExpression => "*",
            SyntaxKind.DivideExpression => "/",
            SyntaxKind.ModuloExpression => "%",
            SyntaxKind.EqualsExpression => "==",
            SyntaxKind.NotEqualsExpression => "~=",
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.GreaterThanExpression => ">",
            SyntaxKind.GreaterThanOrEqualExpression => ">=",
            SyntaxKind.LogicalAndExpression => "and",
            SyntaxKind.LogicalOrExpression => "or",
            SyntaxKind.CoalesceExpression => "or",
            _ => $"--[[{bin.Kind()}]]"
        };
        return $"{left} {op} {right}";
    }

    private string VisitPrefixUnary(SemanticModel model, PrefixUnaryExpressionSyntax prefix)
    {
        var operand = VisitExpression(model, prefix.Operand);
        return prefix.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => $"-{operand}",
            SyntaxKind.LogicalNotExpression => $"not {operand}",
            SyntaxKind.PreIncrementExpression => $"({operand} + 1)",
            SyntaxKind.PreDecrementExpression => $"({operand} - 1)",
            _ => $"--[[ TODO unary: {prefix.Kind()} ]]"
        };
    }

    private string VisitPostfixUnary(SemanticModel model,
        PostfixUnaryExpressionSyntax postfix)
    {
        var operand = VisitExpression(model, postfix.Operand);
        return postfix.Kind() switch
        {
            SyntaxKind.PostIncrementExpression => $"{operand} + 1",
            SyntaxKind.PostDecrementExpression => $"{operand} - 1",
            _ => $"--[[ TODO postfix: {postfix.Kind()} ]]"
        };
    }

    private string VisitInvocation(SemanticModel model,
        InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList();

        if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            var symbol = model.GetSymbolInfo(ma).Symbol;
            var methodName = ma.Name.Identifier.Text;

            // Check for List<T>/IEnumerable<T> method calls → runtime library
            if (symbol is IMethodSymbol methodSym && TryMapCollectionMethod(
                    model, ma, methodSym, methodName, args, out var result))
                return result;

            // Regular instance method
            if (symbol is IMethodSymbol { IsStatic: false })
            {
                var obj = VisitExpression(model, ma.Expression);
                return $"{obj}:{methodName}({string.Join(", ", args)})";
            }

            var target = VisitExpression(model, invocation.Expression);
            return $"{target}({string.Join(", ", args)})";
        }

        var targetExpr = VisitExpression(model, invocation.Expression);
        return $"{targetExpr}({string.Join(", ", args)})";
    }

    private bool TryMapCollectionMethod(SemanticModel model,
        MemberAccessExpressionSyntax ma, IMethodSymbol methodSym,
        string methodName, List<string> args, out string result)
    {
        result = "";
        var receiverType = model.GetTypeInfo(ma.Expression).Type;
        if (receiverType == null) return false;

        var typeDef = receiverType.OriginalDefinition.ToDisplayString();
        var obj = VisitExpression(model, ma.Expression);

        // List<T> instance methods → List.Method(list, args)
        if (IsListType(typeDef))
        {
            switch (methodName)
            {
                case "Add":
                    result = $"table.insert({obj}, {string.Join(", ", args)})";
                    return true;
                case "Remove":
                    result = $"List.Remove({obj}, {string.Join(", ", args)})";
                    return true;
                case "RemoveAt":
                    result = $"table.remove({obj}, {args[0]} + 1)";
                    return true;
                case "Clear":
                    result = $"(function() for k in pairs({obj}) do {obj}[k] = nil end end)()";
                    return true;
            }
            if (ListRuntimeMethods.Contains(methodName))
            {
                var allArgs = new List<string> { obj };
                allArgs.AddRange(args);
                result = $"List.{methodName}({string.Join(", ", allArgs)})";
                return true;
            }
        }

        // Dictionary<K,V> methods
        if (IsDictType(typeDef))
        {
            switch (methodName)
            {
                case "Add":
                    result = $"{obj}[{args[0]}] = {args[1]}";
                    return true;
                case "Remove":
                    result = $"Dict.Remove({obj}, {args[0]})";
                    return true;
                case "ContainsKey":
                    result = $"({obj}[{args[0]}] ~= nil)";
                    return true;
                case "TryGetValue":
                    // Simplified: just check and assign
                    result = $"({obj}[{args[0]}] ~= nil)";
                    return true;
            }
        }

        // LINQ extension methods on IEnumerable<T>
        if (methodSym.IsExtensionMethod && ListRuntimeMethods.Contains(methodName))
        {
            var allArgs = new List<string> { obj };
            allArgs.AddRange(args);
            result = $"List.{methodName}({string.Join(", ", allArgs)})";
            return true;
        }

        return false;
    }

    private static bool IsListType(string typeDef) =>
        typeDef.StartsWith("System.Collections.Generic.List<");

    private static bool IsDictType(string typeDef) =>
        typeDef.StartsWith("System.Collections.Generic.Dictionary<");

    private string VisitMemberAccess(SemanticModel model,
        MemberAccessExpressionSyntax memberAccess)
    {
        var obj = VisitExpression(model, memberAccess.Expression);
        var member = memberAccess.Name.Identifier.Text;

        // Check for .Count on List<T> → #list
        var symbol = model.GetSymbolInfo(memberAccess).Symbol;
        if (symbol is IPropertySymbol propSym)
        {
            var receiverType = model.GetTypeInfo(memberAccess.Expression).Type;
            var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";
            if (member == "Count" && (IsListType(typeDef) || IsDictType(typeDef)))
            {
                return IsDictType(typeDef)
                    ? $"Dict.Count({obj})"
                    : $"#{obj}";
            }
            if (member == "Keys" && IsDictType(typeDef))
                return $"Dict.Keys({obj})";
            if (member == "Values" && IsDictType(typeDef))
                return $"Dict.Values({obj})";
        }

        return $"{obj}.{member}";
    }

    private string VisitElementAccess(SemanticModel model,
        ElementAccessExpressionSyntax elemAccess)
    {
        var obj = VisitExpression(model, elemAccess.Expression);
        var index = VisitExpression(model, elemAccess.ArgumentList.Arguments[0].Expression);
        var receiverType = model.GetTypeInfo(elemAccess.Expression).Type;
        var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";

        // List<T> indexer: 0-indexed → 1-indexed
        if (IsListType(typeDef))
            return $"{obj}[{index} + 1]";

        // Dictionary<K,V> indexer
        return $"{obj}[{index}]";
    }

    private string VisitAssignment(SemanticModel model, AssignmentExpressionSyntax assign)
    {
        var left = VisitExpression(model, assign.Left);
        var right = VisitExpression(model, assign.Right);
        return assign.Kind() switch
        {
            SyntaxKind.SimpleAssignmentExpression => $"{left} = {right}",
            SyntaxKind.AddAssignmentExpression => $"{left} = {left} + {right}",
            SyntaxKind.SubtractAssignmentExpression => $"{left} = {left} - {right}",
            SyntaxKind.MultiplyAssignmentExpression => $"{left} = {left} * {right}",
            SyntaxKind.DivideAssignmentExpression => $"{left} = {left} / {right}",
            SyntaxKind.ModuloAssignmentExpression => $"{left} = {left} % {right}",
            _ => $"--[[ TODO assign: {assign.Kind()} ]]"
        };
    }

    private string VisitObjectCreation(SemanticModel model,
        ObjectCreationExpressionSyntax creation)
    {
        var typeSymbol = model.GetTypeInfo(creation).Type;
        var typeName = typeSymbol?.Name ?? creation.Type.ToString();
        var typeDef = typeSymbol?.OriginalDefinition.ToDisplayString() ?? "";

        // new List<T> { ... } → { items }
        if (IsListType(typeDef))
            return VisitListInitializer(model, creation);
        // new Dictionary<K,V> { ... } → { [k]=v, ... }
        if (IsDictType(typeDef))
            return VisitDictInitializer(model, creation);

        var args = creation.ArgumentList?.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList() ?? [];
        return $"{typeName}.new({string.Join(", ", args)})";
    }

    private string VisitListInitializer(SemanticModel model,
        ObjectCreationExpressionSyntax creation)
    {
        if (creation.Initializer == null) return "{}";
        var items = creation.Initializer.Expressions
            .Select(e => VisitExpression(model, e));
        return $"{{{string.Join(", ", items)}}}";
    }

    private string VisitDictInitializer(SemanticModel model,
        ObjectCreationExpressionSyntax creation)
    {
        if (creation.Initializer == null) return "{}";
        var entries = new List<string>();
        foreach (var expr in creation.Initializer.Expressions)
        {
            if (expr is InitializerExpressionSyntax kvInit && kvInit.Expressions.Count == 2)
            {
                var key = VisitExpression(model, kvInit.Expressions[0]);
                var value = VisitExpression(model, kvInit.Expressions[1]);
                entries.Add($"[{key}] = {value}");
            }
            else if (expr is AssignmentExpressionSyntax assignExpr)
            {
                var key = VisitExpression(model, assignExpr.Left);
                var value = VisitExpression(model, assignExpr.Right);
                entries.Add($"[{key}] = {value}");
            }
        }
        return $"{{{string.Join(", ", entries)}}}";
    }

    private string VisitImplicitObjectCreation(SemanticModel model,
        ImplicitObjectCreationExpressionSyntax creation)
    {
        var typeSymbol = model.GetTypeInfo(creation).ConvertedType;
        var typeName = typeSymbol?.Name ?? "UNKNOWN";
        var args = creation.ArgumentList.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList();
        return $"{typeName}.new({string.Join(", ", args)})";
    }

    private string VisitTernary(SemanticModel model, ConditionalExpressionSyntax ternary)
    {
        var cond = VisitExpression(model, ternary.Condition);
        var t = VisitExpression(model, ternary.WhenTrue);
        var f = VisitExpression(model, ternary.WhenFalse);
        return $"(function() if {cond} then return {t} else return {f} end end)()";
    }

    private string VisitInterpolatedString(SemanticModel model,
        InterpolatedStringExpressionSyntax interp)
    {
        var parts = new List<string>();
        foreach (var content in interp.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    parts.Add($"\"{text.TextToken.Text}\"");
                    break;
                case InterpolationSyntax hole:
                    parts.Add($"tostring({VisitExpression(model, hole.Expression)})");
                    break;
            }
        }
        return string.Join(" .. ", parts);
    }

    private string VisitSimpleLambda(SemanticModel model,
        SimpleLambdaExpressionSyntax lambda)
    {
        var param = lambda.Parameter.Identifier.Text;
        if (lambda.ExpressionBody != null)
            return $"function({param}) return " +
                   $"{VisitExpression(model, lambda.ExpressionBody)} end";
        return $"function({param}) {VisitLambdaBlock(model, lambda.Block!)} end";
    }

    private string VisitParenthesizedLambda(SemanticModel model,
        ParenthesizedLambdaExpressionSyntax lambda)
    {
        var parameters = string.Join(", ",
            lambda.ParameterList.Parameters.Select(p => p.Identifier.Text));
        if (lambda.ExpressionBody != null)
            return $"function({parameters}) return " +
                   $"{VisitExpression(model, lambda.ExpressionBody)} end";
        return $"function({parameters}) {VisitLambdaBlock(model, lambda.Block!)} end";
    }

    private string VisitLambdaBlock(SemanticModel model, BlockSyntax block)
    {
        var savedSb = _sb.ToString();
        var savedIndent = _indent;
        _sb.Clear();
        _indent = 0;

        foreach (var s in block.Statements)
            VisitStatement(model, s);

        var body = _sb.ToString().Trim();
        _sb.Clear();
        _sb.Append(savedSb);
        _indent = savedIndent;
        return body;
    }
}
