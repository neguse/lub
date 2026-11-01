using System.Collections;

namespace TinyCs.Vm;

/// <summary>IL の木を辿る小さな道具: closure が捕捉する変数の名前を求める (il-spec §7)。
/// 名前で数えるので保守的 (同じ名前の別の変数も cell になりうるが、意味は変わらない)。</summary>
internal static class IlWalk
{
    /// <summary>node の子 (IlNode / IlBlock / IlTableEntry / tuple / 列) を列挙する。</summary>
    public static IEnumerable<object> Children(object node)
    {
        foreach (var p in node.GetType().GetProperties())
        {
            if (p.Name is "Origin" or "EqualityContract" || p.GetIndexParameters().Length > 0) continue;
            var v = p.GetValue(node);
            foreach (var c in Flatten(v)) yield return c;
        }
    }

    private static IEnumerable<object> Flatten(object? v)
    {
        switch (v)
        {
            case null or string:
                yield break;
            case IlNode or IlBlock or IlTableEntry:
                yield return v;
                yield break;
            case IEnumerable e:
                foreach (var x in e)
                    foreach (var c in Flatten(x)) yield return c;
                yield break;
        }
        var t = v.GetType();
        if (t.IsGenericType && t.FullName!.StartsWith("System.ValueTuple"))
            foreach (var f in t.GetFields())
                foreach (var c in Flatten(f.GetValue(v))) yield return c;
    }

    /// <summary>node の中で宣言される変数の名前 (local、引数、ループ変数)。</summary>
    public static void Declared(object node, HashSet<string> names)
    {
        switch (node)
        {
            case IlLocal l: names.Add(l.Name); break;
            case IlClosure c:
                foreach (var p in c.Params) names.Add(p);
                foreach (var p in c.PatternLocals) names.Add(p);
                break;
            case IlNumericFor f: names.Add(f.Var); break;
            case IlForeachList f: names.Add(f.Var); break;
            case IlForeachDict f: names.Add(f.Var); break;
            case IlForeachRunes f: names.Add(f.Var); break;
            case IlForPairs f:
                names.Add(f.KVar);
                if (f.VVar is { } v) names.Add(v);
                break;
        }
        foreach (var c in Children(node)) Declared(c, names);
    }

    public static void Used(object node, HashSet<string> names)
    {
        if (node is IlVar v) names.Add(v.Name);
        foreach (var c in Children(node)) Used(c, names);
    }

    /// <summary>closure の自由な名前: 使う位置で、closure の中の外側の scope に宣言が無い名前。
    /// scope は block、loop の変数、closure の引数 (C# の名前の scope と同じ形)。型の名前も入るので、呼ぶ側が変数に絞る。</summary>
    public static HashSet<string> Free(IlClosure c)
    {
        var free = new HashSet<string>();
        var scopes = new List<HashSet<string>> { new(c.Params.Concat(c.PatternLocals)) };
        if (c.Body is { } body) VisitBlock(body, scopes, free);
        else Visit(c.ExprBody!, scopes, free);
        return free;
    }

    private static bool Declared(List<HashSet<string>> scopes, string name) => scopes.Any(s => s.Contains(name));

    private static void VisitBlock(IlBlock block, List<HashSet<string>> scopes, HashSet<string> free)
    {
        scopes.Add([]);
        foreach (var st in block.Stats) Visit(st, scopes, free);
        scopes.RemoveAt(scopes.Count - 1);
    }

    private static void Scoped(IEnumerable<string> names, Action body, List<HashSet<string>> scopes)
    {
        scopes.Add(new HashSet<string>(names));
        body();
        scopes.RemoveAt(scopes.Count - 1);
    }

    private static void Visit(object node, List<HashSet<string>> scopes, HashSet<string> free)
    {
        switch (node)
        {
            case IlVar v:
                if (!Declared(scopes, v.Name)) free.Add(v.Name);
                return;
            case IlBlock b:
                VisitBlock(b, scopes, free);
                return;
            case IlLocal l:
                if (l.Init is { } init) Visit(init, scopes, free);
                scopes[^1].Add(l.Name);
                return;
            case IlMultiAssign { Declare: true } m:
                foreach (var v in m.Values) Visit(v, scopes, free);
                foreach (var t in m.Targets)
                    if (t is IlVar tv) scopes[^1].Add(tv.Name);
                return;
            case IlClosure c:
                // 入れ子の closure: その自由な名前のうち、ここで宣言されていないものが外へ出る
                foreach (var name in Free(c))
                    if (!Declared(scopes, name)) free.Add(name);
                return;
            case IlNumericFor f:
                Visit(f.Start, scopes, free);
                Visit(f.Limit, scopes, free);
                Scoped([f.Var], () => VisitBlock(f.Body, scopes, free), scopes);
                return;
            case IlForeachList f:
                Visit(f.Coll, scopes, free);
                Scoped([f.Var], () => VisitBlock(f.Body, scopes, free), scopes);
                return;
            case IlForeachDict f:
                Visit(f.Coll, scopes, free);
                Scoped([f.Var], () => VisitBlock(f.Body, scopes, free), scopes);
                return;
            case IlForeachRunes f:
                Visit(f.Str, scopes, free);
                Scoped([f.Var], () => VisitBlock(f.Body, scopes, free), scopes);
                return;
            case IlForPairs f:
                Visit(f.Coll, scopes, free);
                Scoped(f.VVar is { } vv ? [f.KVar, vv] : [f.KVar], () => VisitBlock(f.Body, scopes, free), scopes);
                return;
            case IlIife iife:
                scopes.Add([]);
                foreach (var st in iife.Stats) Visit(st, scopes, free);
                scopes.RemoveAt(scopes.Count - 1);
                return;
        }
        foreach (var ch in Children(node)) Visit(ch, scopes, free);
    }

    /// <summary>node の中のどれかの closure が捕捉する名前。</summary>
    public static HashSet<string> Captured(object node)
    {
        var set = new HashSet<string>();
        Visit(node);
        return set;

        void Visit(object n)
        {
            if (n is IlClosure c)
            {
                set.UnionWith(Free(c));
                return; // 中の closure の自由な名前は Free(c) に含まれる
            }
            foreach (var ch in Children(n)) Visit(ch);
        }
    }
}
