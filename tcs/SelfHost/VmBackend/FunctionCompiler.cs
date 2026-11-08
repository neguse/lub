using System.Collections.Generic;
using TcsC;
using TcsSelf;
using TinySystem.Text;

namespace TcsVm;

/// <summary>1 つの関数を IL から命令列にする (doc/vm-design.md §15)。旧 tcsvm/Emitter/FunctionCompiler.cs (C#) を TinyC# に写したもの。
/// register の種類は関数の中で固定し (§6.1)、temp は種類と型ごとの空き list で使い回す。
/// サブセットは partial を持たないので、1 つの class を 1 つのファイルに置く。</summary>
public sealed class FunctionCompiler
{
    private ModuleBuilder _m;
    private FunctionDef _f;
    private string _className;
    // 命令の 64 bit の語の下と上の 32 bit
    private List<int> _lo = new List<int>();
    private List<int> _hi = new List<int>();
    private List<Dictionary<string, Var>> _scopes = new List<Dictionary<string, Var>>();
    // 型を書かない local (`IlLocal("__tcs_ret", null, null)`) の型: 同じ文の列の後の多重代入の左辺から決める
    private Dictionary<string, VType> _untyped = new Dictionary<string, VType>();
    // 空いた temp の register (RegInfo.Key() → 積んだ順。最後に積んだものから使う)
    private Dictionary<string, List<int>> _free = new Dictionary<string, List<int>>();
    private List<int> _temps = new List<int>();
    private Dictionary<string, List<int>> _freeBlocks = new Dictionary<string, List<int>>();
    private List<BlockUse> _blocksInUse = new List<BlockUse>();
    private List<LoopLabels> _loops = new List<LoopLabels>();
    private List<Label> _breakScopes = new List<Label>();
    private List<IifeFrame> _iifes = new List<IifeFrame>();
    /// <summary>return ごとに戻り値の後ろに足す変数 (struct の instance method の self、out の引数)。</summary>
    private List<Var> _extraRets = new List<Var>();
    private HashSet<int> _labelTargets = new HashSet<int>();
    private HashSet<string> _captured = new HashSet<string>();
    private VType _returnType = VType.Void;
    private int _lastLine = -1;
    // --trace: 直前の文 (同じ構文から分けた文の列では、先頭でだけ行を記録する)
    private IlStat? _prevTraceStat;
    private int _lambdas;

    public FunctionCompiler(ModuleBuilder m, FunctionDef f, string className)
    {
        _m = m;
        _f = f;
        _className = className;
    }

    // 命令の数 (C# 版の _code.Count)
    private int CodeCount => _lo.Count;

    // ---- 入口 ----

