using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// 文字列にできない静的型の文字列化 (ToString()、文字列連結、補間、Console.WriteLine、
// string.Join の要素)。C# は型名や record の member の一覧を書くが、Lua は table の
// アドレスを書き、C backend は受け付けない。文字列にできるのは string / 数値 / bool /
// char / enum とその T? と Rune と、ToString を override した型 (string.Join の要素を除く。
// 要素は runtime が文字列にするので override を呼べない)。object と型引数は値で決まる
public static partial class TinyCsComplianceFacts
{
    public const string UnsupportedToStringPrefix = "UnsupportedToString(";

    public static bool TryGetUnsupportedToString(SyntaxNode node, SemanticModel model,
        out string syntaxName)
    {
        syntaxName = "";
        var type = node switch
        {
            InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } call
                when model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "ToString" } =>
                ToStringReceiverType(call, model),
            InterpolationSyntax hole => model.GetTypeInfo(hole.Expression).Type,
            // source の struct は object への変換 (StructBoxing) が診断する
            ExpressionSyntax operand when IsStringConcatOperand(operand, model)
                || IsStringifiedArgument(operand, model) =>
                model.GetTypeInfo(operand).Type is { } operandType && !IsSourceStructValue(operandType)
                    ? operandType : null,
            ArgumentSyntax { Expression: var values } argument when IsJoinValues(argument, model) =>
                JoinElementType(argument, model),
            _ => null,
        };
        // Utf8Bytes の文字列化は `u.ToString()` と `u?.ToString()` だけ
        if (IsUtf8BytesType(type) && node is InvocationExpressionSyntax) return false;
        // Rune は値そのものの文字列化だけで、string.Join の要素と Rune? の `?.ToString()` はサブセットの外
        if (type == null || IsStringifiable(type)
            || (HasToStringOverride(type) && node is not ArgumentSyntax)
            || (IsRuneType(type)
                && node is not (ArgumentSyntax or InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax })))
            return false;
        syntaxName = $"{UnsupportedToStringPrefix}{type.ToDisplayString()})";
        return true;
    }

    /// <summary>source の型が `override string ToString()` を宣言しているか。</summary>
    public static bool HasToStringOverride(ITypeSymbol? type) =>
        ToStringOverride(type) != null;

    public static IMethodSymbol? ToStringOverride(ITypeSymbol? type) =>
        type?.TypeKind is TypeKind.Class or TypeKind.Struct
            ? type.GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(m =>
                m is { IsOverride: true, IsImplicitlyDeclared: false, Parameters.Length: 0,
                    DeclaringSyntaxReferences.Length: > 0 })
            : null;

    private static bool IsStringifiable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type is ITypeParameterSymbol or IErrorTypeSymbol or { TypeKind: TypeKind.Enum }
            || type.SpecialType is SpecialType.System_String or SpecialType.System_Object
                or SpecialType.System_Boolean or SpecialType.System_Char
                or >= SpecialType.System_SByte and <= SpecialType.System_Double;
    }

    private static bool IsStringConcatOperand(ExpressionSyntax operand, SemanticModel model) =>
        operand.Parent is ExpressionSyntax add
        && add.Kind() is SyntaxKind.AddExpression or SyntaxKind.AddAssignmentExpression
        && model.GetTypeInfo(add).Type?.SpecialType == SpecialType.System_String;

    private static bool IsStringifiedArgument(ExpressionSyntax operand, SemanticModel model) =>
        operand.Parent is ArgumentSyntax { Parent.Parent: InvocationExpressionSyntax call }
        && model.GetSymbolInfo(call).Symbol is IMethodSymbol
        {
            Name: "WriteLine", ContainingType: { Name: "Console", ContainingNamespace.Name: "System" },
        };

    // `x.ToString()`、`x?.ToString()`、型の中の `ToString()` (this) の受け手の型
    private static ITypeSymbol? ToStringReceiverType(InvocationExpressionSyntax call, SemanticModel model) =>
        call.Expression switch
        {
            MemberAccessExpressionSyntax access => model.GetTypeInfo(access.Expression).Type,
            MemberBindingExpressionSyntax => call.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
                .FirstOrDefault(c => c.WhenNotNull.Span.Contains(call.Span)) is { } conditional
                && model.GetTypeInfo(conditional.Expression).Type is { } receiver
                    ? receiver is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
                        ? n.TypeArguments[0] : receiver
                    : null,
            IdentifierNameSyntax => model.GetEnclosingSymbol(call.SpanStart)?.ContainingType,
            _ => null,
        };

    private static bool IsSourceStructValue(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type is { TypeKind: TypeKind.Struct, DeclaringSyntaxReferences.Length: > 0 };
    }

    // string.Join(separator, values) の values (List<T> / T[] / Dictionary の Values など) の要素
    private static bool IsJoinValues(ArgumentSyntax argument, SemanticModel model) =>
        argument.Parent is ArgumentListSyntax { Arguments.Count: 2 } list
        && list.Arguments[1] == argument
        && list.Parent is InvocationExpressionSyntax call
        && model.GetSymbolInfo(call).Symbol is IMethodSymbol
        {
            Name: "Join", ContainingType.SpecialType: SpecialType.System_String,
        };

    private static ITypeSymbol? JoinElementType(ArgumentSyntax argument, SemanticModel model) =>
        model.GetSymbolInfo((InvocationExpressionSyntax)argument.Parent!.Parent!).Symbol is IMethodSymbol
            { IsGenericMethod: true } join
            ? join.TypeArguments[0]
            : (model.GetTypeInfo(argument.Expression).ConvertedType as IArrayTypeSymbol)?.ElementType;
}
