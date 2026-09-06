using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// 名前の写像 (LuaNaming) を emit の各所から引くための helper。
public partial class LuaEmitter
{
    /// <summary>member 名の Lua 側表記。</summary>
    private static string N(string csharpName) => LuaNaming.Member(csharpName);

    /// <summary>symbol の Lua 側 member 名 (enum メンバは UPPER_SNAKE)。</summary>
    private static string N(ISymbol symbol) => LuaNaming.MemberName(symbol);

    /// <summary>static アクセスの型参照。参照専用型は小文字パス、ユーザ型は
    /// そのままの型名。</summary>
    private string TypeRef(INamedTypeSymbol? type) =>
        type == null ? "" :
        IsReferenceOnlyType(type) ? LuaNaming.RefTypePath(type) : type.Name;

    /// <summary>C# の const (enum メンバ以外) は値を inline する。</summary>
    private static string? ConstLiteral(ISymbol? symbol)
    {
        if (symbol is not IFieldSymbol { HasConstantValue: true } f
            || f.ContainingType?.TypeKind == TypeKind.Enum)
            return null;
        return f.ConstantValue switch
        {
            null => "nil",
            bool b => b ? "true" : "false",
            string s => EscapeLuaString(s),
            char c => EscapeLuaString(c.ToString()),
            float x => FormatLuaNumber(x),
            double x => FormatLuaNumber(x),
            decimal x => FormatLuaNumber((double)x),
            var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "nil",
        };
    }

    private static string FormatLuaNumber(double x)
    {
        if (double.IsNaN(x)) return "(0/0)";
        if (double.IsPositiveInfinity(x)) return "math.huge";
        if (double.IsNegativeInfinity(x)) return "(-math.huge)";
        var text = x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains('e')
            ? text : text + ".0";
    }

    // 同じ型の中で写像後の名前が衝突するメンバ (`Foo` と `foo`) を警告する。
    private void WarnLuaNameCollisions(TypeDeclarationSyntax type)
    {
        var seen = new Dictionary<string, (string Name, SyntaxToken Token)>(StringComparer.Ordinal);
        foreach (var (name, token) in EnumerateMemberNames(type))
        {
            var lua = LuaNaming.Member(name);
            if (seen.TryGetValue(lua, out var prev))
            {
                if (prev.Name == name) continue; // overload は同名で衝突しない
                var loc = token.GetLocation().GetLineSpan();
                var file = string.IsNullOrEmpty(loc.Path) ? "" : loc.Path;
                Warnings.Add($"{file}({loc.StartLinePosition.Line + 1}," +
                    $"{loc.StartLinePosition.Character + 1}): naming: " +
                    $"'{prev.Name}' and '{name}' both map to Lua '{lua}'");
            }
            else
            {
                seen[lua] = (name, token);
            }
        }
    }

    private static IEnumerable<(string Name, SyntaxToken Token)> EnumerateMemberNames(
        TypeDeclarationSyntax type)
    {
        if (type is RecordDeclarationSyntax { ParameterList: not null } rec)
            foreach (var p in rec.ParameterList.Parameters)
                yield return (p.Identifier.ValueText, p.Identifier);
        foreach (var member in type.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var v in field.Declaration.Variables)
                        yield return (v.Identifier.ValueText, v.Identifier);
                    break;
                case PropertyDeclarationSyntax prop:
                    yield return (prop.Identifier.ValueText, prop.Identifier);
                    break;
                case MethodDeclarationSyntax method:
                    yield return (method.Identifier.ValueText, method.Identifier);
                    break;
            }
        }
    }
}
