using System;
using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// source map の 1 項: Lua の行 → C# のファイルと行
public sealed class SourceMapEntry
{
    public int LuaLine;
    public string File;
    public int Line;

    public SourceMapEntry(int luaLine, string file, int line)
    {
        LuaLine = luaLine;
        File = file;
        Line = line;
    }
}

// IL → Lua の emit と、式の位置の条件式などを文に下ろす lowering。
// 出力の行、字下げ、source map、continue の label、--trace の印を持つ。
// 意味の決定は builder で済んでいて、ここは IL の形だけを見る。
public sealed class LuaGen
{
    // ---- 出力 ----

    // 出力の断片 (どれも改行で終わる)。closure の本体を文字列にするときは入れ替える
    public List<string> Chunks = new List<string>();
    public int Indent;
    public int LuaLine = 1;
    // 次に書く行に載せる C# の位置 (null なら載せない)
    public string? SourceFile;
    public int SourceLine;
    public List<SourceMapEntry> SourceMap = new List<SourceMapEntry>();

    // ---- continue の label ----

    public int ContinueCounter;
    public List<int> ContinueStack = new List<int>();
    public HashSet<int> UsedContinueLabels = new HashSet<int>();

    // ---- --trace ----

    // emit 中の関数が trace の対象のファイルのものか (lambda は外側の関数に従う)
    public bool TraceFunction;
    public const string TraceLocal = "local __tcs_at";

    // ---- lowering の temp ----

    public const string LowerTempPrefix = "__tcs_t";
    public int LowerTempCount;

    public const string MathModuleGlobal = "tcs_math";
    public const string Utf8ModuleGlobal = "tcs_utf8";

    public string Text()
    {
        return string.Join("", Chunks);
    }

    public void SetSource(IlPos origin)
    {
        SourceFile = origin.File;
        SourceLine = origin.Line;
    }

    public void ClearSource()
    {
        SourceFile = null;
    }

    private void AddSourceMap(int luaLine, string file, int line)
    {
        int n = SourceMap.Count;
        if (n > 0 && SourceMap[n - 1].LuaLine == luaLine)
        {
            SourceMap[n - 1] = new SourceMapEntry(luaLine, file, line);
            return;
        }
        SourceMap.Add(new SourceMapEntry(luaLine, file, line));
    }

    private void RemoveSourceMapFrom(int luaLine)
    {
        while (SourceMap.Count > 0 && SourceMap[SourceMap.Count - 1].LuaLine >= luaLine)
            SourceMap.RemoveAt(SourceMap.Count - 1);
    }

    public void AppendLine(string line)
    {
        if (line == "")
        {
            Chunks.Add("\n");
            LuaLine++;
            return;
        }
        if (SourceFile != null)
        {
            AddSourceMap(LuaLine, SourceFile, SourceLine);
            SourceFile = null;
        }
        // Lua は '(' で始まる文を、呼び出せる式で終わる前の文につなげて読む
        // (`local b = t[k]` + `(f)()` は `t[k](f)()`)。文の行はすべてここを通るので、';' を前に置いて分ける
        if (line.StartsWith("(")) line = ";" + line;
        var output = Spaces(Indent * 2) + line;
        Chunks.Add(output + "\n");
        LuaLine += CountLines(output);
    }

