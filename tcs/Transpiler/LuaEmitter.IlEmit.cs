using System.Collections.Immutable;

namespace TinyCs;

// IL → Lua emit。SemanticModel には依存しない (意味決定は builder 側で完了
// している)。出力は legacy visitor と同形 — M1 は挙動不変が完了条件。
public partial class LuaEmitter
{
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
                AppendLine(RenderIl(call.Call));
                break;
            case IlReturn ret:
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
        IlCallStat call => RenderIl(call.Call),
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
        IlNewArray => "{}",  // 長さは Lua 表現に現れない (legacy 互換)
        IlIsType isType => $"__tcs_is({RenderIl(isType.E)}, {isType.TypeRef})",
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
            return $"function({paramList}) {locals}return " +
                $"{RenderIl(closure.ExprBody)} end";
        }
        return $"function({paramList}) {RenderIlClosureBlock(closure.Body!)} end";
    }

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
}
