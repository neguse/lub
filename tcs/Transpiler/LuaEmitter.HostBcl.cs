using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// host 連携で使う BCL の判定。emit は IL builder と legacy visitor の
// 両方がここを引く。
//   Environment.GetEnvironmentVariable(s) → os.getenv(s)
//   int.Parse(s)                          → math.tointeger(tonumber(s))
//   double.Parse(s) / float.Parse(s)      → tonumber(s)
//   foreach (var r in s.EnumerateRunes()) → for _, r in utf8.codes(s)
//   r.Value (System.Text.Rune)            → r
//   s[i]                                  → string.sub(s, i + 1, i + 1)
//   (int)s[i] / (int)ch                   → string.byte(s, i + 1) / string.byte(ch)
public partial class LuaEmitter
{
    private static bool IsTypeNamed(ITypeSymbol? type, string fullName) =>
        type is INamedTypeSymbol named
        && named.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            == fullName;

    private static bool IsEnvironmentGetEnv(ISymbol? symbol) =>
        symbol is IMethodSymbol { Name: "GetEnvironmentVariable" } m
        && IsTypeNamed(m.ContainingType, "System.Environment");

    /// <summary>int.Parse → "int"、double/float.Parse → "float"、それ以外は null。</summary>
    private static string? NumericParseKind(ISymbol? symbol)
    {
        if (symbol is not IMethodSymbol { Name: "Parse", IsStatic: true } m
            || m.Parameters.Length != 1)
            return null;
        return m.ContainingType.SpecialType switch
        {
            SpecialType.System_Int32 => "int",
            SpecialType.System_Double or SpecialType.System_Single => "float",
            _ => null,
        };
    }

    private static bool IsRuneValue(ISymbol? symbol) =>
        symbol is IPropertySymbol { Name: "Value" } p
        && IsTypeNamed(p.ContainingType, "System.Text.Rune");

    /// <summary>`s.EnumerateRunes()` なら receiver の s を返す。</summary>
    private static ExpressionSyntax? TryGetEnumerateRunesReceiver(
        SemanticModel model, ExpressionSyntax expr)
    {
        if (expr is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "EnumerateRunes"
                } ma
            })
            return null;
        return model.GetTypeInfo(ma.Expression).Type?.SpecialType
            == SpecialType.System_String
            ? ma.Expression
            : null;
    }

    private static bool IsCharType(ITypeSymbol? type) =>
        type?.SpecialType == SpecialType.System_Char;

    private static bool IsStringElementAccess(SemanticModel model,
        ExpressionSyntax expr, out ExpressionSyntax receiver,
        out ExpressionSyntax index)
    {
        receiver = null!;
        index = null!;
        if (expr is not ElementAccessExpressionSyntax ea
            || ea.ArgumentList.Arguments.Count != 1
            || model.GetTypeInfo(ea.Expression).Type?.SpecialType
                != SpecialType.System_String)
            return false;
        receiver = ea.Expression;
        index = ea.ArgumentList.Arguments[0].Expression;
        return true;
    }

    /// <summary>(int)c 形の char→整数 cast か。</summary>
    private static bool IsCharToIntCast(SemanticModel model,
        CastExpressionSyntax cast)
    {
        var target = model.GetTypeInfo(cast.Type).Type;
        var operand = model.GetTypeInfo(cast.Expression).Type;
        return IsIntegralType(target) && IsCharType(operand);
    }
}
