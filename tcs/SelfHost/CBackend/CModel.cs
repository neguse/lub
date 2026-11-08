using System.Collections.Generic;
using TinySystem.Text;

namespace TcsC;

// tcs の C backend (IL → C、旧 tcs2c/ の C# 版を写したもの) の型と名前。
// 例外を持たないサブセットなので、誤りは CFail に最初の 1 つを記録し、呼んだ側は既定の値で続ける。
// 出力は誤りが無いときだけ使う (CEmitter.Emit が CFail.Error を見る)

public static class CFail
{
    public static string Error = "";

    public static void Report(string message)
    {
        if (Error == "") Error = message;
    }

    public static bool Failed => Error != "";
}

public enum CTypeKind
{
    Void,
    I32,
    F32,
    Bool,
    String,
    Ref,
    Array,
    List,
    Queue,
    HashSet,
    Null,
    Dict,
    Kvp,
    Closure,
    StructVal,
    Nullable,
    Random,
    Object,
}

// C の値の型。構造等価は Key (型の正規の文字列) で比べる (Same)
public sealed class CType
{
    public CTypeKind Kind;
    public string? Name;
    public CType? Element;
    public CType? KeyType;
    public List<CType>? Parameters;
    public string Key;

    public CType(CTypeKind kind, string? name, CType? element, CType? keyType, List<CType>? parameters)
    {
        Kind = kind;
        Name = name;
        Element = element;
        KeyType = keyType;
        Parameters = parameters;
        var parts = new List<string>();
        parts.Add(KindName(kind));
        if (name != null) parts.Add(":" + name);
        if (element != null) parts.Add("<" + element.Key + ">");
        if (keyType != null) parts.Add("{" + keyType.Key + "}");
        if (parameters != null)
        {
            parts.Add("(");
            foreach (var p in parameters) parts.Add(p.Key + ";");
            parts.Add(")");
        }
        Key = string.Join("", parts);
    }

    public static readonly CType Void = new CType(CTypeKind.Void, null, null, null, null);
    public static readonly CType I32 = new CType(CTypeKind.I32, null, null, null, null);
    public static readonly CType F32 = new CType(CTypeKind.F32, null, null, null, null);
    public static readonly CType Bool = new CType(CTypeKind.Bool, null, null, null, null);
    public static readonly CType String = new CType(CTypeKind.String, null, null, null, null);
    public static readonly CType Null = new CType(CTypeKind.Null, null, null, null, null);
    // TinySystem.Random の instance (runtime の TcsRandom、GC object)
    public static readonly CType Random = new CType(CTypeKind.Random, null, null, null, null);
    // TinySystem.Text.Utf8Bytes。CEmitter が足す struct (文字列、開始位置、長さ) の値
    public const string Utf8BytesStruct = "TinySystem_Text_Utf8Bytes";
    public static readonly CType Utf8Bytes = new CType(CTypeKind.StructVal, Utf8BytesStruct, null, null, null);
    // `object`: 参照型はそのまま、int / float / bool は box (TcsBox)。実行時型 tag (GC header の type_id) で cast を検査する
    public static readonly CType Object = new CType(CTypeKind.Object, null, null, null, null);

    public static CType Ref(string name) => new CType(CTypeKind.Ref, name, null, null, null);
    // データ struct。C では素の値型 (ポインタなし)
    public static CType Struct(string name) => new CType(CTypeKind.StructVal, name, null, null, null);
    public static CType Array(CType element) => new CType(CTypeKind.Array, null, element, null, null);
    public static CType List(CType? element) => new CType(CTypeKind.List, null, element, null, null);
    public static CType Queue(CType element) => new CType(CTypeKind.Queue, null, element, null, null);
    public static CType HashSet(CType element) => new CType(CTypeKind.HashSet, null, element, null, null);
    public static CType Dict(CType key, CType value) => new CType(CTypeKind.Dict, null, value, key, null);
    public static CType Kvp(CType key, CType value) => new CType(CTypeKind.Kvp, null, value, key, null);
    // 関数値。Element = 戻り型 (void 可)、Parameters = 引数型
    public static CType Closure(CType ret, List<CType> parameters) => new CType(CTypeKind.Closure, null, ret, null, parameters);
    // Nullable<T> (値型のみ)。C は { bool has; T v; }
    public static CType Nullable(CType element) => new CType(CTypeKind.Nullable, null, element, null, null);

    public static bool Same(CType? a, CType? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a.Key == b.Key;
    }

    public bool IsNullableValue => Kind == CTypeKind.Nullable;

