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

    // source assembly 内で simple 名が重複する型名 (--ref の型は除く)。
    // LuaLocalRenamer が tree ごとに compilation を作り直すが型の集合は
    // 変わらないので、最初に見た assembly で一度だけ数える。
    private HashSet<string>? _collidingTypeNames;

    /// <summary>型の Lua global 名。namespace は透過で simple 名を使い、
    /// 別 namespace に同名の型がある場合だけ namespace 修飾名
    /// (`A.Color` → `A_Color`) にして上書きを避ける。</summary>
    internal string TypeName(ITypeSymbol type)
    {
        // metadata の型 (BCL / TinySystem) は runtime の名前で呼ぶので写さない
        if (type is not INamedTypeSymbol named
            || named.DeclaringSyntaxReferences.Length == 0)
            return type.Name;
        _collidingTypeNames ??= CollectCollidingTypeNames(named.ContainingAssembly);
        if (!_collidingTypeNames.Contains(named.Name)
            || named.ContainingNamespace is not { IsGlobalNamespace: false } ns)
            return named.Name;
        return ns.ToDisplayString().Replace('.', '_') + "_" + named.Name;
    }

    private HashSet<string> CollectCollidingTypeNames(IAssemblySymbol? assembly)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var colliding = new HashSet<string>(StringComparer.Ordinal);
        if (assembly == null) return colliding;
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            foreach (var member in stack.Pop().GetMembers())
            {
                if (member is INamespaceSymbol child)
                    stack.Push(child);
                else if (member is INamedTypeSymbol t
                    && t.DeclaringSyntaxReferences.Length > 0
                    && !IsReferenceOnlyType(t)
                    && !seen.Add(t.Name))
                    colliding.Add(t.Name);
            }
        }
        return colliding;
    }

    // C# の simple 名だけでなく、emit が裸で参照する namespace 修飾名も
    // ローカル束縛と衝突させない。--ref 型の除外・名前の写像は TypeName と共通。
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
                    && type.DeclaringSyntaxReferences.Length > 0
                    && !IsReferenceOnlyType(type))
                    names.Add(TypeName(type));
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
