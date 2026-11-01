using System.Collections.Immutable;
using System.Text;
using TinyCs.Backend;

namespace TinyCs.Vm;

/// <summary>.tcsb の 1 module。型 / 定数 / static / 関数の表を作り、byte 列に書く (形式は tcsvm_load.c の parse)。
/// 関数は呼ばれた (到達した) ものだけを作る。</summary>
internal sealed class ModuleBuilder
{
    public const uint Magic = 0x42534354; // "TCSB"
    public const uint Version = 4;
    public const ushort NoType = 0xFFFF;

    private readonly Dictionary<string, IlClassInfo> _classes = [];
    private readonly Dictionary<string, IlStructInfo> _structs = [];
    private readonly Dictionary<string, IlEnumInfo> _enums = [];
    private readonly Dictionary<string, string> _displayToName = [];

    private readonly List<string> _strings = [];
    private readonly Dictionary<string, int> _stringIndex = [];
    private readonly List<byte[]> _consts = [];
    private readonly Dictionary<string, int> _constIndex = [];
    private readonly List<VType> _types = [];
    private readonly Dictionary<VType, int> _typeIndex = [];
    private readonly List<(Kind Kind, ushort Type)> _statics = [];
    /// <summary>static の field の表 ("型.field"、先頭の slot、型)。reload は名前で旧版の static と対応させる
    /// (doc/object-model.md §6)。遅延初期化の flag は "型.&lt;init&gt;"。</summary>
    private readonly List<(string Name, int Slot, VType Type)> _staticFields = [];

    public IlClassInfo? ClassInfo(string name) => _classes.GetValueOrDefault(Canon(name));
    private readonly Dictionary<(string Class, string Field), (int Slot, VType Type)> _staticSlots = [];
    public readonly List<FunctionDef> Functions = [];
    private readonly Dictionary<string, int> _functionIndex = [];
    private readonly Queue<FunctionDef> _pending = [];
    public int Entry = -1;

    public ModuleBuilder(IlExportResult program)
    {
        foreach (var c in program.Classes)
        {
            _classes[c.Name] = c;
            if (c.DisplayName is { } d) _displayToName[d] = c.Name;
        }
        if (!program.Structs.IsDefault)
            foreach (var s in program.Structs)
            {
                _structs[s.Name] = s;
                if (s.DisplayName is { } d) _displayToName[d] = s.Name;
            }
        if (!program.ForeignMethods.IsDefault)
            foreach (var m in program.ForeignMethods) _foreignMethods[m.Name] = m;
        if (!program.ForeignValues.IsDefault)
            foreach (var v in program.ForeignValues) _foreignValues[v.Name] = v;
        if (!program.EnumTypes.IsDefault)
            foreach (var e in program.EnumTypes)
            {
                _enums[e.Name] = e;
                if (e.DisplayName is { } d) _displayToName[d] = e.Name;
            }
    }

    // host (--ref の stub) の関数と値。名前は IL の host の名前 ("eng.gfx.begin")
    private readonly Dictionary<string, IlForeignMethod> _foreignMethods = [];
    private readonly Dictionary<string, IlForeignValue> _foreignValues = [];

    public IlForeignMethod? ForeignMethod(string name) => _foreignMethods.GetValueOrDefault(name);

    /// <summary>`--ref` の interface の method (receiver は interface の表示名、method は C# の名前)。</summary>
    public IlForeignMethod? ForeignInterfaceMethod(string receiver, string method) =>
        _foreignMethods.Values.FirstOrDefault(m => m.Receiver == receiver
            && (m.MethodName == method || m.Name.EndsWith("." + method))); // IL は host の名前 (snake_case) で呼ぶ
    public IlForeignValue? ForeignValue(string name) => _foreignValues.GetValueOrDefault(name);

    public IReadOnlyDictionary<string, IlClassInfo> Classes => _classes;
    public IReadOnlyDictionary<string, IlStructInfo> Structs => _structs;

    public bool IsStruct(string name) => _structs.ContainsKey(Canon(name));

