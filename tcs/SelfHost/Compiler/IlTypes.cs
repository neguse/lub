using System.Collections.Generic;

namespace TcsSelf;

// 型の判定 (TypeSym の上で)。
// タプルは library の System.ValueTuple<...> (TinySystem/ValueTuple.cs) の generic な可変 struct として扱う
public static class IlTypes
{
    public static TypeSym UnwrapNullable(TypeSym t)
    {
        return t.Kind == TypeKind.Nullable ? t.Elem : t;
    }

    public static bool IsNullableValueType(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.Nullable;
    }

    public static bool IsFloating(TypeSym? t)
    {
        if (t == null) return false;
        var u = UnwrapNullable(t);
        return u.Kind == TypeKind.Float || u.Kind == TypeKind.Double;
    }

    public static bool IsIntegral(TypeSym? t)
    {
        if (t == null) return false;
        var u = UnwrapNullable(t);
        switch (u.Kind)
        {
            case TypeKind.Int:
            case TypeKind.Long:
            case TypeKind.Short:
            case TypeKind.Byte:
            case TypeKind.UInt:
                return true;
        }
        return false;
    }

    // 整数の元 (char を含む)
    public static bool IsIntegralSource(TypeSym? t)
    {
        if (t == null) return false;
        return IsIntegral(t) || t.Kind == TypeKind.Char;
    }

    public static bool IsChar(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.Char;
    }

    public static bool IsBool(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.Bool;
    }

    public static bool IsString(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.String;
    }

    public static bool IsList(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.List;
    }

    public static bool IsDict(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.Dict;
    }

    // Dictionary の Keys / Values (runtime は配列を返す)
    public static bool IsDictCollection(TypeSym? t)
    {
        return t != null && (t.Kind == TypeKind.KeyCollection || t.Kind == TypeKind.ValueCollection);
    }

    public static bool IsQueue(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.Queue;
    }

    public static bool IsHashSet(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.HashSet;
    }

    public static bool IsArray(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.Array;
    }

    public static bool IsRune(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.Rune;
    }

    public static bool IsUtf8Bytes(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.Utf8Bytes;
    }

    public static bool IsTuple(TypeSym? t)
    {
        return t != null && t.Kind == TypeKind.Tuple;
    }

    // 値型か (Roslyn の ITypeSymbol.IsValueType と同じ)。型引数は値型ではない
    public static bool IsValueType(TypeSym t)
    {
        return t.IsValueType;
    }

    // source に宣言した struct (タプルは library の ValueTuple)
    public static bool IsUserStruct(TypeSym? t)
    {
        if (t == null) return false;
        if (t.Kind == TypeKind.Tuple) return true;
        return t.Kind == TypeKind.Struct && t.Decl != null;
    }

    // 変更できる struct (readonly でない)
    public static bool IsMutableStruct(TypeSym? t)
    {
        if (!IsUserStruct(t)) return false;
        if (t!.Kind == TypeKind.Tuple) return true;
        return !t.Decl!.Decl.Has(Mods.Readonly);
    }

    // List / 配列の要素
    public static TypeSym? ElementOf(TypeSym? t)
    {
        if (t == null) return null;
        if (t.Kind == TypeKind.Array || t.Kind == TypeKind.List) return t.Elem;
        if (t.Args != null && t.Args.Count == 1) return t.Args[0];
        return null;
    }

    // 要素の default が null の配列 (`new T[n]` の要素が null になりうる。TCS1003)
    public static bool HasNilableElements(TypeSym? t)
    {
        if (t == null || t.Kind != TypeKind.Array) return false;
        var e = t.Elem;
        return !e.IsValueType || e.Kind == TypeKind.TypeParam || e.Kind == TypeKind.Nullable;
    }

    // C# の keyword と同じ綴りの名前は @ を付けて表示する (`@event.C`)
    private static string EscapedName(string fullName)
    {
        var parts = fullName.Split('.');
        for (int i = 0; i < parts.Length; i++)
        {
            if (Lexer.IsKeyword(parts[i])) parts[i] = "@" + parts[i];
        }
        return string.Join(".", parts);
    }

    // Roslyn の ToDisplayString (C backend の型の表記)
    public static string Display(TypeSym t)
    {
        if (t.Annotated) return DisplayPlain(t) + "?";
        return DisplayPlain(t);
    }

