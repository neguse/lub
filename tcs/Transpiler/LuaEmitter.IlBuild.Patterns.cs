using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder: pattern / switch 式 / switch 文 / is 式 / conditional access /
// lambda。legacy (Patterns.cs / Statements.cs VisitSwitch) の写像。
public partial class LuaEmitter
{
    // legacy EmitTypeCheck の写像
    private IlExpr? BuildTypeCheck(IlExpr expr, ITypeSymbol? patternType,
        string? typeRef) =>
        LuaTypeNameFor(patternType) is { } luaType
            ? new IlIsLuaType(expr, luaType)
            : typeRef == null ? null : new IlIsType(expr, typeRef);

    private string? BuildTypeRefText(SemanticModel model, ExpressionSyntax expr)
    {
        var built = BuildExpr(model, expr);
        return built is IlVar or IlField ? RenderIl(built) : null;
    }

    // legacy VisitPattern の写像 (switch 式/文の arm 用)
    private IlExpr? BuildPattern(SemanticModel model, PatternSyntax pattern,
        IlExpr governing) => pattern switch
    {
        ConstantPatternSyntax cp
            when model.GetSymbolInfo(cp.Expression).Symbol is ITypeSymbol patType =>
            BuildTypeCheck(governing, patType,
                BuildTypeRefText(model, cp.Expression)),
        ConstantPatternSyntax cp => BuildExpr(model, cp.Expression) is { } value
            ? new IlBin(IlBinOp.Eq, governing, value) : null,
        DiscardPatternSyntax => new IlLit("true"),
        DeclarationPatternSyntax dp =>
            BuildTypeCheck(governing, model.GetTypeInfo(dp.Type).Type,
                FormatTypeReference(dp.Type)),
        RecursivePatternSyntax rp => BuildRecursivePattern(model, governing, rp),
        RelationalPatternSyntax rel => BuildExpr(model, rel.Expression) is { } v
            ? new IlBin(RelationalIlOp(rel), governing, v) : null,
        BinaryPatternSyntax bp =>
            BuildPattern(model, bp.Left, governing) is { } l
            && BuildPattern(model, bp.Right, governing) is { } r
                ? new IlParen(new IlBin(
                    bp.IsKind(SyntaxKind.AndPattern) ? IlBinOp.And : IlBinOp.Or,
                    l, r))
                : null,
        _ => null,
    };

    // legacy VisitIsSubPattern の写像 (is 式の sub-pattern 用)
    private IlExpr? BuildIsSubPattern(SemanticModel model, IlExpr expr,
        PatternSyntax pattern) => pattern switch
    {
        ConstantPatternSyntax cp
            when model.GetSymbolInfo(cp.Expression).Symbol is ITypeSymbol patType =>
            BuildTypeCheck(expr, patType, BuildTypeRefText(model, cp.Expression)),
        ConstantPatternSyntax cp => BuildExpr(model, cp.Expression) is { } value
            ? new IlBin(IlBinOp.Eq, expr, value) : null,
        TypePatternSyntax tp =>
            BuildTypeCheck(expr, model.GetTypeInfo(tp.Type).Type,
                FormatTypeReference(tp.Type)),
        DeclarationPatternSyntax dp =>
            BuildTypeCheck(expr, model.GetTypeInfo(dp.Type).Type,
                FormatTypeReference(dp.Type)),
        UnaryPatternSyntax { RawKind: (int)SyntaxKind.NotPattern } notPat =>
            BuildIsSubPattern(model, expr, notPat.Pattern) is { } sub
                ? new IlUn(IlUnOp.Not, new IlParen(sub)) : null,
        RelationalPatternSyntax rp => BuildExpr(model, rp.Expression) is { } v
            ? new IlBin(RelationalIlOp(rp), expr, v) : null,
        BinaryPatternSyntax bp =>
            BuildIsSubPattern(model, expr, bp.Left) is { } l
            && BuildIsSubPattern(model, expr, bp.Right) is { } r
                ? new IlParen(new IlBin(
                    bp.IsKind(SyntaxKind.AndPattern) ? IlBinOp.And : IlBinOp.Or,
                    l, r))
                : null,
        RecursivePatternSyntax rp => BuildRecursivePattern(model, expr, rp),
        _ => null,
    };

