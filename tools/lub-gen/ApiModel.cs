using System.Collections.Generic;
using TcsSelf;

namespace LubGen;

// cs-lib/lub_stub.cs から読んだ API 面。
// 名前は C# 名 (中立表記) と、tcs の名前の規則で導いた snake_case の名前 (LuaName。C の名前、tcs VM の host の名前、API docs が使う) を両方持つ。
// C の名前と型は CHeader が規則で導く。

public enum LubTypeKind
{
    Void,
    Int,
    Double,
    Bool,
    String,
    Enum,
    Handle,
    Keyed,
    View,
    Record,
    List,
    Array,
    Func,
    Native,
    // struct の record (C の layout の値で渡る)
    Struct,
}

/// <summary>stub の型。
/// Name は enum なら "Gfx.LoadAction"、class なら class 名。
/// List / Array は Elem、Func は FuncParams と FuncReturn。
/// </summary>
public sealed class TypeRef
{
    public LubTypeKind Kind;
    public string Name;
    public bool Nullable;
    public TypeRef? Elem;
    public List<TypeRef>? FuncParams;
    public TypeRef? FuncReturn;

    public TypeRef(LubTypeKind kind, string name, bool nullable, TypeRef? elem, List<TypeRef>? funcParams, TypeRef? funcReturn)
    {
        Kind = kind;
        Name = name;
        Nullable = nullable;
        Elem = elem;
        FuncParams = funcParams;
        FuncReturn = funcReturn;
    }

    public static TypeRef VoidType = new TypeRef(LubTypeKind.Void, "void", false, null, null, null);

    public static TypeRef Of(LubTypeKind kind, string name) => new TypeRef(kind, name, false, null, null, null);

    public TypeRef AsNullable() => new TypeRef(Kind, Name, true, Elem, FuncParams, FuncReturn);

    public bool IsScalar => Kind == LubTypeKind.Int || Kind == LubTypeKind.Double || Kind == LubTypeKind.Bool || Kind == LubTypeKind.Enum;

    // struct の record の field に置ける型 (int / float / bool / enum と、それだけでできた struct の record)
    public bool IsBlittable => !Nullable && (IsScalar || Kind == LubTypeKind.Struct);

    public override string ToString()
    {
        string q = Nullable ? "?" : "";
        if (Kind == LubTypeKind.List) return "List<" + Elem!.ToString() + ">" + q;
        if (Kind == LubTypeKind.Array) return Elem!.ToString() + "[]" + q;
        if (Kind == LubTypeKind.Func)
        {
            var parts = new List<string>();
            foreach (var p in FuncParams!) parts.Add(p.ToString());
            return "Func<" + string.Join(", ", parts) + ", " + FuncReturn!.ToString() + ">" + q;
        }
        return Name + q;
    }
}

/// <summary>定数の値 (C# 版の object: int / float / bool / string)。</summary>
public enum ValueKind
{
    None,
    Int,
    Float,
    Bool,
    String,
}

public sealed class ConstVal
{
    public ValueKind Kind;
    public int I;
    public float F;
    public bool B;
    public string S = "";

    public ConstVal(ValueKind kind)
    {
        Kind = kind;
    }

    public static ConstVal FromConst(ConstValue? c)
    {
        if (c == null) return new ConstVal(ValueKind.None);
        if (c.Kind == ConstKind.Int)
        {
            var v = new ConstVal(ValueKind.Int);
            v.I = c.I;
            return v;
        }
        if (c.Kind == ConstKind.Float)
        {
            var v = new ConstVal(ValueKind.Float);
            v.F = c.F;
            return v;
        }
        if (c.Kind == ConstKind.Bool)
        {
            var v = new ConstVal(ValueKind.Bool);
            v.B = c.B;
            return v;
        }
        if (c.Kind == ConstKind.String)
        {
            var v = new ConstVal(ValueKind.String);
            v.S = c.S;
            return v;
        }
        return new ConstVal(ValueKind.None);
    }

