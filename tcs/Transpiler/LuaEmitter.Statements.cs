using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public partial class LuaEmitter
{
    private void VisitStatement(SemanticModel model, StatementSyntax stmt)
    {
        switch (stmt)
        {
            case ReturnStatementSyntax ret:
                AppendLine(ret.Expression != null
                    ? $"return {VisitExpression(model, ret.Expression)}" : "return");
                break;
            case LocalDeclarationStatementSyntax local:
                foreach (var v in local.Declaration.Variables)
                {
                    var init = v.Initializer != null
                        ? $" = {VisitExpression(model, v.Initializer.Value)}" : "";
                    AppendLine($"local {v.Identifier.Text}{init}");
                }
                break;
            case ExpressionStatementSyntax exprStmt:
                VisitExpressionStatement(model, exprStmt);
                break;
            case IfStatementSyntax ifStmt:
                VisitIf(model, ifStmt, isRoot: true);
                break;
            case WhileStatementSyntax whileStmt:
                AppendLine($"while {VisitExpression(model, whileStmt.Condition)} do");
                _indent++;
                VisitBlock(model, whileStmt.Statement);
                _indent--;
                AppendLine("end");
                break;
            case ForStatementSyntax forStmt:
                VisitFor(model, forStmt);
                break;
            case ForEachStatementSyntax foreachStmt:
                VisitForEach(model, foreachStmt);
                break;
            case BlockSyntax block:
                foreach (var s in block.Statements)
                    VisitStatement(model, s);
                break;
            case BreakStatementSyntax:
                AppendLine("break");
                break;
        }
    }

    private void VisitExpressionStatement(SemanticModel model, ExpressionStatementSyntax exprStmt)
    {
        // Handle i++ / i-- as statements: emit as i = i + 1
        if (exprStmt.Expression is PostfixUnaryExpressionSyntax postfix)
        {
            var operand = VisitExpression(model, postfix.Operand);
            var op = postfix.Kind() switch
            {
                SyntaxKind.PostIncrementExpression => "+",
                SyntaxKind.PostDecrementExpression => "-",
                _ => null
            };
            if (op != null) { AppendLine($"{operand} = {operand} {op} 1"); return; }
        }
        if (exprStmt.Expression is PrefixUnaryExpressionSyntax prefix)
        {
            var operand = VisitExpression(model, prefix.Operand);
            var op = prefix.Kind() switch
            {
                SyntaxKind.PreIncrementExpression => "+",
                SyntaxKind.PreDecrementExpression => "-",
                _ => null
            };
            if (op != null) { AppendLine($"{operand} = {operand} {op} 1"); return; }
        }
        AppendLine(VisitExpression(model, exprStmt.Expression));
    }

    private void VisitBlock(SemanticModel model, StatementSyntax stmt)
    {
        if (stmt is BlockSyntax block)
            foreach (var s in block.Statements) VisitStatement(model, s);
        else
            VisitStatement(model, stmt);
    }

    private void VisitIf(SemanticModel model, IfStatementSyntax ifStmt, bool isRoot)
    {
        AppendLine($"{(isRoot ? "if" : "elseif")} {VisitExpression(model, ifStmt.Condition)} then");
        _indent++;
        VisitBlock(model, ifStmt.Statement);
        _indent--;

        if (ifStmt.Else != null)
        {
            if (ifStmt.Else.Statement is IfStatementSyntax elseIf)
                VisitIf(model, elseIf, isRoot: false);
            else
            {
                AppendLine("else");
                _indent++;
                VisitBlock(model, ifStmt.Else.Statement);
                _indent--;
                AppendLine("end");
            }
        }
        else
        {
            AppendLine("end");
        }
    }

    private void VisitFor(SemanticModel model, ForStatementSyntax forStmt)
    {
        if (TryEmitSimpleFor(model, forStmt)) return;

        // General case: while loop
        if (forStmt.Declaration != null)
            foreach (var v in forStmt.Declaration.Variables)
            {
                var init = v.Initializer != null
                    ? $" = {VisitExpression(model, v.Initializer.Value)}" : "";
                AppendLine($"local {v.Identifier.Text}{init}");
            }

        var cond = forStmt.Condition != null
            ? VisitExpression(model, forStmt.Condition) : "true";
        AppendLine($"while {cond} do");
        _indent++;
        VisitBlock(model, forStmt.Statement);
        foreach (var inc in forStmt.Incrementors)
            AppendLine(VisitExpression(model, inc));
        _indent--;
        AppendLine("end");
    }

    private bool TryEmitSimpleFor(SemanticModel model, ForStatementSyntax forStmt)
    {
        if (forStmt.Declaration?.Variables.Count != 1) return false;
        var decl = forStmt.Declaration.Variables[0];
        if (decl.Initializer == null) return false;
        var varName = decl.Identifier.Text;
        var start = VisitExpression(model, decl.Initializer.Value);

        if (forStmt.Condition is not BinaryExpressionSyntax cond) return false;
        if (cond.Left is not IdentifierNameSyntax condId
            || condId.Identifier.Text != varName) return false;

        if (forStmt.Incrementors.Count != 1) return false;
        var inc = forStmt.Incrementors[0];
        bool isIncByOne = inc is PostfixUnaryExpressionSyntax
                { RawKind: (int)SyntaxKind.PostIncrementExpression }
            || (inc is AssignmentExpressionSyntax
                { RawKind: (int)SyntaxKind.AddAssignmentExpression } addAssign
                && addAssign.Right is LiteralExpressionSyntax { Token.Text: "1" });
        if (!isIncByOne) return false;

        var end = VisitExpression(model, cond.Right);
        var limit = cond.Kind() switch
        {
            SyntaxKind.LessThanExpression => $"{end} - 1",
            SyntaxKind.LessThanOrEqualExpression => end,
            _ => null
        };
        if (limit == null) return false;

        AppendLine($"for {varName} = {start}, {limit} do");
        _indent++;
        VisitBlock(model, forStmt.Statement);
        _indent--;
        AppendLine("end");
        return true;
    }

    private void VisitForEach(SemanticModel model, ForEachStatementSyntax foreachStmt)
    {
        var varName = foreachStmt.Identifier.Text;
        var collection = VisitExpression(model, foreachStmt.Expression);
        var typeInfo = model.GetTypeInfo(foreachStmt.Expression);
        var typeName = typeInfo.Type?.OriginalDefinition.ToDisplayString() ?? "";

        if (typeName.StartsWith("System.Collections.Generic.Dictionary"))
        {
            AppendLine($"for {varName}_key, {varName}_value in pairs({collection}) do");
            _indent++;
            AppendLine($"local {varName} = {{Key = {varName}_key, Value = {varName}_value}}");
            VisitBlock(model, foreachStmt.Statement);
            _indent--;
            AppendLine("end");
        }
        else
        {
            AppendLine($"for _, {varName} in ipairs({collection}) do");
            _indent++;
            VisitBlock(model, foreachStmt.Statement);
            _indent--;
            AppendLine("end");
        }
    }
}