    public string CanonName(string name) => Canon(name);

    private string Canon(string name) => _displayToName.TryGetValue(name, out var n) ? n : name;

    public bool IsClass(string name) => _classes.ContainsKey(Canon(name));

    /// <summary>static field を持てる型の名前 (class か struct)。</summary>
    public bool IsTypeName(string name) => _classes.ContainsKey(Canon(name)) || _structs.ContainsKey(Canon(name));

    public bool IsEnum(string name) => _enums.ContainsKey(Canon(name));

    public int EnumValue(string enumName, string member)
    {
        var e = _enums[Canon(enumName)];
        foreach (var (name, value) in e.Members)
            if (name == member) return value;
        throw new VmException($"no member {member} in enum {enumName}");
    }

    // ---- 型 ----

    public VType Parse(string type)
    {
        type = type.Trim();
        // 参照型の nullable 注釈は型を変えない。値型の T? は Nullable で、まだ持たない
        if (type.EndsWith('?'))
        {
            var inner = Parse(type[..^1]);
            return inner.IsRef ? inner : new NullableT(inner);
        }
        if (type.EndsWith("[]")) return new ArrayT(Parse(type[..^2]));
        // Dictionary の Keys / Values は VM では足した順の List (LINQ の受け手)
        if (type.EndsWith(">.ValueCollection") && Parse(type[..^".ValueCollection".Length]) is DictT vd) return new ListT(vd.Value);
        if (type.EndsWith(">.KeyCollection") && Parse(type[..^".KeyCollection".Length]) is DictT kd) return new ListT(kd.Key);
        var lt = type.IndexOf('<');
        if (lt > 0 && type.EndsWith('>'))
        {
            var head = type[..lt];
            var args = SplitArgs(type[(lt + 1)..^1]).Select(Parse).ToList();
            switch (head)
            {
                case "System.Collections.Generic.List" or "List" when args.Count == 1:
                    return new ListT(args[0]);
                case "System.Collections.Generic.Dictionary" or "Dictionary" when args.Count == 2:
                    return new DictT(args[0], args[1]);
                case "System.Func" or "Func":
                    return new FuncT(args.Take(args.Count - 1).ToList(), args[^1]);
                case "System.Action" or "Action":
                    return new FuncT(args, VType.Void);
                // Predicate<T> / Comparison<T> は Func<T, bool> / Func<T, T, int> と同じ関数型 (tcs2c と同じ)
                case "System.Predicate" or "Predicate" when args.Count == 1:
                    return new FuncT([args[0]], VType.Bool);
                case "System.Comparison" or "Comparison" when args.Count == 1:
                    return new FuncT([args[0], args[0]], VType.Int);
                case "System.Nullable" or "Nullable" when args.Count == 1:
                    return new NullableT(args[0]);
                case "System.Collections.Generic.KeyValuePair" or "KeyValuePair" when args.Count == 2:
                    return new KvpT(args[0], args[1]);
                // Queue<T> は VM では List (Dequeue は先頭を外す)、HashSet<T> は Dictionary<T, bool> (列挙は .NET と同じ順)
                case "System.Collections.Generic.Queue" or "Queue" when args.Count == 1:
                    return new ListT(args[0]);
                case "System.Collections.Generic.HashSet" or "HashSet" when args.Count == 1:
                    return new DictT(args[0], VType.Bool);
            }
            throw new VmException($"type '{type}' is not in the VM yet");
        }
        switch (type)
        {
            case "int" or "uint" or "char" or "byte" or "sbyte" or "short" or "ushort": return VType.Int;
            case "object": return VType.Object;
            case "TinySystem.Text.Utf8Bytes" or "Utf8Bytes": return new Utf8T();
            case "TinySystem.Random" or "Random": return new RandomT();
            case "float": return VType.Float;
            case "bool": return VType.Bool;
            case "void": return VType.Void;
            case "string": return VType.String;
            case "System.Action" or "Action": return new FuncT([], VType.Void);
        }
        var name = Canon(type);
        if (_classes.TryGetValue(name, out var cls)) return cls.IsInterface ? new IfaceT(name) : new ClassT(name);
        if (_structs.ContainsKey(name)) return new StructT(name);
        if (_enums.ContainsKey(name)) return VType.Int; // enum は i32 (il-spec §2)
        // --ref の module の interface (X-4): VM の class が実装し、C の module が呼ぶ。VM の中では object として持つ
        // IL は型 test で `Ed_ITool`、宣言で `Ed.ITool` と書くので、表示名にそろえて 1 つの型にする
        var host = _classes.Values.Where(c => !c.Interfaces.IsDefaultOrEmpty).SelectMany(c => c.Interfaces)
            .Concat(_foreignMethods.Values.Select(m => m.Receiver))
            .FirstOrDefault(n => n != null && (n == type || n.Replace('.', '_') == type));
        if (host is not null) return new HostIfaceT(host);
        throw new VmException($"type '{type}' is not in the VM yet");
    }

    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '<' or '(' or '[') depth++;
            else if (s[i] is '>' or ')' or ']') depth--;
            else if (s[i] == ',' && depth == 0)
            {
                list.Add(s[start..i]);
                start = i + 1;
            }
        }
        list.Add(s[start..]);
        return list;
    }

    public IReadOnlyList<Leaf> Leaves(VType t)
    {
        var list = new List<Leaf>();
        Flatten(t, "", list, 0);
        return list;
    }

    private void Flatten(VType t, string path, List<Leaf> list, int depth)
    {
        if (depth > 32) throw new VmException("struct nesting too deep");
        string Sub(string name) => path == "" ? name : path + "." + name;
        switch (t)
        {
            case StructT s:
                foreach (var f in _structs[s.Name].Fields.Where(f => !f.IsStatic))
                    Flatten(Parse(f.Type), Sub(f.Name), list, depth + 1);
                break;
            case NullableT n:
                list.Add(new Leaf(Sub("HasValue"), VType.Bool));
                Flatten(n.Inner, Sub("Value"), list, depth + 1);
                break;
            case KvpT kv:
                Flatten(kv.Key, Sub("Key"), list, depth + 1);
                Flatten(kv.Value, Sub("Value"), list, depth + 1);
                break;
            case TupleT tu:
                for (var k = 0; k < tu.Items.Count; k++)
                    if (tu.Items[k] is not VoidT) Flatten(tu.Items[k], Sub($"Item{k + 1}"), list, depth + 1);
                break;
            case Utf8T:
                list.Add(new Leaf(Sub("S"), VType.String));
                list.Add(new Leaf(Sub("O"), VType.Int));
                list.Add(new Leaf(Sub("Length"), VType.Int));
                break;
            case PrimT:
            case { IsRef: true }:
                list.Add(new Leaf(path, t));
                break;
            default:
                throw new VmException($"no leaves for {t}");
        }
    }

    /// <summary>class の instance の葉 (instance field を struct まで展開)。cell は中の値の葉。</summary>
    public IReadOnlyList<Leaf> ObjectLeaves(VType t)
    {
        if (t is CellT cell) return Leaves(cell.Inner);
        var cls = (ClassT)t;
        var list = new List<Leaf>();
        foreach (var f in _classes[cls.Name].Fields.Where(f => !f.IsStatic))
            Flatten(Parse(f.Type), f.Name, list, 0);
        return list;
    }

    public VType FieldType(VType owner, string field)
    {
        switch (owner)
        {
            case NullableT n when field == "Value": return n.Inner;
            case NullableT when field == "HasValue": return VType.Bool;
            case KvpT kv when field == "Key": return kv.Key;
            case KvpT kv when field == "Value": return kv.Value;
            case Utf8T when field == "Length": return VType.Int;
        }
        IEnumerable<IlFieldInfo> fields = owner switch
        {
            ClassT c => _classes[c.Name].Fields,
            StructT s => _structs[s.Name].Fields,
            _ => throw new VmException($"field '{field}' of {owner} is not in the VM yet"),
        };
        var info = fields.FirstOrDefault(f => f.Name == field && !f.IsStatic)
            ?? throw new VmException($"no field '{field}' in {owner}");
        return Parse(info.Type);
    }

    public ushort TypeIndex(VType t)
    {
        if (t is PrimT or NullT) return NoType;
        if (_typeIndex.TryGetValue(t, out var i)) return (ushort)i;
        if (!t.IsRef && !t.IsAggregate) throw new VmException($"no type index for {t}");
        i = _types.Count;
        _types.Add(t);
        _typeIndex[t] = i;
        if (_types.Count > NoType) throw new VmException("too many types");
        // 中の型も表に入れる (再帰する class は上で番号を先に取っているので止まる)
        switch (t)
        {
            case ClassT c:
                foreach (var l in ObjectLeaves(t)) TypeIndex(l.Type);
                foreach (var iface in Interfaces(c.Name)) TypeIndex(new IfaceT(iface));
                foreach (var iface in HostInterfaces(c.Name)) TypeIndex(iface);
                break;
            case CellT:
                foreach (var l in ObjectLeaves(t)) TypeIndex(l.Type);
                break;
            case { IsAggregate: true }:
                foreach (var l in Leaves(t)) TypeIndex(l.Type);
                break;
            case ArrayT a: TypeIndex(a.Elem); break;
            case ListT l:
                TypeIndex(l.Elem);
                break;
            case DictT d:
                if (d.Key.IsAggregate)
                    throw new VmException($"{t}: struct keys are not in the VM yet");
                TypeIndex(d.Key);
                TypeIndex(d.Value);
                break;
            case FuncT f:
                foreach (var p in f.Params) foreach (var l in Leaves(p)) TypeIndex(l.Type);
                if (f.Ret is not VoidT) foreach (var l in Leaves(f.Ret)) TypeIndex(l.Type);
                break;
        }
        return (ushort)i;
    }

    public (Kind Kind, ushort Type) Info(VType leafType) =>
        (leafType is PrimT p ? p.Kind : Kind.Ref, TypeIndex(leafType));

    // ---- interface ----

    /// <summary>class が実装する interface (program にあるものだけ)。</summary>
    public IEnumerable<string> Interfaces(string cls) =>
        _classes[cls].Interfaces.IsDefault ? []
            : _classes[cls].Interfaces.Select(Canon).Where(n => _classes.TryGetValue(n, out var i) && i.IsInterface);

    /// <summary>class が実装する --ref の module の interface (X-4)。C の module の interface の判定が名前で引く (object-model §10.6)。</summary>
    public IEnumerable<HostIfaceT> HostInterfaces(string cls) =>
        _classes[cls].Interfaces.IsDefault ? []
            : _classes[cls].Interfaces
                .Where(n => !_classes.ContainsKey(Canon(n)) && !n.Contains('<') && !n.StartsWith("System.", StringComparison.Ordinal))
                .Select(n => Parse(n)).OfType<HostIfaceT>();

    /// <summary>method の呼び出しの関数型。out の引数は引数から外し、戻り値と out の値を多値 (TupleT) で返す
    /// (実装の関数は戻り値の葉の後に out の葉を返す)。</summary>
    public FuncT SelectorType(IlMethodInfo method)
    {
        var types = method.ParameterTypes.Select(Parse).ToList();
        var ret = Parse(method.ReturnType);
        if (method.OutParameters.IsDefaultOrEmpty) return new FuncT(types, ret);
        var outs = method.OutParameters.ToHashSet();
        var ins = method.Parameters.Select((p, i) => (p, types[i])).Where(x => !outs.Contains(x.p)).Select(x => x.Item2).ToList();
        var outTypes = method.Parameters.Select((p, i) => (p, types[i])).Where(x => outs.Contains(x.p)).Select(x => x.Item2);
        return new FuncT(ins, new TupleT([.. (ret is VoidT ? [] : new[] { ret }), .. outTypes]));
    }

    /// <summary>class が interface (継承した interface を含む) を実装するか。</summary>
    private bool Implements(string cls, string iface)
    {
        var seen = new HashSet<string>();
        var work = new Stack<string>(Interfaces(cls));
        while (work.Count > 0)
        {
            var i = work.Pop();
            if (i == iface) return true;
            if (!seen.Add(i)) continue;
            if (_classes[i].Interfaces is { IsDefault: false } more)
                foreach (var m in more.Select(Canon).Where(n => _classes.ContainsKey(n))) work.Push(m);
        }
        return false;
    }

    private readonly List<(string Name, FuncT Type)> _selectors = [];
    private readonly Dictionary<string, int> _selectorIndex = [];
    private readonly Dictionary<string, List<(int Selector, int Func)>> _itables = [];

    /// <summary>interface の method の selector。実装する class の itable に関数を入れる (到達した class の method だけを作る)。</summary>
    public int Selector(string iface, IlMethodInfo method, Func<string, IlMethodInfo, FuncT, int> requestMethod)
    {
        // selector は名前と関数型で 1 つ (特殊化した generic な interface は同じ名前で型が違う: IGen<int> と IGen<string>)
        var ft = SelectorType(method);
        var key = $"{method.Name}:{ft}";
        if (_selectorIndex.TryGetValue(key, out var i)) return i;
        i = _selectors.Count;
        _selectors.Add((method.Name, ft));
        _selectorIndex[key] = i;
        TypeIndex(ft);
        iface = Canon(iface);
        foreach (var (name, cls) in _classes)
        {
            if (cls.IsInterface || cls.IsExternal || !Interfaces(name).Any()) continue;
            var impl = cls.Methods.FirstOrDefault(m => m.Name == method.Name && !m.IsStatic);
            if (impl is null) continue;
            // 型の違う実装は、この interface を実装する class だけ (adapter で型を合わせる)
            var same = SelectorType(impl) == ft;
            if (!same && !Implements(name, iface)) continue;
            if (!_itables.TryGetValue(name, out var table)) _itables[name] = table = [];
            table.Add((i, requestMethod(name, impl, ft)));
        }
        return i;
    }

    // ---- runtime の関数 (CALLR) ----

    private readonly List<string> _intrinsics = [];
    private readonly Dictionary<string, int> _intrinsicIndex = [];

    public int Intrinsic(string name)
    {
        if (_intrinsicIndex.TryGetValue(name, out var i)) return i;
        i = _intrinsics.Count;
        _intrinsics.Add(name);
        _intrinsicIndex[name] = i;
        return i;
    }

    // ---- 定数 ----

    public uint Const(byte[] utf8)
    {
        var key = System.Convert.ToBase64String(utf8);
        if (!_constIndex.TryGetValue(key, out var i))
        {
            i = _consts.Count;
            _consts.Add(utf8);
            _constIndex[key] = i;
        }
        TypeIndex(VType.String);
        return (uint)i;
    }

    // ---- static ----

    public (int Slot, VType Type) Static(string cls, string field)
    {
        cls = Canon(cls);
        if (_staticSlots.TryGetValue((cls, field), out var s)) return s;
        var fields = _classes.TryGetValue(cls, out var ci) ? ci.Fields
            : _structs.TryGetValue(cls, out var si) && !si.StaticFields.IsDefault ? si.StaticFields : [];
        var info = fields.FirstOrDefault(f => f.Name == field && f.IsStatic)
            ?? throw new VmException($"no static '{field}' in {cls}");
        var type = Parse(info.Type);
        var slot = _statics.Count;
        foreach (var l in Leaves(type)) _statics.Add(Info(l.Type));
        _staticFields.Add(($"{cls}.{field}", slot, type));
        return _staticSlots[(cls, field)] = (slot, type);
    }

    // ---- static の初期化 (il-spec と tcs2c と同じ): initializer が定数だけの型は最初に、
    //      ほかの型は最初の static の読み書きで 1 回だけ (C# §15.5.6.2 と同じ。前方参照と循環を扱える) ----

    private static bool IsConstantExpr(IlExpr e) => e switch
    {
        IlLit => true,
        IlParen p => IsConstantExpr(p.E),
        IlUn u => IsConstantExpr(u.E),
        IlBin b => IsConstantExpr(b.L) && IsConstantExpr(b.R),
        _ => false,
    };

    /// <summary>static field を持つ型 (class と struct) を宣言の順に。</summary>
    public IEnumerable<(string Name, IReadOnlyList<IlFieldInfo> Statics)> StaticOwners()
    {
        foreach (var (name, c) in _classes)
            if (!c.IsInterface && !c.IsExternal) yield return (name, c.Fields.Where(f => f.IsStatic).ToList());
        foreach (var (name, s) in _structs)
            if (!s.StaticFields.IsDefaultOrEmpty) yield return (name, s.StaticFields.Where(f => f.IsStatic).ToList());
    }

    public bool IsLazyStatic(string owner)
    {
        owner = Canon(owner);
        var fields = _classes.TryGetValue(owner, out var c) ? c.Fields
            : _structs.TryGetValue(owner, out var s) && !s.StaticFields.IsDefault ? s.StaticFields : [];
        return fields.Any(f => f.IsStatic && f.Init is { } init && !IsConstantExpr(init));
    }

    private readonly Dictionary<string, int> _initFlags = [];

    /// <summary>遅延初期化の型の「初期化を始めた」flag (bool の static)。</summary>
    public int InitFlag(string owner)
    {
        owner = Canon(owner);
        if (_initFlags.TryGetValue(owner, out var slot)) return slot;
        slot = _statics.Count;
        _statics.Add((Kind.Bool, NoType));
        _staticFields.Add(($"{owner}.<init>", slot, VType.Bool));
        return _initFlags[owner] = slot;
    }

    private int _sharedRandom = -1;

    /// <summary>Random.Shared を置く隠れた static (最初の参照で作って seed する)。</summary>
    public int SharedRandomSlot()
    {
        if (_sharedRandom >= 0) return _sharedRandom;
        _sharedRandom = _statics.Count;
        _statics.Add(Info(new RandomT()));
        _staticFields.Add(("TinySystem.Random.Shared", _sharedRandom, new RandomT()));
        return _sharedRandom;
    }

    // ---- 関数 ----

    /// <summary>関数の番号を返す。初めてなら作り、compile を待ち行列に入れる。</summary>
    public int Request(string name, Action<FunctionDef> compile)
    {
        if (_functionIndex.TryGetValue(name, out var i)) return i;
        i = Functions.Count;
        if (i > 0xFFFF) throw new VmException("too many functions");
        var def = new FunctionDef(name) { Compile = compile };
        Functions.Add(def);
        _functionIndex[name] = i;
        _pending.Enqueue(def);
        return i;
    }

    public int? FindFunction(string name) => _functionIndex.TryGetValue(name, out var i) ? i : null;

    /// <summary>待ち行列が空になるまで compile する (compile が新しい関数を呼べば、それも入る)。</summary>
    public void CompilePending()
    {
        while (_pending.Count > 0)
        {
            var def = _pending.Dequeue();
            def.Compile!(def);
        }
    }

    public int Str(string s)
    {
        if (_stringIndex.TryGetValue(s, out var i)) return i;
        i = _strings.Count;
        _strings.Add(s);
        _stringIndex[s] = i;
        return i;
    }

    // ---- 書き出し ----

    /// <summary>reload の計画の入力: 型記述子の表と static の (型、field)。</summary>
    public (List<TypeTable.Entry> Types, List<(string Owner, string Field)> Statics) Describe()
    {
        var entries = new List<TypeTable.Entry>();
        for (var i = 0; i < _types.Count; i++) entries.Add(TypeEntry(_types[i]));
        var statics = _staticFields.Select(s =>
        {
            var dot = s.Name.LastIndexOf('.');
            return (s.Name[..dot], s.Name[(dot + 1)..]);
        }).ToList();
        return (entries, statics);
    }

    public byte[] Write()
    {
        foreach (var f in Functions)
        {
            Str(f.Name);
            Str(f.File);
        }
        foreach (var (name, _) in _selectors) Str(name);
        foreach (var name in _intrinsics) Str(name);
        // static の field の表の型は型の表より先に決める (struct の型を表に足す)
        var staticFields = _staticFields.Select(s => (Name: Str(s.Name), s.Slot, Info: FieldInfo(s.Type))).ToList();
        // 型の表 (doc/object-model.md §4)。名前は string 表に先に入れる
        var entries = new List<TypeTable.Entry>();
        for (var i = 0; i < _types.Count; i++) entries.Add(TypeEntry(_types[i]));
        foreach (var name in TypeTable.Names(entries)) Str(name);

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic);
        w.Write(Version);
        w.Write(OpAbi.Hash());

        w.Write(_strings.Count);
        foreach (var s in _strings)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write(b.Length);
            w.Write(b);
        }

        w.Write(_consts.Count);
        foreach (var c in _consts)
        {
            w.Write(c.Length);
            w.Write(c);
        }

        TypeTable.Write(w, entries, Str);

        w.Write(_selectors.Count);
        foreach (var (name, ft) in _selectors)
        {
            w.Write(Str(name));
            w.Write(TypeIndex(ft));
        }

        w.Write(_intrinsics.Count);
        foreach (var name in _intrinsics) w.Write(Str(name));

        w.Write(_statics.Count);
        foreach (var info in _statics) WriteInfo(w, info);
        w.Write(staticFields.Count);
        foreach (var (name, slot, info) in staticFields)
        {
            w.Write(name);
            w.Write(slot);
            w.Write(info.Kind);
            w.Write(info.Type);
        }

        w.Write(Functions.Count);
        foreach (var f in Functions)
        {
            if (f.Code is null) throw new VmException($"function {f.Name} has no body");
            w.Write(Str(f.Name));
            w.Write(Str(f.File));
            w.Write((ushort)f.NParams);
            w.Write((ushort)f.Rets.Count);
            w.Write((ushort)f.Regs.Count);
            foreach (var info in f.Regs) WriteInfo(w, info);
            foreach (var info in f.Rets) WriteInfo(w, info);
            w.Write((ushort)f.Caps.Count);
            foreach (var info in f.Caps) WriteInfo(w, info);
            w.Write(f.Code.Count);
            foreach (var ins in f.Code) w.Write(ins);
            w.Write(f.Lines.Count);
            foreach (var (pc, line) in f.Lines)
            {
                w.Write(pc);
                w.Write(line);
            }
        }
        w.Write(Entry);
        return ms.ToArray();
    }

    // ---- 型記述子 (doc/object-model.md §4) ----

    private TypeTable.Info FieldInfo(VType t) => t switch
    {
        PrimT p => new((byte)p.Kind, NoType),
        { IsAggregate: true } => new(TypeTable.KindStruct, TypeIndex(t)),
        _ => new(TypeTable.KindRef, TypeIndex(t)),
    };

    /// <summary>値型の field (名前と型)。葉 (Leaves) はこの列を深さ優先で展開した順。</summary>
    private IEnumerable<(string Name, VType Type)> AggregateFields(VType t) => t switch
    {
        StructT s => _structs[s.Name].Fields.Where(f => !f.IsStatic).Select(f => (f.Name, Parse(f.Type))),
        NullableT n => [("HasValue", VType.Bool), ("Value", n.Inner)],
        KvpT kv => [("Key", kv.Key), ("Value", kv.Value)],
        Utf8T => [("S", VType.String), ("O", VType.Int), ("Length", VType.Int)],
        _ => throw new VmException($"fields of {t}"),
    };

    private ImmutableArray<TypeTable.Field> Fields(IEnumerable<(string Name, VType Type)> fields) =>
        [.. fields.Select(f => new TypeTable.Field(f.Name, FieldInfo(f.Type)))];

    private TypeTable.Entry TypeEntry(VType t)
    {
        var name = t.ToString()!;
        switch (t)
        {
            case ClassT c:
            {
                var info = _classes[c.Name];
                var table = _itables.TryGetValue(c.Name, out var it) ? it.OrderBy(x => x.Selector).ToList() : [];
                return new TypeTable.Entry(TypeTable.Class, name)
                {
                    LayoutHash = TypeTable.ParseLayoutHash(info.LayoutHash),
                    Flags = (byte)((info.IsRecord ? TypeTable.FlagRecord : 0)
                        | (info.Methods.Any(m => m.Name == "OnReload" && !m.IsStatic) ? TypeTable.FlagHasOnReload : 0)),
                    Fields = Fields(info.Fields.Where(f => !f.IsStatic).Select(f => (f.Name, Parse(f.Type)))),
                    Interfaces = [.. Interfaces(c.Name).Select(i => TypeIndex(new IfaceT(i))),
                        .. HostInterfaces(c.Name).Select(i => TypeIndex(i))],
                    Itable = [.. table.Select(x => ((ushort)x.Selector, (ushort)x.Func))],
                };
            }
            case CellT cell:
                return new TypeTable.Entry(TypeTable.Class, name) { Fields = Fields([("value", cell.Inner)]) };
            case RandomT:
                // 8 個の int = 64 bit の状態 × 4 (runtime の random.* が読み書きする)。先頭の pad で状態を
                // payload の 8 byte 目から置く (type_id の 4 byte の後。64 bit の読み書きを揃える)
                return new TypeTable.Entry(TypeTable.Class, name)
                {
                    Fields = Fields(Enumerable.Range(0, 9).Select(i => (i == 0 ? "pad" : $"s{i - 1}", VType.Int))),
                };
            case { IsAggregate: true }:
                return new TypeTable.Entry(TypeTable.Struct, name)
                {
                    LayoutHash = t is StructT st ? TypeTable.ParseLayoutHash(_structs[st.Name].LayoutHash) : 0,
                    Fields = Fields(AggregateFields(t)),
                };
            case ArrayT a: return new TypeTable.Entry(TypeTable.Array, name) { Element = FieldInfo(a.Elem) };
            case ListT l: return new TypeTable.Entry(TypeTable.List, name) { Element = FieldInfo(l.Elem) };
            case DictT d: return new TypeTable.Entry(TypeTable.Dict, name) { Key = FieldInfo(d.Key), Element = FieldInfo(d.Value) };
            case FuncT f:
                return new TypeTable.Entry(TypeTable.Func, name)
                {
                    Params = [.. f.Params.SelectMany(Leaves).Select(l => FieldInfo(l.Type))],
                    Rets = [.. (f.Ret is VoidT ? [] : Leaves(f.Ret)).Select(l => FieldInfo(l.Type))],
                };
            case StringT: return new TypeTable.Entry(TypeTable.String, name);
            case IfaceT: return new TypeTable.Entry(TypeTable.Iface, name);
            case ObjectT or HostIfaceT: return new TypeTable.Entry(TypeTable.Object, name);
            case BoxT b: return new TypeTable.Entry(TypeTable.Box, name) { Element = new((byte)b.Kind, NoType) };
        }
        throw new VmException($"no type entry for {t}");
    }

    private static void WriteInfo(BinaryWriter w, (Kind Kind, ushort Type) info)
    {
        w.Write((byte)info.Kind);
        w.Write(info.Type);
    }

}

internal sealed class FunctionDef(string name)
{
    public string Name { get; } = name;
    public string File = "";
    public int NParams;
    public List<(Kind Kind, ushort Type)> Regs = [];
    public List<(Kind Kind, ushort Type)> Rets = [];
    public List<(Kind Kind, ushort Type)> Caps = [];
    public List<ulong>? Code;
    public List<(int Pc, int Line)> Lines = [];
    public Action<FunctionDef>? Compile;
}
