using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder: 副作用 lvalue の temp 化、custom property accessor、??=、
// 分解代入、out 引数 multi-return、object initializer。legacy の
// TryLowerLvalue / EmitPropertyAssignment / EmitDeconstruction 系の写像。
public partial class LuaEmitter
{
    // legacy TryLowerLvalue の写像: (setup 文列, access place)。force は
    // 副作用が無くても受け手 / 添字を temp に固定する (EvalOrder 参照)
    private (List<IlStat> Setup, IlExpr Access)? BuildLoweredTarget(
        SemanticModel model, ExpressionSyntax left, bool force = false)
    {
        switch (left)
        {
            case MemberAccessExpressionSyntax ma
                when force || HasLvalueSideEffect(model, ma.Expression, ma.Expression):
            {
                var recv = BuildExpr(model, ma.Expression);
                if (recv == null) return null;
                return ([new IlLocal("__tcs_obj", recv)],
                    new IlField(new IlVar("__tcs_obj"),
                        model.GetSymbolInfo(ma).Symbol is { } lowSym
                            ? N(lowSym) : N(ma.Name.Identifier.ValueText)));
            }
            case ElementAccessExpressionSyntax ea
                when force || HasLvalueSideEffect(model, ea, ea.Expression):
            {
                var recv = BuildExpr(model, ea.Expression);
                var index = BuildExpr(model,
                    ea.ArgumentList.Arguments[0].Expression);
                if (recv == null || index == null) return null;
                var receiverType = model.GetTypeInfo(ea.Expression).Type;
                var typeDef = receiverType?.OriginalDefinition
                    .ToDisplayString() ?? "";
                var plusOne = IsListType(typeDef)
                    || receiverType is IArrayTypeSymbol;
                return ([
                    new IlLocal("__tcs_obj", recv),
                    new IlLocal("__tcs_idx", index)],
                    new IlIndex(new IlVar("__tcs_obj"),
                        new IlVar("__tcs_idx"), plusOne));
            }
            default:
                return null;
        }
    }

    // lvalue の受け手 / 添字 (evaluated) の評価に副作用があるか。custom
    // property の getter は呼び出しなので含める。ただし値型の受け手は temp が
    // copy になり更新が消えるため、getter だけを理由には temp 化しない
    private static bool HasLvalueSideEffect(SemanticModel model,
        ExpressionSyntax evaluated, ExpressionSyntax receiver) =>
        HasSideEffectSyntax(evaluated)
        || (model.GetTypeInfo(receiver).Type is { IsReferenceType: true }
            && evaluated.DescendantNodesAndSelf().Any(n =>
                n is IdentifierNameSyntax or MemberAccessExpressionSyntax
                && model.GetSymbolInfo(n).Symbol is IPropertySymbol p
                && IsCustomProperty(p)));

    // custom property の (receiver ノード, 名前, 副作用有無, static か,
    // struct 所有型名 — struct accessor は自由関数呼びになる)
    private (IlExpr Recv, string Name, bool SideEffect, bool IsStatic,
        string? StructOwner)?
        BuildPropTarget(SemanticModel model, ExpressionSyntax left)
    {
        switch (left)
        {
            case IdentifierNameSyntax id
                when model.GetSymbolInfo(id).Symbol is IPropertySymbol prop
                    && IsCustomProperty(prop):
                return prop.IsStatic
                    ? (new IlVar(TypeRef(prop.ContainingType)),
                        N(prop), false, true, null)
                    : (new IlVar("self"), N(prop), false, false,
                        IsUserStruct(prop.ContainingType)
                            ? TypeRef(prop.ContainingType) : null);
            case MemberAccessExpressionSyntax ma
                when model.GetSymbolInfo(ma).Symbol is IPropertySymbol prop
                    && IsCustomProperty(prop):
            {
                if (prop.IsStatic)
                    return (new IlVar(TypeRef(prop.ContainingType)),
                        N(prop), false, true, null);
                var recv = BuildExpr(model, ma.Expression);
                return recv == null
                    ? null
                    : (recv, N(prop),
                        HasLvalueSideEffect(model, ma.Expression, ma.Expression), false,
                        IsUserStruct(model.GetTypeInfo(ma.Expression).Type)
                            ? TypeRef(prop.ContainingType) : null);
            }
            default:
                return null;
        }
    }

