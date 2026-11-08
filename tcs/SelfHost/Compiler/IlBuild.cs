using System.Collections.Generic;

namespace TcsSelf;

// 束縛済みの木 → IL。束縛の結果 (Node の Type / Sym / Info と Conv の node) から IL を作る。
// 意味の決定 (整数の除算、連結、添字、facade の解決) はここで済ませ、未対応の構文は null を返す
public sealed class IlBuild
{
    private readonly IlContext Ctx;

    public IlBuild(IlContext ctx)
    {
        Ctx = ctx;
    }

    // 写していない形。比較 (参照実装との出力の一致) で見えるよう内部の誤りに記録する
    private IlExpr? Unported(string what, Node at)
    {
        Ctx.Internal("IlBuild: " + what + " (" + at.Kind + " at " + at.File.Path + ":" + at.File.LineOf(at.Pos) + ")");
        return null;
    }

    private bool UnportedStat(string what, Node at)
    {
        Unported(what, at);
        return false;
    }

    // 構文の位置 (同じ node には同じ IlPos)
    public static IlPos Origin(Node node)
    {
        if (node.IlOrigin == null)
        {
            node.IlOrigin = new IlPos(node.File.Path, node.File.LineOf(node.Pos));
            node.IlOrigin.Col = node.File.ColumnOf(node.Pos);
        }
        return node.IlOrigin;
    }

    // ---- 文 ----

    public bool BuildStatsInto(List<Node>? statements, List<IlStat> acc)
    {
        if (statements == null) return true;
        foreach (var stmt in statements)
        {
            if (!BuildStatInto(stmt, acc)) return false;
        }
        return true;
    }

    private IlBlock? BuildBlock(Node stmt)
    {
        var acc = new List<IlStat>();
        bool ok = stmt.Kind == NodeKind.Block ? BuildStatsInto(stmt.List, acc) : BuildStatInto(stmt, acc);
        return ok ? new IlBlock(acc) : null;
    }

    // var で宣言した参照型の local は、#nullable enable の文脈では null 許容の注釈が付く (Roslyn の表示)
    private static TypeSym LocalDisplayType(Node decl, TypeSym t)
    {
        bool isVar = decl.A != null && decl.A.Kind == NodeKind.TypeName && decl.A.Text == "var" && decl.A.A == null && decl.A.List == null;
        if (!isVar) return t;
        // 無効の文脈の var は注釈を持たない (初期化子の型の注釈も落ちる)
        if (!decl.File.NullableEnabled || t.Oblivious) return t.WithoutAnnotation();
        if (t.Annotated) return t;
        if (t.IsValueType && !(t.Kind == TypeKind.TypeParam && !t.IsStructParam) || t.IsError || t.Kind == TypeKind.Null) return t;
        return t.WithAnnotation();
    }

    // out var / is-pattern の designation の前宣言。文と、method の式本体 (=>) の前に置く
    private void AddPreDecls(Node node, List<IlStat> acc)
    {
        var origin = Origin(node);
        foreach (var d in CollectPreDecls(node))
        {
            var dl = d.Local;
            // discard (`out var _`) は型を持たない
            acc.Add(new IlLocal(dl != null ? dl.LuaName : "_", null, dl != null && d.Text != "_" ? IlTypes.Display(dl.Type) : null, origin));
        }
    }

    public bool BuildStatInto(Node stmt, List<IlStat> acc)
    {
        var origin = Origin(stmt);
        if (stmt.Kind != NodeKind.Block) AddPreDecls(stmt, acc);
        switch (stmt.Kind)
        {
            case NodeKind.Return:
            {
                if (stmt.A != null && IsInMultiReturnMethod()) return BuildTupleReturnInto(stmt.A, origin, acc);
                IlExpr? value = null;
                if (stmt.A != null)
                {
                    value = BuildExpr(stmt.A);
                    if (value == null) return false;
                    value = WrapStructCopy(stmt.A, value);
                }
                AddReturnStat(acc, value, origin);
                return true;
            }
            case NodeKind.LocalDecl:
            {
                if (stmt.List == null) return true;
                foreach (var v in stmt.List)
                {
                    IlExpr? init = null;
                    if (v.A != null)
                    {
                        init = BuildExpr(v.A);
                        if (init == null) return false;
                        init = WrapStructCopy(v.A, init);
                    }
                    var name = v.Local != null ? v.Local.LuaName : v.Text;
                    var declaredType = v.Local != null ? IlTypes.Display(LocalDisplayType(stmt, v.Local.Type)) : null;
                    // 初期化子の条件式は文にする (return の位置と同じ方針)
                    if (init is IlTernary lt)
                    {
                        var varNode = new IlVar(name, null);
                        acc.Add(new IlLocal(name, null, declaredType, origin));
                        var thenB = new IlBlock(One(new IlAssign(varNode, lt.T, null)));
                        var elseB = new IlBlock(One(new IlAssign(varNode, lt.F, null)));
                        acc.Add(new IlIf(Arm(lt.Cond, thenB), elseB, origin));
                        continue;
                    }
                    if (init is IlIife li)
                    {
                        var shape = TryGetValueChainShape(li);
                        if (shape != null)
                        {
                            var varNode = new IlVar(name, null);
                            acc.Add(new IlLocal(name, null, declaredType, origin));
                            foreach (var st in shape.Setup) acc.Add(IlTree.WithOrigin(st, origin));
                            if (shape.Chain != null) acc.Add(IlTree.WithOrigin(SwitchChainAsAssigns(shape.Chain, varNode), origin));
                            else acc.Add(new IlAssign(varNode, shape.Tail!, origin));
                            continue;
                        }
                    }
                    acc.Add(new IlLocal(name, init, declaredType, origin));
                }
                return true;
            }
            case NodeKind.ExprStmt:
                return BuildExprStatInto(stmt.A!, stmt, acc);
            case NodeKind.If:
            {
                var built = BuildIf(stmt);
                if (built == null) return false;
                // root の条件の値を返す IIFE は if の前へ出す (root の条件は無条件に最初に評価されるので評価の順は変わらない。
                // elseif の条件は前の条件が偽のときだけ評価されるので対象外)
                if (built.Arms[0].Cond is IlIife condIife)
                {
                    var shape = TryGetValueChainShape(condIife);
                    if (shape != null)
                    {
                        var condVar = new IlVar("__tcs_cond" + _condHoist, null);
                        _condHoist++;
                        acc.Add(new IlLocal(condVar.Name, null, "bool", origin));
                        foreach (var st in shape.Setup) acc.Add(IlTree.WithOrigin(st, origin));
                        if (shape.Chain != null) acc.Add(IlTree.WithOrigin(SwitchChainAsAssigns(shape.Chain, condVar), origin));
                        else acc.Add(new IlAssign(condVar, shape.Tail!, origin));
                        var arms = new List<IlArm>();
                        arms.Add(new IlArm(condVar, built.Arms[0].Body));
                        for (int i = 1; i < built.Arms.Count; i++) arms.Add(built.Arms[i]);
                        built = built with { Arms = arms };
                    }
                }
                acc.Add(built with { Origin = origin });
                return true;
            }
            case NodeKind.While:
            {
                var cond = BuildExpr(stmt.A!);
                var body = BuildBlock(stmt.B!);
                if (cond == null || body == null) return false;
                acc.Add(new IlWhile(cond, body, null, false, origin));
                return true;
            }
            case NodeKind.Block:
                // do..end は挟まず平らにする
                return BuildStatsInto(stmt.List, acc);
            case NodeKind.Break:
                acc.Add(new IlBreak(origin));
                return true;
            case NodeKind.Continue:
                acc.Add(new IlContinue(origin));
                return true;
            case NodeKind.DoWhile:
            {
                var body = BuildBlock(stmt.B!);
                var cond = BuildExpr(stmt.A!);
                if (body == null || cond == null) return false;
                // 条件は本体の後に自分の位置で評価される (--trace)
                acc.Add(new IlRepeat(body, WithExprOrigin(cond, Origin(stmt.A!)), origin));
                return true;
            }
            case NodeKind.Empty:
                return true;
            case NodeKind.DeconstructAssign:
                return BuildDeconstructInto(stmt, stmt.A!, origin, acc);
            case NodeKind.For:
                return BuildForInto(stmt, acc);
            case NodeKind.Foreach:
                return BuildForeachInto(stmt, acc);
            case NodeKind.Switch:
                return BuildSwitchStatInto(stmt, acc);
        }
        return UnportedStat("statement", stmt);
    }

    private IlIf? BuildIf(Node ifStmt)
    {
        var arms = new List<IlArm>();
        IlBlock? elseBlock = null;
        var current = ifStmt;
        while (true)
        {
            var cond = BuildExpr(current.A!);
            var body = BuildBlock(current.B!);
            if (cond == null || body == null) return null;
            // elseif の条件は if 文の位置でなく自分の位置で評価される (--trace)
            arms.Add(new IlArm(current == ifStmt ? cond : WithExprOrigin(cond, Origin(current.A!)), body));
            if (current.C == null) break;
            if (current.C.Kind == NodeKind.If)
            {
                if (CollectPreDecls(current.C).Count > 0)
                {
                    var nested = new List<IlStat>();
                    if (!BuildStatInto(current.C, nested)) return null;
                    elseBlock = new IlBlock(nested);
                    break;
                }
                current = current.C;
                continue;
            }
            elseBlock = BuildBlock(current.C);
            if (elseBlock == null) return null;
            break;
        }
        return new IlIf(arms, elseBlock, null);
    }

    private void AddReturnStat(List<IlStat> acc, IlExpr? value, IlPos? origin)
    {
        if (value is IlTernary ternary)
        {
            var thenB = new IlBlock(One(new IlReturn(ternary.T, null)));
            var elseB = new IlBlock(One(new IlReturn(ternary.F, null)));
            acc.Add(new IlIf(Arm(ternary.Cond, thenB), elseB, origin));
            return;
        }
        // switch 式は inline (return がそのまま効く)。else の無いものは値の無い return に変わるのを避けて IIFE のまま
        if (value is IlIife iife)
        {
            var shape = TryGetValueChainShape(iife);
            if (shape != null && shape.Complete)
            {
                foreach (var st in shape.Setup) acc.Add(IlTree.WithOrigin(st, origin));
                if (shape.Chain != null) acc.Add(IlTree.WithOrigin(shape.Chain, origin));
                else acc.Add(new IlReturn(shape.Tail, origin));
                return;
            }
        }
        acc.Add(new IlReturn(value, origin));
    }

    private int _condHoist;

    // 値を返す IIFE の形を認識する。対象は [IlLocal*, IlIf (各 arm = 前置の文* + 末尾の IlReturn、else も同じ形)?, IlReturn?]
    // (switch 式 / ?. / ?? / TryGetValue / GetValueOrDefault を下ろしたもの)。末尾の IlReturn は else へ正規化する。
    // Complete = 全経路が値を返す (false なら一致しないとき nil。return の位置では値の無い return になるので使えない)
    private static ValueChain? TryGetValueChainShape(IlIife iife)
    {
        var stats = new List<IlStat>();
        foreach (var st in iife.Stats) stats.Add(st);
        IlReturn? trailing = null;
        if (stats.Count > 0 && stats[stats.Count - 1] is IlReturn tr && tr.Value != null)
        {
            trailing = tr;
            stats.RemoveAt(stats.Count - 1);
        }
        // 線形の形 (分岐なし): [前置の宣言*, return v]
        if (trailing != null && LeadingOk(stats, stats.Count))
            return new ValueChain(stats, null, trailing.Value, true);
        if (stats.Count == 0 || !(stats[stats.Count - 1] is IlIf tail)) return null;
        if (!LeadingOk(stats, stats.Count - 1)) return null;
        if (tail.Else != null && trailing != null) return null;
        foreach (var a in tail.Arms)
        {
            if (!IsReturningBlock(a.Body)) return null;
        }
        if (tail.Else != null && !IsReturningBlock(tail.Else)) return null;
        var setup = new List<IlStat>();
        for (int i = 0; i < stats.Count - 1; i++) setup.Add(stats[i]);
        IlIf chain = tail;
        if (trailing != null) chain = tail with { Else = new IlBlock(One(trailing)) };
        return new ValueChain(setup, chain, null, tail.Else != null || trailing != null);
    }