    public void CompileMethod(ExMethod method)
    {
        if (method.Body == null)
        {
            CFail.Report(_f.Name + ": no IL body");
            return;
        }
        _returnType = _m.Parse(method.ReturnType);
        var types = new List<VType>();
        foreach (var t in method.ParameterTypes) types.Add(_m.Parse(t));
        var names = new List<string>();
        var ins = new List<VType>();
        var outs = new List<NamedType>();
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            if (method.OutParameters.Contains(method.Parameters[i])) outs.Add(new NamedType(method.Parameters[i], types[i]));
            else
            {
                names.Add(method.Parameters[i]);
                ins.Add(types[i]);
            }
        }
        Prologue(method.Body, method.IsStatic ? null : VType.OfClass(_className), names, ins, outs, false);
        Block(method.Body, false);
        Finish();
    }

    /// <summary>struct の member (il-spec §10)。instance member は self を値で受け、return ごとに self の今の値を
    /// 戻り値の後ろに返す (呼ぶ側が場所に書き戻す。copy-in / copy-out)。</summary>
    public void CompileStructMethod(ExMethod method)
    {
        if (method.Body == null)
        {
            CFail.Report(_f.Name + ": no IL body");
            return;
        }
        _returnType = _m.Parse(method.ReturnType);
        var types = new List<VType>();
        foreach (var t in method.ParameterTypes) types.Add(_m.Parse(t));
        var names = new List<string>();
        var ins = new List<VType>();
        var outs = new List<NamedType>();
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            if (method.OutParameters.Contains(method.Parameters[i])) outs.Add(new NamedType(method.Parameters[i], types[i]));
            else
            {
                names.Add(method.Parameters[i]);
                ins.Add(types[i]);
            }
        }
        Prologue(method.Body, method.IsStatic ? null : VType.OfStruct(_className), names, ins, outs, !method.IsStatic);
        Block(method.Body, false);
        Finish();
    }

    /// <summary>struct の constructor: 0 の値 → field の initializer → 本文、で作った self を返す (il-spec §10)。</summary>
    public void CompileStructCtor(ExCtor ctor, List<ExField> initializers)
    {
        var selfType = VType.OfStruct(_className);
        _returnType = VType.Void;
        var types = new List<VType>();
        foreach (var t in ctor.ParameterTypes) types.Add(_m.Parse(t));
        foreach (var l in _m.Leaves(selfType)) _f.Rets.Add(_m.Info(l.Type));
        var body = ctor.Body != null ? ctor.Body : new IlBlock(new List<IlStat>());
        _captured = IlWalk.Captured(body);
        _scopes.Add(new Dictionary<string, Var>());
        var parameters = new List<Val>();
        for (int i = 0; i < ctor.Parameters.Count; i++) parameters.Add(NewVal(types[i]));
        _f.NParams = _f.Regs.Count;
        for (int i = 0; i < ctor.Parameters.Count; i++)
        {
            string name = ctor.Parameters[i];
            _scopes[0][name] = _captured.Contains(name) ? NewCell(parameters[i]) : Var.OfReg(parameters[i]);
        }
        var selfVar = Declare("self", selfType, null);
        _extraRets.Add(selfVar);
        EndStatement();
        foreach (var field in initializers)
        {
            Assign(new IlField(new IlVar("self", null), field.Name, null), field.Init!);
            EndStatement();
        }
        Block(body, false);
        if (!EndsFlow()) EmitReturn(null);
        Finish();
    }

    /// <summary>constructor: field の initializer、本文の順 (il-spec §9)。field の default は NEW が 0 で埋める。</summary>
    public void CompileCtor(ExCtor? ctor, List<ExField> initializers)
    {
        var body = ctor != null && ctor.Body != null ? ctor.Body : new IlBlock(new List<IlStat>());
        var types = new List<VType>();
        var names = new List<string>();
        if (ctor != null)
        {
            foreach (var t in ctor.ParameterTypes) types.Add(_m.Parse(t));
            foreach (var p in ctor.Parameters) names.Add(p);
        }
        Prologue(body, VType.OfClass(_className), names, types, null, false);
        foreach (var field in initializers)
        {
            Assign(new IlField(new IlVar("self", null), field.Name, null), field.Init!);
            EndStatement();
        }
        if (ctor != null && ctor.BaseArgs.Count > 0)
        {
            CFail.Report(_f.Name + ": base is not in the VM");
            return;
        }
        Block(body, false);
        Finish();
    }

    /// <summary>class の static field の initializer をまとめた関数 ("C.&lt;static&gt;")。</summary>
    public void CompileStaticInit(List<ExField> fields, bool lazy)
    {
        _scopes.Add(new Dictionary<string, Var>());
        if (lazy)
        {
            // 1 回だけ: flag を先に立てるので、循環は初期化中の値 (default) を読む (C# と同じ)
            int flag = _m.InitFlag(_className);
            var t = Temp(VType.Bool);
            var go = new Label();
            EmitImm(Op.LDS, t.Reg, flag);
            Jump(Op.JF, go, t.Reg, 0);
            Emit(Op.RET0);
            Mark(go);
            EmitImm(Op.LDK, t.Reg, 1);
            EmitImm(Op.STS, t.Reg, flag);
            EndStatement();
        }
        foreach (var field in fields)
        {
            int slot = _m.Static(_className, field.Name, out VType type);
            var v = ExprAs(field.Init!, type, null);
            for (int k = 0; k < v.Regs.Count; k++) EmitImm(Op.STS, v.Regs[k], slot + k);
            EndStatement();
        }
        Finish();
    }

    /// <summary>reload で足した instance field の initializer (doc/object-model.md §6)。引数を取らず、field の値を返す
    /// (C# の field initializer は this を読めない)。</summary>
    public void CompileFieldInit(ExField field)
    {
        _returnType = _m.Parse(field.Type);
        Prologue(field.Init!, null, new List<string>(), new List<VType>(), null, false);
        EmitReturn(field.Init);
        Finish();
    }

    private VType? _patternFound;

    private VType PatternLocalType(object body, string name)
    {
        _patternFound = null;
        FindPatternType(body, name);
        if (_patternFound == null)
        {
            CFail.Report(_f.Name + ": type of pattern variable " + name);
            return VType.Int;
        }
        return _patternFound;
    }

    private void FindPatternType(object n, string name)
    {
        if (_patternFound != null) return;
        if (n is IlIsType t && t.E is IlVar v && v.Name == name)
        {
            _patternFound = _m.ParseTypeRef(t.TypeRef);
            return;
        }
        if (n is IlIsPrimType lt && lt.E is IlVar lv && lv.Name == name)
        {
            if (lt.Type == IlPrimType.String) _patternFound = VType.String;
            else if (lt.Type == IlPrimType.Bool) _patternFound = VType.Bool;
            else if (lt.Type == IlPrimType.Float) _patternFound = VType.Float;
            else _patternFound = VType.Int;
            return;
        }
        foreach (var ch in IlNodes.Children(n)) FindPatternType(ch, name);
    }

    /// <summary>lambda: 引数の型は delegate の型 ft から、捕捉は caps の名前と中の型。</summary>
    public void CompileLambda(IlClosure c, VType ft, List<NamedType> caps, string file, int line)
    {
        // 式本体の lambda は位置を持たないことがあるので、作った場所の行から始める
        _f.File = file;
        if (line > 0) _f.Lines.Add(new LinePc(0, line));
        object body = c.Body != null ? c.Body : c.ExprBody!;
        _returnType = ft.Ret!;
        if (c.Params.Count != ft.Params.Count)
        {
            CFail.Report(_f.Name + ": lambda arity");
            return;
        }
        var ps = new List<VType>();
        foreach (var p in ft.Params) ps.Add(p);
        Prologue(body, null, c.Params, ps, null, false);
        // pattern の変数 (`o is Circle c`): 型は本文の型 test から
        foreach (var name in c.PatternLocals) Declare(name, PatternLocalType(body, name), null);
        for (int i = 0; i < caps.Count; i++)
        {
            _scopes[0][caps[i].Name] = Var.OfCap(i, caps[i].Type);
            _f.Caps.Add(_m.Info(VType.OfCell(caps[i].Type)));
        }
        if (c.Body != null) Block(c.Body, false);
        else
        {
            LineE(c.ExprBody!);
            if (ft.Ret!.Tag == VT.Void) Expr(c.ExprBody!, null, true);
            else EmitReturn(c.ExprBody!);
            EndStatement();
        }
        Finish();
    }

    /// <summary>引数を register に置き、closure が捕捉する引数は cell に移す。outs は out の引数 (local にして
    /// return ごとに返す)、selfOut は struct の self を return ごとに返す。</summary>
    private void Prologue(object body, VType? selfType, List<string> names, List<VType> types, List<NamedType>? outs, bool selfOut)
    {
        _captured = IlWalk.Captured(body);
        _scopes.Add(new Dictionary<string, Var>());
        var pnames = new List<string>();
        var pvals = new List<Val>();
        _traceSelf = 0;
        if (selfType != null)
        {
            pnames.Add("self");
            pvals.Add(NewVal(selfType));
            // class の self は呼び出しの間ずっと parameter の register にある (struct の self は値なので個体を持たない)
            if (selfType.Tag == VT.Class) _traceSelf = pvals[0].Regs[0] + 1;
        }
        for (int i = 0; i < names.Count; i++)
        {
            pnames.Add(names[i]);
            pvals.Add(NewVal(types[i]));
        }
        _f.NParams = _f.Regs.Count;
        if (_returnType.Tag != VT.Void)
        {
            foreach (var l in _m.Leaves(_returnType)) _f.Rets.Add(_m.Info(l.Type));
        }
        for (int i = 0; i < pnames.Count; i++)
            _scopes[0][pnames[i]] = _captured.Contains(pnames[i]) ? NewCell(pvals[i]) : Var.OfReg(pvals[i]);
        // 戻り値の後に out の値、最後に struct の self (呼ぶ側の StructCall と同じ順)
        if (outs != null)
        {
            foreach (var o in outs)
            {
                _extraRets.Add(Declare(o.Name, o.Type, null));
                foreach (var l in _m.Leaves(o.Type)) _f.Rets.Add(_m.Info(l.Type));
            }
        }
        if (selfOut)
        {
            _extraRets.Add(_scopes[0]["self"]);
            foreach (var l in _m.Leaves(selfType!)) _f.Rets.Add(_m.Info(l.Type));
        }
        EndStatement();
    }

    private Var NewCell(Val init)
    {
        var cellType = VType.OfCell(init.Type);
        int reg = NewReg(cellType);
        Emit(Op.NEW, reg, 0, _m.TypeIndex(cellType));
        StoreCell(reg, init.Type, init);
        return Var.OfCell(reg, init.Type);
    }

    private void Finish()
    {
        if (CFail.Failed) return;
        // 末尾に落ちる経路があれば終端を足す。値を返す関数の末尾には届かない (C# が保証) が、
        // verifier が通る形にするため型の合う RET を置く
        if (!EndsFlow())
        {
            if (_returnType.Tag == VT.Void) EmitReturn(null);
            else
            {
                var parts = new List<Val>();
                parts.Add(ZeroTemp(_returnType));
                foreach (var x in _extraRets) parts.Add(ReadVar(x));
                var regs = new List<int>();
                var ptypes = new List<VType>();
                foreach (var p in parts)
                {
                    foreach (var r in p.Regs) regs.Add(r);
                    ptypes.Add(p.Type);
                }
                int b = Block(LeafTypes(ptypes));
                for (int i = 0; i < regs.Count; i++) Emit(Op.MOV, b + i, regs[i]);
                Emit(Op.RET, b);
            }
        }
        _f.CodeLo = _lo;
        _f.CodeHi = _hi;
        _f.HasCode = true;
        if (_f.Regs.Count > 0xFFFF) CFail.Report(_f.Name + ": too many registers");
    }

    private bool EndsFlow()
    {
        if (_lo.Count == 0 || _labelTargets.Contains(_lo.Count)) return false;
        int op = _lo[_lo.Count - 1] & 0xFF;
        return op == (int)Op.RET || op == (int)Op.RET0 || op == (int)Op.JMP;
    }

    // ---- register ----

    private int NewReg(VType leafType)
    {
        _f.Regs.Add(_m.Info(leafType));
        return _f.Regs.Count - 1;
    }

    private Val NewVal(VType t)
    {
        var regs = new List<int>();
        foreach (var l in _m.Leaves(t)) regs.Add(NewReg(l.Type));
        return new Val(t, regs);
    }

    private Val Temp(VType t)
    {
        var leaves = _m.Leaves(t);
        var regs = new List<int>();
        for (int i = 0; i < leaves.Count; i++)
        {
            string key = _m.Info(leaves[i].Type).Key();
            int r;
            if (_free.TryGetValue(key, out List<int>? stack) && stack.Count > 0)
            {
                r = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
            }
            else r = NewReg(leaves[i].Type);
            regs.Add(r);
            _temps.Add(r);
        }
        return new Val(t, regs);
    }

    /// <summary>文の終わり: その文の temp と呼び出しの引数の区画を空きに戻す。</summary>
    private void EndStatement()
    {
        foreach (var r in _temps)
        {
            string key = _f.Regs[r].Key();
            if (!_free.TryGetValue(key, out List<int>? stack))
            {
                stack = new List<int>();
                _free[key] = stack;
            }
            stack.Add(r);
        }
        _temps.Clear();
        foreach (var u in _blocksInUse)
        {
            if (!_freeBlocks.TryGetValue(u.Key, out List<int>? s))
            {
                s = new List<int>();
                _freeBlocks[u.Key] = s;
            }
            s.Add(u.Base);
        }
        _blocksInUse.Clear();
    }

    /// <summary>連続する register の区画 (呼び出しの引数と戻り値)。同じ並びの区画を文の間で使い回す。</summary>
    private int Block(List<VType> leafTypes)
    {
        if (leafTypes.Count == 0) return 0;
        var keys = new List<string>();
        foreach (var t in leafTypes) keys.Add(_m.Info(t).Key());
        string key = string.Join(";", keys);
        int b;
        if (_freeBlocks.TryGetValue(key, out List<int>? s) && s.Count > 0)
        {
            b = s[s.Count - 1];
            s.RemoveAt(s.Count - 1);
        }
        else
        {
            b = _f.Regs.Count;
            foreach (var t in leafTypes) NewReg(t);
        }
        _blocksInUse.Add(new BlockUse(key, b));
        return b;
    }

    private List<VType> LeafTypes(List<VType> types)
    {
        var r = new List<VType>();
        foreach (var t in types)
        {
            if (t.Tag == VT.Void) continue;
            foreach (var l in _m.Leaves(t)) r.Add(l.Type);
        }
        return r;
    }

    // ---- 命令 ----
    // 語は op (8 bit) | a (16) << 8 | b (16) << 24 | c (16) << 40 | x (8) << 56。即値の形は op | a << 8 | imm (32) << 24

    private void Emit(Op op, int a = 0, int b = 0, int c = 0, int x = 0)
    {
        if (a < 0 || a > 0xFFFF || b < 0 || b > 0xFFFF || c < 0 || c > 0xFFFF || x < 0 || x > 0xFF)
        {
            CFail.Report(_f.Name + ": operand out of range");
            return;
        }
        _lo.Add((int)op | a << 8 | (b & 0xFF) << 24);
        _hi.Add(b >> 8 | c << 8 | x << 24);
    }

    private void EmitImm(Op op, int a, int imm)
    {
        _lo.Add((int)op | (a & 0xFFFF) << 8 | (imm & 0xFF) << 24);
        _hi.Add(imm >> 8 & 0xFFFFFF);
    }

    private void Mark(Label l)
    {
        l.Target = _lo.Count;
        _labelTargets.Add(_lo.Count);
        foreach (var at in l.Patches) Patch(at, l.Target);
    }

    private void Patch(int at, int target)
    {
        int off = target - (at + 1);
        if (off < -(1 << 23) || off >= 1 << 23)
        {
            CFail.Report(_f.Name + ": jump too far");
            return;
        }
        _hi[at] = (_hi[at] & 0xFF) | (off & 0xFFFFFF) << 8;
    }

    private void Jump(Op op, Label l, int a = 0, int b = 0)
    {
        int at = _lo.Count;
        _lo.Add((int)op | (a & 0xFFFF) << 8 | (b & 0xFF) << 24);
        _hi.Add((b & 0xFFFF) >> 8);
        if (l.Target >= 0) Patch(at, l.Target);
        else l.Patches.Add(at);
    }

    private void LineE(IlExpr node)
    {
        LineAt(node.Origin);
    }

    private void LineS(IlStat node)
    {
        LineAt(node.Origin);
    }

    private void LineAt(IlPos? origin)
    {
        if (origin == null) return;
        _f.At = origin;
        int line = origin.Line;
        if (_f.File == "")
        {
            _f.File = FileName(origin.File);
            _f.Path = VmTrace.Normalize(origin.File);
            if (_m.IsTraced(origin.File)) _m.TraceClass(_className);
        }
        if (line == _lastLine) return;
        _lastLine = line;
        if (_f.Lines.Count > 0 && _f.Lines[_f.Lines.Count - 1].Pc == _lo.Count)
            _f.Lines[_f.Lines.Count - 1] = new LinePc(_lo.Count, line);
        else
            _f.Lines.Add(new LinePc(_lo.Count, line));
    }

    // path の最後の部分 (Path.GetFileName)
    private static string FileName(string path)
    {
        var u = new Utf8Bytes(path);
        int start = 0;
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] == '/' || u[i] == '\\') start = i + 1;
        }
        return u.Slice(start, u.Length - start).ToString();
    }

    // ---- 変数 ----

    private Var? Lookup(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out Var? v)) return v;
        }
        return null;
    }

    /// <summary>新しい変数を宣言する。closure が捕捉する名前なら cell に住む。</summary>
    private Var Declare(string name, VType type, IlExpr? init)
    {
        Var v;
        if (_captured.Contains(name))
        {
            var val = init == null ? ZeroTemp(type) : ExprAs(init, type, null);
            v = NewCell(val);
        }
        else
        {
            var val = NewVal(type);
            if (init != null) ExprAs(init, type, val);
            else EmitZero(val);
            v = Var.OfReg(val);
        }
        _scopes[_scopes.Count - 1][name] = v;
        return v;
    }

    private Val ZeroTemp(VType t)
    {
        var v = Temp(t);
        EmitZero(v);
        return v;
    }

    private int CellReg(Var v)
    {
        if (v.K == VarKind.Cell) return v.Reg;
        if (v.K == VarKind.Cap)
        {
            var t = Temp(VType.OfCell(v.Inner));
            Emit(Op.LDCAP, t.Reg, 0, v.Index);
            return t.Reg;
        }
        CFail.Report("not a cell");
        return 0;
    }

    private void StoreCell(int cell, VType inner, Val v)
    {
        var leaves = _m.Leaves(inner);
        for (int k = 0; k < leaves.Count; k++) Emit(Pick(Op.STF_I32, leaves[k].Kind), cell, v.Regs[k], k);
    }

    // object / interface どうしの参照の比較 (IL の __tcs_refeq。record の値の等価と分けるため IL は __tcs_refeq で書く)。
    // VM の == は参照の比較なので、Eq の二項演算として読む (無ければ null)
    private static IlExpr? RefEq(IlExpr e)
    {
        if (e is IlCall c && c.Callee == "__tcs_refeq" && c.Args.Count == 2) return new IlBin(IlBinOp.Eq, c.Args[0], c.Args[1], null);
        return null;
    }

    // 種類ごとに並んだ命令 (I32、F32、Bool、Ref の順) から kind の命令を選ぶ
    private static Op Pick(Op i32, Kind kind) => (Op)((int)i32 + (int)kind);

    // 誤りを記録した後に返す仮の値 (出力は使われない)
    private Val BadVal()
    {
        var regs = new List<int>();
        regs.Add(0);
        return new Val(VType.Int, regs);
    }

    // ---- 文 ----

    private void Block(IlBlock block, bool newScope = true)
    {
        if (newScope) _scopes.Add(new Dictionary<string, Var>());
        TypeUntypedLocals(block.Stats);
        foreach (var s in block.Stats) Stat(s);
        if (newScope) _scopes.RemoveAt(_scopes.Count - 1);
    }

    /// <summary>型も初期値も無い local は、続く多重代入 (out の呼び出しの結果を受ける) の左辺の位置の型にする。
    /// 式の中の out の呼び出し (`bool ok = Host.Try(x, out y)`) を IL は IIFE の temp と多重代入にする。</summary>
    private void TypeUntypedLocals(List<IlStat> stats)
    {
        for (int i = 0; i < stats.Count; i++)
        {
            if (!(stats[i] is IlLocal l) || l.Type != null || l.Init != null || l.Name == "_") continue;
            for (int k = i + 1; k < stats.Count; k++)
            {
                if (!(stats[k] is IlMultiAssign ma)) continue;
                var types = MultiTypes(ma);
                for (int t = 0; t < ma.Targets.Count && t < types.Count; t++)
                {
                    if (ma.Targets[t] is IlVar v && v.Name == l.Name) _untyped[l.Name] = types[t];
                }
                if (_untyped.ContainsKey(l.Name)) break;
            }
        }
    }

    // --trace: 行の印に付ける self の register + 1 (class の instance の method でなければ 0)
    private int _traceSelf;

    // --trace: 文の頭で行を通った印を置く (class の instance の method なら self も付け、VM が (行, self) の組で記録する)。
    // builder が 1 つの文を同じ Origin の文の列に分けたときは、列の先頭でだけ置く
    private void TraceStat(IlStat s)
    {
        var origin = s.Origin;
        var prev = _prevTraceStat;
        _prevTraceStat = s;
        if (origin == null || !_m.IsTraced(origin.File)) return;
        if (prev != null && prev.Origin == origin && (prev is IlLocal || prev is IlAssign || prev is IlCallStat || prev is IlMultiAssign)) return;
        EmitImm(Op.TRACE, _traceSelf, _m.TraceLine(origin.File, origin.Line));
    }

    private void TracePoint(IlPos? origin)
    {
        if (origin == null || !_m.IsTraced(origin.File)) return;
        EmitImm(Op.TRACE, _traceSelf, _m.TraceLine(origin.File, origin.Line));
    }

    // --trace: 代入の後に、代入先の値を控える。
    // 代入先は副作用無しに読み直せるもの (変数、field、定数か変数の添字) だけで、builder の作った一時変数は除く
    private void TraceValue(IlExpr target, IlPos? origin)
    {
        if (origin == null || !_m.IsTraced(origin.File)) return;
        string label = TraceLabel(target);
        if (label == "") return;
        var v = Expr(target);
        if (CFail.Failed || v.Regs.Count == 0 || v.Regs.Count > 64) return;
        int site = _m.TraceValue(origin.File, origin.Line, origin.Col, label, v.Type, v.Regs.Count);
        if (site > 0xFFFF) return;
        for (int k = 0; k < v.Regs.Count; k++) Emit(Op.TVAL, v.Regs[k], k, site);
    }

    // 代入先の C# の書き方 (読み直せないものは "")。
    // 自分の class の static は class 名を付けずに書く (書いた形に近い)
    private string TraceLabel(IlExpr e)
    {
        if (e is IlVar v)
        {
            if (v.Name.StartsWith("__tcs") || v.Name == "self") return "";
            return v.Name;
        }
        if (e is IlField f)
        {
            if (f.Recv is IlVar owner && (owner.Name == "self" || owner.Name == _className)) return f.Name;
            string recv = TraceLabel(f.Recv);
            return recv == "" ? "" : recv + "." + f.Name;
        }
        if (e is IlIndex ix)
        {
            string recv = TraceLabel(ix.Recv);
            string idx = ix.Idx is IlLit lit ? IlLiterals.Display(lit) : TraceLabel(ix.Idx);
            return recv == "" || idx == "" ? "" : recv + "[" + idx + "]";
        }
        if (e is IlParen p) return TraceLabel(p.E);
        return "";
    }

    private void Stat(IlStat s)
    {
        if (CFail.Failed) return;
        LineS(s);
        TraceStat(s);
        if (s is IlLocal discardLocal && discardLocal.Name == "_" && discardLocal.Type == null && discardLocal.Init == null)
        {
        }
        else if (s is IlLocal local)
        {
            VType type;
            VType? inferred;
            if (local.Type != null) type = _m.Parse(local.Type);
            else if (local.Init != null) type = TypeOf(local.Init);
            else if (_untyped.TryGetValue(local.Name, out inferred)) type = inferred!;
            else
            {
                CFail.Report($"{_f.Name}: local {local.Name} has no type");
                return;
            }
            Declare(local.Name, type, local.Init);
            if (local.Init != null) TraceValue(new IlVar(local.Name, local.Origin), local.Origin);
        }
        else if (s is IlAssign discard && discard.Target is IlVar dv && dv.Name == "_" && Lookup("_") == null)
        {
            // 捨てる out (`out _`): builder は宣言の無い `_` に値を代入する
            Expr(discard.Value, null, true);
        }
        else if (s is IlAssign assign)
        {
            Assign(assign.Target, assign.Value);
            TraceValue(assign.Target, assign.Origin);
        }
        else if (s is IlCallStat call)
        {
            Expr(call.Call, null, true);
        }
        else if (s is IlIf iff)
        {
            var end = new Label();
            foreach (var arm in iff.Arms)
            {
                var next = new Label();
                Cond(arm.Cond, false, next);
                EndStatement();
                Block(arm.Body);
                Jump(Op.JMP, end);
                Mark(next);
            }
            if (iff.Else != null) Block(iff.Else);
            Mark(end);
        }
        else if (s is IlWhile w)
        {
            var top = new Label();
            var cont = new Label();
            var exit = new Label();
            Mark(top);
            // 条件は反復ごとに評価するので、毎回行を記録する (先頭の文の印とは別に)
            if (!(w.Cond is IlLit)) TracePoint(w.Origin);
            Cond(w.Cond, false, exit);
            EndStatement();
            _loops.Add(new LoopLabels(exit, cont));
            Block(w.Body);
            _loops.RemoveAt(_loops.Count - 1);
            Mark(cont);
            if (w.Trailer != null) Block(w.Trailer);
            Jump(Op.JMP, top);
            Mark(exit);
        }
        else if (s is IlRepeat r)
        {
            var top = new Label();
            var cont = new Label();
            var exit = new Label();
            Mark(top);
            _loops.Add(new LoopLabels(exit, cont));
            Block(r.Body);
            _loops.RemoveAt(_loops.Count - 1);
            Mark(cont);
            Cond(r.Cond, true, top);
            EndStatement();
            Mark(exit);
        }
        else if (s is IlNumericFor nf)
        {
            NumericFor(nf);
        }
        else if (s is IlForeachList fl)
        {
            ForeachList(fl);
        }
        else if (s is IlBreak)
        {
            if (_loops.Count == 0 && _breakScopes.Count == 0)
            {
                CFail.Report("break outside loop");
                return;
            }
            Jump(Op.JMP, _breakScopes.Count > 0 ? _breakScopes[_breakScopes.Count - 1] : _loops[_loops.Count - 1].Break);
        }
        else if (s is IlContinue)
        {
            Jump(Op.JMP, _loops[_loops.Count - 1].Continue);
        }
        else if (s is IlReturn iret && _iifes.Count > 0)
        {
            var frame = _iifes[_iifes.Count - 1];
            if (iret.Value != null && frame.Result != null) ExprAs(iret.Value, frame.Type, frame.Result);
            else if (iret.Value != null) Expr(iret.Value, null, true); // void の IIFE の `return f()` (`a?.M()` の文)
            Jump(Op.JMP, frame.End);
        }
        else if (s is IlReturn ret)
        {
            EmitReturn(ret.Value);
        }
        else if (s is IlMultiAssign ma)
        {
            MultiAssign(ma);
        }
        else if (s is IlForeachDict fd)
        {
            ForeachDict(fd);
        }
        else if (s is IlForeachRunes fr)
        {
            ForeachRunes(fr);
        }
        else if (s is IlForPairs fp && P1IsClear(fp))
        {
            // List.Clear / Dictionary.Clear の lowering (全部の key に null を入れる)
            var ct = TypeOf(fp.Coll);
            var coll = Expr(fp.Coll);
            if (ct.Tag == VT.List) Emit(Op.LIST_CLEAR, coll.Reg);
            else if (ct.Tag == VT.Dict) Emit(Op.CALLR, coll.Reg, 0, _m.Intrinsic("dict.clear"));
            else
            {
                CFail.Report($"{_f.Name}: clear of {ct.Display()}");
                return;
            }
        }
        else if (s is IlDo d)
        {
            Block(d.Body);
        }
        else if (s is IlBreakScope bs)
        {
            var end = new Label();
            _breakScopes.Add(end);
            Block(bs.Body);
            _breakScopes.RemoveAt(_breakScopes.Count - 1);
            Mark(end);
        }
        else
        {
            CFail.Report($"{_f.Name}: statement {IlNodes.Name(s)} is not in the VM yet");
            return;
        }
        EndStatement();
    }

    // C# 版の `IlForPairs { VVar: null, Body.Stats: [IlAssign { Target: IlIndex { Recv: IlVar cv, Idx: IlVar ck },
    // Value: IlLit { Kind: IlLitKind.Null } }] } fp when fp.Coll is IlVar cv2 && cv2.Name == cv.Name && ck.Name == fp.KVar`
    private static bool P1IsClear(IlForPairs fp)
    {
        if (fp.VVar != null) return false;
        if (fp.Body.Stats.Count != 1) return false;
        if (fp.Body.Stats[0] is IlAssign a && a.Target is IlIndex ti && ti.Recv is IlVar cv && ti.Idx is IlVar ck
            && IlLiterals.IsNull(a.Value))
            return fp.Coll is IlVar cv2 && cv2.Name == cv.Name && ck.Name == fp.KVar;
        return false;
    }

    /// <summary>return: 戻り値の葉に、struct の self と out の引数の今の値を続けて返す。</summary>
    private void EmitReturn(IlExpr? value)
    {
        var value0 = value;
        if (value0 != null && _returnType.Tag == VT.Void)
        {
            // 式本体の void method (`void Push(int v) => items.Add(v);`): 式を評価して捨てる
            Expr(value0, null, true);
            value0 = null;
        }
        var parts = new List<Val>();
        if (value0 != null) parts.Add(ExprAs(value0, _returnType, null));
        foreach (var extra in _extraRets) parts.Add(ReadVar(extra));
        if (parts.Count == 0)
        {
            Emit(Op.RET0);
            return;
        }
        var regs = new List<int>();
        var ptypes = new List<VType>();
        foreach (var p in parts)
        {
            foreach (var pr in p.Regs) regs.Add(pr);
        }
        foreach (var p in parts) ptypes.Add(p.Type);
        var b = Block(LeafTypes(ptypes));
        for (var i = 0; i < regs.Count; i++)
            if (b + i != regs[i]) Emit(Op.MOV, b + i, regs[i]);
        Emit(Op.RET, b);
    }

    private Val ReadVar(Var v)
    {
        if (v.K == VarKind.Reg) return v.Val!;
        var cregs = new List<int>();
        cregs.Add(CellReg(v));
        return LoadPlace(Place.Make(v.Type, "field", 0, new Val(VType.OfCell(v.Type), cregs), null, null, 0, null), null);
    }

    private int Contiguous(Val v)
    {
        var ok = true;
        for (var i = 1; i < v.Regs.Count; i++)
        {
            if (v.Regs[i] != v.Regs[0] + i) ok = false;
        }
        if (ok) return v.Regs[0];
        var types = new List<VType>();
        types.Add(v.Type);
        var b = Block(LeafTypes(types));
        for (var i = 0; i < v.Regs.Count; i++) Emit(Op.MOV, b + i, v.Regs[i]);
        return b;
    }

    /// <summary>数の for (IlNumericFor: v を start から limit まで 1 ずつ増やす)。limit は 1 回だけ評価し、
    /// v が本文で書き換えられるときは隠れた counter を回す (il-reference: IlNumericFor)。</summary>
    private void NumericFor(IlNumericFor nf)
    {
        _scopes.Add(new Dictionary<string, Var>());
        var assigned = AssignsVar(nf.Body, nf.Var) || _captured.Contains(nf.Var);
        var limit = NewVal(VType.Int);
        var counter = NewVal(VType.Int);
        ExprAs(nf.Start, VType.Int, counter);
        ExprAs(nf.Limit, VType.Int, limit);
        EndStatement();
        var top = new Label();
        var cont = new Label();
        var exit = new Label();
        Jump(Op.JF_LE_I32, exit, counter.Reg, limit.Reg);
        Mark(top);
        TracePoint(nf.Origin);
        _scopes.Add(new Dictionary<string, Var>());
        if (assigned)
        {
            var v = Declare(nf.Var, VType.Int, null);
            WriteVar(v, counter);
        }
        else _scopes[_scopes.Count - 1][nf.Var] = Var.OfReg(counter);
        EndStatement();
        _loops.Add(new LoopLabels(exit, cont));
        Block(nf.Body);
        _loops.RemoveAt(_loops.Count - 1);
        _scopes.RemoveAt(_scopes.Count - 1);
        Mark(cont);
        Jump(Op.JT_EQ_I32, exit, counter.Reg, limit.Reg);
        Emit(Op.ADDI_I32, counter.Reg, counter.Reg, 1);
        Jump(Op.JMP, top);
        Mark(exit);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    /// <summary>foreach (List / 配列)。反復変数は反復ごとに新しい (il-spec §7)。
    /// 長さは反復ごとに読み直す (本文が足した要素も回る)。</summary>
    private void ForeachList(IlForeachList fl)
    {
        _scopes.Add(new Dictionary<string, Var>());
        var collType = TypeOf(fl.Coll);
        VType elem;
        if (collType.Tag == VT.Array) elem = collType.Elem!;
        else if (collType.Tag == VT.List) elem = collType.Elem!;
        else
        {
            CFail.Report($"{_f.Name}: foreach over {collType.Display()} is not in the VM yet");
            return;
        }
        var coll = NewVal(collType);
        ExprAs(fl.Coll, collType, coll);
        var i = NewVal(VType.Int);
        EmitImm(Op.LDK, i.Reg, 0);
        var n = NewVal(VType.Int);
        EndStatement();
        var top = new Label();
        var cont = new Label();
        var exit = new Label();
        Mark(top);
        TracePoint(fl.Origin);
        Emit(collType.Tag == VT.Array ? Op.ARR_LEN : Op.LIST_LEN, n.Reg, coll.Reg);
        Jump(Op.JF_LT_I32, exit, i.Reg, n.Reg);
        _scopes.Add(new Dictionary<string, Var>());
        var item = LoadPlace(collType.Tag == VT.Array
            ? Place.Make(elem, "elem", 0, null, coll, i, 0, collType)
            : Place.Make(elem, "list", 0, null, coll, i, 0, collType), null);
        var v = _captured.Contains(fl.Var) ? NewCell(item) : Var.OfReg(CopyToNew(item));
        _scopes[_scopes.Count - 1][fl.Var] = v;
        EndStatement();
        _loops.Add(new LoopLabels(exit, cont));
        Block(fl.Body);
        _loops.RemoveAt(_loops.Count - 1);
        _scopes.RemoveAt(_scopes.Count - 1);
        Mark(cont);
        Emit(Op.ADDI_I32, i.Reg, i.Reg, 1);
        Jump(Op.JMP, top);
        Mark(exit);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    private Val CopyToNew(Val v)
    {
        var d = NewVal(v.Type);
        Store(d, v);
        return d;
    }

    private static bool AssignsVar(IlBlock body, string name)
    {
        foreach (var s in body.Stats)
        {
            if (AssignsVar(s, name)) return true;
        }
        return false;
    }

    private static bool AssignsVar(IlStat s, string name)
    {
        if (s is IlAssign asg)
        {
            if (asg.Target is IlVar av) return av.Name == name;
            return false;
        }
        if (s is IlMultiAssign m)
        {
            foreach (var t in m.Targets)
            {
                if (t is IlVar mv && mv.Name == name) return true;
            }
            return false;
        }
        if (s is IlLocal l) return l.Name == name; // 同名の local が隠す: 保守的に counter を分ける
        if (s is IlIf i)
        {
            foreach (var a in i.Arms)
            {
                if (AssignsVar(a.Body, name)) return true;
            }
            return i.Else != null && AssignsVar(i.Else, name);
        }
        if (s is IlWhile w) return AssignsVar(w.Body, name) || (w.Trailer != null && AssignsVar(w.Trailer, name));
        if (s is IlRepeat r) return AssignsVar(r.Body, name);
        if (s is IlNumericFor f) return f.Var == name || AssignsVar(f.Body, name);
        if (s is IlForeachList fe) return fe.Var == name || AssignsVar(fe.Body, name);
        if (s is IlDo d) return AssignsVar(d.Body, name);
        if (s is IlBreakScope b) return AssignsVar(b.Body, name);
        return false;
    }

    // ---- 代入と格納場所 ----

    private void Assign(IlExpr target, IlExpr value)
    {
        var tgt = target;
        while (tgt is IlParen p) tgt = p.E;
        if (tgt is IlVar v)
        {
            var vr = Lookup(v.Name);
            if (vr != null)
            {
                // 結果を local へ直接書く。with / 初期化子 / `??` などは dest を書いてから残りを評価するので、
                // 最後の命令だけが dest を書く形でない右辺が local を読むなら temp を経る
                if (vr.K == VarKind.Reg && (WritesDestLast(value) || !MayRead(value, v.Name)))
                    ExprAs(value, vr.Type, vr.Val!);
                else if (vr.K == VarKind.Reg)
                    Store(vr.Val!, ExprAs(value, vr.Type, null));
                else
                    WriteVar(vr, ExprAs(value, vr.Type, null));
                return;
            }
        }
        if (tgt is IlIndex di && !di.PlusOne)
        {
            var dt = TypeOf(di.Recv);
            if (dt.Tag == VT.Dict)
            {
                // C# と同じく key、値の順に評価してから入れる
                var dict = Expr(di.Recv);
                var key = ExprAs(di.Idx, dt.Key!, null);
                var dv = ExprAs(value, dt.Value!, null);
                var at = Temp(VType.Int);
                Emit(Op.DICT_INS, at.Reg, dict.Reg, key.Reg);
                StorePlace(Place.Make(dt.Value!, "dict", 0, null, dict, at, 0, null), dv);
                return;
            }
        }
        var place = PlaceOf(tgt);
        var val = ExprAs(value, place.Type, null);
        StorePlace(place, val);
    }

    // 子を temp に評価してから最後の命令だけで dest を書く式 (dest を子に渡さない)
    private static bool WritesDestLast(IlExpr e)
    {
        switch (e)
        {
            case IlLit:
            case IlVar:
            case IlField:
            case IlIndex:
            case IlLen:
            case IlUn:
            case IlNumericConvert:
                return true;
            case IlBin b:
                return b.Op != IlBinOp.And && b.Op != IlBinOp.Or;
        }
        return false;
    }

    // node (IL の式か文) の中に変数 name の出現があるか。名前で数えるので保守的
    private static bool MayRead(object node, string name)
    {
        if (node is IlVar x) return x.Name == name;
        foreach (var c in IlNodes.Children(node))
        {
            if (MayRead(c, name)) return true;
        }
        return false;
    }

    private void WriteVar(Var v, Val val)
    {
        if (v.K == VarKind.Reg) Store(v.Val!, val);
        else StoreCell(CellReg(v), v.Type, Convert(val, v.Type));
    }

    private Place PlaceOf(IlExpr e)
    {
        if (e is IlParen p) return PlaceOf(p.E);
        if (e is IlVar v)
        {
            var vr = Lookup(v.Name);
            if (vr != null)
            {
                if (vr.K == VarKind.Reg) return Place.Make(vr.Type, "local", 0, vr.Val, null, null, 0, null);
                var cregs = new List<int>();
                cregs.Add(CellReg(vr));
                return Place.Make(vr.Type, "field", 0, new Val(VType.OfCell(vr.Type), cregs), null, null, 0, null);
            }
        }
        else if (e is IlField sf && sf.Recv is IlVar cls && Lookup(cls.Name) == null && _m.IsTypeName(cls.Name))
        {
            EnsureStaticInit(cls.Name);
            int slot = _m.Static(cls.Name, sf.Name, out VType stype);
            return Place.Make(stype, "static", slot, null, null, null, 0, null);
        }
        else if (e is IlField f)
        {
            var recvType = TypeOf(f.Recv);
            var type = _m.FieldType(recvType, f.Name);
            if (recvType.Tag == VT.Class)
            {
                var obj = Expr(f.Recv);
                return Place.Make(type, "field", 0, obj, null, null, LeafIndex(_m.ObjectLeaves(recvType), f.Name), null);
            }
            // struct の中の field: 外側の場所の葉の範囲を狭める
            var outer = PlaceOf(f.Recv);
            var inner = LeafIndex(_m.Leaves(recvType), f.Name);
            var q = outer.Copy();
            q.Type = type;
            q.Leaf = outer.Leaf + inner;
            return q;
        }
        else if (e is IlIndex idx)
        {
            var collType = TypeOf(idx.Recv);
            if (collType.Tag == VT.Array)
            {
                var arr = Expr(idx.Recv);
                var ai = ExprAs(idx.Idx, VType.Int, null);
                return Place.Make(collType.Elem!, "elem", 0, null, arr, ai, 0, collType);
            }
            if (collType.Tag == VT.List)
            {
                var list = Expr(idx.Recv);
                var li = ExprAs(idx.Idx, VType.Int, null);
                return Place.Make(collType.Elem!, "list", 0, null, list, li, 0, collType);
            }
            if (collType.Tag == VT.Dict) return DictPlace(idx, collType, false);
            CFail.Report($"{_f.Name}: index of {collType.Display()} is not in the VM yet");
            return Place.Make(VType.Int, "local", 0, BadVal(), null, null, 0, null);
        }
        if (TypeOf(e).IsAggregate)
        {
            // 値 (呼び出しの結果など) の field: 値を temp に置いて、その中の葉を読む (書いても捨てられる。C# と同じ)
            var tv = CopyToTemp(Expr(e));
            return Place.Make(tv.Type, "local", 0, tv, null, null, 0, null);
        }
        CFail.Report($"{_f.Name}: place {IlNodes.Name(e)} is not in the VM yet");
        return Place.Make(VType.Int, "local", 0, BadVal(), null, null, 0, null);
    }

    private Val CopyToTemp(Val v)
    {
        var d = Temp(v.Type);
        Store(d, v);
        return d;
    }

    /// <summary>遅延初期化の型の static に触る前に、その型の initializer を (まだなら) 走らせる。</summary>
    private void EnsureStaticInit(string owner)
    {
        var owner0 = _m.CanonName(owner);
        if (!_m.IsLazyStatic(owner0) || _f.Name == owner0 + ".<static>") return;
        var index = _m.FindFunction(owner0 + ".<static>");
        if (index < 0)
        {
            CFail.Report($"{_f.Name}: no static initializer of {owner0}");
            return;
        }
        var flag = Temp(VType.Bool);
        var done = new Label();
        EmitImm(Op.LDS, flag.Reg, _m.InitFlag(owner0));
        Jump(Op.JT, done, flag.Reg);
        Emit(Op.CALL, 0, 0, index);
        Mark(done);
    }

    /// <summary>Dictionary の key の entry。読みは無い key で fault (DICT_FIND)、書きは足す (DICT_INS)。</summary>
    private Place DictPlace(IlIndex idx, VType d, bool insert)
    {
        var dict = Expr(idx.Recv);
        var key = ExprAs(idx.Idx, d.Key!, null);
        var at = Temp(VType.Int);
        Emit(insert ? Op.DICT_INS : Op.DICT_FIND, at.Reg, dict.Reg, key.Reg);
        return Place.Make(d.Value!, "dict", 0, null, dict, at, 0, null);
    }

    private static int LeafIndex(List<Leaf> leaves, string field)
    {
        for (var i = 0; i < leaves.Count; i++)
            if (leaves[i].Path == field || leaves[i].Path.StartsWith(field + "."))
                return i;
        CFail.Report($"no leaf {field}");
        return 0;
    }

    private void StorePlace(Place p, Val v)
    {
        var leaves = _m.Leaves(p.Type);
        for (var k = 0; k < leaves.Count; k++)
        {
            var r = v.Regs[k];
            var kind = leaves[k].Kind;
            if (p.Kind == "static") EmitImm(Op.STS, r, p.Slot + p.Leaf + k);
            else if (p.Kind == "field") Emit(Pick(Op.STF_I32, kind), p.Obj!.Reg, r, p.Leaf + k);
            else if (p.Kind == "elem" && p.Owner!.Elem!.IsAggregate)
                Emit(Pick(Op.STEF_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r, p.Leaf + k);
            else if (p.Kind == "elem") Emit(Pick(Op.STE_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r);
            else if (p.Kind == "list" && p.Owner!.Elem!.IsAggregate)
                Emit(Pick(Op.LIST_STF_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r, p.Leaf + k);
            else if (p.Kind == "list") Emit(Pick(Op.LIST_ST_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r);
            else if (p.Kind == "dict") Emit(Pick(Op.DICT_STV_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r, p.Leaf + k);
            else if (p.Kind == "local") Emit(Op.MOV, p.Obj!.Regs[p.Leaf + k], r);
        }
    }

    private Val LoadPlace(Place p, Val? dest)
    {
        var leaves = _m.Leaves(p.Type);
        if (p.Kind == "local" && dest == null)
        {
            var sub = new List<int>();
            var objRegs = p.Obj!.Regs;
            for (var j = p.Leaf; j < objRegs.Count && j < p.Leaf + leaves.Count; j++) sub.Add(objRegs[j]);
            return new Val(p.Type, sub);
        }
        Val d;
        if (dest != null) d = dest;
        else d = Temp(p.Type);
        for (var k = 0; k < leaves.Count; k++)
        {
            var r = d.Regs[k];
            var kind = leaves[k].Kind;
            if (p.Kind == "static") EmitImm(Op.LDS, r, p.Slot + p.Leaf + k);
            else if (p.Kind == "field") Emit(Pick(Op.LDF_I32, kind), r, p.Obj!.Reg, p.Leaf + k);
            else if (p.Kind == "elem" && p.Owner!.Elem!.IsAggregate)
                Emit(Pick(Op.LDEF_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg, p.Leaf + k);
            else if (p.Kind == "elem") Emit(Pick(Op.LDE_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg);
            else if (p.Kind == "list" && p.Owner!.Elem!.IsAggregate)
                Emit(Pick(Op.LIST_LDF_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg, p.Leaf + k);
            else if (p.Kind == "list") Emit(Pick(Op.LIST_LD_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg);
            else if (p.Kind == "dict") Emit(Pick(Op.DICT_LDV_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg, p.Leaf + k);
            else if (p.Kind == "local") Emit(Op.MOV, r, p.Obj!.Regs[p.Leaf + k]);
        }
        return d;
    }

    private void Store(Val dest, Val src)
    {
        var s = Convert(src, dest.Type);
        for (var k = 0; k < dest.Regs.Count; k++)
            if (dest.Regs[k] != s.Regs[k])
                Emit(Op.MOV, dest.Regs[k], s.Regs[k]);
    }

    private Val Store2(Val dest, Val src)
    {
        Store(dest, src);
        return dest;
    }

    private void EmitZero(Val v)
    {
        var leaves = _m.Leaves(v.Type);
        for (var k = 0; k < leaves.Count; k++)
            if (leaves[k].Kind == Kind.Ref) Emit(Op.LDNULL, v.Regs[k]);
            else EmitImm(Op.LDK, v.Regs[k], 0);
    }

    // ---- 型 ----

    private VType TypeOf(IlExpr e)
    {
        var refEq = RefEq(e);
        if (refEq != null) return TypeOf(refEq);
        if (e is IlLit lit) return LitType(lit);
        if (e is IlVar v)
        {
            var vr = Lookup(v.Name);
            if (vr == null)
            {
                CFail.Report($"{_f.Name}: unknown name {v.Name}");
                return VType.Int;
            }
            return vr.Type;
        }
        if (e is IlParen p) return TypeOf(p.E);
        if (e is IlField hf)
        {
            var hv = HostName(hf);
            if (hv != null)
            {
                var fv = _m.ForeignValue(hv);
                if (fv != null) return fv.HasConstant ? VType.Int : _m.Parse(fv.Type);
            }
        }
        if (e is IlField rf && rf.Recv is IlVar rv && rv.Name == "TinySystem.Random" && rf.Name == "Shared") return VType.Random();
        if (e is IlField mf && mf.Recv is IlVar mc && (mc.Name == "TinySystem.Math" || mc.Name == "Math" || mc.Name == "MathF")
            && (mf.Name == "PI" || mf.Name == "E" || mf.Name == "Tau") && Lookup(mc.Name) == null) return VType.Float;
        if (e is IlField ef && ef.Recv is IlVar en && Lookup(en.Name) == null && _m.IsEnum(en.Name)) return VType.Int;
        if (e is IlField sf && sf.Recv is IlVar cls && Lookup(cls.Name) == null && _m.IsTypeName(cls.Name))
        {
            _m.Static(cls.Name, sf.Name, out VType stype);
            return stype;
        }
        if (e is IlField f) return _m.FieldType(TypeOf(f.Recv), f.Name);
        if (e is IlIndex i)
        {
            var it = TypeOf(i.Recv);
            if (it.Tag == VT.Array) return it.Elem!;
            if (it.Tag == VT.List) return it.Elem!;
            if (it.Tag == VT.Dict) return it.Value!;
            CFail.Report($"{_f.Name}: index of {it.Display()}");
            return VType.Int;
        }
        if (e is IlLen) return VType.Int;
        if (e is IlBin b) return BinType(b);
        if (e is IlUn u)
        {
            if (u.Op == IlUnOp.Not) return VType.Bool;
            if (u.Op == IlUnOp.ToFloat) return VType.Float;
            return TypeOf(u.E);
        }
        if (e is IlCall c) return CallType(c);
        if (e is IlInvoke inv)
        {
            var rt = TypeOf(inv.Recv);
            if ((inv.Method == "NextFloat" || inv.Method == "NextSingle") && rt.Tag == VT.Random) return VType.Float;
            if (rt.Tag == VT.Random) return VType.Int;
            if (rt.Tag == VT.HostIface)
            {
                var fm = _m.ForeignInterfaceMethod(rt.Name, inv.Method);
                if (fm == null)
                {
                    CFail.Report($"{_f.Name}: no host interface method {rt.Name}.{inv.Method}");
                    return VType.Int;
                }
                return _m.Parse(fm.ReturnType);
            }
            if (rt.Tag == VT.Class && _m.Classes.TryGetValue(rt.Name, out ExClass? hostClass) && hostClass.IsExternal)
            {
                var hm = _m.ForeignInterfaceMethod(rt.Name, inv.Method);
                if (hm == null)
                {
                    CFail.Report($"{_f.Name}: no host method {rt.Name}.{inv.Method}");
                    return VType.Int;
                }
                return _m.Parse(hm.ReturnType);
            }
            return _m.Parse(InstanceMethod(rt, inv.Method).ReturnType);
        }
        if (e is IlDynCall d)
        {
            var sr = StaticRef(d.Callee, out ExMethod? srMethod);
            if (sr != null) return _m.Parse(srMethod!.ReturnType);
            var hn = HostName(d.Callee);
            if (hn != null)
            {
                var hm = _m.ForeignMethod(hn);
                if (hm != null) return _m.Parse(hm.ReturnType);
            }
            var ft = TypeOf(d.Callee);
            if (ft.Tag == VT.Func) return ft.Ret!;
            CFail.Report($"{_f.Name}: call of non-delegate");
            return VType.Int;
        }
        if (e is IlNewObj n) return _m.ParseTypeRef(n.TypeName);
        if (e is IlNewQueue q) return _m.Parse(q.Type);
        if (e is IlNewHashSet h) return _m.Parse(h.Type);
        if (e is IlNewArray a) return VType.OfArray(_m.Parse(a.ElementType));
        if (e is IlTable t) return TableType(t);
        if (e is IlNumericConvert nc) return _m.Parse(nc.TargetType);
        if (e is IlStructCopy sc) return TypeOf(sc.E);
        if (e is IlTernary tern) return TernaryType(TypeOf(tern.T), TypeOf(tern.F));
        if (e is IlIife iife) return IifeType(iife);
        if (e is IlIsType || e is IlIsPrimType || e is IlNullableHasValue) return VType.Bool;
        if (e is IlCast cast) return _m.ParseTypeRef(cast.TypeRef);
        if (e is IlRefCast rc) return _m.Parse(rc.TargetType);
        if (e is IlNullableWrap w) return VType.OfNullable(_m.Parse(w.Type));
        if (e is IlNullableValue nv)
        {
            var nt = TypeOf(nv.E);
            return nt.Tag == VT.Nullable ? nt.Inner! : nt;
        }
        if (e is IlNullableGetOrDefault g)
        {
            var gd = TypeOf(g.Default);
            if (gd.Tag == VT.Nullable || gd.Tag == VT.Null)
            {
                var ge = TypeOf(g.E);
                if (ge.Tag == VT.Nullable && gd.Tag == VT.Null) return ge;
                return gd.Tag == VT.Null ? ge : gd;
            }
            return gd;
        }
        if (e is IlLiftedBin lb) return LiftedType(lb);
        if (e is IlLiftedUn lu)
        {
            if (lu.Op == IlUnOp.ToFloat) return VType.OfNullable(VType.Float);
            return TypeOf(lu.E);
        }
        if (e is IlWith wi) return TypeOf(wi.Src);
        CFail.Report($"{_f.Name}: expression {IlNodes.Name(e)} is not in the VM yet");
        return VType.Int;
    }

    /// <summary>`c ? a : b` の型。片方が null ならもう片方、参照型どうしで違えば object (C# の target-typed な条件式で、
    /// 代入先が object / interface のときだけ通る)。</summary>
    private static VType TernaryType(VType t, VType f)
    {
        if (t.Tag == VT.Null) return f;
        if (f.Tag == VT.Null || VType.Same(t, f) || !t.IsRef || !f.IsRef) return t;
        return VType.Object;
    }

    private VType TableType(IlTable t)
    {
        if (t.ElementType == null && t.ObjectType != null)
        {
            var host = _m.Parse(t.ObjectType);
            if (host.Tag == VT.Class) return host; // host の data class の record
            if (host.Tag == VT.Struct) return host; // --ref の struct
        }
        if (t.ElementType == null)
        {
            CFail.Report($"{_f.Name}: table without element type");
            return VType.Int;
        }
        var elem = _m.Parse(t.ElementType);
        if (t.KeyType != null) return VType.OfDict(_m.Parse(t.KeyType), elem);
        return t.IsArray ? VType.OfArray(elem) : VType.OfList(elem);
    }

    private static VType LitType(IlLit lit)
    {
        if (lit.Kind == IlLitKind.Float) return VType.Float;
        if (lit.Kind == IlLitKind.Int) return VType.Int;
        if (lit.Kind == IlLitKind.Bool) return VType.Bool;
        if (lit.Kind == IlLitKind.String) return VType.String;
        return VType.Null;
    }

    private VType BinType(IlBin b)
    {
        var op = b.Op;
        if (op == IlBinOp.Eq || op == IlBinOp.Ne || op == IlBinOp.Lt || op == IlBinOp.Le || op == IlBinOp.Gt || op == IlBinOp.Ge)
            return VType.Bool;
        // IL の `a or b` は、ref の値なら null の合体 (`??`)
        if (op == IlBinOp.And || op == IlBinOp.Or)
        {
            var l = TypeOf(b.L);
            return l.IsRef ? l : VType.Bool;
        }
        if (op == IlBinOp.BitAnd || op == IlBinOp.BitOr || op == IlBinOp.BitXor || op == IlBinOp.Shl || op == IlBinOp.Shr)
            return TypeOf(b.L);
        if (op == IlBinOp.DivNum) return VType.Float;
        if (op == IlBinOp.Concat) return VType.String;
        return VType.Same(TypeOf(b.L), VType.Float) || VType.Same(TypeOf(b.R), VType.Float) ? VType.Float : VType.Int;
    }

    private VType CallType(IlCall c)
    {
        var callee = c.Callee;
        if (callee.StartsWith("TinySystem.Dict.") || callee.StartsWith("TinySystem.Math."))
            return CallType(new IlCall(Strings.From(callee, ContractFacts.Len("TinySystem.")), c.Args, c.Origin));
        if (callee == "List.Remove") return VType.Bool;
        if (callee == "__tcs_irem" || callee == "__tcs_idiv" || callee == "__tcs_trunc" || callee == "__tcs_cadd"
            || callee == "__tcs_csub" || callee == "__tcs_cmul" || callee == "__tcs_cneg" || callee == "Math.CompareTo"
            || callee == "Dict.Count") return VType.Int;
        if (callee == "__tcs_str" || callee == "__tcs_fstr" || callee == "__tcs_bstr" || callee == "__tcs_nstr") return VType.String;
        if (callee == "__tcs_writeline" || callee == "List.Add") return VType.Void;
        if (callee == "List.Sort" && c.Args.Count == 2) return VType.Void;
        if (callee == "Dict.ContainsKey") return VType.Bool;
        if (callee.EndsWith(".op_Equality")) return VType.Bool;
        var sm = StructMember(callee, out ExMethod? smMethod, out ExCtor? smCtor);
        if (sm != null) return smCtor != null ? VType.OfStruct(sm) : _m.Parse(smMethod!.ReturnType);
        var lt = LibraryType(c);
        if (lt != null) return lt;
        StaticMethod(callee, out ExMethod method);
        return _m.Parse(method.ReturnType);
    }

    private string StaticMethod(string callee, out ExMethod method)
    {
        method = new ExMethod();
        var u = new Utf8Bytes(callee);
        var dot = -1;
        for (var i = 0; i < u.Length; i++)
        {
            if (u[i] == '.') dot = i;
        }
        if (dot < 0)
        {
            CFail.Report($"{_f.Name}: call '{callee}' is not in the VM yet");
            return "";
        }
        var cls = ContractFacts.Sub(callee, 0, dot);
        var name = Strings.From(callee, dot + 1);
        if (!_m.Classes.TryGetValue(cls, out ExClass? info))
        {
            CFail.Report($"{_f.Name}: call '{callee}' is not in the VM yet");
            return "";
        }
        var found = P1SingleMethod(info.Methods, name, 1);
        if (found == null) found = P1SingleMethod(info.Methods, name, 0);
        if (found == null)
        {
            CFail.Report($"{_f.Name}: no static method {callee}");
            return "";
        }
        method = found;
        return cls;
    }

    private ExMethod InstanceMethod(VType recv, string name)
    {
        if (recv.Tag == VT.Iface)
        {
            var im = P1SingleMethod(P1ClassMethods(recv.Name), name, 0);
            if (im == null)
            {
                CFail.Report($"{_f.Name}: no method {recv.Name}.{name}");
                return new ExMethod();
            }
            return im;
        }
        if (recv.Tag != VT.Class)
        {
            CFail.Report($"{_f.Name}: method {name} on {recv.Display()} is not in the VM yet");
            return new ExMethod();
        }
        var cm = P1SingleMethod(P1ClassMethods(recv.Name), name, 2);
        if (cm == null)
        {
            CFail.Report($"{_f.Name}: no method {recv.Name}.{name}");
            return new ExMethod();
        }
        return cm;
    }

    // C# 版の `_m.Classes[name].Methods` (無い key は KeyNotFoundException)
    private List<ExMethod> P1ClassMethods(string cls)
    {
        if (!_m.Classes.TryGetValue(cls, out ExClass? info))
        {
            CFail.Report($"The given key '{cls}' was not present in the dictionary.");
            return new List<ExMethod>();
        }
        return info.Methods;
    }

    // C# 版の Methods.SingleOrDefault(m => m.Name == name [&& m.IsStatic | && !m.IsStatic])。
    // staticMode: 0 は static を問わない、1 は static だけ、2 は instance だけ。2 つ以上あれば C# と同じく誤り
    private static ExMethod? P1SingleMethod(List<ExMethod> methods, string name, int staticMode)
    {
        ExMethod? found = null;
        foreach (var m in methods)
        {
            if (m.Name != name) continue;
            if (staticMode == 1 && !m.IsStatic) continue;
            if (staticMode == 2 && m.IsStatic) continue;
            if (found != null)
            {
                CFail.Report("Sequence contains more than one matching element");
                return found;
            }
            found = m;
        }
        return found;
    }

    // ---- 式 ----

    /// <summary>型 to の値として評価する。null の literal、lambda、method group は型 to から作る。</summary>
    private Val ExprAs(IlExpr e, VType to, Val? dest = null)
    {
        while (e is IlParen p) e = p.E;
        Val v;
        if (IlLiterals.IsNull(e) && to.Tag == VT.Nullable)
        {
            v = dest != null ? dest : Temp(to);
            EmitZero(v);
            return v;
        }
        if (IlLiterals.IsNull(e))
        {
            if (!to.IsRef)
            {
                CFail.Report(_f.Name + ": null as " + to.Display());
                return BadVal();
            }
            v = dest != null ? dest : Temp(to);
            Emit(Op.LDNULL, v.Reg);
            return v;
        }
        if (e is IlClosure c)
        {
            if (to.Tag != VT.Func)
            {
                CFail.Report(_f.Name + ": lambda as " + to.Display());
                return BadVal();
            }
            return Closure(c, to, dest);
        }
        if (e is IlMethodGroup g)
        {
            if (to.Tag != VT.Func)
            {
                CFail.Report(_f.Name + ": method group as " + to.Display());
                return BadVal();
            }
            return MethodGroup(g, to, dest);
        }
        if (dest != null && VType.Same(TypeOf(e), to)) return Expr(e, dest, false);
        v = Convert(Expr(e, null, false), to);
        if (dest == null) return v;
        Store(dest, v);
        return dest;
    }

    private Val Convert(Val v, VType to)
    {
        if (VType.Same(v.Type, to) || to.Tag == VT.Void) return v;
        if (VType.Same(v.Type, VType.Int) && VType.Same(to, VType.Float))
        {
            var t = Temp(VType.Float);
            Emit(Op.I2F, t.Reg, v.Reg);
            return t;
        }
        if (v.Type.Tag == VT.Null && to.IsRef)
        {
            var t = Temp(to);
            Emit(Op.LDNULL, t.Reg);
            return t;
        }
        if (v.Type.Tag == VT.Null && to.Tag == VT.Nullable)
            return ZeroTemp(to);
        if (v.Type.Tag == VT.Nullable && VType.Same(v.Type.Inner, to))
            return new Val(to, P2Regs.From(v.Regs, 1)); // pattern の変数へ (`some is int x` は値があると確かめた後で x = some)
        if (v.Type.Tag == VT.Nullable && to.Tag == VT.Nullable && VType.Same(v.Type.Inner, VType.Int)
            && VType.Same(to.Inner, VType.Float))
        {
            var t = Temp(to);
            Emit(Op.MOV, t.Regs[0], v.Regs[0]);
            Emit(Op.I2F, t.Regs[1], v.Regs[1]);
            return t;
        }
        if (to.Tag == VT.Nullable && !v.Type.IsRef && v.Type.Tag != VT.Nullable)
        {
            // T → T? (il-spec §3 の IlNullableWrap と同じ)
            var inner = Convert(v, to.Inner!);
            var t = Temp(to);
            EmitImm(Op.LDK, t.Regs[0], 1);
            for (var k = 0; k < inner.Regs.Count; k++) Emit(Op.MOV, t.Regs[k + 1], inner.Regs[k]);
            return t;
        }
        if (to.Tag == VT.Object && v.Type.Tag == VT.Prim)
        {
            // 値型を object に入れる (box)
            var box = VType.OfBox(v.Type.PKind);
            var t = Temp(to);
            Emit(Pick(Op.BOX_I32, v.Type.PKind), t.Reg, v.Reg, _m.TypeIndex(box));
            return t;
        }
        if (to.Tag == VT.Array && v.Type.Tag == VT.List && VType.Same(v.Type.Elem, to.Elem))
        {
            // List → 配列 (runtime が List で返す String.Split などを string[] に入れる)。要素を写す
            var lt = v.Type;
            var at = to;
            var arr = Temp(to);
            var n = Temp(VType.Int);
            var i = Temp(VType.Int);
            Emit(Op.LIST_LEN, n.Reg, v.Reg);
            Emit(Op.NEWARR, arr.Reg, n.Reg, _m.TypeIndex(to));
            EmitImm(Op.LDK, i.Reg, 0);
            var top = new Label();
            var exit = new Label();
            Mark(top);
            Jump(Op.JF_LT_I32, exit, i.Reg, n.Reg);
            var item = LoadPlace(Place.Make(lt.Elem!, "list", 0, null, v, i, 0, lt), null);
            StorePlace(Place.Make(at.Elem!, "elem", 0, null, arr, i, 0, at), item);
            Emit(Op.ADDI_I32, i.Reg, i.Reg, 1);
            Jump(Op.JMP, top);
            Mark(exit);
            return arr;
        }
        if (to.IsRef && v.Type.IsRef)
        {
            var t = Temp(to);
            if (to.Tag == VT.Object || (to.Tag == VT.Iface && v.Type.Tag == VT.Class) || to.Tag == VT.HostIface
                || v.Type.Tag == VT.HostIface && to.Tag == VT.Object)
                Emit(Op.MOV, t.Reg, v.Reg); // 広げる変換は検査が通す
            else
                // 狭める暗黙の変換は pattern の local への代入 (`q = s; q is Sq`)。合わなければ null にして型を保つ
                Emit(Op.ASCAST, t.Reg, v.Reg, _m.TypeIndex(to));
            return t;
        }
        CFail.Report(_f.Name + ": cannot convert " + v.Type.Display() + " to " + to.Display());
        return BadVal();
    }

    private Val Expr(IlExpr e, Val? dest = null, bool discard = false)
    {
        // 誤りを報告した後は形 (葉の数) が当てにならないので、続きを作らない (誤りは呼び出し側が出す)
        if (CFail.Failed) return dest != null ? dest : BadVal();
        var refEq = RefEq(e);
        if (refEq != null) return Expr(refEq, dest, discard);
        if (e is IlParen p)
            return Expr(p.E, dest, discard);
        if (e is IlLit lit)
        {
            var type = LitType(lit);
            if (type.Tag == VT.Null)
            {
                CFail.Report(_f.Name + ": null literal without a type");
                return BadVal();
            }
            var d = dest != null ? dest : Temp(type);
            if (type.Tag == VT.String) EmitImm(Op.LDSTR, d.Reg, _m.Const(VBytes.Utf8(lit.S)));
            else EmitImm(Op.LDK, d.Reg, LitBits(lit, type));
            return d;
        }
        if (e is IlVar iv)
        {
            var vr = Lookup(iv.Name);
            if (vr == null)
            {
                CFail.Report(_f.Name + ": unknown name " + iv.Name);
                return BadVal();
            }
            if (vr.K == VarKind.Reg)
            {
                if (dest == null) return vr.Val!;
                Store(dest, vr.Val!);
                return dest;
            }
            return LoadPlace(PlaceOf(e), dest);
        }
        if (e is IlField hf)
        {
            var hv = HostName(hf);
            if (hv != null)
            {
                var fv = _m.ForeignValue(hv);
                if (fv != null)
                {
                    // host の値: enum の定数は literal、ほかは引数の無い host の関数 (getter) で読む
                    if (fv.HasConstant)
                    {
                        var d0 = dest != null ? dest : Temp(VType.Int);
                        EmitImm(Op.LDK, d0.Reg, fv.Constant);
                        return d0;
                    }
                    var getter = new ExForeignMethod();
                    getter.Name = hv;
                    getter.ReturnType = fv.Type;
                    return HostCall(hv, getter, new List<IlExpr>(), dest);
                }
            }
        }
        if (e is IlField rf && rf.Recv is IlVar rv && rv.Name == "TinySystem.Random" && rf.Name == "Shared")
            return SharedRandom(dest);
        if (e is IlNewQueue nq)
        {
            var t = _m.Parse(nq.Type);
            var d = dest != null ? dest : Temp(t);
            Emit(Op.NEWLIST, d.Reg, 0, _m.TypeIndex(t));
            return d;
        }
        if (e is IlNewHashSet nh)
        {
            var t = _m.Parse(nh.Type);
            var d = dest != null ? dest : Temp(t);
            Emit(Op.NEWDICT, d.Reg, 0, _m.TypeIndex(t));
            return d;
        }
        if (e is IlField mf && mf.Recv is IlVar mc
            && (mc.Name == "TinySystem.Math" || mc.Name == "Math" || mc.Name == "MathF")
            && (mf.Name == "PI" || mf.Name == "E" || mf.Name == "Tau")
            && Lookup(mc.Name) == null)
        {
            var d = dest != null ? dest : Temp(VType.Float);
            // MathF.PI / MathF.E / MathF.Tau の binary32 の bit の並び (C# 版の BitConverter.SingleToUInt32Bits)
            int value;
            if (mf.Name == "PI") value = 0x40490FDB;
            else if (mf.Name == "E") value = 0x402DF854;
            else value = 0x40C90FDB;
            EmitImm(Op.LDK, d.Reg, value);
            return d;
        }
        if (e is IlField ef && ef.Recv is IlVar en && Lookup(en.Name) == null && _m.IsEnum(en.Name))
        {
            var d = dest != null ? dest : Temp(VType.Int);
            EmitImm(Op.LDK, d.Reg, _m.EnumValue(en.Name, ef.Name));
            return d;
        }
        if (e is IlField || e is IlIndex)
            return LoadPlace(PlaceOf(e), dest);
        if (e is IlLen len)
        {
            var t = TypeOf(len.E);
            Op op;
            if (t.Tag == VT.Array) op = Op.ARR_LEN;
            else if (t.Tag == VT.List) op = Op.LIST_LEN;
            else if (t.Tag == VT.Dict) op = Op.DICT_LEN;
            else
            {
                CFail.Report(_f.Name + ": length of " + t.Display() + " is not in the VM yet");
                return BadVal();
            }
            var coll = Expr(len.E, null, false);
            var d = dest != null ? dest : Temp(VType.Int);
            Emit(op, d.Reg, coll.Reg);
            return d;
        }
        if (e is IlBin b)
            return Bin(b, dest);
        if (e is IlUn u)
            return Un(u, dest);
        if (e is IlCall cl)
            return Call(cl, dest, discard);
        if (e is IlInvoke inv)
            return Invoke(inv, dest);
        if (e is IlDynCall dc)
            return DynCall(dc, dest);
        if (e is IlNewObj n)
            return NewObj(n, dest);
        if (e is IlNewArray a)
        {
            var type = VType.OfArray(_m.Parse(a.ElementType));
            var alen = ExprAs(a.Length, VType.Int, null);
            var d = dest != null ? dest : Temp(type);
            Emit(Op.NEWARR, d.Reg, alen.Reg, _m.TypeIndex(type));
            return d;
        }
        if (e is IlTable tb)
            return Table(tb, TableType(tb), dest);
        if (e is IlTernary tn)
        {
            var type = TypeOf(tn);
            var d = dest != null ? dest : Temp(type);
            var no = new Label();
            var end = new Label();
            Cond(tn.Cond, false, no);
            ExprAs(tn.T, type, d);
            Jump(Op.JMP, end);
            Mark(no);
            ExprAs(tn.F, type, d);
            Mark(end);
            return d;
        }
        if (e is IlStructCopy sc)
            // register は値なので、copy 地点 (il-spec §10) は評価そのもの。`__copy` 関数は要らない
            return Expr(sc.E, dest, false);
        if (e is IlNumericConvert nc)
        {
            var to = _m.Parse(nc.TargetType);
            var v = Expr(nc.Value, null, false);
            if (v.Type.IsRef && to.Tag == VT.Prim)
            {
                // object からの unbox (実行時の型が違えば fault)
                var ud = dest != null ? dest : Temp(to);
                Emit(Pick(Op.UNBOX_I32, to.PKind), ud.Reg, v.Reg, _m.TypeIndex(VType.OfBox(to.PKind)));
                return ud;
            }
            if (v.Type.Tag == VT.Nullable && to.Tag == VT.Nullable)
            {
                // int? → float? (値があれば変換)
                var nd = dest != null ? dest : Temp(to);
                var none = new Label();
                var end = new Label();
                Jump(Op.JF, none, v.Regs[0]);
                var conv = Convert(new Val(v.Type.Inner!, P2Regs.From(v.Regs, 1)), to.Inner!);
                EmitImm(Op.LDK, nd.Regs[0], 1);
                for (var k = 0; k < conv.Regs.Count; k++) Emit(Op.MOV, nd.Regs[k + 1], conv.Regs[k]);
                Jump(Op.JMP, end);
                Mark(none);
                EmitZero(nd);
                Mark(end);
                return nd;
            }
            if (v.Type.Tag == VT.Nullable && to.Tag != VT.Nullable)
            {
                // (T)T? は .Value と同じ (null なら fault)。中身を目的の型に変換する
                Emit(Op.NVAL, v.Regs[0]);
                v = new Val(v.Type.Inner!, P2Regs.From(v.Regs, 1));
            }
            if (VType.Same(v.Type, to))
            {
                if (dest == null) return v;
                Store(dest, v);
                return dest;
            }
            var d = dest != null ? dest : Temp(to);
            if (VType.Same(v.Type, VType.Int) && VType.Same(to, VType.Float)) Emit(Op.I2F, d.Reg, v.Reg);
            else if (VType.Same(v.Type, VType.Float) && VType.Same(to, VType.Int)) Emit(Op.F2I, d.Reg, v.Reg);
            else
            {
                CFail.Report(_f.Name + ": conversion " + v.Type.Display() + " -> " + to.Display() + " is not in the VM yet");
                return BadVal();
            }
            return d;
        }
        if (e is IlIife pi && pi.Stats.Count == 2 && pi.Stats[0] is IlAssign pa && pa.Target is IlVar pv
            && pi.Stats[1] is IlReturn pr && pr.Value is IlIsPrimType pt && pt.E is IlVar tv
            && pv.Name == tv.Name && TypeOf(pa.Value).Tag == VT.Nullable)
        {
            var plv = Lookup(pv.Name);
            if (plv == null || plv.Type.Tag != VT.Nullable)
            {
                // `n is int x` で n が int?: 値があるときだけ x に入れ、結果は値の有無
                var nv0 = Expr(pa.Value, null, false);
                var d = dest != null ? dest : Temp(VType.Bool);
                var skip = new Label();
                Emit(Op.MOV, d.Reg, nv0.Regs[0]);
                Jump(Op.JF, skip, d.Reg);
                AssignTarget(pv, Convert(nv0, Lookup(pv.Name)!.Type), false);
                Mark(skip);
                return d;
            }
        }
        if (e is IlIife iife)
            return Iife(iife, dest);
        if (e is IlIsType it)
        {
            var v = Expr(it.E, null, false);
            var d = dest != null ? dest : Temp(VType.Bool);
            if (!v.Type.IsRef)
            {
                EmitImm(Op.LDK, d.Reg, 0); // 値型は class / interface ではない (TCS1001 が外す形の残り)
                return d;
            }
            Emit(Op.ISTYPE, d.Reg, v.Reg, _m.TypeIndex(_m.ParseTypeRef(it.TypeRef)));
            return d;
        }
        if (e is IlIsPrimType vlt && (TypeOf(vlt.E).Tag == VT.Nullable || TypeOf(vlt.E).Tag == VT.Prim))
        {
            // 値型の `x is int` (x が int? なら値の有無、int なら常に true)
            var v = Expr(vlt.E, null, false);
            var d = dest != null ? dest : Temp(VType.Bool);
            if (v.Type.Tag == VT.Nullable) Emit(Op.MOV, d.Reg, v.Regs[0]);
            else EmitImm(Op.LDK, d.Reg, 1);
            return d;
        }
        if (e is IlIsPrimType ln && ln.Type == IlPrimType.Number)
        {
            // object の `is int` / `is float` は IL では number (int と float の箱のどちらか)
            var v = Expr(ln.E, null, false);
            var d = dest != null ? dest : Temp(VType.Bool);
            var end = new Label();
            Emit(Op.ISTYPE, d.Reg, v.Reg, _m.TypeIndex(VType.OfBox(Kind.I32)));
            Jump(Op.JT, end, d.Reg);
            Emit(Op.ISTYPE, d.Reg, v.Reg, _m.TypeIndex(VType.OfBox(Kind.F32)));
            Mark(end);
            return d;
        }
        if (e is IlIsPrimType lt)
        {
            var v = Expr(lt.E, null, false);
            var d = dest != null ? dest : Temp(VType.Bool);
            VType target = VType.String;
            if (lt.Type == IlPrimType.Bool) target = VType.OfBox(Kind.Bool);
            else if (lt.Type == IlPrimType.Int) target = VType.OfBox(Kind.I32);
            else if (lt.Type == IlPrimType.Float) target = VType.OfBox(Kind.F32);
            Emit(Op.ISTYPE, d.Reg, v.Reg, _m.TypeIndex(target));
            return d;
        }
        if (e is IlCast ct)
        {
            var to = _m.ParseTypeRef(ct.TypeRef);
            var v = Expr(ct.E, null, false);
            var d = dest != null ? dest : Temp(to);
            Emit(Op.CAST, d.Reg, v.Reg, _m.TypeIndex(to));
            return d;
        }
        if (e is IlRefCast rc)
        {
            var to = _m.Parse(rc.TargetType);
            if (P2Regs.IsNil(rc.Value)) return ExprAs(rc.Value, to, dest);
            var v = Expr(rc.Value, null, false);
            if (!v.Type.IsRef && v.Type.Tag != VT.Null)
            {
                // 値型の cast (`(S?)x` で x が S? / S) は変換だけ
                var cv = Convert(v, to);
                if (dest == null) return cv;
                return Store2(dest, cv);
            }
            if (to.Tag == VT.Prim)
            {
                var d0 = dest != null ? dest : Temp(to);
                Emit(Pick(Op.UNBOX_I32, to.PKind), d0.Reg, v.Reg, _m.TypeIndex(VType.OfBox(to.PKind)));
                return d0;
            }
            var d = dest != null ? dest : Temp(to);
            Emit(Op.CAST, d.Reg, v.Reg, _m.TypeIndex(to));
            return d;
        }
        if (e is IlNullableWrap w)
        {
            var inner = _m.Parse(w.Type);
            var v = ExprAs(w.E, inner, null);
            var d = dest != null ? dest : Temp(VType.OfNullable(inner));
            EmitImm(Op.LDK, d.Regs[0], 1);
            for (var k = 0; k < v.Regs.Count; k++)
                if (d.Regs[k + 1] != v.Regs[k]) Emit(Op.MOV, d.Regs[k + 1], v.Regs[k]);
            return d;
        }
        if (e is IlNullableHasValue nhv)
        {
            var v = Expr(nhv.E, null, false);
            var d = dest != null ? dest : Temp(VType.Bool);
            if (v.Type.IsRef)
            {
                // ref の null の判定
                var no = new Label();
                var end = new Label();
                Jump(Op.JNULL, no, v.Reg);
                EmitImm(Op.LDK, d.Reg, 1);
                Jump(Op.JMP, end);
                Mark(no);
                EmitImm(Op.LDK, d.Reg, 0);
                Mark(end);
                return d;
            }
            Emit(Op.MOV, d.Reg, v.Regs[0]);
            return d;
        }
        if (e is IlNullableValue nv)
        {
            var v = Expr(nv.E, null, false);
            if (v.Type.Tag != VT.Nullable)
            {
                if (dest == null) return v;
                return Store2(dest, v);
            }
            Emit(Op.NVAL, v.Regs[0]);
            var inner = new Val(v.Type.Inner!, P2Regs.From(v.Regs, 1));
            if (dest == null) return inner;
            return Store2(dest, inner);
        }
        if (e is IlNullableGetOrDefault g)
            return NullableOr(g, dest);
        if (e is IlLiftedBin lb)
            return LiftedBin(lb, dest);
        if (e is IlLiftedUn lu)
            return LiftedUn(lu, dest);
        if (e is IlWith wi)
            return With(wi, dest);
        if (e is IlClosure || e is IlMethodGroup)
        {
            CFail.Report(_f.Name + ": delegate without a target type");
            return BadVal();
        }
        CFail.Report(_f.Name + ": expression " + IlNodes.Name(e) + " is not in the VM yet");
        return BadVal();
    }

    // literal の値の 32 bit (type は式の静的型。float の位置の整数の literal は float の値にする)
    private static int LitBits(IlLit lit, VType type)
    {
        if (VType.Same(type, VType.Float))
            return TcsC.Constants.FloatBits(lit.Kind == IlLitKind.Float ? lit.F : (float)lit.I);
        return lit.I;
    }

    private Val Bin(IlBin b, Val? dest)
    {
        var resultType = BinType(b);
        if ((b.Op == IlBinOp.Or || b.Op == IlBinOp.And) && resultType.IsRef)
        {
            if (b.Op == IlBinOp.And)
            {
                CFail.Report(_f.Name + ": `and` on references");
                return BadVal();
            }
            // null の合体: 左が null でなければ左、null なら右 (右は必要なときだけ評価)
            var d = dest != null ? dest : Temp(resultType);
            var end = new Label();
            ExprAs(b.L, resultType, d);
            Jump(Op.JNN, end, d.Reg);
            ExprAs(b.R, resultType, d);
            Mark(end);
            return d;
        }
        if ((b.Op == IlBinOp.Eq || b.Op == IlBinOp.Ne) && (TypeOf(b.L).Tag == VT.Nullable || TypeOf(b.R).Tag == VT.Nullable))
        {
            // T? と null の比較は値の有無、T? どうしは lifted の等価 (il-spec §3)
            var nullR = P2Regs.IsNil(b.R);
            var nullL = P2Regs.IsNil(b.L);
            if (nullR || nullL)
            {
                var v = Expr(nullR ? b.L : b.R, null, false);
                var d0 = dest != null ? dest : Temp(VType.Bool);
                if (b.Op == IlBinOp.Eq) Emit(Op.NOT_B, d0.Reg, v.Regs[0]);
                else Emit(Op.MOV, d0.Reg, v.Regs[0]);
                return d0;
            }
            return LiftedBin(new IlLiftedBin(b.Op == IlBinOp.Eq ? IlLiftedOp.Eq : IlLiftedOp.Ne, b.L, b.R, null), dest);
        }
        if (b.Op == IlBinOp.Concat)
        {
            var l = ToStr(b.L);
            var r = ToStr(b.R);
            var d = dest != null ? dest : Temp(VType.String);
            Emit(Op.CONCAT, d.Reg, l.Reg, r.Reg);
            return d;
        }
        if (b.Op == IlBinOp.Eq || b.Op == IlBinOp.Ne || b.Op == IlBinOp.Lt || b.Op == IlBinOp.Le || b.Op == IlBinOp.Gt
            || b.Op == IlBinOp.Ge || b.Op == IlBinOp.And || b.Op == IlBinOp.Or)
        {
            // bool の値: 比較は EQ / LT / LE、論理は分岐で作る
            var d = dest != null ? dest : Temp(VType.Bool);
            if (b.Op == IlBinOp.And || b.Op == IlBinOp.Or)
            {
                var no = new Label();
                var end = new Label();
                Cond(b, false, no);
                EmitImm(Op.LDK, d.Reg, 1);
                Jump(Op.JMP, end);
                Mark(no);
                EmitImm(Op.LDK, d.Reg, 0);
                Mark(end);
                return d;
            }
            var l = Operands(b, null, out Val r, out VType t);
            var op = Compare(b.Op, t, out bool swap, out bool negate);
            Emit(op, d.Reg, swap ? r.Reg : l.Reg, swap ? l.Reg : r.Reg);
            if (negate) Emit(Op.NOT_B, d.Reg, d.Reg);
            return d;
        }
        var al = Operands(b, resultType, out Val ar, out VType at);
        var ad = dest != null ? dest : Temp(resultType);
        var fl = VType.Same(at, VType.Float);
        Op aop;
        if (b.Op == IlBinOp.AddNum && !fl) aop = Op.ADD_I32;
        else if (b.Op == IlBinOp.Sub && !fl) aop = Op.SUB_I32;
        else if (b.Op == IlBinOp.Mul && !fl) aop = Op.MUL_I32;
        else if (b.Op == IlBinOp.BitAnd && !fl) aop = Op.AND_I32;
        else if (b.Op == IlBinOp.BitOr && !fl) aop = Op.OR_I32;
        else if (b.Op == IlBinOp.BitXor && !fl) aop = Op.XOR_I32;
        else if (b.Op == IlBinOp.Shl && !fl) aop = Op.SHL_I32;
        else if (b.Op == IlBinOp.Shr && !fl) aop = Op.SHR_I32;
        else if (b.Op == IlBinOp.AddNum && fl) aop = Op.ADD_F32;
        else if (b.Op == IlBinOp.Sub && fl) aop = Op.SUB_F32;
        else if (b.Op == IlBinOp.Mul && fl) aop = Op.MUL_F32;
        else if (b.Op == IlBinOp.DivNum && fl) aop = Op.DIV_F32;
        else
        {
            CFail.Report(_f.Name + ": operator " + IlNodes.IlBinOpName(b.Op) + " on " + at.Display() + " is not in the VM yet");
            return BadVal();
        }
        Emit(aop, ad.Reg, al.Reg, ar.Reg);
        return ad;
    }

    /// <summary>文字列化 (il-spec §13): int は 10 進、float は runtime/fmt、bool は True / False、string の null は空文字列。</summary>
    /// <summary>IL の string.format (il-spec §13): 書式の literal を compile 時に文字列の部分と変換に分け、変換ごとに
    /// runtime の fmt.i / fmt.f / fmt.s (変換 1 つの spec と値) を呼んで連結する。意味は tcs2c の tcs_string_format と同じ。</summary>
    private Val Format(List<int> format, List<IlExpr> args, Val? dest)
    {
        var parts = new List<Val>();
        var text = new List<int>();
        var next = 0;
        var i = 0;
        while (i < format.Count)
        {
            if (format[i] != '%')
            {
                text.Add(format[i]);
                i++;
                continue;
            }
            if (i + 1 < format.Count && format[i + 1] == '%')
            {
                text.Add(37); // '%'
                i += 2;
                continue;
            }
            var start = i;
            i++;
            while (i < format.Count && P2Regs.IsFormatFlag(format[i])) i++;
            while (i < format.Count && format[i] >= '0' && format[i] <= '9') i++;
            if (i < format.Count && format[i] == '.')
            {
                i++;
                while (i < format.Count && format[i] >= '0' && format[i] <= '9') i++;
            }
            if (i >= format.Count)
            {
                CFail.Report(_f.Name + ": invalid format string");
                return BadVal();
            }
            var conv = format[i];
            i++;
            var spec = P2Regs.Slice(format, start, i);
            if (next >= args.Count)
            {
                CFail.Report(_f.Name + ": string.format has no value for a conversion");
                return BadVal();
            }
            var arg = args[next];
            next++;
            FormatFlush(parts, text);
            if (conv == 's')
            {
                var sv = ToStr(arg);
                if (spec.Count == 2)
                {
                    parts.Add(sv);
                    continue;
                }
                parts.Add(FormatCall("fmt.s", spec, sv));
                continue;
            }
            var t = TypeOf(arg);
            var isFloat = _m.Info(t).Kind == Kind.F32;
            if (!isFloat && _m.Info(t).Kind != Kind.I32)
            {
                CFail.Report(_f.Name + ": string.format %" + ((char)conv).ToString() + " of " + t.Display());
                return BadVal();
            }
            parts.Add(FormatCall(isFloat ? "fmt.f" : "fmt.i", spec, ExprAs(arg, isFloat ? VType.Float : VType.Int, null)));
        }
        FormatFlush(parts, text);
        if (parts.Count == 0)
        {
            var e = dest != null ? dest : Temp(VType.String);
            EmitImm(Op.LDSTR, e.Reg, _m.Const(new List<int>()));
            return e;
        }
        var acc = parts[0];
        for (var k = 1; k < parts.Count; k++)
        {
            Val d;
            if (k == parts.Count - 1 && dest != null) d = dest;
            else d = Temp(VType.String);
            Emit(Op.CONCAT, d.Reg, acc.Reg, parts[k].Reg);
            acc = d;
        }
        if (dest != null && acc.Reg != dest.Reg) Emit(Op.MOV, dest.Reg, acc.Reg);
        if (dest != null) return dest;
        return acc;
    }

    // C# 版の Format の local 関数 Flush: 貯めた文字列の部分を 1 つの値にする
    private void FormatFlush(List<Val> parts, List<int> text)
    {
        if (text.Count == 0) return;
        var t = Temp(VType.String);
        var copy = new List<int>();
        foreach (var x in text) copy.Add(x);
        EmitImm(Op.LDSTR, t.Reg, _m.Const(copy));
        parts.Add(t);
        text.Clear();
    }

    private Val FormatCall(string intrinsic, List<int> spec, Val value)
    {
        var types = new List<VType>();
        types.Add(value.Type);
        types.Add(VType.String);
        var b = Block(types);
        if (b != value.Reg) Emit(Op.MOV, b, value.Reg);
        EmitImm(Op.LDSTR, b + 1, _m.Const(spec));
        var d = Temp(VType.String);
        Emit(Op.CALLR, b, d.Reg, _m.Intrinsic(intrinsic));
        return d;
    }

    private Val ToStr(IlExpr e)
    {
        while (e is IlParen p) e = p.E;
        if (e is IlCall c && (c.Callee == "__tcs_str" || c.Callee == "__tcs_fstr" || c.Callee == "__tcs_bstr" || c.Callee == "__tcs_nstr")
            && c.Args.Count == 1) e = c.Args[0];
        var t = TypeOf(e);
        if (t.Tag == VT.String || t.Tag == VT.Null) return ExprAs(e, VType.String, null);
        if (t.Tag == VT.Nullable)
        {
            // T? の値なしは空文字列 (C# の補間と同じ)
            var nv = Expr(e, null, false);
            var d0 = Temp(VType.String);
            var none = new Label();
            var end = new Label();
            Jump(Op.JF, none, nv.Regs[0]);
            Store(d0, ToStrVal(new Val(t.Inner!, P2Regs.From(nv.Regs, 1))));
            Jump(Op.JMP, end);
            Mark(none);
            EmitImm(Op.LDSTR, d0.Reg, _m.Const(new List<int>()));
            Mark(end);
            return d0;
        }
        if (t.Tag == VT.Object || t.Tag == VT.Iface || t.Tag == VT.Box)
        {
            var ov = Expr(e, null, false);
            var d1 = Temp(VType.String);
            Emit(Op.CALLR, ov.Reg, d1.Reg, _m.Intrinsic("object.tostring"));
            return d1;
        }
        if (t.Tag == VT.Class)
        {
            ExMethod? ts = null;
            foreach (var m in _m.Classes[t.Name].Methods)
            {
                if (m.Name == "ToString" && !m.IsStatic)
                {
                    ts = m;
                    break;
                }
            }
            if (ts != null)
            {
                // ToString の override
                return EmitCall(Op.CALL, RequestMethod(t.Name, ts), e, ts, new List<IlExpr>(), null);
            }
        }
        return ToStrVal(Expr(e, null, false));
    }

    private Val ToStrVal(Val v)
    {
        var t = v.Type;
        if (t.Tag == VT.String) return v;
        var d = Temp(VType.String);
        if (VType.Same(t, VType.Int)) Emit(Op.I2S, d.Reg, v.Reg);
        else if (VType.Same(t, VType.Float)) Emit(Op.F2S, d.Reg, v.Reg);
        else if (VType.Same(t, VType.Bool)) Emit(Op.B2S, d.Reg, v.Reg);
        else
        {
            CFail.Report(_f.Name + ": ToString of " + t.Display() + " is not in the VM yet");
            return BadVal();
        }
        return d;
    }

    /// <summary>二項の operand を評価し、int と float が混ざれば int を float に揃える (C# の暗黙変換)。</summary>
    private Val Operands(IlBin b, VType? want, out Val r, out VType t)
    {
        var lt = TypeOf(b.L);
        var rt = TypeOf(b.R);
        VType tt;
        if (VType.Same(want, VType.Float) || VType.Same(lt, VType.Float) || VType.Same(rt, VType.Float)) tt = VType.Float;
        else if (lt.Tag == VT.Null) tt = rt;
        else tt = lt;
        if (b.Op == IlBinOp.Shl || b.Op == IlBinOp.Shr) tt = VType.Int;
        if (tt.Tag == VT.Null)
        {
            CFail.Report(_f.Name + ": null compared with null");
            r = BadVal();
            t = VType.Int;
            return BadVal();
        }
        var l = ExprAs(b.L, tt, null);
        r = ExprAs(b.R, b.Op == IlBinOp.Shl || b.Op == IlBinOp.Shr ? VType.Int : tt, null);
        t = tt;
        return l;
    }

    private Op Compare(IlBinOp op, VType t, out bool swap, out bool negate)
    {
        var cmp = CompareStatic(_m, op, t, out bool cs, out bool cn);
        swap = cs;
        negate = cn;
        return cmp;
    }

    private static Op CompareStatic(ModuleBuilder m, IlBinOp op, VType t, out bool swap, out bool negate)
    {
        swap = false;
        negate = false;
        if (VType.Same(t, VType.Int))
        {
            if (op == IlBinOp.Eq) return Op.EQ_I32;
            if (op == IlBinOp.Ne)
            {
                negate = true;
                return Op.EQ_I32;
            }
            if (op == IlBinOp.Lt) return Op.LT_I32;
            if (op == IlBinOp.Le) return Op.LE_I32;
            swap = true;
            if (op == IlBinOp.Gt) return Op.LT_I32;
            return Op.LE_I32;
        }
        if (VType.Same(t, VType.Float))
        {
            if (op == IlBinOp.Eq) return Op.EQ_F32;
            if (op == IlBinOp.Ne)
            {
                negate = true;
                return Op.EQ_F32;
            }
            if (op == IlBinOp.Lt) return Op.LT_F32;
            if (op == IlBinOp.Le) return Op.LE_F32;
            swap = true;
            if (op == IlBinOp.Gt) return Op.LT_F32;
            return Op.LE_F32;
        }
        var eqOrNe = op == IlBinOp.Eq || op == IlBinOp.Ne;
        if (VType.Same(t, VType.Bool) && eqOrNe)
        {
            negate = op == IlBinOp.Ne;
            return Op.EQ_B;
        }
        if (t.Tag == VT.String && eqOrNe)
        {
            negate = op == IlBinOp.Ne;
            return Op.EQ_STR;
        }
        if (t.Tag == VT.Class && m.Classes[t.Name].IsRecord && eqOrNe)
        {
            negate = op == IlBinOp.Ne;
            return Op.EQ_REC;
        }
        if (t.IsRef && eqOrNe)
        {
            negate = op == IlBinOp.Ne;
            return Op.EQ_REF;
        }
        CFail.Report("comparison " + IlNodes.IlBinOpName(op) + " on " + t.Display() + " is not in the VM yet");
        return Op.EQ_I32;
    }

    /// <summary>条件分岐: cond が jumpIf のとき target へ跳ぶ。比較は融合した分岐命令にする。</summary>
    private void Cond(IlExpr cond, bool jumpIf, Label target)
    {
        var refEq = RefEq(cond);
        if (refEq != null)
        {
            Cond(refEq, jumpIf, target);
            return;
        }
        if (cond is IlParen p)
        {
            Cond(p.E, jumpIf, target);
            return;
        }
        if (cond is IlUn nu && nu.Op == IlUnOp.Not)
        {
            Cond(nu.E, !jumpIf, target);
            return;
        }
        if (cond is IlLit lit && lit.Kind == IlLitKind.Bool)
        {
            if ((lit.I != 0) == jumpIf) Jump(Op.JMP, target);
            return;
        }
        if (cond is IlBin conj && conj.Op == IlBinOp.And && VType.Same(TypeOf(conj.L), VType.Bool))
        {
            if (!jumpIf)
            {
                Cond(conj.L, false, target);
                Cond(conj.R, false, target);
            }
            else
            {
                var skip = new Label();
                Cond(conj.L, false, skip);
                Cond(conj.R, true, target);
                Mark(skip);
            }
            return;
        }
        if (cond is IlBin disj && disj.Op == IlBinOp.Or && VType.Same(TypeOf(disj.L), VType.Bool))
        {
            if (jumpIf)
            {
                Cond(disj.L, true, target);
                Cond(disj.R, true, target);
            }
            else
            {
                var skip = new Label();
                Cond(disj.L, true, skip);
                Cond(disj.R, false, target);
                Mark(skip);
            }
            return;
        }
        if (cond is IlBin nb && (nb.Op == IlBinOp.Eq || nb.Op == IlBinOp.Ne)
            && (TypeOf(nb.L).Tag == VT.Nullable || TypeOf(nb.R).Tag == VT.Nullable))
        {
            // T? の比較は値の bool を作ってから分岐する (Bin が null との比較と lifted の等価を扱う)
            var v = Bin(nb, null);
            Jump(jumpIf ? Op.JT : Op.JF, target, v.Reg);
            return;
        }
        if (cond is IlBin b && (b.Op == IlBinOp.Eq || b.Op == IlBinOp.Ne || b.Op == IlBinOp.Lt || b.Op == IlBinOp.Le
            || b.Op == IlBinOp.Gt || b.Op == IlBinOp.Ge))
        {
            var lt = TypeOf(b.L);
            var rt = TypeOf(b.R);
            if (P2Regs.IsNumber(lt) && P2Regs.IsNumber(rt))
            {
                var l = Operands(b, null, out Val r, out VType t);
                var f = VType.Same(t, VType.Float);
                // a > b は b < a、a >= b は b <= a (NaN でも同じ)。a != b は !(a == b)
                Op op;
                var swap = false;
                var inverted = false;
                if (b.Op == IlBinOp.Lt) op = f ? Op.JT_LT_F32 : Op.JT_LT_I32;
                else if (b.Op == IlBinOp.Le) op = f ? Op.JT_LE_F32 : Op.JT_LE_I32;
                else if (b.Op == IlBinOp.Gt)
                {
                    op = f ? Op.JT_LT_F32 : Op.JT_LT_I32;
                    swap = true;
                }
                else if (b.Op == IlBinOp.Ge)
                {
                    op = f ? Op.JT_LE_F32 : Op.JT_LE_I32;
                    swap = true;
                }
                else if (b.Op == IlBinOp.Eq) op = f ? Op.JT_EQ_F32 : Op.JT_EQ_I32;
                else
                {
                    op = f ? Op.JT_EQ_F32 : Op.JT_EQ_I32;
                    inverted = true;
                }
                // JT_x と JF_x は隣り合う
                var jop = jumpIf != inverted ? op : (Op)((int)op + 1);
                Jump(jop, target, swap ? r.Reg : l.Reg, swap ? l.Reg : r.Reg);
                return;
            }
            // ref と null の比較は JNULL / JNN
            var nullR = P2Regs.IsNil(b.R);
            var nullL = P2Regs.IsNil(b.L);
            if ((b.Op == IlBinOp.Eq || b.Op == IlBinOp.Ne) && nullR != nullL)
            {
                var v = Expr(nullR ? b.L : b.R, null, false);
                var whenNull = b.Op == IlBinOp.Eq;
                Jump(whenNull == jumpIf ? Op.JNULL : Op.JNN, target, v.Reg);
                return;
            }
        }
        var c = ExprAs(cond, VType.Bool, null);
        Jump(jumpIf ? Op.JT : Op.JF, target, c.Reg);
    }

    private Val Un(IlUn u, Val? dest)
    {
        if (u.Op == IlUnOp.Neg)
        {
            // 負の literal は定数にする
            if (u.E is IlLit lit)
            {
                var t = LitType(lit);
                var d0 = dest != null ? dest : Temp(t);
                var bits = LitBits(lit, t);
                EmitImm(Op.LDK, d0.Reg, VType.Same(t, VType.Float) ? bits ^ int.MinValue : -bits);
                return d0;
            }
            var v = Expr(u.E, null, false);
            var d = dest != null ? dest : Temp(v.Type);
            Emit(VType.Same(v.Type, VType.Float) ? Op.NEG_F32 : Op.NEG_I32, d.Reg, v.Reg);
            return d;
        }
        if (u.Op == IlUnOp.Not)
        {
            var v = ExprAs(u.E, VType.Bool, null);
            var d = dest != null ? dest : Temp(VType.Bool);
            Emit(Op.NOT_B, d.Reg, v.Reg);
            return d;
        }
        if (u.Op == IlUnOp.BitNot)
        {
            var v = Expr(u.E, null, false);
            var d = dest != null ? dest : Temp(VType.Int);
            Emit(Op.BNOT_I32, d.Reg, v.Reg);
            return d;
        }
        if (u.Op == IlUnOp.ToFloat)
        {
            var v = Expr(u.E, null, false);
            var d = dest != null ? dest : Temp(VType.Float);
            Emit(Op.I2F, d.Reg, v.Reg);
            return d;
        }
        CFail.Report("unary " + IlNodes.IlUnOpName(u.Op));
        return BadVal();
    }

    // ---- 呼び出し ----

    private Val Call(IlCall c, Val? dest, bool discard)
    {
        // TinySystem.Dict.X / TinySystem.Math.X は Dict.X / Math.X と同じ
        if (c.Callee.StartsWith("TinySystem.Dict.") || c.Callee.StartsWith("TinySystem.Math."))
            return Call(new IlCall(Strings.From(c.Callee, Strings.ByteLength("TinySystem.")), c.Args, c.Origin), dest, discard);
        if (c.Callee == "__tcs_irem" || c.Callee == "__tcs_idiv" || c.Callee == "__tcs_cadd" || c.Callee == "__tcs_csub"
            || c.Callee == "__tcs_cmul")
        {
            var l = ExprAs(c.Args[0], VType.Int, null);
            var r = ExprAs(c.Args[1], VType.Int, null);
            var d = dest != null ? dest : Temp(VType.Int);
            Op op;
            if (c.Callee == "__tcs_irem") op = Op.REM_I32;
            else if (c.Callee == "__tcs_idiv") op = Op.DIV_I32;
            else if (c.Callee == "__tcs_cadd") op = Op.CADD_I32;
            else if (c.Callee == "__tcs_csub") op = Op.CSUB_I32;
            else op = Op.CMUL_I32;
            Emit(op, d.Reg, l.Reg, r.Reg);
            return d;
        }
        if (c.Callee == "__tcs_cneg")
        {
            var v = ExprAs(c.Args[0], VType.Int, null);
            var d = dest != null ? dest : Temp(VType.Int);
            Emit(Op.CNEG_I32, d.Reg, v.Reg);
            return d;
        }
        if (c.Callee == "__tcs_trunc")
        {
            var v = ExprAs(c.Args[0], VType.Float, null);
            var d = dest != null ? dest : Temp(VType.Int);
            Emit(Op.F2I, d.Reg, v.Reg);
            return d;
        }
        if (c.Callee == "Math.CompareTo")
        {
            var lt = TypeOf(c.Args[0]);
            var t = VType.Same(lt, VType.Float) || VType.Same(TypeOf(c.Args[1]), VType.Float) ? VType.Float : lt;
            if (!VType.Same(t, VType.Int) && !VType.Same(t, VType.Float))
            {
                CFail.Report(_f.Name + ": CompareTo on " + t.Display() + " is not in the VM yet");
                return BadVal();
            }
            var l = ExprAs(c.Args[0], t, null);
            var r = ExprAs(c.Args[1], t, null);
            var d = dest != null ? dest : Temp(VType.Int);
            Emit(VType.Same(t, VType.Float) ? Op.CMP_F32 : Op.CMP_I32, d.Reg, l.Reg, r.Reg);
            return d;
        }
        if (c.Callee == "__tcs_str" || c.Callee == "__tcs_fstr" || c.Callee == "__tcs_bstr" || c.Callee == "__tcs_nstr")
        {
            var s = ToStr(c);
            if (dest == null) return s;
            Store(dest, s);
            return dest;
        }
        if (c.Callee == "__tcs_writeline")
        {
            Print(c);
            return new Val(VType.Void, new List<int>());
        }
        if (c.Callee == "List.Add")
        {
            if (c.Args.Count != 2)
            {
                CFail.Report(_f.Name + ": List.Insert is not in the VM yet");
                return BadVal();
            }
            var lt = TypeOf(c.Args[0]);
            if (lt.Tag != VT.List)
            {
                CFail.Report(_f.Name + ": List.Add on non-List");
                return BadVal();
            }
            var list = Expr(c.Args[0], null, false);
            var v = ExprAs(c.Args[1], lt.Elem!, null);
            ListAdd(list, lt, v);
            return new Val(VType.Void, new List<int>());
        }
        if (c.Callee == "List.Sort" && c.Args.Count == 2)
        {
            var lt = TypeOf(c.Args[0]);
            if (lt.Tag != VT.List)
            {
                CFail.Report(_f.Name + ": List.Sort without a comparison is not in the VM yet");
                return BadVal();
            }
            var list = Expr(c.Args[0], null, false);
            var cmpParams = new List<VType>();
            cmpParams.Add(lt.Elem!);
            cmpParams.Add(lt.Elem!);
            var cmp = ExprAs(c.Args[1], VType.OfFunc(cmpParams, VType.Int), null);
            Emit(Op.LIST_SORT, list.Reg, cmp.Reg);
            return new Val(VType.Void, new List<int>());
        }
        if (c.Callee == "Dict.Count")
        {
            if (TypeOf(c.Args[0]).Tag != VT.Dict)
            {
                CFail.Report(_f.Name + ": Count on non-Dictionary");
                return BadVal();
            }
            var d0 = Expr(c.Args[0], null, false);
            var d = dest != null ? dest : Temp(VType.Int);
            Emit(Op.DICT_LEN, d.Reg, d0.Reg);
            return d;
        }
        if (c.Callee == "Dict.ContainsKey")
        {
            var dt = TypeOf(c.Args[0]);
            if (dt.Tag != VT.Dict)
            {
                CFail.Report(_f.Name + ": ContainsKey on non-Dictionary");
                return BadVal();
            }
            var d0 = Expr(c.Args[0], null, false);
            var key = ExprAs(c.Args[1], dt.Key!, null);
            var d = dest != null ? dest : Temp(VType.Bool);
            Emit(Op.DICT_HAS, d.Reg, d0.Reg, key.Reg);
            return d;
        }
        if (c.Callee.EndsWith(".op_Equality") && c.Args.Count >= 2)
        {
            var et = TypeOf(c.Args[0]);
            if (et.IsAggregate) return ValueEq(c.Args[0], c.Args[1], et, dest);
        }
        string? sm = StructMember(c.Callee, out ExMethod? smMethod, out ExCtor? smCtor);
        if (sm != null)
        {
            if (smCtor != null)
                return StructCtorCall(c, sm, smCtor, dest);
            return StructCall(c, sm, smMethod!, null, dest);
        }
        Val? lib = Library(c, dest);
        if (lib != null) return lib;
        string cls = StaticMethod(c.Callee, out ExMethod method);
        var index = RequestMethod(cls, method);
        if (!method.IsStatic)
        {
            // instance method を `C.M(recv, args)` の形で呼ぶ (ToString の override など)
            var rest = new List<IlExpr>();
            for (int i = 1; i < c.Args.Count; i++) rest.Add(c.Args[i]);
            return EmitCall(Op.CALL, index, c.Args[0], method, rest, dest);
        }
        return EmitCall(Op.CALL, index, null, method, c.Args, dest);
    }

    /// <summary>struct / record struct の値の等価 (il-spec §10: 葉ごと。float は Equals と同じく NaN どうしが等しい、
    /// string は内容、record class は field ごと、ほかの ref は identity)。</summary>
    private Val ValueEq(IlExpr a, IlExpr b, VType t, Val? dest)
    {
        var l = ExprAs(a, t, null);
        var r = ExprAs(b, t, null);
        var d = dest != null ? dest : Temp(VType.Bool);
        var no = new Label();
        var end = new Label();
        var leaves = _m.Leaves(t);
        var eq = Temp(VType.Bool);
        for (int k = 0; k < leaves.Count; k++)
        {
            var lt = leaves[k].Type;
            if (VType.Same(lt, VType.Float))
            {
                // a == b || (a != a && b != b)
                var same = new Label();
                Jump(Op.JT_EQ_F32, same, l.Regs[k], r.Regs[k]);
                Jump(Op.JT_EQ_F32, no, l.Regs[k], l.Regs[k]);
                Jump(Op.JT_EQ_F32, no, r.Regs[k], r.Regs[k]);
                Mark(same);
                continue;
            }
            var cmpType = lt.Tag == VT.Class || lt.Tag == VT.Iface || lt.Tag == VT.Object || lt.Tag == VT.Box ? VType.Object : lt;
            Op op = Compare(IlBinOp.Eq, cmpType, out bool swap0, out bool negate0);
            // record と object / interface の葉は実行時の型の値の等価 (EqualityComparer<T>.Default。中の値が record なら field ごと)
            if (lt.Tag == VT.Class && _m.Classes[lt.Name].IsRecord) op = Op.EQ_REC;
            if (lt.Tag == VT.Object || lt.Tag == VT.Iface || lt.Tag == VT.Box) op = Op.EQ_REC;
            Emit(op, eq.Reg, l.Regs[k], r.Regs[k]);
            Jump(Op.JF, no, eq.Reg);
        }
        EmitImm(Op.LDK, d.Reg, 1);
        Jump(Op.JMP, end);
        Mark(no);
        EmitImm(Op.LDK, d.Reg, 0);
        Mark(end);
        return d;
    }

    private string? StructMember(string callee, out ExMethod? method, out ExCtor? ctor)
    {
        method = null;
        ctor = null;
        var u = new Utf8Bytes(callee);
        int dot = -1;
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] == '.') dot = i;
        }
        if (dot < 0) return null;
        string st = Strings.Prefix(callee, dot);
        string name = Strings.From(callee, dot + 1);
        if (!_m.Structs.TryGetValue(st, out ExStruct? info)) return null;
        foreach (var k in info.Ctors)
        {
            if (k.Name == name)
            {
                ctor = k;
                return st;
            }
        }
        foreach (var mi in info.Methods)
        {
            if (mi.Name == name)
            {
                method = mi;
                return st;
            }
        }
        return null;
    }

    private Val StructCtorCall(IlCall c, string st, ExCtor ctor, Val? dest)
    {
        var info = _m.Structs[st];
        var inits = new List<ExField>();
        foreach (var f in info.Fields)
        {
            if (!f.IsStatic && f.Init != null) inits.Add(f);
        }
        var index = _m.Request(st + "." + ctor.Name, def => new FunctionCompiler(_m, def, st).CompileStructCtor(ctor, inits));
        var types = new List<VType>();
        foreach (var p in ctor.ParameterTypes) types.Add(_m.Parse(p));
        var selfT = VType.OfStruct(st);
        var rets = new List<VType>();
        rets.Add(selfT);
        var r = EmitCallTyped(Op.CALL, index, null, types, c.Args, rets);
        return Typed(selfT, r, dest);
    }

    /// <summary>itable の関数: 実装の型が selector の関数型と同じならそのまま、違えば (generic な interface の T を int や
    /// string で実装した) 型を合わせる adapter を作る。</summary>
    private int RequestImpl(string cls, ExMethod method, VType ft)
    {
        if (VType.Same(_m.SelectorType(method), ft)) return RequestMethod(cls, method);
        return _m.Request(cls + "." + method.Name + "<adapter>", def => new FunctionCompiler(_m, def, cls).CompileAdapter(method, ft));
    }

    /// <summary>interface の selector の型で受け、実装の method を呼ぶ (引数は unbox / cast、戻り値は box / 広げる)。</summary>
    public void CompileAdapter(ExMethod impl, VType ft)
    {
        _returnType = ft.Ret!;
        var names = new List<string>();
        for (int i = 0; i < ft.Params.Count; i++) names.Add("__a" + i);
        var implTypes = new List<VType>();
        foreach (var p in impl.ParameterTypes) implTypes.Add(_m.Parse(p));
        var args = new List<IlExpr>();
        args.Add(new IlVar("self", null));
        for (int i = 0; i < names.Count; i++)
        {
            if (!VType.Same(implTypes[i], ft.Params[i]))
                args.Add(new IlRefCast(new IlVar(names[i], null), impl.ParameterTypes[i], null));
            else
                args.Add(new IlVar(names[i], null));
        }
        var invokeArgs = new List<IlExpr>();
        for (int i = 1; i < args.Count; i++) invokeArgs.Add(args[i]);
        IlExpr call = new IlInvoke(new IlVar("self", null), impl.Name, invokeArgs, null);
        IlStat body;
        if (ft.Ret!.Tag == VT.Void) body = new IlCallStat(call, null);
        else body = new IlReturn(call, null);
        var stats = new List<IlStat>();
        stats.Add(body);
        var block = new IlBlock(stats);
        var ps = new List<VType>();
        foreach (var p in ft.Params) ps.Add(p);
        Prologue(block, VType.OfClass(_className), names, ps, null, false);
        Block(block, false);
        Finish();
    }

    private int RequestMethod(string cls, ExMethod method)
    {
        return _m.Request(cls + "." + method.Name, def => new FunctionCompiler(_m, def, cls).CompileMethod(method));
    }

    /// <summary>引数を区画に直接書き、CALL を出す。recv は instance method の self (先頭の引数)。</summary>
    private Val EmitCall(Op op, int index, IlExpr? recv, ExMethod method, List<IlExpr> args, Val? dest)
    {
        var paramTypes = new List<VType>();
        foreach (var p in method.ParameterTypes) paramTypes.Add(_m.Parse(p));
        if (paramTypes.Count != args.Count)
        {
            CFail.Report(_f.Name + ": default arguments are not in the VM yet");
            return BadVal();
        }
        var allE = new List<IlExpr>();
        var allT = new List<VType>();
        if (recv != null)
        {
            allE.Add(recv);
            allT.Add(TypeOf(recv));
        }
        for (int i = 0; i < args.Count; i++)
        {
            allE.Add(args[i]);
            allT.Add(paramTypes[i]);
        }
        var block = Block(LeafTypes(allT));
        var at = block;
        for (int i = 0; i < allE.Count; i++)
        {
            var t = allT[i];
            var n = _m.Leaves(t).Count;
            ExprAs(allE[i], t, new Val(t, P3Regs.Range(at, n)));
            at += n;
        }
        if (recv != null) Emit(Op.NNULL, block); // C# は引数の評価の後で null を見る
        return CallTail(op, block, index, _m.Parse(method.ReturnType), dest);
    }

    private Val CallTail(Op op, int block, int index, VType retType, Val? dest)
    {
        if (retType.Tag == VT.Void)
        {
            Emit(op, block, 0, index);
            return new Val(VType.Void, new List<int>());
        }
        // 戻り値は連続する register に入る。dest が連続ならそこへ直接
        Val ret;
        if (dest != null && VType.Same(dest.Type, retType) && P3Regs.IsContiguous(dest.Regs))
            ret = dest;
        else
        {
            var n = _m.Leaves(retType).Count;
            var one = new List<VType>();
            one.Add(retType);
            var rb = Block(LeafTypes(one));
            ret = new Val(retType, P3Regs.Range(rb, n));
        }
        Emit(op, block, ret.Regs[0], index);
        if (dest != null && dest != ret)
        {
            Store(dest, ret);
            return dest;
        }
        return ret;
    }

    // host の instance method: 受け手を先頭の引数にした host の関数の呼び出し
    private Val HostMethodCall(ExForeignMethod fm, VType recvType, IlInvoke inv, Val? dest)
    {
        var withSelf = new ExForeignMethod();
        withSelf.Name = fm.Name;
        withSelf.ReturnType = fm.ReturnType;
        withSelf.Receiver = fm.Receiver;
        withSelf.MethodName = fm.MethodName;
        withSelf.Parameters = new List<ExForeignParameter>();
        withSelf.Parameters.Add(new ExForeignParameter("self", recvType.Name, false, null));
        foreach (var fp in fm.Parameters) withSelf.Parameters.Add(fp);
        var hostArgs = new List<IlExpr>();
        hostArgs.Add(inv.Recv);
        foreach (var a in inv.Args) hostArgs.Add(a);
        return HostCall(fm.Name, withSelf, hostArgs, dest);
    }

    private Val Invoke(IlInvoke inv, Val? dest)
    {
        var recvType = TypeOf(inv.Recv);
        if (recvType.Tag == VT.Random) return RandomCall(inv, dest);
        if (recvType.Tag == VT.HostIface)
        {
            // --ref の interface の method: C の module の dispatcher を host の関数として呼ぶ (受け手は先頭の引数)
            var fm = _m.ForeignInterfaceMethod(recvType.Name, inv.Method);
            if (fm == null)
            {
                CFail.Report(_f.Name + ": no host interface method " + recvType.Name + "." + inv.Method);
                return BadVal();
            }
            return HostMethodCall(fm, recvType, inv, dest);
        }
        if (recvType.Tag == VT.Class && _m.Classes.TryGetValue(recvType.Name, out ExClass? hostClass) && hostClass.IsExternal)
        {
            // host の data class の instance method (--ref の class): host の関数を呼ぶ (受け手は先頭の引数)
            var fm = _m.ForeignInterfaceMethod(recvType.Name, inv.Method);
            if (fm == null)
            {
                CFail.Report(_f.Name + ": no host method " + recvType.Name + "." + inv.Method);
                return BadVal();
            }
            return HostMethodCall(fm, recvType, inv, dest);
        }
        if (recvType.Tag == VT.Iface)
        {
            ExMethod? im = null;
            int found = 0;
            foreach (var mi in _m.Classes[recvType.Name].Methods)
            {
                if (mi.Name == inv.Method)
                {
                    if (found == 0) im = mi;
                    found++;
                }
            }
            if (found > 1)
            {
                CFail.Report("Sequence contains more than one matching element");
                return BadVal();
            }
            if (im == null)
            {
                CFail.Report(_f.Name + ": no method " + recvType.Name + "." + inv.Method);
                return BadVal();
            }
            var sel = _m.Selector(recvType.Name, im, (c0, m0, t0) => RequestImpl(c0, m0, t0));
            var ft = _m.SelectorType(im);
            var withRecv = new List<VType>();
            withRecv.Add(recvType);
            foreach (var p in ft.Params) withRecv.Add(p);
            var block = Block(LeafTypes(withRecv));
            ExprAs(inv.Recv, recvType, new Val(recvType, P3Regs.Range(block, 1)));
            var at = block + 1;
            for (int i = 0; i < inv.Args.Count; i++)
            {
                var n = _m.Leaves(ft.Params[i]).Count;
                ExprAs(inv.Args[i], ft.Params[i], new Val(ft.Params[i], P3Regs.Range(at, n)));
                at += n;
            }
            return CallTail(Op.CALLI, block, sel, ft.Ret!, dest);
        }
        var method = InstanceMethod(recvType, inv.Method);
        if (recvType.Tag != VT.Class)
        {
            CFail.Report(_f.Name + ": invoke on " + recvType.Display());
            return BadVal();
        }
        var cls = recvType.Name;
        var index = RequestMethod(cls, method);
        return EmitCall(Op.CALL, index, inv.Recv, method, inv.Args, dest);
    }

    /// <summary>`C.M(...)` が IlDynCall(IlField(IlVar(C), M)) の形で来たときの static method (別の file の class の呼び出し)。</summary>
    private string? StaticRef(IlExpr callee, out ExMethod? method)
    {
        method = null;
        IlExpr e = callee;
        while (e is IlParen p) e = p.E;
        if (!(e is IlField f) || !(f.Recv is IlVar cls) || Lookup(cls.Name) != null)
            return null;
        if (_m.IsStruct(cls.Name))
        {
            var st = _m.Structs[_m.CanonName(cls.Name)];
            ExMethod? sm = null;
            int found = 0;
            foreach (var mi in st.Methods)
            {
                if (mi.Name == f.Name && mi.IsStatic)
                {
                    if (found == 0) sm = mi;
                    found++;
                }
            }
            if (found > 1)
            {
                CFail.Report("Sequence contains more than one matching element");
                return null;
            }
            if (sm == null) return null;
            method = sm;
            return _m.CanonName(cls.Name);
        }
        if (!_m.IsClass(cls.Name)) return null;
        var info = _m.Classes[cls.Name];
        foreach (var x in info.Fields)
        {
            if (x.IsStatic && x.Name == f.Name) return null; // delegate 型の static field
        }
        ExMethod? cm = null;
        int count = 0;
        foreach (var mi in info.Methods)
        {
            if (mi.Name == f.Name && mi.IsStatic)
            {
                if (count == 0) cm = mi;
                count++;
            }
        }
        if (count > 1)
        {
            CFail.Report("Sequence contains more than one matching element");
            return null;
        }
        if (cm == null) return null;
        method = cm;
        return cls.Name;
    }

    /// <summary>host (--ref の stub) の名前: `IlField(IlVar("eng.gfx"), "begin")` → "eng.gfx.begin"。local や型の名前なら null。</summary>
    private string? HostName(IlExpr e)
    {
        IlExpr x = e;
        while (x is IlParen p) x = p.E;
        if (!(x is IlField f) || !(f.Recv is IlVar mod) || Lookup(mod.Name) != null || _m.IsTypeName(mod.Name)) return null;
        var name = mod.Name + "." + f.Name;
        return _m.ForeignMethod(name) != null || _m.ForeignValue(name) != null ? name : null;
    }

    /// <summary>host の関数 (doc/vm-design.md §8.5): runtime の関数と同じ CALLR で、名前は host が tvm_host で登録したもの。
    /// register では引数と戻り値を葉に展開する (struct は葉の並び、class / string / List は ref)。
    /// VM が呼ぶときに struct を C の layout の塊に写すので、struct の型は module の型の表に入れる。</summary>
    private Val HostCall(string name, ExForeignMethod m, List<IlExpr> args, Val? dest)
    {
        foreach (var p in m.Parameters)
        {
            if (p.IsOut)
            {
                CFail.Report(_f.Name + ": host function " + name + " with out must be a multiple assignment");
                return BadVal();
            }
        }
        var rets = new List<VType>();
        var ret0 = _m.Parse(m.ReturnType);
        if (ret0.Tag != VT.Void) rets.Add(ret0);
        var r = HostCallLeaves(name, m, args, rets);
        if (ret0.Tag == VT.Void) return new Val(VType.Void, new List<int>());
        var rv = new Val(ret0, r.Regs);
        return dest == null ? rv : Store2(dest, rv);
    }

    /// <summary>host の関数の out (doc/vm-design.md §8.5): `[戻り値?, out...] = host(in...)`。
    /// 結果の register は戻り値の葉と out の葉の並び (host は out を pointer で受け、VM が呼び出しの後にこの register へ書く)。</summary>
    private Val HostCallOuts(string name, ExForeignMethod m, List<IlExpr> args)
    {
        return HostCallLeaves(name, m, args, HostOutTypes(m));
    }

    /// <summary>host の関数の結果の型の列 (戻り値があれば先頭、続けて out の型)。</summary>
    private List<VType> HostOutTypes(ExForeignMethod m)
    {
        var rets = new List<VType>();
        var ret0 = _m.Parse(m.ReturnType);
        if (ret0.Tag != VT.Void) rets.Add(ret0);
        foreach (var p in m.Parameters)
        {
            if (p.IsOut) rets.Add(_m.Parse(p.Type));
        }
        return rets;
    }

    private Val HostCallLeaves(string name, ExForeignMethod m, List<IlExpr> args, List<VType> rets)
    {
        var types = new List<VType>();
        var ins = new List<ExForeignParameter>();
        foreach (var p in m.Parameters)
        {
            if (p.IsOut) continue;
            types.Add(_m.Parse(p.Type));
            ins.Add(p);
        }
        var all = new List<IlExpr>();
        foreach (var a in args) all.Add(a);
        for (int i = all.Count; i < types.Count; i++)
        {
            var dflt = ins[i].Default;
            if (dflt == null)
            {
                CFail.Report(_f.Name + ": " + name + " needs argument " + ins[i].Name);
                return BadVal();
            }
            all.Add(dflt);
        }
        foreach (var t in types)
        {
            if (t.IsAggregate) _m.TypeIndex(t);
        }
        foreach (var t in rets)
        {
            if (t.IsAggregate) _m.TypeIndex(t);
        }
        var block = Block(LeafTypes(types));
        var at = block;
        for (int i = 0; i < types.Count; i++)
        {
            var n = _m.Leaves(types[i]).Count;
            ExprAs(all[i], types[i], new Val(types[i], P3Regs.Range(at, n)));
            at += n;
        }
        var retTypes = LeafTypes(rets);
        var rb = Block(retTypes);
        Emit(Op.CALLR, block, rb, _m.Intrinsic(name));
        return new Val(VType.Void, P3Regs.Range(rb, retTypes.Count));
    }

    private Val DynCall(IlDynCall dc, Val? dest)
    {
        string? hn = HostName(dc.Callee);
        if (hn != null)
        {
            var hm = _m.ForeignMethod(hn);
            if (hm != null) return HostCall(hn, hm, dc.Args, dest);
        }
        string? srClass = StaticRef(dc.Callee, out ExMethod? srMethod);
        if (srClass != null)
        {
            if (_m.IsStruct(srClass))
                return StructCall(new IlCall(srClass + "." + srMethod!.Name, dc.Args, null), srClass, srMethod!, null, dest);
            return EmitCall(Op.CALL, RequestMethod(srClass, srMethod!), null, srMethod!, dc.Args, dest);
        }
        var ft = TypeOf(dc.Callee);
        if (ft.Tag != VT.Func)
        {
            CFail.Report(_f.Name + ": call of non-delegate");
            return BadVal();
        }
        if (ft.Params.Count != dc.Args.Count)
        {
            CFail.Report(_f.Name + ": delegate arity");
            return BadVal();
        }
        var withFn = new List<VType>();
        withFn.Add(ft);
        foreach (var p in ft.Params) withFn.Add(p);
        var block = Block(LeafTypes(withFn));
        ExprAs(dc.Callee, ft, new Val(ft, P3Regs.Range(block, 1)));
        var at = block + 1;
        for (int i = 0; i < dc.Args.Count; i++)
        {
            var n = _m.Leaves(ft.Params[i]).Count;
            ExprAs(dc.Args[i], ft.Params[i], new Val(ft.Params[i], P3Regs.Range(at, n)));
            at += n;
        }
        return CallTail(Op.CALLD, block, _m.TypeIndex(ft), ft.Ret!, dest);
    }

    /// <summary>lambda: 本文を別の関数にし、捕捉する変数の cell を closure に入れる。</summary>
    private Val Closure(IlClosure c, VType ft, Val? dest)
    {
        var free = IlWalk.Free(c);
        // 名前の順 (ordinal) に並べる (安定な挿入 sort)
        var sorted = new List<string>();
        foreach (var name in free)
        {
            int at = sorted.Count;
            while (at > 0 && string.CompareOrdinal(sorted[at - 1], name) > 0) at--;
            sorted.Insert(at, name);
        }
        var capInfo = new List<NamedType>();
        var capVars = new List<Var>();
        foreach (var name in sorted)
        {
            var v = Lookup(name);
            if (v != null)
            {
                if (v.K == VarKind.Reg)
                {
                    CFail.Report(_f.Name + ": captured " + name + " is not in a cell");
                    return BadVal();
                }
                capInfo.Add(new NamedType(name, v.Type));
                capVars.Add(v);
            }
        }
        // 名前は外側の関数、何番目か、本文の指紋 (reload が生きている closure を新版の lambda に対応させる鍵。tcsvm_reload.c)
        var print = VBytes.Sha256Hex8(IlNodes.Dump(c));
        int lambdaNo = _lambdas;
        _lambdas++;
        var lambdaName = _f.Name + "<lambda" + lambdaNo + ":" + print + ">";
        string file = _f.File;
        int line = _lastLine;
        var index = _m.Request(lambdaName,
            def => new FunctionCompiler(_m, def, _className).CompileLambda(c, ft, capInfo, file, line));
        var cellTypes = new List<VType>();
        foreach (var ci in capInfo) cellTypes.Add(VType.OfCell(ci.Type));
        var block = Block(cellTypes);
        for (int i = 0; i < capVars.Count; i++)
            Emit(Op.MOV, block + i, CellReg(capVars[i]));
        var d = dest != null ? dest : Temp(ft);
        Emit(Op.CLOSURE, d.Reg, block, index);
        return d;
    }

    /// <summary>static method の method group (il-spec §10): 捕捉の無い closure で、関数そのものを指す。</summary>
    private Val MethodGroup(IlMethodGroup g, VType ft, Val? dest)
    {
        int index;
        ExMethod method;
        string? memberStruct = StructMember(g.TypeRef + "." + g.Method, out ExMethod? memberMethod, out ExCtor? memberCtor);
        if (memberStruct != null && memberMethod != null && memberMethod.IsStatic)
        {
            // struct の static method (struct の static は class と同じ自由関数)
            string st = memberStruct;
            ExMethod sm = memberMethod;
            method = sm;
            index = _m.Request(st + "." + sm.Name,
                def => new FunctionCompiler(_m, def, st).CompileStructMethod(sm));
        }
        else
        {
            string cls = StaticMethod(g.TypeRef + "." + g.Method, out ExMethod m0);
            method = m0;
            index = RequestMethod(cls, method);
        }
        var paramTypes = new List<VType>();
        foreach (var p in method.ParameterTypes) paramTypes.Add(_m.Parse(p));
        bool sameParams = paramTypes.Count == ft.Params.Count;
        if (sameParams)
        {
            for (int i = 0; i < paramTypes.Count; i++)
            {
                if (!VType.Same(paramTypes[i], ft.Params[i]))
                {
                    sameParams = false;
                    break;
                }
            }
        }
        if (!sameParams || !VType.Same(_m.Parse(method.ReturnType), ft.Ret))
        {
            CFail.Report(_f.Name + ": method group " + g.Method + " with converted signature is not in the VM yet");
            return BadVal();
        }
        var d = dest != null ? dest : Temp(ft);
        Emit(Op.CLOSURE, d.Reg, 0, index);
        return d;
    }

    /// <summary>Random.Shared: 隠れた static。null なら作って時刻から seed する。</summary>
    private Val SharedRandom(Val? dest)
    {
        var slot = _m.SharedRandomSlot();
        var d = dest != null ? dest : Temp(VType.Random());
        var done = new Label();
        EmitImm(Op.LDS, d.Reg, slot);
        Jump(Op.JNN, done, d.Reg);
        Emit(Op.NEW, d.Reg, 0, _m.TypeIndex(VType.Random()));
        Emit(Op.CALLR, d.Reg, 0, _m.Intrinsic("random.autoseed"));
        EmitImm(Op.STS, d.Reg, slot);
        Mark(done);
        return d;
    }

    private Val NewObj(IlNewObj n, Val? dest)
    {
        var type = _m.ParseTypeRef(n.TypeName);
        if (type.Tag == VT.Random)
        {
            var rd = dest != null ? dest : Temp(type);
            Emit(Op.NEW, rd.Reg, 0, _m.TypeIndex(type));
            if (n.Args.Count == 1)
            {
                var bt = new List<VType>();
                bt.Add(type);
                bt.Add(VType.Int);
                var b = Block(bt);
                Emit(Op.MOV, b, rd.Reg);
                ExprAs(n.Args[0], VType.Int, new Val(VType.Int, P3Regs.Range(b + 1, 1)));
                Emit(Op.CALLR, b, 0, _m.Intrinsic("random.seed"));
            }
            else Emit(Op.CALLR, rd.Reg, 0, _m.Intrinsic("random.autoseed"));
            return rd;
        }
        if (type.Tag == VT.Struct)
        {
            if (n.Args.Count != 0)
            {
                CFail.Report(_f.Name + ": struct constructors are not in the VM yet");
                return BadVal();
            }
            var d0 = dest != null ? dest : Temp(type);
            EmitZero(d0);
            return d0;
        }
        if (type.Tag != VT.Class)
        {
            CFail.Report(_f.Name + ": new " + type.Display() + " is not in the VM yet");
            return BadVal();
        }
        string ctName = type.Name;
        var cls = _m.Classes[ctName];
        ExCtor? ctor = null;
        if (cls.Ctors.Count != 0)
        {
            int found = 0;
            foreach (var k in cls.Ctors)
            {
                if (k.Name == n.Ctor)
                {
                    if (found == 0) ctor = k;
                    found++;
                }
            }
            if (found > 1)
            {
                CFail.Report("Sequence contains more than one matching element");
                return BadVal();
            }
        }
        if (ctor == null && cls.Ctors.Count != 0)
        {
            CFail.Report(_f.Name + ": no constructor " + n.Ctor + " in " + ctName);
            return BadVal();
        }
        var inits = new List<ExField>();
        foreach (var f in cls.Fields)
        {
            if (!f.IsStatic && f.Init != null) inits.Add(f);
        }
        var d = dest != null ? dest : Temp(type);
        if (ctor == null && inits.Count == 0)
        {
            if (n.Args.Count != 0)
            {
                CFail.Report(_f.Name + ": arguments without a constructor");
                return BadVal();
            }
            Emit(Op.NEW, d.Reg, 0, _m.TypeIndex(type));
            return d;
        }
        // 引数を評価してから NEW し、その object を先頭の引数にして constructor を呼ぶ
        var paramTypes = new List<VType>();
        if (ctor != null)
        {
            foreach (var p in ctor.ParameterTypes) paramTypes.Add(_m.Parse(p));
        }
        if (paramTypes.Count != n.Args.Count)
        {
            CFail.Report(_f.Name + ": default arguments are not in the VM yet");
            return BadVal();
        }
        var withObj = new List<VType>();
        withObj.Add(type);
        foreach (var p in paramTypes) withObj.Add(p);
        var block = Block(LeafTypes(withObj));
        var at = block + 1;
        for (int i = 0; i < n.Args.Count; i++)
        {
            var k = _m.Leaves(paramTypes[i]).Count;
            ExprAs(n.Args[i], paramTypes[i], new Val(paramTypes[i], P3Regs.Range(at, k)));
            at += k;
        }
        Emit(Op.NEW, block, 0, _m.TypeIndex(type));
        var ctorName = ctName + "." + (ctor != null ? ctor.Name : "new");
        var index = _m.Request(ctorName, def => new FunctionCompiler(_m, def, ctName).CompileCtor(ctor, inits));
        Emit(Op.CALL, block, 0, index);
        Store(d, new Val(type, P3Regs.Range(block, 1)));
        return d;
    }

    private Val Table(IlTable t, VType type, Val? dest)
    {
        if (type.Tag == VT.Struct && t.ObjectType != null) return StructTable(t, type, dest);
        var d = dest != null ? dest : Temp(type);
        if (type.Tag == VT.Dict)
        {
            Emit(Op.NEWDICT, d.Reg, 0, _m.TypeIndex(type));
            foreach (var e in t.Entries)
            {
                if (e.Key == null)
                {
                    CFail.Report(_f.Name + ": dictionary entry without a key");
                    return BadVal();
                }
                var key = ExprAs(e.Key, type.Key!, null);
                var v = ExprAs(e.Value, type.Value!, null);
                var slotAt = Temp(VType.Int);
                Emit(Op.DICT_INS, slotAt.Reg, d.Reg, key.Reg);
                StorePlace(Place.Make(type.Value!, "dict", 0, null, d, slotAt, 0, null), v);
            }
            return d;
        }
        if (type.Tag == VT.List)
        {
            Emit(Op.NEWLIST, d.Reg, 0, _m.TypeIndex(type));
            foreach (var e in t.Entries) ListAdd(d, type, ExprAs(e.Value, type.Elem!, null));
            return d;
        }
        if (type.Tag == VT.Class)
        {
            if (_m.Classes.TryGetValue(type.Name, out ExClass? ci) && ci.IsExternal)
            {
                // host の data class (--ref の stub の record): 名前つきの entry を field に書き、無い field は initializer
                Emit(Op.NEW, d.Reg, 0, _m.TypeIndex(type));
                foreach (var field in ci.Fields)
                {
                    if (field.IsStatic) continue;
                    IlTableEntry? entry = null;
                    foreach (var e in t.Entries)
                    {
                        if (e.NameKey == field.Name)
                        {
                            entry = e;
                            break;
                        }
                    }
                    IlExpr? value = entry != null && entry.NameKey != null ? entry.Value : field.Init;
                    if (value == null) continue;
                    var ft = _m.Parse(field.Type);
                    var v = ExprAs(value, ft, null);
                    StorePlace(Place.Make(ft, "field", 0, d, null, null, LeafIndex(_m.ObjectLeaves(type), field.Name), null), v);
                }
                return d;
            }
        }
        if (type.Tag == VT.Array)
        {
            var len = Temp(VType.Int);
            EmitImm(Op.LDK, len.Reg, t.Entries.Count);
            Emit(Op.NEWARR, d.Reg, len.Reg, _m.TypeIndex(type));
            for (int i = 0; i < t.Entries.Count; i++)
            {
                var idx = Temp(VType.Int);
                EmitImm(Op.LDK, idx.Reg, i);
                var v = ExprAs(t.Entries[i].Value, type.Elem!, null);
                StorePlace(Place.Make(type.Elem!, "elem", 0, null, d, idx, 0, type), v);
            }
            return d;
        }
        CFail.Report(_f.Name + ": table of " + type.Display());
        return BadVal();
    }

    // --ref の struct の object initializer: 0 の値に、書いた順に名前の field を入れる (右辺は代入先を読めるので一時の値に作る)
    private Val StructTable(IlTable t, VType type, Val? dest)
    {
        var tmp = Temp(type);
        EmitZero(tmp);
        var leaves = _m.Leaves(type);
        foreach (var e in t.Entries)
        {
            if (e.NameKey == null)
            {
                CFail.Report(_f.Name + ": struct initializer entry without a name");
                return BadVal();
            }
            var ft = _m.FieldType(type, e.NameKey);
            var n = _m.Leaves(ft).Count;
            ExprAs(e.Value, ft, new Val(ft, P5Regs.Slice(tmp.Regs, LeafIndex(leaves, e.NameKey), n)));
        }
        return dest == null ? tmp : Store2(dest, tmp);
    }

    // ---- TinySystem の関数 (il-spec §13): runtime の関数 (CALLR) か専用の命令 ----

    private static string LibName(string callee)
    {
        if (callee.StartsWith("TinySystem.Math.") || callee.StartsWith("TinySystem.List.") || callee.StartsWith("TinySystem.String.")
            || callee.StartsWith("TinySystem.Dict."))
            return Strings.From(callee, 11);
        if (callee.StartsWith("MathF.")) return "Math." + Strings.From(callee, 6);
        return callee;
    }

    // lambda と method group は代入先の型で決まるので、ここでは型を問わない (C# 版の LibPlan の局所関数 A)
    private VType LibPlanArg(IlCall c, int i)
    {
        if (i >= c.Args.Count) return VType.Void;
        var a = c.Args[i];
        if (a is IlClosure || a is IlMethodGroup) return VType.Void;
        if (a is IlField af && (af.Name == "__copy" || af.Name == "op_Equality")) return VType.Void;
        return TypeOf(a);
    }

    private LibCall? LibPlan(IlCall c)
    {
        var name = LibName(c.Callee);
        var n = c.Args.Count;
        var f = false;
        for (int i = 0; i < n; i++)
        {
            if (VType.Same(LibPlanArg(c, i), VType.Float))
            {
                f = true;
                break;
            }
        }
        var num = f ? VType.Float : VType.Int;
        var sfx = f ? ".f" : ".i";
        var S = VType.String;
        var I = VType.Int;
        var F = VType.Float;
        var none = new List<VType>();
        if (name == "Math.Min" || name == "Math.Max")
            return new LibCall("math." + Strings.From(name, 5).ToLower() + sfx, P4Lists.Types2(num, num), P4Lists.Types1(num));
        if (name == "Math.Abs") return new LibCall("math.abs" + sfx, P4Lists.Types1(num), P4Lists.Types1(num));
        if (name == "Math.Sign") return new LibCall("math.sign" + sfx, P4Lists.Types1(num), P4Lists.Types1(I));
        if (name == "Math.Clamp") return new LibCall("math.clamp" + sfx, P4Lists.Types3(num, num, num), P4Lists.Types1(num));
        if (name == "Math.Floor" || name == "Math.Ceiling" || name == "Math.Ceil" || name == "Math.Sqrt" || name == "Math.Sin"
            || name == "Math.Cos" || name == "Math.Tan" || name == "Math.Exp")
            return new LibCall("math." + (name == "Math.Ceil" ? "ceiling" : Strings.From(name, 5).ToLower()), P4Lists.Types1(F), P4Lists.Types1(F));
        if (name == "Math.Log")
        {
            if (n == 1) return new LibCall("math.log", P4Lists.Types1(F), P4Lists.Types1(F));
            return new LibCall("math.logb", P4Lists.Types2(F, F), P4Lists.Types1(F));
        }
        if (name == "Math.Atan2" || name == "Math.Pow")
            return new LibCall("math." + Strings.From(name, 5).ToLower(), P4Lists.Types2(F, F), P4Lists.Types1(F));
        if (name == "Math.Round")
        {
            if (n == 1) return new LibCall("math.round", P4Lists.Types1(F), P4Lists.Types1(F));
            return new LibCall("math.round2", P4Lists.Types2(F, I), P4Lists.Types1(F));
        }
        if (name == "String.FormatFloat") return new LibCall("string.formatfloat", P4Lists.Types2(F, S), P4Lists.Types1(S));
        if (name == "String.FormatInt") return new LibCall("string.formatint", P4Lists.Types2(I, S), P4Lists.Types1(S));
        if (name == "String.FormatEnum") return new LibCall("string.formatenum", P4Lists.Types2(I, S), P4Lists.Types1(S));
        if (name == "String.Trim") return new LibCall("string.trim", P4Lists.Types1(S), P4Lists.Types1(S));
        if (name == "String.ToUpper") return new LibCall("string.toupper", P4Lists.Types1(S), P4Lists.Types1(S));
        if (name == "String.ToLower") return new LibCall("string.tolower", P4Lists.Types1(S), P4Lists.Types1(S));
        if (name == "String.Replace") return new LibCall("string.replace", P4Lists.Types3(S, S, S), P4Lists.Types1(S));
        if (name == "String.Contains") return new LibCall("string.contains", P4Lists.Types2(S, S), P4Lists.Types1(VType.Bool));
        if (name == "String.StartsWith") return new LibCall("string.startswith", P4Lists.Types2(S, S), P4Lists.Types1(VType.Bool));
        if (name == "String.EndsWith") return new LibCall("string.endswith", P4Lists.Types2(S, S), P4Lists.Types1(VType.Bool));
        if (name == "String.IsNullOrEmpty") return new LibCall("string.isnullorempty", P4Lists.Types1(S), P4Lists.Types1(VType.Bool));
        if (name == "String.CompareOrdinal") return new LibCall("string.compareordinal", P4Lists.Types2(S, S), P4Lists.Types1(I));
        if (name == "String.Join" && n == 2)
        {
            var jl = LibPlanArg(c, 1);
            if (jl.Tag == VT.List) return new LibCall("string.join", P4Lists.Types2(S, jl), P4Lists.Types1(S));
        }
        if (name == "String.Split" && n == 1) return new LibCall("string.splitws", P4Lists.Types1(S), none, VType.OfList(S));
        if (name == "String.Split" && n == 2) return new LibCall("string.split", P4Lists.Types2(S, S), none, VType.OfList(S));
        if (name == "Char.ConvertFromUtf32") return new LibCall("char.fromutf32", P4Lists.Types1(I), P4Lists.Types1(S));
        if (name == "Char.IsDigit" || name == "Char.IsUpper" || name == "Char.IsLower" || name == "Char.IsLetter" || name == "Char.IsLetterOrDigit"
            || name == "Char.IsWhiteSpace")
            return new LibCall("char." + Strings.From(name, 5).ToLower(), P4Lists.Types1(I), P4Lists.Types1(VType.Bool));
        if (name == "Char.ToUpper" || name == "Char.ToLower")
            return new LibCall("char." + Strings.From(name, 5).ToLower(), P4Lists.Types1(I), P4Lists.Types1(I));
        if (name == "__tcs_upper") return new LibCall("string.toupper", P4Lists.Types1(S), P4Lists.Types1(S));
        if (name == "__tcs_lower") return new LibCall("string.tolower", P4Lists.Types1(S), P4Lists.Types1(S));
        if (name == "String.JoinEnum" && n >= 3)
        {
            var el = LibPlanArg(c, 1);
            if (el.Tag == VT.List) return new LibCall("string.joinenum", P4Lists.Types3(S, el, S), P4Lists.Types1(S));
        }
        if (name == "__tcs_fparse") return new LibCall("float.parse", P4Lists.Types1(S), P4Lists.Types1(F));
        if (name == "__tcs_iparse") return new LibCall("int.parse", P4Lists.Types1(S), P4Lists.Types1(I));
        if (name == "__tcs_char") return new LibCall("char.cast", P4Lists.Types1(I), P4Lists.Types1(I));
        if (name == "__tcs_write") return new LibCall("io.write", P4Lists.Types1(S), none);
        if (name == "__tcs_getenv") return new LibCall("os.getenv", P4Lists.Types1(S), P4Lists.Types1(S));
        if (name == "TinySystem.Utf8.RuneCount") return new LibCall("utf8.runecount", P4Lists.Types1(S), P4Lists.Types1(I));
        if (name == "TinySystem.Utf8.RuneSubstring") return new LibCall("utf8.runesubstring", P4Lists.Types3(S, I, I), P4Lists.Types1(S));
        if (name == "TinySystem.Utf8.At") return new LibCall("utf8.at", P4Lists.Types2(VType.Utf8(), I), P4Lists.Types1(I));
        if (name == "TinySystem.Utf8.Slice") return new LibCall("utf8.slice", P4Lists.Types3(VType.Utf8(), I, I), P4Lists.Types1(VType.Utf8()));
        if (name == "TinySystem.Utf8.ToString") return new LibCall("utf8.tostring", P4Lists.Types1(VType.Utf8()), P4Lists.Types1(S));
        if (name == "List.GetRange")
        {
            var gl = LibPlanArg(c, 0);
            if (gl.Tag == VT.List) return new LibCall("list.getrange", P4Lists.Types3(gl, I, I), P4Lists.Types1(gl));
        }
        if (name == "List.Copy")
        {
            var cl = LibPlanArg(c, 0);
            if (cl.Tag == VT.List) return new LibCall("list.copy", P4Lists.Types1(cl), P4Lists.Types1(cl));
        }
        if (name == "List.IndexOf")
        {
            var il = LibPlanArg(c, 0);
            if (il.Tag == VT.List && !il.Elem!.IsAggregate) return new LibCall("list.indexof", P4Lists.Types2(il, il.Elem!), P4Lists.Types1(I));
        }
        if (name == "List.Contains")
        {
            var ol = LibPlanArg(c, 0);
            if (ol.Tag == VT.List && !ol.Elem!.IsAggregate)
                return new LibCall("list.contains", P4Lists.Types2(ol, ol.Elem!), P4Lists.Types1(VType.Bool));
        }
        if (name == "List.Remove")
        {
            var rl = LibPlanArg(c, 0);
            if (rl.Tag == VT.List && !rl.Elem!.IsAggregate)
                return new LibCall("list.remove", P4Lists.Types2(rl, rl.Elem!), P4Lists.Types1(VType.Bool));
        }
        if (name == "List.FindIndex")
        {
            var fl = LibPlanArg(c, 0);
            if (fl.Tag == VT.List)
                return new LibCall("list.findindex", P4Lists.Types2(fl, VType.OfFunc(P4Lists.Types1(fl.Elem!), VType.Bool)), P4Lists.Types1(I));
        }
        if (name == "List.RemoveAll")
        {
            var al = LibPlanArg(c, 0);
            if (al.Tag == VT.List)
                return new LibCall("list.removeall", P4Lists.Types2(al, VType.OfFunc(P4Lists.Types1(al.Elem!), VType.Bool)), P4Lists.Types1(I));
        }
        if (name == "List.Reverse")
        {
            var vl = LibPlanArg(c, 0);
            if (vl.Tag == VT.List) return new LibCall("list.reverse", P4Lists.Types1(vl), none);
        }
        if (name == "List.Sort" && n == 1)
        {
            var sl = LibPlanArg(c, 0);
            if (sl.Tag == VT.List) return new LibCall("list.sort0", P4Lists.Types1(sl), none);
        }
        return null;
    }

    private VType? LibraryType(IlCall c)
    {
        var callee = c.Callee;
        if (callee == "__tcs_cstr" || callee == "__tcs_format") return VType.String;
        if (callee == "__tcs_iparse") return VType.Int;
        if (callee == "TinySystem.Random.Seed" || callee == "TcsQueue.Enqueue" || callee == "TcsQueue.Clear" || callee == "TcsHashSet.Clear")
            return VType.Void;
        if (callee == "TcsQueue.Dequeue" || callee == "TcsQueue.Peek")
        {
            var qt = TypeOf(c.Args[0]);
            if (qt.Tag == VT.List) return qt.Elem!;
        }
        if (callee == "TcsQueue.Count" || callee == "TcsHashSet.Count") return VType.Int;
        if (callee == "TcsQueue.ToList") return TypeOf(c.Args[0]);
        if (callee == "TcsQueue.Contains" || callee == "TcsHashSet.Add" || callee == "TcsHashSet.Remove" || callee == "TcsHashSet.Contains")
            return VType.Bool;
        if (callee == "TcsHashSet.ToList")
        {
            var hs = TypeOf(c.Args[0]);
            if (hs.Tag == VT.Dict) return VType.OfList(hs.Key!);
        }
        if (callee == "__tcs_fmod") return VType.Float;
        if (callee == "List.Insert" || callee == "List.RemoveAt" || callee == "Dict.Remove") return callee == "Dict.Remove" ? VType.Bool : VType.Void;
        if (callee == "TinySystem.Utf8.New" || callee == "TinySystem.Utf8.Default") return VType.Utf8();
        if (callee == "Dict.Keys")
        {
            var dk = TypeOf(c.Args[0]);
            if (dk.Tag == VT.Dict) return VType.OfList(dk.Key!);
        }
        if (callee == "Dict.Values")
        {
            var dv = TypeOf(c.Args[0]);
            if (dv.Tag == VT.Dict) return VType.OfList(dv.Value!);
        }
        var ln = LibName(callee);
        if (ln == "List.Copy")
        {
            var ca = TypeOf(c.Args[0]);
            if (ca.Tag == VT.Array) return VType.OfList(ca.Elem!);
        }
        if (ln == "String.Join" || ln == "String.JoinEnum") return VType.String;
        if (ln == "List.Contains" || ln == "TcsQueue.Contains") return VType.Bool;
        if (ln == "List.IndexOf") return VType.Int;
        if (ln.StartsWith("String.Format")) return VType.String;
        var plan = LibPlan(c);
        if (plan == null) return null;
        var nl = plan.NewList;
        if (nl != null) return nl;
        return plan.Rets.Count == 1 ? plan.Rets[0] : VType.Void;
    }

    private Val? Library(IlCall c, Val? dest)
    {
        var lname = LibName(c.Callee);
        if (lname == "String.FormatFloat" || lname == "String.FormatInt" || lname == "String.FormatEnum")
        {
            var fnt = TypeOf(c.Args[0]);
            if (fnt.Tag == VT.Nullable)
            {
                // T? の値なしは空文字列 (il-spec §13)
                var nv = Expr(c.Args[0]);
                var d = dest ?? Temp(VType.String);
                var none = new Label();
                var end = new Label();
                Jump(Op.JF, none, nv.Regs[0]);
                var inner = CopyToTemp(new Val(fnt.Inner!, P4Lists.RegsFrom(nv.Regs, 1)));
                Scoped(inner, x => Library(new IlCall(c.Callee, P4Lists.ReplaceArg(c.Args, 0, x), null), d));
                Jump(Op.JMP, end);
                Mark(none);
                EmitImm(Op.LDSTR, d.Reg, _m.Const(new List<int>()));
                Mark(end);
                return d;
            }
        }
        if ((lname == "List.Copy" || lname == "String.Join" || lname == "String.JoinEnum") && c.Args.Count >= 2 - (lname == "List.Copy" ? 1 : 0))
        {
            var ai = lname == "List.Copy" ? 0 : 1;
            var at = TypeOf(c.Args[ai]);
            if (at.Tag == VT.Array)
            {
                // 配列から List を作ってから渡す
                var arr = Expr(c.Args[ai]);
                var lt = VType.OfList(at.Elem!);
                var list = Temp(lt);
                Emit(Op.NEWLIST, list.Reg, 0, _m.TypeIndex(lt));
                var i = Temp(VType.Int);
                var n = Temp(VType.Int);
                EmitImm(Op.LDK, i.Reg, 0);
                Emit(Op.ARR_LEN, n.Reg, arr.Reg);
                var top = new Label();
                var exit = new Label();
                Mark(top);
                Jump(Op.JF_LT_I32, exit, i.Reg, n.Reg);
                var item = LoadPlace(Place.Make(at.Elem!, "elem", 0, null, arr, i, 0, at), null);
                ListAdd(list, lt, item);
                Emit(Op.ADDI_I32, i.Reg, i.Reg, 1);
                Jump(Op.JMP, top);
                Mark(exit);
                if (lname == "List.Copy") return dest == null ? list : Store2(dest, list);
                return Scoped(list, x => Library(new IlCall(c.Callee, P4Lists.ReplaceArg(c.Args, ai, x), null), dest));
            }
        }
        if (lname == "List.Contains" || lname == "List.IndexOf" || lname == "TcsQueue.Contains")
        {
            var slt = TypeOf(c.Args[0]);
            if (slt.Tag == VT.List && slt.Elem!.IsAggregate) return StructFind(c, slt, lname == "List.IndexOf", dest);
        }
        if (lname == "List.Remove")
        {
            var rlt = TypeOf(c.Args[0]);
            if (rlt.Tag == VT.List && rlt.Elem!.IsAggregate)
            {
                // struct の要素の Remove: 値の等価で番号を探し、あれば RemoveAt
                var list = CopyToTemp(Expr(c.Args[0]));
                var at = Scoped(list, x => StructFind(new IlCall(c.Callee, P4Lists.ReplaceArg(c.Args, 0, x), c.Origin), rlt, true, null));
                var d = dest ?? Temp(VType.Bool);
                var none = new Label();
                var end = new Label();
                var zero = Temp(VType.Int);
                EmitImm(Op.LDK, zero.Reg, 0);
                Jump(Op.JF_LE_I32, none, zero.Reg, at.Reg);
                Emit(Op.LIST_REMOVE_AT, list.Reg, at.Reg);
                EmitImm(Op.LDK, d.Reg, 1);
                Jump(Op.JMP, end);
                Mark(none);
                EmitImm(Op.LDK, d.Reg, 0);
                Mark(end);
                return d;
            }
        }
        var callee = c.Callee;
        if (callee == "TinySystem.Random.Seed")
        {
            var r = SharedRandom(null);
            var b = Block(P4Lists.Types2(VType.Random(), VType.Int));
            Emit(Op.MOV, b, r.Reg);
            ExprAs(c.Args[0], VType.Int, new Val(VType.Int, P4Lists.Reg(b + 1)));
            Emit(Op.CALLR, b, 0, _m.Intrinsic("random.seed"));
            return new Val(VType.Void, new List<int>());
        }
        if (callee == "TcsQueue.Enqueue")
        {
            var qt = TypeOf(c.Args[0]);
            if (qt.Tag == VT.List)
            {
                var q = Expr(c.Args[0]);
                ListAdd(q, qt, ExprAs(c.Args[1], qt.Elem!));
                return new Val(VType.Void, new List<int>());
            }
        }
        if (callee == "TcsQueue.Dequeue" || callee == "TcsQueue.Peek")
        {
            var qt = TypeOf(c.Args[0]);
            if (qt.Tag == VT.List)
            {
                // 空なら先頭の読みが fault する (Queue empty)
                var q = Expr(c.Args[0]);
                var zero = Temp(VType.Int);
                EmitImm(Op.LDK, zero.Reg, 0);
                var d = dest ?? Temp(qt.Elem!);
                LoadPlace(Place.Make(qt.Elem!, "list", 0, null, q, zero, 0, qt), d);
                if (callee == "TcsQueue.Dequeue") Emit(Op.LIST_REMOVE_AT, q.Reg, zero.Reg);
                return d;
            }
        }
        if (callee == "TcsQueue.Count" || callee == "TcsHashSet.Count")
        {
            var q = Expr(c.Args[0]);
            var d = dest ?? Temp(VType.Int);
            Emit(q.Type.Tag == VT.Dict ? Op.DICT_LEN : Op.LIST_LEN, d.Reg, q.Reg);
            return d;
        }
        if (callee == "TcsQueue.Clear")
        {
            Emit(Op.LIST_CLEAR, Expr(c.Args[0]).Reg);
            return new Val(VType.Void, new List<int>());
        }
        if (callee == "TcsHashSet.Clear")
        {
            var hr = Expr(c.Args[0]).Reg;
            Emit(Op.CALLR, hr, 0, _m.Intrinsic("dict.clear"));
            return new Val(VType.Void, new List<int>());
        }
        if (callee == "TcsQueue.ToList" && TypeOf(c.Args[0]).Tag == VT.List)
            return Library(new IlCall("List.Copy", P4Lists.Exprs1(c.Args[0]), null), dest);
        if (callee == "TcsQueue.Contains")
            return Library(new IlCall("List.Contains", P4Lists.Exprs2(c.Args[0], c.Args[1]), null), dest);
        if (callee == "TcsHashSet.ToList")
            return Library(new IlCall("Dict.Keys", P4Lists.Exprs1(c.Args[0]), null), dest);
        if (callee == "TcsHashSet.Add" || callee == "TcsHashSet.Remove" || callee == "TcsHashSet.Contains")
        {
            var st = TypeOf(c.Args[0]);
            if (st.Tag == VT.Dict)
            {
                var b = Block(P4Lists.Types2(st, st.Key!));
                ExprAs(c.Args[0], st, new Val(st, P4Lists.Reg(b)));
                ExprAs(c.Args[1], st.Key!, new Val(st.Key!, P4Lists.Reg(b + 1)));
                var d = dest ?? Temp(VType.Bool);
                Emit(Op.CALLR, b, d.Reg, _m.Intrinsic("set." + Strings.From(callee, 11).ToLower()));
                return d;
            }
        }
        if (callee == "__tcs_write" && c.Args.Count == 1)
        {
            var sv = ToStr(c.Args[0]);
            Emit(Op.CALLR, sv.Reg, 0, _m.Intrinsic("io.write"));
            return new Val(VType.Void, new List<int>());
        }
        if (callee == "__tcs_format" && c.Args.Count >= 1)
        {
            var a0 = c.Args[0];
            if (a0 is IlLit fmtLit && fmtLit.Kind == IlLitKind.String)
                return Format(VBytes.Utf8(fmtLit.S), P4Lists.ExprsFrom(c.Args, 1), dest);
        }
        if (callee == "__tcs_cstr")
        {
            var v = ExprAs(c.Args[0], VType.Int);
            var d = dest ?? Temp(VType.String);
            Emit(Op.CHR, d.Reg, v.Reg);
            return d;
        }
        if (callee == "__tcs_fmod")
        {
            var l = ExprAs(c.Args[0], VType.Float);
            var r = ExprAs(c.Args[1], VType.Float);
            var d = dest ?? Temp(VType.Float);
            Emit(Op.FMOD_F32, d.Reg, l.Reg, r.Reg);
            return d;
        }
        if (callee == "List.Insert")
        {
            var lt = TypeOf(c.Args[0]);
            if (lt.Tag == VT.List)
            {
                var list = Expr(c.Args[0]);
                var at = ExprAs(c.Args[1], VType.Int);
                var v = ExprAs(c.Args[2], lt.Elem!);
                Emit(Op.LIST_INSERT0, list.Reg, at.Reg);
                StorePlace(Place.Make(lt.Elem!, "list", 0, null, list, at, 0, lt), v);
                return new Val(VType.Void, new List<int>());
            }
        }
        if (callee == "List.RemoveAt" && c.Args.Count == 2 && TypeOf(c.Args[0]).Tag == VT.List)
        {
            var list = Expr(c.Args[0]);
            var at = ExprAs(c.Args[1], VType.Int);
            Emit(Op.LIST_REMOVE_AT, list.Reg, at.Reg);
            return new Val(VType.Void, new List<int>());
        }
        if (callee == "Dict.Remove")
        {
            var dt = TypeOf(c.Args[0]);
            if (dt.Tag == VT.Dict)
            {
                var d0 = Expr(c.Args[0]);
                var key = ExprAs(c.Args[1], dt.Key!);
                var d = dest ?? Temp(VType.Bool);
                Emit(Op.DICT_REMOVE, d.Reg, d0.Reg, key.Reg);
                return d;
            }
        }
        if (callee == "Dict.Keys" || callee == "Dict.Values")
        {
            var dt = TypeOf(c.Args[0]);
            if (dt.Tag == VT.Dict)
            {
                // 足した順の key / 値の新しい List
                var keys = callee == "Dict.Keys";
                var lt = VType.OfList(keys ? dt.Key! : dt.Value!);
                var dict = Expr(c.Args[0]);
                var d = dest ?? Temp(lt);
                Emit(Op.NEWLIST, d.Reg, 0, _m.TypeIndex(lt));
                var i = Temp(VType.Int);
                var n = Temp(VType.Int);
                var live = Temp(VType.Bool);
                EmitImm(Op.LDK, i.Reg, 0);
                var top = new Label();
                var next = new Label();
                var exit = new Label();
                Mark(top);
                Emit(Op.DICT_USED, n.Reg, dict.Reg);
                Jump(Op.JF_LT_I32, exit, i.Reg, n.Reg);
                Emit(Op.DICT_LIVE, live.Reg, dict.Reg, i.Reg);
                Jump(Op.JF, next, live.Reg);
                var item = Temp(lt.Elem!);
                if (keys) Emit(Op.DICT_LDK, item.Reg, dict.Reg, i.Reg);
                else LoadPlace(Place.Make(dt.Value!, "dict", 0, null, dict, i, 0, null), item);
                ListAdd(d, lt, item);
                Mark(next);
                Emit(Op.ADDI_I32, i.Reg, i.Reg, 1);
                Jump(Op.JMP, top);
                Mark(exit);
                return d;
            }
        }
        if (callee == "TinySystem.Utf8.New")
        {
            var str = ExprAs(c.Args[0], VType.String);
            var d = dest ?? Temp(VType.Utf8());
            Emit(Op.MOV, d.Regs[0], str.Reg);
            EmitImm(Op.LDK, d.Regs[1], 0);
            Emit(Op.CALLR, str.Reg, d.Regs[2], _m.Intrinsic("string.bytelen"));
            return d;
        }
        if (callee == "TinySystem.Utf8.Default")
        {
            var d = dest ?? Temp(VType.Utf8());
            EmitImm(Op.LDSTR, d.Regs[0], _m.Const(new List<int>()));
            EmitImm(Op.LDK, d.Regs[1], 0);
            EmitImm(Op.LDK, d.Regs[2], 0);
            return d;
        }
        if (callee == "String.Join" && (c.Args.Count != 2 || TypeOf(c.Args[1]).Tag != VT.List))
        {
            // Join(sep, a, b, ...): 連結
            var sep = ToStr(c.Args[0]);
            var acc = ToStr(c.Args[1]);
            for (var i = 2; i < c.Args.Count; i++)
            {
                var t = Temp(VType.String);
                Emit(Op.CONCAT, t.Reg, acc.Reg, sep.Reg);
                var piece = ToStr(c.Args[i]);
                var t2 = Temp(VType.String);
                Emit(Op.CONCAT, t2.Reg, t.Reg, piece.Reg);
                acc = t2;
            }
            return dest == null ? acc : Store2(dest, acc);
        }
        var plan = LibPlan(c);
        if (plan == null) return null;
        // 結果を足す List、引数、戻り値の区画
        var newList = plan.NewList;
        var argTypes = new List<VType>();
        if (newList != null) argTypes.Add(newList);
        foreach (var pt in plan.Params) argTypes.Add(pt);
        var block = Block(LeafTypes(argTypes));
        var at2 = block;
        if (newList != null)
        {
            Emit(Op.NEWLIST, at2, 0, _m.TypeIndex(newList));
            at2++;
        }
        for (var i = 0; i < plan.Params.Count; i++)
        {
            var t = plan.Params[i];
            var k = _m.Leaves(t).Count;
            ExprAs(c.Args[i], t, new Val(t, P4Lists.Range(at2, k)));
            at2 += k;
        }
        var retTypes = LeafTypes(plan.Rets);
        var rb = Block(retTypes);
        Emit(Op.CALLR, block, rb, _m.Intrinsic(plan.Name));
        if (newList != null)
        {
            var lv = new Val(newList, P4Lists.Reg(block));
            return dest == null ? lv : Store2(dest, lv);
        }
        if (plan.Rets.Count == 0) return new Val(VType.Void, new List<int>());
        var result = new Val(plan.Rets[0], P4Lists.Range(rb, retTypes.Count));
        return dest == null ? result : Store2(dest, result);
    }

    /// <summary>値を一時の名前で IL に渡す (IL の式を組み立て直して同じ経路で emit する)。</summary>
    private Val Scoped(Val v, System.Func<IlExpr, Val?> body)
    {
        var name = $"__vm_tmp{_scopes.Count}_{CodeCount}";
        var scope = new Dictionary<string, Var>();
        scope[name] = Var.OfReg(v);
        _scopes.Add(scope);
        var r = body(new IlVar(name, null));
        _scopes.RemoveAt(_scopes.Count - 1);
        if (r == null)
        {
            CFail.Report($"{_f.Name}: library call");
            return BadVal();
        }
        return r;
    }

    /// <summary>struct の要素の List.Contains / IndexOf (EqualityComparer&lt;T&gt;.Default と同じ葉ごとの等価)。</summary>
    private Val StructFind(IlCall c, VType lt, bool index, Val? dest)
    {
        var list = Expr(c.Args[0]);
        var item = CopyToTemp(ExprAs(c.Args[1], lt.Elem!));
        var i = Temp(VType.Int);
        var n = Temp(VType.Int);
        var d = dest ?? Temp(index ? VType.Int : VType.Bool);
        EmitImm(Op.LDK, i.Reg, 0);
        Emit(Op.LIST_LEN, n.Reg, list.Reg);
        var top = new Label();
        var found = new Label();
        var notFound = new Label();
        var end = new Label();
        Mark(top);
        Jump(Op.JF_LT_I32, notFound, i.Reg, n.Reg);
        var elem = LoadPlace(Place.Make(lt.Elem!, "list", 0, null, list, i, 0, lt), null);
        var eq = Scoped(elem, x => Scoped(item, y => ValueEq(x, y, lt.Elem!, null)));
        Jump(Op.JT, found, eq.Reg);
        Emit(Op.ADDI_I32, i.Reg, i.Reg, 1);
        Jump(Op.JMP, top);
        Mark(found);
        if (index) Emit(Op.MOV, d.Reg, i.Reg);
        else EmitImm(Op.LDK, d.Reg, 1);
        Jump(Op.JMP, end);
        Mark(notFound);
        EmitImm(Op.LDK, d.Reg, index ? -1 : 0);
        Mark(end);
        return d;
    }

    /// <summary>foreach (var r in s.EnumerateRunes()): rune (code point の整数) ごと。</summary>
    private void ForeachRunes(IlForeachRunes fr)
    {
        _scopes.Add(new Dictionary<string, Var>());
        var str = NewVal(VType.String);
        ExprAs(fr.Str, VType.String, str);
        var len = NewVal(VType.Int);
        Emit(Op.CALLR, str.Reg, len.Reg, _m.Intrinsic("string.bytelen"));
        var pos = NewVal(VType.Int);
        EmitImm(Op.LDK, pos.Reg, 0);
        EndStatement();
        var top = new Label();
        var exit = new Label();
        Mark(top);
        Jump(Op.JF_LT_I32, exit, pos.Reg, len.Reg);
        var args = Block(P4Lists.Types2(VType.String, VType.Int));
        Emit(Op.MOV, args, str.Reg);
        Emit(Op.MOV, args + 1, pos.Reg);
        var rets = Block(P4Lists.Types2(VType.Int, VType.Int));
        Emit(Op.CALLR, args, rets, _m.Intrinsic("utf8.runenext"));
        Emit(Op.MOV, pos.Reg, rets);
        _scopes.Add(new Dictionary<string, Var>());
        var rune = new Val(VType.Int, P4Lists.Reg(rets + 1));
        _scopes[_scopes.Count - 1][fr.Var] = _captured.Contains(fr.Var) ? NewCell(rune) : Var.OfReg(CopyToNew(rune));
        EndStatement();
        _loops.Add(new LoopLabels(exit, top));
        Block(fr.Body);
        _loops.RemoveAt(_loops.Count - 1);
        _scopes.RemoveAt(_scopes.Count - 1);
        Jump(Op.JMP, top);
        Mark(exit);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    // ---- IIFE (式の位置の文の列。switch 式、?. など) ----

    private Val Iife(IlIife iife, Val? dest)
    {
        var type = IifeType(iife);
        // 値は temp に作る (dest が中で読む変数のこともある: `q += q++` は q を IIFE の中で読む)
        Val? d = null;
        if (type.Tag != VT.Void) d = Temp(type);
        if (d != null) EmitZero(d); // return に届かない経路は値なし (?. の null 側)
        // 中の文の temp と区画は外の式の temp と分ける (文の終わりで外の temp を空きに戻さない)
        var savedTemps = new List<int>();
        foreach (var r in _temps) savedTemps.Add(r);
        var savedBlocks = new List<BlockUse>();
        foreach (var u in _blocksInUse) savedBlocks.Add(u);
        _temps.Clear();
        _blocksInUse.Clear();
        var savedLoops = new List<LoopLabels>();
        foreach (var lp in _loops) savedLoops.Add(lp);
        var savedScopes = new List<Label>();
        foreach (var bs in _breakScopes) savedScopes.Add(bs);
        _loops.Clear();
        _breakScopes.Clear();
        var end = new Label();
        _iifes.Add(new IifeFrame(d, end, type));
        _scopes.Add(new Dictionary<string, Var>());
        TypeUntypedLocals(iife.Stats);
        foreach (var st in iife.Stats) Stat(st);
        _scopes.RemoveAt(_scopes.Count - 1);
        _iifes.RemoveAt(_iifes.Count - 1);
        foreach (var lp in savedLoops) _loops.Add(lp);
        foreach (var bs in savedScopes) _breakScopes.Add(bs);
        Mark(end);
        EndStatement();
        foreach (var r in savedTemps) _temps.Add(r);
        foreach (var u in savedBlocks) _blocksInUse.Add(u);
        if (d != null && dest != null)
        {
            Store(dest, d);
            return dest;
        }
        if (d != null) return d;
        return new Val(VType.Void, new List<int>());
    }

    /// <summary>IIFE の値の型: 中の local を仮の scope に置いて、最初の値つき return の型を見る。</summary>
    private VType IifeType(IlIife iife)
    {
        _scopes.Add(new Dictionary<string, Var>());
        var t = ReturnType(iife.Stats);
        _scopes.RemoveAt(_scopes.Count - 1);
        if (t == null) return VType.Void;
        return t;
    }

    private VType? ReturnType(List<IlStat> stats)
    {
        VType? nullFound = null;
        TypeUntypedLocals(stats);
        foreach (var st in stats)
        {
            VType? t = null;
            if (st is IlLocal l)
            {
                VType lt;
                VType? inferred;
                if (l.Type != null) lt = _m.Parse(l.Type);
                else if (l.Init != null) lt = TypeOf(l.Init);
                else if (_untyped.TryGetValue(l.Name, out inferred)) lt = inferred!;
                else lt = VType.Void;
                _scopes[_scopes.Count - 1][l.Name] = Var.OfReg(new Val(lt, new List<int>()));
            }
            else if (st is IlMultiAssign ma && ma.Declare)
            {
                var types = MultiTypes(ma);
                for (var k = 0; k < ma.Targets.Count && k < types.Count; k++)
                {
                    if (ma.Targets[k] is IlVar tv) _scopes[_scopes.Count - 1][tv.Name] = Var.OfReg(new Val(types[k], new List<int>()));
                }
            }
            else if (st is IlReturn rs && rs.Value != null)
            {
                t = TypeOf(rs.Value);
            }
            else if (st is IlIf iff)
            {
                foreach (var arm in iff.Arms)
                {
                    if (t == null) t = ReturnTypeScoped(arm.Body.Stats);
                }
                if (iff.Else != null && t == null) t = ReturnTypeScoped(iff.Else.Stats);
            }
            else if (st is IlDo dd) t = ReturnTypeScoped(dd.Body.Stats);
            else if (st is IlBreakScope bsc) t = ReturnTypeScoped(bsc.Body.Stats);
            else if (st is IlWhile w) t = ReturnTypeScoped(w.Body.Stats);
            else if (st is IlRepeat rp) t = ReturnTypeScoped(rp.Body.Stats);
            else if (st is IlNumericFor nf)
            {
                var forScope = new Dictionary<string, Var>();
                forScope[nf.Var] = Var.OfReg(new Val(VType.Int, new List<int>()));
                _scopes.Add(forScope);
                t = ReturnType(nf.Body.Stats);
                _scopes.RemoveAt(_scopes.Count - 1);
            }
            if (t != null && t.Tag == VT.Null) nullFound = t;
            else if (t != null) return t;
        }
        return nullFound;
    }

    // C# 版の ReturnType の局所関数 Scoped: 仮の scope の中で ReturnType
    private VType? ReturnTypeScoped(List<IlStat> inner)
    {
        _scopes.Add(new Dictionary<string, Var>());
        var t = ReturnType(inner);
        _scopes.RemoveAt(_scopes.Count - 1);
        return t;
    }

    // ---- Nullable (il-spec §3) ----

    /// <summary>`e ?? d`: d は T (結果 T) か T? (結果 T?)。d は e が値なしのときだけ評価する。</summary>
    private Val NullableOr(IlNullableGetOrDefault g, Val? dest)
    {
        var type = TypeOf(g);
        var e = Expr(g.E);
        var d = dest ?? Temp(type);
        var end = new Label();
        var none = new Label();
        if (e.Type.Tag == VT.Nullable)
        {
            Jump(Op.JF, none, e.Regs[0]);
            Store(d, type.Tag == VT.Nullable ? e : new Val(e.Type.Inner!, P5Regs.From(e.Regs, 1)));
        }
        else
        {
            // ref の `??`
            Jump(Op.JNULL, none, e.Reg);
            Store(d, e);
        }
        Jump(Op.JMP, end);
        Mark(none);
        ExprAs(g.Default, type, d);
        Mark(end);
        return d;
    }

    private VType LiftedType(IlLiftedBin b)
    {
        if (b.Op == IlLiftedOp.Eq || b.Op == IlLiftedOp.Ne || b.Op == IlLiftedOp.Lt || b.Op == IlLiftedOp.Le
            || b.Op == IlLiftedOp.Gt || b.Op == IlLiftedOp.Ge)
            return VType.Bool;
        if (b.Op == IlLiftedOp.And || b.Op == IlLiftedOp.Or) return VType.OfNullable(VType.Bool);
        return VType.OfNullable(LiftedInner(b));
    }

    private VType LiftedInner(IlLiftedBin b)
    {
        var lt0 = TypeOf(b.L);
        var l = lt0.Tag == VT.Nullable ? lt0.Inner! : TypeOf(b.L);
        var rt0 = TypeOf(b.R);
        var r = rt0.Tag == VT.Nullable ? rt0.Inner! : TypeOf(b.R);
        if (b.Op == IlLiftedOp.Shl || b.Op == IlLiftedOp.Shr) return l;
        return VType.Same(l, VType.Float) || VType.Same(r, VType.Float) ? VType.Float : l;
    }

    private Val LiftedBin(IlLiftedBin b, Val? dest)
    {
        var inner = LiftedInner(b);
        var nt = VType.OfNullable(inner);
        var rt = b.Op == IlLiftedOp.Shl || b.Op == IlLiftedOp.Shr ? VType.OfNullable(VType.Int) : nt;
        if (b.Op == IlLiftedOp.And || b.Op == IlLiftedOp.Or)
        {
            // bool? の三値論理: And は片方が false なら false、Or は片方が true なら true、ほかは両方に値があれば値
            var bn = VType.OfNullable(VType.Bool);
            var l0 = ExprAs(b.L, bn);
            var r0 = ExprAs(b.R, bn);
            var d0 = dest ?? Temp(bn);
            var decided = b.Op == IlLiftedOp.And ? 0 : 1;
            var setDecided = new Label();
            var end0 = new Label();
            var notL = new Label();
            var notR = new Label();
            // l が値ありで決める値なら
            Jump(Op.JF, notL, l0.Regs[0]);
            Jump(decided == 0 ? Op.JF : Op.JT, setDecided, l0.Regs[1]);
            Mark(notL);
            Jump(Op.JF, notR, r0.Regs[0]);
            Jump(decided == 0 ? Op.JF : Op.JT, setDecided, r0.Regs[1]);
            Mark(notR);
            // どちらも決める値でない: 両方に値があれば !decided、無ければ値なし
            var none = new Label();
            Jump(Op.JF, none, l0.Regs[0]);
            Jump(Op.JF, none, r0.Regs[0]);
            EmitImm(Op.LDK, d0.Regs[0], 1);
            EmitImm(Op.LDK, d0.Regs[1], 1 - decided);
            Jump(Op.JMP, end0);
            Mark(none);
            EmitImm(Op.LDK, d0.Regs[0], 0);
            EmitImm(Op.LDK, d0.Regs[1], 0);
            Jump(Op.JMP, end0);
            Mark(setDecided);
            EmitImm(Op.LDK, d0.Regs[0], 1);
            EmitImm(Op.LDK, d0.Regs[1], decided);
            Mark(end0);
            return d0;
        }
        var l = ExprAs(b.L, nt);
        var r = ExprAs(b.R, rt);
        var lv = new Val(inner, P5Regs.From(l.Regs, 1));
        var rv = new Val(rt.Inner!, P5Regs.From(r.Regs, 1));
        if (b.Op == IlLiftedOp.Eq || b.Op == IlLiftedOp.Ne)
        {
            // 両方値なしで等しい、片方だけ値なしで等しくない、両方値ありは値の比較
            var d1 = dest ?? Temp(VType.Bool);
            var differ = new Label();
            var end1 = new Label();
            var bothNone = new Label();
            var hasEq = Temp(VType.Bool);
            Emit(Op.EQ_B, hasEq.Reg, l.Regs[0], r.Regs[0]);
            Jump(Op.JF, differ, hasEq.Reg);
            Jump(Op.JF, bothNone, l.Regs[0]);
            var op1 = Compare(IlBinOp.Eq, inner, out bool swap1, out bool negate1);
            Emit(op1, d1.Reg, lv.Reg, rv.Reg);
            if (b.Op == IlLiftedOp.Ne) Emit(Op.NOT_B, d1.Reg, d1.Reg);
            Jump(Op.JMP, end1);
            Mark(bothNone);
            EmitImm(Op.LDK, d1.Reg, b.Op == IlLiftedOp.Eq ? 1 : 0);
            Jump(Op.JMP, end1);
            Mark(differ);
            EmitImm(Op.LDK, d1.Reg, b.Op == IlLiftedOp.Eq ? 0 : 1);
            Mark(end1);
            return d1;
        }
        if (b.Op == IlLiftedOp.Lt || b.Op == IlLiftedOp.Le || b.Op == IlLiftedOp.Gt || b.Op == IlLiftedOp.Ge)
        {
            var d2 = dest ?? Temp(VType.Bool);
            var no = new Label();
            var end2 = new Label();
            Jump(Op.JF, no, l.Regs[0]);
            Jump(Op.JF, no, r.Regs[0]);
            IlBinOp bop;
            if (b.Op == IlLiftedOp.Lt) bop = IlBinOp.Lt;
            else if (b.Op == IlLiftedOp.Le) bop = IlBinOp.Le;
            else if (b.Op == IlLiftedOp.Gt) bop = IlBinOp.Gt;
            else bop = IlBinOp.Ge;
            var op2 = Compare(bop, inner, out bool swap2, out bool negate2);
            Emit(op2, d2.Reg, swap2 ? rv.Reg : lv.Reg, swap2 ? lv.Reg : rv.Reg);
            Jump(Op.JMP, end2);
            Mark(no);
            EmitImm(Op.LDK, d2.Reg, 0);
            Mark(end2);
            return d2;
        }
        // 算術 / bit / shift: 片方でも値なしなら値なし
        var d = dest ?? Temp(nt);
        var none2 = new Label();
        var end3 = new Label();
        Jump(Op.JF, none2, l.Regs[0]);
        Jump(Op.JF, none2, r.Regs[0]);
        var f = VType.Same(inner, VType.Float);
        var isBool = VType.Same(inner, VType.Bool);
        var dv = d.Regs[1];
        switch (b.Op)
        {
            case IlLiftedOp.Add: Emit(f ? Op.ADD_F32 : Op.ADD_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.Sub: Emit(f ? Op.SUB_F32 : Op.SUB_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.Mul: Emit(f ? Op.MUL_F32 : Op.MUL_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.DivFloat: Emit(Op.DIV_F32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.DivInt: Emit(Op.DIV_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.RemFloat: Emit(Op.FMOD_F32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.RemInt: Emit(Op.REM_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.BitAnd: Emit(isBool ? Op.AND_B : Op.AND_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.BitOr: Emit(isBool ? Op.OR_B : Op.OR_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.BitXor: Emit(isBool ? Op.NE_B : Op.XOR_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.Shl: Emit(Op.SHL_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.Shr: Emit(Op.SHR_I32, dv, lv.Reg, rv.Reg); break;
            default:
                CFail.Report($"lifted {b.Op}");
                return BadVal();
        }
        EmitImm(Op.LDK, d.Regs[0], 1);
        Jump(Op.JMP, end3);
        Mark(none2);
        EmitZero(d);
        Mark(end3);
        return d;
    }

    private Val LiftedUn(IlLiftedUn u, Val? dest)
    {
        var t = TypeOf(u.E);
        var v = ExprAs(u.E, t);
        var ntInner = t.Inner!;
        var d = dest ?? Temp(u.Op == IlUnOp.ToFloat ? VType.OfNullable(VType.Float) : t);
        var none = new Label();
        var end = new Label();
        Jump(Op.JF, none, v.Regs[0]);
        var inner = new Val(ntInner, P5Regs.From(v.Regs, 1));
        switch (u.Op)
        {
            case IlUnOp.Neg: Emit(VType.Same(ntInner, VType.Float) ? Op.NEG_F32 : Op.NEG_I32, d.Regs[1], inner.Reg); break;
            case IlUnOp.Not: Emit(Op.NOT_B, d.Regs[1], inner.Reg); break;
            case IlUnOp.BitNot: Emit(Op.BNOT_I32, d.Regs[1], inner.Reg); break;
            case IlUnOp.ToFloat: Emit(Op.I2F, d.Regs[1], inner.Reg); break;
            default:
                CFail.Report($"lifted {u.Op}");
                return BadVal();
        }
        EmitImm(Op.LDK, d.Regs[0], 1);
        Jump(Op.JMP, end);
        Mark(none);
        EmitZero(d);
        Mark(end);
        return d;
    }

    // ---- record の with ----

    private Val With(IlWith w, Val? dest)
    {
        var type = TypeOf(w.Src);
        var src = Expr(w.Src);
        if (type.Tag == VT.Class)
        {
            var d0 = dest ?? Temp(type);
            Emit(Op.CLONE, d0.Reg, src.Reg);
            foreach (var o in w.Overrides)
            {
                var v = ExprAs(o.Value, _m.FieldType(type, o.Name));
                StorePlace(Place.Make(_m.FieldType(type, o.Name), "field", 0, d0, null, null, LeafIndex(_m.ObjectLeaves(type), o.Name), null), v);
            }
            return d0;
        }
        // struct: 値の copy に上書き
        var d = NewVal(type);
        Store(d, src);
        foreach (var o in w.Overrides)
        {
            var ft = _m.FieldType(type, o.Name);
            var v = ExprAs(o.Value, ft);
            StorePlace(Place.Make(ft, "local", 0, d, null, null, LeafIndex(_m.Leaves(type), o.Name), null), v);
        }
        return dest == null ? d : Store2(dest, d);
    }

    // ---- 多重代入 (out の引数、分解) ----

    /// <summary>多重代入の右辺の値の型の列。</summary>
    private List<VType> MultiTypes(IlMultiAssign ma)
    {
        if (ma.Values.Count == 1 && ma.Values[0] is IlCall c0 && c0.Callee == "Math.TryParseInt")
        {
            var r0 = new List<VType>();
            r0.Add(VType.Bool);
            r0.Add(VType.Int);
            return r0;
        }
        if (ma.Values.Count == 1 && ma.Values[0] is IlCall c1 && c1.Callee == "Math.TryParseFloat")
        {
            var r1 = new List<VType>();
            r1.Add(VType.Bool);
            r1.Add(VType.Float);
            return r1;
        }
        if (ma.Values.Count == 1 && ma.Values[0] is IlCall tg && tg.Callee == "Dict.TryGet")
        {
            var dt = TypeOf(tg.Args[0]);
            if (dt.Tag == VT.Dict)
            {
                var r2 = new List<VType>();
                r2.Add(VType.Bool);
                r2.Add(dt.Value!);
                return r2;
            }
        }
        if (ma.Values.Count == 1 && ma.Values[0] is IlCall hc0 && _m.ForeignMethod(hc0.Callee) != null)
            return HostOutTypes(_m.ForeignMethod(hc0.Callee)!);
        if (ma.Values.Count == 1)
        {
            var info = OutCallOf(ma.Values[0]);
            if (info != null)
            {
                var list = new List<VType>();
                if (info.Ret.Tag != VT.Void) list.Add(info.Ret);
                foreach (var o in info.Outs) list.Add(o);
                return list;
            }
        }
        var types = new List<VType>();
        foreach (var v in ma.Values) types.Add(TypeOf(v));
        return types;
    }

    // 別の class の static method を class 名で呼ぶ形 (Foo.Two(...)) は、builder が IlDynCall(IlField(IlVar("Foo"), "Two")) にする。
    // Foo が変数でなく class なら、名前で呼ぶ IlCall にする (out 引数の多値を同じ経路で受けるため)
    private IlExpr StaticCallForm(IlExpr e)
    {
        if (e is IlDynCall dc && dc.Callee is IlField f && f.Recv is IlVar owner && Lookup(owner.Name) == null
            && _m.ClassInfo(owner.Name) != null)
            return new IlCall(owner.Name + "." + f.Name, dc.Args, dc.Origin);
        return e;
    }

    private OutInfo? OutCallOf(IlExpr e0)
    {
        var e = StaticCallForm(e0);
        if (e is IlCall c) return OutCall(c);
        if (e is IlInvoke inv)
        {
            var rt = TypeOf(inv.Recv);
            if (rt.Tag == VT.Class) return OutCall(new IlCall(rt.Name + "." + inv.Method, P5Exprs.Prepend(inv.Recv, inv.Args), null));
        }
        return null;
    }

    private void MultiAssign(IlMultiAssign ma0)
    {
        var ma = ma0;
        if (ma.Values.Count == 1)
        {
            var normalized = StaticCallForm(ma.Values[0]);
            if (normalized != ma.Values[0])
            {
                var nv0 = new List<IlExpr>();
                nv0.Add(normalized);
                ma = new IlMultiAssign(ma.Targets, nv0, ma.Declare, ma.Origin);
            }
        }
        if (ma.Values.Count == 1 && ma.Values[0] is IlInvoke inv)
        {
            var ict = TypeOf(inv.Recv);
            if (ict.Tag == VT.Class)
            {
                var iinfo = OutCall(new IlCall(ict.Name + "." + inv.Method, P5Exprs.Prepend(inv.Recv, inv.Args), null));
                if (iinfo != null)
                {
                    // instance method の out (receiver を先頭の引数にして同じ経路)
                    var nv = new List<IlExpr>();
                    nv.Add(new IlCall(ict.Name + "." + inv.Method, P5Exprs.Prepend(inv.Recv, inv.Args), null));
                    ma = new IlMultiAssign(ma.Targets, nv, ma.Declare, ma.Origin);
                }
            }
        }
        if (ma.Values.Count == 1 && ma.Values[0] is IlInvoke iinv && TypeOf(iinv.Recv).Tag == VT.Iface)
        {
            var multi = Invoke(iinv, null);
            if (multi.Type.Tag == VT.Tuple)
            {
                var tu = multi.Type;
                // interface の method の out: 戻り値と out の値の多値を左へ
                var at = 0;
                for (var i = 0; i < ma.Targets.Count && i < tu.Items.Count; i++)
                {
                    var n = _m.Leaves(tu.Items[i]).Count;
                    AssignTarget(ma.Targets[i], new Val(tu.Items[i], P5Regs.Slice(multi.Regs, at, n)), ma.Declare);
                    at += n;
                }
                return;
            }
        }
        if (ma.Values.Count == 1 && ma.Values[0] is IlCall tc
            && (tc.Callee == "Math.TryParseInt" || tc.Callee == "Math.TryParseFloat" || tc.Callee == "Dict.TryGet")
            && ma.Targets.Count == 2)
        {
            var found = TryCall(tc, out Val value);
            AssignTarget(ma.Targets[0], found, ma.Declare);
            AssignTarget(ma.Targets[1], value, ma.Declare);
            return;
        }
        if (ma.Values.Count == 1 && ma.Values[0] is IlCall hcall && _m.ForeignMethod(hcall.Callee) != null)
        {
            // host の関数の out: [戻り値?, out...] = host(in...)
            var hm = _m.ForeignMethod(hcall.Callee)!;
            var htypes = HostOutTypes(hm);
            var hres = HostCallOuts(hcall.Callee, hm, hcall.Args);
            var hat = 0;
            for (var i = 0; i < ma.Targets.Count && i < htypes.Count; i++)
            {
                var n = _m.Leaves(htypes[i]).Count;
                AssignTarget(ma.Targets[i], new Val(htypes[i], P5Regs.Slice(hres.Regs, hat, n)), ma.Declare);
                hat += n;
            }
            return;
        }
        if (ma.Values.Count == 1 && ma.Values[0] is IlCall call)
        {
            var info = OutCall(call);
            if (info != null)
            {
                // [戻り値?, out...] = call
                var types = new List<VType>();
                if (info.Ret.Tag != VT.Void) types.Add(info.Ret);
                foreach (var o in info.Outs) types.Add(o);
                var results = CallWithOuts(call, info);
                var at = 0;
                for (var i = 0; i < ma.Targets.Count; i++)
                {
                    var t = types[i];
                    var n = _m.Leaves(t).Count;
                    var v = new Val(t, P5Regs.Slice(results.Regs, at, n));
                    at += n;
                    AssignTarget(ma.Targets[i], v, ma.Declare);
                }
                return;
            }
        }
        if (ma.Targets.Count == ma.Values.Count)
        {
            // 右辺を全部評価してから左へ (il-spec §10 の分解の順)
            var vals = new List<Val>();
            foreach (var v in ma.Values) vals.Add(CopyToNew(Expr(v)));
            for (var i = 0; i < ma.Targets.Count; i++) AssignTarget(ma.Targets[i], vals[i], ma.Declare);
            return;
        }
        CFail.Report($"{_f.Name}: multiple assignment shape is not in the VM yet");
    }

    private void AssignTarget(IlExpr target, Val v, bool declare)
    {
        if (target is IlVar uv && uv.Name == "_" && Lookup("_") == null) return;
        if (declare && target is IlVar dv)
        {
            Var nvar;
            if (_captured.Contains(dv.Name)) nvar = NewCell(v);
            else nvar = Var.OfReg(CopyToNew(v));
            _scopes[_scopes.Count - 1][dv.Name] = nvar;
            return;
        }
        if (target is IlVar tv)
        {
            var existing = Lookup(tv.Name);
            if (existing != null)
            {
                WriteVar(existing, Convert(v, existing.Type));
                return;
            }
        }
        var place = PlaceOf(target);
        StorePlace(place, Convert(v, place.Type));
    }

    /// <summary>TryParse / TryGetValue の lowering 先: (見つかったか、値。無ければ既定値)。</summary>
    private Val TryCall(IlCall c, out Val value)
    {
        if (c.Callee == "Dict.TryGet")
        {
            var dt = TypeOf(c.Args[0]);
            var dict = Expr(c.Args[0]);
            var key = ExprAs(c.Args[1], dt.Key!);
            var found0 = CopyToNew(ZeroTemp(VType.Bool));
            var value0 = NewVal(dt.Value!);
            Emit(Op.DICT_HAS, found0.Reg, dict.Reg, key.Reg);
            var no = new Label();
            var end0 = new Label();
            Jump(Op.JF, no, found0.Reg);
            var at = Temp(VType.Int);
            Emit(Op.DICT_FIND, at.Reg, dict.Reg, key.Reg);
            LoadPlace(Place.Make(dt.Value!, "dict", 0, null, dict, at, 0, null), value0);
            Jump(Op.JMP, end0);
            Mark(no);
            if (c.Args.Count > 2) ExprAs(c.Args[2], dt.Value!, value0);
            else EmitZero(value0);
            Mark(end0);
            value = value0;
            return found0;
        }
        var isInt = c.Callee == "Math.TryParseInt";
        var vt = isInt ? VType.Int : VType.Float;
        var s = ExprAs(c.Args[0], VType.String);
        var bt = new List<VType>();
        bt.Add(VType.Bool);
        bt.Add(vt);
        var rb = Block(bt);
        Emit(Op.CALLR, s.Reg, rb, _m.Intrinsic(isInt ? "int.tryparse" : "float.tryparse"));
        var found = CopyToNew(new Val(VType.Bool, P5Regs.One(rb)));
        var value1 = CopyToNew(new Val(vt, P5Regs.One(rb + 1)));
        if (c.Args.Count > 1)
        {
            var ok = new Label();
            Jump(Op.JT, ok, found.Reg);
            ExprAs(c.Args[1], vt, value1);
            Mark(ok);
        }
        value = value1;
        return found;
    }

    private OutInfo? OutCall(IlCall call)
    {
        var u = new Utf8Bytes(call.Callee);
        var dot = -1;
        for (var k = 0; k < u.Length; k++)
        {
            if (u[k] == '.') dot = k;
        }
        if (dot < 0) return null;
        var cls = u.Slice(0, dot).ToString();
        var name = u.Slice(dot + 1, u.Length - dot - 1).ToString();
        ExMethod? method = null;
        var isStruct = false;
        if (_m.Classes.TryGetValue(cls, out ExClass? ci))
        {
            foreach (var m in ci.Methods)
            {
                if (m.Name == name)
                {
                    method = m;
                    break;
                }
            }
        }
        else if (_m.Structs.TryGetValue(cls, out ExStruct? si) && si.Methods.Count > 0)
        {
            foreach (var m in si.Methods)
            {
                if (m.Name == name)
                {
                    method = m;
                    break;
                }
            }
            isStruct = true;
        }
        if (method == null || method.OutParameters.Count == 0) return null;
        var types = new List<VType>();
        foreach (var pt in method.ParameterTypes) types.Add(_m.Parse(pt));
        var outs = new List<VType>();
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (method.OutParameters.Contains(method.Parameters[i])) outs.Add(types[i]);
        }
        return new OutInfo(cls, method, _m.Parse(method.ReturnType), outs, isStruct);
    }

    /// <summary>out を持つ関数の呼び出し: 戻り値の葉と out の葉が並んだ値を返す。</summary>
    private Val CallWithOuts(IlCall call, OutInfo info)
    {
        var all = new List<VType>();
        if (info.Ret.Tag != VT.Void) all.Add(info.Ret);
        foreach (var o in info.Outs) all.Add(o);
        if (info.Struct)
            return StructCall(call, info.Class, info.Method, all, null);
        var index = RequestMethod(info.Class, info.Method);
        var types = new List<VType>();
        foreach (var pt in info.Method.ParameterTypes) types.Add(_m.Parse(pt));
        var inTypes = new List<VType>();
        for (var i = 0; i < info.Method.Parameters.Count; i++)
        {
            if (!info.Method.OutParameters.Contains(info.Method.Parameters[i])) inTypes.Add(types[i]);
        }
        IlExpr? recv = null;
        if (!info.Method.IsStatic) recv = call.Args[0];
        var args = new List<IlExpr>();
        for (var i = info.Method.IsStatic ? 0 : 1; i < call.Args.Count; i++) args.Add(call.Args[i]);
        return EmitCallTyped(Op.CALL, index, recv, inTypes, args, all);
    }

    /// <summary>引数を区画に直接書き、CALL を出す。戻り値は rets の型の葉が並ぶ。</summary>
    private Val EmitCallTyped(Op op, int index, IlExpr? recv, List<VType> paramTypes, List<IlExpr> args,
        List<VType> rets)
    {
        if (paramTypes.Count != args.Count)
        {
            CFail.Report($"{_f.Name}: default arguments are not in the VM yet");
            return BadVal();
        }
        var allE = new List<IlExpr>();
        var allT = new List<VType>();
        if (recv != null)
        {
            allE.Add(recv);
            allT.Add(TypeOf(recv));
        }
        for (var i = 0; i < args.Count; i++)
        {
            allE.Add(args[i]);
            allT.Add(paramTypes[i]);
        }
        var block = Block(LeafTypes(allT));
        var at = block;
        for (var i = 0; i < allE.Count; i++)
        {
            var t = allT[i];
            var n = _m.Leaves(t).Count;
            ExprAs(allE[i], t, new Val(t, P5Regs.Range(at, n)));
            at += n;
        }
        if (recv != null) Emit(Op.NNULL, block);
        var retTypes = LeafTypes(rets);
        var rb = Block(retTypes);
        Emit(op, block, rb, index);
        return new Val(VType.Void, P5Regs.Range(rb, retTypes.Count));
    }

    /// <summary>struct の member の呼び出し (il-spec §10): `S.M(recv, args)`。instance member は self を値で渡し、
    /// 戻ってきた self を recv の場所に書き戻す (recv が変数・field・要素のとき)。extraRets は out の分。</summary>
    private Val StructCall(IlCall call, string st, ExMethod method, List<VType>? extraRets = null, Val? dest = null)
    {
        var index = _m.Request(st + "." + method.Name, def => new FunctionCompiler(_m, def, st).CompileStructMethod(method));
        var types = new List<VType>();
        foreach (var pt in method.ParameterTypes) types.Add(_m.Parse(pt));
        var inTypes = new List<VType>();
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (!method.OutParameters.Contains(method.Parameters[i])) inTypes.Add(types[i]);
        }
        var ret = _m.Parse(method.ReturnType);
        var rets = new List<VType>();
        if (extraRets != null)
        {
            foreach (var x in extraRets) rets.Add(x);
        }
        else if (ret.Tag != VT.Void) rets.Add(ret);
        if (method.IsStatic)
        {
            var r0 = EmitCallTyped(Op.CALL, index, null, inTypes, call.Args, rets);
            return extraRets == null && ret.Tag != VT.Void ? Typed(ret, r0, dest) : r0;
        }
        var selfType = VType.OfStruct(st);
        var recvExpr = call.Args[0];
        // IlStructCopy は copy に対する呼び出し (暗黙の ToString など)。書き戻さない
        while (recvExpr is IlParen pp) recvExpr = pp.E;
        Place? place = null;
        if (IsVariable(recvExpr)) place = PlaceOf(recvExpr);
        // place の受け手は C# では参照で渡るので、self の値は引数を評価した後に読む (引数が要素を書き換えても、その後の値に呼ぶ)
        Val? selfVal = null;
        if (place == null) selfVal = ExprAs(call.Args[0], selfType);
        var allT = new List<VType>();
        allT.Add(selfType);
        for (var i = 1; i < call.Args.Count; i++) allT.Add(inTypes[i - 1]);
        var block = Block(LeafTypes(allT));
        var at = block;
        var selfSlot = new Val(selfType, P5Regs.Range(block, _m.Leaves(selfType).Count));
        for (var i = 0; i < allT.Count; i++)
        {
            var t = allT[i];
            var n = _m.Leaves(t).Count;
            var slot = new Val(t, P5Regs.Range(at, n));
            if (i == 0)
            {
                if (selfVal != null) Store(slot, selfVal);
            }
            else ExprAs(call.Args[i], t, slot);
            at += n;
        }
        if (place != null) LoadPlace(place, selfSlot);
        var withSelf = new List<VType>();
        foreach (var x in rets) withSelf.Add(x);
        withSelf.Add(selfType);
        var retTypes = LeafTypes(withSelf);
        var rb = Block(retTypes);
        Emit(Op.CALL, block, rb, index);
        var retLeaves = LeafTypes(rets).Count;
        var newSelf = new Val(selfType, P5Regs.Range(rb + retLeaves, _m.Leaves(selfType).Count));
        if (place != null) StorePlace(place, newSelf);
        var r = new Val(VType.Void, P5Regs.Range(rb, retLeaves));
        return extraRets == null && ret.Tag != VT.Void ? Typed(ret, r, dest) : r;
    }

    /// <summary>C# の「変数」か (il-spec §10: local / 引数 / field / 配列の要素 / this)。List / Dictionary の
    /// indexer と呼び出しの結果は値 (struct の member の変異は捨てられる)。</summary>
    private bool IsVariable(IlExpr e0)
    {
        var e = e0;
        while (e is IlParen p) e = p.E;
        if (e is IlVar v) return Lookup(v.Name) != null;
        if (e is IlField fc && fc.Recv is IlVar cls && Lookup(cls.Name) == null && _m.IsTypeName(cls.Name)) return true;
        if (e is IlField f) return TypeOf(f.Recv).Tag == VT.Class || (TypeOf(f.Recv).IsAggregate && IsVariable(f.Recv));
        if (e is IlIndex i) return TypeOf(i.Recv).Tag == VT.Array;
        return false;
    }

    private Val Typed(VType t, Val raw, Val? dest)
    {
        var v = new Val(t, raw.Regs);
        return dest == null ? v : Store2(dest, v);
    }

    // ---- Dictionary の foreach (足した順) ----

    private void ForeachDict(IlForeachDict fd)
    {
        _scopes.Add(new Dictionary<string, Var>());
        var dt = TypeOf(fd.Coll);
        if (dt.Tag != VT.Dict)
        {
            CFail.Report($"{_f.Name}: foreach over non-Dictionary");
            return;
        }
        var dict = NewVal(dt);
        ExprAs(fd.Coll, dt, dict);
        var i = NewVal(VType.Int);
        EmitImm(Op.LDK, i.Reg, 0);
        var n = NewVal(VType.Int);
        var live = NewVal(VType.Bool);
        // 列挙は挿入順 (support-matrix §18)。途中で key を足したら fault (版で見る)
        // dict と版は loop の間ずっと要るので、文の間で使い回す区画でなく専用の連続する register に置く
        var a0 = NewVal(dt);
        var a1 = NewVal(VType.Int);
        if (a1.Reg != a0.Reg + 1)
        {
            CFail.Report($"{_f.Name}: dictionary foreach registers");
            return;
        }
        var args = a0.Reg;
        Emit(Op.MOV, args, dict.Reg);
        Emit(Op.CALLR, args, args + 1, _m.Intrinsic("dict.version"));
        EndStatement();
        var top = new Label();
        var cont = new Label();
        var exit = new Label();
        Mark(top);
        Emit(Op.CALLR, args, 0, _m.Intrinsic("dict.check"));
        Emit(Op.DICT_USED, n.Reg, dict.Reg);
        Jump(Op.JF_LT_I32, exit, i.Reg, n.Reg);
        Emit(Op.DICT_LIVE, live.Reg, dict.Reg, i.Reg);
        Jump(Op.JF, cont, live.Reg);
        _scopes.Add(new Dictionary<string, Var>());
        var kvt = VType.OfKvp(dt.Key!, dt.Value!);
        var kv = NewVal(kvt);
        var keyLeaves = _m.Leaves(dt.Key!).Count;
        Emit(Op.DICT_LDK, kv.Regs[0], dict.Reg, i.Reg);
        LoadPlace(Place.Make(dt.Value!, "dict", 0, null, dict, i, 0, null), new Val(dt.Value!, P5Regs.From(kv.Regs, keyLeaves)));
        Var kvar;
        if (_captured.Contains(fd.Var)) kvar = NewCell(kv);
        else kvar = Var.OfReg(kv);
        _scopes[_scopes.Count - 1][fd.Var] = kvar;
        EndStatement();
        _loops.Add(new LoopLabels(exit, cont));
        Block(fd.Body);
        _loops.RemoveAt(_loops.Count - 1);
        _scopes.RemoveAt(_scopes.Count - 1);
        Mark(cont);
        Emit(Op.ADDI_I32, i.Reg, i.Reg, 1);
        Jump(Op.JMP, top);
        Mark(exit);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    /// <summary>Random の instance method (il-spec §13): Next() は [0, int.MaxValue)、Next(max)、Next(min, max)、Range は両端を含む。</summary>
    private Val RandomCall(IlInvoke inv, Val? dest)
    {
        var r = Expr(inv.Recv);
        if (inv.Method == "NextFloat" || inv.Method == "NextSingle")
        {
            var df = dest ?? Temp(VType.Float);
            Emit(Op.CALLR, r.Reg, df.Reg, _m.Intrinsic("random.float"));
            return df;
        }
        var bt = new List<VType>();
        bt.Add(VType.Random());
        bt.Add(VType.Int);
        bt.Add(VType.Int);
        var b = Block(bt);
        Emit(Op.MOV, b, r.Reg);
        if (inv.Args.Count == 0)
        {
            EmitImm(Op.LDK, b + 1, 0);
            EmitImm(Op.LDK, b + 2, 0x7FFFFFFF);
        }
        else if (inv.Args.Count == 1)
        {
            EmitImm(Op.LDK, b + 1, 0);
            ExprAs(inv.Args[0], VType.Int, new Val(VType.Int, P5Regs.One(b + 2)));
        }
        else
        {
            ExprAs(inv.Args[0], VType.Int, new Val(VType.Int, P5Regs.One(b + 1)));
            ExprAs(inv.Args[1], VType.Int, new Val(VType.Int, P5Regs.One(b + 2)));
        }
        var d = dest ?? Temp(VType.Int);
        Emit(Op.CALLR, b, d.Reg, _m.Intrinsic(inv.Method == "Range" ? "random.range" : "random.next"));
        return d;
    }

    /// <summary>List.Add: 1 葉の要素は LIST_ADD、struct は 0 の要素を足してから葉を書く。</summary>
    private void ListAdd(Val list, VType lt, Val v)
    {
        if (!lt.Elem!.IsAggregate)
        {
            Emit(Pick(Op.LIST_ADD_I32, _m.Leaves(lt.Elem!)[0].Kind), list.Reg, v.Reg);
            return;
        }
        Emit(Op.LIST_PUSH0, list.Reg);
        var last = Temp(VType.Int);
        Emit(Op.LIST_LEN, last.Reg, list.Reg);
        Emit(Op.ADDI_I32, last.Reg, last.Reg, 0xFFFF);
        StorePlace(Place.Make(lt.Elem!, "list", 0, null, list, last, 0, lt), v);
    }

    private void Print(IlCall c)
    {
        if (c.Args.Count == 0)
        {
            var empty = Temp(VType.String);
            EmitImm(Op.LDSTR, empty.Reg, _m.Const(new List<int>()));
            Emit(Op.PRINT_S, empty.Reg);
            return;
        }
        if (c.Args.Count != 1)
        {
            CFail.Report("print with several values");
            return;
        }
        var arg = c.Args[0];
        if (arg is IlCall conv && (conv.Callee == "__tcs_fstr" || conv.Callee == "__tcs_bstr" || conv.Callee == "__tcs_str"))
            arg = conv.Args[0];
        var t = TypeOf(arg);
        if (t.Tag != VT.Prim)
        {
            Emit(Op.PRINT_S, ToStr(c.Args[0]).Reg);
            return;
        }
        var v = Expr(arg);
        if (VType.Same(t, VType.Float)) Emit(Op.PRINT_F32, v.Reg);
        else if (VType.Same(t, VType.Int)) Emit(Op.PRINT_I32, v.Reg);
        else Emit(Op.PRINT_B, v.Reg);
    }
}

/// <summary>値の格納場所 (C# 版の record Place)。Kind は "reg" / "static" / "field" / "elem" / "dict" など (C# 版のまま)。
/// 作るのは Place.Make (C# 版の名前付きの引数を、この順の位置の引数にする)。</summary>
public sealed class Place
{
    public VType Type;
    public string Kind;
    public int Slot;
    public Val? Obj;
    public Val? Arr;
    public Val? Idx;
    public int Leaf;
    public VType? Owner;

    public Place(VType type, string kind)
    {
        Type = type;
        Kind = kind;
    }

    public static Place Make(VType type, string kind, int slot, Val? obj, Val? arr, Val? idx, int leaf, VType? owner)
    {
        var p = new Place(type, kind);
        p.Slot = slot;
        p.Obj = obj;
        p.Arr = arr;
        p.Idx = idx;
        p.Leaf = leaf;
        p.Owner = owner;
        return p;
    }

    // C# 版の `with { ... }` の代わりの写し
    public Place Copy() => Make(Type, Kind, Slot, Obj, Arr, Idx, Leaf, Owner);
}

/// <summary>jump の行き先。Target は命令の番号 (未定なら -1)、Patches は行き先を待つ jump の命令。</summary>
public sealed class Label
{
    public int Target = -1;
    public List<int> Patches = new List<int>();
}

/// <summary>使っている register の区画 (呼び出しの引数と戻り値。文の終わりに空きに戻す)。</summary>
public sealed class BlockUse
{
    public string Key;
    public int Base;

    public BlockUse(string key, int b)
    {
        Key = key;
        Base = b;
    }
}

/// <summary>loop の break と continue の行き先。</summary>
public sealed class LoopLabels
{
    public Label Break;
    public Label Continue;

    public LoopLabels(Label brk, Label cont)
    {
        Break = brk;
        Continue = cont;
    }
}

/// <summary>式の位置の文の列 (IIFE) の結果の置き場所、終わり、型。</summary>
public sealed class IifeFrame
{
    public Val? Result;
    public Label End;
    public VType Type;

    public IifeFrame(Val? result, Label end, VType type)
    {
        Result = result;
        End = end;
        Type = type;
    }
}

/// <summary>式 (P2) の道具: register の列の部分、literal の判定、16 進の数字。</summary>
public static class P2Regs
{
    // C# 版の Regs[start..]
    public static List<int> From(List<int> regs, int start)
    {
        var r = new List<int>();
        for (int i = start; i < regs.Count; i++) r.Add(regs[i]);
        return r;
    }

    // C# 版の bytes[start..end]
    public static List<int> Slice(List<int> items, int start, int end)
    {
        var r = new List<int>();
        for (int i = start; i < end; i++) r.Add(items[i]);
        return r;
    }

    public static bool IsNil(IlExpr? e) => IlLiterals.IsNull(e);

    // PrimT { Kind: Kind.I32 or Kind.F32 }
    public static bool IsNumber(VType t) => t.Tag == VT.Prim && (t.PKind == Kind.I32 || t.PKind == Kind.F32);

    // 書式の flag ("-+ #0" のどれか)
    public static bool IsFormatFlag(int c) => c == '-' || c == '+' || c == ' ' || c == '#' || c == '0';

    // 16 進の数字の値 (数字でなければ -1)
    public static int HexDigit(int c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    // 16 進の数字列を 32 bit の並びとして読む (C# 版の uint.Parse(.., NumberStyles.HexNumber))
    public static int ParseHex(string digits)
    {
        var u = new Utf8Bytes(digits);
        int value = 0;
        for (int i = 0; i < u.Length; i++)
        {
            int d = HexDigit(u[i]);
            if (d < 0)
            {
                CFail.Report("invalid hex literal 0x" + digits);
                return 0;
            }
            value = value << 4 | d;
        }
        return value;
    }
}

/// <summary>register の番号の列の道具 (P3: 呼び出し)。</summary>
public static class P3Regs
{
    // start から count 個の連続する番号 (C# 版の Enumerable.Range(start, count).ToArray())
    public static List<int> Range(int start, int count)
    {
        var r = new List<int>();
        for (int i = 0; i < count; i++) r.Add(start + i);
        return r;
    }

    // 番号が先頭から連続しているか
    public static bool IsContiguous(List<int> regs)
    {
        for (int k = 0; k < regs.Count; k++)
        {
            if (regs[k] != regs[0] + k) return false;
        }
        return true;
    }
}

/// <summary>runtime の関数の呼び出しの形。Params は引数の型 (IL の引数の先頭から)、Rets は戻り値の型。
/// NewList は結果を足す空の List を先頭の引数として作って渡す (Split など)。</summary>
public sealed class LibCall
{
    public string Name;
    public List<VType> Params;
    public List<VType> Rets;
    public VType? NewList;

    public LibCall(string name, List<VType> parameters, List<VType> rets, VType? newList = null)
    {
        Name = name;
        Params = parameters;
        Rets = rets;
        NewList = newList;
    }
}

/// <summary>P4 (TinySystem の関数) の List の組み立て (C# 版の collection 式と範囲の代わり)。</summary>
public static class P4Lists
{
    public static List<VType> Types1(VType a)
    {
        var r = new List<VType>();
        r.Add(a);
        return r;
    }

    public static List<VType> Types2(VType a, VType b)
    {
        var r = new List<VType>();
        r.Add(a);
        r.Add(b);
        return r;
    }

    public static List<VType> Types3(VType a, VType b, VType c)
    {
        var r = new List<VType>();
        r.Add(a);
        r.Add(b);
        r.Add(c);
        return r;
    }

    // [r]
    public static List<int> Reg(int reg)
    {
        var r = new List<int>();
        r.Add(reg);
        return r;
    }

    // Enumerable.Range(start, count)
    public static List<int> Range(int start, int count)
    {
        var r = new List<int>();
        for (int i = 0; i < count; i++) r.Add(start + i);
        return r;
    }

    // regs[start..]
    public static List<int> RegsFrom(List<int> regs, int start)
    {
        var r = new List<int>();
        for (int i = start; i < regs.Count; i++) r.Add(regs[i]);
        return r;
    }

    // [e]
    public static List<IlExpr> Exprs1(IlExpr a)
    {
        var r = new List<IlExpr>();
        r.Add(a);
        return r;
    }

    // [a, b]
    public static List<IlExpr> Exprs2(IlExpr a, IlExpr b)
    {
        var r = new List<IlExpr>();
        r.Add(a);
        r.Add(b);
        return r;
    }

    // args[start..]
    public static List<IlExpr> ExprsFrom(List<IlExpr> args, int start)
    {
        var r = new List<IlExpr>();
        for (int i = start; i < args.Count; i++) r.Add(args[i]);
        return r;
    }

    // [.. args[..at], x, .. args[(at + 1)..]]
    public static List<IlExpr> ReplaceArg(List<IlExpr> args, int at, IlExpr x)
    {
        var r = new List<IlExpr>();
        for (int i = 0; i < args.Count; i++)
        {
            if (i == at) r.Add(x);
            else r.Add(args[i]);
        }
        return r;
    }
}

/// <summary>out を持つ関数の呼び出しの情報 (C# 版の private record OutInfo)。</summary>
public sealed class OutInfo
{
    public string Class;
    public ExMethod Method;
    public VType Ret;
    public List<VType> Outs;
    public bool Struct;

    public OutInfo(string cls, ExMethod method, VType ret, List<VType> outs, bool isStruct)
    {
        Class = cls;
        Method = method;
        Ret = ret;
        Outs = outs;
        Struct = isStruct;
    }
}

/// <summary>register の列の部分と連番 (C# 版の Regs[a..]、Skip / Take、Enumerable.Range、[r])。</summary>
public static class P5Regs
{
    public static List<int> From(List<int> regs, int start)
    {
        var r = new List<int>();
        for (int i = start; i < regs.Count; i++) r.Add(regs[i]);
        return r;
    }

    public static List<int> Slice(List<int> regs, int skip, int take)
    {
        var r = new List<int>();
        for (int i = skip; i < regs.Count && i < skip + take; i++) r.Add(regs[i]);
        return r;
    }

    public static List<int> Range(int start, int n)
    {
        var r = new List<int>();
        for (int i = 0; i < n; i++) r.Add(start + i);
        return r;
    }

    public static List<int> One(int reg)
    {
        var r = new List<int>();
        r.Add(reg);
        return r;
    }
}

/// <summary>式の列 (C# 版の [recv, .. args])。</summary>
public static class P5Exprs
{
    public static List<IlExpr> Prepend(IlExpr first, List<IlExpr> rest)
    {
        var r = new List<IlExpr>();
        r.Add(first);
        foreach (var e in rest) r.Add(e);
        return r;
    }
}
