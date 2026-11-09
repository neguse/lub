using System.Collections.Generic;

namespace TcsSelf;

// 確実な代入 (C# §9.4.4) の状態: 代入済みの local の集合。到達できない場所は全ての local を代入済みとみなす
public sealed class DaState
{
    public bool Unreachable;
    public HashSet<LocalSym> Assigned = new HashSet<LocalSym>();

    public DaState Copy()
    {
        var s = new DaState();
        s.Unreachable = Unreachable;
        foreach (var l in Assigned) s.Assigned.Add(l);
        return s;
    }

    public static DaState Dead()
    {
        var s = new DaState();
        s.Unreachable = true;
        return s;
    }

    // 合流: 両方の経路で代入済みの local だけが代入済み (到達できない経路は無視する)
    public static DaState Join(DaState a, DaState b)
    {
        if (a.Unreachable) return b.Copy();
        if (b.Unreachable) return a.Copy();
        var s = new DaState();
        foreach (var l in a.Assigned)
        {
            if (b.Assigned.Contains(l)) s.Assigned.Add(l);
        }
        return s;
    }

    public bool Has(LocalSym l) => Unreachable || Assigned.Contains(l);
}

// 束縛した後の本体を走査して、初期化子なしで宣言した local (LocalSym.Tracked) を代入する前に読んだら CS0165 にする。
// 条件式は真のときと偽のときの状態を分ける (&& || ! と定数)。loop の本体の始まりは loop に入る前の状態
// (本体は代入を足すだけなので、戻ってくる経路との合流は入る前の状態と同じ)。
// pattern と out var の local は宣言した時点で代入済みなので追わない
public sealed class DefiniteAssignment
{
    private readonly Diagnostics _diags;
    private DaState _s = new DaState();
    // break / continue の先 (loop と switch) ごとの、飛んだ時点の状態の合流
    private readonly List<DaState?> _breaks = new List<DaState?>();
    private readonly List<DaState?> _continues = new List<DaState?>();
    private readonly List<bool> _isLoop = new List<bool>();
    private readonly HashSet<LocalSym> _reported = new HashSet<LocalSym>();

    public DefiniteAssignment(Diagnostics diags)
    {
        _diags = diags;
    }

    public void AnalyzeBody(Node body)
    {
        _s = new DaState();
        if (IsStatement(body)) Stmt(body);
        else Expr(body);
    }

    public void AnalyzeStatements(List<Node> stmts)
    {
        _s = new DaState();
        foreach (var st in stmts) Stmt(st);
    }

