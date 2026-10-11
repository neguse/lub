using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// Dictionary<float, V> の key。Lua table は NaN を key にできない (代入は
// `table index is NaN`、読みは常に nil)。.NET の EqualityComparer<float>.Default
// は NaN 同士を等しいとみなすので、float の key は runtime の Dict.FloatKey で
// NaN を 1 つの sentinel key に替えてから引き、列挙 (Keys / foreach) で NaN に
// 戻す。float 以外の key は従来どおり raw の table 操作
public partial class LuaEmitter
{
    private static bool HasFloatKey(ITypeSymbol? dictType) =>
        dictType is INamedTypeSymbol
        {
            TypeArguments: [{ SpecialType: SpecialType.System_Single }, _],
        } named
        && IsDictType(named.OriginalDefinition.ToDisplayString());

    // NaN でない定数の key は sentinel にならないので素通しする
    private static IlExpr DictKey(SemanticModel model, ITypeSymbol? dictType,
        ExpressionSyntax? keySyntax, IlExpr key) =>
        !HasFloatKey(dictType)
        || keySyntax != null && model.GetConstantValue(keySyntax) is { HasValue: true, Value: { } c }
            && !(c is float f && float.IsNaN(f))
            ? key
            : new IlCall("Dict.FloatKey", [key]);

    private static IlExpr DictKeys(ITypeSymbol? dictType, IlExpr dict) =>
        new IlCall(HasFloatKey(dictType) ? "Dict.FloatKeys" : "Dict.Keys", [dict]);

    // foreach の KeyValuePair.Key を sentinel から NaN に戻す
    private static IlBlock WithFloatKeyOut(ITypeSymbol? dictType, string var,
        IlBlock body)
    {
        if (!HasFloatKey(dictType)) return body;
        var key = new IlField(new IlVar(var), "Key");
        return new IlBlock([
            new IlAssign(key, new IlCall("Dict.FloatKeyOut", [key])),
            .. body.Stats]);
    }
}
