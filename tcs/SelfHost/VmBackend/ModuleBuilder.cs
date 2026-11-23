using System.Collections.Generic;
using TcsC;
using TcsSelf;
using TinySystem.Text;

namespace TcsVm;

/// <summary>One entry of the static field table ("type.field", first slot, type).</summary>
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

/// <summary>A type with static fields, and its static fields ((Name, Statics) in the C# version).</summary>
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

/// <summary>A static (type, field) (input of the reload plan).</summary>
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

/// <summary>Selector of an interface method (name and function type).</summary>
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

/// <summary>--trace: path prefixes of the files to record (VmSession and the CLI set them before building a module).
/// If empty, nothing is recorded. Paths are compared with '\\' changed to '/'.</summary>
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

/// <summary>Location of an assignment value for --trace (#c of TVAL). The value is Leaves leaves of type Type.</summary>
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

/// <summary>A field of a value type (name and type).</summary>
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

/// <summary>One .tcsb module. Builds the type / constant / static / function tables and writes them to a byte sequence (the format is what tcsvm_load.c parses).
/// Only functions that are called (reached) are built. Output follows the IL order (Dictionary is used only for lookup).</summary>
public sealed class ModuleBuilder
{
    public const int Magic = 0x42534354; // "TCSB"
    public const int Version = 4;
    public const int NoType = 0xFFFF;

    // Tables for lookup (not enumerated. The order is in the List below)
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
    /// <summary>Static field table ("type.field", first slot, type). Reload maps them to the old version's statics by name
    /// (doc/object-model.md §6). The lazy-initialization flag is "type.&lt;init&gt;".</summary>
    private List<StaticField> _staticFields = new List<StaticField>();
    // "type\nfield" → static field
    private Dictionary<string, StaticField> _staticSlots = new Dictionary<string, StaticField>();
    public List<FunctionDef> Functions = new List<FunctionDef>();
    private Dictionary<string, int> _functionIndex = new Dictionary<string, int>();
    // --trace: the line table ("file:line" → index), the locations of assignment values, and the classes of the recorded files (in the order found)
    private List<string> _traceLineFiles = new List<string>();
    private List<int> _traceLines = new List<int>();
    private Dictionary<string, int> _traceLineIndex = new Dictionary<string, int>();
    public List<TraceValueSite> TraceValues = new List<TraceValueSite>();
    private List<string> _tracedClasses = new List<string>();
    private Dictionary<string, bool> _tracedPaths = new Dictionary<string, bool>();
    private List<FunctionDef> _pending = new List<FunctionDef>();
    private int _pendingHead;
    public int Entry = -1;

    // Host (--ref stub) functions and values. Names are IL host names ("eng.gfx.begin")
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

