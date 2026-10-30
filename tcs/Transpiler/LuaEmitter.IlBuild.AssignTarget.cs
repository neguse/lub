using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder: 代入先の格納場所。C# は代入先の受け手 / 添字を右辺より先に
// 評価してその格納場所へ書き、式位置の代入の値は代入した値。Lua の struct は
// 親の slot に入った table なので、左辺の table を右辺より先に評価する代入文
// (`h.data.n = f()`) は、右辺が途中の struct を差し替えると古い table に書く。
// 式位置の代入を左辺の読み直しで値にすると、右辺が差し替えた受け手を読む。
public partial class LuaEmitter
{
    // 値型 place の struct field を辿った起点。Base は参照型 prefix (配列要素
    // なら配列) か変数 (local / parameter / this / static field / this の
    // field)、Stop は起点の node (配列要素なら要素、それ以外は Base)。辿れない
    // (値を返す呼び出し等) なら null
    private static (ExpressionSyntax Stop, ExpressionSyntax Base,
        ExpressionSyntax? Index, bool IsVariable)? StructSlotPath(
        SemanticModel model, ExpressionSyntax place)
    {
        while (true)
        {
            if (model.GetTypeInfo(place).Type is not { IsValueType: true })
                return (place, place, null, false);
            switch (place)
            {
                case ParenthesizedExpressionSyntax p:
                    place = p.Expression;
                    break;
                case MemberAccessExpressionSyntax ma
                    when model.GetSymbolInfo(ma).Symbol is IFieldSymbol
                        { IsStatic: false }:
                    place = ma.Expression;
                    break;
                case ElementAccessExpressionSyntax ea
                    when model.GetTypeInfo(ea.Expression).Type
                        is IArrayTypeSymbol:
                    return (ea, ea.Expression,
                        ea.ArgumentList.Arguments[0].Expression, false);
                case ThisExpressionSyntax:
                    return (place, place, null, true);
                case IdentifierNameSyntax or MemberAccessExpressionSyntax
                    when model.GetSymbolInfo(place).Symbol
                        is IFieldSymbol or ILocalSymbol or IParameterSymbol:
                    return (place, place, null, true);
                default:
                    return null;
            }
        }
    }

    // struct の member への代入 (= / op= / ??=) を、右辺の後に経路を辿り直して
    // 書く必要があるか。右辺が格納先を書き換え得ないなら不要。local /
    // parameter / this の直下の member (`s.X`) は変数そのものが格納先 (Lua の
    // local は register、C の struct は固定の領域) なので不要
    private static bool NeedsStructSlotAssign(SemanticModel model,
        AssignmentExpressionSyntax assign)
    {
        if (assign.Left is not MemberAccessExpressionSyntax ma
            || model.GetSymbolInfo(ma).Symbol is not
                { IsStatic: false, Kind: SymbolKind.Field or SymbolKind.Property }
            || model.GetTypeInfo(ma.Expression).Type is not { IsValueType: true }
            || StructSlotPath(model, ma.Expression) is not { } path)
            return false;
        var direct = path.IsVariable
            && Unparen(ma.Expression) == path.Stop
            && (path.Stop is ThisExpressionSyntax
                || model.GetSymbolInfo(path.Stop).Symbol
                    is ILocalSymbol or IParameterSymbol);
        return !direct && MayWriteStorage(model, assign.Right);
    }

    private static ExpressionSyntax Unparen(ExpressionSyntax e) =>
        e is ParenthesizedExpressionSyntax p ? Unparen(p.Expression) : e;