    /// <summary>C# の値の文字列 (Convert.ToString(v, InvariantCulture) と同じ。float は "R")。</summary>
    public string Text()
    {
        if (Kind == ValueKind.Int) return I.ToString();
        if (Kind == ValueKind.Float) return F.ToString("R");
        if (Kind == ValueKind.Bool) return B ? "True" : "False";
        return S;
    }

    public bool Same(ConstVal o)
    {
        if (Kind != o.Kind) return false;
        if (Kind == ValueKind.Int) return I == o.I;
        if (Kind == ValueKind.Float) return F == o.F;
        if (Kind == ValueKind.Bool) return B == o.B;
        return S == o.S;
    }
}

/// <summary>Optional は省略できる引数。
/// Default は null 以外の既定値 (`int code = 0` の 0) で、null を既定にする引数 (`int? id = null`) では None。
/// </summary>
public sealed class ApiParam
{
    public string Name;
    public string LuaName;
    public TypeRef Type;
    public bool Optional;
    public bool IsOut;
    // [LubArray(n)] の n (無ければ -1)
    public int ArrayLen;
    public ConstVal Default;
    // [LubCount]: 直前の配列の引数から使う要素の数 (C と .NET は配列と一緒に渡し、tcs VM と tcs2c の host の関数は別の引数で受ける)
    public bool IsCount;

    public ApiParam(string name, string luaName, TypeRef type, bool optional, bool isOut, int arrayLen, ConstVal dflt)
    {
        Name = name;
        LuaName = luaName;
        Type = type;
        Optional = optional;
        IsOut = isOut;
        ArrayLen = arrayLen;
        Default = dflt;
    }

    public bool HasDefault => Default.Kind != ValueKind.None;

    /// <summary>既定値の C# / C の literal (int / float / bool だけ。他は lub-gen check が止める)。無ければ null。
    /// </summary>
    public string? DefaultLiteral
    {
        get
        {
            if (Default.Kind == ValueKind.Bool) return Default.B ? "true" : "false";
            if (Default.Kind == ValueKind.Int) return Default.I.ToString();
            if (Default.Kind == ValueKind.Float) return Default.F.ToString("R") + "f";
            return null;
        }
    }
}

public sealed class ApiFunction
{
    public string Name;
    public string LuaName;
    public TypeRef Return;
    public List<ApiParam> Params;
    public string Doc;
    public bool NoFail;
    public bool Maybe;
    public bool NoC;

    public ApiFunction(string name, string luaName, TypeRef ret, List<ApiParam> ps, string doc, bool noFail, bool maybe, bool noC)
    {
        Name = name;
        LuaName = luaName;
        Return = ret;
        Params = ps;
        Doc = doc;
        NoFail = noFail;
        Maybe = maybe;
        NoC = noC;
    }
}

public sealed class ApiEnumMember
{
    public string Name;
    public string LuaName;
    public int Value;
    public string Doc;

    public ApiEnumMember(string name, string luaName, int value, string doc)
    {
        Name = name;
        LuaName = luaName;
        Value = value;
        Doc = doc;
    }
}

public sealed class ApiEnum
{
    public string Name;
    public string Namespace;
    public string LuaPath;
    public List<ApiEnumMember> Members;
    public string Doc;
    public int Line;

    public ApiEnum(string name, string ns, string luaPath, List<ApiEnumMember> members, string doc, int line)
    {
        Name = name;
        Namespace = ns;
        LuaPath = luaPath;
        Members = members;
        Doc = doc;
        Line = line;
    }
}

public sealed class ApiField
{
    public string Name;
    public string LuaName;
    public TypeRef Type;
    public bool Optional;
    public string Doc;
    // [LubArray(n)] の n (無ければ -1)
    public int ArrayLen;
    public bool Bits;
    // struct の record の property の書き方 ("init" / "set")。field なら ""
    public string Accessor = "";
    // struct の record の readonly の field (readonly struct の field)
    public bool Readonly;

