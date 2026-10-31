using System.Collections.Generic;

namespace TcsSelf;

public enum TypeKind
{
    Error,
    Void,
    Null,
    Int,
    Float,
    Bool,
    String,
    Char,
    Byte,
    UInt,
    Short,
    Object,
    Double,
    Long,
    // user の型 (Decl を持つ)
    Class,
    Struct,
    Interface,
    Enum,
    // 型引数 (Index は宣言の順。IsMethod なら method の型引数)
    TypeParam,
    // 組み込みの generic な型 (Args を持つ)
    List,
    Dict,
    HashSet,
    Queue,
    KeyValuePair,
    // Dictionary の Keys / Values (Args = K, V)。Lua は List の写し
    KeyCollection,
    ValueCollection,
    Array,
    Nullable,
    // delegate (Action / Func / Comparison / Predicate)。Args = parameter の型、Ret = 戻り型
    Delegate,
    Tuple,
    Utf8Bytes,
    Rune,
    Random,
    // 型の付かない lambda と method group (変換先が決まるまでの仮の型)
    Lambda,
    MethodGroup,
    // static class の名前などの、値でない型の参照
    StaticRef,
}

public sealed class TypeSym
{
    public TypeKind Kind;
    public string Name;
    public TypeDeclSym? Decl;
    public List<TypeSym>? Args;
    public TypeSym? Ret;
    public int Index;
    public bool IsMethodParam;
    // where T : struct の型引数 (T? は Nullable<T>)
    public bool IsStructParam;
    // Tuple の要素名
    public List<string>? Names;