    // 前置の文は無条件に実行する宣言・代入・呼び出しだけ (前へ出しても同じ)
    private static bool LeadingOk(List<IlStat> stats, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var st = stats[i];
            if (st is IlLocal || st is IlAssign || st is IlCallStat) continue;
            if (st is IlMultiAssign ma && ma.Declare) continue;
            return false;
        }
        return true;
    }

    private static bool IsReturningBlock(IlBlock block)
    {
        int n = block.Stats.Count;
        if (n == 0) return false;
        if (!(block.Stats[n - 1] is IlReturn r) || r.Value == null) return false;
        for (int i = 0; i < n - 1; i++)
        {
            if (block.Stats[i] is IlReturn) return false;
        }
        return true;
    }

    // 各 arm の末尾の return を target への代入にした if の連鎖
    private static IlIf SwitchChainAsAssigns(IlIf chain, IlExpr target)
    {
        var arms = new List<IlArm>();
        foreach (var a in chain.Arms) arms.Add(new IlArm(a.Cond, RewriteReturnAsAssign(a.Body, target)));
        return new IlIf(arms, chain.Else != null ? RewriteReturnAsAssign(chain.Else, target) : null, chain.Origin);
    }

    private static IlBlock RewriteReturnAsAssign(IlBlock body, IlExpr target)
    {
        var stats = new List<IlStat>();
        int n = body.Stats.Count;
        for (int i = 0; i < n - 1; i++) stats.Add(body.Stats[i]);
        stats.Add(new IlAssign(target, ((IlReturn)body.Stats[n - 1]).Value!, null));
        return new IlBlock(stats);
    }

    // 式本体 (=>) の値は return 文の値と同じ copy 地点 (il-spec §10)。void の本体は式文なので copy しない
    // method の式本体は void でも return の文にする (void の method なら backend は式を評価して捨ててから戻る)。値の copy は void でない本体だけ
    public bool BuildReturnInto(Node expr, List<IlStat> acc)
    {
        if (IsInMultiReturnMethod()) return BuildTupleReturnInto(expr, Origin(expr), acc);
        AddPreDecls(expr, acc);
        var sym = Ctx.CurrentFunction;
        var built = BuildExpr(expr);
        if (built == null) return false;
        bool isVoid = sym != null && sym.Type.Kind == TypeKind.Void;
        AddReturnStat(acc, isVoid ? built : WrapStructCopy(expr, built), Origin(expr));
        return true;
    }

    // 文の位置の式
    public bool BuildExprStatInto(Node expr, Node? originNode, List<IlStat> acc)
    {
        IlPos? origin = originNode != null ? Origin(originNode) : null;
        if (expr.Kind == NodeKind.PostIncDec || expr.Kind == NodeKind.PreIncDec)
            return BuildIncrementInto(expr.A!, expr.Text == "++", origin, acc);
        switch (expr.Kind)
        {
            case NodeKind.Call:
            {
                if (expr.CondTop && expr.CondChain)
                {
                    var condCall = BuildExpr(expr);
                    if (condCall == null) return false;
                    acc.Add(new IlCallStat(condCall, origin));
                    return true;
                }
                if (IsUserOutMethod(expr.Sym)) return BuildUserOutStatInto(expr, expr.Sym!, origin, acc);
                // multi-return の method の文の位置の呼び出しは値を詰めない
                if (expr.Sym != null && IsMultiReturnMethod(expr.Sym))
                {
                    var tupleCall = BuildInvocationCall(expr);
                    if (tupleCall == null) return false;
                    acc.Add(new IlCallStat(tupleCall, origin));
                    return true;
                }
                if (BuildDictAddStatInto(expr, origin, acc)) return true;
                if (IsRefOutCall(expr)) return BuildRefMultiReturnStatInto(expr, origin, acc);
                var call = BuildExpr(expr);
                if (call == null) return false;
                acc.Add(new IlCallStat(call, origin));
                return true;
            }
            case NodeKind.Assign:
                return BuildAssignInto(expr, origin, acc);
        }
        return UnportedStat("expression statement", expr);
    }

    // 値の copy 地点 (il-spec §10): 代入 / 引数 / return / 値の文脈の読み。新しい値は他から参照されないので copy しない。
    // readonly な struct は不変なので copy を省く。struct の T? も、値があれば中の struct を copy する
    public IlExpr WrapStructCopy(Node src, IlExpr built)
    {
        if (IsFreshStructIl(built) || IsFreshStructValue(src)) return built;
        // 式の自然な型 (暗黙の変換の前)。自然な型を持たない式 (target-typed な `c ? h.F : null`) は変換先の型
        var t = StripConv(src).Type;
        if (t == null || t.Kind == TypeKind.Null || t.Kind == TypeKind.Error) t = src.Type;
        if (t == null) return built;
        if (IlTypes.IsNullableValueType(t) && IlTypes.IsMutableStruct(t.Elem))
            return new IlStructCopy(built, TypeName(t.Elem), true, StructCopiers(t.Elem, false), null);
        return CopyIfMutableStruct(t, built);
    }

    // 書き換える / lambda が捕捉する struct の parameter を入口で copy した値 (il-spec §10)。copy しないなら null。
    // 呼ばれた側の判断を本文の中に閉じるので、本文だけの hot reload で判断が古くならない
    public IlExpr? ParameterEntryCopy(Node fn, Node p)
    {
        if (Ctx.KeepsArgumentCopies) return null;
        // out parameter は呼び出し元の値を受け取らないので copy する値が無い
        if (p.Has(Mods.Out) || p.Local == null) return null;
        var t = p.Local.Type;
        if (!CopiesOnEntryWhenWritten(t)) return null;
        if (!(IsCapturedByLambda(fn, p.Local) || IsWritten(fn, p.Local, false))) return null;
        return CopyIfMutableStruct(t, new IlVar(IlNames.ParamName(p), null));
    }

    // 入口の copy の対象。generic な struct は呼び出し側だけが型引数の copier を持つので除く
    private static bool CopiesOnEntryWhenWritten(TypeSym? t)
    {
        if (!IlTypes.IsMutableStruct(t)) return false;
        return t!.Kind == TypeKind.Struct && (t.Args == null || t.Args.Count == 0);
    }

    // n の中に variable を指す識別子があるか
    private static bool Mentions(Node n, LocalSym variable)
    {
        if (n.Kind == NodeKind.Name && n.Local == variable) return true;
        return AnyChild(n, c => Mentions(c, variable));
    }

    private static bool IsCapturedByLambda(Node scope, LocalSym variable)
    {
        if (scope.Kind == NodeKind.Lambda) return Mentions(scope, variable);
        return AnyChild(scope, c => IsCapturedByLambda(c, variable));
    }

    // n の中に、variable の値を書き換えうる出現があるか。parentIsAssign は n が代入の直接の子であること
    private static bool IsWritten(Node n, LocalSym variable, bool parentIsAssign)
    {
        switch (n.Kind)
        {
            case NodeKind.Assign:
                if (RootsAt(n.A!, variable)) return true;
                if (IsWritten(n.A!, variable, true)) return true;
                return n.B != null && IsWritten(n.B, variable, true);
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                if (RootsAt(n.A!, variable)) return true;
                break;
            case NodeKind.Arg:
                if ((n.Has(Mods.Ref) || n.Has(Mods.Out) || n.Has(Mods.In)) && n.A != null && RootsAt(n.A, variable)) return true;
                break;
            case NodeKind.Tuple:
            case NodeKind.DeconstructAssign:
                if (n.List != null)
                {
                    foreach (var e in n.List)
                    {
                        var x = e.Kind == NodeKind.Arg ? e.A : e;
                        if (x != null && RootsAt(x, variable)) return true;
                    }
                }
                break;
            case NodeKind.Call:
            {
                // readonly でない instance method の呼び出しは受け手を書き換えうる
                var callee = n.A;
                if (callee != null && callee.Kind == NodeKind.Member && callee.A != null && n.Sym != null
                    && n.Info != CallKind.Extension && n.Sym.Kind == SymKind.Method && !n.Sym.IsStatic
                    && n.Sym.Builtin == "" && !(n.Sym.Decl != null && n.Sym.Decl.Has(Mods.Readonly))
                    && RootsAt(callee.A, variable))
                    return true;
                break;
            }
            case NodeKind.Member:
                // getter が readonly でない property の読みも受け手を書き換えうる (代入の直接の子は除く)
                if (!parentIsAssign && n.A != null && n.Sym != null && n.Sym.Kind == SymKind.Property && n.Sym.Builtin == ""
                    && !n.Sym.IsStatic && !n.Sym.IsAuto && !(n.Sym.Decl != null && n.Sym.Decl.Has(Mods.Readonly))
                    && RootsAt(n.A, variable))
                    return true;
                break;
        }
        return AnyChild(n, c => IsWritten(c, variable, false));
    }

    // e が variable の値の中の場所か (括弧と、値型の field / 自動実装の property をたどる)
    private static bool RootsAt(Node e, LocalSym variable)
    {
        switch (e.Kind)
        {
            case NodeKind.Paren:
            case NodeKind.Conv:
                return RootsAt(e.A!, variable);
            case NodeKind.Name:
                return e.Local == variable;
            case NodeKind.Member:
                if (e.A == null || e.Sym == null) return false;
                if (e.Sym.Kind != SymKind.Field && !(e.Sym.Kind == SymKind.Property && (e.Sym.IsAuto || e.Sym.Builtin != "")))
                    return false;
                if (e.Type == null || !e.Type.IsValueType) return false;
                return RootsAt(e.A, variable);
        }
        return false;
    }

    // 呼び出しの間に変わらない場所 (lambda が捕捉しない local / parameter で、後の引数と受け手に現れないもの) の引数は copy を省く
    private bool CanShareArgument(Symbol? callee, List<Node> args, int offset, int index, Node? receiver)
    {
        if (Ctx.KeepsArgumentCopies || callee == null || callee.Params == null) return false;
        if (index + offset >= callee.Params.Count) return false;
        var parameter = callee.Params[index + offset];
        if (parameter.IsOut || !CopiesOnEntryWhenWritten(parameter.Type)) return false;
        if (callee.Owner == null || callee.Owner.IsRef || callee.Builtin != "") return false;
        if (callee.Decl == null || !(callee.Decl.Kind == NodeKind.Method || callee.Decl.Kind == NodeKind.OperatorDecl || callee.Decl.Kind == NodeKind.Ctor)) return false;
        var arg = args[index];
        if (arg.Kind != NodeKind.Name || arg.Local == null) return false;
        var variable = arg.Local;
        if (ScopeRoots.Count == 0) return false;
        var scope = ScopeRoots[ScopeRoots.Count - 1];
        if (IsCapturedByLambda(scope, variable)) return false;
        for (int i = index + 1; i < args.Count; i++)
        {
            if (Mentions(args[i], variable)) return false;
        }
        if (receiver != null && Mentions(receiver, variable)) return false;
        return true;
    }

    // 呼び出しの引数の式 (Arg の中身)
    private static List<Node> ArgExprs(Node call)
    {
        var r = new List<Node>();
        if (call.List == null) return r;
        foreach (var a in call.List) r.Add(a.A!);
        return r;
    }

    // IL の式としての型にする (C backend は三項と ?? の両側が同じ型であることを求める)
    private static IlExpr Ex(IlExpr e)
    {
        return e;
    }

    // ---- 式 ----

    public IlExpr? BuildExpr(Node expr)
    {
        if (_subNode != null && expr == _subNode) return _subIl;
        if (expr.CondTop && expr.CondChain && expr != _condBuilding) return BuildConditionalTop(expr);
        switch (expr.Kind)
        {
            case NodeKind.Conv:
                return BuildConversion(expr);
            case NodeKind.IntLit:
            case NodeKind.FloatLit:
            case NodeKind.StringLit:
            case NodeKind.CharLit:
            case NodeKind.BoolLit:
            case NodeKind.NullLit:
                return BuildLiteral(expr);
            case NodeKind.Paren:
            {
                var inner = BuildExpr(expr.A!);
                return inner == null ? null : new IlParen(inner, null);
            }
            // checked / unchecked は中の演算の文脈 (CheckedIntCall)
            case NodeKind.Checked:
            {
                bool outer = _checked;
                _checked = expr.Value == 1;
                var inner = BuildExpr(expr.A!);
                _checked = outer;
                return inner == null ? null : new IlParen(inner, null);
            }
            case NodeKind.This:
                return new IlVar("self", null);
            case NodeKind.Name:
                return BuildIdentifier(expr);
            case NodeKind.InterpString:
                return BuildInterpolatedString(expr);
            case NodeKind.Binary:
                return BuildBinary(expr);
            case NodeKind.Call:
                return BuildInvocation(expr);
            case NodeKind.Member:
                return BuildMemberAccess(expr);
            case NodeKind.New:
                return BuildObjectCreation(expr);
            case NodeKind.Unary:
                return BuildPrefixUnary(expr);
            case NodeKind.Assign:
                return BuildAssignExpr(expr);
            case NodeKind.Tuple:
                return BuildTupleLiteral(expr);
            case NodeKind.PreIncDec:
                return BuildIncrementExpr(expr.A!, expr.Text == "++", true);
            case NodeKind.PostIncDec:
                return BuildIncrementExpr(expr.A!, expr.Text == "++", false);
            case NodeKind.Cast:
                return BuildCast(expr);
            case NodeKind.Conditional:
            {
                var cond = BuildExpr(expr.A!);
                var t = BuildExpr(expr.B!);
                var f = BuildExpr(expr.C!);
                if (cond == null || t == null || f == null) return null;
                return new IlTernary(cond, t, f, null);
            }
            case NodeKind.Index:
                return BuildElementAccess(expr);
            case NodeKind.DefaultLit:
                return DefaultIl(expr.Type);
            case NodeKind.Is:
                return BuildIsPattern(expr);
            case NodeKind.As:
                return BuildAsExpr(expr);
            case NodeKind.SwitchExpr:
                return BuildSwitchExpression(expr);
            case NodeKind.Lambda:
                return BuildLambda(expr);
            case NodeKind.NewArray:
                return BuildNewArray(expr);
            case NodeKind.ArrayInit:
            {
                var at = expr.Type;
                return BuildArrayItems(expr, at != null && at.Kind == TypeKind.Array ? IlTypes.Display(at.Elem) : null);
            }
        }
        return Unported("expression", expr);
    }

    // 暗黙と明示の変換 (Binder の Conv)。参照実装の ApplyNumericConversion / ApplyNullableConversion
    private IlExpr? BuildConversion(Node conv)
    {
        bool promoted = conv == _promotedOperand;
        _promotedOperand = null;
        // 数の変換は括弧の中の式に掛ける (Roslyn は括弧の中の式にも変換先の型を付け、参照実装は内側で変換する)
        if ((conv.Info == ConvKind.IntToFloat || conv.Info == ConvKind.NullableIntToFloat) && conv.A!.Kind == NodeKind.Paren && !promoted)
        {
            var innerConv = new Node(NodeKind.Conv, conv.File, conv.Pos);
            innerConv.End = conv.End;
            innerConv.A = conv.A.A;
            innerConv.Type = conv.Type;
            innerConv.Info = conv.Info;
            innerConv.HasConst = conv.HasConst;
            innerConv.Const = conv.Const;
            var converted = BuildConversion(innerConv);
            return converted == null ? null : new IlParen(converted, null);
        }
        var inner = BuildExpr(conv.A!);
        if (inner == null) return null;
        switch (conv.Info)
        {
            case ConvKind.Identity:
                // binder が畳んだ整数の定数の float への変換 (float の literal にする。算術の operand は除く)
                if (conv.HasConst && conv.Const != null && conv.Const.Kind == ConstKind.Float && !promoted
                    && IlTypes.UnwrapNullable(conv.Type!).Kind == TypeKind.Float && IlTypes.IsIntegralSource(IlTypes.UnwrapNullable(NaturalType(conv.A!)))
                    && !IsFloatConverted(inner))
                    inner = FloatLit(conv.Const.F);
                return ApplyNullableConversion(conv, inner);
            case ConvKind.MethodGroup:
                return inner;
            case ConvKind.NullableIntToFloat:
            {
                // int? → float? は値があるときだけ変換する
                if (IlTypes.UnwrapNullable(conv.Type!).Kind != TypeKind.Float) return inner;
                if (IsFloatConverted(inner) || promoted) return inner;
                if (!IlTypes.IsNullableValueType(NaturalType(conv.A!))) return ApplyNullableConversion(conv, new IlNumericConvert(inner, "float", null, null));
                return new IlLiftedUn(IlUnOp.ToFloat, inner, null);
            }
            case ConvKind.IntToFloat:
            {
                // 変換先が float (double ではない) のときだけ明示の変換にする
                if (IlTypes.UnwrapNullable(conv.Type!).Kind != TypeKind.Float) return ApplyNullableConversion(conv, inner);
                if (IsFloatConverted(inner) || promoted) return ApplyNullableConversion(conv, inner);
                if (conv.A!.HasConst && conv.A.Const != null)
                {
                    var c = conv.A.Const;
                    float f = c.Kind == ConstKind.Float ? c.F : (float)c.I;
                    return ApplyNullableConversion(conv, FloatLit(f));
                }
                // int → float? は数の変換の後に T? へ包む
                return ApplyNullableConversion(conv, new IlNumericConvert(inner, "float", null, null));
            }
        }
        return Unported("conversion " + conv.Info, conv);
    }

    // 組み込みの float の算術 (+ - * / %) の operand は VM と C の演算が int を float に揃えるので明示の変換を省く (BuildBinary が印を付ける)
    private bool IsPromotedArithmeticOperand(Node conv)
    {
        return conv == _promotedOperand;
    }

    private Node? _promotedOperand;
    // checked の文脈の中か (i32 の + - * と単項 - は overflow で fault する helper)
    private bool _checked;

    private static bool IsFloatConverted(IlExpr built)
    {
        while (built is IlParen paren) built = paren.E;
        if (built is IlNumericConvert nc && nc.TargetType == "float") return true;
        if (built is IlLit lit && lit.Type == "float") return true;
        if (built is IlLiftedUn lu && lu.Op == IlUnOp.ToFloat) return true;
        return false;
    }

    // f32 の literal。負の数は括弧で包む (`a - -1.0` の連結で `--` にしない)
    public static IlExpr FloatLit(float value)
    {
        var text = value.ToString();
        if (!text.Contains(".") && !text.Contains("E")) text = text + ".0";
        var lit = new IlLit(text, "float", null);
        if (value < 0) return new IlParen(lit, null);
        return lit;
    }

    public static IlExpr LitFromConst(string text, bool isFloat)
    {
        if (text == "-2147483648") return IntMinValueIl();
        return new IlLit(text, isFloat ? "float" : null, null);
    }

    // int.MinValue は i32 の literal (2147483648 の符号の反転) では書けないので、式で書く
    private static IlExpr IntMinValueIl()
    {
        return new IlParen(new IlBin(IlBinOp.Sub, new IlUn(IlUnOp.Neg, new IlLit("2147483647", null, null), null), new IlLit("1", null, null), null), null);
    }

    private IlExpr? BuildLiteral(Node lit)
    {
        switch (lit.Kind)
        {
            case NodeKind.IntLit:
            case NodeKind.FloatLit:
                return new IlLit(ConvertNumericLiteral(lit), lit.Kind == NodeKind.FloatLit ? "float" : null, null);
            case NodeKind.StringLit:
                return new IlLit(IlLiterals.QuoteString(lit.Text), null, null);
            case NodeKind.CharLit:
                return new IlLit("" + lit.Value, null, null);
            case NodeKind.BoolLit:
                return new IlLit(lit.Value != 0 ? "true" : "false", null, null);
            case NodeKind.NullLit:
                return new IlLit("nil", null, null);
        }
        return Unported("literal", lit);
    }

    // 数の literal の字面: 桁区切りと接尾辞を外す。2 進は 10 進に、16 進は区切りだけ外す。float の literal は小数点も指数も無ければ ".0" を付ける
    private static string ConvertNumericLiteral(Node lit)
    {
        var text = lit.File.Slice(lit.Pos, lit.End);
        if (text.StartsWith("0b") || text.StartsWith("0B")) return "" + (lit.Const != null ? lit.Const.I : 0);
        // 16 進の字面は数字に f / d を含むので、suffix は u / l だけを除く (0xFFu)
        if (text.StartsWith("0x") || text.StartsWith("0X")) return StripIntegerSuffix(text).Replace("_", "");
        var result = StripNumericSuffix(text).Replace("_", "");
        if (lit.Kind == NodeKind.FloatLit && !result.Contains(".") && !result.Contains("e") && !result.Contains("E"))
            result = result + ".0";
        return result;
    }

    private static string StripIntegerSuffix(string text)
    {
        var u = new TinySystem.Text.Utf8Bytes(text);
        int n = u.Length;
        while (n > 0 && (u[n - 1] == 'u' || u[n - 1] == 'U' || u[n - 1] == 'l' || u[n - 1] == 'L')) n--;
        return u.Slice(0, n).ToString();
    }

    private static string StripNumericSuffix(string text)
    {
        var u = new TinySystem.Text.Utf8Bytes(text);
        int n = u.Length;
        while (n > 0)
        {
            int c = u[n - 1];
            if (c == 'f' || c == 'F' || c == 'd' || c == 'D' || c == 'm' || c == 'M' || c == 'u' || c == 'U' || c == 'l' || c == 'L') n--;
            else break;
        }
        return u.Slice(0, n).ToString();
    }

    // 補間の文字列 (揃えは literal だけ)
    private IlExpr? BuildInterpolatedString(Node interp)
    {
        var parts = new List<IlExpr>();
        if (interp.List != null)
        {
            foreach (var content in interp.List)
            {
                if (content.Kind == NodeKind.StringLit)
                {
                    parts.Add(new IlLit(IlLiterals.QuoteString(content.Text), null, null));
                    continue;
                }
                var inner = BuildExpr(content.A!);
                if (inner == null) return null;
                // bool / string / char は書式を使わない。Rune と ToString の override を持つ型も書式を無視する
                var holeType = NaturalType(content.A!);
                var ht = IlTypes.UnwrapNullable(holeType);
                string? format = content.Text != "" ? content.Text : null;
                if (IlTypes.IsRune(ht) || HasToStringOverride(ht) || ht.Kind == TypeKind.Bool || ht.Kind == TypeKind.String || ht.Kind == TypeKind.Char) format = null;
                IlExpr? rendered = null;
                if (format != null)
                {
                    var enumName = WrapEnumFormat(holeType, inner, format);
                    if (enumName is IlCall ec && ec.Callee == "String.FormatEnum") rendered = enumName;
                    if (rendered == null) rendered = WrapNumberFormat(holeType, inner, format);
                    if (rendered == null) rendered = new IlCall("string.format", Two(new IlLit(IlLiterals.QuoteString(ConvertFormatSpecifier(format)), null, null), inner), false, null);
                }
                else rendered = WrapFloatToString(content.A!, inner);
                if (rendered == null) return null;
                if (content.B != null)
                {
                    if (!content.B.HasConst || content.B.Const == null || content.B.Const.Kind != ConstKind.Int) return null;
                    rendered = new IlCall("string.format", Two(new IlLit(IlLiterals.QuoteString("%" + content.B.Const.I + "s"), null, null), rendered), false, null);
                }
                parts.Add(rendered);
            }
        }
        if (parts.Count == 0) return new IlLit("\"\"", null, null);
        var result = parts[0];
        for (int i = 1; i < parts.Count; i++) result = new IlBin(IlBinOp.Concat, result, parts[i], null);
        // `..` の連接は atomic でない (IlLen / IlField が先頭の要素にだけ結合する) ので、受け手の位置でも安全なよう括弧で閉じる
        return parts.Count == 1 ? result : Ex(new IlParen(result, null));
    }

    // 数 (と T?) の標準の書式は .NET の ToString(format) と同じ文字列にする (String.FormatFloat / String.FormatInt、il-spec §13)。
    // 精度は 9 桁までの数で、"G03" は "G3" と同じなので数に直して渡す。R は G と同じで、float は精度を使わない
    private static IlExpr? WrapNumberFormat(TypeSym? type, IlExpr value, string format)
    {
        bool isFloat = IlTypes.IsFloating(type);
        var u = new TinySystem.Text.Utf8Bytes(format);
        int n = u.Length;
        if ((!isFloat && !IlTypes.IsIntegral(type)) || n == 0 || n > 10) return null;
        for (int i = 1; i < n; i++)
        {
            if (u[i] < '0' || u[i] > '9') return null;
        }
        int first = u[0];
        int kind = first == 'R' ? 'G' : first == 'r' ? 'g' : first;
        var kinds = isFloat ? "GgEeFfNn" : "GgEeFfNnDdXx";
        var ku = new TinySystem.Text.Utf8Bytes(kinds);
        bool known = false;
        for (int i = 0; i < ku.Length; i++)
        {
            if (ku[i] == kind) known = true;
        }
        if (!known) return null;
        var kindText = Strings.Prefix(kind == first ? format : (kind == 'G' ? "G" : "g"), 1);
        string spec;
        if (n == 1 || (isFloat && kind != first)) spec = kindText;
        else
        {
            int digits = 0;
            for (int i = 1; i < n; i++) digits = digits * 10 + (u[i] - '0');
            spec = kindText + digits;
        }
        return new IlCall(isFloat ? "String.FormatFloat" : "String.FormatInt", Two(value, new IlLit(IlLiterals.QuoteString(spec), null, null)), false, null);
    }

    private static bool AllZeros(string s)
    {
        var u = new TinySystem.Text.Utf8Bytes(s);
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] != '0') return false;
        }
        return true;
    }

    // C# の書式指定を string.format の指定 (printf の形。runtime/fmt が読む) に
    private static string ConvertFormatSpecifier(string fmt)
    {
        if (fmt == "") return "%s";
        var first = Strings.Prefix(fmt, 1);
        var c = first.ToUpper();
        var precision = Strings.From(fmt, 1);
        switch (c)
        {
            case "F": return precision == "" ? "%.6f" : "%." + precision + "f";
            case "N": return precision == "" ? "%.2f" : "%." + precision + "f";
            // D の精度は符号を除いた最小の桁数 (printf の %.Nd と同じ)。精度 0 は精度なしと同じ
            case "D": return AllZeros(precision) ? "%d" : "%." + precision + "d";
            case "X":
                if (first == "X") return precision == "" ? "%X" : "%0" + precision + "X";
                return precision == "" ? "%x" : "%0" + precision + "x";
            case "E":
                if (first == "E") return precision == "" ? "%E" : "%." + precision + "E";
                return precision == "" ? "%e" : "%." + precision + "e";
            case "G": return precision == "" ? "%g" : "%." + precision + "g";
        }
        return "%s";
    }

    // ---- 呼び出し ----

    // 式の位置の ++ / --。文の位置と同じ place の解決 (副作用のある受け手 / 添字は temp) で 1 回だけ代入し、
    // 前置は更新後、後置は更新前の値を返す IIFE
    private IlExpr? BuildIncrementExpr(Node operand, bool increment, bool prefix)
    {
        var stats = new List<IlStat>();
        IlExpr read;
        var prop = BuildPropTarget(operand, true);
        if (prop != null)
        {
            foreach (var st in prop.Setup) stats.Add(st);
            IlExpr pv = new IlVar("__tcs_v", null);
            stats.Add(new IlLocal("__tcs_v", prefix ? StepValue(operand, BuildPropGet(prop), increment) : BuildPropGet(prop), null, null));
            stats.Add(new IlCallStat(BuildPropSet(prop, prefix ? pv : StepValue(operand, pv, increment)), null));
            stats.Add(new IlReturn(pv, null));
            return new IlIife(stats, null);
        }
        if (NeedsLoweredLvalue(operand))
        {
            var lowered = BuildLoweredTarget(operand, false);
            if (lowered == null) return null;
            foreach (var st in lowered.Setup) stats.Add(st);
            read = lowered.Access;
        }
        else
        {
            var target = BuildExpr(operand);
            if (target == null) return null;
            read = target;
        }
        IlExpr value = new IlVar("__tcs_v", null);
        stats.Add(new IlLocal("__tcs_v", prefix ? StepValue(operand, read, increment) : read, null, null));
        stats.Add(new IlAssign(read, prefix ? value : StepValue(operand, value, increment), null));
        stats.Add(new IlReturn(value, null));
        return new IlIife(stats, null);
    }

    // ---- ?. (il-spec §3) ----
    // セルフホストの木の ?. は鎖の各 node に CondChain、最も外の node に CondTop、`?.` / `?[` の位置に Value = 1 を持つ。
    // 参照実装の ConditionalAccess の入れ子 (Expression = 最初の ?. の受け手、WhenNotNull = その上の鎖。鎖の中の次の ?. は入れ子) に対応づけて組み立てる

    private Node? _subNode;
    private IlExpr? _subIl;
    private Node? _condBuilding;
    private Node? _condBinding;

    private static bool IsCondPoint(Node n)
    {
        return (n.Kind == NodeKind.Member || n.Kind == NodeKind.Index || n.Kind == NodeKind.Call) && n.Value == 1;
    }

    private static Node? ChainNext(Node n)
    {
        if (n.Kind == NodeKind.Member || n.Kind == NodeKind.Call || n.Kind == NodeKind.Index || n.Kind == NodeKind.Conv) return n.A;
        return null;
    }

    // ?. の鎖の外側から内側への node の列 (最後が最初の ?.)
    private static List<Node> ChainPath(Node top)
    {
        var path = new List<Node>();
        var n = top;
        while (true)
        {
            path.Add(n);
            if (IsCondPoint(n) && (n.A == null || !StripConv(n.A).CondChain)) break;
            var next = ChainNext(n);
            if (next == null || !(next.CondChain || (next.Kind == NodeKind.Conv && StripConv(next).CondChain))) break;
            n = next;
        }
        return path;
    }

    private IlExpr? BuildConditionalTop(Node top)
    {
        var path = ChainPath(top);
        var first = path[path.Count - 1];
        if (!IsCondPoint(first) || first.A == null) return Unported("conditional chain", top);
        // 鎖の中は持ち上げる前の型で組み立てる (Roslyn の WhenNotNull の中の型)
        var saved = new List<TypeSym?>();
        foreach (var n in path)
        {
            saved.Add(n.Type);
            if (n.CondChain && n.Type2 != null) n.Type = n.Type2;
        }
        var outerBuilding = _condBuilding;
        _condBuilding = top;
        IlExpr? result = null;
        var receiver = BuildExpr(first.A);
        if (receiver != null) result = BuildConditionalCore(receiver, NaturalType(first.A), path, path.Count - 1, 0);
        _condBuilding = outerBuilding;
        for (int i = 0; i < path.Count; i++) path[i].Type = saved[i];
        return result;
    }

    // BuildConditionalCore が作った IIFE (`__tcs_ca = ...; if (__tcs_ca != nil) ...`)
    private static bool IsConditionalCore(IlExpr e)
    {
        if (!(e is IlIife iife) || iife.Stats.Count == 0) return false;
        return iife.Stats[0] is IlLocal l && l.Name == "__tcs_ca";
    }

    // receiver を local へ 1 回評価し、null でなければ path[bottom..top] (path[bottom] が ?. の位置) を返す IIFE。
    // `T?` の receiver は値の有無と中の値の明示の node。結果が値型 (T? でない) なら T? に包む
    private IlExpr? BuildConditionalCore(IlExpr receiver, TypeSym receiverType, List<Node> path, int bottom, int top)
    {
        bool nullableValue = IlTypes.IsNullableValueType(receiverType);
        var underlying = nullableValue ? receiverType.Elem : receiverType;
        var objName = nullableValue ? "__tcs_cav" : "__tcs_ca";
        var whenNotNull = BuildConditionalWhenNotNull(path, bottom, top, new IlVar(objName, null), underlying);
        if (whenNotNull == null) return null;
        var resultType = path[top].Type;
        // 内側の ?. (入れ子の IIFE) の結果は既に T? (Roslyn の入れ子の ConditionalAccess の型)
        if (resultType != null && resultType.IsValueType && resultType.Kind != TypeKind.Void && !IlTypes.IsNullableValueType(resultType) && !(whenNotNull is IlNullableWrap)
            && !IsConditionalCore(whenNotNull))
            whenNotNull = new IlNullableWrap(whenNotNull, IlTypes.Display(resultType), null);
        var ca = new IlVar("__tcs_ca", null);
        IlExpr cond = nullableValue ? new IlNullableHasValue(ca, null) : new IlBin(IlBinOp.Ne, ca, new IlLit("nil", null, null), null);
        var thenStats = new List<IlStat>();
        if (nullableValue) thenStats.Add(new IlLocal(objName, new IlNullableValue(ca, null), null, null));
        thenStats.Add(new IlReturn(whenNotNull, null));
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_ca", receiver, null, null));
        stats.Add(new IlIf(Arm(cond, new IlBlock(thenStats)), null, null));
        return new IlIife(stats, null);
    }

    private IlExpr? BuildConditionalWhenNotNull(List<Node> path, int bottom, int top, IlExpr obj, TypeSym receiverType)
    {
        // 鎖の中の次の ?. (入れ子の ConditionalAccess)
        for (int i = bottom - 1; i >= top; i--)
        {
            if (!IsCondPoint(path[i])) continue;
            // 入れ子の Expression は path[i].A (= path[i + 1] 以下)
            var inner = BuildConditionalWhenNotNull(path, bottom, i + 1, obj, receiverType);
            if (inner == null) return null;
            return BuildConditionalCore(inner, NaturalType(path[i].A!), path, i, top);
        }
        // 最初の ?. の binding (method の binding は呼び出しまで)
        var b = path[bottom];
        int rootIndex = bottom;
        if (b.Kind == NodeKind.Member && bottom - 1 >= top && path[bottom - 1].Kind == NodeKind.Call && path[bottom - 1].A == b) rootIndex = bottom - 1;
        var rootIl = BuildConditionalBinding(path[rootIndex], b, obj, receiverType);
        if (rootIl == null || rootIndex == top) return rootIl;
        // 鎖の残り: binding を作った IL に置き換えて組み立てる
        var outerSub = _subNode;
        var outerSubIl = _subIl;
        _subNode = path[rootIndex];
        _subIl = rootIl;
        var outerBuilding = _condBuilding;
        _condBuilding = path[top];
        var built = BuildExpr(path[top]);
        _condBuilding = outerBuilding;
        _subNode = outerSub;
        _subIl = outerSubIl;
        return built;
    }

    // ?. の位置の binding (`.M` / `.M(args)` / `[i]` / `d?.Invoke(args)`) を、受け手を obj にして組み立てる
    private IlExpr? BuildConditionalBinding(Node root, Node binding, IlExpr obj, TypeSym receiverType)
    {
        var recvNode = binding.A!;
        // struct の custom property は受け手を copy した値で読む (`S?` の .Value は copy)
        if (root == binding && binding.Kind == NodeKind.Member && binding.Sym != null && binding.Sym.Kind == SymKind.Property && IsCustomProperty(binding.Sym) && IlTypes.IsUserStruct(receiverType))
            return new IlCall(TypeRef(binding.Sym.Owner!) + ".get_" + N(binding.Sym), One(ConditionalStructReceiver(obj, receiverType)), false, null);
        var savedType = recvNode.Type;
        recvNode.Type = receiverType;
        var outerSub = _subNode;
        var outerSubIl = _subIl;
        var outerBinding = _condBinding;
        _subNode = recvNode;
        _subIl = obj;
        _condBinding = binding;
        var outerBuilding = _condBuilding;
        _condBuilding = root;
        var built = BuildExpr(root);
        _condBuilding = outerBuilding;
        _condBinding = outerBinding;
        _subNode = outerSub;
        _subIl = outerSubIl;
        recvNode.Type = savedType;
        return built;
    }

    private IlExpr ConditionalStructReceiver(IlExpr obj, TypeSym type)
    {
        if (!IlTypes.IsMutableStruct(type)) return obj;
        return new IlStructCopy(obj, TypeName(type), false, StructCopiers(type, false), null);
    }

    // ---- タプルと分解 (参照実装の Tuples)。タプルの値は struct (TinySystem/ValueTuple.cs)。
    // タプルを返す method を multi-return にする形は ForCBackend でない IlContext のときだけで、IlExporter は使わない

    private int _deconstructTemp;

    // multi-return の method。戻り値の宣言がタプルの method だけ。実装する interface の method の戻り値もタプルであること
    public bool IsMultiReturnMethod(Symbol m)
    {
        if (Ctx.ForCBackend || m.Kind != SymKind.Method || m.Builtin != "" || m.Owner == null || m.Owner.IsRef) return false;
        if (m.Type.Kind != TypeKind.Tuple || m.HasOut || m.Decl == null || m.Decl.Kind != NodeKind.Method) return false;
        foreach (var iface in IlTypes.AllInterfaces(m.Owner))
        {
            foreach (var member in iface.Decl!.Members)
            {
                if (member.Name != m.Name) continue;
                if (member.Kind != SymKind.Method) return false;
                if (!SameParamTypes(m, member, iface)) continue;
                if (member.Type.Kind != TypeKind.Tuple) return false;
            }
        }
        return true;
    }

    private static bool SameParamTypes(Symbol impl, Symbol iface, TypeSym ifaceType)
    {
        if (impl.ParamCount != iface.ParamCount) return false;
        for (int i = 0; i < impl.ParamCount; i++)
        {
            if (!TypeSym.Same(impl.Params![i].Type, iface.Params![i].Type.Subst(ifaceType.Args, null))) return false;
        }
        return true;
    }

    // return の位置が multi-return の method の本体の中 (lambda の中ではない) か
    private bool IsInMultiReturnMethod()
    {
        return Ctx.CurrentLambdaVoid == 0 && Ctx.CurrentFunction != null && IsMultiReturnMethod(Ctx.CurrentFunction);
    }

    private static IlExpr PackTuple(Symbol m, IlExpr call)
    {
        return new IlCall("__tcs_tuple" + m.Type.Args!.Count, One(call), false, null);
    }

    private static Node WithoutParens(Node e)
    {
        while (e.Kind == NodeKind.Paren) e = e.A!;
        return e;
    }

    private static Node TupleElementNode(Node e)
    {
        return e.Kind == NodeKind.Arg ? e.A! : e;
    }

    private IlExpr? BuildTupleLiteral(Node tuple)
    {
        var entries = new List<IlTableEntry>();
        if (tuple.List != null)
        {
            for (int i = 0; i < tuple.List.Count; i++)
            {
                var el = TupleElementNode(tuple.List[i]);
                var value = BuildExpr(el);
                if (value == null) return null;
                entries.Add(new IlTableEntry(null, WrapStructCopy(el, value), "Item" + (i + 1)));
            }
        }
        return new IlTable(entries, null, null, false, null, false, null);
    }

    // `return (a, b)` / `return F()` は値のまま返し、それ以外は local から要素を並べる
    private bool BuildTupleReturnInto(Node value, IlPos? origin, List<IlStat> acc)
    {
        var core = WithoutParens(StripConv(value));
        if (core.Kind == NodeKind.Tuple)
        {
            var items = new List<IlExpr>();
            if (core.List != null)
            {
                foreach (var a in core.List)
                {
                    var el = TupleElementNode(a);
                    var item = BuildExpr(el);
                    if (item == null) return false;
                    items.Add(WrapStructCopy(el, item));
                }
            }
            acc.Add(new IlReturn(new IlValues(items, null), origin));
            return true;
        }
        if (core.Kind == NodeKind.Call && core.Sym != null && IsMultiReturnMethod(core.Sym))
        {
            var forwarded = BuildInvocationCall(core);
            if (forwarded == null) return false;
            acc.Add(new IlReturn(forwarded, origin));
            return true;
        }
        var built = BuildExpr(value);
        if (built == null) return false;
        var tuple = NaturalType(value);
        bool fresh = IsFreshStructIl(built) || IsFreshStructValue(value);
        var fields = new List<IlExpr>();
        for (int i = 0; i < tuple.Args!.Count; i++)
        {
            IlExpr field = new IlField(new IlVar("__tcs_ret", null), "Item" + (i + 1), null);
            fields.Add(fresh ? field : CopyIfMutableStruct(tuple.Args[i], field));
        }
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_ret", built, null, null));
        stats.Add(new IlReturn(new IlValues(fields, null), null));
        acc.Add(new IlDo(new IlBlock(stats), origin));
        return true;
    }

    // ---- 分解。C# の順: 左辺の受け手と添字 → 右辺 → 左から順に代入 ----

    private static DTarget? DeconstructTargetOf(Node target)
    {
        var t = StripConv(target);
        switch (t.Kind)
        {
            case NodeKind.PatDecl:
                if (t.Local == null) return null;
                return DTarget.Declared(t.Local.LuaName, t.Local.Type);
            case NodeKind.PatDiscard:
                return DTarget.Discard();
            case NodeKind.DeconstructAssign:
            case NodeKind.Tuple:
            {
                var items = new List<DTarget>();
                if (t.List != null)
                {
                    foreach (var x in t.List)
                    {
                        var item = DeconstructTargetOf(TupleElementNode(x));
                        if (item == null) return null;
                        items.Add(item);
                    }
                }
                return DTarget.Nested(items);
            }
            case NodeKind.Name:
                if (t.Text == "_" && t.Info != RefKind.Local && t.Sym == null) return DTarget.Discard();
                return t.Type != null ? DTarget.PlaceOf(t, t.Type) : null;
            case NodeKind.Member:
            case NodeKind.Index:
                return t.Type != null ? DTarget.PlaceOf(t, t.Type) : null;
        }
        return null;
    }

    // 分解できる型の成分: タプル、record (positional な property)
    private static List<ValueMember>? DeconstructMembers(TypeSym? type, int count)
    {
        if (type == null) return null;
        type = Binder.TupleViewOf(type);
        if (type.Kind == TypeKind.Tuple)
        {
            var r = new List<ValueMember>();
            for (int i = 0; i < type.Args!.Count; i++) r.Add(new ValueMember("Item" + (i + 1), type.Args[i]));
            return r;
        }
        if (type.Decl == null) return null;
        var props = new List<ValueMember>();
        foreach (var m in type.Decl.Members)
        {
            if (m.Kind != SymKind.Property || m.IsStatic) continue;
            if (m.Decl != null && m.Decl.Kind == NodeKind.Property && !m.Decl.Has(Mods.Public)) continue;
            props.Add(new ValueMember(m.Name, type.Args != null && type.Args.Count > 0 ? m.Type.Subst(type.Args, null) : m.Type));
        }
        if (props.Count < count) return null;
        var taken = new List<ValueMember>();
        for (int i = 0; i < count; i++) taken.Add(props[i]);
        return taken;
    }

    private bool BuildDeconstructInto(Node targets, Node right, IlPos? origin, List<IlStat> acc)
    {
        var target = DeconstructTargetOf(targets);
        if (target == null || target.Kind != DTarget.KNested) return false;
        var rhs = WithoutParens(right);
        int flat = TryBuildFlatDeconstruct(target, rhs, origin, acc);
        if (flat >= 0) return flat == 1;
        var stats = new List<IlStat>();
        var assigns = new List<DAssign>();
        var declared = new List<DTarget>();
        DeclaredNames(target, declared);
        foreach (var d in declared) acc.Add(new IlLocal(d.Name, null, IlTypes.Display(d.Type!), origin));
        if (!FixPlaces(target, stats)) return false;
        if (!BindValues(target, rhs, stats, assigns)) return false;
        AddAssigns(assigns, stats);
        acc.Add(new IlDo(new IlBlock(stats), origin));
        return true;
    }

    private static void DeclaredNames(DTarget t, List<DTarget> into)
    {
        if (t.Kind == DTarget.KDeclared) into.Add(t);
        else if (t.Kind == DTarget.KNested)
        {
            foreach (var i in t.Items) DeclaredNames(i, into);
        }
    }

    private string NewDeconstructTemp()
    {
        _deconstructTemp++;
        return "__tcs_d" + _deconstructTemp;
    }

    // 宣言だけの分解 (`var (a, b) = ...`) と local への代入は、右辺の値を一度に束縛できる。1 = 組み立てた、0 = 失敗、-1 = 当てはまらない
    private int TryBuildFlatDeconstruct(DTarget target, Node rhs, IlPos? origin, List<IlStat> acc)
    {
        bool declare = true;
        bool locals = true;
        foreach (var i in target.Items)
        {
            if (i.Kind != DTarget.KDeclared && i.Kind != DTarget.KDiscard) declare = false;
            if (!(i.Kind == DTarget.KPlace && i.Place!.Kind == NodeKind.Name && i.Place.Local != null)) locals = false;
        }
        if (!declare && !locals) return -1;
        var names = new List<string>();
        foreach (var i in target.Items)
        {
            if (i.Kind == DTarget.KDeclared) names.Add(i.Name);
            else if (i.Kind == DTarget.KPlace) names.Add(i.Place!.Local!.LuaName);
            else names.Add("_");
        }
        if (!declare)
        {
            var seen = new HashSet<string>();
            foreach (var n in names)
            {
                if (!seen.Add(n)) return -1;
            }
        }
        var targets = new List<IlExpr>();
        foreach (var n in names) targets.Add(new IlVar(n, null));
        if (rhs.Kind == NodeKind.Call && rhs.Sym != null && IsMultiReturnMethod(rhs.Sym))
        {
            var elements = NaturalType(rhs).Args!;
            if (elements.Count != target.Items.Count) return -1;
            for (int i = 0; i < elements.Count; i++)
            {
                var tt = target.Items[i].Type;
                if (tt != null && !TypeSym.Same(elements[i], tt)) return -1;
            }
            var built = BuildInvocationCall(rhs);
            if (built == null) return 0;
            acc.Add(new IlMultiAssign(targets, One(built), declare, origin));
            return 1;
        }
        if (rhs.Kind == NodeKind.Tuple && rhs.List != null && rhs.List.Count == targets.Count)
        {
            foreach (var a in rhs.List)
            {
                if (WithoutParens(StripConv(TupleElementNode(a))).Kind == NodeKind.Tuple) return -1;
            }
            var values = new List<IlExpr>();
            for (int i = 0; i < targets.Count; i++)
            {
                var el = TupleElementNode(rhs.List[i]);
                var built = BuildExpr(el);
                if (built == null) return 0;
                var tt = target.Items[i].Type;
                var src = el.Type;
                if (FoldsToFloatConst(el, tt))
                {
                    built = FloatLit((float)el.Const!.I);
                    src = TypeSym.FloatType;
                }
                values.Add(ConvertDeconstructed(WrapStructCopy(el, built), src, tt));
            }
            acc.Add(new IlMultiAssign(targets, values, declare, origin));
            return 1;
        }
        return -1;
    }

    // 要素を代入する place の受け手と添字を、右辺の評価の前に固定する
    private bool FixPlaces(DTarget target, List<IlStat> stats)
    {
        if (target.Kind == DTarget.KNested)
        {
            foreach (var i in target.Items)
            {
                if (!FixPlaces(i, stats)) return false;
            }
            return true;
        }
        if (target.Kind != DTarget.KPlace) return true;
        var place = target.Place!;
        if (IsCustomPropertyTarget(place)) return false;
        bool direct = place.Kind == NodeKind.Name;
        if (!direct && place.Kind == NodeKind.Member && place.A != null && NaturalType(place.A).IsValueType)
        {
            var baseExpr = StructPlaceBase(place.A);
            direct = baseExpr == null || baseExpr == place.A;
        }
        if (direct)
        {
            var built = BuildExpr(place);
            if (built == null) return false;
            target.Fixed = built;
            return true;
        }
        var lowered = BuildLoweredTarget(place, true);
        if (lowered == null) return false;
        target.Fixed = RenameFixedTemps(lowered.Setup, lowered.Access, stats);
        return true;
    }

    // BuildLoweredTarget の temp は決まった名前なので、place ごとに別の名前にする
    private IlExpr RenameFixedTemps(List<IlStat> setup, IlExpr access, List<IlStat> into)
    {
        var renames = new Dictionary<string, string>();
        foreach (var stat in setup)
        {
            var local = (IlLocal)stat;
            var init = RenameVars(local.Init!, renames);
            renames[local.Name] = NewDeconstructTemp();
            into.Add(local with { Name = renames[local.Name], Init = init });
        }
        return RenameVars(access, renames);
    }

    private static IlExpr RenameVars(IlExpr e, Dictionary<string, string> renames)
    {
        if (e is IlVar v && renames.TryGetValue(v.Name, out var name)) return new IlVar(name, null);
        var kids = IlTree.Children(e);
        if (kids.Count == 0) return e;
        var renamed = new List<IlExpr>();
        foreach (var k in kids) renamed.Add(RenameVars(k, renames));
        return IlTree.WithChildren(e, renamed);
    }

    private bool BindValues(DTarget target, Node rhs, List<IlStat> stats, List<DAssign> assigns)
    {
        rhs = WithoutParens(rhs);
        if (target.Kind == DTarget.KNested)
        {
            if (rhs.Kind == NodeKind.Tuple)
            {
                if (rhs.List == null || rhs.List.Count != target.Items.Count) return false;
                for (int i = 0; i < target.Items.Count; i++)
                {
                    if (!BindValues(target.Items[i], TupleElementNode(rhs.List[i]), stats, assigns)) return false;
                }
                return true;
            }
            var type = NaturalType(rhs);
            if (rhs.Kind == NodeKind.Call && rhs.Sym != null && IsMultiReturnMethod(rhs.Sym))
            {
                var multi = BuildInvocationCall(rhs);
                if (multi == null) return false;
                var temps = new List<IlExpr>();
                foreach (var _ in target.Items) temps.Add(new IlVar(NewDeconstructTemp(), null));
                stats.Add(new IlMultiAssign(temps, One(multi), true, null));
                for (int i = 0; i < temps.Count; i++)
                {
                    if (!BindValue(target.Items[i], temps[i], type.Args![i], stats, assigns)) return false;
                }
                return true;
            }
            var built = BuildExpr(rhs);
            if (built == null) return false;
            return BindValue(target, built, type, stats, assigns);
        }
        var value = BuildExpr(rhs);
        if (value == null) return false;
        value = WrapStructCopy(rhs, value);
        var sourceType = rhs.Type;
        if (FoldsToFloatConst(rhs, target.Type))
        {
            value = FloatLit((float)rhs.Const!.I);
            sourceType = TypeSym.FloatType;
        }
        var temp = NewDeconstructTemp();
        var declType = target.Type ?? sourceType;
        stats.Add(new IlLocal(temp, value, declType != null ? IlTypes.Display(declType) : null, null));
        assigns.Add(new DAssign(target, ConvertDeconstructed(new IlVar(temp, null), sourceType, target.Type)));
        return true;
    }

    // 値を temp に束縛した上で target へ渡す。入れ子は成分へたどる
    private bool BindValue(DTarget target, IlExpr value, TypeSym? type, List<IlStat> stats, List<DAssign> assigns)
    {
        if (target.Kind != DTarget.KNested)
        {
            assigns.Add(new DAssign(target, ConvertDeconstructed(CopyIfMutableStruct(type, value), type, target.Type)));
            return true;
        }
        var members = DeconstructMembers(type, target.Items.Count);
        if (members == null || members.Count != target.Items.Count) return false;
        var holder = value;
        // 分解の temp でない値 (user の変数を含む) は temp に置く。代入先が右辺の変数の成分でも、右辺は代入の前の値を読む
        if (!(value is IlVar v && v.Name.StartsWith("__tcs_d")))
        {
            var temp = NewDeconstructTemp();
            stats.Add(new IlLocal(temp, (value is IlVar) ? CopyIfMutableStruct(type, value) : value, type != null ? IlTypes.Display(type) : null, null));
            holder = new IlVar(temp, null);
        }
        for (int i = 0; i < members.Count; i++)
        {
            if (!BindValue(target.Items[i], new IlField(holder, members[i].Name, null), members[i].Type, stats, assigns)) return false;
        }
        return true;
    }

    // タプルの literal の整数の定数の要素を float の分解の先へ: Roslyn は literal の要素を先の型へ変換する (binder は式の先の型を見ないので変換を持たない)
    private static bool FoldsToFloatConst(Node el, TypeSym? target)
    {
        if (target == null || IlTypes.UnwrapNullable(target).Kind != TypeKind.Float) return false;
        return el.Kind != NodeKind.Conv && el.HasConst && el.Const != null && el.Const.Kind == ConstKind.Int && IlTypes.IsIntegralSource(el.Type);
    }

    // 分解の成分の暗黙の変換 (`float f; (f, g) = (1, 2)`)
    private static IlExpr ConvertDeconstructed(IlExpr value, TypeSym? source, TypeSym? target)
    {
        if (target == null || source == null || TypeSym.Same(source, target)) return value;
        var inner = IlTypes.IsNullableValueType(target) ? target.Elem : target;
        if (inner.Kind == TypeKind.Float && IlTypes.IsIntegralSource(source)) value = new IlNumericConvert(value, "float", null, null);
        if (IlTypes.IsNullableValueType(target) && !IlTypes.IsNullableValueType(source)) return new IlNullableWrap(value, IlTypes.Display(inner), null);
        return value;
    }

    private static void AddAssigns(List<DAssign> assigns, List<IlStat> stats)
    {
        foreach (var a in assigns)
        {
            if (a.Target.Kind == DTarget.KDeclared) stats.Add(new IlAssign(new IlVar(a.Target.Name, null), a.Value, null));
            else if (a.Target.Kind == DTarget.KPlace) stats.Add(new IlAssign(a.Target.Fixed!, a.Value, null));
        }
    }

    // `foreach (var (a, b) in list)`: 要素を 1 つの変数で受けて、本体の先頭で分解する
    private bool BuildForeachDeconstructInto(Node loop, List<IlStat> acc)
    {
        var collType = NaturalType(loop.B!);
        TypeSym? elementType = null;
        if (collType.Kind == TypeKind.Array || collType.Kind == TypeKind.List) elementType = collType.Elem;
        if (elementType == null) return false;
        var items = new List<DTarget>();
        foreach (var nm in loop.List!)
        {
            if (nm.Text == "_" && nm.Local == null) items.Add(DTarget.Discard());
            else if (nm.Local != null) items.Add(DTarget.Declared(nm.Local.LuaName, nm.Local.Type));
            else return false;
        }
        var target = DTarget.Nested(items);
        var collection = BuildExpr(loop.B!);
        var body = BuildBlock(loop.C!);
        if (collection == null || body == null) return false;
        var stats = new List<IlStat>();
        var assigns = new List<DAssign>();
        var declared = new List<DTarget>();
        DeclaredNames(target, declared);
        var all = new List<IlStat>();
        foreach (var d in declared) all.Add(new IlLocal(d.Name, null, IlTypes.Display(d.Type!), null));
        var item = NewDeconstructTemp();
        if (!BindValue(target, new IlVar(item, null), elementType, stats, assigns)) return false;
        AddAssigns(assigns, stats);
        foreach (var s in stats) all.Add(s);
        foreach (var s in body.Stats) all.Add(s);
        acc.Add(new IlForeachList(item, collection, new IlBlock(all), false, Origin(loop)));
        return true;
    }

    // ---- user の演算子 ----

    // user の演算子は呼び出しの場所で選ばれた overload の static 関数を直接呼ぶ (`V.__mul_2(a, b)`)。
    // --ref の型の演算子は実装が host にあるので呼び出しにしない (IL の演算のまま残る)。struct の引数は呼び出しと同じく copy して渡す
    private IlExpr? TryBuildUserOperatorCall(Node expr, List<Node> operands, List<IlExpr> built)
    {
        var op = expr.Sym;
        if (op == null || op.Kind != SymKind.Method || op.Builtin != "" || op.Owner == null || op.Owner.IsInterface || op.Owner.IsRef || op.Decl == null) return null;
        var name = IlNames.OperatorName(op.Decl);
        if (name == null) return null;
        var args = new List<IlExpr>();
        for (int i = 0; i < built.Count; i++)
        {
            if (CanShareArgument(op, operands, 0, i, null)) args.Add(built[i]);
            else args.Add(WrapStructCopy(operands[i], built[i]));
        }
        foreach (var a in OperatorTypeArgDefaults(op, operands)) args.Add(a);
        return new IlCall(TypeRef(op.Owner) + "." + name, args, false, null);
    }

    // generic な型の演算子は static な method と同じく型引数の既定値を末尾に受ける。型引数は自分の型の operand から
    private List<IlExpr> OperatorTypeArgDefaults(Symbol op, List<Node> operands)
    {
        var r = new List<IlExpr>();
        if (!IlTypes.PassesTypeArgDefaults(op.Owner)) return r;
        List<TypeSym>? args = null;
        foreach (var o in operands)
        {
            var t = IlTypes.UnwrapNullable(NaturalType(o));
            if (args == null && t.Decl == op.Owner && t.Args != null) args = t.Args;
        }
        if (args == null) args = op.Owner!.SelfType().Args;
        foreach (var a in args!) r.Add(TypeArgDefaultValue(a));
        return r;
    }

    // ---- out 引数 ----

    // 自作の method の out: 呼び出しは IlMultiAssign (VM は out の値を戻り値の後ろに並べて返し、C はポインタで写す)。受け手の順は parameter の順で、先頭が戻り値
    private static bool IsUserOutMethod(Symbol? m)
    {
        return m != null && m.Kind == SymKind.Method && m.HasOut && m.Builtin == "" && m.Owner != null && !m.Owner.IsRef;
    }

    // out の引数の受け手 (out var x は x、out _ と書けない場所は _)
    private static List<IlExpr> OutTargets(Node call)
    {
        var r = new List<IlExpr>();
        if (call.List == null) return r;
        foreach (var a in call.List)
        {
            if (!a.Has(Mods.Out)) continue;
            r.Add(new IlVar(OutArgumentName(a), null));
        }
        return r;
    }

    private static string OutArgumentName(Node arg)
    {
        var e = StripConv(arg.A!);
        if (e.Kind == NodeKind.PatDecl) return e.Local != null ? e.Local.LuaName : "_";
        if (e.Kind == NodeKind.PatDiscard) return "_";
        if (e.Kind == NodeKind.Name) return e.Local != null ? e.Local.LuaName : e.Text;
        return "_";
    }

    private IlExpr? BuildUserOutExpr(Node call, Symbol m)
    {
        var built = BuildInvocationCall(call);
        if (built == null) return null;
        var outs = OutTargets(call);
        var stats = new List<IlStat>();
        if (m.Type.Kind == TypeKind.Void)
        {
            stats.Add(new IlMultiAssign(outs, One(built), false, null));
            return new IlIife(stats, null);
        }
        var targets = new List<IlExpr>();
        targets.Add(new IlVar("__tcs_ret", null));
        foreach (var o in outs) targets.Add(o);
        stats.Add(new IlLocal("__tcs_ret", null, IlTypes.Display(m.Type), null));
        stats.Add(new IlMultiAssign(targets, One(built), false, null));
        stats.Add(new IlReturn(new IlVar("__tcs_ret", null), null));
        return new IlIife(stats, null);
    }

    private bool BuildUserOutStatInto(Node call, Symbol m, IlPos? origin, List<IlStat> acc)
    {
        var built = BuildInvocationCall(call);
        if (built == null) return false;
        var outs = OutTargets(call);
        if (m.Type.Kind == TypeKind.Void)
        {
            acc.Add(new IlMultiAssign(outs, One(built), false, origin));
            return true;
        }
        var targets = new List<IlExpr>();
        targets.Add(new IlVar("__tcs_ret", null));
        foreach (var o in outs) targets.Add(o);
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_ret", null, IlTypes.Display(m.Type), null));
        stats.Add(new IlMultiAssign(targets, One(built), false, null));
        acc.Add(new IlDo(new IlBlock(stats), origin));
        return true;
    }

    // --ref の host の method の out (受け手の member の形の呼び出し): 自作の method と同じく IlMultiAssign (受け手は戻り値、out の順)
    private static bool IsRefOutCall(Node call)
    {
        var m = call.Sym;
        return m != null && m.Kind == SymKind.Method && m.HasOut && m.Builtin == "" && m.Owner != null && m.Owner.IsRef
            && call.A != null && call.A.Kind == NodeKind.Member;
    }

    private IlExpr? BuildRefMultiReturnValue(Node call, List<IlExpr> outTargets)
    {
        var m = call.Sym!;
        var callArgs = new List<IlExpr>();
        if (call.List != null)
        {
            foreach (var a in call.List)
            {
                if (a.Has(Mods.Out)) outTargets.Add(new IlVar(OutArgumentName(a), null));
                else if (a.Has(Mods.Ref)) return null;
                else
                {
                    var built = BuildExpr(a.A!);
                    if (built == null) return null;
                    callArgs.Add(built);
                }
            }
        }
        if (m.IsStatic) return new IlCall(TypeRef(m.Owner!) + "." + N(m), callArgs, false, null);
        var recv = BuildExpr(call.A!.A!);
        return recv == null ? null : new IlInvoke(recv, N(m), callArgs, null);
    }

    private IlExpr? BuildRefMultiReturnExpr(Node call)
    {
        var outs = new List<IlExpr>();
        var value = BuildRefMultiReturnValue(call, outs);
        if (value == null || outs.Count == 0) return null;
        // 値の無いものは文の位置だけ
        if (call.Sym!.Type.Kind == TypeKind.Void) return null;
        return new IlIife(RetMultiAssign(outs, value, call.Sym!.Type), null);
    }

    // 戻り値の local は型を持つ (C と VM は host の関数の戻り値をこの型の変数で受ける)
    private static List<IlStat> RetMultiAssign(List<IlExpr> outs, IlExpr value, TypeSym ret)
    {
        var targets = new List<IlExpr>();
        targets.Add(new IlVar("__tcs_ret", null));
        foreach (var o in outs) targets.Add(o);
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_ret", null, IlTypes.Display(ret), null));
        stats.Add(new IlMultiAssign(targets, One(value), false, null));
        stats.Add(new IlReturn(new IlVar("__tcs_ret", null), null));
        return stats;
    }

    private bool BuildRefMultiReturnStatInto(Node call, IlPos? origin, List<IlStat> acc)
    {
        var outs = new List<IlExpr>();
        var value = BuildRefMultiReturnValue(call, outs);
        if (value == null || outs.Count == 0) return false;
        if (call.Sym!.Type.Kind == TypeKind.Void)
        {
            acc.Add(new IlMultiAssign(outs, One(value), false, origin));
            return true;
        }
        acc.Add(new IlCallStat(new IlIife(RetMultiAssign(outs, value, call.Sym!.Type), null), origin));
        return true;
    }

    // out を持つ method の本体: out は本体の local で、値は return が戻り値の後ろに並べて返す。IIFE と closure の return は式の中なので対象外
    public IlBlock? BuildOutMethodBody(Node method)
    {
        var outVars = new List<IlExpr>();
        var stats = new List<IlStat>();
        if (method.List != null)
        {
            foreach (var p in method.List)
            {
                if (!p.Has(Mods.Out)) continue;
                var name = IlNames.ParamName(p);
                stats.Add(new IlLocal(name, null, null, null));
                outVars.Add(new IlVar(name, null));
            }
        }
        bool returnsVoid = method.Sym != null && method.Sym.Type.Kind == TypeKind.Void;
        if (method.B != null)
        {
            if (method.B.List != null && !BuildStatsInto(method.B.List, stats)) return null;
        }
        else if (method.C != null)
        {
            var expr = method.C;
            AddPreDecls(expr, stats);
            if (returnsVoid)
            {
                if (!BuildExprStatInto(expr, expr, stats)) return null;
            }
            else
            {
                var value = BuildExpr(expr);
                if (value == null) return null;
                AddReturnStat(stats, WrapStructCopy(expr, value), Origin(expr));
            }
        }
        else return null;
        var body = ReturnOuts(stats, outVars);
        if (returnsVoid && (body.Count == 0 || !(body[body.Count - 1] is IlReturn)))
            body.Add(new IlReturn(new IlValues(outVars, null), null));
        return new IlBlock(body);
    }

    private static List<IlStat> ReturnOuts(List<IlStat> stats, List<IlExpr> outs)
    {
        var r = new List<IlStat>();
        foreach (var stat in stats) r.Add(ReturnOutsStat(stat, outs));
        return r;
    }

    private static IlBlock ReturnOutsBlock(IlBlock b, List<IlExpr> outs)
    {
        return new IlBlock(ReturnOuts(b.Stats, outs));
    }

    private static IlStat ReturnOutsStat(IlStat stat, List<IlExpr> outs)
    {
        switch (stat)
        {
            case IlReturn ret:
            {
                var items = new List<IlExpr>();
                if (ret.Value != null) items.Add(ret.Value);
                foreach (var o in outs) items.Add(o);
                return ret with { Value = new IlValues(items, null) };
            }
            case IlIf c:
            {
                var arms = new List<IlArm>();
                foreach (var a in c.Arms) arms.Add(new IlArm(a.Cond, ReturnOutsBlock(a.Body, outs)));
                return c with { Arms = arms, Else = c.Else != null ? ReturnOutsBlock(c.Else, outs) : null };
            }
            case IlWhile x1: return x1 with { Body = ReturnOutsBlock(x1.Body, outs) };
            case IlRepeat x2: return x2 with { Body = ReturnOutsBlock(x2.Body, outs) };
            case IlNumericFor x3: return x3 with { Body = ReturnOutsBlock(x3.Body, outs) };
            case IlForeachList x4: return x4 with { Body = ReturnOutsBlock(x4.Body, outs) };
            case IlForeachDict x5: return x5 with { Body = ReturnOutsBlock(x5.Body, outs) };
            case IlForeachRunes x6: return x6 with { Body = ReturnOutsBlock(x6.Body, outs) };
            case IlForPairs x7: return x7 with { Body = ReturnOutsBlock(x7.Body, outs) };
            case IlDo x8: return x8 with { Body = ReturnOutsBlock(x8.Body, outs) };
            case IlBreakScope x9: return x9 with { Body = ReturnOutsBlock(x9.Body, outs) };
        }
        return stat;
    }

    // record / struct の with: 複製の上に member を書く。struct の with は値の copy (入れ子の struct も写す) の上に書く
    private IlExpr? BuildWithExpr(Node withExpr)
    {
        var src = BuildExpr(withExpr.A!);
        if (src == null) return null;
        src = CopyIfMutableStruct(NaturalType(withExpr.A!), src);
        var overrides = new List<IlOverride>();
        if (withExpr.B != null && withExpr.B.List != null)
        {
            foreach (var e in withExpr.B.List)
            {
                if (e.Kind != NodeKind.MemberInit || e.A == null) continue;
                var value = BuildExpr(e.A);
                if (value == null) return null;
                overrides.Add(new IlOverride(e.Sym != null ? N(e.Sym) : e.Text, WrapStructCopy(e.A, value)));
            }
        }
        return new IlWith(src, overrides, null);
    }

    private IlExpr? BuildInvocation(Node call)
    {
        if (call.Text == "with") return BuildWithExpr(call);
        var m = call.Sym;
        if (IsUserOutMethod(m)) return BuildUserOutExpr(call, m!);
        if (IsRefOutCall(call)) return BuildRefMultiReturnExpr(call);
        if (m != null && IsMultiReturnMethod(m))
        {
            var packed = BuildInvocationCall(call);
            return packed == null ? null : PackTuple(m, packed);
        }
        return BuildInvocationCall(call);
    }

    private IlExpr? BuildInvocationCall(Node call)
    {
        if (call.Info == CallKind.Nameof)
        {
            if (call.HasConst && call.Const != null) return new IlLit(IlLiterals.QuoteString(call.Const.S), null, null);
            return Unported("nameof", call);
        }
        var m = call.Sym;
        var callee = call.A!;
        if (call != _condBinding && callee != _condBinding && (call.Value == 1 || (callee.Kind == NodeKind.Member && callee.Value == 1))) return Unported("conditional call", call);
        if (call.Info == CallKind.Delegate)
        {
            var args0 = BuildArgs(call, null);
            if (args0 == null) return null;
            // `f?.Invoke(args)`: ?. の受け手 (delegate の値) を呼ぶ
            if (callee == _subNode && _subIl != null) return new IlDynCall(_subIl, args0, null);
            // delegate の field / property の直接の呼び出し
            if (callee.Kind == NodeKind.Member && callee.Sym != null && (callee.Sym.Kind == SymKind.Field || callee.Sym.Kind == SymKind.Property))
            {
                var recv0 = callee.Sym.IsStatic ? Ex(new IlVar(TypeRef(callee.Sym.Owner!), null)) : BuildExpr(callee.A!);
                if (recv0 == null) return null;
                if (callee.Sym.Kind == SymKind.Property && IsCustomProperty(callee.Sym))
                    return new IlDynCall(new IlInvoke(recv0, "get_" + N(callee.Sym), new List<IlExpr>(), null), args0, null);
                return new IlDynCall(new IlField(recv0, N(callee.Sym), null), args0, null);
            }
            if (callee.Kind == NodeKind.Name && callee.Sym != null && (callee.Sym.Kind == SymKind.Field || callee.Sym.Kind == SymKind.Property))
            {
                if (callee.Sym.Kind == SymKind.Property && IsCustomProperty(callee.Sym)) return null;
                IlExpr owner = callee.Sym.IsStatic ? new IlVar(TypeRef(callee.Sym.Owner!), null) : new IlVar("self", null);
                return new IlDynCall(new IlField(owner, N(callee.Sym), null), args0, null);
            }
            var fn = BuildExpr(callee);
            if (fn == null) return null;
            return new IlDynCall(fn, args0, null);
        }
        if (m == null) return Unported("call without symbol", call);
        var args = BuildArgs(call, m);
        if (args == null) return null;
        if (m.Builtin != "") return BuildBuiltinCall(call, m, args);
        args = WithOmittedDefaults(call, m, args);
        if (call.Info == CallKind.Extension)
        {
            // user の拡張メソッド: 静的の呼び出し (受け手が第 1 引数。値型の受け手は値の copy)
            if (callee.Kind == NodeKind.Member && callee.A != null && callee.A.Info != RefKind.Type)
            {
                var extRecv = BuildExpr(callee.A);
                if (extRecv == null) return null;
                var all = new List<IlExpr>();
                all.Add(WrapStructCopy(callee.A, extRecv));
                foreach (var a in args) all.Add(a);
                return new IlCall(TypeRef(m.Owner!) + "." + N(m), all, false, null);
            }
            return new IlCall(TypeRef(m.Owner!) + "." + N(m), args, false, null);
        }
        if (callee.Kind == NodeKind.Member)
        {
            var recvNode = callee.A!;
            if (m.IsStatic)
            {
                // 拡張メソッドを static の形で呼ぶ (`Agg.Total(xs, f)`): 型の関数の静的な呼び出し
                if (m.IsExtension && m.Owner != null) return new IlCall(TypeRef(m.Owner) + "." + N(m), args, false, null);
                var recv = BuildExpr(recvNode);
                if (recv == null) return null;
                return new IlDynCall(new IlField(recv, N(m), null), args, null);
            }
            var r = BuildExpr(recvNode);
            if (r == null) return null;
            // struct の instance member は自由関数で、静的に呼ぶ (il-spec §10)
            if (IlTypes.IsUserStruct(recvNode.Type))
            {
                var all = new List<IlExpr>();
                all.Add(StructReceiverArg(recvNode, r));
                foreach (var a in args) all.Add(a);
                return new IlCall(TypeRef(m.Owner!) + "." + N(m), all, false, null);
            }
            return new IlInvoke(r, N(m), args, null);
        }
        if (callee.Kind == NodeKind.Name)
        {
            if (m.IsStatic) return new IlCall(TypeRef(m.Owner!) + "." + N(m), args, false, null);
            if (IlTypes.IsUserStruct(m.Owner!.SelfType()))
            {
                var all = new List<IlExpr>();
                all.Add(new IlVar("self", null));
                foreach (var a in args) all.Add(a);
                return new IlCall(TypeRef(m.Owner) + "." + N(m), all, false, null);
            }
            return new IlInvoke(new IlVar("self", null), N(m), args, null);
        }
        return Unported("callee", call);
    }

    // 呼び出しの引数 (out は multi-return の受け手で、引数にならない)
    private List<IlExpr>? BuildArgs(Node call, Symbol? m)
    {
        var args = new List<IlExpr>();
        if (call.List == null) return args;
        for (int i = 0; i < call.List.Count; i++)
        {
            var arg = call.List[i];
            if (arg.Has(Mods.Out)) continue;
            if (arg.Has(Mods.Ref) || arg.Has(Mods.In)) return null;
            var built = BuildExpr(arg.A!);
            if (built == null) return null;
            args.Add(WrapArgumentCopy(m, call, i, built));
        }
        return args;
    }

    // 引数は値渡し (il-spec §10 の引数の copy 地点)
    private IlExpr WrapArgumentCopy(Symbol? callee, Node call, int index, IlExpr built)
    {
        var arg = call.List![index].A!;
        Node? receiver = call.Kind == NodeKind.Call && call.A != null && call.A.Kind == NodeKind.Member ? call.A.A : null;
        int offset = call.Kind == NodeKind.Call && call.Info == CallKind.Extension && call.A != null && call.A.Kind == NodeKind.Member && call.A.A != null && call.A.A.Info != RefKind.Type ? 1 : 0;
        if (CanShareArgument(callee, ArgExprs(call), offset, index, receiver)) return built;
        return WrapStructCopy(arg, built);
    }

    // C# は省略した引数の既定値を呼び出し側で、静的に束縛した宣言から決める
    private List<IlExpr> WithOmittedDefaults(Node call, Symbol m, List<IlExpr> args)
    {
        if (m.Owner == null || m.Owner.IsRef || m.Params == null) return args;
        var typeArgDefaults = TypeArgDefaultArgs(call, m);
        int given = args.Count;
        var filled = new List<IlExpr>();
        foreach (var a in args) filled.Add(a);
        int k = 0;
        // 受け手の形で呼ぶ拡張メソッドは this の parameter を数えない (Roslyn の reduced な method)
        bool reduced = call.Info == CallKind.Extension && call.A != null && call.A.Kind == NodeKind.Member && call.A.A != null && call.A.A.Info != RefKind.Type;
        int pi = 0;
        foreach (var p in m.Params)
        {
            if (reduced && pi++ == 0) continue;
            if (p.IsOut) continue;
            if (k++ < given) continue;
            IlExpr? built = null;
            if (p.Default != null) built = ContainsTypeParam(p.Type) ? DefaultIl(p.Type.Subst(ClassTypeArgsOf(call, m), call.TypeArgs)) : BuildExpr(p.Default);
            if (built != null) filled.Add(built);
            else if (typeArgDefaults.Count > 0) filled.Add(new IlLit("nil", null, null));
            else return args;
        }
        foreach (var t in typeArgDefaults) filled.Add(t);
        return filled;
    }

    private static bool ContainsTypeParam(TypeSym t)
    {
        return t.ContainsTypeParam();
    }

    // 呼び出しの側の隠れ引数。宣言の側と同じ順で、型引数ごとの既定値
    private List<IlExpr> TypeArgDefaultArgs(Node call, Symbol m)
    {
        var r = new List<IlExpr>();
        if ((m.Kind == SymKind.Ctor || m.IsStatic) && IlTypes.PassesTypeArgDefaults(m.Owner))
        {
            var classArgs = ClassTypeArgsOf(call, m);
            if (classArgs != null)
            {
                foreach (var a in classArgs) r.Add(TypeArgDefaultValue(a));
            }
        }
        if (m.TypeParams != null && m.TypeParams.Count > 0 && call.TypeArgs != null)
        {
            foreach (var a in call.TypeArgs) r.Add(TypeArgDefaultValue(a));
        }
        return r;
    }

    // 呼び出した member の型の型引数 (`Box<int>.Of` の int、generic な型の中の呼び出しなら自分の型引数、new の型)
    private static List<TypeSym>? ClassTypeArgsOf(Node call, Symbol m)
    {
        if (m.Owner == null || m.Owner.TypeParams.Count == 0) return null;
        if (call.Kind == NodeKind.New) return call.Type != null ? call.Type.Args : null;
        var callee = call.A;
        if (callee != null && callee.Kind == NodeKind.Member && callee.A != null && callee.A.Type != null && callee.A.Type.Args != null) return callee.A.Type.Args;
        return m.Owner.SelfType().Args;
    }

    // 組み込みの method (BCL)
    private IlExpr? BuildBuiltinCall(Node call, Symbol m, List<IlExpr> args)
    {
        var id = m.Builtin;
        var callee = call.A!;
        Node? recvNode = callee.Kind == NodeKind.Member && callee.A != null && callee.A.Info != RefKind.Type ? callee.A : null;
        TypeSym? recvType = recvNode != null ? recvNode.Type : null;
        if (callee.Kind == NodeKind.Member && IsFacadeReceiver(callee.A))
            return new IlCall(callee.A!.Type!.Name + "." + callee.Text, args, false, null);
        if (id == "Int.TryParse" || id == "Float.TryParse") return BuildNumericTryParse(call, id == "Int.TryParse" ? "Int" : "Float");
        if (id == "Dict.TryGetValue" && recvNode != null) return BuildDictTryGetValue(call, recvNode, m);
        // int / float の CompareTo (同じ型): -1 / 0 / 1 を返す intrinsic
        if (id == "Math.CompareTo" && recvNode != null)
        {
            var cmpRecv = BuildExpr(recvNode);
            return cmpRecv == null ? null : new IlCall("Math.CompareTo", Prepend(cmpRecv, args), false, null);
        }
        switch (id)
        {
            case "Console.WriteLine":
            case "Console.Write":
            {
                var printArgs = new List<IlExpr>();
                for (int i = 0; i < args.Count; i++)
                {
                    var src = call.List![i].A!;
                    printArgs.Add(StringifyForPrint(src, args[i]));
                }
                return new IlCall(id == "Console.Write" ? "io.write" : "print", printArgs, false, null);
            }
            case "Environment.GetEnvironmentVariable":
                return new IlCall("os.getenv", args, false, null);
            case "String.IsNullOrEmpty":
            case "String.CompareOrdinal":
                return new IlCall("String." + callee.Text, args, false, null);
            case "String.Join":
            case "String.JoinAny":
            case "String.JoinArgs":
            {
                if (args.Count == 2)
                {
                    var elem = IlTypes.ElementOf(NaturalType(call.List![1].A!));
                    // enum の要素は member の名前にしてから Join する (Enum.ToString と同じ)
                    var joinSpec = elem != null ? EnumFormatSpec(elem, false) : null;
                    if (joinSpec != null)
                    {
                        var ja = new List<IlExpr>();
                        ja.Add(args[0]);
                        ja.Add(args[1]);
                        ja.Add(new IlLit(IlLiterals.QuoteString(joinSpec), null, null));
                        return new IlCall("String.JoinEnum", ja, false, null);
                    }
                }
                return new IlCall("String.Join", args, false, null);
            }
            case "Int.Parse":
                return new IlCall("math.tointeger", One(new IlCall("tonumber", args, false, null)), false, null);
            case "Float.Parse":
                return new IlNumericConvert(new IlCall("tonumber", args, false, null), "float", null, null);
            case "Char.ToUpper":
            case "Char.ToLower":
            case "Char.ConvertFromUtf32":
                return new IlCall("Char." + callee.Text, args, false, null);
            // TinySystem.Text の static の形 (Runes.RuneCount(s))
            case "Runes.RuneCountStatic":
            case "Runes.RuneSubstringStatic":
                return new IlCall("TinySystem.Utf8." + callee.Text, args, false, null);
            case "Math.Abs":
            case "Math.Min":
            case "Math.Max":
            case "Math.Atan2":
            case "Math.Clamp":
            case "Math.Pow":
            case "Math.Sign":
                return new IlCall(id, args, false, null);
            case "Math.Ceiling":
                return new IlCall("Math.Ceil", args, false, null);
        }
        if (id.StartsWith("Math.") && callee.Kind == NodeKind.Member && IsFacadeReceiver(callee.A))
            return new IlCall("TinySystem.Math." + callee.Text, args, false, null);
        // TinySystem の facade の static (TinySystem.Random.Seed、TinySystem.List.Contains)
        if (id == "Random.Seed") return new IlCall("TinySystem.Random." + callee.Text, args, false, null);
        // char の static な method
        if (id.StartsWith("Char.") && m.IsStatic) return new IlCall("Char." + callee.Text, args, false, null);
        if (id.StartsWith("Math.") && callee.Kind == NodeKind.Member)
            return new IlCall("Math." + (callee.Text == "Ceiling" ? "Ceil" : callee.Text), args, false, null);
        if (recvNode == null) return Unported("builtin static " + id, call);
        var recv = BuildExpr(recvNode);
        if (recv == null) return null;
        switch (id)
        {
            // TinySystem.Text の method は runtime の TinySystem.Utf8 の呼び出し (受け手は第 1 引数)
            case "Runes.RuneCount":
            case "Runes.RuneSubstring":
            case "Utf8.Slice":
            case "Utf8.ToString":
                return new IlCall("TinySystem.Utf8." + callee.Text, Prepend(recv, args), false, null);
            case "List.Add":
                return ListAdd(recvType!, recv, args);
            case "List.RemoveAt":
                return new IlCall("table.remove", Two(recv, new IlBin(IlBinOp.AddNum, args[0], new IlLit("1", null, null), null)), IlTypes.HasNilListElements(recvType), null);
            case "List.Clear":
            case "Dict.Clear":
                return BuildClear(recv);
            case "List.Sort":
                return new IlCall("List.Sort", Prepend(recv, args), false, null);
            case "List.Contains":
            case "List.IndexOf":
            case "List.Remove":
                return new IlCall("List." + callee.Text, Prepend(recv, WithStructElementOps(recvType, callee.Text, args)), false, null);
            case "List.FindIndex":
            case "List.RemoveAll":
            case "List.Insert":
                return new IlCall("List." + callee.Text, Prepend(recv, args), false, null);
            case "List.GetRange":
            {
                var ga = Prepend(recv, args);
                foreach (var c in ListElementCopier(recvType)) ga.Add(c);
                return new IlCall("List.GetRange", ga, false, null);
            }
            case "Queue.Contains":
                return new IlCall("TcsQueue.Contains", Prepend(recv, WithStructElementOps(recvType, "Contains", args)), false, null);
            case "Dict.ContainsKey":
            case "Dict.Remove":
                return new IlCall("Dict." + callee.Text, Two(recv, DictKey(recvType, call.List![0].A, args[0])), false, null);
            case "Queue.Enqueue":
            case "Queue.Dequeue":
            case "Queue.Clear":
                return new IlCall("TcsQueue." + callee.Text, Prepend(recv, args), false, null);
            case "Queue.Peek":
                return CopyIfMutableStruct(recvType!.Elem, new IlCall("TcsQueue.Peek", One(recv), false, null));
            case "HashSet.Add":
            case "HashSet.Remove":
            case "HashSet.Contains":
            case "HashSet.Clear":
                return new IlCall("TcsHashSet." + callee.Text, Prepend(recv, args), false, null);
            case "String.Contains":
            case "String.ContainsChar":
            case "String.Replace":
            case "String.ReplaceChar":
            case "String.Split":
            case "String.SplitChar":
            case "String.SplitWhite":
                return new IlCall("String." + callee.Text, Prepend(recv, WrapCharArgs(call, args)), false, null);
            case "String.StartsWith":
            case "String.EndsWith":
            case "String.StartsWithChar":
            case "String.EndsWithChar":
                return new IlCall("String." + callee.Text, Two(recv, WrapCharArgs(call, args)[0]), false, null);
            // TinySystem.Random の instance の method
            case "Random.Next":
            case "Random.NextFloat":
            case "Random.Range":
                return new IlInvoke(recv, callee.Text, args, null);
            case "Nullable.GetValueOrDefault":
                return new IlNullableGetOrDefault(recv, DefaultIl(IlTypes.UnwrapNullable(recvType!)), null);
            case "Nullable.GetValueOrDefaultValue":
            {
                // method の引数なので常に 1 回評価する (?? と違い遅延しない)
                var stats = new List<IlStat>();
                stats.Add(new IlLocal("__tcs_val", recv, null, null));
                stats.Add(new IlLocal("__tcs_fb", args[0], null, null));
                stats.Add(new IlReturn(new IlNullableGetOrDefault(new IlVar("__tcs_val", null), new IlVar("__tcs_fb", null), null), null));
                return new IlIife(stats, null);
            }
            case "String.Trim":
                return new IlCall("String.Trim", One(recv), false, null);
            case "String.ToUpper":
                return new IlCall("string.upper", One(recv), false, null);
            case "String.ToLower":
                return new IlCall("string.lower", One(recv), false, null);
            case "String.ToString":
                return new IlCall("tostring", One(recv), false, null);
            case "Int.ToString":
            case "Float.ToString":
            case "Bool.ToString":
            case "Char.ToString":
            case "Object.ToString":
            case "Nullable.ToString":
            case "Rune.ToString":
            case "TypeParam.ToString":
                return WrapFloatToString(recvNode, recv);
            case "Enum.ToString":
                return WrapFloatToString(recvNode, recv);
            case "Enum.ToStringFormat":
            {
                // enum の書式 ("G" / "F" / "D")
                var fa = call.List![0].A!;
                if (fa.HasConst && fa.Const != null && fa.Const.Kind == ConstKind.String) return WrapEnumFormat(NaturalType(recvNode), recv, fa.Const.S);
                return null;
            }
            case "Float.ToStringFormat":
            case "Int.ToStringFormat":
            {
                // 数の標準の書式 (補間の `{x:F2}` と同じ)。空の書式は書式なしと同じ
                var fa = call.List![0].A!;
                if (!(fa.HasConst && fa.Const != null && fa.Const.Kind == ConstKind.String)) return null;
                if (fa.Const.S == "") return WrapFloatToString(recvNode, recv);
                return WrapNumberFormat(NaturalType(recvNode), recv, fa.Const.S);
            }
        }
        return Unported("builtin " + id, call);
    }

    // string の method の char の引数は 1 文字の string にして runtime へ渡す (char は整数の code unit)
    private static List<IlExpr> WrapCharArgs(Node call, List<IlExpr> args)
    {
        var r = new List<IlExpr>();
        for (int i = 0; i < args.Count; i++)
        {
            if (call.List != null && i < call.List.Count && IlTypes.IsChar(NaturalType(call.List[i].A!))) r.Add(new IlCall("string.char", One(args[i]), false, null));
            else r.Add(args[i]);
        }
        return r;
    }

    // out の引数の受け手 (out var x は前宣言した local、既存の local はそのもの)
    private static IlExpr? OutTarget(Node arg)
    {
        var a = arg.A!;
        if (a.Kind == NodeKind.PatDecl) return new IlVar(a.Local != null ? a.Local.LuaName : a.Text, null);
        if (a.Kind == NodeKind.PatDiscard) return new IlVar("_", null);
        if (a.Kind == NodeKind.Name) return new IlVar(a.Local != null ? a.Local.LuaName : a.Text, null);
        return null;
    }

    // int.TryParse(s, out v) / float.TryParse: runtime の Math.TryParseInt / TryParseFloat の (found, value) を受ける
    private IlExpr? BuildNumericTryParse(Node call, string kind)
    {
        if (call.Count != 2) return null;
        var textArg = call.List![0];
        var outArg = call.List[1];
        if (textArg.Has(Mods.Ref) || textArg.Has(Mods.Out)) return null;
        var text = BuildExpr(textArg.A!);
        if (text == null) return null;
        var target = OutTarget(outArg);
        if (target == null) return null;
        var stats = new List<IlStat>();
        var targets = Two(new IlVar("__tcs_found", null), new IlVar("__tcs_v", null));
        stats.Add(new IlMultiAssign(targets, One(new IlCall("Math.TryParse" + kind, Two(text, new IlLit(kind == "Float" ? "0.0" : "0", null, null)), false, null)), true, null));
        stats.Add(new IlAssign(target, new IlVar("__tcs_v", null), null));
        stats.Add(new IlReturn(new IlVar("__tcs_found", null), null));
        return new IlIife(stats, null);
    }

    // Dictionary.TryGetValue(key, out v): multi-return の intrinsic (Dict.TryGet)
    private IlExpr? BuildDictTryGetValue(Node call, Node recvNode, Symbol m)
    {
        if (call.Count != 2) return null;
        var keyArg = call.List![0];
        var outArg = call.List[1];
        if (keyArg.Has(Mods.Ref) || keyArg.Has(Mods.Out)) return null;
        var key = BuildExpr(keyArg.A!);
        var recv = BuildExpr(recvNode);
        if (key == null || recv == null) return null;
        var target = OutTarget(outArg);
        if (target == null) return null;
        var dictType = NaturalType(recvNode);
        var valueType = dictType.Kind == TypeKind.Dict ? dictType.Arg(1) : null;
        var defaultValue = DefaultIl(valueType);
        var stats = new List<IlStat>();
        var targets = Two(new IlVar("__tcs_found", null), new IlVar("__tcs_v", null));
        var getArgs = new List<IlExpr>();
        getArgs.Add(recv);
        getArgs.Add(DictKey(dictType, keyArg.A, key));
        getArgs.Add(defaultValue);
        stats.Add(new IlMultiAssign(targets, One(new IlCall("Dict.TryGet", getArgs, false, null)), true, null));
        // out への書き出しは格納した値の copy
        stats.Add(new IlAssign(target, CopyIfMutableStruct(valueType, new IlVar("__tcs_v", null)), null));
        stats.Add(new IlReturn(new IlVar("__tcs_found", null), null));
        return new IlIife(stats, null);
    }

    // Console.WriteLine の引数の文字列化
    private IlExpr StringifyForPrint(Node src, IlExpr value)
    {
        var t = NaturalType(src);
        var user = UserToString(src, value);
        if (user != null) return user;
        var en = WrapEnumToString(t, value);
        if (en != null) return en;
        if (IsStringifiedByValue(t)) return StringifyByValue(t, value);
        if (IlTypes.IsFloating(t)) return new IlCall("__tcs_fstr", One(value), false, null);
        if (IlTypes.IsChar(t)) return new IlCall("string.char", One(value), false, null);
        if (IlTypes.IsRune(t)) return RuneToString(value);
        if (t.Kind == TypeKind.Bool) return new IlCall("__tcs_bstr", One(value), false, null);
        return value;
    }

    // List.Add の IL。型引数の要素の List は値型の List かもしれないので runtime の List.Add を通す
    private static IlCall ListAdd(TypeSym listType, IlExpr recv, List<IlExpr> args)
    {
        if (listType.Elem.Kind == TypeKind.TypeParam) return new IlCall("List.Add", Prepend(recv, args), false, null);
        return new IlCall("table.insert", Prepend(recv, args), IlTypes.HasNilListElements(listType), null);
    }

    // List.Clear / Dictionary.Clear: 全ての key を nil に (C と VM の backend はこの形を runtime の clear にする)
    private static IlExpr BuildClear(IlExpr recv)
    {
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_obj", recv, null, null));
        var body = new IlBlock(One(new IlAssign(new IlIndex(new IlVar("__tcs_obj", null), new IlVar("k", null), false, false, false, false, null), new IlLit("nil", null, null), null)));
        stats.Add(new IlForPairs("k", null, new IlVar("__tcs_obj", null), body, null));
        return new IlIife(stats, null);
    }

    // struct の要素の Contains / IndexOf / Remove は値の等価。型別の op_Equality を末尾の引数で渡す
    private List<IlExpr> WithStructElementOps(TypeSym? listType, string methodName, List<IlExpr> args)
    {
        if (methodName != "Contains" && methodName != "IndexOf" && methodName != "Remove") return args;
        var elem = IlTypes.ElementOf(listType);
        if (!IlTypes.IsUserStruct(elem)) return args;
        var r = new List<IlExpr>();
        foreach (var a in args) r.Add(a);
        r.Add(new IlField(new IlVar(TypeName(elem!), null), "op_Equality", null));
        return r;
    }

    private static List<IlExpr> Prepend(IlExpr first, List<IlExpr> rest)
    {
        var r = new List<IlExpr>();
        r.Add(first);
        foreach (var x in rest) r.Add(x);
        return r;
    }

    // ---- 生成 ----

    private IlExpr? BuildObjectCreation(Node creation)
    {
        var t = creation.Type;
        if (t == null) return Unported("new without type", creation);
        var init = creation.B;
        if (IlTypes.IsList(t))
        {
            if (creation.Count == 1)
            {
                // copy constructor `new List<T>(source)`。変更できる struct の要素なら型別の __copy を渡す
                var srcArg = creation.List![0];
                var st = NaturalType(srcArg.A!);
                if (init != null || srcArg.Has(Mods.Ref) || srcArg.Has(Mods.Out) || !(IlTypes.IsArray(st) || IlTypes.IsList(st) || IlTypes.IsDictCollection(st))) return null;
                var source = BuildExpr(srcArg.A!);
                if (source == null) return null;
                var cargs = One(source);
                foreach (var c in ListElementCopier(t)) cargs.Add(c);
                return new IlCall("List.Copy", cargs, false, null);
            }
            if (init == null) return new IlTable(new List<IlTableEntry>(), IlTypes.Display(t.Elem), null, false, null, false, null);
            var items = new List<IlTableEntry>();
            if (init.List != null)
            {
                foreach (var e in init.List)
                {
                    var built = BuildExpr(e);
                    if (built == null) return null;
                    items.Add(new IlTableEntry(null, WrapStructCopy(e, built), null));
                }
            }
            return new IlTable(items, IlTypes.Display(t.Elem), null, false, null, IlTypes.HasNilListElements(t), null);
        }
        if (IlTypes.IsQueue(t)) return new IlNewQueue(IlTypes.Display(t), null);
        if (IlTypes.IsHashSet(t))
        {
            var created = new IlNewHashSet(IlTypes.Display(t), null);
            if (init == null) return created;
            // C# と同じく項ごとに Add する
            var stats = new List<IlStat>();
            stats.Add(new IlLocal("__tcs_set", created, null, null));
            if (init.List != null)
            {
                foreach (var e in init.List)
                {
                    var built = BuildExpr(e);
                    if (built == null) return null;
                    stats.Add(new IlCallStat(new IlCall("TcsHashSet.Add", Two(new IlVar("__tcs_set", null), built), false, null), null));
                }
            }
            stats.Add(new IlReturn(new IlVar("__tcs_set", null), null));
            return new IlIife(stats, null);
        }
        if (IlTypes.IsDict(t))
        {
            if (init == null) return new IlTable(new List<IlTableEntry>(), IlTypes.Display(t.Arg(1)), IlTypes.Display(t.Arg(0)), false, null, false, null);
            var entries = new List<IlTableEntry>();
            if (init.List != null)
            {
                foreach (var e in init.List)
                {
                    Node keyNode;
                    Node valueNode;
                    if (e.Kind == NodeKind.ArrayInit && e.Count == 2)
                    {
                        keyNode = e.List![0];
                        valueNode = e.List[1];
                    }
                    else if (e.Kind == NodeKind.IndexInit)
                    {
                        keyNode = e.A!;
                        valueNode = e.B!;
                    }
                    else return null;
                    var key = BuildExpr(keyNode);
                    var value = BuildExpr(valueNode);
                    if (key == null || value == null) return null;
                    entries.Add(new IlTableEntry(DictKey(t, keyNode, key), WrapStructCopy(valueNode, value), null));
                }
            }
            return new IlTable(entries, IlTypes.Display(t.Arg(1)), IlTypes.Display(t.Arg(0)), false, null, false, null);
        }
        // `new T()` (T : new()) は受け取った既定値の記述子から新しい値を作る
        if (t.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(t);
            if (passed != null) return new IlCall("__tcs_nt", One(passed), false, null);
            return Unported("new T()", creation);
        }
        if (t.Decl != null && t.Decl.IsRef)
        {
            // --ref の型は名前付きの table (ctor の引数は対象外)
            if (creation.Count > 0) return null;
            return BuildRefTypeTable(init, IlTypes.Display(t));
        }
        if (IlTypes.IsUtf8Bytes(t))
        {
            var uargs = new List<IlExpr>();
            if (creation.List != null)
            {
                foreach (var a in creation.List)
                {
                    var b = BuildExpr(a.A!);
                    if (b == null) return null;
                    uargs.Add(b);
                }
            }
            return new IlCall(uargs.Count == 0 ? "TinySystem.Utf8.Default" : "TinySystem.Utf8.New", uargs, false, null);
        }
        // facade の型 (TinySystem.Random) は user の型と同じ名前でもぶつからないよう修飾する
        if (t.Kind == TypeKind.Random)
        {
            var rargs = new List<IlExpr>();
            if (creation.List != null)
            {
                foreach (var a in creation.List)
                {
                    var b = BuildExpr(a.A!);
                    if (b == null) return null;
                    rargs.Add(b);
                }
            }
            return new IlNewObj("TinySystem.Random", rargs, "new", null);
        }
        if (t.Decl == null) return Unported("builtin type creation " + t.Display(), creation);
        var args = new List<IlExpr>();
        var ctor = creation.Sym;
        if (creation.List != null)
        {
            for (int i = 0; i < creation.List.Count; i++)
            {
                var a = creation.List[i];
                if (a.Has(Mods.Out) || a.Has(Mods.Ref) || a.Has(Mods.In)) return null;
                var built = BuildExpr(a.A!);
                if (built == null) return null;
                args.Add(WrapArgumentCopy(ctor, creation, i, built));
            }
        }
        bool hasCtorArgs = args.Count > 0;
        if (ctor != null && ctor.Params != null)
        {
            for (int i = args.Count; i < ctor.Params.Count; i++)
            {
                var p = ctor.Params[i];
                // 型引数を含む型の既定値は作った型の型引数で決まる既定値
                IlExpr? d = null;
                if (p.Default != null) d = ContainsTypeParam(p.Type) ? DefaultIl(p.Type.Subst(t.Args, null)) : BuildExpr(p.Default);
                if (d != null) args.Add(d);
                else args.Add(new IlLit("nil", null, null));
            }
        }
        // generic な型の new / ctor は型引数の既定値を受ける
        if (ctor != null && IlTypes.PassesTypeArgDefaults(t.Decl))
        {
            foreach (var a in TypeArgDefaultArgs(creation, ctor)) args.Add(a);
        }
        else if (ctor == null && IlTypes.PassesTypeArgDefaults(t.Decl) && t.Args != null)
        {
            foreach (var a in t.Args) args.Add(TypeArgDefaultValue(a));
        }
        var ctorName = ctor != null && ctor.Kind == SymKind.Ctor && ctor.Decl != null && ctor.Decl.Kind == NodeKind.Ctor ? IlNames.CtorName(ctor) : "ctor";
        IlExpr made;
        if (IlTypes.IsUserStruct(t) && hasCtorArgs) made = new IlCall(TypeName(t) + "." + ctorName, args, false, null);
        else made = new IlNewObj(TypeName(t), args, ctorName == "ctor" ? "new" : ctorName, null);
        if (init != null) return BuildObjectInitializerExpr(made, init, t);
        return made;
    }

    // object の初期化子: `new T(args) { M = v, Nested = { X = 1 }, Items = { a, b } }` を IIFE に落とす。
    // 入れ子の初期化子は C# と同じく既存の member への代入 / Add (new はしない)
    private IlExpr? BuildObjectInitializerExpr(IlExpr ctor, Node init, TypeSym? created)
    {
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_init", ctor, null, null));
        if (!AddInitializerStats(new IlVar("__tcs_init", null), init, stats, created)) return null;
        stats.Add(new IlReturn(new IlVar("__tcs_init", null), null));
        return new IlIife(stats, null);
    }

    // owner は初期化する値の型 (member の型の型引数を写す)
    private bool AddInitializerStats(IlExpr target, Node init, List<IlStat> stats, TypeSym? owner)
    {
        if (init.List == null) return true;
        foreach (var e in init.List)
        {
            if (e.Kind != NodeKind.MemberInit) return false;
            var initSym = e.Sym;
            TypeSym? memberType = initSym != null ? initSym.Type : null;
            if (memberType != null && owner != null && owner.Args != null && owner.Args.Count > 0) memberType = memberType.Subst(owner.Args, null);
            var initName = initSym != null ? N(initSym) : e.Text;
            bool customProp = initSym != null && initSym.Kind == SymKind.Property && IsCustomProperty(initSym);
            string? structOwner = customProp && IlTypes.IsUserStruct(initSym!.Owner!.SelfType()) ? TypeRef(initSym.Owner) : null;
            var rhsNode = e.A!;
            if (rhsNode.Kind == NodeKind.ObjectInit || rhsNode.Kind == NodeKind.CollectionInit)
            {
                IlExpr member = customProp ? PropGet(target, initName, false, structOwner, new List<IlExpr>()) : Ex(new IlField(target, initName, null));
                if (rhsNode.Kind == NodeKind.ObjectInit)
                {
                    if (!AddInitializerStats(member, rhsNode, stats, memberType)) return false;
                    continue;
                }
                if (!IlTypes.IsList(memberType)) return false;
                if (rhsNode.List != null)
                {
                    foreach (var item in rhsNode.List)
                    {
                        var built = BuildExpr(item);
                        if (built == null) return false;
                        stats.Add(new IlCallStat(ListAdd(memberType!, member, One(WrapStructCopy(item, built))), null));
                    }
                }
                continue;
            }
            var rhs = BuildExpr(rhsNode);
            if (rhs == null) return false;
            var value = TypeArgStoreCopy(memberType, WrapStructCopy(rhsNode, rhs));
            if (customProp) stats.Add(new IlCallStat(PropSet(target, initName, false, value, structOwner, new List<IlExpr>()), null));
            else stats.Add(new IlAssign(new IlField(target, initName, null), value, null));
        }
        return true;
    }

    private IlExpr? BuildRefTypeTable(Node? init, string type)
    {
        var entries = new List<IlTableEntry>();
        if (init == null || init.List == null) return new IlTable(entries, null, null, false, type, false, null);
        foreach (var e in init.List)
        {
            if (e.Kind != NodeKind.MemberInit || e.A == null || e.A.Kind == NodeKind.ObjectInit || e.A.Kind == NodeKind.CollectionInit) return null;
            var value = BuildExpr(e.A);
            if (value == null) return null;
            entries.Add(new IlTableEntry(null, WrapStructCopy(e.A, value), e.Sym != null ? N(e.Sym) : e.Text));
        }
        return new IlTable(entries, null, null, false, type, false, null);
    }

    // 要素を写す List の操作 (copy constructor、GetRange) に渡す、変更できる struct の要素の型別の __copy
    private List<IlExpr> ListElementCopier(TypeSym? listType)
    {
        var r = new List<IlExpr>();
        var elem = IlTypes.ElementOf(listType);
        if (elem != null && IlTypes.IsMutableStruct(elem)) r.Add(new IlField(new IlVar(TypeName(elem), null), "__copy", null));
        return r;
    }

    // ---- 配列 ----

    private IlExpr? BuildNewArray(Node na)
    {
        var at = na.Type;
        var elem = at != null && at.Kind == TypeKind.Array ? at.Elem : null;
        var elemText = elem != null ? IlTypes.Display(elem) : null;
        if (na.C != null) return BuildArrayItems(na.C, elemText);
        if (na.B != null && elem != null)
        {
            var len = BuildExpr(na.B);
            if (len == null) return null;
            // 型引数の要素は受け取った既定値 (struct の記述子なら要素ごとに zero 値)
            IlExpr? d = elem.Kind == TypeKind.TypeParam ? TypeArgDefaultValue(elem) : DefaultIl(elem);
            return new IlNewArray(elemText!, len, d, null);
        }
        return new IlTable(new List<IlTableEntry>(), elemText, null, true, null, false, null);
    }

    private IlExpr? BuildArrayItems(Node init, string? elementType)
    {
        var items = new List<IlTableEntry>();
        if (init.List != null)
        {
            foreach (var e in init.List)
            {
                var built = BuildExpr(e);
                if (built == null) return null;
                items.Add(new IlTableEntry(null, WrapStructCopy(e, built), null));
            }
        }
        return new IlTable(items, elementType, null, true, null, false, null);
    }

    // ---- 単項演算 ----

    private IlExpr? BuildPrefixUnary(Node u)
    {
        var op = u.Text;
        if (op == "++" || op == "--") return BuildIncrementExpr(u.A!, op == "++", true);
        if (op == "!" && u.A != null && false) return null;
        var operand = BuildExpr(u.A!);
        if (operand == null) return null;
        if (op == "-" && u.Sym != null && u.Sym.Builtin == "")
        {
            var operands = new List<Node>();
            operands.Add(u.A!);
            var userNeg = TryBuildUserOperatorCall(u, operands, One(operand));
            if (userNeg != null) return userNeg;
        }
        if (IlTypes.IsNullableValueType(u.A!.Type))
        {
            if (op == "-") return new IlLiftedUn(IlUnOp.Neg, operand, null);
            if (op == "!") return new IlLiftedUn(IlUnOp.Not, operand, null);
            if (op == "~") return new IlLiftedUn(IlUnOp.BitNot, operand, null);
            return null;
        }
        if (op == "-" && u.HasConst && u.Const != null && u.Const.Kind == ConstKind.Int && u.Const.I == -2147483647 - 1) return IntMinValueIl();
        if (op == "-" && _checked && u.Type != null && u.Type.Kind == TypeKind.Int) return new IlCall("__tcs_cneg", One(operand), false, null);
        if (op == "-") return new IlUn(IlUnOp.Neg, operand, null);
        if (op == "!") return new IlUn(IlUnOp.Not, operand, null);
        if (op == "~") return new IlUn(IlUnOp.BitNot, operand, null);
        if (op == "+") return operand;
        return null;
    }

    // ---- cast ----

    private IlExpr? BuildCast(Node cast)
    {
        var target = cast.Type!;
        var src = cast.B!;
        // 暗黙に変換できる cast は binder が式を変換先へ変換している。参照実装と同じく元の式と型から組み立てる
        if (src.Kind == NodeKind.Conv && src.A != null && (src.Info == ConvKind.Identity || src.Info == ConvKind.IntToFloat || src.Info == ConvKind.NullableIntToFloat)) src = src.A;
        var srcType = src.Type;
        // (int)'a' などの定数の cast は畳む
        if (cast.HasConst && cast.Const != null && cast.Const.Kind == ConstKind.Int && IlTypes.IsChar(srcType)) return new IlLit("" + cast.Const.I, null, null);
        if (IlTypes.IsChar(target) && srcType != null && (IlTypes.IsFloating(srcType) || IlTypes.IsIntegralSource(srcType) || srcType.Kind == TypeKind.Enum) && !IlTypes.IsChar(srcType))
        {
            var c = BuildExpr(src);
            if (c == null) return null;
            if (IlTypes.IsFloating(srcType)) c = new IlCall("__tcs_trunc", One(c), false, null);
            // 範囲が分かっている元 (byte、0..255 の定数) は検査を省く
            bool small = srcType.Kind == TypeKind.Byte || (cast.HasConst && cast.Const != null && cast.Const.Kind == ConstKind.Int && cast.Const.I >= 0 && cast.Const.I <= 255)
                || (src.HasConst && src.Const != null && src.Const.Kind == ConstKind.Float && src.Const.F > -1 && src.Const.F < 256);
            return small ? c : Ex(new IlCall("__tcs_char", One(c), false, null));
        }
        if (IlTypes.IsIntegral(target) && IlTypes.IsFloating(srcType))
        {
            var f = BuildExpr(src);
            return f == null ? null : new IlCall("__tcs_trunc", One(f), false, null);
        }
        var value = BuildExpr(src);
        if (value == null) return null;
        if (srcType == null) return value;
        if (target.Kind == TypeKind.Int || target.Kind == TypeKind.Float || target.Kind == TypeKind.Double)
        {
            bool runtimeTest = (srcType.IsReferenceLike || srcType.Kind == TypeKind.TypeParam) && srcType.Kind != TypeKind.Null;
            if (runtimeTest)
                return new IlNumericConvert(value, target.Kind == TypeKind.Int ? "int" : "float", target.Kind == TypeKind.Int ? "integer" : "float", null);
            bool toInt = target.Kind == TypeKind.Int;
            // T? は特別な型ではないので変換が残る (参照実装の NumericCastIl)
            if (!toInt && IlTypes.IsFloating(srcType) && !IlTypes.IsNullableValueType(srcType)) return value;
            return new IlNumericConvert(value, toInt ? "int" : "float", null, null);
        }
        // (float?)x で x が int?: 値があるときだけ変換する
        if (target.Kind == TypeKind.Nullable && target.Elem.Kind == TypeKind.Float && srcType.Kind == TypeKind.Nullable && IlTypes.IsIntegralSource(srcType.Elem))
            return new IlLiftedUn(IlUnOp.ToFloat, value, null);
        // (int?)x: 明示の T → T? の変換
        if (target.Kind == TypeKind.Nullable && srcType.Kind != TypeKind.Nullable && srcType.Kind != TypeKind.Null)
            return new IlNullableWrap(value, IlTypes.Display(srcType), null);
        // user の class への downcast は IlCast (C backend の実行時の検査点。upcast と同じ型は透過)
        // 元の型は名前の付いた型 (object、interface、class など。型引数と配列を除く。参照実装の INamedTypeSymbol)
        bool namedSource = srcType.Kind != TypeKind.TypeParam && srcType.Kind != TypeKind.Array && srcType.Kind != TypeKind.Null;
        if (target.Kind == TypeKind.Class && target.Decl != null && namedSource && !TypeSym.Same(target, srcType) && !(srcType.Decl != null && IsDerivedFrom(srcType.Decl, target.Decl)))
            return new IlCast(value, CheckTypeName(target), TypeRef(target.Decl), null);
        bool refTest = (IsReferenceType(srcType) || srcType.Kind == TypeKind.TypeParam) && !IsImplicitReference(srcType, target);
        if (!IsReferenceType(target) && target.Kind != TypeKind.Bool && target.Kind != TypeKind.Nullable) return value;
        string? check = null;
        IlExpr? checkArg = null;
        if (refTest)
        {
            if (target.Kind == TypeKind.String)
            {
                check = "__tcs_tcast";
                checkArg = new IlLit("\"string\"", null, null);
            }
            else if (target.Kind == TypeKind.Bool)
            {
                check = "__tcs_vcast";
                checkArg = new IlLit("\"boolean\"", null, null);
            }
            else if (target.Kind == TypeKind.Interface)
            {
                check = "__tcs_icast";
                checkArg = new IlLit(IlLiterals.QuoteString(TypeName(target)), null, null);
            }
            else if (target.Kind == TypeKind.Class && target.Decl != null)
            {
                check = "__tcs_cast";
                checkArg = new IlVar(TypeRef(target.Decl), null);
            }
        }
        // cast の型の `?` の注釈は #nullable enable の文脈だけで残る
        return new IlRefCast(value, IlTypes.Display(cast.File.NullableEnabled ? target : target.WithoutAnnotation()), check, checkArg, null);
    }

    private static bool IsDerivedFrom(TypeDeclSym source, TypeDeclSym target)
    {
        TypeDeclSym? cur = source;
        while (cur != null)
        {
            if (cur == target) return true;
            cur = cur.BaseClass;
        }
        return false;
    }

    // 暗黙の参照の変換があるか (同じ型、object へ、基底の class へ、実装する interface へ、null から)
    private static bool IsImplicitReference(TypeSym from, TypeSym to)
    {
        if (TypeSym.Same(from, to) || to.Kind == TypeKind.Object || from.Kind == TypeKind.Null) return true;
        if (from.Decl != null && to.Decl != null)
        {
            if (IsDerivedFrom(from.Decl, to.Decl)) return true;
            if (to.Decl.IsInterface)
            {
                foreach (var i in IlTypes.AllInterfaces(from.Decl))
                {
                    if (i.Decl == to.Decl) return true;
                }
            }
        }
        return false;
    }

    // `x as I` (interface): 型の検査を通れば値、通らなければ nil
    private IlExpr? BuildAsExpr(Node asExpr)
    {
        var target = asExpr.Type;
        if (target == null || target.Kind != TypeKind.Interface) return Unported("as", asExpr);
        var operand = BuildExpr(asExpr.A!);
        if (operand == null) return null;
        var inputType = NaturalType(asExpr.A!);
        return BindPatternInput(operand, input =>
        {
            var check = BuildTypeCheck(input, inputType, target, null);
            if (check == null) return null;
            return new IlTernary(check, new IlRefCast(input, IlTypes.Display(target), null, null, null), new IlLit("nil", null, null), null);
        });
    }

    // ---- 添字 ----

    private IlExpr? BuildElementAccess(Node ea)
    {
        if (ea.Value == 1 && ea != _condBinding) return Unported("conditional index", ea);
        var recv = BuildExpr(ea.A!);
        var index = BuildExpr(ea.List![0]);
        if (recv == null || index == null) return null;
        var rt = ea.A!.Type;
        if (IlTypes.IsUtf8Bytes(rt)) return new IlCall("TinySystem.Utf8.At", Two(recv, index), false, null);
        bool plusOne = IlTypes.IsList(rt) || IlTypes.IsArray(rt);
        return new IlIndex(recv, DictKey(rt, ea.List![0], index), plusOne, IlTypes.HasNilableElements(rt), IlTypes.HasBoolElements(rt), IlTypes.HasNilListElements(rt), null);
    }

    // ---- Nullable<T> (il-spec §3 / §13) ----

    private static bool IsNullableWrapped(IlExpr built)
    {
        while (built is IlParen paren) built = paren.E;
        return built is IlNullableWrap;
    }

    // 変換先が T? で式自体が T? でなければ IlNullableWrap (T は変換先の中の型)。null / default の literal はそのまま nil
    private IlExpr ApplyNullableConversion(Node conv, IlExpr built)
    {
        if (IsNullableWrapped(built)) return built;
        var target = conv.Type;
        var src = conv.A!.Type;
        if (target == null || !IlTypes.IsNullableValueType(target) || src == null || IlTypes.IsNullableValueType(src)
            || src.Kind == TypeKind.Null || src.Kind == TypeKind.Object || src.IsError)
            return built;
        if (StripConv(conv.A).Kind == NodeKind.DefaultLit && StripConv(conv.A).A == null) return built;
        // 括弧の式は内側を包む (Roslyn は括弧の内側の式にも同じ変換先の型を報告する)
        return WrapInsideParen(built, target.Elem);
    }

    private static bool IsNullLiteral(Node e)
    {
        var x = StripConv(e);
        return x.Kind == NodeKind.NullLit || (x.Kind == NodeKind.DefaultLit && x.A == null);
    }

    private static IlLiftedOp? LiftedOpFor(string op, TypeSym? underlying)
    {
        switch (op)
        {
            case "+": return IlLiftedOp.Add;
            case "-": return IlLiftedOp.Sub;
            case "*": return IlLiftedOp.Mul;
            case "/": return IlTypes.IsIntegral(underlying) ? IlLiftedOp.DivInt : IlLiftedOp.DivFloat;
            case "%": return IlTypes.IsIntegral(underlying) ? IlLiftedOp.RemInt : IlLiftedOp.RemFloat;
            case "&": return underlying != null && underlying.Kind == TypeKind.Bool ? IlLiftedOp.And : IlLiftedOp.BitAnd;
            case "|": return underlying != null && underlying.Kind == TypeKind.Bool ? IlLiftedOp.Or : IlLiftedOp.BitOr;
            case "^": return IlLiftedOp.BitXor;
            case "<<": return IlLiftedOp.Shl;
            case ">>": return IlLiftedOp.Shr;
            case "==": return IlLiftedOp.Eq;
            case "!=": return IlLiftedOp.Ne;
            case "<": return IlLiftedOp.Lt;
            case "<=": return IlLiftedOp.Le;
            case ">": return IlLiftedOp.Gt;
            case ">=": return IlLiftedOp.Ge;
        }
        return null;
    }

    // ++ / -- の値: T? なら lifted の Add / Sub
    private static IlExpr StepValue(Node operand, IlExpr read, bool increment)
    {
        if (IlTypes.IsNullableValueType(NaturalType(operand)))
            return new IlLiftedBin(increment ? IlLiftedOp.Add : IlLiftedOp.Sub, read, new IlNullableWrap(new IlLit("1", null, null), "int", null), null);
        return new IlBin(increment ? IlBinOp.AddNum : IlBinOp.Sub, read, new IlLit("1", null, null), null);
    }

    // null との比較 / ?? / lifted の二項演算。当てはまらなければ null (通常の経路へ)
    private IlExpr? TryBuildNullableBinary(Node bin, IlExpr left, IlExpr right)
    {
        var op = bin.Text;
        var leftType = NaturalType(bin.A!);
        var rightType = NaturalType(bin.B!);
        // 文字列の連結は lifted ではない
        if (leftType.Kind == TypeKind.String || rightType.Kind == TypeKind.String || bin.Type!.Kind == TypeKind.String) return null;
        bool leftNullable = IlTypes.IsNullableValueType(leftType);
        bool rightNullable = IlTypes.IsNullableValueType(rightType);
        bool isEq = op == "==";
        bool isNe = op == "!=";
        if ((isEq || isNe) && leftNullable && IsNullLiteral(bin.B!))
            return isEq ? new IlUn(IlUnOp.Not, new IlNullableHasValue(left, null), null) : new IlNullableHasValue(left, null);
        if ((isEq || isNe) && rightNullable && IsNullLiteral(bin.A!))
            return isEq ? new IlUn(IlUnOp.Not, new IlNullableHasValue(right, null), null) : new IlNullableHasValue(right, null);
        // null 許容でない値の型と null の比較 (常に偽 / 真)。値は T? へ包んで比べる (括弧は内側を包む)
        if ((isEq || isNe) && IsNullLiteral(bin.B!) && IsPlainValueType(leftType))
            return new IlBin(isEq ? IlBinOp.Eq : IlBinOp.Ne, WrapInsideParen(left, leftType), right, null);
        if ((isEq || isNe) && IsNullLiteral(bin.A!) && IsPlainValueType(rightType))
            return new IlBin(isEq ? IlBinOp.Eq : IlBinOp.Ne, left, WrapInsideParen(right, rightType), null);
        if (op == "??") return leftNullable ? new IlNullableGetOrDefault(left, right, null) : null;
        if (!leftNullable && !rightNullable) return null;
        var resultType = IlTypes.UnwrapNullable(bin.Type!);
        bool comparison = isEq || isNe || op == "<" || op == "<=" || op == ">" || op == ">=";
        var underlying = resultType.Kind == TypeKind.Bool && !comparison ? resultType : IlTypes.UnwrapNullable(leftNullable ? leftType : rightType);
        var lop = LiftedOpFor(op, underlying);
        if (lop == null) return null;
        // 括弧の式は内側を包む (Roslyn は括弧の内側の式にも同じ変換先の型を報告する)
        var l2 = IsNullableWrapped(left) || leftNullable ? left : WrapInsideParen(left, leftType);
        var r2 = IsNullableWrapped(right) || rightNullable ? right : WrapInsideParen(right, rightType);
        return new IlLiftedBin(lop.Value, l2, r2, null);
    }

    private static bool IsPlainValueType(TypeSym t) =>
        t.Kind == TypeKind.Int || t.Kind == TypeKind.Float || t.Kind == TypeKind.Bool || t.Kind == TypeKind.Char || t.Kind == TypeKind.Byte
        || t.Kind == TypeKind.UInt || t.Kind == TypeKind.Short || t.Kind == TypeKind.Enum;

    private static IlExpr WrapInsideParen(IlExpr e, TypeSym t)
    {
        if (IsNullableWrapped(e)) return e;
        if (e is IlParen p) return new IlParen(WrapInsideParen(p.E, t), p.Origin);
        return new IlNullableWrap(e, IlTypes.Display(t), null);
    }

    // 文の位置の Dictionary.Add は key への代入 (IlAssign と IlIndex) にする。key が既にあっても上書きし、.NET の ArgumentException に当たる fault は無い
    private bool BuildDictAddStatInto(Node call, IlPos? origin, List<IlStat> acc)
    {
        if (call.Sym == null || call.Sym.Builtin != "Dict.Add" || call.Count != 2) return false;
        var callee = call.A!;
        if (callee.Kind != NodeKind.Member || callee.A == null) return false;
        foreach (var a in call.List!)
        {
            if (a.Has(Mods.Ref) || a.Has(Mods.Out) || a.Has(Mods.In)) return false;
        }
        var recv = BuildExpr(callee.A);
        var key = BuildExpr(call.List![0].A!);
        var valueArg = call.List[1].A!;
        var value = BuildExpr(valueArg);
        if (recv == null || key == null || value == null) return false;
        acc.Add(new IlAssign(new IlIndex(recv, DictKey(NaturalType(callee.A), call.List![0].A, key), false, false, false, false, null), WrapStructCopy(valueArg, value), origin));
        return true;
    }

    // ---- 代入 ----

    private bool BuildAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var left = assign.A!;
        if (left.Kind == NodeKind.Name && left.Text == "_" && left.Info != RefKind.Local)
        {
            var discarded = BuildExpr(assign.B!);
            if (discarded == null) return false;
            acc.Add(new IlLocal("_", discarded, null, origin));
            return true;
        }
        if (left.Kind == NodeKind.Tuple) return UnportedStat("deconstruct assignment", assign);
        if (NeedsStructSlotAssign(assign)) return BuildStructSlotAssignInto(assign, origin, acc);
        if (IsCustomPropertyTarget(left)) return BuildPropAssignInto(assign, origin, acc);
        if (assign.Text == "??=") return BuildCoalesceAssignInto(assign, origin, acc);
        if (NeedsLoweredAssign(assign)) return BuildLoweredAssignInto(assign, origin, acc);
        if (assign.Text == "=")
        {
            var target = BuildExpr(left);
            var value = BuildExpr(assign.B!);
            if (target == null || value == null) return false;
            value = WrapStructCopy(assign.B!, value);
            if (target is IlField || target is IlIndex) value = TypeArgStoreCopy(NaturalType(left), value);
            // target が素の local なら条件式の右辺を if 文へ
            if (value is IlTernary at && target is IlVar)
            {
                var thenB = new IlBlock(One(new IlAssign(target, at.T, null)));
                var elseB = new IlBlock(One(new IlAssign(target, at.F, null)));
                acc.Add(new IlIf(Arm(at.Cond, thenB), elseB, origin));
                return true;
            }
            if (value is IlIife ai && target is IlVar)
            {
                var shape = TryGetValueChainShape(ai);
                if (shape != null)
                {
                    foreach (var st in shape.Setup) acc.Add(IlTree.WithOrigin(st, origin));
                    if (shape.Chain != null) acc.Add(IlTree.WithOrigin(SwitchChainAsAssigns(shape.Chain, target), origin));
                    else acc.Add(new IlAssign(target, shape.Tail!, origin));
                    return true;
                }
            }
            acc.Add(new IlAssign(target, value, origin));
            return true;
        }
        var op = CompoundOperator(assign);
        if (op == null) return false;
        var read = BuildExpr(left);
        var right = BuildExpr(assign.B!);
        if (read == null || right == null) return false;
        // `x op= a ⊕ b` は x = x op (a ⊕ b)。演算子の優先順位に依らず右辺を 1 項として括る
        if (right is IlBin || right is IlLiftedBin || right is IlTernary) right = new IlParen(right, null);
        IlExpr? applied;
        if (WritesLocalReadBy(left, assign.B!))
        {
            var r = right;
            applied = SnapshotOperand(read, l => BuildCompoundValue(assign, op, l, r));
        }
        else applied = BuildCompoundValue(assign, op, read, right);
        if (applied == null) return false;
        acc.Add(new IlAssign(read, applied, origin));
        return true;
    }

    // 複合代入の演算子の綴り (`..` は連結、`~` は xor)。string の += は `..`。bool の &= / |= / ^= は対象外 (null)
    private static string? CompoundOperator(Node assign)
    {
        var lt = NaturalType(assign.A!);
        bool boolTarget = lt.Kind == TypeKind.Bool || (lt.Kind == TypeKind.Nullable && lt.Elem.Kind == TypeKind.Bool);
        switch (assign.Text)
        {
            case "+=": return lt.Kind == TypeKind.String ? ".." : "+";
            case "-=": return "-";
            case "*=": return "*";
            case "/=": return "/";
            case "%=": return "%";
            case "&=": return boolTarget ? null : "&";
            case "|=": return boolTarget ? null : "|";
            case "^=": return boolTarget ? null : "~";
            case "<<=": return "<<";
            case ">>=": return ">>";
        }
        return null;
    }

    private IlExpr? BuildCompoundValue(Node assign, string op, IlExpr read, IlExpr right)
    {
        // `x op= y` の user の演算子: 引数の位置なので右辺の括りは外す
        if (assign.Sym != null && assign.Sym.Builtin == "" && assign.Sym.Kind == SymKind.Method)
        {
            var operands = new List<Node>();
            operands.Add(assign.A!);
            operands.Add(assign.B!);
            var built = new List<IlExpr>();
            built.Add(read);
            built.Add(right is IlParen paren ? paren.E : right);
            var userOp = TryBuildUserOperatorCall(assign, operands, built);
            if (userOp != null) return userOp;
            return Unported("user compound operator", assign);
        }
        var type = NaturalType(assign.A!);
        if (IlTypes.IsNullableValueType(type))
        {
            var lifted = LiftedOpFor(op == "~" ? "^" : op, IlTypes.UnwrapNullable(type));
            if (lifted != null) return new IlLiftedBin(lifted.Value, read, right, null);
        }
        if (op == "/" && IlTypes.IsIntegral(type)) return new IlCall("__tcs_idiv", Two(read, right), false, null);
        if (op == "%" && IlTypes.IsIntegral(type)) return new IlCall("__tcs_irem", Two(read, right), false, null);
        if (op == "%" && IlTypes.IsFloating(type)) return new IlCall("math.fmod", Two(read, right), false, null);
        if (op == "..")
            return new IlBin(IlBinOp.Concat, new IlParen(new IlBin(IlBinOp.Or, read, new IlLit("\"\"", null, null), null), null), WrapConcatOperand(assign.B!, right), null);
        switch (op)
        {
            case "+": return new IlBin(IlBinOp.AddNum, read, right, null);
            case "-": return new IlBin(IlBinOp.Sub, read, right, null);
            case "*": return new IlBin(IlBinOp.Mul, read, right, null);
            case "/": return new IlBin(IlBinOp.DivNum, read, right, null);
            case "%": return new IlBin(IlBinOp.RemNum, read, right, null);
            case "&": return new IlBin(IlBinOp.BitAnd, read, right, null);
            case "|": return new IlBin(IlBinOp.BitOr, read, right, null);
            case "~": return new IlBin(IlBinOp.BitXor, read, right, null);
            case "<<": return new IlBin(IlBinOp.Shl, read, right, null);
            case ">>": return new IlBin(IlBinOp.Shr, read, right, null);
        }
        return null;
    }

    private bool BuildIncrementInto(Node operand, bool increment, IlPos? origin, List<IlStat> acc)
    {
        if (IsCustomPropertyTarget(operand) || NeedsLoweredLvalue(operand)) return BuildLoweredIncrementInto(operand, increment, origin, acc);
        var target = BuildExpr(operand);
        if (target == null) return false;
        acc.Add(new IlAssign(target, StepValue(operand, target, increment), origin));
        return true;
    }

    // custom property (accessor を通す) への代入か
    private static bool IsCustomPropertyTarget(Node left)
    {
        var x = StripConv(left);
        if ((x.Kind == NodeKind.Name || x.Kind == NodeKind.Member) && x.Sym != null && x.Sym.Kind == SymKind.Property) return IsCustomProperty(x.Sym);
        return false;
    }

    // ---- 代入先の格納場所 (参照実装の AssignTarget / Lowered) ----

    // 参照型か (Roslyn の IsReferenceType。型引数は制約に依らず参照型としない)
    private static bool IsReferenceType(TypeSym? t)
    {
        if (t == null || t.IsValueType) return false;
        switch (t.Kind)
        {
            case TypeKind.TypeParam:
            case TypeKind.Null:
            case TypeKind.Error:
            case TypeKind.Void:
            case TypeKind.MethodGroup:
            case TypeKind.StaticRef:
                return false;
        }
        return true;
    }

    // 評価が副作用を持ちうる構文 (呼び出し、構築、?.、代入、with、++ / --)
    private static bool HasSideEffectSyntax(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.Call:
            case NodeKind.New:
            case NodeKind.Assign:
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                return true;
            case NodeKind.Member:
            case NodeKind.Index:
                if (n.Value == 1) return true;
                break;
        }
        return AnyChild(n, c => HasSideEffectSyntax(c));
    }

    // n の中に custom property の読みがあるか
    private static bool ReadsCustomProperty(Node n)
    {
        if ((n.Kind == NodeKind.Name || n.Kind == NodeKind.Member) && n.Sym != null && n.Sym.Kind == SymKind.Property && IsCustomProperty(n.Sym)) return true;
        return AnyChild(n, c => ReadsCustomProperty(c));
    }

    // lvalue の受け手 / 添字 (evaluated) の評価に副作用があるか。custom property の getter は呼び出しなので含める。
    // 値型の受け手は temp が copy になり更新が消えるので、参照型の prefix まで struct の field / 配列の要素で辿れるときだけ getter を理由にする
    private static bool HasLvalueSideEffect(Node evaluated, Node receiver)
    {
        if (HasSideEffectSyntax(evaluated)) return true;
        return (IsReferenceType(NaturalType(receiver)) || StructPlaceBase(receiver) != null) && ReadsCustomProperty(evaluated);
    }

    // 値型の place `prefix.f1.f2` / `prefix[i].f` を struct の field / 配列の要素で辿った先の参照型の prefix。辿れなければ null
    private static Node? StructPlaceBase(Node place)
    {
        while (true)
        {
            if (!NaturalType(place).IsValueType) return place;
            var p = StripConv(place);
            if (p.Kind == NodeKind.Paren) place = p.A!;
            else if (p.Kind == NodeKind.Member && p.A != null && p.Sym != null && p.Sym.Kind == SymKind.Field && !p.Sym.IsStatic && p.Info == RefKind.Field) place = p.A;
            else if (p.Kind == NodeKind.Index && p.Value != 1 && NaturalType(p.A!).Kind == TypeKind.Array) return p.A;
            else return null;
        }
    }

    // 受け手や添字を先に評価しておく必要がある lvalue (副作用のある受け手 / 添字)
    private static bool NeedsLoweredLvalue(Node left)
    {
        var x = StripConv(left);
        if (x.Kind == NodeKind.Member && x.A != null) return HasLvalueSideEffect(x.A, x.A);
        if (x.Kind == NodeKind.Index) return HasLvalueSideEffect(x, x.A!);
        return false;
    }

    private static Node? AssignReceiver(Node left)
    {
        var x = StripConv(left);
        if (x.Kind == NodeKind.Member || x.Kind == NodeKind.Index) return x.A;
        return null;
    }

    // 代入の受け手 / 添字を temp にする必要があるか。副作用のある lvalue に加え、右辺が書き換える local を受け手 / 添字が読むとき。
    // 値型の受け手は temp が copy になり代入が消えるので対象外
    private bool NeedsLoweredAssign(Node assign)
    {
        if (assign.Text == "??=") return false;
        if (assign.Text != "=" && NeedsLoweredLvalue(assign.A!)) return true;
        var recv = AssignReceiver(assign.A!);
        return recv != null && IsReferenceType(NaturalType(recv)) && WritesLocalReadBy(assign.A!, assign.B!);
    }

    // `??=` の右辺が受け手 / 添字の読む local を書き換えるか
    private bool CoalesceRightWritesTarget(Node assign)
    {
        if (assign.Text != "??=") return false;
        var recv = AssignReceiver(assign.A!);
        return recv != null && IsReferenceType(NaturalType(recv)) && WritesLocalReadBy(assign.A!, assign.B!);
    }

    // 受け手 / 添字を temp に固定した代入先 (setup の文と、場所の式)。force は副作用が無くても固定する
    private LoweredTarget? BuildLoweredTarget(Node left, bool force)
    {
        var x = StripConv(left);
        if (x.Kind == NodeKind.Member && x.A != null && (force || HasLvalueSideEffect(x.A, x.A)))
        {
            var recv = BuildFixedReceiver(x.A);
            if (recv == null) return null;
            return new LoweredTarget(recv.Setup, new IlField(recv.Access, x.Sym != null ? N(x.Sym) : x.Text, null));
        }
        if (x.Kind == NodeKind.Index && (force || HasLvalueSideEffect(x, x.A!)))
        {
            var recv = BuildExpr(x.A!);
            var indexNode = x.List![0];
            var index = BuildExpr(indexNode);
            if (recv == null || index == null) return null;
            var rt = NaturalType(x.A!);
            bool plusOne = rt.Kind == TypeKind.List || rt.Kind == TypeKind.Array;
            var setup = new List<IlStat>();
            setup.Add(new IlLocal("__tcs_obj", recv, null, null));
            setup.Add(new IlLocal("__tcs_idx", DictKey(rt, indexNode, index), null, null));
            return new LoweredTarget(setup, new IlIndex(new IlVar("__tcs_obj", null), new IlVar("__tcs_idx", null), plusOne, IlTypes.HasNilableElements(rt), IlTypes.HasBoolElements(rt), IlTypes.HasNilListElements(rt), null));
        }
        return null;
    }

    // 受け手を 1 回だけ評価する place に固定する。参照型はそのまま temp、値型は参照型の prefix (と配列の添字) だけを temp にして
    // field / 要素の経路を組み直す (struct を copy すると書き込みが元に届かない)
    private LoweredTarget? BuildFixedReceiver(Node recv)
    {
        var baseExpr = StructPlaceBase(recv);
        if (baseExpr != null && baseExpr != recv)
        {
            var built = BuildExpr(baseExpr);
            if (built == null) return null;
            var setup = new List<IlStat>();
            setup.Add(new IlLocal("__tcs_obj", built, null, null));
            var access = BuildStructPlace(recv, baseExpr, setup);
            return access == null ? null : new LoweredTarget(setup, access);
        }
        var whole = BuildExpr(recv);
        if (whole == null) return null;
        var s = new List<IlStat>();
        s.Add(new IlLocal("__tcs_obj", whole, null, null));
        return new LoweredTarget(s, new IlVar("__tcs_obj", null));
    }

    // StructPlaceBase の経路を __tcs_obj (= baseExpr) から組み直す
    private IlExpr? BuildStructPlace(Node place, Node baseExpr, List<IlStat> setup)
    {
        if (place == baseExpr) return new IlVar("__tcs_obj", null);
        var p = StripConv(place);
        if (p != place) return BuildStructPlace(p, baseExpr, setup);
        switch (p.Kind)
        {
            case NodeKind.Paren:
                return BuildStructPlace(p.A!, baseExpr, setup);
            case NodeKind.Member:
            {
                var inner = BuildStructPlace(p.A!, baseExpr, setup);
                return inner == null ? null : new IlField(inner, N(p.Sym!), null);
            }
            case NodeKind.Index:
            {
                var index = BuildExpr(p.List![0]);
                if (index == null) return null;
                setup.Add(new IlLocal("__tcs_idx", index, null, null));
                var at = NaturalType(p.A!);
                return new IlIndex(new IlVar("__tcs_obj", null), new IlVar("__tcs_idx", null), true, IlTypes.HasNilableElements(at), false, IlTypes.HasNilListElements(at), null);
            }
        }
        return null;
    }

    // custom property の代入先 (受け手、名前、static か、struct の所有型 (struct の accessor は自由関数の呼び出し)、static な accessor の型引数の既定値)。
    // fixUnstable は読み直すと変わりうる参照型の受け手も temp に固定する (式の位置は setter を右辺の後に呼ぶ)
    private PropTarget? BuildPropTarget(Node left, bool fixUnstable)
    {
        var x = StripConv(left);
        if (x.Sym == null || x.Sym.Kind != SymKind.Property || !IsCustomProperty(x.Sym)) return null;
        var prop = x.Sym;
        if (x.Kind == NodeKind.Name)
        {
            if (prop.IsStatic) return new PropTarget(new List<IlStat>(), new IlVar(TypeRef(prop.Owner!), null), N(prop), true, null, StaticAccessorArgs(prop, null));
            return new PropTarget(new List<IlStat>(), new IlVar("self", null), N(prop), false, IlTypes.IsUserStruct(prop.Owner!.SelfType()) ? TypeRef(prop.Owner) : null, new List<IlExpr>());
        }
        if (x.Kind != NodeKind.Member || x.A == null) return null;
        if (prop.IsStatic) return new PropTarget(new List<IlStat>(), new IlVar(TypeRef(prop.Owner!), null), N(prop), true, null, StaticAccessorArgs(prop, x.A));
        string? owner = IlTypes.IsUserStruct(NaturalType(x.A)) ? TypeRef(prop.Owner!) : null;
        if (HasLvalueSideEffect(x.A, x.A) || (fixUnstable && !IsStableRead(x.A) && IsReferenceType(NaturalType(x.A))))
        {
            var fixedRecv = BuildFixedReceiver(x.A);
            if (fixedRecv == null) return null;
            return new PropTarget(fixedRecv.Setup, fixedRecv.Access, N(prop), false, owner, new List<IlExpr>());
        }
        var recv = BuildExpr(x.A);
        if (recv == null) return null;
        return new PropTarget(new List<IlStat>(), recv, N(prop), false, owner, new List<IlExpr>());
    }

    private static IlExpr BuildPropGet(PropTarget p)
    {
        return PropGet(p.Recv, p.Name, p.IsStatic, p.StructOwner, p.StaticArgs);
    }

    private static IlExpr PropGet(IlExpr recv, string name, bool isStatic, string? structOwner, List<IlExpr> staticArgs)
    {
        if (structOwner != null) return new IlCall(structOwner + ".get_" + name, One(recv), false, null);
        if (isStatic) return new IlDynCall(new IlField(recv, "get_" + name, null), staticArgs, null);
        return new IlInvoke(recv, "get_" + name, new List<IlExpr>(), null);
    }

    private static IlExpr BuildPropSet(PropTarget p, IlExpr value)
    {
        return PropSet(p.Recv, p.Name, p.IsStatic, value, p.StructOwner, p.StaticArgs);
    }

    private static IlExpr PropSet(IlExpr recv, string name, bool isStatic, IlExpr value, string? structOwner, List<IlExpr> staticArgs)
    {
        if (structOwner != null) return new IlCall(structOwner + ".set_" + name, Two(recv, value), false, null);
        if (isStatic)
        {
            var args = new List<IlExpr>();
            args.Add(value);
            foreach (var a in staticArgs) args.Add(a);
            return new IlDynCall(new IlField(recv, "set_" + name, null), args, null);
        }
        return new IlInvoke(recv, "set_" + name, One(value), null);
    }

    // 文の位置の custom property への代入
    private bool BuildPropAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var prop = BuildPropTarget(assign.A!, false);
        if (prop == null) return false;
        var right = BuildExpr(assign.B!);
        if (right == null) return false;
        IlStat body;
        bool needsWrap = prop.Setup.Count > 0;
        if (assign.Text == "=")
        {
            // setter の value は値渡しの引数
            body = new IlCallStat(BuildPropSet(prop, WrapStructCopy(assign.B!, right)), null);
        }
        else if (assign.Text == "??=")
        {
            var setBody = new IlBlock(One(new IlCallStat(BuildPropSet(prop, WrapStructCopy(assign.B!, right)), null)));
            body = new IlIf(Arm(IsNullIl(BuildPropGet(prop), NaturalType(assign.A!)), setBody), null, null);
            // if の形は常に IIFE で包む
            needsWrap = true;
        }
        else
        {
            var op = CompoundOperator(assign);
            if (op == null) return false;
            var applied = BuildCompoundValue(assign, op, BuildPropGet(prop), new IlParen(right, null));
            if (applied == null) return false;
            body = new IlCallStat(BuildPropSet(prop, applied), null);
        }
        if (!needsWrap)
        {
            acc.Add(IlTree.WithOrigin(body, origin));
            return true;
        }
        var stats = new List<IlStat>();
        foreach (var st in prop.Setup) stats.Add(st);
        stats.Add(body);
        acc.Add(new IlCallStat(new IlIife(stats, null), origin));
        return true;
    }

    // 式の位置の custom property への代入: 受け手を 1 回だけ評価し、代入した値を式の値にする (getter を読み直さない)。
    // setter の value は値渡しの引数なので copy を渡し、setter が value を書き換えても式の値は変わらない
    private IlIife? BuildPropAssignExpr(Node assign)
    {
        var prop = BuildPropTarget(assign.A!, true);
        if (prop == null) return null;
        var right = BuildExpr(assign.B!);
        if (right == null) return null;
        var stats = new List<IlStat>();
        foreach (var st in prop.Setup) stats.Add(st);
        var value = new IlVar("__tcs_v", null);
        var get = BuildPropGet(prop);
        var set = new IlCallStat(BuildPropSet(prop, CopyIfMutableStruct(NaturalType(assign.A!), value)), null);
        if (assign.Text == "=")
        {
            stats.Add(new IlLocal("__tcs_v", WrapStructCopy(assign.B!, right), null, null));
            stats.Add(set);
        }
        else if (assign.Text == "??=")
        {
            stats.Add(new IlLocal("__tcs_v", get, null, null));
            var body = new List<IlStat>();
            body.Add(new IlAssign(value, WrapStructCopy(assign.B!, right), null));
            body.Add(set);
            stats.Add(new IlIf(Arm(IsNullIl(value, NaturalType(assign.A!)), new IlBlock(body)), null, null));
        }
        else
        {
            var op = CompoundOperator(assign);
            if (op == null) return null;
            var applied = BuildCompoundValue(assign, op, get, new IlParen(right, null));
            if (applied == null) return null;
            stats.Add(new IlLocal("__tcs_v", applied, null, null));
            stats.Add(set);
        }
        stats.Add(new IlReturn(value, null));
        return new IlIife(stats, null);
    }

    // null の判定: `T?` は値の有無、参照型は nil との比較
    private static IlExpr IsNullIl(IlExpr e, TypeSym? type)
    {
        if (IlTypes.IsNullableValueType(type)) return new IlUn(IlUnOp.Not, new IlNullableHasValue(e, null), null);
        return new IlBin(IlBinOp.Eq, e, new IlLit("nil", null, null), null);
    }

    // 受け手 / 添字を temp に固定してから右辺を評価し、代入後の place を値として返す IIFE
    private IlIife? BuildLoweredAssign(Node assign)
    {
        var lowered = BuildLoweredTarget(assign.A!, true);
        if (lowered == null) return null;
        var right = BuildExpr(assign.B!);
        if (right == null) return null;
        var stats = new List<IlStat>();
        foreach (var st in lowered.Setup) stats.Add(st);
        if (assign.Text == "??=")
        {
            var body = new IlBlock(One(new IlAssign(lowered.Access, WrapStructCopy(assign.B!, right), null)));
            stats.Add(new IlIf(Arm(IsNullIl(lowered.Access, NaturalType(assign.A!)), body), null, null));
            stats.Add(new IlReturn(lowered.Access, null));
            return new IlIife(stats, null);
        }
        IlExpr? applied;
        if (assign.Text == "=") applied = WrapStructCopy(assign.B!, right);
        else
        {
            var op = CompoundOperator(assign);
            applied = op == null ? null : BuildCompoundValue(assign, op, lowered.Access, new IlParen(right, null));
        }
        if (applied == null) return null;
        stats.Add(new IlAssign(lowered.Access, applied, null));
        stats.Add(new IlReturn(lowered.Access, null));
        return new IlIife(stats, null);
    }

    private bool BuildLoweredAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var iife = BuildLoweredAssign(assign);
        if (iife == null) return false;
        acc.Add(new IlCallStat(iife, origin));
        return true;
    }

    // ++ / -- の custom property / 固定した lvalue の経路
    private bool BuildLoweredIncrementInto(Node operand, bool increment, IlPos? origin, List<IlStat> acc)
    {
        var prop = BuildPropTarget(operand, false);
        if (prop != null)
        {
            var body = new IlCallStat(BuildPropSet(prop, StepValue(operand, BuildPropGet(prop), increment)), null);
            if (prop.Setup.Count > 0)
            {
                var ps = new List<IlStat>();
                foreach (var st in prop.Setup) ps.Add(st);
                ps.Add(body);
                acc.Add(new IlDo(new IlBlock(ps), origin));
            }
            else acc.Add(new IlCallStat(body.Call, origin));
            return true;
        }
        var lowered = BuildLoweredTarget(operand, false);
        if (lowered == null) return false;
        var stats = new List<IlStat>();
        foreach (var st in lowered.Setup) stats.Add(st);
        stats.Add(new IlAssign(lowered.Access, StepValue(operand, lowered.Access, increment), null));
        acc.Add(new IlDo(new IlBlock(stats), origin));
        return true;
    }

    // ??= (文の位置、custom property 以外)
    private bool BuildCoalesceAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var built = BuildExpr(assign.B!);
        if (built == null) return false;
        var right = WrapStructCopy(assign.B!, built);
        var leftType = NaturalType(assign.A!);
        var lowered = BuildLoweredTarget(assign.A!, CoalesceRightWritesTarget(assign));
        if (lowered != null)
        {
            var stats = new List<IlStat>();
            foreach (var st in lowered.Setup) stats.Add(st);
            stats.Add(new IlIf(Arm(IsNullIl(lowered.Access, leftType), new IlBlock(One(new IlAssign(lowered.Access, right, null)))), null, null));
            acc.Add(new IlDo(new IlBlock(stats), origin));
            return true;
        }
        var left = BuildExpr(assign.A!);
        if (left == null) return false;
        acc.Add(new IlIf(Arm(IsNullIl(left, leftType), new IlBlock(One(new IlAssign(left, right, null)))), null, origin));
        return true;
    }

    // 式の位置の ??= の値。左辺が `T?` なら式の型は `T` なので、代入後の place の中の値にする
    private static IlExpr CoalesceAssignValue(Node assign, IlExpr assigned)
    {
        if (assign.Text == "??=" && IlTypes.IsNullableValueType(NaturalType(assign.A!)) && !IlTypes.IsNullableValueType(assign.Type))
            return new IlNullableValue(assigned, null);
        return assigned;
    }

    // 値型の place の struct の field を辿った起点 (参照実装の StructSlotPath)。辿れなければ null
    private static StructSlot? StructSlotPath(Node place)
    {
        while (true)
        {
            if (!NaturalType(place).IsValueType) return new StructSlot(place, place, null, false);
            var p = StripConv(place);
            switch (p.Kind)
            {
                case NodeKind.Paren:
                    place = p.A!;
                    continue;
                case NodeKind.Member:
                    if (p.A != null && p.Sym != null && p.Sym.Kind == SymKind.Field && !p.Sym.IsStatic && p.Info == RefKind.Field)
                    {
                        place = p.A;
                        continue;
                    }
                    if (p.Sym != null && p.Sym.Kind == SymKind.Field && p.Info == RefKind.Field) return new StructSlot(p, p, null, true);
                    return null;
                case NodeKind.Index:
                    if (p.Value != 1 && NaturalType(p.A!).Kind == TypeKind.Array) return new StructSlot(p, p.A!, p.List![0], false);
                    return null;
                case NodeKind.This:
                    return new StructSlot(p, p, null, true);
                case NodeKind.Name:
                    if (p.Info == RefKind.Local || (p.Info == RefKind.Field && p.Sym != null && p.Sym.Kind == SymKind.Field)) return new StructSlot(p, p, null, true);
                    return null;
            }
            return null;
        }
    }

    // struct の member への代入 (= / op= / ??=) を、右辺の後に経路を辿り直して書く必要があるか。右辺が格納先を書き換えないなら不要。
    // local / parameter / this の直下の member (`s.X`) は変数そのものが格納先なので不要
    private bool NeedsStructSlotAssign(Node assign)
    {
        var ma = StripConv(assign.A!);
        if (ma.Kind != NodeKind.Member || ma.A == null || ma.Sym == null || ma.Sym.IsStatic) return false;
        if (ma.Sym.Kind != SymKind.Field && ma.Sym.Kind != SymKind.Property) return false;
        if (!NaturalType(ma.A).IsValueType) return false;
        var path = StructSlotPath(ma.A);
        if (path == null) return false;
        var stop = path.Stop;
        bool direct = path.IsVariable && Unparen(ma.A) == stop && (stop.Kind == NodeKind.This || (stop.Kind == NodeKind.Name && stop.Info == RefKind.Local));
        return !direct && MayWriteStorage(assign.B!);
    }

    private static Node Unparen(Node e)
    {
        var x = StripConv(e);
        return x.Kind == NodeKind.Paren ? Unparen(x.A!) : x;
    }

    // 参照型の prefix と配列の添字を (読み直すと変わりうるなら) temp に固定し、`__tcs_v` に値を置いてから辿り直した place に書く文
    private List<IlStat>? BuildStructSlotAssign(Node assign, bool asValue)
    {
        var ma = StripConv(assign.A!);
        var path = StructSlotPath(ma.A!)!;
        var stats = new List<IlStat>();
        IlExpr? root = path.IsVariable ? BuildExpr(path.Base) : FixSlot(path.Base, "__tcs_obj", assign, stats);
        if (root != null && path.Index != null)
        {
            var idx = FixSlot(path.Index, "__tcs_idx", assign, stats);
            var bt = NaturalType(path.Base);
            root = idx != null ? new IlIndex(root, idx, true, IlTypes.HasNilableElements(bt), false, IlTypes.HasNilListElements(bt), null) : null;
        }
        var right = BuildExpr(assign.B!);
        var recv = root == null ? null : RebuildSlot(ma.A!, path.Stop, root);
        if (recv == null || right == null) return null;
        var member = ma.Sym!;
        string? owner = member.Kind == SymKind.Property && IsCustomProperty(member) ? TypeRef(member.Owner!) : null;
        IlExpr read = owner != null ? PropGet(recv, N(member), false, owner, new List<IlExpr>()) : Ex(new IlField(recv, N(member), null));
        IlExpr v = new IlVar("__tcs_v", null);
        // 式の値に使う `__tcs_v` を setter の value の書き換えから守る
        IlStat write;
        if (owner != null) write = new IlCallStat(PropSet(recv, N(member), false, asValue ? CopyIfMutableStruct(NaturalType(ma), v) : v, owner, new List<IlExpr>()), null);
        else write = new IlAssign(read, v, null);
        if (assign.Text == "??=")
        {
            stats.Add(new IlLocal("__tcs_v", read, null, null));
            var body = new List<IlStat>();
            body.Add(new IlAssign(v, WrapStructCopy(assign.B!, right), null));
            body.Add(write);
            stats.Add(new IlIf(Arm(IsNullIl(v, NaturalType(ma)), new IlBlock(body)), null, null));
            return stats;
        }
        IlExpr? value;
        if (assign.Text == "=") value = WrapStructCopy(assign.B!, right);
        else
        {
            var op = CompoundOperator(assign);
            value = op != null ? BuildCompoundValue(assign, op, read, new IlParen(right, null)) : null;
        }
        if (value == null) return null;
        stats.Add(new IlLocal("__tcs_v", value, null, null));
        stats.Add(write);
        return stats;
    }

    private IlExpr? FixSlot(Node e, string temp, Node assign, List<IlStat> stats)
    {
        var built = BuildExpr(e);
        if (built == null || (IsStableRead(e) && !WritesLocalReadBy(e, assign.B!))) return built;
        stats.Add(new IlLocal(temp, built, null, null));
        return new IlVar(temp, null);
    }

    private IlExpr? RebuildSlot(Node place, Node stop, IlExpr root)
    {
        if (place == stop) return root;
        var p = StripConv(place);
        if (p == stop) return root;
        if (p.Kind == NodeKind.Paren) return RebuildSlot(p.A!, stop, root);
        if (p.Kind == NodeKind.Member)
        {
            var inner = RebuildSlot(p.A!, stop, root);
            return inner == null ? null : new IlField(inner, N(p.Sym!), null);
        }
        return null;
    }

    private bool BuildStructSlotAssignInto(Node assign, IlPos? origin, List<IlStat> acc)
    {
        var stats = BuildStructSlotAssign(assign, false);
        if (stats == null) return false;
        acc.Add(new IlDo(new IlBlock(stats), origin));
        return true;
    }

    // 式の位置の代入 (`(i = y) >= 0`、`arr[x = 1]`): 文として代入し、代入した値を式の値にする IIFE。
    // 変更できる struct の値は格納先の table ではなくその copy。`T?` への ??= の値は中の値
    private IlExpr? BuildAssignExpr(Node assign)
    {
        IlIife? iife;
        if (NeedsStructSlotAssign(assign))
        {
            var stats = BuildStructSlotAssign(assign, true);
            if (stats == null) iife = null;
            else
            {
                stats.Add(new IlReturn(new IlVar("__tcs_v", null), null));
                iife = new IlIife(stats, null);
            }
        }
        else if (IsCustomPropertyTarget(assign.A!)) iife = BuildPropAssignExpr(assign);
        else if (NeedsLoweredAssign(assign) || NeedsFixedAssignTarget(assign)) iife = BuildLoweredAssign(assign);
        else
        {
            var stats = new List<IlStat>();
            if (!BuildExprStatInto(assign, null, stats)) return null;
            var assigned = BuildExpr(assign.A!);
            if (assigned == null) iife = null;
            else
            {
                stats.Add(new IlReturn(assigned, null));
                iife = new IlIife(stats, null);
            }
        }
        if (iife == null) return null;
        int n = iife.Stats.Count;
        if (n == 0 || !(iife.Stats[n - 1] is IlReturn last) || last.Value == null) return iife;
        var rest = new List<IlStat>();
        for (int i = 0; i < n - 1; i++) rest.Add(iife.Stats[i]);
        rest.Add(new IlReturn(CopyIfMutableStruct(assign.Type, CoalesceAssignValue(assign, last.Value)), null));
        return iife with { Stats = rest };
    }

    // 式の位置の代入 (参照型の受け手) の値を、代入の後に左辺を読み直して得ると受け手 / 添字を評価し直すか
    private bool NeedsFixedAssignTarget(Node assign)
    {
        if (NeedsLoweredLvalue(assign.A!) || CoalesceRightWritesTarget(assign)) return true;
        var x = StripConv(assign.A!);
        Node? recv = null;
        Node? index = null;
        if (x.Kind == NodeKind.Member) recv = x.A;
        else if (x.Kind == NodeKind.Index)
        {
            recv = x.A;
            index = x.List![0];
        }
        return recv != null && IsReferenceType(NaturalType(recv)) && StripConv(recv).Info != RefKind.Type
            && !(IsStableRead(recv) && (index == null || IsStableRead(index)));
    }

    // 右辺が書き換えない限り、読み直しても同じ値になる式 (local / parameter / this / 型名 / 定数とその演算)
    private static bool IsStableRead(Node e)
    {
        switch (e.Kind)
        {
            case NodeKind.IntLit:
            case NodeKind.FloatLit:
            case NodeKind.StringLit:
            case NodeKind.CharLit:
            case NodeKind.BoolLit:
            case NodeKind.NullLit:
            case NodeKind.DefaultLit:
            case NodeKind.This:
                return true;
            case NodeKind.Paren:
            case NodeKind.Conv:
            case NodeKind.Cast:
                return IsStableRead(e.A!);
            case NodeKind.Binary:
                if (IsUserOperator(e.Sym)) return false;
                return IsStableRead(e.A!) && IsStableRead(e.B!);
            case NodeKind.Unary:
                if (e.Text != "-" && e.Text != "!" && e.Text != "~") return false;
                return IsStableRead(e.A!);
            case NodeKind.Name:
            case NodeKind.Member:
                if (e.Info == RefKind.Local || e.Info == RefKind.Type || e.Info == RefKind.Namespace || e.Info == RefKind.EnumMember) return true;
                return e.Info == RefKind.Field && e.Sym != null && e.Sym.IsConst;
        }
        return false;
    }

    private static bool IsUserOperator(Symbol? s)
    {
        return s != null && s.Kind == SymKind.Method && s.Builtin == "" && s.Owner != null && !s.Owner.IsRef;
    }

    // 評価が既存の格納先 (field / 配列の要素 / 捕捉された local) を書き換えうるか
    private bool MayWriteStorage(Node n)
    {
        switch (n.Kind)
        {
            case NodeKind.Assign:
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                return true;
            case NodeKind.Binary:
                if (IsUserOperator(n.Sym)) return true;
                break;
            case NodeKind.Unary:
                if (IsUserOperator(n.Sym)) return true;
                break;
            case NodeKind.Call:
                if (n.Text == "with" || CallRunsUserCode(n)) return true;
                break;
            case NodeKind.New:
                if (NewRunsUserCode(n)) return true;
                break;
            case NodeKind.Name:
            case NodeKind.Member:
                if (n.Sym != null && n.Sym.Kind == SymKind.Property && IsCustomProperty(n.Sym)) return true;
                break;
        }
        return AnyChild(n, c => MayWriteStorage(c));
    }

    // 呼び出しが状態を書き換えうるか。書き換えないとみなすのは、delegate・out を受けない Math / MathF・数値 / bool / char / string の method
    private bool CallRunsUserCode(Node call)
    {
        var m = call.Sym;
        if (m == null || m.Kind != SymKind.Method) return true;
        if (m.Params != null)
        {
            foreach (var p in m.Params)
            {
                if (p.IsOut || p.Type.Kind == TypeKind.Delegate) return true;
            }
        }
        if (m.Builtin == "") return true;
        var b = m.Builtin;
        var callee = call.A;
        if (b.StartsWith("Math.")) return callee != null && callee.Kind == NodeKind.Member && IsFacadeReceiver(callee.A);
        if (b.StartsWith("String.") || b.StartsWith("Float.") || b.StartsWith("Int.") || b.StartsWith("Char.") || b.StartsWith("Bool.")) return false;
        return true;
    }

    // 構築が状態を書き換えうるか。source に無い構築子と struct の暗黙の構築子 (field を zero にするだけ) は書き換えない
    private static bool NewRunsUserCode(Node creation)
    {
        var t = creation.Type;
        if (t == null || t.Decl == null) return false;
        var ctor = creation.Sym;
        if (t.Decl.IsStruct && (ctor == null || ctor.Decl == null)) return false;
        return true;
    }

    // 型引数の型の field / 配列の要素への代入は、struct の記述子なら copy する (__tcs_tc)
    private IlExpr TypeArgStoreCopy(TypeSym? storage, IlExpr value)
    {
        if (storage != null && storage.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(storage);
            if (passed != null && !IsFreshStructIl(value)) return new IlCall("__tcs_tc", Two(passed, value), false, null);
        }
        return value;
    }

    // ---- for ----

    // 関数の本体の根 (for の範囲の判定に使う。lambda の中は lambda)
    public List<Node> ScopeRoots = new List<Node>();

    private bool BuildForInto(Node forStmt, List<IlStat> acc)
    {
        var origin = Origin(forStmt);
        var simple = TryBuildSimpleFor(forStmt);
        if (simple != null)
        {
            acc.Add(simple with { Origin = origin });
            return true;
        }
        Node? declaration = null;
        if (forStmt.List != null && forStmt.List.Count == 1 && forStmt.List[0].Kind == NodeKind.LocalDecl) declaration = forStmt.List[0];
        // 宣言したループ変数は for の中だけの変数 (C# の scope)。宣言のある for は do..end で囲う
        var loop = declaration != null ? new List<IlStat>() : acc;
        if (declaration == null && forStmt.List != null)
        {
            foreach (var init in forStmt.List)
            {
                var e = init.Kind == NodeKind.ExprStmt ? init.A! : init;
                if (!BuildExprStatInto(e, forStmt, loop)) return false;
            }
        }
        if (declaration != null && declaration.List != null)
        {
            foreach (var v in declaration.List)
            {
                IlExpr? init = null;
                if (v.A != null)
                {
                    init = BuildExpr(v.A);
                    if (init == null) return false;
                }
                // 宣言型を載せる (`for (float y = -1; ...)` は初期値からは int に見える)
                var name = v.Local != null ? v.Local.LuaName : v.Text;
                loop.Add(new IlLocal(name, init, v.Local != null ? IlTypes.Display(v.Local.Type) : null, origin));
            }
        }
        IlExpr cond = new IlLit("true", null, null);
        if (forStmt.A != null)
        {
            var built = BuildExpr(forStmt.A);
            if (built == null) return false;
            cond = built;
        }
        var body = BuildBlock(forStmt.B!);
        if (body == null) return false;
        var trailer = new List<IlStat>();
        if (forStmt.List2 != null)
        {
            foreach (var inc in forStmt.List2)
            {
                if (!BuildExprStatInto(inc, forStmt, trailer)) return false;
            }
        }
        bool scopeBody = forStmt.Count2 > 0 && ContainsDirectContinue(forStmt.B!);
        loop.Add(new IlWhile(cond, body, new IlBlock(trailer), scopeBody, origin));
        if (loop != acc) acc.Add(new IlDo(new IlBlock(loop), origin));
        return true;
    }

    private IlNumericFor? TryBuildSimpleFor(Node forStmt)
    {
        if (forStmt.List == null || forStmt.List.Count != 1 || forStmt.List[0].Kind != NodeKind.LocalDecl) return null;
        var declNode = forStmt.List[0];
        if (declNode.List == null || declNode.List.Count != 1) return null;
        var decl = declNode.List[0];
        if (decl.A == null) return null;
        var varName = decl.Text;
        var condNode = forStmt.A != null ? StripConv(forStmt.A) : null;
        if (condNode == null || condNode.Kind != NodeKind.Binary) return null;
        var condLeft = StripConv(condNode.A!);
        if (condLeft.Kind != NodeKind.Name || condLeft.Text != varName) return null;
        if (forStmt.Count2 != 1) return null;
        var inc = forStmt.List2![0];
        bool isIncByOne = (inc.Kind == NodeKind.PostIncDec && inc.Text == "++")
            || (inc.Kind == NodeKind.Assign && inc.Text == "+=" && StripConv(inc.B!).Kind == NodeKind.IntLit && SourceText(StripConv(inc.B!)) == "1");
        if (!isIncByOne) return null;
        // 増分の先が loop の変数でなければ (`for (int i = 0; i < n; j++)`) 一般の for
        var incTarget = StripConv(inc.A!);
        if (incTarget.Kind != NodeKind.Name || incTarget.Text != varName || incTarget.Local != decl.Local) return null;
        if (!IsLoopInvariantBound(condNode.B!)) return null;
        if (IsAssignedWithin(forStmt.B!, varName)) return null;
        if (IsCapturedByLambdaWithin(forStmt.B!, varName)) return null;
        var start = BuildExpr(decl.A);
        var end = BuildExpr(condNode.B!);
        var body = BuildBlock(forStmt.B!);
        if (start == null || end == null || body == null) return null;
        IlExpr? limit = null;
        if (condNode.Text == "<") limit = new IlBin(IlBinOp.Sub, end, new IlLit("1", null, null), null);
        else if (condNode.Text == "<=") limit = end;
        if (limit == null) return null;
        var name = decl.Local != null ? decl.Local.LuaName : varName;
        return new IlNumericFor(name, start, limit, body, null);
    }

    private static string SourceText(Node n)
    {
        return n.File.Slice(n.Pos, n.End);
    }

    private bool IsLoopInvariantBound(Node bound)
    {
        var b = StripConv(bound);
        switch (b.Kind)
        {
            case NodeKind.IntLit:
            case NodeKind.FloatLit:
            case NodeKind.StringLit:
            case NodeKind.CharLit:
            case NodeKind.BoolLit:
            case NodeKind.NullLit:
                return true;
        }
        if (b.Kind != NodeKind.Name || b.Info != RefKind.Local) return false;
        var scope = ScopeRoots.Count > 0 ? ScopeRoots[ScopeRoots.Count - 1] : bound;
        return !IsAssignedWithin(scope, b.Text);
    }

    // scope の中で name (の綴りの識別子) に代入するか (代入、++ / --、ref / out の引数)
    private static bool IsAssignedWithin(Node scope, string name)
    {
        switch (scope.Kind)
        {
            case NodeKind.Assign:
            {
                var l = scope.A!;
                if (l.Kind == NodeKind.Name && l.Text == name) return true;
                break;
            }
            case NodeKind.PostIncDec:
            case NodeKind.PreIncDec:
            {
                var o = scope.A!;
                if (o.Kind == NodeKind.Name && o.Text == name) return true;
                break;
            }
            case NodeKind.Arg:
                if ((scope.Has(Mods.Ref) || scope.Has(Mods.Out) || scope.Has(Mods.In)) && scope.A != null && scope.A.Kind == NodeKind.Name && scope.A.Text == name) return true;
                break;
        }
        return AnyChild(scope, c => IsAssignedWithin(c, name));
    }

    // scope の中の lambda が name (の綴りの識別子) を含むか
    private static bool IsCapturedByLambdaWithin(Node scope, string name)
    {
        if (scope.Kind == NodeKind.Lambda) return ContainsIdentifier(scope, name);
        return AnyChild(scope, c => IsCapturedByLambdaWithin(c, name));
    }

    private static bool ContainsIdentifier(Node n, string name)
    {
        if ((n.Kind == NodeKind.Name || n.Kind == NodeKind.Member || n.Kind == NodeKind.TypeName) && n.Text == name) return true;
        return AnyChild(n, c => ContainsIdentifier(c, name));
    }

    // この loop 自身を対象とする continue の有無 (入れ子の loop の中の continue は降りない)
    private static bool ContainsDirectContinue(Node stmt)
    {
        if (stmt.Kind == NodeKind.Continue) return true;
        if (stmt.Kind == NodeKind.For || stmt.Kind == NodeKind.While || stmt.Kind == NodeKind.DoWhile || stmt.Kind == NodeKind.Foreach) return false;
        return AnyChild(stmt, c => ContainsDirectContinue(c));
    }

    private static bool AnyChild(Node n, System.Func<Node, bool> f)
    {
        if (n.A != null && f(n.A)) return true;
        if (n.B != null && f(n.B)) return true;
        if (n.C != null && f(n.C)) return true;
        if (n.D != null && f(n.D)) return true;
        if (n.List != null)
        {
            foreach (var x in n.List)
            {
                if (f(x)) return true;
            }
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2)
            {
                if (f(x)) return true;
            }
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3)
            {
                if (f(x)) return true;
            }
        }
        return false;
    }

    // ---- 前宣言 ----

    // 文の前に宣言する局所変数: out の宣言形 (out var x / out _) と、入れ子の文や lambda に入らない is-pattern の designation。
    // if の elseif の条件の designation も含める
    private List<Node> CollectPreDecls(Node stmt)
    {
        var outs = new List<Node>();
        CollectOutDecls(stmt, stmt, outs);
        var pats = new List<Node>();
        CollectIsDesignations(stmt, stmt, pats);
        var chain = stmt;
        while (chain.Kind == NodeKind.If && chain.C != null && chain.C.Kind == NodeKind.If)
        {
            CollectIsDesignations(chain.C.A!, chain.C.A!, pats);
            chain = chain.C;
        }
        var result = new List<Node>();
        var names = new List<string>();
        foreach (var d in outs)
        {
            var n = d.Local != null ? d.Local.LuaName : "_";
            if (!names.Contains(n))
            {
                names.Add(n);
                result.Add(d);
            }
        }
        foreach (var d in pats)
        {
            var n = d.Local!.LuaName;
            if (!names.Contains(n))
            {
                names.Add(n);
                result.Add(d);
            }
        }
        return result;
    }

    private static bool IsStatementKind(NodeKind k)
    {
        switch (k)
        {
            case NodeKind.Block:
            case NodeKind.LocalDecl:
            case NodeKind.DeconstructDecl:
            case NodeKind.ExprStmt:
            case NodeKind.If:
            case NodeKind.While:
            case NodeKind.DoWhile:
            case NodeKind.For:
            case NodeKind.Foreach:
            case NodeKind.Break:
            case NodeKind.Continue:
            case NodeKind.Return:
            case NodeKind.Switch:
            case NodeKind.Empty:
                return true;
        }
        return false;
    }

    // out の宣言形の引数 (この文の直下。入れ子の文と lambda には入らない)
    // 参照実装は out の引数の最も近い文で決めるので、式本体の lambda の中の out var もこの文の前に宣言する
    private void CollectOutDecls(Node n, Node stmt, List<Node> into)
    {
        if (n != stmt && IsStatementKind(n.Kind)) return;
        if (n.Kind == NodeKind.Arg && n.Has(Mods.Out) && n.A != null)
        {
            if (n.A.Kind == NodeKind.PatDecl) into.Add(n.A);
            else if (n.A.Kind == NodeKind.PatDiscard || (n.A.Kind == NodeKind.Name && n.A.Text == "_" && n.A.Info != RefKind.Local)) into.Add(n.A);
        }
        VisitChildren(n, c => CollectOutDecls(c, stmt, into));
    }

    // is-pattern の designation (pattern の根の宣言だけ。入れ子の文と lambda には入らない)
    // scope 自身は数えない (参照実装の DescendantNodes。elseif の条件そのものが is のときはその designation を前宣言しない)
    private void CollectIsDesignations(Node n, Node scope, List<Node> into)
    {
        if (n != scope && (IsStatementKind(n.Kind) || n.Kind == NodeKind.Lambda)) return;
        if (n != scope && n.Kind == NodeKind.Is && n.B != null && n.B.Kind == NodeKind.PatDecl && n.B.Local != null) into.Add(n.B);
        VisitChildren(n, c => CollectIsDesignations(c, scope, into));
    }

    private static void VisitChildren(Node n, System.Action<Node> f)
    {
        if (n.A != null) f(n.A);
        if (n.B != null) f(n.B);
        if (n.C != null) f(n.C);
        if (n.D != null) f(n.D);
        if (n.List != null)
        {
            foreach (var x in n.List) f(x);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) f(x);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) f(x);
        }
    }

    // ---- foreach ----

    private bool BuildForeachInto(Node f, List<IlStat> acc)
    {
        var origin = Origin(f);
        if (f.Count > 0) return BuildForeachDeconstructInto(f, acc);
        var varName = f.Local != null ? f.Local.LuaName : f.Text;
        var collNode = f.B!;
        var runesRecv = TryGetEnumerateRunesReceiver(collNode);
        if (runesRecv != null)
        {
            var str = BuildExpr(runesRecv);
            var runesBody = BuildBlock(f.C!);
            if (str == null || runesBody == null) return false;
            acc.Add(new IlForeachRunes(varName, str, runesBody, origin));
            return true;
        }
        var coll = BuildExpr(collNode);
        var body = BuildBlock(f.C!);
        if (coll == null || body == null) return false;
        var collType = NaturalType(collNode);
        // Dictionary の本体だけ IlForeachDict (KeyValuePair) で反復する。Keys / Values は runtime が配列を返すので List の反復
        if (IlTypes.IsDict(collType))
        {
            // float の key は NaN の置き換えを戻す
            if (IlTypes.IsFloating(collType.Arg(0)))
            {
                var key = new IlField(new IlVar(varName, null), "Key", null);
                var withKey = new List<IlStat>();
                withKey.Add(new IlAssign(key, new IlCall("Dict.FloatKeyOut", One(key), false, null), null));
                foreach (var st in body.Stats) withKey.Add(st);
                body = new IlBlock(withKey);
            }
            acc.Add(new IlForeachDict(varName, coll, body, origin));
            return true;
        }
        // Queue / HashSet は今の要素の写しを反復する
        if (IlTypes.IsQueue(collType)) coll = new IlCall("TcsQueue.ToList", One(coll), false, null);
        if (IlTypes.IsHashSet(collType)) coll = new IlCall("TcsHashSet.ToList", One(coll), false, null);
        // 反復の変数は要素の copy (struct の要素への変更を元へ残さない)
        var iterVar = new IlVar(varName, null);
        var copied = CopyIfMutableStruct(f.Local != null ? f.Local.Type : null, iterVar);
        if (copied is IlStructCopy)
        {
            var stats = new List<IlStat>();
            stats.Add(new IlAssign(iterVar, copied, null));
            foreach (var st in body.Stats) stats.Add(st);
            body = new IlBlock(stats);
        }
        acc.Add(new IlForeachList(varName, coll, body, !IlTypes.IsArray(collType) && !IlTypes.IsDictCollection(collType), origin));
        return true;
    }

    // `s.EnumerateRunes()` なら受け手の s
    private static Node? TryGetEnumerateRunesReceiver(Node expr)
    {
        var x = StripConv(expr);
        if (x.Kind != NodeKind.Call || x.A == null || x.A.Kind != NodeKind.Member || x.A.Text != "EnumerateRunes") return null;
        if (x.A.A == null || NaturalType(x.A.A).Kind != TypeKind.String) return null;
        return x.A.A;
    }

    // ---- パターン ----

    private int _patternTempDepth;

    // 入力の値を 1 回だけ計算して照合する。読み直すと値や副作用が変わりうる入力は temp に評価してから照合する。
    // field の読みは、照合の途中の getter や演算子が field を書き換えうるので、照合が呼び出しを含むなら temp に置く
    private IlExpr? BindPatternInput(IlExpr input, System.Func<IlExpr, IlExpr?> build)
    {
        if (input is IlLit || input is IlVar) return build(input);
        if (IlTree.IsCallFree(input))
        {
            var direct = build(input);
            if (direct == null || IlTree.IsCallFree(direct)) return direct;
        }
        var name = "__tcs_pat" + _patternTempDepth;
        _patternTempDepth++;
        var applied = build(new IlVar(name, null));
        _patternTempDepth--;
        if (applied == null) return null;
        var stats = new List<IlStat>();
        stats.Add(new IlLocal(name, input, null, null));
        stats.Add(new IlReturn(applied, null));
        return new IlIife(stats, null);
    }

    // 型の参照: source に宣言した型は IL の型の名前、それ以外は書いた名前。名前だけで表せない型は null
    private string? FormatTypeReference(Node typeNode, TypeSym? t)
    {
        if (t != null && t.Decl != null && !t.Decl.IsRef) return TypeName(t);
        if (typeNode.Kind == NodeKind.TypeName && typeNode.Count == 0) return typeNode.Text;
        if (typeNode.Kind == NodeKind.Name || typeNode.Kind == NodeKind.Member) return typeNode.Text;
        return null;
    }

    private static string? LuaTypeNameFor(TypeSym? patternType)
    {
        if (patternType == null) return null;
        var t = IlTypes.UnwrapNullable(patternType);
        if (t.Kind == TypeKind.String) return "string";
        if (t.Kind == TypeKind.Bool) return "boolean";
        if (t.Kind == TypeKind.Enum || IlTypes.IsIntegral(t) || IlTypes.IsFloating(t)) return "number";
        return null;
    }

    private static bool IsStructuralType(TypeSym? t)
    {
        return IlTypes.IsArray(t) || IlTypes.IsList(t) || IlTypes.IsQueue(t) || IlTypes.IsHashSet(t) || IlTypes.IsDict(t);
    }

    // 型の照合。struct の入力 (S / S?) を同じ struct の型で照合するのは値の有無だけ
    private IlExpr? BuildTypeCheck(IlExpr expr, TypeSym? inputType, TypeSym? patternType, string? typeRef)
    {
        var luaType = LuaTypeNameFor(patternType);
        if (luaType != null) return new IlIsLuaType(expr, luaType, null);
        if (IsStructuralType(patternType))
        {
            if (inputType != null && TypeSym.Same(inputType, patternType!)) return new IlBin(IlBinOp.Ne, expr, new IlLit("nil", null, null), null);
            return null;
        }
        if (patternType != null && patternType.IsValueType && inputType != null)
        {
            if (TypeSym.Same(inputType, patternType)) return BindPatternInput(expr, x => new IlLit("true", null, null));
            if (IlTypes.IsNullableValueType(inputType) && TypeSym.Same(inputType.Elem, patternType)) return new IlNullableHasValue(expr, null);
        }
        if (patternType != null && patternType.Kind == TypeKind.Interface) return new IlIsType(expr, CheckTypeName(patternType), true, null);
        // --ref の class (tcs check と C は TCS1001 で止め、tcs VM は型の表で判定する、object-model §10.6) は cast と同じ IL の型名で指す
        if (patternType != null && patternType.Kind == TypeKind.Class && patternType.Decl != null && patternType.Decl.IsRef) return new IlIsType(expr, CheckTypeName(patternType), false, null);
        if (typeRef == null) return null;
        return new IlIsType(expr, typeRef, false, null);
    }

    private static IlBinOp RelationalIlOp(string op)
    {
        switch (op)
        {
            case ">": return IlBinOp.Gt;
            case ">=": return IlBinOp.Ge;
            case "<": return IlBinOp.Lt;
            case "<=": return IlBinOp.Le;
        }
        return IlBinOp.Eq;
    }

    private static IlExpr BinaryPatternIl(Node bp, IlExpr l, IlExpr r)
    {
        return new IlParen(new IlBin(bp.Kind == NodeKind.PatAnd ? IlBinOp.And : IlBinOp.Or, l, r, null), null);
    }

    // `and` の右辺の入力は左辺が絞った型。`S?` の入力を左辺が struct の S に絞ったら右辺は中の値を照合する
    private static IlExpr AndRightInput(Node bp, IlExpr input, TypeSym? inputType)
    {
        if (bp.Kind == NodeKind.PatAnd && IlTypes.IsNullableValueType(inputType) && IlTypes.IsUserStruct(PatternNarrowedType(bp.A!, inputType)))
            return new IlNullableValue(input, null);
        return input;
    }

    private static TypeSym? NarrowedInput(Node bp, TypeSym? input)
    {
        if (bp.Kind == NodeKind.PatAnd) return PatternNarrowedType(bp.A!, input);
        return input;
    }

    // pattern が一致したときに入力が絞られる型 (Roslyn の NarrowedType): 型 / 宣言の pattern はその型、
    // property pattern は書いた型か、値型の T? の入力なら中の型 (null に一致しない)
    private static TypeSym? PatternNarrowedType(Node p, TypeSym? input)
    {
        switch (p.Kind)
        {
            case NodeKind.PatType:
            case NodeKind.PatDecl:
                return p.Type;
            case NodeKind.PatProp:
                if (p.A != null) return p.Type;
                return input != null && IlTypes.IsNullableValueType(input) ? input.Elem : input;
        }
        return input;
    }

    // 定数のパターンの中の型の参照 (`case Foo:` の Foo が型)
    private static bool IsTypeConstant(Node p)
    {
        return p.Kind == NodeKind.PatType;
    }

    // switch の arm / case のパターン
    private IlExpr? BuildPattern(Node pattern, IlExpr governing, TypeSym? inputType)
    {
        switch (pattern.Kind)
        {
            case NodeKind.PatConst:
            {
                if (IsNullLiteral(pattern.A!) && IlTypes.IsNullableValueType(inputType))
                    return new IlUn(IlUnOp.Not, new IlNullableHasValue(governing, null), null);
                var value = BuildExpr(pattern.A!);
                return value == null ? null : new IlBin(IlBinOp.Eq, governing, value, null);
            }
            case NodeKind.PatDiscard:
            case NodeKind.PatVar:
                return new IlLit("true", null, null);
            case NodeKind.PatType:
                return BuildTypeCheck(governing, inputType, pattern.Type, FormatTypeReference(pattern.A!, pattern.Type));
            case NodeKind.PatDecl:
            {
                if (IsStructDesignation(pattern, inputType))
                    return BuildStructDeclarationIs(new IlVar(pattern.Local!.LuaName, null), governing, inputType!, pattern.Type!);
                return BuildTypeCheck(governing, inputType, pattern.Type, FormatTypeReference(pattern.A!, pattern.Type));
            }
            case NodeKind.PatProp:
                return BuildRecursivePattern(governing, pattern, inputType);
            case NodeKind.PatRel:
            {
                var v = BuildExpr(pattern.A!);
                return v == null ? null : new IlBin(RelationalIlOp(pattern.Text), governing, v, null);
            }
            case NodeKind.PatAnd:
            case NodeKind.PatOr:
                return BindPatternInput(governing, input =>
                {
                    var l = BuildPattern(pattern.A!, input, inputType);
                    if (l == null) return null;
                    var r = BuildPattern(pattern.B!, AndRightInput(pattern, input, inputType), NarrowedInput(pattern, inputType));
                    return r == null ? null : BinaryPatternIl(pattern, l, r);
                });
            case NodeKind.PatNot:
            {
                var sub = BuildIsSubPattern(governing, pattern.A!, inputType);
                return sub == null ? null : new IlUn(IlUnOp.Not, new IlParen(sub, null), null);
            }
        }
        return Unported("pattern", pattern);
    }

    // is 式の sub-pattern
    private IlExpr? BuildIsSubPattern(IlExpr expr, Node pattern, TypeSym? inputType)
    {
        switch (pattern.Kind)
        {
            case NodeKind.PatConst:
            {
                var value = BuildExpr(pattern.A!);
                return value == null ? null : new IlBin(IlBinOp.Eq, expr, value, null);
            }
            case NodeKind.PatType:
            case NodeKind.PatDecl:
                return BuildTypeCheck(expr, inputType, pattern.Type, FormatTypeReference(pattern.A!, pattern.Type));
            case NodeKind.PatNot:
            {
                var sub = BuildIsSubPattern(expr, pattern.A!, inputType);
                return sub == null ? null : new IlUn(IlUnOp.Not, new IlParen(sub, null), null);
            }
            case NodeKind.PatRel:
            {
                var v = BuildExpr(pattern.A!);
                return v == null ? null : new IlBin(RelationalIlOp(pattern.Text), expr, v, null);
            }
            case NodeKind.PatAnd:
            case NodeKind.PatOr:
                return BindPatternInput(expr, input =>
                {
                    var l = BuildIsSubPattern(input, pattern.A!, inputType);
                    if (l == null) return null;
                    var r = BuildIsSubPattern(AndRightInput(pattern, input, inputType), pattern.B!, NarrowedInput(pattern, inputType));
                    return r == null ? null : BinaryPatternIl(pattern, l, r);
                });
            case NodeKind.PatProp:
                return BuildRecursivePattern(expr, pattern, inputType);
            case NodeKind.PatDiscard:
            case NodeKind.PatVar:
                return null;
        }
        return Unported("is sub-pattern", pattern);
    }

    private IlExpr? BuildRecursivePattern(IlExpr expr, Node rp, TypeSym? inputType)
    {
        if (rp.Text != "") return null;
        var subpatterns = new List<Node>();
        if (rp.List != null)
        {
            foreach (var sub in rp.List)
            {
                if (sub.Text != "") subpatterns.Add(sub);
            }
        }
        var patternType = rp.A == null ? null : rp.Type;
        bool nullableValue = IlTypes.IsNullableValueType(inputType) && (patternType == null || TypeSym.Same(inputType!.Elem, patternType));
        bool nullCheck = rp.A == null && inputType != null && !inputType.IsValueType;
        int inputReads = subpatterns.Count + (rp.A != null || nullableValue || nullCheck ? 1 : 0);
        if (inputReads > 1) return BindPatternInput(expr, input => BuildRecursiveCore(input, rp, inputType, patternType, nullableValue, nullCheck, subpatterns));
        return BuildRecursiveCore(expr, rp, inputType, patternType, nullableValue, nullCheck, subpatterns);
    }

    private IlExpr? BuildRecursiveCore(IlExpr input, Node rp, TypeSym? inputType, TypeSym? patternType, bool nullableValue, bool nullCheck, List<Node> subpatterns)
    {
        var conditions = new List<IlExpr>();
        if (nullableValue) conditions.Add(new IlNullableHasValue(input, null));
        else if (rp.A != null)
        {
            var check = BuildTypeCheck(input, inputType, patternType, FormatTypeReference(rp.A, patternType));
            if (check == null) return null;
            conditions.Add(check);
        }
        else if (nullCheck) conditions.Add(new IlBin(IlBinOp.Ne, input, new IlLit("nil", null, null), null));
        // 型の検査を通った後なので、member は検査した型として読む
        IlExpr recv = input;
        if (nullableValue) recv = new IlNullableValue(input, null);
        else if (patternType != null && patternType.IsReferenceLike && subpatterns.Count > 0 && (inputType == null || !TypeSym.Same(inputType, patternType)))
            recv = new IlRefCast(input, IlTypes.Display(patternType), null, null, null);
        foreach (var sub in subpatterns)
        {
            var sym = sub.Sym;
            var propName = sym != null ? N(sym) : sub.Text;
            IlExpr propExpr = sym != null && sym.Kind == SymKind.Property && IsCustomProperty(sym)
                ? new IlInvoke(recv, "get_" + propName, new List<IlExpr>(), null)
                : new IlField(recv, propName, null);
            var cond = BuildIsSubPattern(propExpr, sub.A!, sub.Type);
            if (cond == null) return null;
            conditions.Add(cond);
        }
        if (conditions.Count == 0) return new IlLit("true", null, null);
        var result = conditions[0];
        for (int i = 1; i < conditions.Count; i++) result = new IlBin(IlBinOp.And, result, conditions[i], null);
        return result;
    }

    // 入力 S / S? に当てた struct S の宣言のパターン
    private static bool IsStructDesignation(Node dp, TypeSym? inputType)
    {
        return dp.Type != null && IlTypes.IsUserStruct(dp.Type) && inputType != null && TypeSym.Same(IlTypes.UnwrapNullable(inputType), dp.Type);
    }

    private IlExpr DesignationValue(IlExpr input, TypeSym inputType, TypeSym declType)
    {
        IlExpr value = IlTypes.IsNullableValueType(inputType) ? Ex(new IlNullableValue(input, null)) : input;
        if (!IlTypes.IsMutableStruct(declType)) return value;
        return new IlStructCopy(value, TypeName(declType), false, StructCopiers(declType, false), null);
    }

    // `x is S v` (x: S / S?): 値があれば copy を束縛して true
    private IlExpr BuildStructDeclarationIs(IlVar target, IlExpr expr, TypeSym inputType, TypeSym declType)
    {
        if (!IlTypes.IsNullableValueType(inputType))
        {
            var stats = new List<IlStat>();
            stats.Add(new IlAssign(target, DesignationValue(expr, inputType, declType), null));
            stats.Add(new IlReturn(new IlLit("true", null, null), null));
            return new IlIife(stats, null);
        }
        return BindPatternInput(expr, input =>
        {
            var inner = new List<IlStat>();
            inner.Add(new IlAssign(target, DesignationValue(input, inputType, declType), null));
            inner.Add(new IlReturn(new IlLit("true", null, null), null));
            var stats = new List<IlStat>();
            stats.Add(new IlIf(Arm(new IlNullableHasValue(input, null), new IlBlock(inner)), null, null));
            stats.Add(new IlReturn(new IlLit("false", null, null), null));
            return new IlIife(stats, null);
        })!;
    }

    // switch の designation の前宣言 (class は入力をそのまま束縛し、struct は型だけ宣言する)
    private IlLocal SwitchDesignationLocal(Node dp, TypeSym? inputType, IlExpr sw)
    {
        var name = dp.Local!.LuaName;
        if (IsStructDesignation(dp, inputType)) return new IlLocal(name, null, IlTypes.Display(dp.Type!), null);
        return new IlLocal(name, sw, dp.Type != null ? IlTypes.Display(dp.Type) : null, null);
    }

    private IlExpr? BuildIsPattern(Node isNode)
    {
        var expr = BuildExpr(isNode.A!);
        if (expr == null) return null;
        var inputType = NaturalType(isNode.A!);
        var pattern = isNode.B!;
        if (pattern.Kind == NodeKind.PatDecl && pattern.Local != null)
        {
            var name = pattern.Local.LuaName;
            if (IsStructDesignation(pattern, inputType)) return BuildStructDeclarationIs(new IlVar(name, null), expr, inputType, pattern.Type!);
            var check = BuildTypeCheck(new IlVar(name, null), inputType, pattern.Type, FormatTypeReference(pattern.A!, pattern.Type));
            if (check == null) return null;
            var stats = new List<IlStat>();
            stats.Add(new IlAssign(new IlVar(name, null), expr, null));
            stats.Add(new IlReturn(check, null));
            return new IlIife(stats, null);
        }
        var sub = BuildIsSubPattern(expr, pattern, inputType);
        return sub == null ? null : new IlParen(sub, null);
    }

    // switch 式: IIFE、前束縛、if の連鎖。arm の値は copy 地点
    private IlExpr? BuildSwitchExpression(Node sx)
    {
        var governing = BuildExpr(sx.A!);
        if (governing == null) return null;
        var inputType = NaturalType(sx.A!);
        var sw = new IlVar("__tcs_sw", null);
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_sw", governing, null, null));
        if (sx.List != null)
        {
            foreach (var arm in sx.List)
            {
                if (arm.A != null && arm.A.Kind == NodeKind.PatDecl && arm.A.Local != null) stats.Add(SwitchDesignationLocal(arm.A, inputType, sw));
            }
        }
        var arms = new List<IlArm>();
        IlBlock? elseBlock = null;
        if (sx.List != null)
        {
            foreach (var arm in sx.List)
            {
                var value = BuildExpr(arm.C!);
                if (value == null) return null;
                value = WrapStructCopy(arm.C!, value);
                if (arm.A!.Kind == NodeKind.PatDiscard && arm.B == null)
                {
                    elseBlock = new IlBlock(One(new IlReturn(value, null)));
                    continue;
                }
                var pattern = BuildPattern(arm.A, sw, inputType);
                if (pattern == null) return null;
                if (arm.B != null)
                {
                    var when = BuildExpr(arm.B);
                    if (when == null) return null;
                    pattern = new IlBin(IlBinOp.And, pattern, when, null);
                }
                arms.Add(new IlArm(pattern, new IlBlock(One(new IlReturn(value, null)))));
            }
        }
        if (arms.Count == 0 && elseBlock == null) return null;
        if (arms.Count > 0) stats.Add(new IlIf(arms, elseBlock, null));
        else stats.Add(new IlIf(Arm(new IlLit("true", null, null), elseBlock!), null, null));
        return new IlIife(stats, null);
    }

    // switch 文
    private bool BuildSwitchStatInto(Node switchStmt, List<IlStat> acc)
    {
        var origin = Origin(switchStmt);
        bool needsBreakScope = SwitchNeedsBreakScope(switchStmt);
        var governing = BuildExpr(switchStmt.A!);
        if (governing == null) return false;
        var inputType = NaturalType(switchStmt.A!);
        var sw = new IlVar("__tcs_sw", null);
        var products = new List<IlStat>();
        products.Add(new IlLocal("__tcs_sw", governing, null, origin));
        Node? defaultSection = null;
        var sections = switchStmt.List ?? new List<Node>();
        foreach (var section in sections)
        {
            if (section.List == null) continue;
            foreach (var label in section.List)
            {
                if (label.Kind == NodeKind.DefaultLabel && defaultSection == null) defaultSection = section;
            }
        }
        foreach (var section in sections)
        {
            if (section.List == null) continue;
            foreach (var label in section.List)
            {
                if (label.Kind == NodeKind.CaseLabel && label.A != null && label.A.Kind == NodeKind.PatDecl && label.A.Local != null)
                    products.Add(SwitchDesignationLocal(label.A, inputType, sw) with { Origin = origin });
            }
        }
        var arms = new List<IlArm>();
        IlBlock? defaultBody = null;
        foreach (var section in sections)
        {
            var caseLabels = new List<Node>();
            if (section.List != null)
            {
                foreach (var l in section.List)
                {
                    if (l.Kind != NodeKind.DefaultLabel) caseLabels.Add(l);
                }
            }
            if (caseLabels.Count == 0) continue;
            IlExpr? cond = null;
            foreach (var label in caseLabels)
            {
                IlExpr? one;
                var pat = label.A!;
                bool constantLabel = label.B == null && (pat.Kind == NodeKind.PatConst || (pat.Kind == NodeKind.PatType && pat.A != null && pat.A.Kind != NodeKind.PredefType && pat.A.Kind != NodeKind.ArrayType && pat.A.Kind != NodeKind.NullableType));
                if (constantLabel && pat.Kind == NodeKind.PatType) one = BuildTypeCheck(sw, inputType, pat.Type, FormatTypeReference(pat.A!, pat.Type));
                else if (constantLabel)
                {
                    if (IsNullLiteral(pat.A!) && IlTypes.IsNullableValueType(inputType)) one = new IlBin(IlBinOp.Eq, sw, new IlLit("nil", null, null), null);
                    else
                    {
                        var v = BuildExpr(pat.A!);
                        one = v == null ? null : new IlBin(IlBinOp.Eq, sw, v, null);
                    }
                }
                else one = BuildPatternLabel(label, sw, inputType);
                if (one == null) return false;
                cond = cond == null ? one : Ex(new IlBin(IlBinOp.Or, cond, one, null));
            }
            if (cond == null) return false;
            var body = BuildSwitchSectionBody(section.List2);
            if (body == null) return false;
            if (section == defaultSection) defaultBody = body;
            arms.Add(new IlArm(cond, body));
        }
        if (defaultSection != null && defaultBody == null)
        {
            defaultBody = BuildSwitchSectionBody(defaultSection.List2);
            if (defaultBody == null) return false;
        }
        if (arms.Count > 0) products.Add(new IlIf(arms, defaultBody, origin));
        else if (defaultBody != null) products.Add(new IlDo(defaultBody, origin));
        if (needsBreakScope) acc.Add(new IlBreakScope(new IlBlock(products), origin));
        else
        {
            foreach (var p in products) acc.Add(p);
        }
        return true;
    }

    private IlExpr? BuildPatternLabel(Node label, IlExpr sw, TypeSym? inputType)
    {
        var condition = BuildPattern(label.A!, sw, inputType);
        if (condition == null) return null;
        if (label.B == null) return new IlParen(condition, null);
        var when = BuildExpr(label.B);
        return when == null ? null : new IlParen(new IlBin(IlBinOp.And, condition, when, null), null);
    }

    private IlBlock? BuildSwitchSectionBody(List<Node>? statements)
    {
        var acc = new List<IlStat>();
        if (statements == null) return new IlBlock(acc);
        var terminal = new List<Node>();
        CollectTerminalBreaks(statements, terminal);
        return BuildSectionStatsInto(statements, terminal, acc) ? new IlBlock(acc) : null;
    }

    private bool BuildSectionStatsInto(List<Node> statements, List<Node> terminal, List<IlStat> acc)
    {
        foreach (var stmt in statements)
        {
            if (terminal.Contains(stmt)) continue;
            if (stmt.Kind == NodeKind.Block)
            {
                if (!BuildSectionStatsInto(stmt.List ?? new List<Node>(), terminal, acc)) return false;
                continue;
            }
            if (!BuildStatInto(stmt, acc)) return false;
        }
        return true;
    }

    private static void CollectTerminalBreaks(List<Node> statements, List<Node> sink)
    {
        if (statements.Count == 0) return;
        var last = statements[statements.Count - 1];
        if (last.Kind == NodeKind.Break) sink.Add(last);
        else if (last.Kind == NodeKind.Block && last.List != null) CollectTerminalBreaks(last.List, sink);
    }

    // 早期の break (暗黙の終端以外で、内側の loop / switch でなくこの switch に束縛される break) を含むか
    private static bool SwitchNeedsBreakScope(Node switchStmt)
    {
        var terminal = new List<Node>();
        if (switchStmt.List != null)
        {
            foreach (var section in switchStmt.List)
            {
                if (section.List2 != null) CollectTerminalBreaks(section.List2, terminal);
            }
        }
        return HasOwnBreak(switchStmt, switchStmt, terminal);
    }

    private static bool HasOwnBreak(Node n, Node sw, List<Node> terminal)
    {
        if (n != sw && (n.Kind == NodeKind.For || n.Kind == NodeKind.While || n.Kind == NodeKind.DoWhile || n.Kind == NodeKind.Foreach || n.Kind == NodeKind.Switch)) return false;
        if (n.Kind == NodeKind.Break && !terminal.Contains(n)) return true;
        return AnyChild(n, c => HasOwnBreak(c, sw, terminal));
    }

    // ---- lambda ----

    private IlExpr? BuildLambda(Node lambda)
    {
        var paramList = new List<string>();
        var copies = new List<IlStat>();
        if (lambda.List != null)
        {
            foreach (var p in lambda.List)
            {
                var name = p.Local != null ? p.Local.LuaName : p.Text;
                paramList.Add(name);
                var t = p.Local != null ? p.Local.Type : null;
                var c = CopyIfMutableStruct(t, new IlVar(name, null));
                if (c is IlStructCopy sc) copies.Add(new IlAssign(sc.E, sc, null));
            }
        }
        var dt = lambda.Type;
        bool returnsVoid = dt != null && dt.Kind == TypeKind.Delegate && dt.Ret != null && dt.Ret.Kind == TypeKind.Void;
        ScopeRoots.Add(lambda);
        var outerFn = Ctx.CurrentLambdaVoid;
        Ctx.CurrentLambdaVoid = returnsVoid ? 1 : 2;
        IlExpr? result = null;
        if (lambda.A != null)
        {
            var body = BuildExpr(lambda.A);
            if (body != null)
            {
                if (!returnsVoid) body = WrapStructCopy(lambda.A, body);
                var locals = new List<string>();
                var designations = new List<Node>();
                CollectIsDesignations(lambda.A, lambda.A, designations);
                foreach (var d in designations)
                {
                    if (!locals.Contains(d.Local!.LuaName)) locals.Add(d.Local.LuaName);
                }
                if (copies.Count == 0) result = new IlClosure(paramList, null, body, locals, null);
                else
                {
                    var stats = new List<IlStat>();
                    foreach (var c in copies) stats.Add(c);
                    if (returnsVoid) stats.Add(new IlCallStat(body, null));
                    else stats.Add(new IlReturn(body, null));
                    result = new IlClosure(paramList, new IlBlock(stats), null, locals, null);
                }
            }
        }
        else if (lambda.B != null)
        {
            var acc = new List<IlStat>();
            foreach (var c in copies) acc.Add(c);
            if (BuildStatsInto(lambda.B.List, acc)) result = new IlClosure(paramList, new IlBlock(acc), null, new List<string>(), null);
        }
        Ctx.CurrentLambdaVoid = outerFn;
        ScopeRoots.RemoveAt(ScopeRoots.Count - 1);
        return result;
    }

    // ---- 名前と member ----

    // const (enum の member を除く) は値を inline する
    public string? ConstLiteral(Symbol? sym)
    {
        if (sym == null || !sym.IsConst || sym.Kind == SymKind.EnumMember || sym.ConstVal == null) return null;
        return ConstText(sym.ConstVal, sym.Type);
    }

    public static string ConstText(ConstValue c, TypeSym t)
    {
        switch (c.Kind)
        {
            case ConstKind.Null: return "nil";
            case ConstKind.Bool: return c.B ? "true" : "false";
            case ConstKind.String: return IlLiterals.QuoteString(c.S);
            case ConstKind.Int: return "" + c.I;
            case ConstKind.Float: return IlLiterals.FormatDoubleOfFloat(c.F);
        }
        return "nil";
    }

    private IlExpr? BuildIdentifier(Node id)
    {
        var sym = id.Sym;
        var constLit = ConstLiteral(sym);
        if (constLit != null) return LitFromConst(constLit, sym != null && (sym.Type.Kind == TypeKind.Float || sym.Type.Kind == TypeKind.Double));
        switch (id.Info)
        {
            case RefKind.Local:
                return new IlVar(id.Local!.LuaName, null);
            case RefKind.MethodGroup:
            {
                var m = id.Sym;
                if (m != null && m.IsStatic && m.Owner != null) return MethodGroup(m, id.TypeArgs);
                return null;
            }
            case RefKind.Field:
            case RefKind.Property:
            {
                var s = sym!;
                if (s.Kind == SymKind.Property && IsCustomProperty(s))
                {
                    if (s.IsStatic) return new IlDynCall(new IlField(new IlVar(TypeRef(s.Owner!), null), "get_" + N(s), null), StaticAccessorArgs(s, null), null);
                    if (IlTypes.IsUserStruct(s.Owner!.SelfType())) return new IlCall(TypeRef(s.Owner) + ".get_" + N(s), One(new IlVar("self", null)), false, null);
                    return new IlInvoke(new IlVar("self", null), "get_" + N(s), new List<IlExpr>(), null);
                }
                if (!s.IsStatic) return new IlField(new IlVar("self", null), N(s), null);
                return new IlField(new IlVar(TypeRef(s.Owner!), null), N(s), null);
            }
            case RefKind.EnumMember:
                return EnumMemberIl(sym!);
            case RefKind.Type:
                if (id.Type != null && id.Type.Decl != null) return new IlVar(TypeRef(id.Type.Decl), null);
                return Unported("type reference", id);
        }
        return Unported("identifier " + id.Info, id);
    }

    // enum の member は型の table の field (--ref の enum は host の path と UPPER_SNAKE の名前)
    private IlExpr EnumMemberIl(Symbol sym)
    {
        // namespace にも型にも属さない --ref の enum は enum 自身の名前の path
        if (sym.Owner != null && sym.Owner.IsRef && sym.Owner.LuaName == "") return new IlField(new IlVar(LuaNames.HostPathSegment(sym.Owner.Name), null), N(sym), null);
        return new IlField(new IlVar(TypeRef(sym.Owner!), null), N(sym), null);
    }

    private IlExpr? BuildMemberAccess(Node ma)
    {
        if (ma.Value == 1 && ma != _condBinding) return Unported("conditional access", ma);
        var sym = ma.Sym;
        if (ma.Info == RefKind.Type)
        {
            if (ma.Type != null && ma.Type.Decl != null) return new IlVar(TypeRef(ma.Type.Decl), null);
            return Unported("type member", ma);
        }
        if (ma.Info == RefKind.Namespace) return new IlVar(ma.Text, null);
        if (sym == null) return Unported("member without symbol", ma);
        var constLit = ConstLiteral(sym);
        if (constLit != null) return LitFromConst(constLit, sym.Type.Kind == TypeKind.Float || sym.Type.Kind == TypeKind.Double);
        if (sym.Kind == SymKind.EnumMember) return EnumMemberIl(sym);
        if (sym.Builtin != "") return BuildBuiltinMember(ma, sym);
        if (ma.Info == RefKind.MethodGroup)
        {
            if (sym.IsStatic && sym.Owner != null) return MethodGroup(sym, ma.TypeArgs);
            return null;
        }
        if (sym.Kind == SymKind.Property)
        {
            var obj = sym.IsStatic ? Ex(new IlVar(TypeRef(sym.Owner!), null)) : BuildExpr(ma.A!);
            if (obj == null) return null;
            if (IsCustomProperty(sym))
            {
                if (sym.IsStatic) return new IlDynCall(new IlField(new IlVar(TypeRef(sym.Owner!), null), "get_" + N(sym), null), StaticAccessorArgs(sym, ma.A), null);
                if (IlTypes.IsUserStruct(ma.A!.Type)) return new IlCall(TypeRef(sym.Owner!) + ".get_" + N(sym), One(StructReceiverArg(ma.A, obj)), false, null);
                return new IlInvoke(obj, "get_" + N(sym), new List<IlExpr>(), null);
            }
            return new IlField(obj, N(sym), null);
        }
        if (sym.Kind == SymKind.Field)
        {
            var obj = sym.IsStatic ? Ex(new IlVar(TypeRef(sym.Owner!), null)) : BuildExpr(ma.A!);
            return obj == null ? null : new IlField(obj, N(sym), null);
        }
        return Unported("member", ma);
    }

    // 組み込みの member の値 (Count、Length、HasValue など)
    private IlExpr? BuildBuiltinMember(Node ma, Symbol sym)
    {
        var id = sym.Builtin;
        var recvType = ma.A != null ? ma.A.Type : null;
        if (IsFacadeReceiver(ma.A)) return new IlField(new IlVar(ma.A!.Type!.Name, null), ma.Text, null);
        switch (id)
        {
            // System.Math の const は double の値を inline する
            case "Math.PI": return new IlLit("3.141592653589793", "float", null);
            case "Math.E": return new IlLit("2.718281828459045", "float", null);
            case "String.Empty": return new IlField(new IlVar("String", null), "Empty", null);
            case "Random.Shared": return new IlField(new IlVar("TinySystem.Random", null), "Shared", null);
            // float の定数は double の値の表記
            case "Float.MaxValue": return new IlLit("3.4028234663852886E+38", "float", null);
            case "Float.MinValue": return new IlLit("-3.4028234663852886E+38", "float", null);
            case "Float.PositiveInfinity": return new IlLit("math.huge", "float", null);
            case "Float.NegativeInfinity": return new IlLit("(-math.huge)", "float", null);
            case "Float.NaN": return new IlLit("(0/0)", "float", null);
            case "Float.Epsilon": return new IlLit("1.401298464324817E-45", "float", null);
        }
        var obj = BuildExpr(ma.A!);
        if (obj == null) return null;
        switch (id)
        {
            case "Nullable.HasValue": return new IlNullableHasValue(obj, null);
            case "Nullable.Value": return new IlNullableValue(obj, null);
            case "Queue.Count": return new IlCall("TcsQueue.Count", One(obj), false, null);
            case "HashSet.Count": return new IlCall("TcsHashSet.Count", One(obj), false, null);
            case "Dict.Count": return new IlCall("Dict.Count", One(obj), false, null);
            case "List.Count": return new IlLen(obj, IlTypes.HasNilListElements(recvType), null);
            case "Dict.Keys": return DictKeys(recvType!, obj);
            case "Dict.Values": return new IlCall("Dict.Values", One(obj), false, null);
            case "Array.Length": return new IlLen(obj, false, null);
            case "Rune.Value": return obj;
            case "Utf8.Length": return new IlField(obj, "Length", null);
            case "KVP.Key": return new IlField(obj, "Key", null);
            case "KVP.Value": return new IlField(obj, "Value", null);
        }
        if (IlTypes.IsDictCollection(recvType) && sym.Name == "Count") return new IlLen(obj, false, null);
        return Unported("builtin member " + id, ma);
    }

    // Dictionary の Keys (float の key は NaN の置換を戻す)
    private IlExpr DictKeys(TypeSym dictType, IlExpr dict)
    {
        if (dictType.Args != null && IlTypes.IsFloating(dictType.Args[0])) return new IlCall("Dict.FloatKeys", One(dict), false, null);
        return new IlCall("Dict.Keys", One(dict), false, null);
    }

    // Dictionary の key (float は NaN を置き換える)
    // float の key は NaN を sentinel に置き換える。NaN でない定数の key は sentinel にならないので素通しする
    private IlExpr DictKey(TypeSym? dictType, Node? keyNode, IlExpr key)
    {
        if (dictType == null || dictType.Kind != TypeKind.Dict || !IlTypes.IsFloating(dictType.Args![0])) return key;
        // NaN だけが自分と等しくない (float.IsNaN は TinyC# に無い)
#pragma warning disable CS1718
        if (keyNode != null && keyNode.HasConst && keyNode.Const != null && keyNode.Const.Kind != ConstKind.Null
            && !(keyNode.Const.Kind == ConstKind.Float && keyNode.Const.F != keyNode.Const.F))
            return key;
#pragma warning restore CS1718
        return new IlCall("Dict.FloatKey", One(key), false, null);
    }

    // 自動実装の property (accessor のどれにも本体が無い)
    public static bool IsAutoProperty(Node prop)
    {
        if (prop.B == null && prop.C == null) return false;
        if (prop.B != null && (prop.B.A != null || prop.B.B != null)) return false;
        if (prop.C != null && (prop.C.A != null || prop.C.B != null)) return false;
        // 式本体の property (`int P => e;`) は B に式を持つ get
        return true;
    }

    // body / 式本体を持つ accessor があるか (自動実装の property と組み込みの property は false)
    public static bool IsCustomProperty(Symbol p)
    {
        if (p.Kind != SymKind.Property) return false;
        if (p.Owner != null && p.Owner.IsInterface) return true;
        var d = p.Decl;
        if (d == null || d.Kind != NodeKind.Property) return false;
        return !IsAutoProperty(d);
    }

    // generic な型の static な property の accessor に渡す型引数の既定値
    // generic な型の static な property の accessor に渡す型引数の既定値 (受け手の型の型引数。型の中の参照は自分の型引数)
    private List<IlExpr> StaticAccessorArgs(Symbol p, Node? recv)
    {
        var r = new List<IlExpr>();
        if (p.Owner == null || !IlTypes.PassesTypeArgDefaults(p.Owner)) return r;
        List<TypeSym>? args = null;
        if (recv != null && recv.Type != null && recv.Type.Decl == p.Owner && recv.Type.Args != null) args = recv.Type.Args;
        if (args == null) args = p.Owner.SelfType().Args;
        foreach (var a in args!) r.Add(DefaultIl(a));
        return r;
    }

    // ---- 名前 ----

    // TinySystem の static な facade の受け手か (`TinySystem.Math`、`using TinySystem;` だけの `Math`)
    private bool IsFacadeReceiver(Node? recv)
    {
        if (recv == null || recv.Type == null) return false;
        if (recv.Type.Kind != TypeKind.StaticRef) return false;
        var n = recv.Type.Name;
        return n == "TinySystem.Math" || n == "TinySystem.String" || n == "TinySystem.List" || n == "TinySystem.Dict";
    }

    // using の印: 1 = TinySystem、2 = System
    private static int CollectUsings(Node container)
    {
        int r = 0;
        if (container.List == null) return r;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Using)
            {
                if (n.Text == "TinySystem") r = r | 1;
                if (n.Text == "System") r = r | 2;
            }
            else if (n.Kind == NodeKind.Namespace) r = r | CollectUsings(n);
        }
        return r;
    }

    // symbol の IL の member 名。--ref の型の member は host の名前 (LuaName)、user の型の method は overload の名前
    public string N(Symbol sym)
    {
        if (sym.Owner != null && sym.Owner.IsRef) return sym.LuaName;
        if (sym.Kind == SymKind.Method) return IlNames.MethodName(sym);
        return sym.Name;
    }

    // static の参照の型の名前 (--ref の型は host の path)
    public string TypeRef(TypeDeclSym t)
    {
        return t.LuaName;
    }

    // 型の検査 (IlIsType) と cast (IlCast) の型名。--ref の型は host の名前 (`ed.transform`) ではなく C# の global の名前 (`Ed_Transform`。export の class の名前と同じ)
    private string CheckTypeName(TypeSym t)
    {
        if (t.Decl != null && t.Decl.IsRef) return t.Decl.FullName.Replace(".", "_");
        return TypeName(t);
    }

    public string TypeName(TypeSym t)
    {
        if (t.Kind == TypeKind.Tuple) return "System_ValueTuple" + t.Args!.Count;
        if (t.Decl != null) return t.Decl.LuaName;
        return t.Name;
    }

    // ---- method group ----

    // struct の parameter は値渡し。runtime (LINQ / Sort など) は要素を copy せずに渡すので、受ける側で copy する (il-spec §10)
    private IlExpr MethodGroup(Symbol method, List<TypeSym>? typeArgs)
    {
        var copyTypes = new List<string?>();
        if (method.Params != null)
        {
            foreach (var p in method.Params) copyTypes.Add(!p.IsOut && IlTypes.IsMutableStruct(p.Type) ? Copier(p.Type, false) : null);
        }
        var typeArgDefaults = new List<IlExpr>();
        if ((method.IsStatic) && IlTypes.PassesTypeArgDefaults(method.Owner))
        {
            foreach (var a in method.Owner!.SelfType().Args!) typeArgDefaults.Add(TypeArgDefaultValue(a));
        }
        if (method.TypeParams != null && typeArgs != null)
        {
            foreach (var a in typeArgs) typeArgDefaults.Add(TypeArgDefaultValue(a));
        }
        bool multiReturn = IsMultiReturnMethod(method);
        if (typeArgDefaults.Count == 0 && !multiReturn) return new IlMethodGroup(TypeRef(method.Owner!), N(method), copyTypes, null);
        var parameters = new List<string>();
        var args = new List<IlExpr>();
        int n = method.ParamCount;
        for (int i = 0; i < n; i++)
        {
            var pn = "__tcs_a" + i;
            parameters.Add(pn);
            args.Add(copyTypes[i] != null ? CopyIfMutableStruct(method.Params![i].Type, new IlVar(pn, null)) : Ex(new IlVar(pn, null)));
        }
        foreach (var t in typeArgDefaults) args.Add(t);
        IlExpr groupCall = new IlCall(TypeRef(method.Owner!) + "." + N(method), args, false, null);
        // delegate の呼び出し側は multi-return を知らないので 1 つの値にして返す
        return new IlClosure(parameters, null, multiReturn ? PackTuple(method, groupCall) : groupCall, new List<string>(), null);
    }

    // ---- 二項演算 ----

    private IlExpr? BuildBinary(Node bin)
    {
        var op = bin.Text;
        if (op == "is" || op == "as") return Unported("is / as", bin);
        // float の算術の operand の int → float の変換は省く (VM と C の演算が int を float に揃える)
        bool arithmetic = op == "+" || op == "-" || op == "*" || op == "/" || op == "%";
        bool builtinOp = bin.Sym == null || bin.Sym.Builtin != "";
        if (arithmetic && builtinOp)
        {
            if (bin.A!.Kind == NodeKind.Conv) _promotedOperand = bin.A;
        }
        var left = BuildExpr(bin.A!);
        if (arithmetic && builtinOp && bin.B!.Kind == NodeKind.Conv) _promotedOperand = bin.B;
        var right = BuildExpr(bin.B!);
        _promotedOperand = null;
        if (left == null || right == null) return null;
        if (WritesLocalReadBy(bin.A!, bin.B!)) return SnapshotOperand(left, l => BuildBinaryOperands(bin, l, right));
        return BuildBinaryOperands(bin, left, right);
    }

    // `local __tcs_l = first; return build(__tcs_l)` の IIFE
    private static IlExpr? SnapshotOperand(IlExpr first, System.Func<IlExpr, IlExpr?> build)
    {
        var applied = build(new IlVar("__tcs_l", null));
        if (applied == null) return null;
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_l", first, null, null));
        stats.Add(new IlReturn(applied, null));
        return new IlIife(stats, null);
    }

    // later の評価が earlier の読む local / parameter を書き換えうるか: 代入 / ++ / -- / ref・out の引数の直接の書き込み、
    // または lambda の中で書かれる (捕捉された) local を読んでいて later が呼び出しを含むとき
    private bool WritesLocalReadBy(Node earlier, Node later)
    {
        var read = new List<LocalSym>();
        CollectLocalReads(earlier, read);
        if (read.Count == 0) return false;
        if (WritesAny(later, read)) return true;
        if (!ContainsCallOrNew(later)) return false;
        var member = ScopeRoots.Count > 0 ? ScopeRoots[0] : null;
        if (member == null) return false;
        foreach (var l in read)
        {
            var one = new List<LocalSym>();
            one.Add(l);
            if (LambdaWrites(member, one)) return true;
        }
        return false;
    }

    private static void CollectLocalReads(Node n, List<LocalSym> into)
    {
        if (n.Kind == NodeKind.Name && n.Info == RefKind.Local && n.Local != null && !into.Contains(n.Local)) into.Add(n.Local);
        VisitChildren(n, c => CollectLocalReads(c, into));
    }

    private static bool ContainsCallOrNew(Node n)
    {
        if (n.Kind == NodeKind.Call || n.Kind == NodeKind.New) return true;
        return AnyChild(n, c => ContainsCallOrNew(c));
    }

    private static bool LambdaWrites(Node n, List<LocalSym> symbols)
    {
        if (n.Kind == NodeKind.Lambda && WritesAny(n, symbols)) return true;
        return AnyChild(n, c => LambdaWrites(c, symbols));
    }

    private static bool WritesAny(Node n, List<LocalSym> symbols)
    {
        switch (n.Kind)
        {
            case NodeKind.Assign:
                if (Targets(n.A!, symbols)) return true;
                break;
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                if (Targets(n.A!, symbols)) return true;
                break;
            case NodeKind.Arg:
                if ((n.Has(Mods.Ref) || n.Has(Mods.Out) || n.Has(Mods.In)) && n.A != null && Targets(n.A, symbols)) return true;
                break;
        }
        return AnyChild(n, c => WritesAny(c, symbols));
    }

    private static bool Targets(Node target, List<LocalSym> symbols)
    {
        var t = StripConv(target);
        if (t.Kind == NodeKind.Name && t.Local != null) return symbols.Contains(t.Local);
        if (t.Kind == NodeKind.Paren) return Targets(t.A!, symbols);
        if (t.Kind == NodeKind.Tuple && t.List != null)
        {
            foreach (var a in t.List)
            {
                var x = a.Kind == NodeKind.Arg ? a.A! : a;
                if (Targets(x, symbols)) return true;
            }
        }
        return false;
    }

    // == が参照の比較になり、しかも実行時の値が record でありうる組み合わせ (片方が record の静的型なら record の等価)。
    // null の literal との比較は値の等価でも参照の比較でも同じ結果なので含めない
    private static bool IsReferenceEquality(TypeSym lt, TypeSym rt)
    {
        if (lt.Kind == TypeKind.Null || rt.Kind == TypeKind.Null) return false;
        if (IsRecordClass(lt) || IsRecordClass(rt)) return false;
        return MayHoldRecord(lt) && MayHoldRecord(rt);
    }

    private static bool IsRecordClass(TypeSym t) => t.Kind == TypeKind.Class && t.Decl != null && t.Decl.IsRecord;

    private static bool MayHoldRecord(TypeSym t) =>
        t.Kind == TypeKind.Object || t.Kind == TypeKind.Interface || t.Kind == TypeKind.TypeParam;

    private IlExpr? BuildBinaryOperands(Node bin, IlExpr left, IlExpr right)
    {
        var op = bin.Text;
        // user の演算子は静的に選ばれた overload の直接の呼び出し
        if (bin.Sym != null && bin.Sym.Builtin == "")
        {
            var operands = new List<Node>();
            operands.Add(bin.A!);
            operands.Add(bin.B!);
            var built = new List<IlExpr>();
            built.Add(left);
            built.Add(right);
            var userOp = TryBuildUserOperatorCall(bin, operands, built);
            if (userOp != null) return userOp;
            // --ref の型の演算子は実装が host にあるので、呼び出しにせず IL の演算のまま残す
            if (!(bin.Sym.Owner != null && bin.Sym.Owner.IsRef)) return Unported("user operator", bin);
        }
        var lt = bin.A!.Type!;
        var rt = bin.B!.Type!;
        var resultType = bin.Type!;
        // record struct の ==/!= は合成の値の等価へ
        if ((op == "==" || op == "!=") && lt.Decl != null && lt.Decl.IsRecord && IlTypes.IsUserStruct(lt))
        {
            var eqCall = new IlCall(TypeName(lt) + ".op_Equality", Two(left, right), false, null);
            if (op == "==") return eqCall;
            return new IlParen(new IlUn(IlUnOp.Not, eqCall, null), null);
        }
        // object / interface / 型引数どうしの ==/!= は C# では参照の比較なので、record の値の等価にしない rawequal にする
        if ((op == "==" || op == "!=") && IsReferenceEquality(lt, rt))
        {
            var refEq = new IlCall("rawequal", Two(left, right), false, null);
            if (op == "==") return refEq;
            return new IlParen(new IlUn(IlUnOp.Not, refEq, null), null);
        }
        var nullableBin = TryBuildNullableBinary(bin, left, right);
        if (nullableBin != null) return nullableBin;
        if (op == "/" && IlTypes.IsIntegral(resultType)) return new IlCall("__tcs_idiv", Two(left, right), false, null);
        if (op == "%")
        {
            if (IlTypes.IsIntegral(resultType)) return new IlCall("__tcs_irem", Two(left, right), false, null);
            if (IlTypes.IsFloating(resultType)) return new IlCall("math.fmod", Two(left, right), false, null);
        }
        bool isConcat = op == "+" && (lt.Kind == TypeKind.String || rt.Kind == TypeKind.String || resultType.Kind == TypeKind.String);
        if (isConcat) return new IlBin(IlBinOp.Concat, WrapConcatOperand(bin.A, left), WrapConcatOperand(bin.B, right), null);
        if (_checked && resultType.Kind == TypeKind.Int && (op == "+" || op == "-" || op == "*"))
        {
            var callee = op == "+" ? "__tcs_cadd" : op == "-" ? "__tcs_csub" : "__tcs_cmul";
            return new IlCall(callee, Two(left, right), false, null);
        }
        bool boolOperand = lt.Kind == TypeKind.Bool || rt.Kind == TypeKind.Bool;
        switch (op)
        {
            case "+": return new IlBin(IlBinOp.AddNum, left, right, null);
            case "-": return new IlBin(IlBinOp.Sub, left, right, null);
            case "*": return new IlBin(IlBinOp.Mul, left, right, null);
            case "/": return new IlBin(IlBinOp.DivNum, left, right, null);
            case "%": return new IlBin(IlBinOp.RemNum, left, right, null);
            case "==": return new IlBin(IlBinOp.Eq, left, right, null);
            case "!=": return new IlBin(IlBinOp.Ne, left, right, null);
            case "<": return new IlBin(IlBinOp.Lt, left, right, null);
            case "<=": return new IlBin(IlBinOp.Le, left, right, null);
            case ">": return new IlBin(IlBinOp.Gt, left, right, null);
            case ">=": return new IlBin(IlBinOp.Ge, left, right, null);
            case "&&": return new IlBin(IlBinOp.And, left, right, null);
            case "||": return new IlBin(IlBinOp.Or, left, right, null);
            case "??": return new IlBin(IlBinOp.Or, left, right, null);
            case "&":
                if (!boolOperand) return new IlBin(IlBinOp.BitAnd, left, right, null);
                break;
            case "|":
                if (!boolOperand) return new IlBin(IlBinOp.BitOr, left, right, null);
                break;
            case "^":
                if (!boolOperand) return new IlBin(IlBinOp.BitXor, left, right, null);
                break;
            case "<<": return new IlBin(IlBinOp.Shl, left, right, null);
            case ">>": return new IlBin(IlBinOp.Shr, left, right, null);
        }
        return null;
    }

    // ---- 文字列化 ----

    // 変換の前の自然な型 (Roslyn の GetTypeInfo(expr).Type)。Binder の Conv の中の式の型
    public static TypeSym NaturalType(Node e)
    {
        while (e.Kind == NodeKind.Conv && e.A != null) e = e.A;
        return e.Type ?? TypeSym.ErrorType;
    }

    public static Node StripConv(Node e)
    {
        while (e.Kind == NodeKind.Conv && e.A != null) e = e.A;
        return e;
    }

    // 文字列化 (補間 / ToString)
    private IlExpr? WrapFloatToString(Node src, IlExpr built)
    {
        var t = NaturalType(src);
        var user = UserToString(src, built);
        if (user != null) return user;
        var en = WrapEnumToString(t, built);
        if (en != null) return en;
        if (IsStringifiedByValue(t)) return StringifyByValue(t, built);
        if (IlTypes.IsFloating(t)) return new IlCall("__tcs_fstr", One(built), false, null);
        if (IlTypes.IsChar(t)) return new IlCall("string.char", One(built), false, null);
        if (IlTypes.IsRune(t)) return RuneToString(built);
        if (t.Kind == TypeKind.Bool) return new IlCall("__tcs_bstr", One(built), false, null);
        return new IlCall("tostring", One(built), false, null);
    }

    // 文字列の連結 (`+` / `+=`) の operand を文字列にする: int 以外の値型と ToString の override はここで文字列化し、
    // null になりうる string は空文字列にする (int は連結 (IlBinOp.Concat) が 10 進にする)
    private IlExpr WrapConcatOperand(Node expr, IlExpr rendered)
    {
        var type = NaturalType(expr);
        var user = UserToString(expr, rendered);
        if (user != null) return user;
        var en = WrapEnumToString(type, rendered);
        if (en != null) return en;
        if (IsStringifiedByValue(type)) return StringifyByValue(type, rendered);
        if (IlTypes.IsFloating(type)) return new IlCall("__tcs_fstr", One(rendered), false, null);
        if (IlTypes.IsChar(type)) return new IlCall("string.char", One(rendered), false, null);
        if (IlTypes.IsRune(type)) return RuneToString(rendered);
        if (IlTypes.UnwrapNullable(type).Kind == TypeKind.Bool)
        {
            if (IlTypes.IsNullableValueType(type)) return new IlCall("__tcs_nstr", One(rendered), false, null);
            return new IlCall("__tcs_bstr", One(rendered), false, null);
        }
        if (type.Kind != TypeKind.String) return rendered;
        var unwrapped = expr;
        while (unwrapped.Kind == NodeKind.Paren || (unwrapped.Kind == NodeKind.Conv && unwrapped.Info == ConvKind.Identity)) unwrapped = unwrapped.A!;
        bool literalLike = unwrapped.Kind == NodeKind.StringLit || unwrapped.Kind == NodeKind.InterpString
            || (unwrapped.Kind == NodeKind.Binary && unwrapped.Text == "+") || (unwrapped.HasConst && unwrapped.Kind != NodeKind.Name && unwrapped.Kind != NodeKind.Member);
        if (literalLike) return rendered;
        return new IlParen(new IlBin(IlBinOp.Or, rendered, new IlLit("\"\"", null, null), null), null);
    }

    // ToString を override した型の値の文字列化: 直接呼ぶ (null と、ToString が返した null は空文字列)
    private IlExpr? UserToString(Node src, IlExpr built)
    {
        var t = NaturalType(src);
        if (t == null || t.Decl == null || t.Decl.IsRef) return null;
        Symbol? method = null;
        var cands = t.Decl.Lookup("ToString");
        if (cands != null)
        {
            foreach (var c in cands)
            {
                if (c.Kind == SymKind.Method && c.ParamCount == 0 && !c.IsStatic) method = c;
            }
        }
        if (method == null) return null;
        if (IlTypes.IsUserStruct(t))
            return EmptyIfNull(new IlCall(TypeRef(t.Decl) + "." + N(method), One(WrapStructCopy(src, built)), false, null));
        var value = new IlVar("__tcs_v", null);
        var stats = new List<IlStat>();
        stats.Add(new IlLocal("__tcs_v", built, null, null));
        stats.Add(new IlReturn(EmptyIfNull(new IlTernary(new IlBin(IlBinOp.Ne, value, new IlLit("nil", null, null), null),
            new IlInvoke(value, N(method), new List<IlExpr>(), null), new IlLit("\"\"", null, null), null)), null));
        return new IlIife(stats, null);
    }

    private static IlExpr EmptyIfNull(IlExpr text)
    {
        return new IlParen(new IlBin(IlBinOp.Or, text, new IlLit("\"\"", null, null), null), null);
    }

    // Rune の値は codepoint の整数そのもの
    private static IlExpr RuneToString(IlExpr rune)
    {
        return new IlCall("Char.ConvertFromUtf32", One(rune), false, null);
    }

    // 文字列化が値で決まる型 (T?、object、型引数)
    private static bool IsStringifiedByValue(TypeSym t)
    {
        return t.Kind == TypeKind.Nullable || t.Kind == TypeKind.Object || t.Kind == TypeKind.TypeParam;
    }

    private IlExpr StringifyByValue(TypeSym t, IlExpr value)
    {
        if (t.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(t);
            if (passed != null) return new IlCall("__tcs_tstr", Two(passed, value), false, null);
        }
        return new IlCall("__tcs_nstr", One(value), false, null);
    }

    // enum の値の文字列化 (ToString / 連結 / 補間 / Console.WriteLine)。.NET の Enum.ToString と同じく、定義された値は member の名前、
    // [Flags] (と書式 "F") は名前の ", " の連結、どちらでも表せない値は数。名前の表は文字列化する場所に置く (String.FormatEnum の第 2 引数)
    private IlExpr? WrapEnumToString(TypeSym t, IlExpr value)
    {
        return WrapEnumToStringFlags(t, value, false);
    }

    private static IlExpr? WrapEnumToStringFlags(TypeSym t, IlExpr value, bool flags)
    {
        var spec = EnumFormatSpec(IlTypes.UnwrapNullable(t), flags);
        if (spec == null) return null;
        return new IlCall("String.FormatEnum", Two(value, new IlLit(IlLiterals.QuoteString(spec), null, null)), false, null);
    }

    // enum の書式 (`e.ToString("G")` と補間の `{e:G}`)。G は名前、F は名前の組み合わせ、D は数。それ以外は null
    private static IlExpr? WrapEnumFormat(TypeSym t, IlExpr value, string format)
    {
        if (format == "G" || format == "g") return WrapEnumToStringFlags(t, value, false);
        if (format == "F" || format == "f") return WrapEnumToStringFlags(t, value, true);
        if (format == "D" || format == "d") return new IlCall("tostring", One(value), false, null);
        return null;
    }

    // spec は `名前:値` を `,` で並べた列。Flags (と書式 "F") は先頭に `*` を付け、全 member を .NET の EnumInfo と同じ並び
    // (値の符号なし昇順。宣言順で整列していればそのまま、でなければ整列) で持つ。それ以外は値ごとに .NET が選ぶ名前を 1 つだけ持つ
    // (同じ値の member は並びの上で 32 個以下なら最初、それより多ければ二分探索で当たったもの)
    public static string? EnumFormatSpec(TypeSym t, bool forceFlags)
    {
        if (t.Kind != TypeKind.Enum || t.Decl == null) return null;
        var d = t.Decl;
        var ub = d.EnumBase != null ? d.EnumBase.Kind : TypeKind.Int;
        int bits = 0;
        if (ub == TypeKind.Byte) bits = 8;
        else if (ub == TypeKind.Short) bits = 16;
        else if (ub == TypeKind.Int || ub == TypeKind.UInt) bits = 32;
        if (bits == 0) return null;
        var names = new List<string>();
        var values = new List<int>();
        foreach (var m in d.Members)
        {
            if (m.Kind != SymKind.EnumMember) continue;
            names.Add(m.Name);
            values.Add(m.ConstVal != null ? m.ConstVal.I : 0);
        }
        var order = new List<int>();
        for (int i = 0; i < values.Count; i++) order.Add(i);
        bool sorted = true;
        for (int i = 0; i + 1 < values.Count; i++)
        {
            if (EnumKeyLess(values[i + 1], values[i], bits)) sorted = false;
        }
        if (!sorted)
        {
            // .NET の Array.Sort(keys, items) と同じ並べ方 (introsort。16 個以下は挿入の整列で同じ値の並びを保つが、
            // それより多いと保たない)。key は値、item は宣言の順
            var keys = new List<int>();
            foreach (var v in values) keys.Add(v);
            EnumIntroSort(keys, order, 0, keys.Count, 2 * (Log2(keys.Count) + 1), bits);
        }
        var entries = new List<string>();
        if (forceFlags || d.IsFlags)
        {
            foreach (var i in order) entries.Add(names[i] + ":" + values[i]);
            return "*" + string.Join(",", entries);
        }
        int k = 0;
        while (k < order.Count)
        {
            int key = values[order[k]];
            int at;
            if (order.Count <= 32) at = k;
            else
            {
                // .NET の二分探索 (同じ値のどれに当たるかも同じ)
                int lo = 0;
                int hi = order.Count - 1;
                at = k;
                while (lo <= hi)
                {
                    int mid = lo + ((hi - lo) >> 1);
                    int v = values[order[mid]];
                    if (EnumKeyEqual(v, key, bits))
                    {
                        at = mid;
                        break;
                    }
                    if (EnumKeyLess(v, key, bits)) lo = mid + 1;
                    else hi = mid - 1;
                }
            }
            entries.Add(names[order[at]] + ":" + values[order[at]]);
            // 同じ値を飛ばす
            int next = k + 1;
            while (next < order.Count && EnumKeyEqual(values[order[next]], key, bits)) next++;
            k = next;
        }
        return string.Join(",", entries);
    }

    private static int Log2(int n)
    {
        int r = 0;
        while (n > 1)
        {
            n = n / 2;
            r++;
        }
        return r;
    }

    // keys[lo..lo+len) と items を .NET の ArraySortHelper<TKey, TValue>.IntroSort と同じ手順で並べる
    private static void EnumIntroSort(List<int> keys, List<int> items, int lo, int len, int depthLimit, int bits)
    {
        int size = len;
        while (size > 1)
        {
            if (size <= 16)
            {
                if (size == 2)
                {
                    EnumSwapIfGreater(keys, items, lo, lo + 1, bits);
                    return;
                }
                if (size == 3)
                {
                    EnumSwapIfGreater(keys, items, lo, lo + 1, bits);
                    EnumSwapIfGreater(keys, items, lo, lo + 2, bits);
                    EnumSwapIfGreater(keys, items, lo + 1, lo + 2, bits);
                    return;
                }
                EnumInsertionSort(keys, items, lo, size, bits);
                return;
            }
            if (depthLimit == 0)
            {
                EnumHeapSort(keys, items, lo, size, bits);
                return;
            }
            depthLimit--;
            int p = EnumPartition(keys, items, lo, size, bits);
            EnumIntroSort(keys, items, lo + p + 1, size - (p + 1), depthLimit, bits);
            size = p;
        }
    }

    private static int EnumPartition(List<int> keys, List<int> items, int lo, int len, int bits)
    {
        int hi = len - 1;
        int middle = hi >> 1;
        EnumSwapIfGreater(keys, items, lo, lo + middle, bits);
        EnumSwapIfGreater(keys, items, lo, lo + hi, bits);
        EnumSwapIfGreater(keys, items, lo + middle, lo + hi, bits);
        int pivot = keys[lo + middle];
        EnumSwap(keys, items, lo + middle, lo + hi - 1);
        int left = 0;
        int right = hi - 1;
        while (left < right)
        {
            left++;
            while (EnumKeyLess(keys[lo + left], pivot, bits)) left++;
            right--;
            while (EnumKeyLess(pivot, keys[lo + right], bits)) right--;
            if (left >= right) break;
            EnumSwap(keys, items, lo + left, lo + right);
        }
        if (left != hi - 1) EnumSwap(keys, items, lo + left, lo + hi - 1);
        return left;
    }

    private static void EnumInsertionSort(List<int> keys, List<int> items, int lo, int len, int bits)
    {
        for (int i = 0; i < len - 1; i++)
        {
            int t = keys[lo + i + 1];
            int tv = items[lo + i + 1];
            int j = i;
            while (j >= 0 && EnumKeyLess(t, keys[lo + j], bits))
            {
                keys[lo + j + 1] = keys[lo + j];
                items[lo + j + 1] = items[lo + j];
                j--;
            }
            keys[lo + j + 1] = t;
            items[lo + j + 1] = tv;
        }
    }

    private static void EnumHeapSort(List<int> keys, List<int> items, int lo, int n, int bits)
    {
        for (int i = n >> 1; i >= 1; i--) EnumDownHeap(keys, items, lo, i, n, bits);
        for (int i = n; i > 1; i--)
        {
            EnumSwap(keys, items, lo, lo + i - 1);
            EnumDownHeap(keys, items, lo, 1, i - 1, bits);
        }
    }

    private static void EnumDownHeap(List<int> keys, List<int> items, int lo, int i, int n, int bits)
    {
        int d = keys[lo + i - 1];
        int dv = items[lo + i - 1];
        while (i <= n >> 1)
        {
            int child = 2 * i;
            if (child < n && EnumKeyLess(keys[lo + child - 1], keys[lo + child], bits)) child++;
            if (!EnumKeyLess(d, keys[lo + child - 1], bits)) break;
            keys[lo + i - 1] = keys[lo + child - 1];
            items[lo + i - 1] = items[lo + child - 1];
            i = child;
        }
        keys[lo + i - 1] = d;
        items[lo + i - 1] = dv;
    }

    private static void EnumSwapIfGreater(List<int> keys, List<int> items, int i, int j, int bits)
    {
        if (i != j && EnumKeyLess(keys[j], keys[i], bits)) EnumSwap(keys, items, i, j);
    }

    private static void EnumSwap(List<int> keys, List<int> items, int i, int j)
    {
        int k = keys[i];
        keys[i] = keys[j];
        keys[j] = k;
        int v = items[i];
        items[i] = items[j];
        items[j] = v;
    }

    // 基底の型の幅で見た符号なしの比較
    private static bool EnumKeyLess(int a, int b, int bits)
    {
        if (bits < 32)
        {
            int mask = (1 << bits) - 1;
            return (a & mask) < (b & mask);
        }
        return (a ^ int.MinValue) < (b ^ int.MinValue);
    }

    private static bool EnumKeyEqual(int a, int b, int bits)
    {
        if (bits < 32)
        {
            int mask = (1 << bits) - 1;
            return (a & mask) == (b & mask);
        }
        return a == b;
    }

    // ---- 型引数の既定値 ----

    private static string TypeArgDefaultName(TypeSym p)
    {
        return (p.IsMethodParam ? "__tcs_dm_" : "__tcs_dc_") + p.Name;
    }

    // 型引数の既定値の IL (method の型引数は隠れ引数、型の型引数は self か隠れ引数)
    public IlExpr? TypeArgDefaultIl(TypeSym p)
    {
        var name = TypeArgDefaultName(p);
        if (p.IsMethodParam) return new IlVar(name, null);
        var decl = Ctx.CurrentDecl;
        if (decl == null || decl.TypeParams.Count == 0) return null;
        if (decl.DeclKind == DeclKinds.Interface || decl.DeclKind == DeclKinds.Enum) return null;
        return Ctx.TypeArgDefaultsOnSelf ? new IlField(new IlVar("self", null), name, null) : new IlVar(name, null);
    }

    // default 値の IL: source の struct は zero 値の IlNewObj、型引数は呼び出し側が渡した既定値、それ以外は literal
    public IlExpr DefaultIl(TypeSym? t)
    {
        if (t != null && t.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(t);
            if (passed != null) return new IlCall("__tcs_dv", One(passed), false, null);
        }
        if (IlTypes.IsUtf8Bytes(t)) return new IlCall("TinySystem.Utf8.Default", new List<IlExpr>(), false, null);
        if (IlTypes.IsUserStruct(t)) return new IlNewObj(TypeName(t!), StructTypeArgDefaults(t!), "new", null);
        return new IlLit(DefaultLiteral(t), null, null);
    }

    // literal で書ける default 値 (数、bool、enum。それ以外は null)。source の struct、Utf8Bytes、渡された型引数の既定値は DefaultIl が先に扱う
    public static string DefaultLiteral(TypeSym? type)
    {
        if (type == null) return "nil";
        // enum の default は member の値に依らず 0 (C#: default(E) == 0)
        if (type.Kind == TypeKind.Enum) return "0";
        switch (type.Kind)
        {
            case TypeKind.Bool: return "false";
            case TypeKind.Char: return "0";
            case TypeKind.Int:
            case TypeKind.Long:
            case TypeKind.UInt:
                return "0";
            // float の既定値は float の literal
            case TypeKind.Float:
            case TypeKind.Double:
                return "0.0";
        }
        return "nil";
    }

    // generic な struct の zero 値 (`S.new`) に渡す型引数の既定値
    public List<IlExpr> StructTypeArgDefaults(TypeSym t)
    {
        var r = new List<IlExpr>();
        if (t.Kind == TypeKind.Tuple)
        {
            foreach (var a in t.Args!) r.Add(TypeArgDefaultValue(a));
            return r;
        }
        if (t.Decl != null && t.Decl.TypeParams.Count > 0 && t.Args != null)
        {
            foreach (var a in t.Args) r.Add(TypeArgDefaultValue(a));
        }
        return r;
    }

    // 型引数の既定値として渡す値: 型引数は受け取ったまま、可変な struct は zero 値と copy の作り方 (記述子)、enum は名前の表の記述子
    public IlExpr TypeArgDefaultValue(TypeSym t)
    {
        if (t.Kind == TypeKind.TypeParam)
        {
            var passed = TypeArgDefaultIl(t);
            if (passed != null) return passed;
        }
        if (NeedsTypeArgDescriptor(t))
            return new IlStructDescriptor(TypeName(t), StructTypeArgDefaults(t), StructCopiers(t, false), HasToStringOverride(t), null);
        if (t.Kind == TypeKind.Enum)
        {
            var spec = EnumFormatSpec(t, false);
            if (spec != null) return new IlCall("__tcs_etd", One(new IlLit(IlLiterals.QuoteString(spec), null, null)), false, null);
        }
        return DefaultIl(t);
    }

    // 型引数に入れたとき、既定値の値では足りない struct: 可変 (copy の作り方が要る) か、ToString の override を持つ
    public static bool NeedsTypeArgDescriptor(TypeSym t)
    {
        return IlTypes.IsMutableStruct(t) || (IlTypes.IsUserStruct(t) && HasToStringOverride(t));
    }

    public static bool HasToStringOverride(TypeSym t)
    {
        if (t.Decl == null) return false;
        var cands = t.Decl.Lookup("ToString");
        if (cands == null) return false;
        foreach (var c in cands)
        {
            if (c.Kind == SymKind.Method && c.ParamCount == 0 && !c.IsStatic) return true;
        }
        return false;
    }

    // ---- struct の copy ----

    // generic な struct の __copy に渡す型引数の copier (`, M.__copy` の形)
    public string StructCopiers(TypeSym t, bool inCopy)
    {
        var args = t.Kind == TypeKind.Tuple ? t.Args : (t.Kind == TypeKind.Struct && t.Decl != null && t.Decl.TypeParams.Count > 0 ? t.Args : null);
        if (args == null) return "";
        var copiers = new List<string>();
        foreach (var a in args) copiers.Add(Copier(a, inCopy));
        while (copiers.Count > 0 && copiers[copiers.Count - 1] == "nil") copiers.RemoveAt(copiers.Count - 1);
        var parts = new List<string>();
        foreach (var c in copiers) parts.Add(", " + c);
        return string.Join("", parts);
    }

    // 型の値を写す関数 (式のテキスト)。写さなくてよい型は nil
    private string Copier(TypeSym t, bool inCopy)
    {
        if (t.Kind == TypeKind.TypeParam && inCopy) return "__tcs_cc_" + t.Name;
        if (IlTypes.IsMutableStruct(t))
        {
            var inner = StructCopiers(t, inCopy);
            if (inner != "") return "function(v) return " + TypeName(t) + ".__copy(v" + inner + ") end";
            return TypeName(t) + ".__copy";
        }
        return "nil";
    }

    // 元の式を持たない値 (分解の成分 / foreach の変数 / lambda の parameter) の copy
    private IlExpr CopyIfMutableStruct(TypeSym? t, IlExpr value)
    {
        if (IlTypes.IsMutableStruct(t)) return new IlStructCopy(value, TypeName(t!), false, StructCopiers(t!, false), null);
        return value;
    }

    // IL の形で新しい値と分かる値
    private static bool IsFreshStructIl(IlExpr built)
    {
        switch (built)
        {
            case IlNewObj:
            case IlStructCopy:
            case IlWith:
            case IlTable:
                return true;
            case IlLit lit:
                return lit.LuaText == "nil";
            case IlCall call:
                return call.Callee == "__tcs_dv" || call.Callee == "__tcs_nt" || call.Callee == "__tcs_tc" || call.Callee.StartsWith("__tcs_tuple");
            case IlNullableWrap wrap:
                return IsFreshStructIl(wrap.E);
            case IlParen paren:
                return IsFreshStructIl(paren.E);
        }
        return false;
    }

    // 値が他から参照されない式か (生成、with、タプル、代入式、switch 式)
    private static bool IsFreshStructValue(Node src)
    {
        switch (src.Kind)
        {
            case NodeKind.Paren:
            case NodeKind.Conv:
                return IsFreshStructValue(src.A!);
            case NodeKind.New:
            case NodeKind.Tuple:
            case NodeKind.Assign:
            case NodeKind.SwitchExpr:
                return true;
            case NodeKind.Call:
                return src.Text == "with";
        }
        return false;
    }

    // struct の method / accessor の受け手: C# の「変数」(local / param / field / 配列の要素 / this) なら直に渡し、
    // rvalue (property / List の indexer / 呼び出しの結果など) は copy
    private IlExpr StructReceiverArg(Node recvSyntax, IlExpr recv)
    {
        // ?. の受け手 (`S?` の .Value) は copy した値 (変更は捨てられる)
        if (_condBinding != null && recvSyntax == _subNode) return ConditionalStructReceiver(recv, NaturalType(recvSyntax));
        if (IsStructVariableReceiver(recvSyntax)) return recv;
        return WrapStructCopy(recvSyntax, recv);
    }

    private bool IsStructVariableReceiver(Node e)
    {
        switch (e.Kind)
        {
            case NodeKind.This: return true;
            case NodeKind.Name: return e.Info == RefKind.Local || e.Info == RefKind.Field && IsWritableField(e.Sym);
            // `x?.F` / `x?[i]` の binding は x が参照型なら変数 (`T?` の x は値の copy)。
            // struct の field は、持ち主の struct が変数のときだけ変数 (property の結果の field は値)
            case NodeKind.Member:
                if (e.Sym == null || e.Sym.Kind != SymKind.Field || !IsWritableField(e.Sym)) return false;
                if (e.Value == 1) return IsReferenceType(NaturalType(e.A!));
                return e.Sym.IsStatic || !NaturalType(e.A!).IsValueType || IsStructVariableReceiver(e.A!);
            case NodeKind.Index: return (e.A!.Type == null || e.A.Type.Kind == TypeKind.Array) && (e.Value != 1 || IsReferenceType(NaturalType(e.A)));
            case NodeKind.Paren:
            case NodeKind.Conv:
                return IsStructVariableReceiver(e.A!);
        }
        return false;
    }

    // readonly な field は、宣言した型の constructor (static なら static constructor) の外では値 (C# の防御的 copy)
    private bool IsWritableField(Symbol? f)
    {
        if (f == null || !f.IsReadonly) return true;
        var fn = Ctx.CurrentFunction;
        return fn != null && fn.Kind == SymKind.Ctor && fn.Owner == f.Owner && fn.IsStatic == f.IsStatic;
    }

    // ---- 小道具 ----

    private static List<IlStat> One(IlStat s)
    {
        var r = new List<IlStat>();
        r.Add(s);
        return r;
    }

    private static List<IlExpr> One(IlExpr e)
    {
        var r = new List<IlExpr>();
        r.Add(e);
        return r;
    }

    private static List<IlExpr> Two(IlExpr a, IlExpr b)
    {
        var r = new List<IlExpr>();
        r.Add(a);
        r.Add(b);
        return r;
    }

    private static List<IlArm> Arm(IlExpr cond, IlBlock body)
    {
        var r = new List<IlArm>();
        r.Add(new IlArm(cond, body));
        return r;
    }

    // 式の Origin を差し替える (elseif の条件と do-while の条件は自分の位置で評価される)
    public static IlExpr WithExprOrigin(IlExpr e, IlPos? origin)
    {
        switch (e)
        {
            case IlLit x1: return x1 with { Origin = origin };
            case IlVar x2: return x2 with { Origin = origin };
            case IlField x3: return x3 with { Origin = origin };
            case IlIndex x4: return x4 with { Origin = origin };
            case IlLen x5: return x5 with { Origin = origin };
            case IlBin x6: return x6 with { Origin = origin };
            case IlUn x7: return x7 with { Origin = origin };
            case IlParen x8: return x8 with { Origin = origin };
            case IlValues x9: return x9 with { Origin = origin };
            case IlTernary x10: return x10 with { Origin = origin };
            case IlCall x11: return x11 with { Origin = origin };
            case IlDynCall x12: return x12 with { Origin = origin };
            case IlInvoke x13: return x13 with { Origin = origin };
            case IlNewObj x14: return x14 with { Origin = origin };
            case IlTable x15: return x15 with { Origin = origin };
            case IlNewArray x16: return x16 with { Origin = origin };
            case IlNewQueue x17: return x17 with { Origin = origin };
            case IlNewHashSet x18: return x18 with { Origin = origin };
            case IlNumericConvert x19: return x19 with { Origin = origin };
            case IlRefCast x20: return x20 with { Origin = origin };
            case IlIsType x21: return x21 with { Origin = origin };
            case IlIsLuaType x22: return x22 with { Origin = origin };
            case IlIife x23: return x23 with { Origin = origin };
            case IlClosure x24: return x24 with { Origin = origin };
            case IlMethodGroup x25: return x25 with { Origin = origin };
            case IlStructCopy x26: return x26 with { Origin = origin };
            case IlNullableWrap x27: return x27 with { Origin = origin };
            case IlNullableHasValue x28: return x28 with { Origin = origin };
            case IlNullableValue x29: return x29 with { Origin = origin };
            case IlNullableGetOrDefault x30: return x30 with { Origin = origin };
            case IlLiftedBin x31: return x31 with { Origin = origin };
            case IlLiftedUn x32: return x32 with { Origin = origin };
            case IlCast x33: return x33 with { Origin = origin };
            case IlStructDescriptor x34: return x34 with { Origin = origin };
            case IlWith x35: return x35 with { Origin = origin };
        }
        return e;
    }
}

