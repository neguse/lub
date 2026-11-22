using System.Collections.Generic;
using TcsC;
using TcsSelf;

namespace TcsVm;

// Types and values of the tcs VM backend (turns IL into .tcsb; a port of the C# version of the old tcsvm/Emitter to TinyC#).
// As in the C backend, the first error is recorded in TcsC.CFail, and the output is used only when there are no errors

/// <summary>Kinds of registers / statics / leaves (doc/vm-design.md §6.1). Same values as TVM_K_* in tcsvm_ops.h.</summary>
public enum Kind
{
    I32 = 0,
    F32 = 1,
    Bool = 2,
    Ref = 3,
}

/// <summary>Kinds of VType (the derived records of VType in the C# version).</summary>
public enum VT
{
    Prim,
    Class,
    Struct,
    Array,
    String,
    List,
    Dict,
    Func,
    Tuple,
    Cell,
    Iface,
    HostIface,
    Object,
    Box,
    Nullable,
    Kvp,
    Utf8,
    Random,
    Void,
    Null,
}

/// <summary>Static type in the emitter. Built from IL type strings (display names).
/// The C# version uses derived records (PrimT, ClassT ...); here it is one class with a kind (Tag) and fields for the contents.
/// Equality is Same (compares kind and contents, like record equality in the C# version).</summary>
public sealed class VType
{
    public VT Tag;
    // Kind for Prim and Box
    public Kind PKind;
    // Name of a Prim (int / float / bool), or of a Class / Struct / Iface / HostIface
    public string Name = "";
    // Element of Array / List
    public VType? Elem;
    // key and value of Dict and Kvp
    public VType? Key;
    public VType? Value;
    // Parameters and return value of Func (Void for Action)
    public List<VType> Params = new List<VType>();
    public VType? Ret;
    // Elements of Tuple
    public List<VType> Items = new List<VType>();
    // Inner type of Cell and Nullable
    public VType? Inner;
    private string _key = "";
    private string _display = "";

    public VType(VT tag)
    {
        Tag = tag;
    }

    public static VType Int = Prim(Kind.I32, "int");
    public static VType Float = Prim(Kind.F32, "float");
    public static VType Bool = Prim(Kind.Bool, "bool");
    public static VType Void = new VType(VT.Void);
    public static VType Null = new VType(VT.Null);
    public static VType String = new VType(VT.String);
    public static VType Object = new VType(VT.Object);

    public static VType Prim(Kind kind, string name)
    {
        var t = new VType(VT.Prim);
        t.PKind = kind;
        t.Name = name;
        return t;
    }

    public static VType Named(VT tag, string name)
    {
        var t = new VType(tag);
        t.Name = name;
        return t;
    }

    public static VType OfClass(string name) => Named(VT.Class, name);
    public static VType OfStruct(string name) => Named(VT.Struct, name);
    public static VType OfIface(string name) => Named(VT.Iface, name);
    public static VType OfHostIface(string name) => Named(VT.HostIface, name);

    public static VType OfArray(VType elem)
    {
        var t = new VType(VT.Array);
        t.Elem = elem;
        return t;
    }

    public static VType OfList(VType elem)
    {
        var t = new VType(VT.List);
        t.Elem = elem;
        return t;
    }

    public static VType OfDict(VType key, VType value)
    {
        var t = new VType(VT.Dict);
        t.Key = key;
        t.Value = value;
        return t;
    }

    public static VType OfKvp(VType key, VType value)
    {
        var t = new VType(VT.Kvp);
        t.Key = key;
        t.Value = value;
        return t;
    }

    public static VType OfFunc(List<VType> parameters, VType ret)
    {
        var t = new VType(VT.Func);
        t.Params = parameters;
        t.Ret = ret;
        return t;
    }

    public static VType OfTuple(List<VType> items)
    {
        var t = new VType(VT.Tuple);
        t.Items = items;
        return t;
    }

    public static VType OfCell(VType inner)
    {
        var t = new VType(VT.Cell);
        t.Inner = inner;
        return t;
    }