    private static IlBinOp RelationalIlOp(RelationalPatternSyntax rp) =>
        rp.OperatorToken.Kind() switch
        {
            SyntaxKind.GreaterThanToken => IlBinOp.Gt,
            SyntaxKind.GreaterThanEqualsToken => IlBinOp.Ge,
            SyntaxKind.LessThanToken => IlBinOp.Lt,
            SyntaxKind.LessThanEqualsToken => IlBinOp.Le,
            _ => IlBinOp.Eq,
        };

    private IlExpr? BuildRecursivePattern(SemanticModel model, IlExpr expr,
        RecursivePatternSyntax rp)
    {
        var conditions = new List<IlExpr>();
        if (rp.Type != null)
        {
            var check = BuildTypeCheck(expr, model.GetTypeInfo(rp.Type).Type,
                FormatTypeReference(rp.Type));
            if (check == null) return null;
            conditions.Add(check);
        }
        if (rp.PositionalPatternClause != null) return null;
        if (rp.Designation != null) return null;
        if (rp.PropertyPatternClause != null)
        {
            foreach (var sub in rp.PropertyPatternClause.Subpatterns)
            {
                if (sub.NameColon == null) continue;
                var propName = sub.NameColon.Name.Identifier.ValueText;
                IlExpr propExpr = model.GetSymbolInfo(sub.NameColon.Name).Symbol
                        is IPropertySymbol patProp && IsCustomProperty(patProp)
                    ? new IlInvoke(expr, $"get_{propName}", [])
                    : new IlField(expr, propName);
                var cond = BuildIsSubPattern(model, propExpr, sub.Pattern);
                if (cond == null) return null;
                conditions.Add(cond);
            }
        }
        if (conditions.Count == 0) return new IlLit("true");
        var result = conditions[0];
        for (var i = 1; i < conditions.Count; i++)
            result = new IlBin(IlBinOp.And, result, conditions[i]);
        return result;
    }

    // legacy VisitSwitchExpression の写像 (IIFE + 前束縛 + if 連鎖)
    private IlExpr? BuildSwitchExpression(SemanticModel model,
        SwitchExpressionSyntax switchExpr)
    {
        var governing = BuildExpr(model, switchExpr.GoverningExpression);
        if (governing == null) return null;
        var sw = new IlVar("__tcs_sw");

        var stats = new List<IlStat> { new IlLocal("__tcs_sw", governing) };
        foreach (var dp in switchExpr.Arms
            .Select(a => a.Pattern)
            .OfType<DeclarationPatternSyntax>()
            .Where(dp => dp.Designation is SingleVariableDesignationSyntax))
        {
            var name = ((SingleVariableDesignationSyntax)dp.Designation!)
                .Identifier.ValueText;
            stats.Add(new IlLocal(name, sw));
        }

        var arms = new List<(IlExpr, IlBlock)>();
        IlBlock? elseBlock = null;
        foreach (var arm in switchExpr.Arms)
        {
            var value = BuildExpr(model, arm.Expression);
            if (value == null) return null;
            if (arm.Pattern is DiscardPatternSyntax)
            {
                elseBlock = new IlBlock([new IlReturn(value)]);
                continue;
            }
            var pattern = BuildPattern(model, arm.Pattern, sw);
            if (pattern == null) return null;
            if (arm.WhenClause != null)
            {
                var when = BuildExpr(model, arm.WhenClause.Condition);
                if (when == null) return null;
                pattern = new IlBin(IlBinOp.And, pattern, when);
            }
            arms.Add((pattern, new IlBlock([new IlReturn(value)])));
        }
        if (arms.Count == 0 && elseBlock == null) return null;
        stats.Add(arms.Count > 0
            ? new IlIf([.. arms], elseBlock)
            : new IlIf([(new IlLit("true"), elseBlock!)], null));
        return new IlIife([.. stats]);
    }

