using System.Collections.Generic;
using TcsC;
using TcsSelf;

namespace TcsVm;

// tcs VM の backend (IL を .tcsb にする。旧 tcsvm/Emitter の C# 版を TinyC# に写したもの) の型と値。
// 誤りは C backend と同じく最初のものを TcsC.CFail に記録し、出力は誤りが無いときだけ使う

/// <summary>register / static / 葉の種類 (doc/vm-design.md §6.1)。tcsvm_ops.h の TVM_K_* と同じ値。</summary>
public enum Kind
{
    I32 = 0,
    F32 = 1,
    Bool = 2,
    Ref = 3,
}

/// <summary>VType の種類 (C# 版の VType の派生 record)。</summary>
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

/// <summary>emitter の静的型。IL の型文字列 (display 名) から作る。
/// C# 版は record の派生 (PrimT、ClassT ...) で、ここでは種類 (Tag) と中身の field を持つ 1 つの class。
/// 等価は Same (C# 版の record の等価と同じく、種類と中身で比べる)。</summary>
public sealed class VType
{
    public VT Tag;
    // Prim と Box の種類
    public Kind PKind;
    // Prim の名前 (int / float / bool)、Class / Struct / Iface / HostIface の名前
    public string Name = "";
    // Array / List の要素
    public VType? Elem;
    // Dict と Kvp の key と value
    public VType? Key;
    public VType? Value;
    // Func の引数と戻り値 (Action は Void)
    public List<VType> Params = new List<VType>();
    public VType? Ret;
    // Tuple の要素
    public List<VType> Items = new List<VType>();
    // Cell と Nullable の中の型
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

    /// <summary>heap の object を指す ref の型か。</summary>
    public bool IsRef =>
        Tag == VT.Class || Tag == VT.Array || Tag == VT.String || Tag == VT.List || Tag == VT.Dict || Tag == VT.Func
        || Tag == VT.Cell || Tag == VT.Iface || Tag == VT.Object || Tag == VT.Box || Tag == VT.Random || Tag == VT.HostIface;

    /// <summary>葉の並びを持つ値型 (VM では STRUCT の型)。</summary>
    public bool IsAggregate => Tag == VT.Struct || Tag == VT.Nullable || Tag == VT.Kvp || Tag == VT.Utf8;

    /// <summary>C# 版の record の ToString と同じ表記。</summary>
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

    /// <summary>等価の鍵: 種類と中身 (C# 版の record の等価)。Func と Tuple は C# 版と同じく表記で比べる。</summary>
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

    /// <summary>Kind の C# の名前 (enum の ToString)。</summary>
    public static string KindName(Kind k)
    {
        if (k == Kind.I32) return "I32";
        if (k == Kind.F32) return "F32";
        if (k == Kind.Bool) return "Bool";
        return "Ref";
    }
}

/// <summary>値の葉 (struct は field を再帰的に展開した列)。Type は葉自身の型 (prim か ref)。</summary>
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

/// <summary>register / 戻り値 / 捕捉 / static の種類と型の番号 (C# 版の (Kind Kind, ushort Type))。</summary>
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

/// <summary>値: 葉ごとの register の列 (prim / ref は 1 つ、struct は葉の数)。</summary>
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

/// <summary>変数: register に住む値 (Reg)、cell に住む値 (Cell。closure が捕捉する。il-spec §7)、
/// 自分の closure が捕捉した cell (Cap)。C# 版の RegVar / CellVar / CapVar。Type は値の型 (cell なら中の型)。</summary>
public sealed class Var
{
    public VarKind K;
    public VType Type;
    // Reg の値
    public Val? Val;
    // Cell の register
    public int Reg;
    // Cap の番号
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

    // Cell と Cap の中の型 (C# 版の Inner)
    public VType Inner => Type;
}

/// <summary>関数の行の表の 1 項 (pc から始まる行)。</summary>
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

/// <summary>1 つの関数。命令は 64 bit の語で、下の 32 bit を CodeLo、上の 32 bit を CodeHi に持つ。</summary>
public sealed class FunctionDef
{
    public string Name;
    public string File = "";
    // ファイルの path ('/' 区切り。--trace と Pick の呼び出し元が使う)
    public string Path = "";
    public int NParams;
    public List<RegInfo> Regs = new List<RegInfo>();
    public List<RegInfo> Rets = new List<RegInfo>();
    public List<RegInfo> Caps = new List<RegInfo>();
    public bool HasCode;
    public List<int> CodeLo = new List<int>();
    public List<int> CodeHi = new List<int>();
    public List<LinePc> Lines = new List<LinePc>();
    public System.Action<FunctionDef>? Compile;

    public FunctionDef(string name)
    {
        Name = name;
    }
}