    public static VType OfNullable(VType inner)
    {
        var t = new VType(VT.Nullable);
        t.Inner = inner;
        return t;
    }

    public static VType OfBox(Kind kind)
    {
        var t = new VType(VT.Box);
        t.PKind = kind;
        return t;
    }

    public static VType Utf8() => new VType(VT.Utf8);

    public static VType Random() => new VType(VT.Random);

    /// <summary>Whether it is a ref type pointing to a heap object.</summary>
    public bool IsRef =>
        Tag == VT.Class || Tag == VT.Array || Tag == VT.String || Tag == VT.List || Tag == VT.Dict || Tag == VT.Func
        || Tag == VT.Cell || Tag == VT.Iface || Tag == VT.Object || Tag == VT.Box || Tag == VT.Random || Tag == VT.HostIface;

    /// <summary>A value type with a sequence of leaves (a STRUCT type in the VM).</summary>
    public bool IsAggregate => Tag == VT.Struct || Tag == VT.Nullable || Tag == VT.Kvp || Tag == VT.Utf8;

    /// <summary>Same notation as the record ToString in the C# version.</summary>
    public string Display()
    {
        if (_display != "") return _display;
        string d;
        switch (Tag)
        {
            case VT.Prim:
            case VT.Class:
            case VT.Struct:
            case VT.Iface:
            case VT.HostIface:
                d = Name;
                break;
            case VT.Array:
                d = Elem!.Display() + "[]";
                break;
            case VT.String:
                d = "string";
                break;
            case VT.List:
                d = "List<" + Elem!.Display() + ">";
                break;
            case VT.Dict:
                d = "Dictionary<" + Key!.Display() + ", " + Value!.Display() + ">";
                break;
            case VT.Func:
                {
                    var parts = new List<string>();
                    foreach (var p in Params) parts.Add(p.Display());
                    if (Ret!.Tag == VT.Void) d = "Action<" + string.Join(", ", parts) + ">";
                    else
                    {
                        parts.Add(Ret.Display());
                        d = "Func<" + string.Join(", ", parts) + ">";
                    }
                    break;
                }
            case VT.Tuple:
                {
                    var parts = new List<string>();
                    foreach (var p in Items) parts.Add(p.Display());
                    d = "(" + string.Join(", ", parts) + ")";
                    break;
                }
            case VT.Cell:
                d = "cell<" + Inner!.Display() + ">";
                break;
            case VT.Object:
                d = "object";
                break;
            case VT.Box:
                d = "box<" + KindName(PKind) + ">";
                break;
            case VT.Nullable:
                d = Inner!.Display() + "?";
                break;
            case VT.Kvp:
                d = "KeyValuePair<" + Key!.Display() + ", " + Value!.Display() + ">";
                break;
            case VT.Utf8:
                d = "Utf8Bytes";
                break;
            case VT.Random:
                d = "Random";
                break;
            case VT.Void:
                d = "void";
                break;
            default:
                d = "null";
                break;
        }
        _display = d;
        return d;
    }

    /// <summary>Equality key: kind and contents (record equality in the C# version). Func and Tuple compare by notation, as in the C# version.</summary>
    public string Ident()
    {
        if (_key != "") return _key;
        string k;
        switch (Tag)
        {
            case VT.Func:
                k = "F:" + Display();
                break;
            case VT.Tuple:
                k = "T:" + Display();
                break;
            case VT.Prim:
                k = "P" + (int)PKind + ":" + Name;
                break;
            case VT.Box:
                k = "B" + (int)PKind;
                break;
            case VT.Class:
            case VT.Struct:
            case VT.Iface:
            case VT.HostIface:
                k = (int)Tag + ":" + Name;
                break;
            case VT.Array:
            case VT.List:
                k = (int)Tag + "(" + Elem!.Ident() + ")";
                break;
            case VT.Dict:
            case VT.Kvp:
                k = (int)Tag + "(" + Key!.Ident() + "," + Value!.Ident() + ")";
                break;
            case VT.Cell:
            case VT.Nullable:
                k = (int)Tag + "(" + Inner!.Ident() + ")";
                break;
            default:
                k = "" + (int)Tag;
                break;
        }
        _key = k;
        return k;
    }

