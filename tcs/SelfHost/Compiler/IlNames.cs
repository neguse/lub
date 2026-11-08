using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// IL の名前の規則 (user の型の method、constructor、演算子、parameter の名前)。host の型の名前は Declarer.cs の LuaNames
public static class IlNames
{
    // user の型の method の名前。同じ型に同名の method が複数あるときだけ
    // parameter の型を `__` でつないで付ける。interface の method を実装する method は interface の method の名前
    public static string MethodName(Symbol m)
    {
        if (m.Kind != SymKind.Method) return m.Name;
        if (m.Owner != null && m.Owner.IsRef) return m.IlName;
        var implemented = ImplementedInterfaceMethod(m);
        // --ref の interface の method は C# の名前 (host の名前ではない。tcs check は RefInterfaceImplementation で止め、tcs VM と C は C# の名前で呼ぶ)
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

    // instance method が実装する interface の method (宣言の型の interface から)
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

    // user の型の constructor の名前。class は `new`、struct は `ctor`。constructor が複数ある型は、
    // 引数のあるものに method の overload と同じ規則で parameter の型を付ける (`new__int__float`)
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

    // 演算子の名前の元 (+ - * / % の 2 項と単項の -)。それ以外は null
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

    // 演算子の名前。同じ型に同じ名前の元の演算子が複数あれば宣言の順に `_1` `_2` … を付ける
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

    // parameter の名前 (LocalNames が付けた local の名前)
    public static string ParamName(Node p)
    {
        return p.Local != null ? p.Local.IlName : p.Text;
    }

    // 型の token: 表示の文字列 (入れ子の型は外側の型の名前も含む) の `[]` を `_arr`、`?` を `_opt` にし、英数字以外の連続を `_` 1 つにする
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

    // Roslyn の表示 (NameAndContainingTypes、型 parameter を含む、特別な型は keyword) と同じ形
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
            // Dictionary の Keys / Values は入れ子の型 (Dictionary<TKey, TValue>.KeyCollection)
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
