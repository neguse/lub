using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public partial class LuaEmitter
{
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
            ImplicitObjectCreationExpressionSyntax ic => VisitImplicitObjectCreation(model, ic),
            ThisExpressionSyntax => "self",
            BaseExpressionSyntax => "self",
            CastExpressionSyntax cast => VisitExpression(model, cast.Expression),
            ConditionalExpressionSyntax ternary => VisitTernary(model, ternary),
            InterpolatedStringExpressionSyntax interp => VisitInterpolatedString(model, interp),
            SimpleLambdaExpressionSyntax lambda => VisitSimpleLambda(model, lambda),
            ParenthesizedLambdaExpressionSyntax lambda =>
                VisitParenthesizedLambda(model, lambda),
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

    private string VisitPostfixUnary(SemanticModel model, PostfixUnaryExpressionSyntax postfix)
    {
        var operand = VisitExpression(model, postfix.Operand);
        return postfix.Kind() switch
        {
            SyntaxKind.PostIncrementExpression => $"{operand} + 1",
            SyntaxKind.PostDecrementExpression => $"{operand} - 1",
            _ => $"--[[ TODO postfix: {postfix.Kind()} ]]"
        };
    }

    private string VisitInvocation(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList();

        if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            var symbol = model.GetSymbolInfo(ma).Symbol;
            if (symbol is IMethodSymbol { IsStatic: false })
            {
                var obj = VisitExpression(model, ma.Expression);
                return $"{obj}:{ma.Name.Identifier.Text}({string.Join(", ", args)})";
            }
            var target = VisitExpression(model, invocation.Expression);
            return $"{target}({string.Join(", ", args)})";
        }

        var targetExpr = VisitExpression(model, invocation.Expression);
        return $"{targetExpr}({string.Join(", ", args)})";
    }

    private string VisitMemberAccess(SemanticModel model,
        MemberAccessExpressionSyntax memberAccess)
    {
        var obj = VisitExpression(model, memberAccess.Expression);
        var member = memberAccess.Name.Identifier.Text;
        return $"{obj}.{member}";
    }

    private string VisitAssignment(SemanticModel model, AssignmentExpressionSyntax assignment)
    {
        var left = VisitExpression(model, assignment.Left);
        var right = VisitExpression(model, assignment.Right);
        return assignment.Kind() switch
        {
            SyntaxKind.SimpleAssignmentExpression => $"{left} = {right}",
            SyntaxKind.AddAssignmentExpression => $"{left} = {left} + {right}",
            SyntaxKind.SubtractAssignmentExpression => $"{left} = {left} - {right}",
            SyntaxKind.MultiplyAssignmentExpression => $"{left} = {left} * {right}",
            SyntaxKind.DivideAssignmentExpression => $"{left} = {left} / {right}",
            SyntaxKind.ModuloAssignmentExpression => $"{left} = {left} % {right}",
            _ => $"--[[ TODO assign: {assignment.Kind()} ]]"
        };
    }

    private string VisitObjectCreation(SemanticModel model,
        ObjectCreationExpressionSyntax creation)
    {
        var typeSymbol = model.GetTypeInfo(creation).Type;
        var typeName = typeSymbol?.Name ?? creation.Type.ToString();
        var args = creation.ArgumentList?.Arguments
            .Select(a => VisitExpression(model, a.Expression)).ToList() ?? [];
        return $"{typeName}.new({string.Join(", ", args)})";
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
        var trueExpr = VisitExpression(model, ternary.WhenTrue);
        var falseExpr = VisitExpression(model, ternary.WhenFalse);
        return $"(function() if {cond} then return {trueExpr} else return {falseExpr} end end)()";
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

    private string VisitSimpleLambda(SemanticModel model, SimpleLambdaExpressionSyntax lambda)
    {
        var param = lambda.Parameter.Identifier.Text;
        if (lambda.ExpressionBody != null)
            return $"function({param}) return {VisitExpression(model, lambda.ExpressionBody)} end";
        return $"function({param}) {VisitLambdaBlock(model, lambda.Block!)} end";
    }

    private string VisitParenthesizedLambda(SemanticModel model,
        ParenthesizedLambdaExpressionSyntax lambda)
    {
        var parameters = string.Join(", ",
            lambda.ParameterList.Parameters.Select(p => p.Identifier.Text));
        if (lambda.ExpressionBody != null)
            return $"function({parameters}) return {VisitExpression(model, lambda.ExpressionBody)} end";
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