    private static int CountLines(string s)
    {
        var u = new Utf8Bytes(s);
        int n = 1;
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] == 10) n++;
        }
        return n;
    }

    private static List<string> _spaces = new List<string>();

    private static string Spaces(int n)
    {
        while (_spaces.Count <= n)
        {
            if (_spaces.Count == 0) _spaces.Add("");
            else _spaces.Add(_spaces[_spaces.Count - 1] + " ");
        }
        return _spaces[n];
    }

    public static string EscapeLuaString(string value)
    {
        var u = new Utf8Bytes(value);
        var parts = new List<string>();
        parts.Add("\"");
        int run = 0;
        for (int i = 0; i < u.Length; i++)
        {
            int c = u[i];
            string? esc = null;
            if (c == '\\') esc = "\\\\";
            else if (c == '"') esc = "\\\"";
            else if (c == '\n') esc = "\\n";
            else if (c == '\r') esc = "\\r";
            else if (c == '\t') esc = "\\t";
            // 制御文字は 2 桁固定の \xXX (Lua は \x を 2 桁で読むので後の文字と混ざらない)
            else if (c < 32 || c == 127) esc = "\\x" + Hex2(c);
            if (esc != null)
            {
                if (i > run) parts.Add(u.Slice(run, i - run).ToString());
                parts.Add(esc);
                run = i + 1;
            }
        }
        if (run < u.Length) parts.Add(u.Slice(run, u.Length - run).ToString());
        parts.Add("\"");
        return string.Join("", parts);
    }

    private static string Hex2(int c)
    {
        var digits = "0123456789ABCDEF";
        var d = new Utf8Bytes(digits);
        return d.Slice(c / 16, 1).ToString() + d.Slice(c % 16, 1).ToString();
    }

    // emit の内部の誤り (IL の形が想定外)。compile の結果に診断として出す
    public List<string> InternalErrors = new List<string>();

    private void Internal(string message)
    {
        InternalErrors.Add(message);
    }

    public static string NormalizeTracePath(string path)
    {
        return path.Replace("\\", "/");
    }

    // ---- continue の label ----

    public int PushContinueLabel()
    {
        ContinueCounter++;
        int label = ContinueCounter;
        ContinueStack.Add(label);
        return label;
    }

    public void PopContinueLabel()
    {
        ContinueStack.RemoveAt(ContinueStack.Count - 1);
    }

    public void EmitContinueLabel(int label)
    {
        if (UsedContinueLabels.Contains(label))
            AppendLine("::_continue_" + label + "::");
    }

    // ---- --trace ----

    // builder が 1 つの文を同じ Origin の文の列に分けたとき (前の宣言、条件の hoist など) は、
    // 列の先頭で 1 回だけ trace の行を出す。前の文が入れ子の文を持つときは出し直す
    private static bool ContinuesTraceRun(IlStat prev, IlStat stat)
    {
        if (stat.Origin == null || prev.Origin != stat.Origin) return false;
        return prev is IlLocal || prev is IlAssign || prev is IlCallStat || prev is IlMultiAssign;
    }

    // elseif の条件 (Origin は条件式) を評価する前に trace の行を置けるよう、
    // arm から先を else の中の if 文にする。trace しない関数では null (elseif のまま)
    private IlIf? TracedElseIf(IlIf ifStat, int arm)
    {
        if (!TraceFunction) return null;
        var origin = ifStat.Arms[arm].Cond.Origin;
        if (origin == null) return null;
        return new IlIf(ArmsFrom(ifStat.Arms, arm), ifStat.Else, origin);
    }

    // while の条件は反復ごとに評価されるので、lowering と同じ形に開いて毎回 trace の行を置く。
    // 定数の条件は評価しないので開かない
    private bool TracesLoopCond(IlWhile w)
    {
        return TraceFunction && w.Origin != null && !(w.Cond is IlLit);
    }

    private void EmitTracePoint(IlPos origin)
    {
        if (!TraceFunction) return;
        var key = NormalizeTracePath(origin.File) + ":" + origin.Line;
        AppendLine("__tcs_at = " + EscapeLuaString(key) + "; __tcs_trace[__tcs_at] = true");
        // 文そのものも source map に載せる (trace の行が位置を使った)
        SetSource(origin);
    }

    // ---- list の小道具 ----

    private static List<IlArm> ArmsFrom(List<IlArm> arms, int start)
    {
        var r = new List<IlArm>();
        for (int i = start; i < arms.Count; i++) r.Add(arms[i]);
        return r;
    }

    private static List<IlExpr> ExprsFrom(List<IlExpr> items, int start)
    {
        var r = new List<IlExpr>();
        for (int i = start; i < items.Count; i++) r.Add(items[i]);
        return r;
    }

    private static List<IlExpr> One(IlExpr a)
    {
        var r = new List<IlExpr>();
        r.Add(a);
        return r;
    }

    private static List<IlExpr> Two(IlExpr a, IlExpr b)
    {
        var r = new List<IlExpr>();
        r.Add(a);
        r.Add(b);
        return r;
    }

    private static List<IlStat> Stats1(IlStat a)
    {
        var r = new List<IlStat>();
        r.Add(a);
        return r;
    }

    private static List<IlArm> Arm1(IlExpr cond, IlBlock body)
    {
        var r = new List<IlArm>();
        r.Add(new IlArm(cond, body));
        return r;
    }

    // Origin だけを差し替えた文
    public static IlStat WithOrigin(IlStat s, IlPos? origin)
    {
        switch (s)
        {
            case IlLocal x: return x with { Origin = origin };
            case IlAssign x: return x with { Origin = origin };
            case IlCallStat x: return x with { Origin = origin };
            case IlIf x: return x with { Origin = origin };
            case IlWhile x: return x with { Origin = origin };
            case IlRepeat x: return x with { Origin = origin };
            case IlNumericFor x: return x with { Origin = origin };
            case IlForeachList x: return x with { Origin = origin };
            case IlForeachDict x: return x with { Origin = origin };
            case IlForeachRunes x: return x with { Origin = origin };
            case IlBreak x: return x with { Origin = origin };
            case IlContinue x: return x with { Origin = origin };
            case IlReturn x: return x with { Origin = origin };
            case IlDo x: return x with { Origin = origin };
            case IlBreakScope x: return x with { Origin = origin };
            case IlMultiAssign x: return x with { Origin = origin };
            case IlForPairs x: return x with { Origin = origin };
        }
        return s;
    }

    // =====================================================================
    // lowering: 式の位置の条件式 / `??` (右辺が呼び出しを含む) を文に下ろす。
    // Lua では式として書けないため、IIFE (評価ごとに closure を作る) ではなく temp の local と if 文にする。
    // Lua の emit の時点の IL → IL の変換で、IL 自体は変えない (tcs2c は IlTernary をそのまま使う)。
    //
    // 評価順 (C# は左から右) を守る規則:
    //   - 文に下ろす式 X の左にある兄弟の operand は、X の前置文より先に評価する必要がある。
    //     左の operand が副作用を持ちうる (呼び出しを含む) か、X までの右の兄弟のどれかが呼び出しを含む
    //     (左の operand が読む状態を書き換えうる) ときは temp に束縛する
    //   - 短絡 (&& / || / ??) の右辺は、条件付きの if の中に前置文を置く
    //   - temp は文ごとに do ... end で囲い、Lua の local の上限 (200) を踏まない
    //   - 条件が毎回評価される位置 (while) は `while true do do 前置文; if not c then break end end ...` に、
    //     elseif は入れ子の else に開く
    // repeat-until の条件 / IIFE の中の文 / field の初期化子 (式だけの文脈) は前置文を置けないので IIFE が残る。
    // =====================================================================

    private string NewLowerTemp()
    {
        LowerTempCount++;
        return LowerTempPrefix + LowerTempCount;
    }

    private static List<IlExpr> Children(IlExpr e)
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
            case IlIsLuaType ilt: r.Add(ilt.E); break;
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

    private static IlExpr WithChildren(IlExpr e, List<IlExpr> k)
    {
        switch (e)
        {
            case IlField f: return f with { Recv = k[0] };
            case IlIndex i: return i with { Recv = k[0], Idx = k[1] };
            case IlLen l: return l with { E = k[0] };
            case IlBin b: return b with { L = k[0], R = k[1] };
            case IlUn u: return u with { E = k[0] };
            case IlParen p: return p with { E = k[0] };
            case IlValues v: return v with { Items = ExprsFrom(k, 0) };
            case IlCall c: return c with { Args = ExprsFrom(k, 0) };
            case IlDynCall d: return d with { Callee = k[0], Args = ExprsFrom(k, 1) };
            case IlInvoke iv: return iv with { Recv = k[0], Args = ExprsFrom(k, 1) };
            case IlNewObj o: return o with { Args = ExprsFrom(k, 0) };
            case IlTable t: return t with { Entries = RebuildEntries(t.Entries, k) };
            case IlNewArray na: return na with { Length = k[0] };
            case IlNumericConvert n: return n with { Value = k[0] };
            case IlRefCast rc: return rc with { Value = k[0] };
            case IlIsType it: return it with { E = k[0] };
            case IlIsLuaType ilt: return ilt with { E = k[0] };
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

    // 文に下ろす必要がある式 (IIFE を避けたいもの)
    private static bool Needs(IlExpr e)
    {
        if (e is IlTernary) return true;
        if (e is IlIife iife && IsMultiReturnCallIife(iife)) return true;
        if (e is IlNullableGetOrDefault g) return !CannotFault(g.Default) || Needs(g.E) || Needs(g.Default);
        foreach (var k in Children(e))
        {
            if (Needs(k)) return true;
        }
        return false;
    }

    // out を持つ呼び出しの値の IIFE: [local ret; ret, outs... = call; return ret]。
    // out の変数は文の前に宣言済みなので、IIFE を除いた文に下ろせる
    private static bool IsMultiReturnCallIife(IlIife iife)
    {
        var s = iife.Stats;
        if (s.Count != 3) return false;
        if (!(s[0] is IlLocal ret) || ret.Init != null) return false;
        if (!(s[1] is IlMultiAssign multi) || multi.Declare || multi.Targets.Count == 0) return false;
        if (!(multi.Targets[0] is IlVar first)) return false;
        if (!(s[2] is IlReturn r) || !(r.Value is IlVar result)) return false;
        return first.Name == ret.Name && result.Name == ret.Name;
    }

    // 前置文より後ろへ評価をずらしても値が変わらない式
    private static bool Stable(IlExpr e, IlExpr later)
    {
        if (e is IlLit || e is IlClosure) return true;
        if (e is IlVar v && v.Name.StartsWith(LowerTempPrefix)) return true;
        return IsCallFree(e) && IsCallFree(later);
    }

    private IlExpr Lower(IlExpr e, List<IlStat> pre)
    {
        if (!Needs(e)) return e;
        if (e is IlTernary t)
        {
            var tmp = NewLowerTemp();
            pre.Add(new IlLocal(tmp, null, null, null));
            foreach (var s in LowerInto(t, v => new IlAssign(new IlVar(tmp, null), v, null))) pre.Add(s);
            return new IlVar(tmp, null);
        }
        if (e is IlIife iife && IsMultiReturnCallIife(iife))
        {
            var tmp = NewLowerTemp();
            var multi = (IlMultiAssign)iife.Stats[1];
            var values = LowerSeq(multi.Values, pre);
            pre.Add(new IlLocal(tmp, null, null, null));
            var targets = new List<IlExpr>();
            targets.Add(new IlVar(tmp, null));
            for (int i = 1; i < multi.Targets.Count; i++) targets.Add(multi.Targets[i]);
            pre.Add(new IlMultiAssign(targets, values, false, null));
            return new IlVar(tmp, null);
        }
        if (e is IlParen p)
        {
            var inner = Lower(p.E, pre);
            if (inner is IlVar) return inner;
            return p with { E = inner };
        }
        if (e is IlBin b && (b.Op == IlBinOp.And || b.Op == IlBinOp.Or) && Needs(b.R))
        {
            var tmp = NewLowerTemp();
            pre.Add(new IlLocal(tmp, Lower(b.L, pre), null, null));
            IlExpr cond = b.Op == IlBinOp.And
                ? new IlVar(tmp, null)
                : new IlUn(IlUnOp.Not, new IlVar(tmp, null), null);
            var body = new IlBlock(LowerInto(b.R, v => new IlAssign(new IlVar(tmp, null), v, null)));
            pre.Add(new IlIf(Arm1(cond, body), null, null));
            return new IlVar(tmp, null);
        }
        if (e is IlNullableGetOrDefault g && (!CannotFault(g.Default) || Needs(g.Default)))
        {
            var tmp = NewLowerTemp();
            pre.Add(new IlLocal(tmp, Lower(g.E, pre), null, null));
            var isNil = new IlBin(IlBinOp.Eq, new IlVar(tmp, null), new IlLit("nil", null, null), null);
            var body = new IlBlock(LowerInto(g.Default, v => new IlAssign(new IlVar(tmp, null), v, null)));
            pre.Add(new IlIf(Arm1(isNil, body), null, null));
            return new IlVar(tmp, null);
        }
        var kids = Children(e);
        int last = -1;
        for (int i = 0; i < kids.Count; i++)
        {
            if (Needs(kids[i])) last = i;
        }
        var lowered = ExprsFrom(kids, 0);
        for (int i = 0; i <= last; i++)
        {
            var li = Lower(kids[i], pre);
            // 右の兄弟 (i+1..last) のどれかが前置文へ評価を移しうるなら束縛する
            if (i < last)
            {
                bool stable = true;
                for (int j = i + 1; j <= last; j++)
                {
                    if (!Stable(li, kids[j]))
                    {
                        stable = false;
                        break;
                    }
                }
                if (!stable)
                {
                    var tmp = NewLowerTemp();
                    pre.Add(new IlLocal(tmp, li, null, null));
                    li = new IlVar(tmp, null);
                }
            }
            lowered[i] = li;
        }
        return WithChildren(e, lowered);
    }

    private List<IlExpr> LowerSeq(List<IlExpr> items, List<IlStat> pre)
    {
        var call = (IlCall)Lower(new IlCall("", ExprsFrom(items, 0), false, null), pre);
        return call.Args;
    }

    // e の値を sink (代入 / return) へ渡す文の列。条件式は temp を経ずに各分岐へ sink を複製する (入れ子は elseif に畳む)
    private List<IlStat> LowerInto(IlExpr e, Func<IlExpr, IlStat> sink)
    {
        var stats = new List<IlStat>();
        var core = e;
        while (core is IlParen p) core = p.E;
        if (core is IlTernary t)
        {
            var cond = Lower(t.Cond, stats);
            stats.Add(MakeIf(cond, new IlBlock(LowerInto(t.T, sink)), new IlBlock(LowerInto(t.F, sink))));
        }
        else
        {
            var value = Lower(e, stats);
            stats.Add(sink(value));
        }
        return stats;
    }

    private static IlIf MakeIf(IlExpr cond, IlBlock then, IlBlock other)
    {
        if (other.Stats.Count == 1 && other.Stats[0] is IlIf nested)
        {
            var arms = Arm1(cond, then);
            foreach (var a in nested.Arms) arms.Add(a);
            return new IlIf(arms, nested.Else, null);
        }
        return new IlIf(Arm1(cond, then), other, null);
    }

    // temp (top-level の IlLocal) を宣言する文の列は do ... end で閉じる
    private static List<IlStat> Scoped(List<IlStat> stats, IlStat original)
    {
        bool hasLocal = false;
        foreach (var s in stats)
        {
            if (s is IlLocal) hasLocal = true;
        }
        if (hasLocal) stats = Stats1(new IlDo(new IlBlock(stats), null));
        var result = new List<IlStat>();
        foreach (var s in stats) result.Add(WithOrigin(s, original.Origin));
        return result;
    }

    private List<IlStat>? LowerStat(IlStat stat)
    {
        switch (stat)
        {
            case IlLocal local when local.Init != null && Needs(local.Init):
            {
                var target = new IlVar(local.Name, null);
                var body = LowerInto(local.Init, v => new IlAssign(target, v, null));
                var result = Stats1(local with { Init = null });
                foreach (var s in Scoped(body, stat)) result.Add(s);
                return result;
            }
            case IlAssign assign when Needs(assign.Value):
                return LowerAssign(assign);
            case IlReturn ret when ret.Value != null && Needs(ret.Value):
                return Scoped(LowerInto(ret.Value, v => new IlReturn(v, null)), stat);
            case IlCallStat call when Needs(call.Call):
            {
                var pre = new List<IlStat>();
                var callee = Lower(call.Call, pre);
                pre.Add(call with { Call = callee });
                return Scoped(pre, stat);
            }
            case IlMultiAssign multi when AnyNeeds(multi.Values) && AllVars(multi.Targets):
            {
                var pre = new List<IlStat>();
                var values = LowerSeq(multi.Values, pre);
                pre.Add(new IlMultiAssign(multi.Targets, values, false, null));
                var scoped = Scoped(pre, stat);
                if (!multi.Declare) return scoped;
                var result = new List<IlStat>();
                foreach (var t in multi.Targets) result.Add(new IlLocal(((IlVar)t).Name, null, null, null));
                foreach (var s in scoped) result.Add(s);
                return result;
            }
            case IlIf ifStat when AnyArmNeeds(ifStat.Arms):
            {
                var pre = new List<IlStat>();
                var cond = Lower(ifStat.Arms[0].Cond, pre);
                IlBlock? rest = ifStat.Else;
                if (ifStat.Arms.Count > 1)
                {
                    IlStat tail = TracedElseIf(ifStat, 1) ?? new IlIf(ArmsFrom(ifStat.Arms, 1), ifStat.Else, null);
                    rest = new IlBlock(Stats1(tail));
                }
                pre.Add(new IlIf(Arm1(cond, ifStat.Arms[0].Body), rest, null));
                return Scoped(pre, stat);
            }
            case IlWhile w when Needs(w.Cond) || TracesLoopCond(w):
            {
                var pre = new List<IlStat>();
                var cond = Lower(w.Cond, pre);
                var brk = new IlBlock(Stats1(new IlBreak(null)));
                pre.Add(new IlIf(Arm1(new IlUn(IlUnOp.Not, new IlParen(cond, null), null), brk), null, null));
                // --trace: 条件を評価する前に毎回 while の行へ戻す
                IlStat head = pre.Count > 1 ? new IlDo(new IlBlock(pre), null) : pre[0];
                head = WithOrigin(head, TraceFunction ? stat.Origin : null);
                var body = Stats1(head);
                foreach (var s in w.Body.Stats) body.Add(s);
                return Stats1(new IlWhile(new IlLit("true", null, null), new IlBlock(body), w.Trailer, w.ScopeBody, stat.Origin));
            }
            case IlNumericFor f when Needs(f.Start) || Needs(f.Limit):
            {
                var pre = new List<IlStat>();
                var bounds = LowerSeq(Two(f.Start, f.Limit), pre);
                pre.Add(f with { Start = bounds[0], Limit = bounds[1] });
                return Scoped(pre, stat);
            }
            case IlForeachList fl when Needs(fl.Coll):
            {
                var pre = new List<IlStat>();
                var c = Lower(fl.Coll, pre);
                pre.Add(fl with { Coll = c });
                return Scoped(pre, stat);
            }
            case IlForeachDict fd when Needs(fd.Coll):
            {
                var pre = new List<IlStat>();
                var c = Lower(fd.Coll, pre);
                pre.Add(fd with { Coll = c });
                return Scoped(pre, stat);
            }
            case IlForeachRunes fr when Needs(fr.Str):
            {
                var pre = new List<IlStat>();
                var c = Lower(fr.Str, pre);
                pre.Add(fr with { Str = c });
                return Scoped(pre, stat);
            }
            case IlForPairs fp when Needs(fp.Coll):
            {
                var pre = new List<IlStat>();
                var c = Lower(fp.Coll, pre);
                pre.Add(fp with { Coll = c });
                return Scoped(pre, stat);
            }
        }
        return null;
    }

    private static bool AnyNeeds(List<IlExpr> items)
    {
        foreach (var x in items)
        {
            if (Needs(x)) return true;
        }
        return false;
    }

    private static bool AllVars(List<IlExpr> items)
    {
        foreach (var x in items)
        {
            if (!(x is IlVar)) return false;
        }
        return true;
    }

    private static bool AnyArmNeeds(List<IlArm> arms)
    {
        foreach (var a in arms)
        {
            if (Needs(a.Cond)) return true;
        }
        return false;
    }

    private List<IlStat> LowerAssign(IlAssign assign)
    {
        // 受け手を先に固定しなくてよい target (素の local / self の直下の field /
        // 値が呼び出しを含まないときの field の連鎖) は分岐へ直接代入する。
        // self.a.b の受け手 self.a は値の評価で差し替わりうるので固定する
        bool direct = assign.Target is IlVar
            || (assign.Target is IlField sf && sf.Recv is IlVar sv && sv.Name == "self")
            || (assign.Target is IlField && IsSimpleReceiverPath(assign.Target) && IsCallFree(assign.Value));
        if (direct)
        {
            var target = assign.Target;
            return Scoped(LowerInto(assign.Value, v => new IlAssign(target, v, null)), assign);
        }
        var pre = new List<IlStat>();
        IlStat result;
        if (assign.Target is IlField f)
        {
            var k = LowerSeq(Two(f.Recv, assign.Value), pre);
            result = new IlAssign(f with { Recv = k[0] }, k[1], null);
        }
        else if (assign.Target is IlIndex ix)
        {
            var items = new List<IlExpr>();
            items.Add(ix.Recv);
            items.Add(ix.Idx);
            items.Add(assign.Value);
            var k = LowerSeq(items, pre);
            result = new IlAssign(ix with { Recv = k[0], Idx = k[1] }, k[2], null);
        }
        else
        {
            result = assign with { Value = Lower(assign.Value, pre) };
        }
        pre.Add(result);
        return Scoped(pre, assign);
    }

    // until は文を置けないので、条件を本体の後 (continue label の後) で temp に評価してから `until not temp` に渡す。
    // C# の do-while の条件は本体の local を参照できないので、本体を do ... end に閉じても意味は変わらない
    private void EmitIlRepeat(IlRepeat repeat)
    {
        int label = PushContinueLabel();
        // --trace: 条件を評価する前に毎回その行へ戻す (Origin は条件式)
        IlPos? traceCond = TraceFunction ? repeat.Cond.Origin : null;
        if (!Needs(repeat.Cond) && traceCond == null)
        {
            AppendLine("repeat");
            Indent++;
            EmitLoopBody(repeat.Body, label, false);
            Indent--;
            AppendLine("until not (" + RenderIl(repeat.Cond) + ")");
            PopContinueLabel();
            return;
        }
        var pre = new List<IlStat>();
        var cond = Lower(repeat.Cond, pre);
        var tmp = NewLowerTemp();
        pre.Add(new IlAssign(new IlVar(tmp, null), cond, null));
        AppendLine("do");
        Indent++;
        AppendLine("local " + tmp);
        AppendLine("repeat");
        Indent++;
        EmitIlStat(new IlDo(repeat.Body, null), true);
        EmitContinueLabel(label);
        if (traceCond != null) EmitTracePoint(traceCond);
        EmitIlStat(new IlDo(new IlBlock(pre), null), true);
        Indent--;
        AppendLine("until not " + tmp);
        Indent--;
        AppendLine("end");
        PopContinueLabel();
    }

    // =====================================================================
    // IL → Lua の emit
    // =====================================================================

    // Lua の return は block の最後の文に限られるので、後に文が続く return
    // (到達しない文、loop の本体の後の continue label / for の増分) は do ... end に閉じる。
    // followed は末尾の文を出す時点で評価する (continue label を使うかは本体を出すまで決まらない)
    public void EmitIlBlock(IlBlock block, Func<bool>? followed)
    {
        var stats = block.Stats;
        for (int i = 0; i < stats.Count; i++)
        {
            bool tracePoint = i == 0 || !ContinuesTraceRun(stats[i - 1], stats[i]);
            bool isLast = i == stats.Count - 1;
            if (stats[i] is IlReturn && (!isLast || (followed != null && followed())))
            {
                var origin = stats[i].Origin;
                if (tracePoint && origin != null) EmitTracePoint(origin);
                AppendLine("do");
                Indent++;
                EmitIlStat(stats[i], false);
                Indent--;
                AppendLine("end");
            }
            else
            {
                EmitIlStat(stats[i], tracePoint);
            }
        }
    }

    private void EmitLoopBody(IlBlock body, int label, bool stepFollows)
    {
        EmitIlBlock(body, () => stepFollows || UsedContinueLabels.Contains(label));
        EmitContinueLabel(label);
    }

    // tracePoint: lowering で同じ origin を持つ文に分かれたときは、分ける前に 1 回だけ trace の行を出す
    public void EmitIlStat(IlStat stat, bool tracePoint)
    {
        if (stat.Origin != null && tracePoint) EmitTracePoint(stat.Origin);
        var lowered = LowerStat(stat);
        if (lowered != null)
        {
            foreach (var s in lowered) EmitIlStat(s, false);
            return;
        }
        if (stat.Origin != null) SetSource(stat.Origin);
        switch (stat)
        {
            case IlLocal local:
                AppendLine(local.Init != null ? "local " + local.Name + " = " + RenderIl(local.Init) : "local " + local.Name);
                return;
            case IlAssign assign:
                AppendLine(RenderIlAssign(assign));
                return;
            case IlCallStat call:
                // 引数を束縛する local は後の文へ漏れないよう do ... end で閉じる
                AppendLine(TryRenderListAddStat(call.Call, "do ", " end") ?? RenderIl(call.Call));
                return;
            case IlReturn ret:
                // 式本体の void の member (`=> list.Add(v)`) の `return table.insert(...)` は値を返さないので文の形にする
                // (関数の末尾なので local は漏れない)
                if (ret.Value != null)
                {
                    var add = TryRenderListAddStat(ret.Value, "", "");
                    if (add != null)
                    {
                        AppendLine(add);
                        return;
                    }
                    AppendLine("return " + RenderIl(ret.Value));
                    return;
                }
                AppendLine("return");
                return;
            case IlBreak:
                AppendLine("break");
                return;
            case IlContinue:
                if (ContinueStack.Count > 0)
                {
                    int lbl = ContinueStack[ContinueStack.Count - 1];
                    UsedContinueLabels.Add(lbl);
                    AppendLine("goto _continue_" + lbl);
                }
                return;
            case IlIf ifStat:
                EmitIlIf(ifStat);
                return;
            case IlWhile whileStat:
                EmitIlWhile(whileStat);
                return;
            case IlRepeat repeat:
                EmitIlRepeat(repeat);
                return;
            case IlNumericFor numFor:
            {
                int label = PushContinueLabel();
                AppendLine("for " + numFor.Var + " = " + RenderIl(numFor.Start) + ", " + RenderIl(numFor.Limit) + " do");
                Indent++;
                EmitLoopBody(numFor.Body, label, false);
                Indent--;
                AppendLine("end");
                PopContinueLabel();
                return;
            }
            case IlForeachList feList when feList.Counted:
            {
                int label = PushContinueLabel();
                AppendLine("do local __tcs_l = " + RenderIl(feList.Coll));
                AppendLine("for __tcs_i = 1, (__tcs_l.n or #__tcs_l) do");
                Indent++;
                AppendLine("local " + feList.Var + " = __tcs_l[__tcs_i]");
                EmitLoopBody(feList.Body, label, false);
                Indent--;
                AppendLine("end end");
                PopContinueLabel();
                return;
            }
            case IlForeachList feList:
            {
                int label = PushContinueLabel();
                AppendLine("for _, " + feList.Var + " in ipairs(" + RenderIl(feList.Coll) + ") do");
                Indent++;
                EmitLoopBody(feList.Body, label, false);
                Indent--;
                AppendLine("end");
                PopContinueLabel();
                return;
            }
            case IlForeachRunes feRunes:
            {
                int label = PushContinueLabel();
                AppendLine("for _, " + feRunes.Var + " in " + Utf8ModuleGlobal + ".codes(" + RenderIl(feRunes.Str) + ") do");
                Indent++;
                EmitLoopBody(feRunes.Body, label, false);
                Indent--;
                AppendLine("end");
                PopContinueLabel();
                return;
            }
            case IlForeachDict feDict:
            {
                int label = PushContinueLabel();
                var v = feDict.Var;
                AppendLine("for " + v + "_key, " + v + "_value in pairs(" + RenderIl(feDict.Coll) + ") do");
                Indent++;
                AppendLine("local " + v + " = {Key = " + v + "_key, Value = " + v + "_value}");
                EmitLoopBody(feDict.Body, label, false);
                Indent--;
                AppendLine("end");
                PopContinueLabel();
                return;
            }
            case IlDo doStat:
                AppendLine("do");
                Indent++;
                EmitIlBlock(doStat.Body, null);
                Indent--;
                AppendLine("end");
                return;
            case IlBreakScope scope:
                // switch に束縛された break の脱出先。continue の goto は外側のループの label へそのまま飛べる
                AppendLine("repeat");
                Indent++;
                EmitIlBlock(scope.Body, null);
                Indent--;
                AppendLine("until true");
                return;
            case IlMultiAssign multi:
                AppendLine(RenderIlMultiAssign(multi));
                return;
            case IlForPairs forPairs:
            {
                int label = PushContinueLabel();
                AppendLine("for " + RenderIlPairsHead(forPairs) + " do");
                Indent++;
                EmitLoopBody(forPairs.Body, label, false);
                Indent--;
                AppendLine("end");
                PopContinueLabel();
                return;
            }
        }
        Internal("unhandled IL statement");
    }

    // List / 配列の要素への代入は範囲を見る (il-spec §12。Lua の table は範囲外の key にも書ける)。
    // 受け手、添字、値の順に評価してから範囲を見る (C# の順序)。値の呼び出しが受け手や添字の変数を書き換えうるので、
    // そのときは受け手と添字を先に local に取る。どれも読み直せるなら helper を呼ばずに範囲を見る
    private string RenderIlAssign(IlAssign assign)
    {
        if (!(assign.Target is IlIndex ix) || !ix.PlusOne || ix.Unchecked)
            return RenderIl(assign.Target) + " = " + RenderIl(assign.Value);
        var recv = RenderIl(ix.Recv);
        var idx = RenderIl(ix.Idx);
        var value = RenderIl(assign.Value);
        if (!IsCallFree(ix.Recv) || !IsCallFree(ix.Idx))
            return (ix.NilElements ? "__tcs_lset" : "__tcs_set") + "(" + recv + ", " + idx + ", " + value + ")";
        // 要素が null になりうる List は nil の slot が範囲内かもしれないので長さと比べる
        if (!IsCallFree(assign.Value))
        {
            var fault = ix.NilElements ? "__tcs_lchk(__tcs_r, __tcs_i)" : "__tcs_oob()";
            return "do local __tcs_r, __tcs_i = " + recv + ", " + idx + " local __tcs_v = " + value + " "
                + "if __tcs_r[__tcs_i + 1] == nil then " + fault + " end __tcs_r[__tcs_i + 1] = __tcs_v end";
        }
        var f = ix.NilElements ? "__tcs_lchk(" + recv + ", " + idx + ")" : "__tcs_oob()";
        return "if " + recv + "[" + idx + " + 1] == nil then " + f + " end " + recv + "[" + idx + " + 1] = " + value;
    }

    private string RenderIlMultiAssign(IlMultiAssign multi)
    {
        return (multi.Declare ? "local " : "") + JoinIl(multi.Targets, ", ") + " = " + JoinIl(multi.Values, ", ");
    }

    private string JoinIl(List<IlExpr> items, string sep)
    {
        var parts = new List<string>();
        foreach (var x in items) parts.Add(RenderIl(x));
        return string.Join(sep, parts);
    }

    private string RenderIlPairsHead(IlForPairs forPairs)
    {
        if (forPairs.VVar != null)
            return forPairs.KVar + ", " + forPairs.VVar + " in pairs(" + RenderIl(forPairs.Coll) + ")";
        return forPairs.KVar + " in pairs(" + RenderIl(forPairs.Coll) + ")";
    }

    // IIFE / do の 1 行の描画。複数行の構造 (while / repeat など) は IIFE の中に置かない (builder の約束)
    private string RenderIlStatInline(IlStat stat)
    {
        switch (stat)
        {
            case IlLocal local:
                if (local.Init != null) return "local " + local.Name + " = " + RenderIl(local.Init);
                return "local " + local.Name;
            case IlAssign assign:
                return RenderIlAssign(assign);
            case IlMultiAssign multi:
                return RenderIlMultiAssign(multi);
            case IlCallStat call:
                return TryRenderListAddStat(call.Call, "", "") ?? RenderIl(call.Call);
            case IlReturn ret:
                if (ret.Value != null) return "return " + RenderIl(ret.Value);
                return "return";
            case IlIf ifStat:
                return RenderIlIfInline(ifStat);
            case IlForPairs forPairs:
                return "for " + RenderIlPairsHead(forPairs) + " do " + RenderIlStatsInline(forPairs.Body.Stats) + " end";
        }
        Internal("IL statement not inline-renderable");
        return "";
    }

    private string RenderIlStatsInline(List<IlStat> stats)
    {
        var parts = new List<string>();
        foreach (var s in stats) parts.Add(RenderIlStatInline(s));
        return string.Join("; ", parts);
    }

    private string RenderIlIfInline(IlIf ifStat)
    {
        var parts = new List<string>();
        for (int i = 0; i < ifStat.Arms.Count; i++)
        {
            var arm = ifStat.Arms[i];
            parts.Add(i == 0 ? "if " : " elseif ");
            parts.Add(RenderIl(arm.Cond));
            parts.Add(" then ");
            parts.Add(RenderIlStatsInline(arm.Body.Stats));
        }
        if (ifStat.Else != null)
        {
            parts.Add(" else ");
            parts.Add(RenderIlStatsInline(ifStat.Else.Stats));
        }
        parts.Add(" end");
        return string.Join("", parts);
    }

    // block の本体を文字列にする (出力の状態を退避して戻す)
    private string RenderIlClosureBlock(IlBlock body)
    {
        var savedChunks = Chunks;
        int savedIndent = Indent;
        int savedLuaLine = LuaLine;
        var savedFile = SourceFile;
        int savedLine = SourceLine;
        Chunks = new List<string>();
        Indent = 0;

        EmitIlBlock(body, null);

        var rendered = string.Join("", Chunks).Trim();
        RemoveSourceMapFrom(savedLuaLine);
        Chunks = savedChunks;
        Indent = savedIndent;
        LuaLine = savedLuaLine;
        SourceFile = savedFile;
        SourceLine = savedLine;
        return rendered;
    }

    private void EmitIlIf(IlIf ifStat)
    {
        for (int i = 0; i < ifStat.Arms.Count; i++)
        {
            if (i > 0)
            {
                var tail = TracedElseIf(ifStat, i);
                if (tail != null)
                {
                    AppendLine("else");
                    Indent++;
                    EmitIlStat(tail, true);
                    Indent--;
                    AppendLine("end");
                    return;
                }
            }
            var arm = ifStat.Arms[i];
            AppendLine((i == 0 ? "if" : "elseif") + " " + RenderIl(arm.Cond) + " then");
            Indent++;
            EmitIlBlock(arm.Body, null);
            Indent--;
        }
        if (ifStat.Else != null)
        {
            AppendLine("else");
            Indent++;
            EmitIlBlock(ifStat.Else, null);
            Indent--;
        }
        AppendLine("end");
    }

    private void EmitIlWhile(IlWhile whileStat)
    {
        int label = PushContinueLabel();
        AppendLine("while " + RenderIl(whileStat.Cond) + " do");
        Indent++;
        if (whileStat.ScopeBody)
        {
            AppendLine("do");
            Indent++;
        }
        EmitLoopBody(whileStat.Body, label, whileStat.Trailer != null && !whileStat.ScopeBody);
        if (whileStat.ScopeBody)
        {
            Indent--;
            AppendLine("end");
        }
        if (whileStat.Trailer != null) EmitIlBlock(whileStat.Trailer, null);
        Indent--;
        AppendLine("end");
        PopContinueLabel();
    }

    private static string RenderIlOp(IlBinOp op)
    {
        switch (op)
        {
            case IlBinOp.AddNum: return "+";
            case IlBinOp.Concat: return "..";
            case IlBinOp.Sub: return "-";
            case IlBinOp.Mul: return "*";
            case IlBinOp.DivNum: return "/";
            case IlBinOp.RemNum: return "%";
            case IlBinOp.Eq: return "==";
            case IlBinOp.Ne: return "~=";
            case IlBinOp.Lt: return "<";
            case IlBinOp.Le: return "<=";
            case IlBinOp.Gt: return ">";
            case IlBinOp.Ge: return ">=";
            case IlBinOp.And: return "and";
            case IlBinOp.Or: return "or";
            case IlBinOp.BitAnd: return "&";
            case IlBinOp.BitOr: return "|";
            case IlBinOp.BitXor: return "~";
            case IlBinOp.Shl: return "<<";
        }
        return ">>";
    }

    public string RenderIl(IlExpr expr)
    {
        switch (expr)
        {
            case IlLit lit:
                return lit.LuaText;
            case IlVar v:
                return v.Name;
            case IlField f:
                return RenderIl(f.Recv) + "." + f.Name;
            case IlIndex ix:
                return RenderIlIndex(ix);
            case IlLen len:
                if (len.NilElements)
                {
                    if (IsSimpleReceiverPath(len.E)) return "(" + RenderIl(len.E) + ".n or #" + RenderIl(len.E) + ")";
                    return "__tcs_count(" + RenderIl(len.E) + ")";
                }
                return "#" + RenderIl(len.E);
            case IlBin bin:
                // シフトは C# の意味 (count & 31、>> は算術) を helper で与える。Lua の >> は論理で、count はマスクしない
                if (bin.Op == IlBinOp.Shl) return "__tcs_shl(" + RenderIl(bin.L) + ", " + RenderIl(bin.R) + ")";
                if (bin.Op == IlBinOp.Shr) return "__tcs_shr(" + RenderIl(bin.L) + ", " + RenderIl(bin.R) + ")";
                return RenderIl(bin.L) + " " + RenderIlOp(bin.Op) + " " + RenderIl(bin.R);
            case IlUn un:
                if (un.Op == IlUnOp.Neg) return NegateText(RenderIl(un.E));
                if (un.Op == IlUnOp.Not) return "not " + RenderIl(un.E);
                return "~" + RenderIl(un.E);
            case IlParen p:
                return "(" + RenderIl(p.E) + ")";
            case IlTernary t:
                return "(function() if " + RenderIl(t.Cond) + " then return " + RenderIl(t.T) + " else return " + RenderIl(t.F) + " end end)()";
            case IlCall call:
                return RenderIlCall(call);
            case IlDynCall dyn:
                return RenderIl(dyn.Callee) + "(" + JoinIl(dyn.Args, ", ") + ")";
            case IlInvoke inv:
                return RenderIl(inv.Recv) + ":" + inv.Method + "(" + JoinIl(inv.Args, ", ") + ")";
            case IlNewObj obj:
                return obj.TypeName + "." + obj.Ctor + "(" + JoinIl(obj.Args, ", ") + ")";
            case IlTable table:
                return RenderIlTable(table);
            // 値型の要素は default を n 個詰める (要素の読み出し / Length が C# と一致)。参照型 (null) は穴を作れないので空の table
            case IlNewQueue:
                return "TcsQueue.new()";
            case IlNewHashSet:
                return "TcsHashSet.new()";
            case IlNewArray na:
                return RenderIlNewArray(na);
            case IlStructDescriptor descriptor:
                return RenderStructDescriptor(descriptor, false);
            case IlNumericConvert convert:
                return RenderIlNumericConvert(convert);
            case IlRefCast refCast:
                if (refCast.LuaCheck != null) return refCast.LuaCheck + "(" + RenderIl(refCast.Value) + ", " + RenderIl(refCast.LuaCheckArg!) + ")";
                return RenderIl(refCast.Value);
            case IlIsType isType:
                if (isType.IsInterface) return "__tcs_implements(" + RenderIl(isType.E) + ", " + EscapeLuaString(isType.TypeRef) + ")";
                return "__tcs_is(" + RenderIl(isType.E) + ", " + isType.TypeRef + ")";
            case IlStructCopy copy:
                if (copy.Nullable)
                {
                    if (copy.Copiers == "") return "__tcs_nlift1(" + RenderIl(copy.E) + ", " + copy.TypeName + ".__copy)";
                    return "__tcs_nlift1(" + RenderIl(copy.E) + ", function(v) return " + copy.TypeName + ".__copy(v" + copy.Copiers + ") end)";
                }
                return copy.TypeName + ".__copy(" + RenderIl(copy.E) + copy.Copiers + ")";
            case IlCast cast:
                return "__tcs_cast(" + RenderIl(cast.E) + ", " + cast.LuaRef + ")";
            case IlNullableWrap wrap:
                return RenderIl(wrap.E);
            case IlNullableHasValue has:
                return "(" + RenderIl(has.E) + " ~= nil)";
            case IlNullableValue val:
                return "__tcs_nval(" + RenderIl(val.E) + ")";
            case IlNullableGetOrDefault gd:
                // ?? の右辺は左が nil のときだけ評価する (評価が fault しうるなら IIFE で遅らせる)
                if (CannotFault(gd.Default)) return "__tcs_nget(" + RenderIl(gd.E) + ", " + RenderIl(gd.Default) + ")";
                return "(function() local __tcs_v = " + RenderIl(gd.E) + "; if __tcs_v ~= nil then return __tcs_v end return " + RenderIl(gd.Default) + " end)()";
            case IlLiftedBin lifted:
                return RenderIlLifted(lifted);
            case IlLiftedUn liftedUn:
                if (liftedUn.Op == IlUnOp.Neg) return "__tcs_nlift1(" + RenderIl(liftedUn.E) + ", __tcs_op_neg)";
                if (liftedUn.Op == IlUnOp.ToFloat) return "__tcs_nlift1(" + RenderIl(liftedUn.E) + ", __tcs_op_tofloat)";
                if (liftedUn.Op == IlUnOp.BitNot) return "__tcs_nlift1(" + RenderIl(liftedUn.E) + ", __tcs_op_bnot)";
                return "__tcs_nnot(" + RenderIl(liftedUn.E) + ")";
            case IlIsLuaType isLua:
                return "type(" + RenderIl(isLua.E) + ") == \"" + isLua.LuaType + "\"";
            case IlValues values:
                return JoinIl(values.Items, ", ");
            case IlIife iife:
                return "(function() " + RenderIlStatsInline(iife.Stats) + " end)()";
            case IlClosure closure:
                return RenderIlClosure(closure);
            case IlMethodGroup group:
                return RenderIlMethodGroup(group);
            case IlWith with:
                return RenderIlWith(with);
        }
        Internal("unhandled IL expression");
        return "";
    }

    // List / 配列の要素の読み出しは範囲外を fault にする。値型の要素は nil を持たないので、nil を読んだら範囲外。
    // false になりうる要素は nil と比べる (受け手と添字を読み直せるなら helper を呼ばない)。
    // 要素が null になりうる List は、nil か false を読んだときだけ長さと比べる
    private string RenderIlIndex(IlIndex ix)
    {
        if (ix.PlusOne && !ix.Unchecked)
        {
            bool free = IsCallFree(ix.Recv) && IsCallFree(ix.Idx);
            if (ix.NilElements)
            {
                if (free) return "(" + RenderIl(ix.Recv) + "[" + RenderIl(ix.Idx) + " + 1] or __tcs_lget(" + RenderIl(ix.Recv) + ", " + RenderIl(ix.Idx) + "))";
                return "__tcs_lget(" + RenderIl(ix.Recv) + ", " + RenderIl(ix.Idx) + ")";
            }
            if (ix.BoolElements)
            {
                if (free)
                {
                    var r = RenderIl(ix.Recv);
                    var i = RenderIl(ix.Idx);
                    return "(" + r + "[" + i + " + 1] or (" + RenderIl(ix.Recv) + "[" + RenderIl(ix.Idx) + " + 1] ~= false and __tcs_oob() or false))";
                }
                return "__tcs_get(" + RenderIl(ix.Recv) + ", " + RenderIl(ix.Idx) + ")";
            }
            return "(" + RenderIl(ix.Recv) + "[" + RenderIl(ix.Idx) + " + 1] or __tcs_oob())";
        }
        return RenderIl(ix.Recv) + "[" + RenderIl(ix.Idx) + (ix.PlusOne ? " + 1" : "") + "]";
    }

    private string RenderIlCall(IlCall call)
    {
        if (call.NilElements && call.Callee == "table.insert")
            return "List.AddCounted(" + RenderIl(call.Args[0]) + ", " + RenderIl(call.Args[1]) + ")";
        if (call.NilElements && call.Callee == "table.remove")
            return "List.RemoveSlot(" + RenderIl(call.Args[0]) + ", " + RenderIl(call.Args[1]) + ")";
        // Floor / Ceil は float のまま丸める // で描く (math.floor / ceil は整数へ変換して -0 の符号を落とす。runtime の wrapper と同じ式)
        if (call.Callee == "Math.Floor" && call.Args.Count == 1)
            return "((" + RenderIl(call.Args[0]) + ") // 1)";
        if (call.Callee == "Math.Ceil" && call.Args.Count == 1)
            return "(-(-(" + RenderIl(call.Args[0]) + ") // 1))";
        // float の key の NaN の置換は、読み直しても値が変わらない key なら inline (sentinel は runtime の Dict.FloatKey と同じ "NaN")
        if (call.Callee == "Dict.FloatKey" && call.Args.Count == 1 && IsCallFree(call.Args[0]))
        {
            var k = RenderIl(call.Args[0]);
            return "(" + k + " ~= " + RenderIl(call.Args[0]) + " and \"NaN\" or " + RenderIl(call.Args[0]) + ")";
        }
        return LuaCallee(call.Callee, call.Args.Count) + "(" + JoinIl(call.Args, ", ") + ")";
    }

    private string RenderIlNewArray(IlNewArray na)
    {
        var d = na.Default;
        if (d is IlNewObj zero)
        {
            if (zero.Args.Count == 0) return "__tcs_arr(" + RenderIl(na.Length) + ", " + zero.TypeName + ".new)";
            return "__tcs_arr(" + RenderIl(na.Length) + ", function() return " + RenderIl(zero) + " end)";
        }
        if (d is IlLit lit && lit.LuaText != "nil") return "__tcs_arr(" + RenderIl(na.Length) + ", " + lit.LuaText + ")";
        // 型引数の要素: 受け取った既定値 (struct の記述子なら要素ごとに zero 値、nil なら空)
        if (d is IlVar || d is IlField) return "__tcs_arr(" + RenderIl(na.Length) + ", " + RenderIl(d) + ")";
        // Utf8Bytes: 既定値は変わらないので共有できる
        if (d is IlCall dc && dc.Callee == "TinySystem.Utf8.Default") return "__tcs_arr(" + RenderIl(na.Length) + ", " + RenderIl(d) + ")";
        return "{}";
    }

    // int → float は `* 1.0` (LUA_32BITS は integer を f32 へ丸める。+ 0.0 だと float の -0 が +0 になる)。float → int は __tcs_trunc
    private string RenderIlNumericConvert(IlNumericConvert convert)
    {
        if (convert.LuaCheck != null) return "__tcs_ncast(" + RenderIl(convert.Value) + ", " + EscapeLuaString(convert.LuaCheck) + ")";
        if (convert.TargetType == "float")
        {
            // float.Parse: 不正な文字列の nil を保ったまま float にする
            if (convert.Value is IlCall parse && parse.Callee == "tonumber") return "__tcs_tofloat(" + RenderIl(parse) + ")";
            if (convert.Value is IlBin) return "((" + RenderIl(convert.Value) + ") * 1.0)";
            return "(" + RenderIl(convert.Value) + " * 1.0)";
        }
        return RenderIl(convert.Value);
    }

    public string RenderStructDescriptor(IlStructDescriptor descriptor, bool inline)
    {
        if (descriptor.NewArgs.Count == 0 && descriptor.Copiers == "" && !inline)
            return descriptor.TypeName + ".__tcs_td";
        var s = "{__tcs_td = true, n = function() return " + descriptor.TypeName + ".new(" + JoinIl(descriptor.NewArgs, ", ") + ") end, "
            + "c = function(v) return " + descriptor.TypeName + ".__copy(v" + descriptor.Copiers + ") end";
        if (descriptor.HasToString)
            s = s + ", s = function(v) return " + descriptor.TypeName + ".ToString(v) or \"\" end";
        return s + "}";
    }

    private static string RenderIlMethodGroup(IlMethodGroup group)
    {
        var fn = group.TypeRef + "." + group.Method;
        bool allNull = true;
        foreach (var t in group.CopyTypes)
        {
            if (t != null) allNull = false;
        }
        if (allNull) return fn;
        var copies = new List<string>();
        foreach (var t in group.CopyTypes) copies.Add(t ?? "false");
        return "__tcs_mg(" + fn + ", " + string.Join(", ", copies) + ")";
    }

    private string RenderIlClosure(IlClosure closure)
    {
        var paramList = string.Join(", ", closure.Params);
        var locals = closure.PatternLocals.Count > 0 ? "local " + string.Join(", ", closure.PatternLocals) + "; " : "";
        // block の本体の lambda は文を trace するので、自分の frame の __tcs_at を持つ
        var trace = TraceFunction && closure.Body != null ? TraceLocal + "; " : "";
        if (closure.ExprBody != null)
        {
            if (Needs(closure.ExprBody))
            {
                var body = new List<IlStat>();
                foreach (var n in closure.PatternLocals) body.Add(new IlLocal(n, null, null, null));
                body.Add(new IlReturn(closure.ExprBody, null));
                return "function(" + paramList + ") " + RenderIlClosureBlock(new IlBlock(body)) + " end";
            }
            var add = TryRenderListAddStat(closure.ExprBody, "", "");
            if (add != null) return "function(" + paramList + ") " + locals + add + " end";
            return "function(" + paramList + ") " + locals + "return " + RenderIl(closure.ExprBody) + " end";
        }
        return "function(" + paramList + ") " + trace + locals + RenderIlClosureBlock(closure.Body!) + " end";
    }

    // lifted の演算: 片方でも nil なら nil (比較は false)。op の関数は prelude の定数なので closure は作らない。
    // Eq / Ne は Lua の == がそのまま (nil == nil は true、nil と値は false)
    private string RenderIlLifted(IlLiftedBin lifted)
    {
        var l = RenderIl(lifted.L);
        var r = RenderIl(lifted.R);
        switch (lifted.Op)
        {
            case IlLiftedOp.Eq: return "(" + l + " == " + r + ")";
            case IlLiftedOp.Ne: return "(" + l + " ~= " + r + ")";
            case IlLiftedOp.Lt: return "__tcs_ncmp(" + l + ", " + r + ", __tcs_op_lt)";
            case IlLiftedOp.Le: return "__tcs_ncmp(" + l + ", " + r + ", __tcs_op_le)";
            case IlLiftedOp.Gt: return "__tcs_ncmp(" + l + ", " + r + ", __tcs_op_gt)";
            case IlLiftedOp.Ge: return "__tcs_ncmp(" + l + ", " + r + ", __tcs_op_ge)";
            case IlLiftedOp.And: return "__tcs_nand(" + l + ", " + r + ")";
            case IlLiftedOp.Or: return "__tcs_nor(" + l + ", " + r + ")";
        }
        return "__tcs_nlift(" + l + ", " + r + ", __tcs_op_" + LiftedOpName(lifted.Op) + ")";
    }

    private static string LiftedOpName(IlLiftedOp op)
    {
        switch (op)
        {
            case IlLiftedOp.Add: return "add";
            case IlLiftedOp.Sub: return "sub";
            case IlLiftedOp.Mul: return "mul";
            case IlLiftedOp.DivFloat: return "div";
            case IlLiftedOp.DivInt: return "idiv";
            case IlLiftedOp.RemFloat: return "fmod";
            case IlLiftedOp.RemInt: return "irem";
            case IlLiftedOp.BitAnd: return "band";
            case IlLiftedOp.BitOr: return "bor";
            case IlLiftedOp.BitXor: return "bxor";
            case IlLiftedOp.Shl: return "shl";
            case IlLiftedOp.Shr: return "shr";
        }
        return "";
    }

    // `--` は Lua では行のコメントになるので、`-` で始まる operand は括弧で包む
    private static string NegateText(string operand)
    {
        if (operand.StartsWith("-")) return "-(" + operand + ")";
        return "-" + operand;
    }

    private string RenderIlWith(IlWith with)
    {
        var assigns = new List<string>();
        foreach (var o in with.Overrides) assigns.Add("__tcs_copy." + o.Name + " = " + RenderIl(o.Value) + "; ");
        string copy;
        if (with.Src is IlStructCopy)
            copy = "local __tcs_copy = " + RenderIl(with.Src) + "; ";
        else
            copy = "local __tcs_src = " + RenderIl(with.Src) + "; "
                + "local __tcs_copy = {}; "
                + "for k,v in pairs(__tcs_src) do __tcs_copy[k] = v end; "
                + "setmetatable(__tcs_copy, getmetatable(__tcs_src)); ";
        return "(function() " + copy + string.Join("", assigns) + "return __tcs_copy end)()";
    }

    private string RenderIlTable(IlTable table)
    {
        if (table.Entries.Count == 0) return "{}";
        var parts = new List<string>();
        foreach (var e in table.Entries)
        {
            if (e.NameKey != null) parts.Add(e.NameKey + " = " + RenderIl(e.Value));
            else if (e.Key != null) parts.Add("[" + RenderIl(e.Key) + "] = " + RenderIl(e.Value));
            else parts.Add(RenderIl(e.Value));
        }
        if (table.NilElements) parts.Add("n = " + table.Entries.Count);
        return "{" + string.Join(", ", parts) + "}";
    }

    // List.Add の文の位置の lowering: `table.insert(t, v)` は C 関数の呼び出しの分だけ遅いので `t[#t + 1] = v` を出す。
    // 文の位置でだけ使う (式の位置の table.insert は IlCall のまま)。受け手は変数 / field の連鎖に限る (1 回の評価の保証)。
    // 引数が呼び出しを含むときは `local __tcs_v = v` に先に束縛する (Lua は `t[#t + 1] = v` で `#t` を右辺より先に評価する)。
    // 要素が null になりうる List は `n` を進める
    private string? TryRenderListAddStat(IlExpr expr, string open, string close)
    {
        if (!(expr is IlCall call) || call.Callee != "table.insert" || call.Args.Count != 2) return null;
        var recv = call.Args[0];
        if (!IsSimpleReceiverPath(recv)) return null;
        var t = RenderIl(recv);
        var value = RenderIl(call.Args[1]);
        bool free = IsCallFree(call.Args[1]);
        if (!call.NilElements)
        {
            if (free) return t + "[#" + t + " + 1] = " + value;
            return open + "local __tcs_v = " + value + "; " + t + "[#" + t + " + 1] = __tcs_v" + close;
        }
        var store = "local __tcs_n = (" + t + ".n or #" + t + ") + 1; " + t + "[__tcs_n] = " + (free ? value : "__tcs_v") + "; " + t + ".n = __tcs_n";
        return "do " + (free ? "" : "local __tcs_v = " + value + "; ") + store + " end";
    }

    private static bool IsSimpleReceiverPath(IlExpr e)
    {
        if (e is IlVar) return true;
        if (e is IlField f) return IsSimpleReceiverPath(f.Recv);
        return false;
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
                return n.LuaCheck == null && IsCallFree(n.Value);
            case IlNullableWrap w:
                return IsCallFree(w.E);
            case IlNullableHasValue h:
                return IsCallFree(h.E);
            case IlIsType it:
                return IsCallFree(it.E);
            case IlIsLuaType ilt:
                return IsCallFree(ilt.E);
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

    // 評価しても fault せず値も変えない式 (呼び出しが無くても field の参照や除算は fault する)
    private static bool CannotFault(IlExpr e)
    {
        if (e is IlLit || e is IlVar) return true;
        if (e is IlParen p) return CannotFault(p.E);
        if (e is IlNullableWrap w) return CannotFault(w.E);
        return false;
    }

    // runtime の Math.* の wrapper のうち、本体が Lua の builtin / tcs_math への素通しのものはそれを直接呼び、
    // Min / Max は chunk の prelude の helper を呼ぶ (意味は wrapper と同じ)。
    // Round / Sign / Clamp / Pow / TryParse* と底付きの Log は変換を持つので wrapper のまま
    private static string LuaCallee(string callee, int argCount)
    {
        switch (callee)
        {
            // Utf8Bytes の添字は範囲外を fault にする
            case "TinySystem.Utf8.At": return "__tcs_u8at";
            case "Math.Min": return "__tcs_min";
            case "Math.Max": return "__tcs_max";
            case "Math.Abs": return "math.abs";
            case "Math.Sqrt": return "math.sqrt";
            case "Math.Sin": return MathModuleGlobal + ".sin";
            case "Math.Cos": return MathModuleGlobal + ".cos";
            case "Math.Tan": return MathModuleGlobal + ".tan";
            case "Math.Exp": return MathModuleGlobal + ".exp";
            case "Math.Atan2": return MathModuleGlobal + ".atan2";
        }
        if (callee == "Math.Log" && argCount == 1) return MathModuleGlobal + ".log";
        return callee;
    }

    // user の演算子は Lua の metamethod (__add など) になり任意のコードを呼ぶ
    private static bool IsOverloadableOp(IlBinOp op)
    {
        return op == IlBinOp.AddNum || op == IlBinOp.Sub || op == IlBinOp.Mul || op == IlBinOp.DivNum || op == IlBinOp.RemNum;
    }

    // 値がプリミティブ (class の instance ではない) と IL の形から言える式。IlVar / field などは型が分からないので false
    private static bool IsPrimitive(IlExpr e)
    {
        if (e is IlLit || e is IlLen || e is IlIsType || e is IlIsLuaType || e is IlNullableHasValue || e is IlNumericConvert) return true;
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

    private static bool IsPureCallee(string callee)
    {
        return callee.StartsWith("__tcs_")
            || callee.StartsWith("math.")
            || callee.StartsWith(MathModuleGlobal + ".")
            || callee.StartsWith("string.")
            || callee.StartsWith("Math.")
            || callee.StartsWith("String.")
            || callee == "tostring"
            || callee == "tonumber";
    }
}
