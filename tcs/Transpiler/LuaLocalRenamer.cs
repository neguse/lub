using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

/// <summary>
/// Lua 予約語 (`local` `end` `nil` ...) と同名のローカル束縛 (local 変数 /
/// parameter / foreach 変数 / pattern designation / lambda parameter) を
/// emit 前に安全な識別子へ写す。member は LuaNaming.Member が `end_` に写すが
/// (`self.end_`)、ローカルは Lua の裸の識別子になるため構文エラーになる。
/// ユーザ型と同名のローカル束縛も写す。emit は型を裸の型名で参照する
/// (`V.new(...)` / operator の `V.__add(a, b)` / 型内の static member) ので、
/// 同名のローカルが Lua でそれを隠す。namespace 衝突時の修飾名も対象。
///
/// 写し方: `x` → `x_`。写した先が同じ member body (lambda 連鎖を含む)
/// に識別子として現れる場合やユーザ型名と同じ場合は、そうでない名前になるまで
/// `_` を足す。宣言と
/// 全参照 (closure 内も) を semantic model で同じ symbol として束ね、syntax
/// tree の token 置換で一括して写すので、emit の各所は名前を知らなくてよい。
/// 置換は改行を増やさないため行番号 (source map) は保たれる。
/// </summary>
public static class LuaLocalRenamer
{
    private static bool NeedsRename(SyntaxTree tree, ICollection<string> typeNames) =>
        tree.GetRoot().DescendantTokens().Any(t => IsRenameCandidate(t, typeNames));

    private static bool IsRenameCandidate(SyntaxToken token,
        ICollection<string> typeNames) =>
        token.IsKind(SyntaxKind.IdentifierToken)
        && (LuaNaming.IsLuaKeyword(token.ValueText)
            || typeNames.Contains(token.ValueText));

    /// <summary>tree に写す対象のローカル束縛が無ければ null。あれば写した
    /// tree を返す (path / options は保持)。</summary>
    public static SyntaxTree? Rename(SemanticModel model,
        ICollection<string>? reservedTypeNames = null)
    {
        var tree = model.SyntaxTree;
        var typeNames = reservedTypeNames ?? model.Compilation.Assembly.TypeNames;
        var root = tree.GetRoot();
        var renames = new Dictionary<ISymbol, string>(
            SymbolEqualityComparer.Default);
        var scopes = new List<SyntaxNode>();
        var takenByScope = new Dictionary<SyntaxNode, HashSet<string>>();
        var chosen = new Dictionary<(SyntaxNode Scope, string Name), string>();

        foreach (var token in root.DescendantTokens()
            .Where(t => IsRenameCandidate(t, typeNames)))
        {
            var symbol = DeclaredLocalSymbol(model, token);
            if (symbol == null || renames.ContainsKey(symbol)) continue;
            var scope = ScopeOf(token.Parent!, root);
            if (!takenByScope.TryGetValue(scope, out var taken))
            {
                taken = scope.DescendantTokens()
                    .Where(t => t.IsKind(SyntaxKind.IdentifierToken))
                    .Select(t => t.ValueText)
                    .ToHashSet(StringComparer.Ordinal);
                takenByScope[scope] = taken;
                scopes.Add(scope);
            }
            var key = (scope, token.ValueText);
            if (!chosen.TryGetValue(key, out var name))
            {
                name = token.ValueText + "_";
                while (taken.Contains(name) || typeNames.Contains(name))
                    name += "_";
                chosen[key] = name;
                taken.Add(name);
            }
            renames[symbol] = name;
        }
        if (renames.Count == 0) return null;

        var tokens = new Dictionary<SyntaxToken, string>();
        foreach (var scope in scopes)
        {
            foreach (var token in scope.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
            {
                var symbol = DeclaredLocalSymbol(model, token)
                    ?? ReferencedSymbol(model, token);
                if (symbol != null && renames.TryGetValue(symbol, out var name))
                    tokens[token] = name;
            }
        }
        var newRoot = root.ReplaceTokens(tokens.Keys, (orig, _) =>
            SyntaxFactory.Identifier(orig.LeadingTrivia, tokens[orig],
                orig.TrailingTrivia));
        return tree.WithRootAndOptions(newRoot, tree.Options);
    }

    /// <summary>compilation の tree を写した tree で差し替え、新しい
    /// (compilation, model, tree) を返す。写すものが無ければそのまま。</summary>
    public static (CSharpCompilation Compilation, SemanticModel Model,
        SyntaxTree Tree) Apply(CSharpCompilation compilation, SemanticModel model,
        SyntaxTree tree, ICollection<string>? reservedTypeNames = null)
    {
        var typeNames = reservedTypeNames ?? compilation.Assembly.TypeNames;
        if (!NeedsRename(tree, typeNames)) return (compilation, model, tree);
        var renamed = Rename(model, typeNames);
        if (renamed == null) return (compilation, model, tree);
        var newCompilation = compilation.ReplaceSyntaxTree(tree, renamed);
        return (newCompilation, newCompilation.GetSemanticModel(renamed), renamed);
    }

    // 宣言 token がローカル束縛 (local / parameter) の symbol を持つならそれ。
    // field / record positional parameter は member なので対象外 (LuaNaming
    // が写す)。
    private static ISymbol? DeclaredLocalSymbol(SemanticModel model,
        SyntaxToken token)
    {
        var node = token.Parent;
        var symbol = node switch
        {
            VariableDeclaratorSyntax v when v.Identifier == token
                => model.GetDeclaredSymbol(v),
            ParameterSyntax p when p.Identifier == token
                => model.GetDeclaredSymbol(p),
            ForEachStatementSyntax f when f.Identifier == token
                => model.GetDeclaredSymbol(f),
            SingleVariableDesignationSyntax d when d.Identifier == token
                => model.GetDeclaredSymbol(d),
            CatchDeclarationSyntax c when c.Identifier == token
                => model.GetDeclaredSymbol(c),
            _ => null,
        };
        return symbol switch
        {
            ILocalSymbol => symbol,
            IParameterSymbol { ContainingSymbol: IMethodSymbol m }
                when node?.Parent?.Parent is not RecordDeclarationSyntax
                    && m.MethodKind is not MethodKind.DelegateInvoke
                => symbol,
            _ => null,
        };
    }

    private static ISymbol? ReferencedSymbol(SemanticModel model,
        SyntaxToken token)
    {
        if (token.Parent is not IdentifierNameSyntax id) return null;
        var info = model.GetSymbolInfo(id);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        return symbol is ILocalSymbol or IParameterSymbol ? symbol : null;
    }

    // 衝突判定の範囲: 最も近い member 宣言 (top-level 文は compilation unit)。
    // lambda / local function は member body に含まれるので一緒に見る。
    private static SyntaxNode ScopeOf(SyntaxNode node, SyntaxNode root) =>
        node.Ancestors().FirstOrDefault(a =>
            a is MemberDeclarationSyntax and not GlobalStatementSyntax
                and not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax)
        ?? root;
}
