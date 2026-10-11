using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// 値で比べる key の Dictionary。.NET の EqualityComparer<K>.Default は struct /
// record struct (ValueType.Equals、合成 Equals) と record class (合成 Equals) の
// key を値で比べるが、Lua table は table の同一性で引くので、等しい値の別の
// table では ContainsKey / 索引が黙って外れる。C backend も同じ key を
// 受け付けない (`Dictionary key type not supported`) ので、Lua でも build を
// 止める TCS1001 にする (#185)。作る地点 (`new` と ToDictionary) で見る
public static partial class TinyCsComplianceFacts
{
    public const string ValueEqualityDictionaryKeyPrefix = "ValueEqualityDictionaryKey(";

    public static bool TryGetUnsupportedDictionaryKey(SyntaxNode node,
        SemanticModel model, out string syntaxName)
    {
        syntaxName = "";
        var created = node switch
        {
            BaseObjectCreationExpressionSyntax creation =>
                model.GetTypeInfo(creation).Type,
            InvocationExpressionSyntax invocation
                when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol
                {
                    Name: "ToDictionary",
                } toDictionary =>
                toDictionary.ReturnType,
            _ => null,
        };
        if (!IsDictType(created)
            || ((INamedTypeSymbol)created!).TypeArguments[0] is not { } key
            || !HasValueEquality(key))
            return false;
        syntaxName = $"{ValueEqualityDictionaryKeyPrefix}{key.ToDisplayString()})";
        return true;
    }

    // struct (record struct と、struct の T? も) と record class
    private static bool HasValueEquality(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
            } nullable)
            type = nullable.TypeArguments[0];
        return type is { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None }
            || type is { TypeKind: TypeKind.Class, IsRecord: true };
    }
}
