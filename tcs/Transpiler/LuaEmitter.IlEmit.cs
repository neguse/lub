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
            default:
                throw new InvalidOperationException(
                    $"unhandled IL statement: {stat.GetType().Name}");
        }
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
        EmitIlBlock(whileStat.Body);
        EmitContinueLabel(label);
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
        _ => throw new InvalidOperationException(
            $"unhandled IL expression: {expr.GetType().Name}"),
    };

    private string RenderIlTable(IlTable table)
    {
        if (table.Entries.Length == 0) return "{}";
        var parts = table.Entries.Select(e => e.Key != null
            ? $"[{RenderIl(e.Key)}] = {RenderIl(e.Value)}"
            : RenderIl(e.Value));
        return $"{{{string.Join(", ", parts)}}}";
    }
}