    public ApiField(string name, string luaName, TypeRef type, bool optional, string doc, int arrayLen, bool bits)
    {
        Name = name;
        LuaName = luaName;
        Type = type;
        Optional = optional;
        Doc = doc;
        ArrayLen = arrayLen;
        Bits = bits;
    }
}

public sealed class ApiConst
{
    public string Name;
    public string LuaName;
    public ConstVal Value;
    public string Doc;

    public ApiConst(string name, string luaName, ConstVal value, string doc)
    {
        Name = name;
        LuaName = luaName;
        Value = value;
        Doc = doc;
    }
}

/// <summary>Line は stub での宣言行 (1 始まり)。
/// API docs の source link に使う。
/// </summary>
public sealed class ApiNamespace
{
    public string Name;
    public string LuaPath;
    public List<ApiFunction> Functions;
    public List<ApiEnum> Enums;
    public List<ApiField> StaticFields;
    public List<ApiConst> Consts;
    public string Doc;
    public int Line;

    public ApiNamespace(string name, string luaPath, List<ApiFunction> functions, List<ApiEnum> enums,
        List<ApiField> staticFields, List<ApiConst> consts, string doc, int line)
    {
        Name = name;
        LuaPath = luaPath;
        Functions = functions;
        Enums = enums;
        StaticFields = staticFields;
        Consts = consts;
        Doc = doc;
        Line = line;
    }

    /// <summary>host の名前の根 (lub / lubx)。</summary>
    public string LuaRoot => LuaPath.Split('.')[0];
}

/// <summary>Kind: handle (runtime 所有の不透明参照) / view (frame 有効のバイト列) / keyed (key で参照する resource) / record (option table や戻り値の平らな構造) / struct (値で渡る record)。
/// </summary>
public sealed class ApiType
{
    public string Name;
    public string Kind;
    public List<ApiField> Fields;
    public List<ApiFunction> Methods;
    public string Doc;
    public int Line;
    // struct の record の宣言の形 ("struct" / "record struct" / "readonly record struct" など。facade が同じ形で書く)
    public string Declaration = "";
    // positional な record の parameter (struct の record では lub-gen check が断る)
    public List<string> PositionalParams = new List<string>();

    public ApiType(string name, string kind, List<ApiField> fields, List<ApiFunction> methods, string doc, int line)
    {
        Name = name;
        Kind = kind;
        Fields = fields;
        Methods = methods;
        Doc = doc;
        Line = line;
    }
}

public sealed class ApiModel
{
    public List<ApiNamespace> Namespaces;
    public List<ApiType> Types;

    public ApiModel(List<ApiNamespace> namespaces, List<ApiType> types)
    {
        Namespaces = namespaces;
        Types = types;
    }

    public ApiType? FindType(string name)
    {
        foreach (var t in Types)
        {
            if (t.Name == name) return t;
        }
        return null;
    }

    public ApiEnum? FindEnum(string qualified)
    {
        foreach (var n in Namespaces)
        {
            foreach (var e in n.Enums)
            {
                if (e.Namespace + "." + e.Name == qualified) return e;
            }
        }
        return null;
    }
}

/// <summary>誤り (C# 版の InvalidOperationException)。最初のものを記録し、呼ぶ側が見て止める。</summary>
public static class GenError
{
    public static string Message = "";

    public static bool Failed => Message != "";

    public static void Report(string message)
    {
        if (Message == "") Message = message;
    }
}

public static class ApiModelLoader
{
    /// <summary>stub の型が全部入る C# の namespace。
    /// namespace 直下の enum (EventKind) はパス `lub` の root の段に置く。
    /// </summary>
    public const string RootNamespace = "Lub";

    /// <summary>lubx が使う C の実装の面 (core API ではない)。
    /// static class だけを持ち、型と enum は Lub のものを使う。
    /// </summary>
    public const string LubxNamespace = "Lubx";