    public TypeSym(TypeKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public static readonly TypeSym ErrorType = new TypeSym(TypeKind.Error, "?");
    public static readonly TypeSym VoidType = new TypeSym(TypeKind.Void, "void");
    public static readonly TypeSym NullType = new TypeSym(TypeKind.Null, "null");
    public static readonly TypeSym IntType = new TypeSym(TypeKind.Int, "int");
    public static readonly TypeSym FloatType = new TypeSym(TypeKind.Float, "float");
    public static readonly TypeSym BoolType = new TypeSym(TypeKind.Bool, "bool");
    public static readonly TypeSym StringType = new TypeSym(TypeKind.String, "string");
    public static readonly TypeSym CharType = new TypeSym(TypeKind.Char, "char");
    public static readonly TypeSym ByteType = new TypeSym(TypeKind.Byte, "byte");
    public static readonly TypeSym UIntType = new TypeSym(TypeKind.UInt, "uint");
    public static readonly TypeSym ShortType = new TypeSym(TypeKind.Short, "short");
    public static readonly TypeSym ObjectType = new TypeSym(TypeKind.Object, "object");
    public static readonly TypeSym DoubleType = new TypeSym(TypeKind.Double, "double");
    public static readonly TypeSym LongType = new TypeSym(TypeKind.Long, "long");
    public static readonly TypeSym Utf8BytesType = new TypeSym(TypeKind.Utf8Bytes, "Utf8Bytes");
    public static readonly TypeSym RuneType = new TypeSym(TypeKind.Rune, "Rune");
    public static readonly TypeSym RandomType = new TypeSym(TypeKind.Random, "Random");
    public static readonly TypeSym LambdaType = new TypeSym(TypeKind.Lambda, "lambda");
    public static readonly TypeSym MethodGroupType = new TypeSym(TypeKind.MethodGroup, "method group");

    public static TypeSym Generic(TypeKind kind, string name, TypeSym a)
    {
        var t = new TypeSym(kind, name);
        t.Args = new List<TypeSym>();
        t.Args.Add(a);
        return t;
    }

    public static TypeSym Generic2(TypeKind kind, string name, TypeSym a, TypeSym b)
    {
        var t = new TypeSym(kind, name);
        t.Args = new List<TypeSym>();
        t.Args.Add(a);
        t.Args.Add(b);
        return t;
    }

    public static TypeSym ListOf(TypeSym e) => Generic(TypeKind.List, "List", e);

    public static TypeSym ArrayOf(TypeSym e) => Generic(TypeKind.Array, "Array", e);

    public static TypeSym NullableOf(TypeSym e) => Generic(TypeKind.Nullable, "Nullable", e);

    public static TypeSym DictOf(TypeSym k, TypeSym v) => Generic2(TypeKind.Dict, "Dictionary", k, v);

    public static TypeSym Delegate(List<TypeSym> parameters, TypeSym ret)
    {
        var t = new TypeSym(TypeKind.Delegate, "delegate");
        t.Args = parameters;
        t.Ret = ret;
        return t;
    }

    public static TypeSym OfDecl(TypeDeclSym decl, List<TypeSym>? args)
    {
        TypeKind kind = TypeKind.Class;
        if (decl.DeclKind == DeclKinds.Struct || decl.DeclKind == DeclKinds.RecordStruct) kind = TypeKind.Struct;
        else if (decl.DeclKind == DeclKinds.Interface) kind = TypeKind.Interface;
        else if (decl.DeclKind == DeclKinds.Enum) kind = TypeKind.Enum;
        var t = new TypeSym(kind, decl.Name);
        t.Decl = decl;
        t.Args = args;
        return t;
    }

    public TypeSym Arg(int i) => Args![i];

    public TypeSym Elem => Args![0];

    public bool IsError => Kind == TypeKind.Error;

    public bool IsNumeric => Kind == TypeKind.Int || Kind == TypeKind.Float || Kind == TypeKind.Char || Kind == TypeKind.Byte || Kind == TypeKind.UInt || Kind == TypeKind.Short || Kind == TypeKind.Double || Kind == TypeKind.Long;

    // Lua で整数の値
    public bool IsIntegral => Kind == TypeKind.Int || Kind == TypeKind.Char || Kind == TypeKind.Byte || Kind == TypeKind.UInt || Kind == TypeKind.Short || Kind == TypeKind.Long;

    public bool IsValueType
    {
        get
        {
            switch (Kind)
            {
                case TypeKind.Int:
                case TypeKind.Float:
                case TypeKind.Bool:
                case TypeKind.Char:
                case TypeKind.Byte:
                case TypeKind.UInt:
                case TypeKind.Short:
                case TypeKind.Double:
                case TypeKind.Long:
                case TypeKind.Struct:
                case TypeKind.Enum:
                case TypeKind.Nullable:
                case TypeKind.KeyValuePair:
                case TypeKind.Tuple:
                case TypeKind.Utf8Bytes:
                case TypeKind.Rune:
                    return true;
            }
            return false;
        }
    }

    // null を値に持てる型
    public bool IsNullable => !IsValueType || Kind == TypeKind.Nullable;

    public bool IsReferenceLike => !IsValueType && Kind != TypeKind.TypeParam;

    public static bool Same(TypeSym a, TypeSym b)
    {
        if (a == b) return true;
        if (a.Kind != b.Kind) return false;
        switch (a.Kind)
        {
            case TypeKind.Class:
            case TypeKind.Struct:
            case TypeKind.Interface:
            case TypeKind.Enum:
                if (a.Decl != b.Decl) return false;
                return SameArgs(a.Args, b.Args);
            case TypeKind.TypeParam:
                return a.Index == b.Index && a.IsMethodParam == b.IsMethodParam && a.Name == b.Name;
            case TypeKind.Delegate:
                if (!SameArgs(a.Args, b.Args)) return false;
                return Same(a.Ret!, b.Ret!);
            case TypeKind.Error:
                return true;
        }
        return SameArgs(a.Args, b.Args);
    }

    private static bool SameArgs(List<TypeSym>? a, List<TypeSym>? b)
    {
        int na = a == null ? 0 : a.Count;
        int nb = b == null ? 0 : b.Count;
        if (na != nb) return false;
        for (int i = 0; i < na; i++)
        {
            if (!Same(a![i], b![i])) return false;
        }
        return true;
    }

    public string Display()
    {
        switch (Kind)
        {
            case TypeKind.Nullable: return Elem.Display() + "?";
            case TypeKind.Array: return Elem.Display() + "[]";
            case TypeKind.Delegate:
                {
                    var parts = new List<string>();
                    foreach (var a in Args!) parts.Add(a.Display());
                    if (Ret!.Kind == TypeKind.Void)
                    {
                        if (parts.Count == 0) return "Action";
                        return "Action<" + string.Join(", ", parts) + ">";
                    }
                    parts.Add(Ret.Display());
                    return "Func<" + string.Join(", ", parts) + ">";
                }
            case TypeKind.Tuple:
                {
                    var parts = new List<string>();
                    foreach (var a in Args!) parts.Add(a.Display());
                    return "(" + string.Join(", ", parts) + ")";
                }
        }
        if (Args != null && Args.Count > 0)
        {
            var parts = new List<string>();
            foreach (var a in Args) parts.Add(a.Display());
            return Name + "<" + string.Join(", ", parts) + ">";
        }
        return Name;
    }

    // 型引数を置き換えた型。typeArgs は型の型引数、methodArgs は method の型引数
    public TypeSym Subst(List<TypeSym>? typeArgs, List<TypeSym>? methodArgs)
    {
        if (Kind == TypeKind.TypeParam)
        {
            if (IsMethodParam)
            {
                if (methodArgs != null && Index < methodArgs.Count) return methodArgs[Index];
                return this;
            }
            if (typeArgs != null && Index < typeArgs.Count) return typeArgs[Index];
            return this;
        }
        if (Args == null && Ret == null) return this;
        var t = new TypeSym(Kind, Name);
        t.Decl = Decl;
        t.Index = Index;
        t.IsMethodParam = IsMethodParam;
        t.IsStructParam = IsStructParam;
        t.Names = Names;
        if (Args != null)
        {
            t.Args = new List<TypeSym>();
            foreach (var a in Args) t.Args.Add(a.Subst(typeArgs, methodArgs));
        }
        if (Ret != null) t.Ret = Ret.Subst(typeArgs, methodArgs);
        return t;
    }

    public bool ContainsTypeParam()
    {
        if (Kind == TypeKind.TypeParam) return true;
        if (Args != null)
        {
            foreach (var a in Args)
            {
                if (a.ContainsTypeParam()) return true;
            }
        }
        if (Ret != null && Ret.ContainsTypeParam()) return true;
        return false;
    }
}

public enum SymKind
{
    Field,
    Property,
    Method,
    Ctor,
    EnumMember,
    Local,
    Param,
    Type,
    Namespace,
}

public sealed class ParamSym
{
    public string Name;
    public TypeSym Type;
    public bool IsOut;
    public Node? Default;
    public bool IsThis;

