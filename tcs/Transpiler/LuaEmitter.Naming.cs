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
    /// <see cref="TypeName(ITypeSymbol)"/>。</summary>
    private string TypeRef(INamedTypeSymbol? type) =>
        type == null ? "" :
        IsReferenceOnlyType(type) ? LuaNaming.RefTypePath(type) : TypeName(type);

    /// <summary>型の Lua global 名。namespace の中の型は namespace 修飾名
    /// (`A.Color` → `A_Color`、`Game.Gfx.Color` → `Game_Gfx_Color`)、namespace に
    /// 属さない型は simple 名。他の型の有無に左右されない。</summary>
    internal string TypeName(ITypeSymbol type)
    {
        // metadata の型 (BCL / TinySystem) は runtime の名前で呼ぶので写さない
        if (type is not INamedTypeSymbol named
            || named.DeclaringSyntaxReferences.Length == 0)
            return type.Name;
        var parts = new List<string> { named.Name };
        // ToDisplayString は keyword の namespace を `@event` と逃がすので Name をつなぐ
        for (var ns = named.ContainingNamespace; ns is { IsGlobalNamespace: false };
            ns = ns.ContainingNamespace)
            parts.Add(ns.Name);
        parts.Reverse();
        return string.Join("_", parts);
    }

    // C# の simple 名だけでなく、emit が裸で参照する namespace 修飾名と
    // --ref 型のパスの先頭 (lub.gfx の lub) もローカル束縛と衝突させない。
    internal HashSet<string> ReservedTypeNames(IAssemblySymbol assembly)
    {
        var names = new HashSet<string>(assembly.TypeNames, StringComparer.Ordinal);
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            foreach (var member in stack.Pop().GetMembers())
            {
                if (member is INamespaceSymbol child)
                    stack.Push(child);
                else if (member is INamedTypeSymbol type
                    && type.DeclaringSyntaxReferences.Length > 0)
                    names.Add(IsReferenceOnlyType(type)
                        ? LuaNaming.RefTypePath(type).Split('.')[0]
                        : TypeName(type));
            }
        }
        return names;
    }

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
        var result = LuaLocalRenamer.Apply((CSharpCompilation)compilation, model, tree,
            ReservedTypeNames(compilation.Assembly));
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
            char c => ((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture),
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
