using System.Collections.Generic;

namespace TcsSelf;

// IL の木の道具 (子の式の列挙と差し替え、位置の差し替え、呼び出しを含まない式の判定)
public static class IlTree
{
    // Origin だけを差し替えた文
    public static IlStat WithOrigin(IlStat s, IlPos? origin)
    {
        switch (s)
        {
            case IlLocal x1: return x1 with { Origin = origin };
            case IlAssign x2: return x2 with { Origin = origin };
            case IlCallStat x3: return x3 with { Origin = origin };
            case IlIf x4: return x4 with { Origin = origin };
            case IlWhile x5: return x5 with { Origin = origin };
            case IlRepeat x6: return x6 with { Origin = origin };
            case IlNumericFor x7: return x7 with { Origin = origin };
            case IlForeachList x8: return x8 with { Origin = origin };
            case IlForeachDict x9: return x9 with { Origin = origin };
            case IlForeachRunes x10: return x10 with { Origin = origin };
            case IlBreak x11: return x11 with { Origin = origin };
            case IlContinue x12: return x12 with { Origin = origin };
            case IlReturn x13: return x13 with { Origin = origin };
            case IlDo x14: return x14 with { Origin = origin };
            case IlBreakScope x15: return x15 with { Origin = origin };
            case IlMultiAssign x16: return x16 with { Origin = origin };
            case IlForPairs x17: return x17 with { Origin = origin };
        }
        return s;
    }

    public static List<IlExpr> Children(IlExpr e)
    {
        var r = new List<IlExpr>();
        switch (e)
        {
            case IlField f: r.Add(f.Recv); break;
            case IlIndex i:
                r.Add(i.Recv);
                r.Add(i.Idx);
                break;
            case IlLen l: r.Add(l.E); break;
            case IlBin b:
                r.Add(b.L);
                r.Add(b.R);
                break;
            case IlUn u: r.Add(u.E); break;
            case IlParen p: r.Add(p.E); break;
            case IlValues v:
                foreach (var x in v.Items) r.Add(x);
                break;
            case IlCall c:
                foreach (var x in c.Args) r.Add(x);
                break;
            case IlDynCall d:
                r.Add(d.Callee);
                foreach (var x in d.Args) r.Add(x);
                break;
            case IlInvoke iv:
                r.Add(iv.Recv);
                foreach (var x in iv.Args) r.Add(x);
                break;
            case IlNewObj o:
                foreach (var x in o.Args) r.Add(x);
                break;
            case IlTable t:
                foreach (var en in t.Entries)
                {
                    if (en.Key != null) r.Add(en.Key);
                    r.Add(en.Value);
                }
                break;
            case IlNewArray na: r.Add(na.Length); break;
            case IlNumericConvert n: r.Add(n.Value); break;
            case IlRefCast rc: r.Add(rc.Value); break;
            case IlIsType it: r.Add(it.E); break;
            case IlIsPrimType ipt: r.Add(ipt.E); break;
            case IlStructCopy s: r.Add(s.E); break;
            case IlCast ca: r.Add(ca.E); break;
            case IlNullableWrap w: r.Add(w.E); break;
            case IlNullableHasValue h: r.Add(h.E); break;
            case IlNullableValue nv: r.Add(nv.E); break;
            case IlNullableGetOrDefault g:
                r.Add(g.E);
                r.Add(g.Default);
                break;
            case IlLiftedBin lb:
                r.Add(lb.L);
                r.Add(lb.R);
                break;
            case IlLiftedUn lu: r.Add(lu.E); break;
        }
        return r;
    }

    public static IlExpr WithChildren(IlExpr e, List<IlExpr> k)
    {
        switch (e)
        {
            case IlField f: return f with { Recv = k[0] };
            case IlIndex i: return i with { Recv = k[0], Idx = k[1] };
            case IlLen l: return l with { E = k[0] };
            case IlBin b: return b with { L = k[0], R = k[1] };
            case IlUn u: return u with { E = k[0] };
            case IlParen p: return p with { E = k[0] };
            case IlValues v: return v with { Items = From(k, 0) };
            case IlCall c: return c with { Args = From(k, 0) };
            case IlDynCall d: return d with { Callee = k[0], Args = From(k, 1) };
            case IlInvoke iv: return iv with { Recv = k[0], Args = From(k, 1) };
            case IlNewObj o: return o with { Args = From(k, 0) };
            case IlTable t: return t with { Entries = RebuildEntries(t.Entries, k) };
            case IlNewArray na: return na with { Length = k[0] };
            case IlNumericConvert n: return n with { Value = k[0] };
            case IlRefCast rc: return rc with { Value = k[0] };
            case IlIsType it: return it with { E = k[0] };
            case IlIsPrimType ipt: return ipt with { E = k[0] };
            case IlStructCopy s: return s with { E = k[0] };
            case IlCast ca: return ca with { E = k[0] };
            case IlNullableWrap w: return w with { E = k[0] };
            case IlNullableHasValue h: return h with { E = k[0] };
            case IlNullableValue nv: return nv with { E = k[0] };
            case IlNullableGetOrDefault g: return g with { E = k[0], Default = k[1] };
            case IlLiftedBin lb: return lb with { L = k[0], R = k[1] };
            case IlLiftedUn lu: return lu with { E = k[0] };
        }
        return e;
    }

