using System.Collections.Generic;
using TcsSelf;

namespace TcsVm;

/// <summary>IL の木を辿る小さな道具: closure が捕捉する変数の名前を求める (il-spec §7)。
/// 名前で数えるので保守的 (同じ名前の別の変数も cell になりうるが、意味は変わらない)。
/// node の子は IlNodes.Children (record の member の順)。</summary>
public static class IlWalk
{
    /// <summary>closure の自由な名前: 使う位置で、closure の中の外側の scope に宣言が無い名前。
    /// scope は block、loop の変数、closure の引数 (C# の名前の scope と同じ形)。型の名前も入るので、呼ぶ側が変数に絞る。</summary>
    public static HashSet<string> Free(IlClosure c)
    {
        var free = new HashSet<string>();
        var scopes = new List<HashSet<string>>();
        var first = new HashSet<string>();
        foreach (var p in c.Params) first.Add(p);
        foreach (var p in c.PatternLocals) first.Add(p);
        scopes.Add(first);
        if (c.Body != null) VisitBlock(c.Body, scopes, free);
        else Visit(c.ExprBody!, scopes, free);
        return free;
    }

    private static bool IsDeclared(List<HashSet<string>> scopes, string name)
    {
        foreach (var s in scopes)
        {
            if (s.Contains(name)) return true;
        }
        return false;
    }

    private static void VisitBlock(IlBlock block, List<HashSet<string>> scopes, HashSet<string> free)
    {
        scopes.Add(new HashSet<string>());
        foreach (var st in block.Stats) Visit(st, scopes, free);
        scopes.RemoveAt(scopes.Count - 1);
    }

    // 名前を 1 つ (または 2 つ) 宣言した scope の中で block を辿る
    private static void ScopedBlock(string name, string? name2, IlBlock body, List<HashSet<string>> scopes, HashSet<string> free)
    {
        var s = new HashSet<string>();
        s.Add(name);
        if (name2 != null) s.Add(name2);
        scopes.Add(s);
        VisitBlock(body, scopes, free);
        scopes.RemoveAt(scopes.Count - 1);
    }

    private static void Visit(object node, List<HashSet<string>> scopes, HashSet<string> free)
    {
        if (node is IlVar v)
        {
            if (!IsDeclared(scopes, v.Name)) free.Add(v.Name);
            return;
        }
        if (node is IlBlock b)
        {
            VisitBlock(b, scopes, free);
            return;
        }
        if (node is IlLocal l)
        {
            if (l.Init != null) Visit(l.Init, scopes, free);
            scopes[scopes.Count - 1].Add(l.Name);
            return;
        }
        if (node is IlMultiAssign m && m.Declare)
        {
            foreach (var x in m.Values) Visit(x, scopes, free);
            foreach (var t in m.Targets)
            {
                if (t is IlVar tv) scopes[scopes.Count - 1].Add(tv.Name);
            }
            return;
        }
        if (node is IlClosure c)
        {
            // 入れ子の closure: その自由な名前のうち、ここで宣言されていないものが外へ出る
            foreach (var name in Free(c))
            {
                if (!IsDeclared(scopes, name)) free.Add(name);
            }
            return;
        }
        if (node is IlNumericFor nf)
        {
            Visit(nf.Start, scopes, free);
            Visit(nf.Limit, scopes, free);
            ScopedBlock(nf.Var, null, nf.Body, scopes, free);
            return;
        }
        if (node is IlForeachList fl)
        {
            Visit(fl.Coll, scopes, free);
            ScopedBlock(fl.Var, null, fl.Body, scopes, free);
            return;
        }
        if (node is IlForeachDict fd)
        {
            Visit(fd.Coll, scopes, free);
            ScopedBlock(fd.Var, null, fd.Body, scopes, free);
            return;
        }
        if (node is IlForeachRunes fr)
        {
            Visit(fr.Str, scopes, free);
            ScopedBlock(fr.Var, null, fr.Body, scopes, free);
            return;
        }
        if (node is IlForPairs fp)
        {
            Visit(fp.Coll, scopes, free);
            ScopedBlock(fp.KVar, fp.VVar, fp.Body, scopes, free);
            return;
        }
        if (node is IlIife iife)
        {
            scopes.Add(new HashSet<string>());
            foreach (var st in iife.Stats) Visit(st, scopes, free);
            scopes.RemoveAt(scopes.Count - 1);
            return;
        }
        foreach (var ch in IlNodes.Children(node)) Visit(ch, scopes, free);
    }

    /// <summary>node の中のどれかの closure が捕捉する名前。</summary>
    public static HashSet<string> Captured(object node)
    {
        var set = new HashSet<string>();
        CollectCaptured(node, set);
        return set;
    }

    private static void CollectCaptured(object n, HashSet<string> set)
    {
        if (n is IlClosure c)
        {
            // 中の closure の自由な名前は Free(c) に含まれる
            foreach (var name in Free(c)) set.Add(name);
            return;
        }
        foreach (var ch in IlNodes.Children(n)) CollectCaptured(ch, set);
    }
}
