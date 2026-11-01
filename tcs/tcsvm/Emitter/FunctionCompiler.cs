using System.Collections.Immutable;
using System.Globalization;

namespace TinyCs.Vm;

/// <summary>値: 葉ごとの register の列 (prim / ref は 1 つ、struct は葉の数)。</summary>
internal sealed record Val(VType Type, int[] Regs)
{
    public int Reg => Regs.Length == 1 ? Regs[0] : throw new VmException($"{Type} is not a single register");
}

/// <summary>1 つの method を IL から命令列にする (doc/vm-design.md §15)。
/// register の種類は関数の中で固定し (§6.1)、temp は種類と型ごとの空き list で使い回す。</summary>
internal sealed class FunctionCompiler
{
    private readonly ModuleBuilder _m;
    private readonly FunctionDef _f;
    private readonly string _className;
    private readonly List<ulong> _code = [];
    private readonly List<Dictionary<string, Val>> _scopes = [];
    private readonly Dictionary<(Kind, ushort), Stack<int>> _free = [];
    private readonly List<int> _temps = [];
    private readonly Dictionary<string, Stack<int>> _freeBlocks = [];
    private readonly List<(string Key, int Base)> _blocksInUse = [];
    private readonly List<(Label Break, Label Continue)> _loops = [];
    private readonly List<Label> _breakScopes = [];
    private VType _returnType = VType.Void;
    private int _lastLine = -1;

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
        if (!method.IsStatic) throw new VmException($"{_f.Name}: instance methods are not in V1");
        if (!method.OutParameters.IsDefaultOrEmpty) throw new VmException($"{_f.Name}: out is not in V1");
        _returnType = _m.Parse(method.ReturnType);
        _scopes.Add([]);
        var types = method.ParameterTypes.IsDefault
            ? throw new VmException($"{_f.Name}: parameter types missing")
            : method.ParameterTypes;
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var t = _m.Parse(types[i]);
            _scopes[^1][method.Parameters[i]] = NewVal(t);
        }
        _f.NParams = _f.Regs.Count;
        if (_returnType is not VoidT)
            foreach (var l in _m.Leaves(_returnType))
                _f.Rets.Add((l.Kind, _m.TypeIndex(l.Type)));
        Block(method.Body, newScope: false);
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
        var kind = leafType is PrimT p ? p.Kind : Kind.Ref;
        var type = _m.TypeIndex(leafType);
        _f.Regs.Add((kind, type));
        return _f.Regs.Count - 1;
    }

    private Val NewVal(VType t) => new(t, _m.Leaves(t).Select(l => NewReg(l.Type)).ToArray());

    private Val Temp(VType t)
    {
        var regs = new int[_m.Leaves(t).Count];
        var leaves = _m.Leaves(t);
        for (var i = 0; i < regs.Length; i++)
        {
            var key = (leaves[i].Kind, _m.TypeIndex(leaves[i].Type));
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
            var (k, t) = _f.Regs[r];
            if (!_free.TryGetValue((k, t), out var stack)) _free[(k, t)] = stack = new Stack<int>();
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

    /// <summary>呼び出しの引数の区画: 連続する register。同じ並びの区画を文の間で使い回す。</summary>
    private int Block(IReadOnlyList<VType> leafTypes)
    {
        var key = string.Join(",", leafTypes.Select(t => $"{(int)(t is PrimT p ? p.Kind : Kind.Ref)}:{_m.TypeIndex(t)}"));
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

    // ---- 命令 ----

    private void Emit(Op op, int a = 0, int b = 0, int c = 0, int x = 0)
    {
        if ((uint)a > 0xFFFF || (uint)b > 0xFFFF || (uint)c > 0xFFFF || (uint)x > 0xFF)
            throw new VmException($"{_f.Name}: operand out of range");
        _code.Add((ulong)op | (ulong)a << 8 | (ulong)b << 24 | (ulong)c << 40 | (ulong)x << 56);
    }

    private void EmitImm(Op op, int a, uint imm)
    {
        _code.Add((ulong)op | (ulong)(ushort)a << 8 | (ulong)imm << 24);
    }

    private sealed class Label
    {
        public int Target = -1;
        public readonly List<int> Patches = [];
    }

    private readonly HashSet<int> _labelTargets = [];

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
                var v = NewVal(type);
                if (local.Init is { } init) ExprAs(init, type, v);
                else EmitZero(v);
                _scopes[^1][local.Name] = v;
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
            case IlBreak:
                if (_loops.Count == 0 && _breakScopes.Count == 0) throw new VmException("break outside loop");
                Jump(Op.JMP, _breakScopes.Count > 0 ? _breakScopes[^1] : _loops[^1].Break);
                break;
            case IlContinue:
                Jump(Op.JMP, _loops[^1].Continue);
                break;
            case IlReturn ret:
                if (ret.Value is null) Emit(Op.RET0);
                else
                {
                    var v = ExprAs(ret.Value, _returnType);
                    // RET は連続する register を返す。struct の葉が散らばっていれば区画に集める
                    var b = Contiguous(v);
                    Emit(Op.RET, b);
                }
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
                throw new VmException($"{_f.Name}: statement {s.GetType().Name} is not in V1");
        }
        EndStatement();
    }

    private int Contiguous(Val v)
    {
        var ok = true;
        for (var i = 1; i < v.Regs.Length; i++) ok &= v.Regs[i] == v.Regs[0] + i;
        if (ok) return v.Regs[0];
        var b = Block(_m.Leaves(v.Type).Select(l => l.Type).ToList());
        for (var i = 0; i < v.Regs.Length; i++) Emit(Op.MOV, b + i, v.Regs[i]);
        return b;
    }

    /// <summary>Lua の numeric for (`for v = start, limit`)。limit は 1 回だけ評価し、
    /// v が本文で書き換えられるときは隠れた counter を回す (il-reference: IlNumericFor)。</summary>
    private void NumericFor(IlNumericFor nf)
    {
        _scopes.Add([]);
        var assigned = AssignsVar(nf.Body, nf.Var);
        var start = Convert(Expr(nf.Start), VType.Int);
        var limit = NewVal(VType.Int);
        Store(limit, Expr(nf.Limit));
        var counter = NewVal(VType.Int);
        Store(counter, start);
        var v = assigned ? NewVal(VType.Int) : counter;
        _scopes[^1][nf.Var] = v;
        EndStatement();
        var top = new Label();
        var cont = new Label();
        var exit = new Label();
        Jump(Op.JF_LE_I32, exit, counter.Reg, limit.Reg);
        Mark(top);
        if (assigned) Emit(Op.MOV, v.Reg, counter.Reg);
        _loops.Add((exit, cont));
        Block(nf.Body);
        _loops.RemoveAt(_loops.Count - 1);
        Mark(cont);
        Jump(Op.JT_EQ_I32, exit, counter.Reg, limit.Reg);
        Emit(Op.ADDI_I32, counter.Reg, counter.Reg, 1);
        Jump(Op.JMP, top);
        Mark(exit);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    private static bool AssignsVar(IlBlock body, string name) =>
        body.Stats.Any(s => AssignsVar(s, name));

    private static bool AssignsVar(IlStat s, string name) => s switch
    {
        IlAssign { Target: IlVar v } => v.Name == name,
        IlMultiAssign m => m.Targets.Any(t => t is IlVar v && v.Name == name),
        IlLocal l => l.Name == name, // 同名の local が隠す: 保守的に counter を分ける
        IlIf i => i.Arms.Any(a => AssignsVar(a.Body, name)) || (i.Else is { } e && AssignsVar(e, name)),
        IlWhile w => AssignsVar(w.Body, name) || (w.Trailer is { } t && AssignsVar(t, name)),
        IlRepeat r => AssignsVar(r.Body, name),
        IlNumericFor f => f.Var == name || AssignsVar(f.Body, name),
        IlDo d => AssignsVar(d.Body, name),
        IlBreakScope b => AssignsVar(b.Body, name),
        _ => false,
    };

    // ---- 代入 ----

    private void Assign(IlExpr target, IlExpr value)
    {
        switch (target)
        {
            case IlVar v when Lookup(v.Name) is { } local:
                // 結果を local へ直接書く (式の最後の命令だけが dest を書くので、右辺が local を読んでもよい)
                ExprAs(value, local.Type, local);
                return;
            case IlParen p:
                Assign(p.E, value);
                return;
        }
        var place = PlaceOf(target);
        var val = ExprAs(value, place.Type);
        StorePlace(place, val);
    }

    /// <summary>格納場所。Base が static / object の field / 配列の要素で、Path はその中の葉の範囲。</summary>
    private sealed record Place(VType Type, string Kind, int Slot = 0, Val? Obj = null, Val? Arr = null,
        Val? Idx = null, int Leaf = 0, VType? Owner = null);

    private Place PlaceOf(IlExpr e)
    {
        switch (e)
        {
            case IlParen p:
                return PlaceOf(p.E);
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
                    return new Place(type, "field", Obj: obj, Leaf: LeafIndex(_m.ClassLeaves(c.Name), f.Name), Owner: recvType);
                }
                // struct の中の field: 外側の場所の葉の範囲を狭める
                var outer = PlaceOf(f.Recv);
                var inner = LeafIndex(_m.Leaves(recvType), f.Name);
                return outer with { Type = type, Leaf = outer.Leaf + inner };
            }
            case IlIndex idx:
            {
                var arrType = TypeOf(idx.Recv) as ArrayT
                    ?? throw new VmException($"{_f.Name}: index of non-array (List is not in V1)");
                var arr = Expr(idx.Recv);
                var i = Convert(Expr(idx.Idx), VType.Int);
                return new Place(arrType.Elem, "elem", Arr: arr, Idx: i, Owner: arrType);
            }
            case IlVar v when Lookup(v.Name) is { } local:
                return new Place(local.Type, "local", Obj: local);
            default:
                throw new VmException($"{_f.Name}: place {e.GetType().Name} is not in V1");
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
                case "static":
                    EmitImm(Op.STS, r, (uint)(p.Slot + p.Leaf + k));
                    break;
                case "field":
                    Emit(Pick(Op.STF_I32, kind), p.Obj!.Reg, r, p.Leaf + k);
                    break;
                case "elem" when ((ArrayT)p.Owner!).Elem is StructT:
                    Emit(Pick(Op.STEF_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r, p.Leaf + k);
                    break;
                case "elem":
                    Emit(Pick(Op.STE_I32, kind), p.Arr!.Reg, p.Idx!.Reg, r);
                    break;
                case "local":
                    Emit(Op.MOV, p.Obj!.Regs[p.Leaf + k], r);
                    break;
            }
        }
    }

    private Val LoadPlace(Place p, Val? dest)
    {
        var d = dest ?? Temp(p.Type);
        var leaves = _m.Leaves(p.Type);
        for (var k = 0; k < leaves.Count; k++)
        {
            var r = d.Regs[k];
            var kind = leaves[k].Kind;
            switch (p.Kind)
            {
                case "static":
                    EmitImm(Op.LDS, r, (uint)(p.Slot + p.Leaf + k));
                    break;
                case "field":
                    Emit(Pick(Op.LDF_I32, kind), r, p.Obj!.Reg, p.Leaf + k);
                    break;
                case "elem" when ((ArrayT)p.Owner!).Elem is StructT:
                    Emit(Pick(Op.LDEF_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg, p.Leaf + k);
                    break;
                case "elem":
                    Emit(Pick(Op.LDE_I32, kind), r, p.Arr!.Reg, p.Idx!.Reg);
                    break;
                case "local":
                    if (dest is null)
                        return new Val(p.Type, p.Obj!.Regs.Skip(p.Leaf).Take(leaves.Count).ToArray());
                    Emit(Op.MOV, r, p.Obj!.Regs[p.Leaf + k]);
                    break;
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

    // ---- 式 ----

    private Val? Lookup(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var v))
                return v;
        return null;
    }

    /// <summary>型 to の値として評価する。null の literal は to の ref register に LDNULL。</summary>
    private Val ExprAs(IlExpr e, VType to, Val? dest = null)
    {
        while (e is IlParen p) e = p.E;
        if (e is IlLit { LuaText: "nil" })
        {
            if (to is not (ClassT or ArrayT)) throw new VmException($"{_f.Name}: null as {to}");
            var d = dest ?? Temp(to);
            Emit(Op.LDNULL, d.Reg);
            return d;
        }
        if (dest is not null && TypeOf(e) == to) return Expr(e, dest);
        var v = Convert(Expr(e), to);
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
        if (v.Type is NullT && to is ClassT or ArrayT)
        {
            var t = Temp(to);
            Emit(Op.LDNULL, t.Reg);
            return t;
        }
        throw new VmException($"{_f.Name}: cannot convert {v.Type} to {to}");
    }

    private VType TypeOf(IlExpr e) => e switch
    {
        IlLit lit => LitType(lit),
        IlVar v => Lookup(v.Name)?.Type ?? throw new VmException($"{_f.Name}: unknown name {v.Name}"),
        IlParen p => TypeOf(p.E),
        IlField { Recv: IlVar cls } f when Lookup(cls.Name) is null && _m.IsClass(cls.Name)
            => _m.Static(cls.Name, f.Name).Type,
        IlField f => _m.FieldType(TypeOf(f.Recv), f.Name),
        IlIndex i => TypeOf(i.Recv) is ArrayT a ? a.Elem : throw new VmException($"{_f.Name}: index of non-array"),
        IlLen => VType.Int,
        IlBin b => BinType(b),
        IlUn { Op: IlUnOp.Not } => VType.Bool,
        IlUn { Op: IlUnOp.ToFloat } => VType.Float,
        IlUn u => TypeOf(u.E),
        IlCall c => CallType(c),
        IlNewObj n => _m.Parse(n.TypeName),
        IlNewArray a => new ArrayT(_m.Parse(a.ElementType)),
        IlNumericConvert nc => _m.Parse(nc.TargetType),
        IlStructCopy sc => TypeOf(sc.E),
        _ => throw new VmException($"{_f.Name}: expression {e.GetType().Name} is not in V1"),
    };

    private static VType LitType(IlLit lit)
    {
        if (lit.Type == "float") return VType.Float;
        if (lit.Type is "int" or "char") return VType.Int;
        var t = lit.LuaText;
        if (t is "true" or "false") return VType.Bool;
        if (t == "nil") return VType.Null;
        if (t.StartsWith('"') || t.StartsWith('\'') || t.StartsWith('[')) throw new VmException("string literal is not in V1");
        if (t.StartsWith("0x") || t.StartsWith("0X")) return VType.Int;
        return t.Contains('.') || t.Contains('e') || t.Contains('E') ? VType.Float : VType.Int;
    }

    private VType BinType(IlBin b) => b.Op switch
    {
        IlBinOp.Eq or IlBinOp.Ne or IlBinOp.Lt or IlBinOp.Le or IlBinOp.Gt or IlBinOp.Ge
            or IlBinOp.And or IlBinOp.Or => VType.Bool,
        IlBinOp.BitAnd or IlBinOp.BitOr or IlBinOp.BitXor or IlBinOp.Shl or IlBinOp.Shr => TypeOf(b.L),
        IlBinOp.DivNum => VType.Float,
        IlBinOp.Concat => throw new VmException("string concat is not in V1"),
        _ => TypeOf(b.L) == VType.Float || TypeOf(b.R) == VType.Float ? VType.Float : VType.Int,
    };

    private VType CallType(IlCall c) => c.Callee switch
    {
        "__tcs_irem" or "__tcs_idiv" or "__tcs_trunc" => VType.Int,
        "print" => VType.Void,
        _ => _m.Parse(Method(c.Callee).ReturnType),
    };

    private IlMethodInfo Method(string callee)
    {
        var dot = callee.LastIndexOf('.');
        if (dot < 0) throw new VmException($"{_f.Name}: call '{callee}' is not in V1");
        var cls = callee[..dot];
        var name = callee[(dot + 1)..];
        if (!_m.Classes.TryGetValue(cls, out var info))
            throw new VmException($"{_f.Name}: call '{callee}' is not in V1");
        return info.Methods.SingleOrDefault(m => m.Name == name && m.IsStatic)
            ?? throw new VmException($"{_f.Name}: no static method {callee}");
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
                var d = dest ?? Temp(type);
                if (type is NullT) throw new VmException("null literal without a type");
                EmitImm(Op.LDK, d.Reg, LitBits(lit, type));
                return d;
            }
            case IlVar v:
            {
                var local = Lookup(v.Name) ?? throw new VmException($"{_f.Name}: unknown name {v.Name}");
                if (dest is null) return local;
                Store(dest, local);
                return dest;
            }
            case IlField or IlIndex:
                return LoadPlace(PlaceOf(e), dest);
            case IlLen len:
            {
                if (TypeOf(len.E) is not ArrayT) throw new VmException($"{_f.Name}: length of non-array is not in V1");
                var arr = Expr(len.E);
                var d = dest ?? Temp(VType.Int);
                Emit(Op.ARR_LEN, d.Reg, arr.Reg);
                return d;
            }
            case IlBin b:
                return Bin(b, dest);
            case IlUn u:
                return Un(u, dest);
            case IlCall c:
                return Call(c, dest, discard);
            case IlNewObj n:
            {
                var type = _m.Parse(n.TypeName);
                if (!n.Args.IsEmpty) throw new VmException($"{_f.Name}: constructor arguments are not in V1");
                if (type is StructT)
                {
                    var d0 = dest ?? Temp(type);
                    EmitZero(d0);
                    return d0;
                }
                var cls = _m.Classes[((ClassT)type).Name];
                if (!cls.Ctors.IsDefaultOrEmpty || cls.Fields.Any(f => !f.IsStatic && f.Init is not null))
                    throw new VmException($"{_f.Name}: constructors and field initializers are not in V1");
                var d = dest ?? Temp(type);
                Emit(Op.NEW, d.Reg, 0, _m.TypeIndex(type));
                return d;
            }
            case IlStructCopy sc:
                // register は値なので、copy 地点 (il-spec §10) は評価そのもの。`__copy` 関数は要らない
                if (sc.Nullable) throw new VmException($"{_f.Name}: nullable struct is not in V1");
                return Expr(sc.E, dest);
            case IlNumericConvert nc:
            {
                if (nc.LuaCheck is not null) throw new VmException($"{_f.Name}: unboxing is not in V1");
                var to = _m.Parse(nc.TargetType);
                var v = Expr(nc.Value);
                if (v.Type == to) return dest is null ? v : Store2(dest, v);
                var d = dest ?? Temp(to);
                if (v.Type == VType.Int && to == VType.Float) Emit(Op.I2F, d.Reg, v.Reg);
                else if (v.Type == VType.Float && to == VType.Int) Emit(Op.F2I, d.Reg, v.Reg);
                else throw new VmException($"{_f.Name}: conversion {v.Type} -> {to} is not in V1");
                return d;
            }
            case IlNewArray a:
            {
                var type = new ArrayT(_m.Parse(a.ElementType));
                var len = Convert(Expr(a.Length), VType.Int);
                var d = dest ?? Temp(type);
                Emit(Op.NEWARR, d.Reg, len.Reg, _m.TypeIndex(type));
                return d;
            }
            default:
                throw new VmException($"{_f.Name}: expression {e.GetType().Name} is not in V1");
        }
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
        switch (b.Op)
        {
            case IlBinOp.Eq or IlBinOp.Ne or IlBinOp.Lt or IlBinOp.Le or IlBinOp.Gt or IlBinOp.Ge
                or IlBinOp.And or IlBinOp.Or:
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
        }
        {
            var resultType = BinType(b);
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
                _ => throw new VmException($"{_f.Name}: operator {b.Op} on {t} is not in V1"),
            };
            Emit(op, d.Reg, l.Reg, r.Reg);
            return d;
        }
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
        if (t is ClassT or ArrayT or NullT && op is IlBinOp.Eq or IlBinOp.Ne) return (Op.EQ_REF, false, op == IlBinOp.Ne);
        throw new VmException($"comparison {op} on {t} is not in V1");
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
            case IlBin { Op: IlBinOp.And } and:
                if (!jumpIf)
                {
                    Cond(and.L, false, target);
                    Cond(and.R, false, target);
                }
                else
                {
                    var skip = new Label();
                    Cond(and.L, false, skip);
                    Cond(and.R, true, target);
                    Mark(skip);
                }
                return;
            case IlBin { Op: IlBinOp.Or } or:
                if (jumpIf)
                {
                    Cond(or.L, true, target);
                    Cond(or.R, true, target);
                }
                else
                {
                    var skip = new Label();
                    Cond(or.L, true, skip);
                    Cond(or.R, false, target);
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
                    var wantTrue = jumpIf != inverted;
                    // JT_x と JF_x は隣り合う
                    var jop = wantTrue ? op : op + 1;
                    Jump(jop, target, swap ? r.Reg : l.Reg, swap ? l.Reg : r.Reg);
                    return;
                }
                break;
            }
        }
        var v = Convert(Expr(cond), VType.Bool);
        Jump(jumpIf ? Op.JT : Op.JF, target, v.Reg);
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
                var v = Expr(u.E);
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

    private Val Call(IlCall c, Val? dest, bool discard)
    {
        switch (c.Callee)
        {
            case "__tcs_irem" or "__tcs_idiv":
            {
                var l = Convert(Expr(c.Args[0]), VType.Int);
                var r = Convert(Expr(c.Args[1]), VType.Int);
                var d = dest ?? Temp(VType.Int);
                Emit(c.Callee == "__tcs_irem" ? Op.REM_I32 : Op.DIV_I32, d.Reg, l.Reg, r.Reg);
                return d;
            }
            case "__tcs_trunc":
            {
                var v = Convert(Expr(c.Args[0]), VType.Float);
                var d = dest ?? Temp(VType.Int);
                Emit(Op.F2I, d.Reg, v.Reg);
                return d;
            }
            case "print":
                Print(c);
                return new Val(VType.Void, []);
        }
        var method = Method(c.Callee);
        var index = _m.FindFunction(c.Callee) ?? throw new VmException($"no function {c.Callee}");
        var paramTypes = method.ParameterTypes.Select(_m.Parse).ToList();
        if (paramTypes.Count != c.Args.Length) throw new VmException($"{_f.Name}: default arguments are not in V1");
        var argLeaves = paramTypes.SelectMany(t => _m.Leaves(t).Select(l => l.Type)).ToList();
        // 引数は区画に直接書く。区画は文の終わりまで他に使われない
        var block = argLeaves.Count > 0 ? Block(argLeaves) : 0;
        var at = block;
        for (var i = 0; i < c.Args.Length; i++)
        {
            var n = _m.Leaves(paramTypes[i]).Count;
            var slot = new Val(paramTypes[i], Enumerable.Range(at, n).ToArray());
            ExprAs(c.Args[i], paramTypes[i], slot);
            at += n;
        }
        var retType = _m.Parse(method.ReturnType);
        if (retType is VoidT)
        {
            Emit(Op.CALL, block, 0, index);
            return new Val(VType.Void, []);
        }
        // 戻り値は連続する register に入る。dest が連続ならそこへ直接
        Val ret;
        if (dest is { } d1 && Enumerable.Range(0, d1.Regs.Length).All(k => d1.Regs[k] == d1.Regs[0] + k))
            ret = d1;
        else
        {
            var rb = Block(_m.Leaves(retType).Select(l => l.Type).ToList());
            ret = new Val(retType, Enumerable.Range(rb, _m.Leaves(retType).Count).ToArray());
        }
        Emit(Op.CALL, block, ret.Regs[0], index);
        if (dest is { } d2 && d2 != ret)
        {
            Store(d2, ret);
            return d2;
        }
        return ret;
    }

    private void Print(IlCall c)
    {
        if (c.Args.Length != 1) throw new VmException("print with several values");
        var arg = c.Args[0];
        if (arg is IlCall { Callee: "__tcs_fstr" or "__tcs_bstr" or "tostring" } conv) arg = conv.Args[0];
        var t = TypeOf(arg);
        var v = Expr(arg);
        if (t == VType.Float) Emit(Op.PRINT_F32, v.Reg);
        else if (t == VType.Int) Emit(Op.PRINT_I32, v.Reg);
        else if (t == VType.Bool) Emit(Op.PRINT_B, v.Reg);
        else throw new VmException($"print of {t} is not in V1");
    }
}
