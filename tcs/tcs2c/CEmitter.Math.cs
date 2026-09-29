using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private CType? MathResult(IlCall call)
    {
        var arity = call.Callee switch
        {
            "Math.Sin" or "Math.Cos" or "Math.Sqrt" or "Math.Floor"
                or "Math.Ceil" or "Math.Abs" => 1,
            "Math.Atan2" or "Math.Pow" or "Math.Min" or "Math.Max"
                or "math.fmod" => 2,
            _ => 0,
        };
        if (arity == 0) return null;
        RequireArity(call.Callee, call.Args.Length, arity);
        var type = CType.I32;
        foreach (var arg in call.Args)
            type = NumericJoin(type, TypeOf(arg), call.Callee);
        return call.Callee is "Math.Abs" or "Math.Min" or "Math.Max"
            ? type : CType.F32;
    }

    private string RenderMath(IlCall call, CType type)
    {
        var function = call.Callee switch
        {
            "Math.Sin" => "sinf", "Math.Cos" => "cosf", "Math.Sqrt" => "sqrtf",
            "Math.Floor" => "floorf", "Math.Ceil" => "ceilf",
            "Math.Atan2" => "atan2f", "Math.Pow" => "powf", "math.fmod" => "fmodf",
            "Math.Abs" => type == CType.I32 ? "tcs_abs_i32" : "fabsf",
            "Math.Min" => type == CType.I32 ? "tcs_min_i32" : "tcs_min_f32",
            "Math.Max" => type == CType.I32 ? "tcs_max_i32" : "tcs_max_f32",
            _ => throw new Tcs2cException($"unsupported math call: {call.Callee}"),
        };
        return RenderOrderedCall(function, type,
            call.Args.Select(a => (type, RenderExpr(a))).ToList());
    }

    private string RenderNumericConvert(IlNumericConvert convert)
    {
        var target = _facts.MapType(convert.TargetType);
        var source = TypeOf(convert.Value);
        if (source == CType.Object) return RenderUnbox(convert.Value, target);
        _ = NumericJoin(target, source, "numeric conversion");
        var value = RenderExpr(convert.Value);
        return target == CType.I32 && source == CType.F32
            ? $"tcs_to_i32({value})" : $"(({target.CName})({value}))";
    }

    private const string MathRuntime = """
        static int32_t tcs_to_i32(float value)
        {
            if (!isfinite(value) || value < -2147483648.0f || value >= 2147483648.0f)
                tcs_fault("integer-conversion-overflow");
            return (int32_t)value;
        }
        static int32_t tcs_abs_i32(int32_t value)
        {
            return value < 0 ? (int32_t)(UINT32_C(0) - (uint32_t)value) : value;
        }
        static int32_t tcs_min_i32(int32_t a, int32_t b) { return b < a ? b : a; }
        static int32_t tcs_max_i32(int32_t a, int32_t b) { return b > a ? b : a; }
        static float tcs_min_f32(float a, float b) { return b < a ? b : a; }
        static float tcs_max_f32(float a, float b) { return b > a ? b : a; }

        """;
}