    public string CName
    {
        get
        {
            switch (Kind)
            {
                case CTypeKind.Void: return "void";
                case CTypeKind.I32: return "int32_t";
                case CTypeKind.F32: return "float";
                case CTypeKind.Bool: return "bool";
                case CTypeKind.String: return "TcsString *";
                case CTypeKind.Ref: return "Tcs_" + Names.Id(Name!) + " *";
                case CTypeKind.StructVal: return "Tcs_" + Names.Id(Name!);
                case CTypeKind.Array: return "TcsArray *";
                case CTypeKind.List: return "TcsList *";
                case CTypeKind.Queue: return "TcsQueue *";
                case CTypeKind.HashSet: return "TcsHashSet *";
                case CTypeKind.Dict: return "TcsDict *";
                case CTypeKind.Closure: return "TcsClosure *";
                case CTypeKind.Random: return "TcsRandom *";
                case CTypeKind.Object: return "void *";
                case CTypeKind.Nullable:
                    switch (Element!.Kind)
                    {
                        case CTypeKind.I32: return "TcsOptI32";
                        case CTypeKind.F32: return "TcsOptF32";
                        case CTypeKind.Bool: return "TcsOptBool";
                        case CTypeKind.StructVal: return "TcsOpt_S_" + Names.Id(Element.Name!);
                    }
                    CFail.Report("unsupported nullable element: " + Element.Display());
                    return "void";
            }
            CFail.Report("unsupported type: " + Display());
            return "void";
        }
    }

    public string ElementCName
    {
        get
        {
            if ((Kind == CTypeKind.Array || Kind == CTypeKind.List) && Element != null) return Element.CName;
            CFail.Report("element type of a non-collection: " + Display());
            return "void";
        }
    }

    public bool CanAssignFrom(CType source)
    {
        if (Same(this, source)) return true;
        if (Kind == CTypeKind.F32 && source.Kind == CTypeKind.I32) return true;
        if (Kind == CTypeKind.Object && (source.IsNullable || source.Kind == CTypeKind.I32 || source.Kind == CTypeKind.F32 || source.Kind == CTypeKind.Bool)) return true;
        if (Kind == CTypeKind.Nullable && (source.Kind == CTypeKind.Null || Element!.CanAssignFrom(source))) return true;
        if (IsNullable && source.Kind == CTypeKind.Null) return true;
        if (Kind == CTypeKind.List && source.Kind == CTypeKind.List && (Element == null || source.Element == null || Same(Element, source.Element))) return true;
        if (Kind == CTypeKind.Dict && source.Kind == CTypeKind.Dict && Same(Element, source.Element) && Same(KeyType, source.KeyType)) return true;
        if (Kind == CTypeKind.Closure && source.Kind == CTypeKind.Closure && Same(Element, source.Element) && Parameters!.Count == source.Parameters!.Count)
        {
            for (int i = 0; i < Parameters.Count; i++)
            {
                if (!Same(Parameters[i], source.Parameters[i])) return false;
            }
            return true;
        }
        return false;
    }

    public bool IsNullable => Kind == CTypeKind.String || Kind == CTypeKind.Ref || Kind == CTypeKind.Array || Kind == CTypeKind.List
        || Kind == CTypeKind.Queue || Kind == CTypeKind.HashSet || Kind == CTypeKind.Dict || Kind == CTypeKind.Closure
        || Kind == CTypeKind.Random || Kind == CTypeKind.Object;

    // 誤りの文の表記 (C# 版の CType.ToString)
    public string Display()
    {
        switch (Kind)
        {
            case CTypeKind.Ref: return "ref " + Name;
            case CTypeKind.StructVal: return "struct " + Name;
            case CTypeKind.Nullable: return Element!.Display() + "?";
            case CTypeKind.Array: return Element!.Display() + "[]";
            case CTypeKind.List: return "list<" + (Element != null ? Element.Display() : "?") + ">";
            case CTypeKind.Queue: return "queue<" + Element!.Display() + ">";
            case CTypeKind.HashSet: return "hashset<" + Element!.Display() + ">";
            case CTypeKind.String: return "string";
            case CTypeKind.Null: return "null";
        }
        return KindName(Kind).ToLower();
    }

    public static string KindName(CTypeKind kind)
    {
        switch (kind)
        {
            case CTypeKind.Void: return "Void";
            case CTypeKind.I32: return "I32";
            case CTypeKind.F32: return "F32";
            case CTypeKind.Bool: return "Bool";
            case CTypeKind.String: return "String";
            case CTypeKind.Ref: return "Ref";
            case CTypeKind.Array: return "Array";
            case CTypeKind.List: return "List";
            case CTypeKind.Queue: return "Queue";
            case CTypeKind.HashSet: return "HashSet";
            case CTypeKind.Null: return "Null";
            case CTypeKind.Dict: return "Dict";
            case CTypeKind.Kvp: return "Kvp";
            case CTypeKind.Closure: return "Closure";
            case CTypeKind.StructVal: return "StructVal";
            case CTypeKind.Nullable: return "Nullable";
            case CTypeKind.Random: return "Random";
        }
        return "Object";
    }
}

