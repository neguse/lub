using System.Collections.Generic;
using TinySystem.Text;

namespace Tcs;

// Rules for IL names (names of user-type methods, constructors, operators, and parameters). Host type names are in HostNames in Declarer.cs
public static class IlNames
{
    // The name of a user-type method. Only when the same type has several methods with the same name
    // are the parameter types appended, joined with `__`. A method implementing an interface method uses the interface method's name
    public static string MethodName(Symbol m)
    {
        if (m.Kind != SymKind.Method) return m.Name;
        if (m.Owner != null && m.Owner.IsRef) return m.IlName;
        var implemented = ImplementedInterfaceMethod(m);
        // Methods of a --ref interface use the C# name (not the host name; tcs check stops at RefInterfaceImplementation, and the tcs VM and C call by the C# name)
        if (implemented != null) return implemented.Owner != null && implemented.Owner.IsRef ? implemented.Name : MethodName(implemented);
        var owner = m.Owner;
        if (owner == null) return m.Name;
        int same = 0;
        foreach (var x in owner.Members)
        {
            if (x.Kind == SymKind.Method && x.Name == m.Name) same++;
        }
        if (same < 2) return m.Name;
        var parts = new List<string>();
        parts.Add(m.Name);
        if (m.Params != null)
        {
            foreach (var p in m.Params)
            {
                parts.Add("__");
                if (p.IsOut) parts.Add("out_");
                parts.Add(TypeToken(p.Type));
            }
        }
        return string.Join("", parts);
    }

    // The interface method that an instance method implements (from the interfaces of the declaring type)
    private static Symbol? ImplementedInterfaceMethod(Symbol m)
    {
        if (m.IsStatic || m.Owner == null || m.Owner.IsInterface) return null;
        var ifaces = new List<TypeSym>();
        CollectAllInterfaces(m.Owner, ifaces);
        foreach (var iface in ifaces)
        {
            var decl = iface.Decl!;
            foreach (var cand in decl.Members)
            {
                if (cand.Kind != SymKind.Method || cand.Name != m.Name) continue;
                if (SameSignature(m, cand, iface)) return cand;
            }
        }
        return null;
    }

    private static void CollectAllInterfaces(TypeDeclSym t, List<TypeSym> into)
    {
        foreach (var i in t.Interfaces)
        {
            if (i.Decl == null) continue;
            bool seen = false;
            foreach (var x in into)
            {
                if (x.Decl == i.Decl) seen = true;
            }
            if (seen) continue;
            into.Add(i);
            CollectAllInterfaces(i.Decl, into);
        }
    }

    private static bool SameSignature(Symbol impl, Symbol iface, TypeSym ifaceType)
    {
        if (impl.ParamCount != iface.ParamCount) return false;
        for (int i = 0; i < impl.ParamCount; i++)
        {
            var it = iface.Params![i].Type.Subst(ifaceType.Args, null);
            if (!TypeSym.Same(impl.Params![i].Type, it)) return false;
        }
        return true;
    }

    // The name of a user-type constructor. `new` for a class, `ctor` for a struct. For a type with several constructors,
    // those with parameters get the parameter types appended by the same rule as method overloads (`new__int__float`)
    public static string CtorName(Symbol ctor)
    {
        var owner = ctor.Owner!;
        var name = owner.IsStruct ? "ctor" : "new";
        int explicitCtors = 0;
        foreach (var c in owner.Ctors)
        {
            if (c.Decl != null && c.Decl.Kind == NodeKind.Ctor) explicitCtors++;
        }
        if (explicitCtors < 2 || ctor.ParamCount == 0) return name;
        var parts = new List<string>();
        parts.Add(name);
        foreach (var p in ctor.Params!) parts.Add("__" + TypeToken(p.Type));
        return string.Join("", parts);
    }

    // The base of an operator name (binary + - * / % and unary -). null otherwise
    private static string? BaseOperatorName(Node op)
    {
        int arity = op.List != null ? op.List.Count : 0;
        if (arity == 2)
        {
            switch (op.Text)
            {
                case "+": return "__add";
                case "-": return "__sub";
                case "*": return "__mul";
                case "/": return "__div";
                case "%": return "__mod";
            }
        }
        if (arity == 1 && op.Text == "-") return "__unm";
        return null;
    }

