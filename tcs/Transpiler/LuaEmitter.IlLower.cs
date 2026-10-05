using System.Collections.Immutable;

namespace TinyCs;

// 式位置の条件式 / `??` (右辺が呼び出しを含む) の文 lowering (#25)。
// Lua では式として書けないため、IIFE (評価ごとに closure を確保) ではなく
// temp local + if 文に下ろす。Lua backend の emit 時 IL→IL 変換で、IL 自体は
// 変えない (tcs2c は IlTernary をそのまま使う)。
//
// 評価順 (C# は左から右) を守る規則:
//   - 文に下ろす式 X の左にある兄弟 operand は、X の前置文より先に評価される
//     必要がある。左 operand が副作用を持ち得る (呼び出しを含む) か、X までの
//     右の兄弟のどれかが呼び出しを含む (左 operand が読む状態を書き換え得る)
//     場合は temp に束縛する
//   - 短絡 (&& / || / ??) の右辺は、条件付きの if 内に前置文を置く
//   - temp は文ごとに do ... end で囲い、Lua の local 上限 (200) を踏まない
//   - 条件が毎回評価される位置 (while) は `while true do do 前置文; if not c
//     then break end end ...` に、elseif は入れ子の else に展開する
// repeat-until の条件 / IIFE 内の文 / field initializer (式のみの文脈) は
// 前置文を置けないので RenderIl の IIFE が残る。
public partial class LuaEmitter
{
    private const string LowerTempPrefix = "__tcs_t";
    private int _lowerTempCount;

    private string NewLowerTemp() => $"{LowerTempPrefix}{++_lowerTempCount}";

    private static IlExpr[] Children(IlExpr e) => e switch
    {
        IlField f => [f.Recv],
        IlIndex i => [i.Recv, i.Idx],
        IlLen l => [l.E],
        IlBin b => [b.L, b.R],
        IlUn u => [u.E],
        IlParen p => [p.E],
        IlCall c => [.. c.Args],
        IlDynCall d => [d.Callee, .. d.Args],
        IlInvoke i => [i.Recv, .. i.Args],
        IlNewObj o => [.. o.Args],
        IlTable t => [.. t.Entries.SelectMany(en => en.Key != null
            ? new[] { en.Key, en.Value } : [en.Value])],
        IlNewArray na => [na.Length],
        IlNumericConvert n => [n.Value],
        IlRefCast r => [r.Value],
        IlIsType t => [t.E],
        IlIsLuaType t => [t.E],
        IlStructCopy s => [s.E],
        IlCast c => [c.E],
        IlNullableWrap w => [w.E],
        IlNullableHasValue h => [h.E],
        IlNullableValue v => [v.E],
        IlNullableGetOrDefault g => [g.E, g.Default],
        IlLiftedBin l => [l.L, l.R],
        IlLiftedUn l => [l.E],
        _ => [],
    };

    private static IlExpr WithChildren(IlExpr e, IlExpr[] k) => e switch
    {
        IlField f => f with { Recv = k[0] },
        IlIndex i => i with { Recv = k[0], Idx = k[1] },
        IlLen l => l with { E = k[0] },
        IlBin b => b with { L = k[0], R = k[1] },
        IlUn u => u with { E = k[0] },
        IlParen p => p with { E = k[0] },
        IlCall c => c with { Args = [.. k] },
        IlDynCall d => d with { Callee = k[0], Args = [.. k.Skip(1)] },
        IlInvoke i => i with { Recv = k[0], Args = [.. k.Skip(1)] },
        IlNewObj o => o with { Args = [.. k] },
        IlTable t => t with { Entries = RebuildEntries(t.Entries, k) },
        IlNewArray na => na with { Length = k[0] },
        IlNumericConvert n => n with { Value = k[0] },
        IlRefCast r => r with { Value = k[0] },
        IlIsType t => t with { E = k[0] },
        IlIsLuaType t => t with { E = k[0] },
        IlStructCopy s => s with { E = k[0] },
        IlCast c => c with { E = k[0] },
        IlNullableWrap w => w with { E = k[0] },
        IlNullableHasValue h => h with { E = k[0] },
        IlNullableValue v => v with { E = k[0] },
        IlNullableGetOrDefault g => g with { E = k[0], Default = k[1] },
        IlLiftedBin l => l with { L = k[0], R = k[1] },
        IlLiftedUn l => l with { E = k[0] },
        _ => e,
    };

