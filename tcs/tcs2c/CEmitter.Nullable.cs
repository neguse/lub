using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// Nullable<T> (il-spec §13): C では { bool has; T v; }。IL の明示ノード
// (Wrap / HasValue / Value / GetOrDefault / Lifted) を 1 対 1 の statement
// expression に落とす。lifted は片方でも値なしなら値なし、比較は false、
// Eq / Ne は両方値なしで等しい、bool? の And / Or は三値論理。
internal sealed partial class CEmitter
{
    private CType RequireNullable(IlExpr expr, string where)
    {
        var type = TypeOf(expr);
        if (type.Kind != CTypeKind.Nullable)
            throw new Tcs2cException($"{where}: operand is not Nullable<T> ({type})");
        return type;
    }

    private CType TypeOfNullableWrap(IlNullableWrap wrap)
    {
        var declared = _facts.MapType(wrap.Type);
        if (declared.Kind == CTypeKind.Nullable) return declared;
        var element = declared.Kind is CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool
            or CTypeKind.StructVal ? declared : TypeOf(wrap.E);
        if (element.Kind is not (CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool
            or CTypeKind.StructVal))
            throw new Tcs2cException($"IlNullableWrap of non-value type: {element}");
        CheckAssignable(element, wrap.E, "nullable wrap");
        return CType.Nullable(element);
    }

    private string RenderNullableWrap(IlNullableWrap wrap)
    {
        var type = TypeOfNullableWrap(wrap);
        return $"(({type.CName}){{ true, {RenderCoerced(wrap.E, type.Element!)} }})";
    }

    private string RenderNullableValue(IlNullableValue val)
    {
        var type = RequireNullable(val.E, "Value");
        var temp = Temp("opt");
        return $"({{ {type.CName} {temp} = {RenderExpr(val.E)}; " +
            $"if (!{temp}.has) tcs_fault(\"nullable-value\"); {temp}.v; }})";
    }

    private CType TypeOfNullableGetOrDefault(IlNullableGetOrDefault gd)
    {
        var type = RequireNullable(gd.E, "??");
        var fallback = TypeOf(gd.Default);
        if (fallback.Kind == CTypeKind.Nullable)
        {
            if (fallback != type) throw new Tcs2cException($"?? operand mismatch: {type}, {fallback}");
            return type;
        }
        if (fallback.Kind == CTypeKind.Null) return type;
        if (type.Element!.Kind is CTypeKind.I32 or CTypeKind.F32
            && fallback.Kind is CTypeKind.I32 or CTypeKind.F32)
            return NumericJoin(type.Element, fallback, "??");
        if (!type.Element.CanAssignFrom(fallback) && type.Element != fallback)
            throw new Tcs2cException($"?? fallback {fallback} is not assignable to {type.Element}");
        return type.Element;
    }

    private string RenderNullableGetOrDefault(IlNullableGetOrDefault gd)
    {
        var type = RequireNullable(gd.E, "??");
        var result = TypeOfNullableGetOrDefault(gd);
        var temp = Temp("opt");
        if (result.Kind == CTypeKind.Nullable)
            return $"({{ {type.CName} {temp} = {RenderExpr(gd.E)}; " +
                $"{temp}.has ? {temp} : {RenderCoerced(gd.Default, type)}; }})";
        return $"({{ {type.CName} {temp} = {RenderExpr(gd.E)}; " +
            $"{temp}.has ? ({result.CName}){temp}.v : ({result.CName})({RenderCoerced(gd.Default, result)}); }})";
    }

    private string RenderNullableToString(IlExpr expr)
    {
        var type = RequireNullable(expr, "__tcs_nstr");
        var temp = Temp("opt");
        var helper = type.Element!.Kind switch
        {
            CTypeKind.I32 => "tcs_string_i32",
            CTypeKind.F32 => "tcs_string_f32",
            CTypeKind.Bool => "tcs_string_bool",
            _ => throw new Tcs2cException($"tostring does not support {type}"),
        };
        return $"({{ {type.CName} {temp} = {RenderExpr(expr)}; " +
            $"{temp}.has ? {helper}({temp}.v) : {InternStringLiteral([])}; }})";
    }

