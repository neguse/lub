using System.Collections.Immutable;
using System.Text;
using TinyCs;

namespace TinyCs.Luoc;

internal sealed partial class CEmitter
{
    // ---- closure (T218-m7): capture-by-variable (il-spec §7) ----
    // 本体内のどこかの closure が参照する名前を集め、その local/param を
    // heap cell (T *) へ box する。過剰 box は無害 (意味等価)
    private void CollectCapturedNames(IlBlock body)
    {
        _capturedNames.Clear();
        void Expr(IlExpr e)
        {
            switch (e)
            {
                case IlClosure closure:
                    var bound = new HashSet<string>(closure.Params);
                    void Inner(IlExpr ie)
                    {
                        switch (ie)
                        {
                            case IlVar v when !bound.Contains(v.Name):
                                _capturedNames.Add(v.Name); break;
                            case IlClosure nested:
                                // ネストの param は shadow (過剰側で安全)
                                Expr(nested); break;
                            default: Walk(ie, Inner, InnerStat); break;
                        }
                    }
                    void InnerStat(IlStat st)
                    {
                        if (st is IlLocal l) bound.Add(l.Name);
                        WalkStat(st, Inner, InnerStat);
                    }
                    if (closure.ExprBody != null) Inner(closure.ExprBody);
                    if (closure.Body != null)
                        foreach (var st in closure.Body.Stats) InnerStat(st);
                    break;
                default:
                    Walk(e, Expr, Stat);
                    break;
            }
        }
        void Stat(IlStat st) => WalkStat(st, Expr, Stat);
        foreach (var st in body.Stats) Stat(st);
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
            case IlStructCopy sc: expr(sc.E); break;
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
            case IlForPairs fp:
                expr(fp.Coll);
                foreach (var b in fp.Body.Stats) stat(b);
                break;
            case IlDo d:
                foreach (var b in d.Body.Stats) stat(b);
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
                $"tcs_alloc(sizeof(*{cell.CName}));");
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
            throw new LuocException("closure arity mismatch with target type");
        var fnName = $"tcs_closure_{_closureSerial++}";
        // 捕捉 = closure 本体が参照する enclosing の boxed 変数
        var captured = new List<(string Name, Variable Cell)>();
        var bound = new HashSet<string>(closure.Params);
        void Note(string name)
        {
            if (bound.Contains(name)) return;
            if (captured.Any(c => c.Name == name)) return;
            if (TryResolve(name) is { Boxed: true } cell)
                captured.Add((name, cell));
        }
        void E(IlExpr e)
        {
            if (e is IlVar v) Note(v.Name);
            else Walk(e, E, S);
        }
        void S(IlStat st)
        {
            if (st is IlLocal l) bound.Add(l.Name);
            WalkStat(st, E, S);
        }
        if (closure.ExprBody != null) E(closure.ExprBody);
        if (closure.Body != null)
            foreach (var st in closure.Body.Stats) S(st);

        // lifted 関数を側帯へ emit (現在の関数を汚さない)
        var declParams = string.Join(", ", new[] { "void **" }
            .Concat(target.Parameters.Select(p => p.CName)));
        _closureDecls.Add(
            $"static {target.Element!.CName} {fnName}({declParams});");
        var saved = _output.Length;
        var savedIndent = _indent;
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
            var cellVar = new Variable($"cell_{Names.Id(captured[i].Name)}",
                captured[i].Cell.Type) { Boxed = true };
            Line($"{captured[i].Cell.Type.CName} *{cellVar.CName} = " +
                $"({captured[i].Cell.Type.CName} *)cells[{i}];");
            AddVariable(captured[i].Name, cellVar);
        }
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
        PopScope();
        _indent--;
        Line("}");
        Line();
        _indent = savedIndent;
        var code = _output.ToString(saved, _output.Length - saved);
        _output.Length = saved;
        _pendingClosures.Add(code);

        var make = new StringBuilder();
        var closTemp = Temp("closure");
        make.Append($"TcsClosure *{closTemp} = tcs_alloc(sizeof(TcsClosure) " +
            $"+ {Math.Max(captured.Count, 1)} * sizeof(void *)); ");
        make.Append($"{closTemp}->fn = (void *){fnName}; ");
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
            throw new LuocException($"method group arity mismatch: {cls}.{method}");
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
        return $"({{ TcsClosure *{closTemp} = tcs_alloc(sizeof(TcsClosure) " +
            $"+ sizeof(void *)); {closTemp}->fn = (void *){fnName}; " +
            $"{closTemp}; }})";
    }
}
