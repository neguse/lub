using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// uint は i32 と同じ表現 (外部 API の facade のための型)。Lua と C は 2^31 以上の値を符号なしで扱わないので、
// 2^31 以上の定数 (UIntRange) と、結果が 0〜2^31-1 を出うる演算 (UIntArithmetic: + - * << ~ ++ -- と複合代入) を止める。
// int と enum への cast の operand の定数 (unchecked((int)0xFFFFFFFF)) と enum の member の値は 2 の補数の bit として受ける
public static partial class TinyCsComplianceFacts
{
    private static bool IsUIntArithmetic(SyntaxNode node, SemanticModel model) => node switch
    {
        BinaryExpressionSyntax binary when binary.Kind()
                is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression
                or SyntaxKind.MultiplyExpression or SyntaxKind.LeftShiftExpression =>
            IsUIntOrNullableUInt(model.GetTypeInfo(binary).Type),
        PrefixUnaryExpressionSyntax unary when unary.Kind() is SyntaxKind.BitwiseNotExpression =>
            IsUIntOrNullableUInt(model.GetTypeInfo(unary).Type),
        PrefixUnaryExpressionSyntax unary when unary.Kind()
                is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression =>
            IsUIntOrNullableUInt(model.GetTypeInfo(unary.Operand).Type),
        PostfixUnaryExpressionSyntax unary when unary.Kind()
                is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression =>
            IsUIntOrNullableUInt(model.GetTypeInfo(unary.Operand).Type),
        AssignmentExpressionSyntax assignment when assignment.Kind()
                is SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression
                or SyntaxKind.MultiplyAssignmentExpression or SyntaxKind.LeftShiftAssignmentExpression =>
            IsUIntOrNullableUInt(model.GetTypeInfo(assignment.Left).Type),
        _ => false,
    };

    private static bool IsUIntOutOfRange(SyntaxNode node, SemanticModel model)
    {
        if (node is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal) return false;
        if (model.GetConstantValue(literal) is not { HasValue: true, Value: uint value } || value < 0x80000000u) return false;
        // -2147483648 の operand
        if (literal.Parent is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression }) return false;
        if (literal.Ancestors().OfType<EnumMemberDeclarationSyntax>().Any()) return false;
        SyntaxNode at = literal;
        while (at.Parent is ParenthesizedExpressionSyntax or CheckedExpressionSyntax) at = at.Parent;
        if (at.Parent is CastExpressionSyntax cast
            && model.GetTypeInfo(cast.Type).Type is { } target
            && (target.SpecialType == SpecialType.System_Int32 || target.TypeKind == TypeKind.Enum))
            return false;
        return true;
    }

    private static bool IsUIntOrNullableUInt(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type?.SpecialType == SpecialType.System_UInt32;
    }
}
