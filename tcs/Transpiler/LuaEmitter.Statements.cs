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
            case ExpressionStatementSyntax exprStmt
                when exprStmt.Expression is AssignmentExpressionSyntax
                {
                    Left: TupleExpressionSyntax tuple,
                    Right: var tupleRhs,
                    RawKind: (int)SyntaxKind.SimpleAssignmentExpression
                }:
                VisitTupleDeconstruction(model, tuple, tupleRhs);
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
            var op = postfix.Kind() switch
            {
                SyntaxKind.PostIncrementExpression => "+",
                SyntaxKind.PostDecrementExpression => "-",
                _ => null
            };
            if (op != null) { EmitIncrement(model, postfix.Operand, op); return; }
        }
        if (expr is PrefixUnaryExpressionSyntax prefix)
        {
            var op = prefix.Kind() switch
            {
                SyntaxKind.PreIncrementExpression => "+",
                SyntaxKind.PreDecrementExpression => "-",
                _ => null
            };
            if (op != null) { EmitIncrement(model, prefix.Operand, op); return; }
        }
        // ??= as statement: emit as if-then block
        if (expr is AssignmentExpressionSyntax
            { RawKind: (int)SyntaxKind.CoalesceAssignmentExpression } coalesce)
        {
            if (TryGetCustomPropertyTarget(model, coalesce.Left) != null)
            {
                // custom property は VisitAssignment の set_/get_ 経路に任せる
                AppendLine(VisitExpression(model, coalesce));
                return;
            }
            var right = VisitExpression(model, coalesce.Right);
            if (TryLowerLvalue(model, coalesce.Left) is { } l)
            {
                AppendLine($"do {l.Setup}if {l.Access} == nil then " +
                    $"{l.Access} = {right} end end");
                return;
            }
            var left = VisitExpression(model, coalesce.Left);
            AppendLine($"if {left} == nil then");
            _indent++;
            AppendLine($"{left} = {right}");
            _indent--;
            AppendLine("end");
            return;
        }
        AppendLine(VisitExpression(model, expr));
    }

    private void EmitIncrement(SemanticModel model, ExpressionSyntax operand,
        string op)
    {
        if (TryGetCustomPropertyTarget(model, operand) is { } prop)
        {
            var target = prop.SideEffect ? "__tcs_obj" : prop.Receiver;
            var body = $"{target}{prop.CallOp}set_{prop.Name}(" +
                $"{target}{prop.CallOp}get_{prop.Name}() {op} 1)";
            AppendLine(prop.SideEffect
                ? $"do local __tcs_obj = {prop.Receiver}; {body} end"
                : body);
            return;
        }
        if (TryLowerLvalue(model, operand) is { } l)
        {
            AppendLine($"do {l.Setup}{l.Access} = {l.Access} {op} 1 end");
            return;
        }
        var target2 = VisitExpression(model, operand);
        AppendLine($"{target2} = {target2} {op} 1");
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

        // Lua numeric for は limit を一度しか評価しないため、C# が毎 iteration
        // 再評価する条件と一致するのは bound が loop-invariant のときだけ。
        // それ以外 (member/call bound、関数内で再代入される local、body 内で
        // 書き換えられる loop 変数) は while lowering へ fallback する。
        if (!IsLoopInvariantBound(model, forStmt, cond.Right)) return false;
        if (IsAssignedWithin(forStmt.Statement, varName)) return false;

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

    private static bool IsLoopInvariantBound(SemanticModel model,
        ForStatementSyntax forStmt, ExpressionSyntax bound)
    {
        if (bound is LiteralExpressionSyntax) return true;
        if (bound is not IdentifierNameSyntax id) return false;

        // local / parameter のみ (field / property は body 内の呼び出し経由で
        // 変わり得る)。関数スコープ内のどこかで再代入されるなら不変とみなさない
        // (loop 後の再代入も含む過剰判定だが、fallback は常に正しい)。
        var symbol = model.GetSymbolInfo(id).Symbol;
        if (symbol is not (ILocalSymbol or IParameterSymbol)) return false;

        var scope = forStmt.Ancestors().FirstOrDefault(a =>
            a is BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax
                or AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax
                or CompilationUnitSyntax) ?? forStmt;
        return !IsAssignedWithin(scope, id.Identifier.ValueText);
    }

    private static bool IsAssignedWithin(SyntaxNode scope, string name) =>
        scope.DescendantNodes().Any(n => n switch
        {
            AssignmentExpressionSyntax assign =>
                assign.Left is IdentifierNameSyntax target
                && target.Identifier.ValueText == name,
            PostfixUnaryExpressionSyntax post
                when post.IsKind(SyntaxKind.PostIncrementExpression)
                    || post.IsKind(SyntaxKind.PostDecrementExpression) =>
                post.Operand is IdentifierNameSyntax target
                && target.Identifier.ValueText == name,
            PrefixUnaryExpressionSyntax pre
                when pre.IsKind(SyntaxKind.PreIncrementExpression)
                    || pre.IsKind(SyntaxKind.PreDecrementExpression) =>
                pre.Operand is IdentifierNameSyntax target
                && target.Identifier.ValueText == name,
            ArgumentSyntax arg =>
                !arg.RefKindKeyword.IsKind(SyntaxKind.None)
                && arg.Expression is IdentifierNameSyntax target
                && target.Identifier.ValueText == name,
            _ => false,
        });

    private void VisitSwitch(SemanticModel model, SwitchStatementSyntax switchStmt)
    {
        // 対象式は local へ一度だけ評価する。ネストした switch は内側 block の
        // local が shadow する。
        var governing = VisitExpression(model, switchStmt.Expression);
        AppendLine($"local __tcs_sw = {governing}");
        var defaultSection = switchStmt.Sections
            .FirstOrDefault(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
        var sections = switchStmt.Sections
            .Where(s => !s.Labels.Any(l => l is DefaultSwitchLabelSyntax))
            .ToList();

        // declaration pattern の designation は if chain の前で束縛する
        // (is-pattern の VisitIf と同じ方針)
        foreach (var label in sections
            .SelectMany(s => s.Labels.OfType<CasePatternSwitchLabelSyntax>())
            .Where(l => l.Pattern is DeclarationPatternSyntax
            {
                Designation: SingleVariableDesignationSyntax
            }))
        {
            var dp = (DeclarationPatternSyntax)label.Pattern;
            var sv = (SingleVariableDesignationSyntax)dp.Designation!;
            AppendLine($"local {sv.Identifier.ValueText} = __tcs_sw");
        }

        bool first = true;
        foreach (var section in sections)
        {
            var conditions = section.Labels.Select(l => l switch
            {
                // `case Circle:` は旧構文の定数ラベルとして parse されるため、
                // semantic で型と判れば metatable 比較にする
                CaseSwitchLabelSyntax c
                    when model.GetSymbolInfo(c.Value).Symbol is ITypeSymbol =>
                    $"getmetatable(__tcs_sw) == {VisitExpression(model, c.Value)}",
                CaseSwitchLabelSyntax c =>
                    $"__tcs_sw == {VisitExpression(model, c.Value)}",
                CasePatternSwitchLabelSyntax p => FormatPatternLabel(model, p),
                _ => WarnUnsupported(l, $"switch label: {l.Kind()}"),
            });
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

    private string FormatPatternLabel(SemanticModel model,
        CasePatternSwitchLabelSyntax label)
    {
        var condition = VisitPattern(model, label.Pattern, "__tcs_sw");
        return label.WhenClause != null
            ? $"({condition} and {VisitExpression(model, label.WhenClause.Condition)})"
            : $"({condition})";
    }

    private void VisitDeconstruction(SemanticModel model,
        DeclarationExpressionSyntax declExpr, ExpressionSyntax rhs)
    {
        if (declExpr.Designation is not ParenthesizedVariableDesignationSyntax pvd)
            return;

        var names = pvd.Variables
            .Select(v => v is SingleVariableDesignationSyntax sv
                ? sv.Identifier.ValueText : "_").ToList();
        EmitDeconstruction(model, rhs, names, declare: true);
    }

    // (a, b) = rhs — 既存変数への分解代入。宣言側と同じ一回評価経路を使う。
    private void VisitTupleDeconstruction(SemanticModel model,
        TupleExpressionSyntax tuple, ExpressionSyntax rhs)
    {
        var targets = new List<string>();
        foreach (var arg in tuple.Arguments)
        {
            if (arg.Expression is DeclarationExpressionSyntax)
            {
                // 混在形 (var a, b) は宣言と代入のスコープが割れるため未対応
                AppendLine(WarnUnsupported(arg.Expression,
                    "mixed declaration in tuple deconstruction"));
                return;
            }
            targets.Add(VisitExpression(model, arg.Expression));
        }
        EmitDeconstruction(model, rhs, targets, declare: false);
    }

    private void EmitDeconstruction(SemanticModel model, ExpressionSyntax rhs,
        List<string> targets, bool declare)
    {
        // RHS は一度だけ評価する (従来は要素数分の式複製で多重評価だった)
        AppendLine($"local __tcs_dec = {VisitExpression(model, rhs)}");
        var typeSymbol = model.GetTypeInfo(rhs).Type;
        var propNames = GetDeconstructPropertyNames(typeSymbol, targets.Count);
        var values = propNames != null
            ? string.Join(", ", propNames.Select(p => $"__tcs_dec.{p}"))
            : "__tcs_dec";
        var prefix = declare ? "local " : "";
        AppendLine($"{prefix}{string.Join(", ", targets)} = {values}");
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