    private const string RootDoc = "lub の runtime API。ゲームは `using Lub;` で `Gfx.BeginPass(...)` と書く。";

    private static List<string> _lines = new List<string>();

    /// <summary>stub を tcs の compiler で宣言まで読み (--ref)、API 面を作る。errors に C# の誤り。</summary>
    public static ApiModel Load(string stubPath, string text, List<string> errors)
    {
        _lines = Text.Lines(text);
        var session = new Session("");
        session.KeepDeclarations = true;
        session.Add(stubPath, text, 1);
        var result = session.Compile();
        foreach (var d in result.Diagnostics)
        {
            if (!d.Contains(": warning ") && !d.StartsWith("warning ")) errors.Add(d);
        }
        var c = session.Declarations;
        var namespaces = new List<ApiNamespace>();
        var types = new List<ApiType>();
        if (c == null) return new ApiModel(namespaces, types);

        var members = TopLevel(c, RootNamespace);
        var rootEnums = new List<ApiEnum>();
        foreach (var t in members)
        {
            if (t.IsEnum) rootEnums.Add(LoadEnum(t, RootNamespace));
        }
        namespaces.Add(new ApiNamespace(RootNamespace, "lub", new List<ApiFunction>(), rootEnums,
            new List<ApiField>(), new List<ApiConst>(), RootDoc, 0));
        foreach (var t in members)
        {
            if (IsClass(t) && t.IsStatic) namespaces.Add(LoadNamespace(t));
        }
        foreach (var t in TopLevel(c, LubxNamespace))
        {
            bool known = false;
            foreach (var n in namespaces)
            {
                if (n.Name == t.Name) known = true;
            }
            if (!IsClass(t) || !t.IsStatic || t.Nested.Count > 0)
                errors.Add(t.Name + ": namespace " + LubxNamespace + " holds only static classes without nested types");
            else if (known)
                errors.Add(t.Name + ": the name is already used in namespace " + RootNamespace);
            else
                namespaces.Add(LoadNamespace(t));
        }
        foreach (var t in members)
        {
            if ((IsClass(t) && !t.IsStatic && !t.Name.EndsWith("Attribute")) || t.IsStruct) types.Add(LoadType(t));
        }
        return new ApiModel(namespaces, types);
    }

    // namespace の直下の型 (宣言の順)
    private static List<TypeDeclSym> TopLevel(Compilation c, string ns)
    {
        var r = new List<TypeDeclSym>();
        foreach (var t in c.Types)
        {
            if (t.Outer == null && t.Namespace == ns && t.IsRef) r.Add(t);
        }
        r.Sort((a, b) => a.Decl.Pos - b.Decl.Pos);
        return r;
    }

    private static bool IsClass(TypeDeclSym t) => t.DeclKind == DeclKinds.Class || t.DeclKind == DeclKinds.RecordClass;

    private static bool IsPublic(Node? decl) => decl != null && decl.Has(Mods.Public);

    private static ApiNamespace LoadNamespace(TypeDeclSym cls)
    {
        var luaPath = IlExporter.RefTypePath(cls);
        var functions = new List<ApiFunction>();
        var fields = new List<ApiField>();
        var consts = new List<ApiConst>();
        foreach (var m in cls.Members)
        {
            if (m.Kind == SymKind.Method && m.IsStatic && IsPublic(m.Decl) && !m.Name.StartsWith("op_")) functions.Add(LoadFunction(m));
        }
        var enums = new List<ApiEnum>();
        var nested = new List<TypeDeclSym>();
        foreach (var t in cls.Nested) nested.Add(t);
        nested.Sort((a, b) => a.Decl.Pos - b.Decl.Pos);
        foreach (var t in nested)
        {
            if (t.IsEnum) enums.Add(LoadEnum(t, cls.Name));
        }
        foreach (var m in cls.Members)
        {
            if (m.Kind == SymKind.Field && m.IsStatic && !m.IsConst && IsPublic(FieldDecl(m))) fields.Add(LoadField(m));
        }
        foreach (var m in cls.Members)
        {
            if (m.Kind == SymKind.Field && m.IsConst && IsPublic(FieldDecl(m)))
            {
                var value = m.ConstVal;
                if (value == null && m.Decl != null && m.Decl.A != null && m.Decl.A.HasConst) value = m.Decl.A.Const;
                consts.Add(new ApiConst(m.Name, LuaNames.HostConst(m.Name), ConstVal.FromConst(value), Doc(FieldDecl(m)!)));
            }
        }
        return new ApiNamespace(cls.Name, luaPath, functions, enums, fields, consts, Doc(cls.Decl), Line(cls.Decl));
    }