    /// <summary>Name of a class in the module's type table (the name written to the type table).</summary>
    public List<string> UsedClasses()
    {
        var names = new List<string>();
        foreach (var t in _types)
        {
            if (t.Tag == VT.Class) names.Add(t.Display());
        }
        return names;
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

    /// <summary>Method of a `--ref` interface (receiver is the interface's display name, method is the C# name).</summary>
    public ExForeignMethod? ForeignInterfaceMethod(string receiver, string method)
    {
        foreach (var name in _foreignOrder)
        {
            var m = _foreignMethods[name];
            // IL calls it by the host name (snake_case)
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

    /// <summary>Name of a type that can have static fields (class or struct).</summary>
    public bool IsTypeName(string name) => Classes.ContainsKey(Canon(name)) || Structs.ContainsKey(Canon(name));

    public bool IsEnum(string name) => _enums.ContainsKey(Canon(name));

    /// <summary>Type name of IlNewObj / IlIsType / IlCast. IL writes user type names here rather than display names
    /// (the `@float` class is `float`), so user classes and structs are looked up before built-in types with the same spelling.</summary>
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

    // ---- Types ----

    public VType Parse(string type0)
    {
        string type = type0.Trim();
        int n = ContractFacts.Len(type);
        // A nullable annotation on a reference type does not change the type. A value type's T? is Nullable, which is not held yet
        if (type.EndsWith("?"))
        {
            var inner = Parse(ContractFacts.Sub(type, 0, n - 1));
            return inner.IsRef ? inner : VType.OfNullable(inner);
        }
        if (type.EndsWith("[]")) return VType.OfArray(Parse(ContractFacts.Sub(type, 0, n - 2)));
        // Dictionary Keys / Values are a List in insertion order in the VM (the receiver of LINQ)
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
            // Predicate<T> / Comparison<T> are the same function types as Func<T, bool> / Func<T, T, int> (same as tcs2c)
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
            // Queue<T> is a List in the VM (Dequeue removes the first element), and HashSet<T> is Dictionary<T, bool> (enumeration is in the same order as .NET)
            if ((head == "System.Collections.Generic.Queue" || head == "Queue") && args.Count == 1) return VType.OfList(args[0]);
            if ((head == "System.Collections.Generic.HashSet" || head == "HashSet") && args.Count == 1) return VType.OfDict(args[0], VType.Bool);
            CFail.Report("type '" + type + "' is not in the VM yet");
            return VType.Void;
        }
        if (type == "int" || type == "uint" || type == "char" || type == "byte" || type == "sbyte" || type == "short" || type == "ushort") return VType.Int;
        if (type == "object") return VType.Object;
        // TinySystem facade. An unqualified name applies only when there is no user type with the same name (the user type shadows it, same as the C backend)
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
        if (_enums.ContainsKey(name)) return VType.Int; // An enum is i32 (il-spec §2)
        // Interface of a --ref module (X-4): implemented by VM classes and called by the C module. Held as object inside the VM
        // IL writes `Ed_ITool` in type tests and `Ed.ITool` in declarations, so they are unified under the display name as one type
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

    // Leaf sequence of a type (computed only once per type. The caller does not modify the sequence)
    private Dictionary<string, List<Leaf>> _leaves = new Dictionary<string, List<Leaf>>();
    private Dictionary<string, List<Leaf>> _objectLeaves = new Dictionary<string, List<Leaf>>();

    public List<Leaf> Leaves(VType t)
    {
        // Not looked up by display name (the user class `@float` and float, and the user class Random and TinySystem.Random, have the same display name)
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

    /// <summary>Leaves of a class instance (instance fields expanded down to structs). A cell gives the leaves of the value inside.</summary>
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
        // Inner types also go into the table (a recursive class stops because it already took its index above)
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

    /// <summary>Interfaces a class implements (only those in the program).</summary>
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

    /// <summary>--ref module interfaces a class implements (X-4). The C module's interface check looks them up by name (object-model §10.6).</summary>
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

    /// <summary>Function type of a method call. out parameters are removed from the parameters, and the return value and out values are returned as multiple values (Tuple)
    /// (the implementing function returns the out leaves after the return value leaves).</summary>
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

    /// <summary>Whether a class implements an interface (including inherited interfaces).</summary>
    private bool Implements(string cls, string iface)
    {
        var seen = new HashSet<string>();
        // C# Stack: pops in the reverse order of pushing
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

    /// <summary>Selector of an interface method. Puts functions into the itables of implementing classes (builds methods only for reached classes).
    /// requestMethod returns a function index from (class, implementing method, selector function type).</summary>
    public int Selector(string iface0, ExMethod method, System.Func<string, ExMethod, VType, int> requestMethod)
    {
        // One selector per name and function type (a specialized generic interface has the same name with different types: IGen<int> and IGen<string>)
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
            // Implementations with different types exist only in classes that implement this interface (an adapter matches the types)
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

    // ---- Runtime functions (CALLR) ----

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

    // ---- Constants ----

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

    /// <summary>First slot of a static field (its type goes into type).</summary>
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

    // ---- Static initialization (same as il-spec and tcs2c): types whose initializers are only constants are initialized first,
    //      other types only once, on the first read or write of a static (same as C# §15.5.6.2; handles forward references and cycles) ----

    private static bool IsConstantExpr(IlExpr e)
    {
        if (e is IlLit) return true;
        if (e is IlParen p) return IsConstantExpr(p.E);
        if (e is IlUn u) return IsConstantExpr(u.E);
        if (e is IlBin b) return IsConstantExpr(b.L) && IsConstantExpr(b.R);
        return false;
    }

    /// <summary>Types with static fields (classes and structs) in declaration order.</summary>
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

    /// <summary>The "initialization started" flag of a lazily initialized type (a bool static).</summary>
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

    /// <summary>Hidden static that holds Random.Shared (created and seeded on first reference).</summary>
    public int SharedRandomSlot()
    {
        if (_sharedRandom >= 0) return _sharedRandom;
        _sharedRandom = _statics.Count;
        _statics.Add(Info(VType.Random()));
        _staticFields.Add(new StaticField("TinySystem.Random.Shared", _sharedRandom, VType.Random()));
        return _sharedRandom;
    }

    // ---- Functions ----

    /// <summary>Returns the function index. If it is new, creates it and queues it for compilation.</summary>
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

    /// <summary>Function index (-1 if missing).</summary>
    public int FindFunction(string name)
    {
        int i;
        if (_functionIndex.TryGetValue(name, out i)) return i;
        return -1;
    }

    /// <summary>Compiles until the queue is empty (if a compiled function calls new functions, they are queued too).</summary>
    public void CompilePending()
    {
        while (_pendingHead < _pending.Count && !CFail.Failed)
        {
            var def = _pending[_pendingHead];
            _pendingHead++;
            def.Compile!(def);
            // Attaches the location of the statement being compiled to the error. Uses the same form as other diagnostics so the host can show the line
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

    /// <summary>Index into the line table (#imm of TRACE).</summary>
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

    /// <summary>Index of an assignment value location (#c of TVAL).</summary>
    public int TraceValue(string path, int line, int col, string label, VType type, int leaves)
    {
        TraceValues.Add(new TraceValueSite(VmTrace.Normalize(path), line, col, label, type, leaves));
        return TraceValues.Count - 1;
    }

    public void TraceClass(string name)
    {
        if (!_tracedClasses.Contains(name)) _tracedClasses.Add(name);
    }

    // ---- Output ----

    /// <summary>Input of the reload plan: the type descriptor table and the static (type, field) pairs.</summary>
    public List<TypeTableEntry> Describe(out List<StaticKey> statics)
    {
        // As in Write, adds the struct types of static fields to the table (structs that no heap object holds are also moved by field name in the reload plan)
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
        // Names of the --trace tables and types of the value locations (decided before the type table)
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
        // Types in the static field table are decided before the type table (struct types are added to the table)
        var staticNames = new List<int>();
        var staticInfos = new List<TypeTableInfo>();
        foreach (var s in _staticFields)
        {
            staticNames.Add(Str(s.Name));
            staticInfos.Add(FieldInfo(s.Type));
        }
        // Type table (doc/object-model.md §4). Names go into the string table first
        var entries = new List<TypeTableEntry>();
        for (int i = 0; i < _types.Count; i++) entries.Add(TypeEntry(_types[i]));
        foreach (var name in TypeTable.Names(entries)) Str(name);

        var w = new List<int>();
        VBytes.I32(w, Magic);
        VBytes.I32(w, Version);
        VBytes.I32(w, OpAbi.Hash);

        VBytes.I32(w, _strings.Strings.Count);
        // The string table does not grow while writing (all names were added above)
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
        // Per-function file paths and --trace tables (read_debug in tcsvm_load.c)
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

    // ---- Type descriptors (doc/object-model.md §4) ----

    private TypeTableInfo FieldInfo(VType t)
    {
        if (t.Tag == VT.Prim) return new TypeTableInfo((int)t.PKind, NoType);
        if (t.IsAggregate) return new TypeTableInfo(TypeTable.KindStruct, TypeIndex(t));
        return new TypeTableInfo(TypeTable.KindRef, TypeIndex(t));
    }

    /// <summary>A field of a value type (name and type). The leaves (Leaves) are this sequence expanded depth-first.</summary>
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
                        | (info.IsExternal ? TypeTable.FlagHostValue : 0); // A data class shared with the host has host_value (doc/object-model.md §3)
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
                        // In selector index order (equal indexes in insertion order; C# OrderBy is stable)
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
                    // 8 ints = 64-bit state × 4 (read and written by runtime random.*). A leading pad places the state
                    // from byte 8 of the payload (after the 4-byte type_id; aligns 64-bit reads and writes)
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
