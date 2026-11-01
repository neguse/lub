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

    /// <summary>closure の自由な名前 (中で使い、中で宣言しない名前)。型の名前も入るので、呼ぶ側が変数に絞る。</summary>
    public static HashSet<string> Free(IlClosure c)
    {
        var used = new HashSet<string>();
        Used(c, used);
        var declared = new HashSet<string>();
        Declared(c, declared);
        used.ExceptWith(declared);
        return used;
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