    // legacy VisitIsPattern の写像
    private IlExpr? BuildIsPattern(SemanticModel model,
        IsPatternExpressionSyntax isPattern)
    {
        var expr = BuildExpr(model, isPattern.Expression);
        if (expr == null) return null;
        if (isPattern.Pattern is DeclarationPatternSyntax
            { Designation: SingleVariableDesignationSyntax sv } dp)
        {
            var name = sv.Identifier.ValueText;
            var check = BuildTypeCheck(new IlVar(name),
                model.GetTypeInfo(dp.Type).Type, FormatTypeReference(dp.Type));
            return check == null ? null : new IlIife([
                new IlAssign(new IlVar(name), expr),
                new IlReturn(check)]);
        }
        var sub = BuildIsSubPattern(model, expr, isPattern.Pattern);
        return sub == null ? null : new IlParen(sub);
    }

    // legacy VisitSwitch (switch 文) の写像
    private bool BuildSwitchStatInto(SemanticModel model,
        SwitchStatementSyntax switchStmt, List<IlStat> acc)
    {
        var governing = BuildExpr(model, switchStmt.Expression);
        if (governing == null) return false;
        var sw = new IlVar("__tcs_sw");
        var products = new List<IlStat>
        {
            new IlLocal("__tcs_sw", governing) { Origin = switchStmt },
        };

        var defaultSection = switchStmt.Sections.FirstOrDefault(s =>
            s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
        var sections = switchStmt.Sections
            .Where(s => !s.Labels.Any(l => l is DefaultSwitchLabelSyntax))
            .ToList();

        foreach (var label in sections
            .SelectMany(s => s.Labels.OfType<CasePatternSwitchLabelSyntax>())
            .Where(l => l.Pattern is DeclarationPatternSyntax
                { Designation: SingleVariableDesignationSyntax }))
        {
            var dp = (DeclarationPatternSyntax)label.Pattern;
            var name = ((SingleVariableDesignationSyntax)dp.Designation!)
                .Identifier.ValueText;
            products.Add(new IlLocal(name, sw) { Origin = switchStmt });
        }

        var arms = new List<(IlExpr, IlBlock)>();
        foreach (var section in sections)
        {
            IlExpr? cond = null;
            foreach (var label in section.Labels)
            {
                IlExpr? one = label switch
                {
                    CaseSwitchLabelSyntax c when model.GetSymbolInfo(c.Value)
                        .Symbol is ITypeSymbol patType =>
                        BuildTypeCheck(sw, patType,
                            BuildTypeRefText(model, c.Value)),
                    CaseSwitchLabelSyntax c =>
                        BuildExpr(model, c.Value) is { } v
                            ? new IlBin(IlBinOp.Eq, sw, v) : null,
                    CasePatternSwitchLabelSyntax p =>
                        BuildPatternLabel(model, p, sw),
                    _ => null,
                };
                if (one == null) return false;
                cond = cond == null ? one : new IlBin(IlBinOp.Or, cond, one);
            }
            if (cond == null) return false;
            var body = BuildSwitchSectionBody(model, section.Statements);
            if (body == null) return false;
            arms.Add((cond, body));
        }

        IlBlock? defaultBody = null;
        if (defaultSection != null)
        {
            defaultBody = BuildSwitchSectionBody(model,
                defaultSection.Statements);
            if (defaultBody == null) return false;
        }

        if (arms.Count > 0)
            products.Add(new IlIf([.. arms], defaultBody) { Origin = switchStmt });
        else if (defaultBody != null)
            products.Add(new IlDo(defaultBody) { Origin = switchStmt });
        acc.AddRange(products);
        return true;
    }

    private IlExpr? BuildPatternLabel(SemanticModel model,
        CasePatternSwitchLabelSyntax label, IlExpr sw)
    {
        var condition = BuildPattern(model, label.Pattern, sw);
        if (condition == null) return null;
        if (label.WhenClause == null) return new IlParen(condition);
        var when = BuildExpr(model, label.WhenClause.Condition);
        return when == null
            ? null
            : new IlParen(new IlBin(IlBinOp.And, condition, when));
    }

    private IlBlock? BuildSwitchSectionBody(SemanticModel model,
        IEnumerable<StatementSyntax> statements)
    {
        var acc = new List<IlStat>();
        foreach (var stmt in statements)
        {
            if (stmt is BreakStatementSyntax) continue; // switch の暗黙 break
            if (!BuildStatInto(model, stmt, acc)) return null;
        }
        return new IlBlock([.. acc]);
    }

    // legacy VisitSimpleLambda / VisitParenthesizedLambda の写像
    private IlExpr? BuildLambda(SemanticModel model, ExpressionSyntax lambda)
    {
        var (paramList, exprBody, block) = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => (
                ImmutableArray.Create(simple.Parameter.Identifier.ValueText),
                simple.ExpressionBody, simple.Block),
            ParenthesizedLambdaExpressionSyntax paren => (
                [.. paren.ParameterList.Parameters
                    .Select(p => p.Identifier.ValueText)],
                paren.ExpressionBody, paren.Block),
            _ => default,
        };
        if (paramList.IsDefault) return null;

        if (exprBody != null)
        {
            var body = BuildExpr(model, exprBody);
            if (body == null) return null;
            var locals = IsPatternDesignationNames(exprBody).ToImmutableArray();
            return new IlClosure(paramList, null, body, locals);
        }
        if (block == null) return null;
        var acc = new List<IlStat>();
        if (!BuildStatsInto(model, block.Statements, acc)) return null;
        return new IlClosure(paramList, new IlBlock([.. acc]), null, []);
    }

