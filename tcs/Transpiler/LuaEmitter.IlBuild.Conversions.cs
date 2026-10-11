using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder の整数 → float 変換 (il-spec §5 / §6)。暗黙変換 (代入・引数・
// return・比較の昇格) も明示ノード IlNumericConvert にする。Lua の integer は
// float と混ぜても比較・格納で丸まらないため (16777217 が 16777216f と等しく
// ならない)、変換を IL に残さないと C# と値が変わる
public partial class LuaEmitter
{
    private static IlExpr ApplyNumericConversion(SemanticModel model,
        ExpressionSyntax expr, IlExpr built)
    {
        var info = model.GetTypeInfo(expr);
        var target = IsNullableValueType(info.ConvertedType)
            ? ((INamedTypeSymbol)info.ConvertedType!).TypeArguments[0]
            : info.ConvertedType;
        if (target?.SpecialType != SpecialType.System_Single
            || !IsIntegralSource(info.Type)
            || IsFloatConverted(built)
            || IsPromotedArithmeticOperand(model, expr))
            return built;
        var constant = model.GetConstantValue(expr);
        if (constant is { HasValue: true, Value: { } integral })
            return FloatLit(integral is char c ? c
                : Convert.ToSingle(integral, CultureInfo.InvariantCulture));
        return new IlNumericConvert(built, "float");
    }

    // `(float)x` の IL。変換元が既に float (double は f32 扱い) なら恒等
    private static IlExpr NumericCastIl(IlExpr value, ITypeSymbol? source,
        ITypeSymbol target)
    {
        var toInt = target.SpecialType == SpecialType.System_Int32;
        if (!toInt && source?.SpecialType is SpecialType.System_Single
                or SpecialType.System_Double)
            return value;
        return new IlNumericConvert(value, toInt ? "int" : "float");
    }

    private static bool IsIntegralSource(ITypeSymbol? type) =>
        type?.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Char;

    private static bool IsFloatConverted(IlExpr built)
    {
        while (built is IlParen paren) built = paren.E;
        return built is IlNumericConvert { TargetType: "float" }
            || built is IlLit { Type: "float" };
    }

    // 組み込みの float 算術 (+ - * / %) の operand は、Lua / C とも演算が
    // integer を float へ変換してから計算するので、明示の変換を省く。相手の
    // operand が Lua でも float であることが前提 (float の既定値と runtime が
    // 返す float (Parse / TryParse / Sum) は float にしている)
    private static bool IsPromotedArithmeticOperand(SemanticModel model,
        ExpressionSyntax expr)
    {
        SyntaxNode parent = expr.Parent!;
        while (parent is ParenthesizedExpressionSyntax) parent = parent.Parent!;
        return parent is BinaryExpressionSyntax bin
            && bin.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression
                or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression
                or SyntaxKind.ModuloExpression
            && model.GetSymbolInfo(bin).Symbol is IMethodSymbol
                { MethodKind: MethodKind.BuiltinOperator };
    }

    // f32 の literal。負数は括弧で包む (`a - -1.0` の連結で `--` にしない)
    private static IlExpr FloatLit(float value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('E')) text += ".0";
        var lit = new IlLit(text, "float");
        return value < 0 ? new IlParen(lit) : lit;
    }
}
