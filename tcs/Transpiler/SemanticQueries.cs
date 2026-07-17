using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// エディタ向けの補完/hover クエリ (T230)。IncrementalCompilationSession の
// speculative fork (ForkWithContent) が返す compilation/tree に対する読み取り
// 専用のクエリで、session 状態には触れない。呼び出しごとに評価し、fork や
// SemanticModel はキャッシュしない (wasm ホストのメモリ膨張防止)。
//
// 補完候補は TinyCsComplianceFacts の allowlist で絞り、tcs で書けない
// BCL member を候補に出さない。Microsoft.CodeAnalysis.Features は使わない
// (wasm バンドル肥大のため。SemanticModel の Lookup* で足りる範囲に限定)。

public sealed class CompletionItem
{
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public required string Detail { get; init; }
}

public sealed class HoverInfo
{
    public required string Display { get; init; }
    public string? Doc { get; init; }
    public required int Start { get; init; }
    public required int End { get; init; }
}

public static class SemanticQueries
{
    public const int MaxCompletionItems = 200;

    // 補完 detail / hover 表示: `int Add(int amount)` 形。containing type は
    // hover のみに含める (補完リストでは冗長)。
    private static readonly SymbolDisplayFormat DetailFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat HoverFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeRef
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeModifiers,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    // ------------------------------------------------------------------
    public static List<CompletionItem> Complete(
        CSharpCompilation comp, SyntaxTree tree, int position)
    {
        var model = comp.GetSemanticModel(tree);
        var root = tree.GetCompilationUnitRoot();
        var text = tree.GetText();
        position = Math.Clamp(position, 0, text.Length);
        var token = root.FindToken(Math.Max(0, position - 1));

        var receiver = FindMemberAccessReceiver(token);
        var symbols = receiver != null
            ? LookupMembers(model, position, receiver)
            : model.LookupSymbols(position);

        var seen = new HashSet<(string, string)>();
        var items = new List<CompletionItem>();
        foreach (var s in symbols)
        {
            if (!s.CanBeReferencedByName || string.IsNullOrEmpty(s.Name))
                continue;
            if (s is IMethodSymbol m
                && m.MethodKind is not (MethodKind.Ordinary
                    or MethodKind.ReducedExtension))
                continue;
            // tcs allowlist 外の API は候補に出さない
            if (TinyCsComplianceFacts.TryGetUnsupportedApi(s, out _))
                continue;
            var kind = KindOf(s);
            if (!seen.Add((s.Name, kind)))
                continue; // overload は名前単位で 1 件
            items.Add(new CompletionItem
            {
                Label = s.Name,
                Kind = kind,
                Detail = s.ToDisplayString(DetailFormat),
            });
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
        if (items.Count > MaxCompletionItems)
            items.RemoveRange(MaxCompletionItems, items.Count - MaxCompletionItems);
        return items;
    }

    // ------------------------------------------------------------------
    public static HoverInfo? Hover(
        CSharpCompilation comp, SyntaxTree tree, int position)
    {
        var model = comp.GetSemanticModel(tree);
        var root = tree.GetCompilationUnitRoot();
        var text = tree.GetText();
        position = Math.Clamp(position, 0, Math.Max(0, text.Length - 1));
        var token = root.FindToken(position);
        if (!token.IsKind(SyntaxKind.IdentifierToken))
            return null;

        // 識別子の参照 (SimpleName) か宣言ノードだけを対象にする。親式への
        // walk はしない — BinaryExpression 等の GetSymbolInfo が user-defined
        // operator method を拾い、未解決識別子の hover に無関係なシンボルが
        // 出るため (PR #1 レビュー指摘)。
        ISymbol? sym = token.Parent is SimpleNameSyntax name
            ? model.GetSymbolInfo(name).Symbol
            : token.Parent != null
                ? model.GetDeclaredSymbol(token.Parent)
                : null;
        if (sym == null)
            return null;

        return new HoverInfo
        {
            Display = sym.ToDisplayString(HoverFormat),
            Doc = ExtractDocSummary(sym),
            Start = token.Span.Start,
            End = token.Span.End,
        };
    }

    // ------------------------------------------------------------------
    // `expr.` / `expr.par` の受け手。member access 文脈でなければ null
    // (スコープ補完に落ちる)。
    private static ExpressionSyntax? FindMemberAccessReceiver(SyntaxToken token)
    {
        if (token.IsKind(SyntaxKind.DotToken))
            return token.Parent switch
            {
                MemberAccessExpressionSyntax ma => ma.Expression,
                QualifiedNameSyntax qn => qn.Left,
                _ => null,
            };
        if (token.Parent is SimpleNameSyntax sn)
        {
            if (sn.Parent is MemberAccessExpressionSyntax ma && ma.Name == sn)
                return ma.Expression;
            if (sn.Parent is QualifiedNameSyntax qn && qn.Right == sn)
                return qn.Left;
        }
        return null;
    }

    private static IEnumerable<ISymbol> LookupMembers(
        SemanticModel model, int position, ExpressionSyntax receiver)
    {
        // 受け手が型名 (Math.) なら static、namespace (TinySystem.) なら
        // その配下、それ以外は式の型の instance member + 拡張メソッド。
        var recvSymbol = model.GetSymbolInfo(receiver).Symbol;
        if (recvSymbol is INamedTypeSymbol typeRecv)
            return model.LookupStaticMembers(position, typeRecv);
        if (recvSymbol is INamespaceSymbol nsRecv)
            return model.LookupSymbols(position, nsRecv);
        var type = model.GetTypeInfo(receiver).Type;
        if (type == null)
            return [];
        return model
            .LookupSymbols(position, type, includeReducedExtensionMethods: true)
            .Where(s => !s.IsStatic);
    }

    private static string KindOf(ISymbol s) => s switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
        INamedTypeSymbol => "class",
        INamespaceSymbol => "namespace",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol { IsConst: true } => "constant",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "constant",
        IFieldSymbol => "variable",
        ILocalSymbol or IParameterSymbol => "variable",
        _ => "text",
    };

    // /// doc comment の <summary> をプレーンテキスト化する。無ければ null。
    private static string? ExtractDocSummary(ISymbol sym)
    {
        var xml = sym.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            xml, "<summary>(.*?)</summary>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success)
            return null;
        var body = System.Text.RegularExpressions.Regex.Replace(
            m.Groups[1].Value, "<[^>]+>", "");
        body = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ").Trim();
        return body.Length > 0 ? body : null;
    }
}