    // legacy VisitConditionalAccess 系の写像
    private IlExpr? BuildConditionalAccess(SemanticModel model,
        ConditionalAccessExpressionSyntax condAccess)
    {
        var receiver = BuildExpr(model, condAccess.Expression);
        if (receiver == null) return null;
        var receiverType = model.GetTypeInfo(condAccess.Expression).Type;
        var whenNotNull = BuildConditionalWhenNotNull(model,
            condAccess.WhenNotNull, new IlVar("__tcs_ca"), receiverType);
        if (whenNotNull == null) return null;
        return new IlIife([
            new IlLocal("__tcs_ca", receiver),
            new IlIf([(new IlBin(IlBinOp.Ne, new IlVar("__tcs_ca"),
                    new IlLit("nil")),
                new IlBlock([new IlReturn(whenNotNull)]))], null)]);
    }

    private IlExpr? BuildConditionalWhenNotNull(SemanticModel model,
        ExpressionSyntax expr, IlExpr obj, ITypeSymbol? receiverType)
    {
        switch (expr)
        {
            case MemberBindingExpressionSyntax mb:
                return BuildConditionalMemberBinding(mb, obj, receiverType);
            case InvocationExpressionSyntax inv
                when inv.Expression is MemberBindingExpressionSyntax mb2:
                return BuildConditionalInvocation(model, mb2, inv.ArgumentList,
                    obj, receiverType);
            case ElementBindingExpressionSyntax eb:
            {
                var index = BuildExpr(model,
                    eb.ArgumentList.Arguments[0].Expression);
                if (index == null) return null;
                var typeDef = receiverType?.OriginalDefinition
                    .ToDisplayString() ?? "";
                return new IlIndex(obj, index,
                    IsListType(typeDef) || receiverType is IArrayTypeSymbol);
            }
            case ConditionalAccessExpressionSyntax nested:
            {
                var inner = BuildConditionalWhenNotNull(model,
                    nested.Expression, obj, receiverType);
                if (inner == null) return null;
                var nestedType = model.GetTypeInfo(nested.Expression).Type;
                var whenNotNull = BuildConditionalWhenNotNull(model,
                    nested.WhenNotNull, new IlVar("__tcs_ca"), nestedType);
                if (whenNotNull == null) return null;
                return new IlIife([
                    new IlLocal("__tcs_ca", inner),
                    new IlIf([(new IlBin(IlBinOp.Ne, new IlVar("__tcs_ca"),
                            new IlLit("nil")),
                        new IlBlock([new IlReturn(whenNotNull)]))], null)]);
            }
            default:
                return BuildExpr(model, expr);
        }
    }

