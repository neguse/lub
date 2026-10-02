using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private string RenderNullableBinary(IlBin binary, CType left, CType right)
    {
        var lhs = Temp("nullable_lhs");
        var rhs = Temp("nullable_rhs");
        string Value(string name, CType type) => type.Kind != CTypeKind.Nullable ? name
            : $"(tcs_unbox({name}, {RuntimeTypeId(type.Element!)})->value." +
                (type.Element!.Kind switch { CTypeKind.Bool => "b", CTypeKind.I32 => "i", _ => "f" }) + ")";
        var prefix = $"({{ {(left == CType.Null ? "void *" : left.CName)} {lhs} = {RenderExpr(binary.L)}; ";
        if (binary.Op == IlBinOp.Or)
            return prefix + $"{lhs} != NULL ? " +
                (TypeOfBinary(binary).Kind == CTypeKind.Nullable ? lhs : Value(lhs, left)) +
                $" : {RenderCoerced(binary.R, TypeOfBinary(binary))}; }})";
        if (binary.Op is not (IlBinOp.Eq or IlBinOp.Ne))
            throw new Tcs2cException($"unsupported nullable operator: {binary.Op}");
        var equality = left == CType.Null || right == CType.Null ? $"({lhs} == {rhs})"
            : left.Kind == CTypeKind.Nullable && right.Kind == CTypeKind.Nullable
                ? $"({lhs} == NULL ? {rhs} == NULL : {rhs} != NULL && {Value(lhs, left)} == {Value(rhs, right)})"
                : $"({(left.Kind == CTypeKind.Nullable ? lhs : rhs)} != NULL && {Value(lhs, left)} == {Value(rhs, right)})";
        return prefix + $"{(right == CType.Null ? "void *" : right.CName)} {rhs} = {RenderExpr(binary.R)}; " +
            (binary.Op == IlBinOp.Ne ? $"!{equality}" : equality) + "; })";
    }
}
