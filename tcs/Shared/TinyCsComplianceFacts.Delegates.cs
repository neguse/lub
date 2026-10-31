using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// delegate の同一性と合成はサブセット外。Lua の delegate は関数、C は変換のたびに作る closure で、
// C# の `==` と multicast の結果を揃えられない。null との比較は値の有無だけで、全経路で同じ結果になる
public static partial class TinyCsComplianceFacts
{
    private static bool TryGetDelegateOperation(SyntaxNode node, SemanticModel model,
        out string syntaxName)
    {
        syntaxName = node switch
        {
            BinaryExpressionSyntax equality
                when (equality.IsKind(SyntaxKind.EqualsExpression)
                        || equality.IsKind(SyntaxKind.NotEqualsExpression))
                    && (IsDelegateOperand(equality.Left, model) || IsDelegateOperand(equality.Right, model))
                    && !IsNullConstant(equality.Left, model) && !IsNullConstant(equality.Right, model)
                    => "DelegateEquality",
            BinaryExpressionSyntax combine
                when (combine.IsKind(SyntaxKind.AddExpression)
                        || combine.IsKind(SyntaxKind.SubtractExpression))
                    && IsDelegateType(model.GetTypeInfo(combine).Type)
                    => "DelegateCombine",
            AssignmentExpressionSyntax compound
                when (compound.IsKind(SyntaxKind.AddAssignmentExpression)
                        || compound.IsKind(SyntaxKind.SubtractAssignmentExpression))
                    && IsDelegateType(model.GetTypeInfo(compound.Left).Type)
                    => "DelegateCombine",
            _ => "",
        };
        return syntaxName.Length > 0;
    }

    // method group の operand は自身の型を持たず、変換先の型で delegate と分かる
    private static bool IsDelegateOperand(ExpressionSyntax operand, SemanticModel model)
    {
        var info = model.GetTypeInfo(operand);
        return IsDelegateType(info.Type) || IsDelegateType(info.ConvertedType);
    }

    private static bool IsDelegateType(ITypeSymbol? type) => type?.TypeKind == TypeKind.Delegate;

    private static bool IsNullConstant(ExpressionSyntax operand, SemanticModel model) =>
        model.GetConstantValue(operand) is { HasValue: true, Value: null };
}