    private static List<IlTableEntry> RebuildEntries(List<IlTableEntry> entries, List<IlExpr> k)
    {
        int i = 0;
        var result = new List<IlTableEntry>();
        foreach (var en in entries)
        {
            IlExpr? key = null;
            if (en.Key != null)
            {
                key = k[i];
                i++;
            }
            var value = k[i];
            i++;
            result.Add(en with { Key = key, Value = value });
        }
        return result;
    }

    // 呼び出しを含まない式 (順序を入れ替えて観測できる副作用が無い)。runtime の helper / math / string は純粋なので許す
    public static bool IsCallFree(IlExpr e)
    {
        switch (e)
        {
            case IlLit:
            case IlVar:
            case IlClosure:
            case IlMethodGroup:
                return true;
            case IlField f:
                return IsCallFree(f.Recv);
            // List / 配列の要素の読み出しは範囲外で fault するので順序を入れ替えない
            case IlIndex ix:
                return (!ix.PlusOne || ix.Unchecked) && IsCallFree(ix.Recv) && IsCallFree(ix.Idx);
            case IlLen len:
                return IsCallFree(len.E);
            case IlBin bin:
                return IsCallFree(bin.L) && IsCallFree(bin.R)
                    && (!IsOverloadableOp(bin.Op) || (IsPrimitive(bin.L) && IsPrimitive(bin.R)));
            case IlUn un:
                return IsCallFree(un.E) && (un.Op != IlUnOp.Neg || IsPrimitive(un.E));
            case IlParen p:
                return IsCallFree(p.E);
            case IlTernary t:
                return IsCallFree(t.Cond) && IsCallFree(t.T) && IsCallFree(t.F);
            case IlStructCopy sc:
                return IsCallFree(sc.E);
            case IlNumericConvert n:
                return !n.Unbox && IsCallFree(n.Value);
            case IlNullableWrap w:
                return IsCallFree(w.E);
            case IlNullableHasValue h:
                return IsCallFree(h.E);
            case IlIsType it:
                return IsCallFree(it.E);
            case IlIsPrimType ipt:
                return IsCallFree(ipt.E);
            case IlNewQueue:
            case IlNewHashSet:
                return true;
            case IlNewArray na:
                return IsCallFree(na.Length) && (na.Default == null || na.Default is IlLit || na.Default is IlNewObj);
            case IlTable tbl:
                foreach (var en in tbl.Entries)
                {
                    if (en.Key != null && !IsCallFree(en.Key)) return false;
                    if (!IsCallFree(en.Value)) return false;
                }
                return true;
            case IlCall c:
                if (!IsPureCallee(c.Callee)) return false;
                foreach (var a in c.Args)
                {
                    if (!IsCallFree(a)) return false;
                }
                return true;
        }
        return false;
    }

    // + - * / % は operand がプリミティブと言えなければ演算子の overload (--ref の型の host の演算子) で、任意のコードを呼びうる
    private static bool IsOverloadableOp(IlBinOp op)
    {
        return op == IlBinOp.AddNum || op == IlBinOp.Sub || op == IlBinOp.Mul || op == IlBinOp.DivNum || op == IlBinOp.RemNum;
    }

    // 値がプリミティブ (class の instance ではない) と IL の形から言える式。IlVar / field などは型が分からないので false
    private static bool IsPrimitive(IlExpr e)
    {
        if (e is IlLit || e is IlLen || e is IlIsType || e is IlIsPrimType || e is IlNullableHasValue || e is IlNumericConvert) return true;
        if (e is IlParen p) return IsPrimitive(p.E);
        bool logical = false;
        if (e is IlBin lb && (lb.Op == IlBinOp.And || lb.Op == IlBinOp.Or)) logical = true;
        if (e is IlUn nu && nu.Op == IlUnOp.Neg) logical = true;
        if (logical)
        {
            foreach (var k in Children(e))
            {
                if (!IsPrimitive(k)) return false;
            }
            return true;
        }
        if (e is IlBin b && IsOverloadableOp(b.Op)) return IsPrimitive(b.L) && IsPrimitive(b.R);
        if (e is IlBin || e is IlUn) return true;
        if (e is IlTernary t) return IsPrimitive(t.T) && IsPrimitive(t.F);
        return false;
    }

    // 副作用の無い intrinsic (標準出力に書く __tcs_write / __tcs_writeline と、環境変数を読む __tcs_getenv を除く)
    private static bool IsPureCallee(string callee)
    {
        if (callee == "__tcs_write" || callee == "__tcs_writeline" || callee == "__tcs_getenv") return false;
        return callee.StartsWith("__tcs_")
            || callee.StartsWith("Math.")
            || callee.StartsWith("String.");
    }

    private static List<IlExpr> From(List<IlExpr> items, int start)
    {
        var r = new List<IlExpr>();
        for (int i = start; i < items.Count; i++) r.Add(items[i]);
        return r;
    }
}