    public ParamSym(string name, TypeSym type)
    {
        Name = name;
        Type = type;
    }
}

// member の symbol (field / property / method / ctor / enum member)。組み込みは Builtin を持つ
public sealed class Symbol
{
    public SymKind Kind;
    public string Name;
    public TypeDeclSym? Owner;
    // field / property の型、method の戻り型
    public TypeSym Type;
    public bool IsStatic;
    public List<ParamSym>? Params;
    public Node? Decl;
    // method の型引数の名前
    public List<string>? TypeParams;
    // Lua での名前 (method の overload を区別した名前)
    public string LuaName;
    public bool IsConst;
    public ConstValue? ConstVal;
    // property: accessor の有無と、本体の無い自動実装か
    public bool HasGetter;
    public bool HasSetter;
    public bool IsAuto;
    // init だけの setter (object 初期化子と with で書ける)
    public bool IsInit;
    public bool IsReadonly;
    // 組み込みの member の id ("List.Add" など)。user の member は ""
    public string Builtin = "";
    public bool IsExtension;
    // method の型引数の制約 (TypeParams と同じ順)
    public List<List<TypeSym>>? TypeParamConstraints;
    // field の初期化子が束縛済みか (const の循環を検出する)
    public int ConstState;
    public TypeDeclSym? TypeRef;

    public Symbol(SymKind kind, string name, TypeSym type)
    {
        Kind = kind;
        Name = name;
        Type = type;
        LuaName = name;
    }

    public int ParamCount => Params == null ? 0 : Params.Count;

    public int RequiredParamCount
    {
        get
        {
            if (Params == null) return 0;
            int n = 0;
            foreach (var p in Params)
            {
                if (p.Default == null) n++;
            }
            return n;
        }
    }

