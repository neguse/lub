using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// 基底なし class の生成コード。instance は `setmetatable({f = v, ...}, C)` の
// 1 つのテーブルコンストラクタで作る (空テーブルへ 1 key ずつ足すとハッシュ部の
// 再確保が field 数に応じて繰り返される)。
// ctor 本文の先頭にある `this.F = E` は、E が他の評価と順序を入れ替えても
// 観測できない式 (IsOrderIndependent) なら、F の既定値の代わりにコンス
// トラクタへ畳む。それ以外 (field を読む・呼び出し・this を渡す) に当たった
// 時点で畳みは止め、残りの本文は元の順序のまま emit する。
public partial class LuaEmitter
{
    private void EmitTableConstructedSelf(SemanticModel model, string className,
        ConstructorDeclarationSyntax? ctor, List<string> ctorParams,
        List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)> fieldInits,
        out List<IlStat>? remainingBody)
    {
        remainingBody = null;
        var folded = new Dictionary<string, IlExpr>();
        if (ctor?.Body != null)
        {
            var body = new List<IlStat>();
            if (BuildStatsInto(model, ctor.Body.Statements, body))
            {
                body.RemoveRange(0, TakeFoldableAssignments(model, fieldInits,
                    [.. ctorParams], body, folded));
                remainingBody = body;
            }
        }

        var entries = fieldInits.Select(f =>
            $"{f.Name} = " + (folded.TryGetValue(f.Name, out var value)
                ? RenderIl(value)
                : f.Init != null
                    ? RenderExprViaIl(model, f.Init)
                    : GetDefaultValueForType(f.Type!)));
        AppendLine($"local self = setmetatable({{{string.Join(", ", entries)}}}, "
            + $"{className})");
    }

    // 先頭から連続する畳める代入の個数を返し、folded に field 名 → 値を入れる
    private int TakeFoldableAssignments(SemanticModel model,
        List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)> fieldInits,
        HashSet<string> ctorParams, List<IlStat> body,
        Dictionary<string, IlExpr> folded)
    {
        var count = 0;
        foreach (var stat in body)
        {
            if (stat is not IlAssign
                {
                    Target: IlField { Recv: IlVar { Name: "self" }, Name: var name },
                    Value: var value,
                })
                break;
            var index = fieldInits.FindIndex(f => f.Name == name);
            if (index < 0 || folded.ContainsKey(name)
                || !IsOrderIndependent(value, ctorParams))
                break;
            // 畳むと既定値 / initializer の評価が消えるので、initializer は
            // 副作用のない式のときだけ捨てる
            var init = fieldInits[index].Init;
            if (init != null && !(BuildExpr(model, init) is { } initIl
                    && IsOrderIndependent(initIl, [])))
                break;
            folded[name] = value;
            count++;
        }
        return count;
    }

    // 副作用・field / static の読み書き・例外・this の参照を持たず、ctor の
    // parameter と定数だけから決まる式。他の initializer の副作用の前後に
    // 評価しても結果が変わらない。整数除算 / 剰余は 0 除算 fault があるので除く
    private static bool IsOrderIndependent(IlExpr expr,
        HashSet<string> ctorParams) => expr switch
    {
        IlLit => true,
        IlVar v => ctorParams.Contains(v.Name),
        IlParen p => IsOrderIndependent(p.E, ctorParams),
        IlUn u => IsOrderIndependent(u.E, ctorParams),
        IlBin { Op: IlBinOp.DivNum or IlBinOp.RemNum } => false,
        IlBin b => IsOrderIndependent(b.L, ctorParams)
            && IsOrderIndependent(b.R, ctorParams),
        IlNumericConvert c => IsOrderIndependent(c.Value, ctorParams),
        IlNullableWrap w => IsOrderIndependent(w.E, ctorParams),
        IlStructCopy c => IsOrderIndependent(c.E, ctorParams),
        IlTable t => t.Entries.All(e =>
            (e.Key == null || IsNonFaultingKey(e.Key))
            && IsOrderIndependent(e.Value, ctorParams)),
        _ => false,
    };

    // [k]=v は k が nil / NaN だとテーブル生成中に fault するので、
    // そうならないと分かる文字列・有限数値・bool リテラルだけ許す
    private static bool IsNonFaultingKey(IlExpr key) => key is IlLit
    {
        LuaText: var text,
    } && (text.StartsWith('"') || text is "true" or "false"
        || (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d)));
}