    // structOwner 非 null = struct の accessor (自由関数呼び。set の receiver
    // は C# が変数を強制する — rvalue への property 代入は CS1612)
    private IlExpr BuildPropGet(IlExpr recv, string name, bool isStatic,
        string? structOwner = null) =>
        structOwner != null
            ? new IlCall($"{structOwner}.get_{name}", [recv])
            : isStatic
                ? new IlDynCall(new IlField(recv, $"get_{name}"), [])
                : new IlInvoke(recv, $"get_{name}", []);

    private IlExpr BuildPropSet(IlExpr recv, string name, bool isStatic,
        IlExpr value, string? structOwner = null) =>
        structOwner != null
            ? new IlCall($"{structOwner}.set_{name}", [recv, value])
            : isStatic
                ? new IlDynCall(new IlField(recv, $"set_{name}"), [value])
                : new IlInvoke(recv, $"set_{name}", [value]);

    // legacy EmitPropertyAssignment の写像 (statement 位置)
    private bool BuildPropAssignInto(SemanticModel model,
        AssignmentExpressionSyntax assign, SyntaxNode? origin,
        List<IlStat> acc)
    {
        if (BuildPropTarget(model, assign.Left) is not { } prop) return false;
        var right = BuildExpr(model, assign.Right);
        if (right == null) return false;
        var target = prop.SideEffect ? new IlVar("__tcs_obj") : prop.Recv;

        IlStat body;
        var needsWrap = prop.SideEffect;
        if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            body = new IlCallStat(
                BuildPropSet(target, prop.Name, prop.IsStatic, right,
                    prop.StructOwner));
        }
        else if (assign.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            body = new IlIf([(IsNullIl(
                    BuildPropGet(target, prop.Name, prop.IsStatic,
                        prop.StructOwner),
                    model.GetTypeInfo(assign.Left).Type),
                new IlBlock([new IlCallStat(
                    BuildPropSet(target, prop.Name, prop.IsStatic, right,
                        prop.StructOwner))]))],
                null);
            needsWrap = true; // legacy は if 形を常に IIFE で包む
        }
        else if (CompoundOperator(model, assign) is { } op)
        {
            var applied = BuildCompoundValue(model, assign, op,
                BuildPropGet(target, prop.Name, prop.IsStatic,
                    prop.StructOwner),
                new IlParen(right));
            if (applied == null) return false;
            body = new IlCallStat(
                BuildPropSet(target, prop.Name, prop.IsStatic, applied,
                    prop.StructOwner));
        }
        else
        {
            return false;
        }