    public bool HasOut
    {
        get
        {
            if (Params == null) return false;
            foreach (var p in Params)
            {
                if (p.IsOut) return true;
            }
            return false;
        }
    }
}

// user の型の宣言
public sealed class TypeDeclSym
{
    public string Name;
    // namespace と外側の型を . でつないだ名前
    public string FullName;
    public string Namespace;
    // Lua の global 名 (A.B.C → A_B_C)
    public string LuaName;
    public int DeclKind;
    public Node Decl;
    public TypeDeclSym? Outer;
    public bool IsStatic;
    public bool IsRef;
    public List<Symbol> Members = new List<Symbol>();
    public Dictionary<string, List<Symbol>> ByName = new Dictionary<string, List<Symbol>>();
    public List<TypeSym> Interfaces = new List<TypeSym>();
    public List<string> TypeParams = new List<string>();
    // 型引数の制約 (TypeParams と同じ順)
    public List<List<TypeSym>> TypeParamConstraints = new List<List<TypeSym>>();
    public List<TypeDeclSym> Nested = new List<TypeDeclSym>();
    // enum の基底型
    public TypeSym? EnumBase;
    public bool IsFlags;
    // record の positional parameter
    public List<ParamSym>? RecordParams;
    public int Order;
    public List<Symbol> Ctors = new List<Symbol>();
    public bool MembersDeclared;
    // struct の instance の状態を書き換えられない (全ての field が readonly で、set のある自動実装の property が無い)。copy を省ける
    public bool IsImmutable;

    public TypeDeclSym(string name, string fullName, string ns, int declKind, Node decl)
    {
        Name = name;
        FullName = fullName;
        Namespace = ns;
        LuaName = fullName.Replace(".", "_");
        DeclKind = declKind;
        Decl = decl;
    }

    public bool IsRecord => DeclKind == DeclKinds.RecordClass || DeclKind == DeclKinds.RecordStruct;

    public bool IsStruct => DeclKind == DeclKinds.Struct || DeclKind == DeclKinds.RecordStruct;

    public bool IsInterface => DeclKind == DeclKinds.Interface;

    public bool IsEnum => DeclKind == DeclKinds.Enum;

    public void AddMember(Symbol s)
    {
        Members.Add(s);
        if (!ByName.TryGetValue(s.Name, out var list))
        {
            list = new List<Symbol>();
            ByName[s.Name] = list;
        }
        list.Add(s);
    }

    public List<Symbol>? Lookup(string name)
    {
        if (ByName.TryGetValue(name, out var list)) return list;
        return null;
    }

    public TypeSym SelfType()
    {
        List<TypeSym>? args = null;
        if (TypeParams.Count > 0)
        {
            args = new List<TypeSym>();
            for (int i = 0; i < TypeParams.Count; i++)
            {
                var tp = new TypeSym(TypeKind.TypeParam, TypeParams[i]);
                tp.Index = i;
                args.Add(tp);
            }
        }
        return TypeSym.OfDecl(this, args);
    }

    public bool Implements(TypeDeclSym iface)
    {
        foreach (var i in Interfaces)
        {
            if (i.Decl == iface) return true;
            if (i.Decl != null && i.Decl.Implements(iface)) return true;
        }
        return false;
    }
}

// 局所変数と parameter
public sealed class LocalSym
{
    public string Name;
    public TypeSym Type;
    public string LuaName;
    public bool IsParam;
    public bool IsOut;
    public bool IsConst;
    public ConstValue? ConstVal;
    // lambda の中から書き換えられる、または capture される
    public bool Captured;
    public int Depth;

    public LocalSym(string name, TypeSym type)
    {
        Name = name;
        Type = type;
        LuaName = name;
    }
}

public enum ConstKind
{
    Int,
    Float,
    Bool,
    String,
    Null,
}

public sealed class ConstValue
{
    public ConstKind Kind;
    public int I;
    public float F;
    public bool B;
    public string S = "";

    public ConstValue(ConstKind kind)
    {
        Kind = kind;
    }

    public static ConstValue OfInt(int v)
    {
        var c = new ConstValue(ConstKind.Int);
        c.I = v;
        return c;
    }

    public static ConstValue OfFloat(float v)
    {
        var c = new ConstValue(ConstKind.Float);
        c.F = v;
        return c;
    }

    public static ConstValue OfBool(bool v)
    {
        var c = new ConstValue(ConstKind.Bool);
        c.B = v;
        return c;
    }

    public static ConstValue OfString(string v)
    {
        var c = new ConstValue(ConstKind.String);
        c.S = v;
        return c;
    }
}
