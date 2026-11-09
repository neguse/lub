using System.Collections.Generic;
using TcsC;
using TcsSelf;
using TinySystem.Text;

namespace TcsVm;

/// <summary>static の field の表の 1 項 ("型.field"、先頭の slot、型)。</summary>
public sealed class StaticField
{
    public string Name;
    public int Slot;
    public VType Type;

    public StaticField(string name, int slot, VType type)
    {
        Name = name;
        Slot = slot;
        Type = type;
    }
}

/// <summary>static field を持つ型と、その static field (C# 版の (Name, Statics))。</summary>
public sealed class StaticOwner
{
    public string Name;
    public List<ExField> Statics;

    public StaticOwner(string name, List<ExField> statics)
    {
        Name = name;
        Statics = statics;
    }
}

/// <summary>static の (型、field) (reload の計画の入力)。</summary>
public sealed class StaticKey
{
    public string Owner;
    public string Field;

    public StaticKey(string owner, string field)
    {
        Owner = owner;
        Field = field;
    }
}

/// <summary>interface の method の selector (名前と関数型)。</summary>
public sealed class SelectorInfo
{
    public string Name;
    public VType Type;

    public SelectorInfo(string name, VType type)
    {
        Name = name;
        Type = type;
    }
}

/// <summary>--trace: 記録するファイルの path の先頭 (VmSession と CLI が module を作る前に置く)。
/// 空なら記録しない。path は '\\' を '/' にして比べる。</summary>
public static class VmTrace
{
    public static List<string> Prefixes = new List<string>();

    public static string Normalize(string path)
    {
        var p = path.Replace("\\", "/");
        while (p.StartsWith("./")) p = Strings.From(p, 2);
        return p;
    }

    public static bool Matches(string path)
    {
        if (Prefixes.Count == 0 || path == "") return false;
        var normalized = Normalize(path);
        foreach (var raw in Prefixes)
        {
            var prefix = Normalize(raw);
            while (prefix.EndsWith("/")) prefix = Strings.Prefix(prefix, Strings.ByteLength(prefix) - 1);
            if (prefix == "" || normalized == prefix || normalized.StartsWith(prefix + "/")) return true;
        }
        return false;
    }
}

/// <summary>--trace の代入の値の場所 (TVAL の #c)。値は Leaves 個の葉で、型は Type。</summary>
public sealed class TraceValueSite
{
    public string File;
    public int Line;
    public int Col;
    public string Label;
    public VType Type;
    public int Leaves;

    public TraceValueSite(string file, int line, int col, string label, VType type, int leaves)
    {
        File = file;
        Line = line;
        Col = col;
        Label = label;
        Type = type;
        Leaves = leaves;
    }
}

/// <summary>値型の field (名前と型)。</summary>
public sealed class NamedType
{
    public string Name;
    public VType Type;

    public NamedType(string name, VType type)
    {
        Name = name;
        Type = type;
    }
}

/// <summary>.tcsb の 1 module。型 / 定数 / static / 関数の表を作り、byte 列に書く (形式は tcsvm_load.c の parse)。
/// 関数は呼ばれた (到達した) ものだけを作る。出力の順は IL の並び (Dictionary は引くだけに使う)。</summary>
public sealed class ModuleBuilder
{
    public const int Magic = 0x42534354; // "TCSB"
    public const int Version = 4;
    public const int NoType = 0xFFFF;

    // 引くための表 (列挙はしない。並びは下の List)
    public Dictionary<string, ExClass> Classes = new Dictionary<string, ExClass>();
    public Dictionary<string, ExStruct> Structs = new Dictionary<string, ExStruct>();
    private List<string> _classOrder = new List<string>();
    private List<string> _structOrder = new List<string>();
    private Dictionary<string, ExEnum> _enums = new Dictionary<string, ExEnum>();
    private Dictionary<string, string> _displayToName = new Dictionary<string, string>();

    private TypeTableStrings _strings = new TypeTableStrings();
    private List<List<int>> _consts = new List<List<int>>();
    private Dictionary<string, int> _constIndex = new Dictionary<string, int>();
    private List<VType> _types = new List<VType>();
    private Dictionary<string, int> _typeIndex = new Dictionary<string, int>();
    private List<RegInfo> _statics = new List<RegInfo>();
    /// <summary>static の field の表 ("型.field"、先頭の slot、型)。reload は名前で旧版の static と対応させる
    /// (doc/object-model.md §6)。遅延初期化の flag は "型.&lt;init&gt;"。</summary>
    private List<StaticField> _staticFields = new List<StaticField>();
    // "型\nfield" → static の field
    private Dictionary<string, StaticField> _staticSlots = new Dictionary<string, StaticField>();
    public List<FunctionDef> Functions = new List<FunctionDef>();
    private Dictionary<string, int> _functionIndex = new Dictionary<string, int>();
    // --trace: 行の表 ("file:line" → 添字)、代入の値の場所、記録するファイルの class (見つけた順)
    private List<string> _traceLineFiles = new List<string>();
    private List<int> _traceLines = new List<int>();
    private Dictionary<string, int> _traceLineIndex = new Dictionary<string, int>();
    public List<TraceValueSite> TraceValues = new List<TraceValueSite>();
    private List<string> _tracedClasses = new List<string>();
    private Dictionary<string, bool> _tracedPaths = new Dictionary<string, bool>();
    private List<FunctionDef> _pending = new List<FunctionDef>();
    private int _pendingHead;
    public int Entry = -1;

    // host (--ref の stub) の関数と値。名前は IL の host の名前 ("eng.gfx.begin")
    private Dictionary<string, ExForeignMethod> _foreignMethods = new Dictionary<string, ExForeignMethod>();
    private List<string> _foreignOrder = new List<string>();
    private Dictionary<string, ExForeignValue> _foreignValues = new Dictionary<string, ExForeignValue>();

