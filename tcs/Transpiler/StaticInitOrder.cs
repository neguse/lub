namespace TinyCs;

// 型の static initializer の実行順 (doc/support-matrix.md §2.3)。依存の解析は compiler (SelfHost/Compiler/StaticInitDeps.cs) が行い、
// module の snapshot (ModuleLinker) はここで型の初期化の順を決める。
// 順は「型を宣言順に触ったときの lazy 初期化」と同じ DFS post-order: 依存の循環は、先に触った型が
// 初期化中 (= pre-zero の default) のまま相手の initializer に読まれる。
public static class StaticInitOrder
{
    /// <summary>roots (宣言順) から deps を辿る DFS post-order。初期化中の型
    /// へ戻る辺 (循環) は辿らない。</summary>
    public static List<T> Order<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> deps)
        where T : notnull
    {
        var order = new List<T>();
        var seen = new HashSet<T>();
        void Visit(T t)
        {
            if (!seen.Add(t)) return;
            foreach (var d in deps(t)) Visit(d);
            order.Add(t);
        }
        foreach (var r in roots) Visit(r);
        return order;
    }
}
