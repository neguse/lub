using System.Collections.Immutable;
using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    // ---- closure: capture-by-variable (il-spec §7) ----
    // 関数 (method / closure) の本体の直下の closure が捕捉する名前を集め、その
    // 関数の local/param を heap cell (T *) へ box する。名前で判断するので
    // 同名の別変数も box しうるが、過剰 box は無害 (意味等価)
    private void CollectCapturedNames(IlBlock body)
    {
        _capturedNames.Clear();
        AddCapturedNames(body);
    }

    private void AddCapturedNames(IlBlock body) =>
        _capturedNames.UnionWith(CapturedByClosuresIn(body.Stats, null));

    // 本体の中の closure (その内側の closure は FreeNames が見る) が外から
    // 参照する名前
    private static HashSet<string> CapturedByClosuresIn(IEnumerable<IlStat> stats,
        IlExpr? exprBody)
    {
        var names = new HashSet<string>();
        void E(IlExpr e)
        {
            if (e is IlClosure closure) names.UnionWith(FreeNames(closure));
            else Walk(e, E, S);
        }
        void S(IlStat st) => WalkStat(st, E, S);
        if (exprBody != null) E(exprBody);
        foreach (var st in stats) S(st);
        return names;
    }

    // closure が外の変数として参照する名前 (入れ子の closure が参照するものも
    // 含む)。初出順。closure の中の local / ループ変数は外の変数と同じ名前に
    // できる (shadow) ので、宣言は block 単位で束縛する
    private static List<string> FreeNames(IlClosure closure)
    {
        var free = new List<string>();
        void E(IlExpr e, HashSet<string> bound)
        {
            switch (e)
            {
                case IlVar v:
                    if (!bound.Contains(v.Name) && !free.Contains(v.Name))
                        free.Add(v.Name);
                    break;
                case IlClosure nested:
                    foreach (var name in FreeNames(nested))
                        E(new IlVar(name), bound);
                    break;
                case IlIife iife: Block(iife.Stats, bound); break;
                default: Walk(e, x => E(x, bound), st => S(st, bound)); break;
            }
        }
        // 外の block の束縛は copy して持ち込み、内の宣言を外へ漏らさない
        void Block(IEnumerable<IlStat> stats, HashSet<string> outer,
            params string?[] declared)
        {
            var bound = new HashSet<string>(outer);
            foreach (var name in declared)
                if (name != null) bound.Add(name);
            foreach (var st in stats) S(st, bound);
        }
        void S(IlStat st, HashSet<string> bound)
        {
            switch (st)
            {
                case IlLocal l:
                    if (l.Init != null) E(l.Init, bound);
                    bound.Add(l.Name);
                    break;
                case IlIf i:
                    foreach (var (cond, body) in i.Arms)
                    {
                        E(cond, bound);
                        Block(body.Stats, bound);
                    }
                    if (i.Else != null) Block(i.Else.Stats, bound);
                    break;
                case IlWhile w:
                    E(w.Cond, bound);
                    Block(w.Body.Stats, bound);
                    if (w.Trailer != null) Block(w.Trailer.Stats, bound);
                    break;
                case IlRepeat rp:
                    Block(rp.Body.Stats, bound);
                    E(rp.Cond, bound);
                    break;
                case IlNumericFor nf:
                    E(nf.Start, bound); E(nf.Limit, bound);
                    Block(nf.Body.Stats, bound, nf.Var);
                    break;
                case IlForeachList fl:
                    E(fl.Coll, bound);
                    Block(fl.Body.Stats, bound, fl.Var);
                    break;
                case IlForeachDict fd:
                    E(fd.Coll, bound);
                    Block(fd.Body.Stats, bound, fd.Var);
                    break;
                case IlForeachRunes fr:
                    E(fr.Str, bound);
                    Block(fr.Body.Stats, bound, fr.Var);
                    break;
                case IlForPairs fp:
                    E(fp.Coll, bound);
                    Block(fp.Body.Stats, bound, fp.KVar, fp.VVar);
                    break;
                case IlDo d: Block(d.Body.Stats, bound); break;
                case IlBreakScope bs: Block(bs.Body.Stats, bound); break;
                default: WalkStat(st, x => E(x, bound), y => S(y, bound)); break;
            }
        }
        var parameters = new HashSet<string>(closure.Params);
        if (closure.ExprBody != null) E(closure.ExprBody, parameters);
        if (closure.Body != null) Block(closure.Body.Stats, parameters);
        return free;
    }

    private static void Walk(IlExpr e, Action<IlExpr> expr, Action<IlStat> stat)
    {
        switch (e)
        {
            case IlField f: expr(f.Recv); break;
            case IlIndex ix: expr(ix.Recv); expr(ix.Idx); break;
            case IlLen l: expr(l.E); break;
            case IlBin b: expr(b.L); expr(b.R); break;
            case IlUn u: expr(u.E); break;
            case IlParen p: expr(p.E); break;
            case IlTernary t: expr(t.Cond); expr(t.T); expr(t.F); break;
            case IlCall c: foreach (var a in c.Args) expr(a); break;
            case IlDynCall d:
                expr(d.Callee); foreach (var a in d.Args) expr(a); break;
            case IlInvoke i:
                expr(i.Recv); foreach (var a in i.Args) expr(a); break;
            case IlNewObj n: foreach (var a in n.Args) expr(a); break;
            case IlNewArray na: expr(na.Length); break;
            case IlTable t:
                foreach (var en in t.Entries)
                {
                    if (en.Key != null) expr(en.Key);
                    expr(en.Value);
                }
                break;
            case IlIsType it: expr(it.E); break;
            case IlIsRefType it: expr(it.E); break;
            case IlStructCopy sc: expr(sc.E); break;
            case IlCast cast: expr(cast.E); break;
            case IlNumericConvert convert: expr(convert.Value); break;
            case IlRefCast refCast: expr(refCast.Value); break;
            case IlNullableWrap w: expr(w.E); break;
            case IlNullableHasValue h: expr(h.E); break;
            case IlNullableValue v: expr(v.E); break;
            case IlNullableGetOrDefault g: expr(g.E); expr(g.Default); break;
            case IlLiftedBin lb: expr(lb.L); expr(lb.R); break;
            case IlLiftedUn lu: expr(lu.E); break;
            case IlWith w:
                expr(w.Src);
                foreach (var o in w.Overrides) expr(o.Value);
                break;
            case IlIife iife: foreach (var st in iife.Stats) stat(st); break;
        }
    }

    private static void WalkStat(IlStat st, Action<IlExpr> expr,
        Action<IlStat> stat)
    {
        switch (st)
        {
            case IlLocal { Init: not null } l: expr(l.Init); break;
            case IlAssign a: expr(a.Target); expr(a.Value); break;
            case IlMultiAssign m:
                foreach (var t in m.Targets) expr(t);
                foreach (var v in m.Values) expr(v);
                break;
            case IlCallStat c: expr(c.Call); break;
            case IlReturn { Value: not null } r: expr(r.Value); break;
            case IlIf i:
                foreach (var (cond, body) in i.Arms)
                {
                    expr(cond);
                    foreach (var b in body.Stats) stat(b);
                }
                if (i.Else != null)
                    foreach (var b in i.Else.Stats) stat(b);
                break;
            case IlWhile w:
                expr(w.Cond);
                foreach (var b in w.Body.Stats) stat(b);
                if (w.Trailer != null)
                    foreach (var b in w.Trailer.Stats) stat(b);
                break;
            case IlRepeat rp:
                foreach (var b in rp.Body.Stats) stat(b);
                expr(rp.Cond);
                break;
            case IlNumericFor nf:
                expr(nf.Start); expr(nf.Limit);
                foreach (var b in nf.Body.Stats) stat(b);
                break;
            case IlForeachList fl:
                expr(fl.Coll);
                foreach (var b in fl.Body.Stats) stat(b);
                break;
            case IlForeachDict fd:
                expr(fd.Coll);
                foreach (var b in fd.Body.Stats) stat(b);
                break;
            case IlForeachRunes fr:
                expr(fr.Str);
                foreach (var b in fr.Body.Stats) stat(b);
                break;
            case IlForPairs fp:
                expr(fp.Coll);
                foreach (var b in fp.Body.Stats) stat(b);
                break;
            case IlDo d:
                foreach (var b in d.Body.Stats) stat(b);
                break;
            case IlBreakScope bs:
                foreach (var b in bs.Body.Stats) stat(b);
                break;
        }
    }

    private void BoxCapturedParameters()
    {
        foreach (var scopeEntry in _scopes.Peek().ToList())
        {
            if (!_capturedNames.Contains(scopeEntry.Key)) continue;
            var old = scopeEntry.Value;
            if (old.Boxed || old.Type.Kind == CTypeKind.Kvp) continue;
            var cell = new Variable($"c_{old.CName}", old.Type)
                { Boxed = true };
            Line($"{old.Type.CName} *{cell.CName} = " +
                $"tcs_new_cell(sizeof(*{cell.CName}), {LayoutRef(old.Type)});");
            Line($"*{cell.CName} = {old.CName};");
            _scopes.Peek()[scopeEntry.Key] = cell;
        }
    }

    private void FlushPendingClosures()
    {
        foreach (var code in _pendingClosures) _output.Append(code);
        _pendingClosures.Clear();
    }

    private static string ClosureFnPtrType(CType closure, string name = "")
    {
        var parameters = string.Join(", ",
            new[] { "void **" }.Concat(closure.Parameters!
                .Select(p => p.CName)));
        return $"{closure.Element!.CName} (*{name})({parameters})";
    }

    // IlClosure → lifted static 関数 + cell 束縛。closure 値は
    // TcsClosure { fn, cells[] }
    private string RenderClosure(IlClosure closure, CType target)
    {
        if (closure.Params.Length != target.Parameters!.Count)
            throw new Tcs2cException("closure arity mismatch with target type");
        var fnName = $"tcs_closure_{_closureSerial++}";
        // 捕捉 = closure 本体が参照する enclosing の boxed 変数と反復の
        // KeyValuePair の copy。入れ子の closure が参照するものも、lifted 関数の
        // cell 経由で中へ渡すので含める
        var captured = new List<(string Name, Variable Cell)>();
        foreach (var name in FreeNames(closure))
            if (TryResolve(name) is { } cell && (cell.Boxed || cell.Snapshot))
                captured.Add((name, cell));

        // lifted 関数を側帯へ emit (現在の関数を汚さない)
        var declParams = string.Join(", ", new[] { "void **" }
            .Concat(target.Parameters.Select(p => p.CName)));
        _closureDecls.Add(
            $"static {target.Element!.CName} {fnName}({declParams});");
        var saved = _output.Length;
        var savedIndent = _indent;
        // 外側の IIFE の return 束縛を closure 本体へ持ち込まない
        var savedIifes = _iifes;
        _iifes = new Stack<(string Result, string Label, CType Type)>();
        _indent = 0;
        Line($"static {target.Element!.CName}");
        var paramList = string.Join(", ", new[] { "void **cells" }
            .Concat(closure.Params.Select((p, i) =>
                $"{target.Parameters[i].CName} v_{Names.Id(p)}_{i}")));
        Line($"{fnName}({paramList})");
        Line("{");
        _indent++;
        PushScope();
        for (var i = 0; i < closure.Params.Length; i++)
            AddVariable(closure.Params[i],
                new Variable($"v_{Names.Id(closure.Params[i])}_{i}",
                    target.Parameters[i]));
        for (var i = 0; i < captured.Count; i++)
        {
            var (name, outer) = captured[i];
            var cellVar = new Variable($"cell_{Names.Id(name)}", outer.Type)
                { Boxed = outer.Boxed, Snapshot = outer.Snapshot };
            var pointer = outer.Snapshot ? "TcsDictNode *" : $"{outer.Type.CName} *";
            Line($"{pointer}{cellVar.CName} = ({pointer})cells[{i}];");
            AddVariable(name, cellVar);
        }
        // この closure の内側の closure が捕捉する parameter / local を、method と
        // 同じく cell にする
        var outerCaptured = _capturedNames;
        _capturedNames = CapturedByClosuresIn(closure.Body?.Stats ?? [],
            closure.ExprBody);
        BoxCapturedParameters();
        if (closure.ExprBody != null)
        {
            if (target.Element == CType.Void)
                Line($"{RenderExpr(closure.ExprBody)};");
            else
                Line($"return {RenderCoerced(closure.ExprBody, target.Element!)};");
        }
        else
        {
            var outerFact = _currentMethodFact;
            _currentMethodFact = new MethodFact("<closure>", fnName, true,
                target.Element!, [.. closure.Params.Select((p, i) =>
                    new ParameterFact(p, target.Parameters[i]))], null!);
            EmitStats(closure.Body!.Stats);
            _currentMethodFact = outerFact;
        }
        _capturedNames = outerCaptured;
        PopScope();
        _indent--;
        Line("}");
        Line();
        _indent = savedIndent;
        _iifes = savedIifes;
        var code = _output.ToString(saved, _output.Length - saved);
        _output.Length = saved;
        _pendingClosures.Add(code);

        var make = new StringBuilder();
        var closTemp = Temp("closure");
        make.Append($"TcsClosure *{closTemp} = tcs_new_closure(" +
            $"(void *){fnName}, {captured.Count}); ");
        for (var i = 0; i < captured.Count; i++)
            make.Append($"{closTemp}->cells[{i}] = (void *){captured[i].Cell.CName}; ");
        return $"({{ {make}{closTemp}; }})";
    }

    private readonly Dictionary<string, string> _groupThunks = new();

    // static method group → 引数素通しの thunk closure (cells 不使用)
    private string RenderStaticGroupThunk(string cls, string method,
        CType target)
    {
        var fact = _facts.Method(cls, method);
        if (fact.Parameters.Count != target.Parameters!.Count)
            throw new Tcs2cException($"method group arity mismatch: {cls}.{method}");
        var key = $"{cls}.{method}";
        if (!_groupThunks.TryGetValue(key, out var fnName))
        {
            fnName = $"tcs_thunk_{Names.Id(cls)}_{Names.Id(method)}";
            _groupThunks[key] = fnName;
            _closureDecls.Add($"static {fact.ReturnType.CName} {fnName}(" +
                string.Join(", ", new[] { "void **" }
                    .Concat(fact.Parameters.Select(p => p.Type.CName))) + ");");
            var parameters = string.Join(", ", new[] { "void **cells" }
                .Concat(fact.Parameters.Select((p, i) =>
                    $"{p.Type.CName} a{i}")));
            var args = string.Join(", ",
                fact.Parameters.Select((_, i) => $"a{i}"));
            var call = $"{Names.Method(cls, method)}({args})";
            var body = fact.ReturnType == CType.Void
                ? $"(void)cells; {call};" : $"(void)cells; return {call};";
            _pendingClosures.Add(
                $"static {fact.ReturnType.CName}\n{fnName}({parameters})\n" +
                "{\n    " + body + "\n}\n\n");
        }
        var closTemp = Temp("closure");
        return $"({{ TcsClosure *{closTemp} = tcs_new_closure(" +
            $"(void *){fnName}, 0); {closTemp}; }})";
    }
}
