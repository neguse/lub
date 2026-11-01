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
        Prologue(method.Body, method.IsStatic ? null : new ClassT(_className), method.Parameters, types);
        Block(method.Body, newScope: false);
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
            else Emit(Op.RET, Contiguous(ExprAs(c.ExprBody!, ft.Ret)));
            EndStatement();
        }
        Finish();
    }

    /// <summary>引数を register に置き、closure が捕捉する引数は cell に移す。</summary>
    private void Prologue(object body, VType? self, IReadOnlyList<string> names, IReadOnlyList<VType> types)
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
            if (_returnType is VoidT) Emit(Op.RET0);
            else
            {
                var dummy = NewVal(_returnType);
                EmitZero(dummy);
                Emit(Op.RET, Contiguous(dummy));
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
            case IlReturn ret:
                if (ret.Value is null) Emit(Op.RET0);
                else Emit(Op.RET, Contiguous(ExprAs(ret.Value, _returnType)));
                break;
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
        if (elem is StructT && collType is ListT) throw new VmException("List of struct is not in the VM yet");
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
            : new Place(elem, "list", Arr: coll, Idx: i), null);
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
            case IlField { Recv: IlVar cls } f when Lookup(cls.Name) is null && _m.IsClass(cls.Name):
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
                        if (l.Elem is StructT) throw new VmException("List of struct is not in the VM yet");
                        var list = Expr(idx.Recv);
                        var i = ExprAs(idx.Idx, VType.Int);
                        return new Place(l.Elem, "list", Arr: list, Idx: i);
                    }
                    case DictT d:
                    {
                        var dict = Expr(idx.Recv);
                        var key = ExprAs(idx.Idx, d.Key);
                        return new Place(d.Value, "dict", Arr: dict, Idx: key);
                    }
                }
                throw new VmException($"{_f.Name}: index of {collType} is not in the VM yet");
            }
            default:
                throw new VmException($"{_f.Name}: place {e.GetType().Name} is not in the VM yet");
        }
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
                case "elem" when ((ArrayT)p.Owner!).Elem is StructT:
                    Emit(Pick(Op.STEF_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r, p.Leaf + k);
                    break;
                case "elem": Emit(Pick(Op.STE_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r); break;
                case "list": Emit(Pick(Op.LIST_ST_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r); break;
                case "dict": Emit(Pick(Op.DICT_SET_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r); break;
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
                case "elem" when ((ArrayT)p.Owner!).Elem is StructT:
                    Emit(Pick(Op.LDEF_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg, p.Leaf + k);
                    break;
                case "elem": Emit(Pick(Op.LDE_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg); break;
                case "list": Emit(Pick(Op.LIST_LD_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg); break;
                case "dict": Emit(Pick(Op.DICT_GET_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg); break;
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
        IlField { Recv: IlVar en } when Lookup(en.Name) is null && _m.IsEnum(en.Name) => VType.Int,
        IlField { Recv: IlVar cls } f when Lookup(cls.Name) is null && _m.IsClass(cls.Name)
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
        IlInvoke inv => _m.Parse(InstanceMethod(TypeOf(inv.Recv), inv.Method).ReturnType),
        IlDynCall d when StaticRef(d.Callee) is { } sr => _m.Parse(sr.Method.ReturnType),
        IlDynCall d => TypeOf(d.Callee) is FuncT ft ? ft.Ret : throw new VmException($"{_f.Name}: call of non-delegate"),
        IlNewObj n => _m.Parse(n.TypeName),
        IlNewArray a => new ArrayT(_m.Parse(a.ElementType)),
        IlTable t => TableType(t),
        IlNumericConvert nc => _m.Parse(nc.TargetType),
        IlStructCopy sc => TypeOf(sc.E),
        IlTernary t => TypeOf(t.T) is NullT ? TypeOf(t.F) : TypeOf(t.T),
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
        "tostring" or "__tcs_fstr" or "__tcs_bstr" => VType.String,
        "print" or "table.insert" or "List.Sort" => VType.Void,
        "Dict.ContainsKey" => VType.Bool,
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
            ?? throw new VmException($"{_f.Name}: no static method {callee}");
        return (cls, method);
    }

    private IlMethodInfo InstanceMethod(VType recv, string name)
    {
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
                if (nc.LuaCheck is not null) throw new VmException($"{_f.Name}: unboxing is not in the VM yet");
                var to = _m.Parse(nc.TargetType);
                var v = Expr(nc.Value);
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
        if (e is IlCall { Callee: "tostring" or "__tcs_fstr" or "__tcs_bstr" } c && c.Args.Length == 1) e = c.Args[0];
        var t = TypeOf(e);
        if (t is StringT or NullT) return ExprAs(e, VType.String);
        var v = Expr(e);
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

    private static (Op Op, bool Swap, bool Negate) Compare(IlBinOp op, VType t)
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
            case "tostring" or "__tcs_fstr" or "__tcs_bstr":
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
                if (lt.Elem is StructT) throw new VmException("List of struct is not in the VM yet");
                var list = Expr(c.Args[0]);
                var v = ExprAs(c.Args[1], lt.Elem);
                Emit(Pick(Op.LIST_ADD_I32, _m.Leaves(lt.Elem)[0].Kind), list.Reg, v.Reg);
                return new Val(VType.Void, []);
            }
            case "List.Sort":
            {
                if (TypeOf(c.Args[0]) is not ListT lt || c.Args.Length != 2)
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
        var (cls, method) = StaticMethod(c.Callee);
        var index = RequestMethod(cls, method);
        return EmitCall(Op.CALL, index, null, method, c.Args, dest);
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

    private Val NewObj(IlNewObj n, Val? dest)
    {
        var type = _m.Parse(n.TypeName);
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
                    Emit(Pick(Op.DICT_SET_I32, _m.Leaves(dt.Value)[0].Kind), d.Reg, key.Reg, v.Reg);
                }
                return d;
            }
            case ListT lt:
            {
                if (lt.Elem is StructT) throw new VmException("List of struct is not in the VM yet");
                Emit(Op.NEWLIST, d.Reg, 0, _m.TypeIndex(lt));
                foreach (var e in t.Entries)
                {
                    var v = ExprAs(e.Value, lt.Elem);
                    Emit(Pick(Op.LIST_ADD_I32, _m.Leaves(lt.Elem)[0].Kind), d.Reg, v.Reg);
                }
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

    private void Print(IlCall c)
    {
        if (c.Args.Length != 1) throw new VmException("print with several values");
        var arg = c.Args[0];
        if (arg is IlCall { Callee: "__tcs_fstr" or "__tcs_bstr" or "tostring" } conv) arg = conv.Args[0];
        var t = TypeOf(arg);
        if (t is StringT or NullT)
        {
            Emit(Op.PRINT_S, ExprAs(arg, VType.String).Reg);
            return;
        }
        var v = Expr(arg);
        if (t == VType.Float) Emit(Op.PRINT_F32, v.Reg);
        else if (t == VType.Int) Emit(Op.PRINT_I32, v.Reg);
        else if (t == VType.Bool) Emit(Op.PRINT_B, v.Reg);
        else throw new VmException($"print of {t} is not in the VM yet");
    }
}
