using System.Globalization;
using System.Text;

namespace TinyCs.Vm;

/// <summary>値: 葉ごとの register の列 (prim / ref は 1 つ、struct は葉の数)。</summary>
internal sealed record Val(VType Type, int[] Regs)
{
    public int Reg => Regs.Length == 1 ? Regs[0] : throw new VmException($"{Type} is not a single register");
}

/// <summary>変数: register に住む値、cell に住む値 (closure が捕捉する。il-spec §7)、自分の closure が捕捉した cell。</summary>
internal abstract record Var(VType Type);

internal sealed record RegVar(Val Val) : Var(Val.Type);

internal sealed record CellVar(int Reg, VType Inner) : Var(Inner);

internal sealed record CapVar(int Index, VType Inner) : Var(Inner);

/// <summary>1 つの関数を IL から命令列にする (doc/vm-design.md §15)。
/// register の種類は関数の中で固定し (§6.1)、temp は種類と型ごとの空き list で使い回す。</summary>
internal sealed class FunctionCompiler
{
    private readonly ModuleBuilder _m;
    private readonly FunctionDef _f;
    private readonly string _className;
    private readonly List<ulong> _code = [];
    private readonly List<Dictionary<string, Var>> _scopes = [];
    private readonly Dictionary<(Kind, ushort), Stack<int>> _free = [];
    private readonly List<int> _temps = [];
    private readonly Dictionary<string, Stack<int>> _freeBlocks = [];
    private readonly List<(string Key, int Base)> _blocksInUse = [];
    private readonly List<(Label Break, Label Continue)> _loops = [];
    private readonly List<Label> _breakScopes = [];
    private readonly List<(Val? Result, Label End, VType Type)> _iifes = [];
    /// <summary>return ごとに戻り値の後ろに足す変数 (struct の instance method の self、out の引数)。</summary>
    private readonly List<Var> _extraRets = [];
    private readonly HashSet<int> _labelTargets = [];
    private HashSet<string> _captured = [];
    private VType _returnType = VType.Void;
    private int _lastLine = -1;
    private int _lambdas;

    public FunctionCompiler(ModuleBuilder m, FunctionDef f, string className)
    {
        _m = m;
        _f = f;
        _className = className;
    }

    // ---- 入口 ----

    public void CompileMethod(IlMethodInfo method)
    {
        if (method.Body is null) throw new VmException($"{_f.Name}: no IL body");
        if (!method.OutParameters.IsDefaultOrEmpty) throw new VmException($"{_f.Name}: out parameters are not in the VM yet");
        _returnType = _m.Parse(method.ReturnType);
        var types = method.ParameterTypes.IsDefault
            ? throw new VmException($"{_f.Name}: parameter types missing")
            : method.ParameterTypes.Select(_m.Parse).ToList();
        var outs = method.OutParameters.IsDefault ? [] : method.OutParameters.ToHashSet();
        var ins = method.Parameters.Select((p, i) => (p, types[i])).Where(x => !outs.Contains(x.p)).ToList();
        Prologue(method.Body, method.IsStatic ? null : new ClassT(_className), ins.Select(x => x.p).ToList(),
            ins.Select(x => x.Item2).ToList(), method.Parameters.Select((p, i) => (p, types[i])).Where(x => outs.Contains(x.p)).ToList());
        Block(method.Body, newScope: false);
        Finish();
    }

    /// <summary>struct の member (il-spec §10)。instance member は self を値で受け、return ごとに self の今の値を
    /// 戻り値の後ろに返す (呼ぶ側が場所に書き戻す。copy-in / copy-out)。</summary>
    public void CompileStructMethod(IlMethodInfo method)
    {
        if (method.Body is null) throw new VmException($"{_f.Name}: no IL body");
        _returnType = _m.Parse(method.ReturnType);
        var types = method.ParameterTypes.Select(_m.Parse).ToList();
        var outs = method.OutParameters.IsDefault ? [] : method.OutParameters.ToHashSet();
        var ins = method.Parameters.Select((p, i) => (p, types[i])).Where(x => !outs.Contains(x.p)).ToList();
        Prologue(method.Body, method.IsStatic ? null : new StructT(_className), ins.Select(x => x.p).ToList(),
            ins.Select(x => x.Item2).ToList(), method.Parameters.Select((p, i) => (p, types[i])).Where(x => outs.Contains(x.p)).ToList(),
            selfOut: !method.IsStatic);
        Block(method.Body, newScope: false);
        Finish();
    }

    /// <summary>struct の constructor: 0 の値 → field の initializer → 本文、で作った self を返す (il-spec §10)。</summary>
    public void CompileStructCtor(IlCtorInfo ctor, IReadOnlyList<IlFieldInfo> initializers)
    {
        var self = new StructT(_className);
        _returnType = VType.Void;
        var types = ctor.ParameterTypes.Select(_m.Parse).ToList();
        foreach (var l in _m.Leaves(self)) _f.Rets.Add(_m.Info(l.Type));
        var body = ctor.Body ?? new IlBlock([]);
        _captured = IlWalk.Captured(body);
        _scopes.Add([]);
        var parameters = ctor.Parameters.Select((p, i) => (p, NewVal(types[i]))).ToList();
        _f.NParams = _f.Regs.Count;
        foreach (var (name, val) in parameters)
            _scopes[0][name] = _captured.Contains(name) ? NewCell(val) : new RegVar(val);
        var selfVar = Declare("self", self, null);
        _extraRets.Add(selfVar);
        EndStatement();
        foreach (var field in initializers)
        {
            Assign(new IlField(new IlVar("self"), field.Name), field.Init!);
            EndStatement();
        }
        Block(body, newScope: false);
        if (!EndsFlow()) EmitReturn(null);
        Finish();
    }

    /// <summary>constructor: field の initializer、本文の順 (il-spec §9)。field の default は NEW が 0 で埋める。</summary>
    public void CompileCtor(IlCtorInfo? ctor, IReadOnlyList<IlFieldInfo> initializers)
    {
        var body = ctor?.Body ?? new IlBlock([]);
        var types = ctor is null ? [] : ctor.ParameterTypes.Select(_m.Parse).ToList();
        Prologue(body, new ClassT(_className), ctor is null ? [] : ctor.Parameters, types);
        foreach (var field in initializers)
        {
            Assign(new IlField(new IlVar("self"), field.Name), field.Init!);
            EndStatement();
        }
        if (ctor is not null && !ctor.BaseArgs.IsDefaultOrEmpty) throw new VmException($"{_f.Name}: base is not in the VM");
        Block(body, newScope: false);
        Finish();
    }

    /// <summary>class の static field の initializer をまとめた関数 ("C.&lt;static&gt;")。</summary>
    public void CompileStaticInit(IEnumerable<IlFieldInfo> fields)
    {
        _scopes.Add([]);
        foreach (var field in fields)
        {
            var (slot, type) = _m.Static(_className, field.Name);
            var v = ExprAs(field.Init!, type);
            for (var k = 0; k < v.Regs.Length; k++) EmitImm(Op.STS, v.Regs[k], (uint)(slot + k));
            EndStatement();
        }
        Finish();
    }

    /// <summary>lambda: 引数の型は delegate の型 ft から、捕捉は caps の名前と中の型。</summary>
    public void CompileLambda(IlClosure c, FuncT ft, IReadOnlyList<(string Name, VType Inner)> caps,
        string file, int line)
    {
        // 式本体の lambda は位置を持たないことがあるので、作った場所の行から始める
        _f.File = file;
        if (line > 0) _f.Lines.Add((0, line));
        object body = (object?)c.Body ?? c.ExprBody!;
        _returnType = ft.Ret;
        if (c.Params.Length != ft.Params.Count) throw new VmException($"{_f.Name}: lambda arity");
        Prologue(body, null, c.Params, ft.Params.ToList());
        for (var i = 0; i < caps.Count; i++)
        {
            _scopes[0][caps[i].Name] = new CapVar(i, caps[i].Inner);
            _f.Caps.Add(_m.Info(new CellT(caps[i].Inner)));
        }
        if (c.Body is { } block) Block(block, newScope: false);
        else
        {
            Line(c.ExprBody!);
            if (ft.Ret is VoidT) Expr(c.ExprBody!, discard: true);
            else EmitReturn(c.ExprBody!);
            EndStatement();
        }
        Finish();
    }

    /// <summary>引数を register に置き、closure が捕捉する引数は cell に移す。outs は out の引数 (local にして
    /// return ごとに返す)、selfOut は struct の self を return ごとに返す。</summary>
    private void Prologue(object body, VType? self, IReadOnlyList<string> names, IReadOnlyList<VType> types,
        IReadOnlyList<(string Name, VType Type)>? outs = null, bool selfOut = false)
    {
        _captured = IlWalk.Captured(body);
        _scopes.Add([]);
        var parameters = new List<(string Name, Val Val)>();
        if (self is not null) parameters.Add(("self", NewVal(self)));
        for (var i = 0; i < names.Count; i++) parameters.Add((names[i], NewVal(types[i])));
        _f.NParams = _f.Regs.Count;
        if (_returnType is not VoidT)
            foreach (var l in _m.Leaves(_returnType))
                _f.Rets.Add(_m.Info(l.Type));
        foreach (var (name, val) in parameters)
            _scopes[0][name] = _captured.Contains(name) ? NewCell(val) : new RegVar(val);
        if (selfOut)
        {
            _extraRets.Add(_scopes[0]["self"]);
            foreach (var l in _m.Leaves(self!)) _f.Rets.Add(_m.Info(l.Type));
        }
        foreach (var (name, type) in outs ?? [])
        {
            _extraRets.Add(Declare(name, type, null));
            foreach (var l in _m.Leaves(type)) _f.Rets.Add(_m.Info(l.Type));
        }
        EndStatement();
    }

    private Var NewCell(Val init)
    {
        var cellType = new CellT(init.Type);
        var reg = NewReg(cellType);
        Emit(Op.NEW, reg, 0, _m.TypeIndex(cellType));
        StoreCell(reg, init.Type, init);
        return new CellVar(reg, init.Type);
    }

    private void Finish()
    {
        // 末尾に落ちる経路があれば終端を足す。値を返す関数の末尾には届かない (C# が保証) が、
        // verifier が通る形にするため型の合う RET を置く
        if (!EndsFlow())
        {
            if (_returnType is VoidT) EmitReturn(null);
            else
            {
                var parts = new List<Val> { ZeroTemp(_returnType) };
                parts.AddRange(_extraRets.Select(ReadVar));
                var regs = parts.SelectMany(p => p.Regs).ToArray();
                var b = Block(LeafTypes(parts.Select(p => p.Type)));
                for (var i = 0; i < regs.Length; i++) Emit(Op.MOV, b + i, regs[i]);
                Emit(Op.RET, b);
            }
        }
        _f.Code = _code;
        if (_f.Regs.Count > 0xFFFF) throw new VmException($"{_f.Name}: too many registers");
    }

    private bool EndsFlow()
    {
        if (_code.Count == 0 || _labelTargets.Contains(_code.Count)) return false;
        return (Op)(_code[^1] & 0xFF) is Op.RET or Op.RET0 or Op.JMP;
    }

    // ---- register ----

    private int NewReg(VType leafType)
    {
        _f.Regs.Add(_m.Info(leafType));
        return _f.Regs.Count - 1;
    }

    private Val NewVal(VType t) => new(t, _m.Leaves(t).Select(l => NewReg(l.Type)).ToArray());

    private Val Temp(VType t)
    {
        var leaves = _m.Leaves(t);
        var regs = new int[leaves.Count];
        for (var i = 0; i < regs.Length; i++)
        {
            var key = _m.Info(leaves[i].Type);
            regs[i] = _free.TryGetValue(key, out var stack) && stack.Count > 0 ? stack.Pop() : NewReg(leaves[i].Type);
            _temps.Add(regs[i]);
        }
        return new Val(t, regs);
    }

    /// <summary>文の終わり: その文の temp と呼び出しの引数の区画を空きに戻す。</summary>
    private void EndStatement()
    {
        foreach (var r in _temps)
        {
            var key = _f.Regs[r];
            if (!_free.TryGetValue(key, out var stack)) _free[key] = stack = new Stack<int>();
            stack.Push(r);
        }
        _temps.Clear();
        foreach (var (key, b) in _blocksInUse)
        {
            if (!_freeBlocks.TryGetValue(key, out var s)) _freeBlocks[key] = s = new Stack<int>();
            s.Push(b);
        }
        _blocksInUse.Clear();
    }

    /// <summary>連続する register の区画 (呼び出しの引数と戻り値)。同じ並びの区画を文の間で使い回す。</summary>
    private int Block(IReadOnlyList<VType> leafTypes)
    {
        if (leafTypes.Count == 0) return 0;
        var key = string.Join(",", leafTypes.Select(t => _m.Info(t)));
        int b;
        if (_freeBlocks.TryGetValue(key, out var s) && s.Count > 0)
            b = s.Pop();
        else
        {
            b = _f.Regs.Count;
            foreach (var t in leafTypes) NewReg(t);
        }
        _blocksInUse.Add((key, b));
        return b;
    }

    private List<VType> LeafTypes(IEnumerable<VType> types) =>
        types.Where(t => t is not VoidT).SelectMany(t => _m.Leaves(t).Select(l => l.Type)).ToList();

    // ---- 命令 ----

    private void Emit(Op op, int a = 0, int b = 0, int c = 0, int x = 0)
    {
        if ((uint)a > 0xFFFF || (uint)b > 0xFFFF || (uint)c > 0xFFFF || (uint)x > 0xFF)
            throw new VmException($"{_f.Name}: operand out of range");
        _code.Add((ulong)op | (ulong)a << 8 | (ulong)b << 24 | (ulong)c << 40 | (ulong)x << 56);
    }

    private void EmitImm(Op op, int a, uint imm) =>
        _code.Add((ulong)op | (ulong)(ushort)a << 8 | (ulong)imm << 24);

    private sealed class Label
    {
        public int Target = -1;
        public readonly List<int> Patches = [];
    }

    private void Mark(Label l)
    {
        l.Target = _code.Count;
        _labelTargets.Add(_code.Count);
        foreach (var at in l.Patches) Patch(at, l.Target);
    }

    private void Patch(int at, int target)
    {
        var off = target - (at + 1);
        if (off < -(1 << 23) || off >= 1 << 23) throw new VmException($"{_f.Name}: jump too far");
        _code[at] = (_code[at] & 0xFFFFFFFFFFUL) | (ulong)(uint)(off & 0xFFFFFF) << 40;
    }

    private void Jump(Op op, Label l, int a = 0, int b = 0)
    {
        var at = _code.Count;
        _code.Add((ulong)op | (ulong)(ushort)a << 8 | (ulong)(ushort)b << 24);
        if (l.Target >= 0) Patch(at, l.Target);
        else l.Patches.Add(at);
    }

    private void Line(IlNode node)
    {
        if (node.Origin is not { } origin) return;
        var span = origin.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        if (_f.File == "") _f.File = Path.GetFileName(span.Path);
        if (line == _lastLine) return;
        _lastLine = line;
        if (_f.Lines.Count > 0 && _f.Lines[^1].Pc == _code.Count)
            _f.Lines[^1] = (_code.Count, line);
        else
            _f.Lines.Add((_code.Count, line));
    }

    // ---- 変数 ----