    // 参照型 prefix と配列添字を (読み直すと変わり得るなら) temp に固定し、
    // `__tcs_v` に値を置いてから辿り直した place に書く文列。custom property
    // は struct の accessor を通す
    private List<IlStat>? BuildStructSlotAssign(SemanticModel model,
        AssignmentExpressionSyntax assign, bool asValue = false)
    {
        var ma = (MemberAccessExpressionSyntax)assign.Left;
        var path = StructSlotPath(model, ma.Expression)!.Value;
        List<IlStat> stats = [];
        IlExpr? Fix(ExpressionSyntax e, string temp)
        {
            var built = BuildExpr(model, e);
            if (built == null || (IsStableRead(model, e)
                    && !WritesLocalReadBy(model, e, assign.Right)))
                return built;
            stats.Add(new IlLocal(temp, built));
            return new IlVar(temp);
        }
        var root = path.IsVariable
            ? BuildExpr(model, path.Base) : Fix(path.Base, "__tcs_obj");
        if (root != null && path.Index != null)
            root = Fix(path.Index, "__tcs_idx") is { } idx
                ? new IlIndex(root, idx, true,
                    HasNilableElements(model.GetTypeInfo(path.Base).Type)) : null;
        IlExpr? Rebuild(ExpressionSyntax place) => place == path.Stop
            ? root
            : place switch
            {
                ParenthesizedExpressionSyntax p => Rebuild(p.Expression),
                MemberAccessExpressionSyntax m => Rebuild(m.Expression)
                    is { } inner
                    ? new IlField(inner, N(model.GetSymbolInfo(m).Symbol!))
                    : null,
                _ => null,
            };
        var right = BuildExpr(model, assign.Right);
        if (Rebuild(ma.Expression) is not { } recv || right == null)
            return null;
        var member = model.GetSymbolInfo(ma).Symbol!;
        var owner = member is IPropertySymbol prop && IsCustomProperty(prop)
            ? TypeRef(prop.ContainingType) : null;
        var read = owner != null
            ? BuildPropGet(recv, N(member), false, owner)
            : new IlField(recv, N(member));
        var v = new IlVar("__tcs_v");
        // 式の値に使う `__tcs_v` を setter の value の書き換えから守る
        IlStat write = owner != null
            ? new IlCallStat(BuildPropSet(recv, N(member), false,
                asValue ? CopyIfMutableStruct(model.GetTypeInfo(ma).Type, v) : v,
                owner))
            : new IlAssign(read, v);
        if (assign.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            stats.Add(new IlLocal(v.Name, read));
            stats.Add(new IlIf([(IsNullIl(v, model.GetTypeInfo(ma).Type),
                new IlBlock([new IlAssign(v,
                    WrapStructCopy(model, assign.Right, right)), write]))], null));
            return stats;
        }
        var value = assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
            ? WrapStructCopy(model, assign.Right, right)
            : CompoundOperator(model, assign) is { } op
                ? BuildCompoundValue(model, assign, op, read, new IlParen(right))
                : null;
        if (value == null) return null;
        stats.Add(new IlLocal(v.Name, value));
        stats.Add(write);
        return stats;
    }

    // 式位置の代入 (`(i = y) >= 0`、`arr[x = 1]`): 文として代入し、代入した値を
    // 式の値にする IIFE (読み直しで受け手が変わり得るときは受け手 / 添字を
    // 固定した place を読む)。変更可能な struct の値は格納先の table ではなく
    // その copy (値を受けた側の変更が格納先に残らない。il-spec §10)。custom
    // property への代入は setter に copy を渡し、式の値は setter の外の temp。
    // `T?` への ??= の値は中の値 (CoalesceAssignValue)
    private IlExpr? BuildAssignExpr(SemanticModel model,
        AssignmentExpressionSyntax assign)
    {
        IlIife? iife;
        if (NeedsStructSlotAssign(model, assign))
            iife = BuildStructSlotAssign(model, assign, asValue: true) is { } stats
                ? new IlIife([.. stats, new IlReturn(new IlVar("__tcs_v"))])
                : null;
        else if (IsCustomPropertyTarget(model, assign.Left))
            iife = BuildPropAssignExpr(model, assign);
        else if (NeedsLoweredAssign(model, assign)
            || NeedsFixedAssignTarget(model, assign))
            iife = BuildLoweredAssign(model, assign);
        else
        {
            var stats = new List<IlStat>();
            if (!BuildExprStatInto(model, assign, null, stats)) return null;
            iife = BuildExpr(model, assign.Left) is { } assigned
                ? new IlIife([.. stats, new IlReturn(assigned)])
                : null;
        }
        if (iife is not { Stats: [.., IlReturn { Value: { } value }] }) return iife;
        return iife with { Stats = [.. iife.Stats[..^1], new IlReturn(
            CopyIfMutableStruct(model.GetTypeInfo(assign).Type,
                CoalesceAssignValue(model, assign, value)))] };
    }

    private bool BuildStructSlotAssignInto(SemanticModel model,
        AssignmentExpressionSyntax assign, SyntaxNode? origin,
        List<IlStat> acc)
    {
        if (BuildStructSlotAssign(model, assign) is not { } stats) return false;
        acc.Add(new IlDo(new IlBlock([.. stats])) { Origin = origin });
        return true;
    }

    // 式位置の代入 (参照型の受け手) の値を、代入後に左辺を読み直して得ると
    // 受け手 / 添字を再評価するか: 評価に副作用がある、または local /
    // parameter / this / 型名 / 定数以外 (右辺が変え得る field 等) を読む。
    // true なら受け手 / 添字を temp に固定する
    private static bool NeedsFixedAssignTarget(SemanticModel model,
        AssignmentExpressionSyntax assign)
    {
        if (NeedsLoweredLvalue(model, assign.Left)
            || CoalesceRightWritesTarget(model, assign)) return true;
        (ExpressionSyntax? recv, ExpressionSyntax? index) = assign.Left switch
        {
            MemberAccessExpressionSyntax ma => (ma.Expression, null),
            ElementAccessExpressionSyntax ea => (ea.Expression,
                ea.ArgumentList.Arguments[0].Expression),
            _ => (null, null),
        };
        return recv != null
            && model.GetTypeInfo(recv).Type is { IsReferenceType: true }
            && model.GetSymbolInfo(recv).Symbol is not ITypeSymbol
            && !(IsStableRead(model, recv)
                && (index == null || IsStableRead(model, index)));
    }

