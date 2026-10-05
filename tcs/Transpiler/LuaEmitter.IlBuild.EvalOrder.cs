using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder: operand の評価順固定。C# は二項演算 / 代入の operand を左から
// 評価して値を確定させるが、Lua は local を register のまま参照する
// (`i + f()` / `t[k] = f()` は f 実行後の i / k を読む) し、C も GNU statement
// expression を含む operand 間の順序を規定しない。後続 operand が書き換え得る
// local を先行 operand が読むときだけ、先行側を temp に退避する。
public partial class LuaEmitter
{
    // `local __tcs_l = first; return build(__tcs_l)` の IIFE
    private static IlExpr? SnapshotOperand(IlExpr first,
        Func<IlExpr, IlExpr?> build)
    {
        var applied = build(new IlVar("__tcs_l"));
        return applied == null ? null : new IlIife([
            new IlLocal("__tcs_l", first),
            new IlReturn(applied)]);
    }

    // 代入の受け手 / 添字を temp 化する必要があるか。副作用のある lvalue
    // (従来条件) に加え、右辺が書き換える local を受け手 / 添字が読む場合。
    // 値型の受け手は temp が copy になり代入が消えるので対象外
    private static bool NeedsLoweredAssign(SemanticModel model,
        AssignmentExpressionSyntax assign)
    {
        if (assign.IsKind(SyntaxKind.CoalesceAssignmentExpression)) return false;
        if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && NeedsLoweredLvalue(assign.Left))
            return true;
        var recv = assign.Left switch
        {
            MemberAccessExpressionSyntax ma => ma.Expression,
            ElementAccessExpressionSyntax ea => ea.Expression,
            _ => null,
        };
        return recv != null
            && model.GetTypeInfo(recv).Type is { IsReferenceType: true }
            && WritesLocalReadBy(model, assign.Left, assign.Right);
    }

    // later の評価が earlier の読む local / parameter を書き換え得るか:
    // 代入 / ++ / -- / ref・out 引数の直接の書き込み、または lambda 内で
    // 書かれる (捕捉された) local を読んでいて later が呼び出しを含む場合
    private static bool WritesLocalReadBy(SemanticModel model,
        SyntaxNode earlier, SyntaxNode later)
    {
        var read = earlier.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Select(id => model.GetSymbolInfo(id).Symbol)
            .Where(s => s is ILocalSymbol or IParameterSymbol)
            .ToHashSet(SymbolEqualityComparer.Default);
        if (read.Count == 0) return false;
        if (WritesAny(model, later, read)) return true;
        return later.DescendantNodesAndSelf().Any(n =>
                n is InvocationExpressionSyntax
                    or BaseObjectCreationExpressionSyntax)
            && read.Any(s => IsWrittenInLambda(model, s!));
    }

    private static bool WritesAny(SemanticModel model, SyntaxNode scope,
        HashSet<ISymbol?> symbols) =>
        scope.DescendantNodesAndSelf().Any(n => n switch
        {
            AssignmentExpressionSyntax a => Targets(model, a.Left, symbols),
            PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.PreIncrementExpression
                    or (int)SyntaxKind.PreDecrementExpression
            } p => Targets(model, p.Operand, symbols),
            PostfixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.PostIncrementExpression
                    or (int)SyntaxKind.PostDecrementExpression
            } p => Targets(model, p.Operand, symbols),
            ArgumentSyntax arg when !arg.RefKindKeyword.IsKind(SyntaxKind.None) =>
                Targets(model, arg.Expression, symbols),
            _ => false,
        });

    private static bool Targets(SemanticModel model, ExpressionSyntax target,
        HashSet<ISymbol?> symbols) => target switch
    {
        IdentifierNameSyntax id => symbols.Contains(model.GetSymbolInfo(id).Symbol),
        ParenthesizedExpressionSyntax p => Targets(model, p.Expression, symbols),
        TupleExpressionSyntax t => t.Arguments.Any(
            a => Targets(model, a.Expression, symbols)),
        _ => false,
    };

    private static bool IsWrittenInLambda(SemanticModel model, ISymbol symbol)
    {
        var member = symbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?.GetSyntax().FirstAncestorOrSelf<MemberDeclarationSyntax>();
        if (member == null || member.SyntaxTree != model.SyntaxTree) return false;
        HashSet<ISymbol?> one = new(SymbolEqualityComparer.Default) { symbol };
        return member.DescendantNodes()
            .OfType<AnonymousFunctionExpressionSyntax>()
            .Any(lambda => WritesAny(model, lambda, one));
    }
}