// 値を返す IIFE の形 (TryGetValueChainShape)
public sealed class ValueChain
{
    public List<IlStat> Setup;
    public IlIf? Chain;
    public IlExpr? Tail;
    public bool Complete;

    public ValueChain(List<IlStat> setup, IlIf? chain, IlExpr? tail, bool complete)
    {
        Setup = setup;
        Chain = chain;
        Tail = tail;
        Complete = complete;
    }
}

// 受け手 / 添字を temp に固定した代入先
public sealed class LoweredTarget
{
    public List<IlStat> Setup;
    public IlExpr Access;

    public LoweredTarget(List<IlStat> setup, IlExpr access)
    {
        Setup = setup;
        Access = access;
    }
}

// 値型の place の struct の field を辿った起点 (Stop は起点の node、Base は参照型の prefix か変数、Index は配列の添字)
public sealed class StructSlot
{
    public Node Stop;
    public Node Base;
    public Node? Index;
    public bool IsVariable;

    public StructSlot(Node stop, Node baseNode, Node? index, bool isVariable)
    {
        Stop = stop;
        Base = baseNode;
        Index = index;
        IsVariable = isVariable;
    }
}

// custom property の代入先
public sealed class PropTarget
{
    public List<IlStat> Setup;
    public IlExpr Recv;
    public string Name;
    public bool IsStatic;
    public string? StructOwner;
    public List<IlExpr> StaticArgs;