    private static ImmutableArray<IlTableEntry> RebuildEntries(
        ImmutableArray<IlTableEntry> entries, IlExpr[] k)
    {
        var i = 0;
        var result = ImmutableArray.CreateBuilder<IlTableEntry>(entries.Length);
        foreach (var en in entries)
        {
            var key = en.Key != null ? k[i++] : null;
            result.Add(en with { Key = key, Value = k[i++] });
        }
        return result.MoveToImmutable();
    }

    // 文に下ろす必要がある式 (IIFE を避けたいもの)
    private static bool Needs(IlExpr e) => e switch
    {
        IlTernary => true,
        IlNullableGetOrDefault g => !IsCallFree(g.Default) || Needs(g.E) || Needs(g.Default),
        _ => Children(e).Any(Needs),
    };

    // 前置文より後ろへ評価をずらしても値が変わらない式
    private static bool Stable(IlExpr e, IlExpr later) => e switch
    {
        IlLit or IlClosure => true,
        IlVar v when v.Name.StartsWith(LowerTempPrefix, StringComparison.Ordinal) => true,
        _ => IsCallFree(e) && IsCallFree(later),
    };

    private IlExpr Lower(IlExpr e, List<IlStat> pre)
    {
        if (!Needs(e)) return e;
        switch (e)
        {
            case IlTernary t:
            {
                var tmp = NewLowerTemp();
                pre.Add(new IlLocal(tmp, null));
                pre.AddRange(LowerInto(t, v => new IlAssign(new IlVar(tmp), v)));
                return new IlVar(tmp);
            }
            case IlParen p:
            {
                var inner = Lower(p.E, pre);
                return inner is IlVar ? inner : p with { E = inner };
            }
            case IlBin { Op: IlBinOp.And or IlBinOp.Or } b when Needs(b.R):
            {
                var tmp = NewLowerTemp();
                pre.Add(new IlLocal(tmp, Lower(b.L, pre)));
                IlExpr cond = b.Op == IlBinOp.And
                    ? new IlVar(tmp) : new IlUn(IlUnOp.Not, new IlVar(tmp));
                pre.Add(new IlIf([(cond, new IlBlock([.. LowerInto(b.R,
                    v => new IlAssign(new IlVar(tmp), v))]))], null));
                return new IlVar(tmp);
            }
            case IlNullableGetOrDefault g when !IsCallFree(g.Default) || Needs(g.Default):
            {
                var tmp = NewLowerTemp();
                pre.Add(new IlLocal(tmp, Lower(g.E, pre)));
                pre.Add(new IlIf([(new IlBin(IlBinOp.Eq, new IlVar(tmp), new IlLit("nil")),
                    new IlBlock([.. LowerInto(g.Default,
                        v => new IlAssign(new IlVar(tmp), v))]))], null));
                return new IlVar(tmp);
            }
        }
        var kids = Children(e);
        var last = Array.FindLastIndex(kids, Needs);
        var lowered = (IlExpr[])kids.Clone();
        for (var i = 0; i <= last; i++)
        {
            var li = Lower(kids[i], pre);
            // 右の兄弟 (i+1..last) のどれかが前置文へ評価を移し得るなら束縛する
            if (i < last && !kids[(i + 1)..(last + 1)].All(k => Stable(li, k)))
            {
                var tmp = NewLowerTemp();
                pre.Add(new IlLocal(tmp, li));
                li = new IlVar(tmp);
            }
            lowered[i] = li;
        }
        return WithChildren(e, lowered);
    }

