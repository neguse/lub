using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder の式構築。legacy VisitExpression / VisitInvocation /
// VisitMemberAccess の意味決定を写像し、未対応は null (method fallback)。
public partial class LuaEmitter
{
    private IlExpr? BuildExpr(SemanticModel model, ExpressionSyntax expr)
    {
        if (expr is InvocationExpressionSyntax nameOf
            && TinyCsComplianceFacts.TryGetUnsupportedSyntax(
                nameOf, model, out _))
            return null;

        switch (expr)
        {
            case LiteralExpressionSyntax
                { RawKind: (int)SyntaxKind.DefaultLiteralExpression } defLit:
            {
                var converted = model.GetTypeInfo(defLit).ConvertedType;
                return converted == null
                    ? null : new IlLit(GetDefaultValueForType(converted));
            }
            case LiteralExpressionSyntax lit:
                return new IlLit(VisitLiteral(lit));
            case IdentifierNameSyntax id:
                return BuildIdentifier(model, id);
            case BinaryExpressionSyntax bin:
                return BuildBinary(model, bin);
            case PrefixUnaryExpressionSyntax prefix:
                return BuildPrefixUnary(model, prefix);
            case PostfixUnaryExpressionSyntax postfix:
                return BuildPostfixUnary(model, postfix);
            case ParenthesizedExpressionSyntax paren:
            {
                var inner = BuildExpr(model, paren.Expression);
                return inner == null ? null : new IlParen(inner);
            }
            case InvocationExpressionSyntax invocation:
                return BuildInvocation(model, invocation);
            case MemberAccessExpressionSyntax ma:
                return BuildMemberAccess(model, ma);
            case ObjectCreationExpressionSyntax creation:
                return BuildObjectCreation(model, creation,
                    creation.ArgumentList?.Arguments, creation.Initializer);
            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return BuildObjectCreation(model, implicitCreation,
                    implicitCreation.ArgumentList.Arguments,
                    implicitCreation.Initializer);
            case ThisExpressionSyntax:
                return new IlVar("self");
            case CastExpressionSyntax cast:
                return BuildExpr(model, cast.Expression);
            case ConditionalExpressionSyntax ternary:
            {
                var cond = BuildExpr(model, ternary.Condition);
                var t = BuildExpr(model, ternary.WhenTrue);
                var f = BuildExpr(model, ternary.WhenFalse);
                return cond == null || t == null || f == null
                    ? null : new IlTernary(cond, t, f);
            }
            case InterpolatedStringExpressionSyntax interp:
                return BuildInterpolatedString(model, interp);
            case ElementAccessExpressionSyntax elemAccess:
            {
                var recv = BuildExpr(model, elemAccess.Expression);
                var index = BuildExpr(model,
                    elemAccess.ArgumentList.Arguments[0].Expression);
                if (recv == null || index == null) return null;
                var receiverType = model.GetTypeInfo(elemAccess.Expression).Type;
                var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";
                var plusOne = IsListType(typeDef)
                    || receiverType is IArrayTypeSymbol;
                return new IlIndex(recv, index, plusOne);
            }
            case DefaultExpressionSyntax def:
            {
                var type = model.GetTypeInfo(def).Type;
                return new IlLit(type != null
                    ? GetDefaultValueForType(type) : "nil");
            }
            default:
                return null;
        }
    }

