using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// syntax + SemanticModel → IL 構築。意味決定 (idiv/concat/+1/facade 解決) は
// すべてここで済ませ、未対応構文は null を返して method 全体を legacy 経路へ
// fallback する (ストラングラー方式)。legacy visitor と出力が一致するよう、
// 判定は legacy と同じ helper (VisitLiteral / CompoundOperator / IsListType 等)
// を共有する。診断を出し得る経路 (WarnUnsupported) は必ず fallback にする。
public partial class LuaEmitter
{
    private IlBlock? TryBuildIlBody(SemanticModel model,
        MethodDeclarationSyntax method)
    {
        if (method.Body == null) return null;
        var stats = new List<IlStat>();
        return BuildStatsInto(model, method.Body.Statements, stats)
            ? new IlBlock([.. stats]) : null;
    }

    // IlExport (M2) 用: method body の IL を出力せずに構築して返す。
    // expression body は IlReturn 1 文へ正規化する。
    internal IlBlock? ExportMethodIl(SemanticModel model,
        MethodDeclarationSyntax method)
    {
        if (method.Body != null) return TryBuildIlBody(model, method);
        if (method.ExpressionBody != null
            && BuildExpr(model, method.ExpressionBody.Expression) is { } expr)
            return new IlBlock([new IlReturn(expr)]);
        return null;
    }

    // IlExport (T228) 用: 式の IL を出力せずに構築して返す (field initializer 等)
    internal IlExpr? ExportExprIl(SemanticModel model, ExpressionSyntax expr) =>
        BuildExpr(model, expr);

    // ---- 共通フック (T214c): 文列 / return 式 / 文位置式を IL 経由で emit ----

    private bool TryEmitStatsViaIl(SemanticModel model,
        IEnumerable<StatementSyntax> statements)
    {
        if (IlDisabled) return false;
        var acc = new List<IlStat>();
        if (!BuildStatsInto(model, statements, acc)) return false;
        IlBodies++;
        EmitIlBlock(new IlBlock([.. acc]));
        return true;
    }

    private bool TryEmitReturnViaIl(SemanticModel model, ExpressionSyntax expr)
    {
        if (IlDisabled) return false;
        var built = BuildExpr(model, expr);
        if (built == null) return false;
        IlBodies++;
        AppendLine($"return {RenderIl(built)}");
        return true;
    }

    private bool TryEmitExprStatViaIl(SemanticModel model,
        ExpressionSyntax expr)
    {
        if (IlDisabled) return false;
        var acc = new List<IlStat>();
        if (!BuildExprStatInto(model, expr, null, acc)) return false;
        IlBodies++;
        foreach (var stat in acc) EmitIlStat(stat);
        return true;
    }

    private bool BuildStatsInto(SemanticModel model,
        IEnumerable<StatementSyntax> statements, List<IlStat> acc)
    {
        foreach (var stmt in statements)
            if (!BuildStatInto(model, stmt, acc)) return false;
        return true;
    }