public static class Names
{
    // C の識別子にできる名前 ([A-Za-z_][A-Za-z0-9_]*)
    public static string Id(string name)
    {
        var u = new Utf8Bytes(name);
        bool ok = u.Length > 0;
        for (int i = 0; i < u.Length && ok; i++)
        {
            int c = u[i];
            bool letter = c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c == '_';
            bool digit = c >= '0' && c <= '9';
            if (!letter && !(digit && i > 0)) ok = false;
        }
        if (!ok) CFail.Report("identifier is not supported by the C backend: " + name);
        return name;
    }

    public static string Class(string name) => "Tcs_" + Id(name);
    public static string Field(string name) => "f_" + Id(name);
    public static string StaticField(string cls, string name) => "tcs_s_" + Id(cls) + "_" + Id(name);
    public static string Method(string cls, string name) => "tcs_m_" + Id(cls) + "_" + Id(name);
    public static string StaticInit(string cls) => "tcs_sinit_" + Id(cls);
    public static string InterfaceCheck(string iface) => "tcs_is_" + Id(iface);
    public static string StaticPlace(string cls, string name) => "tcs_sp_" + Id(cls) + "_" + Id(name);
    public static string New(string cls) => "tcs_new_" + Id(cls);
    public static string TypeId(string cls) => "TCS_TYPE_" + Id(cls);
    public static string TypeIdMax(string cls) => "TCS_TYPE_MAX_" + Id(cls);
    public static string Dispatch(string cls, string method) => "tcs_dispatch_" + Id(cls) + "_" + Id(method);
    public static string Init(string cls) => "tcs_init_" + Id(cls);
    public static string RecordEq(string cls) => "tcs_eq_" + Id(cls);
    public static string StructCtor(string st) => "tcs_ctor_S_" + Id(st);
    public static string StructEq(string st) => "tcs_eq_S_" + Id(st);
    public static string ClassLayout(string cls) => "tcs_layout_C_" + Id(cls);
    public static string StructLayout(string st) => "tcs_layout_S_" + Id(st);
    public static string NullableLayout(string st) => "tcs_layout_N_" + Id(st);
}

public static class Constants
{
    public static string I32(int value)
    {
        if (value == int.MinValue) return "INT32_MIN";
        if (value < 0) return "-INT32_C(" + (-value) + ")";
        return "INT32_C(" + value + ")";
    }

    public static string F32(float value) => F32Bits(FloatBits(value));

    // binary32 の bit の並び (FloatBits の形) の f32 の定数
    public static string F32Bits(int bits) => "tcs_f32(UINT32_C(0x" + Hex8(bits) + "))";

    // binary32 の bit の並び (int に入れた 32 bit。負の数は上位の bit が立つ)。2 倍と 1/2 倍は丸めないので、指数と仮数を正確に取り出せる。
    // 23 bit の後ろに残りがあれば最も近い偶数へ丸める (float が binary32 の .NET、C、wasm では残りは 0)
    public static int FloatBits(float value)
    {
        // NaN だけが自分と等しくない (float.IsNaN は TinyC# に無い)
#pragma warning disable CS1718
        if (value != value) return 0x7fc00000;
#pragma warning restore CS1718
        int sign = 0;
        if (value < 0f || value == 0f && 1f / value < 0f)
        {
            sign = int.MinValue;
            value = -value;
        }
        if (value == 0f) return sign;
        if (value > float.MaxValue) return sign | 0x7f800000;
        int exp = 0;
        float m = value;
        while (m >= 2f)
        {
            m = m / 2f;
            exp++;
        }
        while (m < 1f && exp > -126)
        {
            m = m * 2f;
            exp--;
        }
        bool subnormal = m < 1f;
        if (!subnormal) m = m - 1f;
        // 仮数の 23 bit (subnormal は m * 2^23 の整数部)
        int mantissa = 0;
        for (int i = 0; i < 23; i++)
        {
            m = m * 2f;
            mantissa = mantissa * 2;
            if (m >= 1f)
            {
                mantissa = mantissa + 1;
                m = m - 1f;
            }
        }
        // 残り m (0 <= m < 1) で丸める: 0.5 より大きいか、ちょうど 0.5 で奇数なら繰り上げる
        if (m > 0.5f || m == 0.5f && (mantissa & 1) == 1)
        {
            mantissa = mantissa + 1;
            if (mantissa == 0x800000)
            {
                mantissa = 0;
                if (subnormal) subnormal = false;
                else exp++;
                if (exp > 127) return sign | 0x7f800000;
            }
        }
        if (subnormal) return sign | mantissa;
        return sign | (exp + 127) << 23 | mantissa;
    }

    public static string Hex8(int bits)
    {
        string digits = "0123456789abcdef";
        var parts = new List<string>();
        for (int shift = 28; shift >= 0; shift -= 4)
        {
            int d = bits >> shift & 15;
            parts.Add(new Utf8Bytes(digits).Slice(d, 1).ToString());
        }
        return string.Join("", parts);
    }
}
