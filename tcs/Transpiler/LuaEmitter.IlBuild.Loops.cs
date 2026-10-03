using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder: for 文 (numeric for への最適化と while 脱糖)。文構築
// (LuaEmitter.IlBuild) のファイル長上限のため別 partial。
public partial class LuaEmitter
{
    private bool BuildForInto(SemanticModel model, ForStatementSyntax forStmt,
        List<IlStat> acc)
    {
        // TryEmitSimpleFor と同じ条件で numeric for へ (ガードも同じ helper)
        if (TryBuildSimpleFor(model, forStmt) is { } simple)
        {
            acc.Add(simple with { Origin = forStmt });
            return true;
        }

        foreach (var init in forStmt.Initializers)
            if (!BuildExprStatInto(model, init, forStmt, acc)) return false;
        if (forStmt.Declaration != null)
            foreach (var v in forStmt.Declaration.Variables)
            {
                IlExpr? init = null;
                if (v.Initializer != null
                    && (init = BuildExpr(model, v.Initializer.Value)) == null)
                    return false;
                // 宣言型を載せる (`for (float y = -1; ...)` は初期値からは int に見える)
                acc.Add(new IlLocal(v.Identifier.ValueText, init,
                    (model.GetDeclaredSymbol(v) as ILocalSymbol)?.Type.ToDisplayString())
                    { Origin = forStmt });
            }

        IlExpr cond = new IlLit("true");
        if (forStmt.Condition != null)
        {
            var built = BuildExpr(model, forStmt.Condition);
            if (built == null) return false;
            cond = built;
        }
        var body = BuildBlock(model, forStmt.Statement);
        if (body == null) return false;
        var trailer = new List<IlStat>();
        foreach (var inc in forStmt.Incrementors)
            if (!BuildExprStatInto(model, inc, forStmt, trailer)) return false;
        var scopeBody = forStmt.Incrementors.Count > 0
            && ContainsDirectContinue(forStmt.Statement);
        acc.Add(new IlWhile(cond, body, new IlBlock([.. trailer]), scopeBody)
            { Origin = forStmt });
        return true;
    }

    private IlNumericFor? TryBuildSimpleFor(SemanticModel model,
        ForStatementSyntax forStmt)
    {
        if (forStmt.Declaration?.Variables.Count != 1) return null;
        var decl = forStmt.Declaration.Variables[0];
        if (decl.Initializer == null) return null;
        var varName = decl.Identifier.ValueText;

        if (forStmt.Condition is not BinaryExpressionSyntax cond) return null;
        if (cond.Left is not IdentifierNameSyntax condId
            || condId.Identifier.ValueText != varName) return null;

        if (forStmt.Incrementors.Count != 1) return null;
        var inc = forStmt.Incrementors[0];
        var isIncByOne = inc is PostfixUnaryExpressionSyntax
                { RawKind: (int)SyntaxKind.PostIncrementExpression }
            || (inc is AssignmentExpressionSyntax
                { RawKind: (int)SyntaxKind.AddAssignmentExpression } addAssign
                && addAssign.Right is LiteralExpressionSyntax { Token.Text: "1" });
        if (!isIncByOne) return null;

        if (!IsLoopInvariantBound(model, forStmt, cond.Right)) return null;
        if (IsAssignedWithin(forStmt.Statement, varName)) return null;
        if (IsCapturedByLambdaWithin(forStmt.Statement, varName)) return null;

        var start = BuildExpr(model, decl.Initializer.Value);
        var end = BuildExpr(model, cond.Right);
        var body = BuildBlock(model, forStmt.Statement);
        if (start == null || end == null || body == null) return null;
        IlExpr? limit = cond.Kind() switch
        {
            SyntaxKind.LessThanExpression =>
                new IlBin(IlBinOp.Sub, end, new IlLit("1")),
            SyntaxKind.LessThanOrEqualExpression => end,
            _ => null,
        };
        return limit == null ? null : new IlNumericFor(varName, start, limit, body);
    }

    // return 位置の値を statement 化込みで追加する共通経路
}