    public ModuleBuilder(ExportResult program)
    {
        foreach (var c in program.Classes)
        {
            if (!Classes.ContainsKey(c.Name)) _classOrder.Add(c.Name);
            Classes[c.Name] = c;
            if (c.DisplayName != null) _displayToName[c.DisplayName] = c.Name;
        }
        foreach (var s in program.Structs)
        {
            if (!Structs.ContainsKey(s.Name)) _structOrder.Add(s.Name);
            Structs[s.Name] = s;
            if (s.DisplayName != null) _displayToName[s.DisplayName] = s.Name;
        }
        foreach (var m in program.ForeignMethods)
        {
            if (!_foreignMethods.ContainsKey(m.Name)) _foreignOrder.Add(m.Name);
            _foreignMethods[m.Name] = m;
        }
        foreach (var v in program.ForeignValues) _foreignValues[v.Name] = v;
        foreach (var e in program.EnumTypes)
        {
            _enums[e.Name] = e;
            if (e.DisplayName != null) _displayToName[e.DisplayName] = e.Name;
        }
    }

    public ExClass? ClassInfo(string name)
    {
        ExClass? c;
        if (Classes.TryGetValue(Canon(name), out c)) return c;
        return null;
    }

    public ExForeignMethod? ForeignMethod(string name)
    {
        ExForeignMethod? m;
        if (_foreignMethods.TryGetValue(name, out m)) return m;
        return null;
    }

    /// <summary>`--ref` の interface の method (receiver は interface の表示名、method は C# の名前)。</summary>
    public ExForeignMethod? ForeignInterfaceMethod(string receiver, string method)
    {
        foreach (var name in _foreignOrder)
        {
            var m = _foreignMethods[name];
            // IL は host の名前 (snake_case) で呼ぶ
            if (m.Receiver == receiver && (m.MethodName == method || m.Name.EndsWith("." + method))) return m;
        }
        return null;
    }

    public ExForeignValue? ForeignValue(string name)
    {
        ExForeignValue? v;
        if (_foreignValues.TryGetValue(name, out v)) return v;
        return null;
    }

    public bool IsStruct(string name) => Structs.ContainsKey(Canon(name));

    public string CanonName(string name) => Canon(name);

    private string Canon(string name)
    {
        string? n;
        if (_displayToName.TryGetValue(name, out n)) return n;
        return name;
    }

    public bool IsClass(string name) => Classes.ContainsKey(Canon(name));

    /// <summary>static field を持てる型の名前 (class か struct)。</summary>
    public bool IsTypeName(string name) => Classes.ContainsKey(Canon(name)) || Structs.ContainsKey(Canon(name));

    public bool IsEnum(string name) => _enums.ContainsKey(Canon(name));

    /// <summary>IlNewObj / IlIsType / IlCast の型の名前。IL はここに user の型の名前を表示名でなく書くので
    /// (`@float` の class は `float`)、同じ綴りの組み込みの型より user の class と struct を先に引く。</summary>
    public VType ParseTypeRef(string name)
    {
        string n = Canon(name);
        ExClass? cls;
        if (Classes.TryGetValue(n, out cls)) return cls.IsInterface ? VType.OfIface(n) : VType.OfClass(n);
        if (Structs.ContainsKey(n)) return VType.OfStruct(n);
        return Parse(name);
    }

    public int EnumValue(string enumName, string member)
    {
        var e = _enums[Canon(enumName)];
        for (int i = 0; i < e.MemberNames.Count; i++)
        {
            if (e.MemberNames[i] == member) return e.MemberValues[i];
        }
        CFail.Report("no member " + member + " in enum " + enumName);
        return 0;
    }

    // ---- 型 ----