    private Var? Lookup(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var v))
                return v;
        return null;
    }

    /// <summary>新しい変数を宣言する。closure が捕捉する名前なら cell に住む。</summary>
    private Var Declare(string name, VType type, IlExpr? init)
    {
        Var v;
        if (_captured.Contains(name))
        {
            var val = init is null ? ZeroTemp(type) : ExprAs(init, type);
            v = NewCell(val);
        }
        else
        {
            var val = NewVal(type);
            if (init is not null) ExprAs(init, type, val);
            else EmitZero(val);
            v = new RegVar(val);
        }
        _scopes[^1][name] = v;
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
        switch (v)
        {
            case CellVar c:
                return c.Reg;
            case CapVar cap:
            {
                var t = Temp(new CellT(cap.Inner));
                Emit(Op.LDCAP, t.Reg, 0, cap.Index);
                return t.Reg;
            }
            default:
                throw new VmException("not a cell");
        }
    }

    private void StoreCell(int cell, VType inner, Val v)
    {
        var leaves = _m.Leaves(inner);
        for (var k = 0; k < leaves.Count; k++) Emit(Pick(Op.STF_I32, leaves[k].Kind), cell, v.Regs[k], k);
    }

    // ---- 文 ----

    private void Block(IlBlock block, bool newScope = true)
    {
        if (newScope) _scopes.Add([]);
        foreach (var s in block.Stats) Stat(s);
        if (newScope) _scopes.RemoveAt(_scopes.Count - 1);
    }

    private void Stat(IlStat s)
    {
        Line(s);
        switch (s)
        {
            case IlLocal { Name: "_", Type: null, Init: null }:
                break;
            case IlLocal local:
            {
                var type = local.Type is { } t ? _m.Parse(t)
                    : local.Init is { } init0 ? TypeOf(init0)
                    : throw new VmException($"{_f.Name}: local {local.Name} has no type");
                Declare(local.Name, type, local.Init);
                break;
            }
            case IlAssign assign:
                Assign(assign.Target, assign.Value);
                break;
            case IlCallStat call:
                Expr(call.Call, discard: true);
                break;
            case IlIf iff:
            {
                var end = new Label();
                foreach (var (cond, body) in iff.Arms)
                {
                    var next = new Label();
                    Cond(cond, false, next);
                    EndStatement();
                    Block(body);
                    Jump(Op.JMP, end);
                    Mark(next);
                }
                if (iff.Else is { } e) Block(e);
                Mark(end);
                break;
            }
            case IlWhile w:
            {
                var top = new Label();
                var cont = new Label();
                var exit = new Label();
                Mark(top);
                Cond(w.Cond, false, exit);
                EndStatement();
                _loops.Add((exit, cont));
                Block(w.Body);
                _loops.RemoveAt(_loops.Count - 1);
                Mark(cont);
                if (w.Trailer is { } trailer) Block(trailer);
                Jump(Op.JMP, top);
                Mark(exit);
                break;
            }
            case IlRepeat r:
            {
                var top = new Label();
                var cont = new Label();
                var exit = new Label();
                Mark(top);
                _loops.Add((exit, cont));
                Block(r.Body);
                _loops.RemoveAt(_loops.Count - 1);
                Mark(cont);
                Cond(r.Cond, true, top);
                EndStatement();
                Mark(exit);
                break;
            }
            case IlNumericFor nf:
                NumericFor(nf);
                break;
            case IlForeachList fl:
                ForeachList(fl);
                break;
            case IlBreak:
                if (_loops.Count == 0 && _breakScopes.Count == 0) throw new VmException("break outside loop");
                Jump(Op.JMP, _breakScopes.Count > 0 ? _breakScopes[^1] : _loops[^1].Break);
                break;
            case IlContinue:
                Jump(Op.JMP, _loops[^1].Continue);
                break;
            case IlReturn ret when _iifes.Count > 0:
            {
                var (result, end, type) = _iifes[^1];
                if (ret.Value is not null && result is not null) ExprAs(ret.Value, type, result);
                Jump(Op.JMP, end);
                break;
            }
            case IlReturn ret:
                EmitReturn(ret.Value);
                break;
            case IlMultiAssign ma:
                MultiAssign(ma);
                break;
            case IlForeachDict fd:
                ForeachDict(fd);
                break;
            case IlForeachRunes fr:
                ForeachRunes(fr);
                break;
            case IlForPairs { VVar: null, Body.Stats: [IlAssign { Target: IlIndex { Recv: IlVar cv, Idx: IlVar ck }, Value: IlLit { LuaText: "nil" } }] } fp
                when fp.Coll is IlVar cv2 && cv2.Name == cv.Name && ck.Name == fp.KVar:
            {
                // List.Clear / Dictionary.Clear の lowering (全部の key に nil を入れる)
                var ct = TypeOf(fp.Coll);
                var coll = Expr(fp.Coll);
                if (ct is ListT) Emit(Op.LIST_CLEAR, coll.Reg);
                else if (ct is DictT) Emit(Op.CALLR, coll.Reg, 0, _m.Intrinsic("dict.clear"));
                else throw new VmException($"{_f.Name}: clear of {ct}");
                break;
            }
            case IlDo d:
                Block(d.Body);
                break;
            case IlBreakScope bs:
            {
                var end = new Label();
                _breakScopes.Add(end);
                Block(bs.Body);
                _breakScopes.RemoveAt(_breakScopes.Count - 1);
                Mark(end);
                break;
            }
            default:
                throw new VmException($"{_f.Name}: statement {s.GetType().Name} is not in the VM yet");
        }
        EndStatement();
    }

    /// <summary>return: 戻り値の葉に、struct の self と out の引数の今の値を続けて返す。</summary>
    private void EmitReturn(IlExpr? value)
    {
        var parts = new List<Val>();
        if (value is not null) parts.Add(ExprAs(value, _returnType));
        foreach (var extra in _extraRets) parts.Add(ReadVar(extra));
        if (parts.Count == 0)
        {
            Emit(Op.RET0);
            return;
        }
        var regs = parts.SelectMany(p => p.Regs).ToArray();
        var b = Block(LeafTypes(parts.Select(p => p.Type)));
        for (var i = 0; i < regs.Length; i++)
            if (b + i != regs[i]) Emit(Op.MOV, b + i, regs[i]);
        Emit(Op.RET, b);
    }

    private Val ReadVar(Var v) => v switch
    {
        RegVar r => r.Val,
        _ => LoadPlace(new Place(v.Type, "field", Obj: new Val(new CellT(v.Type), [CellReg(v)])), null),
    };

    private int Contiguous(Val v)
    {
        var ok = true;
        for (var i = 1; i < v.Regs.Length; i++) ok &= v.Regs[i] == v.Regs[0] + i;
        if (ok) return v.Regs[0];
        var b = Block(LeafTypes([v.Type]));
        for (var i = 0; i < v.Regs.Length; i++) Emit(Op.MOV, b + i, v.Regs[i]);
        return b;
    }

    /// <summary>Lua の numeric for (`for v = start, limit`)。limit は 1 回だけ評価し、
    /// v が本文で書き換えられるときは隠れた counter を回す (il-reference: IlNumericFor)。</summary>
    private void NumericFor(IlNumericFor nf)
    {
        _scopes.Add([]);
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
        _scopes.Add([]);
        if (assigned)
        {
            var v = Declare(nf.Var, VType.Int, null);
            WriteVar(v, counter);
        }
        else _scopes[^1][nf.Var] = new RegVar(counter);
        EndStatement();
        _loops.Add((exit, cont));
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
    /// 長さは反復ごとに読み直す (Lua の ipairs と同じく、本文が足した要素も回る)。</summary>
    private void ForeachList(IlForeachList fl)
    {
        _scopes.Add([]);
        var collType = TypeOf(fl.Coll);
        var elem = collType switch
        {
            ArrayT a => a.Elem,
            ListT l => l.Elem,
            _ => throw new VmException($"{_f.Name}: foreach over {collType} is not in the VM yet"),
        };
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
        Emit(collType is ArrayT ? Op.ARR_LEN : Op.LIST_LEN, n.Reg, coll.Reg);
        Jump(Op.JF_LT_I32, exit, i.Reg, n.Reg);
        _scopes.Add([]);
        var item = LoadPlace(collType is ArrayT
            ? new Place(elem, "elem", Arr: coll, Idx: i, Owner: collType)
            : new Place(elem, "list", Arr: coll, Idx: i, Owner: collType), null);
        var v = _captured.Contains(fl.Var) ? NewCell(item) : new RegVar(CopyToNew(item));
        _scopes[^1][fl.Var] = v;
        EndStatement();
        _loops.Add((exit, cont));
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

    private static bool AssignsVar(IlBlock body, string name) => body.Stats.Any(s => AssignsVar(s, name));

    private static bool AssignsVar(IlStat s, string name) => s switch
    {
        IlAssign { Target: IlVar v } => v.Name == name,
        IlMultiAssign m => m.Targets.Any(t => t is IlVar v && v.Name == name),
        IlLocal l => l.Name == name, // 同名の local が隠す: 保守的に counter を分ける
        IlIf i => i.Arms.Any(a => AssignsVar(a.Body, name)) || (i.Else is { } e && AssignsVar(e, name)),
        IlWhile w => AssignsVar(w.Body, name) || (w.Trailer is { } t && AssignsVar(t, name)),
        IlRepeat r => AssignsVar(r.Body, name),
        IlNumericFor f => f.Var == name || AssignsVar(f.Body, name),
        IlForeachList f => f.Var == name || AssignsVar(f.Body, name),
        IlDo d => AssignsVar(d.Body, name),
        IlBreakScope b => AssignsVar(b.Body, name),
        _ => false,
    };

    // ---- 代入と格納場所 ----

    private void Assign(IlExpr target, IlExpr value)
    {
        while (target is IlParen p) target = p.E;
        if (target is IlVar v && Lookup(v.Name) is { } var)
        {
            if (var is RegVar r)
                // 結果を local へ直接書く (式の最後の命令だけが dest を書くので、右辺が local を読んでもよい)
                ExprAs(value, r.Type, r.Val);
            else
                WriteVar(var, ExprAs(value, var.Type));
            return;
        }
        if (target is IlIndex { PlusOne: false } di && TypeOf(di.Recv) is DictT dt)
        {
            // C# と同じく key、値の順に評価してから入れる
            var dict = Expr(di.Recv);
            var key = ExprAs(di.Idx, dt.Key);
            var dv = ExprAs(value, dt.Value);
            var at = Temp(VType.Int);
            Emit(Op.DICT_INS, at.Reg, dict.Reg, key.Reg);
            StorePlace(new Place(dt.Value, "dict", Arr: dict, Idx: at), dv);
            return;
        }
        var place = PlaceOf(target);
        var val = ExprAs(value, place.Type);
        StorePlace(place, val);
    }

    private void WriteVar(Var v, Val val)
    {
        switch (v)
        {
            case RegVar r:
                Store(r.Val, val);
                break;
            default:
                StoreCell(CellReg(v), v.Type, Convert(val, v.Type));
                break;
        }
    }

    /// <summary>格納場所。Kind は static / field (object か cell の葉) / elem (配列) / list / dict / local。
    /// Leaf はその中の葉の番号の始まり。</summary>
    private sealed record Place(VType Type, string Kind, int Slot = 0, Val? Obj = null, Val? Arr = null,
        Val? Idx = null, int Leaf = 0, VType? Owner = null);

    private Place PlaceOf(IlExpr e)
    {
        switch (e)
        {
            case IlParen p:
                return PlaceOf(p.E);
            case IlVar v when Lookup(v.Name) is { } var:
                return var switch
                {
                    RegVar r => new Place(r.Type, "local", Obj: r.Val),
                    _ => new Place(var.Type, "field", Obj: new Val(new CellT(var.Type), [CellReg(var)])),
                };
            case IlField { Recv: IlVar cls } f when Lookup(cls.Name) is null && _m.IsTypeName(cls.Name):
            {
                var (slot, type) = _m.Static(cls.Name, f.Name);
                return new Place(type, "static", Slot: slot);
            }
            case IlField f:
            {
                var recvType = TypeOf(f.Recv);
                var type = _m.FieldType(recvType, f.Name);
                if (recvType is ClassT c)
                {
                    var obj = Expr(f.Recv);
                    return new Place(type, "field", Obj: obj, Leaf: LeafIndex(_m.ObjectLeaves(c), f.Name));
                }
                // struct の中の field: 外側の場所の葉の範囲を狭める
                var outer = PlaceOf(f.Recv);
                var inner = LeafIndex(_m.Leaves(recvType), f.Name);
                return outer with { Type = type, Leaf = outer.Leaf + inner };
            }
            case IlIndex idx:
            {
                var collType = TypeOf(idx.Recv);
                switch (collType)
                {
                    case ArrayT a:
                    {
                        var arr = Expr(idx.Recv);
                        var i = ExprAs(idx.Idx, VType.Int);
                        return new Place(a.Elem, "elem", Arr: arr, Idx: i, Owner: a);
                    }
                    case ListT l:
                    {
                        var list = Expr(idx.Recv);
                        var i = ExprAs(idx.Idx, VType.Int);
                        return new Place(l.Elem, "list", Arr: list, Idx: i, Owner: l);
                    }
                    case DictT d:
                        return DictPlace(idx, d, insert: false);
                }
                throw new VmException($"{_f.Name}: index of {collType} is not in the VM yet");
            }
            default:
                if (TypeOf(e).IsAggregate)
                {
                    // 値 (呼び出しの結果など) の field: 値を temp に置いて、その中の葉を読む (書いても捨てられる。C# と同じ)
                    var v = CopyToTemp(Expr(e));
                    return new Place(v.Type, "local", Obj: v);
                }
                throw new VmException($"{_f.Name}: place {e.GetType().Name} is not in the VM yet");
        }
    }

    private Val CopyToTemp(Val v)
    {
        var d = Temp(v.Type);
        Store(d, v);
        return d;
    }

    /// <summary>Dictionary の key の entry。読みは無い key で fault (DICT_FIND)、書きは足す (DICT_INS)。</summary>
    private Place DictPlace(IlIndex idx, DictT d, bool insert)
    {
        var dict = Expr(idx.Recv);
        var key = ExprAs(idx.Idx, d.Key);
        var at = Temp(VType.Int);
        Emit(insert ? Op.DICT_INS : Op.DICT_FIND, at.Reg, dict.Reg, key.Reg);
        return new Place(d.Value, "dict", Arr: dict, Idx: at);
    }

    private static int LeafIndex(IReadOnlyList<Leaf> leaves, string field)
    {
        for (var i = 0; i < leaves.Count; i++)
            if (leaves[i].Path == field || leaves[i].Path.StartsWith(field + "."))
                return i;
        throw new VmException($"no leaf {field}");
    }

    private void StorePlace(Place p, Val v)
    {
        var leaves = _m.Leaves(p.Type);
        for (var k = 0; k < leaves.Count; k++)
        {
            var r = v.Regs[k];
            var kind = leaves[k].Kind;
            switch (p.Kind)
            {
                case "static": EmitImm(Op.STS, r, (uint)(p.Slot + p.Leaf + k)); break;
                case "field": Emit(Pick(Op.STF_I32, kind), p.Obj!.Reg, r, p.Leaf + k); break;
                case "elem" when ((ArrayT)p.Owner!).Elem.IsAggregate:
                    Emit(Pick(Op.STEF_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r, p.Leaf + k);
                    break;
                case "elem": Emit(Pick(Op.STE_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r); break;
                case "list" when ((ListT)p.Owner!).Elem.IsAggregate:
                    Emit(Pick(Op.LIST_STF_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r, p.Leaf + k);
                    break;
                case "list": Emit(Pick(Op.LIST_ST_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r); break;
                case "dict": Emit(Pick(Op.DICT_STV_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r, p.Leaf + k); break;
                case "local": Emit(Op.MOV, p.Obj!.Regs[p.Leaf + k], r); break;
            }
        }
    }

    private Val LoadPlace(Place p, Val? dest)
    {
        var leaves = _m.Leaves(p.Type);
        if (p.Kind == "local" && dest is null)
            return new Val(p.Type, p.Obj!.Regs.Skip(p.Leaf).Take(leaves.Count).ToArray());
        var d = dest ?? Temp(p.Type);
        for (var k = 0; k < leaves.Count; k++)
        {
            var r = d.Regs[k];
            var kind = leaves[k].Kind;
            switch (p.Kind)
            {
                case "static": EmitImm(Op.LDS, r, (uint)(p.Slot + p.Leaf + k)); break;
                case "field": Emit(Pick(Op.LDF_I32, kind), r, p.Obj!.Reg, p.Leaf + k); break;
                case "elem" when ((ArrayT)p.Owner!).Elem.IsAggregate:
                    Emit(Pick(Op.LDEF_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg, p.Leaf + k);
                    break;
                case "elem": Emit(Pick(Op.LDE_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg); break;
                case "list" when ((ListT)p.Owner!).Elem.IsAggregate:
                    Emit(Pick(Op.LIST_LDF_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg, p.Leaf + k);
                    break;
                case "list": Emit(Pick(Op.LIST_LD_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg); break;
                case "dict": Emit(Pick(Op.DICT_LDV_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg, p.Leaf + k); break;
                case "local": Emit(Op.MOV, r, p.Obj!.Regs[p.Leaf + k]); break;
            }
        }
        return d;
    }

    /// <summary>I32 / F32 / BOOL / REF の 4 つが並ぶ命令の組から種類で選ぶ。</summary>
    private static Op Pick(Op i32, Kind kind) => (Op)((int)i32 + (int)kind);

    private void Store(Val dest, Val src)
    {
        src = Convert(src, dest.Type);
        for (var k = 0; k < dest.Regs.Length; k++)
            if (dest.Regs[k] != src.Regs[k])
                Emit(Op.MOV, dest.Regs[k], src.Regs[k]);
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

    private VType TypeOf(IlExpr e) => e switch
    {
        IlLit lit => LitType(lit),
        IlVar v => Lookup(v.Name)?.Type ?? throw new VmException($"{_f.Name}: unknown name {v.Name}"),
        IlParen p => TypeOf(p.E),
        IlField { Recv: IlVar { Name: "TinySystem.Random" }, Name: "Shared" } => new RandomT(),
        IlField { Recv: IlVar en } when Lookup(en.Name) is null && _m.IsEnum(en.Name) => VType.Int,
        IlField { Recv: IlVar cls } f when Lookup(cls.Name) is null && _m.IsTypeName(cls.Name)
            => _m.Static(cls.Name, f.Name).Type,
        IlField f => _m.FieldType(TypeOf(f.Recv), f.Name),
        IlIndex i => TypeOf(i.Recv) switch
        {
            ArrayT a => a.Elem,
            ListT l => l.Elem,
            DictT d => d.Value,
            var t => throw new VmException($"{_f.Name}: index of {t}"),
        },
        IlLen => VType.Int,
        IlBin b => BinType(b),
        IlUn { Op: IlUnOp.Not } => VType.Bool,
        IlUn { Op: IlUnOp.ToFloat } => VType.Float,
        IlUn u => TypeOf(u.E),
        IlCall c => CallType(c),
        IlInvoke { Method: "NextFloat" or "NextSingle" } inv when TypeOf(inv.Recv) is RandomT => VType.Float,
        IlInvoke inv when TypeOf(inv.Recv) is RandomT => VType.Int,
        IlInvoke inv => _m.Parse(InstanceMethod(TypeOf(inv.Recv), inv.Method).ReturnType),
        IlDynCall d when StaticRef(d.Callee) is { } sr => _m.Parse(sr.Method.ReturnType),
        IlDynCall d => TypeOf(d.Callee) is FuncT ft ? ft.Ret : throw new VmException($"{_f.Name}: call of non-delegate"),
        IlNewObj n => _m.Parse(n.TypeName),
        IlNewQueue q => _m.Parse(q.Type),
        IlNewHashSet h => _m.Parse(h.Type),
        IlNewArray a => new ArrayT(_m.Parse(a.ElementType)),
        IlTable t => TableType(t),
        IlNumericConvert nc => _m.Parse(nc.TargetType),
        IlStructCopy sc => TypeOf(sc.E),
        IlTernary t => TypeOf(t.T) is NullT ? TypeOf(t.F) : TypeOf(t.T),
        IlIife iife => IifeType(iife),
        IlIsType or IlIsLuaType or IlNullableHasValue => VType.Bool,
        IlCast c => _m.Parse(c.TypeRef),
        IlRefCast rc => _m.Parse(rc.TargetType),
        IlNullableWrap w => new NullableT(_m.Parse(w.Type)),
        IlNullableValue nv => TypeOf(nv.E) is NullableT nt ? nt.Inner : TypeOf(nv.E),
        IlNullableGetOrDefault g => TypeOf(g.Default) is NullableT or NullT ? TypeOf(g.E) is NullableT ne && TypeOf(g.Default) is NullT ? ne : TypeOf(g.Default) is NullT ? TypeOf(g.E) : TypeOf(g.Default)
            : TypeOf(g.Default),
        IlLiftedBin lb => LiftedType(lb),
        IlLiftedUn { Op: IlUnOp.ToFloat } => new NullableT(VType.Float),
        IlLiftedUn lu => TypeOf(lu.E),
        IlWith w => TypeOf(w.Src),
        _ => throw new VmException($"{_f.Name}: expression {e.GetType().Name} is not in the VM yet"),
    };

    private VType TableType(IlTable t)
    {
        if (t.ElementType is null) throw new VmException($"{_f.Name}: table without element type");
        var elem = _m.Parse(t.ElementType);
        if (t.KeyType is { } k) return new DictT(_m.Parse(k), elem);
        return t.IsArray ? new ArrayT(elem) : new ListT(elem);
    }

    private static VType LitType(IlLit lit)
    {
        if (lit.Type == "float") return VType.Float;
        if (lit.Type is "int" or "char") return VType.Int;
        var t = lit.LuaText;
        if (t is "true" or "false") return VType.Bool;
        if (t == "nil") return VType.Null;
        if (t.StartsWith('"') || t.StartsWith('\'')) return VType.String;
        if (t.StartsWith("0x") || t.StartsWith("0X")) return VType.Int;
        return t.Contains('.') || t.Contains('e') || t.Contains('E') ? VType.Float : VType.Int;
    }

    private VType BinType(IlBin b) => b.Op switch
    {
        IlBinOp.Eq or IlBinOp.Ne or IlBinOp.Lt or IlBinOp.Le or IlBinOp.Gt or IlBinOp.Ge => VType.Bool,
        // Lua の `a or b` は、ref の値なら null の合体 (IL の `??` の名残り)
        IlBinOp.And or IlBinOp.Or => TypeOf(b.L) is { IsRef: true } l ? l : VType.Bool,
        IlBinOp.BitAnd or IlBinOp.BitOr or IlBinOp.BitXor or IlBinOp.Shl or IlBinOp.Shr => TypeOf(b.L),
        IlBinOp.DivNum => VType.Float,
        IlBinOp.Concat => VType.String,
        _ => TypeOf(b.L) == VType.Float || TypeOf(b.R) == VType.Float ? VType.Float : VType.Int,
    };

    private VType CallType(IlCall c) => c.Callee switch
    {
        "__tcs_irem" or "__tcs_idiv" or "__tcs_trunc" or "__tcs_cadd" or "__tcs_csub" or "__tcs_cmul"
            or "__tcs_cneg" or "Math.CompareTo" or "Dict.Count" => VType.Int,
        "tostring" or "__tcs_fstr" or "__tcs_bstr" or "__tcs_nstr" => VType.String,
        "print" or "table.insert" => VType.Void,
        "List.Sort" when c.Args.Length == 2 => VType.Void,
        "Dict.ContainsKey" => VType.Bool,
        _ when c.Callee.EndsWith(".op_Equality") => VType.Bool,
        _ when StructMember(c.Callee) is { } sm => sm.Ctor is not null ? new StructT(sm.Struct) : _m.Parse(sm.Method!.ReturnType),
        _ when LibraryType(c) is { } lt => lt,
        _ => _m.Parse(StaticMethod(c.Callee).Method.ReturnType),
    };

    private (string Class, IlMethodInfo Method) StaticMethod(string callee)
    {
        var dot = callee.LastIndexOf('.');
        if (dot < 0) throw new VmException($"{_f.Name}: call '{callee}' is not in the VM yet");
        var cls = callee[..dot];
        var name = callee[(dot + 1)..];
        if (!_m.Classes.TryGetValue(cls, out var info))
            throw new VmException($"{_f.Name}: call '{callee}' is not in the VM yet");
        var method = info.Methods.SingleOrDefault(m => m.Name == name && m.IsStatic)
            ?? info.Methods.SingleOrDefault(m => m.Name == name)
            ?? throw new VmException($"{_f.Name}: no static method {callee}");
        return (cls, method);
    }

    private IlMethodInfo InstanceMethod(VType recv, string name)
    {
        if (recv is IfaceT it)
            return _m.Classes[it.Name].Methods.SingleOrDefault(m => m.Name == name)
                ?? throw new VmException($"{_f.Name}: no method {it.Name}.{name}");
        if (recv is not ClassT c) throw new VmException($"{_f.Name}: method {name} on {recv} is not in the VM yet");
        return _m.Classes[c.Name].Methods.SingleOrDefault(m => m.Name == name && !m.IsStatic)
            ?? throw new VmException($"{_f.Name}: no method {c.Name}.{name}");
    }

    // ---- 式 ----

    /// <summary>型 to の値として評価する。null の literal、lambda、method group は型 to から作る。</summary>
    private Val ExprAs(IlExpr e, VType to, Val? dest = null)
    {
        while (e is IlParen p) e = p.E;
        Val v;
        switch (e)
        {
            case IlLit { LuaText: "nil" } when to is NullableT:
            {
                v = dest ?? Temp(to);
                EmitZero(v);
                return v;
            }
            case IlLit { LuaText: "nil" }:
            {
                if (!to.IsRef) throw new VmException($"{_f.Name}: null as {to}");
                v = dest ?? Temp(to);
                Emit(Op.LDNULL, v.Reg);
                return v;
            }
            case IlClosure c:
                return Closure(c, to as FuncT ?? throw new VmException($"{_f.Name}: lambda as {to}"), dest);
            case IlMethodGroup g:
                return MethodGroup(g, to as FuncT ?? throw new VmException($"{_f.Name}: method group as {to}"), dest);
        }
        if (dest is not null && TypeOf(e) == to) return Expr(e, dest);
        v = Convert(Expr(e), to);
        if (dest is null) return v;
        Store(dest, v);
        return dest;
    }

    private Val Convert(Val v, VType to)
    {
        if (v.Type == to || to is VoidT) return v;
        if (v.Type == VType.Int && to == VType.Float)
        {
            var t = Temp(VType.Float);
            Emit(Op.I2F, t.Reg, v.Reg);
            return t;
        }
        if (v.Type is NullT && to.IsRef)
        {
            var t = Temp(to);
            Emit(Op.LDNULL, t.Reg);
            return t;
        }
        if (v.Type is NullT && to is NullableT)
            return ZeroTemp(to);
        if (to is NullableT nt && !v.Type.IsRef && v.Type is not NullableT)
        {
            // T → T? (il-spec §3 の IlNullableWrap と同じ)
            var inner = Convert(v, nt.Inner);
            var t = Temp(to);
            EmitImm(Op.LDK, t.Regs[0], 1);
            for (var k = 0; k < inner.Regs.Length; k++) Emit(Op.MOV, t.Regs[k + 1], inner.Regs[k]);
            return t;
        }
        if (to is ObjectT && v.Type is PrimT p)
        {
            // 値型を object に入れる (box)
            var box = new BoxT(p.Kind);
            var t = Temp(to);
            Emit(Pick(Op.BOX_I32, p.Kind), t.Reg, v.Reg, _m.TypeIndex(box));
            return t;
        }
        if (to.IsRef && v.Type.IsRef)
        {
            var t = Temp(to);
            if (to is ObjectT || (to is IfaceT && v.Type is ClassT))
                Emit(Op.MOV, t.Reg, v.Reg); // 広げる変換は検査が通す
            else
                // 狭める暗黙の変換は pattern の local への代入 (`q = s; q is Sq`)。合わなければ null にして型を保つ
                Emit(Op.ASCAST, t.Reg, v.Reg, _m.TypeIndex(to));
            return t;
        }
        throw new VmException($"{_f.Name}: cannot convert {v.Type} to {to}");
    }

    private Val Expr(IlExpr e, Val? dest = null, bool discard = false)
    {
        switch (e)
        {
            case IlParen p:
                return Expr(p.E, dest, discard);
            case IlLit lit:
            {
                var type = LitType(lit);
                if (type is NullT) throw new VmException($"{_f.Name}: null literal without a type");
                var d = dest ?? Temp(type);
                if (type is StringT) EmitImm(Op.LDSTR, d.Reg, _m.Const(LuaString(lit.LuaText)));
                else EmitImm(Op.LDK, d.Reg, LitBits(lit, type));
                return d;
            }
            case IlVar v:
            {
                var var = Lookup(v.Name) ?? throw new VmException($"{_f.Name}: unknown name {v.Name}");
                if (var is RegVar r)
                {
                    if (dest is null) return r.Val;
                    Store(dest, r.Val);
                    return dest;
                }
                return LoadPlace(PlaceOf(e), dest);
            }
            case IlField { Recv: IlVar { Name: "TinySystem.Random" }, Name: "Shared" }:
                return SharedRandom(dest);
            case IlNewQueue nq:
            {
                var t = _m.Parse(nq.Type);
                var d = dest ?? Temp(t);
                Emit(Op.NEWLIST, d.Reg, 0, _m.TypeIndex(t));
                return d;
            }
            case IlNewHashSet nh:
            {
                var t = _m.Parse(nh.Type);
                var d = dest ?? Temp(t);
                Emit(Op.NEWDICT, d.Reg, 0, _m.TypeIndex(t));
                return d;
            }
            case IlField { Recv: IlVar en } f when Lookup(en.Name) is null && _m.IsEnum(en.Name):
            {
                var d = dest ?? Temp(VType.Int);
                EmitImm(Op.LDK, d.Reg, (uint)_m.EnumValue(en.Name, f.Name));
                return d;
            }
            case IlField or IlIndex:
                return LoadPlace(PlaceOf(e), dest);
            case IlLen len:
            {
                var t = TypeOf(len.E);
                var op = t switch
                {
                    ArrayT => Op.ARR_LEN,
                    ListT => Op.LIST_LEN,
                    DictT => Op.DICT_LEN,
                    _ => throw new VmException($"{_f.Name}: length of {t} is not in the VM yet"),
                };
                var coll = Expr(len.E);
                var d = dest ?? Temp(VType.Int);
                Emit(op, d.Reg, coll.Reg);
                return d;
            }
            case IlBin b:
                return Bin(b, dest);
            case IlUn u:
                return Un(u, dest);
            case IlCall c:
                return Call(c, dest, discard);
            case IlInvoke inv:
                return Invoke(inv, dest);
            case IlDynCall dc:
                return DynCall(dc, dest);
            case IlNewObj n:
                return NewObj(n, dest);
            case IlNewArray a:
            {
                var type = new ArrayT(_m.Parse(a.ElementType));
                var len = ExprAs(a.Length, VType.Int);
                var d = dest ?? Temp(type);
                Emit(Op.NEWARR, d.Reg, len.Reg, _m.TypeIndex(type));
                return d;
            }
            case IlTable t:
                return Table(t, TableType(t), dest);
            case IlTernary t:
            {
                var type = TypeOf(t);
                var d = dest ?? Temp(type);
                var no = new Label();
                var end = new Label();
                Cond(t.Cond, false, no);
                ExprAs(t.T, type, d);
                Jump(Op.JMP, end);
                Mark(no);
                ExprAs(t.F, type, d);
                Mark(end);
                return d;
            }
            case IlStructCopy sc:
                // register は値なので、copy 地点 (il-spec §10) は評価そのもの。`__copy` 関数は要らない
                if (sc.Nullable) throw new VmException($"{_f.Name}: nullable struct is not in the VM yet");
                return Expr(sc.E, dest);
            case IlNumericConvert nc:
            {
                var to = _m.Parse(nc.TargetType);
                var v = Expr(nc.Value);
                if (v.Type.IsRef && to is PrimT up)
                {
                    // object からの unbox (実行時の型が違えば fault)
                    var ud = dest ?? Temp(to);
                    Emit(Pick(Op.UNBOX_I32, up.Kind), ud.Reg, v.Reg, _m.TypeIndex(new BoxT(up.Kind)));
                    return ud;
                }
                if (v.Type is NullableT vn && to is NullableT tn)
                {
                    // int? → float? (値があれば変換)
                    var nd = dest ?? Temp(to);
                    var none = new Label();
                    var end = new Label();
                    Jump(Op.JF, none, v.Regs[0]);
                    var conv = Convert(new Val(vn.Inner, v.Regs[1..]), tn.Inner);
                    EmitImm(Op.LDK, nd.Regs[0], 1);
                    for (var k = 0; k < conv.Regs.Length; k++) Emit(Op.MOV, nd.Regs[k + 1], conv.Regs[k]);
                    Jump(Op.JMP, end);
                    Mark(none);
                    EmitZero(nd);
                    Mark(end);
                    return nd;
                }
                if (v.Type == to)
                {
                    if (dest is null) return v;
                    Store(dest, v);
                    return dest;
                }
                var d = dest ?? Temp(to);
                if (v.Type == VType.Int && to == VType.Float) Emit(Op.I2F, d.Reg, v.Reg);
                else if (v.Type == VType.Float && to == VType.Int) Emit(Op.F2I, d.Reg, v.Reg);
                else throw new VmException($"{_f.Name}: conversion {v.Type} -> {to} is not in the VM yet");
                return d;
            }
            case IlIife iife:
                return Iife(iife, dest);
            case IlIsType it:
            {
                var v = Expr(it.E);
                var d = dest ?? Temp(VType.Bool);
                if (!v.Type.IsRef)
                {
                    EmitImm(Op.LDK, d.Reg, 0); // 値型は class / interface ではない (TCS1001 が外す形の残り)
                    return d;
                }
                Emit(Op.ISTYPE, d.Reg, v.Reg, _m.TypeIndex(_m.Parse(it.TypeRef)));
                return d;
            }
            case IlIsLuaType lt:
            {
                var v = Expr(lt.E);
                var d = dest ?? Temp(VType.Bool);
                VType? target = lt.LuaType switch
                {
                    "string" => VType.String,
                    "boolean" => new BoxT(Kind.Bool),
                    "integer" => new BoxT(Kind.I32),
                    "float" => new BoxT(Kind.F32),
                    _ => null,
                };
                if (target is null) throw new VmException($"{_f.Name}: type test {lt.LuaType} is not in the VM yet");
                Emit(Op.ISTYPE, d.Reg, v.Reg, _m.TypeIndex(target));
                return d;
            }
            case IlCast c:
            {
                var to = _m.Parse(c.TypeRef);
                var v = Expr(c.E);
                var d = dest ?? Temp(to);
                Emit(Op.CAST, d.Reg, v.Reg, _m.TypeIndex(to));
                return d;
            }
            case IlRefCast rc:
            {
                var to = _m.Parse(rc.TargetType);
                var v = Expr(rc.Value);
                if (to is PrimT p)
                {
                    var d0 = dest ?? Temp(to);
                    Emit(Pick(Op.UNBOX_I32, p.Kind), d0.Reg, v.Reg, _m.TypeIndex(new BoxT(p.Kind)));
                    return d0;
                }
                var d = dest ?? Temp(to);
                Emit(Op.CAST, d.Reg, v.Reg, _m.TypeIndex(to));
                return d;
            }
            case IlNullableWrap w:
            {
                var inner = _m.Parse(w.Type);
                var v = ExprAs(w.E, inner);
                var d = dest ?? Temp(new NullableT(inner));
                EmitImm(Op.LDK, d.Regs[0], 1);
                for (var k = 0; k < v.Regs.Length; k++)
                    if (d.Regs[k + 1] != v.Regs[k]) Emit(Op.MOV, d.Regs[k + 1], v.Regs[k]);
                return d;
            }
            case IlNullableHasValue hv:
            {
                var v = Expr(hv.E);
                var d = dest ?? Temp(VType.Bool);
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
            case IlNullableValue nv:
            {
                var v = Expr(nv.E);
                if (v.Type is not NullableT nt) return dest is null ? v : Store2(dest, v);
                Emit(Op.NVAL, v.Regs[0]);
                var inner = new Val(nt.Inner, v.Regs[1..]);
                return dest is null ? inner : Store2(dest, inner);
            }
            case IlNullableGetOrDefault g:
                return NullableOr(g, dest);
            case IlLiftedBin lb:
                return LiftedBin(lb, dest);
            case IlLiftedUn lu:
                return LiftedUn(lu, dest);
            case IlWith w:
                return With(w, dest);
            case IlClosure or IlMethodGroup:
                throw new VmException($"{_f.Name}: delegate without a target type");
            default:
                throw new VmException($"{_f.Name}: expression {e.GetType().Name} is not in the VM yet");
        }
    }

    /// <summary>IL の string literal は Lua の literal の text (`"a\"b\n"`、10 進の `\ddd`)。octet 列に戻す。</summary>
    private static byte[] LuaString(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
            throw new VmException($"string literal {text} is not in the VM yet");
        var bytes = new List<byte>();
        var plain = new StringBuilder();
        void FlushPlain()
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(plain.ToString()));
            plain.Clear();
        }
        for (var i = 1; i < text.Length - 1; i++)
        {
            var ch = text[i];
            if (ch != '\\')
            {
                plain.Append(ch);
                continue;
            }
            FlushPlain();
            var n = text[++i];
            switch (n)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'v': bytes.Add(11); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case '"': bytes.Add((byte)'"'); break;
                case '\'': bytes.Add((byte)'\''); break;
                case 'x':
                    bytes.Add(byte.Parse(text.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                default:
                    if (char.IsAsciiDigit(n))
                    {
                        var j = i;
                        while (j < i + 3 && j < text.Length - 1 && char.IsAsciiDigit(text[j])) j++;
                        bytes.Add((byte)int.Parse(text[i..j], CultureInfo.InvariantCulture));
                        i = j - 1;
                        break;
                    }
                    throw new VmException($"string escape \\{n} is not in the VM yet");
            }
        }
        FlushPlain();
        return bytes.ToArray();
    }

    private static uint LitBits(IlLit lit, VType type)
    {
        var t = lit.LuaText;
        if (type == VType.Bool) return t == "true" ? 1u : 0u;
        if (type == VType.Float)
        {
            // IL の f32 literal は 10 進の text。.NET の float.Parse は最近接への丸め (il-spec §6)
            var f = float.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            return BitConverter.SingleToUInt32Bits(f);
        }
        if (t.StartsWith("0x") || t.StartsWith("0X"))
            return uint.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (uint)int.Parse(t, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private Val Bin(IlBin b, Val? dest)
    {
        var resultType = BinType(b);
        if (b.Op is IlBinOp.Or or IlBinOp.And && resultType.IsRef)
        {
            if (b.Op == IlBinOp.And) throw new VmException($"{_f.Name}: `and` on references");
            // null の合体: 左が null でなければ左、null なら右 (右は必要なときだけ評価)
            var d = dest ?? Temp(resultType);
            var end = new Label();
            ExprAs(b.L, resultType, d);
            Jump(Op.JNN, end, d.Reg);
            ExprAs(b.R, resultType, d);
            Mark(end);
            return d;
        }
        if (b.Op == IlBinOp.Concat)
        {
            var l = ToStr(b.L);
            var r = ToStr(b.R);
            var d = dest ?? Temp(VType.String);
            Emit(Op.CONCAT, d.Reg, l.Reg, r.Reg);
            return d;
        }
        if (b.Op is IlBinOp.Eq or IlBinOp.Ne or IlBinOp.Lt or IlBinOp.Le or IlBinOp.Gt or IlBinOp.Ge
            or IlBinOp.And or IlBinOp.Or)
        {
            // bool の値: 比較は EQ / LT / LE、論理は分岐で作る
            var d = dest ?? Temp(VType.Bool);
            if (b.Op is IlBinOp.And or IlBinOp.Or)
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
            var (l, r, t) = Operands(b);
            var (op, swap, negate) = Compare(b.Op, t);
            Emit(op, d.Reg, swap ? r.Reg : l.Reg, swap ? l.Reg : r.Reg);
            if (negate) Emit(Op.NOT_B, d.Reg, d.Reg);
            return d;
        }
        {
            var (l, r, t) = Operands(b, resultType);
            var d = dest ?? Temp(resultType);
            var op = (b.Op, t == VType.Float) switch
            {
                (IlBinOp.AddNum, false) => Op.ADD_I32,
                (IlBinOp.Sub, false) => Op.SUB_I32,
                (IlBinOp.Mul, false) => Op.MUL_I32,
                (IlBinOp.BitAnd, false) => Op.AND_I32,
                (IlBinOp.BitOr, false) => Op.OR_I32,
                (IlBinOp.BitXor, false) => Op.XOR_I32,
                (IlBinOp.Shl, false) => Op.SHL_I32,
                (IlBinOp.Shr, false) => Op.SHR_I32,
                (IlBinOp.AddNum, true) => Op.ADD_F32,
                (IlBinOp.Sub, true) => Op.SUB_F32,
                (IlBinOp.Mul, true) => Op.MUL_F32,
                (IlBinOp.DivNum, true) => Op.DIV_F32,
                _ => throw new VmException($"{_f.Name}: operator {b.Op} on {t} is not in the VM yet"),
            };
            Emit(op, d.Reg, l.Reg, r.Reg);
            return d;
        }
    }

    /// <summary>文字列化 (il-spec §13): int は 10 進、float は runtime/fmt、bool は True / False、string の null は空文字列。</summary>
    private Val ToStr(IlExpr e)
    {
        while (e is IlParen p) e = p.E;
        if (e is IlCall { Callee: "tostring" or "__tcs_fstr" or "__tcs_bstr" or "__tcs_nstr" } c && c.Args.Length == 1) e = c.Args[0];
        var t = TypeOf(e);
        if (t is StringT or NullT) return ExprAs(e, VType.String);
        if (t is NullableT nt)
        {
            // T? の値なしは空文字列 (C# の補間と同じ)
            var nv = Expr(e);
            var d0 = Temp(VType.String);
            var none = new Label();
            var end = new Label();
            Jump(Op.JF, none, nv.Regs[0]);
            Store(d0, ToStrVal(new Val(nt.Inner, nv.Regs[1..])));
            Jump(Op.JMP, end);
            Mark(none);
            EmitImm(Op.LDSTR, d0.Reg, _m.Const([]));
            Mark(end);
            return d0;
        }
        if (t is ObjectT or IfaceT or BoxT)
        {
            var ov = Expr(e);
            var d1 = Temp(VType.String);
            Emit(Op.CALLR, ov.Reg, d1.Reg, _m.Intrinsic("object.tostring"));
            return d1;
        }
        if (t is ClassT ct && _m.Classes[ct.Name].Methods.FirstOrDefault(m => m.Name == "ToString" && !m.IsStatic) is { } ts)
        {
            // ToString の override
            return EmitCall(Op.CALL, RequestMethod(ct.Name, ts), e, ts, [], null);
        }
        return ToStrVal(Expr(e));
    }

    private Val ToStrVal(Val v)
    {
        var t = v.Type;
        if (t is StringT) return v;
        var d = Temp(VType.String);
        if (t == VType.Int) Emit(Op.I2S, d.Reg, v.Reg);
        else if (t == VType.Float) Emit(Op.F2S, d.Reg, v.Reg);
        else if (t == VType.Bool) Emit(Op.B2S, d.Reg, v.Reg);
        else throw new VmException($"{_f.Name}: ToString of {t} is not in the VM yet");
        return d;
    }

    /// <summary>二項の operand を評価し、int と float が混ざれば int を float に揃える (C# の暗黙変換)。</summary>
    private (Val L, Val R, VType T) Operands(IlBin b, VType? want = null)
    {
        var lt = TypeOf(b.L);
        var rt = TypeOf(b.R);
        var t = want == VType.Float || lt == VType.Float || rt == VType.Float ? VType.Float
            : lt is NullT ? rt : lt;
        if (b.Op is IlBinOp.Shl or IlBinOp.Shr) t = VType.Int;
        if (t is NullT) throw new VmException($"{_f.Name}: null compared with null");
        var l = ExprAs(b.L, t);
        var r = ExprAs(b.R, b.Op is IlBinOp.Shl or IlBinOp.Shr ? VType.Int : t);
        return (l, r, t);
    }

    private (Op Op, bool Swap, bool Negate) Compare(IlBinOp op, VType t) => Compare(_m, op, t);

    private static (Op Op, bool Swap, bool Negate) Compare(ModuleBuilder m, IlBinOp op, VType t)
    {
        if (t == VType.Int)
            return op switch
            {
                IlBinOp.Eq => (Op.EQ_I32, false, false),
                IlBinOp.Ne => (Op.EQ_I32, false, true),
                IlBinOp.Lt => (Op.LT_I32, false, false),
                IlBinOp.Le => (Op.LE_I32, false, false),
                IlBinOp.Gt => (Op.LT_I32, true, false),
                _ => (Op.LE_I32, true, false),
            };
        if (t == VType.Float)
            return op switch
            {
                IlBinOp.Eq => (Op.EQ_F32, false, false),
                IlBinOp.Ne => (Op.EQ_F32, false, true),
                IlBinOp.Lt => (Op.LT_F32, false, false),
                IlBinOp.Le => (Op.LE_F32, false, false),
                IlBinOp.Gt => (Op.LT_F32, true, false),
                _ => (Op.LE_F32, true, false),
            };
        if (t == VType.Bool && op is IlBinOp.Eq or IlBinOp.Ne) return (Op.EQ_B, false, op == IlBinOp.Ne);
        if (t is StringT && op is IlBinOp.Eq or IlBinOp.Ne) return (Op.EQ_STR, false, op == IlBinOp.Ne);
        if (t is ClassT c && m.Classes[c.Name].IsRecord && op is IlBinOp.Eq or IlBinOp.Ne)
            return (Op.EQ_REC, false, op == IlBinOp.Ne);
        if (t.IsRef && op is IlBinOp.Eq or IlBinOp.Ne) return (Op.EQ_REF, false, op == IlBinOp.Ne);
        throw new VmException($"comparison {op} on {t} is not in the VM yet");
    }

    /// <summary>条件分岐: cond が jumpIf のとき target へ跳ぶ。比較は融合した分岐命令にする。</summary>
    private void Cond(IlExpr cond, bool jumpIf, Label target)
    {
        switch (cond)
        {
            case IlParen p:
                Cond(p.E, jumpIf, target);
                return;
            case IlUn { Op: IlUnOp.Not } not:
                Cond(not.E, !jumpIf, target);
                return;
            case IlLit { LuaText: "true" or "false" } lit:
                if ((lit.LuaText == "true") == jumpIf) Jump(Op.JMP, target);
                return;
            case IlBin { Op: IlBinOp.And } conj when TypeOf(conj.L) == VType.Bool:
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
            case IlBin { Op: IlBinOp.Or } disj when TypeOf(disj.L) == VType.Bool:
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
            case IlBin b when b.Op is IlBinOp.Eq or IlBinOp.Ne or IlBinOp.Lt or IlBinOp.Le or IlBinOp.Gt or IlBinOp.Ge:
            {
                var lt = TypeOf(b.L);
                var rt = TypeOf(b.R);
                if (lt is PrimT { Kind: Kind.I32 or Kind.F32 } && rt is PrimT { Kind: Kind.I32 or Kind.F32 })
                {
                    var (l, r, t) = Operands(b);
                    var f = t == VType.Float;
                    // a > b は b < a、a >= b は b <= a (NaN でも同じ)。a != b は !(a == b)
                    var (op, swap, inverted) = b.Op switch
                    {
                        IlBinOp.Lt => (f ? Op.JT_LT_F32 : Op.JT_LT_I32, false, false),
                        IlBinOp.Le => (f ? Op.JT_LE_F32 : Op.JT_LE_I32, false, false),
                        IlBinOp.Gt => (f ? Op.JT_LT_F32 : Op.JT_LT_I32, true, false),
                        IlBinOp.Ge => (f ? Op.JT_LE_F32 : Op.JT_LE_I32, true, false),
                        IlBinOp.Eq => (f ? Op.JT_EQ_F32 : Op.JT_EQ_I32, false, false),
                        _ => (f ? Op.JT_EQ_F32 : Op.JT_EQ_I32, false, true),
                    };
                    // JT_x と JF_x は隣り合う
                    var jop = jumpIf != inverted ? op : op + 1;
                    Jump(jop, target, swap ? r.Reg : l.Reg, swap ? l.Reg : r.Reg);
                    return;
                }
                // ref と null の比較は JNULL / JNN
                var nullR = b.R is IlLit { LuaText: "nil" };
                var nullL = b.L is IlLit { LuaText: "nil" };
                if (b.Op is IlBinOp.Eq or IlBinOp.Ne && (nullR ^ nullL))
                {
                    var v = Expr(nullR ? b.L : b.R);
                    var whenNull = b.Op == IlBinOp.Eq;
                    Jump(whenNull == jumpIf ? Op.JNULL : Op.JNN, target, v.Reg);
                    return;
                }
                break;
            }
        }
        var c = ExprAs(cond, VType.Bool);
        Jump(jumpIf ? Op.JT : Op.JF, target, c.Reg);
    }

    private Val Un(IlUn u, Val? dest)
    {
        switch (u.Op)
        {
            case IlUnOp.Neg:
            {
                // 負の literal は定数にする
                if (u.E is IlLit lit)
                {
                    var t = LitType(lit);
                    var d0 = dest ?? Temp(t);
                    var bits = LitBits(lit, t);
                    EmitImm(Op.LDK, d0.Reg, t == VType.Float ? bits ^ 0x80000000u : (uint)-(int)bits);
                    return d0;
                }
                var v = Expr(u.E);
                var d = dest ?? Temp(v.Type);
                Emit(v.Type == VType.Float ? Op.NEG_F32 : Op.NEG_I32, d.Reg, v.Reg);
                return d;
            }
            case IlUnOp.Not:
            {
                var v = ExprAs(u.E, VType.Bool);
                var d = dest ?? Temp(VType.Bool);
                Emit(Op.NOT_B, d.Reg, v.Reg);
                return d;
            }
            case IlUnOp.BitNot:
            {
                var v = Expr(u.E);
                var d = dest ?? Temp(VType.Int);
                Emit(Op.BNOT_I32, d.Reg, v.Reg);
                return d;
            }
            case IlUnOp.ToFloat:
            {
                var v = Expr(u.E);
                var d = dest ?? Temp(VType.Float);
                Emit(Op.I2F, d.Reg, v.Reg);
                return d;
            }
        }
        throw new VmException($"unary {u.Op}");
    }

    // ---- 呼び出し ----

    private Val Call(IlCall c, Val? dest, bool discard)
    {
        switch (c.Callee)
        {
            case "__tcs_irem" or "__tcs_idiv" or "__tcs_cadd" or "__tcs_csub" or "__tcs_cmul":
            {
                var l = ExprAs(c.Args[0], VType.Int);
                var r = ExprAs(c.Args[1], VType.Int);
                var d = dest ?? Temp(VType.Int);
                var op = c.Callee switch
                {
                    "__tcs_irem" => Op.REM_I32,
                    "__tcs_idiv" => Op.DIV_I32,
                    "__tcs_cadd" => Op.CADD_I32,
                    "__tcs_csub" => Op.CSUB_I32,
                    _ => Op.CMUL_I32,
                };
                Emit(op, d.Reg, l.Reg, r.Reg);
                return d;
            }
            case "__tcs_cneg":
            {
                var v = ExprAs(c.Args[0], VType.Int);
                var d = dest ?? Temp(VType.Int);
                Emit(Op.CNEG_I32, d.Reg, v.Reg);
                return d;
            }
            case "__tcs_trunc":
            {
                var v = ExprAs(c.Args[0], VType.Float);
                var d = dest ?? Temp(VType.Int);
                Emit(Op.F2I, d.Reg, v.Reg);
                return d;
            }
            case "Math.CompareTo":
            {
                var lt = TypeOf(c.Args[0]);
                var t = lt == VType.Float || TypeOf(c.Args[1]) == VType.Float ? VType.Float : lt;
                if (t != VType.Int && t != VType.Float) throw new VmException($"{_f.Name}: CompareTo on {t} is not in the VM yet");
                var l = ExprAs(c.Args[0], t);
                var r = ExprAs(c.Args[1], t);
                var d = dest ?? Temp(VType.Int);
                Emit(t == VType.Float ? Op.CMP_F32 : Op.CMP_I32, d.Reg, l.Reg, r.Reg);
                return d;
            }
            case "tostring" or "__tcs_fstr" or "__tcs_bstr" or "__tcs_nstr":
            {
                var s = ToStr(c);
                if (dest is null) return s;
                Store(dest, s);
                return dest;
            }
            case "print":
                Print(c);
                return new Val(VType.Void, []);
            case "table.insert":
            {
                if (c.Args.Length != 2) throw new VmException($"{_f.Name}: List.Insert is not in the VM yet");
                if (TypeOf(c.Args[0]) is not ListT lt) throw new VmException($"{_f.Name}: table.insert on non-List");
                var list = Expr(c.Args[0]);
                var v = ExprAs(c.Args[1], lt.Elem);
                ListAdd(list, lt, v);
                return new Val(VType.Void, []);
            }
            case "List.Sort" when c.Args.Length == 2:
            {
                if (TypeOf(c.Args[0]) is not ListT lt)
                    throw new VmException($"{_f.Name}: List.Sort without a comparison is not in the VM yet");
                var list = Expr(c.Args[0]);
                var cmp = ExprAs(c.Args[1], new FuncT([lt.Elem, lt.Elem], VType.Int));
                Emit(Op.LIST_SORT, list.Reg, cmp.Reg);
                return new Val(VType.Void, []);
            }
            case "Dict.Count":
            {
                if (TypeOf(c.Args[0]) is not DictT) throw new VmException($"{_f.Name}: Count on non-Dictionary");
                var d0 = Expr(c.Args[0]);
                var d = dest ?? Temp(VType.Int);
                Emit(Op.DICT_LEN, d.Reg, d0.Reg);
                return d;
            }
            case "Dict.ContainsKey":
            {
                if (TypeOf(c.Args[0]) is not DictT dt) throw new VmException($"{_f.Name}: ContainsKey on non-Dictionary");
                var d0 = Expr(c.Args[0]);
                var key = ExprAs(c.Args[1], dt.Key);
                var d = dest ?? Temp(VType.Bool);
                Emit(Op.DICT_HAS, d.Reg, d0.Reg, key.Reg);
                return d;
            }
        }
        if (c.Callee.EndsWith(".op_Equality") && c.Args.Length >= 2 && TypeOf(c.Args[0]) is { IsAggregate: true } et)
            return ValueEq(c.Args[0], c.Args[1], et, dest);
        if (StructMember(c.Callee) is { } sm)
        {
            if (sm.Ctor is { } ctor)
                return StructCtorCall(c, sm.Struct, ctor, dest);
            return StructCall(c, sm.Struct, sm.Method!, dest: dest);
        }
        if (Library(c, dest) is { } lib) return lib;
        var (cls, method) = StaticMethod(c.Callee);
        var index = RequestMethod(cls, method);
        if (!method.IsStatic)
            // instance method を `C.M(recv, args)` の形で呼ぶ (ToString の override など)
            return EmitCall(Op.CALL, index, c.Args[0], method, c.Args[1..], dest);
        return EmitCall(Op.CALL, index, null, method, c.Args, dest);
    }

    /// <summary>struct / record struct の値の等価 (il-spec §10: 葉ごと。float は Equals と同じく NaN どうしが等しい、
    /// string は内容、record class は field ごと、ほかの ref は identity)。</summary>
    private Val ValueEq(IlExpr a, IlExpr b, VType t, Val? dest)
    {
        var l = ExprAs(a, t);
        var r = ExprAs(b, t);
        var d = dest ?? Temp(VType.Bool);
        var no = new Label();
        var end = new Label();
        var leaves = _m.Leaves(t);
        var eq = Temp(VType.Bool);
        for (var k = 0; k < leaves.Count; k++)
        {
            var lt = leaves[k].Type;
            if (lt == VType.Float)
            {
                // a == b || (a != a && b != b)
                var same = new Label();
                Jump(Op.JT_EQ_F32, same, l.Regs[k], r.Regs[k]);
                Jump(Op.JT_EQ_F32, no, l.Regs[k], l.Regs[k]);
                Jump(Op.JT_EQ_F32, no, r.Regs[k], r.Regs[k]);
                Mark(same);
                continue;
            }
            var (op, _, _) = Compare(IlBinOp.Eq, lt is ClassT or IfaceT or ObjectT or BoxT ? VType.Object : lt);
            if (lt is ClassT ct && _m.Classes[ct.Name].IsRecord) op = Op.EQ_REC;
            if (lt is ObjectT or IfaceT or BoxT) op = Op.EQ_REF;
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

    private (string Struct, IlMethodInfo? Method, IlCtorInfo? Ctor)? StructMember(string callee)
    {
        var dot = callee.LastIndexOf('.');
        if (dot < 0) return null;
        var st = callee[..dot];
        var name = callee[(dot + 1)..];
        if (!_m.Structs.TryGetValue(st, out var info)) return null;
        if (!info.Ctors.IsDefaultOrEmpty && info.Ctors.FirstOrDefault(k => k.Name == name) is { } ctor)
            return (st, null, ctor);
        if (!info.Methods.IsDefaultOrEmpty && info.Methods.FirstOrDefault(m => m.Name == name) is { } m)
            return (st, m, null);
        return null;
    }

    private Val StructCtorCall(IlCall c, string st, IlCtorInfo ctor, Val? dest)
    {
        var info = _m.Structs[st];
        var inits = info.Fields.Where(f => !f.IsStatic && f.Init is not null).ToList();
        var index = _m.Request(st + "." + ctor.Name, def => new FunctionCompiler(_m, def, st).CompileStructCtor(ctor, inits));
        var types = ctor.ParameterTypes.Select(_m.Parse).ToList();
        var self = new StructT(st);
        var r = EmitCallTyped(Op.CALL, index, null, types, c.Args, [self]);
        return Typed(self, r, dest);
    }

    private int RequestMethod(string cls, IlMethodInfo method) =>
        _m.Request(cls + "." + method.Name, def => new FunctionCompiler(_m, def, cls).CompileMethod(method));

    /// <summary>引数を区画に直接書き、CALL を出す。recv は instance method の self (先頭の引数)。</summary>
    private Val EmitCall(Op op, int index, IlExpr? recv, IlMethodInfo method, IReadOnlyList<IlExpr> args, Val? dest)
    {
        var paramTypes = method.ParameterTypes.Select(_m.Parse).ToList();
        if (paramTypes.Count != args.Count) throw new VmException($"{_f.Name}: default arguments are not in the VM yet");
        var all = new List<(IlExpr E, VType T)>();
        if (recv is not null) all.Add((recv, TypeOf(recv)));
        all.AddRange(args.Select((a, i) => (a, paramTypes[i])));
        var block = Block(LeafTypes(all.Select(x => x.T)));
        var at = block;
        foreach (var (e, t) in all)
        {
            var n = _m.Leaves(t).Count;
            ExprAs(e, t, new Val(t, Enumerable.Range(at, n).ToArray()));
            at += n;
        }
        if (recv is not null) Emit(Op.NNULL, block); // C# は引数の評価の後で null を見る
        return CallTail(op, block, index, _m.Parse(method.ReturnType), dest);
    }

    private Val CallTail(Op op, int block, int index, VType retType, Val? dest)
    {
        if (retType is VoidT)
        {
            Emit(op, block, 0, index);
            return new Val(VType.Void, []);
        }
        // 戻り値は連続する register に入る。dest が連続ならそこへ直接
        Val ret;
        if (dest is { } d1 && d1.Type == retType && Enumerable.Range(0, d1.Regs.Length).All(k => d1.Regs[k] == d1.Regs[0] + k))
            ret = d1;
        else
        {
            var n = _m.Leaves(retType).Count;
            var rb = Block(LeafTypes([retType]));
            ret = new Val(retType, Enumerable.Range(rb, n).ToArray());
        }
        Emit(op, block, ret.Regs[0], index);
        if (dest is { } d2 && d2 != ret)
        {
            Store(d2, ret);
            return d2;
        }
        return ret;
    }

    private Val Invoke(IlInvoke inv, Val? dest)
    {
        var recvType = TypeOf(inv.Recv);
        if (recvType is RandomT) return RandomCall(inv, dest);
        if (recvType is IfaceT it)
        {
            var im = _m.Classes[it.Name].Methods.SingleOrDefault(m => m.Name == inv.Method)
                ?? throw new VmException($"{_f.Name}: no method {it.Name}.{inv.Method}");
            var sel = _m.Selector(it.Name, im, (cls, m) => RequestMethod(cls, m));
            var ft = new FuncT(im.ParameterTypes.Select(_m.Parse).ToList(), _m.Parse(im.ReturnType));
            var block = Block(LeafTypes(ft.Params.Prepend(recvType)));
            ExprAs(inv.Recv, recvType, new Val(recvType, [block]));
            var at = block + 1;
            for (var i = 0; i < inv.Args.Length; i++)
            {
                var n = _m.Leaves(ft.Params[i]).Count;
                ExprAs(inv.Args[i], ft.Params[i], new Val(ft.Params[i], Enumerable.Range(at, n).ToArray()));
                at += n;
            }
            return CallTail(Op.CALLI, block, sel, ft.Ret, dest);
        }
        var method = InstanceMethod(recvType, inv.Method);
        var cls = ((ClassT)recvType).Name;
        var index = RequestMethod(cls, method);
        return EmitCall(Op.CALL, index, inv.Recv, method, inv.Args, dest);
    }

    /// <summary>`C.M(...)` が IlDynCall(IlField(IlVar(C), M)) の形で来たときの static method (別の file の class の呼び出し)。</summary>
    private (string Class, IlMethodInfo Method)? StaticRef(IlExpr callee)
    {
        while (callee is IlParen p) callee = p.E;
        if (callee is not IlField { Recv: IlVar cls } f || Lookup(cls.Name) is not null || !_m.IsClass(cls.Name))
            return null;
        var info = _m.Classes[cls.Name];
        if (info.Fields.Any(x => x.IsStatic && x.Name == f.Name)) return null; // delegate 型の static field
        var method = info.Methods.SingleOrDefault(m => m.Name == f.Name && m.IsStatic);
        return method is null ? null : (cls.Name, method);
    }

    private Val DynCall(IlDynCall dc, Val? dest)
    {
        if (StaticRef(dc.Callee) is { } sr)
            return EmitCall(Op.CALL, RequestMethod(sr.Class, sr.Method), null, sr.Method, dc.Args, dest);
        if (TypeOf(dc.Callee) is not FuncT ft) throw new VmException($"{_f.Name}: call of non-delegate");
        if (ft.Params.Count != dc.Args.Length) throw new VmException($"{_f.Name}: delegate arity");
        var block = Block(LeafTypes(ft.Params.Prepend(ft)));
        ExprAs(dc.Callee, ft, new Val(ft, [block]));
        var at = block + 1;
        for (var i = 0; i < dc.Args.Length; i++)
        {
            var n = _m.Leaves(ft.Params[i]).Count;
            ExprAs(dc.Args[i], ft.Params[i], new Val(ft.Params[i], Enumerable.Range(at, n).ToArray()));
            at += n;
        }
        return CallTail(Op.CALLD, block, _m.TypeIndex(ft), ft.Ret, dest);
    }

    /// <summary>lambda: 本文を別の関数にし、捕捉する変数の cell を closure に入れる。</summary>
    private Val Closure(IlClosure c, FuncT ft, Val? dest)
    {
        var free = IlWalk.Free(c);
        var caps = new List<(string Name, VType Inner, Var Var)>();
        foreach (var name in free.OrderBy(n => n, StringComparer.Ordinal))
            if (Lookup(name) is { } v)
            {
                if (v is RegVar) throw new VmException($"{_f.Name}: captured {name} is not in a cell");
                caps.Add((name, v.Type, v));
            }
        var capInfo = caps.Select(x => (x.Name, x.Inner)).ToList();
        var lambdaName = $"{_f.Name}<lambda{_lambdas++}>";
        var (file, line) = (_f.File, _lastLine);
        var index = _m.Request(lambdaName,
            def => new FunctionCompiler(_m, def, _className).CompileLambda(c, ft, capInfo, file, line));
        var block = Block(caps.Select(x => (VType)new CellT(x.Inner)).ToList());
        for (var i = 0; i < caps.Count; i++)
            Emit(Op.MOV, block + i, CellReg(caps[i].Var));
        var d = dest ?? Temp(ft);
        Emit(Op.CLOSURE, d.Reg, block, index);
        return d;
    }

    /// <summary>static method の method group (il-spec §10): 捕捉の無い closure で、関数そのものを指す。</summary>
    private Val MethodGroup(IlMethodGroup g, FuncT ft, Val? dest)
    {
        var (cls, method) = StaticMethod(g.TypeRef + "." + g.Method);
        var paramTypes = method.ParameterTypes.Select(_m.Parse).ToList();
        if (!paramTypes.SequenceEqual(ft.Params) || _m.Parse(method.ReturnType) != ft.Ret)
            throw new VmException($"{_f.Name}: method group {g.Method} with converted signature is not in the VM yet");
        var index = RequestMethod(cls, method);
        var d = dest ?? Temp(ft);
        Emit(Op.CLOSURE, d.Reg, 0, index);
        return d;
    }

    /// <summary>Random.Shared: 隠れた static。null なら作って時刻から seed する。</summary>
    private Val SharedRandom(Val? dest)
    {
        var slot = _m.SharedRandomSlot();
        var d = dest ?? Temp(new RandomT());
        var done = new Label();
        EmitImm(Op.LDS, d.Reg, (uint)slot);
        Jump(Op.JNN, done, d.Reg);
        Emit(Op.NEW, d.Reg, 0, _m.TypeIndex(new RandomT()));
        Emit(Op.CALLR, d.Reg, 0, _m.Intrinsic("random.autoseed"));
        EmitImm(Op.STS, d.Reg, (uint)slot);
        Mark(done);
        return d;
    }

    private Val NewObj(IlNewObj n, Val? dest)
    {
        var type = _m.Parse(n.TypeName);
        if (type is RandomT)
        {
            var rd = dest ?? Temp(type);
            Emit(Op.NEW, rd.Reg, 0, _m.TypeIndex(type));
            if (n.Args.Length == 1)
            {
                var b = Block([type, VType.Int]);
                Emit(Op.MOV, b, rd.Reg);
                ExprAs(n.Args[0], VType.Int, new Val(VType.Int, [b + 1]));
                Emit(Op.CALLR, b, 0, _m.Intrinsic("random.seed"));
            }
            else Emit(Op.CALLR, rd.Reg, 0, _m.Intrinsic("random.autoseed"));
            return rd;
        }
        if (type is StructT)
        {
            if (!n.Args.IsEmpty) throw new VmException($"{_f.Name}: struct constructors are not in the VM yet");
            var d0 = dest ?? Temp(type);
            EmitZero(d0);
            return d0;
        }
        if (type is not ClassT ct) throw new VmException($"{_f.Name}: new {type} is not in the VM yet");
        var cls = _m.Classes[ct.Name];
        var ctor = cls.Ctors.IsDefaultOrEmpty ? null : cls.Ctors.SingleOrDefault(k => k.Name == n.Ctor);
        if (ctor is null && !cls.Ctors.IsDefaultOrEmpty) throw new VmException($"{_f.Name}: no constructor {n.Ctor} in {ct.Name}");
        var inits = cls.Fields.Where(f => !f.IsStatic && f.Init is not null).ToList();
        var d = dest ?? Temp(type);
        if (ctor is null && inits.Count == 0)
        {
            if (!n.Args.IsEmpty) throw new VmException($"{_f.Name}: arguments without a constructor");
            Emit(Op.NEW, d.Reg, 0, _m.TypeIndex(type));
            return d;
        }
        // 引数を評価してから NEW し、その object を先頭の引数にして constructor を呼ぶ
        var paramTypes = ctor is null ? [] : ctor.ParameterTypes.Select(_m.Parse).ToList();
        if (paramTypes.Count != n.Args.Length) throw new VmException($"{_f.Name}: default arguments are not in the VM yet");
        var block = Block(LeafTypes(paramTypes.Prepend(type)));
        var at = block + 1;
        for (var i = 0; i < n.Args.Length; i++)
        {
            var k = _m.Leaves(paramTypes[i]).Count;
            ExprAs(n.Args[i], paramTypes[i], new Val(paramTypes[i], Enumerable.Range(at, k).ToArray()));
            at += k;
        }
        Emit(Op.NEW, block, 0, _m.TypeIndex(type));
        var ctorName = ct.Name + "." + (ctor?.Name ?? "new");
        var index = _m.Request(ctorName, def => new FunctionCompiler(_m, def, ct.Name).CompileCtor(ctor, inits));
        Emit(Op.CALL, block, 0, index);
        Store(d, new Val(type, [block]));
        return d;
    }

    private Val Table(IlTable t, VType type, Val? dest)
    {
        var d = dest ?? Temp(type);
        switch (type)
        {
            case DictT dt:
            {
                Emit(Op.NEWDICT, d.Reg, 0, _m.TypeIndex(dt));
                foreach (var e in t.Entries)
                {
                    if (e.Key is null) throw new VmException($"{_f.Name}: dictionary entry without a key");
                    var key = ExprAs(e.Key, dt.Key);
                    var v = ExprAs(e.Value, dt.Value);
                    var at = Temp(VType.Int);
                    Emit(Op.DICT_INS, at.Reg, d.Reg, key.Reg);
                    StorePlace(new Place(dt.Value, "dict", Arr: d, Idx: at), v);
                }
                return d;
            }
            case ListT lt:
            {
                Emit(Op.NEWLIST, d.Reg, 0, _m.TypeIndex(lt));
                foreach (var e in t.Entries) ListAdd(d, lt, ExprAs(e.Value, lt.Elem));
                return d;
            }
            case ArrayT at:
            {
                var len = Temp(VType.Int);
                EmitImm(Op.LDK, len.Reg, (uint)t.Entries.Length);
                Emit(Op.NEWARR, d.Reg, len.Reg, _m.TypeIndex(at));
                for (var i = 0; i < t.Entries.Length; i++)
                {
                    var idx = Temp(VType.Int);
                    EmitImm(Op.LDK, idx.Reg, (uint)i);
                    var v = ExprAs(t.Entries[i].Value, at.Elem);
                    StorePlace(new Place(at.Elem, "elem", Arr: d, Idx: idx, Owner: at), v);
                }
                return d;
            }
        }
        throw new VmException($"{_f.Name}: table of {type}");
    }



    // ---- TinySystem の関数 (il-spec §13): runtime の関数 (CALLR) か専用の命令 ----

    /// <summary>runtime の関数の呼び出しの形。Params は引数の型 (IL の引数の先頭から)、Rets は戻り値の型。
    /// NewList は結果を足す空の List を先頭の引数として作って渡す (Split など)。</summary>
    private sealed record LibCall(string Name, IReadOnlyList<VType> Params, IReadOnlyList<VType> Rets, ListT? NewList = null);

    private static string LibName(string callee) =>
        callee.StartsWith("TinySystem.Math.") ? callee["TinySystem.".Length..]
        : callee.StartsWith("MathF.") ? "Math." + callee["MathF.".Length..]
        : callee;

    private LibCall? LibPlan(IlCall c)
    {
        var name = LibName(c.Callee);
        // lambda と method group は代入先の型で決まるので、ここでは型を問わない
        VType A(int i)
        {
            if (i >= c.Args.Length || c.Args[i] is IlClosure or IlMethodGroup || c.Args[i] is IlField { Name: "__copy" or "op_Equality" })
                return VType.Void;
            return TypeOf(c.Args[i]);
        }
        var n = c.Args.Length;
        var f = Enumerable.Range(0, n).Any(i => A(i) == VType.Float);
        var num = f ? VType.Float : VType.Int;
        var sfx = f ? ".f" : ".i";
        var S = VType.String;
        var I = VType.Int;
        var F = VType.Float;
        switch (name)
        {
            case "Math.Min" or "Math.Max": return new($"math.{name[5..].ToLowerInvariant()}{sfx}", [num, num], [num]);
            case "Math.Abs": return new($"math.abs{sfx}", [num], [num]);
            case "Math.Sign": return new($"math.sign{sfx}", [num], [I]);
            case "Math.Clamp": return new($"math.clamp{sfx}", [num, num, num], [num]);
            case "Math.Floor" or "Math.Ceiling" or "Math.Ceil" or "Math.Sqrt" or "Math.Sin" or "Math.Cos" or "Math.Tan" or "Math.Exp":
                return new($"math.{(name == "Math.Ceil" ? "ceiling" : name[5..].ToLowerInvariant())}", [F], [F]);
            case "Math.Log": return n == 1 ? new("math.log", [F], [F]) : new("math.logb", [F, F], [F]);
            case "Math.Atan2" or "Math.Pow": return new($"math.{name[5..].ToLowerInvariant()}", [F, F], [F]);
            case "Math.Round": return n == 1 ? new("math.round", [F], [F]) : new("math.round2", [F, I], [F]);
            case "String.FormatFloat": return new("string.formatfloat", [F, S], [S]);
            case "String.FormatInt": return new("string.formatint", [I, S], [S]);
            case "String.FormatEnum": return new("string.formatenum", [I, S], [S]);
            case "String.Trim": return new("string.trim", [S], [S]);
            case "String.ToUpper": return new("string.toupper", [S], [S]);
            case "String.ToLower": return new("string.tolower", [S], [S]);
            case "String.Replace": return new("string.replace", [S, S, S], [S]);
            case "String.Contains": return new("string.contains", [S, S], [VType.Bool]);
            case "String.StartsWith": return new("string.startswith", [S, S], [VType.Bool]);
            case "String.EndsWith": return new("string.endswith", [S, S], [VType.Bool]);
            case "String.IsNullOrEmpty": return new("string.isnullorempty", [S], [VType.Bool]);
            case "String.CompareOrdinal": return new("string.compareordinal", [S, S], [I]);
            case "String.Join" when n == 2 && A(1) is ListT jl: return new("string.join", [S, jl], [S]);
            case "String.Split" when n == 1: return new("string.splitws", [S], [], new ListT(S));
            case "String.Split" when n == 2: return new("string.split", [S, S], [], new ListT(S));
            case "Char.ConvertFromUtf32": return new("char.fromutf32", [I], [S]);
            case "Char.IsDigit" or "Char.IsUpper" or "Char.IsLower" or "Char.IsLetter" or "Char.IsLetterOrDigit" or "Char.IsWhiteSpace":
                return new("char." + name[5..].ToLowerInvariant(), [I], [VType.Bool]);
            case "Char.ToUpper" or "Char.ToLower": return new("char." + name[5..].ToLowerInvariant(), [I], [I]);
            case "string.upper": return new("string.toupper", [S], [S]);
            case "string.lower": return new("string.tolower", [S], [S]);
            case "String.JoinEnum" when n >= 3 && A(1) is ListT el: return new("string.joinenum", [S, el, S], [S]);
            case "tonumber": return new("float.parse", [S], [F]);
            case "__tcs_char": return new("char.cast", [I], [I]);
            case "io.write": return new("io.write", [S], []);
            case "TinySystem.Utf8.RuneCount": return new("utf8.runecount", [S], [I]);
            case "TinySystem.Utf8.RuneSubstring": return new("utf8.runesubstring", [S, I, I], [I is var _ ? S : S]);
            case "TinySystem.Utf8.At": return new("utf8.at", [new Utf8T(), I], [I]);
            case "TinySystem.Utf8.Slice": return new("utf8.slice", [new Utf8T(), I, I], [new Utf8T()]);
            case "TinySystem.Utf8.ToString": return new("utf8.tostring", [new Utf8T()], [S]);
            case "List.GetRange" when A(0) is ListT gl: return new("list.getrange", [gl, I, I], [gl]);
            case "List.Copy" when A(0) is ListT cl: return new("list.copy", [cl], [cl]);
            case "List.IndexOf" when A(0) is ListT { Elem.IsAggregate: false } il: return new("list.indexof", [il, il.Elem], [I]);
            case "List.Contains" when A(0) is ListT { Elem.IsAggregate: false } ol: return new("list.contains", [ol, ol.Elem], [VType.Bool]);
            case "List.Remove" when A(0) is ListT { Elem.IsAggregate: false } rl: return new("list.remove", [rl, rl.Elem], [VType.Bool]);
            case "List.FindIndex" when A(0) is ListT fl: return new("list.findindex", [fl, new FuncT([fl.Elem], VType.Bool)], [I]);
            case "List.RemoveAll" when A(0) is ListT al: return new("list.removeall", [al, new FuncT([al.Elem], VType.Bool)], [I]);
            case "List.Reverse" when A(0) is ListT vl: return new("list.reverse", [vl], []);
            case "List.Sort" when n == 1 && A(0) is ListT sl: return new("list.sort0", [sl], []);
        }
        return null;
    }

    private VType? LibraryType(IlCall c)
    {
        switch (c.Callee)
        {
            case "string.char": return VType.String;
            case "TinySystem.Random.Seed" or "TcsQueue.Enqueue" or "TcsQueue.Clear" or "TcsHashSet.Clear": return VType.Void;
            case "TcsQueue.Dequeue" or "TcsQueue.Peek" when TypeOf(c.Args[0]) is ListT qt: return qt.Elem;
            case "TcsQueue.Count" or "TcsHashSet.Count": return VType.Int;
            case "TcsQueue.ToList": return TypeOf(c.Args[0]);
            case "TcsQueue.Contains" or "TcsHashSet.Add" or "TcsHashSet.Remove" or "TcsHashSet.Contains": return VType.Bool;
            case "TcsHashSet.ToList" when TypeOf(c.Args[0]) is DictT hs: return new ListT(hs.Key);
            case "math.fmod": return VType.Float;
            case "List.Insert" or "table.remove" or "Dict.Remove": return c.Callee == "Dict.Remove" ? VType.Bool : VType.Void;
            case "TinySystem.Utf8.New" or "TinySystem.Utf8.Default": return new Utf8T();
            case "Dict.Keys" when TypeOf(c.Args[0]) is DictT dk: return new ListT(dk.Key);
            case "Dict.Values" when TypeOf(c.Args[0]) is DictT dv: return new ListT(dv.Value);
        }
        if (LibPlan(c) is not { } plan) return null;
        if (plan.NewList is { } nl) return nl;
        return plan.Rets.Count == 1 ? plan.Rets[0] : VType.Void;
    }

    private Val? Library(IlCall c, Val? dest)
    {
        switch (c.Callee)
        {
            case "TinySystem.Random.Seed":
            {
                var r = SharedRandom(null);
                var b = Block([new RandomT(), VType.Int]);
                Emit(Op.MOV, b, r.Reg);
                ExprAs(c.Args[0], VType.Int, new Val(VType.Int, [b + 1]));
                Emit(Op.CALLR, b, 0, _m.Intrinsic("random.seed"));
                return new Val(VType.Void, []);
            }
            case "TcsQueue.Enqueue" when TypeOf(c.Args[0]) is ListT qt:
            {
                var q = Expr(c.Args[0]);
                ListAdd(q, qt, ExprAs(c.Args[1], qt.Elem));
                return new Val(VType.Void, []);
            }
            case "TcsQueue.Dequeue" or "TcsQueue.Peek" when TypeOf(c.Args[0]) is ListT qt:
            {
                // 空なら先頭の読みが fault する (Queue empty)
                var q = Expr(c.Args[0]);
                var zero = Temp(VType.Int);
                EmitImm(Op.LDK, zero.Reg, 0);
                var d = dest ?? Temp(qt.Elem);
                LoadPlace(new Place(qt.Elem, "list", Arr: q, Idx: zero, Owner: qt), d);
                if (c.Callee == "TcsQueue.Dequeue") Emit(Op.LIST_REMOVE_AT, q.Reg, zero.Reg);
                return d;
            }
            case "TcsQueue.Count" or "TcsHashSet.Count":
            {
                var q = Expr(c.Args[0]);
                var d = dest ?? Temp(VType.Int);
                Emit(q.Type is DictT ? Op.DICT_LEN : Op.LIST_LEN, d.Reg, q.Reg);
                return d;
            }
            case "TcsQueue.Clear":
                Emit(Op.LIST_CLEAR, Expr(c.Args[0]).Reg);
                return new Val(VType.Void, []);
            case "TcsHashSet.Clear":
                Emit(Op.CALLR, Expr(c.Args[0]).Reg, 0, _m.Intrinsic("dict.clear"));
                return new Val(VType.Void, []);
            case "TcsQueue.ToList" when TypeOf(c.Args[0]) is ListT:
                return Library(new IlCall("List.Copy", [c.Args[0]]), dest);
            case "TcsQueue.Contains":
                return Library(new IlCall("List.Contains", [c.Args[0], c.Args[1]]), dest);
            case "TcsHashSet.ToList":
                return Library(new IlCall("Dict.Keys", [c.Args[0]]), dest);
            case "TcsHashSet.Add" or "TcsHashSet.Remove" or "TcsHashSet.Contains" when TypeOf(c.Args[0]) is DictT st:
            {
                var b = Block([st, st.Key]);
                ExprAs(c.Args[0], st, new Val(st, [b]));
                ExprAs(c.Args[1], st.Key, new Val(st.Key, [b + 1]));
                var d = dest ?? Temp(VType.Bool);
                Emit(Op.CALLR, b, d.Reg, _m.Intrinsic("set." + c.Callee["TcsHashSet.".Length..].ToLowerInvariant()));
                return d;
            }
            case "io.write" when c.Args.Length == 1:
            {
                var sv = ToStr(c.Args[0]);
                Emit(Op.CALLR, sv.Reg, 0, _m.Intrinsic("io.write"));
                return new Val(VType.Void, []);
            }
            case "string.char":
            {
                var v = ExprAs(c.Args[0], VType.Int);
                var d = dest ?? Temp(VType.String);
                Emit(Op.CHR, d.Reg, v.Reg);
                return d;
            }
            case "math.fmod":
            {
                var l = ExprAs(c.Args[0], VType.Float);
                var r = ExprAs(c.Args[1], VType.Float);
                var d = dest ?? Temp(VType.Float);
                Emit(Op.FMOD_F32, d.Reg, l.Reg, r.Reg);
                return d;
            }
            case "List.Insert" when TypeOf(c.Args[0]) is ListT lt:
            {
                var list = Expr(c.Args[0]);
                var at = ExprAs(c.Args[1], VType.Int);
                var v = ExprAs(c.Args[2], lt.Elem);
                Emit(Op.LIST_INSERT0, list.Reg, at.Reg);
                StorePlace(new Place(lt.Elem, "list", Arr: list, Idx: at, Owner: lt), v);
                return new Val(VType.Void, []);
            }
            case "table.remove" when c.Args.Length == 2 && TypeOf(c.Args[0]) is ListT:
            {
                var list = Expr(c.Args[0]);
                // Lua の table.remove(t, i) の i は 1 始まり (IL は +1 した式を渡す)
                var at = ExprAs(c.Args[1], VType.Int);
                var i0 = Temp(VType.Int);
                Emit(Op.ADDI_I32, i0.Reg, at.Reg, 0xFFFF);
                Emit(Op.LIST_REMOVE_AT, list.Reg, i0.Reg);
                return new Val(VType.Void, []);
            }
            case "Dict.Remove" when TypeOf(c.Args[0]) is DictT dt:
            {
                var d0 = Expr(c.Args[0]);
                var key = ExprAs(c.Args[1], dt.Key);
                var d = dest ?? Temp(VType.Bool);
                Emit(Op.DICT_REMOVE, d.Reg, d0.Reg, key.Reg);
                return d;
            }
            case "Dict.Keys" or "Dict.Values" when TypeOf(c.Args[0]) is DictT dt:
            {
                // 足した順の key / 値の新しい List
                var keys = c.Callee == "Dict.Keys";
                var lt = new ListT(keys ? dt.Key : dt.Value);
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
                var item = Temp(lt.Elem);
                if (keys) Emit(Op.DICT_LDK, item.Reg, dict.Reg, i.Reg);
                else LoadPlace(new Place(dt.Value, "dict", Arr: dict, Idx: i), item);
                ListAdd(d, lt, item);
                Mark(next);
                Emit(Op.ADDI_I32, i.Reg, i.Reg, 1);
                Jump(Op.JMP, top);
                Mark(exit);
                return d;
            }
            case "TinySystem.Utf8.New":
            {
                var str = ExprAs(c.Args[0], VType.String);
                var d = dest ?? Temp(new Utf8T());
                Emit(Op.MOV, d.Regs[0], str.Reg);
                EmitImm(Op.LDK, d.Regs[1], 0);
                Emit(Op.CALLR, str.Reg, d.Regs[2], _m.Intrinsic("string.bytelen"));
                return d;
            }
            case "TinySystem.Utf8.Default":
            {
                var d = dest ?? Temp(new Utf8T());
                EmitImm(Op.LDSTR, d.Regs[0], _m.Const([]));
                EmitImm(Op.LDK, d.Regs[1], 0);
                EmitImm(Op.LDK, d.Regs[2], 0);
                return d;
            }
            case "String.Join" when c.Args.Length != 2 || TypeOf(c.Args[1]) is not ListT:
            {
                // Join(sep, a, b, ...): 連結
                var sep = ToStr(c.Args[0]);
                var acc = ToStr(c.Args[1]);
                for (var i = 2; i < c.Args.Length; i++)
                {
                    var t = Temp(VType.String);
                    Emit(Op.CONCAT, t.Reg, acc.Reg, sep.Reg);
                    var piece = ToStr(c.Args[i]);
                    var t2 = Temp(VType.String);
                    Emit(Op.CONCAT, t2.Reg, t.Reg, piece.Reg);
                    acc = t2;
                }
                return dest is null ? acc : Store2(dest, acc);
            }
        }
        if (LibPlan(c) is not { } plan) return null;
        // 結果を足す List、引数、戻り値の区画
        var argTypes = new List<VType>();
        if (plan.NewList is { } newList) argTypes.Add(newList);
        argTypes.AddRange(plan.Params);
        var block = Block(LeafTypes(argTypes));
        var at2 = block;
        if (plan.NewList is { } nl2)
        {
            Emit(Op.NEWLIST, at2, 0, _m.TypeIndex(nl2));
            at2++;
        }
        for (var i = 0; i < plan.Params.Count; i++)
        {
            var t = plan.Params[i];
            var k = _m.Leaves(t).Count;
            ExprAs(c.Args[i], t, new Val(t, Enumerable.Range(at2, k).ToArray()));
            at2 += k;
        }
        var retTypes = LeafTypes(plan.Rets);
        var rb = Block(retTypes);
        Emit(Op.CALLR, block, rb, _m.Intrinsic(plan.Name));
        if (plan.NewList is { } nl3)
        {
            var lv = new Val(nl3, [block]);
            return dest is null ? lv : Store2(dest, lv);
        }
        if (plan.Rets.Count == 0) return new Val(VType.Void, []);
        var result = new Val(plan.Rets[0], Enumerable.Range(rb, retTypes.Count).ToArray());
        return dest is null ? result : Store2(dest, result);
    }

    /// <summary>foreach (var r in s.EnumerateRunes()): rune (code point の整数) ごと。</summary>
    private void ForeachRunes(IlForeachRunes fr)
    {
        _scopes.Add([]);
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
        var args = Block([VType.String, VType.Int]);
        Emit(Op.MOV, args, str.Reg);
        Emit(Op.MOV, args + 1, pos.Reg);
        var rets = Block([VType.Int, VType.Int]);
        Emit(Op.CALLR, args, rets, _m.Intrinsic("utf8.runenext"));
        Emit(Op.MOV, pos.Reg, rets);
        _scopes.Add([]);
        var rune = new Val(VType.Int, [rets + 1]);
        _scopes[^1][fr.Var] = _captured.Contains(fr.Var) ? NewCell(rune) : new RegVar(CopyToNew(rune));
        EndStatement();
        _loops.Add((exit, top));
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
        var d = type is VoidT ? null : dest ?? Temp(type);
        if (d is not null) EmitZero(d); // return に届かない経路は値なし (?. の null 側)
        // 中の文の temp と区画は外の式の temp と分ける (文の終わりで外の temp を空きに戻さない)
        var savedTemps = _temps.ToList();
        var savedBlocks = _blocksInUse.ToList();
        _temps.Clear();
        _blocksInUse.Clear();
        var savedLoops = _loops.ToList();
        var savedScopes = _breakScopes.ToList();
        _loops.Clear();
        _breakScopes.Clear();
        var end = new Label();
        _iifes.Add((d, end, type));
        _scopes.Add([]);
        foreach (var st in iife.Stats) Stat(st);
        _scopes.RemoveAt(_scopes.Count - 1);
        _iifes.RemoveAt(_iifes.Count - 1);
        _loops.AddRange(savedLoops);
        _breakScopes.AddRange(savedScopes);
        Mark(end);
        EndStatement();
        _temps.AddRange(savedTemps);
        _blocksInUse.AddRange(savedBlocks);
        return d ?? new Val(VType.Void, []);
    }

    /// <summary>IIFE の値の型: 中の local を仮の scope に置いて、最初の値つき return の型を見る。</summary>
    private VType IifeType(IlIife iife)
    {
        _scopes.Add([]);
        try
        {
            return ReturnType(iife.Stats) ?? VType.Void;
        }
        finally
        {
            _scopes.RemoveAt(_scopes.Count - 1);
        }
    }

    private VType? ReturnType(IEnumerable<IlStat> stats)
    {
        VType? nullFound = null;
        foreach (var st in stats)
        {
            VType? t = null;
            switch (st)
            {
                case IlLocal l:
                {
                    var lt = l.Type is { } ts ? _m.Parse(ts) : l.Init is { } i ? TypeOf(i) : VType.Void;
                    _scopes[^1][l.Name] = new RegVar(new Val(lt, []));
                    break;
                }
                case IlReturn { Value: { } v }:
                    t = TypeOf(v);
                    break;
                case IlIf iff:
                    foreach (var arm in iff.Arms)
                        t ??= Scoped(arm.Body.Stats);
                    if (iff.Else is { } e) t ??= Scoped(e.Stats);
                    break;
                case IlDo d: t = Scoped(d.Body.Stats); break;
                case IlBreakScope b: t = Scoped(b.Body.Stats); break;
                case IlWhile w: t = Scoped(w.Body.Stats); break;
                case IlRepeat r: t = Scoped(r.Body.Stats); break;
                case IlNumericFor f:
                    _scopes.Add(new() { [f.Var] = new RegVar(new Val(VType.Int, [])) });
                    t = ReturnType(f.Body.Stats);
                    _scopes.RemoveAt(_scopes.Count - 1);
                    break;
            }
            if (t is NullT) nullFound = t;
            else if (t is not null) return t;
        }
        return nullFound;

        VType? Scoped(IEnumerable<IlStat> inner)
        {
            _scopes.Add([]);
            try { return ReturnType(inner); }
            finally { _scopes.RemoveAt(_scopes.Count - 1); }
        }
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
        if (e.Type is NullableT)
        {
            Jump(Op.JF, none, e.Regs[0]);
            Store(d, type is NullableT ? e : new Val(((NullableT)e.Type).Inner, e.Regs[1..]));
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

    private VType LiftedType(IlLiftedBin b) => b.Op switch
    {
        IlLiftedOp.Eq or IlLiftedOp.Ne or IlLiftedOp.Lt or IlLiftedOp.Le or IlLiftedOp.Gt or IlLiftedOp.Ge => VType.Bool,
        IlLiftedOp.And or IlLiftedOp.Or => new NullableT(VType.Bool),
        _ => new NullableT(LiftedInner(b)),
    };

    private VType LiftedInner(IlLiftedBin b)
    {
        var l = TypeOf(b.L) is NullableT ln ? ln.Inner : TypeOf(b.L);
        var r = TypeOf(b.R) is NullableT rn ? rn.Inner : TypeOf(b.R);
        if (b.Op is IlLiftedOp.Shl or IlLiftedOp.Shr) return l;
        return l == VType.Float || r == VType.Float ? VType.Float : l;
    }

    private Val LiftedBin(IlLiftedBin b, Val? dest)
    {
        var inner = LiftedInner(b);
        var nt = new NullableT(inner);
        var rt = b.Op is IlLiftedOp.Shl or IlLiftedOp.Shr ? new NullableT(VType.Int) : nt;
        if (b.Op is IlLiftedOp.And or IlLiftedOp.Or)
        {
            // bool? の三値論理: And は片方が false なら false、Or は片方が true なら true、ほかは両方に値があれば値
            var bn = new NullableT(VType.Bool);
            var l0 = ExprAs(b.L, bn);
            var r0 = ExprAs(b.R, bn);
            var d0 = dest ?? Temp(bn);
            var decided = b.Op == IlLiftedOp.And ? 0u : 1u;
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
        var lv = new Val(inner, l.Regs[1..]);
        var rv = new Val(rt.Inner, r.Regs[1..]);
        if (b.Op is IlLiftedOp.Eq or IlLiftedOp.Ne)
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
            var (op, swap, negate) = Compare(IlBinOp.Eq, inner);
            Emit(op, d1.Reg, lv.Reg, rv.Reg);
            if (b.Op == IlLiftedOp.Ne) Emit(Op.NOT_B, d1.Reg, d1.Reg);
            Jump(Op.JMP, end1);
            Mark(bothNone);
            EmitImm(Op.LDK, d1.Reg, b.Op == IlLiftedOp.Eq ? 1u : 0u);
            Jump(Op.JMP, end1);
            Mark(differ);
            EmitImm(Op.LDK, d1.Reg, b.Op == IlLiftedOp.Eq ? 0u : 1u);
            Mark(end1);
            _ = swap;
            _ = negate;
            return d1;
        }
        if (b.Op is IlLiftedOp.Lt or IlLiftedOp.Le or IlLiftedOp.Gt or IlLiftedOp.Ge)
        {
            var d2 = dest ?? Temp(VType.Bool);
            var no = new Label();
            var end2 = new Label();
            Jump(Op.JF, no, l.Regs[0]);
            Jump(Op.JF, no, r.Regs[0]);
            var bop = b.Op switch { IlLiftedOp.Lt => IlBinOp.Lt, IlLiftedOp.Le => IlBinOp.Le, IlLiftedOp.Gt => IlBinOp.Gt, _ => IlBinOp.Ge };
            var (op, swap, _) = Compare(bop, inner);
            Emit(op, d2.Reg, swap ? rv.Reg : lv.Reg, swap ? lv.Reg : rv.Reg);
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
        var f = inner == VType.Float;
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
            case IlLiftedOp.BitAnd: Emit(inner == VType.Bool ? Op.AND_B : Op.AND_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.BitOr: Emit(inner == VType.Bool ? Op.OR_B : Op.OR_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.BitXor: Emit(inner == VType.Bool ? Op.NE_B : Op.XOR_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.Shl: Emit(Op.SHL_I32, dv, lv.Reg, rv.Reg); break;
            case IlLiftedOp.Shr: Emit(Op.SHR_I32, dv, lv.Reg, rv.Reg); break;
            default: throw new VmException($"lifted {b.Op}");
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
        var nt = (NullableT)t;
        var d = dest ?? Temp(u.Op == IlUnOp.ToFloat ? new NullableT(VType.Float) : t);
        var none = new Label();
        var end = new Label();
        Jump(Op.JF, none, v.Regs[0]);
        var inner = new Val(nt.Inner, v.Regs[1..]);
        switch (u.Op)
        {
            case IlUnOp.Neg: Emit(nt.Inner == VType.Float ? Op.NEG_F32 : Op.NEG_I32, d.Regs[1], inner.Reg); break;
            case IlUnOp.Not: Emit(Op.NOT_B, d.Regs[1], inner.Reg); break;
            case IlUnOp.BitNot: Emit(Op.BNOT_I32, d.Regs[1], inner.Reg); break;
            case IlUnOp.ToFloat: Emit(Op.I2F, d.Regs[1], inner.Reg); break;
            default: throw new VmException($"lifted {u.Op}");
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
        if (type is ClassT c)
        {
            var d0 = dest ?? Temp(type);
            Emit(Op.CLONE, d0.Reg, src.Reg);
            foreach (var (name, value) in w.Overrides)
            {
                var v = ExprAs(value, _m.FieldType(type, name));
                StorePlace(new Place(_m.FieldType(type, name), "field", Obj: d0, Leaf: LeafIndex(_m.ObjectLeaves(c), name)), v);
            }
            return d0;
        }
        // struct: 値の copy に上書き
        var d = NewVal(type);
        Store(d, src);
        foreach (var (name, value) in w.Overrides)
        {
            var ft = _m.FieldType(type, name);
            var v = ExprAs(value, ft);
            StorePlace(new Place(ft, "local", Obj: d, Leaf: LeafIndex(_m.Leaves(type), name)), v);
        }
        return dest is null ? d : Store2(dest, d);
    }

    // ---- 多重代入 (out の引数、分解) ----

    private void MultiAssign(IlMultiAssign ma)
    {
        if (ma.Values is [IlCall { Callee: "Math.TryParseInt" or "Math.TryParseFloat" or "Dict.TryGet" } tc] && ma.Targets.Length == 2)
        {
            var (found, value) = TryCall(tc);
            AssignTarget(ma.Targets[0], found, ma.Declare);
            AssignTarget(ma.Targets[1], value, ma.Declare);
            return;
        }
        if (ma.Values.Length == 1 && ma.Values[0] is IlCall call && OutCall(call) is { } info)
        {
            // [戻り値?, out...] = call
            var types = new List<VType>();
            if (info.Ret is not VoidT) types.Add(info.Ret);
            types.AddRange(info.Outs);
            var results = CallWithOuts(call, info);
            var at = 0;
            for (var i = 0; i < ma.Targets.Length; i++)
            {
                var t = types[i];
                var n = _m.Leaves(t).Count;
                var v = new Val(t, results.Regs.Skip(at).Take(n).ToArray());
                at += n;
                AssignTarget(ma.Targets[i], v, ma.Declare);
            }
            return;
        }
        if (ma.Targets.Length == ma.Values.Length)
        {
            // 右辺を全部評価してから左へ (il-spec §10 の分解の順)
            var vals = ma.Values.Select(v => CopyToNew(Expr(v))).ToList();
            for (var i = 0; i < ma.Targets.Length; i++) AssignTarget(ma.Targets[i], vals[i], ma.Declare);
            return;
        }
        throw new VmException($"{_f.Name}: multiple assignment shape is not in the VM yet");
    }

    private void AssignTarget(IlExpr target, Val v, bool declare)
    {
        if (target is IlVar { Name: "_" } && Lookup("_") is null) return;
        if (declare && target is IlVar dv)
        {
            var var = _captured.Contains(dv.Name) ? NewCell(v) : new RegVar(CopyToNew(v));
            _scopes[^1][dv.Name] = var;
            return;
        }
        if (target is IlVar tv && Lookup(tv.Name) is { } existing)
        {
            WriteVar(existing, Convert(v, existing.Type));
            return;
        }
        var place = PlaceOf(target);
        StorePlace(place, Convert(v, place.Type));
    }

    /// <summary>TryParse / TryGetValue の lowering 先: (見つかったか、値。無ければ既定値)。</summary>
    private (Val Found, Val Value) TryCall(IlCall c)
    {
        if (c.Callee == "Dict.TryGet")
        {
            var dt = (DictT)TypeOf(c.Args[0]);
            var dict = Expr(c.Args[0]);
            var key = ExprAs(c.Args[1], dt.Key);
            var found0 = CopyToNew(ZeroTemp(VType.Bool));
            var value0 = NewVal(dt.Value);
            Emit(Op.DICT_HAS, found0.Reg, dict.Reg, key.Reg);
            var no = new Label();
            var end0 = new Label();
            Jump(Op.JF, no, found0.Reg);
            var at = Temp(VType.Int);
            Emit(Op.DICT_FIND, at.Reg, dict.Reg, key.Reg);
            LoadPlace(new Place(dt.Value, "dict", Arr: dict, Idx: at), value0);
            Jump(Op.JMP, end0);
            Mark(no);
            if (c.Args.Length > 2) ExprAs(c.Args[2], dt.Value, value0);
            else EmitZero(value0);
            Mark(end0);
            return (found0, value0);
        }
        var isInt = c.Callee == "Math.TryParseInt";
        var vt = isInt ? VType.Int : VType.Float;
        var s = ExprAs(c.Args[0], VType.String);
        var rb = Block([VType.Bool, vt]);
        Emit(Op.CALLR, s.Reg, rb, _m.Intrinsic(isInt ? "int.tryparse" : "float.tryparse"));
        var found = CopyToNew(new Val(VType.Bool, [rb]));
        var value = CopyToNew(new Val(vt, [rb + 1]));
        if (c.Args.Length > 1)
        {
            var ok = new Label();
            Jump(Op.JT, ok, found.Reg);
            ExprAs(c.Args[1], vt, value);
            Mark(ok);
        }
        return (found, value);
    }

    private sealed record OutInfo(string Class, IlMethodInfo Method, VType Ret, List<VType> Outs, bool Struct);

    private OutInfo? OutCall(IlCall call)
    {
        var dot = call.Callee.LastIndexOf('.');
        if (dot < 0) return null;
        var cls = call.Callee[..dot];
        var name = call.Callee[(dot + 1)..];
        IlMethodInfo? method = null;
        var isStruct = false;
        if (_m.Classes.TryGetValue(cls, out var ci)) method = ci.Methods.FirstOrDefault(m => m.Name == name);
        else if (_m.Structs.TryGetValue(cls, out var si) && !si.Methods.IsDefault)
        {
            method = si.Methods.FirstOrDefault(m => m.Name == name);
            isStruct = true;
        }
        if (method is null || method.OutParameters.IsDefaultOrEmpty) return null;
        var types = method.ParameterTypes.Select(_m.Parse).ToList();
        var outs = method.Parameters.Select((p, i) => (p, types[i])).Where(x => method.OutParameters.Contains(x.p))
            .Select(x => x.Item2).ToList();
        return new OutInfo(cls, method, _m.Parse(method.ReturnType), outs, isStruct);
    }

    /// <summary>out を持つ関数の呼び出し: 戻り値の葉と out の葉が並んだ値を返す。</summary>
    private Val CallWithOuts(IlCall call, OutInfo info)
    {
        var all = new List<VType>();
        if (info.Ret is not VoidT) all.Add(info.Ret);
        all.AddRange(info.Outs);
        if (info.Struct)
            return StructCall(call, info.Class, info.Method, extraRets: all);
        var index = RequestMethod(info.Class, info.Method);
        var types = info.Method.ParameterTypes.Select(_m.Parse).ToList();
        var inTypes = info.Method.Parameters.Select((p, i) => (p, types[i])).Where(x => !info.Method.OutParameters.Contains(x.p))
            .Select(x => x.Item2).ToList();
        var recv = info.Method.IsStatic ? null : call.Args[0];
        var args = info.Method.IsStatic ? call.Args.ToList() : call.Args.Skip(1).ToList();
        return EmitCallTyped(Op.CALL, index, recv, inTypes, args, all);
    }

    /// <summary>引数を区画に直接書き、CALL を出す。戻り値は rets の型の葉が並ぶ。</summary>
    private Val EmitCallTyped(Op op, int index, IlExpr? recv, IReadOnlyList<VType> paramTypes, IReadOnlyList<IlExpr> args,
        IReadOnlyList<VType> rets)
    {
        if (paramTypes.Count != args.Count) throw new VmException($"{_f.Name}: default arguments are not in the VM yet");
        var all = new List<(IlExpr E, VType T)>();
        if (recv is not null) all.Add((recv, TypeOf(recv)));
        all.AddRange(args.Select((a, i) => (a, paramTypes[i])));
        var block = Block(LeafTypes(all.Select(x => x.T)));
        var at = block;
        foreach (var (e, t) in all)
        {
            var n = _m.Leaves(t).Count;
            ExprAs(e, t, new Val(t, Enumerable.Range(at, n).ToArray()));
            at += n;
        }
        if (recv is not null) Emit(Op.NNULL, block);
        var retTypes = LeafTypes(rets);
        var rb = Block(retTypes);
        Emit(op, block, rb, index);
        return new Val(VType.Void, Enumerable.Range(rb, retTypes.Count).ToArray());
    }

    /// <summary>struct の member の呼び出し (il-spec §10): `S.M(recv, args)`。instance member は self を値で渡し、
    /// 戻ってきた self を recv の場所に書き戻す (recv が変数・field・要素のとき)。extraRets は out の分。</summary>
    private Val StructCall(IlCall call, string st, IlMethodInfo method, IReadOnlyList<VType>? extraRets = null, Val? dest = null)
    {
        var index = _m.Request(st + "." + method.Name, def => new FunctionCompiler(_m, def, st).CompileStructMethod(method));
        var types = method.ParameterTypes.Select(_m.Parse).ToList();
        var outs = method.OutParameters.IsDefault ? [] : method.OutParameters.ToHashSet();
        var inTypes = method.Parameters.Select((p, i) => (p, types[i])).Where(x => !outs.Contains(x.p)).Select(x => x.Item2).ToList();
        var ret = _m.Parse(method.ReturnType);
        var rets = extraRets?.ToList() ?? (ret is VoidT ? [] : [ret]);
        if (method.IsStatic)
        {
            var r0 = EmitCallTyped(Op.CALL, index, null, inTypes, call.Args, rets);
            return extraRets is null && ret is not VoidT ? Typed(ret, r0, dest) : r0;
        }
        var selfType = new StructT(st);
        var recvExpr = call.Args[0];
        while (recvExpr is IlParen or IlStructCopy)
            recvExpr = recvExpr is IlParen pp ? pp.E : ((IlStructCopy)recvExpr).E;
        Place? place = recvExpr is IlVar or IlField or IlIndex ? PlaceOf(recvExpr) : null;
        var self = place is not null ? LoadPlace(place, null) : ExprAs(call.Args[0], selfType);
        var all = new List<(Val? V, IlExpr? E, VType T)> { (self, null, selfType) };
        all.AddRange(call.Args.Skip(1).Select((a, i) => ((Val?)null, (IlExpr?)a, inTypes[i])));
        var block = Block(LeafTypes(all.Select(x => x.T)));
        var at = block;
        foreach (var (v, e, t) in all)
        {
            var n = _m.Leaves(t).Count;
            var slot = new Val(t, Enumerable.Range(at, n).ToArray());
            if (v is not null) Store(slot, v);
            else ExprAs(e!, t, slot);
            at += n;
        }
        var retTypes = LeafTypes(rets.Append(selfType));
        var rb = Block(retTypes);
        Emit(Op.CALL, block, rb, index);
        var retLeaves = LeafTypes(rets).Count;
        var newSelf = new Val(selfType, Enumerable.Range(rb + retLeaves, _m.Leaves(selfType).Count).ToArray());
        if (place is not null) StorePlace(place, newSelf);
        var r = new Val(VType.Void, Enumerable.Range(rb, retLeaves).ToArray());
        return extraRets is null && ret is not VoidT ? Typed(ret, r, dest) : r;
    }

    private Val Typed(VType t, Val raw, Val? dest)
    {
        var v = new Val(t, raw.Regs);
        return dest is null ? v : Store2(dest, v);
    }

    // ---- Dictionary の foreach (足した順) ----

    private void ForeachDict(IlForeachDict fd)
    {
        _scopes.Add([]);
        if (TypeOf(fd.Coll) is not DictT dt) throw new VmException($"{_f.Name}: foreach over non-Dictionary");
        var dict = NewVal(dt);
        ExprAs(fd.Coll, dt, dict);
        var i = NewVal(VType.Int);
        EmitImm(Op.LDK, i.Reg, 0);
        var n = NewVal(VType.Int);
        var live = NewVal(VType.Bool);
        EndStatement();
        var top = new Label();
        var cont = new Label();
        var exit = new Label();
        Mark(top);
        Emit(Op.DICT_USED, n.Reg, dict.Reg);
        Jump(Op.JF_LT_I32, exit, i.Reg, n.Reg);
        Emit(Op.DICT_LIVE, live.Reg, dict.Reg, i.Reg);
        Jump(Op.JF, cont, live.Reg);
        _scopes.Add([]);
        var kvt = new KvpT(dt.Key, dt.Value);
        var kv = NewVal(kvt);
        var keyLeaves = _m.Leaves(dt.Key).Count;
        Emit(Op.DICT_LDK, kv.Regs[0], dict.Reg, i.Reg);
        LoadPlace(new Place(dt.Value, "dict", Arr: dict, Idx: i), new Val(dt.Value, kv.Regs[keyLeaves..]));
        _scopes[^1][fd.Var] = _captured.Contains(fd.Var) ? NewCell(kv) : new RegVar(kv);
        EndStatement();
        _loops.Add((exit, cont));
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
        if (inv.Method is "NextFloat" or "NextSingle")
        {
            var df = dest ?? Temp(VType.Float);
            Emit(Op.CALLR, r.Reg, df.Reg, _m.Intrinsic("random.float"));
            return df;
        }
        var b = Block([new RandomT(), VType.Int, VType.Int]);
        Emit(Op.MOV, b, r.Reg);
        switch (inv.Args.Length)
        {
            case 0:
                EmitImm(Op.LDK, b + 1, 0);
                EmitImm(Op.LDK, b + 2, 0x7FFFFFFF);
                break;
            case 1:
                EmitImm(Op.LDK, b + 1, 0);
                ExprAs(inv.Args[0], VType.Int, new Val(VType.Int, [b + 2]));
                break;
            default:
                ExprAs(inv.Args[0], VType.Int, new Val(VType.Int, [b + 1]));
                ExprAs(inv.Args[1], VType.Int, new Val(VType.Int, [b + 2]));
                break;
        }
        var d = dest ?? Temp(VType.Int);
        Emit(Op.CALLR, b, d.Reg, _m.Intrinsic(inv.Method == "Range" ? "random.range" : "random.next"));
        return d;
    }

    /// <summary>List.Add: 1 葉の要素は LIST_ADD、struct は 0 の要素を足してから葉を書く。</summary>
    private void ListAdd(Val list, ListT lt, Val v)
    {
        if (!lt.Elem.IsAggregate)
        {
            Emit(Pick(Op.LIST_ADD_I32, _m.Leaves(lt.Elem)[0].Kind), list.Reg, v.Reg);
            return;
        }
        Emit(Op.LIST_PUSH0, list.Reg);
        var last = Temp(VType.Int);
        Emit(Op.LIST_LEN, last.Reg, list.Reg);
        Emit(Op.ADDI_I32, last.Reg, last.Reg, 0xFFFF);
        StorePlace(new Place(lt.Elem, "list", Arr: list, Idx: last, Owner: lt), v);
    }

    private void Print(IlCall c)
    {
        if (c.Args.Length != 1) throw new VmException("print with several values");
        var arg = c.Args[0];
        if (arg is IlCall { Callee: "__tcs_fstr" or "__tcs_bstr" or "tostring" } conv) arg = conv.Args[0];
        var t = TypeOf(arg);
        if (t is not PrimT)
        {
            Emit(Op.PRINT_S, ToStr(c.Args[0]).Reg);
            return;
        }
        var v = Expr(arg);
        if (t == VType.Float) Emit(Op.PRINT_F32, v.Reg);
        else if (t == VType.Int) Emit(Op.PRINT_I32, v.Reg);
        else Emit(Op.PRINT_B, v.Reg);
    }
}