    private IlExpr[] LowerSeq(IlExpr[] items, List<IlStat> pre) =>
        ((IlCall)Lower(new IlCall("", [.. items]), pre)).Args.ToArray();

    // e の値を sink (代入 / return) へ渡す文列。条件式は temp を経ずに
    // 各分岐へ sink を複製する (入れ子は elseif に畳む)
    private List<IlStat> LowerInto(IlExpr e, Func<IlExpr, IlStat> sink)
    {
        var stats = new List<IlStat>();
        var core = e;
        while (core is IlParen p) core = p.E;
        if (core is IlTernary t)
        {
            var cond = Lower(t.Cond, stats);
            stats.Add(MakeIf(cond,
                new IlBlock([.. LowerInto(t.T, sink)]),
                new IlBlock([.. LowerInto(t.F, sink)])));
        }
        else
        {
            var value = Lower(e, stats);
            stats.Add(sink(value));
        }
        return stats;
    }

    private static IlIf MakeIf(IlExpr cond, IlBlock then, IlBlock other) =>
        other.Stats is [IlIf nested]
            ? new IlIf([(cond, then), .. nested.Arms], nested.Else)
            : new IlIf([(cond, then)], other);

    // temp (top-level の IlLocal) を宣言する文列は do ... end で閉じる
    private static List<IlStat> Scoped(List<IlStat> stats, IlStat original)
    {
        if (stats.Any(s => s is IlLocal))
            stats = [new IlDo(new IlBlock([.. stats]))];
        return [.. stats.Select(s => s with { Origin = original.Origin })];
    }

    private List<IlStat>? LowerStat(IlStat stat)
    {
        switch (stat)
        {
            case IlLocal { Init: { } init } local when Needs(init):
            {
                var target = new IlVar(local.Name);
                var body = LowerInto(init, v => new IlAssign(target, v));
                return [local with { Init = null }, .. Scoped(body, stat)];
            }
            case IlAssign assign when Needs(assign.Value):
                return LowerAssign(assign);
            case IlReturn { Value: { } value } when Needs(value):
                return Scoped(LowerInto(value, v => new IlReturn(v)), stat);
            case IlCallStat call when Needs(call.Call):
            {
                var pre = new List<IlStat>();
                var callee = Lower(call.Call, pre);
                pre.Add(call with { Call = callee });
                return Scoped(pre, stat);
            }
            case IlMultiAssign multi when multi.Values.Any(Needs)
                && multi.Targets.All(t => t is IlVar):
            {
                var pre = new List<IlStat>();
                var values = LowerSeq([.. multi.Values], pre);
                var assign = new IlMultiAssign(multi.Targets, [.. values], false);
                var scoped = Scoped([.. pre, assign], stat);
                return multi.Declare
                    ? [.. multi.Targets.Select(t => (IlStat)new IlLocal(((IlVar)t).Name, null)), .. scoped]
                    : scoped;
            }
            case IlIf ifStat when ifStat.Arms.Any(a => Needs(a.Cond)):
            {
                var pre = new List<IlStat>();
                var cond = Lower(ifStat.Arms[0].Cond, pre);
                var rest = ifStat.Arms.Length > 1
                    ? new IlBlock([new IlIf(ifStat.Arms[1..], ifStat.Else)]) : ifStat.Else;
                pre.Add(new IlIf([(cond, ifStat.Arms[0].Body)], rest));
                return Scoped(pre, stat);
            }
            case IlWhile w when Needs(w.Cond):
            {
                var pre = new List<IlStat>();
                var cond = Lower(w.Cond, pre);
                pre.Add(new IlIf([(new IlUn(IlUnOp.Not, new IlParen(cond)),
                    new IlBlock([new IlBreak()]))], null));
                var head = new IlDo(new IlBlock([.. pre]));
                return [new IlWhile(new IlLit("true"),
                    new IlBlock([head, .. w.Body.Stats]), w.Trailer, w.ScopeBody)
                    { Origin = stat.Origin }];
            }
            case IlNumericFor f when Needs(f.Start) || Needs(f.Limit):
            {
                var pre = new List<IlStat>();
                var bounds = LowerSeq([f.Start, f.Limit], pre);
                pre.Add(f with { Start = bounds[0], Limit = bounds[1] });
                return Scoped(pre, stat);
            }
            case IlForeachList f when Needs(f.Coll):
                return LowerHead(stat, f.Coll, (s, c) => ((IlForeachList)s) with { Coll = c });
            case IlForeachDict f when Needs(f.Coll):
                return LowerHead(stat, f.Coll, (s, c) => ((IlForeachDict)s) with { Coll = c });
            case IlForeachRunes f when Needs(f.Str):
                return LowerHead(stat, f.Str, (s, c) => ((IlForeachRunes)s) with { Str = c });
            case IlForPairs f when Needs(f.Coll):
                return LowerHead(stat, f.Coll, (s, c) => ((IlForPairs)s) with { Coll = c });
            default:
                return null;
        }
    }

