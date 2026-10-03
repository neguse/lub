using System.Collections.Immutable;

namespace TinyCs;

// IL → Lua emit。SemanticModel には依存しない (意味決定は builder 側で完了
// している)。出力は legacy visitor と同形 (移行中は挙動不変が条件)。
public partial class LuaEmitter
{
    /// <summary>reload chunk (HotReload.cs) が field initializer IL を
    /// migration 式として render するための公開面。</summary>
    public string RenderIlExpr(IlExpr expr) => RenderIl(expr);

    private void EmitIlBlock(IlBlock block)
    {
        foreach (var stat in block.Stats)
            EmitIlStat(stat);
    }

    private void EmitIlStat(IlStat stat)
    {
        if (stat.Origin != null) SetSource(stat.Origin);
        switch (stat)
        {
            case IlLocal local:
                AppendLine(local.Init != null
                    ? $"local {local.Name} = {RenderIl(local.Init)}"
                    : $"local {local.Name}");
                break;
            case IlAssign assign:
                AppendLine($"{RenderIl(assign.Target)} = {RenderIl(assign.Value)}");
                break;
            case IlCallStat call:
                // 引数束縛の local は後続文へ漏れないよう do ... end で閉じる
                AppendLine(TryRenderListAddStat(call.Call, "do ", " end")
                    ?? RenderIl(call.Call));
                break;
            case IlReturn ret:
                // expression-bodied void member (`=> list.Add(v)`) の
                // `return table.insert(...)` は値を返さないので文形に落とす
                // (関数末尾なので local は漏れない)
                if (ret.Value != null && TryRenderListAddStat(ret.Value) is { } add)
                {
                    AppendLine(add);
                    break;
                }
                AppendLine(ret.Value != null
                    ? $"return {RenderIl(ret.Value)}" : "return");
                break;
            case IlBreak:
                AppendLine("break");
                break;
            case IlContinue:
                if (_continueStack.Count > 0)
                {
                    var lbl = _continueStack.Peek();
                    _usedContinueLabels.Add(lbl);
                    AppendLine($"goto _continue_{lbl}");
                }
                break;
            case IlIf ifStat:
                EmitIlIf(ifStat);
                break;
            case IlWhile whileStat:
                EmitIlWhile(whileStat);
                break;
            case IlRepeat repeat:
            {
                var label = PushContinueLabel();
                AppendLine("repeat");
                _indent++;
                EmitIlBlock(repeat.Body);
                EmitContinueLabel(label);
                _indent--;
                AppendLine($"until not ({RenderIl(repeat.Cond)})");
                PopContinueLabel();
                break;
            }
            case IlNumericFor numFor:
            {
                var label = PushContinueLabel();
                AppendLine($"for {numFor.Var} = {RenderIl(numFor.Start)}, " +
                    $"{RenderIl(numFor.Limit)} do");
                _indent++;
                EmitIlBlock(numFor.Body);
                EmitContinueLabel(label);
                _indent--;
                AppendLine("end");
                PopContinueLabel();
                break;
            }
            case IlForeachList feList:
            {
                var label = PushContinueLabel();
                AppendLine($"for _, {feList.Var} in ipairs({RenderIl(feList.Coll)}) do");
                _indent++;
                EmitIlBlock(feList.Body);
                EmitContinueLabel(label);
                _indent--;
                AppendLine("end");
                PopContinueLabel();
                break;
            }
            case IlForeachRunes feRunes:
            {
                var label = PushContinueLabel();
                AppendLine($"for _, {feRunes.Var} in utf8.codes({RenderIl(feRunes.Str)}) do");
                _indent++;
                EmitIlBlock(feRunes.Body);
                EmitContinueLabel(label);
                _indent--;
                AppendLine("end");
                PopContinueLabel();
                break;
            }
            case IlForeachDict feDict:
            {
                var label = PushContinueLabel();
                var v = feDict.Var;
                AppendLine($"for {v}_key, {v}_value in pairs({RenderIl(feDict.Coll)}) do");
                _indent++;
                AppendLine($"local {v} = {{Key = {v}_key, Value = {v}_value}}");
                EmitIlBlock(feDict.Body);
                EmitContinueLabel(label);
                _indent--;
                AppendLine("end");
                PopContinueLabel();
                break;
            }
            case IlDo doStat:
                AppendLine("do");
                _indent++;
                EmitIlBlock(doStat.Body);
                _indent--;
                AppendLine("end");
                break;
            case IlBreakScope scope:
                // switch 束縛 break の脱出先。continue の goto は外側ループの
                // label へそのまま飛べる (外側 block の label は可視)
                AppendLine("repeat");
                _indent++;
                EmitIlBlock(scope.Body);
                _indent--;
                AppendLine("until true");
                break;
            case IlComment comment:
                AppendLine(comment.Text);
                break;
            case IlMultiAssign multi:
                AppendLine(RenderIlMultiAssign(multi));
                break;
            case IlForPairs forPairs:
            {
                var label = PushContinueLabel();
                AppendLine($"for {RenderIlPairsHead(forPairs)} do");
                _indent++;
                EmitIlBlock(forPairs.Body);
                EmitContinueLabel(label);
                _indent--;
                AppendLine("end");
                PopContinueLabel();
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"unhandled IL statement: {stat.GetType().Name}");
        }
    }