    // field の symbol の宣言 (Symbol.Decl は宣言子なので、その外側の field の宣言)
    private static Node? FieldDecl(Symbol f)
    {
        var d = f.Decl;
        if (d == null) return null;
        if (d.Kind == NodeKind.Field) return d;
        var owner = f.Owner!.Decl;
        if (owner.List == null) return null;
        foreach (var n in owner.List)
        {
            if (n.Kind != NodeKind.Field || n.List == null) continue;
            foreach (var v in n.List)
            {
                if (v == d) return n;
            }
        }
        return null;
    }

    private static ApiEnum LoadEnum(TypeDeclSym e, string ns)
    {
        var members = new List<ApiEnumMember>();
        foreach (var m in e.Members)
        {
            if (m.Kind != SymKind.EnumMember) continue;
            int v = m.ConstVal == null ? 0 : m.ConstVal.I;
            members.Add(new ApiEnumMember(m.Name, LuaNames.HostConst(m.Name), v, Doc(m.Decl!)));
        }
        return new ApiEnum(e.Name, ns, IlExporter.RefTypePath(e), members, Doc(e.Decl), Line(e.Decl));
    }

    private static ApiType LoadType(TypeDeclSym t)
    {
        var tk = TypeKindOf(t);
        string kind = "record";
        if (tk == LubTypeKind.Handle) kind = "handle";
        else if (tk == LubTypeKind.View) kind = "view";
        else if (tk == LubTypeKind.Keyed) kind = "keyed";
        else if (tk == LubTypeKind.Native) kind = "native";
        if (t.IsStruct) kind = "struct";
        var fields = new List<ApiField>();
        var methods = new List<ApiFunction>();
        foreach (var m in t.Members)
        {
            if (m.Kind == SymKind.Field && !m.IsStatic && IsPublic(FieldDecl(m)))
            {
                var field = LoadField(m);
                field.Readonly = t.IsStruct && m.IsReadonly;
                fields.Add(field);
            }
            // struct の record の auto property (readonly record struct の { get; init; }) は field と同じく値の並びに入る
            if (t.IsStruct && m.Kind == SymKind.Property && !m.IsStatic && IsPublic(m.Decl)) fields.Add(LoadProperty(m));
        }
        foreach (var m in t.Members)
        {
            if (m.Kind == SymKind.Method && !m.IsStatic && IsPublic(m.Decl) && !m.Name.StartsWith("op_")) methods.Add(LoadFunction(m));
        }
        // 継承は TinyC# の外 (stub も同じ)。共通の field は各 class に書き、受け渡す部分は field に持つ
        if (t.BaseClass != null) GenError.Report(t.Name + ": a stub class must not have a base class (" + t.BaseClass.Name + ")");
        var type = new ApiType(t.Name, kind, fields, methods, Doc(t.Decl), Line(t.Decl));
        if (t.IsStruct)
        {
            type.Declaration = (t.Decl.Has(Mods.Readonly) ? "readonly " : "") + (t.IsRecord ? "record struct" : "struct");
            if (t.RecordParams != null)
            {
                foreach (var p in t.RecordParams) type.PositionalParams.Add(p.Name);
            }
        }
        return type;
    }

