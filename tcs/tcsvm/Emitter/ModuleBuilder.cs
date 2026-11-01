using System.Text;

namespace TinyCs.Vm;

/// <summary>.tcsb の 1 module。型 / static / 関数の表を作り、byte 列に書く (形式は tcsvm.c の parse)。</summary>
internal sealed class ModuleBuilder
{
    public const uint Magic = 0x42534354; // "TCSB"
    public const uint Version = 1;
    public const ushort NoType = 0xFFFF;

    private readonly IlExportResult _program;
    private readonly Dictionary<string, IlClassInfo> _classes = [];
    private readonly Dictionary<string, IlStructInfo> _structs = [];
    private readonly Dictionary<string, string> _displayToName = [];

    private readonly List<string> _strings = [];
    private readonly Dictionary<string, int> _stringIndex = [];
    private readonly List<VType> _types = [];
    private readonly Dictionary<VType, int> _typeIndex = [];
    private readonly List<(Kind Kind, ushort Type)> _statics = [];
    private readonly Dictionary<(string Class, string Field), (int Slot, VType Type)> _staticSlots = [];
    public readonly List<FunctionDef> Functions = [];
    private readonly Dictionary<string, int> _functionIndex = [];
    public int Entry = -1;

    public ModuleBuilder(IlExportResult program)
    {
        _program = program;
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
        if (!program.EnumTypes.IsDefault && program.EnumTypes.Length > 0)
            throw new VmException("enum is not in V1");
    }

    public IReadOnlyDictionary<string, IlClassInfo> Classes => _classes;

    public bool IsClass(string name) => _classes.ContainsKey(Canon(name));

    private string Canon(string name) => _displayToName.TryGetValue(name, out var n) ? n : name;

    // ---- 型 ----

    public VType Parse(string type)
    {
        type = type.Trim();
        if (type.EndsWith("[]")) return new ArrayT(Parse(type[..^2]));
        switch (type)
        {
            case "int": return VType.Int;
            case "float": return VType.Float;
            case "bool": return VType.Bool;
            case "void": return VType.Void;
        }
        var name = Canon(type);
        if (_classes.ContainsKey(name)) return new ClassT(name);
        if (_structs.ContainsKey(name)) return new StructT(name);
        throw new VmException($"type '{type}' is not in V1");
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
        switch (t)
        {
            case StructT s:
                foreach (var f in _structs[s.Name].Fields.Where(f => !f.IsStatic))
                    Flatten(Parse(f.Type), path == "" ? f.Name : path + "." + f.Name, list, depth + 1);
                break;
            case PrimT or ClassT or ArrayT:
                list.Add(new Leaf(path, t));
                break;
            default:
                throw new VmException($"no leaves for {t}");
        }
    }

    /// <summary>class の instance の葉 (instance field を struct まで展開)。</summary>
    public IReadOnlyList<Leaf> ClassLeaves(string cls)
    {
        var list = new List<Leaf>();
        foreach (var f in _classes[cls].Fields.Where(f => !f.IsStatic))
            Flatten(Parse(f.Type), f.Name, list, 0);
        return list;
    }

    public VType FieldType(VType owner, string field)
    {
        IEnumerable<IlFieldInfo> fields = owner switch
        {
            ClassT c => _classes[c.Name].Fields,
            StructT s => _structs[s.Name].Fields,
            _ => throw new VmException($"field '{field}' of {owner}"),
        };
        var info = fields.FirstOrDefault(f => f.Name == field && !f.IsStatic)
            ?? throw new VmException($"no field '{field}' in {owner}");
        return Parse(info.Type);
    }

    public ushort TypeIndex(VType t)
    {
        if (t is PrimT) return NoType;
        if (_typeIndex.TryGetValue(t, out var i)) return (ushort)i;
        if (t is not (ClassT or StructT or ArrayT)) throw new VmException($"no type index for {t}");
        i = _types.Count;
        _types.Add(t);
        _typeIndex[t] = i;
        // 葉の ref の型も表に入れる (再帰する class は上で番号を先に取っているので止まる)
        IEnumerable<Leaf> leaves = t switch
        {
            ClassT c => ClassLeaves(c.Name),
            StructT => Leaves(t),
            _ => [],
        };
        foreach (var l in leaves) TypeIndex(l.Type);
        if (t is ArrayT a) TypeIndex(a.Elem);
        if (_types.Count > NoType) throw new VmException("too many types");
        return (ushort)i;
    }

    // ---- static ----

    public (int Slot, VType Type) Static(string cls, string field)
    {
        cls = Canon(cls);
        if (_staticSlots.TryGetValue((cls, field), out var s)) return s;
        var info = _classes[cls].Fields.FirstOrDefault(f => f.Name == field && f.IsStatic)
            ?? throw new VmException($"no static '{field}' in {cls}");
        var type = Parse(info.Type);
        var slot = _statics.Count;
        foreach (var l in Leaves(type)) _statics.Add((l.Kind, TypeIndex(l.Type)));
        return _staticSlots[(cls, field)] = (slot, type);
    }

    // ---- 関数 ----

    public int DeclareFunction(string name)
    {
        if (_functionIndex.TryGetValue(name, out var i)) return i;
        i = Functions.Count;
        Functions.Add(new FunctionDef(name));
        _functionIndex[name] = i;
        if (i > 0xFFFF) throw new VmException("too many functions");
        return i;
    }

    public int? FindFunction(string name) => _functionIndex.TryGetValue(name, out var i) ? i : null;

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
        foreach (var t in _types) Str(t.ToString()!);

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

        w.Write(_types.Count);
        foreach (var t in _types)
        {
            switch (t)
            {
                case ClassT c:
                    WriteLeaves(w, 1, t, ClassLeaves(c.Name));
                    break;
                case StructT:
                    WriteLeaves(w, 2, t, Leaves(t));
                    break;
                case ArrayT a:
                    w.Write((byte)3);
                    w.Write(Str(t.ToString()!));
                    w.Write(a.Elem is StructT ? (byte)4 : (byte)Leaves(a.Elem)[0].Kind);
                    w.Write(a.Elem is PrimT ? NoType : TypeIndex(a.Elem));
                    break;
            }
        }

        w.Write(_statics.Count);
        foreach (var (kind, type) in _statics)
        {
            w.Write((byte)kind);
            w.Write(type);
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
            foreach (var (k, t) in f.Regs)
            {
                w.Write((byte)k);
                w.Write(t);
            }
            foreach (var (k, t) in f.Rets)
            {
                w.Write((byte)k);
                w.Write(t);
            }
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

    private void WriteLeaves(BinaryWriter w, byte tkind, VType t, IReadOnlyList<Leaf> leaves)
    {
        w.Write(tkind);
        w.Write(Str(t.ToString()!));
        w.Write((ushort)leaves.Count);
        foreach (var l in leaves)
        {
            w.Write((byte)l.Kind);
            w.Write(TypeIndex(l.Type));
        }
    }
}

internal sealed class FunctionDef(string name)
{
    public string Name { get; } = name;
    public string File = "";
    public int NParams;
    public List<(Kind Kind, ushort Type)> Regs = [];
    public List<(Kind Kind, ushort Type)> Rets = [];
    public List<ulong>? Code;
    public List<(int Pc, int Line)> Lines = [];
}
