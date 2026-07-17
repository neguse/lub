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
    // legacy TryLowerLvalue の写像: (setup 文列, access place)
    private (List<IlStat> Setup, IlExpr Access)? BuildLoweredTarget(
        SemanticModel model, ExpressionSyntax left)
    {
        switch (left)
        {
            case MemberAccessExpressionSyntax ma
                when HasSideEffectSyntax(ma.Expression):
            {
                var recv = BuildExpr(model, ma.Expression);
                if (recv == null) return null;
                return ([new IlLocal("__tcs_obj", recv)],
                    new IlField(new IlVar("__tcs_obj"),
                        ma.Name.Identifier.ValueText));
            }
            case ElementAccessExpressionSyntax ea when HasSideEffectSyntax(ea):
            {
                var recv = BuildExpr(model, ea.Expression);
                var index = BuildExpr(model,
                    ea.ArgumentList.Arguments[0].Expression);
                if (recv == null || index == null) return null;
                var receiverType = model.GetTypeInfo(ea.Expression).Type;
                var typeDef = receiverType?.OriginalDefinition
                    .ToDisplayString() ?? "";
                var adjusted = IsListType(typeDef)
                    || receiverType is IArrayTypeSymbol
                        ? new IlBin(IlBinOp.AddNum, index, new IlLit("1"))
                        : index;
                return ([
                    new IlLocal("__tcs_obj", recv),
                    new IlLocal("__tcs_idx", adjusted)],
                    new IlIndex(new IlVar("__tcs_obj"),
                        new IlVar("__tcs_idx"), false));
            }
            default:
                return null;
        }
    }

    // custom property の (receiver ノード, 名前, 副作用有無, static か)
    private (IlExpr Recv, string Name, bool SideEffect, bool IsStatic)?
        BuildPropTarget(SemanticModel model, ExpressionSyntax left)
    {
        switch (left)
        {
            case IdentifierNameSyntax id
                when model.GetSymbolInfo(id).Symbol is IPropertySymbol prop
                    && IsCustomProperty(prop):
                return prop.IsStatic
                    ? (new IlVar(prop.ContainingType.Name),
                        id.Identifier.ValueText, false, true)
                    : (new IlVar("self"), id.Identifier.ValueText, false, false);
            case MemberAccessExpressionSyntax ma
                when model.GetSymbolInfo(ma).Symbol is IPropertySymbol prop
                    && IsCustomProperty(prop):
            {
                if (prop.IsStatic)
                    return (new IlVar(prop.ContainingType.Name),
                        ma.Name.Identifier.ValueText, false, true);
                var recv = BuildExpr(model, ma.Expression);
                return recv == null
                    ? null
                    : (recv, ma.Name.Identifier.ValueText,
                        HasSideEffectSyntax(ma.Expression), false);
            }
            default:
                return null;
        }
    }

    private IlExpr BuildPropGet(IlExpr recv, string name, bool isStatic) =>
        isStatic
            ? new IlDynCall(new IlField(recv, $"get_{name}"), [])
            : new IlInvoke(recv, $"get_{name}", []);

    private IlExpr BuildPropSet(IlExpr recv, string name, bool isStatic,
        IlExpr value) =>
        isStatic
            ? new IlDynCall(new IlField(recv, $"set_{name}"), [value])
            : new IlInvoke(recv, $"set_{name}", [value]);

    // legacy EmitPropertyAssignment の写像 (statement 位置)
    private bool BuildPropAssignInto(SemanticModel model,
        AssignmentExpressionSyntax assign, StatementSyntax origin,
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
                BuildPropSet(target, prop.Name, prop.IsStatic, right));
        }
        else if (assign.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            body = new IlIf([(new IlBin(IlBinOp.Eq,
                    BuildPropGet(target, prop.Name, prop.IsStatic),
                    new IlLit("nil")),
                new IlBlock([new IlCallStat(
                    BuildPropSet(target, prop.Name, prop.IsStatic, right))]))],
                null);
            needsWrap = true; // legacy は if 形を常に IIFE で包む
        }
        else if (CompoundOperator(model, assign) is { } op)
        {
            var applied = BuildCompoundValue(model, assign, op,
                BuildPropGet(target, prop.Name, prop.IsStatic),
                new IlParen(right));
            if (applied == null) return false;
            body = new IlCallStat(
                BuildPropSet(target, prop.Name, prop.IsStatic, applied));
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

    // legacy EmitIncrement の custom property / lowered lvalue 経路
    private bool BuildLoweredIncrementInto(SemanticModel model,
        ExpressionSyntax operand, bool increment, StatementSyntax origin,
        List<IlStat> acc)
    {
        var op = increment ? IlBinOp.AddNum : IlBinOp.Sub;
        if (BuildPropTarget(model, operand) is { } prop)
        {
            var target = prop.SideEffect ? new IlVar("__tcs_obj") : prop.Recv;
            var body = new IlCallStat(BuildPropSet(target, prop.Name,
                prop.IsStatic,
                new IlBin(op, BuildPropGet(target, prop.Name, prop.IsStatic),
                    new IlLit("1"))));
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
                    new IlBin(op, lowered.Access, new IlLit("1")))]))
                { Origin = origin });
            return true;
        }
        return false;
    }

    // ??= (statement 位置、custom property 以外)
    private bool BuildCoalesceAssignInto(SemanticModel model,
        AssignmentExpressionSyntax assign, StatementSyntax origin,
        List<IlStat> acc)
    {
        var right = BuildExpr(model, assign.Right);
        if (right == null) return false;
        if (BuildLoweredTarget(model, assign.Left) is { } lowered)
        {
            acc.Add(new IlDo(new IlBlock([.. lowered.Setup,
                new IlIf([(new IlBin(IlBinOp.Eq, lowered.Access,
                        new IlLit("nil")),
                    new IlBlock([new IlAssign(lowered.Access, right)]))],
                    null)]))
                { Origin = origin });
            return true;
        }
        var left = BuildExpr(model, assign.Left);
        if (left == null) return false;
        acc.Add(new IlIf([(new IlBin(IlBinOp.Eq, left, new IlLit("nil")),
                new IlBlock([new IlAssign(left, right)]))], null)
            { Origin = origin });
        return true;
    }

    // compound + lowered lvalue (statement 位置): IIFE 形の写像
    private bool BuildLoweredCompoundInto(SemanticModel model,
        AssignmentExpressionSyntax assign, StatementSyntax origin,
        List<IlStat> acc)
    {
        if (BuildLoweredTarget(model, assign.Left) is not { } lowered)
            return false;
        var op = CompoundOperator(model, assign);
        var right = BuildExpr(model, assign.Right);
        if (op == null || right == null) return false;
        var applied = BuildCompoundValue(model, assign, op, lowered.Access,
            new IlParen(right));
        if (applied == null) return false;
        acc.Add(new IlCallStat(new IlIife([.. lowered.Setup,
                new IlAssign(lowered.Access, applied),
                new IlReturn(lowered.Access)]))
            { Origin = origin });
        return true;
    }

    // legacy EmitDeconstruction の写像
    private bool BuildDeconstructionInto(SemanticModel model,
        ExpressionSyntax rhs, List<IlExpr> targets, bool declare,
        StatementSyntax origin, List<IlStat> acc)
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
        var methodName = ma.Name.Identifier.ValueText;
        if (method.IsStatic)
            return new IlCall($"{method.ContainingType.Name}.{methodName}",
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
        InvocationExpressionSyntax invocation, StatementSyntax origin,
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

    // legacy EmitObjectInitializer / EmitRefTypeTable の写像
    private IlExpr? BuildObjectInitializerExpr(SemanticModel model,
        IlExpr ctor, InitializerExpressionSyntax initializer)
    {
        var stats = new List<IlStat> { new IlLocal("__tcs_init", ctor) };
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
            var init = new IlVar("__tcs_init");
            stats.Add(model.GetSymbolInfo(name).Symbol is IPropertySymbol prop
                    && IsCustomProperty(prop)
                ? new IlCallStat(new IlInvoke(init,
                    $"set_{name.Identifier.ValueText}", [value]))
                : new IlAssign(new IlField(init, name.Identifier.ValueText),
                    value));
        }
        stats.Add(new IlReturn(new IlVar("__tcs_init")));
        return new IlIife([.. stats]);
    }

    private IlExpr? BuildRefTypeTable(SemanticModel model,
        InitializerExpressionSyntax? initializer)
    {
        if (initializer == null) return new IlTable([]);
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
                name.Identifier.ValueText));
        }
        return new IlTable([.. entries]);
    }
}