    private static ApiFunction LoadFunction(Symbol m)
    {
        var ps = new List<ApiParam>();
        var decl = m.Decl!;
        if (m.Params != null)
        {
            for (int i = 0; i < m.Params.Count; i++)
            {
                var p = m.Params[i];
                Node? pn = decl.List != null && i < decl.List.Count ? decl.List[i] : null;
                bool optional = p.Default != null;
                var dflt = new ConstVal(ValueKind.None);
                if (p.Default != null && p.Default.HasConst) dflt = ConstVal.FromConst(p.Default.Const);
                var ap = new ApiParam(p.Name, LuaNames.HostMember(p.Name), Resolve(p.Type), optional, p.IsOut,
                    pn == null ? -1 : ArrayLen(pn), dflt);
                ap.IsCount = pn != null && HasAttr(pn, "LubCount");
                ps.Add(ap);
            }
        }
        return new ApiFunction(m.Name, LuaNames.HostMember(m.Name), Resolve(m.Type), ps, Doc(decl),
            HasAttr(decl, "LubNoFail"), HasAttr(decl, "LubMaybe"), HasAttr(decl, "LubNoC"));
    }

    private static ApiField LoadProperty(Symbol p)
    {
        var decl = p.Decl!;
        var field = new ApiField(p.Name, LuaNames.HostMember(p.Name), Resolve(p.Type), p.Type.Annotated || p.Type.Kind == TypeKind.Nullable,
            Doc(decl), ArrayLen(decl), HasAttr(decl, "LubBits"));
        field.Accessor = p.IsInit ? "init" : p.HasSetter ? "set" : "get";
        return field;
    }

    private static ApiField LoadField(Symbol f)
    {
        var decl = FieldDecl(f)!;
        bool optional = f.Type.Annotated || f.Type.Kind == TypeKind.Nullable;
        return new ApiField(f.Name, LuaNames.HostMember(f.Name), Resolve(f.Type), optional, Doc(decl), ArrayLen(decl),
            HasAttr(decl, "LubBits"));
    }

    private static int Line(Node n) => n.File.LineOf(n.Pos);

    private static bool SameAttr(string written, string name) =>
        written == name || written == name + "Attribute" || written.EndsWith("." + name) || written.EndsWith("." + name + "Attribute");

    private static bool HasAttr(Node n, string name)
    {
        if (n.D == null || n.D.Kind != NodeKind.Attribute || n.D.List == null) return false;
        foreach (var a in n.D.List)
        {
            if (SameAttr(a.Text, name)) return true;
        }
        return false;
    }

    private static int ArrayLen(Node n)
    {
        if (n.D == null || n.D.Kind != NodeKind.Attribute || n.D.List == null) return -1;
        foreach (var a in n.D.List)
        {
            if (!SameAttr(a.Text, "LubArray") || a.List == null || a.List.Count == 0) continue;
            var arg = a.List[0];
            if (arg.HasConst && arg.Const!.Kind == ConstKind.Int) return arg.Const.I;
            return Text.ParseInt(arg.Text);
        }
        return -1;
    }

    private static LubTypeKind TypeKindOf(TypeDeclSym t)
    {
        if (HasAttr(t.Decl, "LubHandle")) return LubTypeKind.Handle;
        if (HasAttr(t.Decl, "LubView")) return LubTypeKind.View;
        if (HasAttr(t.Decl, "LubKeyed")) return LubTypeKind.Keyed;
        if (HasAttr(t.Decl, "LubNative")) return LubTypeKind.Native;
        return LubTypeKind.Record;
    }