    private string RenderIlMultiAssign(IlMultiAssign multi) =>
        $"{(multi.Declare ? "local " : "")}" +
        $"{string.Join(", ", multi.Targets.Select(RenderIl))} = " +
        $"{string.Join(", ", multi.Values.Select(RenderIl))}";

    private string RenderIlPairsHead(IlForPairs forPairs) =>
        forPairs.VVar != null
            ? $"{forPairs.KVar}, {forPairs.VVar} in pairs({RenderIl(forPairs.Coll)})"
            : $"{forPairs.KVar} in pairs({RenderIl(forPairs.Coll)})";

    // IIFE / do 圧縮用の 1 行 render。複数行構造 (while/repeat 等) は
    // IIFE 内に置かない builder 側契約。
    private string RenderIlStatInline(IlStat stat) => stat switch
    {
        IlLocal { Init: not null } local =>
            $"local {local.Name} = {RenderIl(local.Init)}",
        IlLocal local => $"local {local.Name}",
        IlAssign assign => $"{RenderIl(assign.Target)} = {RenderIl(assign.Value)}",
        IlMultiAssign multi => RenderIlMultiAssign(multi),
        IlCallStat call => TryRenderListAddStat(call.Call) ?? RenderIl(call.Call),
        IlReturn { Value: not null } ret => $"return {RenderIl(ret.Value)}",
        IlReturn => "return",
        IlIf ifStat => RenderIlIfInline(ifStat),
        IlForPairs forPairs =>
            $"for {RenderIlPairsHead(forPairs)} do " +
            $"{RenderIlStatsInline(forPairs.Body.Stats)} end",
        _ => throw new InvalidOperationException(
            $"IL statement not inline-renderable: {stat.GetType().Name}"),
    };

    private string RenderIlStatsInline(ImmutableArray<IlStat> stats) =>
        string.Join("; ", stats.Select(RenderIlStatInline));