    // T? 同士 / T? と T / T? と null の等価 (lifted Eq)
    private string NullableEqualExpr(CType type, string left, string right)
    {
        var inner = FieldEqualExpr(type.Element!, $"{left}.v", $"{right}.v");
        return $"({left}.has == {right}.has && (!{left}.has || {inner}))";
    }

    private string RenderNullableEquality(IlBin binary, CType leftType, CType rightType)
    {
        var negate = binary.Op == IlBinOp.Ne ? "!" : "";
        var nullable = leftType.Kind == CTypeKind.Nullable ? leftType : rightType;
        var l = Temp("eq_lhs");
        var r = Temp("eq_rhs");
        var sb = new StringBuilder();
        sb.Append($"{nullable.CName} {l} = {RenderCoerced(binary.L, nullable)}; ");
        sb.Append($"{nullable.CName} {r} = {RenderCoerced(binary.R, nullable)}; ");
        return $"({{ {sb}{negate}{NullableEqualExpr(nullable, l, r)}; }})";
    }

    private static bool IsComparison(IlLiftedOp op) =>
        op is IlLiftedOp.Eq or IlLiftedOp.Ne or IlLiftedOp.Lt or IlLiftedOp.Le
            or IlLiftedOp.Gt or IlLiftedOp.Ge;

    private CType TypeOfLiftedBin(IlLiftedBin lifted)
    {
        var left = RequireNullable(lifted.L, $"lifted {lifted.Op}");
        var right = RequireNullable(lifted.R, $"lifted {lifted.Op}");
        var le = left.Element!;
        var re = right.Element!;
        switch (lifted.Op)
        {
            case IlLiftedOp.And or IlLiftedOp.Or:
                if (le != CType.Bool || re != CType.Bool)
                    throw new Tcs2cException("lifted And/Or operands must be bool?");
                return CType.Nullable(CType.Bool);
            case IlLiftedOp.Eq or IlLiftedOp.Ne:
                if (le != re && NumericJoin(le, re, "lifted equality") is null)
                    throw new Tcs2cException("lifted equality operand mismatch");
                return CType.Bool;
            case IlLiftedOp.Lt or IlLiftedOp.Le or IlLiftedOp.Gt or IlLiftedOp.Ge:
                _ = NumericJoin(le, re, $"lifted {lifted.Op}");
                return CType.Bool;
            case IlLiftedOp.BitXor when le == CType.Bool && re == CType.Bool:
                return CType.Nullable(CType.Bool);
            case IlLiftedOp.BitAnd or IlLiftedOp.BitOr or IlLiftedOp.BitXor
                or IlLiftedOp.Shl or IlLiftedOp.Shr or IlLiftedOp.DivInt or IlLiftedOp.RemInt:
                RequireType(CType.I32, le, $"lifted {lifted.Op}");
                RequireType(CType.I32, re, $"lifted {lifted.Op}");
                return CType.Nullable(CType.I32);
            case IlLiftedOp.DivFloat or IlLiftedOp.RemFloat:
                _ = NumericJoin(le, re, $"lifted {lifted.Op}");
                return CType.Nullable(CType.F32);
            default:
                return CType.Nullable(NumericJoin(le, re, $"lifted {lifted.Op}"));
        }
    }

