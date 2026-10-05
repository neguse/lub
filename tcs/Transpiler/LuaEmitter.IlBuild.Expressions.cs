using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TinyCs;

// IL builder の式構築。legacy VisitExpression / VisitInvocation /
// VisitMemberAccess の意味決定を写像し、未対応は null (method fallback)。
public partial class LuaEmitter
{
    // 式構築の入口。T → T? の暗黙変換 (Roslyn の ConvertedType) をここで
    // 一元的に IlNullableWrap に写す (代入 / 引数 / return / lifted operand)
    private IlExpr? BuildExpr(SemanticModel model, ExpressionSyntax expr)
    {
        var built = BuildExprCore(model, expr);
        return built == null ? null : ApplyNullableConversion(model, expr, built);
    }

    private IlExpr? BuildExprCore(SemanticModel model, ExpressionSyntax expr)
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
                return converted == null ? null : DefaultIl(converted);
            }
            case LiteralExpressionSyntax lit:
                if (lit.IsKind(SyntaxKind.CharacterLiteralExpression)
                    && lit.Token.Value is char nonAscii && nonAscii > 127)
                    return null; // NonAsciiCharLiteral (Shared facts が診断)
                return new IlLit(VisitLiteral(lit),
                    lit.Token.Value is float or double ? "float" : null);
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
            case AssignmentExpressionSyntax assignExpr:
            {
                // 式位置の代入 (`(i = y) >= 0`、`arr[x = 1]`): 文として代入し
                // 代入後の左辺を値にする IIFE
                if (IsCustomPropertyTarget(model, assignExpr.Left))
                    return BuildPropAssignExpr(model, assignExpr);
                if (NeedsLoweredAssign(model, assignExpr))
                    return BuildLoweredAssign(model, assignExpr);
                var assignStats = new List<IlStat>();
                if (!BuildExprStatInto(model, assignExpr, null, assignStats))
                    return null;
                var assigned = BuildExpr(model, assignExpr.Left);
                return assigned == null
                    ? null : new IlIife([.. assignStats, new IlReturn(assigned)]);
            }
            case CastExpressionSyntax cast:
            {
                // (int)'a' 等の定数 cast は畳む
                var constant = model.GetConstantValue(cast);
                if (constant.HasValue && constant.Value is int or long
                    && IsCharType(model.GetTypeInfo(cast.Expression).Type))
                    return new IlLit(Convert.ToString(constant.Value,
                        System.Globalization.CultureInfo.InvariantCulture)!);
                // char ↔ int の cast は恒等 (char は整数 code unit)。
                // (char)f の float → char は (int)f と同じ truncation
                if (IsFloatToIntCast(model, cast)
                    || (IsCharType(model.GetTypeInfo(cast.Type).Type)
                        && IsFloatingType(model.GetTypeInfo(cast.Expression).Type)))
                {
                    var f = BuildExpr(model, cast.Expression);
                    return f == null ? null : new IlCall("__tcs_trunc", [f]);
                }
                // (int?)x: 明示の T → T? 変換
                if (IsNullableValueType(model.GetTypeInfo(cast.Type).Type)
                    && !IsNullableValueType(model.GetTypeInfo(cast.Expression).Type)
                    && model.GetTypeInfo(cast.Expression).Type is { } castInner)
                {
                    var wrapped = BuildExpr(model, cast.Expression);
                    return wrapped == null
                        ? null : new IlNullableWrap(wrapped, castInner.ToDisplayString());
                }
                // user class / record への downcast は IlCast で明示する (C
                // backend の実行時 check 点。upcast・同型は透過)
                if (model.GetTypeInfo(cast.Type).Type is INamedTypeSymbol
                        { TypeKind: TypeKind.Class } castTarget
                    && IsUserDeclaredType(castTarget)
                    && model.GetTypeInfo(cast.Expression).Type is INamedTypeSymbol castSource
                    && !SymbolEqualityComparer.Default.Equals(castTarget, castSource)
                    && !IsDerivedFrom(castSource, castTarget))
                {
                    var inner = BuildExpr(model, cast.Expression);
                    return inner == null
                        ? null : new IlCast(inner, TypeName(castTarget));
                }
                var value = BuildExpr(model, cast.Expression);
                var target = model.GetTypeInfo(cast.Type).Type;
                if (value == null || target == null) return value;
                if (target.SpecialType is SpecialType.System_Int32
                    or SpecialType.System_Single or SpecialType.System_Double)
                    return new IlNumericConvert(value,
                        target.SpecialType == SpecialType.System_Int32 ? "int" : "float");
                return target.IsReferenceType || target.SpecialType == SpecialType.System_Boolean
                    || target.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                    ? new IlRefCast(value, target.ToDisplayString()) : value;
            }
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
                if (receiverType?.SpecialType == SpecialType.System_String)
                {
                    // s[i] は整数 code unit (byte)
                    return new IlCall("string.byte",
                        [recv, new IlBin(IlBinOp.AddNum, index, new IlLit("1"))]);
                }
                var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";
                var plusOne = IsListType(typeDef)
                    || receiverType is IArrayTypeSymbol;
                return new IlIndex(recv, index, plusOne);
            }
            case DefaultExpressionSyntax def:
            {
                return DefaultIl(model.GetTypeInfo(def).Type);
            }
            case SwitchExpressionSyntax switchExpr:
                return BuildSwitchExpression(model, switchExpr);
            case IsPatternExpressionSyntax isPattern:
                return BuildIsPattern(model, isPattern);
            case ConditionalAccessExpressionSyntax condAccess:
                return BuildConditionalAccess(model, condAccess);
            case SimpleLambdaExpressionSyntax or
                ParenthesizedLambdaExpressionSyntax:
                return BuildLambda(model, expr);
            case ArrayCreationExpressionSyntax arr:
            {
                if (arr.Initializer == null)
                {
                    var elemSymbol = (model.GetTypeInfo(arr).Type
                        as IArrayTypeSymbol)?.ElementType;
                    var elemType = elemSymbol?.ToDisplayString();
                    // jagged (new T[n][]) も外側の長さ n で確保する
                    var sizeExpr = arr.Type.RankSpecifiers.Count >= 1
                        && arr.Type.RankSpecifiers[0].Sizes.Count == 1
                        && arr.Type.RankSpecifiers[0].Sizes[0]
                            is not OmittedArraySizeExpressionSyntax
                        ? arr.Type.RankSpecifiers[0].Sizes[0] : null;
                    if (elemType != null && sizeExpr != null
                        && BuildExpr(model, sizeExpr) is { } len)
                        return new IlNewArray(elemType, len, DefaultIl(elemSymbol));
                    return new IlTable([], elemType, IsArray: true);
                }
                return BuildArrayItems(model, arr.Initializer,
                    (model.GetTypeInfo(arr).Type as IArrayTypeSymbol)
                        ?.ElementType.ToDisplayString());
            }
            case ImplicitArrayCreationExpressionSyntax implArr:
                return BuildArrayItems(model, implArr.Initializer,
                    (model.GetTypeInfo(implArr).Type as IArrayTypeSymbol)
                        ?.ElementType.ToDisplayString());
            case WithExpressionSyntax withExpr:
                return BuildWithExpr(model, withExpr);
            case MemberBindingExpressionSyntax mb:
                return new IlField(new IlVar(_condAccessVar),
                    model.GetSymbolInfo(mb).Symbol is { } mbSym
                        ? N(mbSym) : N(mb.Name.Identifier.ValueText));
            case DeclarationExpressionSyntax declaration:
                return new IlVar(VisitDeclarationExpression(declaration));
            case PredefinedTypeSyntax predefined:
                return new IlVar(ResolvePredefinedType(predefined));
            case BaseExpressionSyntax:
                return new IlVar("self");
            default:
                return null;
        }
    }

    private IlExpr? BuildIdentifier(SemanticModel model, IdentifierNameSyntax id)
    {
        var symbol = model.GetSymbolInfo(id).Symbol;
        var name = id.Identifier.ValueText;
        if (ConstLiteral(symbol) is { } constLit)
            return LitFromConst(constLit, symbol);
        switch (symbol)
        {
            case IMethodSymbol { IsStatic: true, ContainingType: not null } sm:
                // static method group = 関数値 (Lua は Class.Method がそのまま
                // 関数)。instance group は診断済み (InstanceMethodGroup)
                return new IlField(new IlVar(TypeRef(sm.ContainingType)), N(sm));
            case IMethodSymbol:
                return null;
            case IPropertySymbol custom when IsCustomProperty(custom):
                return custom.IsStatic
                    ? new IlDynCall(new IlField(
                        new IlVar(TypeRef(custom.ContainingType)), $"get_{N(custom)}"), [])
                    : IsUserStruct(custom.ContainingType)
                        ? new IlCall($"{TypeRef(custom.ContainingType)}.get_{N(custom)}",
                            [new IlVar("self")])
                        : new IlInvoke(new IlVar("self"), $"get_{N(custom)}", []);
            case IFieldSymbol { IsStatic: false }
                or IPropertySymbol { IsStatic: false }:
                return new IlField(new IlVar("self"), N(symbol));
            case IFieldSymbol { IsStatic: true, ContainingType: not null } sf:
                return new IlField(new IlVar(TypeRef(sf.ContainingType)), N(sf));
            case IPropertySymbol { IsStatic: true, ContainingType: not null } sp:
                return new IlField(new IlVar(TypeRef(sp.ContainingType)), N(sp));
            case ILocalSymbol or IParameterSymbol:
                return new IlVar(name);
            case INamedTypeSymbol named:
                return new IlVar(TypeRef(named));
            case INamespaceSymbol:
                return new IlVar(name);
            default:
                return null;
        }
    }

    private IlExpr? BuildBinary(SemanticModel model, BinaryExpressionSyntax bin)
    {
        // designation なしの `x is Type` (legacy VisitBinary の IsExpression 経路)
        if (bin.IsKind(SyntaxKind.IsExpression))
        {
            var operand = BuildExpr(model, bin.Left);
            if (operand == null) return null;
            var patternType = model.GetTypeInfo(bin.Right).Type;
            var typeRef = bin.Right is TypeSyntax typeSyntax
                ? FormatTypeReference(model, typeSyntax)
                : BuildTypeRefText(model, bin.Right);
            var check = BuildTypeCheck(operand, patternType, typeRef);
            return check == null ? null : new IlParen(check);
        }

        var left = BuildExpr(model, bin.Left);
        var right = BuildExpr(model, bin.Right);
        if (left == null || right == null) return null;
        return WritesLocalReadBy(model, bin.Left, bin.Right)
            ? SnapshotOperand(left, l => BuildBinaryOperands(model, bin, l, right))
            : BuildBinaryOperands(model, bin, left, right);
    }

    private IlExpr? BuildBinaryOperands(SemanticModel model,
        BinaryExpressionSyntax bin, IlExpr left, IlExpr right)
    {
        // user-defined operator は結果型 / operand 型に依らず静的に選ばれた
        // overload の直呼び (int を返す `/` や string operand の `+` を組み込みの
        // 整数除算 / 連結に化けさせない)
        if (TryBuildUserOperatorCall(model, bin, left, right) is { } userOp)
            return userOp;

        // record struct の ==/!= は合成値等価へ (plain table の raw == は
        // identity 比較になってしまう)
        if ((bin.IsKind(SyntaxKind.EqualsExpression)
                || bin.IsKind(SyntaxKind.NotEqualsExpression))
            && model.GetTypeInfo(bin.Left).Type is INamedTypeSymbol
                { IsRecord: true } eqType
            && IsUserStruct(eqType))
        {
            var eqCall = new IlCall($"{TypeName(eqType)}.op_Equality",
                [left, right]);
            return bin.IsKind(SyntaxKind.EqualsExpression)
                ? eqCall
                : new IlParen(new IlUn(IlUnOp.Not, eqCall));
        }

        if (TryBuildNullableBinary(model, bin, left, right) is { } nullableBin)
            return nullableBin;

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


        var isStringConcat = bin.Kind() == SyntaxKind.AddExpression &&
            (model.GetTypeInfo(bin.Left).Type?.SpecialType == SpecialType.System_String ||
             model.GetTypeInfo(bin.Right).Type?.SpecialType == SpecialType.System_String ||
             model.GetTypeInfo(bin).Type?.SpecialType == SpecialType.System_String);
        if (isStringConcat)
            return new IlBin(IlBinOp.Concat,
                WrapConcatOperand(model, bin.Left, left),
                WrapConcatOperand(model, bin.Right, right));

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

    // f32 出力の shortest round-trip 化 (il-spec §13 / 付録 A)。
    // 静的型が float/double の値の文字列化地点で __tcs_fstr を挟む
    private IlExpr WrapFloatToString(SemanticModel model,
        ExpressionSyntax src, IlExpr built) =>
        IsNullableValueType(model.GetTypeInfo(src).Type)
            ? new IlCall("__tcs_nstr", [built])
            : IsFloatingType(model.GetTypeInfo(src).Type)
                ? new IlCall("__tcs_fstr", [built])
                : IsCharType(model.GetTypeInfo(src).Type)
                    ? new IlCall("string.char", [built])
                    : new IlCall("tostring", [built]);

    // 文字列連結 (`+` / `+=`) の operand を Lua の `..` が受ける形にする。
    // float は shortest round-trip (__tcs_fstr)、bool は ToString / 補間と
    // 同じ tostring (`..` は boolean を拒否する。#22)、bool? の null は C# と
    // 同じく空文字列。string は legacy NullSafeConcatOperand の写像
    private static IlExpr WrapConcatOperand(SemanticModel model,
        ExpressionSyntax expr, IlExpr rendered)
    {
        var type = model.GetTypeInfo(expr).Type;
        // T? は C# と同じく null を空文字列に (__tcs_nstr)
        if (IsNullableValueType(type))
            return new IlCall("__tcs_nstr", [rendered]);
        if (IsFloatingType(type))
            return new IlCall("__tcs_fstr", [rendered]);
        if (IsCharType(type))
            return new IlCall("string.char", [rendered]);
        if (UnwrapNullable(type)?.SpecialType == SpecialType.System_Boolean)
            return IsNullableValueType(type)
                ? new IlCall("__tcs_nstr", [rendered])
                : new IlCall("tostring", [rendered]);
        if (type?.SpecialType != SpecialType.System_String)
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

    // legacy VisitInvocation の funnel を同じ順序で写像する
    private IlExpr? BuildInvocation(SemanticModel model,
        InvocationExpressionSyntax invocation)
    {
        // nameof(x) は C# のコンパイル時定数 (識別子名の文字列)
        if (model.GetOperation(invocation) is INameOfOperation
            && model.GetConstantValue(invocation) is { HasValue: true, Value: string nameText })
            return new IlLit(EscapeLuaString(nameText));
        // out 引数を取る経路 (user method multi-return / Dict.TryGetValue) は
        // 通常の引数構築より先に分岐する
        if (invocation.Expression is MemberAccessExpressionSyntax maEarly
            && model.GetSymbolInfo(maEarly).Symbol is IMethodSymbol earlyMethod)
        {
            if (earlyMethod.Parameters.Any(p => p.RefKind == RefKind.Out)
                && earlyMethod.DeclaringSyntaxReferences
                    .Any(r => ReferenceTrees.Contains(r.SyntaxTree)))
                return BuildRefMultiReturnExpr(model, invocation, maEarly,
                    earlyMethod);
            if (maEarly.Name.Identifier.ValueText == "TryGetValue"
                && IsDictType(model.GetTypeInfo(maEarly.Expression).Type
                    ?.OriginalDefinition.ToDisplayString() ?? ""))
                return BuildDictTryGetValue(model, invocation, maEarly,
                    earlyMethod);
            if (NumericTryParseKind(earlyMethod) is { } tryParseKind)
                return BuildNumericTryParse(model, invocation, tryParseKind);
        }

        if (invocation.ArgumentList.Arguments
            .Any(a => !a.RefKindKeyword.IsKind(SyntaxKind.None)))
            return null;
        var args = new List<IlExpr>();
        foreach (var a in invocation.ArgumentList.Arguments)
        {
            var built = BuildExpr(model, a.Expression);
            if (built == null) return null;
            args.Add(WrapStructCopy(model, a.Expression, built));
        }
        var argArr = args.ToImmutableArray();

        if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            var symbol = model.GetSymbolInfo(ma).Symbol;
            var methodName = ma.Name.Identifier.ValueText;

            // delegate 型の field / auto property の直接呼び出し `obj.F(args)`
            // (legacy と同じ `obj.f(args)`)。custom property は fallback
            if (symbol is IFieldSymbol or IPropertySymbol
                && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol
                    { MethodKind: MethodKind.DelegateInvoke })
            {
                if (symbol is IPropertySymbol delegateProp
                    && IsCustomProperty(delegateProp))
                    return null;
                var delegateRecv = BuildExpr(model, ma.Expression);
                return delegateRecv == null
                    ? null
                    : new IlDynCall(new IlField(delegateRecv, N(symbol)), argArr);
            }

            if (ma.Expression is BaseExpressionSyntax
                && symbol is IMethodSymbol baseMethod)
                return new IlCall(
                    $"{TypeRef(baseMethod.ContainingType)}.{N(baseMethod)}",
                    [new IlVar("self"), .. argArr]);

            if (symbol is IMethodSymbol { IsStatic: true } staticFacade
                && IsTinySystemFacade(staticFacade.ContainingType))
                return new IlCall(
                    $"TinySystem.{staticFacade.ContainingType.Name}.{methodName}", argArr);

            if (symbol is IMethodSymbol collectionMethod
                && TryBuildCollectionCall(model, ma, collectionMethod,
                    methodName, argArr, out var collectionResult))
                return collectionResult;

            if (methodName == "GetValueOrDefault" && symbol is IMethodSymbol gvd
                && gvd.ContainingType.OriginalDefinition.SpecialType
                    == SpecialType.System_Nullable_T)
            {
                var obj = BuildExpr(model, ma.Expression);
                if (obj == null) return null;
                var underlying = ((INamedTypeSymbol)gvd.ContainingType)
                    .TypeArguments[0];
                if (argArr.Length == 0)
                    return new IlNullableGetOrDefault(obj, DefaultIl(underlying));
                // method 引数なので常に 1 回評価 (?? と違い遅延しない)
                return new IlIife([
                    new IlLocal("__tcs_val", obj),
                    new IlLocal("__tcs_fb", argArr[0]),
                    new IlReturn(new IlNullableGetOrDefault(
                        new IlVar("__tcs_val"), new IlVar("__tcs_fb")))]);
            }

            if (methodName == "WriteLine" && symbol is IMethodSymbol console
                && console.ContainingType.ToDisplayString() == "System.Console")
            {
                var printArgs = argArr.ToArray();
                for (var i = 0; i < printArgs.Length; i++)
                {
                    var argType = model.GetTypeInfo(invocation.ArgumentList
                        .Arguments[i].Expression).Type;
                    if (IsNullableValueType(argType))
                        printArgs[i] = new IlCall("__tcs_nstr", [printArgs[i]]);
                    else if (IsFloatingType(argType))
                        printArgs[i] = new IlCall("__tcs_fstr", [printArgs[i]]);
                    else if (IsCharType(argType))
                        printArgs[i] = new IlCall("string.char", [printArgs[i]]);
                }
                return new IlCall("print", [.. printArgs]);
            }

            if (symbol is IMethodSymbol { IsStatic: true } charMethod
                && charMethod.ContainingType.SpecialType == SpecialType.System_Char)
                return new IlCall($"Char.{methodName}", argArr);

            if (symbol is IMethodSymbol mathMethod
                && mathMethod.ContainingType.ToDisplayString() == "System.Math")
            {
                var luaName = methodName == "Ceiling" ? "Ceil" : methodName;
                return new IlCall($"Math.{luaName}", argArr);
            }

            if (IsEnvironmentGetEnv(symbol))
                return new IlCall("os.getenv", argArr);
            if (NumericParseKind(symbol) is { } parseKind)
                return parseKind == "int"
                    ? new IlCall("math.tointeger", [new IlCall("tonumber", argArr)])
                    : new IlCall("tonumber", argArr);

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
                if (TryBuildStringCall(recvStr, methodName,
                        WrapCharArgs(model, invocation.ArgumentList, argArr)) is { } strCall)
                    return strCall;
            }

            if (methodName == "ToString")
            {
                var recvAny = BuildExpr(model, ma.Expression);
                return recvAny == null
                    ? null : WrapFloatToString(model, ma.Expression, recvAny);
            }

            if (symbol is IMethodSymbol { IsExtensionMethod: true } ext)
            {
                // user 定義の拡張メソッド: 静的呼び出し (receiver が第 1 引数、
                // 値型 receiver は by-value copy)。BCL の拡張は API facts が診断
                var reduced = ext.ReducedFrom ?? ext;
                if (!reduced.Locations.Any(l => l.IsInSource)) return null;
                var extRecv = BuildExpr(model, ma.Expression);
                if (extRecv == null) return null;
                return new IlCall($"{TypeRef(reduced.ContainingType)}.{N(reduced)}",
                    [WrapStructCopy(model, ma.Expression, extRecv), .. argArr]);
            }

            if (symbol is IMethodSymbol { IsStatic: false } instMethod)
            {
                var recv = BuildExpr(model, ma.Expression);
                if (recv == null) return null;
                // struct は metatable が無いので自由関数を静的ディスパッチ
                if (IsUserStruct(model.GetTypeInfo(ma.Expression).Type))
                    return new IlCall(
                        $"{TypeRef(instMethod.ContainingType)}.{N(instMethod)}",
                        [StructReceiverArg(model, ma.Expression, recv),
                         .. argArr]);
                return new IlInvoke(recv, N(instMethod), argArr);
            }

            if (symbol is IMethodSymbol { IsStatic: true } staticMethod)
            {
                // legacy 末尾: VisitMemberAccess default 経由の `Type.Method(args)`
                var recv = BuildExpr(model, ma.Expression);
                return recv == null
                    ? null
                    : new IlDynCall(new IlField(recv, N(staticMethod)), argArr);
            }
            return null;
        }

        if (invocation.Expression is IdentifierNameSyntax idCallee)
        {
            var symbol = model.GetSymbolInfo(idCallee).Symbol;
            var name = idCallee.Identifier.ValueText;
            if (symbol is IMethodSymbol { ContainingType: not null } method)
                return method.IsStatic
                    ? new IlCall($"{TypeRef(method.ContainingType)}.{N(method)}", argArr)
                    : IsUserStruct(method.ContainingType)
                        ? new IlCall($"{TypeRef(method.ContainingType)}.{N(method)}",
                            [new IlVar("self"), .. argArr])
                        : new IlInvoke(new IlVar("self"), N(method), argArr);
            if (symbol is ILocalSymbol or IParameterSymbol)
                return new IlDynCall(new IlVar(name), argArr);
            // 非修飾の delegate field / auto property 呼び出し `F(args)`
            if (symbol is IFieldSymbol or IPropertySymbol
                && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol
                    { MethodKind: MethodKind.DelegateInvoke })
            {
                if (symbol is IPropertySymbol delegateProp
                    && IsCustomProperty(delegateProp))
                    return null;
                IlExpr owner = symbol.IsStatic
                    ? new IlVar(TypeRef(symbol.ContainingType))
                    : new IlVar("self");
                return new IlDynCall(new IlField(owner, N(symbol)), argArr);
            }
            return null;
        }

        // 一般 callee 式 (fs[0]() 等) — legacy の `{expr}({args})` と同型
        var callee = BuildExpr(model, invocation.Expression);
        return callee == null ? null : new IlDynCall(callee, argArr);
    }

    // legacy VisitMemberAccess の写像
    private IlExpr? BuildMemberAccess(SemanticModel model,
        MemberAccessExpressionSyntax ma)
    {
        var symbol = model.GetSymbolInfo(ma).Symbol;
        var member = ma.Name.Identifier.ValueText;

        if (symbol is IFieldSymbol { IsStatic: true } facadeField
            && IsTinySystemFacade(facadeField.ContainingType))
            return new IlField(new IlVar($"TinySystem.{facadeField.ContainingType.Name}"), member);
        if (symbol is IPropertySymbol { IsStatic: true } facadeProp
            && IsTinySystemFacade(facadeProp.ContainingType))
            return new IlField(new IlVar($"TinySystem.{facadeProp.ContainingType.Name}"), member);

        // 入れ子の型参照 (Lub.Gfx) — 参照専用型は小文字パスで平らに置く
        if (symbol is INamedTypeSymbol namedType)
            return new IlVar(TypeRef(namedType));
        if (ConstLiteral(symbol) is { } constLit)
            return LitFromConst(constLit, symbol);
        // Rune.Value: utf8.codes の値は codepoint 整数そのもの
        if (IsRuneValue(symbol))
            return BuildExpr(model, ma.Expression);

        if (symbol is IPropertySymbol propSym)
        {
            var receiverType = model.GetTypeInfo(ma.Expression).Type;
            var typeDef = receiverType?.OriginalDefinition.ToDisplayString() ?? "";
            var obj = BuildExpr(model, ma.Expression);
            if (obj == null) return null;

            if (receiverType?.OriginalDefinition.SpecialType
                == SpecialType.System_Nullable_T)
            {
                if (member == "HasValue") return new IlNullableHasValue(obj);
                if (member == "Value") return new IlNullableValue(obj);
            }
            if (member == "Count" && (IsListType(typeDef) || IsDictType(typeDef)
                    || IsDictCollectionType(typeDef)))
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
            if (IsCustomProperty(propSym))
                return propSym.IsStatic
                    ? new IlDynCall(new IlField(
                        new IlVar(TypeRef(propSym.ContainingType)),
                        $"get_{N(propSym)}"), [])
                    : IsUserStruct(model.GetTypeInfo(ma.Expression).Type)
                        ? new IlCall(
                            $"{TypeRef(propSym.ContainingType)}.get_{N(propSym)}",
                            [StructReceiverArg(model, ma.Expression, obj)])
                        : new IlInvoke(obj, $"get_{N(propSym)}", []);
            return new IlField(obj, N(propSym));
        }

        if (symbol is IFieldSymbol fieldSym)
        {
            var obj = BuildExpr(model, ma.Expression);
            return obj == null ? null : new IlField(obj, N(fieldSym));
        }

        if (symbol is IMethodSymbol { IsStatic: true, ContainingType: not null } smg)
            return new IlField(new IlVar(TypeName(smg.ContainingType)), smg.Name);

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

        string? TypeArg(int i) =>
            typeSymbol is INamedTypeSymbol { TypeArguments.Length: > 0 } named
            && named.TypeArguments.Length > i
                ? named.TypeArguments[i].ToDisplayString() : null;
        if (IsListType(typeDef))
        {
            if (initializer == null) return new IlTable([], TypeArg(0));
            var items = new List<IlTableEntry>();
            foreach (var e in initializer.Expressions)
            {
                var built = BuildExpr(model, e);
                if (built == null) return null;
                items.Add(new IlTableEntry(null, built));
            }
            return new IlTable([.. items], TypeArg(0));
        }
        if (IsDictType(typeDef))
        {
            if (initializer == null)
                return new IlTable([], TypeArg(1), TypeArg(0));
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
                else if (e is AssignmentExpressionSyntax
                    {
                        Left: ImplicitElementAccessSyntax
                            { ArgumentList.Arguments.Count: 1 } indexInit
                    } indexAssign)
                {
                    // indexer initializer: { ["k"] = v } (legacy と同じ [k] = v 項)
                    var key = BuildExpr(model,
                        indexInit.ArgumentList.Arguments[0].Expression);
                    var value = BuildExpr(model, indexAssign.Right);
                    if (key == null || value == null) return null;
                    entries.Add(new IlTableEntry(key, value));
                }
                else
                {
                    return null;
                }
            }
            return new IlTable([.. entries], TypeArg(1), TypeArg(0));
        }

        if (typeSymbol == null) return null;
        var args = new List<IlExpr>();
        foreach (var a in argumentList ?? [])
        {
            if (!a.RefKindKeyword.IsKind(SyntaxKind.None)) return null;
            var built = BuildExpr(model, a.Expression);
            if (built == null) return null;
            // ctor 引数も by-value (il-spec §10 の引数 copy 地点)
            args.Add(WrapStructCopy(model, a.Expression, built));
        }
        if (IsReferenceOnlyType(typeSymbol))
            // ctor 引数つきは legacy が警告する経路 — fallback
            return args.Count > 0
                ? null : BuildRefTypeTable(model, initializer, typeSymbol.ToDisplayString());
        // struct の明示 ctor は S.ctor (zero 初期化 + 本文)。`new S()` は
        // ctor を通らない zero 値なので S.new のまま
        // facade 型 (TinySystem.Random) は user 型と同名でも衝突しないよう修飾
        var newName = IsTinySystemFacade(typeSymbol as INamedTypeSymbol)
            ? $"TinySystem.{typeSymbol.Name}" : TypeName(typeSymbol);
        var ctor = IsUserStruct(typeSymbol) && args.Count > 0
            ? (IlExpr)new IlCall($"{TypeName(typeSymbol)}.ctor", [.. args])
            : new IlNewObj(newName, [.. args]);
        return initializer != null
            ? BuildObjectInitializerExpr(model, ctor, initializer)
            : ctor;
    }

}