    public PropTarget(List<IlStat> setup, IlExpr recv, string name, bool isStatic, string? structOwner, List<IlExpr> staticArgs)
    {
        Setup = setup;
        Recv = recv;
        Name = name;
        IsStatic = isStatic;
        StructOwner = structOwner;
        StaticArgs = staticArgs;
    }
}

// 分解の先 (宣言した local、_、代入する場所、入れ子)
public sealed class DTarget
{
    public const int KDeclared = 0;
    public const int KDiscard = 1;
    public const int KPlace = 2;
    public const int KNested = 3;
    public int Kind;
    public string Name = "";
    public TypeSym? Type;
    public Node? Place;
    public List<DTarget> Items = new List<DTarget>();
    // 右辺より先に固定した場所
    public IlExpr? Fixed;

    public static DTarget Declared(string name, TypeSym type)
    {
        var t = new DTarget();
        t.Kind = KDeclared;
        t.Name = name;
        t.Type = type;
        return t;
    }

    public static DTarget Discard()
    {
        var t = new DTarget();
        t.Kind = KDiscard;
        return t;
    }

    public static DTarget PlaceOf(Node place, TypeSym type)
    {
        var t = new DTarget();
        t.Kind = KPlace;
        t.Place = place;
        t.Type = type;
        return t;
    }

    public static DTarget Nested(List<DTarget> items)
    {
        var t = new DTarget();
        t.Kind = KNested;
        t.Items = items;
        return t;
    }
}

// 分解の成分の代入
public sealed class DAssign
{
    public DTarget Target;
    public IlExpr Value;

    public DAssign(DTarget target, IlExpr value)
    {
        Target = target;
        Value = value;
    }
}