    private IlExpr? BuildConditionalMemberBinding(
        MemberBindingExpressionSyntax mb, IlExpr obj, ITypeSymbol? receiverType)
    {
        var member = mb.Name.Identifier.ValueText;
        var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";

        if (member == "Count" && (IsListType(typeDef) || IsDictType(typeDef)))
            return IsDictType(typeDef)
                ? new IlCall("Dict.Count", [obj]) : new IlLen(obj);
        if (member == "Keys" && IsDictType(typeDef))
            return new IlCall("Dict.Keys", [obj]);
        if (member == "Values" && IsDictType(typeDef))
            return new IlCall("Dict.Values", [obj]);
        if (member == "Length"
            && (receiverType?.SpecialType == SpecialType.System_String
                || receiverType is IArrayTypeSymbol))
            return new IlLen(obj);
        if (FindInstanceProperty(receiverType, member) is { } condProp
            && IsCustomProperty(condProp))
            return new IlInvoke(obj, $"get_{member}", []);
        return new IlField(obj, member);
    }

    private IlExpr? BuildConditionalInvocation(SemanticModel model,
        MemberBindingExpressionSyntax mb, ArgumentListSyntax argList,
        IlExpr obj, ITypeSymbol? receiverType)
    {
        var methodName = mb.Name.Identifier.ValueText;
        var args = new List<IlExpr>();
        foreach (var a in argList.Arguments)
        {
            if (!a.RefKindKeyword.IsKind(SyntaxKind.None)) return null;
            var built = BuildExpr(model, a.Expression);
            if (built == null) return null;
            args.Add(built);
        }
        var argArr = args.ToImmutableArray();
        var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";

        if (receiverType?.SpecialType == SpecialType.System_String)
            return TryBuildStringCall(obj, methodName, argArr)
                ?? new IlInvoke(obj, methodName, argArr);

        if (IsListType(typeDef))
        {
            switch (methodName)
            {
                case "Add":
                    return new IlCall("table.insert", [obj, .. argArr]);
                case "Remove":
                    return new IlCall("List.Remove", [obj, .. argArr]);
                case "RemoveAt":
                    return new IlCall("table.remove",
                        [obj, new IlBin(IlBinOp.AddNum, argArr[0],
                            new IlLit("1"))]);
                case "Clear":
                case "FirstOrDefault":
                case "LastOrDefault":
                    return null; // IIFE / default 埋め込み経路 — fallback
            }
            if (ListRuntimeMethods.Contains(methodName))
                return new IlCall($"List.{methodName}", [obj, .. argArr]);
        }

        if (IsDictType(typeDef))
        {
            return methodName switch
            {
                "Remove" => new IlCall("Dict.Remove", [obj, argArr[0]]),
                "ContainsKey" => new IlCall("Dict.ContainsKey",
                    [obj, argArr[0]]),
                "Add" or "TryGetValue" => null,
                _ => new IlInvoke(obj, methodName, argArr),
            };
        }

        return new IlInvoke(obj, methodName, argArr);
    }
}
