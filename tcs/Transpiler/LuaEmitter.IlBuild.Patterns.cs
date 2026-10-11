using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TinyCs;

// IL builder: pattern / switch 式 / switch 文 / is 式 / conditional access /
// lambda。legacy (Patterns.cs / Statements.cs VisitSwitch) の写像。
public partial class LuaEmitter
{
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
            BuildTypeCheck(governing, PatternInputType(model, cp), patType,
                BuildTypeRefText(model, cp.Expression)),
        ConstantPatternSyntax cp when IsNullLiteral(cp.Expression)
            && IsNullableValueType(model.GetTypeInfo(cp.Expression).ConvertedType) =>
            new IlUn(IlUnOp.Not, new IlNullableHasValue(governing)),
        ConstantPatternSyntax cp when IsNullLiteral(cp.Expression)
            && IsNullableValueType(model.GetTypeInfo(cp.Expression).ConvertedType) =>
            new IlUn(IlUnOp.Not, new IlNullableHasValue(governing)),
        ConstantPatternSyntax cp => BuildExpr(model, cp.Expression) is { } value
            ? new IlBin(IlBinOp.Eq, governing, value) : null,
        DiscardPatternSyntax => new IlLit("true"),
        TypePatternSyntax tp =>
            BuildTypeCheck(governing, PatternInputType(model, tp),
                model.GetTypeInfo(tp.Type).Type, FormatTypeReference(model, tp.Type)),
        DeclarationPatternSyntax { Designation: SingleVariableDesignationSyntax sv } dp
            when StructDesignation(model, dp) is (var inputType, var declType) =>
            BuildStructDeclarationIs(new IlVar(sv.Identifier.ValueText), governing,
                inputType, declType),
        DeclarationPatternSyntax dp =>
            BuildTypeCheck(governing, PatternInputType(model, dp),
                model.GetTypeInfo(dp.Type).Type, FormatTypeReference(model, dp.Type)),
        RecursivePatternSyntax rp => BuildRecursivePattern(model, governing, rp),
        RelationalPatternSyntax rel => BuildExpr(model, rel.Expression) is { } v
            ? new IlBin(RelationalIlOp(rel), governing, v) : null,
        BinaryPatternSyntax bp => BindPatternInput(governing, input =>
            BuildPattern(model, bp.Left, input) is { } l
            && BuildPattern(model, bp.Right, AndRightInput(model, bp, input)) is { } r
                ? BinaryPatternIl(bp, l, r) : null),
        _ => null,
    };

    // legacy VisitIsSubPattern の写像 (is 式の sub-pattern 用)
    private IlExpr? BuildIsSubPattern(SemanticModel model, IlExpr expr,
        PatternSyntax pattern) => pattern switch
    {
        ConstantPatternSyntax cp
            when model.GetSymbolInfo(cp.Expression).Symbol is ITypeSymbol patType =>
            BuildTypeCheck(expr, PatternInputType(model, cp), patType,
                BuildTypeRefText(model, cp.Expression)),
        ConstantPatternSyntax cp => BuildExpr(model, cp.Expression) is { } value
            ? new IlBin(IlBinOp.Eq, expr, value) : null,
        TypePatternSyntax tp =>
            BuildTypeCheck(expr, PatternInputType(model, tp),
                model.GetTypeInfo(tp.Type).Type, FormatTypeReference(model, tp.Type)),
        DeclarationPatternSyntax dp =>
            BuildTypeCheck(expr, PatternInputType(model, dp),
                model.GetTypeInfo(dp.Type).Type, FormatTypeReference(model, dp.Type)),
        UnaryPatternSyntax { RawKind: (int)SyntaxKind.NotPattern } notPat =>
            BuildIsSubPattern(model, expr, notPat.Pattern) is { } sub
                ? new IlUn(IlUnOp.Not, new IlParen(sub)) : null,
        RelationalPatternSyntax rp => BuildExpr(model, rp.Expression) is { } v
            ? new IlBin(RelationalIlOp(rp), expr, v) : null,
        BinaryPatternSyntax bp => BindPatternInput(expr, input =>
            BuildIsSubPattern(model, input, bp.Left) is { } l
            && BuildIsSubPattern(model, AndRightInput(model, bp, input), bp.Right) is { } r
                ? BinaryPatternIl(bp, l, r) : null),
        RecursivePatternSyntax rp => BuildRecursivePattern(model, expr, rp),
        _ => null,
    };

    private static IlExpr BinaryPatternIl(BinaryPatternSyntax bp, IlExpr l,
        IlExpr r) =>
        new IlParen(new IlBin(
            bp.IsKind(SyntaxKind.AndPattern) ? IlBinOp.And : IlBinOp.Or, l, r));

    private int _patternTempDepth;

    // pattern は入力の値を 1 回だけ計算して照合する。入力を複数回読む pattern
    // (and / or、複数条件の property pattern) で、読み直すと値や副作用が変わり
    // 得る式は temp に 1 回評価してから照合する。入れ子の temp は C の
    // 初期化子が同名の新しい変数を指さないよう深さで名前を分ける
    private IlExpr? BindPatternInput(IlExpr input, Func<IlExpr, IlExpr?> build)
    {
        if (IsCallFree(input)) return build(input);
        var name = $"__tcs_pat{_patternTempDepth}";
        _patternTempDepth++;
        var applied = build(new IlVar(name));
        _patternTempDepth--;
        return applied == null ? null : new IlIife([
            new IlLocal(name, input),
            new IlReturn(applied)]);
    }

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
        if (rp.PositionalPatternClause != null) return null;
        if (rp.Designation != null) return null;
        var subpatterns = rp.PropertyPatternClause?.Subpatterns
            .Where(sub => sub.NameColon != null).ToList() ?? [];
        // 型を書かない property pattern も null を除く (`{ }` は非 null test)。
        // `T?` の member は中の値から読む (型が `T` のときも同じ)
        var inputType = PatternInputType(model, rp);
        var patternType = rp.Type == null ? null : model.GetTypeInfo(rp.Type).Type;
        var nullableValue = IsNullableValueType(inputType) && (patternType == null
            || SymbolEqualityComparer.Default.Equals(UnwrapNullable(inputType),
                patternType));
        var nullCheck = rp.Type == null && inputType is { IsValueType: false };
        var inputReads = subpatterns.Count
            + (rp.Type != null || nullableValue || nullCheck ? 1 : 0);
        return inputReads > 1
            ? BindPatternInput(expr, input => Build(input))
            : Build(expr);

        IlExpr? Build(IlExpr input)
        {
            var conditions = new List<IlExpr>();
            if (nullableValue)
                conditions.Add(new IlNullableHasValue(input));
            else if (rp.Type != null)
            {
                var check = BuildTypeCheck(input, inputType, patternType,
                    FormatTypeReference(model, rp.Type));
                if (check == null) return null;
                conditions.Add(check);
            }
            else if (nullCheck)
                conditions.Add(new IlBin(IlBinOp.Ne, input, new IlLit("nil")));
            var recv = nullableValue ? new IlNullableValue(input) : input;
            foreach (var sub in subpatterns)
            {
                var patSym = model.GetSymbolInfo(sub.NameColon!.Name).Symbol;
                var propName = patSym != null
                    ? N(patSym) : N(sub.NameColon.Name.Identifier.ValueText);
                IlExpr propExpr = patSym is IPropertySymbol patProp
                        && IsCustomProperty(patProp)
                    ? new IlInvoke(recv, $"get_{propName}", [])
                    : new IlField(recv, propName);
                var cond = BuildIsSubPattern(model, propExpr, sub.Pattern);
                if (cond == null) return null;
                conditions.Add(cond);
            }
            if (conditions.Count == 0) return new IlLit("true");
            var result = conditions[0];
            for (var i = 1; i < conditions.Count; i++)
                result = new IlBin(IlBinOp.And, result, conditions[i]);
            return result;
        }
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
            stats.Add(SwitchDesignationLocal(model, dp, name, sw));
        }

        var arms = new List<(IlExpr, IlBlock)>();
        IlBlock? elseBlock = null;
        foreach (var arm in switchExpr.Arms)
        {
            var value = BuildExpr(model, arm.Expression);
            if (value == null) return null;
            if (arm.Pattern is DiscardPatternSyntax && arm.WhenClause == null)
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
            if (StructDesignation(model, dp) is (var inputType, var declType))
                return BuildStructDeclarationIs(new IlVar(name), expr, inputType,
                    declType);
            var check = BuildTypeCheck(new IlVar(name), PatternInputType(model, dp),
                model.GetTypeInfo(dp.Type).Type, FormatTypeReference(model, dp.Type));
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
        // 早期 break (terminal でない switch 束縛 break) は IlBreakScope で
        // 抜ける。continue は外側ループ束縛のまま
        var needsBreakScope = SwitchNeedsBreakScope(switchStmt);
        var governing = BuildExpr(model, switchStmt.Expression);
        if (governing == null) return false;
        var sw = new IlVar("__tcs_sw");
        var products = new List<IlStat>
        {
            new IlLocal("__tcs_sw", governing) { Origin = switchStmt },
        };

        var defaultSection = switchStmt.Sections.FirstOrDefault(s =>
            s.Labels.Any(l => l is DefaultSwitchLabelSyntax));

        foreach (var label in switchStmt.Sections
            .SelectMany(s => s.Labels.OfType<CasePatternSwitchLabelSyntax>())
            .Where(l => l.Pattern is DeclarationPatternSyntax
                { Designation: SingleVariableDesignationSyntax }))
        {
            var dp = (DeclarationPatternSyntax)label.Pattern;
            var name = ((SingleVariableDesignationSyntax)dp.Designation!)
                .Identifier.ValueText;
            products.Add(SwitchDesignationLocal(model, dp, name, sw)
                with { Origin = switchStmt });
        }

        // default と同じ section の case も出現順に照合する (後の section の
        // case より先に当たる)。本文は arm と else で共有する
        var arms = new List<(IlExpr, IlBlock)>();
        IlBlock? defaultBody = null;
        foreach (var section in switchStmt.Sections)
        {
            var caseLabels = section.Labels
                .Where(l => l is not DefaultSwitchLabelSyntax).ToList();
            if (caseLabels.Count == 0) continue;
            IlExpr? cond = null;
            foreach (var label in caseLabels)
            {
                IlExpr? one = label switch
                {
                    CaseSwitchLabelSyntax c when model.GetSymbolInfo(c.Value)
                        .Symbol is ITypeSymbol patType =>
                        BuildTypeCheck(sw, PatternInputType(model, c), patType,
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
            if (section == defaultSection) defaultBody = body;
            arms.Add((cond, body));
        }

        if (defaultSection != null && defaultBody == null)
        {
            defaultBody = BuildSwitchSectionBody(model,
                defaultSection.Statements);
            if (defaultBody == null) return false;
        }

        if (arms.Count > 0)
            products.Add(new IlIf([.. arms], defaultBody) { Origin = switchStmt });
        else if (defaultBody != null)
            products.Add(new IlDo(defaultBody) { Origin = switchStmt });
        if (needsBreakScope)
            acc.Add(new IlBreakScope(new IlBlock([.. products])) { Origin = switchStmt });
        else
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
        IReadOnlyList<StatementSyntax> statements)
    {
        // 暗黙 break は末尾 block 連鎖の末尾にも現れる (case X: { ...; break; })。
        // 早期 break は IlBreak のまま残し、IlBreakScope が受ける
        var terminal = new HashSet<StatementSyntax>();
        CollectTerminalBreaks(statements, terminal);
        var acc = new List<IlStat>();
        return BuildSectionStatsInto(model, statements, terminal, acc)
            ? new IlBlock([.. acc])
            : null;
    }

    private bool BuildSectionStatsInto(SemanticModel model,
        IReadOnlyList<StatementSyntax> statements,
        HashSet<StatementSyntax> terminal, List<IlStat> acc)
    {
        foreach (var stmt in statements)
        {
            if (terminal.Contains(stmt))
                continue;
            if (stmt is BlockSyntax block)
            {
                if (!BuildSectionStatsInto(model, block.Statements, terminal,
                        acc))
                    return false;
                continue;
            }
            if (!BuildStatInto(model, stmt, acc))
                return false;
        }
        return true;
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

        // struct parameter は値渡し。runtime (LINQ / Sort 等) は要素を copy せず
        // 渡すので、呼び出し側ではなく入口で copy する (il-spec §10)
        var symbol = model.GetSymbolInfo(lambda).Symbol as IMethodSymbol;
        var copies = (symbol?.Parameters ?? []).Select(p =>
                CopyIfMutableStruct(p.Type, new IlVar(paramList[p.Ordinal])))
            .OfType<IlStructCopy>()
            .Select(IlStat (c) => new IlAssign(c.E, c))
            .ToList();
        if (exprBody != null)
        {
            var body = BuildExpr(model, exprBody);
            if (body == null) return null;
            var locals = IsPatternDesignationNames(exprBody).ToImmutableArray();
            if (copies.Count == 0)
                return new IlClosure(paramList, null, body, locals);
            IlStat tail = symbol!.ReturnsVoid ? new IlCallStat(body) : new IlReturn(body);
            return new IlClosure(paramList, new IlBlock([.. copies, tail]), null, locals);
        }
        if (block == null) return null;
        var acc = new List<IlStat>(copies);
        if (!BuildStatsInto(model, block.Statements, acc)) return null;
        return new IlClosure(paramList, new IlBlock([.. acc]), null, []);
    }

    // when-not-null 側の MemberBinding (`.X`) が指す receiver local 名。
    // 参照型 receiver は `__tcs_ca` そのもの、`T?` receiver は値を取り出した
    // `__tcs_cav`
    private string _condAccessVar = "__tcs_ca";

    // `?.` (il-spec §3): receiver を local へ 1 回評価し、null でなければ
    // when-not-null を返す IIFE。`T?` receiver は IlNullableHasValue /
    // IlNullableValue の明示ノード (nil 比較を IL に残さない)。結果が
    // 非 nullable 値型なら C# どおり T? に wrap する (null 側は値なし)
    private IlExpr? BuildConditionalAccess(SemanticModel model,
        ConditionalAccessExpressionSyntax condAccess)
    {
        var receiver = BuildExpr(model, condAccess.Expression);
        if (receiver == null) return null;
        return BuildConditionalCore(model, receiver,
            model.GetTypeInfo(condAccess.Expression).Type, condAccess.WhenNotNull);
    }

    private IlExpr? BuildConditionalCore(SemanticModel model, IlExpr receiver,
        ITypeSymbol? receiverType, ExpressionSyntax whenNotNullSyntax)
    {
        var nullableValue = IsNullableValueType(receiverType);
        var underlying = nullableValue
            ? ((INamedTypeSymbol)receiverType!).TypeArguments[0] : receiverType;
        var objName = nullableValue ? "__tcs_cav" : "__tcs_ca";
        var saved = _condAccessVar;
        _condAccessVar = objName;
        var whenNotNull = BuildConditionalWhenNotNull(model, whenNotNullSyntax,
            new IlVar(objName), underlying);
        _condAccessVar = saved;
        if (whenNotNull == null) return null;
        var resultType = model.GetTypeInfo(whenNotNullSyntax).Type;
        if (resultType is { IsValueType: true, SpecialType: not SpecialType.System_Void }
            && !IsNullableValueType(resultType)
            && whenNotNull is not IlNullableWrap)
            whenNotNull = new IlNullableWrap(whenNotNull, resultType.ToDisplayString());
        var ca = new IlVar("__tcs_ca");
        IlExpr cond = nullableValue
            ? new IlNullableHasValue(ca)
            : new IlBin(IlBinOp.Ne, ca, new IlLit("nil"));
        var then = nullableValue
            ? new IlBlock([new IlLocal(objName, new IlNullableValue(ca)),
                new IlReturn(whenNotNull)])
            : new IlBlock([new IlReturn(whenNotNull)]);
        return new IlIife([
            new IlLocal("__tcs_ca", receiver),
            new IlIf([(cond, then)], null)]);
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
                return BuildConditionalInvocation(model, mb2, inv,
                    obj, receiverType);
            case ElementBindingExpressionSyntax eb:
            {
                var index = BuildExpr(model,
                    eb.ArgumentList.Arguments[0].Expression);
                if (index == null) return null;
                var typeDef = receiverType?.OriginalDefinition
                    .ToDisplayString() ?? "";
                return new IlIndex(obj, DictKey(model, receiverType,
                        eb.ArgumentList.Arguments[0].Expression, index),
                    IsListType(typeDef) || receiverType is IArrayTypeSymbol);
            }
            case ConditionalAccessExpressionSyntax nested:
            {
                var inner = BuildConditionalWhenNotNull(model,
                    nested.Expression, obj, receiverType);
                if (inner == null) return null;
                return BuildConditionalCore(model, inner,
                    model.GetTypeInfo(nested.Expression).Type, nested.WhenNotNull);
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

        if (member == "Count" && (IsListType(typeDef) || IsDictType(typeDef)
                || IsDictCollectionType(typeDef)))
            return IsDictType(typeDef)
                ? new IlCall("Dict.Count", [obj]) : new IlLen(obj);
        if (member == "Keys" && IsDictType(typeDef))
            return DictKeys(receiverType, obj);
        if (member == "Values" && IsDictType(typeDef))
            return new IlCall("Dict.Values", [obj]);
        if (member == "Length"
            && (receiverType?.SpecialType == SpecialType.System_String
                || receiverType is IArrayTypeSymbol))
            return new IlLen(obj);
        if (FindInstanceProperty(receiverType, member) is { } condProp
            && IsCustomProperty(condProp))
            return IsUserStruct(receiverType)
                ? new IlCall($"{TypeRef(condProp.ContainingType)}.get_{N(condProp)}",
                    [ConditionalStructReceiver(obj, receiverType!)])
                : new IlInvoke(obj, $"get_{N(condProp)}", []);
        var memberSym = receiverType?.GetMembers(member).FirstOrDefault();
        return new IlField(obj, memberSym != null ? N(memberSym) : N(member));
    }

    private IlExpr? BuildConditionalInvocation(SemanticModel model,
        MemberBindingExpressionSyntax mb, InvocationExpressionSyntax inv,
        IlExpr obj, ITypeSymbol? receiverType)
    {
        var methodName = mb.Name.Identifier.ValueText;
        var args = new List<IlExpr>();
        foreach (var a in inv.ArgumentList.Arguments)
        {
            if (!a.RefKindKeyword.IsKind(SyntaxKind.None)) return null;
            var built = BuildExpr(model, a.Expression);
            if (built == null) return null;
            args.Add(built);
        }
        var argArr = WithOmittedDefaults(model, inv, [.. args]);
        var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";

        // user 定義の拡張メソッドは `x.M()` と同じく静的呼び出し
        if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol { ReducedFrom: { } ext }
            && ext.Locations.Any(l => l.IsInSource))
            return new IlCall($"{TypeRef(ext.ContainingType)}.{N(ext)}",
                [IsUserStruct(receiverType)
                    ? ConditionalStructReceiver(obj, receiverType!) : obj, .. argArr]);
        if (IsNumericCompareTo(model.GetSymbolInfo(inv).Symbol))
            return BuildNumericCompareTo(obj, argArr);

        if (receiverType?.SpecialType == SpecialType.System_String)
            return TryBuildStringCall(obj, methodName,
                    WrapCharArgs(model, inv.ArgumentList, argArr))
                ?? new IlInvoke(obj, methodName, argArr);

        if (IsListType(typeDef))
        {
            switch (methodName)
            {
                case "Add":
                    return new IlCall("table.insert", [obj, .. argArr]);
                case "Remove":
                    return new IlCall("List.Remove", [obj, .. WithStructElementOps(
                        receiverType, null, methodName, argArr)]);
                case "RemoveAt":
                    return new IlCall("table.remove",
                        [obj, new IlBin(IlBinOp.AddNum, argArr[0],
                            new IlLit("1"))]);
                case "Clear":
                    return BuildListClear(obj);
                case "FirstOrDefault":
                case "LastOrDefault":
                {
                    var invoked = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                    if (invoked == null) return null;
                    var predicate = argArr.Length > 0 ? argArr[0] : new IlLit("nil");
                    return new IlCall($"List.{methodName}",
                        [obj, predicate, DefaultIl(invoked.ReturnType)]);
                }
            }
            if (ListRuntimeMethods.Contains(methodName))
                return new IlCall($"List.{methodName}", [obj, .. WithStructElementOps(
                    receiverType, model.GetSymbolInfo(inv).Symbol as IMethodSymbol,
                    methodName, argArr)]);
        }

        if (IsDictType(typeDef))
        {
            return methodName switch
            {
                "Remove" => new IlCall("Dict.Remove", [obj, DictKey(model,
                    receiverType, inv.ArgumentList.Arguments[0].Expression, argArr[0])]),
                "ContainsKey" => new IlCall("Dict.ContainsKey", [obj, DictKey(model,
                    receiverType, inv.ArgumentList.Arguments[0].Expression, argArr[0])]),
                "Add" or "TryGetValue" => null,
                _ => new IlInvoke(obj, methodName, argArr),
            };
        }

        var methodSym = receiverType?.GetMembers(methodName)
            .OfType<IMethodSymbol>().FirstOrDefault();
        // struct (`S?` receiver の .Value) は静的ディスパッチ。receiver は
        // rvalue (C# の .Value は copy) なので変異は捨てられる
        if (IsUserStruct(receiverType) && methodSym != null)
            return new IlCall($"{TypeRef(methodSym.ContainingType)}.{N(methodSym)}",
                [ConditionalStructReceiver(obj, receiverType!), .. argArr]);
        return new IlInvoke(obj, methodSym != null ? N(methodSym) : N(methodName),
            argArr);
    }

    private IlExpr ConditionalStructReceiver(IlExpr obj, ITypeSymbol type) =>
        type is INamedTypeSymbol { IsReadOnly: true }
            ? obj : new IlStructCopy(obj, TypeName(type));
}