        if (!needsWrap)
        {
            acc.Add(body switch
            {
                IlCallStat c => new IlCallStat(c.Call) { Origin = origin },
                _ => body with { Origin = origin },
            });
            return true;
        }
        var stats = new List<IlStat>();
        if (prop.SideEffect) stats.Add(new IlLocal("__tcs_obj", prop.Recv));
        stats.Add(body);
        acc.Add(new IlCallStat(new IlIife([.. stats])) { Origin = origin });
        return true;
    }

    // 式位置の custom property 代入: 受け手を 1 回だけ評価し、setter に
    // 渡した値を式の値にする (getter を読み直さない)
    private IlIife? BuildPropAssignExpr(SemanticModel model,
        AssignmentExpressionSyntax assign)
    {
        if (BuildPropTarget(model, assign.Left) is not { } prop) return null;
        var right = BuildExpr(model, assign.Right);
        if (right == null) return null;
        var stats = new List<IlStat>();
        var target = prop.Recv;
        if (prop.SideEffect)
        {
            stats.Add(new IlLocal("__tcs_obj", prop.Recv));
            target = new IlVar("__tcs_obj");
        }
        var value = new IlVar("__tcs_v");
        var get = BuildPropGet(target, prop.Name, prop.IsStatic,
            prop.StructOwner);
        var set = new IlCallStat(BuildPropSet(target, prop.Name,
            prop.IsStatic, value, prop.StructOwner));
        if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            stats.Add(new IlLocal("__tcs_v",
                WrapStructCopy(model, assign.Right, right)));
            stats.Add(set);
        }
        else if (assign.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            stats.Add(new IlLocal("__tcs_v", get));
            stats.Add(new IlIf([(IsNullIl(value,
                    model.GetTypeInfo(assign.Left).Type),
                new IlBlock([new IlAssign(value, right), set]))], null));
        }
        else if (CompoundOperator(model, assign) is { } op
            && BuildCompoundValue(model, assign, op, get,
                new IlParen(right)) is { } applied)
        {
            stats.Add(new IlLocal("__tcs_v", applied));
            stats.Add(set);
        }
        else
        {
            return null;
        }
        stats.Add(new IlReturn(value));
        return new IlIife([.. stats]);
    }

    // legacy EmitIncrement の custom property / lowered lvalue 経路
    private bool BuildLoweredIncrementInto(SemanticModel model,
        ExpressionSyntax operand, bool increment, SyntaxNode? origin,
        List<IlStat> acc)
    {
        var op = increment ? IlBinOp.AddNum : IlBinOp.Sub;
        if (BuildPropTarget(model, operand) is { } prop)
        {
            var target = prop.SideEffect ? new IlVar("__tcs_obj") : prop.Recv;
            var body = new IlCallStat(BuildPropSet(target, prop.Name,
                prop.IsStatic,
                StepValue(model, operand, BuildPropGet(target, prop.Name,
                    prop.IsStatic, prop.StructOwner), increment),
                prop.StructOwner));
            if (prop.SideEffect)
                acc.Add(new IlDo(new IlBlock([
                    new IlLocal("__tcs_obj", prop.Recv), body]))
                    { Origin = origin });
            else
                acc.Add(new IlCallStat(body.Call) { Origin = origin });
            return true;
        }
        if (BuildLoweredTarget(model, operand) is { } lowered)
        {
            acc.Add(new IlDo(new IlBlock([.. lowered.Setup,
                new IlAssign(lowered.Access,
                    StepValue(model, operand, lowered.Access, increment))]))
                { Origin = origin });
            return true;
        }
        return false;
    }

    // ??= (statement 位置、custom property 以外)
    private bool BuildCoalesceAssignInto(SemanticModel model,
        AssignmentExpressionSyntax assign, SyntaxNode? origin,
        List<IlStat> acc)
    {
        var right = BuildExpr(model, assign.Right);
        if (right == null) return false;
        var leftType = model.GetTypeInfo(assign.Left).Type;
        if (BuildLoweredTarget(model, assign.Left) is { } lowered)
        {
            acc.Add(new IlDo(new IlBlock([.. lowered.Setup,
                new IlIf([(IsNullIl(lowered.Access, leftType),
                    new IlBlock([new IlAssign(lowered.Access, right)]))],
                    null)]))
                { Origin = origin });
            return true;
        }
        var left = BuildExpr(model, assign.Left);
        if (left == null) return false;
        acc.Add(new IlIf([(IsNullIl(left, leftType),
                new IlBlock([new IlAssign(left, right)]))], null)
            { Origin = origin });
        return true;
    }

    // null 判定: `T?` は明示ノード (値なし)、参照型は nil 比較
    private static IlExpr IsNullIl(IlExpr e, ITypeSymbol? type) =>
        IsNullableValueType(type)
            ? new IlUn(IlUnOp.Not, new IlNullableHasValue(e))
            : new IlBin(IlBinOp.Eq, e, new IlLit("nil"));

    // lowered lvalue への代入 (simple / compound)。受け手 / 添字を temp に
    // 固定してから右辺を評価し、代入後の place を値として返す IIFE
    private IlIife? BuildLoweredAssign(SemanticModel model,
        AssignmentExpressionSyntax assign)
    {
        if (BuildLoweredTarget(model, assign.Left,
                force: true) is not { } lowered)
            return null;
        var right = BuildExpr(model, assign.Right);
        if (right == null) return null;
        IlExpr? applied;
        if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
            applied = WrapStructCopy(model, assign.Right, right);
        else
        {
            var op = CompoundOperator(model, assign);
            applied = op == null ? null : BuildCompoundValue(model, assign, op,
                lowered.Access, new IlParen(right));
        }
        if (applied == null) return null;
        return new IlIife([.. lowered.Setup,
            new IlAssign(lowered.Access, applied),
            new IlReturn(lowered.Access)]);
    }

    private bool BuildLoweredAssignInto(SemanticModel model,
        AssignmentExpressionSyntax assign, SyntaxNode? origin,
        List<IlStat> acc)
    {
        if (BuildLoweredAssign(model, assign) is not { } iife) return false;
        acc.Add(new IlCallStat(iife) { Origin = origin });
        return true;
    }

    // legacy EmitDeconstruction の写像
    private bool BuildDeconstructionInto(SemanticModel model,
        ExpressionSyntax rhs, List<IlExpr> targets, bool declare,
        SyntaxNode? origin, List<IlStat> acc)
    {
        var rhsBuilt = BuildExpr(model, rhs);
        if (rhsBuilt == null) return false;
        var typeSymbol = model.GetTypeInfo(rhs).Type;
        var propNames = GetDeconstructPropertyNames(typeSymbol, targets.Count);
        var values = propNames != null
            ? propNames.Select(IlExpr (p) =>
                new IlField(new IlVar("__tcs_dec"), p)).ToImmutableArray()
            : [new IlVar("__tcs_dec")];
        acc.Add(new IlLocal("__tcs_dec", rhsBuilt) { Origin = origin });
        acc.Add(new IlMultiAssign([.. targets], values, declare)
            { Origin = origin });
        return true;
    }

    // legacy EmitRefMultiReturnCall の写像 (out 引数 → Lua multi-return)
    private IlExpr? BuildRefMultiReturnValue(SemanticModel model,
        InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax ma,
        IMethodSymbol method, out List<IlExpr> outTargets, out bool returnsVoid)
    {
        outTargets = [];
        returnsVoid = method.ReturnsVoid;
        var callArgs = new List<IlExpr>();
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                var name = TryGetOutArgumentName(arg);
                outTargets.Add(new IlVar(
                    string.IsNullOrEmpty(name) ? "_" : name!));
            }
            else if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))
            {
                return null;
            }
            else
            {
                var built = BuildExpr(model, arg.Expression);
                if (built == null) return null;
                callArgs.Add(built);
            }
        }
        var methodName = N(method);
        if (method.IsStatic)
            return new IlCall($"{TypeRef(method.ContainingType)}.{methodName}",
                [.. callArgs]);
        var recv = BuildExpr(model, ma.Expression);
        return recv == null
            ? null : new IlInvoke(recv, methodName, [.. callArgs]);
    }

    private IlExpr? BuildRefMultiReturnExpr(SemanticModel model,
        InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax ma,
        IMethodSymbol method)
    {
        var call = BuildRefMultiReturnValue(model, invocation, ma, method,
            out var outs, out var returnsVoid);
        if (call == null || outs.Count == 0) return null;
        if (returnsVoid)
            return null; // 値なしは statement 専用 (BuildRefMultiReturnStatInto)
        return new IlIife([
            new IlLocal("__tcs_ret", null),
            new IlMultiAssign([new IlVar("__tcs_ret"), .. outs], [call], false),
            new IlReturn(new IlVar("__tcs_ret"))]);
    }

    private bool BuildRefMultiReturnStatInto(SemanticModel model,
        InvocationExpressionSyntax invocation, SyntaxNode? origin,
        List<IlStat> acc)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
            return false;
        if (model.GetSymbolInfo(ma).Symbol is not IMethodSymbol method)
            return false;
        if (!method.Parameters.Any(p => p.RefKind == RefKind.Out)
            || !method.DeclaringSyntaxReferences
                .Any(r => ReferenceTrees.Contains(r.SyntaxTree)))
            return false;
        var call = BuildRefMultiReturnValue(model, invocation, ma, method,
            out var outs, out var returnsVoid);
        if (call == null || outs.Count == 0) return false;
        if (returnsVoid)
        {
            acc.Add(new IlMultiAssign([.. outs], [call], false)
                { Origin = origin });
            return true;
        }
        acc.Add(new IlCallStat(new IlIife([
                new IlLocal("__tcs_ret", null),
                new IlMultiAssign([new IlVar("__tcs_ret"), .. outs], [call],
                    false),
                new IlReturn(new IlVar("__tcs_ret"))]))
            { Origin = origin });
        return true;
    }

    // object initializer: `new T(args) { M = v, Nested = { X = 1 }, Items = { a, b } }`
    // を IIFE (local __tcs_init = ctor; 代入...; return) に落とす。入れ子の
    // initializer は C# と同じく既存 member への代入 / Add (new はしない)
    private IlExpr? BuildObjectInitializerExpr(SemanticModel model,
        IlExpr ctor, InitializerExpressionSyntax initializer)
    {
        var stats = new List<IlStat> { new IlLocal("__tcs_init", ctor) };
        if (!AddInitializerStats(model, new IlVar("__tcs_init"), initializer, stats))
            return null;
        stats.Add(new IlReturn(new IlVar("__tcs_init")));
        return new IlIife([.. stats]);
    }

    private bool AddInitializerStats(SemanticModel model, IlExpr target,
        InitializerExpressionSyntax initializer, List<IlStat> stats)
    {
        foreach (var expr in initializer.Expressions)
        {
            if (expr is not AssignmentExpressionSyntax
                { Left: IdentifierNameSyntax name } assign)
                return false;
            var initSym = model.GetSymbolInfo(name).Symbol;
            var initName = initSym != null ? N(initSym) : N(name.Identifier.ValueText);
            var customProp = initSym is IPropertySymbol prop && IsCustomProperty(prop)
                ? prop : null;
            var structOwner = customProp != null && IsUserStruct(customProp.ContainingType)
                ? TypeRef(customProp.ContainingType) : null;
            if (assign.Right is InitializerExpressionSyntax nested)
            {
                IlExpr member = customProp != null
                    ? BuildPropGet(target, initName, isStatic: false, structOwner)
                    : new IlField(target, initName);
                if (nested.IsKind(SyntaxKind.ObjectInitializerExpression))
                {
                    if (!AddInitializerStats(model, member, nested, stats))
                        return false;
                    continue;
                }
                var memberType = model.GetTypeInfo(name).Type?.OriginalDefinition
                    .ToDisplayString() ?? "";
                if (!nested.IsKind(SyntaxKind.CollectionInitializerExpression)
                    || !IsListType(memberType))
                    return false;
                foreach (var item in nested.Expressions)
                {
                    var built = BuildExpr(model, item);
                    if (built == null) return false;
                    stats.Add(new IlCallStat(new IlCall("table.insert", [member, built])));
                }
                continue;
            }
            var value = BuildExpr(model, assign.Right);
            if (value == null) return false;
            stats.Add(customProp != null
                ? new IlCallStat(BuildPropSet(target, initName, isStatic: false, value,
                    structOwner))
                : new IlAssign(new IlField(target, initName), value));
        }
        return true;
    }

    private IlExpr? BuildRefTypeTable(SemanticModel model,
        InitializerExpressionSyntax? initializer, string type)
    {
        if (initializer == null) return new IlTable([], ObjectType: type);
        var entries = new List<IlTableEntry>();
        foreach (var expr in initializer.Expressions)
        {
            if (expr is not AssignmentExpressionSyntax
                {
                    Left: IdentifierNameSyntax name,
                    Right: not InitializerExpressionSyntax
                } assign)
                return null;
            var value = BuildExpr(model, assign.Right);
            if (value == null) return null;
            entries.Add(new IlTableEntry(null, value,
                model.GetSymbolInfo(name).Symbol is { } entrySym
                    ? N(entrySym) : N(name.Identifier.ValueText)));
        }
        return new IlTable([.. entries], ObjectType: type);
    }
}