    // The operator name. If the same type has several operators with the same base name, `_1` `_2` … are appended in declaration order
    public static string? OperatorName(Node op)
    {
        var mm = BaseOperatorName(op);
        if (mm == null) return null;
        var owner = op.Sym != null ? op.Sym.Owner : null;
        if (owner == null || owner.Decl.List == null) return mm;
        var overloads = new List<Node>();
        foreach (var m in owner.Decl.List)
        {
            if (m.Kind == NodeKind.OperatorDecl && BaseOperatorName(m) == mm) overloads.Add(m);
        }
        if (overloads.Count == 1) return mm;
        return mm + "_" + (overloads.IndexOf(op) + 1);
    }

    // The parameter name (the local name assigned by LocalNames)
    public static string ParamName(Node p)
    {
        return p.Local != null ? p.Local.IlName : p.Text;
    }

    // Type token: in the display string (a nested type includes the outer type's name), `[]` becomes `_arr`, `?` becomes `_opt`, and each run of non-alphanumeric characters becomes a single `_`
    public static string TypeToken(TypeSym type)
    {
        var text = TokenDisplay(type).Replace("[]", "_arr").Replace("?", "_opt");
        var u = new Utf8Bytes(text);
        var parts = new List<string>();
        bool lastUnderscore = false;
        for (int i = 0; i < u.Length; i++)
        {
            int c = u[i];
            if (Strings.IsAsciiLetterOrDigit(c))
            {
                parts.Add(u.Slice(i, 1).ToString());
                lastUnderscore = false;
            }
            else if (parts.Count > 0 && !lastUnderscore)
            {
                parts.Add("_");
                lastUnderscore = true;
            }
        }
        if (lastUnderscore) parts.RemoveAt(parts.Count - 1);
        return string.Join("", parts);
    }

    // Same form as Roslyn's display (NameAndContainingTypes, including type parameters, keywords for special types)
    public static string TokenDisplay(TypeSym t)
    {
        switch (t.Kind)
        {
            case TypeKind.Nullable: return TokenDisplay(t.Elem) + "?";
            case TypeKind.Array: return TokenDisplay(t.Elem) + "[]";
            case TypeKind.Tuple:
                {
                    var parts = new List<string>();
                    foreach (var a in t.Args!) parts.Add(TokenDisplay(a));
                    return "(" + string.Join(", ", parts) + ")";
                }
            case TypeKind.Delegate:
                {
                    var parts = new List<string>();
                    foreach (var a in t.Args!) parts.Add(TokenDisplay(a));
                    if (t.Ret!.Kind == TypeKind.Void)
                    {
                        if (parts.Count == 0) return "Action";
                        return "Action<" + string.Join(", ", parts) + ">";
                    }
                    parts.Add(TokenDisplay(t.Ret));
                    return "Func<" + string.Join(", ", parts) + ">";
                }
            // Dictionary's Keys / Values are nested types (Dictionary<TKey, TValue>.KeyCollection)
            case TypeKind.KeyCollection:
                return "Dictionary<" + TokenDisplay(t.Arg(0)) + ", " + TokenDisplay(t.Arg(1)) + ">.KeyCollection";
            case TypeKind.ValueCollection:
                return "Dictionary<" + TokenDisplay(t.Arg(0)) + ", " + TokenDisplay(t.Arg(1)) + ">.ValueCollection";
            case TypeKind.Class:
            case TypeKind.Struct:
            case TypeKind.Interface:
            case TypeKind.Enum:
                if (t.Decl != null)
                {
                    var name = t.Decl.Name;
                    var outer = t.Decl.Outer;
                    while (outer != null)
                    {
                        name = outer.Name + "." + name;
                        outer = outer.Outer;
                    }
                    if (t.Args != null && t.Args.Count > 0)
                    {
                        var parts = new List<string>();
                        foreach (var a in t.Args) parts.Add(TokenDisplay(a));
                        return name + "<" + string.Join(", ", parts) + ">";
                    }
                    if (t.Decl.TypeParams.Count > 0)
                        return name + "<" + string.Join(", ", t.Decl.TypeParams) + ">";
                    return name;
                }
                break;
        }
        if (t.Args != null && t.Args.Count > 0)
        {
            var parts = new List<string>();
            foreach (var a in t.Args) parts.Add(TokenDisplay(a));
            return t.Name + "<" + string.Join(", ", parts) + ">";
        }
        return t.Name;
    }
}
