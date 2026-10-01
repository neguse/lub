using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    // Lua 予約語と同名のローカル束縛を写した tree / model に差し替える
    // (LuaLocalRenamer)。同じ tree を複数回 Visit する (top-level 文の 2 pass)
    // ので結果を覚える。
    private readonly Dictionary<SyntaxTree, (CSharpCompilation Compilation,
        SemanticModel Model, SyntaxTree Tree)> _renamedTrees = [];

    private (CSharpCompilation Compilation, SemanticModel Model, SyntaxTree Tree)
        RenameKeywordLocals(Compilation compilation, SemanticModel model,
            SyntaxTree tree)
    {
        if (_renamedTrees.TryGetValue(tree, out var cached)) return cached;
        var result = LuaLocalRenamer.Apply((CSharpCompilation)compilation, model, tree);
        _renamedTrees[tree] = result;
        return result;
    }

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
}
