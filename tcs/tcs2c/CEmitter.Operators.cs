using TinyCs;

namespace TinyCs.Tcs2c;

// user-defined operator。IL は素の IlBin / IlUn のまま (Lua は metamethod が
// 実行時の operand 型で overload へ分岐する)。C は operand の静的型で overload を
// 選び、static method の直呼びにする。同じ operator の overload は Lua 出力と同じ
// `__mul_1` `__mul_2` … (宣言順) に改名して別関数にする。
internal sealed partial class CEmitter
{
    private static bool IsOperatorName(string name) =>
        name is "__add" or "__sub" or "__mul" or "__div" or "__mod" or "__unm";

    private static IlClassInfo RenameOperatorOverloads(IlClassInfo cls)
    {
        var overloaded = cls.Methods.Where(m => m.IsStatic && IsOperatorName(m.Name))
            .GroupBy(m => m.Name).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet();
        if (overloaded.Count == 0) return cls;
        var serial = new Dictionary<string, int>();
        return cls with
        {
            Methods = [.. cls.Methods.Select(m =>
            {
                if (!m.IsStatic || !overloaded.Contains(m.Name)) return m;
                var index = serial[m.Name] = serial.GetValueOrDefault(m.Name) + 1;
                return m with { Name = $"{m.Name}_{index}" };
            })],
        };
    }

    private static string? OperatorName(IlBinOp op) => op switch
    {
        IlBinOp.AddNum => "__add",
        IlBinOp.Sub => "__sub",
        IlBinOp.Mul => "__mul",
        IlBinOp.DivNum => "__div",
        IlBinOp.RemNum => "__mod",
        _ => null,
    };

    /// <summary>operand のどちらかが class 参照の算術 IlBin は user-defined
    /// operator (それ以外は null)。</summary>
    private MethodFact? UserOperator(IlBinOp op, CType left, CType right) =>
        (left.Kind == CTypeKind.Ref || right.Kind == CTypeKind.Ref)
        && OperatorName(op) is { } name
            ? ResolveOperator(name, [left, right]) : null;

    private CType TypeOfUnary(IlUn unary)
    {
        var type = TypeOf(unary.E);
        return unary.Op == IlUnOp.Neg && type.Kind == CTypeKind.Ref
            ? ResolveOperator("__unm", [type]).ReturnType : type;
    }

    // C# の overload 解決は operand 型の class (とその基底) の operator が候補。
    // 完全一致を優先し、無ければ暗黙変換 (int → float / upcast) で合うもの
    private MethodFact ResolveOperator(string name, CType[] operands)
    {
        var candidates = new List<MethodFact>();
        foreach (var operand in operands.Where(o => o.Kind == CTypeKind.Ref).Distinct())
            for (string? cur = operand.Name; cur != null; cur = _classes[cur].BaseName)
                candidates.AddRange(_classes[cur].Methods
                    .Where(m => m.IsStatic && m.Parameters.Length == operands.Length
                        && (m.Name == name || m.Name.StartsWith(name + "_", StringComparison.Ordinal)))
                    .Select(m => _facts.Method(cur, m.Name)));
        return candidates.FirstOrDefault(c => c.Parameters.Select(p => p.Type).SequenceEqual(operands))
            ?? candidates.FirstOrDefault(c => c.Parameters.Zip(operands).All(p =>
                p.First.Type.CanAssignFrom(p.Second)
                || p.First.Type.Kind == CTypeKind.Ref && p.Second.Kind == CTypeKind.Ref
                    && IsAncestorOrSame(p.First.Type.Name!, p.Second.Name!)))
            ?? throw new Tcs2cException(
                $"no user-defined operator {name} for ({string.Join(", ", operands.Select(o => o.ToString()))})");
    }
}