    private bool BuildStatInto(SemanticModel model, StatementSyntax stmt,
        List<IlStat> acc)
    {
        // out var / is-pattern designation の前宣言 (legacy
        // EmitOutVarDeclarations と同じ位置・同じ名前集合)
        if (stmt is not BlockSyntax)
            foreach (var name in CollectPreDeclNames(stmt))
                acc.Add(new IlLocal(name, null) { Origin = stmt });

        switch (stmt)
        {
            case ReturnStatementSyntax ret:
            {
                IlExpr? value = null;
                if (ret.Expression != null
                    && (value = BuildExpr(model, ret.Expression)) == null)
                    return false;
                if (value != null)
                    value = WrapStructCopy(model, ret.Expression!, value);
                acc.Add(new IlReturn(value) { Origin = stmt });
                return true;
            }
            case LocalDeclarationStatementSyntax decon
                when decon.Declaration.Variables.Count == 1
                && decon.Declaration.Variables[0].Initializer?.Value
                    is AssignmentExpressionSyntax
                    {
                        Left: DeclarationExpressionSyntax declExpr,
                        Right: var rhs
                    }:
            {
                // legacy VisitDeconstruction: 非 paren designation は無出力
                if (declExpr.Designation is not
                    ParenthesizedVariableDesignationSyntax pvd)
                    return true;
                var targets = pvd.Variables
                    .Select(IlExpr (v) => new IlVar(
                        v is SingleVariableDesignationSyntax sv
                            ? sv.Identifier.ValueText : "_"))
                    .ToList();
                return BuildDeconstructionInto(model, rhs, targets,
                    declare: true, stmt, acc);
            }
            case LocalDeclarationStatementSyntax local:
            {
                if (local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                    return false;
                foreach (var v in local.Declaration.Variables)
                {
                    IlExpr? init = null;
                    if (v.Initializer != null
                        && (init = BuildExpr(model, v.Initializer.Value)) == null)
                        return false;
                    if (init != null)
                        init = WrapStructCopy(model, v.Initializer!.Value, init);
                    acc.Add(new IlLocal(v.Identifier.ValueText, init)
                        { Origin = stmt });
                }
                return true;
            }
            case ExpressionStatementSyntax exprStmt:
                return BuildExprStatInto(model, exprStmt.Expression, stmt, acc);
            case IfStatementSyntax ifStmt:
            {
                var built = BuildIf(model, ifStmt);
                if (built == null) return false;
                acc.Add(built with { Origin = stmt });
                return true;
            }
            case WhileStatementSyntax whileStmt:
            {
                var cond = BuildExpr(model, whileStmt.Condition);
                var body = BuildBlock(model, whileStmt.Statement);
                if (cond == null || body == null) return false;
                acc.Add(new IlWhile(cond, body, null) { Origin = stmt });
                return true;
            }
            case ForStatementSyntax forStmt:
                return BuildForInto(model, forStmt, acc);
            case ForEachStatementSyntax foreachStmt:
            {
                var coll = BuildExpr(model, foreachStmt.Expression);
                var body = BuildBlock(model, foreachStmt.Statement);
                if (coll == null || body == null) return false;
                var typeName = model.GetTypeInfo(foreachStmt.Expression).Type
                    ?.OriginalDefinition.ToDisplayString() ?? "";
                var varName = foreachStmt.Identifier.ValueText;
                acc.Add(typeName.StartsWith(
                        "System.Collections.Generic.Dictionary")
                    ? new IlForeachDict(varName, coll, body) { Origin = stmt }
                    : new IlForeachList(varName, coll, body) { Origin = stmt });
                return true;
            }
            case BlockSyntax block:
                // legacy と同じく do..end は挟まず flatten する
                return BuildStatsInto(model, block.Statements, acc);
            case BreakStatementSyntax:
                acc.Add(new IlBreak { Origin = stmt });
                return true;
            case ContinueStatementSyntax:
                acc.Add(new IlContinue { Origin = stmt });
                return true;
            case DoStatementSyntax doStmt:
            {
                var body = BuildBlock(model, doStmt.Statement);
                var cond = BuildExpr(model, doStmt.Condition);
                if (body == null || cond == null) return false;
                acc.Add(new IlRepeat(body, cond) { Origin = stmt });
                return true;
            }
            case SwitchStatementSyntax switchStmt:
                return BuildSwitchStatInto(model, switchStmt, acc);
            default:
                return false;
        }
    }

    private IlBlock? BuildBlock(SemanticModel model, StatementSyntax stmt)
    {
        var acc = new List<IlStat>();
        var ok = stmt is BlockSyntax block
            ? BuildStatsInto(model, block.Statements, acc)
            : BuildStatInto(model, stmt, acc);
        return ok ? new IlBlock([.. acc]) : null;
    }

    private IlIf? BuildIf(SemanticModel model, IfStatementSyntax ifStmt)
    {
        var arms = new List<(IlExpr, IlBlock)>();
        IlBlock? elseBlock = null;
        var current = ifStmt;
        while (true)
        {
            var cond = BuildExpr(model, current.Condition);
            var body = BuildBlock(model, current.Statement);
            if (cond == null || body == null) return null;
            arms.Add((cond, body));
            if (current.Else == null) break;
            if (current.Else.Statement is IfStatementSyntax elseIf)
            {
                // elseif 条件の前宣言 (is-pattern) が要る chain は fallback
                if (CollectPreDeclNames(elseIf).Count > 0) return null;
                current = elseIf;
                continue;
            }
            elseBlock = BuildBlock(model, current.Else.Statement);
            if (elseBlock == null) return null;
            break;
        }
        return new IlIf([.. arms], elseBlock);
    }

    // 文位置の式。legacy VisitExpressionAsStatement を写像する。
    private bool BuildExprStatInto(SemanticModel model, ExpressionSyntax expr,
        SyntaxNode? origin, List<IlStat> acc)
    {
        if (expr is PostfixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.PostIncrementExpression
                    or (int)SyntaxKind.PostDecrementExpression
            } postfix)
            return BuildIncrementInto(model, postfix.Operand,
                postfix.IsKind(SyntaxKind.PostIncrementExpression), origin, acc);
        if (expr is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.PreIncrementExpression
                    or (int)SyntaxKind.PreDecrementExpression
            } prefix)
            return BuildIncrementInto(model, prefix.Operand,
                prefix.IsKind(SyntaxKind.PreIncrementExpression), origin, acc);

