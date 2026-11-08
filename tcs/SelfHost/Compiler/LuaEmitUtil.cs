using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

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
