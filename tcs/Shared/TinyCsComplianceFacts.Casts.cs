using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// object / interface / 型引数の値を具体的な型へ戻す cast と型 test は、3 経路で同じ判定ができる型だけに許す。
// Lua は型引数を消し、List / 配列 / Dictionary は型を持たない table、delegate は関数、
// char / byte / enum は int と同じ値なので、実行時に見分けられない
public static partial class TinyCsComplianceFacts
{
    private static bool HasRuntimeIdentity(ITypeSymbol type, Func<SyntaxTree, bool>? isReferenceTree) =>
        type.SpecialType is SpecialType.System_Object or SpecialType.System_String
            or SpecialType.System_Boolean or SpecialType.System_Int32 or SpecialType.System_Single
        || (type is INamedTypeSymbol { IsGenericType: false, TypeKind: TypeKind.Class or TypeKind.Interface }
            && type.DeclaringSyntaxReferences.Length > 0
            && !(isReferenceTree != null
                && type.DeclaringSyntaxReferences.Any(r => isReferenceTree(r.SyntaxTree))));

    // 値を実行時に調べる変換か (型が静的に分かる変換と数値の変換は調べない)
    private static bool IsRuntimeTest(SemanticModel model, ITypeSymbol source, ITypeSymbol target) =>
        (source.IsReferenceType || source is ITypeParameterSymbol)
        && !model.Compilation.ClassifyConversion(source, target).IsImplicit;

    private static bool TryGetUnverifiableCast(SyntaxNode node, SemanticModel model,
        Func<SyntaxTree, bool>? isReferenceTree, out string syntaxName)
    {
        syntaxName = "";
        var (source, target, name) = node switch
        {
            CastExpressionSyntax cast => (model.GetTypeInfo(cast.Expression).Type,
                model.GetTypeInfo(cast.Type).Type, "UnverifiableCast"),
            _ when TypeTestOperands(node, model) is ({ } input, { } type) => (input, type, "UnverifiableTypeTest"),
            _ => (null, null, ""),
        };
        if (source == null || target == null || target.TypeKind == TypeKind.Error
            || !IsRuntimeTest(model, source, target) || HasRuntimeIdentity(target, isReferenceTree))
            return false;
        syntaxName = $"{name}({target.ToDisplayString()})";
        return true;
    }

    // char / byte / enum などを object に入れる。Lua と C は int と同じ値にしてしまい、取り出しも文字列化も .NET と揃わない
    private static bool TryGetValueBoxing(SyntaxNode node, SemanticModel model, out string syntaxName)
    {
        syntaxName = "";
        // 括弧と member access は外側の式が変換を受ける
        if (node is not ExpressionSyntax expr || expr.Parent is ParenthesizedExpressionSyntax
            || (expr.Parent is MemberAccessExpressionSyntax outer && outer.Name == expr))
            return false;
        var info = model.GetTypeInfo(expr);
        var (source, target) = node is CastExpressionSyntax cast
            ? (model.GetTypeInfo(cast.Expression).Type, info.Type)
            : (info.Type, info.ConvertedType);
        if (source is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            source = nullable.TypeArguments[0];
        if (source is not { IsValueType: true } || target is not { IsReferenceType: true }
            || source.TypeKind == TypeKind.Struct && source.SpecialType == SpecialType.None
            || source.SpecialType is SpecialType.System_Int32 or SpecialType.System_Single
                or SpecialType.System_Boolean)
            return false;
        // 文字列化は型のまま文字列に写すので object の値にならない
        if (IsStringConcatOperand(expr, model) || IsStringifiedArgument(expr, model))
            return false;
        syntaxName = $"ValueBoxing({source.ToDisplayString()})";
        return true;
    }
}