    // legacy ResolveIdentifier の写像 (bare method group と custom property は
    // fallback、未解決 symbol も安全側で fallback)
    private IlExpr? BuildIdentifier(SemanticModel model, IdentifierNameSyntax id)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        var name = id.Identifier.ValueText;
        switch (symbol)
        {
            case IMethodSymbol:
                return null;
            case IPropertySymbol custom when IsCustomProperty(custom):
                return null;
            case IFieldSymbol { IsStatic: false }
                or IPropertySymbol { IsStatic: false }:
                return new IlField(new IlVar("self"), name);
            case IFieldSymbol { IsStatic: true, ContainingType: not null } sf:
                return new IlField(new IlVar(sf.ContainingType.Name), name);
            case IPropertySymbol { IsStatic: true, ContainingType: not null } sp:
                return new IlField(new IlVar(sp.ContainingType.Name), name);
            case ILocalSymbol or IParameterSymbol:
                return new IlVar(name);
            case INamedTypeSymbol or INamespaceSymbol:
                return new IlVar(name);
            default:
                return null;
        }
    }

    private IlExpr? BuildBinary(SemanticModel model, BinaryExpressionSyntax bin)
    {
        if (bin.IsKind(SyntaxKind.IsExpression)) return null;

        var left = BuildExpr(model, bin.Left);
        var right = BuildExpr(model, bin.Right);
        if (left == null || right == null) return null;

        if (bin.IsKind(SyntaxKind.DivideExpression)
            && IsIntegralType(model.GetTypeInfo(bin).Type))
            return new IlCall("__tcs_idiv", [left, right]);
        if (bin.IsKind(SyntaxKind.ModuloExpression))
        {
            var type = model.GetTypeInfo(bin).Type;
            if (IsIntegralType(type))
                return new IlCall("__tcs_irem", [left, right]);
            if (IsFloatingType(type))
                return new IlCall("math.fmod", [left, right]);
        }

        // bool の ?? は IIFE 経路 (legacy) — fallback
        if (bin.IsKind(SyntaxKind.CoalesceExpression)
            && UnwrapNullable(model.GetTypeInfo(bin.Left).Type)?.SpecialType
                == SpecialType.System_Boolean)
            return null;

        var isStringConcat = bin.Kind() == SyntaxKind.AddExpression &&
            (model.GetTypeInfo(bin.Left).Type?.SpecialType == SpecialType.System_String ||
             model.GetTypeInfo(bin.Right).Type?.SpecialType == SpecialType.System_String ||
             model.GetTypeInfo(bin).Type?.SpecialType == SpecialType.System_String);
        if (isStringConcat)
        {
            left = WrapConcatOperand(model, bin.Left, left);
            right = WrapConcatOperand(model, bin.Right, right);
            return new IlBin(IlBinOp.Concat, left, right);
        }

        IlBinOp? op = bin.Kind() switch
        {
            SyntaxKind.AddExpression => IlBinOp.AddNum,
            SyntaxKind.SubtractExpression => IlBinOp.Sub,
            SyntaxKind.MultiplyExpression => IlBinOp.Mul,
            SyntaxKind.DivideExpression => IlBinOp.DivNum,
            SyntaxKind.ModuloExpression => IlBinOp.RemNum,
            SyntaxKind.EqualsExpression => IlBinOp.Eq,
            SyntaxKind.NotEqualsExpression => IlBinOp.Ne,
            SyntaxKind.LessThanExpression => IlBinOp.Lt,
            SyntaxKind.LessThanOrEqualExpression => IlBinOp.Le,
            SyntaxKind.GreaterThanExpression => IlBinOp.Gt,
            SyntaxKind.GreaterThanOrEqualExpression => IlBinOp.Ge,
            SyntaxKind.LogicalAndExpression => IlBinOp.And,
            SyntaxKind.LogicalOrExpression => IlBinOp.Or,
            SyntaxKind.CoalesceExpression => IlBinOp.Or,
            SyntaxKind.BitwiseAndExpression when !HasBoolOperand(model, bin) =>
                IlBinOp.BitAnd,
            SyntaxKind.BitwiseOrExpression when !HasBoolOperand(model, bin) =>
                IlBinOp.BitOr,
            SyntaxKind.ExclusiveOrExpression when !HasBoolOperand(model, bin) =>
                IlBinOp.BitXor,
            SyntaxKind.LeftShiftExpression => IlBinOp.Shl,
            SyntaxKind.RightShiftExpression => IlBinOp.Shr,
            _ => null,
        };
        return op == null ? null : new IlBin(op.Value, left, right);
    }

    // legacy NullSafeConcatOperand の写像
    private static IlExpr WrapConcatOperand(SemanticModel model,
        ExpressionSyntax expr, IlExpr rendered)
    {
        if (model.GetTypeInfo(expr).Type?.SpecialType
                != SpecialType.System_String)
            return rendered;
        var unwrapped = expr;
        while (unwrapped is ParenthesizedExpressionSyntax paren)
            unwrapped = paren.Expression;
        return unwrapped is LiteralExpressionSyntax
            or InterpolatedStringExpressionSyntax
            or BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression }
            ? rendered
            : new IlParen(new IlBin(IlBinOp.Or, rendered, new IlLit("\"\"")));
    }

    private IlExpr? BuildPrefixUnary(SemanticModel model,
        PrefixUnaryExpressionSyntax prefix)
    {
        var operand = BuildExpr(model, prefix.Operand);
        if (operand == null) return null;
        return prefix.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => new IlUn(IlUnOp.Neg, operand),
            SyntaxKind.LogicalNotExpression => new IlUn(IlUnOp.Not, operand),
            SyntaxKind.BitwiseNotExpression => new IlUn(IlUnOp.BitNot, operand),
            SyntaxKind.PreIncrementExpression =>
                new IlParen(new IlBin(IlBinOp.AddNum, operand, new IlLit("1"))),
            SyntaxKind.PreDecrementExpression =>
                new IlParen(new IlBin(IlBinOp.Sub, operand, new IlLit("1"))),
            _ => null,
        };
    }

    private IlExpr? BuildPostfixUnary(SemanticModel model,
        PostfixUnaryExpressionSyntax postfix)
    {
        var operand = BuildExpr(model, postfix.Operand);
        if (operand == null) return null;
        return postfix.Kind() switch
        {
            SyntaxKind.PostIncrementExpression =>
                new IlBin(IlBinOp.AddNum, operand, new IlLit("1")),
            SyntaxKind.PostDecrementExpression =>
                new IlBin(IlBinOp.Sub, operand, new IlLit("1")),
            SyntaxKind.SuppressNullableWarningExpression => operand,
            _ => null,
        };
    }

    // legacy VisitInvocation の funnel を同じ順序で写像する
    private IlExpr? BuildInvocation(SemanticModel model,
        InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments
            .Any(a => !a.RefKindKeyword.IsKind(SyntaxKind.None)))
            return null;
        var args = new List<IlExpr>();
        foreach (var a in invocation.ArgumentList.Arguments)
        {
            var built = BuildExpr(model, a.Expression);
            if (built == null) return null;
            args.Add(built);
        }
        var argArr = args.ToImmutableArray();

        if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            var symbol = model.GetSymbolInfo(ma).Symbol;
            var methodName = ma.Name.Identifier.ValueText;

            if (ma.Expression is BaseExpressionSyntax) return null;

            if (symbol is IMethodSymbol { IsStatic: true } staticFacade
                && IsTinySystemFacade(staticFacade.ContainingType))
                return new IlCall(
                    $"{staticFacade.ContainingType.Name}.{methodName}", argArr);

            // --ref out 引数 multi-return は fallback
            if (symbol is IMethodSymbol refMethod
                && refMethod.Parameters.Any(p => p.RefKind == RefKind.Out)
                && refMethod.DeclaringSyntaxReferences
                    .Any(r => ReferenceTrees.Contains(r.SyntaxTree)))
                return null;

            if (symbol is IMethodSymbol collectionMethod
                && TryBuildCollectionCall(model, ma, collectionMethod,
                    methodName, argArr, out var collectionResult))
                return collectionResult;

            if (methodName == "GetValueOrDefault" && symbol is IMethodSymbol gvd
                && gvd.ContainingType.OriginalDefinition.SpecialType
                    == SpecialType.System_Nullable_T)
                return null;

            if (methodName == "WriteLine" && symbol is IMethodSymbol console
                && console.ContainingType.ToDisplayString() == "System.Console")
                return new IlCall("print", argArr);

            if (symbol is IMethodSymbol mathMethod
                && mathMethod.ContainingType.ToDisplayString() == "System.Math")
            {
                var luaName = methodName == "Ceiling" ? "Ceil" : methodName;
                return new IlCall($"Math.{luaName}", argArr);
            }

            if (symbol is IMethodSymbol stringStatic
                && stringStatic.ContainingType.SpecialType
                    == SpecialType.System_String
                && methodName is "Join" or "IsNullOrEmpty")
                return new IlCall($"String.{methodName}", argArr);

            if (model.GetTypeInfo(ma.Expression).Type?.SpecialType
                == SpecialType.System_String)
            {
                var recvStr = BuildExpr(model, ma.Expression);
                if (recvStr == null) return null;
                if (TryBuildStringCall(recvStr, methodName, argArr) is { } strCall)
                    return strCall;
            }

            if (methodName == "ToString")
            {
                var recvAny = BuildExpr(model, ma.Expression);
                return recvAny == null
                    ? null : new IlCall("tostring", [recvAny]);
            }

            if (symbol is IMethodSymbol { IsExtensionMethod: true }) return null;

            if (symbol is IMethodSymbol { IsStatic: false })
            {
                var recv = BuildExpr(model, ma.Expression);
                return recv == null
                    ? null : new IlInvoke(recv, methodName, argArr);
            }

            if (symbol is IMethodSymbol { IsStatic: true })
            {
                // legacy 末尾: VisitMemberAccess default 経由の `Type.Method(args)`
                var recv = BuildExpr(model, ma.Expression);
                return recv == null
                    ? null
                    : new IlDynCall(new IlField(recv, methodName), argArr);
            }
            return null;
        }

        if (invocation.Expression is IdentifierNameSyntax idCallee)
        {
            var symbol = model.GetSymbolInfo(idCallee).Symbol;
            var name = idCallee.Identifier.ValueText;
            if (symbol is IMethodSymbol { ContainingType: not null } method)
                return method.IsStatic
                    ? new IlCall($"{method.ContainingType.Name}.{name}", argArr)
                    : new IlInvoke(new IlVar("self"), name, argArr);
            if (symbol is ILocalSymbol or IParameterSymbol)
                return new IlDynCall(new IlVar(name), argArr);
            return null;
        }

        return null;
    }

    // legacy TryMapCollectionMethod の写像 (Dict と Clear の IIFE 経路は
    // fallback)。戻り false は「この段は不一致 — funnel 続行」。
    private bool TryBuildCollectionCall(SemanticModel model,
        MemberAccessExpressionSyntax ma, IMethodSymbol methodSym,
        string methodName, ImmutableArray<IlExpr> args, out IlExpr? result)
    {
        result = null;
        var receiverType = model.GetTypeInfo(ma.Expression).Type;
        if (receiverType == null) return false;
        var typeDef = receiverType.OriginalDefinition.ToDisplayString();
        if (IsDictType(typeDef))
        {
            // Dict は Add が代入形など statement 依存 — method fallback
            result = null;
            return methodName is "Add" or "Remove" or "ContainsKey"
                or "TryGetValue" or "Clear";
        }
        if (!IsListType(typeDef))
        {
            if (methodSym.IsExtensionMethod
                && ListRuntimeMethods.Contains(methodName))
            {
                var recvExt = BuildExpr(model, ma.Expression);
                if (recvExt == null) return true; // fallback
                result = new IlCall($"List.{methodName}",
                    [recvExt, .. args]);
                return true;
            }
            return false;
        }

        var recv = BuildExpr(model, ma.Expression);
        if (recv == null) return true; // List method だが受け手未対応 → fallback
        switch (methodName)
        {
            case "Add":
                result = new IlCall("table.insert", [recv, .. args]);
                return true;
            case "Remove":
                result = new IlCall("List.Remove", [recv, .. args]);
                return true;
            case "RemoveAt":
                result = new IlCall("table.remove",
                    [recv, new IlBin(IlBinOp.AddNum, args[0], new IlLit("1"))]);
                return true;
            case "Clear":
                return true; // IIFE — fallback
            case "Sort":
                result = new IlCall("List.Sort", [recv, .. args]);
                return true;
            case "FirstOrDefault":
            case "LastOrDefault":
            {
                var predicate = args.Length > 0 ? args[0] : new IlLit("nil");
                result = new IlCall($"List.{methodName}",
                    [recv, predicate,
                     new IlLit(GetDefaultValueForType(methodSym.ReturnType))]);
                return true;
            }
        }
        if (ListRuntimeMethods.Contains(methodName))
        {
            result = new IlCall($"List.{methodName}", [recv, .. args]);
            return true;
        }
        return false;
    }

    // legacy MapStringMethodCall の写像 (default の `obj:m(...)` 形は不一致
    // として null → funnel 続行)
    private static IlExpr? TryBuildStringCall(IlExpr recv, string methodName,
        ImmutableArray<IlExpr> args) => methodName switch
    {
        "Contains" => new IlCall("String.Contains", [recv, .. args]),
        "IndexOf" => new IlCall("String.IndexOf", [recv, .. args]),
        "Replace" => new IlCall("String.Replace", [recv, .. args]),
        "StartsWith" => new IlCall("String.StartsWith", [recv, args[0]]),
        "EndsWith" => new IlCall("String.EndsWith", [recv, args[0]]),
        "Trim" => new IlCall("String.Trim", [recv]),
        "Substring" => new IlCall("String.Substring", [recv, .. args]),
        "ToUpper" => new IlCall("string.upper", [recv]),
        "ToLower" => new IlCall("string.lower", [recv]),
        "Split" => new IlCall("String.Split", [recv, .. args]),
        "ToString" => new IlCall("tostring", [recv]),
        _ => null,
    };

    // legacy VisitMemberAccess の写像
    private IlExpr? BuildMemberAccess(SemanticModel model,
        MemberAccessExpressionSyntax ma)
    {
        var symbol = model.GetSymbolInfo(ma).Symbol;
        var member = ma.Name.Identifier.ValueText;

        if (symbol is IFieldSymbol { IsStatic: true } facadeField
            && IsTinySystemFacade(facadeField.ContainingType))
            return new IlField(new IlVar(facadeField.ContainingType.Name), member);
        if (symbol is IPropertySymbol { IsStatic: true } facadeProp
            && IsTinySystemFacade(facadeProp.ContainingType))
            return new IlField(new IlVar(facadeProp.ContainingType.Name), member);

        if (symbol is IPropertySymbol propSym)
        {
            var receiverType = model.GetTypeInfo(ma.Expression).Type;
            var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";
            var obj = BuildExpr(model, ma.Expression);
            if (obj == null) return null;

            if (receiverType?.OriginalDefinition.SpecialType
                == SpecialType.System_Nullable_T)
            {
                if (member == "HasValue")
                    return new IlParen(new IlBin(IlBinOp.Ne, obj,
                        new IlLit("nil")));
                if (member == "Value") return obj;
            }
            if (member == "Count" && (IsListType(typeDef) || IsDictType(typeDef)))
                return IsDictType(typeDef)
                    ? new IlCall("Dict.Count", [obj])
                    : new IlLen(obj);
            if (member == "Keys" && IsDictType(typeDef))
                return new IlCall("Dict.Keys", [obj]);
            if (member == "Values" && IsDictType(typeDef))
                return new IlCall("Dict.Values", [obj]);
            if (member == "Length"
                && (receiverType?.SpecialType == SpecialType.System_String
                    || receiverType is IArrayTypeSymbol))
                return new IlLen(obj);
            if (IsCustomProperty(propSym)) return null;
            return new IlField(obj, member);
        }

        if (symbol is IFieldSymbol)
        {
            var obj = BuildExpr(model, ma.Expression);
            return obj == null ? null : new IlField(obj, member);
        }

        return null;
    }

    private IlExpr? BuildObjectCreation(SemanticModel model,
        ExpressionSyntax creation,
        IEnumerable<ArgumentSyntax>? argumentList,
        InitializerExpressionSyntax? initializer)
    {
        var typeSymbol = creation is ObjectCreationExpressionSyntax
            ? model.GetTypeInfo(creation).Type
            : model.GetTypeInfo(creation).ConvertedType;
        var typeDef = typeSymbol?.OriginalDefinition.ToDisplayString() ?? "";

        if (IsListType(typeDef))
        {
            if (initializer == null) return new IlTable([]);
            var items = new List<IlTableEntry>();
            foreach (var e in initializer.Expressions)
            {
                var built = BuildExpr(model, e);
                if (built == null) return null;
                items.Add(new IlTableEntry(null, built));
            }
            return new IlTable([.. items]);
        }
        if (IsDictType(typeDef))
        {
            if (initializer == null) return new IlTable([]);
            var entries = new List<IlTableEntry>();
            foreach (var e in initializer.Expressions)
            {
                if (e is InitializerExpressionSyntax
                    { Expressions.Count: 2 } kvInit)
                {
                    var key = BuildExpr(model, kvInit.Expressions[0]);
                    var value = BuildExpr(model, kvInit.Expressions[1]);
                    if (key == null || value == null) return null;
                    entries.Add(new IlTableEntry(key, value));
                }
                else
                {
                    return null; // indexer initializer 等は fallback
                }
            }
            return new IlTable([.. entries]);
        }

        if (typeSymbol == null || IsReferenceOnlyType(typeSymbol)) return null;
        if (initializer != null) return null;
        var args = new List<IlExpr>();
        foreach (var a in argumentList ?? [])
        {
            if (!a.RefKindKeyword.IsKind(SyntaxKind.None)) return null;
            var built = BuildExpr(model, a.Expression);
            if (built == null) return null;
            args.Add(built);
        }
        return new IlNewObj(typeSymbol.Name, [.. args]);
    }

    // legacy VisitInterpolatedString の写像 (alignment はリテラルのみ対応)
    private IlExpr? BuildInterpolatedString(SemanticModel model,
        InterpolatedStringExpressionSyntax interp)
    {
        var parts = new List<IlExpr>();
        foreach (var content in interp.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                    parts.Add(new IlLit(EscapeLuaString(text.TextToken.ValueText
                        .Replace("{{", "{", StringComparison.Ordinal)
                        .Replace("}}", "}", StringComparison.Ordinal))));
                    break;
                case InterpolationSyntax hole:
                {
                    var inner = BuildExpr(model, hole.Expression);
                    if (inner == null) return null;
                    IlExpr rendered;
                    if (hole.FormatClause != null)
                    {
                        var luaFmt = ConvertFormatSpecifier(
                            hole.FormatClause.FormatStringToken.Text);
                        rendered = new IlCall("string.format",
                            [new IlLit($"\"{luaFmt}\""), inner]);
                    }
                    else
                    {
                        rendered = new IlCall("tostring", [inner]);
                    }
                    if (hole.AlignmentClause != null)
                    {
                        if (hole.AlignmentClause.Value is not
                            (LiteralExpressionSyntax
                             or PrefixUnaryExpressionSyntax
                             {
                                 RawKind: (int)SyntaxKind.UnaryMinusExpression,
                                 Operand: LiteralExpressionSyntax
                             }))
                            return null;
                        var align = hole.AlignmentClause.Value.ToString();
                        rendered = new IlCall("string.format",
                            [new IlLit($"\"%{align}s\""), rendered]);
                    }
                    parts.Add(rendered);
                    break;
                }
                default:
                    return null;
            }
        }
        if (parts.Count == 0) return null;
        var result = parts[0];
        for (var i = 1; i < parts.Count; i++)
            result = new IlBin(IlBinOp.Concat, result, parts[i]);
        return result;
    }
}