        switch (expr)
        {
            case AssignmentExpressionSyntax
            {
                Left: DeclarationExpressionSyntax declExpr,
                Right: var rhs,
            }:
            {
                if (declExpr.Designation is not
                    ParenthesizedVariableDesignationSyntax pvd)
                    return true; // legacy 同様に無出力
                var targets = pvd.Variables
                    .Select(IlExpr (v) => new IlVar(
                        v is SingleVariableDesignationSyntax sv
                            ? sv.Identifier.ValueText : "_"))
                    .ToList();
                return BuildDeconstructionInto(model, rhs, targets,
                    declare: true, origin, acc);
            }
            case AssignmentExpressionSyntax
            {
                Left: TupleExpressionSyntax tuple,
                Right: var tupleRhs,
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
            }:
            {
                var targets = new List<IlExpr>();
                foreach (var arg in tuple.Arguments)
                {
                    // 混在形 (var a, b) は legacy が警告 → fallback
                    if (arg.Expression is DeclarationExpressionSyntax)
                        return false;
                    var built = BuildExpr(model, arg.Expression);
                    if (built == null) return false;
                    targets.Add(built);
                }
                return BuildDeconstructionInto(model, tupleRhs, targets,
                    declare: false, origin, acc);
            }
            case AssignmentExpressionSyntax assign
                when IsCustomPropertyTarget(model, assign.Left):
                return BuildPropAssignInto(model, assign, origin, acc);
            case AssignmentExpressionSyntax coalesce
                when coalesce.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                return BuildCoalesceAssignInto(model, coalesce, origin, acc);
            case AssignmentExpressionSyntax lowered
                when !lowered.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && NeedsLoweredLvalue(lowered.Left):
                return BuildLoweredCompoundInto(model, lowered, origin, acc);
            case AssignmentExpressionSyntax assign:
                return BuildAssignInto(model, assign, origin, acc);
            case InvocationExpressionSyntax invocation:
            {
                if (BuildDictAddStatInto(model, invocation, origin, acc))
                    return true;
                if (BuildRefMultiReturnStatInto(model, invocation, origin, acc))
                    return true;
                var call = BuildExpr(model, expr);
                if (call == null) return false;
                acc.Add(new IlCallStat(call) { Origin = origin });
                return true;
            }
            case ConditionalAccessExpressionSyntax:
            {
                var call = BuildExpr(model, expr);
                if (call == null) return false;
                acc.Add(new IlCallStat(call) { Origin = origin });
                return true;
            }
            default:
                return false;
        }
    }

    // Dictionary.Add は Lua では代入形 (legacy TryMapCollectionMethod)
    private bool BuildDictAddStatInto(SemanticModel model,
        InvocationExpressionSyntax invocation, SyntaxNode? origin,
        List<IlStat> acc)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax ma
            || ma.Name.Identifier.ValueText != "Add")
            return false;
        var typeDef = model.GetTypeInfo(ma.Expression).Type
            ?.OriginalDefinition.ToDisplayString() ?? "";
        if (!IsDictType(typeDef)) return false;
        if (invocation.ArgumentList.Arguments.Count != 2) return false;
        if (invocation.ArgumentList.Arguments
            .Any(a => !a.RefKindKeyword.IsKind(SyntaxKind.None)))
            return false;
        var recv = BuildExpr(model, ma.Expression);
        var key = BuildExpr(model,
            invocation.ArgumentList.Arguments[0].Expression);
        var value = BuildExpr(model,
            invocation.ArgumentList.Arguments[1].Expression);
        if (recv == null || key == null || value == null) return false;
        acc.Add(new IlAssign(new IlIndex(recv, key, false), value)
            { Origin = origin });
        return true;
    }

    private bool BuildIncrementInto(SemanticModel model,
        ExpressionSyntax operand, bool increment, SyntaxNode? origin,
        List<IlStat> acc)
    {
        if (IsCustomPropertyTarget(model, operand) || NeedsLoweredLvalue(operand))
            return BuildLoweredIncrementInto(model, operand, increment,
                origin, acc);
        var target = BuildExpr(model, operand);
        if (target == null) return false;
        acc.Add(new IlAssign(target,
                new IlBin(increment ? IlBinOp.AddNum : IlBinOp.Sub, target,
                    new IlLit("1")))
            { Origin = origin });
        return true;
    }

    private bool BuildAssignInto(SemanticModel model,
        AssignmentExpressionSyntax assign, SyntaxNode? origin,
        List<IlStat> acc)
    {
        if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            var target = BuildExpr(model, assign.Left);
            var value = BuildExpr(model, assign.Right);
            if (target == null || value == null) return false;
            value = WrapStructCopy(model, assign.Right, value);
            acc.Add(new IlAssign(target, value) { Origin = origin });
            return true;
        }

        // compound。副作用のある lvalue は legacy が temp へ下げる — fallback
        if (NeedsLoweredLvalue(assign.Left)) return false;
        var op = CompoundOperator(model, assign);
        if (op == null) return false;
        var read = BuildExpr(model, assign.Left);
        var right = BuildExpr(model, assign.Right);
        if (read == null || right == null) return false;
        var applied = BuildCompoundValue(model, assign, op, read, right);
        if (applied == null) return false;
        acc.Add(new IlAssign(read, applied) { Origin = origin });
        return true;
    }

    // legacy ApplyCompound の写像 (right は非 lowered 経路なので生のまま)
    private IlExpr? BuildCompoundValue(SemanticModel model,
        AssignmentExpressionSyntax assign, string op, IlExpr read, IlExpr right)
    {
        var type = model.GetTypeInfo(assign.Left).Type;
        return op switch
        {
            "/" when IsIntegralType(type) =>
                new IlCall("__tcs_idiv", [read, right]),
            "%" when IsIntegralType(type) =>
                new IlCall("__tcs_irem", [read, right]),
            "%" when IsFloatingType(type) =>
                new IlCall("math.fmod", [read, right]),
            ".." => new IlBin(IlBinOp.Concat,
                new IlParen(new IlBin(IlBinOp.Or, read, new IlLit("\"\""))),
                new IlParen(new IlBin(IlBinOp.Or, right, new IlLit("\"\"")))),
            _ => LuaOpToIl(op) is { } ilOp
                ? new IlBin(ilOp, read, right) : null,
        };
    }

    private static IlBinOp? LuaOpToIl(string op) => op switch
    {
        "+" => IlBinOp.AddNum,
        "-" => IlBinOp.Sub,
        "*" => IlBinOp.Mul,
        "/" => IlBinOp.DivNum,
        "%" => IlBinOp.RemNum,
        "&" => IlBinOp.BitAnd,
        "|" => IlBinOp.BitOr,
        "~" => IlBinOp.BitXor,
        "<<" => IlBinOp.Shl,
        ">>" => IlBinOp.Shr,
        _ => null,
    };

    private bool BuildForInto(SemanticModel model, ForStatementSyntax forStmt,
        List<IlStat> acc)
    {
        // TryEmitSimpleFor と同じ条件で numeric for へ (ガードも同じ helper)
        if (TryBuildSimpleFor(model, forStmt) is { } simple)
        {
            acc.Add(simple with { Origin = forStmt });
            return true;
        }

        if (forStmt.Initializers.Count > 0) return false;
        if (forStmt.Declaration != null)
            foreach (var v in forStmt.Declaration.Variables)
            {
                IlExpr? init = null;
                if (v.Initializer != null
                    && (init = BuildExpr(model, v.Initializer.Value)) == null)
                    return false;
                acc.Add(new IlLocal(v.Identifier.ValueText, init)
                    { Origin = forStmt });
            }

        IlExpr cond = new IlLit("true");
        if (forStmt.Condition != null)
        {
            var built = BuildExpr(model, forStmt.Condition);
            if (built == null) return false;
            cond = built;
        }
        var body = BuildBlock(model, forStmt.Statement);
        if (body == null) return false;
        var trailer = new List<IlStat>();
        foreach (var inc in forStmt.Incrementors)
            if (!BuildExprStatInto(model, inc, forStmt, trailer)) return false;
        var scopeBody = forStmt.Incrementors.Count > 0
            && ContainsDirectContinue(forStmt.Statement);
        acc.Add(new IlWhile(cond, body, new IlBlock([.. trailer]), scopeBody)
            { Origin = forStmt });
        return true;
    }

    private IlNumericFor? TryBuildSimpleFor(SemanticModel model,
        ForStatementSyntax forStmt)
    {
        if (forStmt.Declaration?.Variables.Count != 1) return null;
        var decl = forStmt.Declaration.Variables[0];
        if (decl.Initializer == null) return null;
        var varName = decl.Identifier.ValueText;

        if (forStmt.Condition is not BinaryExpressionSyntax cond) return null;
        if (cond.Left is not IdentifierNameSyntax condId
            || condId.Identifier.ValueText != varName) return null;

        if (forStmt.Incrementors.Count != 1) return null;
        var inc = forStmt.Incrementors[0];
        var isIncByOne = inc is PostfixUnaryExpressionSyntax
                { RawKind: (int)SyntaxKind.PostIncrementExpression }
            || (inc is AssignmentExpressionSyntax
                { RawKind: (int)SyntaxKind.AddAssignmentExpression } addAssign
                && addAssign.Right is LiteralExpressionSyntax { Token.Text: "1" });
        if (!isIncByOne) return null;

        if (!IsLoopInvariantBound(model, forStmt, cond.Right)) return null;
        if (IsAssignedWithin(forStmt.Statement, varName)) return null;
        if (IsCapturedByLambdaWithin(forStmt.Statement, varName)) return null;

        var start = BuildExpr(model, decl.Initializer.Value);
        var end = BuildExpr(model, cond.Right);
        var body = BuildBlock(model, forStmt.Statement);
        if (start == null || end == null || body == null) return null;
        IlExpr? limit = cond.Kind() switch
        {
            SyntaxKind.LessThanExpression =>
                new IlBin(IlBinOp.Sub, end, new IlLit("1")),
            SyntaxKind.LessThanOrEqualExpression => end,
            _ => null,
        };
        return limit == null ? null : new IlNumericFor(varName, start, limit, body);
    }

    // 値型の copy 地点 (il-spec §10): 代入 / 引数 / return / 値文脈読み。
    // 生成直後 (IlNewObj / initializer IIFE / 既に copy 済み) は fresh で不要
    private IlExpr WrapStructCopy(SemanticModel model, ExpressionSyntax src,
        IlExpr built) =>
        IsUserStruct(model.GetTypeInfo(src).Type)
        && built is not (IlNewObj or IlIife or IlStructCopy)
            ? new IlStructCopy(built) : built;

    // ---- 検出 helper (legacy の判定部だけを写し、render は行わない) ----

    private bool IsCustomPropertyTarget(SemanticModel model,
        ExpressionSyntax target) => target switch
    {
        IdentifierNameSyntax id =>
            model.GetSymbolInfo(id).Symbol is IPropertySymbol p
            && IsCustomProperty(p),
        MemberAccessExpressionSyntax ma =>
            model.GetSymbolInfo(ma).Symbol is IPropertySymbol p2
            && IsCustomProperty(p2),
        _ => false,
    };

    // legacy TryLowerLvalue が temp を挟む条件 (受け手/添字に副作用)
    private static bool NeedsLoweredLvalue(ExpressionSyntax left) => left switch
    {
        MemberAccessExpressionSyntax ma => HasSideEffectSyntax(ma.Expression),
        ElementAccessExpressionSyntax ea => HasSideEffectSyntax(ea),
        _ => false,
    };

    private static List<string> CollectPreDeclNames(StatementSyntax stmt)
    {
        var patternScopes = new List<SyntaxNode> { stmt };
        var chain = stmt as IfStatementSyntax;
        while (chain?.Else?.Statement is IfStatementSyntax elseIf)
        {
            patternScopes.Add(elseIf.Condition);
            chain = elseIf;
        }

        return stmt.DescendantNodes()
            .OfType<ArgumentSyntax>()
            .Where(arg => arg.Ancestors().OfType<StatementSyntax>()
                .FirstOrDefault() == stmt)
            .Select(TryGetOutArgumentName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Concat(patternScopes.SelectMany(IsPatternDesignationNames))
            .Distinct()
            .ToList();
    }
}