    public static bool Same(VType? a, VType? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a.Ident() == b.Ident();
    }

    /// <summary>C# name of Kind (enum ToString).</summary>
    public static string KindName(Kind k)
    {
        if (k == Kind.I32) return "I32";
        if (k == Kind.F32) return "F32";
        if (k == Kind.Bool) return "Bool";
        return "Ref";
    }
}

/// <summary>Leaves of a value (for a struct, the sequence of fields expanded recursively). Type is the leaf's own type (prim or ref).</summary>
public sealed class Leaf
{
    public string Path;
    public VType Type;

    public Leaf(string path, VType type)
    {
        Path = path;
        Type = type;
    }

    public Kind Kind => Type.Tag == VT.Prim ? Type.PKind : Kind.Ref;
}

/// <summary>Kind and type index of a register / return value / capture / static ((Kind Kind, ushort Type) in the C# version).</summary>
public sealed class RegInfo
{
    public Kind Kind;
    public int Type;

    public RegInfo(Kind kind, int type)
    {
        Kind = kind;
        Type = type;
    }

    public string Key() => (int)Kind + "," + Type;
}

/// <summary>Value: the sequence of registers per leaf (one for prim / ref, the number of leaves for a struct).</summary>
public sealed class Val
{
    public VType Type;
    public List<int> Regs;

    public Val(VType type, List<int> regs)
    {
        Type = type;
        Regs = regs;
    }

    public int Reg
    {
        get
        {
            if (Regs.Count == 1) return Regs[0];
            CFail.Report(Type.Display() + " is not a single register");
            return 0;
        }
    }
}

public enum VarKind
{
    Reg,
    Cell,
    Cap,
}

/// <summary>Variable: a value living in a register (Reg), a value living in a cell (Cell; captured by a closure, il-spec §7),
/// or a cell captured by the current closure (Cap). RegVar / CellVar / CapVar in the C# version. Type is the value type (for a cell, the inner type).</summary>
public sealed class Var
{
    public VarKind K;
    public VType Type;
    // Value for Reg
    public Val? Val;
    // Register for Cell
    public int Reg;
    // Index for Cap
    public int Index;

    public Var(VarKind k, VType type)
    {
        K = k;
        Type = type;
    }

    public static Var OfReg(Val val)
    {
        var v = new Var(VarKind.Reg, val.Type);
        v.Val = val;
        return v;
    }

    public static Var OfCell(int reg, VType inner)
    {
        var v = new Var(VarKind.Cell, inner);
        v.Reg = reg;
        return v;
    }

    public static Var OfCap(int index, VType inner)
    {
        var v = new Var(VarKind.Cap, inner);
        v.Index = index;
        return v;
    }

    // Inner type for Cell and Cap (Inner in the C# version)
    public VType Inner => Type;
}

/// <summary>One entry of a function's line table (the line starting at pc).</summary>
public sealed class LinePc
{
    public int Pc;
    public int Line;

    public LinePc(int pc, int line)
    {
        Pc = pc;
        Line = line;
    }
}

/// <summary>One function. Instructions are 64-bit words, with the lower 32 bits in CodeLo and the upper 32 bits in CodeHi.</summary>
public sealed class FunctionDef
{
    public string Name;
    public string File = "";
    // File path ('/'-separated. Used by --trace and the callers of Pick)
    public string Path = "";
    public int NParams;
    public List<RegInfo> Regs = new List<RegInfo>();
    public List<RegInfo> Rets = new List<RegInfo>();
    public List<RegInfo> Caps = new List<RegInfo>();
    public bool HasCode;
    public List<int> CodeLo = new List<int>();
    public List<int> CodeHi = new List<int>();
    public List<LinePc> Lines = new List<LinePc>();
    // The C# position last being compiled (the error location when the function could not be built)
    public IlPos? At;
    public System.Action<FunctionDef>? Compile;

    public FunctionDef(string name)
    {
        Name = name;
    }
}
