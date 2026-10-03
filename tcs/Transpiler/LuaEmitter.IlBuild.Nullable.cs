using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder の Nullable<T> (il-spec §3 / §13): T → T? の暗黙変換、null 比較、
// lifted 演算子、?? を明示ノードへ写す。Lua の nil 方言は IL に残さない。
public partial class LuaEmitter
{
    private static bool IsNullableValueType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static bool IsNullableWrapped(IlExpr built)
    {
        while (built is IlParen paren) built = paren.E;
        return built is IlNullableWrap;
    }

    // Roslyn の ConvertedType が T? で式自体が非 nullable なら IlNullableWrap
    // (T は変換先の underlying。`float? f = 0` は int ではなく float)。null /
    // default literal (Type == null) はそのまま nil
    private static IlExpr ApplyNullableConversion(SemanticModel model,
        ExpressionSyntax expr, IlExpr built)
    {
        // 括弧式は Roslyn が内側の式にも同じ ConvertedType を報告する
        // (`(x) != null`)。内側で wrap 済みなら重ねない
        if (IsNullableWrapped(built)) return built;
        var info = model.GetTypeInfo(expr);
        if (!IsNullableValueType(info.ConvertedType)
            || IsNullableValueType(info.Type)
            || info.Type == null
            || info.Type.SpecialType == SpecialType.System_Object)
            return built;
        if (info.Type is IErrorTypeSymbol) return built;
        var underlying = ((INamedTypeSymbol)info.ConvertedType!).TypeArguments[0];
        return new IlNullableWrap(built, underlying.ToDisplayString());
    }

    private static IlLiftedOp? LiftedOpFor(string op, ITypeSymbol? underlying) => op switch
    {
        "+" => IlLiftedOp.Add,
        "-" => IlLiftedOp.Sub,
        "*" => IlLiftedOp.Mul,
        "/" => IsIntegralType(underlying) ? IlLiftedOp.DivInt : IlLiftedOp.DivFloat,
        "%" => IsIntegralType(underlying) ? IlLiftedOp.RemInt : IlLiftedOp.RemFloat,
        "&" => IlLiftedOp.BitAnd,
        "|" => IlLiftedOp.BitOr,
        "^" => IlLiftedOp.BitXor,
        "<<" => IlLiftedOp.Shl,
        ">>" => IlLiftedOp.Shr,
        _ => null,
    };

    private static IlLiftedOp? LiftedOpFor(SyntaxKind kind, ITypeSymbol? underlying) =>
        kind switch
        {
            SyntaxKind.AddExpression => IlLiftedOp.Add,
            SyntaxKind.SubtractExpression => IlLiftedOp.Sub,
            SyntaxKind.MultiplyExpression => IlLiftedOp.Mul,
            SyntaxKind.DivideExpression =>
                IsIntegralType(underlying) ? IlLiftedOp.DivInt : IlLiftedOp.DivFloat,
            SyntaxKind.ModuloExpression =>
                IsIntegralType(underlying) ? IlLiftedOp.RemInt : IlLiftedOp.RemFloat,
            SyntaxKind.BitwiseAndExpression =>
                underlying?.SpecialType == SpecialType.System_Boolean
                    ? IlLiftedOp.And : IlLiftedOp.BitAnd,
            SyntaxKind.BitwiseOrExpression =>
                underlying?.SpecialType == SpecialType.System_Boolean
                    ? IlLiftedOp.Or : IlLiftedOp.BitOr,
            SyntaxKind.ExclusiveOrExpression => IlLiftedOp.BitXor,
            SyntaxKind.LeftShiftExpression => IlLiftedOp.Shl,
            SyntaxKind.RightShiftExpression => IlLiftedOp.Shr,
            SyntaxKind.EqualsExpression => IlLiftedOp.Eq,
            SyntaxKind.NotEqualsExpression => IlLiftedOp.Ne,
            SyntaxKind.LessThanExpression => IlLiftedOp.Lt,
            SyntaxKind.LessThanOrEqualExpression => IlLiftedOp.Le,
            SyntaxKind.GreaterThanExpression => IlLiftedOp.Gt,
            SyntaxKind.GreaterThanOrEqualExpression => IlLiftedOp.Ge,
            _ => null,
        };

    // ++ / -- の値: T? なら lifted Add / Sub
    private static IlExpr StepValue(SemanticModel model, ExpressionSyntax operand,
        IlExpr read, bool increment) =>
        IsNullableValueType(model.GetTypeInfo(operand).Type)
            ? new IlLiftedBin(increment ? IlLiftedOp.Add : IlLiftedOp.Sub, read,
                new IlNullableWrap(new IlLit("1"), "int"))
            : new IlBin(increment ? IlBinOp.AddNum : IlBinOp.Sub, read, new IlLit("1"));

    private static bool IsNullLiteral(ExpressionSyntax e) =>
        e is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }
        || e is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression };

    // null 比較 / ?? / lifted の二項演算。該当しなければ null (通常経路へ)
    private IlExpr? TryBuildNullableBinary(SemanticModel model,
        BinaryExpressionSyntax bin, IlExpr left, IlExpr right)
    {
        var leftType = model.GetTypeInfo(bin.Left).Type;
        var rightType = model.GetTypeInfo(bin.Right).Type;
        // 文字列連結は lifted ではない (T? operand は __tcs_nstr で文字列化)
        if (leftType?.SpecialType == SpecialType.System_String
            || rightType?.SpecialType == SpecialType.System_String
            || model.GetTypeInfo(bin).Type?.SpecialType == SpecialType.System_String)
            return null;
        var leftNullable = IsNullableValueType(leftType);
        var rightNullable = IsNullableValueType(rightType);
        var isEq = bin.IsKind(SyntaxKind.EqualsExpression);
        var isNe = bin.IsKind(SyntaxKind.NotEqualsExpression);

        // x == null / x != null (値型 nullable)
        if ((isEq || isNe) && leftNullable && IsNullLiteral(bin.Right))
            return isEq ? new IlUn(IlUnOp.Not, new IlNullableHasValue(left))
                : new IlNullableHasValue(left);
        if ((isEq || isNe) && rightNullable && IsNullLiteral(bin.Left))
            return isEq ? new IlUn(IlUnOp.Not, new IlNullableHasValue(right))
                : new IlNullableHasValue(right);

        if (bin.IsKind(SyntaxKind.CoalesceExpression))
            return leftNullable ? new IlNullableGetOrDefault(left, right) : null;

        // lifted: どちらかが T? なら両辺とも T? (BuildExpr が ConvertedType で
        // wrap 済み)。結果の基底型は Roslyn の演算結果から
        if (!leftNullable && !rightNullable) return null;
        var resultType = UnwrapNullable(model.GetTypeInfo(bin).Type);
        var underlying = resultType?.SpecialType == SpecialType.System_Boolean
            && !(isEq || isNe) && !bin.IsKind(SyntaxKind.LessThanExpression)
            && !bin.IsKind(SyntaxKind.LessThanOrEqualExpression)
            && !bin.IsKind(SyntaxKind.GreaterThanExpression)
            && !bin.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
            ? resultType
            : UnwrapNullable(leftNullable ? leftType : rightType);
        var op = LiftedOpFor(bin.Kind(), underlying);
        if (op == null) return null;
        return new IlLiftedBin(op.Value,
            IsNullableWrapped(left) || leftNullable
                ? left : new IlNullableWrap(left, leftType?.ToDisplayString() ?? "int"),
            IsNullableWrapped(right) || rightNullable
                ? right : new IlNullableWrap(right, rightType?.ToDisplayString() ?? "int"));
    }
}
