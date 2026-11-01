using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// 文字列の byte 単位の道具 (サブセットの string は位置の API を持たない)
public static class Strings
{
    public static int ByteLength(string s)
    {
        return new Utf8Bytes(s).Length;
    }

    // start byte 目から後ろ
    public static string From(string s, int start)
    {
        var u = new Utf8Bytes(s);
        if (start >= u.Length) return "";
        return u.Slice(start, u.Length - start).ToString();
    }

    // 先頭の n byte
    public static string Prefix(string s, int n)
    {
        var u = new Utf8Bytes(s);
        if (n >= u.Length) return s;
        return u.Slice(0, n).ToString();
    }

    public static bool IsAsciiLetterOrDigit(int c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    }
}

// Lua の数の表記
public static class LuaNumbers
{
    // 有限の数の literal か (double.TryParse で読めて有限)。tcs が出す literal の形 (10 進、16 進、指数) だけを見る
    public static bool IsFiniteNumberText(string text)
    {
        var u = new Utf8Bytes(text);
        int n = u.Length;
        if (n == 0) return false;
        int i = 0;
        if (u[0] == '-' || u[0] == '+') i = 1;
        if (i >= n) return false;
        bool digits = false;
        for (; i < n; i++)
        {
            int c = u[i];
            if (c >= '0' && c <= '9') digits = true;
            else if (c == '.' || c == 'e' || c == 'E' || c == '-' || c == '+') continue;
            else return false;
        }
        return digits;
    }
}

// static の初期化子の実行の順 (参照実装の StaticInitOrder.Order)。
// roots (宣言の順) から deps をたどる DFS の帰りがけ順。初期化中の型へ戻る辺 (循環) はたどらない
public static class StaticInitOrder
{
    public static List<string> Order(List<string> roots, Dictionary<string, List<string>>? deps)
    {
        var order = new List<string>();
        var seen = new HashSet<string>();
        foreach (var r in roots) Visit(r, deps, order, seen);
        return order;
    }

    private static void Visit(string t, Dictionary<string, List<string>>? deps, List<string> order, HashSet<string> seen)
    {
        if (!seen.Add(t)) return;
        if (deps != null && deps.TryGetValue(t, out var ds))
        {
            foreach (var d in ds) Visit(d, deps, order, seen);
        }
        order.Add(t);
    }
}

// 参照実装の LuaNaming (Shared/LuaNaming.cs) のうち、user の型の名前の規則
public static class LuaNaming
{
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
