using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public partial class LuaEmitter
{
    private void VisitStatement(SemanticModel model, StatementSyntax stmt)
    {
        SetSource(stmt);
        if (stmt is not BlockSyntax && EmitOutVarDeclarations(stmt))
            SetSource(stmt);

        switch (stmt)
        {
            case ReturnStatementSyntax ret:
                AppendLine(ret.Expression != null
                    ? $"return {VisitExpression(model, ret.Expression)}" : "return");
                break;
            case LocalDeclarationStatementSyntax decon
                when decon.Declaration.Variables.Count == 1
                && decon.Declaration.Variables[0].Initializer?.Value
                    is AssignmentExpressionSyntax
                    {
                        Left: DeclarationExpressionSyntax declExpr,
                        Right: var rhs
                    }:
                VisitDeconstruction(model, declExpr, rhs);
                break;
            case LocalDeclarationStatementSyntax local
                when local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword):
                AppendLine(WarnUnsupported(local, "statement: UsingDeclaration"));
                break;
            case LocalDeclarationStatementSyntax local:
                foreach (var v in local.Declaration.Variables)
                {
                    var init = v.Initializer != null
                        ? $" = {VisitExpression(model, v.Initializer.Value)}" : "";
                    AppendLine($"local {v.Identifier.ValueText}{init}");
                }
                break;
            case ExpressionStatementSyntax exprStmt
                when exprStmt.Expression is AssignmentExpressionSyntax
                {
                    Left: DeclarationExpressionSyntax declExpr2,
                    Right: var rhs2
                }:
                VisitDeconstruction(model, declExpr2, rhs2);
                break;
            case ExpressionStatementSyntax exprStmt:
                VisitExpressionStatement(model, exprStmt);
                break;
            case IfStatementSyntax ifStmt:
                VisitIf(model, ifStmt, isRoot: true);
                break;
            case WhileStatementSyntax whileStmt:
                VisitWhile(model, whileStmt);
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
            case ContinueStatementSyntax:
                if (_continueStack.Count > 0)
                {
                    var lbl = _continueStack.Peek();
                    _usedContinueLabels.Add(lbl);
                    AppendLine($"goto _continue_{lbl}");
                }
                break;
            case DoStatementSyntax doStmt:
                VisitDoWhile(model, doStmt);
                break;
            case SwitchStatementSyntax switchStmt:
                VisitSwitch(model, switchStmt);
                break;
            case LockStatementSyntax lockStmt:
                AppendLine(WarnUnsupported(lockStmt,
                    "statement: LockStatement"));
                AppendLine("do");
                _indent++;
                VisitBlock(model, lockStmt.Statement);
                _indent--;
                AppendLine("end");
                break;
            default:
                AppendLine(WarnUnsupported(stmt, $"statement: {stmt.Kind()}"));
                break;
        }
    }

    private bool EmitOutVarDeclarations(StatementSyntax stmt)
    {
        var names = stmt.DescendantNodes()
            .OfType<ArgumentSyntax>()
            .Where(arg => arg.Ancestors().OfType<StatementSyntax>().FirstOrDefault() == stmt)
            .Select(TryGetOutArgumentName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .ToList();

        foreach (var name in names)
            AppendLine($"local {name}");

        return names.Count > 0;
    }

    private void VisitExpressionStatement(SemanticModel model, ExpressionStatementSyntax exprStmt)
    {
        VisitExpressionAsStatement(model, exprStmt.Expression);
    }

    private void VisitExpressionAsStatement(SemanticModel model, ExpressionSyntax expr)
    {
        // Handle i++ / i-- as statements: emit as i = i + 1
        if (expr is PostfixUnaryExpressionSyntax postfix)
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
        if (expr is PrefixUnaryExpressionSyntax prefix)
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
        // ??= as statement: emit as if-then block
        if (expr is AssignmentExpressionSyntax
            { RawKind: (int)SyntaxKind.CoalesceAssignmentExpression } coalesce)
        {
            var left = VisitExpression(model, coalesce.Left);
            var right = VisitExpression(model, coalesce.Right);
            AppendLine($"if {left} == nil then");
            _indent++;
            AppendLine($"{left} = {right}");
            _indent--;
            AppendLine("end");
            return;
        }
        AppendLine(VisitExpression(model, expr));
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
        // Declaration pattern: emit local variable binding before the if
        if (isRoot && ifStmt.Condition is IsPatternExpressionSyntax
            { Pattern: DeclarationPatternSyntax dp } isPat)
        {
            var expr = VisitExpression(model, isPat.Expression);
            var varName = dp.Designation is SingleVariableDesignationSyntax sv
                ? sv.Identifier.ValueText : "_";
            AppendLine($"local {varName} = {expr}");
        }
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

    private void VisitWhile(SemanticModel model, WhileStatementSyntax whileStmt)
    {
        var label = PushContinueLabel();
        AppendLine($"while {VisitExpression(model, whileStmt.Condition)} do");
        _indent++;
        VisitBlock(model, whileStmt.Statement);
        EmitContinueLabel(label);
        _indent--;
        AppendLine("end");
        PopContinueLabel();
    }

    private void VisitDoWhile(SemanticModel model, DoStatementSyntax doStmt)
    {
        var label = PushContinueLabel();
        AppendLine("repeat");
        _indent++;
        VisitBlock(model, doStmt.Statement);
        EmitContinueLabel(label);
        _indent--;
        AppendLine($"until not ({VisitExpression(model, doStmt.Condition)})");
        PopContinueLabel();
    }

    private int PushContinueLabel()
    {
        var label = ++_continueCounter;
        _continueStack.Push(label);
        return label;
    }

    private void PopContinueLabel() => _continueStack.Pop();

    private void EmitContinueLabel(int label)
    {
        if (_usedContinueLabels.Contains(label))
            AppendLine($"::_continue_{label}::");
    }

    private readonly HashSet<int> _usedContinueLabels = [];

    private static bool ContainsContinue(StatementSyntax stmt) => stmt
        .DescendantNodes()
        .OfType<ContinueStatementSyntax>()
        .Any();

    private void VisitFor(SemanticModel model, ForStatementSyntax forStmt)
    {
        if (TryEmitSimpleFor(model, forStmt)) return;

        // General case: while loop
        if (forStmt.Declaration != null)
            foreach (var v in forStmt.Declaration.Variables)
            {
                var init = v.Initializer != null
                    ? $" = {VisitExpression(model, v.Initializer.Value)}" : "";
                AppendLine($"local {v.Identifier.ValueText}{init}");
            }

        var label = PushContinueLabel();
        var cond = forStmt.Condition != null
            ? VisitExpression(model, forStmt.Condition) : "true";
        AppendLine($"while {cond} do");
        _indent++;
        VisitBlock(model, forStmt.Statement);
        EmitContinueLabel(label);
        foreach (var inc in forStmt.Incrementors)
            VisitExpressionAsStatement(model, inc);
        _indent--;
        AppendLine("end");
        PopContinueLabel();
    }

    private bool TryEmitSimpleFor(SemanticModel model, ForStatementSyntax forStmt)
    {
        if (forStmt.Declaration?.Variables.Count != 1) return false;
        var decl = forStmt.Declaration.Variables[0];
        if (decl.Initializer == null) return false;
        var varName = decl.Identifier.ValueText;
        var start = VisitExpression(model, decl.Initializer.Value);

        if (forStmt.Condition is not BinaryExpressionSyntax cond) return false;
        if (cond.Left is not IdentifierNameSyntax condId
            || condId.Identifier.ValueText != varName) return false;

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

        var label = PushContinueLabel();
        AppendLine($"for {varName} = {start}, {limit} do");
        _indent++;
        VisitBlock(model, forStmt.Statement);
        EmitContinueLabel(label);
        _indent--;
        AppendLine("end");
        PopContinueLabel();
        return true;
    }

    private void VisitSwitch(SemanticModel model, SwitchStatementSyntax switchStmt)
    {
        var governing = VisitExpression(model, switchStmt.Expression);
        var defaultSection = switchStmt.Sections
            .FirstOrDefault(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
        var sections = switchStmt.Sections
            .Where(s => !s.Labels.Any(l => l is DefaultSwitchLabelSyntax))
            .ToList();
        bool first = true;
        foreach (var section in sections)
        {
            var conditions = section.Labels
                .OfType<CaseSwitchLabelSyntax>()
                .Select(l => $"{governing} == {VisitExpression(model, l.Value)}");
            var cond = string.Join(" or ", conditions);
            AppendLine($"{(first ? "if" : "elseif")} {cond} then");
            _indent++;
            foreach (var stmt in section.Statements)
            {
                // Skip break statements in switch (they're implicit in Lua if-elseif)
                if (stmt is BreakStatementSyntax) continue;
                VisitStatement(model, stmt);
            }
            _indent--;
            first = false;
        }

        if (defaultSection != null)
        {
            AppendLine(first ? "do" : "else");
            _indent++;
            foreach (var stmt in defaultSection.Statements)
            {
                if (stmt is BreakStatementSyntax) continue;
                VisitStatement(model, stmt);
            }
            _indent--;
        }

        AppendLine("end");
    }

    private void VisitDeconstruction(SemanticModel model,
        DeclarationExpressionSyntax declExpr, ExpressionSyntax rhs)
    {
        var obj = VisitExpression(model, rhs);
        if (declExpr.Designation is ParenthesizedVariableDesignationSyntax pvd)
        {
            var names = pvd.Variables
                .Select(v => v is SingleVariableDesignationSyntax sv
                    ? sv.Identifier.ValueText : "_").ToList();
            // Get type of rhs to find property/parameter names
            var typeSymbol = model.GetTypeInfo(rhs).Type;
            var propNames = GetDeconstructPropertyNames(typeSymbol, names.Count);

            if (propNames != null)
            {
                var values = propNames.Select(p => $"{obj}.{p}");
                AppendLine($"local {string.Join(", ", names)} = {string.Join(", ", values)}");
            }
            else
            {
                AppendLine($"local {string.Join(", ", names)} = {obj}");
            }
        }
    }

    private static List<string>? GetDeconstructPropertyNames(ITypeSymbol? type, int count)
    {
        if (type is not INamedTypeSymbol named) return null;
        // Record positional parameters → properties with matching names
        var props = named.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public)
            .Select(p => p.Name).ToList();
        if (props.Count >= count) return props.Take(count).ToList();
        return null;
    }

    private void VisitForEach(SemanticModel model, ForEachStatementSyntax foreachStmt)
    {
        var varName = foreachStmt.Identifier.ValueText;
        var collection = VisitExpression(model, foreachStmt.Expression);
        var typeInfo = model.GetTypeInfo(foreachStmt.Expression);
        var typeName = typeInfo.Type?.OriginalDefinition.ToDisplayString() ?? "";

        var label = PushContinueLabel();
        if (typeName.StartsWith("System.Collections.Generic.Dictionary"))
        {
            AppendLine($"for {varName}_key, {varName}_value in pairs({collection}) do");
            _indent++;
            AppendLine($"local {varName} = {{Key = {varName}_key, Value = {varName}_value}}");
            VisitBlock(model, foreachStmt.Statement);
            EmitContinueLabel(label);
            _indent--;
            AppendLine("end");
        }
        else
        {
            AppendLine($"for _, {varName} in ipairs({collection}) do");
            _indent++;
            VisitBlock(model, foreachStmt.Statement);
            EmitContinueLabel(label);
            _indent--;
            AppendLine("end");
        }
        PopContinueLabel();
    }
}
