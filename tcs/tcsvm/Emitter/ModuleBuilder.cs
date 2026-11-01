using System.Text;

namespace TinyCs.Vm;

/// <summary>.tcsb の 1 module。型 / 定数 / static / 関数の表を作り、byte 列に書く (形式は tcsvm_load.c の parse)。
/// 関数は呼ばれた (到達した) ものだけを作る。</summary>
internal sealed class ModuleBuilder
{
    public const uint Magic = 0x42534354; // "TCSB"
    public const uint Version = 2;
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
        if (!program.EnumTypes.IsDefault)
            foreach (var e in program.EnumTypes)
            {
                _enums[e.Name] = e;
                if (e.DisplayName is { } d) _displayToName[d] = e.Name;
            }
    }

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
            case "int" or "char" or "byte" or "sbyte" or "short" or "ushort": return VType.Int;
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

    private readonly List<(string Name, FuncT Type)> _selectors = [];
    private readonly Dictionary<string, int> _selectorIndex = [];
    private readonly Dictionary<string, List<(int Selector, int Func)>> _itables = [];

    /// <summary>interface の method の selector。実装する class の itable に関数を入れる (到達した class の method だけを作る)。</summary>
    public int Selector(string iface, IlMethodInfo method, Func<string, IlMethodInfo, int> requestMethod)
    {
        if (_selectorIndex.TryGetValue(method.Name, out var i)) return i;
        var ft = new FuncT(method.ParameterTypes.Select(Parse).ToList(), Parse(method.ReturnType));
        i = _selectors.Count;
        _selectors.Add((method.Name, ft));
        _selectorIndex[method.Name] = i;
        TypeIndex(ft);
        foreach (var (name, cls) in _classes)
        {
            if (cls.IsInterface || cls.IsExternal || !Interfaces(name).Any()) continue;
            var impl = cls.Methods.FirstOrDefault(m => m.Name == method.Name && !m.IsStatic);
            if (impl is null) continue;
            if (!_itables.TryGetValue(name, out var table)) _itables[name] = table = [];
            table.Add((i, requestMethod(name, impl)));
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
        return _staticSlots[(cls, field)] = (slot, type);
    }

    private int _sharedRandom = -1;

    /// <summary>Random.Shared を置く隠れた static (最初の参照で作って seed する)。</summary>
    public int SharedRandomSlot()
    {
        if (_sharedRandom >= 0) return _sharedRandom;
        _sharedRandom = _statics.Count;
        _statics.Add(Info(new RandomT()));
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

    public byte[] Write()
    {
        foreach (var f in Functions)
        {
            Str(f.Name);
            Str(f.File);
        }
        foreach (var (name, _) in _selectors) Str(name);
        foreach (var name in _intrinsics) Str(name);
        for (var i = 0; i < _types.Count; i++) Str(_types[i].ToString()!);

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

        w.Write(_types.Count);
        foreach (var t in _types)
        {
            switch (t)
            {
                case ClassT c:
                {
                    WriteLeaves(w, 1, t, ObjectLeaves(t));
                    w.Write(_classes[c.Name].IsRecord ? (byte)1 : (byte)0);
                    var ifaces = Interfaces(c.Name).ToList();
                    w.Write((ushort)ifaces.Count);
                    foreach (var iface in ifaces) w.Write(TypeIndex(new IfaceT(iface)));
                    var table = _itables.TryGetValue(c.Name, out var it) ? it.OrderBy(x => x.Selector).ToList() : [];
                    w.Write((ushort)table.Count);
                    foreach (var (sel, fn) in table)
                    {
                        w.Write((ushort)sel);
                        w.Write((ushort)fn);
                    }
                    break;
                }
                case CellT:
                    WriteLeaves(w, 1, t, ObjectLeaves(t));
                    w.Write((byte)0);
                    w.Write((ushort)0);
                    w.Write((ushort)0);
                    break;
                case RandomT:
                    // 8 個の int の葉 = 64 bit の状態 × 4 (runtime の random.* が読み書きする)
                    WriteLeaves(w, 1, t, Enumerable.Range(0, 8).Select(i => new Leaf($"s{i}", VType.Int)).ToList());
                    w.Write((byte)0);
                    w.Write((ushort)0);
                    w.Write((ushort)0);
                    break;
                case { IsAggregate: true }:
                    WriteLeaves(w, 2, t, Leaves(t));
                    break;
                case IfaceT:
                    WriteHead(w, 8, t);
                    break;
                case ObjectT:
                    WriteHead(w, 9, t);
                    break;
                case BoxT b:
                    WriteHead(w, 10, t);
                    w.Write((byte)b.Kind);
                    break;
                case ArrayT a:
                    WriteHead(w, 3, t);
                    WriteElem(w, a.Elem);
                    break;
                case StringT:
                    WriteHead(w, 4, t);
                    break;
                case ListT l:
                    WriteHead(w, 5, t);
                    WriteElem(w, l.Elem);
                    break;
                case DictT d:
                    WriteHead(w, 6, t);
                    WriteElem(w, d.Key);
                    WriteElem(w, d.Value);
                    break;
                case FuncT f:
                {
                    WriteHead(w, 7, t);
                    var ps = f.Params.SelectMany(Leaves).ToList();
                    w.Write((ushort)ps.Count);
                    foreach (var l in ps) WriteInfo(w, Info(l.Type));
                    var rs = f.Ret is VoidT ? [] : Leaves(f.Ret);
                    w.Write((ushort)rs.Count);
                    foreach (var l in rs) WriteInfo(w, Info(l.Type));
                    break;
                }
            }
        }

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

    private void WriteHead(BinaryWriter w, byte tkind, VType t)
    {
        w.Write(tkind);
        w.Write(Str(t.ToString()!));
    }

    private void WriteElem(BinaryWriter w, VType elem)
    {
        if (elem.IsAggregate)
        {
            w.Write((byte)4);
            w.Write(TypeIndex(elem));
        }
        else WriteInfo(w, Info(elem));
    }

    private static void WriteInfo(BinaryWriter w, (Kind Kind, ushort Type) info)
    {
        w.Write((byte)info.Kind);
        w.Write(info.Type);
    }

    private void WriteLeaves(BinaryWriter w, byte tkind, VType t, IReadOnlyList<Leaf> leaves)
    {
        WriteHead(w, tkind, t);
        w.Write((ushort)leaves.Count);
        foreach (var l in leaves) WriteInfo(w, Info(l.Type));
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