    private string RenderIlIfInline(IlIf ifStat)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < ifStat.Arms.Length; i++)
        {
            var (cond, body) = ifStat.Arms[i];
            sb.Append(i == 0 ? "if " : " elseif ");
            sb.Append(RenderIl(cond));
            sb.Append(" then ");
            sb.Append(RenderIlStatsInline(body.Stats));
        }
        if (ifStat.Else != null)
        {
            sb.Append(" else ");
            sb.Append(RenderIlStatsInline(ifStat.Else.Stats));
        }
        sb.Append(" end");
        return sb.ToString();
    }

    // legacy VisitLambdaBlock と同じ save/restore で block body を文字列化する
    private string RenderIlClosureBlock(IlBlock body)
    {
        var savedSb = _sb.ToString();
        var savedIndent = _indent;
        var savedLuaLine = _luaLine;
        var savedSource = _currentSource;
        _sb.Clear();
        _indent = 0;

        EmitIlBlock(body);

        var rendered = _sb.ToString().Trim();
        SourceMap.RemoveFrom(savedLuaLine);
        _sb.Clear();
        _sb.Append(savedSb);
        _indent = savedIndent;
        _luaLine = savedLuaLine;
        _currentSource = savedSource;
        return rendered;
    }

    private void EmitIlIf(IlIf ifStat)
    {
        for (var i = 0; i < ifStat.Arms.Length; i++)
        {
            var (cond, body) = ifStat.Arms[i];
            AppendLine($"{(i == 0 ? "if" : "elseif")} {RenderIl(cond)} then");
            _indent++;
            EmitIlBlock(body);
            _indent--;
        }
        if (ifStat.Else != null)
        {
            AppendLine("else");
            _indent++;
            EmitIlBlock(ifStat.Else);
            _indent--;
        }
        AppendLine("end");
    }

    private void EmitIlWhile(IlWhile whileStat)
    {
        var label = PushContinueLabel();
        AppendLine($"while {RenderIl(whileStat.Cond)} do");
        _indent++;
        if (whileStat.ScopeBody)
        {
            AppendLine("do");
            _indent++;
        }
        EmitIlBlock(whileStat.Body);
        EmitContinueLabel(label);
        if (whileStat.ScopeBody)
        {
            _indent--;
            AppendLine("end");
        }
        if (whileStat.Trailer != null)
            EmitIlBlock(whileStat.Trailer);
        _indent--;
        AppendLine("end");
        PopContinueLabel();
    }

    private static string RenderIlOp(IlBinOp op) => op switch
    {
        IlBinOp.AddNum => "+",
        IlBinOp.Concat => "..",
        IlBinOp.Sub => "-",
        IlBinOp.Mul => "*",
        IlBinOp.DivNum => "/",
        IlBinOp.RemNum => "%",
        IlBinOp.Eq => "==",
        IlBinOp.Ne => "~=",
        IlBinOp.Lt => "<",
        IlBinOp.Le => "<=",
        IlBinOp.Gt => ">",
        IlBinOp.Ge => ">=",
        IlBinOp.And => "and",
        IlBinOp.Or => "or",
        IlBinOp.BitAnd => "&",
        IlBinOp.BitOr => "|",
        IlBinOp.BitXor => "~",
        IlBinOp.Shl => "<<",
        _ => ">>",
    };

    private string RenderIl(IlExpr expr) => expr switch
    {
        IlLit lit => lit.LuaText,
        IlVar v => v.Name,
        IlField f => $"{RenderIl(f.Recv)}.{f.Name}",
        IlIndex ix => $"{RenderIl(ix.Recv)}[{RenderIl(ix.Idx)}{(ix.PlusOne ? " + 1" : "")}]",
        IlLen len => $"#{RenderIl(len.E)}",
        // シフトは C# 意味論 (count & 31、>> は算術) を helper で与える。
        // Lua native の >> は論理、count は無マスク (il-spec §4 / support-matrix §4.2)
        IlBin { Op: IlBinOp.Shl } shl => $"__tcs_shl({RenderIl(shl.L)}, {RenderIl(shl.R)})",
        IlBin { Op: IlBinOp.Shr } shr => $"__tcs_shr({RenderIl(shr.L)}, {RenderIl(shr.R)})",
        IlBin bin => $"{RenderIl(bin.L)} {RenderIlOp(bin.Op)} {RenderIl(bin.R)}",
        IlUn { Op: IlUnOp.Neg } un => $"-{RenderIl(un.E)}",
        IlUn { Op: IlUnOp.Not } un => $"not {RenderIl(un.E)}",
        IlUn un => $"~{RenderIl(un.E)}",
        IlParen p => $"({RenderIl(p.E)})",
        IlTernary t =>
            $"(function() if {RenderIl(t.Cond)} then return {RenderIl(t.T)} " +
            $"else return {RenderIl(t.F)} end end)()",
        IlCall call =>
            $"{call.Callee}({string.Join(", ", call.Args.Select(RenderIl))})",
        IlDynCall dyn =>
            $"{RenderIl(dyn.Callee)}({string.Join(", ", dyn.Args.Select(RenderIl))})",
        IlInvoke inv =>
            $"{RenderIl(inv.Recv)}:{inv.Method}({string.Join(", ", inv.Args.Select(RenderIl))})",
        IlNewObj obj =>
            $"{obj.TypeName}.new({string.Join(", ", obj.Args.Select(RenderIl))})",
        IlTable table => RenderIlTable(table),
        // 値型要素は default を n 個詰める (要素読み / Length が C# と一致)。
        // 参照型 (null) は Lua table に穴を作れないので空 table
        IlNewArray na => na.Default switch
        {
            IlNewObj zero => $"__tcs_arr({RenderIl(na.Length)}, {zero.TypeName}.new)",
            IlLit { LuaText: not "nil" } lit => $"__tcs_arr({RenderIl(na.Length)}, {lit.LuaText})",
            _ => "{}",
        },
        IlNumericConvert convert => RenderIl(convert.Value),
        IlRefCast refCast => RenderIl(refCast.Value),
        IlIsType isType => $"__tcs_is({RenderIl(isType.E)}, {isType.TypeRef})",
        IlStructCopy copy => $"{copy.TypeName}.__copy({RenderIl(copy.E)})",
        IlCast cast => RenderIl(cast.E),
        IlNullableWrap wrap => RenderIl(wrap.E),
        IlNullableHasValue has => $"({RenderIl(has.E)} ~= nil)",
        IlNullableValue val => $"__tcs_nval({RenderIl(val.E)})",
        // ?? の右辺は左が nil のときだけ評価 (呼び出しを含めば IIFE で遅延)
        IlNullableGetOrDefault gd => IsCallFree(gd.Default)
            ? $"__tcs_nget({RenderIl(gd.E)}, {RenderIl(gd.Default)})"
            : $"(function() local __tcs_v = {RenderIl(gd.E)}; " +
              $"if __tcs_v ~= nil then return __tcs_v end return {RenderIl(gd.Default)} end)()",
        IlLiftedBin lifted => RenderIlLifted(lifted),
        IlLiftedUn liftedUn => liftedUn.Op switch
        {
            IlUnOp.Neg => $"__tcs_nlift1({RenderIl(liftedUn.E)}, __tcs_op_neg)",
            IlUnOp.BitNot => $"__tcs_nlift1({RenderIl(liftedUn.E)}, __tcs_op_bnot)",
            _ => $"__tcs_nnot({RenderIl(liftedUn.E)})",
        },
        IlIsLuaType isLua => $"type({RenderIl(isLua.E)}) == \"{isLua.LuaType}\"",
        IlIife iife => $"(function() {RenderIlStatsInline(iife.Stats)} end)()",
        IlClosure closure => RenderIlClosure(closure),
        IlWith with => RenderIlWith(with),
        _ => throw new InvalidOperationException(
            $"unhandled IL expression: {expr.GetType().Name}"),
    };

    private string RenderIlClosure(IlClosure closure)
    {
        var paramList = string.Join(", ", closure.Params);
        if (closure.ExprBody != null)
        {
            var locals = closure.PatternLocals.Length > 0
                ? $"local {string.Join(", ", closure.PatternLocals)}; " : "";
            if (TryRenderListAddStat(closure.ExprBody) is { } add)
                return $"function({paramList}) {locals}{add} end";
            return $"function({paramList}) {locals}return " +
                $"{RenderIl(closure.ExprBody)} end";
        }
        return $"function({paramList}) {RenderIlClosureBlock(closure.Body!)} end";
    }

    // lifted 演算: 片方でも nil なら nil (比較は false)。op 関数は prelude の
    // 定数なので closure 確保は無い。Eq / Ne は Lua の == がそのまま
    // (nil == nil は true、nil と値は false)
    private string RenderIlLifted(IlLiftedBin lifted)
    {
        var l = RenderIl(lifted.L);
        var r = RenderIl(lifted.R);
        return lifted.Op switch
        {
            IlLiftedOp.Eq => $"({l} == {r})",
            IlLiftedOp.Ne => $"({l} ~= {r})",
            IlLiftedOp.Lt => $"__tcs_ncmp({l}, {r}, __tcs_op_lt)",
            IlLiftedOp.Le => $"__tcs_ncmp({l}, {r}, __tcs_op_le)",
            IlLiftedOp.Gt => $"__tcs_ncmp({l}, {r}, __tcs_op_gt)",
            IlLiftedOp.Ge => $"__tcs_ncmp({l}, {r}, __tcs_op_ge)",
            IlLiftedOp.And => $"__tcs_nand({l}, {r})",
            IlLiftedOp.Or => $"__tcs_nor({l}, {r})",
            _ => $"__tcs_nlift({l}, {r}, __tcs_op_{LiftedOpName(lifted.Op)})",
        };
    }

    private static string LiftedOpName(IlLiftedOp op) => op switch
    {
        IlLiftedOp.Add => "add", IlLiftedOp.Sub => "sub", IlLiftedOp.Mul => "mul",
        IlLiftedOp.DivFloat => "div", IlLiftedOp.DivInt => "idiv",
        IlLiftedOp.RemFloat => "fmod", IlLiftedOp.RemInt => "irem",
        IlLiftedOp.BitAnd => "band", IlLiftedOp.BitOr => "bor",
        IlLiftedOp.BitXor => "bxor", IlLiftedOp.Shl => "shl", IlLiftedOp.Shr => "shr",
        _ => throw new InvalidOperationException($"not a lifted arithmetic op: {op}"),
    };

    private string RenderIlWith(IlWith with)
    {
        var overrides = string.Join("; ", with.Overrides
            .Select(o => $"__tcs_copy.{o.Name} = {RenderIl(o.Value)}"));
        return $"(function() local __tcs_src = {RenderIl(with.Src)}; " +
            "local __tcs_copy = {}; " +
            "for k,v in pairs(__tcs_src) do __tcs_copy[k] = v end; " +
            "setmetatable(__tcs_copy, getmetatable(__tcs_src)); " +
            $"{overrides}; " +
            "return __tcs_copy end)()";
    }

    private string RenderIlTable(IlTable table)
    {
        if (table.Entries.Length == 0) return "{}";
        var parts = table.Entries.Select(e =>
            e.NameKey != null ? $"{e.NameKey} = {RenderIl(e.Value)}"
            : e.Key != null ? $"[{RenderIl(e.Key)}] = {RenderIl(e.Value)}"
            : RenderIl(e.Value));
        return $"{{{string.Join(", ", parts)}}}";
    }

    // List.Add の文位置 lowering (#24): `table.insert(t, v)` は C 関数呼び出し
    // の分だけ遅い (実測 52ns → 25ns/要素) ので `t[#t + 1] = v` を出力する。
    // 文位置でのみ有効 (式位置の table.insert は IlCall のまま)。
    // 受け手は変数 / field 連鎖に限る (1 回評価の保証。それ以外は fallback)。
    // 引数が呼び出しを含む場合は `local __tcs_v = v` に先に束縛する — Lua は
    // `t[#t + 1] = v` で `#t` を右辺より先に評価するため、右辺が t に副作用を
    // 持つと C# (引数評価 → Add) と順序が入れ替わる。open/close は束縛を
    // 伴う 2 文を囲む文字列 (block 位置の do ... end)。
    private string? TryRenderListAddStat(IlExpr expr, string open = "",
        string close = "")
    {
        if (expr is not IlCall { Callee: "table.insert", Args.Length: 2 } call)
            return null;
        var recv = call.Args[0];
        if (!IsSimpleReceiverPath(recv)) return null;
        var t = RenderIl(recv);
        var value = RenderIl(call.Args[1]);
        if (IsCallFree(call.Args[1]))
            return $"{t}[#{t} + 1] = {value}";
        return $"{open}local __tcs_v = {value}; {t}[#{t} + 1] = __tcs_v{close}";
    }

    private static bool IsSimpleReceiverPath(IlExpr e) => e switch
    {
        IlVar => true,
        IlField f => IsSimpleReceiverPath(f.Recv),
        _ => false,
    };

    // 呼び出しを含まない式 (順序入れ替えで観測できる副作用が無い)。
    // runtime helper / math / string は純粋なので許す。
    private static bool IsCallFree(IlExpr e) => e switch
    {
        IlLit or IlVar or IlClosure => true,
        IlField f => IsCallFree(f.Recv),
        IlIndex ix => IsCallFree(ix.Recv) && IsCallFree(ix.Idx),
        IlLen len => IsCallFree(len.E),
        IlBin bin => IsCallFree(bin.L) && IsCallFree(bin.R),
        IlUn un => IsCallFree(un.E),
        IlParen p => IsCallFree(p.E),
        IlTernary t => IsCallFree(t.Cond) && IsCallFree(t.T) && IsCallFree(t.F),
        IlStructCopy sc => IsCallFree(sc.E),
        IlCast cast => IsCallFree(cast.E),
        IlNullableWrap w => IsCallFree(w.E),
        IlNullableHasValue h => IsCallFree(h.E),
        IlIsType it => IsCallFree(it.E),
        IlIsLuaType ilt => IsCallFree(ilt.E),
        IlNewArray na => IsCallFree(na.Length)
            && (na.Default is null or IlLit or IlNewObj),
        IlTable tbl => tbl.Entries.All(
            en => (en.Key == null || IsCallFree(en.Key)) && IsCallFree(en.Value)),
        IlCall c => IsPureCallee(c.Callee) && c.Args.All(IsCallFree),
        _ => false,
    };

    private static bool IsPureCallee(string callee) =>
        callee.StartsWith("__tcs_", StringComparison.Ordinal)
        || callee.StartsWith("math.", StringComparison.Ordinal)
        || callee.StartsWith("string.", StringComparison.Ordinal)
        || callee.StartsWith("Math.", StringComparison.Ordinal)
        || callee.StartsWith("String.", StringComparison.Ordinal)
        || callee is "tostring" or "tonumber";
}