    private string RenderLiftedBin(IlLiftedBin lifted)
    {
        var result = TypeOfLiftedBin(lifted);
        var left = RequireNullable(lifted.L, "lifted");
        var right = RequireNullable(lifted.R, "lifted");
        var x = Temp("lx");
        var y = Temp("ly");
        var sb = new StringBuilder();
        sb.Append($"{left.CName} {x} = {RenderExpr(lifted.L)}; ");
        sb.Append($"{right.CName} {y} = {RenderExpr(lifted.R)}; ");
        var xv = $"{x}.v";
        var yv = $"{y}.v";
        switch (lifted.Op)
        {
            case IlLiftedOp.Eq:
                return $"({{ {sb}{x}.has == {y}.has && (!{x}.has || {xv} == {yv}); }})";
            case IlLiftedOp.Ne:
                return $"({{ {sb}!({x}.has == {y}.has && (!{x}.has || {xv} == {yv})); }})";
            case IlLiftedOp.Lt or IlLiftedOp.Le or IlLiftedOp.Gt or IlLiftedOp.Ge:
            {
                var op = lifted.Op switch
                {
                    IlLiftedOp.Lt => "<", IlLiftedOp.Le => "<=", IlLiftedOp.Gt => ">", _ => ">=",
                };
                return $"({{ {sb}{x}.has && {y}.has && {xv} {op} {yv}; }})";
            }
            case IlLiftedOp.And:
                return $"({{ {sb}{result.CName} r = {{0}}; " +
                    $"if (({x}.has && !{xv}) || ({y}.has && !{yv})) {{ r.has = true; r.v = false; }} " +
                    $"else if ({x}.has && {y}.has) {{ r.has = true; r.v = true; }} r; }})";
            case IlLiftedOp.Or:
                return $"({{ {sb}{result.CName} r = {{0}}; " +
                    $"if (({x}.has && {xv}) || ({y}.has && {yv})) {{ r.has = true; r.v = true; }} " +
                    $"else if ({x}.has && {y}.has) {{ r.has = true; r.v = false; }} r; }})";
        }
        var elem = result.Element!;
        var value = lifted.Op switch
        {
            IlLiftedOp.Add => $"({xv} + {yv})",
            IlLiftedOp.Sub => $"({xv} - {yv})",
            IlLiftedOp.Mul => $"({xv} * {yv})",
            IlLiftedOp.DivFloat => $"((float){xv} / (float){yv})",
            IlLiftedOp.DivInt => $"tcs_idiv({xv}, {yv})",
            IlLiftedOp.RemFloat => $"fmodf({xv}, {yv})",
            IlLiftedOp.RemInt => $"tcs_irem({xv}, {yv})",
            IlLiftedOp.BitAnd => $"({xv} & {yv})",
            IlLiftedOp.BitOr => $"({xv} | {yv})",
            IlLiftedOp.BitXor => elem == CType.Bool ? $"({xv} != {yv})" : $"({xv} ^ {yv})",
            IlLiftedOp.Shl => $"tcs_shl({xv}, {yv})",
            IlLiftedOp.Shr => $"tcs_shr({xv}, {yv})",
            _ => throw new Tcs2cException($"unsupported lifted op: {lifted.Op}"),
        };
        return $"({{ {sb}{result.CName} r = {{0}}; " +
            $"if ({x}.has && {y}.has) {{ r.has = true; r.v = ({elem.CName})({value}); }} r; }})";
    }

    private CType TypeOfLiftedUn(IlLiftedUn lifted)
    {
        var type = RequireNullable(lifted.E, "lifted unary");
        return lifted.Op switch
        {
            IlUnOp.Neg when type.Element!.Kind is CTypeKind.I32 or CTypeKind.F32 => type,
            IlUnOp.Not when type.Element == CType.Bool => type,
            IlUnOp.BitNot when type.Element == CType.I32 => type,
            _ => throw new Tcs2cException($"invalid lifted unary {lifted.Op} for {type}"),
        };
    }

    private string RenderLiftedUn(IlLiftedUn lifted)
    {
        var type = TypeOfLiftedUn(lifted);
        var x = Temp("lx");
        var value = lifted.Op switch
        {
            IlUnOp.Neg => $"-{x}.v", IlUnOp.Not => $"!{x}.v", _ => $"~{x}.v",
        };
        return $"({{ {type.CName} {x} = {RenderExpr(lifted.E)}; " +
            $"if ({x}.has) {x}.v = {value}; {x}; }})";
    }
}