    private static bool IsStatement(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.Block:
            case NodeKind.LocalDecl:
            case NodeKind.ExprStmt:
            case NodeKind.If:
            case NodeKind.While:
            case NodeKind.DoWhile:
            case NodeKind.For:
            case NodeKind.Foreach:
            case NodeKind.Return:
            case NodeKind.Break:
            case NodeKind.Continue:
            case NodeKind.Switch:
            case NodeKind.Empty:
            case NodeKind.Unsupported:
            case NodeKind.DeconstructDecl:
                return true;
        }
        return false;
    }

    // ---- 文 ----

    private void Stmt(Node s)
    {
        switch (s.Kind)
        {
            case NodeKind.Block:
                if (s.List != null)
                {
                    foreach (var st in s.List) Stmt(st);
                }
                return;
            case NodeKind.LocalDecl:
                if (s.List == null) return;
                foreach (var v in s.List)
                {
                    if (v.A != null)
                    {
                        Expr(v.A);
                        if (v.Local != null) _s.Assigned.Add(v.Local);
                    }
                }
                return;
            case NodeKind.ExprStmt:
                Expr(s.A!);
                return;
            case NodeKind.If:
                {
                    var t = new DaState();
                    var f = new DaState();
                    Cond(s.A!, t, f);
                    _s = t;
                    Stmt(s.B!);
                    var afterThen = _s;
                    _s = f;
                    if (s.C != null) Stmt(s.C);
                    _s = DaState.Join(afterThen, _s);
                    return;
                }
            case NodeKind.While:
                {
                    var t = new DaState();
                    var f = new DaState();
                    Cond(s.A!, t, f);
                    PushTarget(true);
                    _s = t;
                    Stmt(s.B!);
                    _s = JoinBreaks(PopTarget(), f);
                    return;
                }
            case NodeKind.DoWhile:
                {
                    PushTarget(true);
                    Stmt(s.B!);
                    var cont = _continues[_continues.Count - 1];
                    if (cont != null) _s = DaState.Join(_s, cont);
                    var t = new DaState();
                    var f = new DaState();
                    Cond(s.A!, t, f);
                    _s = JoinBreaks(PopTarget(), f);
                    return;
                }
            case NodeKind.For:
                {
                    if (s.List != null)
                    {
                        foreach (var init in s.List)
                        {
                            if (init.Kind == NodeKind.LocalDecl) Stmt(init);
                            else Expr(init.A!);
                        }
                    }
                    var t = _s.Copy();
                    var f = DaState.Dead();
                    if (s.A != null)
                    {
                        t = new DaState();
                        f = new DaState();
                        Cond(s.A, t, f);
                    }
                    PushTarget(true);
                    _s = t;
                    Stmt(s.B!);
                    var cont = _continues[_continues.Count - 1];
                    if (cont != null) _s = DaState.Join(_s, cont);
                    if (s.List2 != null)
                    {
                        foreach (var u in s.List2) Expr(u);
                    }
                    _s = JoinBreaks(PopTarget(), f);
                    return;
                }
            case NodeKind.Foreach:
                {
                    Expr(s.B!);
                    var before = _s.Copy();
                    PushTarget(true);
                    Stmt(s.C!);
                    PopTarget();
                    // 本体は 1 度も走らないことがある
                    _s = before;
                    return;
                }
            case NodeKind.Switch:
                Switch(s);
                return;
            case NodeKind.Return:
                if (s.A != null) Expr(s.A);
                _s = DaState.Dead();
                return;
            case NodeKind.Break:
                Jump(_breaks, false);
                return;
            case NodeKind.Continue:
                Jump(_continues, true);
                return;
            case NodeKind.Unsupported:
                return;
        }
        // DeconstructDecl、文の位置の分解の代入 (DeconstructAssign) など: 式として評価順に見る
        Expr(s);
    }

    private void Switch(Node s)
    {
        Expr(s.A!);
        var entry = _s.Copy();
        PushTarget(false);
        bool hasDefault = false;
        // 定数の switch: どの label も値に合わない section は到達できない。合う section があれば、外へ抜ける経路は無い
        var value = s.A!.HasConst ? s.A.Const : null;
        if (s.List != null)
        {
            foreach (var sec in s.List)
            {
                _s = entry.Copy();
                if (value != null)
                {
                    int match = SectionMatch(sec, value);
                    if (match == 0) _s = DaState.Dead();
                    else if (match == 2) hasDefault = true;
                }
                if (sec.List != null)
                {
                    foreach (var label in sec.List)
                    {
                        if (label.Kind == NodeKind.DefaultLabel) hasDefault = true;
                        else if (label.A != null && label.A.Kind == NodeKind.PatDiscard && label.B == null) hasDefault = true;
                        if (label.Kind == NodeKind.CaseLabel && label.B != null)
                        {
                            // when の式は、その label を通るときだけ評価する
                            var t = new DaState();
                            var f = new DaState();
                            var save = _s;
                            Cond(label.B, t, f);
                            _s = save;
                        }
                    }
                }
                if (sec.List2 != null)
                {
                    foreach (var st in sec.List2) Stmt(st);
                }
            }
        }
        var breaks = PopTarget();
        // どの section にも入らないときは switch の式の後の状態のまま抜ける
        _s = hasDefault ? DaState.Dead() : entry;
        if (breaks != null) _s = DaState.Join(_s, breaks);
    }

    // section の label と定数 v: 0 = どれも合わない、1 = 合うかもしれない、2 = 必ず合う
    private static int SectionMatch(Node sec, ConstValue v)
    {
        int result = 0;
        if (sec.List == null) return 1;
        foreach (var label in sec.List)
        {
            if (label.Kind != NodeKind.CaseLabel) return 1;
            var pat = label.A;
            if (pat == null || pat.Kind != NodeKind.PatConst || pat.A == null || !pat.A.HasConst || pat.A.Const == null) return 1;
            if (SameConst(pat.A.Const, v)) result = label.B == null ? 2 : 1;
        }
        return result;
    }

    private static bool SameConst(ConstValue a, ConstValue b)
    {
        if (a.Kind != b.Kind) return false;
        switch (a.Kind)
        {
            case ConstKind.Int: return a.I == b.I;
            case ConstKind.Bool: return a.B == b.B;
            case ConstKind.String: return a.S == b.S;
            case ConstKind.Float: return a.F == b.F;
        }
        return false;
    }

    private void PushTarget(bool isLoop)
    {
        _breaks.Add(null);
        _continues.Add(null);
        _isLoop.Add(isLoop);
    }

    private DaState? PopTarget()
    {
        int top = _breaks.Count - 1;
        var b = _breaks[top];
        _breaks.RemoveAt(top);
        _continues.RemoveAt(top);
        _isLoop.RemoveAt(top);
        return b;
    }

    private static DaState JoinBreaks(DaState? breaks, DaState normal) => breaks == null ? normal : DaState.Join(normal, breaks);

    // break は一番内側の loop / switch、continue は一番内側の loop へ飛ぶ
    private void Jump(List<DaState?> targets, bool loopOnly)
    {
        int i = targets.Count - 1;
        if (loopOnly)
        {
            while (i >= 0 && !_isLoop[i]) i--;
        }
        if (i >= 0)
        {
            var cur = targets[i];
            targets[i] = cur == null ? _s.Copy() : DaState.Join(cur, _s);
        }
        _s = DaState.Dead();
    }

    // ---- 式 ----

    private static bool IsConstBool(Node e, bool value) =>
        e.HasConst && e.Const != null && e.Const.Kind == ConstKind.Bool && e.Const.B == value;

    // 条件式: 真のときの状態を whenTrue に、偽のときの状態を whenFalse に入れる
    private void Cond(Node e, DaState whenTrue, DaState whenFalse)
    {
        DaState t;
        DaState f;
        // 定数の式は定数でない local を読まないので、中を見ない (! && || を畳んだ式を Expr に渡すと Cond に戻ってくる)
        if (IsConstBool(e, true))
        {
            t = _s.Copy();
            f = DaState.Dead();
        }
        else if (IsConstBool(e, false))
        {
            t = DaState.Dead();
            f = _s.Copy();
        }
        else if (e.Kind == NodeKind.Paren || e.Kind == NodeKind.Conv && e.A != null && e.Type != null && e.Type.Kind == TypeKind.Bool && e.A.Type != null && e.A.Type.Kind == TypeKind.Bool)
        {
            t = new DaState();
            f = new DaState();
            Cond(e.A!, t, f);
        }
        else if (e.Kind == NodeKind.Unary && e.Text == "!" && e.Sym == null)
        {
            t = new DaState();
            f = new DaState();
            Cond(e.A!, f, t);
        }
        else if (e.Kind == NodeKind.Binary && e.Sym == null && (e.Text == "&&" || e.Text == "||"))
        {
            var lt = new DaState();
            var lf = new DaState();
            Cond(e.A!, lt, lf);
            _s = e.Text == "&&" ? lt : lf;
            var rt = new DaState();
            var rf = new DaState();
            Cond(e.B!, rt, rf);
            if (e.Text == "&&")
            {
                t = rt;
                f = DaState.Join(lf, rf);
            }
            else
            {
                t = DaState.Join(lt, rt);
                f = rf;
            }
        }
        else
        {
            Expr(e);
            t = _s.Copy();
            f = _s.Copy();
        }
        Assign(whenTrue, t);
        Assign(whenFalse, f);
        _s = DaState.Join(t, f);
    }

    private static void Assign(DaState into, DaState from)
    {
        into.Unreachable = from.Unreachable;
        into.Assigned = from.Assigned;
    }

    private void Expr(Node? e)
    {
        if (e == null) return;
        switch (e.Kind)
        {
            case NodeKind.Name:
                Read(e);
                Children(e);
                return;
            case NodeKind.Assign:
                if (e.Text == "=")
                {
                    WriteTarget(e.A!);
                    Expr(e.B);
                    MarkAssigned(e.A!);
                }
                else
                {
                    Expr(e.A);
                    if (e.Text == "??=")
                    {
                        // 右辺は左辺が null のときだけ評価する
                        var before = _s.Copy();
                        Expr(e.B);
                        _s = DaState.Join(before, _s);
                    }
                    else Expr(e.B);
                }
                return;
            case NodeKind.DeconstructAssign:
                Expr(e.A);
                Targets(e);
                return;
            case NodeKind.Binary:
                if (e.Sym == null && (e.Text == "&&" || e.Text == "||"))
                {
                    var t = new DaState();
                    var f = new DaState();
                    Cond(e, t, f);
                    return;
                }
                if (e.Text == "??")
                {
                    Expr(e.A);
                    var before = _s.Copy();
                    Expr(e.B);
                    _s = DaState.Join(before, _s);
                    return;
                }
                Expr(e.A);
                Expr(e.B);
                return;
            case NodeKind.Unary:
                if (e.Text == "!" && e.Sym == null)
                {
                    var t = new DaState();
                    var f = new DaState();
                    Cond(e, t, f);
                    return;
                }
                Expr(e.A);
                return;
            case NodeKind.Conditional:
                {
                    var t = new DaState();
                    var f = new DaState();
                    Cond(e.A!, t, f);
                    _s = t;
                    Expr(e.B);
                    var afterTrue = _s;
                    _s = f;
                    Expr(e.C);
                    _s = DaState.Join(afterTrue, _s);
                    return;
                }
            case NodeKind.Call:
                Call(e);
                return;
            case NodeKind.Member:
            case NodeKind.Index:
                Expr(e.A);
                if (e.Value == 1)
                {
                    // ?. ?[ の先は受け手が null でないときだけ評価する
                    var before = _s.Copy();
                    ChildrenExceptA(e);
                    _s = DaState.Join(before, _s);
                }
                else ChildrenExceptA(e);
                return;
            case NodeKind.Lambda:
                Lambda(e);
                return;
            case NodeKind.SwitchExpr:
                SwitchExpr(e);
                return;
            case NodeKind.Is:
                Expr(e.A);
                return;
            case NodeKind.As:
            case NodeKind.Cast:
                Expr(e.B);
                return;
            case NodeKind.Typeof:
            case NodeKind.DefaultLit:
            case NodeKind.TypeName:
            case NodeKind.PredefType:
            case NodeKind.ArrayType:
            case NodeKind.NullableType:
            case NodeKind.TupleType:
                return;
            case NodeKind.PostIncDec:
            case NodeKind.PreIncDec:
                Expr(e.A);
                return;
        }
        Children(e);
    }

    private void Call(Node e)
    {
        var callee = e.A!;
        bool conditional = callee.Kind == NodeKind.Member && callee.Value == 1;
        if (callee.Kind == NodeKind.Member) Expr(callee.A);
        else Expr(callee);
        var before = conditional ? _s.Copy() : null;
        if (e.List != null)
        {
            foreach (var arg in e.List)
            {
                var a = arg.A;
                if (a == null) continue;
                if (arg.Has(Mods.Out))
                {
                    WriteTarget(a);
                    continue;
                }
                Expr(a);
            }
            // out の引数は呼び出しの後で代入済み
            foreach (var arg in e.List)
            {
                if (arg.A != null && arg.Has(Mods.Out)) MarkAssigned(arg.A);
            }
        }
        if (before != null) _s = DaState.Join(before, _s);
    }

    private void Lambda(Node e)
    {
        // lambda の本体は作った時点の状態で見る。本体の代入と jump は外に出ない
        var save = _s;
        var saveBreaks = new List<DaState?>(_breaks);
        var saveContinues = new List<DaState?>(_continues);
        var saveLoop = new List<bool>(_isLoop);
        _breaks.Clear();
        _continues.Clear();
        _isLoop.Clear();
        _s = save.Copy();
        if (e.A != null) Expr(e.A);
        else if (e.B != null) Stmt(e.B);
        _breaks.Clear();
        _continues.Clear();
        _isLoop.Clear();
        foreach (var b in saveBreaks) _breaks.Add(b);
        foreach (var c in saveContinues) _continues.Add(c);
        foreach (var l in saveLoop) _isLoop.Add(l);
        _s = save;
    }

    private void SwitchExpr(Node e)
    {
        Expr(e.A);
        var entry = _s.Copy();
        DaState? result = null;
        if (e.List != null)
        {
            foreach (var arm in e.List)
            {
                _s = entry.Copy();
                if (arm.B != null)
                {
                    var t = new DaState();
                    var f = new DaState();
                    Cond(arm.B, t, f);
                    _s = t;
                }
                Expr(arm.C);
                result = result == null ? _s : DaState.Join(result, _s);
            }
        }
        _s = result ?? entry;
    }

    // 分解の先 (入れ子を含む) を代入済みにする
    private void Targets(Node targets)
    {
        if (targets.List == null) return;
        foreach (var t in targets.List)
        {
            if (t.Kind == NodeKind.DeconstructAssign) Targets(t);
            else if (t.Kind == NodeKind.Name)
            {
                WriteTarget(t);
                MarkAssigned(t);
            }
            else if (t.Kind != NodeKind.PatDecl && t.Kind != NodeKind.PatDiscard) Expr(t);
        }
    }

    // 書き込み先: 名前そのものは読まない。a.F や a[i] の受け手と添字は読む
    private void WriteTarget(Node t)
    {
        if (t.Kind == NodeKind.Name) return;
        if (t.Kind == NodeKind.Paren && t.A != null)
        {
            WriteTarget(t.A);
            return;
        }
        Expr(t);
    }

    private void MarkAssigned(Node t)
    {
        if (t.Kind == NodeKind.Paren && t.A != null)
        {
            MarkAssigned(t.A);
            return;
        }
        if (t.Kind == NodeKind.Name && t.Info == RefKind.Local && t.Local != null && !_s.Unreachable) _s.Assigned.Add(t.Local);
    }

    private void Read(Node e)
    {
        if (e.Info != RefKind.Local || e.Local == null || !e.Local.Tracked) return;
        if (_s.Has(e.Local) || _reported.Contains(e.Local)) return;
        _reported.Add(e.Local);
        _diags.Error("CS0165", "Use of unassigned local variable '" + e.Local.Name + "'", e.File, e.Pos);
    }

    private void Children(Node e)
    {
        Expr(e.A);
        ChildrenExceptA(e);
    }

    private void ChildrenExceptA(Node e)
    {
        Expr(e.B);
        Expr(e.C);
        Expr(e.D);
        if (e.List != null)
        {
            foreach (var x in e.List) Expr(x);
        }
        if (e.List2 != null)
        {
            foreach (var x in e.List2) Expr(x);
        }
        if (e.List3 != null)
        {
            foreach (var x in e.List3) Expr(x);
        }
    }
}
