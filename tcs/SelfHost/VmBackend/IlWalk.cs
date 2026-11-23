using System.Collections.Generic;
using TcsSelf;

namespace TcsVm;

/// <summary>A small helper that walks the IL tree: finds the names of variables captured by closures (il-spec §7).
/// Counts by name, so it is conservative (another variable with the same name may also become a cell, but the meaning does not change).
/// A node's children are IlNodes.Children (in the order of the record members).</summary>
public static class IlWalk
{
    /// <summary>Free names of a closure: names that, at the place of use, have no declaration in the enclosing scopes inside the closure.
    /// Scopes are blocks, loop variables, and closure parameters (the same shape as C# name scopes). Type names are included too, so the caller narrows them down to variables.</summary>
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

    // Walks block inside a scope that declares one (or two) names
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
            // Nested closure: of its free names, those not declared here go outward
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

    /// <summary>Names captured by any closure inside node.</summary>
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
            // Free names of inner closures are included in Free(c)
            foreach (var name in Free(c)) set.Add(name);
            return;
        }
        foreach (var ch in IlNodes.Children(n)) CollectCaptured(ch, set);
    }
}