    private List<IlStat> LowerHead(IlStat stat, IlExpr coll,
        Func<IlStat, IlExpr, IlStat> rebuild)
    {
        var pre = new List<IlStat>();
        var lowered = Lower(coll, pre);
        pre.Add(rebuild(stat, lowered));
        return Scoped(pre, stat);
    }

    private List<IlStat> LowerAssign(IlAssign assign)
    {
        // 評価が 1 回で済む target (純 local / self 起点の field 連鎖 /
        // 値が呼び出しを含まないときの field 連鎖) は分岐へ直接代入する
        if (assign.Target is IlVar
            || (assign.Target is IlField && IsSimpleReceiverPath(assign.Target)
                && (IsCallFree(assign.Value) || IsSelfRooted(assign.Target))))
            return Scoped(LowerInto(assign.Value,
                v => new IlAssign(assign.Target, v)), assign);

        var pre = new List<IlStat>();
        IlStat result;
        switch (assign.Target)
        {
            case IlField f:
            {
                var k = LowerSeq([f.Recv, assign.Value], pre);
                result = new IlAssign(f with { Recv = k[0] }, k[1]);
                break;
            }
            case IlIndex ix:
            {
                var k = LowerSeq([ix.Recv, ix.Idx, assign.Value], pre);
                result = new IlAssign(ix with { Recv = k[0], Idx = k[1] }, k[2]);
                break;
            }
            default:
                result = assign with { Value = Lower(assign.Value, pre) };
                break;
        }
        pre.Add(result);
        return Scoped(pre, assign);
    }

    private static bool IsSelfRooted(IlExpr e) => e switch
    {
        IlVar v => v.Name == "self",
        IlField f => IsSelfRooted(f.Recv),
        _ => false,
    };

    // until は文を置けないので、条件を本体の後 (continue label の後) で temp に
    // 評価してから `until not temp` に渡す。C# の do-while 条件は本体の local を
    // 参照できないため、本体を do ... end に閉じても意味は変わらない
    private void EmitIlRepeat(IlRepeat repeat)
    {
        var label = PushContinueLabel();
        if (!Needs(repeat.Cond))
        {
            AppendLine("repeat");
            _indent++;
            EmitIlBlock(repeat.Body);
            EmitContinueLabel(label);
            _indent--;
            AppendLine($"until not ({RenderIl(repeat.Cond)})");
            PopContinueLabel();
            return;
        }
        var pre = new List<IlStat>();
        var cond = Lower(repeat.Cond, pre);
        var tmp = NewLowerTemp();
        pre.Add(new IlAssign(new IlVar(tmp), cond));
        AppendLine("do");
        _indent++;
        AppendLine($"local {tmp}");
        AppendLine("repeat");
        _indent++;
        EmitIlStat(new IlDo(repeat.Body));
        EmitContinueLabel(label);
        EmitIlStat(new IlDo(new IlBlock([.. pre])));
        _indent--;
        AppendLine($"until not {tmp}");
        _indent--;
        AppendLine("end");
        PopContinueLabel();
    }
}
