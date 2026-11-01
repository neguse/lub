using System.Collections.Generic;

namespace TcsSelf;

// 型の判定 (参照実装の LuaEmitter.Helpers / Structs の判定を、セルフホストの TypeSym で写したもの)。
// タプルは参照実装と同じく、library の System.ValueTuple<...> (TinySystem/ValueTuple.cs) の generic な可変 struct として扱う
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

    // 整数の元 (char を含む)。参照実装の IsIntegralSource
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

    // 値型か (参照実装の ITypeSymbol.IsValueType)。型引数は値型ではない
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

    // List / 配列の要素が false でありうる (bool、型引数、object)
    public static bool HasBoolElements(TypeSym? t)
    {
        var e = ElementOf(t);
        if (e == null) return false;
        if (e.Kind == TypeKind.Nullable) e = e.Elem;
        return e.Kind == TypeKind.TypeParam || e.Kind == TypeKind.Bool || e.Kind == TypeKind.Object;
    }

    // 要素の default が nil の配列 (`new T[n]` が穴を持つ。TCS1003)
    public static bool HasNilableElements(TypeSym? t)
    {
        if (t == null || t.Kind != TypeKind.Array) return false;
        var e = t.Elem;
        return !e.IsValueType || e.Kind == TypeKind.TypeParam || e.Kind == TypeKind.Nullable;
    }

    // 要素が null になりうる List
    public static bool HasNilListElements(TypeSym? t)
    {
        if (t == null || t.Kind != TypeKind.List) return false;
        var e = t.Elem;
        return !e.IsValueType || e.Kind == TypeKind.TypeParam || e.Kind == TypeKind.Nullable;
    }

    // Roslyn の ToDisplayString (C backend の型の表記)
    public static string Display(TypeSym t)
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
            case TypeKind.Tuple:
            {
                var parts = new List<string>();
                foreach (var a in t.Args!) parts.Add(Display(a));
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
                if (t.Decl != null)
                {
                    var name = t.Decl.FullName;
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
}