    public static TypeRef Resolve(TypeSym t)
    {
        bool nullable = t.Annotated;
        switch (t.Kind)
        {
            case TypeKind.Void:
                return TypeRef.VoidType;
            case TypeKind.Int:
                return new TypeRef(LubTypeKind.Int, "int", nullable, null, null, null);
            case TypeKind.Float:
                return new TypeRef(LubTypeKind.Double, "float", nullable, null, null, null);
            case TypeKind.Bool:
                return new TypeRef(LubTypeKind.Bool, "bool", nullable, null, null, null);
            case TypeKind.String:
                return new TypeRef(LubTypeKind.String, "string", nullable, null, null, null);
            case TypeKind.Array:
                return new TypeRef(LubTypeKind.Array, "array", nullable, Resolve(t.Args![0]), null, null);
            case TypeKind.Nullable:
                return Resolve(t.Args![0].WithoutAnnotation()).AsNullable();
            case TypeKind.List:
                return new TypeRef(LubTypeKind.List, "List", nullable, Resolve(t.Args![0]), null, null);
            case TypeKind.Delegate:
            {
                var ps = new List<TypeRef>();
                if (t.Args != null)
                {
                    foreach (var a in t.Args) ps.Add(Resolve(a));
                }
                return new TypeRef(LubTypeKind.Func, "Func", nullable, null, ps, Resolve(t.Ret!));
            }
            case TypeKind.Enum:
            {
                var d = t.Decl!;
                string ns = d.Outer != null ? d.Outer.Name : RootNamespace;
                return new TypeRef(LubTypeKind.Enum, ns + "." + d.Name, nullable, null, null, null);
            }
            case TypeKind.Class:
                return new TypeRef(TypeKindOf(t.Decl!), t.Decl!.Name, nullable, null, null, null);
            case TypeKind.Struct:
                return new TypeRef(LubTypeKind.Struct, t.Decl!.Name, nullable, null, null, null);
        }
        GenError.Report("unsupported stub type: " + t.Name);
        return TypeRef.VoidType;
    }

    // ---- doc comment (宣言の前の `///` の行の <summary>) ----

    private static string Doc(Node decl)
    {
        // 宣言が行の始まりに無ければ (同じ行の前に別の token がある enum の member など) doc は無い
        int at = decl.File.LineOf(decl.Pos);
        string before = Text.Trim(Text.Sub(_lines[at - 1], 0, decl.File.ColumnOf(decl.Pos) - 1));
        if (before != "" && !before.StartsWith("[")) return "";
        // 宣言の行から上へ、属性の行を飛ばし、続く `///` の行を集める
        int line = at - 2;
        // 空行も飛ばす (C# の compiler も、空行を挟んだ doc comment を宣言に付ける)
        while (line >= 0 && (Text.Trim(_lines[line]).StartsWith("[") || Text.Trim(_lines[line]) == "")) line--;
        var docs = new List<string>();
        while (line >= 0)
        {
            string l = Text.TrimStart(_lines[line]);
            if (!l.StartsWith("///")) break;
            docs.Insert(0, Text.From(l, 3));
            line--;
        }
        if (docs.Count == 0) return "";
        string xml = string.Join("\n", docs);
        // 整形式でない XML (閉じない tag など) の doc は C# の compiler と同じく無いものとする
        if (!Text.WellFormedXml(xml)) return "";
        string body = Text.Between(xml, "<summary>", "</summary>");
        if (body == "") return "";
        body = Text.StripTags(body);
        body = body.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        // 行の継ぎ目は空白 1 つに畳む。
        // CJK 同士の継ぎ目には空白を入れない。
        var parts = new List<string>();
        int last = 0;
        foreach (var raw in body.Split('\n'))
        {
            string l = Text.Trim(raw);
            if (l == "") continue;
            if (parts.Count > 0 && !(Text.IsWide(last) && Text.IsWide(Text.FirstRune(l)))) parts.Add(" ");
            parts.Add(l);
            last = Text.LastRune(l);
        }
        return Text.Trim(Text.CollapseSpaces(string.Join("", parts)));
    }
}
