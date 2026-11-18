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
    // User type (has Decl)
    Class,
    Struct,
    Interface,
    Enum,
    // Type argument (Index is the declaration order; a method type argument if IsMethod)
    TypeParam,
    // Built-in generic type (has Args)
    List,
    Dict,
    HashSet,
    Queue,
    KeyValuePair,
    // Dictionary's Keys / Values (Args = K, V). The runtime returns a copy of the elements
    KeyCollection,
    ValueCollection,
    Array,
    Nullable,
    // Delegate (Action / Func / Comparison / Predicate). Args = parameter types, Ret = return type
    Delegate,
    Tuple,
    Utf8Bytes,
    Rune,
    Random,
    // Untyped lambda and method group (a provisional type until the target is known)
    Lambda,
    MethodGroup,
    // A type reference that is not a value, such as a static class name
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
    // A type argument with where T : struct (T? is Nullable<T>)
    public bool IsStructParam;
    // Tuple element names
    public List<string>? Names;
    // Nullable annotation of a reference type (`string?`). It does not change meaning and appears only in the type notation for the C backend (IlTypes.Display)
    public bool Annotated;
    // A type without a nullable context (type syntax inserted by monomorphization). Does not annotate var locals
    public bool Oblivious;

    public TypeSym(TypeKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    // The type with the `?` annotation removed
    public TypeSym WithoutAnnotation()
    {
        if (!Annotated) return this;
        var t = WithAnnotation();
        t.Annotated = false;
        return t;
    }

    // A copy with the `?` annotation added
    public TypeSym WithAnnotation()
    {
        var t = new TypeSym(Kind, Name);
        t.Decl = Decl;
        t.Args = Args;
        t.Ret = Ret;
        t.Index = Index;
        t.IsMethodParam = IsMethodParam;
        t.IsStructParam = IsStructParam;
        t.Names = Names;
        t.Oblivious = Oblivious;
        t.Annotated = true;
        return t;
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
    // sbyte and ushort have the same Kind as short, and the name tells the width and sign apart (IntegerBits, IsUnsignedInteger)
    public static readonly TypeSym SByteType = new TypeSym(TypeKind.Short, "sbyte");
    public static readonly TypeSym UShortType = new TypeSym(TypeKind.Short, "ushort");
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

    // BCL types that exist in .NET but not in the subset (treated as error types; member references are reported as TCS1002)
    public static TypeSym Bcl(string metadataName) => new TypeSym(TypeKind.Error, "bcl:" + metadataName);

    public bool IsBcl => Kind == TypeKind.Error && Name.StartsWith("bcl:");

    public string BclKey => Strings.From(Name, 4);

    public bool IsNumeric => Kind == TypeKind.Int || Kind == TypeKind.Float || Kind == TypeKind.Char || Kind == TypeKind.Byte || Kind == TypeKind.UInt || Kind == TypeKind.Short || Kind == TypeKind.Double || Kind == TypeKind.Long;

    // Integer types
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

    // Types that can hold null
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

    // The name written in diagnostics (C# display: BCL and user types are namespace-qualified, built-in types are keywords)
    public string FullDisplay()
    {
        switch (Kind)
        {
            case TypeKind.Nullable: return Elem.FullDisplay() + "?";
            case TypeKind.Array: return Elem.FullDisplay() + "[]";
            case TypeKind.Delegate:
                {
                    var parts = new List<string>();
                    foreach (var a in Args!) parts.Add(a.FullDisplay());
                    if (Ret!.Kind == TypeKind.Void)
                    {
                        if (parts.Count == 0) return "System.Action";
                        return "System.Action<" + string.Join(", ", parts) + ">";
                    }
                    parts.Add(Ret.FullDisplay());
                    return "System.Func<" + string.Join(", ", parts) + ">";
                }
            case TypeKind.Tuple:
                {
                    var parts = new List<string>();
                    foreach (var a in Args!) parts.Add(a.FullDisplay());
                    return "(" + string.Join(", ", parts) + ")";
                }
            case TypeKind.KeyCollection:
            case TypeKind.ValueCollection:
                return "System.Collections.Generic.Dictionary<" + ArgsFullDisplay() + ">." + Name;
        }
        string name = Name;
        switch (Kind)
        {
            case TypeKind.List:
            case TypeKind.Dict:
            case TypeKind.HashSet:
            case TypeKind.Queue:
            case TypeKind.KeyValuePair:
                name = "System.Collections.Generic." + (Kind == TypeKind.Dict ? "Dictionary" : Kind == TypeKind.List ? "List" : Name);
                break;
            case TypeKind.Rune:
                name = "System.Text.Rune";
                break;
            case TypeKind.Utf8Bytes:
                name = "TinySystem.Text.Utf8Bytes";
                break;
        }
        if (Decl != null) name = Decl.FullName;
        if (Args != null && Args.Count > 0) return name + "<" + ArgsFullDisplay() + ">";
        return name;
    }

    private string ArgsFullDisplay()
    {
        var parts = new List<string>();
        if (Args != null)
        {
            foreach (var a in Args) parts.Add(a.FullDisplay());
        }
        return string.Join(", ", parts);
    }

    // The type with type arguments substituted. typeArgs are the type's type arguments, methodArgs the method's
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
    // ref / in / out parameter (distinguishes overloads from value parameters)
    public bool IsByRef;
    public Node? Default;
    public bool IsThis;

    public ParamSym(string name, TypeSym type)
    {
        Name = name;
        Type = type;
    }
}

// Member symbol (field / property / method / ctor / enum member). Built-ins have Builtin
public sealed class Symbol
{
    public SymKind Kind;
    public string Name;
    public TypeDeclSym? Owner;
    // Type of a field / property, return type of a method
    public TypeSym Type;
    public bool IsStatic;
    public List<ParamSym>? Params;
    public Node? Decl;
    // Names of the method's type arguments
    public List<string>? TypeParams;
    // Name in IL (overload-distinguished name for methods, host name for members of --ref types, renamed name for locals)
    public string IlName;
    public bool IsConst;
    public ConstValue? ConstVal;
    // property: which accessors exist, and whether it is auto-implemented without a body
    public bool HasGetter;
    public bool HasSetter;
    public bool IsAuto;
    // init-only setter (can be written in object initializers and with)
    public bool IsInit;
    public bool IsReadonly;
    // ID of a built-in member ("List.Add", etc.). "" for user members
    public string Builtin = "";
    // Owner key of a built-in member (the owner in the Builtins table)
    public string BuiltinOwner = "";
    public bool IsExtension;
    // Constraints of the method's type arguments (same order as TypeParams)
    public List<List<TypeSym>>? TypeParamConstraints;
    // where T : class of the method's type arguments (same order as TypeParams)
    public List<bool>? TypeParamClass;
    // Whether the field initializer has been bound (detects const cycles)
    public int ConstState;
    public TypeDeclSym? TypeRef;

    public Symbol(SymKind kind, string name, TypeSym type)
    {
        Kind = kind;
        Name = name;
        Type = type;
        IlName = name;
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

// User type declaration
public sealed class TypeDeclSym
{
    public string Name;
    // Scope for resolving names inside the type (created by the Declarer)
    public TypeScope? Scope;
    // Name with the namespace and outer types joined by .
    public string FullName;
    public string Namespace;
    // IL type name (A.B.C → A_B_C; the host path for --ref types)
    public string IlName;
    public int DeclKind;
    public Node Decl;
    public TypeDeclSym? Outer;
    public bool IsStatic;
    public bool IsRef;
    // Base class of a --ref type (inheritance of host data classes). User types have no inheritance
    public TypeDeclSym? BaseClass;
    // A --ref attribute declaration (inherits System.Attribute). Used only for name resolution
    public bool IsAttribute;
    public List<Symbol> Members = new List<Symbol>();
    public Dictionary<string, List<Symbol>> ByName = new Dictionary<string, List<Symbol>>();
    public List<TypeSym> Interfaces = new List<TypeSym>();
    public List<string> TypeParams = new List<string>();
    // Type argument constraints (same order as TypeParams)
    public List<List<TypeSym>> TypeParamConstraints = new List<List<TypeSym>>();
    // where T : class of the type arguments (same order as TypeParams)
    public List<bool> TypeParamClass = new List<bool>();
    public List<TypeDeclSym> Nested = new List<TypeDeclSym>();
    // Underlying type of an enum
    public TypeSym? EnumBase;
    public bool IsFlags;
    // Positional parameters of a record
    public List<ParamSym>? RecordParams;
    public int Order;
    public List<Symbol> Ctors = new List<Symbol>();
    public bool MembersDeclared;
    // The struct instance state cannot be modified (all fields are readonly, and there is no auto-implemented property with set). Copies can be skipped
    public bool IsImmutable;

    public TypeDeclSym(string name, string fullName, string ns, int declKind, Node decl)
    {
        Name = name;
        FullName = fullName;
        Namespace = ns;
        IlName = fullName.Replace(".", "_");
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
        // A --ref host data class also has the base members
        if (BaseClass != null) return BaseClass.Lookup(name);
        return null;
    }

    // Whether this type or a base is other (inheritance of --ref host classes)
    public bool DerivesFrom(TypeDeclSym other)
    {
        TypeDeclSym? cur = this;
        while (cur != null)
        {
            if (cur == other) return true;
            cur = cur.BaseClass;
        }
        return false;
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

// Locals and parameters
public sealed class LocalSym
{
    public string Name;
    public TypeSym Type;
    public string IlName;
    public bool IsParam;
    public bool IsOut;
    public bool IsConst;
    public ConstValue? ConstVal;
    // A local declared without an initializer (tracked by definite assignment analysis)
    public bool Tracked;
    // Modified from inside a lambda, or captured
    public bool Captured;
    public int Depth;

    public LocalSym(string name, TypeSym type)
    {
        Name = name;
        Type = type;
        IlName = name;
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

    // A float NaN (only NaN is not equal to itself; TinyC# has no float.IsNaN)
    public bool IsNaN()
    {
        var f = F;
#pragma warning disable CS1718
        return Kind == ConstKind.Float && f != f;
#pragma warning restore CS1718
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
