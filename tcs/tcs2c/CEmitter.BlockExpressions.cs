using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private CType TypeOfBlockExpression(IlIife block)
    {
        PushScope();
        try
        {
            foreach (var stat in block.Stats)
                if (stat is IlLocal local)
                    AddVariable(local.Name, new Variable(local.Name, local.Type is null
                        ? TypeOf(local.Init!) : _facts.MapType(local.Type)));
            return block.Stats.LastOrDefault() is IlReturn { Value: { } value }
                ? TypeOf(value) : CType.Void;
        }
        finally { PopScope(); }
    }

    private string RenderBlockExpression(IlIife block)
    {
        var start = _output.Length;
        PushScope();
        try
        {
            for (int i = 0; i < block.Stats.Length; i++)
            {
                var stat = block.Stats[i];
                if (i == block.Stats.Length - 1 && stat is IlReturn ret)
                {
                    Line(ret.Value is null ? "(void)0;" : $"{RenderExpr(ret.Value)};");
                    continue;
                }
                RejectEarlyReturn(stat);
                if (!EmitListClear(stat)) EmitStat(stat);
            }
            if (block.Stats.LastOrDefault() is not IlReturn) Line("(void)0;");
            return "({\n" + _output.ToString(start, _output.Length - start) + "})";
        }
        finally { _output.Length = start; PopScope(); }
    }

    private bool EmitListClear(IlStat stat)
    {
        if (stat is not IlForPairs { VVar: null, Coll: IlVar receiver,
            Body.Stats: [IlAssign { Target: IlIndex { Recv: IlVar target, Idx: IlVar key },
                Value: IlLit { LuaText: "nil" } }] } loop
            || receiver.Name != target.Name || key.Name != loop.KVar
            || TypeOf(receiver).Kind != CTypeKind.List) return false;
        Line($"((TcsList *)tcs_nonnull({RenderExpr(receiver)}))->length = 0;");
        return true;
    }

    private static void RejectEarlyReturn(IlStat stat)
    {
        if (stat is IlReturn) throw new Tcs2cException("early return in block expression is unsupported");
        WalkStat(stat, _ => { }, RejectEarlyReturn);
    }
}
