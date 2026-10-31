using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// byte は i32 と同じ表現で、Lua と C は 0〜255 に丸めない (.NET は丸める)。
// 丸めが起きる式を止め、値を読むだけの用途 (`byte b = u[i];`、範囲内の定数) を残す
public static partial class TinyCsComplianceFacts
{
    private static bool IsByteNarrowing(SyntaxNode node, SemanticModel model) => node switch
    {
        CastExpressionSyntax cast => IsByteOrNullableByte(model.GetTypeInfo(cast.Type).Type)
            && !IsConstantInByteRange(model.GetConstantValue(cast.Expression)),
        PrefixUnaryExpressionSyntax unary when unary.Kind()
                is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression =>
            IsByteOrNullableByte(model.GetTypeInfo(unary.Operand).Type),
        PostfixUnaryExpressionSyntax unary when unary.Kind()
                is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression =>
            IsByteOrNullableByte(model.GetTypeInfo(unary.Operand).Type),
        AssignmentExpressionSyntax assignment when assignment.Kind()
                is not (SyntaxKind.SimpleAssignmentExpression or SyntaxKind.CoalesceAssignmentExpression) =>
            IsByteOrNullableByte(model.GetTypeInfo(assignment.Left).Type),
        _ => false,
    };

    private static bool IsByteOrNullableByte(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type?.SpecialType == SpecialType.System_Byte;
    }

    private static bool IsConstantInByteRange(Optional<object?> constant) => constant.HasValue && constant.Value switch
    {
        byte => true,
        sbyte v => v >= 0,
        short v => v is >= 0 and <= 255,
        ushort v => v <= 255,
        int v => v is >= 0 and <= 255,
        uint v => v <= 255,
        long v => v is >= 0 and <= 255,
        ulong v => v <= 255,
        char v => v <= 255,
        _ => false,
    };
}
