using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL 化できない本文 / 式の扱い (T250 で legacy visitor を廃止)。IL builder は
// 未対応構文で null を返すだけなので、ここで原因ノードを特定して TCS1001 を
// 出し、本文は実行時 error の stub にする。Shared facts が既に診断する構文
// (WarnUnsupported が TryGetUnsupportedSyntax で判定) は二重に出さない。
public partial class LuaEmitter
{
    private void EmitUnsupportedBody(SemanticModel model,
        IEnumerable<SyntaxNode> nodes)
    {
        var culprit = FindIlCulprit(model, nodes);
        var comment = WarnUnsupported(culprit, DescribeCulprit(model, culprit));
        AppendLine($"error({EscapeLuaString($"TinyC#: unsupported {culprit.Kind()}")}) {comment}");
    }

    // 式 1 個 (field initializer / parameter default / base 引数) の Lua 表記。
    // field / property initializer は field への代入なので copyStruct (il-spec §10)。
    // IL にできない式は評価した時点で止まる実行時 error (本文の stub と同じ)
    private string RenderExprViaIl(SemanticModel model, ExpressionSyntax expr,
        bool copyStruct = false)
    {
        var built = BuildExpr(model, expr);
        if (built != null)
            return RenderIl(copyStruct ? WrapStructCopy(model, expr, built) : built);
        var culprit = FindIlCulprit(model, [expr]);
        return $"{WarnUnsupported(culprit, DescribeCulprit(model, culprit))} " +
            $"error({EscapeLuaString($"TinyC#: unsupported {culprit.Kind()}")})";
    }

    private static string DescribeCulprit(SemanticModel model, SyntaxNode node) =>
        StructuralTypeTestIn(model, node) is { } type
            ? $"type test against {type} (Lua tables carry no List / array / Dictionary type)"
            : node switch
            {
                StatementSyntax => $"statement: {node.Kind()}",
                _ => $"expression: {node.Kind()}",
            };

    // node 内の構造型 (List / 配列 / Dictionary) への型 test の型。Lua では
    // IL にしない (StructuralTypeTests) ので、それが失敗の原因なら名指しする
    private static string? StructuralTypeTestIn(SemanticModel model, SyntaxNode node) =>
        node.DescendantNodesAndSelf()
            .Select(n => n switch
            {
                BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression } b =>
                    model.GetTypeInfo(b.Right).Type,
                TypePatternSyntax tp => model.GetTypeInfo(tp.Type).Type,
                DeclarationPatternSyntax dp => model.GetTypeInfo(dp.Type).Type,
                RecursivePatternSyntax { Type: { } rt } => model.GetTypeInfo(rt).Type,
                ConstantPatternSyntax cp =>
                    model.GetSymbolInfo(cp.Expression).Symbol as ITypeSymbol,
                _ => null,
            })
            .FirstOrDefault(IsStructuralType)?.ToDisplayString();

    // IL 化に失敗する最小のノード: 子がすべて build できるのに自分は
    // できないノード。型名 (TypeSyntax) と文脈依存の断片 (MemberBinding /
    // out var の宣言式) には降りない
    private SyntaxNode FindIlCulprit(SemanticModel model,
        IEnumerable<SyntaxNode> nodes)
    {
        SyntaxNode? first = null;
        foreach (var node in nodes)
        {
            first ??= node;
            if (IlFails(model, node)) return Descend(model, node);
        }
        return first ?? throw new InvalidOperationException("no nodes");
    }

    private SyntaxNode Descend(SemanticModel model, SyntaxNode node)
    {
        // Shared facts が診断する構文はそこで止める (警告は facts 側が出す)
        if (TinyCsComplianceFacts.TryGetUnsupportedSyntax(node, out _)) return node;
        foreach (var child in node.ChildNodes())
        {
            if (TinyCsComplianceFacts.TryGetUnsupportedSyntax(child, out _)) return child;
            if (child is TypeSyntax or MemberBindingExpressionSyntax
                or DeclarationExpressionSyntax)
                continue;
            if (child is StatementSyntax or ExpressionSyntax)
            {
                if (IlFails(model, child)) return Descend(model, child);
                continue;
            }
            // 引数リスト / 初期化子などの構造ノードは透過して探す
            if (FindFailingDescendant(model, child) is { } inner)
                return inner;
        }
        return node;
    }

    private SyntaxNode? FindFailingDescendant(SemanticModel model, SyntaxNode node)
    {
        foreach (var child in node.ChildNodes())
        {
            if (TinyCsComplianceFacts.TryGetUnsupportedSyntax(child, out _)) return child;
            if (child is TypeSyntax or MemberBindingExpressionSyntax
                or DeclarationExpressionSyntax)
                continue;
            if (child is StatementSyntax or ExpressionSyntax)
            {
                if (IlFails(model, child)) return Descend(model, child);
                continue;
            }
            if (FindFailingDescendant(model, child) is { } inner) return inner;
        }
        return null;
    }

    private bool IlFails(SemanticModel model, SyntaxNode node) => node switch
    {
        StatementSyntax stat => !BuildStatsInto(model, [stat], []),
        ExpressionSyntax expr => BuildExpr(model, expr) == null,
        _ => false,
    };
}