    public VType Parse(string type0)
    {
        string type = type0.Trim();
        int n = ContractFacts.Len(type);
        // 参照型の nullable 注釈は型を変えない。値型の T? は Nullable で、まだ持たない
        if (type.EndsWith("?"))
        {
            var inner = Parse(ContractFacts.Sub(type, 0, n - 1));
            return inner.IsRef ? inner : VType.OfNullable(inner);
        }
        if (type.EndsWith("[]")) return VType.OfArray(Parse(ContractFacts.Sub(type, 0, n - 2)));
        // Dictionary の Keys / Values は VM では足した順の List (LINQ の受け手)
        if (type.EndsWith(">.ValueCollection"))
        {
            var vd = Parse(ContractFacts.Sub(type, 0, n - ContractFacts.Len(".ValueCollection")));
            if (CFail.Failed) return VType.Void;
            if (vd.Tag == VT.Dict) return VType.OfList(vd.Value!);
        }
        if (type.EndsWith(">.KeyCollection"))
        {
            var kd = Parse(ContractFacts.Sub(type, 0, n - ContractFacts.Len(".KeyCollection")));
            if (CFail.Failed) return VType.Void;
            if (kd.Tag == VT.Dict) return VType.OfList(kd.Key!);
        }
        int lt = ContractFacts.IndexOfByte(type, '<');
        if (lt > 0 && type.EndsWith(">"))
        {
            string head = ContractFacts.Sub(type, 0, lt);
            var args = new List<VType>();
            foreach (var a in SplitArgs(ContractFacts.Sub(type, lt + 1, n - 1))) args.Add(Parse(a));
            if (CFail.Failed) return VType.Void;
            if ((head == "System.Collections.Generic.List" || head == "List") && args.Count == 1) return VType.OfList(args[0]);
            if ((head == "System.Collections.Generic.Dictionary" || head == "Dictionary") && args.Count == 2) return VType.OfDict(args[0], args[1]);
            if (head == "System.Func" || head == "Func")
            {
                var ps = new List<VType>();
                for (int i = 0; i < args.Count - 1; i++) ps.Add(args[i]);
                return VType.OfFunc(ps, args[args.Count - 1]);
            }
            if (head == "System.Action" || head == "Action") return VType.OfFunc(args, VType.Void);
            // Predicate<T> / Comparison<T> は Func<T, bool> / Func<T, T, int> と同じ関数型 (tcs2c と同じ)
            if ((head == "System.Predicate" || head == "Predicate") && args.Count == 1)
            {
                var ps = new List<VType>();
                ps.Add(args[0]);
                return VType.OfFunc(ps, VType.Bool);
            }
            if ((head == "System.Comparison" || head == "Comparison") && args.Count == 1)
            {
                var ps = new List<VType>();
                ps.Add(args[0]);
                ps.Add(args[0]);
                return VType.OfFunc(ps, VType.Int);
            }
            if ((head == "System.Nullable" || head == "Nullable") && args.Count == 1) return VType.OfNullable(args[0]);
            if ((head == "System.Collections.Generic.KeyValuePair" || head == "KeyValuePair") && args.Count == 2) return VType.OfKvp(args[0], args[1]);
            // Queue<T> は VM では List (Dequeue は先頭を外す)、HashSet<T> は Dictionary<T, bool> (列挙は .NET と同じ順)
            if ((head == "System.Collections.Generic.Queue" || head == "Queue") && args.Count == 1) return VType.OfList(args[0]);
            if ((head == "System.Collections.Generic.HashSet" || head == "HashSet") && args.Count == 1) return VType.OfDict(args[0], VType.Bool);
            CFail.Report("type '" + type + "' is not in the VM yet");
            return VType.Void;
        }
        if (type == "int" || type == "uint" || type == "char" || type == "byte" || type == "sbyte" || type == "short" || type == "ushort") return VType.Int;
        if (type == "object") return VType.Object;
        // TinySystem の facade。修飾の無い名前は、同じ名前の user の型が無いときだけ (user の型が隠す。C backend と同じ)
        if (type == "TinySystem.Text.Utf8Bytes" || (type == "Utf8Bytes" && !IsTypeName("Utf8Bytes"))) return VType.Utf8();
        if (type == "TinySystem.Random" || (type == "Random" && !IsTypeName("Random"))) return VType.Random();
        if (type == "float") return VType.Float;
        if (type == "bool") return VType.Bool;
        if (type == "void") return VType.Void;
        if (type == "string") return VType.String;
        if (type == "System.Action" || type == "Action") return VType.OfFunc(new List<VType>(), VType.Void);
        string name = Canon(type);
        ExClass? cls;
        if (Classes.TryGetValue(name, out cls)) return cls.IsInterface ? VType.OfIface(name) : VType.OfClass(name);
        if (Structs.ContainsKey(name)) return VType.OfStruct(name);
        if (_enums.ContainsKey(name)) return VType.Int; // enum は i32 (il-spec §2)
        // --ref の module の interface (X-4): VM の class が実装し、C の module が呼ぶ。VM の中では object として持つ
        // IL は型 test で `Ed_ITool`、宣言で `Ed.ITool` と書くので、表示名にそろえて 1 つの型にする
        string? host = null;
        foreach (var cn in _classOrder)
        {
            if (host != null) break;
            foreach (var i in Classes[cn].Interfaces)
            {
                if (i == type || i.Replace(".", "_") == type)
                {
                    host = i;
                    break;
                }
            }
        }
        if (host == null)
        {
            foreach (var fn in _foreignOrder)
            {
                var r = _foreignMethods[fn].Receiver;
                if (r != null && (r == type || r.Replace(".", "_") == type))
                {
                    host = r;
                    break;
                }
            }
        }
        if (host != null) return VType.OfHostIface(host);
        CFail.Report("type '" + type + "' is not in the VM yet");
        return VType.Void;
    }

    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var u = new Utf8Bytes(s);
        int depth = 0;
        int start = 0;
        for (int i = 0; i < u.Length; i++)
        {
            int c = u[i];
            if (c == '<' || c == '(' || c == '[') depth++;
            else if (c == '>' || c == ')' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                list.Add(ContractFacts.Sub(s, start, i));
                start = i + 1;
            }
        }
        list.Add(ContractFacts.Sub(s, start, u.Length));
        return list;
    }

    // 型の葉の列 (型ごとに 1 度だけ求める。呼ぶ側は列を書き換えない)
    private Dictionary<string, List<Leaf>> _leaves = new Dictionary<string, List<Leaf>>();
    private Dictionary<string, List<Leaf>> _objectLeaves = new Dictionary<string, List<Leaf>>();

    public List<Leaf> Leaves(VType t)
    {
        // 表示名では引かない (user の class `@float` と float、user の class Random と TinySystem.Random は同じ表示名)
        string key = t.Ident();
        List<Leaf>? cached;
        if (_leaves.TryGetValue(key, out cached)) return cached;
        var list = new List<Leaf>();
        Flatten(t, "", list, 0);
        if (!CFail.Failed) _leaves[key] = list;
        return list;
    }

    private static string SubPath(string path, string name) => path == "" ? name : path + "." + name;

    private void Flatten(VType t, string path, List<Leaf> list, int depth)
    {
        if (CFail.Failed) return;
        if (depth > 32)
        {
            CFail.Report("struct nesting too deep");
            return;
        }
        switch (t.Tag)
        {
            case VT.Struct:
                foreach (var f in Structs[t.Name].Fields)
                {
                    if (!f.IsStatic) Flatten(Parse(f.Type), SubPath(path, f.Name), list, depth + 1);
                }
                return;
            case VT.Nullable:
                list.Add(new Leaf(SubPath(path, "HasValue"), VType.Bool));
                Flatten(t.Inner!, SubPath(path, "Value"), list, depth + 1);
                return;
            case VT.Kvp:
                Flatten(t.Key!, SubPath(path, "Key"), list, depth + 1);
                Flatten(t.Value!, SubPath(path, "Value"), list, depth + 1);
                return;
            case VT.Tuple:
                for (int k = 0; k < t.Items.Count; k++)
                {
                    if (t.Items[k].Tag != VT.Void) Flatten(t.Items[k], SubPath(path, "Item" + (k + 1)), list, depth + 1);
                }
                return;
            case VT.Utf8:
                list.Add(new Leaf(SubPath(path, "S"), VType.String));
                list.Add(new Leaf(SubPath(path, "O"), VType.Int));
                list.Add(new Leaf(SubPath(path, "Length"), VType.Int));
                return;
        }
        if (t.Tag == VT.Prim || t.IsRef)
        {
            list.Add(new Leaf(path, t));
            return;
        }
        CFail.Report("no leaves for " + t.Display());
    }

    /// <summary>class の instance の葉 (instance field を struct まで展開)。cell は中の値の葉。</summary>
    public List<Leaf> ObjectLeaves(VType t)
    {
        if (t.Tag == VT.Cell) return Leaves(t.Inner!);
        string key = t.Ident();
        List<Leaf>? cached;
        if (_objectLeaves.TryGetValue(key, out cached)) return cached;
        var list = new List<Leaf>();
        foreach (var f in Classes[t.Name].Fields)
        {
            if (!f.IsStatic) Flatten(Parse(f.Type), f.Name, list, 0);
        }
        if (!CFail.Failed) _objectLeaves[key] = list;
        return list;
    }

    public VType FieldType(VType owner, string field)
    {
        if (owner.Tag == VT.Nullable && field == "Value") return owner.Inner!;
        if (owner.Tag == VT.Nullable && field == "HasValue") return VType.Bool;
        if (owner.Tag == VT.Kvp && field == "Key") return owner.Key!;
        if (owner.Tag == VT.Kvp && field == "Value") return owner.Value!;
        if (owner.Tag == VT.Utf8 && field == "Length") return VType.Int;
        List<ExField> fields;
        if (owner.Tag == VT.Class) fields = Classes[owner.Name].Fields;
        else if (owner.Tag == VT.Struct) fields = Structs[owner.Name].Fields;
        else
        {
            CFail.Report("field '" + field + "' of " + owner.Display() + " is not in the VM yet");
            return VType.Void;
        }
        foreach (var f in fields)
        {
            if (f.Name == field && !f.IsStatic) return Parse(f.Type);
        }
        CFail.Report("no field '" + field + "' in " + owner.Display());
        return VType.Void;
    }

    public int TypeIndex(VType t)
    {
        if (t.Tag == VT.Prim || t.Tag == VT.Null) return NoType;
        int i;
        if (_typeIndex.TryGetValue(t.Ident(), out i)) return i;
        if (!t.IsRef && !t.IsAggregate)
        {
            CFail.Report("no type index for " + t.Display());
            return NoType;
        }
        i = _types.Count;
        _types.Add(t);
        _typeIndex[t.Ident()] = i;
        if (_types.Count > NoType)
        {
            CFail.Report("too many types");
            return NoType;
        }
        // 中の型も表に入れる (再帰する class は上で番号を先に取っているので止まる)
        switch (t.Tag)
        {
            case VT.Class:
                foreach (var l in ObjectLeaves(t)) TypeIndex(l.Type);
                foreach (var iface in Interfaces(t.Name)) TypeIndex(VType.OfIface(iface));
                foreach (var iface in HostInterfaces(t.Name)) TypeIndex(iface);
                break;
            case VT.Cell:
                foreach (var l in ObjectLeaves(t)) TypeIndex(l.Type);
                break;
            case VT.Array:
                TypeIndex(t.Elem!);
                break;
            case VT.List:
                TypeIndex(t.Elem!);
                break;
            case VT.Dict:
                if (t.Key!.IsAggregate)
                {
                    CFail.Report(t.Display() + ": struct keys are not in the VM yet");
                    return i;
                }
                TypeIndex(t.Key);
                TypeIndex(t.Value!);
                break;
            case VT.Func:
                foreach (var p in t.Params)
                {
                    foreach (var l in Leaves(p)) TypeIndex(l.Type);
                }
                if (t.Ret!.Tag != VT.Void)
                {
                    foreach (var l in Leaves(t.Ret)) TypeIndex(l.Type);
                }
                break;
            default:
                if (t.IsAggregate)
                {
                    foreach (var l in Leaves(t)) TypeIndex(l.Type);
                }
                break;
        }
        return i;
    }

    public RegInfo Info(VType leafType) => new RegInfo(leafType.Tag == VT.Prim ? leafType.PKind : Kind.Ref, TypeIndex(leafType));

    // ---- interface ----

    /// <summary>class が実装する interface (program にあるものだけ)。</summary>
    public List<string> Interfaces(string cls)
    {
        var r = new List<string>();
        foreach (var n0 in Classes[cls].Interfaces)
        {
            string n = Canon(n0);
            ExClass? i;
            if (Classes.TryGetValue(n, out i) && i.IsInterface) r.Add(n);
        }
        return r;
    }

    /// <summary>class が実装する --ref の module の interface (X-4)。C の module の interface の判定が名前で引く (object-model §10.6)。</summary>
    public List<VType> HostInterfaces(string cls)
    {
        var r = new List<VType>();
        foreach (var n in Classes[cls].Interfaces)
        {
            if (Classes.ContainsKey(Canon(n)) || n.Contains("<") || n.StartsWith("System.")) continue;
            var t = Parse(n);
            if (t.Tag == VT.HostIface) r.Add(t);
        }
        return r;
    }

    /// <summary>method の呼び出しの関数型。out の引数は引数から外し、戻り値と out の値を多値 (Tuple) で返す
    /// (実装の関数は戻り値の葉の後に out の葉を返す)。</summary>
    public VType SelectorType(ExMethod method)
    {
        var types = new List<VType>();
        foreach (var p in method.ParameterTypes) types.Add(Parse(p));
        var ret = Parse(method.ReturnType);
        if (method.OutParameters.Count == 0) return VType.OfFunc(types, ret);
        var ins = new List<VType>();
        var outTypes = new List<VType>();
        if (ret.Tag != VT.Void) outTypes.Add(ret);
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            if (method.OutParameters.Contains(method.Parameters[i])) outTypes.Add(types[i]);
            else ins.Add(types[i]);
        }
        return VType.OfFunc(ins, VType.OfTuple(outTypes));
    }

    /// <summary>class が interface (継承した interface を含む) を実装するか。</summary>
    private bool Implements(string cls, string iface)
    {
        var seen = new HashSet<string>();
        // C# の Stack: 積んだ逆の順に取り出す
        var work = new List<string>();
        foreach (var i in Interfaces(cls)) work.Add(i);
        while (work.Count > 0)
        {
            var i = work[work.Count - 1];
            work.RemoveAt(work.Count - 1);
            if (i == iface) return true;
            if (seen.Contains(i)) continue;
            seen.Add(i);
            foreach (var m0 in Classes[i].Interfaces)
            {
                string m = Canon(m0);
                if (Classes.ContainsKey(m)) work.Add(m);
            }
        }
        return false;
    }

    private List<SelectorInfo> _selectors = new List<SelectorInfo>();
    private Dictionary<string, int> _selectorIndex = new Dictionary<string, int>();
    private Dictionary<string, List<TypeTableItable>> _itables = new Dictionary<string, List<TypeTableItable>>();

    /// <summary>interface の method の selector。実装する class の itable に関数を入れる (到達した class の method だけを作る)。
    /// requestMethod は (class、実装の method、selector の関数型) から関数の番号を返す。</summary>
    public int Selector(string iface0, ExMethod method, System.Func<string, ExMethod, VType, int> requestMethod)
    {
        // selector は名前と関数型で 1 つ (特殊化した generic な interface は同じ名前で型が違う: IGen<int> と IGen<string>)
        var ft = SelectorType(method);
        string key = method.Name + ":" + ft.Display();
        int i;
        if (_selectorIndex.TryGetValue(key, out i)) return i;
        i = _selectors.Count;
        _selectors.Add(new SelectorInfo(method.Name, ft));
        _selectorIndex[key] = i;
        TypeIndex(ft);
        string iface = Canon(iface0);
        foreach (var name in _classOrder)
        {
            var cls = Classes[name];
            if (cls.IsInterface || cls.IsExternal || Interfaces(name).Count == 0) continue;
            ExMethod? impl = null;
            foreach (var m in cls.Methods)
            {
                if (m.Name == method.Name && !m.IsStatic)
                {
                    impl = m;
                    break;
                }
            }
            if (impl == null) continue;
            // 型の違う実装は、この interface を実装する class だけ (adapter で型を合わせる)
            bool same = VType.Same(SelectorType(impl), ft);
            if (!same && !Implements(name, iface)) continue;
            List<TypeTableItable>? table;
            if (!_itables.TryGetValue(name, out table))
            {
                table = new List<TypeTableItable>();
                _itables[name] = table;
            }
            table.Add(new TypeTableItable(i, requestMethod(name, impl, ft)));
        }
        return i;
    }

    // ---- runtime の関数 (CALLR) ----

    private List<string> _intrinsics = new List<string>();
    private Dictionary<string, int> _intrinsicIndex = new Dictionary<string, int>();

    public int Intrinsic(string name)
    {
        int i;
        if (_intrinsicIndex.TryGetValue(name, out i)) return i;
        i = _intrinsics.Count;
        _intrinsics.Add(name);
        _intrinsicIndex[name] = i;
        return i;
    }

    // ---- 定数 ----

    public int Const(List<int> utf8)
    {
        string key = VBytes.Base64(utf8);
        int i;
        if (!_constIndex.TryGetValue(key, out i))
        {
            i = _consts.Count;
            _consts.Add(utf8);
            _constIndex[key] = i;
        }
        TypeIndex(VType.String);
        return i;
    }

    // ---- static ----

    /// <summary>static の field の先頭の slot (type に型)。</summary>
    public int Static(string cls0, string field, out VType type)
    {
        string cls = Canon(cls0);
        string key = cls + "\n" + field;
        StaticField? s;
        if (_staticSlots.TryGetValue(key, out s))
        {
            type = s.Type;
            return s.Slot;
        }
        List<ExField> fields = StaticFieldsOf(cls);
        ExField? info = null;
        foreach (var f in fields)
        {
            if (f.Name == field && f.IsStatic)
            {
                info = f;
                break;
            }
        }
        if (info == null)
        {
            CFail.Report("no static '" + field + "' in " + cls);
            type = VType.Int;
            return 0;
        }
        type = Parse(info.Type);
        int slot = _statics.Count;
        foreach (var l in Leaves(type)) _statics.Add(Info(l.Type));
        var sf = new StaticField(cls + "." + field, slot, type);
        _staticFields.Add(sf);
        _staticSlots[key] = sf;
        return slot;
    }

    private List<ExField> StaticFieldsOf(string owner)
    {
        ExClass? c;
        if (Classes.TryGetValue(owner, out c)) return c.Fields;
        ExStruct? s;
        if (Structs.TryGetValue(owner, out s)) return s.StaticFields;
        return new List<ExField>();
    }

    // ---- static の初期化 (il-spec と tcs2c と同じ): initializer が定数だけの型は最初に、
    //      ほかの型は最初の static の読み書きで 1 回だけ (C# §15.5.6.2 と同じ。前方参照と循環を扱える) ----

    private static bool IsConstantExpr(IlExpr e)
    {
        if (e is IlLit) return true;
        if (e is IlParen p) return IsConstantExpr(p.E);
        if (e is IlUn u) return IsConstantExpr(u.E);
        if (e is IlBin b) return IsConstantExpr(b.L) && IsConstantExpr(b.R);
        return false;
    }

    /// <summary>static field を持つ型 (class と struct) を宣言の順に。</summary>
    public List<StaticOwner> StaticOwners()
    {
        var r = new List<StaticOwner>();
        foreach (var name in _classOrder)
        {
            var c = Classes[name];
            if (c.IsInterface || c.IsExternal) continue;
            var statics = new List<ExField>();
            foreach (var f in c.Fields)
            {
                if (f.IsStatic) statics.Add(f);
            }
            r.Add(new StaticOwner(name, statics));
        }
        foreach (var name in _structOrder)
        {
            var s = Structs[name];
            if (s.StaticFields.Count == 0) continue;
            var statics = new List<ExField>();
            foreach (var f in s.StaticFields)
            {
                if (f.IsStatic) statics.Add(f);
            }
            r.Add(new StaticOwner(name, statics));
        }
        return r;
    }

    public bool IsLazyStatic(string owner)
    {
        foreach (var f in StaticFieldsOf(Canon(owner)))
        {
            if (f.IsStatic && f.Init != null && !IsConstantExpr(f.Init)) return true;
        }
        return false;
    }

    private Dictionary<string, int> _initFlags = new Dictionary<string, int>();

    /// <summary>遅延初期化の型の「初期化を始めた」flag (bool の static)。</summary>
    public int InitFlag(string owner0)
    {
        string owner = Canon(owner0);
        int slot;
        if (_initFlags.TryGetValue(owner, out slot)) return slot;
        slot = _statics.Count;
        _statics.Add(new RegInfo(Kind.Bool, NoType));
        _staticFields.Add(new StaticField(owner + ".<init>", slot, VType.Bool));
        _initFlags[owner] = slot;
        return slot;
    }

    private int _sharedRandom = -1;

    /// <summary>Random.Shared を置く隠れた static (最初の参照で作って seed する)。</summary>
    public int SharedRandomSlot()
    {
        if (_sharedRandom >= 0) return _sharedRandom;
        _sharedRandom = _statics.Count;
        _statics.Add(Info(VType.Random()));
        _staticFields.Add(new StaticField("TinySystem.Random.Shared", _sharedRandom, VType.Random()));
        return _sharedRandom;
    }

    // ---- 関数 ----

    /// <summary>関数の番号を返す。初めてなら作り、compile を待ち行列に入れる。</summary>
    public int Request(string name, System.Action<FunctionDef> compile)
    {
        int i;
        if (_functionIndex.TryGetValue(name, out i)) return i;
        i = Functions.Count;
        if (i > 0xFFFF)
        {
            CFail.Report("too many functions");
            return 0;
        }
        var def = new FunctionDef(name);
        def.Compile = compile;
        Functions.Add(def);
        _functionIndex[name] = i;
        _pending.Add(def);
        return i;
    }

    /// <summary>関数の番号 (無ければ -1)。</summary>
    public int FindFunction(string name)
    {
        int i;
        if (_functionIndex.TryGetValue(name, out i)) return i;
        return -1;
    }

    /// <summary>待ち行列が空になるまで compile する (compile が新しい関数を呼べば、それも入る)。</summary>
    public void CompilePending()
    {
        while (_pendingHead < _pending.Count && !CFail.Failed)
        {
            var def = _pending[_pendingHead];
            _pendingHead++;
            def.Compile!(def);
            // 誤りに変換していた文の場所を付ける。他の診断と同じ形にして、host が行を示せるようにする
            if (CFail.Failed && def.At != null)
                CFail.Error = def.At.File + "(" + def.At.Line + "," + def.At.Col + "): error TCS1001: " + CFail.Error;
        }
    }

    public int Str(string s) => _strings.Str(s);

    // ---- --trace ----

    public bool IsTraced(string path)
    {
        bool traced;
        if (_tracedPaths.TryGetValue(path, out traced)) return traced;
        traced = VmTrace.Matches(path);
        _tracedPaths[path] = traced;
        return traced;
    }

    /// <summary>行の表の添字 (TRACE の #imm)。</summary>
    public int TraceLine(string path, int line)
    {
        string file = VmTrace.Normalize(path);
        string key = file + ":" + line;
        int index;
        if (_traceLineIndex.TryGetValue(key, out index)) return index;
        index = _traceLines.Count;
        _traceLineFiles.Add(file);
        _traceLines.Add(line);
        _traceLineIndex[key] = index;
        return index;
    }

    /// <summary>代入の値の場所の添字 (TVAL の #c)。</summary>
    public int TraceValue(string path, int line, int col, string label, VType type, int leaves)
    {
        TraceValues.Add(new TraceValueSite(VmTrace.Normalize(path), line, col, label, type, leaves));
        return TraceValues.Count - 1;
    }

    public void TraceClass(string name)
    {
        if (!_tracedClasses.Contains(name)) _tracedClasses.Add(name);
    }

    // ---- 書き出し ----

    /// <summary>reload の計画の入力: 型記述子の表と static の (型、field)。</summary>
    public List<TypeTableEntry> Describe(out List<StaticKey> statics)
    {
        // Write と同じく static の field の struct の型を表に足す (heap の object が持たない struct も、reload の計画で field の名前で移す)
        foreach (var s in _staticFields) FieldInfo(s.Type);
        var entries = new List<TypeTableEntry>();
        for (int i = 0; i < _types.Count; i++) entries.Add(TypeEntry(_types[i]));
        statics = new List<StaticKey>();
        foreach (var s in _staticFields)
        {
            int dot = LastDot(s.Name);
            statics.Add(new StaticKey(ContractFacts.Sub(s.Name, 0, dot), ContractFacts.Sub(s.Name, dot + 1, ContractFacts.Len(s.Name))));
        }
        return entries;
    }

    private static int LastDot(string s)
    {
        var u = new Utf8Bytes(s);
        for (int i = u.Length - 1; i >= 0; i--)
        {
            if (u[i] == '.') return i;
        }
        return -1;
    }

    public List<int> Write()
    {
        foreach (var f in Functions)
        {
            Str(f.Name);
            Str(f.File);
        }
        foreach (var s in _selectors) Str(s.Name);
        foreach (var name in _intrinsics) Str(name);
        // --trace の表の名前と、値の場所の型 (型の表より先に決める)
        foreach (var f in Functions) Str(f.Path);
        foreach (var file in _traceLineFiles) Str(file);
        var traceInfos = new List<TypeTableInfo>();
        foreach (var site in TraceValues)
        {
            Str(site.File);
            Str(site.Label);
            traceInfos.Add(FieldInfo(site.Type));
        }
        foreach (var name in _tracedClasses) Str(name);
        // static の field の表の型は型の表より先に決める (struct の型を表に足す)
        var staticNames = new List<int>();
        var staticInfos = new List<TypeTableInfo>();
        foreach (var s in _staticFields)
        {
            staticNames.Add(Str(s.Name));
            staticInfos.Add(FieldInfo(s.Type));
        }
        // 型の表 (doc/object-model.md §4)。名前は string 表に先に入れる
        var entries = new List<TypeTableEntry>();
        for (int i = 0; i < _types.Count; i++) entries.Add(TypeEntry(_types[i]));
        foreach (var name in TypeTable.Names(entries)) Str(name);

        var w = new List<int>();
        VBytes.I32(w, Magic);
        VBytes.I32(w, Version);
        VBytes.I32(w, OpAbi.Hash);

        VBytes.I32(w, _strings.Strings.Count);
        // 書く間に string 表は増えない (名前は上で全部入れた)
        foreach (var s in _strings.Strings)
        {
            var b = VBytes.Utf8(s);
            VBytes.I32(w, b.Count);
            VBytes.Bytes(w, b);
        }

        VBytes.I32(w, _consts.Count);
        foreach (var c in _consts)
        {
            VBytes.I32(w, c.Count);
            VBytes.Bytes(w, c);
        }

        TypeTable.Write(w, entries, _strings);

        VBytes.I32(w, _selectors.Count);
        foreach (var s in _selectors)
        {
            VBytes.I32(w, Str(s.Name));
            VBytes.U16(w, TypeIndex(s.Type));
        }

        VBytes.I32(w, _intrinsics.Count);
        foreach (var name in _intrinsics) VBytes.I32(w, Str(name));

        VBytes.I32(w, _statics.Count);
        foreach (var info in _statics) WriteInfo(w, info);
        VBytes.I32(w, _staticFields.Count);
        for (int i = 0; i < _staticFields.Count; i++)
        {
            VBytes.I32(w, staticNames[i]);
            VBytes.I32(w, _staticFields[i].Slot);
            VBytes.U8(w, staticInfos[i].Kind);
            VBytes.U16(w, staticInfos[i].Type);
        }

        VBytes.I32(w, Functions.Count);
        foreach (var f in Functions)
        {
            if (!f.HasCode)
            {
                CFail.Report("function " + f.Name + " has no body");
                return w;
            }
            VBytes.I32(w, Str(f.Name));
            VBytes.I32(w, Str(f.File));
            VBytes.U16(w, f.NParams);
            VBytes.U16(w, f.Rets.Count);
            VBytes.U16(w, f.Regs.Count);
            foreach (var info in f.Regs) WriteInfo(w, info);
            foreach (var info in f.Rets) WriteInfo(w, info);
            VBytes.U16(w, f.Caps.Count);
            foreach (var info in f.Caps) WriteInfo(w, info);
            VBytes.I32(w, f.CodeLo.Count);
            for (int i = 0; i < f.CodeLo.Count; i++)
            {
                VBytes.I32(w, f.CodeLo[i]);
                VBytes.I32(w, f.CodeHi[i]);
            }
            VBytes.I32(w, f.Lines.Count);
            foreach (var l in f.Lines)
            {
                VBytes.I32(w, l.Pc);
                VBytes.I32(w, l.Line);
            }
        }
        VBytes.I32(w, Entry);
        // 関数ごとのファイルの path と --trace の表 (tcsvm_load.c の read_debug)
        VBytes.I32(w, Functions.Count);
        foreach (var f in Functions) VBytes.I32(w, Str(f.Path));
        VBytes.I32(w, _traceLines.Count);
        for (int i = 0; i < _traceLines.Count; i++)
        {
            VBytes.I32(w, Str(_traceLineFiles[i]));
            VBytes.I32(w, _traceLines[i]);
        }
        VBytes.I32(w, TraceValues.Count);
        for (int i = 0; i < TraceValues.Count; i++)
        {
            var site = TraceValues[i];
            VBytes.I32(w, Str(site.File));
            VBytes.I32(w, site.Line);
            VBytes.I32(w, site.Col);
            VBytes.I32(w, Str(site.Label));
            VBytes.U8(w, traceInfos[i].Kind);
            VBytes.U16(w, traceInfos[i].Type);
            VBytes.U16(w, site.Leaves);
        }
        VBytes.I32(w, _tracedClasses.Count);
        foreach (var name in _tracedClasses) VBytes.I32(w, Str(name));
        return w;
    }

    // ---- 型記述子 (doc/object-model.md §4) ----

    private TypeTableInfo FieldInfo(VType t)
    {
        if (t.Tag == VT.Prim) return new TypeTableInfo((int)t.PKind, NoType);
        if (t.IsAggregate) return new TypeTableInfo(TypeTable.KindStruct, TypeIndex(t));
        return new TypeTableInfo(TypeTable.KindRef, TypeIndex(t));
    }

    /// <summary>値型の field (名前と型)。葉 (Leaves) はこの列を深さ優先で展開した順。</summary>
    private List<NamedType> AggregateFields(VType t)
    {
        var r = new List<NamedType>();
        if (t.Tag == VT.Struct)
        {
            foreach (var f in Structs[t.Name].Fields)
            {
                if (!f.IsStatic) r.Add(new NamedType(f.Name, Parse(f.Type)));
            }
        }
        else if (t.Tag == VT.Nullable)
        {
            r.Add(new NamedType("HasValue", VType.Bool));
            r.Add(new NamedType("Value", t.Inner!));
        }
        else if (t.Tag == VT.Kvp)
        {
            r.Add(new NamedType("Key", t.Key!));
            r.Add(new NamedType("Value", t.Value!));
        }
        else if (t.Tag == VT.Utf8)
        {
            r.Add(new NamedType("S", VType.String));
            r.Add(new NamedType("O", VType.Int));
            r.Add(new NamedType("Length", VType.Int));
        }
        else CFail.Report("fields of " + t.Display());
        return r;
    }

    private List<TypeTableField> Fields(List<NamedType> fields)
    {
        var r = new List<TypeTableField>();
        foreach (var f in fields) r.Add(new TypeTableField(f.Name, FieldInfo(f.Type)));
        return r;
    }

    private TypeTableEntry TypeEntry(VType t)
    {
        string name = t.Display();
        switch (t.Tag)
        {
            case VT.Class:
            {
                var info = Classes[t.Name];
                var e = new TypeTableEntry(TypeTable.Class, name);
                e.LayoutHash = TypeTable.ParseLayoutHash(info.LayoutHash);
                bool hasOnReload = false;
                foreach (var m in info.Methods)
                {
                    if (m.Name == "OnReload" && !m.IsStatic) hasOnReload = true;
                }
                e.Flags = (info.IsRecord ? TypeTable.FlagRecord : 0) | (hasOnReload ? TypeTable.FlagHasOnReload : 0)
                    | (info.IsExternal ? TypeTable.FlagHostValue : 0); // host と共有する data class は host_value を持つ (doc/object-model.md §3)
                var fields = new List<NamedType>();
                foreach (var f in info.Fields)
                {
                    if (!f.IsStatic) fields.Add(new NamedType(f.Name, Parse(f.Type)));
                }
                e.Fields = Fields(fields);
                foreach (var i in Interfaces(t.Name)) e.Interfaces.Add(TypeIndex(VType.OfIface(i)));
                foreach (var i in HostInterfaces(t.Name)) e.Interfaces.Add(TypeIndex(i));
                List<TypeTableItable>? table;
                if (_itables.TryGetValue(t.Name, out table))
                {
                    // selector の番号の順 (同じ番号は足した順。C# の OrderBy は安定)
                    var sorted = new List<TypeTableItable>();
                    foreach (var x in table) sorted.Add(x);
                    for (int a = 1; a < sorted.Count; a++)
                    {
                        var cur = sorted[a];
                        int b = a - 1;
                        while (b >= 0 && sorted[b].Selector > cur.Selector)
                        {
                            sorted[b + 1] = sorted[b];
                            b--;
                        }
                        sorted[b + 1] = cur;
                    }
                    e.Itable = sorted;
                }
                return e;
            }
            case VT.Cell:
            {
                var e = new TypeTableEntry(TypeTable.Class, name);
                var fields = new List<NamedType>();
                fields.Add(new NamedType("value", t.Inner!));
                e.Fields = Fields(fields);
                return e;
            }
            case VT.Random:
            {
                // 8 個の int = 64 bit の状態 × 4 (runtime の random.* が読み書きする)。先頭の pad で状態を
                // payload の 8 byte 目から置く (type_id の 4 byte の後。64 bit の読み書きを揃える)
                var e = new TypeTableEntry(TypeTable.Class, name);
                var fields = new List<NamedType>();
                for (int i = 0; i < 9; i++) fields.Add(new NamedType(i == 0 ? "pad" : "s" + (i - 1), VType.Int));
                e.Fields = Fields(fields);
                return e;
            }
            case VT.Array:
            {
                var e = new TypeTableEntry(TypeTable.Array, name);
                e.Element = FieldInfo(t.Elem!);
                return e;
            }
            case VT.List:
            {
                var e = new TypeTableEntry(TypeTable.List, name);
                e.Element = FieldInfo(t.Elem!);
                return e;
            }
            case VT.Dict:
            {
                var e = new TypeTableEntry(TypeTable.Dict, name);
                e.Key = FieldInfo(t.Key!);
                e.Element = FieldInfo(t.Value!);
                return e;
            }
            case VT.Func:
            {
                var e = new TypeTableEntry(TypeTable.Func, name);
                foreach (var p in t.Params)
                {
                    foreach (var l in Leaves(p)) e.Params.Add(FieldInfo(l.Type));
                }
                if (t.Ret!.Tag != VT.Void)
                {
                    foreach (var l in Leaves(t.Ret)) e.Rets.Add(FieldInfo(l.Type));
                }
                return e;
            }
            case VT.String:
                return new TypeTableEntry(TypeTable.String, name);
            case VT.Iface:
                return new TypeTableEntry(TypeTable.Iface, name);
            case VT.Object:
            case VT.HostIface:
                return new TypeTableEntry(TypeTable.Object, name);
            case VT.Box:
            {
                var e = new TypeTableEntry(TypeTable.Box, name);
                e.Element = new TypeTableInfo((int)t.PKind, NoType);
                return e;
            }
        }
        if (t.IsAggregate)
        {
            var e = new TypeTableEntry(TypeTable.Struct, name);
            e.LayoutHash = t.Tag == VT.Struct ? TypeTable.ParseLayoutHash(Structs[t.Name].LayoutHash) : 0;
            e.Fields = Fields(AggregateFields(t));
            return e;
        }
        CFail.Report("no type entry for " + t.Display());
        return new TypeTableEntry(TypeTable.Object, name);
    }

    private static void WriteInfo(List<int> w, RegInfo info)
    {
        VBytes.U8(w, (int)info.Kind);
        VBytes.U16(w, info.Type);
    }
}