    // `??=` の右辺が受け手 / 添字の読む local を書き換えるか
    private static bool CoalesceRightWritesTarget(SemanticModel model,
        AssignmentExpressionSyntax assign) =>
        assign.IsKind(SyntaxKind.CoalesceAssignmentExpression)
        && assign.Left is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax
        && model.GetTypeInfo(assign.Left is MemberAccessExpressionSyntax ma
            ? ma.Expression
            : ((ElementAccessExpressionSyntax)assign.Left).Expression).Type is { IsReferenceType: true }
        && WritesLocalReadBy(model, assign.Left, assign.Right);

    // 右辺が書き換えない限り、読み直しても同じ値になる式 (local / parameter /
    // this / 型名 / 定数とその演算)
    private static bool IsStableRead(SemanticModel model, ExpressionSyntax e) =>
        e switch
        {
            LiteralExpressionSyntax or ThisExpressionSyntax => true,
            ParenthesizedExpressionSyntax p => IsStableRead(model, p.Expression),
            CastExpressionSyntax c => IsStableRead(model, c.Expression),
            BinaryExpressionSyntax b =>
                model.GetSymbolInfo(b).Symbol is not IMethodSymbol
                    { MethodKind: MethodKind.UserDefinedOperator }
                && IsStableRead(model, b.Left) && IsStableRead(model, b.Right),
            PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression
                    or (int)SyntaxKind.LogicalNotExpression
                    or (int)SyntaxKind.BitwiseNotExpression
            } u => IsStableRead(model, u.Operand),
            IdentifierNameSyntax or MemberAccessExpressionSyntax =>
                model.GetSymbolInfo(e).Symbol is ILocalSymbol or IParameterSymbol
                    or ITypeSymbol or INamespaceSymbol or IFieldSymbol { IsConst: true },
            _ => false,
        };

    // 評価が既存の格納先 (field / 配列要素 / 捕捉された local) を書き換え得るか:
    // 代入 / ++ / --、user code を走らせ得る呼び出し・構築・custom property の
    // getter・user 定義演算子
    private static bool MayWriteStorage(SemanticModel model, SyntaxNode node) =>
        node.DescendantNodesAndSelf().Any(n => n switch
        {
            AssignmentExpressionSyntax or WithExpressionSyntax => true,
            PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.PreIncrementExpression
                    or (int)SyntaxKind.PreDecrementExpression
            } => true,
            PostfixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.PostIncrementExpression
                    or (int)SyntaxKind.PostDecrementExpression
            } => true,
            InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax =>
                RunsUserCode(model.GetSymbolInfo(n).Symbol),
            IdentifierNameSyntax or MemberAccessExpressionSyntax =>
                model.GetSymbolInfo(n).Symbol is IPropertySymbol p
                && IsCustomProperty(p),
            BinaryExpressionSyntax or PrefixUnaryExpressionSyntax =>
                model.GetSymbolInfo(n).Symbol is IMethodSymbol
                    { MethodKind: MethodKind.UserDefinedOperator } op
                && op.Locations.Any(l => l.IsInSource),
            _ => false,
        });

    // 呼び出し / 構築が状態を書き換え得るか。書き換えないとみなすのは、
    // delegate・ref 引数を受けない Math / MathF・数値 / bool / char / string の
    // method と source に無い構築子 (自身の外の状態を変えない)、struct の暗黙の
    // 構築子 (field を zero にするだけ)。collection の method は中身を変え得る
    // (`List.Remove` が `Count` を変える)
    private static bool RunsUserCode(ISymbol? symbol) =>
        symbol is not IMethodSymbol m
        || m.Parameters.Any(p => p.RefKind != RefKind.None
            || p.Type.TypeKind == TypeKind.Delegate)
        || !(m.MethodKind == MethodKind.Constructor
            ? !m.Locations.Any(l => l.IsInSource)
                || (m.IsImplicitlyDeclared && m.ContainingType.IsValueType)
            : !m.Locations.Any(l => l.IsInSource)
                && (m.ContainingType.ToDisplayString() is "System.Math"
                        or "System.MathF"
                    || m.ContainingType.SpecialType == SpecialType.System_String
                    || (m.ContainingType.SpecialType != SpecialType.None
                        && m.ContainingType.IsValueType)));
}