    private static string DisplayPlain(TypeSym t)
    {
        switch (t.Kind)
        {
            case TypeKind.Nullable: return Display(t.Elem) + "?";
            case TypeKind.Array: return Display(t.Elem) + "[]";
            case TypeKind.List: return "System.Collections.Generic.List<" + Display(t.Elem) + ">";
            case TypeKind.Dict: return "System.Collections.Generic.Dictionary<" + Display(t.Arg(0)) + ", " + Display(t.Arg(1)) + ">";
            case TypeKind.HashSet: return "System.Collections.Generic.HashSet<" + Display(t.Elem) + ">";
            case TypeKind.Queue: return "System.Collections.Generic.Queue<" + Display(t.Elem) + ">";
            case TypeKind.KeyValuePair: return "System.Collections.Generic.KeyValuePair<" + Display(t.Arg(0)) + ", " + Display(t.Arg(1)) + ">";
            case TypeKind.KeyCollection:
            case TypeKind.ValueCollection:
                return "System.Collections.Generic.Dictionary<" + Display(t.Arg(0)) + ", " + Display(t.Arg(1)) + ">." + t.Name;
            case TypeKind.Tuple:
            {
                // 要素の名前があれば型の後ろに書く (`(int x, int y)`)
                var parts = new List<string>();
                for (int i = 0; i < t.Args!.Count; i++)
                {
                    string name = t.Names != null && i < t.Names.Count ? t.Names[i] : "";
                    parts.Add(name != "" ? Display(t.Args[i]) + " " + name : Display(t.Args[i]));
                }
                return "(" + string.Join(", ", parts) + ")";
            }
            case TypeKind.Delegate:
            {
                var parts = new List<string>();
                foreach (var a in t.Args!) parts.Add(Display(a));
                if (t.Ret!.Kind == TypeKind.Void)
                {
                    if (parts.Count == 0) return "System.Action";
                    return "System.Action<" + string.Join(", ", parts) + ">";
                }
                parts.Add(Display(t.Ret));
                return "System.Func<" + string.Join(", ", parts) + ">";
            }
            case TypeKind.Class:
            case TypeKind.Struct:
            case TypeKind.Interface:
            case TypeKind.Enum:
                // library の System.ValueTupleN はタプルの構文で表示する (`(T1, T2)`)
                if (t.Decl != null && t.Decl.Decl.File.IsLibrary && t.Decl.FullName.StartsWith("System.ValueTuple"))
                {
                    var parts = new List<string>();
                    if (t.Args != null && t.Args.Count > 0)
                    {
                        foreach (var a in t.Args) parts.Add(Display(a));
                    }
                    else
                    {
                        foreach (var p in t.Decl.TypeParams) parts.Add(p);
                    }
                    return "(" + string.Join(", ", parts) + ")";
                }
                if (t.Decl != null)
                {
                    var name = EscapedName(t.Decl.FullName);
                    if (t.Args != null && t.Args.Count > 0)
                    {
                        var parts = new List<string>();
                        foreach (var a in t.Args) parts.Add(Display(a));
                        return name + "<" + string.Join(", ", parts) + ">";
                    }
                    if (t.Decl.TypeParams.Count > 0) return name + "<" + string.Join(", ", t.Decl.TypeParams) + ">";
                    return name;
                }
                break;
            case TypeKind.Utf8Bytes: return "TinySystem.Text.Utf8Bytes";
            case TypeKind.Rune: return "System.Text.Rune";
            case TypeKind.Random: return "TinySystem.Random";
        }
        return t.Name;
    }

    // 値を構成する member (field と自動実装の property。positional な property を含む)。
    // 基底の record から同名の member を継承する positional parameter は property にならない
    public static List<ValueMember> ValueMembers(TypeSym? t)
    {
        var r = new List<ValueMember>();
        if (t == null) return r;
        if (t.Kind == TypeKind.Tuple)
        {
            for (int i = 0; i < t.Args!.Count; i++) r.Add(new ValueMember("Item" + (i + 1), t.Args[i]));
            return r;
        }
        if (t.Decl == null) return r;
        bool generic = t.Args != null && t.Args.Count > 0;
        foreach (var m in t.Decl.Members)
        {
            if (m.IsStatic || m.IsConst) continue;
            if (m.Kind == SymKind.Field || (m.Kind == SymKind.Property && m.IsAuto))
            {
                if (m.Kind == SymKind.Property && t.Decl.BaseClass != null && t.Decl.BaseClass.Lookup(m.Name) != null) continue;
                r.Add(new ValueMember(m.Name, generic ? m.Type.Subst(t.Args, null) : m.Type));
            }
        }
        return r;
    }

    // Roslyn の AllInterfaces と同じ順 (基底の型から、宣言の逆順に基底の interface を後置で集めて、最後に反転する)
    public static List<TypeSym> AllInterfaces(TypeDeclSym t)
    {
        var result = new List<TypeSym>();
        var visited = new List<TypeDeclSym>();
        TypeDeclSym? cur = t;
        while (cur != null)
        {
            for (int i = cur.Interfaces.Count - 1; i >= 0; i--) AddAllInterfaces(cur.Interfaces[i], visited, result);
            cur = cur.BaseClass;
        }
        var reversed = new List<TypeSym>();
        for (int i = result.Count - 1; i >= 0; i--) reversed.Add(result[i]);
        return reversed;
    }

    private static void AddAllInterfaces(TypeSym iface, List<TypeDeclSym> visited, List<TypeSym> result)
    {
        if (iface.Decl == null || visited.Contains(iface.Decl)) return;
        visited.Add(iface.Decl);
        var bases = iface.Decl.Interfaces;
        for (int i = bases.Count - 1; i >= 0; i--) AddAllInterfaces(bases[i], visited, result);
        result.Add(iface);
    }

    // 型引数の既定値を受ける型 (source の generic な class / struct)
    public static bool PassesTypeArgDefaults(TypeDeclSym? t)
    {
        if (t == null || t.TypeParams.Count == 0) return false;
        return t.DeclKind == DeclKinds.Class || t.DeclKind == DeclKinds.Struct || t.DeclKind == DeclKinds.RecordClass || t.DeclKind == DeclKinds.RecordStruct;
    }
}

// struct の値を構成する member (名前と型)
public sealed class ValueMember
{
    public string Name;
    public TypeSym Type;

    public ValueMember(string name, TypeSym type)
    {
        Name = name;
        Type = type;
    }
}
